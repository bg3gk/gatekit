-- AI.Step (mods/AI/README.md): one act, the wait until the game has settled, the answer. The rule
-- held here: a walk answers when the character stands, with the new place in `now.where`, and
-- says what it did; an act the bridge cannot begin is refused before anything is done, with why
-- and a hint that names the corrected call - an unknown act, a missing argument, a target nobody
-- knows; a line given with the step shows over the character's head and is kept, with the
-- thought, in the step's row.
-- Staging: none. The walk is GateKit's scripted order (cheat); every call of the bridge writes its
-- row, so the steps that call it are cheat steps.
-- Run: gatekit --instance <copy> test tests -k ai/step --speed working

test { level = "GustavX:Basic_Level_A",
       about = "A step walks and answers the new place; a refused act says why and how to call it right." }

local function at(game) return game.server("local x, y, z = Osi.GetPosition(Osi.GetHostCharacter()); return { x, y, z }") end

function run(game)
    game.wait_state("Running", "server", 120)
    step("the picture before", "cheat", function()
        game.server("Mods.GateKitAI.AI.Session.Start({ mode = 'open', plan = 'off' }) return Mods.GateKitAI.AI.Look({})", 30)
    end)
    local from = at(game)
    local goal = { from[1] + 6, from[2], from[3] + 5 }
    local mark = game.mark()
    local walked = step("a walk of eight metres, with a line", "cheat", function()
        return game.server(string.format(
            "return Mods.GateKitAI.AI.Step({ ['do'] = 'go', target = '%f,%f,%f', say = 'Over there, then', think = 'nothing here' })",
            goal[1], goal[2], goal[3]), 90)
    end)
    step("it stands at the goal, and where says so", "cheat", function()
        check(walked.ok and contains(walked.did, "walked to"), "the walk was done", walked)
        local p = at(game)
        check(math.abs(p[1] - goal[1]) < 1.5 and math.abs(p[3] - goal[3]) < 1.5, "the character is at the goal", p, goal)
        check(type(walked.now.where) == "string", "where is in the answer", walked.now)
        local x, z = walked.now.where:match("at (%-?[%d%.]+),%-?[%d%.]+,(%-?[%d%.]+)")
        check(x and math.abs(tonumber(x) - p[1]) < 0.3 and math.abs(tonumber(z) - p[3]) < 0.3, "where names the place it stands at", walked.now.where, p)
        local r = row(mark, "ai_step", function(payload) return payload["do"] == "go" end, 5)
        eq(r.payload.say, "Over there, then", "the row keeps the line")
        eq(r.payload.think, "nothing here", "the row keeps the thought")
        eq(r.payload.way, "ordered", "the row says how the act goes")
        row(mark, "ai_said", function(payload) return payload.say == "Over there, then" end, 5)
    end)
    step("an unknown act", "cheat", function()
        local no = game.server("return Mods.GateKitAI.AI.Step({ ['do'] = 'dance' })", 30)
        check(no.ok == false and contains(no.why_not, "no such act") and contains(no.hint, "ai_step"), "refused, with the acts and a call", no)
    end)
    step("an act without its target", "cheat", function()
        local no = game.server("return Mods.GateKitAI.AI.Step({ ['do'] = 'go' })", 30)
        check(no.ok == false and contains(no.why_not, "needs target") and contains(no.hint, '"do":"go"'), "refused, the hint is the call with a target", no)
    end)
    step("a target nobody knows", "cheat", function()
        local before = at(game)
        local no = game.server("return Mods.GateKitAI.AI.Step({ ['do'] = 'use', target = 'Nobody Of That Name' })", 30)
        check(no.ok == false and contains(no.why_not, "Nobody Of That Name") and no.hint ~= "", "refused, naming what was asked", no)
        local after = at(game)
        check(math.abs(after[1] - before[1]) < 0.1 and math.abs(after[3] - before[3]) < 0.1, "nothing was done", before, after)
    end)
end
