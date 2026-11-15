-- eoc::MaximumRollResultBoostComponent, eoc::MaximumRollResultBoostComponent: made to appear by a sweep step, no model in the loop.
-- Claim: lead eoc::MaximumRollResultBoostComponent: AddBoosts MaximumRollResult in 18 argument shapes, one call each makes them appear on a fresh dummy.
-- Reached by cheat (direct MaximumRollResult), in GustavX:Basic_Level_A; plan `leads`, step lead-boost:MaximumRollResult.
-- Written 2026-11-14 by `gatekit sweep run`. Replay: gatekit --instance <copy> test tests/_coverage -k sweep_leads_lead_boost_MaximumRollResult

test { level = "GustavX:Basic_Level_A" }

function run(game)
    game.wait_state("Running", "server", 120)
    -- Watched from before the first step, so a component that lives one frame is seen too.
    watch("client", "eoc::MaximumRollResultBoostComponent")
    watch("server", "eoc::MaximumRollResultBoostComponent")

    -- A fresh dummy (a temporary neutral human) for the step to work on.
    game.lua("server", [[
SweepDummy = GK.Stage.Spawn{ template = "human", faction = "neutral", distance = 5 }[1]
return { SweepDummy, SweepTarget }
]])

    game.wait("server", [[
GK.Stage.Ready({ SweepDummy, SweepTarget })
]], 20)

    -- The sweep's step lead-boost:MaximumRollResult: direct MaximumRollResult (a cheat: the cause is staged, no player acts).
    game.lua("server", [[
local D, T = SweepDummy, SweepTarget
local H = Osi.GetHostCharacter()
pcall(Osi.AddBoosts, D, "MaximumRollResult()", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "MaximumRollResult(1)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "MaximumRollResult(10)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "MaximumRollResult(true)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "MaximumRollResult(Strength)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "MaximumRollResult(Strength,1)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "MaximumRollResult(Attack,10)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "MaximumRollResult(Attack,1)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "MaximumRollResult(All,10)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "MaximumRollResult(Fire)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "MaximumRollResult(Tiny)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "MaximumRollResult(Huge)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "MaximumRollResult(1,1)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "MaximumRollResult(Attack,Success)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "MaximumRollResult(SavingThrow,Critical)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "MaximumRollResult(MeleeWeaponAttack)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "MaximumRollResult(Strength,Attack)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "MaximumRollResult(Piercing)", "GateKitSweep", D)
]], 20)
    game.sleep(1.5)

    local seen = alive("client", "eoc::MaximumRollResultBoostComponent", 10)
    print("eoc::MaximumRollResultBoostComponent alive on client: " .. fmt(seen))
    seen = alive("server", "eoc::MaximumRollResultBoostComponent", 1)
    print("eoc::MaximumRollResultBoostComponent alive on server: " .. fmt(seen))

    -- Leave nothing behind for the next replay.
    game.lua("server", [[
local D, T = SweepDummy, SweepTarget
local H = Osi.GetHostCharacter()
pcall(Osi.RemoveBoosts, D, "MaximumRollResult()", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "MaximumRollResult(1)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "MaximumRollResult(10)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "MaximumRollResult(true)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "MaximumRollResult(Strength)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "MaximumRollResult(Strength,1)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "MaximumRollResult(Attack,10)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "MaximumRollResult(Attack,1)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "MaximumRollResult(All,10)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "MaximumRollResult(Fire)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "MaximumRollResult(Tiny)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "MaximumRollResult(Huge)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "MaximumRollResult(1,1)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "MaximumRollResult(Attack,Success)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "MaximumRollResult(SavingThrow,Critical)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "MaximumRollResult(MeleeWeaponAttack)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "MaximumRollResult(Strength,Attack)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "MaximumRollResult(Piercing)", 0, "GateKitSweep", D)
for _, id in ipairs({ D, T }) do pcall(GK.Stage.Remove, id) end
SweepDummy, SweepTarget = nil, nil
return true
]])
end
