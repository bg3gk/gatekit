-- ls::anubis::savegame::RestoringStateComponent, ls::anubis::savegame::WaitingForConfigChangeComponent: made to appear by a sweep step, no model in the loop.
-- Claim: boost FallDamageMultiplier (FallDamageMultiplier(0.5)) through passive FelineFall makes them appear on a fresh dummy.
-- Reached by cheat (passive FelineFall), in GustavX:Basic_Level_A; plan `boosts`, step boost:FallDamageMultiplier.
-- Written 2026-11-14 by `gatekit sweep run`. Replay: gatekit --instance <copy> test tests/_coverage -k sweep_boosts_boost_FallDamageMultiplier

test { level = "GustavX:Basic_Level_A" }

function run(game)
    game.wait_state("Running", "server", 120)
    -- Watched from before the first step, so a component that lives one frame is seen too.
    watch("server", "ls::anubis::savegame::RestoringStateComponent")
    watch("server", "ls::anubis::savegame::WaitingForConfigChangeComponent")

    -- A fresh dummy (a temporary neutral human) for the step to work on.
    game.lua("server", [[
SweepDummy = GK.Stage.Spawn{ template = "human", faction = "neutral", distance = 5 }[1]
return { SweepDummy, SweepTarget }
]])

    game.wait("server", [[
GK.Stage.Ready({ SweepDummy, SweepTarget })
]], 20)

    -- The sweep's step boost:FallDamageMultiplier: passive FelineFall (a cheat: the cause is staged, no player acts).
    game.lua("server", [[
local D, T = SweepDummy, SweepTarget
Osi.AddPassive(D, "FelineFall")
]], 20)
    game.sleep(1.5)

    local seen = alive("server", "ls::anubis::savegame::RestoringStateComponent", 10)
    print("ls::anubis::savegame::RestoringStateComponent alive on server: " .. fmt(seen))
    seen = alive("server", "ls::anubis::savegame::WaitingForConfigChangeComponent", 1)
    print("ls::anubis::savegame::WaitingForConfigChangeComponent alive on server: " .. fmt(seen))

    -- Leave nothing behind for the next replay.
    game.lua("server", [[
local D, T = SweepDummy, SweepTarget
pcall(Osi.RemovePassive, D, "FelineFall")
for _, id in ipairs({ D, T }) do pcall(GK.Stage.Remove, id) end
SweepDummy, SweepTarget = nil, nil
return true
]])
end
