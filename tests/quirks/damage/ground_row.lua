-- Every weapon attack throws a second damage roll that takes no hit points: the main-hand attack's
-- own data deals the weapon's damage at the ground under the target as the attack is cast, before
-- its roll (Target_MainHandAttack, SpellProperties: DealDamage at GROUND), so a miss - here a
-- natural 1 - still throws the weapon's damage dice, from the attacker's own damage stream, while
-- the target's hit points do not change. Neither the hit, the combat log nor the tooltip shows
-- this roll; by bg3.wiki ("Attack rolls") a missed attack deals no damage and throws none.
-- research/studies/rolls.md, "Asking the game for a chance" and "The dice against the wiki". Found in build 4.1.1.7631656; when this fails,
-- the game changed (a tool that counts the dice drawn from a stream counts this one).
-- Staging (cheat): the host first, a hostile goblin beside it kept alive, the fight; the dagger
-- attack with its die rigged to 1. Reading: observation.
-- Run: gatekit --instance <copy> test tests -k quirks/damage/ground_row

test { level = "GustavX:Basic_Level_A", surprises = { "combat_started" },
           about = "Every weapon attack throws a second damage roll that takes no hit points." }

local function uuid(id) return id and string.match(id, "(%x+%-%x+%-%x+%-%x+%-%x+)$") or id end

function run(game)
    game.wait_state("Running", "server", 120)
    local host = uuid(game.server("return Osi.GetHostCharacter()"))
    step("the roll log on", "observation", function()
        check(game.server("return GK.RollLog.Available()"), "this Script Extender build throws the roll event")
        game.server("return GK.EventLog.On('dice', { seconds = 600 })")
    end)
    local staged = game.mark()
    local goblin = uuid(step("a goblin beside the host, the fight", "cheat", function()
        local g = game.server([[
            local h = Osi.GetHostCharacter()
            GK.Rolls.Pin{ kind = "Initiative", n = 100 }
            local x, y, z = Osi.GetPosition(h)
            local g = GK.Stage.Spawn{ template = "goblin", at = { x + 1.5, y, z }, faction = "enemy" }[1]
            GK.Stage.Immortal(true, g)
            return g
        ]])
        game.wait("client", string.format("Ext.Entity.Get(%q) ~= nil", g), 10)
        game.server(string.format("return GK.Stage.Fight({ %q })", g))
        return g
    end))
    step("the host's turn", "observation", function()
        local turn = row(staged, "game_turn", function(p) return p.who ~= nil end, 60)
        need(uuid(turn.payload.who) == host, "the host has the first turn")
    end)
    local before = game.server(string.format("return Osi.GetHitpoints(%q)", goblin))
    local mark = game.mark()
    step("the dagger, its die rigged to 1", "cheat", function()
        local cast = game.server(string.format([[
            GK.Rolls.Rig{ who = %q, kind = "Attack", natural = 1 }
            return GK.Cheat.Cast(%q, "Target_MainHandAttack", %q)
        ]], host, host, goblin))
        check(cast and cast.job, "the cast is asked", cast)
    end)
    step("a miss, a damage roll, no hit points taken", "observation", function()
        local hit = row(mark, "game_hit", function(p) return uuid(p.who) == goblin and uuid(p.by) == host end, 30)
        eq(hit.payload.outcome, "critical miss", "the attack missed")
        local damage = row(mark, "dice_thrown", function(p) return uuid(p.who) == host and string.match(p.kind or "", "Damage$") end, 5).payload
        eq(damage.spell, "Target_MainHandAttack", "the damage roll belongs to the attack")
        eq(damage.cause, "damage at a position", "thrown for the damage at the ground")
        check(#(damage.dice or {}) >= 1, "its dice were drawn", damage)
        eq(game.server(string.format("return Osi.GetHitpoints(%q)", goblin)), before, "the goblin's hit points did not change")
        local dealt = game.events(mark, { "game_damage" })
        for _, r in ipairs(dealt.rows) do check(uuid(r.payload.who) ~= goblin, "no damage row of the goblin", r.payload) end
        print(("missed with a natural 1; thrown %s %s = %s for the ground; hit points %d before and after")
            :format(tostring(damage.count) .. tostring(damage.dieType), join(damage.dice, "+"), tostring(damage.total), before))
    end)
end
