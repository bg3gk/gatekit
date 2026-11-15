-- esv::hit::RedirectionTrackingComponent: the shortest way to make it appear again.
-- Claim: Warding Bond cast by the host on a spawned ally, then damage to the ally, brings RedirectionTracking alive.
-- Backed by: repeated, reached by cheat, in GustavX:WLD_Main_A
-- Still unresolved: bg3_verify server esv::hit::Redirection: 1 layout agreed, 0 disagreed. Caveat: in the fresh capture First=1 (seen on the first tick), so it may have been alive from the cast rather than from the damage; not separated. Fields not read.
-- Recorded 2026-11-14. Replay: gatekit --instance <copy> test tests/_coverage -k esv_hit_RedirectionTracking

test { level = "GustavX:WLD_Main_A" }

function run(game)
    game.wait_state("Running", "server", 120)
    -- Watched from before the first step, so a component that lives one frame is seen too.
    watch("server", "esv::hit::RedirectionTrackingComponent")

    game.lua("server", [[
local H=Osi.GetHostCharacter(); Osi.AddSpell(H,"Target_WardingBond",0,0); local p=GK.See.Position(H); _G.GKW=GK.Stage.Spawn{template="human",at={p[1]+3,p[2],p[3]},faction="ally"}[1]
]])

    game.sleep(2)

    game.lua("server", [[
Osi.UseSpell(Osi.GetHostCharacter(),"Target_WardingBond",_G.GKW)
]])

    game.sleep(3)

    game.lua("server", [[
Osi.ApplyDamage(_G.GKW,3,"Slashing",Osi.GetHostCharacter())
]])

    game.sleep(2)

    local seen = alive("server", "esv::hit::RedirectionTrackingComponent", 10)
    print("esv::hit::RedirectionTrackingComponent alive on server: " .. fmt(seen))
end
