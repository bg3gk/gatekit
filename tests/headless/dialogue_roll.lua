-- A dialogue with an active roll, for a differential check (`gatekit logic-diff`): a staged
-- talker's dialogue whose first answer is a Persuasion check (GK.Fixture.Talker{ dialog = "roll" }),
-- the host's skill check pinned at 20, the answer chosen, the roll thrown, the dialogue walked to its
-- end by its last answers. The server's record (Osiris events, every throw, the game log) of a run
-- drawn and a run with the fork's cuts must be the same. Event-driven: every wait is on the game's
-- own state (the attention rows of the dialogue and the roll window, the server's dialogue flag);
-- no sleeps. W3 round 4 of the start-zone night.

test { level = "GustavX:Basic_Level_A", mods = {},
       logic_diff = { window = "family_on:family_off", rigged = true } }

function run(game)
    game.wait_state("Running", "server", 60)
    check(game.server("return GK.Stage.Quiet()"), "the tutorials could not be marked as seen")
    game.server('return GK.EventLog.On({ "osiris", "dice" }, { seconds = 600 })')

    local host = game.server("return Osi.GetHostCharacter()")
    game.server("GK.FixtureT = GK.Fixture.Talker{ dialog = 'roll' } return true")
    game.wait("server", "GK.Fixture.Ready('Talker')", 15)
    local talker = game.server("return GK.FixtureT.entities[1].id")
    check(game.server("return GK.Rolls.PinSources(1000)").ok, "the dice's sources could not be pinned")
    local pin = game.server(('return GK.Rolls.Pin{ who = "%s", kind = "SkillCheck", n = 20 }'):format(host))
    check(pin.ok and pin.inForce, "the host's skill check is pinned", pin)
    game.server(('return GK.Stage.Digest({ "%s", "%s" }, "before the talk")'):format(host, talker))

    local mark = game.mark()
    game.server(('return GK.Cheat.Talk(nil, "%s")'):format(talker))
    game.wait_attention({ "dialogue", "dialogue_choice" }, 30, mark)
    game.wait("ui", "GK.Gui.DialoguePhase() and GK.Gui.DialoguePhase().phase == 'choice'", 30)
    game.ui("return GK.Answer(1)")
    game.wait_attention({ "roll" }, 30, mark)
    -- every window the game puts up is answered as it comes - the roll thrown, the dialogue's last
    -- answer chosen - until none is left; each poll acts only on what the game shows at that moment
    game.wait("ui", "(function() local b = GK.Gui.Blocking().window; if b then GK.Gui.Resolve({ roll = 'throw', dialogue = 'last' }) end; return not b end)()", 60)
    game.wait("server", ('GK.InDialog("%s") == false'):format(host), 30)

    local rolled
    for _, row in ipairs(game.events(mark, { "game_roll" }).rows or {}) do
        if row.payload.active then rolled = row.payload end
    end
    check(rolled and rolled.natural == 20 and rolled.success == true, "the pinned check passed", rolled)
    game.server(('return GK.Stage.Digest({ "%s", "%s" }, "after the talk")'):format(host, talker))
    -- the scenario's stretch ends here (`logic-diff --window family_on:family_off`)
    game.server('return GK.EventLog.Off(nil, "the scenario ended")')
    game.server(('return GK.Rolls.Unpin{ who = "%s", kind = "SkillCheck" }'):format(host))
    print(("dialogue with a roll: natural %s against %s, success %s"):format(rolled.natural, rolled.dc, tostring(rolled.success)))
end
