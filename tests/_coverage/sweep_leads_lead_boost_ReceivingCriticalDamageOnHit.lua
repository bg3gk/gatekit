-- eoc::ReceivingCriticalDamageOnHitBoostComponent, eoc::ReceivingCriticalDamageOnHitBoostComponent: made to appear by a sweep step, no model in the loop.
-- Claim: lead eoc::ReceivingCriticalDamageOnHitBoostComponent: AddBoosts ReceivingCriticalDamageOnHit in 18 argument shapes, one call each makes them appear on a fresh dummy.
-- Reached by cheat (direct ReceivingCriticalDamageOnHit), in GustavX:Basic_Level_A; plan `leads`, step lead-boost:ReceivingCriticalDamageOnHit.
-- Written 2026-11-14 by `gatekit sweep run`. Replay: gatekit --instance <copy> test tests/_coverage -k sweep_leads_lead_boost_ReceivingCriticalDamageOnHit

test { level = "GustavX:Basic_Level_A" }

function run(game)
    game.wait_state("Running", "server", 120)
    -- Watched from before the first step, so a component that lives one frame is seen too.
    watch("client", "eoc::ReceivingCriticalDamageOnHitBoostComponent")
    watch("server", "eoc::ReceivingCriticalDamageOnHitBoostComponent")

    -- A fresh dummy (a temporary neutral human) for the step to work on.
    game.lua("server", [[
SweepDummy = GK.Stage.Spawn{ template = "human", faction = "neutral", distance = 5 }[1]
return { SweepDummy, SweepTarget }
]])

    game.wait("server", [[
GK.Stage.Ready({ SweepDummy, SweepTarget })
]], 20)

    -- The sweep's step lead-boost:ReceivingCriticalDamageOnHit: direct ReceivingCriticalDamageOnHit (a cheat: the cause is staged, no player acts).
    game.lua("server", [[
local D, T = SweepDummy, SweepTarget
local H = Osi.GetHostCharacter()
pcall(Osi.AddBoosts, D, "ReceivingCriticalDamageOnHit()", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "ReceivingCriticalDamageOnHit(1)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "ReceivingCriticalDamageOnHit(10)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "ReceivingCriticalDamageOnHit(true)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "ReceivingCriticalDamageOnHit(Strength)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "ReceivingCriticalDamageOnHit(Strength,1)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "ReceivingCriticalDamageOnHit(Attack,10)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "ReceivingCriticalDamageOnHit(Attack,1)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "ReceivingCriticalDamageOnHit(All,10)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "ReceivingCriticalDamageOnHit(Fire)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "ReceivingCriticalDamageOnHit(Tiny)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "ReceivingCriticalDamageOnHit(Huge)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "ReceivingCriticalDamageOnHit(1,1)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "ReceivingCriticalDamageOnHit(Attack,Success)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "ReceivingCriticalDamageOnHit(SavingThrow,Critical)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "ReceivingCriticalDamageOnHit(MeleeWeaponAttack)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "ReceivingCriticalDamageOnHit(Strength,Attack)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "ReceivingCriticalDamageOnHit(Piercing)", "GateKitSweep", D)
]], 20)
    game.sleep(1.5)

    local seen = alive("client", "eoc::ReceivingCriticalDamageOnHitBoostComponent", 10)
    print("eoc::ReceivingCriticalDamageOnHitBoostComponent alive on client: " .. fmt(seen))
    seen = alive("server", "eoc::ReceivingCriticalDamageOnHitBoostComponent", 1)
    print("eoc::ReceivingCriticalDamageOnHitBoostComponent alive on server: " .. fmt(seen))

    -- Leave nothing behind for the next replay.
    game.lua("server", [[
local D, T = SweepDummy, SweepTarget
local H = Osi.GetHostCharacter()
pcall(Osi.RemoveBoosts, D, "ReceivingCriticalDamageOnHit()", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "ReceivingCriticalDamageOnHit(1)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "ReceivingCriticalDamageOnHit(10)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "ReceivingCriticalDamageOnHit(true)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "ReceivingCriticalDamageOnHit(Strength)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "ReceivingCriticalDamageOnHit(Strength,1)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "ReceivingCriticalDamageOnHit(Attack,10)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "ReceivingCriticalDamageOnHit(Attack,1)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "ReceivingCriticalDamageOnHit(All,10)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "ReceivingCriticalDamageOnHit(Fire)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "ReceivingCriticalDamageOnHit(Tiny)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "ReceivingCriticalDamageOnHit(Huge)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "ReceivingCriticalDamageOnHit(1,1)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "ReceivingCriticalDamageOnHit(Attack,Success)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "ReceivingCriticalDamageOnHit(SavingThrow,Critical)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "ReceivingCriticalDamageOnHit(MeleeWeaponAttack)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "ReceivingCriticalDamageOnHit(Strength,Attack)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "ReceivingCriticalDamageOnHit(Piercing)", 0, "GateKitSweep", D)
for _, id in ipairs({ D, T }) do pcall(GK.Stage.Remove, id) end
SweepDummy, SweepTarget = nil, nil
return true
]])
end
