-- Two enabled mods declare startup videos, and one puts its entries around all of the other's:
-- `Before = "<owner>:*"` is before every entry of that mod, `After = "<owner>:*"` after all of them
-- (the fork's Docs/API.md, "Startup Videos"). The two mods are the bridge's own pair, both enabled
-- in every copy: Weave, loaded first, adds A1 (TWO) and A2 (THREE); the bridge hides the game's
-- entry and adds B1 (ONE) before all of Weave's and B2 (FOUR) after them. Without the constraints
-- the list would be TWO, THREE, ONE, FOUR; with them it is ONE, TWO, THREE, FOUR, and the start
-- plays it so (_start.lua). A mod folder lying loose under Data/Mods with its meta.lsx and a
-- declaration, but not in the mod list, is no mod: the fork reads only the mods the game's mod list
-- enables, and its declaration plays nothing.
-- The code: each clip is found at Video/<name>.bk2 by code:unplaced::FindVideoFile, and the names
-- of the one entry play in turn from the player's queue (code:ecl::GameControl::ShowMovie,
-- code:ecl::VideoManager.QueueCount).
-- Venue: a start of its own with the startup videos on (`startup_videos`), the declarations, the
-- loose folder and four of our clips put into the copy before it (`files`), ending when the bridge
-- first answers (`until_lua`). Class: observation.
-- Run: gatekit --instance <copy> test tests -k startup/videos/two_mods

test { until_lua = true, startup_videos = true,
       files = {
           ["Data/Video/GK_ONE.bk2"] = "ONE.bk2",
           ["Data/Video/GK_TWO.bk2"] = "TWO.bk2",
           ["Data/Video/GK_THREE.bk2"] = "THREE.bk2",
           ["Data/Video/GK_FOUR.bk2"] = "FOUR.bk2",
           ["Data/Mods/Gwynerva_Weave/ScriptExtender/StartupVideos.json"] = "weave.json",
           ["Data/Mods/Gwynerva_GateKit/ScriptExtender/StartupVideos.json"] = "gatekit.json",
           ["Data/Mods/GateKit_Loose/meta.lsx"] = "loose_meta.lsx",
           ["Data/Mods/GateKit_Loose/ScriptExtender/StartupVideos.json"] = "loose.json",
       },
       about = "A mod's Before and After another mod's \"<owner>:*\" put its entries around all of that mod's; a loose mod folder outside the mod list declares nothing." }

local S = require("_start")
local entry = S.entry

function run(game)
    step("two mods' declarations, as the start played them", "observation", function()
        local r = S.read(game)
        game.save("start.txt", fmt(r) .. "\n")
        check(r.enabled, "the player's switch is on (startup_videos)", r.enabled)
        check(r.mods.Gwynerva_Weave and r.mods.Gwynerva_Weave.declared and r.mods.Gwynerva_Weave.uuid == S.WEAVE,
            "Weave's declaration was found, under its UUID", r.mods)
        check(r.mods.Gwynerva_GateKit and r.mods.Gwynerva_GateKit.declared, "the bridge's declaration was found", r.mods)
        check(r.mods.GateKit_Loose == nil, "the loose folder is not among the enabled mods", r.mods)
        eq(r.list, { entry(S.BRIDGE, "B1", "GK_ONE"), entry(S.WEAVE, "A1", "GK_TWO"), entry(S.WEAVE, "A2", "GK_THREE"),
            entry(S.BRIDGE, "B2", "GK_FOUR") }, "the composition: the bridge's around all of Weave's")
        eq(r.warnings, {}, "no warnings")
        S.played(r, { "GK_ONE", "GK_TWO", "GK_THREE", "GK_FOUR" })
    end)
end
