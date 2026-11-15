-- eoc::ObjectSizeOverrideBoostComponent, eoc::ObjectSizeOverrideBoostComponent: made to appear by a sweep step, no model in the loop.
-- Claim: lead eoc::ObjectSizeOverrideBoostComponent: AddBoosts ObjectSizeOverride in 18 argument shapes, one call each makes them appear on a fresh dummy.
-- Reached by cheat (direct ObjectSizeOverride), in GustavX:Basic_Level_A; plan `leads`, step lead-boost:ObjectSizeOverride.
-- Written 2026-11-14 by `gatekit sweep run`. Replay: gatekit --instance <copy> test tests/_coverage -k sweep_leads_lead_boost_ObjectSizeOverride

test { level = "GustavX:Basic_Level_A" }

function run(game)
    game.wait_state("Running", "server", 120)
    -- Watched from before the first step, so a component that lives one frame is seen too.
    watch("client", "eoc::ObjectSizeOverrideBoostComponent")
    watch("server", "eoc::ObjectSizeOverrideBoostComponent")

    -- A fresh dummy (a temporary neutral human) for the step to work on.
    game.lua("server", [[
SweepDummy = GK.Stage.Spawn{ template = "human", faction = "neutral", distance = 5 }[1]
return { SweepDummy, SweepTarget }
]])

    game.wait("server", [[
GK.Stage.Ready({ SweepDummy, SweepTarget })
]], 20)

    -- The sweep's step lead-boost:ObjectSizeOverride: direct ObjectSizeOverride (a cheat: the cause is staged, no player acts).
    game.lua("server", [[
local D, T = SweepDummy, SweepTarget
local H = Osi.GetHostCharacter()
pcall(Osi.AddBoosts, D, "ObjectSizeOverride()", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "ObjectSizeOverride(1)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "ObjectSizeOverride(10)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "ObjectSizeOverride(true)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "ObjectSizeOverride(Strength)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "ObjectSizeOverride(Strength,1)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "ObjectSizeOverride(Attack,10)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "ObjectSizeOverride(Attack,1)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "ObjectSizeOverride(All,10)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "ObjectSizeOverride(Fire)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "ObjectSizeOverride(Tiny)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "ObjectSizeOverride(Huge)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "ObjectSizeOverride(1,1)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "ObjectSizeOverride(Attack,Success)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "ObjectSizeOverride(SavingThrow,Critical)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "ObjectSizeOverride(MeleeWeaponAttack)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "ObjectSizeOverride(Strength,Attack)", "GateKitSweep", D)
pcall(Osi.AddBoosts, D, "ObjectSizeOverride(Piercing)", "GateKitSweep", D)
]], 20)
    game.sleep(1.5)

    local seen = alive("client", "eoc::ObjectSizeOverrideBoostComponent", 10)
    print("eoc::ObjectSizeOverrideBoostComponent alive on client: " .. fmt(seen))
    seen = alive("server", "eoc::ObjectSizeOverrideBoostComponent", 1)
    print("eoc::ObjectSizeOverrideBoostComponent alive on server: " .. fmt(seen))

    -- Leave nothing behind for the next replay.
    game.lua("server", [[
local D, T = SweepDummy, SweepTarget
local H = Osi.GetHostCharacter()
pcall(Osi.RemoveBoosts, D, "ObjectSizeOverride()", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "ObjectSizeOverride(1)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "ObjectSizeOverride(10)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "ObjectSizeOverride(true)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "ObjectSizeOverride(Strength)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "ObjectSizeOverride(Strength,1)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "ObjectSizeOverride(Attack,10)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "ObjectSizeOverride(Attack,1)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "ObjectSizeOverride(All,10)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "ObjectSizeOverride(Fire)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "ObjectSizeOverride(Tiny)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "ObjectSizeOverride(Huge)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "ObjectSizeOverride(1,1)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "ObjectSizeOverride(Attack,Success)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "ObjectSizeOverride(SavingThrow,Critical)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "ObjectSizeOverride(MeleeWeaponAttack)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "ObjectSizeOverride(Strength,Attack)", 0, "GateKitSweep", D)
pcall(Osi.RemoveBoosts, D, "ObjectSizeOverride(Piercing)", 0, "GateKitSweep", D)
for _, id in ipairs({ D, T }) do pcall(GK.Stage.Remove, id) end
SweepDummy, SweepTarget = nil, nil
return true
]])
end
