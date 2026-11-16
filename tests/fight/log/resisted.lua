-- A hit against a resistance says what was rolled and what was dealt: the game_hit row's `types`
-- holds, per damage type, the hit's own TotalDamagePerType (rolled) and FinalDamagePerType (dealt),
-- and `resisted` the resistance that applied (HitDesc.Damage.Resistances; Server/GameLog.lua,
-- docs/contracts/lua-api.md, "The family fight"). Resistance halves the damage, rounded down (bg3.wiki,
-- "Resistance"): a rolled 7 is dealt 3. What the game does with a rolled 1 is its own and is held
-- by test:quirks/damage/resistance_floor.
-- Staged with a hostile goblin five metres off holding Resistance(Fire,Resistant), the host first;
-- the host's Fire Bolt has its attack die rigged to hit and its damage die rigged to 7.
-- Staging (cheat): the host kept alive, the pin, the goblin and its boost, the fight; the cast
-- and its dice (cheat). Reading: observation.
-- Run: gatekit --instance <copy> test tests/fight -k fight/log/resisted

test { level = "GustavX:Basic_Level_A", before = "stage", surprises = { "combat_started" },
           about = "A hit against a resistance says what was rolled and what was dealt." }

local function uuid(id) return id and string.match(id, "(%x+%-%x+%-%x+%-%x+%-%x+)$") or id end

local host, goblin

function stage(game)
    game.wait_state("Running", "server", 120)
    host = uuid(game.server("return Osi.GetHostCharacter()"))
    local staged = game.mark()
    goblin = uuid(step("a fire-resistant goblin five metres off, the host first, the fight", "cheat", function()
        local g = game.server([[
            local h = Osi.GetHostCharacter()
            GK.Stage.Immortal(true, h)
            GK.Rolls.Pin{ kind = "Initiative", n = 100 }
            local x, y, z = Osi.GetPosition(h)
            local g = GK.Stage.Spawn{ template = "goblin", at = { x + 5, y, z }, faction = "enemy" }[1]
            local boost = "Resistance(Fire,Resistant)"
            GK.Stage.Track{ kind = "other", note = "the goblin resists fire", op = { op = "boost", who = g, text = boost, cause = "GKResisted" } }
            Osi.AddBoosts(g, boost, "GKResisted", g)
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

local function bolt(die, dealt)
    return function(game)
        local mark = game.mark()
        step("Fire Bolt, its damage die " .. die, "cheat", function()
            local cast = game.server(string.format([[
                local h = Osi.GetHostCharacter()
                GK.Rolls.Rig{ who = h, kind = "Attack", natural = 15 }
                GK.Rolls.Rig{ who = h, kind = "Damage", natural = %d }
                return GK.Cheat.Cast(h, "Projectile_FireBolt", %q)
            ]], die, goblin))
            check(cast and cast.job, "the cast is asked", cast)
        end)
        step("what was rolled and what was dealt", "observation", function()
            local hit = row(mark, "game_hit", function(p) return uuid(p.who) == goblin and uuid(p.by) == host end, 30).payload
            need(hit.outcome == "hit", "the bolt hit")
            local fire = (hit.types or {}).Fire
            check(fire ~= nil, "the row says the fire damage", hit)
            eq(fire.rolled, die, "rolled: the die")
            eq(fire.dealt, dealt, "dealt: half, rounded down")
            check(hit.resisted ~= nil and hit.resisted[1].type == "Fire", "the row names the resistance that applied", hit.resisted)
        end)
    end
end

case("die 7", bolt(7, 3))
