-- esv::rest::ScriptPhaseStartedEventOneFrameComponent: the shortest way to make it appear again.
-- Claim: The supplies-screen rest path: open the map (m), press its CampTravel and accept the box (the host is moved to a camp spot in the world, about -648,0,-186), then GK.Gui.LongRest() opens the MakeCamp supplies screen (without the CampTravel step it opens a plain End the day box instead); ProcessRestCommand and accepting the next box light esv::rest::ScriptPhaseStartedEventOneFrameComponent together with the other esv::rest and esv::camp types of that rest.
-- Backed by: repeated, reached by play, in GustavX:WLD_Main_A
-- Still unresolved: ["Fields not read (one-frame/size 0). What the map CampTravel box does exactly is not separated: it moves the host but no level load happens (TravelError Unavailable). Full supplies only (40 of 40 required); a partial rest was not tried."]
-- Recorded 2026-11-14. Replay: gatekit --instance <copy> test tests/_coverage -k rest_ScriptPhaseStarted

test { level = "GustavX:WLD_Main_A" }

function run(game)
    game.wait_state("Running", "server", 120)
    -- Watched from before the first step, so a component that lives one frame is seen too.
    watch("server", "esv::rest::ScriptPhaseStartedEventOneFrameComponent")

    game.sleep(3)

    game.key("m")

    game.sleep(2)

    game.lua("client", [[
GK.Cheat.Run("JournalMap","CampTravel")
]])

    game.sleep(3)

    game.lua("client", [[
GK.BoxPress(1)
]])

    game.sleep(3)

    game.lua("client", [[
GK.Gui.LongRest()
]])

    game.sleep(3)

    game.lua("client", [[
GK.Cheat.Run("MakeCamp","ProcessRestCommand")
]])

    game.sleep(3)

    game.lua("client", [[
GK.BoxPress(1)
]])

    game.sleep(5)

    local seen = alive("server", "esv::rest::ScriptPhaseStartedEventOneFrameComponent", 10)
    print("esv::rest::ScriptPhaseStartedEventOneFrameComponent alive on server: " .. fmt(seen))
end
