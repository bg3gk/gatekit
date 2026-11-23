-- A mod shows its own loading screen for one destination and leaves the game's for the others,
-- without replacing the game's file: its state machine (Keyboard.xaml) extends the interface's
-- `Loading` state (ModType="Extend") with two pages of its own, made with the game's loading
-- screen at every load and bound to the same view model (ContextName LoadingScreen: LevelName,
-- LoadingState, Progress). A DataTrigger on `LevelName` = WLD_Main_A (Act 1) shows the first
-- page (GK_OwnLoading.xaml): a vector wallpaper of the mod's own over the whole screen, drawn over
-- the game's (same layer, made after it), and the load's percentage beside the mod's indicator
-- (_files/Resources.xaml, test:startup/loading/d20). The second page (GK_OwnSlides.xaml) gives the
-- mod its own slideshow with the game's own control, ls:LSImageSequence: its own list of images
-- (three of the game's single loading pictures by name, art 13, 14 and 15 - a set no act shows;
-- a mod's own image needs a DDS listed in its GUI/metadata.lsf), its own clock (4 s a slot); it
-- is made only for Act 1, by a template a DataTrigger sets, as the game's screen picks its act's
-- set. On any other load (here the creche, CRE_Main_A) the mod's pages show nothing and the
-- game's screen shows its own picture (research/studies/startup-videos.md, "The loading screen").
-- Read from the live interface (Ext.UI: the pages' elements, GK_OWN_*, shown or not, their size
-- and the order the widgets are drawn in) and from the event log: `loading_wallpaper` rows of the
-- start timeline of GateKit's Script Extender build, which sees the mod's slideshow as it sees the
-- game's (`code:gui::LSImageSequence::OnLoaded`; bridge-protocol.md, "The loading screen's look").
-- Venue: a fresh game in the creche; the party moves to Act 1 and back (cheats: the stage's level
-- move). The bridge mod Weave, enabled in every copy, carries the files (`files`).
-- Run: gatekit --instance <copy> test tests -k startup/loading/own_screen

test { level = "GustavX:CRE_Main_A",
       files = {
           ["Data/Mods/Gwynerva_Weave/GUI/Resources/Resources.xaml"] = "Resources.xaml",
           ["Data/Mods/Gwynerva_Weave/GUI/StateMachines/Keyboard.xaml"] = "Keyboard.xaml",
           ["Data/Mods/Gwynerva_Weave/GUI/Pages/GK_OwnLoading.xaml"] = "GK_OwnLoading.xaml",
           ["Data/Mods/Gwynerva_Weave/GUI/Pages/GK_OwnSlides.xaml"] = "GK_OwnSlides.xaml",
       },
       about = "A mod's Extend of the Loading state shows its own wallpaper and slideshow on an Act 1 load, over the game's, and nothing on another load." }

local D = require("_d20")
local OURS = { "loadScreen_BGartwork_13", "loadScreen_BGartwork_14", "loadScreen_BGartwork_15" }
local KINDS = { "loading_wallpaper", "loading", "loading_ended" }
local LATE_MS = 3000

-- One load: the move asked, the interface read while it lasts, the rows of the load once the
-- slideshow shown last has gone.
local function load(game, level, shot)
    game.wait_state("Running", "client", 120)
    local before = game.mark()
    step("the party moved to " .. level, "cheat", function()
        check(game.server("return GK.Stage.Level('" .. level .. "')"), "the level move asked")
    end)
    local readings, photo, rows
    step("the load to " .. level .. " read", "observation", function()
        game.wait_row(before, { "loading" }, nil, 60)
        readings, photo = D.watch(game, { since = before, seconds = 180, shot = shot,
            photo = function(r) return D.screen(r) and D.page(r, "GK_OWN_Act1") ~= nil end })
        game.wait_row(before, { "loading_ended" }, nil, 180)
        game.wait_state("Running", "client", 120)
        game.wait_row(before, { "loading_wallpaper" }, function(p) return p.what == "gone" end, 60)
        rows = game.events(before, KINDS).rows
        game.save(level .. "_readings.txt", fmt(readings) .. "\n")
        game.save(level .. "_rows.txt", fmt(rows) .. "\n")
    end)
    return readings, photo, rows
end

-- the mod's slideshow page (its x:Name); the game's own loading screen is root
local OWNER = "GK_OWN_Slides"

local function ours(images)
    return images and same(sorted(images), OURS)
end

function run(game)
    local readings, photo, rows = load(game, "WLD_Main_A", "own_screen_act1.jpg")
    step("on the Act 1 load the mod's wallpaper covers the game's screen", "observation", function()
        local seen = 0
        for _, r in ipairs(readings) do
            local screen, act1 = D.screen(r), D.page(r, "GK_OWN_Act1")
            local page = D.page(r, "GK_OWN_Loading")
            if screen and page and act1 then
                seen = seen + 1
                check(act1.visible, "the mod's Act 1 picture is shown", act1)
                check(act1.width >= 3840 and act1.height >= 2160, "it fills the screen (3840 x 2160)", act1)
                local own
                for _, w in ipairs(r.widgets) do if w.name == "GK_OWN_Loading" then own = w end end
                check(own.depth == screen.depth and own.order > screen.order,
                      "the mod's page is drawn after the game's loading screen, on the same layer", { mod = own, game = screen })
                local title = D.page(r, "GK_OWN_Title")
                check(title and title.visible, "the mod's title is shown", title)
                eq(#D.dice(r, "GK_OWN_Loading"), 1, "the mod's page carries the d20", r)
            end
        end
        check(seen >= 2, "the mod's page was read on the loading screen at least twice", #readings)
        check(photo, "a picture of the mod's screen was taken")
    end)
    step("on the Act 1 load the mod's own slideshow runs, on its own clock", "observation", function()
        local sets, current = {}, nil
        for _, r in ipairs(rows) do
            local p = r.payload
            if r.kind == "loading_wallpaper" and p.what == "set" and p.owner == OWNER and ours(p.images) then
                current = { set = p, shown = {} }
                sets[#sets + 1] = current
            elseif current and r.kind == "loading_wallpaper" and p.look == "sequence" and p.owner == OWNER then
                if p.what == "shown" then current.shown[#current.shown + 1] = p end
                if p.what == "gone" and not current.gone then current.gone = p end
            end
        end
        check(#sets >= 1, "the load made the mod's slideshow with the mod's own set", rows)
        for _, s in ipairs(sets) do
            local set = s.set
            eq(set.slot, 4000, "each of its images holds the mod's slot of 4 s")
            -- the n-th image shown is the order's, round after round, and comes on the slideshow's
            -- clock: never before n-1 slots, and late at most by the fades of the images before it
            -- (the control's storyboard holds each image for its slot between its fades)
            for n, p in ipairs(s.shown) do
                eq(p.image, set.images[(n - 1) % #set.images + 1], "the image shown is the next of its shuffled order, round after round")
                local due = set.ms + (n - 1) * set.slot
                local latest = set.ms + (n - 1) * (set.slot + set.fadeIn + set.fadeOut) + LATE_MS
                check(p.ms >= due and p.ms <= latest, "the image comes on the slideshow's clock",
                      { n = n, shownMs = p.ms, dueMs = due, latestMs = latest })
            end
        end
        local last = sets[#sets]
        check(#last.shown > 0, "the mod's slideshow showed an image", last)
        if last.gone and last.gone.ms > last.set.ms + last.set.slot + LATE_MS then
            check(#last.shown > 1, "a slideshow alive past its slot changed image", last)
        end
        local mine
        for _, r in ipairs(readings) do mine = mine or D.page(r, "GK_OWN_Sequence") end
        check(mine and mine.visible, "the slideshow's control is in the mod's page", mine)
        game.save("act1_shows.txt", fmt(sets) .. "\n")
    end)

    readings, photo, rows = load(game, "CRE_Main_A", "own_screen_other.jpg")
    step("on another load the mod's pages show nothing and the game's screen shows its own picture", "observation", function()
        local seen = 0
        for _, r in ipairs(readings) do
            if D.screen(r) and D.page(r, "GK_OWN_Loading") then
                seen = seen + 1
                local act1 = D.page(r, "GK_OWN_Act1")
                check(not act1.visible, "the mod's Act 1 picture is not shown", act1)
                check(not D.page(r, "GK_OWN_Sequence"), "the mod's slideshow is not made", r.pages)
            end
        end
        check(seen >= 2, "the loading screen and the mod's page were read at least twice", #readings)
        local game_shown = false
        for _, r in ipairs(rows) do
            local p = r.payload
            if r.kind == "loading_wallpaper" then
                check(not (p.owner == OWNER and p.visible), "nothing of the mod's page is visible", p)
                if p.what == "shown" then
                    check(p.owner ~= OWNER or not p.visible, "nothing of the mod's slideshow is shown", p)
                    if p.owner == "root" and p.image == "loadScreen_BGartwork_4" and p.look == "single" then game_shown = true end
                end
            end
        end
        check(game_shown, "the game's own creche picture (art 4) was shown", rows)
        check(photo, "a picture of the game's screen was taken")
    end)
end
