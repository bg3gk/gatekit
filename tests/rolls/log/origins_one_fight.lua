-- The five fight cases of test:rolls/log/origins in ONE staged fight: the player's weapon attack,
-- the player's spell attack and its damage, a target's save against the player's spell, the three
-- attacks of one Scorching Ray, and a hostile creature's attack and its damage - each a row of the
-- roll log (`dice_thrown`) that says who rolled, what kind from which stream, what code asked and
-- why, and what the roll is for (docs/contracts/lua-api.md, section 17, GK.RollLog). The rules and
-- the expectations are those of test:rolls/log/origins; this file is the cumulative form of them,
-- an experiment in time: the fight is staged once and the cases run one after another in it.
-- How: `reset = "none"`, so the ladder cleans nothing between the cases (it only opens the stage
-- scope again); `before` stages the fight once - the first case to run does it, so that any case
-- can still run alone with -k - and before every later case checks that the fight goes on as the
-- case needs (the goblin alive and in the fight, the host's turn) and gives the host its turn's
-- resources back (GK.Stage.Refill) instead of a new scene. A case that leaves the fight otherwise
-- makes the cases after it unmet, naming what is wrong. The hostile creature's attack needs the
-- goblin's turn, so its case is the last: it passes the host's turn.
-- Staging (cheat): the roll log's family, a hostile goblin kept alive, the three spells taught and
-- their slots, the host first, the fight, the pins, the casts (GK.Cheat.Cast), the turn passed,
-- the resources given back. The weapon attack is the player's own path (fair). Reading: observation.
-- A limit of its own: about 25 s at speed 1 (one fight staged, five acts in it, one enemy turn).
-- reset = "none" also means the world is not cleaned before the test: after a test that leaves a
-- fight, `stage` finds the host in it and the test is unmet, saying so.
-- Run: gatekit --instance <copy> test tests -k rolls/log/origins_one_fight

test { level = "GustavX:Basic_Level_A", before = "stage", reset = "none", surprises = { "combat_started", "roll" },
           about = "The fight's rolls are in the roll log with who rolled, what for and why, read in one fight." }

local function uuid(id) return id and string.match(id, "(%x+%-%x+%-%x+%-%x+%-%x+)$") or id end

local host, goblin
local SPELLS = { "Projectile_FireBolt", "Target_SacredFlame", "Projectile_ScorchingRay" }

--- The fight, staged by the first case to run; for every later case, checked and the host refilled.
function stage(game)
    if goblin then
        step("the fight goes on, the host's turn, its resources back", "cheat", function()
            local s = game.server(string.format([[
                local c = GK.Stage.Combat()
                return { active = c and c.active, goblin = Osi.IsDead(%q) ~= 1 and Osi.IsInCombat(%q) == 1 }
            ]], goblin, goblin))
            need(s.goblin, "the goblin is alive and in the fight (a case before left it otherwise)")
            need(uuid(s.active) == host, "the host's turn goes on (a case before passed it): " .. tostring(s.active))
            check(game.server("return GK.Stage.Refill{}").ok, "the host's action, bonus action, movement and reaction are back")
            game.server("return GK.EventLog.On('dice', { seconds = 600 })")
        end)
        return
    end
    game.wait_state("Running", "server", 120)
    host = uuid(game.server("return Osi.GetHostCharacter()"))
    step("the roll log on, no fight left from before", "observation", function()
        check(game.server("return GK.RollLog.Available()"), "this Script Extender build throws the roll event")
        need(game.server("return Osi.IsInCombat(Osi.GetHostCharacter())") ~= 1,
            "the host is in no fight: reset = 'none' cleans nothing before this test")
        game.server("return GK.EventLog.On('dice', { seconds = 600 })")
    end)
    local staged = game.mark()
    goblin = step("a hostile goblin, the spells and slots, the fight, the host first", "cheat", function()
        check(game.server("return GK.Rolls.Pin{ kind = 'Initiative', n = 100 }").ok, "the host's initiative is pinned")
        local g = game.server(string.format([[
            local h = Osi.GetHostCharacter()
            for _, spell in ipairs({ %q, %q, %q }) do GK.Stage.Teach(spell, h) end
            local text = "ActionResource(SpellSlot,4,1);ActionResource(SpellSlot,4,2)"
            GK.Stage.Track{ kind = "other", note = "spell slots of the host", op = { op = "boost", who = h, text = text, cause = "GKRollLog" } }
            Osi.AddBoosts(h, text, "GKRollLog", h)
            local x, y, z = Osi.GetPosition(h)
            local g = GK.Stage.Spawn{ template = "goblin", at = { x + 3, y, z }, faction = "enemy" }[1]
            GK.Stage.Immortal(true, g)
            return g
        ]], SPELLS[1], SPELLS[2], SPELLS[3]))
        game.wait("client", string.format("Ext.Entity.Get(%q) ~= nil", g), 10)
        game.server(string.format("return GK.Stage.Fight({ %q })", g))
        return uuid(g)
    end)
    step("the host's turn", "observation", function()
        local turn = row(staged, "game_turn", function(p) return p.who ~= nil end, 60)
        need(uuid(turn.payload.who) == host, "the host has the first turn")
    end)
end

--- The stream a roll of `actor` draws from: Player... for a player's character, NPC... otherwise.
local function stream_of(game, actor, what)
    local player = game.server(string.format("return Osi.IsPlayer(%q)", actor)) == 1
    return (player and "Player" or "NPC") .. what
end

--- The first dice_thrown row after `mark` that `match` accepts.
local function thrown(mark, match, seconds)
    return row(mark, "dice_thrown", function(p) return match(p) end, seconds or 30).payload
end

--- The row says what `want` says (test:rolls/log/origins, the same function).
local function says(p, want)
    eq(uuid(p.who), want.who, "who rolled")
    check(p.kind == want.kind or (want.pattern and string.match(p.kind or "", want.pattern)), "the kind of roll", p.kind, want)
    eq(p.stream, want.stream, "the stream")
    eq(p.origin, want.origin, "the asker")
    if want.cause then eq(p.cause, want.cause, "the cause") end
    if want.source then eq(uuid(p.source), want.source, "the first party: who cast, attacked or caused") end
    if want.target then eq(uuid(p.target), want.target, "the second party: the target or the bearer") end
    if want.spell then eq(p.spell, want.spell, "the spell") end
    if want.status then eq(p.status, want.status, "the status") end
    check(type(p.count) == "number" and p.count >= 1, "the formula names its dice", p)
    eq(#p.dice, (p.dieType == "D20" and 2 or 1) * p.count, "the dice drawn for " .. tostring(p.count) .. " " .. tostring(p.dieType))
end

local function cast(game, spell, target)
    local mark = game.mark()
    step("cast " .. spell, "cheat", function()
        local c = game.server(string.format("return GK.Cheat.Cast(%q, %q, %q)", host, spell, target))
        check(c and c.job, "the cast is asked", c)
    end)
    return mark
end

case("weapon attack", function(game)
    local mark = game.mark()
    step("attack the goblin", "fair", function()
        check(game.ui(string.format("return GK.Act.Attack(%q)", goblin), 120).ok, "the game finished the attack")
    end)
    step("the row", "observation", function()
        local p = thrown(mark, function(p) return uuid(p.who) == host and string.match(p.kind or "", "Attack$") end)
        says(p, { who = host, pattern = "WeaponAttack$", stream = stream_of(game, host, "Spell"),
                  origin = "unplaced::RollAttackAgainstArmourClass", cause = "spell", source = host, target = goblin })
    end)
end)

case("spell attack and damage", function(game)
    step("the attack hits", "cheat", function()
        check(game.server(string.format("return GK.Rolls.Pin{ who = %q, kind = 'RangedSpellAttack', n = 19 }", host)).ok, "the attack die is pinned")
    end)
    local mark = cast(game, "Projectile_FireBolt", goblin)
    step("the rows", "observation", function()
        local attack = thrown(mark, function(p) return uuid(p.who) == host and p.kind == "RangedSpellAttack" end)
        says(attack, { who = host, kind = "RangedSpellAttack", stream = stream_of(game, host, "Spell"),
                       origin = "unplaced::RollSpellAttackAgainstArmourClass", cause = "spell",
                       source = host, target = goblin, spell = "Projectile_FireBolt" })
        local damage = thrown(mark, function(p) return uuid(p.who) == host and p.kind == "RangedSpellDamage" end)
        says(damage, { who = host, kind = "RangedSpellDamage", stream = stream_of(game, host, "Damage"),
                       origin = "unplaced::RollDamageExpressionDice", source = host, spell = "Projectile_FireBolt" })
    end)
end)

case("target's save", function(game)
    local mark = cast(game, "Target_SacredFlame", goblin)
    step("the row", "observation", function()
        local p = thrown(mark, function(p) return uuid(p.who) == goblin end)
        -- the target rolls, from the stream of the one who cast
        says(p, { who = goblin, kind = "SavingThrow", stream = stream_of(game, host, "Spell"),
                  origin = "unplaced::RollSavingThrow", cause = "spell",
                  source = host, target = goblin, spell = "Target_SacredFlame" })
    end)
end)

case("three rays", function(game)
    local mark = cast(game, "Projectile_ScorchingRay", goblin)
    step("the rows", "observation", function()
        local function ray(p) return uuid(p.who) == host and p.kind == "RangedSpellAttack" end
        local n = 0
        row(mark, "dice_thrown", function(p) if ray(p) then n = n + 1 end return n == 3 end, 30)
        local rays = {}
        for _, r in ipairs(game.events(mark, { "dice_thrown" }).rows or {}) do
            if ray(r.payload) then rays[#rays + 1] = r.payload end
        end
        eq(#rays, 3, "attack rows of one cast")
        local spell = stream_of(game, host, "Spell")
        for _, p in ipairs(rays) do
            says(p, { who = host, kind = "RangedSpellAttack", stream = spell,
                      origin = "unplaced::RollSpellAttackAgainstArmourClass", cause = "spell",
                      source = host, target = goblin, spell = "Projectile_ScorchingRay" })
        end
    end)
end)

-- the last: it passes the host's turn, which no case after it could stand
case("creature's attack", function(game)
    step("the goblin's attack hits", "cheat", function()
        check(game.server(string.format("return GK.Rolls.Pin{ who = %q, kind = 'MeleeWeaponAttack', n = 19 }", goblin)).ok, "the goblin's attack die is pinned")
    end)
    local mark = game.mark()
    step("the host's turn passes", "cheat", function()
        game.server(string.format("return GK.Stage.EndTurn(%q)", host))
    end)
    step("the rows", "observation", function()
        local attack = thrown(mark, function(p) return uuid(p.who) == goblin and string.match(p.kind or "", "Attack$") end, 60)
        says(attack, { who = goblin, pattern = "WeaponAttack$", stream = stream_of(game, goblin, "Spell"),
                       origin = "unplaced::RollAttackAgainstArmourClass", cause = "spell", source = goblin, target = host })
        local damage = thrown(mark, function(p) return uuid(p.who) == goblin and string.match(p.kind or "", "Damage$") end, 30)
        says(damage, { who = goblin, pattern = "WeaponDamage$", stream = stream_of(game, goblin, "Damage"),
                       origin = "unplaced::RollDamageDice" })
    end)
end)
