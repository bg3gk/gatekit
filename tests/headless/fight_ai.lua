-- A fight the game's AI plays, every throw rigged to one die, the server's logic recorded for a
-- differential check (`gatekit logic-diff`): the same run with the game drawn and with the fork's
-- cuts (RenderCut, FastLoad*, IdleSleep) must leave the same record. Two goblins, the host only
-- ends its own turns; the goblins choose and act by the game's AI for three rounds.
--
-- The turn order is pinned before the fight begins (W3 round 4): goblins spawned hostile beside the
-- host start the fight on the tick they are made, before any pin, and all three rolled 4 (d4 + Dex,
-- the Generic stream gave the same dice every run) - the game then orders a tie by the participants'
-- places in its turn order (eoc::TurnOrderCompare), not by a die, and the two goblins came in either
-- order. So the goblins are made neutral, their initiatives pinned apart and every throw rigged, and
-- only then made enemies: initiative 3+Dex+50, 3+Dex+20, the host 3+Dex - no tie.
-- At the start of each of the host's turns (a still moment: the goblins' turns are over, the
-- host's waits for its player), a logic_digest row (GK.Stage.Digest) of the three: positions,
-- health, statuses, equipment.

test { level = "GustavX:Basic_Level_A", mods = {},
       -- Before CombatStarted the participants draw independently: the same weapon transitions
       -- occur in either order between rendered runs. Assert preparation and compare combat in order.
       logic_diff = { window = "combat_started:family_off", rigged = true } }

function run(game)
    game.wait_state("Running", "server", 60)
    check(game.server("return GK.Stage.Quiet()"), "the tutorials could not be marked as seen")
    -- what is recorded: Osiris events, every throw, the fight's own log (AI options, casts, turns)
    game.server('return GK.EventLog.On({ "osiris", "dice", "fight" }, { seconds = 600 })')

    local goblins = game.server('return GK.Stage.Spawn{ template = "goblin", count = 2, faction = "neutral", ' ..
        'equipment = { "longsword", "shield" } }')
    check(#goblins == 2, "two goblins expected", goblins)
    game.wait("server", ("GK.Stage.Ready(%s)"):format(lua_list(goblins)), 10)
    for _, goblin in ipairs(goblins) do
        game.wait("server", ('((GK.Character("%s").equipment["Melee Main Weapon"] or {}).template or "")'):format(goblin) ..
            ':find("Longsword", 1, true) ~= nil', 10)
    end
    check(game.server(('return GK.Stage.Combat("%s") == nil'):format(goblins[1])), "a neutral goblin is in no fight yet")
    check(game.server(('return GK.Rolls.Pin{ who = "%s", kind = "Initiative", n = 50 }'):format(goblins[1])).ok, "pin 1")
    check(game.server(('return GK.Rolls.Pin{ who = "%s", kind = "Initiative", n = 20 }'):format(goblins[2])).ok, "pin 2")
    -- every source of the dice pinned (W3 round 4): with only the rig, the throws a rig replaces still
    -- came from streams seeded by the load's clock, and the damage rows' `critical` (taken from the
    -- throw before the rig) differed between runs; with the streams' base and seeds fixed, the
    -- throws before the rig are the same too
    local sources = game.server("return GK.Rolls.PinSources(1000)")
    check(sources.ok, "the dice's sources could not be pinned", sources)
    local rig = game.server("return GK.Rolls.Rig{ natural = 3, times = 0 }")
    check(rig.ok, "the throws could not be rigged", rig)
    -- the end of the scenario's stretch in the game's own event (a poll's moment let a goblin's first
    -- act of round 4 into one native record and not another): the families off in the Osiris event
    -- that begins round 4
    game.server([[
        GK.HeadlessFight = { done = false }
        if not GK.HeadlessFightHooked then
            GK.HeadlessFightHooked = true
            Ext.Osiris.RegisterListener("CombatRoundStarted", 2, "after", function(_, round)
                local s = GK.HeadlessFight
                if s and not s.done and round >= 4 then
                    s.done = true
                    GK.EventLog.Off(nil, "the scenario ended")
                end
            end)
        end
        return true]])
    game.server(("return GK.Stage.Fight(%s)"):format(lua_list(goblins)))
    game.wait("server", "(GK.Stage.Combat() or {}).active and true", 20)

    local host = game.server("return Osi.GetHostCharacter()")
    local all = lua_list({ goblins[1], goblins[2], host })
    local order = game.server("return GK.Stage.Combat()").order
    local initiatives = {}
    for _, o in ipairs(order) do initiatives[#initiatives + 1] = o.initiative end
    check(#order == 3 and order[1].uuid == goblins[1] and order[2].uuid == goblins[2] and order[3].uuid == host,
        "the pinned turn order: goblin 1, goblin 2, the host", order)

    -- the host ends each of its turns; the goblins' turns are the AI's, waited for by the turn
    -- passing (the fight's own state), until round 4 begins or the fight is over
    local done = "local c = GK.Stage.Combat(); return c == nil or c.round >= 4"
    local turns = 0
    while not game.server(done) do
        game.wait("server", ('(function() local c = GK.Stage.Combat(); return c == nil or c.round >= 4 or c.active == "%s" end)()'):format(host), 120)
        if game.server(done) then break end
        game.server(('return GK.Stage.Digest(%s, "host turn %d")'):format(all, turns + 1))
        game.server(('return GK.Stage.EndTurn("%s")'):format(host))
        turns = turns + 1
        game.wait("server", ('(function() local c = GK.Stage.Combat(); return c == nil or c.active ~= "%s" end)()'):format(host), 20)
    end
    -- the scenario's stretch ended in round 4's own event (`logic-diff --window combat_started:family_off`);
    -- a fight over sooner ends it here
    game.server('if not GK.HeadlessFight.done then GK.HeadlessFight.done = true GK.EventLog.Off(nil, "the scenario ended") end return true')
    local fight = game.server("return GK.Stage.Combat()")
    local hp = {}
    for i, g in ipairs(goblins) do hp[i] = game.server(('return Osi.GetHitpoints("%s")'):format(g)) end
    local hostHp = game.server(('return Osi.GetHitpoints("%s")'):format(host))
    game.server("return GK.Stage.Reset()")
    print(("fight played by the AI: initiatives %s, %d host turns ended, round %s, goblins' hp %s, host's hp %s")
        :format(join(initiatives), turns, fight and fight.round or "over", join(hp), hostHp))
end
