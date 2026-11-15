-- eoc::WeaponDamageTypeOverrideBoostComponent, eoc::WeaponDamageTypeOverrideBoostComponent: made to appear by a sweep step, no model in the loop.
-- Claim: lead eoc::WeaponDamageTypeOverrideBoostComponent: AddBoosts WeaponDamageTypeOverride in 18 argument shapes, one call each makes them appear on a fresh dummy.
-- Reached by cheat (direct WeaponDamageTypeOverride), in GustavX:Basic_Level_A; plan `leads`, step lead-boost:WeaponDamageTypeOverride.
-- Written 2026-11-14 by `gatekit sweep run`. Replay: gatekit --instance <copy> test tests/_coverage -k sweep_leads_lead_boost_WeaponDamageTypeOverride

test { level = "GustavX:Basic_Level_A" }

function run(game)
    game.wait_state("Running", "server", 120)
    -- Watched from before the first step, so a component that lives one frame is seen too.
    watch("client", "eoc::WeaponDamageTypeOverrideBoostComponent")
    watch("server", "eoc::WeaponDamageTypeOverrideBoostComponent")

    -- A fresh dummy (a temporary neutral human) for the step to work on.
    game.lua("server", [[
SweepDummy = GK.Stage.Spawn{ template = "human", faction = "neutral", distance = 5 }[1]
return { SweepDummy, SweepTarget }
]])

    game.wait("server", [[
GK.Stage.Ready({ SweepDummy, SweepTarget })
]], 20)

    -- The sweep's step lead-boost:WeaponDamageTypeOverride: direct WeaponDamageTypeOverride (a cheat: the cause is staged, no player acts).
    game.lua("server", [[
local D, T = SweepDummy, SweepTarget
local H = Osi.GetHostCharacter()
pcall(Osi.AddBoosts, D, "WeaponDamageTypeOverride()", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "WeaponDamageTypeOverride(1)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "WeaponDamageTypeOverride(10)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "WeaponDamageTypeOverride(true)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "WeaponDamageTypeOverride(Strength)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "WeaponDamageTypeOverride(Strength,1)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "WeaponDamageTypeOverride(Attack,10)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "WeaponDamageTypeOverride(Attack,1)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "WeaponDamageTypeOverride(All,10)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "WeaponDamageTypeOverride(Fire)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "WeaponDamageTypeOverride(Tiny)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "WeaponDamageTypeOverride(Huge)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "WeaponDamageTypeOverride(1,1)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "WeaponDamageTypeOverride(Attack,Success)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "WeaponDamageTypeOverride(SavingThrow,Critical)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "WeaponDamageTypeOverride(MeleeWeaponAttack)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "WeaponDamageTypeOverride(Strength,Attack)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "WeaponDamageTypeOverride(Piercing)", "GateKitSweep", D)
]], 20)
    game.sleep(1.5)

    local seen = alive("client", "eoc::WeaponDamageTypeOverrideBoostComponent", 10)
    print("eoc::WeaponDamageTypeOverrideBoostComponent alive on client: " .. fmt(seen))
    seen = alive("server", "eoc::WeaponDamageTypeOverrideBoostComponent", 1)
    print("eoc::WeaponDamageTypeOverrideBoostComponent alive on server: " .. fmt(seen))

    -- Leave nothing behind for the next replay.
    game.lua("server", [[
local D, T = SweepDummy, SweepTarget
local H = Osi.GetHostCharacter()
pcall(Osi.RemoveBoosts, D, "WeaponDamageTypeOverride()", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "WeaponDamageTypeOverride(1)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "WeaponDamageTypeOverride(10)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "WeaponDamageTypeOverride(true)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "WeaponDamageTypeOverride(Strength)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "WeaponDamageTypeOverride(Strength,1)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "WeaponDamageTypeOverride(Attack,10)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "WeaponDamageTypeOverride(Attack,1)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "WeaponDamageTypeOverride(All,10)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "WeaponDamageTypeOverride(Fire)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "WeaponDamageTypeOverride(Tiny)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "WeaponDamageTypeOverride(Huge)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "WeaponDamageTypeOverride(1,1)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "WeaponDamageTypeOverride(Attack,Success)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "WeaponDamageTypeOverride(SavingThrow,Critical)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "WeaponDamageTypeOverride(MeleeWeaponAttack)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "WeaponDamageTypeOverride(Strength,Attack)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "WeaponDamageTypeOverride(Piercing)", 0, "GateKitSweep", D)
for _, id in ipairs({ D, T }) do pcall(GK.Stage.Remove, id) end
SweepDummy, SweepTarget = nil, nil
return true
]])
end
