-- The first start's loading screen draws a mod's own waiting indicator from its first frame: at the
-- client's first StartLoading GateKit's Script Extender build runs the game's own merge of the enabled
-- mods' interface (the merge block of code:gui::GameUI::UpdateFromUI: code:ui::ModManager::RegisterMods,
-- code:ui::TextureProvider::LoadTextureMetadataInternal, code:gui::GameUI::ResetModdedLibs on
-- code:Noesis::ResourceDictionary::GetMergedDictionaries of code:Noesis::GUI::GetApplicationResources,
-- code:ui::StateMachine::ApplyMods; its switch EarlyModInterface), before the loading screen is made, so the screen draws the mod's `BusyElement` - here a gold d20
-- (_files/Resources.xaml) whose face carries the load's percentage - and never the game's spinner.
-- The rule everywhere else: test:startup/loading/d20.
-- Read from the live interface (Ext.UI, in the interface's own context) as soon as the bridge first
-- answers, in the client's LoadModule, and on until the loading screen is gone.
-- Venue: a start of its own ending when the bridge first answers (`until_lua`); the bridge mod
-- Weave, enabled in every copy, carries the file (`files`). Class: observation.
-- Run: gatekit --instance <copy> test tests -k startup/loading/d20_first_start

test { until_lua = true,
       files = { ["Data/Mods/Gwynerva_Weave/GUI/Resources/Resources.xaml"] = "Resources.xaml" },
       about = "On the first start the loading screen draws the mod's d20 with the load's percentage from its first frame, never the game's spinner." }

local D = require("_d20")

function run(game)
    local readings, photo
    step("the interface read from the bridge's first answer until the loading screen is gone", "observation", function()
        -- until the start's load ends: its `loading_ended` row (the client enters Menu; the
        -- timeline's LoadingScreenHidden is in that tick)
        readings, photo = D.watch(game, { since = game.mark(), seconds = 90, shot = "d20_first_start.jpg",
            photo = function(r) return D.screen(r) and #D.dice(r, D.SCREEN.widget) > 0 end })
        game.save("readings.txt", fmt(readings) .. "\n")
    end)
    step("the mods' interface is applied before the first loading screen is made", "observation", function()
        local t = game.lua("client", [[
            local at = {}
            for _, r in ipairs(Ext.StartupVideos.Timeline()) do
                if r.What == "State" and r.To == "StartLoading" and not at.start then at.start = r.Ms end
                if r.What == "EarlyModInterface" and not at.merged then at.merged = r.Ms end
                if r.What == "LoadingScreenShown" and not at.shown then at.shown = r.Ms end
            end
            return at]])
        check(t.merged, "the extender ran the game's merge of the mods' interface at the first StartLoading", t)
        check(t.start and t.merged >= t.start, "after StartLoading was entered", t)
    end)
    step("the mod's d20 from the first reading to the end, never the game's spinner, the percentage on its face", "observation", function()
        local first, game_spinner, dice, screens = nil, 0, 0, 0
        for i, r in ipairs(readings) do
            if D.screen(r) then
                screens = screens + 1
                local d = D.dice(r, D.SCREEN.widget)[1]
                if d then
                    first = first or i
                    dice = dice + 1
                    eq(#D.spinners(r, D.SCREEN.widget), 0, "with the d20 the game's spinner is not drawn", r)
                    if r.percent then
                        eq(d.face, r.percent, "the face shows the percentage the loading screen prints itself", r)
                    end
                elseif #D.spinners(r, D.SCREEN.widget) > 0 then
                    game_spinner = game_spinner + 1
                end
            end
        end
        check(first, "the loading screen drew the mod's d20", #readings)
        check(dice >= 3, "the d20 was read on the screen at least three times", dice)
        eq(game_spinner, 0, "the game's spinner was never read on the screen")
        check(photo, "a picture of the d20 on the first start was taken")
    end)
end
