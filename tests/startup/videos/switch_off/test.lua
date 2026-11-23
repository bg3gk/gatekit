-- The player's switch: `"StartupVideos": false` in Script Extender's settings hides every entry,
-- whatever the mods declare (the fork's Docs/API.md, "Startup Videos"). The bridge declares a clip
-- of its own here, and the declaration is found and still plays nothing: the fork writes no entry
-- into the game's playlist, the player starts nothing, and the client leaves StartLoading at once
-- (_start.lua). Every start that does not ask for the videos runs this way (`up` writes the switch
-- false unless --startup-videos).
-- Venue: a start of its own with the switch off (no `startup_videos`), the declaration and one of
-- our clips put into the copy before it (`files`), ending when the bridge first answers
-- (`until_lua`). Class: observation.
-- Run: gatekit --instance <copy> test tests -k startup/videos/switch_off

test { until_lua = true,
       files = {
           ["Data/Video/GK_ONE.bk2"] = "ONE.bk2",
           ["Data/Mods/Gwynerva_GateKit/ScriptExtender/StartupVideos.json"] = "StartupVideos.json",
       },
       about = "With the player's switch off the start plays no video, whatever the mods declare, and the game goes on at once." }

local S = require("_start")

function run(game)
    step("a declaration under the switch off, as the start played it", "observation", function()
        local r = S.read(game)
        game.save("start.txt", fmt(r) .. "\n")
        check(r.enabled == false, "the player's switch is off", r.enabled)
        eq(r.game, { S.GAME }, "the game's own entries were met")
        check(r.mods.Gwynerva_GateKit and r.mods.Gwynerva_GateKit.declared, "the bridge's declaration was found", r.mods)
        eq(r.list, {}, "the composition: nothing")
        S.played(r, {})
    end)
end
