-- Every roll the game's roll manager throws is a `dice_thrown` row of the roll log that says who
-- rolled (`who`), what kind of roll from which stream (`kind`, `stream`, the dice drawn), what code
-- asked for it (`origin`) and why (`cause`) (docs/contracts/lua-api.md, section 17, GK.RollLog).
-- One case for each unlike roll: a story's passive check, a dialogue's check, a status's save as it
-- is applied (on the host) and on its turn (on a staged creature), the player's weapon attack, the
-- player's spell attack and its damage, a target's save against the player's spell, the three
-- attacks of one Scorching Ray, and a hostile creature's attack and its damage. The stream is read
-- from who acts (a player's attack draws from PlayerSpell, a creature's from NPCSpell), the roller
-- from the scene; a d20 roll draws a pair of dice, a damage roll the dice its formula names.
-- Staging (cheat): the stage scope, the roll log's family, the creatures, the fight with the host
-- first, the spells taught and their slots, the pins that make a hit a hit, the statuses, the
-- passive check (the story's RequestPassiveRoll), the casts (GK.Cheat.Cast: Osi.UseSpell rolls
-- nothing), the talk, the turn passed. The weapon attack and the dialogue's answer and throw are
-- the player's own path (fair). Reading the rows: observation.
-- Not covered: initiative (its row has no roller and kind None, by the contract's own words); the
-- cause of a damage roll, which the log does not place yet (test:rolls/log/unknown).
-- A case with a fight stages it anew (about 5 s): each case runs from a clean scene.
-- Run: gatekit --instance <copy> test tests/rolls -k rolls/log/origins

test { level = "GustavX:Basic_Level_A", before = "stage", surprises = { "combat_started", "dialogue", "roll" },
           about = "Every roll the game throws is in the roll log with who rolled, what for and why." }

local function uuid(id) return id and string.match(id, "(%x+%-%x+%-%x+%-%x+%-%x+)$") or id end

local host

function stage(game)
    game.wait_state("Running", "server", 120)
    host = uuid(game.server("return Osi.GetHostCharacter()"))
    step("the roll log on", "observation", function()
        check(game.server("return GK.RollLog.Available()"), "this Script Extender build throws the roll event")
        game.server("return GK.EventLog.On('dice', { seconds = 600 })")
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

--- The row says what `want` says: who, kind (a name, or a Lua pattern), stream, origin, cause; and
--- its dice are as many as the roll draws: the pair of a d20, the count of any other die.
local function says(p, want)
    eq(uuid(p.who), want.who, "who rolled")
    check(p.kind == want.kind or (want.pattern and string.match(p.kind or "", want.pattern)), "the kind of roll", p.kind, want)
    eq(p.stream, want.stream, "the stream")
    eq(p.origin, want.origin, "the asker")
    if want.cause then eq(p.cause, want.cause, "the cause") end
    check(type(p.count) == "number" and p.count >= 1, "the formula names its dice", p)
    eq(#p.dice, (p.dieType == "D20" and 2 or 1) * p.count, "the dice drawn for " .. tostring(p.count) .. " " .. tostring(p.dieType))
end

--- A hostile goblin three metres off, kept alive; the host first; the fight begun on the host's turn.
local function fight(game)
    local staged = game.mark()
    local goblin = step("a hostile goblin, the fight, the host first", "cheat", function()
        check(game.server("return GK.Rolls.Pin{ kind = 'Initiative', n = 100 }").ok, "the host's initiative is pinned")
        local g = game.server([[
            local x, y, z = Osi.GetPosition(Osi.GetHostCharacter())
            local g = GK.Stage.Spawn{ template = "goblin", at = { x + 3, y, z }, faction = "enemy" }[1]
            GK.Stage.Immortal(true, g)
            return g
        ]])
        game.wait("client", string.format("Ext.Entity.Get(%q) ~= nil", g), 10)
        game.server(string.format("return GK.Stage.Fight({ %q })", g))
        return uuid(g)
    end)
    step("the host's turn", "observation", function()
        local turn = row(staged, "game_turn", function(p) return p.who ~= nil end, 60)
        need(uuid(turn.payload.who) == host, "the host has the first turn")
    end)
    return goblin
end

--- Teaches the host a spell and gives it spell slots through the ledger.
local function teach(game, spell)
    step("the host knows " .. spell, "cheat", function()
        game.server(string.format([[
            local h = Osi.GetHostCharacter()
            GK.Stage.Teach(%q, h)
            local text = "ActionResource(SpellSlot,4,1);ActionResource(SpellSlot,4,2)"
            GK.Stage.Track{ kind = "other", note = "spell slots of the host", op = { op = "boost", who = h, text = text, cause = "GKRollLog" } }
            Osi.AddBoosts(h, text, "GKRollLog", h)
            return true
        ]], spell))
    end)
end

local function cast(game, spell, target)
    local mark = game.mark()
    step("cast " .. spell, "cheat", function()
        local c = game.server(string.format("return GK.Cheat.Cast(%q, %q, %q)", host, spell, target))
        check(c and c.job, "the cast is asked", c)
    end)
    return mark
end

case("passive check", function(game)
    local mark = game.mark()
    step("the story asks a Perception check", "cheat", function()
        game.server(string.format([[
            local dc
            for _, id in ipairs(Ext.StaticData.GetAll("DifficultyClass")) do dc = id break end
            Osi.RequestPassiveRoll(%q, %q, "SkillCheck", "Perception", dc, 0, "GKRollLog")
            return true
        ]], host, host))
    end)
    step("the row", "observation", function()
        local p = thrown(mark, function(p) return uuid(p.who) == host end)
        says(p, { who = host, kind = "SkillCheck", stream = "Generic", origin = "unplaced::RollSkillCheck", cause = "request" })
        check(p.request and p.request.fromScript, "the request says a script asked", p.request)
    end)
end)

case("dialogue check", function(game)
    step("a talker whose dialogue has a check", "cheat", function()
        game.server("GKTalker = GK.Fixture.Talker{ dialog = 'roll' } return true")
        game.wait("server", "GK.Fixture.Ready('Talker')", 15)
    end)
    local mark = game.mark()
    step("talk", "cheat", function()
        game.server("return GK.Cheat.Talk(nil, GKTalker.entities[1].id)")
    end)
    step("answer with the check and throw", "fair", function()
        game.wait_attention({ "dialogue", "dialogue_choice" }, 30, mark)
        game.wait("ui", "GK.Gui.DialoguePhase() and GK.Gui.DialoguePhase().phase == 'choice'", 30)
        game.ui("return GK.Answer(1)")
        game.wait_attention({ "roll" }, 30, mark)
        for _ = 1, 10 do
            local r = game.ui("return GK.Gui.Resolve({ roll = 'throw', dialogue = 'last' })")
            if r and r.result then break end
        end
    end)
    step("the row", "observation", function()
        local p = thrown(mark, function(p) return p.cause == "dialogue throw" end)
        says(p, { who = host, kind = "SkillCheck", stream = "Dialog", origin = "unplaced::RollSkillCheck", cause = "dialogue throw" })
    end)
end)

case("status applied", function(game)
    local mark = game.mark()
    step("a status that asks a save as it lands", "cheat", function()
        -- BIBBERBANG_FUMES: OnApplyRoll "not SavingThrow(Ability.Constitution, 15)" in the game's data
        game.server(string.format("return GK.Stage.Status('BIBBERBANG_FUMES', %q, 1)", host))
    end)
    step("the row", "observation", function()
        local p = thrown(mark, function(p) return uuid(p.who) == host and p.kind == "SavingThrow" end)
        says(p, { who = host, kind = "SavingThrow", stream = "Generic", origin = "unplaced::RollSavingThrow", cause = "status applied" })
    end)
end)

case("status on its turn", function(game)
    local other = step("a creature in Hold Person", "cheat", function()
        -- HOLD_PERSON: RemoveConditions "SavingThrow(Ability.Wisdom, SourceSpellDC())" on its turn
        local g = game.server(string.format("return GK.Stage.Spawn{ template = GK.Fixture.Defaults.enemy, faction = 'neutral', near = %q, distance = 4 }[1]", host))
        game.wait("server", string.format("GK.Stage.Ready({ %q })", g), 15)
        return uuid(g)
    end)
    local mark = game.mark()
    step("the status", "cheat", function()
        game.server(string.format("return GK.Stage.Status('HOLD_PERSON', %q, 5)", other))
    end)
    step("the row", "observation", function()
        local p = thrown(mark, function(p) return uuid(p.who) == other end, 30)
        says(p, { who = other, kind = "SavingThrow", stream = "Generic", origin = "unplaced::RollSavingThrow", cause = "status event" })
    end)
end)

case("weapon attack", function(game)
    local goblin = fight(game)
    local mark = game.mark()
    step("attack the goblin", "fair", function()
        check(game.ui(string.format("return GK.Act.Attack(%q)", goblin), 120).ok, "the game finished the attack")
    end)
    step("the row", "observation", function()
        local p = thrown(mark, function(p) return uuid(p.who) == host and string.match(p.kind or "", "Attack$") end)
        says(p, { who = host, pattern = "WeaponAttack$", stream = stream_of(game, host, "Spell"),
                  origin = "unplaced::RollAttackAgainstArmourClass", cause = "spell" })
    end)
end)

case("spell attack and damage", function(game)
    local goblin = fight(game)
    teach(game, "Projectile_FireBolt")
    step("the attack hits", "cheat", function()
        check(game.server(string.format("return GK.Rolls.Pin{ who = %q, kind = 'RangedSpellAttack', n = 19 }", host)).ok, "the attack die is pinned")
    end)
    local mark = cast(game, "Projectile_FireBolt", goblin)
    step("the rows", "observation", function()
        local attack = thrown(mark, function(p) return uuid(p.who) == host and p.kind == "RangedSpellAttack" end)
        says(attack, { who = host, kind = "RangedSpellAttack", stream = stream_of(game, host, "Spell"),
                       origin = "unplaced::RollSpellAttackAgainstArmourClass", cause = "spell" })
        local damage = thrown(mark, function(p) return uuid(p.who) == host and p.kind == "RangedSpellDamage" end)
        says(damage, { who = host, kind = "RangedSpellDamage", stream = stream_of(game, host, "Damage"),
                       origin = "unplaced::RollDamageExpressionDice" })
    end)
end)

case("target's save", function(game)
    local goblin = fight(game)
    teach(game, "Target_SacredFlame")
    local mark = cast(game, "Target_SacredFlame", goblin)
    step("the row", "observation", function()
        local p = thrown(mark, function(p) return uuid(p.who) == goblin end)
        -- the target rolls, from the stream of the one who cast
        says(p, { who = goblin, kind = "SavingThrow", stream = stream_of(game, host, "Spell"),
                  origin = "unplaced::RollSavingThrow", cause = "spell" })
    end)
end)

case("three rays", function(game)
    local goblin = fight(game)
    teach(game, "Projectile_ScorchingRay")
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
                      origin = "unplaced::RollSpellAttackAgainstArmourClass", cause = "spell" })
        end
    end)
end)

case("creature's attack", function(game)
    local goblin = fight(game)
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
                       origin = "unplaced::RollAttackAgainstArmourClass", cause = "spell" })
        local damage = thrown(mark, function(p) return uuid(p.who) == goblin and string.match(p.kind or "", "Damage$") end, 30)
        says(damage, { who = goblin, pattern = "WeaponDamage$", stream = stream_of(game, goblin, "Damage"),
                       origin = "unplaced::RollDamageDice" })
    end)
end)
