-- eoc::WeightBoostComponent, eoc::WeightBoostComponent: made to appear by a sweep step, no model in the loop.
-- Claim: boost Weight (Weight(100)) through passive EnlargeWeightLarge makes them appear on a fresh dummy.
-- Reached by cheat (AddBoosts Weight(100)), in GustavX:Basic_Level_A; plan `boosts`, step boost:Weight.
-- Written 2026-11-14 by `gatekit sweep run`. Replay: gatekit --instance <copy> test tests/_coverage -k sweep_boosts_boost_Weight

test { level = "GustavX:Basic_Level_A" }

function run(game)
    game.wait_state("Running", "server", 120)
    -- Watched from before the first step, so a component that lives one frame is seen too.
    watch("client", "eoc::WeightBoostComponent")
    watch("server", "eoc::WeightBoostComponent")

    -- A fresh dummy (a temporary neutral human) for the step to work on.
    game.lua("server", [[
SweepDummy = GK.Stage.Spawn{ template = "human", faction = "neutral", distance = 5 }[1]
return { SweepDummy, SweepTarget }
]])

    game.wait("server", [[
GK.Stage.Ready({ SweepDummy, SweepTarget })
]], 20)

    -- The sweep's step boost:Weight: AddBoosts Weight(100) (a cheat: the cause is staged, no player acts).
    game.lua("server", [[
local D, T = SweepDummy, SweepTarget
Osi.AddBoosts(D, "Weight(100)", "GateKitSweep", D)
]], 20)
    game.sleep(1.5)

    local seen = alive("client", "eoc::WeightBoostComponent", 10)
    print("eoc::WeightBoostComponent alive on client: " .. fmt(seen))
    seen = alive("server", "eoc::WeightBoostComponent", 1)
    print("eoc::WeightBoostComponent alive on server: " .. fmt(seen))

    -- Leave nothing behind for the next replay.
    game.lua("server", [[
local D, T = SweepDummy, SweepTarget
pcall(Osi.RemoveBoosts, D, "Weight(100)", 0, "GateKitSweep", D)
for _, id in ipairs({ D, T }) do pcall(GK.Stage.Remove, id) end
SweepDummy, SweepTarget = nil, nil
return true
]])
end
