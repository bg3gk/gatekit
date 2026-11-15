-- esv::trap::DisarmResultEventOneFrameComponent: the shortest way to make it appear again.
-- Claim: One frame, when the roll has finished and the trap's armed state has been decided. Seen with the standing capture and a per-tick count watcher, twice; field layout NOT checked.
-- Backed by: repeated, reached by cheat, in TUT_Avernus_C
-- Still unresolved: Fields not read: only existence and timing were established. The client's real Pick lock / Disarm menu path was not used - the request is the one the net handler would queue, put there by GateKit's Script Extender fork.
-- Recorded 2026-11-13.
-- unreplayed since rewrite (2026-11-14): rewritten as a test from the free-form recipe's own steps, not yet replayed live.
-- Replay: gatekit --instance <copy> test tests/_coverage -k disarm_DisarmResultEventOneFrame

test { level = "GustavX:TUT_Avernus_C" }

function run(game)
    game.wait_state("Running", "server", 120)
    -- Watched from before the first step, so a component that lives one frame is seen too.
    watch("server", "esv::trap::DisarmResultEventOneFrameComponent")

    -- Nautiloid, host Astarion. Trap and toolkit are staged (cheat); the disarm itself is the player's request.
    game.lua("server", [[
local W = Osi.GetHostCharacter()
local p = GK.See.Position(W)
local kit = GK.Spawn("22c74b5e-bef2-41b1-b9ed-f4acc766d4ee")              -- TOOL_GEN_Disam_Toolkit_A (sic)
Osi.ToInventory(kit, W, 1, 0, 0)
local trap = GK.Spawn("997df298-088e-4146-ab73-d512fc69b0eb", p[1]+3, p[2], p[3])  -- PUZ_GEN_Trap_Mine_A_Rusty_A: armed and discovered on spawn
GK.Stage.Disarm(trap, W, kit)    -- GK.Cheat.Disarm does not find this kit (it looks for "TrapDisarm" in the template name)
]])
    game.sleep(2)

    -- the dice window, one phase a call (WaitForStart -> StartRoll -> ResultReady)
    game.lua("client", [[
GK.RollNext()
]])
    game.sleep(2)

    game.lua("client", [[
GK.RollNext()
]])
    game.sleep(2)

    game.lua("client", [[
GK.RollNext()
]])
    game.sleep(2)

    local seen = alive("server", "esv::trap::DisarmResultEventOneFrameComponent", 10)
    print("esv::trap::DisarmResultEventOneFrameComponent alive on server: " .. fmt(seen))
end
