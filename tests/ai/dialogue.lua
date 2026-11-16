-- AI.Step in a dialogue (mods/AI/README.md). The rule held here: the step that asks a creature to
-- talk answers when the dialogue waits for the player, with the spoken line and the numbered
-- answers in `now.waiting` and the step that answers it; while it waits any other act is refused
-- with what waits, and `continue` is refused on a choice of several; `answer` with a number
-- chooses it - the dialogue moves on to another line or ends, and `waiting` says which.
-- Staging (cheat): GK.Fixture.Talker. Talking is GateKit's scripted order; the answers are the
-- player's own (GK.Gui.Resolve). Every call of the bridge writes its row, so the steps that call
-- it are cheat steps.
-- Run: gatekit --instance <copy> test tests -k ai/dialogue --speed working

test { level = "GustavX:Basic_Level_A",
       about = "A dialogue's answers appear in `waiting`, numbered, and a step chooses one." }

local function call(game, body, timeout) return game.server("return Mods.GateKitAI.AI.Step({ " .. body .. " })", timeout or 90) end

function run(game)
    game.wait_state("Running", "server", 120)
    local talker = step("a talker", "cheat", function()
        local f = game.server("GK.Stage.Begin() return GK.Fixture.Talker()", 60)
        check(f and f.entities and f.entities[1], "the fixture stands", f)
        game.wait("server", "GK.Fixture.Ready('Talker')", 30)
        game.server("Mods.GateKitAI.AI.Session.Start({ mode = 'open', plan = 'off' }) return Mods.GateKitAI.AI.Look({})", 30)
        return f.entities[1].id
    end)
    local asked = step("asked to talk", "cheat", function() return call(game, string.format("['do'] = 'talk', target = %q", talker)) end)
    local waiting = step("the line and its answers wait", "cheat", function()
        check(asked.ok, "the talk began", asked)
        local line = asked.now.waiting and asked.now.waiting[1]
        check(line and contains(line, "1) ") and contains(line, "2) ") and contains(line, "do=answer"), "numbered answers and the step that answers", asked.now)
        return line
    end)
    step("another act is refused while it waits", "cheat", function()
        local no = call(game, "['do'] = 'go', target = '0,0,0'")
        check(no.ok == false and contains(no.why_not, "waits for an answer") and contains(no.hint, '"do":"go"'), "refused with what waits", no)
        local on = call(game, "['do'] = 'continue'")
        check(on.ok == false and contains(on.why_not, "choice") and contains(on.hint, '"do":"answer"'), "going on is refused on a choice", on)
    end)
    local chosen = step("answer 1", "cheat", function() return call(game, "['do'] = 'answer', n = 1") end)
    step("the dialogue moved on", "cheat", function()
        check(chosen.ok and contains(chosen.did, "chose answer 1"), "the answer was chosen", chosen)
        check(chosen.now.waiting ~= nil, "waiting changed", chosen.now)
        check(chosen.now.waiting[1] ~= waiting, "it is not the same question", chosen.now.waiting)
    end)
    step("to its end", "cheat", function()
        local now = chosen.now
        for _ = 1, 8 do
            local line = now.waiting and now.waiting[#now.waiting]
            if not line then
                local look = game.server("return Mods.GateKitAI.AI.Look({ part = 'waiting' })", 30)
                line = look.now.waiting[#look.now.waiting]
            end
            if not line then return end
            local next = contains(line, "do=continue") and call(game, "['do'] = 'continue'") or call(game, "['do'] = 'answer', n = 1")
            check(next.ok, "the step was done", next)
            now = next.now
        end
        local left = game.server("return Mods.GateKitAI.AI.Look({ part = 'waiting' })", 30)
        check(#left.now.waiting == 0, "nothing waits at the end", left.now)
    end)
end
