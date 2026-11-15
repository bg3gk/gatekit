-- eoc::notification::SpellCastRollAbortComponent, eoc::spell_cast::SpellRollAbortEventOneFrameComponent, eoc::notification::SpellCastRollAbortComponent, eoc::spell_cast::SpellRollAbortEventOneFrameComponent: made to appear by a sweep step, no model in the loop.
-- Claim: SpellAnimationType Telekinesis: the host casts Throw_WYR_Circus_AxeThrow (Throw, level 0) through GK.Cheat.Cast, checked makes them appear on a fresh dummy.
-- Reached by cheat (spell Throw_WYR_Circus_AxeThrow), in GustavX:Basic_Level_A; plan `spelltypes`, step cast:SpellAnimationType:Telekinesis.
-- Written 2026-11-14 by `gatekit sweep run`. Replay: gatekit --instance <copy> test tests/_coverage -k sweep_spelltypes_cast_SpellAnimationType_Telekinesis

test { level = "GustavX:Basic_Level_A" }

function run(game)
    game.wait_state("Running", "server", 120)
    -- Watched from before the first step, so a component that lives one frame is seen too.
    watch("client", "eoc::notification::SpellCastRollAbortComponent")
    watch("client", "eoc::spell_cast::SpellRollAbortEventOneFrameComponent")
    watch("server", "eoc::notification::SpellCastRollAbortComponent")
    watch("server", "eoc::spell_cast::SpellRollAbortEventOneFrameComponent")

    -- A fresh dummy (a temporary neutral human) for the step to work on.
    game.lua("server", [[
SweepDummy = GK.Stage.Spawn{ template = "human", faction = "neutral", distance = 5 }[1]
SweepTarget = GK.Stage.Spawn{ template = "human", faction = "neutral", distance = 8 }[1]
return { SweepDummy, SweepTarget }
]])

    game.wait("server", [[
GK.Stage.Ready({ SweepDummy, SweepTarget })
]], 20)

    -- The sweep's step cast:SpellAnimationType:Telekinesis: spell Throw_WYR_Circus_AxeThrow (a cheat: the cause is staged, no player acts).
    game.lua("server", [[
local D, T = SweepDummy, SweepTarget
local H = Osi.GetHostCharacter()
SweepItem = GK.Stage.Spawn{ template = "potion", near = H, distance = 1 }[1]
GK.Stage.Teach("Throw_WYR_Circus_AxeThrow", H)
GK.Stage.Restore(H)
SweepJob = nil
Ext.Timer.WaitFor(1500, function() SweepJob = GK.Cheat.Cast(H, "Throw_WYR_Circus_AxeThrow", T, { walk = true, target2 = SweepItem }) end)
]], 20)
    game.sleep(8.5)

    local seen = alive("client", "eoc::notification::SpellCastRollAbortComponent", 10)
    print("eoc::notification::SpellCastRollAbortComponent alive on client: " .. fmt(seen))
    seen = alive("client", "eoc::spell_cast::SpellRollAbortEventOneFrameComponent", 1)
    print("eoc::spell_cast::SpellRollAbortEventOneFrameComponent alive on client: " .. fmt(seen))
    seen = alive("server", "eoc::notification::SpellCastRollAbortComponent", 1)
    print("eoc::notification::SpellCastRollAbortComponent alive on server: " .. fmt(seen))
    seen = alive("server", "eoc::spell_cast::SpellRollAbortEventOneFrameComponent", 1)
    print("eoc::spell_cast::SpellRollAbortEventOneFrameComponent alive on server: " .. fmt(seen))

    -- Leave nothing behind for the next replay.
    game.lua("server", [[
local D, T = SweepDummy, SweepTarget
local H = Osi.GetHostCharacter()
pcall(GK.Cheat.Stop, H)
pcall(GK.Stage.EndCombat, H)
pcall(Osi.RemoveSpell, H, "Throw_WYR_Circus_AxeThrow", 0)
if SweepItem then pcall(Osi.RequestDelete, SweepItem); SweepItem = nil end
for _, id in ipairs({ D, T }) do pcall(GK.Stage.Remove, id) end
SweepDummy, SweepTarget = nil, nil
return true
]])
end
