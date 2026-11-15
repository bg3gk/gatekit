-- esv::script::SavegameLoadStartedOneFrameComponent: the shortest way to make it appear again.
-- Claim: Saving through the save window and loading that save through the load window makes esv::script::SavegameLoadStartedOneFrameComponent (server) and ecl/esv::GameStateLoadSessionComponent appear, never seen before. With the capture running across the load (Resumed shown), they were listed new_to_the_record by bg3_seen_new right after the level was Running again.
-- Backed by: sighting only, reached by play, in GustavX:SCL_Main_A
-- Still unresolved: Seen once. Not repeated with a fresh capture. bg3_verify (server, prefix esv::script::SavegameLoadStarted, record) answered nothing_alive (one frame). Recipe steps not replayed by me. esv::party::PostReloadStoryUpdateEvent: not seen in this load.
-- Recorded 2026-11-14. Replay: gatekit --instance <copy> test tests/_coverage -k savegame_load_started_via_load_window

test { level = "GustavX:SCL_Main_A" }

function run(game)
    game.wait_state("Running", "server", 120)
    -- Watched from before the first step, so a component that lives one frame is seen too.
    watch("server", "esv::script::SavegameLoadStartedOneFrameComponent")

    game.lua("client", [[
return GK.Gui.SaveAs("CV2_recipe")
]])

    game.sleep(3)

    game.lua("client", [[
return GK.Gui.SaveAs("CV2_recipe")
]])

    game.sleep(3)

    game.lua("client", [[
return GK.Gui.SaveAs("CV2_recipe")
]])

    game.sleep(4)

    game.lua("client", [[
return GK.Gui.LoadLatest()
]])

    game.sleep(3)

    game.lua("client", [[
return GK.Gui.LoadLatest()
]])

    game.sleep(3)

    game.lua("client", [[
return GK.Gui.LoadLatest()
]])

    game.sleep(40)

    local seen = alive("server", "esv::script::SavegameLoadStartedOneFrameComponent", 10)
    print("esv::script::SavegameLoadStartedOneFrameComponent alive on server: " .. fmt(seen))
end
