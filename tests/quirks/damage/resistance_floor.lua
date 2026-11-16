-- Resistance never brings a damage below 1: a damage of 1 against a resistance is dealt as 1,
-- where bg3.wiki ("Resistance": the damage is halved, rounded down) gives 0; a damage of 3 is
-- dealt as 1, as the wiki says, and is the control. Found in build 4.1.1.7631656; when this
-- fails, the game changed. The floor is held for the two ways a damage is rolled: a weapon's own
-- roll (the dagger) and the die of a spell's stats expression (a Fire Bolt). Read from the
-- game_hit row's `types` (rolled, dealt) and `resisted`, the fields test:fight/log/resisted holds.
-- Staging (cheat): the host first, a hostile goblin holding Resistance(Piercing,Resistant) and
-- Resistance(Fire,Resistant) - beside the host for the dagger, five metres off for the bolt (a
-- Fire Bolt asked beside the target makes the host step away first and no hit comes within the
-- limit) - the fight; the attack and its dice rigged (attack 15, the damage the case's).
-- Reading: observation.
-- Run: gatekit --instance <copy> test tests -k quirks/damage/resistance_floor

test { level = "GustavX:Basic_Level_A", surprises = { "combat_started" },
           about = "Resistance never brings damage below 1." }

local function uuid(id) return id and string.match(id, "(%x+%-%x+%-%x+%-%x+%-%x+)$") or id end

local host, goblin

--- The scene of a case: the goblin `metres` from the host (beside it for the dagger, five metres
--- off for the bolt, as test:fight/log/resisted stages it).
local function stage(game, metres)
    game.wait_state("Running", "server", 120)
    host = uuid(game.server("return Osi.GetHostCharacter()"))
    step("the roll log on", "observation", function()
        check(game.server("return GK.Rolls.CanRig()"), "this Script Extender build raises the roll event")
    end)
    local staged = game.mark()
    goblin = uuid(step("a goblin that resists piercing and fire " .. metres .. " m from the host, the fight", "cheat", function()
        local g = game.server(string.format([[
            local h = Osi.GetHostCharacter()
            GK.Rolls.Pin{ kind = "Initiative", n = 100 }
            local x, y, z = Osi.GetPosition(h)
            local g = GK.Stage.Spawn{ template = "goblin", at = { x + %f, y, z }, faction = "enemy" }[1]
            local boost = "Resistance(Piercing,Resistant);Resistance(Fire,Resistant)"
            GK.Stage.Track{ kind = "other", note = "the goblin resists piercing and fire", op = { op = "boost", who = g, text = boost, cause = "GKQuirk" } }
            Osi.AddBoosts(g, boost, "GKQuirk", g)
            GK.Stage.Immortal(true, g)
            return g
        ]], metres))
        game.wait("client", string.format("Ext.Entity.Get(%q) ~= nil", g), 10)
        game.server(string.format("return GK.Stage.Fight({ %q })", g))
        return g
    end))
    step("the scene is as staged", "observation", function()
        local turn = row(staged, "game_turn", function(p) return p.who ~= nil end, 60)
        need(uuid(turn.payload.who) == host, "the host has the first turn")
    end)
end

--- One hit of `spell` whose damage of `kind` is rigged to `rolled` (`rig`: how the rig names it -
--- the weapon's total, or the natural die of a spell's expression), and what must be dealt.
local function strike(what, spell, kind, rig, rolled, dealt, metres)
    return function(game)
        stage(game, metres)
        local mark = game.mark()
        step(what .. ", its damage " .. rolled, "cheat", function()
            local cast = game.server(string.format([[
                local h = Osi.GetHostCharacter()
                GK.Rolls.Rig{ who = h, kind = "Attack", natural = 15 }
                GK.Rolls.Rig{ who = h, kind = "Damage", %s = %d }
                return GK.Cheat.Cast(h, %q, %q)
            ]], rig, rolled, spell, goblin))
            check(cast and cast.job, "the cast is asked", cast)
        end)
        step("rolled and dealt", "observation", function()
            local hit = row(mark, "game_hit", function(p) return uuid(p.who) == goblin and uuid(p.by) == host end, 30).payload
            need(hit.outcome == "hit", what .. " hit")
            local damage = (hit.types or {})[kind]
            check(damage ~= nil, "the row says the " .. kind .. " damage", hit)
            eq(damage.rolled, rolled, "rolled: the rigged damage")
            check(hit.resisted ~= nil and hit.resisted[1].type == kind, "the resistance applied", hit.resisted)
            eq(damage.dealt, dealt, "dealt: half rounded down, but never below 1")
            print(("rolled %d, dealt %d (the wiki's half: %d)"):format(damage.rolled, damage.dealt, math.floor(rolled / 2)))
        end)
    end
end

case("weapon_of_one", strike("the dagger", "Target_MainHandAttack", "Piercing", "total", 1, 1, 1.5))
case("three_becomes_one", strike("the dagger", "Target_MainHandAttack", "Piercing", "total", 3, 1, 1.5))
case("fire_bolt_of_one", strike("the Fire Bolt", "Projectile_FireBolt", "Fire", "natural", 1, 1, 5))
