-- ecl::tutorial::CameraMovingComponent: the shortest way to make it appear again.
-- Claim: One tap of W with tutorials on makes CameraMovingComponent appear (client).
-- Backed by: repeated, reached by play, in GustavX:WLD_Main_A
-- Still unresolved: Rotating (Q held) and AtLeashLimit (D held ~10s) were lit by HOLDING keys via Ext.Input.InjectKeyDown(GK.ScanCode(k)); a key tap does not move the camera, and a recipe step cannot hold a key: wish for a hold step {key, hold}.
-- Recorded 2026-11-14. Replay: gatekit --instance <copy> test tests/_coverage -k camera_move_key_w

test { level = "GustavX:WLD_Main_A" }

function run(game)
    game.wait_state("Running", "server", 120)
    -- Watched from before the first step, so a component that lives one frame is seen too.
    watch("client", "ecl::tutorial::CameraMovingComponent")

    game.key("w")

    game.sleep(1)

    local seen = alive("client", "ecl::tutorial::CameraMovingComponent", 10)
    print("ecl::tutorial::CameraMovingComponent alive on client: " .. fmt(seen))
end
