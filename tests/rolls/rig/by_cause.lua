-- A rig can name a roll by what the roll is for: the spell it belongs to. Two spells that each
-- force a save on the same creature are cast one after the other; the rig names the second
-- spell's save only, and is set before either is cast. The first save is the game's own, the
-- second is the rigged die (docs/contracts/lua-api.md, section 17, GK.Rolls.Rig: `spell`). A rig
-- that named only the roller and the kind would have taken the first.
-- Staging (cheat): the goblin, the fight, the spells taught, the rig, the casts. Reading: observation.
-- Run: gatekit --instance <copy> test tests/rolls -k rolls/rig/by_cause

test { level = "GustavX:Basic_Level_A", surprises = { "combat_started" },
           about = "A rig that names a spell decides the save against that spell and no other." }

local function uuid(id) return id and string.match(id, "(%x+%-%x+%-%x+%-%x+%-%x+)$") or id end

local FIRST, SECOND = "Target_SacredFlame", "Target_ViciousMockery"

function run(game)
    game.wait_state("Running", "server", 120)
    local host = uuid(game.server("return Osi.GetHostCharacter()"))
    step("the roll log on", "observation", function()
        check(game.server("return GK.Rolls.CanRig()"), "this Script Extender build raises the roll event")
        game.server("return GK.EventLog.On('dice', { seconds = 600 })")
    end)
    local staged = game.mark()
    local goblin = step("a goblin, the fight, two spells that force a save", "cheat", function()
        local g = game.server(string.format([[
            local h = Osi.GetHostCharacter()
            GK.Rolls.Pin{ kind = "Initiative", n = 100 }
            GK.Stage.Teach(%q, h)
            GK.Stage.Teach(%q, h)
            local x, y, z = Osi.GetPosition(h)
            local g = GK.Stage.Spawn{ template = "goblin", at = { x + 3, y, z }, faction = "enemy" }[1]
            GK.Stage.Immortal(true, g)
            return g
        ]], FIRST, SECOND))
        game.wait("client", string.format("Ext.Entity.Get(%q) ~= nil", g), 10)
        game.server(string.format("return GK.Stage.Fight({ %q })", g))
        return uuid(g)
    end)
    step("the host's turn", "observation", function()
        local turn = row(staged, "game_turn", function(p) return p.who ~= nil end, 60)
        need(uuid(turn.payload.who) == host, "the host has the first turn")
    end)
    step("the rig names the second spell", "cheat", function()
        local r = game.server(string.format("return GK.Rolls.Rig{ who = %q, kind = 'SavingThrow', spell = %q, natural = 20 }", goblin, SECOND))
        check(r.ok, "the rig is set", r)
    end)
    local function save_of(spell)
        local mark = game.mark()
        step("cast " .. spell, "cheat", function()
            local c = game.server(string.format("return GK.Cheat.Cast(%q, %q, %q)", host, spell, goblin))
            check(c and c.job, "the cast is asked", c)
        end)
        return step("the save against " .. spell, "observation", function()
            local p = row(mark, "dice_thrown", function(p) return uuid(p.who) == goblin and p.kind == "SavingThrow" end, 30).payload
            eq(p.spell, spell, "the save says which spell it is against")
            return p
        end)
    end
    local first = save_of(FIRST)
    step("the first save is the game's own", "observation", function()
        eq(first.rigged, nil, "no rig decided it")
        local rigs = game.server("return GK.Rolls.Rigs()")
        eq(#rigs, 1, "the rig is still waiting")
        eq(rigs[1].decided, 0, "and has decided nothing")
    end)
    step("the host has its action again", "cheat", function()
        -- the first cast spends the action as it ends: refilled before then, the action is spent again
        game.wait("server", string.format("Ext.Entity.Get(%q).SpellCastIsCasting == nil", host), 20)
        check(game.server(string.format("return GK.Stage.Refill{ who = %q, action = true }", host)).ok, "the action is given back")
    end)
    local second = save_of(SECOND)
    step("the second save is the rig's", "observation", function()
        eq(second.natural, 20, "the rigged die")
        check(second.rigged ~= nil, "the row says a rig decided it", second)
        eq(#game.server("return GK.Rolls.Rigs()"), 0, "the rig is spent")
    end)
end
