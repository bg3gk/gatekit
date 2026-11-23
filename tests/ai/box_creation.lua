-- A message box over character creation in AI.Step: the question of the window's close button
-- ("return to the main menu?", the widget Dialog_box). The rule held here: the box is an
-- attention state (`window:box`), `now.waiting` names it with its buttons and the step that
-- answers it; a press on the creation window is refused while it stands; `answer` with the "No"
-- button closes it and leaves creation open, and the press is taken again.
-- Run: gatekit --instance <copy> test tests -k ai/box_creation --speed working

test { level = "GustavX:SYS_CC_I",
       about = "Creation's close question waits with its buttons; a press behind it is refused; No closes it." }

local W = "CharacterCreation"
local function call(game, body, timeout) return game.server("return Mods.GateKitAI.AI.Step({ " .. body .. " })", timeout or 90) end
local function has(list, text)
    for _, line in ipairs(list or {}) do if contains(line, text) then return line end end
end

function run(game)
    game.wait_state("Running", "server", 120)
    game.wait("client", "GK.Gui.Widget('" .. W .. "') ~= nil", 60)
    local opened = step("the close button pressed", "cheat", function()
        game.server("Mods.GateKitAI.AI.Session.Start({ mode = 'open', plan = 'off' }) return true", 30)
        return call(game, "['do'] = 'ui', window = '" .. W .. "', control = 'ExitCC'")
    end)
    step("the box waits with its buttons and the step that answers", "cheat", function()
        check(opened.ok, "the button was pressed", opened)
        game.wait("client", "GK.Attention.Waiting['window:box'] ~= nil", 20)
        local seen = call(game, "['do'] = 'wait'")
        local line = has(seen.now.waiting, "a box window") or has(game.server("return Mods.GateKitAI.AI.Look({ part = 'waiting' })", 30).now.waiting, "a box window")
        check(line and contains(line, "1) ") and contains(line, "2) ") and contains(line, "do=answer"), "the box's line", line)
    end)
    local behind = step("a press on creation behind it", "cheat", function()
        return call(game, "['do'] = 'ui', window = '" .. W .. "', control = 'maleBtn'")
    end)
    step("is refused with the step that answers", "cheat", function()
        check(behind.ok == false and contains(behind.why_not, "waits over " .. W) and contains(behind.why_not, "do=answer"), "the refusal", behind)
    end)
    local no = step("answer 2 (No)", "cheat", function() return call(game, "['do'] = 'answer', n = 2") end)
    step("the box is gone, creation stays, the press is taken again", "cheat", function()
        check(no.ok and contains(no.did, "chose answer 2"), "No was pressed", no)
        game.wait("client", "GK.Attention.Waiting['window:box'] == nil", 20)
        local again = call(game, "['do'] = 'ui', window = '" .. W .. "', control = 'maleBtn'")
        check(again.ok, "the press behind is taken now", again)
        call(game, "['do'] = 'ui', window = '" .. W .. "', control = 'femaleBtn'")
    end)

    -- The story's own question: TUT_CharacterCreation, five seconds into creation, calls
    -- ResetTutorialsRequest(user, 1) when WasTutorialCompletedForUser is 1; the client asks only
    -- when the profile has tips already shown. Staged with the same call.
    step("a profile that has seen tips and completed the tutorials is asked to resume them", "cheat", function()
        game.server([[local host = Osi.GetHostCharacter() local user = Osi.GetReservedUserID(host)
            Osi.SetTutorialShown(host, 'Camera_Rotation_27b3873b-0353-4284-b901-32edbcfa9fe1')
            Osi.CompleteTutorialForUser(user) return true]], 30)
        -- the profile's shown tips reach the client a moment later; the request is made then
        game.sleep(2)
        game.server("Osi.ResetTutorialsRequest(Osi.GetReservedUserID(Osi.GetHostCharacter()), 1) return true", 30)
        game.wait("client", "GK.Attention.Waiting['window:box'] ~= nil", 20)
    end)
    step("the story's call is a row", "cheat", function()
        local rows = game.server("local out = {} for _, r in ipairs(GK.EventLog.Since(0, { kinds = { tutorial_reset_request = true } }).rows) do out[#out + 1] = r.payload end return out", 30)
        check(#rows > 0 and rows[#rows].ask == true, "tutorial_reset_request with ask", rows)
    end)
    local line = step("the question's line", "cheat", function()
        local seen = game.server("return Mods.GateKitAI.AI.Look({ part = 'waiting' })", 30)
        return has(seen.now.waiting, "a box window")
    end)
    step("it has two buttons and the step that answers", "cheat", function()
        check(line and contains(line, "1) ") and contains(line, "2) ") and contains(line, "do=answer"), "the question's line", line)
    end)
    local refused = step("a press behind the question", "cheat", function()
        return call(game, "['do'] = 'ui', window = '" .. W .. "', control = 'maleBtn'")
    end)
    step("is refused", "cheat", function()
        check(refused.ok == false and contains(refused.why_not, "waits over " .. W), "the refusal", refused)
    end)
    local no = step("answer 2 (do not resume)", "cheat", function() return call(game, "['do'] = 'answer', n = 2") end)
    step("the question is gone and the tutorials stay completed", "cheat", function()
        check(no.ok, "the second button was pressed", no)
        game.wait("client", "GK.Attention.Waiting['window:box'] == nil", 20)
        eq(game.server("return Osi.WasTutorialCompletedForUser(Osi.GetReservedUserID(Osi.GetHostCharacter()))"), 1, "the tutorials stay completed")
    end)
end
