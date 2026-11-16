-- What a creature is about to do is first a list: the game's AI scores every action it could take
-- (Ext.System.ServerAi.Ai[subject].Actions) some ten to twenty ticks before it casts, and takes
-- one. The fight log writes the list as `fight_options` - each candidate with its spell, target,
-- score and the parts of it, its range, costs, the damage the AI expects and the game's own chance
-- of its roll - and the one taken as `fight_choice` (docs/contracts/lua-api.md, "The family fight"; the
-- family `fight`, Server/FightLog.lua). Staged as test:fight/log/intent stages it: a hostile
-- goblin a few metres off with the first turn, acting by its own AI. The chance the AI weighed is
-- held to the dice the attack then throws, as test:rolls/chance/agrees_with_dice holds it.
-- Staging (cheat): the host kept alive, the pin, the goblin, the fight. Reading: observation.
-- Run: gatekit --instance <copy> test tests/fight -k fight/log/options

test { level = "GustavX:Basic_Level_A", surprises = { "combat_started" },
           about = "What a creature weighed before it acted is in the log, the best first, with the one it took and the chance it counted on." }

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
    local seen = step("its options, its choice, its cast", "observation", function()
        local action = row(staged, "fight_action", function(p) return uuid(p.who) == goblin end, 60)
        local rows = game.events(staged, { "fight_options", "fight_choice" }).rows
        local options, choice
        for _, r in ipairs(rows) do
            if uuid(r.payload.who) == goblin and r.seq < action.seq then
                if r.kind == "fight_options" then options = r end
                if r.kind == "fight_choice" then choice = r end
            end
        end
        check(options ~= nil, "a fight_options row of the goblin comes before its fight_action row", action.payload)
        check(choice ~= nil and choice.seq > options.seq, "and the choice after the options", choice)
        check(#options.payload.options > 0, "the goblin weighed something", options.payload)
        return { action = action.payload, options = options.payload.options, choice = choice.payload }
    end)
    step("it took one of its options, the one the choice names", "observation", function()
        local a, c = seen.action, seen.choice
        local target = type(a.targets and a.targets[1]) == "string" and uuid(a.targets[1]) or nil
        eq(c.spell, a.spell, "the choice names the spell cast")
        if target then eq(uuid(c.target), target, "and its target") end
        local taken = c.rank and seen.options[c.rank]
        check(taken ~= nil and taken.spell == a.spell, "the action taken is among the options, at the rank the choice gives", c, seen.options)
        eq(c.options, #seen.options, "the choice counts the options")
        for i = 2, #seen.options do
            check((seen.options[i - 1].score or 0) >= (seen.options[i].score or 0), "the options are sorted by score, the best first", seen.options)
        end
        for _, o in ipairs(seen.options) do
            if o.chance ~= nil then check(o.chance >= 0 and o.chance <= 1, "a chance is between 0 and 1", o) end
        end
        print(("took %s at rank %d of %d (score %.2f, chance %s)"):format(a.spell, c.rank, c.options, c.score or 0, tostring(taken.chance)))
    end)
    step("the chance it weighed is the chance of the dice it threw", "observation", function()
        local taken = seen.options[seen.choice.rank]
        need(taken.chance ~= nil and seen.action.spell and seen.action.spell:match("Attack"), "the goblin attacks with a roll the game gives a chance of", taken)
        local p = row(staged, "dice_thrown", function(p) return uuid(p.who) == goblin and string.match(p.kind or "", "Attack$") end, 30).payload
        local faces = math.max(1, math.min(19, 21 - (p.dc - p.bonus)))
        local one = faces / 20
        local want = p.advantage == "advantage" and 1 - (1 - one) ^ 2 or p.advantage == "disadvantage" and one ^ 2 or one
        check(math.abs(taken.chance - want) < 0.0001, "the chance of the dice",
            { weighed = taken.chance, want = want, dc = p.dc, bonus = p.bonus, advantage = p.advantage })
        print(("weighed %.4f; the roll had difficulty %d, bonus %d, %s"):format(taken.chance, p.dc, p.bonus, p.advantage or "no advantage"))
    end)
end
