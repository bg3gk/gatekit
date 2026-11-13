-- Popups: the windows that stop the game until the player answers - a dialogue, a dice roll, a
-- reaction prompt, a message box, a tutorial, loot, trade, rest, rewards ... - read and resolved
-- from code through GK.Gui (Client/Windows.lua). UI side (`ui` in the stand).
--
--   GK.Gui.Windows.<name>        registry entries (widget, read, step, close, blocking = true)
--   GK.Gui.Blocking()            the top-most window that waits for the player right now, with its
--                                summary and what can be done: { ok, window, widget, summary,
--                                choices, policy, all = { { window, widget } ... } }; window = nil
--                                when nothing waits
--   GK.Gui.Resolve(policy)       ONE step through whatever blocks, by a policy table:
--                                { dialogue = "last"|"first"|n|"text", roll = "throw"|"reroll",
--                                  reaction = "decline"|"react"|n, box = "affirmative"|"negative"|n|"text",
--                                  tutorial = "close", loot = "takeall"|"close", trade = "close",
--                                  pickpocket = "close", rest = "rest"|"cancel", reward = "accept"|n,
--                                  learn = "close"|"learn", split = "confirm"|"cancel", book = "close",
--                                  gamemenu = "resume", save = "close", load = "close" }
--                                a field may also be a table of step options ({ answer = 2 }).
--   GK.Gui.QuickSave() / QuickLoad() / SaveAs(name) / LoadLatest()
--   GK.Gui.Trade{ move = n | "name", side = "player"|"trader", balance = true, finish = true }
--
-- Rules: every Noesis access is guarded; results are { ok, ... } / { ok = false, error }; nothing
-- here throws for a game-side problem. No element or view model is kept across UI calls (each
-- call finds them again). GetAllProperties is never called on visual elements. Escape is never
-- injected: every window is answered by its own commands.
--
-- Found 2026-11-13 (game 4.1.1.7631656) from the XAML pages (Mods/MainUI/GUI/Pages/*.xaml,
-- Public/Game/GUI/Widgets/MessageBox.xaml) and the live view models.

local UI = GK.Gui
if not UI or not UI.Windows then return false end
local W = UI.Windows

local UNKNOWN = "ls::TranslatedStringRepository::s_HandleUnknown"
local CAP = 40

-- Guarded access ----------------------------------------------------------------------------

local function guard(f, ...)
    local ok, a, b = pcall(f, ...)
    if ok then return a, b end
    return nil, tostring(a)
end

local function get(object, key)
    if object == nil then return nil end
    local ok, value = pcall(function() return object[key] end)
    if ok then return value end
    return nil
end

local function path(object, dotted)
    for part in dotted:gmatch("[^%.]+") do
        object = get(object, part)
        if object == nil then return nil end
    end
    return object
end

local function count(collection)
    if collection == nil then return 0 end
    local ok, n = pcall(function() return #collection end)
    return ok and type(n) == "number" and n or 0
end

local function at(collection, i)
    local ok, value = pcall(function() return collection[i] end)
    return ok and value or nil
end

local function typeOf(object)
    local ok, name = pcall(function() return tostring(object.Type) end)
    return ok and name or "?"
end

-- A view-model property that `.X` hides (`.Type` is the Noesis type name).
local function property(object, key)
    if object == nil then return nil end
    local ok, value = pcall(function() return object:GetProperty(key) end)
    if ok then return value end
    return nil
end

local function text(value)
    if value == nil then return nil end
    if type(value) == "userdata" then
        value = get(value, "Text")
        if type(value) ~= "string" then return nil end
    end
    if type(value) ~= "string" or value == "" or value == UNKNOWN then return nil end
    if value:match("^h%x+g%x+g%x+g%x+g%x+$") or value:match("^ResStr_%d+$") then
        local ok, translated = pcall(Ext.Loca.GetTranslatedString, value)
        if ok and type(translated) == "string" and translated ~= "" and translated ~= UNKNOWN then return translated end
        return nil
    end
    return value
end

-- Text without markup (<i>, <LSTag ...>), trimmed.
local function clean(value)
    local s = text(value)
    if not s then return nil end
    s = s:gsub("<[^>]->", ""):gsub("^%s+", ""):gsub("%s+$", "")
    return s ~= "" and s or nil
end

local function num(value)
    if type(value) ~= "number" then return nil end
    if value ~= value or value == math.huge or value == -math.huge then return nil end
    if value == math.floor(value) then return math.floor(value) end
    return math.floor(value * 100 + 0.5) / 100
end

local function plain(value)
    local kind = type(value)
    if kind == "string" then return text(value) end
    if kind == "number" then return num(value) end
    if kind == "boolean" then return value end
    if kind == "userdata" then
        local s = guard(function() return tostring(value) end)
        if s and not s:find("userdata", 1, true) then return s end
    end
    return nil
end

local function each(collection, fn, cap)
    cap = cap or CAP
    local out, total = {}, 0
    for i = 1, count(collection) do
        local item = at(collection, i)
        if item ~= nil then
            local ok, value = pcall(fn, item, i)
            if ok and value ~= nil then
                total = total + 1
                if #out < cap then out[#out + 1] = value end
            end
        end
    end
    return out, total
end

-- Executes a command of a view model (a Lua string, number or boolean parameter is boxed).
local function run(command, parameter, label)
    if command == nil then return { ok = false, error = "no command " .. tostring(label) } end
    -- an ls.GameCommand (TakeItemCommand, TakeAllAndCloseCommand ...) wraps the command it runs
    if typeOf(command) == "ls.GameCommand" then
        command = get(command, "ActionCommand")
        if command == nil then return { ok = false, error = "no ActionCommand in " .. tostring(label) } end
    end
    local kind = type(parameter)
    if kind == "string" or kind == "number" or kind == "boolean" then
        local boxed = guard(function() return Ext.UI.Box(parameter) end)
        parameter = boxed
    end
    local _, problem = guard(function() command:Execute(parameter) end)
    if problem then return { ok = false, error = tostring(label) .. ": " .. problem } end
    return { ok = true, ran = label }
end

local function runOn(data, name, parameter)
    return run(get(data, name), parameter, name)
end

-- Presses a named control of an open widget with its LIVE Command and CommandParameter (what a
-- click passes, bindings resolved by the game) - for buttons whose catalogued chain cannot be
-- resolved from the view model.
local function pressLive(widget, name)
    local element = UI.Find(widget, name)
    if not element then return { ok = false, error = "no control " .. tostring(name) .. " in " .. tostring(widget) } end
    if guard(function() return element.IsEnabled end) == false then return { ok = false, error = "disabled: " .. tostring(name) } end
    local command = guard(function() return element.Command end)
    local parameter = guard(function() return element.CommandParameter end)
    return run(command, parameter, name)
end

local function lower(value) return tostring(value or ""):lower() end

local function characterName(character)
    local name = text(get(character, "Name"))
    if name then return name end
    local uuid = get(character, "EntityUUID")
    if type(uuid) == "string" then
        local ok, translated = pcall(function()
            return Ext.Loca.GetTranslatedString(Ext.Entity.Get(uuid).DisplayName.NameKey.Handle.Handle)
        end)
        if ok and type(translated) == "string" and translated ~= "" then return translated end
    end
    return nil
end

local function itemSummary(item, i)
    if item == nil then return nil end
    local out = { n = i, name = text(get(item, "Name")) or text(get(item, "NameAlchemy")) or "?" }
    local n = num(get(item, "Count"))
    if n and n ~= 1 then out.count = n end
    local value = num(get(item, "Value")) or num(get(item, "Gold"))
    if value and value > 0 then out.value = value end
    if get(item, "IsStolen") == true then out.stolen = true end
    local rarity = plain(get(item, "Rarity"))
    if rarity and rarity ~= "Common" and rarity ~= "None" then out.rarity = rarity end
    return out
end

-- The items of an inventory view model (Slots -> { Object = VMItem }), numbered by slot order.
local function slotsOf(inventory, cap)
    local out, objects = {}, {}
    for i = 1, count(get(inventory, "Slots")) do
        local object = get(at(get(inventory, "Slots"), i), "Object")
        if object then
            objects[#objects + 1] = object
            if #out < (cap or CAP) then out[#out + 1] = itemSummary(object, #objects) end
        end
    end
    return out, objects
end

-- A reader that always returns a table.
local function reader(fn)
    return function(data, ui)
        local ok, result = pcall(fn, data, ui)
        if ok and type(result) == "table" then return result end
        return { error = ok and "the reader returned nothing" or tostring(result) }
    end
end

-- A step that always returns a table.
local function stepper(fn)
    return function(ui, options)
        local ok, result = pcall(fn, ui, options or {})
        if ok and type(result) == "table" then return result end
        return { ok = false, error = ok and "the step returned nothing" or tostring(result) }
    end
end

-- The policy choice of a step: options.choice (from GK.Gui.Resolve) or options.pick.
local function choiceOf(options)
    return options.choice or options.pick
end

-- Whether an element of an open widget is shown (Visibility not Collapsed / Hidden).
local function shown(element)
    if element == nil then return false end
    local visibility = guard(function() return tostring(element.Visibility) end)
    return visibility ~= "Collapsed" and visibility ~= "Hidden"
end

-- dialogue: Dialogue.xaml + Public/Game/GUI/Library/DialogueTemplates.xaml ---------------------
-- CurrentPlayer.SelectedCharacter.PlayerCharacterProperties.ActiveDialogue (gui::VMDialogue):
-- SpeakerName, BodyText, ShowAnswers, Answers -> gui::VMDialogueAnswer {AnswerIdx, BodyText,
-- Enabled, AnswerTags, CtxAnswer.Params}, ContinueAnswer, CanTrade, DialogFileName. An answer is
-- chosen by the widget's SelectAnswerCommand(answer VM) (the answer row's Command); the continue
-- row is the same command with ContinueAnswer; a voiced line is skipped by SelectorSkipCommand
-- (PlayerId) - the input binding Dialogue.ContinueBinding (the key that skips a line).

local function activeDialogue(data)
    return path(data, "CurrentPlayer.SelectedCharacter.PlayerCharacterProperties.ActiveDialogue")
end

-- "[PERSUASION] [CLERIC] Let me help." -> { "PERSUASION", "CLERIC" }, "Let me help."
local function splitTags(s)
    local tags = {}
    if not s then return tags, s end
    while true do
        local tag, rest = s:match("^%s*%[([^%]]+)%]%s*(.*)$")
        if not tag then break end
        tags[#tags + 1] = tag
        s = rest
    end
    return tags, s
end

local function answerOf(answer, i)
    local body = clean(get(answer, "BodyText"))
    if not body then
        local parts = {}
        each(path(answer, "CtxAnswer.Params"), function(p)
            local t = clean(get(p, "Text"))
            if t then parts[#parts + 1] = (plain(property(p, "Type")) == "Tag") and ("[" .. t .. "] ") or t end
            return true
        end)
        body = #parts > 0 and table.concat(parts) or clean(path(answer, "CtxAnswer.Text"))
    end
    local tags, rest = splitTags(body)
    each(get(answer, "AnswerTags"), function(tag)
        local t = clean(get(tag, "Text")) or clean(get(tag, "Name")) or plain(property(tag, "Type"))
        if t then tags[#tags + 1] = t end
        return true
    end)
    local idx = num(get(answer, "AnswerIdx"))
    local out = { n = idx and (idx + 1) or i, text = rest or body or "" }
    if #tags > 0 then out.tags = tags end
    if get(answer, "Enabled") == false then out.enabled = false end
    return out
end

local function dialogueAnswers(ad)
    return (each(get(ad, "Answers"), answerOf))
end

W.dialogue = W.dialogue or {}
W.dialogue.widget = "Dialogue"
W.dialogue.blocking = true
W.dialogue.policy = "dialogue"
W.dialogue.about = "a conversation: answers, continue, skip a voiced line"
W.dialogue.read = reader(function(data)
    local ad = activeDialogue(data)
    local out = { speaker = clean(get(ad, "SpeakerName")), text = clean(get(ad, "BodyText")) }
    out.showAnswers = get(ad, "ShowAnswers") == true
    out.listening = get(data, "ActiveDialogueIsListening") == true
    out.answers = dialogueAnswers(ad)
    out.continue = get(ad, "ContinueAnswer") ~= nil
    if get(ad, "CanTrade") == true then out.canTrade = true end
    if get(ad, "AllowAttack") == true then out.canAttack = true end
    out.file = plain(get(ad, "DialogFileName"))
    out.instance = num(get(ad, "DialogInstanceID"))
    local selectedAnswer = clean(path(ad, "SelectedAnswer.BodyText"))
    if selectedAnswer then out.chosen = selectedAnswer end
    out.state = out.showAnswers and (#out.answers > 0 and "answers" or (out.continue and "continue" or "waiting"))
        or (out.listening and "listening" or "line")
    return out
end)

-- Which answer: options.answer (a number as shown, or part of the text), else options.choice /
-- pick ("first", "last", "second", "third", a number, or part of the text); default "last".
local function pickAnswer(answers, options)
    local want = options.answer
    if want == nil then want = choiceOf(options) end
    if want == nil then want = "last" end
    local usable = {}
    for _, a in ipairs(answers) do if a.enabled ~= false then usable[#usable + 1] = a end end
    if #usable == 0 then return nil end
    if type(want) == "number" then
        for _, a in ipairs(answers) do if a.n == want then return a end end
        return nil, "no answer " .. want
    end
    local named = { first = 1, second = 2, third = 3 }
    if want == "last" then return usable[#usable] end
    if named[want] then return usable[math.min(named[want], #usable)] end
    for _, a in ipairs(usable) do
        if lower(a.text):find(lower(want), 1, true) then return a end
        for _, tag in ipairs(a.tags or {}) do
            if lower(tag):find(lower(want), 1, true) then return a end
        end
    end
    return nil, "no answer like " .. tostring(want)
end

W.dialogue.step = stepper(function(ui, options)
    local data = ui.Data("Dialogue")
    if not data then return { done = true, did = "closed" } end
    local ad = activeDialogue(data)
    if not ad then return { did = "nothing", wait = 300, note = "no active dialogue yet" } end
    local showAnswers = get(ad, "ShowAnswers") == true
    if showAnswers then
        local list = get(ad, "Answers")
        local answers = dialogueAnswers(ad)
        if #answers > 0 then
            local chosen, problem = pickAnswer(answers, options)
            if not chosen then return { ok = false, error = problem or "no enabled answer", answers = answers } end
            for i = 1, count(list) do
                local vm = at(list, i)
                local idx = num(get(vm, "AnswerIdx"))
                if (idx and idx + 1 or i) == chosen.n then
                    local result = runOn(data, "SelectAnswerCommand", vm)
                    result.did = "answer " .. chosen.n .. ": " .. tostring(chosen.text)
                    result.wait = 800
                    return result
                end
            end
            return { ok = false, error = "answer " .. chosen.n .. " vanished" }
        end
        local continue = get(ad, "ContinueAnswer")
        if continue then
            local result = runOn(data, "SelectAnswerCommand", continue)
            result.did = "continue"
            result.wait = 600
            return result
        end
    end
    -- a line being spoken (or a cinematic moment): skip it as the continue key does
    if options.skip == false then return { did = "waiting for the line", wait = 500 } end
    local result = runOn(data, "SelectorSkipCommand", num(get(data, "PlayerId")) or 1)
    result.did = "skip line"
    result.wait = 400
    return result
end)
W.dialogue.close = function() return false, "a dialogue ends by its answers" end

-- roll: ActiveRoll.xaml (gui::DCActiveRoll) ---------------------------------------------------
-- RollState: IntroductionAnimation, WaitForStart, StartRoll, StopRoll, ResultReady, WaitForReRoll.
-- Roll {DifficultyCheck, ShowDC, RollAdvantageType, Modifiers -> {Name/Description, Value}},
-- SkillOrAbility / AbilityCheckText, BoostModifierList, InspirationPoints, FinalResult, Success.
-- The dice are thrown by UserTriggeredNextPhaseCommand (the UIAccept key and the dice's click);
-- the result is left by ContinueCommand; RollAgainCommand spends inspiration.

local function rollList(collection)
    return (each(collection, function(vm)
        local name = clean(get(vm, "Name")) or clean(get(vm, "Description"))
        local value = get(vm, "Value")
        local entry = { name = name }
        if type(value) == "number" then entry.value = num(value) elseif type(value) == "string" then entry.value = value end
        if get(vm, "IsDisabled") == true then entry.off = true end
        if not name and entry.value == nil then return nil end
        return entry
    end, 12))
end

local function rollRead(data)
    local out = { state = plain(get(data, "RollState")) or "?" }
    out.check = clean(get(data, "AbilityCheckText")) or clean(get(data, "SkillOrAbility"))
    out.skill = clean(get(data, "SkillOrAbility"))
    out.context = plain(get(data, "RollContext"))
    local roll = get(data, "Roll")
    if roll then
        local dc = num(get(roll, "DifficultyCheck"))
        if dc and dc > 0 then out.dc = dc end
        local advantage = plain(get(roll, "RollAdvantageType"))
        if advantage and advantage ~= "None" then out.advantage = advantage end
        out.modifiers = rollList(get(roll, "Modifiers"))
        if #out.modifiers == 0 then out.modifiers = nil end
    end
    local boosts = rollList(get(data, "BoostModifierList"))
    if #boosts > 0 then out.boosts = boosts end
    out.inspiration = num(get(data, "InspirationPoints"))
    local low, high = num(get(data, "MinBonusValue")), num(get(data, "MaxBonusValue"))
    if low then out.bonus = (low == high) and low or { low, high } end
    if out.state == "ResultReady" or out.state == "WaitForReRoll" then
        out.result = num(get(data, "FinalResult"))
        out.success = get(data, "Success")
        if type(out.success) ~= "boolean" then out.success = nil end
    end
    return out
end

W.roll = W.roll or {}
W.roll.widget = "ActiveRoll"
W.roll.blocking = true
W.roll.policy = "roll"
W.roll.about = "a dice roll the player throws (skill checks in dialogues, lockpicking ...)"
W.roll.read = reader(rollRead)
W.roll.step = stepper(function(ui, options)
    local data = ui.Data("ActiveRoll")
    if not data then return { done = true, did = "closed" } end
    local state = plain(get(data, "RollState")) or "?"
    if state == "WaitForStart" or state == "WaitForReRoll" then
        local result = runOn(data, "UserTriggeredNextPhaseCommand", true)
        if not result.ok then result = runOn(data, "TriggerNextPhaseCommand") end
        result.did = "throw (" .. state .. ")"
        result.wait = 1200
        return result
    elseif state == "ResultReady" then
        local wantReroll = choiceOf(options) == "reroll" or options.reroll == true
        if wantReroll and get(data, "Success") == false and (num(get(data, "InspirationPoints")) or 0) > 0 then
            local result = runOn(data, "RollAgainCommand")
            result.did = "reroll with inspiration"
            result.wait = 1200
            return result
        end
        local result = runOn(data, "ContinueCommand")
        if not result.ok then result = runOn(data, "UserTriggeredNextPhaseCommand", true) end
        result.did = "continue (result " .. tostring(num(get(data, "FinalResult"))) .. ", "
            .. (get(data, "Success") == true and "success" or "failure") .. ")"
        result.wait = 800
        return result
    end
    -- IntroductionAnimation, StartRoll, StopRoll: the dice are moving
    return { did = "waiting (" .. state .. ")", wait = 500 }
end)
W.roll.close = function(ui)
    local data = ui.Data("ActiveRoll")
    local result = runOn(data, "CancelCommand")
    return result.ok, result.error
end

-- reaction: ReactionDecisionPopup.xaml (DCReactionDecision) -------------------------------------
-- Data.InterruptEvents -> VMInterruptEvent {Interruptors -> {Character, InterruptDecisions ->
-- {Interrupt (Name), Decision}}, Description}. The widget stays in the tree with no events: it
-- blocks only while InterruptEvents has some. A reaction row (ReactButton) runs
-- InterruptReactCommand with a MultiBinding pair (decision, interruptor) only the live control
-- holds; Confirm runs InterruptAcceptPlayerCommand(event), Do Not React
-- InterruptDontReactPlayerCommand(event).

local function interruptEvents(data)
    return path(data, "Data.InterruptEvents")
end

W.reaction = W.reaction or {}
W.reaction.widget = "ReactionDecisionPopup"
W.reaction.blocking = true
W.reaction.policy = "reaction"
W.reaction.about = "a reaction prompt (opportunity attack, Shield ...) asking the player"
W.reaction.active = function(data) return count(interruptEvents(data)) > 0 end
W.reaction.read = reader(function(data)
    local events = interruptEvents(data)
    local out = { pending = count(events) }
    local event = at(events, 1)
    if not event then return out end
    out.description = clean(get(event, "Description"))
    local options = {}
    out.interruptors = each(get(event, "Interruptors"), function(interruptor)
        local who = characterName(get(interruptor, "Character"))
        local decisions = each(get(interruptor, "InterruptDecisions"), function(decision)
            local interrupt = get(decision, "Interrupt")
            local entry = { who = who, name = clean(get(interrupt, "Name")) or clean(get(interrupt, "DisplayName")) or plain(get(interrupt, "PrototypeID")) or "?",
                            decision = plain(get(decision, "Decision")) }
            options[#options + 1] = entry
            entry.n = #options
            return entry.name
        end, 12)
        return { who = who, reactions = decisions }
    end, 8)
    out.options = options
    return out
end)

W.reaction.step = stepper(function(ui, options)
    local data = ui.Data("ReactionDecisionPopup")
    local event = at(interruptEvents(data), 1)
    if not event then return { done = true, did = "no prompt" } end
    local choice = choiceOf(options) or "decline"
    if options.react ~= nil then choice = options.react == false and "decline" or (options.react == true and "react" or options.react) end
    if choice == "decline" then
        local result = runOn(data, "InterruptDontReactPlayerCommand", event)
        result.did = "do not react"
        result.wait = 600
        return result
    end
    -- react: the row (by number in the reader's options, or part of its name; default 1) is
    -- chosen the way its click does it - the live ReactButton's own command and parameter
    local which = type(choice) == "number" and choice or options.which or 1
    if type(choice) == "string" and choice ~= "react" then which = choice end
    local rows = ui.Controls("ReactionDecisionPopup", { name = "ReactButton" })
    if not rows.ok or #rows.controls == 0 then return { ok = false, error = "no reaction rows on screen" } end
    local index = type(which) == "number" and which or nil
    if not index then
        local summary = W.reaction.read(data, ui)
        for _, option in ipairs(summary.options or {}) do
            if lower(option.name):find(lower(which), 1, true) then index = option.n break end
        end
    end
    index = index or 1
    -- a row already chosen (Decision = Use) is not clicked again: that would undo it
    local decisionChosen = false
    local k = 0
    each(get(event, "Interruptors"), function(interruptor)
        each(get(interruptor, "InterruptDecisions"), function(decision)
            k = k + 1
            if k == index and plain(get(decision, "Decision")) == "Use" then decisionChosen = true end
            return true
        end)
        return true
    end)
    -- A lone interruptor's row reacts by itself (checked 2026-11-13: the click alone made the
    -- opportunity attack). Confirm right after the click closed the prompt WITHOUT the reaction
    -- (the row's command is deferred and its Decision not yet Use), so Confirm is run only in a
    -- later step, for a row already chosen - the case of several interruptors.
    if decisionChosen then
        local accept = runOn(data, "InterruptAcceptPlayerCommand", event)
        accept.did = "confirm option " .. tostring(index)
        accept.wait = 800
        return accept
    end
    local picked = ui.Click("ReactionDecisionPopup", "ReactButton", index)
    return { ok = picked.ok and true or false, error = picked.error, did = "react with option " .. tostring(index),
             wait = 800 }
end)
W.reaction.close = function(ui)
    local data = ui.Data("ReactionDecisionPopup")
    local result = runOn(data, "InterruptDontReactPlayerCommand", at(interruptEvents(data), 1))
    return result.ok, result.error
end

-- box: Public/Game/GUI/Widgets/MessageBox.xaml (ls.DCMessageBox, widget "Dialog_box") ------------
-- TitleProperty, TextProperty, IsGameOver, Actions -> {Name, BoundEvent (UIAccept / UICancel ...),
-- ActionCommand, ActionCommandParameter}, InputTextProperty, CheckBoxValueProperty, RequestedTimer.
-- A button runs ActionCommand(ActionCommandParameter); a string parameter ("CloseMessageBox") is
-- boxed first.

local function boxButtons(data)
    return (each(get(data, "Actions"), function(action, i)
        return { n = i, text = clean(get(action, "Name")) or clean(get(action, "ActionName")) or "?",
                 event = plain(get(action, "BoundEvent")) }
    end, 8))
end

W.box = W.box or {}
W.box.widget = "Dialog_box"
W.box.blocking = true
W.box.policy = "box"
W.box.about = "a message box (a question with buttons, a notice, game over)"
W.box.read = reader(function(data)
    local out = { title = clean(get(data, "TitleProperty")), text = clean(get(data, "TextProperty")),
                  buttons = boxButtons(data) }
    if get(data, "IsGameOver") == true then out.gameOver = true end
    local input = get(data, "InputTextProperty")
    if type(input) == "string" and input ~= "" then out.input = input end
    local checkbox = get(data, "CheckBoxValueProperty")
    if type(checkbox) == "boolean" then out.checkbox = checkbox end
    local timer = num(get(data, "RequestedTimer"))
    if timer and timer > 0 then out.timer = timer end
    return out
end)

local AFFIRMATIVE = { UIAccept = 1, UIMessageBoxA = 2, UIMessageBoxY = 3 }
local NEGATIVE = { UICancel = 1, UIMessageBoxB = 2, UIMessageBoxX = 3, UIBack = 4 }

W.box.step = stepper(function(ui, options)
    local data = ui.Data("Dialog_box")
    if not data then return { done = true, did = "closed" } end
    if get(data, "IsGameOver") == true and not options.gameOver then
        return { ok = false, stuck = true, gameOver = true, did = "game over: left for the caller",
                 error = "a game-over box: both its buttons leave the game" }
    end
    local buttons = boxButtons(data)
    if #buttons == 0 then return { ok = false, stuck = true, error = "the box has no buttons" } end
    local want = options.button
    if want == nil then want = choiceOf(options) end
    if want == nil then want = "affirmative" end
    -- a box over the game menu asks whether to leave the game (Main Menu, Quit, Load): "yes"
    -- only when the caller names the button
    if want == "affirmative" and options.button == nil and ui.IsOpen("GameMenu") and not options.leave then
        return { ok = false, stuck = true, did = "left for the caller",
                 error = "a box over the game menu (it may leave the game): pass button = n, or box = 'negative'" }
    end
    local pick
    if type(want) == "number" then
        pick = buttons[want]
    elseif want == "affirmative" or want == "negative" then
        local rank = want == "affirmative" and AFFIRMATIVE or NEGATIVE
        for _, b in ipairs(buttons) do
            if rank[b.event] and (not pick or rank[b.event] < rank[pick.event]) then pick = b end
        end
        if not pick then pick = want == "affirmative" and buttons[1] or buttons[#buttons] end
    else
        for _, b in ipairs(buttons) do
            if lower(b.text):find(lower(want), 1, true) then pick = b break end
        end
    end
    if not pick then return { ok = false, error = "no button " .. tostring(want), buttons = buttons } end
    if options.input ~= nil then guard(function() data.InputTextProperty = tostring(options.input) end) end
    local action = at(get(data, "Actions"), pick.n)
    local parameter = get(action, "ActionCommandParameter")
    local result = run(get(action, "ActionCommand"), parameter, "button " .. pick.n)
    result.did = "pressed " .. pick.n .. ": " .. tostring(pick.text)
    result.wait = 600
    return result
end)
W.box.close = function(ui)
    local result = W.box.step(ui, { choice = "negative" })
    return result.ok, result.error
end

-- tutorials -----------------------------------------------------------------------------------
-- ModalTutorial (Tutorial.xaml, the big tutorial window that pauses the game): Tutorial {Title,
-- Description, TutorialID}, CurrentStep, NextStep, PreviousStep, FinishTutorial (its close button
-- and the ToggleInGameMenu key). TutorialPopup (TutorialPopup.xaml, the side tutorial):
-- Tutorials[0] {Title, Description}; its button raises CustomEvent("CloseWidget").

W.tutorial = W.tutorial or {}
W.tutorial.widget = "ModalTutorial"
W.tutorial.blocking = true
W.tutorial.policy = "tutorial"
W.tutorial.about = "a tutorial window that pauses the game"
W.tutorial.read = reader(function(data)
    local tutorial = get(data, "Tutorial")
    return { title = clean(get(tutorial, "Title")), text = clean(get(tutorial, "Description")),
             id = plain(get(tutorial, "TutorialID")), step = num(get(data, "CurrentStep")) }
end)
W.tutorial.step = stepper(function(ui, options)
    local data = ui.Data("ModalTutorial")
    if not data then return { done = true, did = "closed" } end
    if choiceOf(options) == "next" then
        local result = runOn(data, "NextStep")
        result.did = "next page"
        return result
    end
    local result = runOn(data, "FinishTutorial")
    result.did = "finish tutorial"
    result.wait = 500
    return result
end)
W.tutorial.close = function(ui)
    local result = runOn(ui.Data("ModalTutorial"), "FinishTutorial")
    return result.ok, result.error
end

W.sidetutorial = W.sidetutorial or {}
W.sidetutorial.widget = "TutorialPopup"
W.sidetutorial.blocking = true
W.sidetutorial.policy = "tutorial"
W.sidetutorial.about = "a tutorial popup at the side of the screen"
W.sidetutorial.read = reader(function(data)
    local first = at(get(data, "Tutorials"), 1)
    return { title = clean(get(first, "Title")), text = clean(get(first, "Description")),
             count = count(get(data, "Tutorials")) }
end)
W.sidetutorial.step = stepper(function(ui)
    local data = ui.Data("TutorialPopup")
    if not data then return { done = true, did = "closed" } end
    local result = runOn(data, "CustomEvent", "CloseWidget")
    result.did = "close side tutorial"
    result.wait = 400
    return result
end)

-- loot: Container.xaml (DCContainerInventory, widget "ContainerWidget") ------------------------
-- CurrentPlayer.ContainerInventoryList -> {LootInventory.Slots -> Object (VMItem)},
-- CurrentPlayer.UIData.CurrentContainer; Take All: TakeAllAndCloseCommand.ActionCommand; the close
-- button: CloseContainerCommand(the container); one item: TakeItemCommand / PickUpCommand(item).

local function containerItems(data)
    local items, objects = {}, {}
    each(path(data, "CurrentPlayer.ContainerInventoryList"), function(container)
        local name = text(get(container, "Name"))
        for i = 1, count(path(container, "LootInventory.Slots")) do
            local object = get(at(path(container, "LootInventory.Slots"), i), "Object")
            if object then
                objects[#objects + 1] = object
                if #items < CAP then
                    local entry = itemSummary(object, #objects)
                    entry.from = name
                    items[#items + 1] = entry
                end
            end
        end
        return true
    end, 8)
    return items, objects
end

W.loot = W.loot or {}
W.loot.widget = "ContainerWidget"
W.loot.blocking = true
W.loot.policy = "loot"
W.loot.about = "an open container or corpse"
W.loot.read = reader(function(data)
    local items, objects = containerItems(data)
    local current = path(data, "CurrentPlayer.UIData.CurrentContainer")
    return { container = text(get(current, "Name")) or text(path(current, "Item.Name")), items = items, count = #objects }
end)
W.loot.step = stepper(function(ui, options)
    local data = ui.Data("ContainerWidget")
    if not data then return { done = true, did = "closed" } end
    local choice = choiceOf(options) or "takeall"
    if options.item then
        local _, objects = containerItems(data)
        local index = options.item
        if type(index) == "string" then
            for i, object in ipairs(objects) do
                if lower(text(get(object, "Name"))):find(lower(index), 1, true) then index = i break end
            end
        end
        local object = type(index) == "number" and objects[index] or nil
        if not object then return { ok = false, error = "no item " .. tostring(options.item) } end
        local result = runOn(data, "TakeItemCommand", object)
        if not result.ok then result = runOn(data, "PickUpCommand", object) end
        result.did = "take item " .. tostring(index)
        return result
    end
    if choice == "close" then
        local result = runOn(data, "CloseContainerCommand", path(data, "CurrentPlayer.UIData.CurrentContainer"))
        result.did = "close"
        result.wait = 400
        return result
    end
    local result = run(path(data, "TakeAllAndCloseCommand.ActionCommand"), nil, "TakeAllAndCloseCommand")
    result.did = "take all and close"
    result.wait = 600
    return result
end)
W.loot.close = function(ui)
    local data = ui.Data("ContainerWidget")
    local result = runOn(data, "CloseContainerCommand", path(data, "CurrentPlayer.UIData.CurrentContainer"))
    return result.ok, result.error
end

-- trade: Trade.xaml (gui::DCTrade) ------------------------------------------------------------
-- Commands: MoveTradeItem(item), BalanceTrade, FinishTrade, FinishBarter, SetTradeMode, SellWares;
-- the window closes with CustomEvent("CloseWidget"). The sides are read from the view model
-- (names found live; see the reader).

local TRADE_LISTS = {
    player = { "PlayerInventory", "CharacterInventory", "PartyInventory" },
    trader = { "TraderInventory", "CurrentTradingCharacter.Inventory", "TraderCharacter.Inventory" },
    playerOffer = { "CharacterOffer", "PlayerOffer" },
    traderOffer = { "TraderOffer" },
}

local function tradeList(data, names)
    for _, name in ipairs(names) do
        local inventory = path(data, name)
        if inventory and get(inventory, "Slots") then return slotsOf(inventory), name end
    end
    return nil
end

W.trade = W.trade or {}
W.trade.widget = "Trade"
W.trade.blocking = true
W.trade.policy = "trade"
W.trade.about = "trading or bartering with a trader"
W.trade.read = reader(function(data)
    local out = { mode = plain(property(data, "Mode")) or plain(get(data, "Mode")) }
    out.trader = characterName(get(data, "CurrentTradingCharacter")) or characterName(get(data, "TraderCharacter"))
    out.character = characterName(get(data, "PlayerTraderCharacter"))
    out.playerGold = num(path(data, "PlayerTraderCharacter.Inventory.Gold"))
    out.traderGold = num(path(data, "CurrentTradingCharacter.Inventory.Gold"))
    out.offerGold = num(get(data, "CharacterOfferGold"))
    out.traderOfferGold = num(get(data, "TraderOfferGold"))
    out.priceModifier = num(get(data, "PriceModifierPercentage"))
    for key, names in pairs(TRADE_LISTS) do
        local items, from = tradeList(data, names)
        if items then out[key] = { items = items, from = from } end
    end
    return out
end)
W.trade.step = stepper(function(ui)
    local data = ui.Data("Trade")
    if not data then return { done = true, did = "closed" } end
    local result = runOn(data, "CustomEvent", "CloseWidget")
    result.did = "close trade"
    result.wait = 600
    return result
end)
W.trade.close = function(ui)
    local result = runOn(ui.Data("Trade"), "CustomEvent", "CloseWidget")
    return result.ok, result.error
end

--- Trade actions: { move = n | "name", side = "player" (default) | "trader" | "playerOffer" |
--- "traderOffer" } moves one item across; { balance = true }; { finish = true } (trade, or barter
--- when options.barter); several in one call run in that order: move, balance, finish.
function UI.Trade(options)
    options = options or {}
    local data = UI.Data("Trade")
    if not data then return { ok = false, error = "no trade window" } end
    local log = {}
    if options.move ~= nil then
        local names = TRADE_LISTS[options.side or "player"]
        if not names then return { ok = false, error = "no side " .. tostring(options.side) } end
        local objects
        for _, name in ipairs(names) do
            local inventory = path(data, name)
            if inventory and get(inventory, "Slots") then
                local _, list = slotsOf(inventory)
                objects = list
                break
            end
        end
        if not objects then return { ok = false, error = "cannot read the " .. tostring(options.side or "player") .. " side" } end
        local index = options.move
        if type(index) == "string" then
            for i, object in ipairs(objects) do
                if lower(text(get(object, "Name"))):find(lower(options.move), 1, true) then index = i break end
            end
        end
        local object = type(index) == "number" and objects[index] or nil
        if not object then return { ok = false, error = "no item " .. tostring(options.move) } end
        local result = runOn(data, "MoveTradeItem", object)
        log[#log + 1] = result
        if not result.ok then return { ok = false, error = result.error, log = log } end
    end
    if options.balance then log[#log + 1] = runOn(UI.Data("Trade"), "BalanceTrade") end
    if options.finish then
        local d = UI.Data("Trade")
        if options.barter then
            log[#log + 1] = runOn(d, "FinishBarter")
        else
            log[#log + 1] = runOn(d, "FinishTrade")
        end
    end
    for _, entry in ipairs(log) do
        if not entry.ok then return { ok = false, error = entry.error, log = log } end
    end
    return { ok = true, log = log }
end

-- pickpocket: Pickpocket.xaml -----------------------------------------------------------------
-- Container.Inventory.Slots (the victim's items), Chance, DifficultyCheck, CanSteal, Finished,
-- TakeItemCommand(item) (Steal), CloseWidgetCommand (Close).

W.pickpocket = W.pickpocket or {}
W.pickpocket.widget = "Pickpocket"
W.pickpocket.blocking = true
W.pickpocket.policy = "pickpocket"
W.pickpocket.about = "pickpocketing a character"
-- The victim's items as the window lists them: Container.Inventory.Slots (what can be stolen;
-- equipped things live in the other Container.Inventories and are not listed). Empty when the
-- victim carries nothing but its equipment.
local function victimItems(data)
    local items, objects = {}, {}
    local lists = { path(data, "Container.Inventory") }
    for _, inventory in ipairs(lists) do
        for i = 1, count(get(inventory, "Slots")) do
            -- the window's list items are the SLOTS (its commands take a slot, not the VMItem)
            local slot = at(get(inventory, "Slots"), i)
            local object = get(slot, "Object")
            if object then
                objects[#objects + 1] = slot
                if #items < CAP then items[#items + 1] = itemSummary(object, #objects) end
            end
        end
    end
    return items, objects
end

W.pickpocket.read = reader(function(data)
    local items = victimItems(data)
    local roll = get(data, "Roll")
    return { victim = characterName(get(data, "Container")), items = items, canSteal = get(data, "CanSteal"),
             check = clean(get(roll, "RollDescription")), chance = num(get(roll, "Chance")),
             dc = num(get(roll, "DifficultyCheck")), finished = get(roll, "Finished"),
             success = get(roll, "Finished") == true and get(roll, "Success") or nil, rolled = num(get(roll, "RolledNumber")) }
end)
W.pickpocket.step = stepper(function(ui, options)
    local data = ui.Data("Pickpocket")
    if not data then return { done = true, did = "closed" } end
    if options.item then
        local _, objects = victimItems(data)
        local index = options.item
        if type(index) == "string" then
            for i, slot in ipairs(objects) do
                if lower(text(path(slot, "Object.Name"))):find(lower(index), 1, true) then index = i break end
            end
        end
        local object = type(index) == "number" and objects[index] or nil
        if not object then return { ok = false, error = "no item " .. tostring(options.item) } end
        -- a click on the item selects it (SelectItemCommand: the chance appears), Steal takes it
        local chose = runOn(data, "SelectItemCommand", object)
        local result = runOn(data, "TakeItemCommand", object)
        result.selected = chose.ok
        result.did = "steal " .. tostring(index)
        result.wait = 1500
        return result
    end
    local result = runOn(data, "CloseWidgetCommand")
    result.did = "close"
    result.wait = 400
    return result
end)
W.pickpocket.close = function(ui)
    local result = runOn(ui.Data("Pickpocket"), "CloseWidgetCommand")
    return result.ok, result.error
end

-- rest: MakeCamp.xaml (the supplies screen of a long rest) and LongRest.xaml (an empty widget
-- that eats input while the rest runs) -----------------------------------------------------------
-- PartyCampSupplies.Slots, SelectedSuppliesAmount, CurrentPlayer.RequiredPartySupplies,
-- AutoSelectCommand(SuppliesView.SelectedItems), ProcessRestCommand, CancelCampCommand. The rest
-- itself is asked for by the HUD's CampTravel (GK.Gui.LongRest).

W.rest = W.rest or {}
W.rest.widget = "MakeCamp"
W.rest.blocking = true
W.rest.policy = "rest"
W.rest.about = "the long-rest supplies screen"
W.rest.open = function(ui)
    local hotbar = ui.Data("HotBar")
    if not hotbar then return false, "no HotBar" end
    local result = runOn(hotbar, "CampTravel")
    return result.ok, result.error
end
W.rest.read = reader(function(data)
    local supplies = slotsOf(get(data, "PartyCampSupplies"))
    return { selected = num(get(data, "SelectedSuppliesAmount")), required = num(path(data, "CurrentPlayer.RequiredPartySupplies")),
             supplies = supplies, initiator = get(data, "IsInitiator") }
end)
W.rest.step = stepper(function(ui, options)
    local data = ui.Data("MakeCamp")
    if not data then return { done = true, did = "closed" } end
    if choiceOf(options) == "cancel" then
        local result = runOn(data, "CancelCampCommand")
        result.did = "cancel rest"
        return result
    end
    local chosen = num(get(data, "SelectedSuppliesAmount")) or 0
    local required = num(path(data, "CurrentPlayer.RequiredPartySupplies")) or 0
    if chosen < required and not options.selected then
        local list = ui.Find("MakeCamp", "SuppliesView")
        local items = list and guard(function() return list.SelectedItems end)
        local result = runOn(data, "AutoSelectCommand", items)
        result.did = "auto-select supplies"
        result.wait = 400
        options.selected = true
        if result.ok and (num(get(ui.Data("MakeCamp"), "SelectedSuppliesAmount")) or 0) > chosen then return result end
        if not result.ok then return result end
    end
    if chosen < required and not options.partial then
        return { ok = false, stuck = true, error = "only " .. chosen .. "/" .. required .. " supplies: a partial rest (pass partial = true)" }
    end
    local result = runOn(data, "ProcessRestCommand")
    result.did = "rest (" .. chosen .. "/" .. required .. " supplies)"
    result.wait = 1500
    return result
end)
W.rest.close = function(ui)
    local result = runOn(ui.Data("MakeCamp"), "CancelCampCommand")
    return result.ok, result.error
end

W.resting = W.resting or { widget = "LongRest", about = "a long rest running (an empty widget eating input)" }
W.resting.read = reader(function() return { resting = true } end)

--- Asks for a long rest as the HUD's Long Rest button does (CampTravel): the supplies screen,
--- or an "End the day?" box first.
function UI.LongRest()
    local hotbar = UI.Data("HotBar")
    if not hotbar then return { ok = false, error = "no HotBar" } end
    local out = runOn(hotbar, "CampTravel")
    out.endTheDay = plain(path(hotbar, "CurrentPlayer.EndTheDayState"))
    return out
end

-- reward: RewardPanel.xaml --------------------------------------------------------------------
-- QuestName, QuestDescription, MandatoryItems.Slots, OptionalItems -> {Object (VMItem)},
-- ReceiverCharacter.Name, ToggleOptionalItemCommand(item), GetRewardsAndClose.ActionCommand (Accept).

W.reward = W.reward or {}
W.reward.widget = "RewardPanel"
W.reward.blocking = true
W.reward.policy = "reward"
W.reward.about = "a quest reward to accept (and maybe one item to choose)"
W.reward.read = reader(function(data)
    local optional = each(get(data, "OptionalItems"), function(entry, i)
        local summary = itemSummary(get(entry, "Object") or entry, i)
        if path(entry, "Object.IsSelected") == true or get(entry, "IsSelected") == true then summary.selected = true end
        return summary
    end, 12)
    return { quest = clean(get(data, "QuestName")), text = clean(get(data, "QuestDescription")),
             receiver = characterName(get(data, "ReceiverCharacter")),
             items = (slotsOf(get(data, "MandatoryItems"))), optional = optional,
             chosen = count(get(data, "SelectedItems")) }
end)
W.reward.step = stepper(function(ui, options)
    local data = ui.Data("RewardPanel")
    if not data then return { done = true, did = "closed" } end
    local optional = get(data, "OptionalItems")
    local want = options.item or (type(choiceOf(options)) == "number" and choiceOf(options)) or nil
    if count(optional) > 0 and count(get(data, "SelectedItems")) == 0 then
        local entry = at(optional, want or 1)
        local result = runOn(data, "ToggleOptionalItemCommand", get(entry, "Object") or entry)
        if not result.ok then result = runOn(data, "ToggleOptionalItemCommand", entry) end
        result.did = "choose reward " .. tostring(want or 1)
        result.wait = 300
        return result
    end
    local result = run(path(data, "GetRewardsAndClose.ActionCommand"), nil, "GetRewardsAndClose")
    result.did = "accept rewards"
    result.wait = 600
    return result
end)

-- learn: LearnSpells.xaml (DCLearnSpells) -----------------------------------------------------
-- LearnableScrolls -> {Name, LearnCost, SpellLevel, IsAlreadyKnown, HasRequiredLevel}, UserGold;
-- Learn: TranscribeScrollsCommand(ScrollList.SelectedItems); close: CustomEvent("CloseLearnSpells").

W.learn = W.learn or {}
W.learn.widget = "LearnSpells"
W.learn.blocking = true
W.learn.policy = "learn"
W.learn.about = "learning spells from scrolls (wizard)"
W.learn.read = reader(function(data)
    local scrolls = each(get(data, "LearnableScrolls"), function(scroll, i)
        local entry = { n = i, name = clean(get(scroll, "Name")) or "?", cost = num(get(scroll, "LearnCost")),
                        level = num(get(scroll, "SpellLevel")) }
        if get(scroll, "IsAlreadyKnown") == true then entry.known = true end
        if get(scroll, "HasRequiredLevel") == false then entry.tooHigh = true end
        return entry
    end)
    return { scrolls = scrolls, gold = num(get(data, "UserGold")) }
end)
W.learn.step = stepper(function(ui, options)
    local data = ui.Data("LearnSpells")
    if not data then return { done = true, did = "closed" } end
    if choiceOf(options) == "learn" or options.scroll then
        local index = options.scroll or 1
        local picked = ui.Select("LearnSpells", "ScrollList", index)
        if not picked.ok then return picked end
        local result = ui.Click("LearnSpells", "TranscribeButton")
        result.did = "learn scroll " .. tostring(index)
        result.wait = 800
        return result
    end
    local result = runOn(data, "CustomEvent", "CloseLearnSpells")
    result.did = "close"
    return result
end)
W.learn.close = function(ui)
    local result = runOn(ui.Data("LearnSpells"), "CustomEvent", "CloseLearnSpells")
    return result.ok, result.error
end

-- split: SplitItem.xaml (CurrentPlayer.UIData.SplitItemData: Item.Name, ItemCount, TargetCount;
-- Confirm: ApplySplitCommand; the widget's CustomEvent("CloseWidget") cancels) ------------------

local function splitData(data)
    return path(data, "CurrentPlayer.UIData.SplitItemData") or data
end

W.split = W.split or {}
W.split.widget = "SplitItem"
W.split.blocking = true
W.split.policy = "split"
W.split.about = "how many of a stack to split off"
W.split.read = reader(function(data)
    local split = splitData(data)
    return { item = clean(path(split, "Item.Name")), count = num(get(split, "ItemCount")), amount = num(get(split, "TargetCount")) }
end)
W.split.step = stepper(function(ui, options)
    local data = ui.Data("SplitItem")
    if not data then return { done = true, did = "closed" } end
    if choiceOf(options) == "cancel" then
        local result = runOn(data, "CustomEvent", "CloseWidget")
        result.did = "cancel split"
        return result
    end
    local split = splitData(data)
    if options.amount then guard(function() split.TargetCount = options.amount end) end
    local result = runOn(data, "ApplySplitCommand")
    if not result.ok then result = runOn(split, "ApplySplitCommand") end
    result.did = "split " .. tostring(num(get(split, "TargetCount")))
    result.wait = 400
    return result
end)

-- book: Book.xaml (BookFullText, BookType, Edition; close: CustomEvent("CloseWidget")) -------------

W.book = W.book or {}
W.book.widget = "Book"
W.book.blocking = true
W.book.policy = "book"
W.book.about = "a book, note or letter being read"
W.book.read = reader(function(data)
    local full = clean(get(data, "BookFullText"))
    return { text = full and full:sub(1, 4000) or nil, length = full and #full or 0, kind = plain(get(data, "BookType")),
             title = clean(get(data, "Name")) or clean(get(data, "Title")) }
end)
W.book.step = stepper(function(ui)
    local data = ui.Data("Book")
    if not data then return { done = true, did = "closed" } end
    local result = runOn(data, "CustomEvent", "CloseWidget")
    result.did = "close book"
    return result
end)

-- game menu, save, load ---------------------------------------------------------------------

W.gamemenu.blocking = true
W.gamemenu.policy = "gamemenu"
W.gamemenu.read = reader(function(_data, ui)
    local rows = ui.Controls("GameMenu")
    local buttons = {}
    for _, row in ipairs(rows.controls or {}) do
        if row.name and row.name:find("Button", 1, true) then buttons[#buttons + 1] = { name = row.name, text = row.text, enabled = row.enabled } end
    end
    return { buttons = buttons }
end)
W.gamemenu.step = stepper(function(ui)
    if not ui.IsOpen("GameMenu") then return { done = true, did = "closed" } end
    local result = ui.Event("CloseGameMenu", "GameMenu")
    result.did = "resume"
    result.wait = 400
    return result
end)

-- DCSavegames: ExistingPlaythroughs -> VMPlaythroughHolder {ProtagonistName, PlaythroughString,
-- Saves, LatestSave}; a save (VMSavegame): Title, Type (QuickSave / AutoSave / Manual ...),
-- TimeString, LevelName, PlayTimeString, Validity. SelectedSave / SelectedPlaythrough; DoLoad.
local function saveSummary(save)
    if save == nil then return nil end
    return { title = text(get(save, "Title")), kind = plain(property(save, "Type")), time = text(get(save, "TimeString")),
             level = text(get(save, "LevelName")), played = text(get(save, "PlayTimeString")), valid = plain(get(save, "Validity")) }
end

local function savesOf(data)
    return (each(get(data, "ExistingPlaythroughs"), function(playthrough)
        return { hero = text(get(playthrough, "ProtagonistName")), id = plain(get(playthrough, "PlaythroughString")),
                 latest = saveSummary(get(playthrough, "LatestSave")), saves = (each(get(playthrough, "Saves"), saveSummary, 5)),
                 count = count(get(playthrough, "Saves")) }
    end, 10))
end

for _, key in ipairs({ "save", "load" }) do
    local window = W[key]
    window.blocking = true
    window.policy = key
    window.read = reader(function(data)
        return { playthroughs = savesOf(data), selected = saveSummary(get(data, "SelectedSave")),
                 saving = get(data, "IsSaving") == true }
    end)
    window.step = stepper(function(ui)
        if not ui.IsOpen(window.widget) then return { done = true, did = "closed" } end
        -- the window's own Cancel button: CustomEvent("CloseWidget") (LoadGameClose did not close
        -- the load window in game, 2026-11-13)
        -- (the catalogue's chain for it fails: DCSavegames has no CustomEvent; the live
        -- control's own Command is the widget's, found through the visual tree)
        local result = pressLive(window.widget, "CloseBtn")
        if not result.ok then result = pressLive(window.widget, "CloseButton") end
        if not result.ok then result = ui.Close(key) end
        result.did = "close"
        result.wait = 400
        return result
    end)
end

W.savename = W.savename or { widget = "SaveGamePopup", blocking = true, policy = "save", about = "the name of a new save" }
W.savename.read = reader(function(data)
    return { name = get(data, "SavegameNameInput"), feedback = plain(get(data, "FeedbackState")) }
end)
W.savename.step = stepper(function(ui, options)
    local data = ui.Data("SaveGamePopup")
    if not data then return { done = true, did = "closed" } end
    if options.name then
        local result = runOn(data, "DoSaveWithName", tostring(options.name))
        result.did = "save as " .. tostring(options.name)
        result.wait = 2000
        return result
    end
    local result = runOn(data, "CustomEvent", "CloseMessageBox")
    result.did = "cancel"
    return result
end)

-- A game command of any widget's data (QuickSaveCommand ...): the HotBar's, else the first.
local function gameCommand(name, parameter)
    for _, widget in ipairs({ "HotBar", "Minimap", "PlayerPortraits", "Overlay" }) do
        local data = UI.Data(widget)
        local command = data and get(data, name)
        if command then return run(command, parameter, name) end
    end
    for _, w in ipairs(UI.Open()) do
        local data = UI.Data(w.name)
        local command = data and get(data, name)
        if command then return run(command, parameter, name) end
    end
    return { ok = false, error = "no widget has " .. name }
end

function UI.QuickSave() return gameCommand("QuickSaveCommand") end
function UI.QuickLoad() return gameCommand("QuickLoadCommand") end

--- A named save: opens the save window's name box (OpenNewSaveDialog) when it is not open yet;
--- call again when the box is up ({ ok, waiting = true } until then).
function UI.SaveAs(name)
    name = tostring(name or "GateKit")
    local data = UI.Data("SaveGamePopup")
    if data then
        local result = runOn(data, "DoSaveWithName", name)
        result.saved = name
        return result
    end
    if not UI.IsOpen("SaveGame") then
        local opened = gameCommand("OpenSaveGameDialogCommand")
        if not opened.ok then opened = UI.Event("OpenNewSaveDialog") end
        opened.waiting = true
        opened.next = "the save window opens; call SaveAs again"
        return opened
    end
    local result = UI.Event("OpenNewSaveDialog", "SaveGame")
    result.waiting = true
    result.next = "the name box opens; call SaveAs again"
    return result
end

--- Loads the newest save: opens the load window first ({ waiting = true }), then selects the
--- newest playthrough's newest save and runs DoLoad.
function UI.LoadLatest()
    local data = UI.Data("LoadGame")
    if not data then
        local result = UI.Event("LoadGameOpen")
        result.waiting = true
        result.next = "the load window opens; call LoadLatest again"
        return result
    end
    -- the newest of ExistingSaves (not sorted) by TimeString "dd/mm/yyyy hh:mm"; on a tie the
    -- later one in the list. A save made a moment ago appears only when the window is opened
    -- after it (the list is read on open).
    local best, bestKey
    each(get(data, "ExistingSaves"), function(save)
        local d, m, y, hh, mm = tostring(text(get(save, "TimeString")) or ""):match("(%d+)/(%d+)/(%d+)%s+(%d+):(%d+)")
        local key = d and tonumber(y .. m .. d .. hh .. mm) or 0
        if not bestKey or key >= bestKey then best, bestKey = save, key end
        return true
    end)
    if not best then return { ok = false, error = "no saves" } end
    local _, problem = guard(function() data.SelectedSave = best end)
    if problem then return { ok = false, error = "could not select the save: " .. problem } end
    -- DoLoad is not on DCSavegames: the Load button's live command runs it
    local result = pressLive("LoadGame", "LoadSaveBtn")
    result.save = saveSummary(best)
    return result
end

-- Blocking and Resolve ------------------------------------------------------------------------

-- Registry order is not z-order: the open widgets are walked (top-most last) and the last one
-- whose window blocks wins.
local function blockingWindows()
    local byWidget = {}
    for name, window in pairs(W) do
        if window.blocking and window.widget and not byWidget[window.widget] then byWidget[window.widget] = name end
    end
    -- a window of the registry under a second name (quests = journal) must not shadow the main one
    if byWidget.Dialogue ~= "dialogue" then byWidget.Dialogue = "dialogue" end
    if byWidget.TutorialPopup ~= "sidetutorial" then byWidget.TutorialPopup = "sidetutorial" end
    local found = {}
    for _, w in ipairs(UI.Open()) do
        local name = byWidget[w.name]
        if name then
            local window = W[name]
            local element = UI.Widget(w.name)
            local active = shown(element)
            if active and window.active then
                local data = element and guard(function() return element.DataContext end)
                active = guard(window.active, data) == true
            end
            if active then found[#found + 1] = { window = name, widget = w.name } end
        end
    end
    return found
end

-- What a step can be told, per window: shown to the caller as `choices`.
local CHOICES = {
    dialogue = "answer = n | text; choice = 'last' | 'first' | n; skip = false to wait for a line",
    roll = "choice = 'throw' | 'reroll' (inspiration on a failure)",
    reaction = "choice = 'decline' | 'react' | n | name",
    box = "button = n | text; choice = 'affirmative' | 'negative'; never a game-over box",
    tutorial = "choice = 'close' | 'next'",
    sidetutorial = "close",
    loot = "choice = 'takeall' | 'close'; item = n | name",
    trade = "close; GK.Gui.Trade{ move, side, balance, finish }",
    pickpocket = "item = n | name (steal); else close",
    rest = "choice = 'rest' | 'cancel'; partial = true",
    reward = "item = n (an optional reward), then accept",
    learn = "choice = 'close' | 'learn'; scroll = n",
    split = "choice = 'confirm' | 'cancel'; amount = n",
    book = "close",
    gamemenu = "resume",
    save = "close", load = "close", savename = "name = '...' or cancel",
}

--- The top-most window that waits for the player right now (see the head of this file).
function UI.Blocking()
    local found = blockingWindows()
    local top = found[#found]
    if not top then return { ok = true, all = found } end
    local window = W[top.window]
    local summary
    if window.read then
        local data = UI.Data(top.widget)
        summary = data and guard(window.read, data, UI) or nil
    end
    return { ok = true, window = top.window, widget = top.widget, summary = summary, choices = CHOICES[top.window],
             policy = window.policy, all = found }
end

-- The default policy: what a player in a hurry does.
UI.DefaultPolicy = UI.DefaultPolicy or {
    dialogue = "last", roll = "throw", reaction = "decline", box = "affirmative", tutorial = "close",
    loot = "takeall", trade = "close", pickpocket = "close", rest = "rest", reward = "accept", learn = "close",
    split = "confirm", book = "close", gamemenu = "resume", save = "close", load = "close",
}

--- ONE step through whatever blocks, by `policy` (fields missing from it come from
--- GK.Gui.DefaultPolicy). Returns the step's result with `window`; { ok, nothing = true } when
--- nothing blocks.
function UI.Resolve(policy)
    policy = policy or {}
    local found = blockingWindows()
    local top = found[#found]
    if not top then return { ok = true, nothing = true } end
    local window = W[top.window]
    local key = window.policy or top.window
    local wanted = policy[key]
    if wanted == nil then wanted = UI.DefaultPolicy[key] end
    local options = type(wanted) == "table" and wanted or { choice = wanted }
    if wanted == "skip" or wanted == "leave" then
        return { ok = true, window = top.window, did = "left for the caller (policy " .. tostring(wanted) .. ")", stuck = true }
    end
    local result = UI.Step(top.window, options)
    result.window = top.window
    result.widget = top.widget
    return result
end

return true
