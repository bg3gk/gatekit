-- The whole WLD_Main_A arrival, for a differential check (`gatekit logic-diff --unordered
-- --mask-ids`): the party sent from Basic_Level_A by the story's own call, the arrival cinematic
-- (CRA_Crash, ~80 s) played at the game's pace, the wake-up dialogue (CRA_AvatarWakesUp) answered
-- with its first choice at every choice, the server's Osiris events, throws and statuses recorded
-- from the call to the end of the wake-up dialogue (the recording switched off in that Osiris
-- event) - past both the new level's gameplay start and the courier's scripted death, whose order
-- is a race of a server timer and the client's readiness (level_change.lua). Drawn and headless
-- (nothing drawn from the process start) must record the same. Event-driven: every wait is on the
-- game's own state. W3 round 6.

test { level = "GustavX:Basic_Level_A", mods = {}, reset = "venue",
       -- what differs between two runs of the game as it is: an ambient goblin's rock throws and the
       -- surfaces walkers step in, and the number the game gives a dialogue instance
       logic_diff = { window = "family_on:family_off", rigged = true, unordered = true, mask_ids = true, ignore = { "instance" },
                      set_aside = { ["ThrowRock"] = "an ambient goblin throws rocks a different number of times in every run",
                                    ["INSURFACE"] = "ambient walkers step in surfaces a different number of times in every run" },
                      -- the dialogue instance's number, and the name the crypt's fire flask stack gets (001 or 002:
                      -- two runs of the game as it is differ there too)
                      rewrite = { [ [[^(osiris \{args:\["[^"]*",)\d+(,\],event:"Dialog(?:Started|Ended)")]] ] = "$1#$2",
                                  [ [[(GRN_FireFlask_A_)\d{3}(_#)]] ] = "$1nnn$2" } } }

function run(game)
    game.wait_state("Running", "server", 60)
    check(game.server("return GK.Stage.Quiet()"), "the tutorials could not be marked as seen")
    local host = game.server("return Osi.GetHostCharacter()")
    game.server(([[
        GK.HeadlessArrival = { host = %q, done = false }
        if not GK.HeadlessArrivalHooked then
            GK.HeadlessArrivalHooked = true
            Ext.Osiris.RegisterListener("DialogEnded", 2, "after", function(dialog, instance)
                local s = GK.HeadlessArrival
                if s and not s.done and tostring(dialog):find("CRA_AvatarWakesUp", 1, true) then
                    s.done = true
                    GK.Stage.Digest({ s.host }, "the wake-up dialogue ended")
                    GK.EventLog.Off(nil, "the scenario ended")
                end
            end)
        end
        return true]]):format(host))
    game.server("return GK.EventLog.Keep(128, 24 * 1024 * 1024)")
    game.server('return GK.EventLog.On({ "osiris", "dice", "status" }, { seconds = 900 })')
    check(game.server("return GK.Rolls.PinSources(1000)").ok, "the dice's sources could not be pinned")
    game.server(('return GK.Stage.Digest({ "%s" }, "before the change")'):format(host))
    local mark = game.mark()
    game.server('return GK.Stage.Level("WLD_Main_A")')
    -- the dialogue's choices, answered as they come (the client's attention rows); the arrival is
    -- over when the server switches its recording off at the wake-up's end
    local answered = 0
    while true do
        local row = game.wait_row(mark, { "dialogue_choice", "family_off" }, nil, 300)
        mark = game.mark()
        if row.kind == "family_off" then break end
        game.answer(1)
        answered = answered + 1
        check(answered < 20, "more choices than the wake-up has", answered)
    end
    game.wait_state("Running", "server", 60)
    local level = game.server("return GK.Stage.CurrentLevel()")
    check(level == "WLD_Main_A", "the party is in WLD_Main_A", level)
    print(("the arrival played through: %d choice(s) answered"):format(answered))
end
