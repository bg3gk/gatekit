-- esv::inventory::SummonedIntoInventoryOneFrameComponent: the shortest way to make it appear again.
-- Claim: Alive for a frame when Goodberry puts its summoned berries into the caster inventory (Max 4 holders, first sighting at tick ~52 after the cast).
-- Backed by: repeated, reached by cheat, in GustavX:WLD_Main_A
-- Still unresolved: ["Fields not read; spell was taught and cast by GK.Stage."]
-- Recorded 2026-11-14. Replay: gatekit --instance <copy> test tests/_coverage -k summoned_into_inventory_goodberry

test { level = "GustavX:WLD_Main_A" }

function run(game)
    game.wait_state("Running", "server", 120)
    -- Watched from before the first step, so a component that lives one frame is seen too.
    watch("server", "esv::inventory::SummonedIntoInventoryOneFrameComponent")

    game.lua("server", [[
local h=Osi.GetHostCharacter(); GK.Stage.Teach("Target_Goodberry", h); GK.Stage.Cast("Target_Goodberry", h, h); return 1
]], 10)

    game.sleep(3)

    local seen = alive("server", "esv::inventory::SummonedIntoInventoryOneFrameComponent", 10)
    print("esv::inventory::SummonedIntoInventoryOneFrameComponent alive on server: " .. fmt(seen))
end
