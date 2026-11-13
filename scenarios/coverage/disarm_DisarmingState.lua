-- esv::trap::DisarmingStateComponent: the shortest way to make it appear again.
-- Claim: Lives for the whole disarm attempt. Seen with the standing capture and a per-tick count watcher, twice; field layout NOT checked.
-- Backed by: repeated, reached by cheat, in TUT_Avernus_C
-- Still unresolved: Fields not read: only existence and timing were established. The client's real Pick lock / Disarm menu path was not used - the request is the one the net handler would queue, put there by GateKit's Script Extender fork.
-- Recorded 2026-11-13.

-- Nautiloid, host Astarion. Trap and toolkit are staged (cheat); the disarm itself is the player's request.
local W = Osi.GetHostCharacter()
local p = GK.See.Position(W)
local kit = GK.Spawn("22c74b5e-bef2-41b1-b9ed-f4acc766d4ee")              -- TOOL_GEN_Disam_Toolkit_A (sic)
Osi.ToInventory(kit, W, 1, 0, 0)
local trap = GK.Spawn("997df298-088e-4146-ab73-d512fc69b0eb", p[1]+3, p[2], p[3])  -- PUZ_GEN_Trap_Mine_A_Rusty_A: armed and discovered on spawn
-- GK.Act.Disarm does not find this kit (it looks for "TrapDisarm" in the template name), so the stage call is used:
GK.Stage.Disarm(trap, W, kit)
-- CLIENT side, three times with a pause: GK.RollNext() clicks the dice window through; afterwards Osi.IsTrapArmed(trap) == 0
