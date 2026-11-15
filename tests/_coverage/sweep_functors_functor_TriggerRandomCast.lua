-- eoc::notification::RandomCastResultComponent, eoc::notification::RandomCastResultComponent, esv::spell_cast::random::IsServerControlledComponent, esv::spell_cast::random::StartSpellCastRequestOneFrameComponent, esv::spell_cast::random::ResultEventOneFrameComponent, esv::spell_cast::random::RequestComponent: made to appear by a sweep step, no model in the loop.
-- Claim: functor TriggerRandomCast (TriggerRandomCast(1,,WYR_SharessCaress_PotentDrinkWild)) through status WYR_SHARESSCARESS_POTENTDRINK.OnApplyFunctors makes them appear on a fresh dummy.
-- Reached by cheat (status WYR_SHARESSCARESS_POTENTDRINK), in GustavX:Basic_Level_A; plan `functors`, step functor:TriggerRandomCast.
-- Written 2026-11-14 by `gatekit sweep run`. Replay: gatekit --instance <copy> test tests/_coverage -k sweep_functors_functor_TriggerRandomCast

test { level = "GustavX:Basic_Level_A" }

function run(game)
    game.wait_state("Running", "server", 120)
    -- Watched from before the first step, so a component that lives one frame is seen too.
    watch("client", "eoc::notification::RandomCastResultComponent")
    watch("server", "eoc::notification::RandomCastResultComponent")
    watch("server", "esv::spell_cast::random::IsServerControlledComponent")
    watch("server", "esv::spell_cast::random::StartSpellCastRequestOneFrameComponent")
    watch("server", "esv::spell_cast::random::ResultEventOneFrameComponent")
    watch("server", "esv::spell_cast::random::RequestComponent")

    -- A fresh dummy (a temporary neutral human) for the step to work on.
    game.lua("server", [[
SweepDummy = GK.Stage.Spawn{ template = "human", faction = "neutral", distance = 5 }[1]
return { SweepDummy, SweepTarget }
]])

    game.wait("server", [[
GK.Stage.Ready({ SweepDummy, SweepTarget })
]], 20)

    -- The sweep's step functor:TriggerRandomCast: status WYR_SHARESSCARESS_POTENTDRINK (a cheat: the cause is staged, no player acts).
    game.lua("server", [[
local D, T = SweepDummy, SweepTarget
Osi.ApplyStatus(D, "WYR_SHARESSCARESS_POTENTDRINK", -1.0, 1, D)
]], 20)
    game.sleep(1.5)

    local seen = alive("client", "eoc::notification::RandomCastResultComponent", 10)
    print("eoc::notification::RandomCastResultComponent alive on client: " .. fmt(seen))
    seen = alive("server", "eoc::notification::RandomCastResultComponent", 1)
    print("eoc::notification::RandomCastResultComponent alive on server: " .. fmt(seen))
    seen = alive("server", "esv::spell_cast::random::IsServerControlledComponent", 1)
    print("esv::spell_cast::random::IsServerControlledComponent alive on server: " .. fmt(seen))
    seen = alive("server", "esv::spell_cast::random::StartSpellCastRequestOneFrameComponent", 1)
    print("esv::spell_cast::random::StartSpellCastRequestOneFrameComponent alive on server: " .. fmt(seen))
    seen = alive("server", "esv::spell_cast::random::ResultEventOneFrameComponent", 1)
    print("esv::spell_cast::random::ResultEventOneFrameComponent alive on server: " .. fmt(seen))
    seen = alive("server", "esv::spell_cast::random::RequestComponent", 1)
    print("esv::spell_cast::random::RequestComponent alive on server: " .. fmt(seen))

    -- Leave nothing behind for the next replay.
    game.lua("server", [[
local D, T = SweepDummy, SweepTarget
pcall(Osi.RemoveStatus, D, "WYR_SHARESSCARESS_POTENTDRINK")
for _, id in ipairs({ D, T }) do pcall(GK.Stage.Remove, id) end
SweepDummy, SweepTarget = nil, nil
return true
]])
end
