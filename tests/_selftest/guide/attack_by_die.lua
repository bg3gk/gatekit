-- A natural 1 misses and a natural 20 is a critical hit, whatever the bonus and the armour class
-- (bg3.wiki, "Dice rolls"). Staging (cheat, `stage`): two hostile goblins, one beside the host
-- and one standing off, so that a kill does not end the fight; the host's initiative pinned so
-- that it acts first; the fight started. Attack (fair): GK.Act.Attack - the hotbar's main-hand
-- attack, the aim, the click, answered when the game has finished it. Reading (observation): the
-- game's own hit row.

test { level = "GustavX:Basic_Level_A", before = "stage", surprises = { "combat_started" },
           about = "A natural 1 misses and a natural 20 is a critical hit." }

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
        game.server(string.format("return GK.Stage.Fight({ %q, %q })", g[1], g[2]))
        return g[1]
    end))
    step("the scene is as staged", "observation", function()
        need(game.server(string.format("return Osi.IsEnemy(%q, Osi.GetHostCharacter())", goblin)) == 1,
            "the goblin is hostile to the host: attacked, it fights and does not talk")
        local turn = row(staged, "game_turn", function(p) return p.who ~= nil end, 60)
        need(uuid(turn.payload.who) == host, "the host has the first turn")
    end)
end

local function attack(die, outcome)
    return function(game)
        step("the die", "cheat", function()
            local pinned = game.server(string.format("return GK.Rolls.Pin{ who = %q, kind = 'Attack', n = %d }", host, die))
            check(pinned.ok and pinned.inForce, "the attack die is pinned", pinned)
        end)
        local mark = game.mark()
        step("attack the goblin", "fair", function()
            local a = game.ui(string.format("return GK.Act.Attack(%q)", goblin), 120)
            check(a.ok, "the game finished the attack", a)
        end)
        step("the game's outcome", "observation", function()
            local hit = row(mark, "game_hit", function(p) return uuid(p.who) == goblin and uuid(p.by) == host end)
            eq(hit.payload.outcome, outcome, "the game's outcome")
        end)
    end
end

case("die 1", attack(1, "critical miss"))
case("die 20", attack(20, "critical"))
