-- esv::rest::RestFinishedEventOneFrameComponent: the shortest way to make it appear again.
-- Claim: GK.Stage.LongRest() from a cold Act 1 camp level (party of one) makes appear on the server within ~4s: esv::rest::RestFinishedEventOneFrameComponent, UserCharacterRestedEventOneFrameComponent, ScriptRestConfirmedEventOneFrameComponent, RestTypeRequestContentLoadedComponent, ScriptPhaseStartedEventOneFrameComponent, plus eoc::rest::LongRestInScriptPhase on both sides. All are tags (size 0).
-- Backed by: sighting only (one stage call; second and third calls reported nothing new because already seen, not tested for recurrence), reached by cheat, in WLD_Campfire_E
-- Still unresolved: Not known: which of the events is the trigger of the others; whether GK.Gui.LongRest (client, returned ran=CampTravel, endTheDay=Public) produces any of it - it showed nothing new and no blocking box afterwards; esv::camp types did not appear from Stage.LongRest, Stage.Camp(true), Command GoToCamp, Command ShortRest. Not reached: RestTypeRequest, RestTypeChosen, StartRequestFailed, ShortRestConsumeResources.
-- Recorded 2026-11-14.
-- unreplayed since rewrite (2026-11-14): rewritten as a test from the free-form recipe's own steps, not yet replayed live.
-- Replay: gatekit --instance <copy> test tests/_coverage -k long_rest_stage_rest_events

test { level = "GustavX:WLD_Campfire_E" }

function run(game)
    game.wait_state("Running", "server", 120)
    -- Watched from before the first step, so a component that lives one frame is seen too.
    watch("server", "esv::rest::RestFinishedEventOneFrameComponent")

    -- a long rest asked for by the story (cheat), from a cold camp level, party of one
    game.lua("server", [[
GK.Stage.LongRest()
]])
    game.sleep(4)

    local seen = alive("server", "esv::rest::RestFinishedEventOneFrameComponent", 10)
    print("esv::rest::RestFinishedEventOneFrameComponent alive on server: " .. fmt(seen))
end
