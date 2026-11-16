-- A saving throw draws two d20 with neither advantage nor disadvantage: the save's row holds two
-- dice, no advantage, and the first die is the one kept; the second is drawn from the stream and
-- shown nowhere (the hit lists the save's natural die and total only). By bg3.wiki ("Dice rolls",
-- "Advantage") a second d20 is rolled only with advantage or disadvantage.
-- research/studies/rolls.md, section 23. Found in build 4.1.1.7631656; when this fails, the game
-- changed (a tool that rewinds or predicts a stream counts two dice for every save).
-- Staging (cheat): Karmic Dice off for the scope (so that the save is the stream's own dice), the
-- host first, a hostile goblin kept alive, the fight, Sacred Flame taught and cast at the goblin;
-- nothing rigged. Reading: observation.
-- Run: gatekit --instance <copy> test tests -k quirks/saves/second_die_always

test { level = "GustavX:Basic_Level_A", surprises = { "combat_started" },
           about = "A saving throw draws two d20 with neither advantage nor disadvantage." }

local function uuid(id) return id and string.match(id, "(%x+%-%x+%-%x+%-%x+%-%x+)$") or id end

local SPELL = "Target_SacredFlame"

function run(game)
    game.wait_state("Running", "server", 120)
    local host = uuid(game.server("return Osi.GetHostCharacter()"))
    step("the roll log on", "observation", function()
        check(game.server("return GK.RollLog.Available()"), "this Script Extender build throws the roll event")
        game.server("return GK.EventLog.On('dice', { seconds = 600 })")
    end)
    local staged = game.mark()
    local goblin = uuid(step("plain dice, a goblin, the fight, the spell", "cheat", function()
        local g = game.server(string.format([[
            local h = Osi.GetHostCharacter()
            GK.Rolls.SetKarmic(false)
            GK.Rolls.Pin{ kind = "Initiative", n = 100 }
            GK.Stage.Teach(%q, h)
            local x, y, z = Osi.GetPosition(h)
            local g = GK.Stage.Spawn{ template = "goblin", at = { x + 3, y, z }, faction = "enemy" }[1]
            GK.Stage.Immortal(true, g)
            return g
        ]], SPELL))
        game.wait("client", string.format("Ext.Entity.Get(%q) ~= nil", g), 10)
        game.wait("server", string.format("Osi.HasSpell(%q, %q) == 1", host, SPELL), 10)
        game.server(string.format("return GK.Stage.Fight({ %q })", g))
        return g
    end))
    step("the host's turn", "observation", function()
        local turn = row(staged, "game_turn", function(p) return p.who ~= nil end, 60)
        need(uuid(turn.payload.who) == host, "the host has the first turn")
    end)
    local mark = game.mark()
    step("cast " .. SPELL, "cheat", function()
        local c = game.server(string.format("return GK.Cheat.Cast(%q, %q, %q)", host, SPELL, goblin))
        check(c and c.job, "the cast is asked", c)
    end)
    step("two dice, no advantage, the first kept", "observation", function()
        local p = row(mark, "dice_thrown", function(p) return uuid(p.who) == goblin and p.kind == "SavingThrow" end, 30).payload
        eq(p.spell, SPELL, "the save against the spell")
        eq(p.forced, nil, "the dice decided it, not Karmic Dice")
        eq(p.advantage, nil, "neither advantage nor disadvantage")
        eq(#(p.dice or {}), 2, "two d20 drawn")
        eq(p.natural, p.dice[1], "the first is kept")
        eq(p.discarded, p.dice[2], "the second is the discarded one")
        print(("save dice %s, kept %d, total %d against %d"):format(join(p.dice, ","), p.natural, p.total, p.dc))
    end)
end
