-- eoc::ArmorAbilityModifierCapOverrideBoostComponent, eoc::ArmorAbilityModifierCapOverrideBoostComponent: made to appear by a sweep step, no model in the loop.
-- Claim: boost ArmorAbilityModifierCapOverride (ArmorAbilityModifierCapOverride(Hide, 3)) through passive MagicInitiate_Cleric makes them appear on a fresh dummy.
-- Reached by cheat (passive MagicInitiate_Cleric), in GustavX:Basic_Level_A; plan `boosts`, step boost:ArmorAbilityModifierCapOverride.
-- Written 2026-11-14 by `gatekit sweep run`. Replay: gatekit --instance <copy> test tests/_coverage -k sweep_boosts_boost_ArmorAbilityModifierCapOverride

test { level = "GustavX:Basic_Level_A" }

function run(game)
    game.wait_state("Running", "server", 120)
    -- Watched from before the first step, so a component that lives one frame is seen too.
    watch("client", "eoc::ArmorAbilityModifierCapOverrideBoostComponent")
    watch("server", "eoc::ArmorAbilityModifierCapOverrideBoostComponent")

    -- A fresh dummy (a temporary neutral human) for the step to work on.
    game.lua("server", [[
SweepDummy = GK.Stage.Spawn{ template = "human", faction = "neutral", distance = 5 }[1]
return { SweepDummy, SweepTarget }
]])

    game.wait("server", [[
GK.Stage.Ready({ SweepDummy, SweepTarget })
]], 20)

    -- The sweep's step boost:ArmorAbilityModifierCapOverride: passive MagicInitiate_Cleric (a cheat: the cause is staged, no player acts).
    game.lua("server", [[
local D, T = SweepDummy, SweepTarget
Osi.AddPassive(D, "MagicInitiate_Cleric")
]], 20)
    game.sleep(1.5)

    local seen = alive("client", "eoc::ArmorAbilityModifierCapOverrideBoostComponent", 10)
    print("eoc::ArmorAbilityModifierCapOverrideBoostComponent alive on client: " .. fmt(seen))
    seen = alive("server", "eoc::ArmorAbilityModifierCapOverrideBoostComponent", 1)
    print("eoc::ArmorAbilityModifierCapOverrideBoostComponent alive on server: " .. fmt(seen))

    -- Leave nothing behind for the next replay.
    game.lua("server", [[
local D, T = SweepDummy, SweepTarget
pcall(Osi.RemovePassive, D, "MagicInitiate_Cleric")
for _, id in ipairs({ D, T }) do pcall(GK.Stage.Remove, id) end
SweepDummy, SweepTarget = nil, nil
return true
]])
end
