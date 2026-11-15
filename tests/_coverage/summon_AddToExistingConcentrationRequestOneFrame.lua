-- esv::summon::AddToExistingConcentrationRequestOneFrameComponent: the shortest way to make it appear again.
-- Claim: One frame on the new summon when a Summon functor with ExtendExistingConcentration=true runs (Target_Moonbeam_Move, the spell that moves a running Moonbeam). Found by listing every Summon functor in the stats via Ext.Stats (fields LateJoinPenalty, ExtendExistingConcentration), then casting the one with the flag. Not produced by the first Moonbeam or by Conjure Woodland Beings (those give AddConcentrationRequestOneFrame instead).
-- Backed by: repeated, reached by cheat, in GustavX:SCL_Main_A
-- Still unresolved: bg3_verify: no layout in the build matches that prefix (not compared); bg3_probe: nothing alive after the frame. Fields not read. Fired with and without combat (first time in a fight).
-- Recorded 2026-11-14. Replay: gatekit --instance <copy> test tests/_coverage -k summon_AddToExistingConcentrationRequestOneFrame

test { level = "GustavX:SCL_Main_A" }

function run(game)
    game.wait_state("Running", "server", 120)
    -- Watched from before the first step, so a component that lives one frame is seen too.
    watch("server", "esv::summon::AddToExistingConcentrationRequestOneFrameComponent")

    game.lua("server", [[
local A=GK.Stage.Party()[1]; Osi.AddBoosts(A,"ActionResource(SpellSlot,3,2)","gk","gk"); Osi.AddBoosts(A,"ActionResource(ActionPoint,3,0)","gk","gk"); GK.Stage.Teach("Target_Moonbeam",A); GK.Stage.Teach("Target_Moonbeam_Move",A); local p=GK.Stage.Position(A); GK.Stage.Cast("Target_Moonbeam",{p[1]+3,p[2],p[3]},A)
]])
    game.sleep(4)

    game.lua("server", [[
local A=GK.Stage.Party()[1]; local p=GK.Stage.Position(A); GK.Stage.Cast("Target_Moonbeam_Move",{p[1]-3,p[2],p[3]},A)
]])
    game.sleep(3)

    local seen = alive("server", "esv::summon::AddToExistingConcentrationRequestOneFrameComponent", 10)
    print("esv::summon::AddToExistingConcentrationRequestOneFrameComponent alive on server: " .. fmt(seen))
end
