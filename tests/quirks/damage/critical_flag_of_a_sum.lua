-- A damage roll of several dice is flagged a critical when the sum of its dice reaches the size
-- of one die: Burning Hands' 3d6 thrown 2+2+2 = 6 is flagged critical success, with no die on its
-- highest face and no critical hit (a save spell has no attack); 1+1+1 = 3 is not. By bg3.wiki
-- ("Dice rolls", "Critical hits") a critical is a natural 20 on the d20 of an attack, and a damage
-- roll is no critical of its own. research/studies/rolls.md, "The second sweep". Found in build
-- 4.1.1.7631656; when this fails, the game changed (a tool that reads a damage roll's critical flag
-- reads this one).
-- The stream's next dice are chosen with GK.Rolls.SetNext (real dice, so that the flag is the
-- game's own and not a rig's); the roll manager takes the new count a tick after it is written,
-- so each cast waits for it.
-- Staging (cheat): the host first, Burning Hands taught and three level 1 spell slots given (the
-- host has none), a hostile goblin two metres off kept alive, the fight; the dice chosen, the
-- casts, the action given back between them. Reading: observation.
-- Run: gatekit --instance <copy> test tests -k quirks/damage/critical_flag_of_a_sum

test { level = "GustavX:Basic_Level_A", surprises = { "combat_started" },
           about = "A damage roll is flagged a critical when the sum of its dice reaches one die's size." }

local function uuid(id) return id and string.match(id, "(%x+%-%x+%-%x+%-%x+%-%x+)$") or id end

local SPELL = "Zone_BurningHands"

function run(game)
    game.wait_state("Running", "server", 120)
    local host = uuid(game.server("return Osi.GetHostCharacter()"))
    step("the roll log on", "observation", function()
        check(game.server("return GK.RollLog.Available()"), "this Script Extender build throws the roll event")
        game.server("return GK.EventLog.On('dice', { seconds = 600 })")
    end)
    local staged = game.mark()
    local goblin = uuid(step("the spell, three slots, a goblin, the fight", "cheat", function()
        local g = game.server(string.format([[
            local h = Osi.GetHostCharacter()
            GK.Rolls.Pin{ kind = "Initiative", n = 100 }
            GK.Stage.Teach(%q, h)
            local boost = "ActionResource(SpellSlot,3,1)"
            GK.Stage.Track{ kind = "other", note = "three level 1 spell slots for the host", op = { op = "boost", who = h, text = boost, cause = "GKQuirk" } }
            Osi.AddBoosts(h, boost, "GKQuirk", h)
            local x, y, z = Osi.GetPosition(h)
            local g = GK.Stage.Spawn{ template = "goblin", at = { x + 2, y, z }, faction = "enemy" }[1]
            GK.Stage.Immortal(true, g)
            return g
        ]], SPELL))
        game.wait("client", string.format("Ext.Entity.Get(%q) ~= nil", g), 10)
        game.wait("server", string.format("Osi.HasSpell(%q, %q) == 1", host, SPELL), 10)
        game.wait("server", string.format([[(function() local ok, n = pcall(function() return Ext.Entity.Get(%q).ActionResources.Resources[GK.Stage.ResourceId("SpellSlot")][1].Amount end) return ok and n and n >= 3 end)()]], host), 10)
        game.server(string.format("return GK.Stage.Fight({ %q })", g))
        return g
    end))
    step("the host's turn", "observation", function()
        local turn = row(staged, "game_turn", function(p) return p.who ~= nil end, 60)
        need(uuid(turn.payload.who) == host, "the host has the first turn")
    end)
    local cast = 0
    local function again()
        if cast > 0 then
            game.wait("server", string.format("Ext.Entity.Get(%q).SpellCastIsCasting == nil", host), 20)
            check(game.server(string.format("return GK.Stage.Refill{ who = %q, action = true }", host)).ok, "the action is given back")
        end
        cast = cast + 1
    end
    local function choose(die)
        return game.server(string.format("return GK.Rolls.SetNext{ type = 'PlayerDamage', die = %d, both = true, dice = 3, faces = 6 }", die))
    end
    step("the player's damage stream", "cheat", function()
        if choose(1).ok then return end
        -- in a game just started the stream is made by the player's first damage roll
        local mark = game.mark()
        again()
        check(game.server(string.format("return GK.Cheat.Cast(%q, %q, %q)", host, SPELL, goblin)).job, "the first cast is asked")
        row(mark, "dice_thrown", function(p) return p.spell == SPELL and string.match(p.kind or "", "Damage$") end, 30)
    end)
    local function burn(die, flagged)
        local mark = game.mark()
        step("Burning Hands, its dice " .. die .. " each", "cheat", function()
            again()
            local n = choose(die)
            need(n.ok, "the player's damage stream exists, so that its next dice can be chosen")
            -- the roll manager takes the count from the component a tick later; a cast before then draws where the stream was
            game.wait("server", string.format("(function() for _, s in ipairs(GK.Rolls.Streams().streams) do if s.type == 'PlayerDamage' then return s.rolls == %d end end end)()", n.rolls), 10)
            local c = game.server(string.format("return GK.Cheat.Cast(%q, %q, %q)", host, SPELL, goblin))
            check(c and c.job, "the cast is asked", c)
        end)
        step("the flag of " .. die .. "+" .. die .. "+" .. die, "observation", function()
            local p = row(mark, "dice_thrown", function(p) return p.spell == SPELL and string.match(p.kind or "", "Damage$") end, 30).payload
            eq(join(p.dice, ","), join({ die, die, die }, ","), "the dice chosen were thrown")
            eq(p.rigged, nil, "no rig")
            local hit = row(mark, "game_hit", function(h) return uuid(h.who) == goblin end, 30).payload
            eq(hit.attack, nil, "no attack roll: no critical hit")
            eq(p.critical, flagged, "the critical flag of the roll")
            print(("3d6 thrown %s = %d: flagged %s"):format(join(p.dice, "+"), p.total, tostring(p.critical)))
        end)
    end
    burn(2, "Success")
    burn(1, "None")
end
