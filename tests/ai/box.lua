-- A message box over the game (the widget Dialog_box: the story's messages, the questions of the
-- menus and of character creation, "Resume the tutorial") in AI.Step. The rule held here: an open
-- box is an attention state (`window:box`), `now.waiting` names it with its text, its numbered
-- buttons and the step that answers it; a press on another window is refused while it stands,
-- with that step as the hint; `answer` with a button's number presses it, the box closes, the
-- state is closed and the press is taken again.
-- Staging (cheat): GK.Fixture.Box, the story's own message box (Osi.OpenMessageBox).
-- Run: gatekit --instance <copy> test tests -k ai/box --speed working

test { level = "GustavX:Basic_Level_A",
       about = "A message box waits in `waiting` with its buttons; a press behind it is refused; answer closes it." }

local function call(game, body, timeout) return game.server("return Mods.GateKitAI.AI.Step({ " .. body .. " })", timeout or 90) end
local function has(list, text)
    for _, line in ipairs(list or {}) do if contains(line, text) then return line end end
end

function run(game)
    game.wait_state("Running", "server", 120)
    step("a box over the game", "cheat", function()
        game.server("GK.Stage.Begin() Mods.GateKitAI.AI.Session.Start({ mode = 'open', plan = 'off' }) GK.Fixture.Box('GateKit box') return true", 60)
        game.wait("client", "GK.Attention.Waiting['window:box'] ~= nil", 20)
    end)
    local seen = step("the step after", "cheat", function() return call(game, "['do'] = 'wait'") end)
    step("it waits with its buttons and the step that answers", "cheat", function()
        local line = has(seen.now.waiting, "a box window")
        check(line and contains(line, "GateKit box") and contains(line, "1) ") and contains(line, "do=answer"), "the box's line", seen.now)
    end)
    local behind = step("a press behind it", "cheat", function() return call(game, "['do'] = 'ui', window = 'HotBar', control = 'anything'") end)
    step("is refused with the step that answers", "cheat", function()
        check(behind.ok == false and contains(behind.why_not, "waits over HotBar") and contains(behind.why_not, "do=answer"), "the refusal", behind)
    end)
    local answered = step("answer 1", "cheat", function() return call(game, "['do'] = 'answer', n = 1") end)
    step("the box is gone and the press is taken again", "cheat", function()
        check(answered.ok and contains(answered.did, "chose answer 1"), "the button was pressed", answered)
        game.wait("client", "GK.Attention.Waiting['window:box'] == nil", 20)
        local again = call(game, "['do'] = 'ui', window = 'HotBar'")
        check(again.ok and not has(again.now.waiting, "a box window"), "nothing waits, the window is read", again)
    end)
end
