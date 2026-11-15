-- A rig decides one throw at the moment it is made and leaves every other throw to the game: of
-- four checks asked in one tick with the third rigged, the third gets the rigged die and the other
-- three are the stream's own (docs/contracts/lua-api.md, section 17, GK.Rolls.Rig). To know what
-- "the stream's own" is, the four are asked twice from the same place of the stream: once free,
-- read from the roll log, then with the streams put back (GK.Rolls.SetStreams of the reading taken
-- before, so the same dice come again) and the third rigged to another die. The rule is about
-- what changes between the two runs, so the checks are asked twice (docs/writing-tests.md, "Once
-- is enough"). Staging (cheat): the stage scope, Karmic Dice off (a forced outcome is not the
-- stream's), the streams, the rig, the passive checks (the story's RequestPassiveRoll). Reading:
-- the roll log's rows (what was thrown), the rig's row (what it replaced) and the game's own
-- roll rows (what the game used): observation.
-- Run: gatekit --instance <copy> test tests/rolls -k rolls/rig/one_of_several

test { level = "GustavX:Basic_Level_A",
           about = "One throw among several in the same tick is decided, the others are the game's own." }

local function uuid(id) return id and string.match(id, "(%x+%-%x+%-%x+%-%x+%-%x+)$") or id end

local ASK = [[
    local h = Osi.GetHostCharacter()
    local dc
    for _, id in ipairs(Ext.StaticData.GetAll("DifficultyClass")) do dc = id break end
    for _ = 1, 4 do Osi.RequestPassiveRoll(h, h, "SkillCheck", "Perception", dc, 0, "GKRig") end
    return true
]]

--- The naturals of the first four rows of `kind` of the host's SkillCheck after `mark`.
local function naturals(game, mark, kind, host)
    local n = 0
    local function mine(p) return uuid(p.who) == host and p.kind == "SkillCheck" end
    row(mark, kind, function(p) if mine(p) then n = n + 1 end return n == 4 end, 30)
    local out = {}
    for _, r in ipairs(game.events(mark, { kind }).rows or {}) do
        if mine(r.payload) and #out < 4 then out[#out + 1] = r.payload.natural end
    end
    return out
end

function run(game)
    game.wait_state("Running", "server", 120)
    local host = uuid(game.server("return Osi.GetHostCharacter()"))
    step("the roll log on", "observation", function()
        check(game.server("return GK.Rolls.CanRig()"), "this Script Extender build raises the roll event")
        game.server("return GK.EventLog.On('dice', { seconds = 600 })")
    end)
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
        return naturals(game, first, "dice_thrown", host)
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
        local thrown = naturals(game, second, "dice_thrown", host)
        eq(thrown, free, "the streams gave the same four dice again")
        local was = row(second, "roll_rigged", nil, 30).payload
        eq(was.natural, rigged, "the rig's die")
        eq(was.was.natural, free[3], "the rig replaced the third throw")
        local used = naturals(game, second, "game_roll", host)
        eq(used, { free[1], free[2], rigged, free[4] }, "the game's four rolls")
        print(("free %s; the third rigged: %s"):format(join(free), join(used)))
    end)
end
