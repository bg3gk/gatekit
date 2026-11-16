-- A roll asked from a place the roll log has no name for is written all the same, and says so:
-- its `origin` is `unknown` and the address it was asked from is kept in `caller`, so that a
-- reader can name the code later (docs/contracts/lua-api.md, section 17, GK.RollLog: "the row is
-- still written"). Every asker the staged scenes reach has a name by now (test:rolls/log/origins),
-- so the log is told to leave one out - GK.RollLog.Forget, which changes the log's table and
-- nothing in the game - and a roll of that asker is thrown: a story's passive check, asked by
-- unplaced::RollSkillCheck. The same check before the forgetting gives the named row to compare.
-- Staging (cheat): the passive check (the story's RequestPassiveRoll). Reading: observation.
-- Run: gatekit --instance <copy> test tests/_selftest -k roll_log_unknown

test { level = "GustavX:Basic_Level_A",
           about = "A roll asked from a place the log does not know is written and marked unknown, not dropped." }

local function uuid(id) return id and string.match(id, "(%x+%-%x+%-%x+%-%x+%-%x+)$") or id end

local ASK = [[
    local h = Osi.GetHostCharacter()
    local dc
    for _, id in ipairs(Ext.StaticData.GetAll("DifficultyClass")) do dc = id break end
    Osi.RequestPassiveRoll(h, h, "SkillCheck", "Perception", dc, 0, "GKUnknown")
    return true
]]

function run(game)
    game.wait_state("Running", "server", 120)
    local host = uuid(game.server("return Osi.GetHostCharacter()"))
    local function mine(p) return uuid(p.who) == host and p.kind == "SkillCheck" end
    step("the roll log on", "observation", function()
        check(game.server("return GK.RollLog.Available()"), "this Script Extender build throws the roll event")
        game.server("return GK.EventLog.On('dice', { seconds = 600 })")
    end)
    local first = game.mark()
    step("a check", "cheat", function() game.server(ASK) end)
    local named = step("its row names the asker", "observation", function()
        local p = row(first, "dice_thrown", mine, 30).payload
        eq(p.origin, "unplaced::RollSkillCheck", "the asker")
        return p
    end)
    step("the log forgets that asker", "observation", function()
        check(game.server("return GK.RollLog.Forget('unplaced::RollSkillCheck')").ok, "left out of the log's table")
    end)
    local second = game.mark()
    step("the check again", "cheat", function() game.server(ASK) end)
    step("the row is written and marked unknown", "observation", function()
        local p = row(second, "dice_thrown", mine, 30).payload
        eq(p.origin, "unknown", "no name for the asker")
        eq(p.what, nil, "nothing said of what the roll is for")
        eq(p.caller, named.caller, "the asking address is kept, the same as the named row's")
        check(string.match(p.caller, "^bg3_dx11%.exe%+0x%x+$"), "an address of the executable", p.caller)
        eq(#p.dice, #named.dice, "the dice are written")
        check(p.total ~= nil and p.natural ~= nil, "the result is written", p)
    end)
end
