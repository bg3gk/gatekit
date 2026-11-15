-- eoc::HorizontalFOVOverrideBoostComponent, eoc::HorizontalFOVOverrideBoostComponent: made to appear by a sweep step, no model in the loop.
-- Claim: boost HorizontalFOVOverride (HorizontalFOVOverride(10)) through status ENTHRALL makes them appear on a fresh dummy.
-- Reached by cheat (status ENTHRALL), in GustavX:Basic_Level_A; plan `boosts`, step boost:HorizontalFOVOverride.
-- Written 2026-11-14 by `gatekit sweep run`. Replay: gatekit --instance <copy> test tests/_coverage -k sweep_boosts_boost_HorizontalFOVOverride

test { level = "GustavX:Basic_Level_A" }

function run(game)
    game.wait_state("Running", "server", 120)
    -- Watched from before the first step, so a component that lives one frame is seen too.
    watch("client", "eoc::HorizontalFOVOverrideBoostComponent")
    watch("server", "eoc::HorizontalFOVOverrideBoostComponent")

    -- A fresh dummy (a temporary neutral human) for the step to work on.
    game.lua("server", [[
SweepDummy = GK.Stage.Spawn{ template = "human", faction = "neutral", distance = 5 }[1]
return { SweepDummy, SweepTarget }
]])

    game.wait("server", [[
GK.Stage.Ready({ SweepDummy, SweepTarget })
]], 20)

    -- The sweep's step boost:HorizontalFOVOverride: status ENTHRALL (a cheat: the cause is staged, no player acts).
    game.lua("server", [[
local D, T = SweepDummy, SweepTarget
Osi.ApplyStatus(D, "ENTHRALL", -1.0, 1, D)
]], 20)
    game.sleep(1.5)

    local seen = alive("client", "eoc::HorizontalFOVOverrideBoostComponent", 10)
    print("eoc::HorizontalFOVOverrideBoostComponent alive on client: " .. fmt(seen))
    seen = alive("server", "eoc::HorizontalFOVOverrideBoostComponent", 1)
    print("eoc::HorizontalFOVOverrideBoostComponent alive on server: " .. fmt(seen))

    -- Leave nothing behind for the next replay.
    game.lua("server", [[
local D, T = SweepDummy, SweepTarget
pcall(Osi.RemoveStatus, D, "ENTHRALL")
for _, id in ipairs({ D, T }) do pcall(GK.Stage.Remove, id) end
SweepDummy, SweepTarget = nil, nil
return true
]])
end
