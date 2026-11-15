-- eoc::gamestate::SavingComponent: the shortest way to make it appear again.
-- Claim: A quicksave makes SavingComponent (and ecl::savegame::IsSavingComponent) live on the client for the save; server gets esv::GameStateSaveComponent and SaveCompletedOneFrameComponent.
-- Backed by: repeated, reached by play, in GustavX:WLD_Main_A
-- Still unresolved: lives only during the save; the recipe check may miss it if the save is quicker than the poll
-- Recorded 2026-11-14. Replay: gatekit --instance <copy> test tests/_coverage -k quicksave_saving

test { level = "GustavX:WLD_Main_A" }

function run(game)
    game.wait_state("Running", "server", 120)
    -- Watched from before the first step, so a component that lives one frame is seen too.
    watch("client", "eoc::gamestate::SavingComponent")

    game.lua("client", [[
GK.Gui.QuickSave()
]])

    game.sleep(1)

    local seen = alive("client", "eoc::gamestate::SavingComponent", 10)
    print("eoc::gamestate::SavingComponent alive on client: " .. fmt(seen))
end
