-- esv::summon::LateJoinPenaltyOneFrameComponent: the shortest way to make it appear again.
-- Claim: One frame on a summon whose Summon functor has LateJoinPenalty=true (Target_ObliterationOrb_Netherbrain) when it is cast in a fight already running; the same cast also gives esv::combat::LateJoinPenaltyComponent (float, 5.96 and 5.97 in the two instances seen, 4 bytes) which stays alive (bg3_probe read it). Only Target_ObliterationOrb_Netherbrain among all spells has the flag (Ext.Stats scan).
-- Backed by: repeated, reached by cheat, in GustavX:SCL_Main_A
-- Still unresolved: First cast right after Teach produced no summon (spell not yet in the book); the second did - hence two casts. esv::combat::LateJoinPenaltyComponent also new to the record. bg3_verify: no layout in the build matches that prefix for either; bg3_probe of the combat one shows a float differing per instance (+0x0). Not tried: whether the penalty needs the fight to be running (cast out of combat).
-- Recorded 2026-11-14. Replay: gatekit --instance <copy> test tests/_coverage -k summon_LateJoinPenaltyOneFrame

test { level = "GustavX:SCL_Main_A" }

function run(game)
    game.wait_state("Running", "server", 120)
    -- Watched from before the first step, so a component that lives one frame is seen too.
    watch("server", "esv::summon::LateJoinPenaltyOneFrameComponent")

    game.lua("server", [[
local A=GK.Stage.Party()[1]; GK.Stage.Quiet(); local e=GK.Stage.Spawn({template="goblin",distance=14}); GK.Stage.Fight(e,A); Osi.AddBoosts(A,"ActionResource(ActionPoint,3,0)","gk","gk"); GK.Stage.Teach("Target_ObliterationOrb_Netherbrain",A)
]])
    game.sleep(5)

    game.lua("server", [[
local A=GK.Stage.Party()[1]; local p=GK.Stage.Position(A); GK.Stage.Cast("Target_ObliterationOrb_Netherbrain",{p[1]+5,p[2],p[3]+1},A)
]])
    game.sleep(2)

    game.lua("server", [[
local A=GK.Stage.Party()[1]; local p=GK.Stage.Position(A); GK.Stage.Cast("Target_ObliterationOrb_Netherbrain",{p[1]-5,p[2],p[3]+1},A)
]])
    game.sleep(3)

    local seen = alive("server", "esv::summon::LateJoinPenaltyOneFrameComponent", 10)
    print("esv::summon::LateJoinPenaltyOneFrameComponent alive on server: " .. fmt(seen))
end
