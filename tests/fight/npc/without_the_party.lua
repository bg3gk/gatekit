-- Two creatures hostile to each other fight with no party member in the fight: the game opens a
-- fight of the two alone as soon as they are enemies in each other's sight, runs their turns in
-- real time as it runs a player's fight, and the fight log writes it as it writes a player's
-- (docs/contracts/lua-api.md, section 21): `fight_joined` for each, `round_started`, `game_turn`
-- and `turn_ended` for each turn, `fight_action` for each attack, `game_death` and `fight_left`
-- with the killer. The party is told of it by `combat_elsewhere` in place of `combat_started`. The
-- host, 25 m away and an enemy of one of the two, is never taken in (at 12 m it is).
-- Staged with a goblin and a human 3 m apart; both miss for two rounds (the attack die pinned at
-- 1), so that whole rounds pass and their length is read; then the goblin's die is pinned at 20
-- and the human, left with one hit point, dies of the next blow. The rounds' ticks are written to
-- rounds.txt.
-- Staging (cheat): the pair, the pins, the wound, the factions. Reading: observation.
-- Limit: 30 s at speed 1 - two whole rounds of two turns, which the rule is about, and a third begun.
-- Run: gatekit --instance <copy> test tests -k fight/npc/without_the_party --speed working

test { level = "GustavX:Basic_Level_A", surprises = { "combat_elsewhere" },
       about = "Two hostile creatures fight to a death with no party member in the fight, and the fight log writes every row of it." }

local N = require("_npc")

function run(game)
    game.wait_state("Running", "server", 120)
    step("the fight log on", "observation", function()
        game.server("return GK.EventLog.On('fight', { seconds = 600 })")
    end)
    local cast = step("a goblin and a human 25 m from the host, both missing, the human with one hit point", "cheat", function()
        local c = N.pair(game, 25, { goblin = 1, human = 1 })
        local hp = game.server(string.format("return Osi.GetHitpoints(%q)", c.human))
        if hp > 1 then game.server(string.format("return GK.Stage.Damage(%d, %q)", hp - 1, c.human)) end
        return c
    end)
    local staged = game.mark()
    step("the two turn hostile to each other", "cheat", function() N.hostile(game, cast) end)
    local combat = step("a fight of the two alone", "observation", function()
        local id = N.joined(game, cast, staged)
        row(staged, "combat_elsewhere", function(p) return p.combat == id end, 30)
        for _, r in ipairs(game.events(staged, { "fight_joined" }).rows or {}) do
            check(r.payload.combat ~= id or N.uuid(r.payload.who) ~= cast.host, "the host is not a member", r.payload)
        end
        return id
    end)
    step("two rounds pass, each creature acting in each", "observation", function()
        local third = row(staged, "round_started", function(p) return p.combat == combat and p.round == 3 end, 60)
        local starts, acted = {}, {}
        for _, r in ipairs(game.events(staged, { "round_started", "fight_action", "turn_ended" }).rows or {}) do
            local p = r.payload
            if r.kind == "round_started" and p.combat == combat then starts[p.round] = r.tick end
            if r.kind == "fight_action" and r.seq < third.seq then acted[N.uuid(p.who)] = (acted[N.uuid(p.who)] or 0) + 1 end
        end
        check((acted[cast.goblin] or 0) >= 2, "the goblin attacked in each of the two rounds", acted)
        check((acted[cast.human] or 0) >= 2, "the human attacked in each of the two rounds", acted)
        check(starts[1] and starts[2] and starts[3], "the three rounds' starts are in the log", starts)
        game.save("rounds.txt", string.format("round 1: %d server ticks, round 2: %d server ticks (two creatures, one attack each)\n",
            starts[2] - starts[1], starts[3] - starts[2]))
    end)
    local killing = game.mark()
    step("the goblin's die at 20", "cheat", function() N.die(game, cast.goblin, 20) end)
    step("the human dies by the goblin, and the fight ends", "observation", function()
        local death = row(killing, "game_death", function(p) return N.uuid(p.who) == cast.human end, 60)
        eq(N.uuid(death.payload.by), cast.goblin, "the killer")
        local left = row(killing, "fight_left", function(p) return N.uuid(p.who) == cast.human end, 10).payload
        eq(left.why, "killed", "why the human left")
        eq(N.uuid(left.by), cast.goblin, "who killed it")
        local won = row(killing, "fight_left", function(p) return N.uuid(p.who) == cast.goblin end, 10).payload
        eq(won.why, "the fight ended", "why the goblin left")
    end)
    step("the host was never in it", "observation", function()
        N.never_joined(game, cast.host, staged, "the host")
        eq(#(game.events(staged, { "combat_started" }).rows or {}), 0, "fights of the party since the staging")
    end)
end
