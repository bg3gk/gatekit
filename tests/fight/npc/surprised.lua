-- A creature caught off guard loses its first turn: the status SURPRISED blocks its action, bonus
-- action, movement and reaction, and leaves at the end of the round (bg3.wiki, "Surprised"; the
-- game's data: the status's boosts are four ActionResourceBlock, its TickType EndRound). The story
-- gives it by script for one turn before the fight begins (its PROC_MakeSurprised applies the
-- status for 6 seconds a turn), and so does this test, between two creatures with no party member
-- in the fight: in round 1 the surprised one's turn begins and ends with no `fight_choice` and no
-- `fight_action` of its own while the other attacks; the status leaves as round 2 begins, and in
-- round 2 it attacks.
-- Stands on test:fight/npc/without_the_party (such a fight begins and runs).
-- Staged with a goblin and a human 3 m apart, 25 m from the host, both missing (the attack die
-- pinned at 1) so that the fight lasts; the human surprised.
-- Staging (cheat): the pair, the pins, the status, the factions. Reading: observation.
-- Limit: 20 s at speed 1 - one whole round and the surprised creature's turn of the second.
-- Run: gatekit --instance <copy> test tests -k fight/npc/surprised --speed working

test { level = "GustavX:Basic_Level_A", surprises = { "combat_elsewhere" },
       about = "A surprised creature's first turn passes with no action of its own; the status leaves with the round and it acts in the second." }

local N = require("_npc")

function run(game)
    game.wait_state("Running", "server", 120)
    step("the fight log on", "observation", function()
        game.server("return GK.EventLog.On('fight', { seconds = 600 })")
    end)
    local cast = step("a goblin and a human 25 m from the host, both missing", "cheat", function()
        return N.pair(game, 25, { goblin = 1, human = 1 })
    end)
    local staged = game.mark()
    step("the human surprised for one turn, the two hostile", "cheat", function()
        game.server(string.format("return GK.Stage.Status('SURPRISED', %q, 1)", cast.human))
        N.hostile(game, cast)
    end)
    local combat = step("a fight of the two, the human surprised in it", "observation", function()
        local id = N.joined(game, cast, staged)
        local s = row(staged, "game_status", function(p) return p.status == "SURPRISED" and N.uuid(p.who) == cast.human end, 30).payload
        eq(s.turns, 1, "the status's turns")
        return id
    end)
    step("round 1: the human's turn passes with nothing done, the goblin attacks", "observation", function()
        local second = row(staged, "round_started", function(p) return p.combat == combat and p.round == 2 end, 60)
        local turn, ended, mine, its = nil, nil, 0, 0
        for _, r in ipairs(game.events(staged, { "game_turn", "turn_ended", "fight_choice", "fight_action" }).rows or {}) do
            if r.seq < second.seq or (r.kind == "turn_ended" and r.payload.round == 1) then
                local who = N.uuid(r.payload.who)
                if r.kind == "game_turn" and who == cast.human then turn = r.tick end
                if r.kind == "turn_ended" and who == cast.human then ended = r.tick end
                if (r.kind == "fight_choice" or r.kind == "fight_action") and who == cast.human then mine = mine + 1 end
                if r.kind == "fight_action" and who == cast.goblin then its = its + 1 end
            end
        end
        check(turn and ended, "the human's turn began and ended in round 1", turn, ended)
        eq(mine, 0, "the surprised human's choices and actions in round 1")
        eq(its, 1, "the goblin's attacks in round 1")
        game.save("surprised.txt", string.format("the surprised creature's turn: %d server ticks\n", ended - turn))
    end)
    step("round 2: the status has left and the human attacks", "observation", function()
        local second = row(staged, "round_started", function(p) return p.combat == combat and p.round == 2 end, 10)
        row(staged, "game_unstatus", function(p) return p.status == "SURPRISED" and N.uuid(p.who) == cast.human end, 30)
        local act = row(staged, "fight_action", function(p, r) return N.uuid(p.who) == cast.human and r.seq > second.seq end, 60)
        eq(N.uuid(act.payload.targets[1]), cast.goblin, "whom the human attacks")
        N.never_joined(game, cast.host, staged, "the host")
    end)
end
