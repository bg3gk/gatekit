-- esv::character::ReevaluatePartyRequestOneFrameComponent: the shortest way to make it appear again.
-- Claim: Osi.RemovePartyFollower of a follower (after AddPartyFollower) lights ReevaluatePartyRequestOneFrame on the server; AddPartyFollower alone did not in the first capture, only on the fresh-capture repeat of the remove.
-- Backed by: repeated, reached by cheat, in GustavX:WLD_Main_A
-- Still unresolved: ["Fields not read (one-frame)."]
-- Recorded 2026-11-14. Replay: gatekit --instance <copy> test tests/_coverage -k character_ReevaluatePartyRequestOneFrame

test { level = "GustavX:WLD_Main_A" }

function run(game)
    game.wait_state("Running", "server", 120)
    -- Watched from before the first step, so a component that lives one frame is seen too.
    watch("server", "esv::character::ReevaluatePartyRequestOneFrameComponent")

    game.lua("server", [[
Osi.AddPartyFollower("3ed74f06-3c60-42dc-83f6-f034cb47c679", Osi.GetHostCharacter())
]])

    game.sleep(3)

    game.lua("server", [[
Osi.RemovePartyFollower("3ed74f06-3c60-42dc-83f6-f034cb47c679", Osi.GetHostCharacter())
]])

    game.sleep(4)

    local seen = alive("server", "esv::character::ReevaluatePartyRequestOneFrameComponent", 10)
    print("esv::character::ReevaluatePartyRequestOneFrameComponent alive on server: " .. fmt(seen))
end
