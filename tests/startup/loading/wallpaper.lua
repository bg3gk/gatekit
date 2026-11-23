-- A load to Act 1 shows the Act 1 wallpapers: the loading screen's slideshow
-- (code:gui::LSImageSequence, owner root: the game's own screen) is made ready when the screen is made, shuffles the Act 1 set anew
-- then, shows the first image of that order, and changes image on the game's own clock - every
-- ImageHoldDuration (20 s), as its storyboard's key frames say.
-- Read from the event log: `loading_wallpaper` rows (`set` with the images in the order they will
-- show, `shown` when the screen's image changed), imported from the start timeline of GateKit's
-- Script Extender build (bridge-protocol.md, "The loading screen's look").
-- Sources: the game's GUI/Widgets/LoadingScreen.xaml (ControlTemplate LoadingImagesAct1: art 3,
-- 5, 7, 9, 11, 12; SequenceStyle: hold 0:0:20, fades 0:0:0.8, Shuffle True); window photos of
-- loads to SCL_Main_A and BGO_Main_A (F6 report), where the image changed 21-22 s after the set
-- was made. A load to Act 1 on a fast machine lasts less than one slot (about 15 s): then the
-- step on the clock holds that no image changed before its slot and that the slideshow's element
-- went before the slot ended; a change, when there is one, comes at its slot (the `gone` row
-- bounds each slideshow's life).
-- Venue: a fresh game in Act 3 (BGO_Main_A); the test moves the party to WLD_Main_A (a cheat:
-- the stage's level move) and leaves it there. Its time is the load's (30-60 s).
-- Run: gatekit --instance <copy> test tests -k startup/loading/wallpaper

test { level = "GustavX:BGO_Main_A",
       about = "A load to Act 1 shows the Act 1 set, shuffled anew when the screen is made, and changes image on the game's clock." }

local ACT1 = { "loadScreen_BGartwork_3", "loadScreen_BGartwork_5", "loadScreen_BGartwork_7",
               "loadScreen_BGartwork_9", "loadScreen_BGartwork_11", "loadScreen_BGartwork_12" }
local KINDS = { "loading_wallpaper", "loading", "loading_ended" }
-- How late after its slot an image may come: the storyboard's clock starts with the control's
-- first frame, after OnLoaded, and the fork reads it on the interface's update.
local LATE_MS = 3000

function run(game)
    game.wait_state("Running", "client", 120)
    local before = game.mark()
    step("the party moved to Act 1", "cheat", function()
        check(game.server("return GK.Stage.Level('WLD_Main_A')"), "the level move asked")
    end)
    local rows
    step("the load ends in Act 1", "observation", function()
        game.wait_row(before, { "loading_ended" }, nil, 180)
        game.wait_state("Running", "client", 120)
        -- the slideshow outlives the load's end by seconds: its `gone` row closes what it showed
        game.wait_row(before, { "loading_wallpaper" }, function(p) return p.what == "gone" and p.look == "sequence" and p.owner == "root" end, 60)
        -- the game's own loading screen only (its widget is root); a mod's wallpaper has its own owner
        rows = {}
        for _, r in ipairs(game.events(before, KINDS).rows) do
            if r.kind ~= "loading_wallpaper" or r.payload.owner == "root" then rows[#rows + 1] = r end
        end
        game.save("rows.txt", fmt(rows) .. "\n")
    end)
    step("each slideshow of the load is the Act 1 set, shuffled, and shows its first image first", "observation", function()
        local sets, began = {}, false
        for _, r in ipairs(rows) do
            if r.kind == "loading" then began = true end
            if r.kind == "loading_wallpaper" and r.payload.what == "set" then
                check(began, "a set is made inside the load, after it began", r)
                sets[#sets + 1] = r.payload
            end
        end
        check(#sets >= 1, "the load made a slideshow", rows)
        -- the slideshow the load ended on: the screen is made again inside a load (a reset of
        -- the interface), and each making shuffles
        for _, set in ipairs(sets) do
            eq(sorted(set.images), sorted(ACT1), "the set is LoadingImagesAct1's")
            eq(set.slot, 20000, "each image holds its slot of 20 s")
        end
        for _, r in ipairs(rows) do
            local p = r.payload
            if r.kind == "loading_wallpaper" and p.what == "shown" and p.look == "sequence" then
                check(has(ACT1, p.image), "the image shown is one of the set", p)
            end
        end
        -- the first image shown after a set is the first of its order
        for i, r in ipairs(rows) do
            if r.kind == "loading_wallpaper" and r.payload.what == "set" then
                for j = i + 1, #rows do
                    local q = rows[j]
                    if q.kind == "loading_wallpaper" and q.payload.what == "set" then break end
                    if q.kind == "loading_wallpaper" and q.payload.what == "shown" then
                        eq(q.payload.image, r.payload.images[1], "the first image shown is the first of the shuffled order")
                        eq(q.payload.index, 1, "at position 1")
                        break
                    end
                end
            end
        end
    end)
    step("the image changes on the slideshow's clock, and only then", "observation", function()
        -- per slideshow: its set, the images it showed, the moment its element went
        local shows, current = {}, nil
        for _, r in ipairs(rows) do
            local p = r.payload
            if r.kind == "loading_wallpaper" and p.what == "set" then
                current = { set = p, changes = {} }
                shows[#shows + 1] = current
            elseif current and r.kind == "loading_wallpaper" and p.look == "sequence" then
                if p.what == "shown" and (p.index or 0) > 1 then current.changes[#current.changes + 1] = p end
                if p.what == "gone" and not current.gone then current.gone = p end
            end
        end
        local last = shows[#shows]
        check(last and last.gone, "the load's last slideshow went", rows)
        for _, show in ipairs(shows) do
            local set = show.set
            for _, p in ipairs(show.changes) do
                eq(p.image, set.images[p.index], "the image is the next of the order")
                local due = set.ms + (p.index - 1) * set.slot
                check(p.ms >= due and p.ms <= due + LATE_MS, "the change comes at its slot, not before",
                      { shownMs = p.ms, dueMs = due, set = set })
            end
            -- a slideshow that lived past its first slot changed image there
            if show.gone and show.gone.ms > set.ms + set.slot + LATE_MS then
                check(#show.changes > 0, "a slideshow alive past its slot changed image", show)
            end
        end
        game.save("shows.txt", fmt(shows) .. "\n")
    end)
end
