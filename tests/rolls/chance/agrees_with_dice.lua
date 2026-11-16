-- The chance the game works out for an attack before it is made is the chance of the dice that
-- are then thrown: with a difficulty (the target's Armour Class) and a bonus read from the roll
-- itself, a d20 hits on (21 - (difficulty - bonus)) faces of twenty, a natural 1 always missing
-- and a natural 20 always hitting (wiki: Attack rolls; research/studies/rolls.md, "Asking the game for a chance").
-- GK.Rolls.Chance asks the game (docs/contracts/lua-api.md, "GK.Rolls"); the attack is then made
-- for real and its row of the roll log read. Each case is a weapon attack with another weapon
-- set: the chance is asked while the other set is in hand, which is where a chance asked
-- carelessly goes wrong (the game counts the weapons in hand).
-- Staging (cheat): the goblin kept alive, the fight, the other weapon set put in hand (a row of
-- the stage ledger, so the clean puts the set back), the cast. Reading: observation.
-- Run: gatekit --instance <copy> test tests/rolls -k rolls/chance/agrees_with_dice

test { level = "GustavX:Basic_Level_A", surprises = { "combat_started" },
           about = "The game's own chance of an attack is the chance of the dice it then throws." }

local function uuid(id) return id and string.match(id, "(%x+%-%x+%-%x+%-%x+%-%x+)$") or id end

local CASES = {
    { name = "melee", spell = "Target_MainHandAttack", distance = 2, held = "Ranged" },
    { name = "ranged", spell = "Projectile_MainHandAttack", distance = 8, held = "Melee" },
}

for _, c in ipairs(CASES) do
    case(c.name, function(game)
        game.wait_state("Running", "server", 120)
        local host = uuid(game.server("return Osi.GetHostCharacter()"))
        step("the roll log on", "observation", function()
            need(game.server("return GK.Verbs.Lacks('Rolls.Chance') == nil"), "this Script Extender build asks the game for a chance")
            game.server("return GK.EventLog.On('dice', { seconds = 600 })")
        end)
        local staged = game.mark()
        local goblin = step("a goblin, the fight, the other weapon set in hand", "cheat", function()
            local g = game.server(string.format([[
                local h = Osi.GetHostCharacter()
                GK.Rolls.Pin{ kind = "Initiative", n = 100 }
                local x, y, z = Osi.GetPosition(h)
                local g = GK.Stage.Spawn{ template = "goblin", at = { x + %d, y, z }, faction = "enemy" }[1]
                GK.Stage.Immortal(true, g)
                return g
            ]], c.distance))
            game.wait("client", string.format("Ext.Entity.Get(%q) ~= nil", g), 10)
            game.server(string.format("return GK.Stage.Fight({ %q })", g))
            -- the weapon set through the stage ledger (the fixtures' `weaponset` undo), so the clean puts it back
            game.server(string.format([[
                local h = Osi.GetHostCharacter()
                local e = Ext.Entity.Get(h)
                local was = tostring(e.WeaponSet.WeaponSet)
                if was ~= %q then
                    GK.Stage.Track{ kind = "other", note = "the host's weapon set " .. was .. " -> %s",
                                    op = { op = "weaponset", who = h, set = was, drawn = Osi.IsWeaponUnsheathed(h) } }
                    e.WeaponSet.WeaponSet = %q
                end
                return true ]], c.held, c.held, c.held))
            return uuid(g)
        end)
        step("the host's turn", "observation", function()
            local turn = row(staged, "game_turn", function(p) return p.who ~= nil end, 60)
            need(uuid(turn.payload.who) == host, "the host has the first turn")
        end)
        local asked = step("the game's chance", "observation", function()
            local a = game.server(string.format("return GK.Rolls.Chance{ who = %q, spell = %q, target = %q }", host, c.spell, goblin))
            check(a.ok, "the game answers", a)
            eq(a.was, c.held, "the set in hand before the question")
            local now = game.server(string.format("return tostring(Ext.Entity.Get(%q).WeaponSet.WeaponSet)", host))
            eq(now, c.held, "and after it: the question changed nothing")
            return a.chance
        end)
        local mark = game.mark()
        step("the attack", "cheat", function()
            local cast = game.server(string.format("return GK.Cheat.Cast(%q, %q, %q)", host, c.spell, goblin))
            check(cast and cast.job, "the cast is asked", cast)
        end)
        step("the dice say the same", "observation", function()
            local p = row(mark, "dice_thrown", function(p) return uuid(p.who) == host and string.match(p.kind or "", "Attack$") end, 30).payload
            local faces = math.max(1, math.min(19, 21 - (p.dc - p.bonus)))
            local one = faces / 20
            local want = p.advantage == "advantage" and 1 - (1 - one) ^ 2 or p.advantage == "disadvantage" and one ^ 2 or one
            check(math.abs(asked - want) < 0.0001, "the chance of the dice", { asked = asked, want = want, dc = p.dc, bonus = p.bonus, advantage = p.advantage })
            print(("%s: asked %.4f; the roll had difficulty %d, bonus %d, %s"):format(c.name, asked, p.dc, p.bonus, p.advantage or "no advantage"))
        end)
    end)
end
