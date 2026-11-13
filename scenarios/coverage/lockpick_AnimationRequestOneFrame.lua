-- esv::lock::AnimationRequestOneFrameComponent: the shortest way to make it appear again.
-- Claim: One frame, with the roll request: the picking animation. Seen with the standing capture and a per-tick count watcher, twice; field layout NOT checked.
-- Backed by: repeated, reached by cheat, in TUT_Avernus_C
-- Still unresolved: Fields not read: only existence and timing were established. The client's real Pick lock / Disarm menu path was not used - the request is the one the net handler would queue, put there by GateKit's Script Extender fork.
-- Recorded 2026-11-13.

-- Nautiloid, host Astarion (carries Thieves' Tools). The level's one locked chest is out of walking reach
-- ("no path there"), so the chest is brought to the host (cheat), then the PLAYER's own request is used.
local W = Osi.GetHostCharacter()
local chest = "95dde668-cee0-47c0-92ed-1072db84f687"   -- CONT_GEN_Chest_Jewel_B
GK.Cheat.Bring(chest, W)                                -- Bring(what, to): the chest is moved next to Astarion
Osi.Lock(chest, "")                                     -- make sure it is locked
GK.Act.Lockpick(W, chest)                               -- queues the engine's own PickLockRequest (as the net handler does)
-- CLIENT side, three times with a pause: GK.RollNext()   (WaitForStart -> StartRoll -> ResultReady) clicks the dice window through
-- Watch: Ext.Debug.GetComponentCounts() on every Ext.Events.Tick for esv::lock::* and esv::unsheath::LockPickingComponent
