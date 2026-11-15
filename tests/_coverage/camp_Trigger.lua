-- esv: the shortest way to make it appear again.
-- Claim: From a clean checkpoint in WLD_Main_A, GK.Gui.LongRest() on the client opens a box (End the day?); accepting it with GK.BoxPress(1) lights esv::camp::TriggerComponent on the server together with esv::camp EndTheDayResultEvent, GatherAtCampCheckEvent, ReadyCheckRequest, Trigger. The rest then runs in the world (night dream dialogue), not in a camp level.
-- Backed by: repeated, reached by play, in GustavX:WLD_Main_A
-- Still unresolved: ["Fields not read (one-frame). Camp travel proper never happens here (TravelError Unavailable); which of the four is written by which step is not separated."]
-- Recorded 2026-11-14. Replay: gatekit --instance <copy> test tests/_coverage -k camp_Trigger

test { level = "GustavX:WLD_Main_A" }

function run(game)
    game.wait_state("Running", "server", 120)
    -- Watched from before the first step, so a component that lives one frame is seen too.
    watch("server", "esv::camp::TriggerComponent")

    game.sleep(3)

    game.lua("client", [[
GK.Gui.LongRest()
]])

    game.sleep(3)

    game.lua("client", [[
GK.BoxPress(1)
]])

    game.sleep(4)

    -- A camp trigger can stand in the world before the steps (up to 6 held at once in the record):
    -- the replay needs one more holder, or a new one, not merely one there.
    local seen = alive("server", "esv::camp::TriggerComponent", 10, { may_exist = true })
    print("esv::camp::TriggerComponent alive on server: " .. fmt(seen))
end
