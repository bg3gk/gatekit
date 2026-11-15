-- eoc::photo_mode::SessionComponent: the shortest way to make it appear again.
-- Claim: Pressing f9 in a fresh game enters photo mode, which brings eoc::photo_mode::SessionComponent alive on the client. Fields not read here.
-- Backed by: repeated, reached by play, in GustavX:WLD_Main_A
-- Still unresolved: Fields not read; written to prove that a recipe replays (wish 35).
-- Recorded 2026-11-14. Replay: gatekit --instance <copy> test tests/_coverage -k photo_mode_f9

test { level = "GustavX:WLD_Main_A" }

function run(game)
    game.wait_state("Running", "server", 120)
    -- Watched from before the first step, so a component that lives one frame is seen too.
    watch("client", "eoc::photo_mode::SessionComponent")

    -- the host is under control: photo mode is gated on the local player
    game.wait("client", [[
GK.Client().controlled
]], 60)

    -- the game's own photo-mode key
    game.key("f9")
    game.sleep(2)

    local seen = alive("client", "eoc::photo_mode::SessionComponent", 10)
    print("eoc::photo_mode::SessionComponent alive on client: " .. fmt(seen))
end
