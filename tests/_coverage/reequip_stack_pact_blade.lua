-- esv::inventory::ReequipStackComponent: the shortest way to make it appear again.
-- Claim: Appears after summoning the Pact Blade, dismissing it and summoning it again (first sighting 27 ticks into the capture, Max 1).
-- Backed by: repeated, reached by cheat, in GustavX:WLD_Main_A
-- Still unresolved: ["Which of the three casts makes it; fields not read."]
-- Recorded 2026-11-14. Replay: gatekit --instance <copy> test tests/_coverage -k reequip_stack_pact_blade

test { level = "GustavX:WLD_Main_A" }

function run(game)
    game.wait_state("Running", "server", 120)
    -- Watched from before the first step, so a component that lives one frame is seen too.
    watch("server", "esv::inventory::ReequipStackComponent")

    game.lua("server", [[
local h=Osi.GetHostCharacter(); GK.Stage.Teach("Shout_PactOfTheBlade_Greatsword", h); GK.Stage.Cast("Shout_PactOfTheBlade_Greatsword", h); return 1
]])

    game.sleep(4)

    game.lua("server", [[
local h=Osi.GetHostCharacter(); GK.Stage.Cast("Shout_PactOfTheBlade_Dismiss", h); return 1
]])

    game.sleep(3)

    game.lua("server", [[
local h=Osi.GetHostCharacter(); GK.Stage.Cast("Shout_PactOfTheBlade_Greatsword", h); return 1
]])

    game.sleep(3)

    local seen = alive("server", "esv::inventory::ReequipStackComponent", 10)
    print("esv::inventory::ReequipStackComponent alive on server: " .. fmt(seen))
end
