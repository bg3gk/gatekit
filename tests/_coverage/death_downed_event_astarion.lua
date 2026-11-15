-- esv::death::DownedEventOneFrameComponent: the shortest way to make it appear again.
-- Claim: One frame when a party member is brought to 0 HP and goes Downed (not dead): Osi.IsDead 0, HP 0, DOWNED status 1.
-- Backed by: repeated, reached by cheat, in GustavX:WLD_Main_A
-- Still unresolved: ["Fields not read."]
-- Recorded 2026-11-14. Replay: gatekit --instance <copy> test tests/_coverage -k death_downed_event_astarion

test { level = "GustavX:WLD_Main_A" }

function run(game)
    game.wait_state("Running", "server", 120)
    -- Watched from before the first step, so a component that lives one frame is seen too.
    watch("server", "esv::death::DownedEventOneFrameComponent")

    game.lua("server", [[
local h=Osi.GetHostCharacter(); return GK.Stage.Damage(9999, h, nil, true)
]])

    game.sleep(2)

    local seen = alive("server", "esv::death::DownedEventOneFrameComponent", 10)
    print("esv::death::DownedEventOneFrameComponent alive on server: " .. fmt(seen))
end
