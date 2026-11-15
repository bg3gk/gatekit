-- eoc::CanSeeThroughBoostComponent, eoc::CanSeeThroughBoostComponent: made to appear by a sweep step, no model in the loop.
-- Claim: eoc::CanSeeThroughBoostComponent: no shipping entry names a boost for it; AddBoosts tries CanSeeThrough(), CanSeeThrough(1), CanSeeThrough(true), CanSeeThrough(Strength), CanSeeThrough(1,1) makes them appear on a fresh dummy.
-- Reached by cheat (direct CanSeeThrough();CanSeeThrough(1);CanSeeThrough(true);CanSeeThrough(Strength);CanSeeThrough(1,1)), in GustavX:Basic_Level_A; plan `boosts`, step boost-direct:CanSeeThrough.
-- Written 2026-11-14 by `gatekit sweep run`. Replay: gatekit --instance <copy> test tests/_coverage -k sweep_boosts_boost_direct_CanSeeThrough

test { level = "GustavX:Basic_Level_A" }

function run(game)
    game.wait_state("Running", "server", 120)
    -- Watched from before the first step, so a component that lives one frame is seen too.
    watch("client", "eoc::CanSeeThroughBoostComponent")
    watch("server", "eoc::CanSeeThroughBoostComponent")

    -- A fresh dummy (a temporary neutral human) for the step to work on.
    game.lua("server", [[
SweepDummy = GK.Stage.Spawn{ template = "human", faction = "neutral", distance = 5 }[1]
return { SweepDummy, SweepTarget }
]])

    game.wait("server", [[
GK.Stage.Ready({ SweepDummy, SweepTarget })
]], 20)

    -- The sweep's step boost-direct:CanSeeThrough: direct CanSeeThrough();CanSeeThrough(1);CanSeeThrough(true);CanSeeThrough(Strength);CanSeeThrough(1,1) (a cheat: the cause is staged, no player acts).
    game.lua("server", [[
local D, T = SweepDummy, SweepTarget
Osi.AddBoosts(D, "CanSeeThrough()", "GateKitSweep", D)
Osi.AddBoosts(D, "CanSeeThrough(1)", "GateKitSweep", D)
Osi.AddBoosts(D, "CanSeeThrough(true)", "GateKitSweep", D)
Osi.AddBoosts(D, "CanSeeThrough(Strength)", "GateKitSweep", D)
Osi.AddBoosts(D, "CanSeeThrough(1,1)", "GateKitSweep", D)
]], 20)
    game.sleep(1.5)

    local seen = alive("client", "eoc::CanSeeThroughBoostComponent", 10)
    print("eoc::CanSeeThroughBoostComponent alive on client: " .. fmt(seen))
    seen = alive("server", "eoc::CanSeeThroughBoostComponent", 1)
    print("eoc::CanSeeThroughBoostComponent alive on server: " .. fmt(seen))

    -- Leave nothing behind for the next replay.
    game.lua("server", [[
local D, T = SweepDummy, SweepTarget
pcall(Osi.RemoveBoosts, D, "CanSeeThrough()", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "CanSeeThrough(1)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "CanSeeThrough(true)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "CanSeeThrough(Strength)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "CanSeeThrough(1,1)", 0, "GateKitSweep", D)
for _, id in ipairs({ D, T }) do pcall(GK.Stage.Remove, id) end
SweepDummy, SweepTarget = nil, nil
return true
]])
end
