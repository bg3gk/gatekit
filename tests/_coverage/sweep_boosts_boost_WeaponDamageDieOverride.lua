-- eoc::WeaponDamageDieOverrideBoostComponent, eoc::WeaponDamageDieOverrideBoostComponent: made to appear by a sweep step, no model in the loop.
-- Claim: boost WeaponDamageDieOverride (WeaponDamageDieOverride(3d8)) through status SHADOWBLADE_TECHNICAL_1 makes them appear on a fresh dummy.
-- Reached by cheat (status SHADOWBLADE_TECHNICAL_1), in GustavX:Basic_Level_A; plan `boosts`, step boost:WeaponDamageDieOverride.
-- Written 2026-11-14 by `gatekit sweep run`. Replay: gatekit --instance <copy> test tests/_coverage -k sweep_boosts_boost_WeaponDamageDieOverride

test { level = "GustavX:Basic_Level_A" }

function run(game)
    game.wait_state("Running", "server", 120)
    -- Watched from before the first step, so a component that lives one frame is seen too.
    watch("client", "eoc::WeaponDamageDieOverrideBoostComponent")
    watch("server", "eoc::WeaponDamageDieOverrideBoostComponent")

    -- A fresh dummy (a temporary neutral human) for the step to work on.
    game.lua("server", [[
SweepDummy = GK.Stage.Spawn{ template = "human", faction = "neutral", distance = 5 }[1]
return { SweepDummy, SweepTarget }
]])

    game.wait("server", [[
GK.Stage.Ready({ SweepDummy, SweepTarget })
]], 20)

    -- The sweep's step boost:WeaponDamageDieOverride: status SHADOWBLADE_TECHNICAL_1 (a cheat: the cause is staged, no player acts).
    game.lua("server", [[
local D, T = SweepDummy, SweepTarget
Osi.ApplyStatus(D, "SHADOWBLADE_TECHNICAL_1", -1.0, 1, D)
]], 20)
    game.sleep(1.5)

    local seen = alive("client", "eoc::WeaponDamageDieOverrideBoostComponent", 10)
    print("eoc::WeaponDamageDieOverrideBoostComponent alive on client: " .. fmt(seen))
    seen = alive("server", "eoc::WeaponDamageDieOverrideBoostComponent", 1)
    print("eoc::WeaponDamageDieOverrideBoostComponent alive on server: " .. fmt(seen))

    -- Leave nothing behind for the next replay.
    game.lua("server", [[
local D, T = SweepDummy, SweepTarget
pcall(Osi.RemoveStatus, D, "SHADOWBLADE_TECHNICAL_1")
for _, id in ipairs({ D, T }) do pcall(GK.Stage.Remove, id) end
SweepDummy, SweepTarget = nil, nil
return true
]])
end
