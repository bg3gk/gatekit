-- A hit's damage terms add up, dice of a stats expression included: the game_hit row lists the
-- hit's rolls - the weapon's (HitDesc.Damage.DamageRolls) and the dice of the damage's stats
-- expression (the RollParams of the expression Script Extender reads as Damage.ConditionRoll) - and
-- its modifiers, and `unlisted` says, per damage type, how much of what was rolled they leave
-- unexplained (Server/GameLog.lua, docs/contracts/lua-api.md, section 21). Sneak Attack's dice are
-- thrown for an expression (DealDamage(LevelMapValue(SneakAttack)); the roll log names the asker
-- RollDamageExpressionDice) and were believed missing from the hit; this test shows where they are:
-- the hit of the Sneak Attack lists them, from its expression, as the dice the roll log saw thrown,
-- and nothing is unlisted. Staged with a hostile goblin beside the host, the host first and with
-- advantage on its attacks (the boost Advantage(AttackRoll)), its attack die rigged to hit without
-- a critical; on its main-hand attack the game uses the interrupt Sneak Attack by itself
-- (Interrupt_SneakAttack), whose damage is a hit of its own.
-- Staging (cheat): the host kept alive, the pin, the goblin, the boost, the fight; the attack
-- and its die (cheat). Reading: observation.
-- Run: gatekit --instance <copy> test tests/fight -k fight/log/unlisted

test { level = "GustavX:Basic_Level_A", surprises = { "combat_started" },
           about = "A hit whose damage came from dice of a stats expression lists those dice, and nothing is unlisted." }

local function uuid(id) return id and string.match(id, "(%x+%-%x+%-%x+%-%x+%-%x+)$") or id end

function run(game)
    game.wait_state("Running", "server", 120)
    local host = uuid(game.server("return Osi.GetHostCharacter()"))
    step("the fight log and the roll log on", "observation", function()
        check(game.server("return GK.RollLog.Available()"), "this Script Extender build throws the roll event")
        game.server("return GK.EventLog.On({ 'fight', 'dice' }, { seconds = 600 })")
    end)
    local staged = game.mark()
    local goblin = uuid(step("a goblin beside the host, the host first with advantage, the fight", "cheat", function()
        local g = game.server([[
            local h = Osi.GetHostCharacter()
            GK.Stage.Immortal(true, h)
            GK.Rolls.Pin{ kind = "Initiative", n = 100 }
            local boost = "Advantage(AttackRoll)"
            GK.Stage.Track{ kind = "other", note = "the host attacks with advantage", op = { op = "boost", who = h, text = boost, cause = "GKUnlisted" } }
            Osi.AddBoosts(h, boost, "GKUnlisted", h)
            local x, y, z = Osi.GetPosition(h)
            local g = GK.Stage.Spawn{ template = "goblin", at = { x + 1.5, y, z }, faction = "enemy" }[1]
            GK.Stage.Immortal(true, g)
            return g
        ]])
        game.wait("client", string.format("Ext.Entity.Get(%q) ~= nil", g), 10)
        game.server(string.format("return GK.Stage.Fight({ %q })", g))
        return g
    end))
    step("the host's turn", "observation", function()
        local turn = row(staged, "game_turn", function(p) return p.who ~= nil end, 60)
        need(uuid(turn.payload.who) == host, "the host has the first turn")
    end)
    local mark = game.mark()
    step("the host's main-hand attack, its die rigged to hit", "cheat", function()
        local a = game.server(string.format([[
            local h = Osi.GetHostCharacter()
            GK.Rolls.Rig{ who = h, kind = "Attack", natural = 15 }
            return GK.Cheat.Cast(h, "Target_MainHandAttack", %q)
        ]], goblin))
        check(a and a.job, "the attack is asked", a)
    end)
    step("the Sneak Attack's hit lists its dice, and nothing is unlisted", "observation", function()
        local used = row(mark, "fight_reaction", function(p) return uuid(p.who) == host end, 30).payload
        need(used.interrupt == "Interrupt_SneakAttack", "the game used Sneak Attack", used)
        local function sneaky(p)
            for _, r in ipairs(p.rolls or {}) do if string.find(r.from or "", "SneakAttack", 1, true) then return true end end
            return false
        end
        local sneak = row(mark, "game_hit", function(p) return uuid(p.by) == host and sneaky(p) end, 10)
        local thrown = 0
        for _, r in ipairs(game.events(mark, { "dice_thrown" }).rows) do
            if r.seq < sneak.seq and uuid(r.payload.who) == host and r.payload.origin == "unplaced::RollDamageExpressionDice" then
                thrown = thrown + (r.payload.total or 0)
            end
        end
        check(thrown > 0, "the roll log saw the expression's dice thrown")
        local listed = 0
        for _, r in ipairs(sneak.payload.rolls) do
            if string.find(r.from or "", "SneakAttack", 1, true) then listed = listed + r.total end
        end
        eq(listed, thrown, "the hit lists the dice the roll log saw thrown")
        eq(sneak.payload.unlisted, nil, "the listed terms add up to what was rolled")
        local weapon = row(mark, "game_hit", function(p) return uuid(p.by) == host and not sneaky(p) and p.outcome == "hit" end, 5)
        eq(weapon.payload.unlisted, nil, "the weapon's hit adds up too")
    end)
end
