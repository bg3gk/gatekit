-- What a creature is about to do is in the log before its die is thrown: the fight log's
-- `fight_action` row is written as the game creates the state of a cast (SpellCastState), some
-- twenty ticks before the cast's first roll, and names the caster, the spell, the targets, the
-- move the caster makes first, and the cast's id - the id the game's hit of the cast carries, so
-- that a reader joins the intent to its outcome (docs/contracts/lua-api.md, section 15; the
-- family `fight`, Server/FightLog.lua). Staged with a hostile goblin a few metres from the host
-- and the host's initiative pinned low, so that the goblin's turn comes first and the goblin walks
-- up and attacks the host by its own AI.
-- The game_hit row carries the hit's own cast id (HitDesc.SpellCastGuid, `cast`).
-- Staging (cheat): the host kept alive, the pin, the goblin, the fight. Reading: observation.
-- Run: gatekit --instance <copy> test tests/fight -k fight/log/intent

test { level = "GustavX:Basic_Level_A", surprises = { "combat_started" },
           about = "A creature's cast is in the fight log, with its target and its id, before the die of the cast is thrown." }

local function uuid(id) return id and string.match(id, "(%x+%-%x+%-%x+%-%x+%-%x+)$") or id end

function run(game)
    game.wait_state("Running", "server", 120)
    local host = uuid(game.server("return Osi.GetHostCharacter()"))
    step("the fight log and the roll log on", "observation", function()
        check(game.server("return GK.RollLog.Available()"), "this Script Extender build throws the roll event")
        game.server("return GK.EventLog.On({ 'fight', 'dice' }, { seconds = 600 })")
    end)
    local staged = game.mark()
    local goblin = step("a goblin five metres off, the goblin first, the fight", "cheat", function()
        local g = game.server([[
            local h = Osi.GetHostCharacter()
            GK.Stage.Immortal(true, h)
            GK.Rolls.Pin{ kind = "Initiative", n = -20 }
            local x, y, z = Osi.GetPosition(h)
            return GK.Stage.Spawn{ template = "goblin", at = { x + 5, y, z }, faction = "enemy" }[1]
        ]])
        game.wait("client", string.format("Ext.Entity.Get(%q) ~= nil", g), 10)
        game.server(string.format("return GK.Stage.Fight({ %q })", g))
        return uuid(g)
    end)
    step("the goblin's turn first", "observation", function()
        local turn = row(staged, "game_turn", function(p) return p.who ~= nil end, 60)
        need(uuid(turn.payload.who) == goblin, "the goblin has the first turn")
    end)
    local action = step("the goblin's cast, before its die", "observation", function()
        local a = row(staged, "fight_action", function(p) return uuid(p.who) == goblin and has(p.targets or {}, host) end, 60)
        local die = row(staged, "dice_thrown", function(p) return uuid(p.who) == goblin and string.match(p.kind or "", "Attack$") end, 30)
        check(a.seq < die.seq, "the intent is written before the attack's die", a, die)
        check(a.payload.spell ~= nil and a.payload.cast ~= nil, "the row names the spell and the cast", a.payload)
        check(a.payload.to ~= nil, "the goblin walks up first: the row says where it casts from", a.payload)
        return a.payload
    end)
    step("the hit carries the cast's id", "observation", function()
        local hit = row(staged, "game_hit", function(p) return uuid(p.by) == goblin and uuid(p.who) == host end, 30)
        eq(hit.payload.cast, action.cast, "the goblin's hit carries the id of the cast the row named")
    end)
end
