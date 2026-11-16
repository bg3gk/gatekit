-- Resistance never brings a damage below 1: a weapon's damage of 1 against a resistance is dealt
-- as 1, where bg3.wiki ("Resistance": the damage is halved, rounded down) gives 0; a damage of 3
-- is dealt as 1, as the wiki says. Found in build 4.1.1.7631656; when this fails, the game changed.
-- The same floor for a spell's damage (a Fire Bolt of 1) is held by test:fight/log/resisted; this
-- test holds it for a weapon, whose damage goes another way (the weapon's own roll, not a stats
-- expression). Read from the game_hit row's `types` (rolled, dealt) and `resisted`.
-- Staging (cheat): the host first, a hostile goblin beside it holding Resistance(Piercing,
-- Resistant), the fight; the dagger attack and its dice rigged (attack 15, damage total the case's).
-- Reading: observation.
-- Run: gatekit --instance <copy> test tests -k quirks/damage/resistance_floor

test { level = "GustavX:Basic_Level_A", before = "stage", surprises = { "combat_started" },
           about = "Resistance never brings damage below 1." }

local function uuid(id) return id and string.match(id, "(%x+%-%x+%-%x+%-%x+%-%x+)$") or id end

local host, goblin

function stage(game)
    game.wait_state("Running", "server", 120)
    host = uuid(game.server("return Osi.GetHostCharacter()"))
    step("the roll log on", "observation", function()
        check(game.server("return GK.Rolls.CanRig()"), "this Script Extender build raises the roll event")
    end)
    local staged = game.mark()
    goblin = uuid(step("a goblin that resists piercing beside the host, the fight", "cheat", function()
        local g = game.server([[
            local h = Osi.GetHostCharacter()
            GK.Rolls.Pin{ kind = "Initiative", n = 100 }
            local x, y, z = Osi.GetPosition(h)
            local g = GK.Stage.Spawn{ template = "goblin", at = { x + 1.5, y, z }, faction = "enemy" }[1]
            local boost = "Resistance(Piercing,Resistant)"
            GK.Stage.Track{ kind = "other", note = "the goblin resists piercing", op = { op = "boost", who = g, text = boost, cause = "GKQuirk" } }
            Osi.AddBoosts(g, boost, "GKQuirk", g)
            GK.Stage.Immortal(true, g)
            return g
        ]])
        game.wait("client", string.format("Ext.Entity.Get(%q) ~= nil", g), 10)
        game.server(string.format("return GK.Stage.Fight({ %q })", g))
        return g
    end))
    step("the scene is as staged", "observation", function()
        local turn = row(staged, "game_turn", function(p) return p.who ~= nil end, 60)
        need(uuid(turn.payload.who) == host, "the host has the first turn")
    end)
end

local function stab(rolled, dealt)
    return function(game)
        local mark = game.mark()
        step("the dagger, its damage " .. rolled, "cheat", function()
            local cast = game.server(string.format([[
                local h = Osi.GetHostCharacter()
                GK.Rolls.Rig{ who = h, kind = "Attack", natural = 15 }
                GK.Rolls.Rig{ who = h, kind = "Damage", total = %d }
                return GK.Cheat.Cast(h, "Target_MainHandAttack", %q)
            ]], rolled, goblin))
            check(cast and cast.job, "the cast is asked", cast)
        end)
        step("rolled and dealt", "observation", function()
            local hit = row(mark, "game_hit", function(p) return uuid(p.who) == goblin and uuid(p.by) == host end, 30).payload
            need(hit.outcome == "hit", "the dagger hit")
            local piercing = (hit.types or {}).Piercing
            check(piercing ~= nil, "the row says the piercing damage", hit)
            eq(piercing.rolled, rolled, "rolled: the rigged damage")
            check(hit.resisted ~= nil and hit.resisted[1].type == "Piercing", "the resistance applied", hit.resisted)
            eq(piercing.dealt, dealt, "dealt: half rounded down, but never below 1")
            print(("rolled %d, dealt %d (the wiki's half: %d)"):format(piercing.rolled, piercing.dealt, math.floor(rolled / 2)))
        end)
    end
end

case("weapon_of_one", stab(1, 1))
case("three_becomes_one", stab(3, 1))
