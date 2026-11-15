-- esv::character::InheritedFactionRequestOneFrameComponent: the shortest way to make it appear again.
-- Claim: Making Shadowheart a party follower of the host (Osi.AddPartyFollower, from anywhere on the level) lights InheritedFactionRequestOneFrame on the server, with eoc::party::FollowerComponent and esv::party::UpdatePartyFollowersRequestOneFrame. RemovePartyFollower lights it again.
-- Backed by: repeated, reached by cheat, in GustavX:WLD_Main_A
-- Still unresolved: ["Fields not read (one-frame, size 0 in the ledger).","Which entity carries it (the follower or the host) not checked."]
-- Recorded 2026-11-14. Replay: gatekit --instance <copy> test tests/_coverage -k character_InheritedFactionRequestOneFrame

test { level = "GustavX:WLD_Main_A" }

function run(game)
    game.wait_state("Running", "server", 120)
    -- Watched from before the first step, so a component that lives one frame is seen too.
    watch("server", "esv::character::InheritedFactionRequestOneFrameComponent")

    game.lua("server", [[
Osi.AddPartyFollower("3ed74f06-3c60-42dc-83f6-f034cb47c679", Osi.GetHostCharacter())
]])

    game.sleep(4)

    local seen = alive("server", "esv::character::InheritedFactionRequestOneFrameComponent", 10)
    print("esv::character::InheritedFactionRequestOneFrameComponent alive on server: " .. fmt(seen))
end
