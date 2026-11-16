-- Who is in a fight is in the fight log: a `fight_joined` row for each creature the fight takes
-- in, with the fight's id and the creature's initiative, and `underway` for one that joins a fight
-- already begun; a `fight_left` row for each that leaves, with why when the game says - killed
-- (and by whom, Osiris KilledBy, which the game's own `game_death` row carries as `by` too),
-- fled, or the fight ended (Osiris CombatEnded) (docs/contracts/
-- lua-api.md, section 21; the family `fight`, Server/FightLog.lua). Staged with a goblin beside
-- the host; a second goblin is made and brought into the fight on the host's first turn; the host
-- kills the first with its own attack, and the second is removed by a cheat, which ends the fight.
-- Staging (cheat): the pins, the goblins, the fight, the first goblin's wounds, the second's
-- death. The attack is the player's own (fair). Reading: observation.
-- Run: gatekit --instance <copy> test tests/fight -k fight/log/membership

test { level = "GustavX:Basic_Level_A", surprises = { "combat_started" },
           about = "The fight log says who joined a fight, at its start or later, and who left it and why." }

local function uuid(id) return id and string.match(id, "(%x+%-%x+%-%x+%-%x+%-%x+)$") or id end

function run(game)
    game.wait_state("Running", "server", 120)
    local host = uuid(game.server("return Osi.GetHostCharacter()"))
    step("the fight log on", "observation", function()
        game.server("return GK.EventLog.On('fight', { seconds = 600 })")
    end)
    local staged = game.mark()
    local first = step("a goblin beside the host, the host first, the fight", "cheat", function()
        local g = game.server([[
            local h = Osi.GetHostCharacter()
            GK.Stage.Immortal(true, h)
            GK.Rolls.Pin{ kind = "Initiative", n = 100 }
            local x, y, z = Osi.GetPosition(h)
            return GK.Stage.Spawn{ template = "goblin", at = { x + 1.6, y, z }, faction = "enemy" }[1]
        ]])
        game.wait("client", string.format("Ext.Entity.Get(%q) ~= nil", g), 10)
        game.server(string.format("return GK.Stage.Fight({ %q })", g))
        return uuid(g)
    end)
    local combat = step("both joined at the start", "observation", function()
        local turn = row(staged, "game_turn", function(p) return p.who ~= nil end, 60)
        need(uuid(turn.payload.who) == host, "the host has the first turn")
        local h = row(staged, "fight_joined", function(p) return uuid(p.who) == host end, 5).payload
        local g = row(staged, "fight_joined", function(p) return uuid(p.who) == first end, 5).payload
        check(h.combat ~= nil and h.combat == g.combat, "one fight, named in both rows", h, g)
        check(h.initiative ~= nil and h.initiative > g.initiative, "the host's pinned initiative is above the goblin's", h, g)
        eq(h.underway, nil, "the host joined a fight not yet begun")
        return h.combat
    end)
    local later = game.mark()
    local second = step("a second goblin brought into the fight", "cheat", function()
        local g = game.server([[
            local x, y, z = Osi.GetPosition(Osi.GetHostCharacter())
            return GK.Stage.Spawn{ template = "goblin", at = { x - 4, y, z + 2 }, faction = "enemy" }[1]
        ]])
        game.wait("client", string.format("Ext.Entity.Get(%q) ~= nil", g), 10)
        game.server(string.format("return GK.Stage.Fight({ %q })", g))
        return uuid(g)
    end)
    step("it joined the fight under way", "observation", function()
        local j = row(later, "fight_joined", function(p) return uuid(p.who) == second end, 30).payload
        eq(j.combat, combat, "the same fight")
        eq(j.underway, true, "the fight had begun")
    end)
    step("the first goblin left with one hit point, the attack's die pinned", "cheat", function()
        local hp = game.server(string.format("return Osi.GetHitpoints(%q)", first))
        if hp > 1 then game.server(string.format("return GK.Stage.Damage(%d, %q)", hp - 1, first)) end
        check(game.server(string.format("return GK.Rolls.Pin{ who = %q, kind = 'Attack', n = 20 }", host)).ok, "the host's attack die is pinned")
    end)
    local killing = game.mark()
    step("the host kills the first goblin", "fair", function()
        check(game.ui(string.format("return GK.Act.Attack(%q)", first), 120).ok, "the game finished the attack")
    end)
    step("it left the fight killed, by the host", "observation", function()
        local l = row(killing, "fight_left", function(p) return uuid(p.who) == first end, 30).payload
        eq(l.combat, combat, "the same fight")
        eq(l.why, "killed", "why it left")
        eq(uuid(l.by), host, "who killed it")
        local death = row(killing, "game_death", function(p) return uuid(p.who) == first end, 30)
        eq(uuid(death.payload.by), host, "the death's own row names the killer")
    end)
    local ending = game.mark()
    step("the second goblin dies", "cheat", function()
        game.server(string.format("return GK.Stage.Kill(%q)", second))
    end)
    step("the host left because the fight ended", "observation", function()
        local l = row(ending, "fight_left", function(p) return uuid(p.who) == host end, 30).payload
        eq(l.combat, combat, "the same fight")
        eq(l.why, "the fight ended", "why the host left")
    end)
end
