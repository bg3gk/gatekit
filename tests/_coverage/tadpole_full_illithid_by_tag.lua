-- eoc::tadpole_tree::FullIllithidComponent: the shortest way to make it appear again.
-- Claim: Story tag FullIllithid (3797bfc4-...) on the host makes FullIllithidComponent appear on server and client. Absent before the tag, from a reset.
-- Backed by: repeated (two captures from reset: lit both times); verify: no layout in the build, nothing comparable, reached by cheat, in GustavX:SCL_Main_A
-- Still unresolved: the game own route not played
-- Recorded 2026-11-14. Replay: gatekit --instance <copy> test tests/_coverage -k tadpole_full_illithid_by_tag

test { level = "GustavX:SCL_Main_A" }

function run(game)
    game.wait_state("Running", "server", 120)
    -- Watched from before the first step, so a component that lives one frame is seen too.
    watch("server", "eoc::tadpole_tree::FullIllithidComponent")

    game.lua("server", [[
Osi.SetTag(Osi.GetHostCharacter(),"3797bfc4-8004-4a19-9578-61ce0714cc0b")
]])

    game.sleep(2)

    local seen = alive("server", "eoc::tadpole_tree::FullIllithidComponent", 10)
    print("eoc::tadpole_tree::FullIllithidComponent alive on server: " .. fmt(seen))
end
