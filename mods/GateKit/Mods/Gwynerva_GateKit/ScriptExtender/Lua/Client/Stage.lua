-- The client half of GK.Stage: the windows that stop a scene until the player does something
-- (a dice roll, a message box), read and answered from code. UI side (`ui` in the stand).
--
--   GK.Blocking()     what is on screen that waits for the player:
--                     { roll = { state, check, dc, result }, box = { title, text, gameOver, buttons },
--                       dialog = true }, or an empty table
--   GK.RollNext()     moves the dice-roll window one phase on (start -> rolling -> result -> closed)
--   GK.BoxPress(n)    presses button n of the message box (as GK.Blocking().box.buttons lists them)
--   GK.Unblock()      one step of getting rid of whatever blocks: advances a roll, accepts an
--                     ordinary message box; returns what it did (nil when nothing blocks).
--                     A "game over" box is reported, never pressed: both its buttons leave the game.

local GK = GK or Mods.GateKit.GK

local function properties(name)
    local model = GK.ViewModel(name)
    if not model then return nil end
    local ok, props = pcall(function() return model:GetAllProperties() end)
    return ok and props or nil
end

--- What is on screen that waits for the player (see the head of this file).
function GK.Blocking()
    local out = {}
    local roll = properties("gui::DCActiveRoll")
    if roll then
        local detail = roll.Roll and roll.Roll:GetAllProperties() or {}
        out.roll = { state = tostring(roll.RollState), check = tostring(roll.AbilityCheckText),
                     dc = detail.DifficultyCheck, result = roll.FinalResult }
    end
    local box = properties("ls.DCMessageBox")
    if box then
        local buttons = {}
        pcall(function()
            for i = 1, #box.Actions do buttons[i] = tostring(box.Actions[i]:GetAllProperties().BoundEvent) end
        end)
        out.box = { title = tostring(box.TitleProperty), text = tostring(box.TextProperty),
                    gameOver = box.IsGameOver == true, buttons = buttons }
    end
    if GK.ViewModel("gui::VMDialogue") then out.dialog = true end
    if GK.ViewModel("gui::DCTrade") then out.trade = true end
    return out
end

--- Moves the dice-roll window one phase on, as the player's click does. Returns the phase it
--- was in ("WaitForStart", "StartRoll", "ResultReady" ...), or nil when no roll is shown.
function GK.RollNext()
    local roll = properties("gui::DCActiveRoll")
    if not roll then return nil end
    local state = tostring(roll.RollState)
    roll.UserTriggeredNextPhaseCommand:Execute(nil)
    return state
end

--- Presses button `n` of the message box on screen.
function GK.BoxPress(n)
    local box = properties("ls.DCMessageBox")
    if not box then error("no message box on screen") end
    local action = box.Actions[n]:GetAllProperties()
    action.ActionCommand:Execute(action.ActionCommandParameter)
    return true
end

-- When the trade window was first seen and when Escape was last pressed for it (milliseconds).
local tradeSeen, tradeClosed = nil, 0

--- One step of removing what blocks the scene; returns what it did, or nil.
function GK.Unblock()
    local state = GK.RollNext()
    if state then return "roll: " .. state end
    -- The trade window: left open for two seconds (so that what trading brings to life can be
    -- seen), then closed the way the player closes it - one Escape, and not another for four
    -- seconds: a second Escape with nothing to close would open the game menu.
    if GK.ViewModel("gui::DCTrade") then
        local now = Ext.Utils.MonotonicTime()
        tradeSeen = tradeSeen or now
        if now - tradeSeen > 2000 and now - tradeClosed > 4000 then
            tradeClosed = now
            GK.Key("ESCAPE")
            return "trade window closed"
        end
        return nil
    end
    tradeSeen = nil
    local box = properties("ls.DCMessageBox")
    if box then
        if box.IsGameOver == true then return "game over" end
        local count = 0
        pcall(function() count = #box.Actions end)
        for i = 1, count do
            if tostring(box.Actions[i]:GetAllProperties().BoundEvent) == "UIAccept" then
                GK.BoxPress(i)
                return "box accepted: " .. tostring(box.TitleProperty)
            end
        end
    end
    return nil
end

-- Autoplay: the game answers itself. Instead of an outside script asking "is there a roll? a
-- question?" again and again, the client reacts in the tick the thing appears: a dice roll is
-- rolled through, an ordinary message box accepted, a dialogue that shows its answers gets the
-- last one. A scene then runs to its end at the speed of the game, with no waiting from outside.
local autoplay = GK.AutoplayState or { on = false, acted = {}, lastText = nil, quietTicks = 0 }
GK.AutoplayState = autoplay

--- Turns autoplay on or off. `options.answers` = false leaves dialogue answers to the caller
--- (rolls and boxes are still handled); `options.pick` = "first" | "second" | "third" | "last"
--- chooses which answer questions get. Returns the state.
function GK.Autoplay(on, options)
    autoplay.on = on ~= false
    autoplay.answers = not (options and options.answers == false)
    -- which answer a question gets: "last" (the default: usually the way out), "first", "second",
    -- "third" - a number of the list, the last one when the list is shorter. A question that
    -- comes back a third time gets the last answer whatever the choice, so no branch loops for ever.
    autoplay.pick = options and options.pick or "last"
    autoplay.asked = {}
    autoplay.acted = {}
    return GK.AutoplayInfo()
end

--- { on, acted = { "roll: WaitForStart", "answer 2 of 3", ... } (the last 20), busy } - `busy`
--- says whether something that waits for the player is on screen right now.
function GK.AutoplayInfo()
    local blocking = GK.Blocking()
    return { on = autoplay.on, acted = autoplay.acted, busy = next(blocking) ~= nil, blocking = blocking }
end

local function note(text)
    autoplay.acted[#autoplay.acted + 1] = text
    if #autoplay.acted > 20 then table.remove(autoplay.acted, 1) end
end

if not GK.AutoplayInstalled then
    GK.AutoplayInstalled = true
    local ticks = 0
    Ext.Events.Tick:Subscribe(function()
        if not GK.AutoplayState.on then return end
        ticks = ticks + 1
        if ticks % 6 ~= 0 then return end  -- a few times a second is plenty
        local ok, problem = pcall(function()
            local did = GK.Unblock()
            if did then
                if did ~= "game over" then note(did) end
                return
            end
            if not GK.AutoplayState.answers then return end
            local dialog = GK.Dialog()
            if dialog and dialog.showAnswers and #dialog.answers > 0 then
                -- the same question still on screen: give the game a moment before answering again
                local key = tostring(dialog.text) .. "#" .. #dialog.answers
                if GK.AutoplayState.lastText == key then
                    GK.AutoplayState.quietTicks = GK.AutoplayState.quietTicks + 1
                    if GK.AutoplayState.quietTicks < 8 then return end
                end
                GK.AutoplayState.lastText, GK.AutoplayState.quietTicks = key, 0
                local state = GK.AutoplayState
                state.asked = state.asked or {}
                state.asked[key] = (state.asked[key] or 0) + 1
                local wanted = ({ first = 1, second = 2, third = 3 })[state.pick or "last"] or #dialog.answers
                if state.asked[key] >= 3 then wanted = #dialog.answers end
                local chosen = dialog.answers[math.min(wanted, #dialog.answers)]
                if chosen.enabled == false then chosen = dialog.answers[#dialog.answers] end
                GK.Answer(chosen.n)
                note("answer " .. chosen.n .. " of " .. #dialog.answers)
            end
        end)
        if not ok then note("error: " .. tostring(problem):sub(1, 80)) end
    end)
end

-- Controls: the buttons of whatever is on screen, found in the UI tree and pressed from code.
-- A button of the game's UI carries the command a click would run (its Command and
-- CommandParameter); executing that command is the click, with no mouse and no window focus.
-- Checked in the game on 2026-11-13: pressing "EnterFTBButton" this way put the host into
-- turn-based mode.
--
--   GK.Controls(filter)   the controls on screen: { { n, name, kind, text, enabled, checked,
--                         context, command } }. filter (optional): part of a name or of a
--                         text, or { name = , text = , kind = , hidden = true (also list
--                         controls that are not visible), limit = }.
--   GK.Press(what)        presses one: by name ("EndTurnButton"), by part of its text
--                         ("Accept"), or by { name = , text = , n = } (n: number in GK.Controls
--                         with the same filter). A toggle or radio button without a command
--                         is switched instead. Returns the row of what was pressed.
--
-- Limits found on 2026-11-13: a command whose parameter is a string (Script Extender's
-- Execute takes objects only) cannot be pressed this way, and some buttons carry only a
-- cosmetic command (the level-up window's "finishLevelUpButton" plays an effect; the level is
-- finished by the window's own command, GK.Command("FinishLevelUp", nil, "gui::DCCharacterLevelUp")).
-- A command that needs a view model as its parameter gets it from the data context, e.g. the
-- level-up window is opened by StartLevelUp with CurrentPlayer.SelectedCharacter.

local controlKinds = { "Button", "CheckBox", "ListBoxItem", "TabItem", "ComboBoxItem" }

local function kindOf(typeName)
    for _, kind in ipairs(controlKinds) do
        if typeName:find(kind, 1, true) then return kind end
    end
end

local function childrenOf(element)
    local count = 0
    pcall(function() count = element.VisualChildrenCount end)
    local out = {}
    for i = 1, count do
        local ok, child = pcall(function() return element:VisualChild(i) end)
        if ok and child then out[#out + 1] = child end
    end
    return out
end

local function loca(value)
    if type(value) == "string" and value:match("^h%x+g%x+g%x+g%x+g%x+$") then
        local ok, resolved = pcall(Ext.Loca.GetTranslatedString, value)
        if ok and resolved and resolved ~= "" then return resolved end
    end
    return value
end

-- The text a player reads on a control: its own content when that is a string, otherwise the
-- text blocks inside it (at most a few levels down).
local function textOf(element)
    local content
    pcall(function() content = element.Content end)
    if type(content) == "string" and content ~= "" then return loca(content) end
    local parts = {}
    local function gather(node, depth)
        if depth > 6 or #parts >= 4 then return end
        local typeName = tostring(node.Type)
        if typeName:find("TextBlock", 1, true) or typeName:find("LSText", 1, true) then
            local text
            pcall(function() text = node.Text end)
            if type(text) == "string" and text ~= "" then parts[#parts + 1] = loca(text) end
        end
        for _, child in ipairs(childrenOf(node)) do gather(child, depth + 1) end
    end
    gather(element, 0)
    return #parts > 0 and table.concat(parts, " | ") or nil
end

local function describe(element, kind)
    local row = { kind = kind, type = tostring(element.Type) }
    pcall(function() local name = element.Name; if name and name ~= "" then row.name = name end end)
    pcall(function() row.enabled = element.IsEnabled end)
    pcall(function() row.checked = element.IsChecked end)
    pcall(function() row.selected = element.IsSelected end)
    pcall(function() local context = element.DataContext; row.context = context and tostring(context.Type) or nil end)
    pcall(function() row.command = element.Command ~= nil end)
    row.text = textOf(element)
    return row
end

local function lower(value) return tostring(value or ""):lower() end

local function controls(filter)
    if type(filter) == "string" then filter = { any = filter } end
    filter = filter or {}
    local rows, elements = {}, {}
    local limit = filter.limit or 200
    local stack, visited = { Ext.UI.GetRoot() }, 0
    while #stack > 0 and visited < 80000 and #rows < limit do
        local element = table.remove(stack)
        visited = visited + 1
        local hidden = false
        if not filter.hidden then
            -- most elements leave Visibility unset (it reads as nil), which means visible
            pcall(function()
                local visibility = tostring(element.Visibility)
                hidden = visibility == "Collapsed" or visibility == "Hidden"
            end)
        end
        if not hidden then  -- nothing under a collapsed element is on screen
            local kind = kindOf(tostring(element.Type))
            if kind then
                local row = describe(element, kind)
                local fits = (not filter.kind or kind == filter.kind)
                    and (not filter.name or lower(row.name):find(lower(filter.name), 1, true))
                    and (not filter.text or lower(row.text):find(lower(filter.text), 1, true))
                    and (not filter.any or lower(row.name):find(lower(filter.any), 1, true)
                         or lower(row.text):find(lower(filter.any), 1, true))
                if fits then
                    row.n = #rows + 1
                    rows[#rows + 1], elements[#elements + 1] = row, element
                end
            end
            local children = childrenOf(element)
            for i = #children, 1, -1 do stack[#stack + 1] = children[i] end
        end
    end
    return rows, elements
end

--- The controls on screen (see the head of this section).
function GK.Controls(filter)
    local rows = controls(filter)
    return rows
end

--- Presses a control (see the head of this section).
function GK.Press(what)
    local filter = type(what) == "table" and what or { any = what }
    local wanted = filter.n
    filter.n = nil
    local rows, elements = controls(filter)
    if #rows == 0 then error("no such control on screen: " .. Ext.Json.Stringify(filter)) end
    local index = wanted or 1
    if not wanted and type(what) == "string" then
        -- an exact name wins over a name or text that merely contains the words
        for i, row in ipairs(rows) do
            if row.name == what then index = i break end
        end
    end
    local row, element = rows[index], elements[index]
    if not row then error("only " .. #rows .. " controls fit") end
    if row.enabled == false then error("the control is disabled: " .. tostring(row.name or row.text)) end
    local command, parameter
    pcall(function() command = element.Command end)
    pcall(function() parameter = element.CommandParameter end)
    if command ~= nil then
        command:Execute(parameter)
        row.pressed = "command"
    elseif row.selected ~= nil then
        element.IsSelected = true
        row.pressed = "selected"
    elseif row.checked ~= nil then
        element.IsChecked = not row.checked
        row.pressed = "toggled"
    else
        error("the control has no command and cannot be selected: " .. tostring(row.name or row.text))
    end
    row.fitting = #rows
    return row
end

return true
