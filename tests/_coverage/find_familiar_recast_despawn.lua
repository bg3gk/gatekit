-- esv::summon::DespawnRequestOneFrameComponent: the shortest way to make it appear again.
-- Claim: Casting Find Familiar (Rat) at a point and then casting it again on the host (about 4 s later) made esv::summon::DespawnRequestOneFrameComponent appear on the server. Likely the first familiar being replaced by the second; the link to the replacement is inferred, not shown.
-- Backed by: sighting only (seen once; a third cast of the Cat variant is reported in the run report), reached by cheat, in WLD_Campfire_E
-- Still unresolved: Whether the first cast alone produces it; whether dismissing or killing the familiar does; what the tag holds (size 0); no entity read of the summon.
-- Recorded 2026-11-14.
-- unreplayed since rewrite (2026-11-14): rewritten as a test from the free-form recipe's own steps, not yet replayed live.
-- Replay: gatekit --instance <copy> test tests/_coverage -k find_familiar_recast_despawn

test { level = "GustavX:WLD_Campfire_E" }

function run(game)
    game.wait_state("Running", "server", 120)
    -- Watched from before the first step, so a component that lives one frame is seen too.
    watch("server", "esv::summon::DespawnRequestOneFrameComponent")

    -- the first familiar, at a point beside the host
    game.lua("server", [[
local h = Osi.GetHostCharacter()
local x, y, z = Osi.GetPosition(h)
GK.Stage.Teach("Target_FindFamiliar_Rat", h)
GK.Stage.Cast("Target_FindFamiliar_Rat", {x + 2, y, z})
]])
    game.sleep(4)

    -- the second cast, the familiar already out
    game.lua("server", [[
GK.Stage.Cast("Target_FindFamiliar_Rat", Osi.GetHostCharacter())
]])
    game.sleep(2)

    local seen = alive("server", "esv::summon::DespawnRequestOneFrameComponent", 10)
    print("esv::summon::DespawnRequestOneFrameComponent alive on server: " .. fmt(seen))
end
