-- A mod's own waiting indicator replaces the game's wherever the game waits, and on the loading
-- screen it carries the load's percentage. The mod defines the key `BusyElement` in its
-- GUI/Resources/Resources.xaml (_files/Resources.xaml: a gold d20 drawn with Paths, breathing and
-- rolling on a Storyboard); the game merges every enabled mod's Resources.xaml into the
-- interface's resources when the mods' interface is applied (code:ui::ModManager::RegisterMods,
-- early in LoadMenu), and its waiting widgets draw their spinner from that key - the loading
-- screen beside its percentage, the saving notice (Saving) beside its word. The die's face binds
-- the widget's `Progress` through the game's converter class (ls:FloatToPercentStringConverter),
-- as the loading screen's own percentage does; where the widget's data has no `Progress` the
-- binding falls back to a 20, the die's own face (research/studies/startup-videos.md, "The loading
-- screen"). The first start's screen: test:startup/loading/d20_first_start.
-- Read from the live interface (Ext.UI, the tree under the root, in the interface's own context):
-- the elements the template names (GK_D20, GK_D20_Face) and the widget each lies in, the game's
-- own spinner images (ico_spinner) and the loading screen's own percentage text (LoadingProgress).
-- Venue: a fresh game in the creche (CRE_Main_A); the party moves to Act 1 (a cheat: the stage's
-- level move) and the interface is read while the load lasts; then a quick save (fair). The bridge
-- mod Weave, enabled in every copy, carries the file (`files`, a loose file in its folder).
-- Run: gatekit --instance <copy> test tests -k startup/loading/d20

test { level = "GustavX:CRE_Main_A",
       files = { ["Data/Mods/Gwynerva_Weave/GUI/Resources/Resources.xaml"] = "Resources.xaml" },
       about = "A mod's BusyElement replaces the game's spinner wherever the game waits; on the loading screen its face carries the load's percentage, elsewhere a 20." }

local D = require("_d20")

function run(game)
    game.wait_state("Running", "client", 120)
    local before = game.mark()
    step("the party moved to Act 1", "cheat", function()
        check(game.server("return GK.Stage.Level('WLD_Main_A')"), "the level move asked")
    end)
    local readings, photo
    step("the interface read while the load lasts", "observation", function()
        game.wait_row(before, { "loading" }, nil, 60)
        readings, photo = D.watch(game, { since = before, seconds = 180, shot = "d20_load.jpg",
            photo = function(r) return D.screen(r) and #D.dice(r, D.SCREEN.widget) > 0 end })
        game.save("load_readings.txt", fmt(readings) .. "\n")
    end)
    step("the loading screen draws the mod's d20, not the game's spinner, and its face carries the load's percentage", "observation", function()
        local seen, scales = 0, {}
        for _, r in ipairs(readings) do
            if D.screen(r) and r.percent then
                seen = seen + 1
                local dice = D.dice(r, D.SCREEN.widget)
                eq(#dice, 1, "the loading screen draws one d20 of the mod", r)
                eq(#D.spinners(r, D.SCREEN.widget), 0, "the game's own spinner is not drawn", r)
                local d = dice[1]
                check(type(d.face) == "string" and d.face:match("^%d+%%$"), "the face shows a percentage", d)
                eq(d.face, r.percent, "the face shows the percentage the loading screen prints itself", r)
                check(type(d.progress) == "number", "the face is bound to the screen's Progress", d)
                scales[string.format("%.3f", d.scale or 0)] = true
            end
        end
        check(seen >= 3, "the loading screen was read at least three times during the load", #readings)
        check(#keys(scales) >= 2, "the die breathes: its scale changes between readings", scales)
        check(photo, "a picture of the load was taken")
    end)
    step("the load ended", "observation", function()
        game.wait_row(before, { "loading_ended" }, nil, 180)
        game.wait_state("Running", "client", 120)
    end)
    local saving = game.mark()
    step("a quick save", "fair", function()
        local r = game.client("return GK.Gui.QuickSave()")
        check(r and r.ok, "the quick save ran", r)
    end)
    step("the saving notice draws the d20 too, with a 20 on its face", "observation", function()
        -- read from the save's begin until the d20 is seen in the notice or the save has ended
        local found
        local saves = D.watch(game, { since = saving, ends = { "saving_ended" }, seconds = 20, shot = "d20_saving.jpg",
            photo = function(r) return #D.dice(r, "Saving") > 0 end,
            done = function(r) found = D.dice(r, "Saving")[1] and r return found end })
        game.save("saving_readings.txt", fmt(saves) .. "\n")
        check(found, "the saving notice (Saving) was read with a d20 in it", #saves)
        local d = D.dice(found, "Saving")[1]
        eq(d.face, "20", "where the data has no Progress the face shows 20", d)
        eq(#D.spinners(found, "Saving"), 0, "the game's own spinner is not drawn in the saving notice", found)
        -- the test ends with the save: its end is the game's row
        game.wait_attention({ "saving_ended" }, 60, saving)
    end)
end
