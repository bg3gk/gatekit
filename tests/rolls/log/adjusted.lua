-- An interrupt may change a roll after it is thrown: the game's own Portent, Shield, Lucky and
-- Bardic Inspiration do (wiki: Reactions; research/studies/rolls.md, section 18). The roll log
-- then holds two rows: `dice_thrown` says what was thrown, `dice_adjusted` what the game goes on
-- with, naming what it replaced and the interrupts listed as used on it, each with its owner,
-- what it asked and whether it is the change seen (`by`; docs/contracts/lua-api.md, sections 17
-- and 21, GK.RollLog). Staged with
-- an interrupt of the game's own kind that sets a roll to 1 (Illusory Self's `SetRoll`), made to
-- fire by itself on the host's own attack; the host's die is rigged to 15 so that the change is
-- one the test can name: 15 thrown, 1 kept, a hit turned into a critical miss.
-- Staging (cheat): the interrupt and its unlocking, the goblin, the fight, the rig. The attack is
-- the player's own (fair). Reading: observation.
-- Run: gatekit --instance <copy> test tests/rolls -k rolls/log/adjusted

test { level = "GustavX:Basic_Level_A", surprises = { "combat_started" },
           about = "A roll an interrupt changes after the throw is in the roll log twice: as thrown, and as the game goes on with it." }

local function uuid(id) return id and string.match(id, "(%x+%-%x+%-%x+%-%x+%-%x+)$") or id end

function run(game)
    game.wait_state("Running", "server", 120)
    local host = uuid(game.server("return Osi.GetHostCharacter()"))
    step("the roll log on", "observation", function()
        check(game.server("return GK.RollLog.Available()"), "this Script Extender build throws the roll event")
        need(game.server("return GK.Verbs.Has('Events.RollAdjusted')"),
            "this Script Extender build says when a roll is changed after the throw")
        game.server("return GK.EventLog.On('dice', { seconds = 600 })")
    end)
    local staged = game.mark()
    local goblin = step("an interrupt that sets the host's attack roll to 1, a goblin, the fight", "cheat", function()
        local g = game.server([[
            local h = Osi.GetHostCharacter()
            local name = "GK_RollLog_SetRoll1"
            local s = Ext.Stats.Get(name) or Ext.Stats.Create(name, "InterruptData", "Interrupt_IllusorySelf")
            s.InterruptContext = { "OnPostRoll" }
            s.Conditions = "IsAbleToReact(context.Observer) and Self(context.Source,context.Observer) and IsAttack() and not IsCritical()"
            s.EnableCondition = ""
            s.EnableContext = {}
            s.Cost = ""
            s.InterruptDefaultValue = { "Enabled" }
            Ext.Stats.Sync(name)
            local text = "UnlockInterrupt(" .. name .. ")"
            GK.Stage.Track{ kind = "other", note = "the interrupt unlocked for the host", op = { op = "boost", who = h, text = text, cause = "GKRollLog" } }
            Osi.AddBoosts(h, text, "GKRollLog", h)
            GK.Rolls.Pin{ kind = "Initiative", n = 100 }
            local x, y, z = Osi.GetPosition(h)
            local g = GK.Stage.Spawn{ template = "goblin", at = { x + 2, y, z }, faction = "enemy" }[1]
            GK.Stage.Immortal(true, g)
            return g
        ]])
        game.wait("client", string.format("Ext.Entity.Get(%q) ~= nil", g), 10)
        -- the unlocking is a boost: the interrupt is the host's some ticks later, and an attack before then is not interrupted
        game.wait("server", [[(function() for _, i in pairs(Ext.Entity.Get(Osi.GetHostCharacter()).InterruptContainer.Interrupts) do local ok, name = pcall(function() return i.InterruptData.Interrupt end) if ok and tostring(name) == "GK_RollLog_SetRoll1" then return true end end return false end)()]], 10)
        game.server(string.format("return GK.Stage.Fight({ %q })", g))
        check(game.server(string.format("return GK.Rolls.Rig{ who = %q, kind = 'Attack', natural = 15 }", host)).ok, "the host's attack die is rigged to 15")
        return uuid(g)
    end)
    step("the host's turn", "observation", function()
        local turn = row(staged, "game_turn", function(p) return p.who ~= nil end, 60)
        need(uuid(turn.payload.who) == host, "the host has the first turn")
    end)
    step("the host can react", "cheat", function()
        -- an interrupt needs its owner able to react: a reaction spent in an earlier fight of the same game
        -- is not given back by the clean between tests
        check(game.server(string.format("return GK.Stage.Refill{ who = %q, reaction = true }", host)).ok, "the reaction is given back")
    end)
    local mark = game.mark()
    step("attack the goblin", "fair", function()
        check(game.ui(string.format("return GK.Act.Attack(%q)", goblin), 120).ok, "the game finished the attack")
    end)
    step("thrown 15, kept 1", "observation", function()
        local thrown = row(mark, "dice_thrown", function(p) return uuid(p.who) == host and string.match(p.kind or "", "Attack$") end, 30).payload
        eq(thrown.natural, 15, "the row of the throw: the die the rig gave")
        local kept = row(mark, "dice_adjusted", nil, 30).payload
        eq(kept.was.natural, thrown.natural, "the change names the die that was thrown")
        eq(kept.was.total, thrown.total, "and its total")
        eq(kept.natural, 1, "the die the game goes on with")
        eq(kept.total, thrown.total - 14, "the total moved with the die")
        eq(kept.critical, "Fail", "a natural 1 is a critical miss")
        eq(kept.dc, thrown.dc, "the same difficulty: the goblin's Armour Class")
        check(kept.outcomeChanged == (thrown.total >= thrown.dc), "the outcome changed exactly when 15 had hit", kept, thrown)
    end)
    step("the change names who made it", "observation", function()
        local kept = row(mark, "dice_adjusted", nil, 30).payload
        local mine
        for _, one in ipairs(kept.by or {}) do
            if one.interrupt == "GK_RollLog_SetRoll1" then mine = one end
        end
        check(mine ~= nil, "the row names the interrupt that set the die", kept)
        eq(uuid(mine.owner), host, "its owner: the host")
        eq(mine.set, 1, "what it asked: the die set to 1")
        check(mine.applied == true, "and its own adjustment is the change seen", mine)
        eq(uuid(kept.source), host, "the roller: the host")
        eq(uuid(kept.target), goblin, "the other party: the goblin")
    end)
end
