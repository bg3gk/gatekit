-- eoc::tadpole_tree::HalfIllithidComponent: the shortest way to make it appear again.
-- Claim: Story tag HalfIllithid (c0cd4ed8-...) on the host makes HalfIllithidComponent appear on server and client (and ecl PowersChanged one-frame on client). Not alive before the tag, from a reset.
-- Backed by: repeated (two captures from reset: lit both times, absent before); verify: no layout in the build, nothing comparable, reached by cheat, in GustavX:SCL_Main_A
-- Still unresolved: the game own route (Emperor tadpole consumption) not played
-- Recorded 2026-11-14. Replay: gatekit --instance <copy> test tests/_coverage -k tadpole_half_illithid_by_tag

test { level = "GustavX:SCL_Main_A" }

function run(game)
    game.wait_state("Running", "server", 120)
    -- Watched from before the first step, so a component that lives one frame is seen too.
    watch("server", "eoc::tadpole_tree::HalfIllithidComponent")

    game.lua("server", [[
Osi.SetTag(Osi.GetHostCharacter(),"c0cd4ed8-11d1-4fb1-ae3a-3a14e41267c8")
]])

    game.sleep(2)

    local seen = alive("server", "eoc::tadpole_tree::HalfIllithidComponent", 10)
    print("eoc::tadpole_tree::HalfIllithidComponent alive on server: " .. fmt(seen))
end
