-- esv: the shortest way to make it appear again.
-- Claim: From a clean checkpoint in WLD_Main_A, GK.Gui.LongRest() on the client opens a box (End the day?); accepting it with GK.BoxPress(1) lights esv::camp::GatherAtCampCheckEventOneFrameComponent on the server together with esv::camp EndTheDayResultEvent, GatherAtCampCheckEvent, ReadyCheckRequest, Trigger. The rest then runs in the world (night dream dialogue), not in a camp level.
-- Backed by: repeated, reached by play, in GustavX:WLD_Main_A
-- Still unresolved: ["Fields not read (one-frame). Camp travel proper never happens here (TravelError Unavailable); which of the four is written by which step is not separated."]
-- Recorded 2026-11-14. Replay: gatekit --instance <copy> test tests/_coverage -k camp_GatherAtCampCheckEventOneFrame

test { level = "GustavX:WLD_Main_A" }

function run(game)
    game.wait_state("Running", "server", 120)
    -- Watched from before the first step, so a component that lives one frame is seen too.
    watch("server", "esv::camp::GatherAtCampCheckEventOneFrameComponent")

    game.sleep(3)

    game.lua("client", [[
GK.Gui.LongRest()
]])

    game.sleep(3)

    game.lua("client", [[
GK.BoxPress(1)
]])

    game.sleep(4)

    local seen = alive("server", "esv::camp::GatherAtCampCheckEventOneFrameComponent", 10)
    print("esv::camp::GatherAtCampCheckEventOneFrameComponent alive on server: " .. fmt(seen))
end
