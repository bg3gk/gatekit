-- A hit forced by Karmic Dice shows a natural die that was never thrown: once misses have built
-- the player's stream a success debt, Karmic Dice decides the next attack a hit before the d20 is
-- thrown - its roll row is `forced`, with no dice - and the hit still names a natural die and a
-- total that add up, as if a d20 had been thrown. The stream moves three draws for it where a
-- thrown d20 moves two: the hidden roll of the wiki and the choice of the die are drawn from it.
-- bg3.wiki, "Dice rolls", "Karmic Debt": a hidden roll decides an automatic success, and the game
-- then chooses a d20 value among those that would have succeeded - the choice is the wiki's too;
-- what neither the hit nor the combat log says is that the die shown was chosen and not thrown,
-- so a reader of the hit takes it for a throw. research/studies/rolls.md, section 20. Found in
-- build 4.1.1.7631656; when this fails, the game changed. The attack is repeated, at most four
-- times (and once more, unchosen, in a game just started, whose stream does not exist yet), because the rule is about what a run of misses does: the misses are real dice (the
-- stream's next die chosen, GK.Rolls.SetNext), so that the debt the game keeps is its own; a rig
-- decides the outcome after the debt is counted from the die first thrown.
-- Staging (cheat): the host first, Karmic Dice on for the scope, a hostile goblin beside it kept
-- alive, the fight; the attacks, the action given back between them. Reading: observation.
-- Run: gatekit --instance <copy> test tests -k quirks/karmic/forced_shows_a_die

test { level = "GustavX:Basic_Level_A", surprises = { "combat_started" },
           about = "A hit forced by Karmic Dice shows a natural die that was never thrown." }

local function uuid(id) return id and string.match(id, "(%x+%-%x+%-%x+%-%x+%-%x+)$") or id end

local MISS = 2   -- with the dagger's +5 against a goblin's 13, a 2 misses and is no critical

function run(game)
    game.wait_state("Running", "server", 120)
    local host = uuid(game.server("return Osi.GetHostCharacter()"))
    step("the roll log on", "observation", function()
        check(game.server("return GK.RollLog.Available()"), "this Script Extender build throws the roll event")
        game.server("return GK.EventLog.On('dice', { seconds = 600 })")
    end)
    local staged = game.mark()
    local goblin = uuid(step("Karmic Dice on, a goblin beside the host, the fight", "cheat", function()
        local g = game.server([[
            local h = Osi.GetHostCharacter()
            GK.Rolls.Pin{ kind = "Initiative", n = 100 }
            local k = GK.Rolls.SetKarmic(true)
            if not (k.ok and k.on) then return { error = k } end
            local x, y, z = Osi.GetPosition(h)
            local g = GK.Stage.Spawn{ template = "goblin", at = { x + 1.5, y, z }, faction = "enemy" }[1]
            GK.Stage.Immortal(true, g)
            return g
        ]])
        check(type(g) == "string", "Karmic Dice is on", g)
        game.wait("client", string.format("Ext.Entity.Get(%q) ~= nil", g), 10)
        game.server(string.format("return GK.Stage.Fight({ %q })", g))
        return g
    end))
    step("the host's turn", "observation", function()
        local turn = row(staged, "game_turn", function(p) return p.who ~= nil end, 60)
        need(uuid(turn.payload.who) == host, "the host has the first turn")
    end)
    local function stream()
        for _, s in ipairs(game.server("return GK.Rolls.Streams().streams")) do if s.type == "PlayerSpell" then return s end end
        return {}
    end
    local forced, hit, debts, drawn = nil, nil, {}, nil
    for attempt = 1, 5 do
        local mark = game.mark()
        local before, warm
        step("attack " .. attempt .. ", the stream's next die a miss", "cheat", function()
            if attempt > 1 then
                game.wait("server", string.format("Ext.Entity.Get(%q).SpellCastIsCasting == nil", host), 20)
                check(game.server(string.format("return GK.Stage.Refill{ who = %q, action = true }", host)).ok, "the action is given back")
            end
            local next = game.server(string.format("return GK.Rolls.SetNext{ type = 'PlayerSpell', die = %d }", MISS))
            -- in a game just started the stream is made by the player's first roll: that attack
            -- goes unchosen (a hit leaves a debt of 0 as it is, a miss adds to it)
            warm = not next.ok and attempt == 1
            need(next.ok or warm, "the player's spell stream exists, so that its next die can be chosen")
            before = next.rolls or 0
            -- the roll manager takes the count from the component a tick later; a cast before then draws where the stream was
            if not warm then
                game.wait("server", string.format("(function() for _, s in ipairs(GK.Rolls.Streams().streams) do if s.type == 'PlayerSpell' then return s.rolls == %d end end end)()", before), 10)
            end
            local c = game.server(string.format("return GK.Cheat.Cast(%q, 'Target_MainHandAttack', %q)", host, goblin))
            check(c and c.job, "the cast is asked", c)
        end)
        local done = step("attack " .. attempt .. ", read", "observation", function()
            local p = row(mark, "dice_thrown", function(p) return uuid(p.who) == host and string.match(p.kind or "", "Attack$") end, 30).payload
            local h = row(mark, "game_hit", function(p) return uuid(p.who) == goblin and uuid(p.by) == host end, 30).payload
            local s = stream()
            debts[#debts + 1] = ("%s %s, debt %.2f, dice drawn %d"):format(p.forced and "forced" or "thrown " .. tostring(p.natural), h.outcome, s.successDebt or 0, (s.rolls or 0) - before)
            if warm then
                debts[#debts] = "unchosen " .. debts[#debts]
                return false
            end
            if p.forced then
                forced, hit, drawn = p, h, (s.rolls or 0) - before
                local n = game.server(string.format("return GK.Rolls.Next{ type = 'PlayerSpell', rolls = %d, dice = 3 }", before))
                print("the stream's three draws, as d20s: " .. join(n.dice, ","))
                return true
            end
            eq(p.natural, MISS, "the die chosen was thrown")
            eq(h.outcome, "miss", "and missed")
            return false
        end)
        if done then break end
    end
    step("the forced hit names a die no one threw", "observation", function()
        print("attacks: " .. join(debts, "; "))
        need(forced ~= nil, "Karmic Dice forced a hit within four chosen misses")
        local a = hit.attack or {}
        print(("the forced row: natural %s, total %s, dc %s, bonus %s; the hit: %s, natural %s, total %s, against %s"):format(tostring(forced.natural),
            tostring(forced.total), tostring(forced.dc), tostring(forced.bonus), tostring(hit.outcome), tostring(a.natural), tostring(a.total), tostring(a.ac)))
        eq(#(forced.dice or {}), 0, "the forced roll drew no dice")
        eq(drawn, 3, "while the stream moved three draws, not a d20's pair")
        eq(hit.outcome, "hit", "the forced roll is a hit")
        check(type(a.natural) == "number" and a.natural >= 1 and a.natural <= 20, "the hit names a natural die", a)
        eq(a.total, a.natural + forced.bonus, "and a total of that die and the bonus")
        check(a.total >= a.ac, "a total that hits", a)
        print(("forced: no dice drawn; the hit shows natural %d, total %d against %d"):format(a.natural, a.total, a.ac))
    end)
end
