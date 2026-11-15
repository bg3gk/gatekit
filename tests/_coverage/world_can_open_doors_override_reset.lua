-- esv::character::CanOpenDoorsOverrideComponent: the shortest way to make it appear again.
-- Claim: Set to 1 then 0 leaves the component in place with CanOpenDoors=false (overwrite).
-- Backed by: repeated, reached by cheat, in GustavX:WLD_Main_A
-- Recorded 2026-11-14. Replay: gatekit --instance <copy> test tests/_coverage -k world_can_open_doors_override_reset

test { level = "GustavX:WLD_Main_A" }

function run(game)
    game.wait_state("Running", "server", 120)
    -- Watched from before the first step, so a component that lives one frame is seen too.
    watch("server", "esv::character::CanOpenDoorsOverrideComponent")

    game.lua("server", [[
Osi.SetCanOpenDoorsOverride(Osi.GetHostCharacter(), 1)
]])
    game.sleep(1)

    game.lua("server", [[
Osi.SetCanOpenDoorsOverride(Osi.GetHostCharacter(), 0)
]])
    game.sleep(1)

    local seen = alive("server", "esv::character::CanOpenDoorsOverrideComponent", 10)
    print("esv::character::CanOpenDoorsOverrideComponent alive on server: " .. fmt(seen))
end
