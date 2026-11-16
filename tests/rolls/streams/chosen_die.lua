-- A die is a function of its stream's seed and count: the count that gives a wanted die is
-- computed and written, and the game's next roll from that stream shows that die
-- (research/studies/rolls.md, "Who asks for a die"). Staging (cheat, `stage`): two hostile goblins,
-- the host first, the fight started. The die (cheat): GK.Rolls.SetNext. The passive check
-- (cheat): the story's RequestPassiveRoll. The attack (fair): GK.Act.Attack. Reading
-- (observation): the game's own roll and hit rows.
-- Run: gatekit --instance <copy> test tests/rolls -k chosen_die

test { level = "GustavX:Basic_Level_A", before = "stage", surprises = { "combat_started" },
           about = "The stream's count chosen beforehand decides the natural die of a check and of an attack." }

local function uuid(id) return id and string.match(id, "(%x+%-%x+%-%x+%-%x+%-%x+)$") or id end

local host, goblin

function stage(game)
    game.wait_state("Running", "server", 120)
    host = uuid(game.server("return Osi.GetHostCharacter()"))
    local staged = game.mark()
    goblin = uuid(step("the host first, two hostile goblins, the fight", "cheat", function()
        check(game.server("return GK.Rolls.Pin{ kind = 'Initiative', n = 100 }").ok, "the host's initiative is pinned")
        local g = game.server([[
            local x, y, z = Osi.GetPosition(Osi.GetHostCharacter())
            local near = GK.Stage.Spawn{ template = "goblin", at = { x + 1.6, y, z }, faction = "enemy" }[1]
            local off = GK.Stage.Spawn{ template = "goblin", at = { x - 6, y, z }, faction = "enemy" }[1]
            return { near, off }
        ]])
        game.wait("client", string.format("Ext.Entity.Get(%q) ~= nil and Ext.Entity.Get(%q) ~= nil", g[1], g[2]), 10)
        game.server(string.format("Osi.SetHitpoints(%q, 200) return GK.Stage.Fight({ %q, %q })", g[1], g[1], g[2]))
        return g[1]
    end))
    step("the scene is as staged", "observation", function()
        local turn = row(staged, "game_turn", function(p) return p.who ~= nil end, 60)
        need(uuid(turn.payload.who) == host, "the host has the first turn")
    end)
end

case("passive check", function(game)
    local chosen
    step("the die", "cheat", function()
        chosen = game.server("return GK.Rolls.SetNext{ type = 'Generic', die = 13 }")
        check(chosen.ok and chosen.dice[1] == 13, "the Generic stream is at a count that gives 13", chosen)
    end)
    local mark = game.mark()
    step("the check", "cheat", function()
        game.server(string.format([[
            local dc
            for _, id in ipairs(Ext.StaticData.GetAll("DifficultyClass")) do dc = id break end
            Osi.RequestPassiveRoll(%q, %q, "SkillCheck", "Perception", dc, 0, "GKChosenDie")
            return true
        ]], host, host))
    end)
    step("the game's roll", "observation", function()
        local roll = row(mark, "game_roll", function(p) return uuid(p.who) == host end)
        eq(roll.payload.natural, 13, "the natural die")
    end)
end)

local function attack(die, outcome)
    return function(game)
        local mark = game.mark()
        local made = false
        step("an attack, so that the player's attack stream is there", "fair", function()
            if not game.server("return GK.Rolls.Next{ type = 'PlayerSpell' }.ok") then
                check(game.ui(string.format("return GK.Act.Attack(%q)", goblin), 120).ok, "the game finished the attack")
                made = true
            end
        end)
        step("the turn comes back", "cheat", function()
            if made then
                game.server(string.format("return GK.Stage.EndTurn(%q)", host))
                row(mark, "game_turn", function(p) return uuid(p.who) == host end, 120)
            end
        end)
        step("the die", "cheat", function()
            local chosen = game.server(string.format("return GK.Rolls.SetNext{ type = 'PlayerSpell', die = %d, both = true }", die))
            check(chosen.ok and chosen.dice[1] == die, "the attack stream is at a count that gives the die", chosen)
        end)
        mark = game.mark()
        step("attack the goblin", "fair", function()
            local a = game.ui(string.format("return GK.Act.Attack(%q)", goblin), 120)
            check(a.ok, "the game finished the attack", a)
        end)
        step("the game's outcome", "observation", function()
            local hit = row(mark, "game_hit", function(p) return uuid(p.who) == goblin and uuid(p.by) == host end)
            eq(hit.payload.attack and hit.payload.attack.natural, die, "the attack's natural die")
            eq(hit.payload.outcome, outcome, "the game's outcome")
        end)
        step("the turn passes", "cheat", function()
            local passed = game.mark()
            game.server(string.format("return GK.Stage.EndTurn(%q)", host))
            row(passed, "game_turn", function(p) return uuid(p.who) == host end, 120)
        end)
    end
end

case("attack 20", attack(20, "critical"))
case("attack 1", attack(1, "critical miss"))
