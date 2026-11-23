-- AI.Step in a dialogue (mods/AI/README.md). The rule held here: the step that asks a creature to
-- talk answers when the dialogue waits for the player, with the spoken line and the numbered
-- answers in `now.waiting` and the step that answers it; while it waits any other act is refused
-- with what waits, and `continue` is refused on a choice of several; `answer` with a number
-- chooses it - the dialogue moves on to another line or ends, and `waiting` says which. Before the
-- press the chosen answer's text takes a violet brush of the game's theme (GK.Cheat.Mark), the other
-- answers keep theirs.
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
        -- the dialogue's window is told by its own line alone: as an open window for do=ui it
        -- would outlive the dialogue in the client's report and read as something still waiting
        for _, other in ipairs(asked.now.waiting) do
            check(not (contains(other, "open windows") and contains(other, "Dialogue")), "the dialogue is not also an open window", asked.now.waiting)
        end
        return line
    end)
    step("another act is refused while it waits", "cheat", function()
        local no = call(game, "['do'] = 'go', target = '0,0,0'")
        check(no.ok == false and contains(no.why_not, "waits for an answer") and contains(no.hint, '"do":"go"'), "refused with what waits", no)
        local on = call(game, "['do'] = 'continue'")
        check(on.ok == false and contains(on.why_not, "choice") and contains(on.hint, '"do":"answer"'), "going on is refused on a choice", on)
    end)
    step("an answer can be shown in the theme's violet: only its own text, for a time of the game", "cheat", function()
        local marked = game.ui([[
            local m = GK.Cheat.Mark('Dialogue', 'answerList#1/LSButton', { seconds = 0.5 })
            local function brushes(handle)
                local row = GK.Gui.Handle('Dialogue', handle)
                local set = {}
                local function walk(e, d)
                    if d > 16 then return end
                    local fg pcall(function() fg = e.Foreground end)
                    if fg then local c pcall(function() c = fg.Color end) if type(c) == 'table' then set[#set + 1] = string.format('%.2f,%.2f,%.2f', c[1], c[2], c[3]) end end
                    local n = 0 pcall(function() n = e.VisualChildrenCount end)
                    for k = 1, n do local ch pcall(function() ch = e:VisualChild(k) end) if ch then walk(ch, d + 1) end end
                end
                walk(row.element, 0)
                return set
            end
            return { ok = m.ok, marked = m.marked, first = brushes('answerList#1/LSButton'), second = brushes('answerList#2/LSButton') }
        ]])
        check(marked.ok and marked.marked > 0, "the first answer's texts took the brush", marked)
        for _, c in ipairs(marked.first) do check(c == "0.40,0.00,1.00", "every text of the first answer is violet", marked.first) end
        for _, c in ipairs(marked.second) do check(c ~= "0.40,0.00,1.00", "the second answer keeps its own colours", marked.second) end
    end)
    local chosen = step("answer 1", "cheat", function() return call(game, "['do'] = 'answer', n = 1") end)
    step("the dialogue moved on", "cheat", function()
        check(chosen.ok and contains(chosen.did, "chose answer 1"), "the answer was chosen", chosen)
        check(chosen.now.waiting ~= nil, "waiting changed", chosen.now)
        check(chosen.now.waiting[1] ~= waiting, "it is not the same question", chosen.now.waiting)
    end)
    -- what waits for an answer: the lines of `waiting` but the open windows', which hold nothing back
    local function answering(list)
        local out = {}
        for _, line in ipairs(list or {}) do
            if not contains(line, "open windows:") then out[#out + 1] = line end
        end
        return out
    end
    step("to its end", "cheat", function()
        local now = chosen.now
        for _ = 1, 8 do
            local lines = answering(now.waiting)
            if #lines == 0 then lines = answering(game.server("return Mods.GateKitAI.AI.Look({ part = 'waiting' })", 30).now.waiting) end
            local line = lines[#lines]
            if not line then return end
            local next = contains(line, "do=continue") and call(game, "['do'] = 'continue'") or call(game, "['do'] = 'answer', n = 1")
            check(next.ok, "the step was done, after the line: " .. line, next)
            now = next.now
        end
        local left = game.server("return Mods.GateKitAI.AI.Look({ part = 'waiting' })", 30)
        check(#answering(left.now.waiting) == 0, "nothing waits at the end", left.now)
    end)
end
