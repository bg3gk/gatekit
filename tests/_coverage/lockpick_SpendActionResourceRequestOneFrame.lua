-- esv::lock::SpendActionResourceRequestOneFrameComponent: the shortest way to make it appear again.
-- Claim: One frame: the action resource the attempt costs. Seen with the standing capture and a per-tick count watcher, twice; field layout NOT checked.
-- Backed by: repeated, reached by cheat, in TUT_Avernus_C
-- Still unresolved: Fields not read: only existence and timing were established. The client's real Pick lock / Disarm menu path was not used - the request is the one the net handler would queue, put there by GateKit's Script Extender fork.
-- Recorded 2026-11-13.
-- unreplayed since rewrite (2026-11-14): rewritten as a test from the free-form recipe's own steps, not yet replayed live.
-- Replay: gatekit --instance <copy> test tests/_coverage -k lockpick_SpendActionResourceRequestOneFrame

test { level = "GustavX:TUT_Avernus_C" }

function run(game)
    game.wait_state("Running", "server", 120)
    -- Watched from before the first step, so a component that lives one frame is seen too.
    watch("server", "esv::lock::SpendActionResourceRequestOneFrameComponent")

    -- Nautiloid, host Astarion (carries Thieves' Tools). The level's one locked chest is out of walking reach, so it is brought to the host (cheat), then the player's own request is used.
    game.lua("server", [[
local W = Osi.GetHostCharacter()
local chest = "95dde668-cee0-47c0-92ed-1072db84f687"   -- CONT_GEN_Chest_Jewel_B
GK.Cheat.Bring(chest, W)                                -- Bring(what, to): the chest is moved next to Astarion
Osi.Lock(chest, "")                                     -- make sure it is locked
GK.Act.Lockpick(W, chest)                               -- queues the engine's own PickLockRequest (as the net handler does)
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

    local seen = alive("server", "esv::lock::SpendActionResourceRequestOneFrameComponent", 10)
    print("esv::lock::SpendActionResourceRequestOneFrameComponent alive on server: " .. fmt(seen))
end
