-- eoc::DamageTakenBonusBoostComponent, eoc::DamageTakenBonusBoostComponent: made to appear by a sweep step, no model in the loop.
-- Claim: boost DamageTakenBonus (DamageTakenBonus(2)) through status GAPING_WOUND makes them appear on a fresh dummy.
-- Reached by cheat (AddBoosts DamageTakenBonus(2)), in GustavX:Basic_Level_A; plan `boosts`, step boost:DamageTakenBonus.
-- Written 2026-11-14 by `gatekit sweep run`. Replay: gatekit --instance <copy> test tests/_coverage -k sweep_boosts_boost_DamageTakenBonus

test { level = "GustavX:Basic_Level_A" }

function run(game)
    game.wait_state("Running", "server", 120)
    -- Watched from before the first step, so a component that lives one frame is seen too.
    watch("client", "eoc::DamageTakenBonusBoostComponent")
    watch("server", "eoc::DamageTakenBonusBoostComponent")

    -- A fresh dummy (a temporary neutral human) for the step to work on.
    game.lua("server", [[
SweepDummy = GK.Stage.Spawn{ template = "human", faction = "neutral", distance = 5 }[1]
return { SweepDummy, SweepTarget }
]])

    game.wait("server", [[
GK.Stage.Ready({ SweepDummy, SweepTarget })
]], 20)

    -- The sweep's step boost:DamageTakenBonus: AddBoosts DamageTakenBonus(2) (a cheat: the cause is staged, no player acts).
    game.lua("server", [[
local D, T = SweepDummy, SweepTarget
Osi.AddBoosts(D, "DamageTakenBonus(2)", "GateKitSweep", D)
]], 20)
    game.sleep(1.5)

    local seen = alive("client", "eoc::DamageTakenBonusBoostComponent", 10)
    print("eoc::DamageTakenBonusBoostComponent alive on client: " .. fmt(seen))
    seen = alive("server", "eoc::DamageTakenBonusBoostComponent", 1)
    print("eoc::DamageTakenBonusBoostComponent alive on server: " .. fmt(seen))

    -- Leave nothing behind for the next replay.
    game.lua("server", [[
local D, T = SweepDummy, SweepTarget
pcall(Osi.RemoveBoosts, D, "DamageTakenBonus(2)", 0, "GateKitSweep", D)
for _, id in ipairs({ D, T }) do pcall(GK.Stage.Remove, id) end
SweepDummy, SweepTarget = nil, nil
return true
]])
end
