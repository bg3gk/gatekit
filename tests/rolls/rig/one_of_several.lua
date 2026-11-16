-- A rig decides the throw it names at the moment it is made and leaves every other throw to the
-- game (docs/contracts/lua-api.md, "GK.Rolls", GK.Rolls.Rig). Two ways of naming, a case each:
--   * nth of four: of four checks asked in one tick with the third rigged, the third gets the
--     rigged die and the other three are the stream's own. To know what "the stream's own" is, the
--     four are asked twice from the same place of the stream: once free, read from the roll log,
--     then with the streams put back (GK.Rolls.SetStreams of the reading taken before, so the same
--     dice come again) and the third rigged to another die. The rule is about what changes between
--     the two runs, so the checks are asked twice (docs/writing-tests.md, "Once is enough").
--   * by spell: a rig can name a roll by what the roll is for. Two spells that each force a save
--     on the same creature are cast one after the other; the rig names the second spell's save
--     only, and is set before either is cast. The first save is the game's own, the second is the
--     rigged die. A rig that named only the roller and the kind would have taken the first.
-- Staging (cheat): the stage scope, Karmic Dice off (a forced outcome is not the stream's), the
-- streams, the rigs, the passive checks (the story's RequestPassiveRoll), the goblin, the fight,
-- the spells taught, the casts. Reading: the roll log's rows (what was thrown), the rig's row
-- (what it replaced) and the game's own roll rows (what the game used): observation.
-- Run: gatekit --instance <copy> test tests/rolls -k rolls/rig/one_of_several

test { level = "GustavX:Basic_Level_A", before = "stage", surprises = { "combat_started" },
           about = "A rig decides the throw it names - by its place among several, or by the spell it is for - and no other." }

local function uuid(id) return id and string.match(id, "(%x+%-%x+%-%x+%-%x+%-%x+)$") or id end

local host

function stage(game)
    game.wait_state("Running", "server", 120)
    host = uuid(game.server("return Osi.GetHostCharacter()"))
    step("the roll log on", "observation", function()
        check(game.server("return GK.Rolls.CanRig()"), "this Script Extender build raises the roll event")
        game.server("return GK.EventLog.On('dice', { seconds = 600 })")
    end)
end

local ASK = [[
    local h = Osi.GetHostCharacter()
    local dc
    for _, id in ipairs(Ext.StaticData.GetAll("DifficultyClass")) do dc = id break end
    for _ = 1, 4 do Osi.RequestPassiveRoll(h, h, "SkillCheck", "Perception", dc, 0, "GKRig") end
    return true
]]

--- The naturals of the first four rows of `kind` of the host's SkillCheck after `mark`.
local function naturals(game, mark, kind)
    local n = 0
    local function mine(p) return uuid(p.who) == host and p.kind == "SkillCheck" end
    row(mark, kind, function(p) if mine(p) then n = n + 1 end return n == 4 end, 30)
    local out = {}
    for _, r in ipairs(game.events(mark, { kind }).rows or {}) do
        if mine(r.payload) and #out < 4 then out[#out + 1] = r.payload.natural end
    end
    return out
end

case("nth of four", function(game)
    step("the dice are the streams' own", "cheat", function()
        check(game.server("return GK.Rolls.SetKarmic(false)").ok, "Karmic Dice off")
    end)
    step("where the streams are", "observation", function()
        -- kept in the game's Lua for the put-back below
        check(game.server("GKRigStreams = GK.Rolls.Streams().saved return GKRigStreams ~= nil"), "the streams are read")
    end)
    local first = game.mark()
    step("four checks", "cheat", function() game.server(ASK) end)
    local free = step("what the stream gave", "observation", function()
        return naturals(game, first, "dice_thrown")
    end)
    local rigged = free[3] <= 10 and free[3] + 5 or free[3] - 5
    step("the streams back, the third rigged", "cheat", function()
        check(game.server("return GK.Rolls.SetStreams(GKRigStreams)").ok, "the streams are put back")
        local r = game.server(string.format("return GK.Rolls.Rig{ kind = 'SkillCheck', nth = 3, natural = %d }", rigged))
        check(r.ok, "the rig is set", r)
    end)
    local second = game.mark()
    step("the four checks again", "cheat", function() game.server(ASK) end)
    step("the third is decided, the others are the stream's", "observation", function()
        local thrown = naturals(game, second, "dice_thrown")
        eq(thrown, { free[1], free[2], rigged, free[4] }, "the roll log's four rows: what the game goes on with")
        local n, third = 0, nil
        for _, r in ipairs(game.events(second, { "dice_thrown" }).rows or {}) do
            local p = r.payload
            if uuid(p.who) == host and p.kind == "SkillCheck" then
                n = n + 1
                if n == 3 then third = p elseif n <= 4 then eq(p.rigged, nil, "a throw the rig left alone") end
            end
        end
        eq(third.rigged.natural, free[3], "the third row says what the stream had thrown")
        local was = row(second, "roll_rigged", nil, 30).payload
        eq(was.natural, rigged, "the rig's die")
        eq(was.was.natural, free[3], "the rig replaced the third throw")
        local used = naturals(game, second, "game_roll")
        eq(used, { free[1], free[2], rigged, free[4] }, "the game's four rolls")
        print(("free %s; the third rigged: %s"):format(join(free), join(used)))
    end)
end)

local FIRST, SECOND = "Target_SacredFlame", "Target_ViciousMockery"

case("by spell", function(game)
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
end)
