-- eoc::swap_places::AnimationRequestComponent: the shortest way to make it appear again.
-- Claim: Benign Transposition cast by the host on a spawned ally makes the swap-places request, lifetime and animation request components appear for a frame or more.
-- Backed by: repeated, reached by cheat, in GustavX:WLD_Main_A
-- Still unresolved: bg3_verify (server, prefix esv::swap_places, record) said nothing_alive for 2 layouts, also with the verify sent right after the cast: one frame, could not be compared. eoc:: prefix: no layout in the build matches. Fields not read.
-- Recorded 2026-11-14. Replay: gatekit --instance <copy> test tests/_coverage -k eoc_swap_places_AnimationRequest

test { level = "GustavX:WLD_Main_A" }

function run(game)
    game.wait_state("Running", "server", 120)
    -- Watched from before the first step, so a component that lives one frame is seen too.
    watch("server", "eoc::swap_places::AnimationRequestComponent")

    game.lua("server", [[
local H=Osi.GetHostCharacter(); Osi.AddSpell(H,"Target_BenignTransposition_Teleport",0,0); local p=GK.See.Position(H); _G.GKU=GK.Stage.Spawn{template="human",at={p[1]+3,p[2],p[3]},faction="ally"}[1]
]])

    game.sleep(2)

    game.lua("server", [[
Osi.UseSpell(Osi.GetHostCharacter(),"Target_BenignTransposition_Teleport",_G.GKU)
]])

    game.sleep(2)

    local seen = alive("server", "eoc::swap_places::AnimationRequestComponent", 10)
    print("eoc::swap_places::AnimationRequestComponent alive on server: " .. fmt(seen))
end
