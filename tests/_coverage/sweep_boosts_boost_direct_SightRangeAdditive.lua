-- eoc::SightRangeAdditiveBoostComponent, eoc::SightRangeAdditiveBoostComponent: made to appear by a sweep step, no model in the loop.
-- Claim: eoc::SightRangeAdditiveBoostComponent: no shipping entry names a boost for it; AddBoosts tries SightRangeAdditive(), SightRangeAdditive(1), SightRangeAdditive(true), SightRangeAdditive(Strength), SightRangeAdditive(1,1) makes them appear on a fresh dummy.
-- Reached by cheat (direct SightRangeAdditive();SightRangeAdditive(1);SightRangeAdditive(true);SightRangeAdditive(Strength);SightRangeAdditive(1,1)), in GustavX:Basic_Level_A; plan `boosts`, step boost-direct:SightRangeAdditive.
-- Written 2026-11-14 by `gatekit sweep run`. Replay: gatekit --instance <copy> test tests/_coverage -k sweep_boosts_boost_direct_SightRangeAdditive

test { level = "GustavX:Basic_Level_A" }

function run(game)
    game.wait_state("Running", "server", 120)
    -- Watched from before the first step, so a component that lives one frame is seen too.
    watch("client", "eoc::SightRangeAdditiveBoostComponent")
    watch("server", "eoc::SightRangeAdditiveBoostComponent")

    -- A fresh dummy (a temporary neutral human) for the step to work on.
    game.lua("server", [[
SweepDummy = GK.Stage.Spawn{ template = "human", faction = "neutral", distance = 5 }[1]
return { SweepDummy, SweepTarget }
]])

    game.wait("server", [[
GK.Stage.Ready({ SweepDummy, SweepTarget })
]], 20)

    -- The sweep's step boost-direct:SightRangeAdditive: direct SightRangeAdditive();SightRangeAdditive(1);SightRangeAdditive(true);SightRangeAdditive(Strength);SightRangeAdditive(1,1) (a cheat: the cause is staged, no player acts).
    game.lua("server", [[
local D, T = SweepDummy, SweepTarget
Osi.AddBoosts(D, "SightRangeAdditive()", "GateKitSweep", D)
Osi.AddBoosts(D, "SightRangeAdditive(1)", "GateKitSweep", D)
Osi.AddBoosts(D, "SightRangeAdditive(true)", "GateKitSweep", D)
Osi.AddBoosts(D, "SightRangeAdditive(Strength)", "GateKitSweep", D)
Osi.AddBoosts(D, "SightRangeAdditive(1,1)", "GateKitSweep", D)
]], 20)
    game.sleep(1.5)

    local seen = alive("client", "eoc::SightRangeAdditiveBoostComponent", 10)
    print("eoc::SightRangeAdditiveBoostComponent alive on client: " .. fmt(seen))
    seen = alive("server", "eoc::SightRangeAdditiveBoostComponent", 1)
    print("eoc::SightRangeAdditiveBoostComponent alive on server: " .. fmt(seen))

    -- Leave nothing behind for the next replay.
    game.lua("server", [[
local D, T = SweepDummy, SweepTarget
pcall(Osi.RemoveBoosts, D, "SightRangeAdditive()", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "SightRangeAdditive(1)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "SightRangeAdditive(true)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "SightRangeAdditive(Strength)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "SightRangeAdditive(1,1)", 0, "GateKitSweep", D)
for _, id in ipairs({ D, T }) do pcall(GK.Stage.Remove, id) end
SweepDummy, SweepTarget = nil, nil
return true
]])
end
