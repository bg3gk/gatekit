-- Popups: the windows that stop the game until the player answers - a dialogue, a dice roll, a
-- reaction prompt, a message box, a tutorial, loot, trade, rest, rewards ... - read and resolved
-- from code through GK.Gui (Client/Windows.lua). UI side (`ui` in `gatekit`).
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
local Cheat = GK.Cheat

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
    -- as GK.Cheat.Run: a command the game's CanExecute turns down is said, not run "ok" (N26)
    local known, can = pcall(function() return command:CanExecute(parameter) end)
    if known and can == false then
        return { ok = false, error = tostring(label) .. " cannot run now: the game's CanExecute says no (nothing was done)" }
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

local function lower(value) return GK.Fold(value) end

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

--- Where the dialogue on screen stands for the player: { phase = "choice" | "continue" | "line",
--- answers, speaker, text, instance, canTrade, set } or nil. `set` names the answers on offer, so
--- a new set in the same dialogue reads as a new choice. GK.Attention records each choice.
function UI.DialoguePhase()
    local data = UI.Data("Dialogue")
    if not data then return nil end
    local r = W.dialogue.read(data, UI)
    if type(r) ~= "table" then return nil end
    local phase = r.state == "answers" and "choice" or (r.state == "continue" and "continue" or "line")
    local texts = {}
    for _, a in ipairs(r.answers or {}) do texts[#texts + 1] = tostring(a.n) .. ":" .. tostring(a.text) end
    return { phase = phase, answers = phase == "choice" and r.answers or nil, speaker = r.speaker, text = r.text,
             instance = r.instance, canTrade = r.canTrade,
             set = tostring(r.instance) .. "|" .. phase .. "|" .. table.concat(texts, "|") .. (phase == "continue" and ("|" .. tostring(r.text)) or "") }
end

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
    -- answers already in the list but not shown yet: they come in a moment - a skip now would be
    -- reported as progress while a choice is what is pending
    if not showAnswers and #dialogueAnswers(ad) > 0 then
        return { did = "waiting for the answers to show", pending = "choice", wait = 200 }
    end
    -- a line being spoken (or a cinematic moment): skip it as the continue key does
    if options.skip == false then return { did = "waiting for the line", pending = "line", wait = 500 } end
    local result = runOn(data, "SelectorSkipCommand", num(get(data, "PlayerId")) or 1)
    result.did = "skip line"
    result.pending = "line"
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

-- One offered or added bonus (an ls.VMBoostModifier): the spell or item that gives it
-- (BoostModifier: its Name, PrototypeID), its die (DiceTypeSet.Str), the range it adds
-- (MinBonusValue..MaxBonusValue - one value once its die is rolled), who gives it (Owner, a
-- character's view model), IsSelected.
local function bonusOf(vm)
    local source = get(vm, "BoostModifier")
    local owner = get(vm, "Owner")
    local low, high = num(get(vm, "MinBonusValue")), num(get(vm, "MaxBonusValue"))
    local name = clean(get(vm, "Name")) or clean(get(vm, "Description")) or clean(get(vm, "Title")) or clean(get(source, "Name"))
    local value = get(vm, "Value")
    if type(value) ~= "number" and low and high then value = (low == high) and low or { low, high } end
    return { name = name, spell = plain(get(source, "PrototypeID")), dice = clean(get(get(vm, "DiceTypeSet"), "Str")),
             value = type(value) == "number" and num(value) or value, by = plain(get(owner, "EntityUUID")),
             selected = get(vm, "IsSelected") == true or nil, off = get(vm, "IsDisabled") == true or nil }
end

local function rollRead(data)
    local out = { state = plain(get(data, "RollState")) or "?" }
    -- the bonuses added to this roll (SelectedBoostModifierList): their die's value once thrown
    local selected = get(data, "SelectedBoostModifierList")
    for i = 1, count(selected) do
        local vm = at(selected, i)
        if vm ~= nil then
            out.added = out.added or {}
            local b = bonusOf(vm)
            out.added[#out.added + 1] = { name = b.name, spell = b.spell, dice = b.dice, value = b.value, by = b.by }
        end
    end
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
    -- who rolls: the window's SourceObject is the roller's character view model
    local source = get(data, "SourceObject")
    local who = source and get(source, "EntityUUID")
    out.who = type(who) == "string" and who ~= "" and who or nil
    if get(data, "IsPureAbilityRoll") == true then out.pure = true end
    -- the natural die a roll needs to pass (the difficulty less the bonus)
    local target = num(get(data, "TargetNumber"))
    if target and target > 0 then out.target = target end
    -- the dice show from StartRoll on: RolledNumber1 (and RolledNumber2 with advantage or
    -- disadvantage) are the natural dice, FinalResult the die that counts. ResultNumber counts up
    -- to the total while the window animates, so the total is the die and the listed bonus.
    local first, second = num(get(data, "RolledNumber1")), num(get(data, "RolledNumber2"))
    if first and first > 0 then out.dice = (second and second > 0) and { first, second } or { first } end
    if out.state == "ResultReady" or out.state == "WaitForReRoll" then
        out.result = num(get(data, "FinalResult"))
        out.natural = out.result
        if out.natural and type(out.bonus) == "number" then out.total = out.natural + out.bonus end
        out.success = get(data, "Success")
        if type(out.success) ~= "boolean" then out.success = nil end
        -- a natural 20 or 1 on the d20 decides the check whatever the total (the game's rule)
        if out.natural == 20 then out.critical = "success" elseif out.natural == 1 then out.critical = "failure" end
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
        -- what the roll came to, read back before the window goes: the total, the DC, the bonuses
        local read = rollRead(data)
        local result = runOn(data, "ContinueCommand")
        if not result.ok then result = runOn(data, "UserTriggeredNextPhaseCommand", true) end
        result.result, result.success, result.dc = read.result, read.success, read.dc
        result.check, result.modifiers, result.boosts, result.advantage = read.check, read.modifiers, read.boosts, read.advantage
        result.did = "continue (result " .. tostring(num(get(data, "FinalResult"))) .. ", "
            .. (get(data, "Success") == true and "success" or "failure") .. ")"
        result.wait = 800
        return result
    end
    -- IntroductionAnimation, StartRoll, StopRoll: the dice are moving. A player's click (UIAccept)
    -- skips the animation: ActiveRoll.xaml binds it to UserTriggeredNextPhaseCommand(true) ("Skip
    -- on Click"), and the result shows at once with the game's own "_Skipped" sound - the roll is
    -- the game's, only the animation is cut. The binding is off for 0.2 s after each state change,
    -- as for a player. options.watch = true lets the animation play.
    if not options.watch and (state == "StartRoll" or state == "StopRoll" or state == "IntroductionAnimation") then
        local result = runOn(data, "UserTriggeredNextPhaseCommand", true)
        result.did = "skip the animation (" .. state .. ")"
        result.wait = 250
        return result
    end
    return { did = "waiting (" .. state .. ")", wait = 500 }
end)
--- The bonuses the roll window offers to add (Guidance, Bardic Inspiration ...): the roller's own
--- (BoostModifierList) and each nearby party member's (NearbyPartyCharacters[i].BoostModifierList;
--- the roller is one of them). The window's AddBonusButton has no command of its own; what adds
--- one is the window's AddModifierCommand with the offered modifier. Rows: { n, name, spell, dice,
--- value, by, from, selected, off }.
local function rollOffers(data)
    local out = {}
    local function add(list, from)
        for i = 1, count(list) do
            local vm = at(list, i)
            if vm ~= nil then
                local row = bonusOf(vm)
                row.n, row.from, row.vm = #out + 1, from, vm
                out[#out + 1] = row
            end
        end
    end
    add(get(data, "BoostModifierList"), "the roller")
    local nearby = get(data, "NearbyPartyCharacters")
    for i = 1, count(nearby) do
        local character = at(nearby, i)
        add(get(character, "BoostModifierList"), clean(get(character, "Name")) or ("party member " .. i))
    end
    return out
end

function UI.RollBonuses()
    local data = UI.Data("ActiveRoll")
    if not data then return { ok = false, error = "no roll window is open" } end
    local rows = {}
    for _, row in ipairs(rollOffers(data)) do
        rows[#rows + 1] = { n = row.n, name = row.name, spell = row.spell, dice = row.dice, value = row.value, by = row.by,
                            from = row.from, selected = row.selected, off = row.off }
    end
    return { ok = true, bonuses = rows }
end

--- Adds offered bonus `which` (its n in GK.Gui.RollBonuses, or part of its name) to the roll.
function UI.AddRollBonus(which)
    local data = UI.Data("ActiveRoll")
    if not data then return { ok = false, error = "no roll window is open" } end
    local offers = rollOffers(data)
    local chosen
    for _, row in ipairs(offers) do
        if row.n == which or (type(which) == "string" and ((row.name and row.name:lower():find(which:lower(), 1, true)) or row.spell == which)) then
            chosen = row
            break
        end
    end
    if not chosen then
        local names = {}
        for _, row in ipairs(offers) do names[#names + 1] = row.n .. " " .. tostring(row.name) end
        return { ok = false, error = "no offered bonus " .. tostring(which) .. "; offered: " .. (#names > 0 and table.concat(names, ", ") or "none") }
    end
    local result = runOn(data, "AddModifierCommand", chosen.vm)
    result.added = chosen.name
    result.from = chosen.from
    return result
end

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
-- (names found live; see the reader). The player's side is PlayerTraderCharacter.Inventory, read
-- live with Dammon in WLD_Main_A; the three names after it were guesses that the Trade data does
-- not have, and the player side could not be read at all while they were the only ones.
-- Finishing presses the window's own TradeButton, as the player does: the view model's
-- FinishTrade ran without error and committed nothing.

local TRADE_LISTS = {
    player = { "PlayerTraderCharacter.Inventory", "PlayerInventory", "CharacterInventory", "PartyInventory" },
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
    out.trader = characterName(get(data, "Trader")) or characterName(get(data, "CurrentTradingCharacter")) or characterName(get(data, "TraderCharacter"))
    out.character = characterName(get(data, "PlayerTraderCharacter"))
    out.playerGold = num(path(data, "PlayerTraderCharacter.Inventory.Gold"))
    -- the trader is `Trader`; CurrentTradingCharacter is the party member trading (seen 2026-11-14)
    out.traderGold = num(path(data, "Trader.Inventory.Gold")) or num(path(data, "CurrentTradingCharacter.Inventory.Gold"))
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

-- The numbers and flags of the trade's view model, for saying why it will not finish.
local function tradeState(data)
    local out = {}
    local ok, properties = pcall(function() return data:GetAllProperties() end)
    if not ok or properties == nil then return { error = "the trade's properties cannot be read" } end
    for key, value in pairs(properties) do
        local kind = type(value)
        if kind == "boolean" or kind == "number" or (kind == "string" and #value < 80) then out[tostring(key)] = value end
    end
    out.playerGold = num(path(data, "PlayerTraderCharacter.Inventory.Gold"))
    -- the trader is `Trader`; CurrentTradingCharacter is the party member trading (seen 2026-11-14)
    out.traderGold = num(path(data, "Trader.Inventory.Gold")) or num(path(data, "CurrentTradingCharacter.Inventory.Gold"))
    return out
end

-- How many of an item (by its name as the window shows it) a side holds: stacks counted by Count.
local function heldOf(data, from, name)
    local total = 0
    local _, objects = slotsOf(path(data, from))
    for _, object in ipairs(objects or {}) do
        if text(get(object, "Name")) == name then total = total + (num(get(object, "Count")) or 1) end
    end
    return total
end

-- A move shows in the window when the game's frames get to it: seen live (N43 of the third run)
-- about 3 s later at a frame cap of 10. Until this long after the move, an item still on its side
-- is "not shown yet", not "refused".
local TRADE_SHOW_MS = 8000
local TRADE_MODES = { trade = 0, barter = 1 }

--- Trade actions: { mode = "trade" | "barter" } sets the window's mode (the view model's
--- SetTradeMode: 0 Trade, 1 Barter - seen live, N42); { move = n | "name", side = "player"
--- (default) | "trader" | "playerOffer" | "traderOffer" } moves one item across; { balance = true };
--- { finish = true } presses the button the window shows, as the player does - TradeButton, or
--- BarterBtn in barter mode (options.barter forces it) - and when the game keeps it disabled says
--- so with the window's numbers and flags in `state`; several in one call run in that order: mode,
--- move, balance, finish.
function UI.Trade(options)
    options = options or {}
    local data = UI.Data("Trade")
    if not data then return { ok = false, error = "no trade window" } end
    local log = {}
    -- Whether the last move reached the other side. A move shows in the window only when the game
    -- gets to it (seconds at a low frame rate), so a later call tells; an item the trader will not
    -- take (seen live: a healing potion) answered ok and stayed where it was, and a stack opens the
    -- game's SplitItem box (how many?) and moves nothing until that box is answered (N41).
    local lastMove
    if UI.PendingTradeMove then
        local pending = UI.PendingTradeMove
        local waited = Ext.Utils.MonotonicTime() - pending.at
        lastMove = { item = pending.name, waitedMs = waited }
        if heldOf(data, pending.from, pending.name) < pending.count then
            lastMove.reached = true
            UI.PendingTradeMove = nil
        elseif UI.IsOpen("SplitItem") then
            lastMove.reached = false
            lastMove.waiting = "SplitItem"
            lastMove.why = "the game's SplitItem box asks how many of the stack to move; nothing moves until it is "
                .. "answered: GK.Gui.Resolve{ split = { amount = n } } - or { split = \"cancel\" }"
        elseif waited < TRADE_SHOW_MS then
            lastMove.reached = "not shown yet"
            lastMove.why = "the window has not shown the move yet (seen live: about 3 s at 10 fps); call GK.Gui.Trade{} again"
        else
            lastMove.reached = false
            lastMove.why = "it is still on its side " .. math.floor(waited / 1000) .. " s later: the other party does not "
                .. "take it (or it is not tradable)"
            UI.PendingTradeMove = nil
        end
    end
    if options.mode ~= nil then
        local mode = TRADE_MODES[lower(tostring(options.mode))]
        if mode == nil and (options.mode == 0 or options.mode == 1) then mode = options.mode end
        if mode == nil then
            return { ok = false, error = "no trade mode " .. tostring(options.mode) .. ": \"trade\" or \"barter\"", lastMove = lastMove }
        end
        local result = runOn(data, "SetTradeMode", mode)
        result.mode = mode == 1 and "barter" or "trade"
        result.check = "the window shows the mode a moment later: GK.Gui.Read(\"trade\").mode"
        log[#log + 1] = result
        if not result.ok then return { ok = false, error = result.error, log = log, lastMove = lastMove } end
    end
    if options.move ~= nil then
        if UI.PendingTradeMove and lastMove and lastMove.reached ~= true then
            return { ok = false, lastMove = lastMove, error = "the last move (" .. tostring(lastMove.item)
                .. ") is not settled, nothing new was moved: " .. tostring(lastMove.why) }
        end
        local names = TRADE_LISTS[options.side or "player"]
        if not names then return { ok = false, error = "no side " .. tostring(options.side) } end
        local objects, from
        for _, name in ipairs(names) do
            local inventory = path(data, name)
            if inventory and get(inventory, "Slots") then
                local _, list = slotsOf(inventory)
                objects, from = list, name
                break
            end
        end
        if not objects then
            return { ok = false, error = "cannot read the " .. tostring(options.side or "player") .. " side: none of "
                .. table.concat(names, ", ") .. " has Slots in this Trade window" }
        end
        local index = options.move
        if type(index) == "string" then
            for i, object in ipairs(objects) do
                if lower(text(get(object, "Name"))):find(lower(options.move), 1, true) then index = i break end
            end
        end
        local object = type(index) == "number" and objects[index] or nil
        if not object then return { ok = false, error = "no item " .. tostring(options.move) } end
        local name = text(get(object, "Name"))
        local stack = num(get(object, "Count")) or 1
        local held = heldOf(data, from, name)
        local result = runOn(data, "MoveTradeItem", object)
        log[#log + 1] = result
        if not result.ok then return { ok = false, error = result.error, log = log, lastMove = lastMove } end
        UI.PendingTradeMove = { name = name, count = held, from = from, at = Ext.Utils.MonotonicTime() }
        result.item = name
        result.moved = false  -- not yet: the window shows a move later, if at all
        if UI.IsOpen("SplitItem") or stack > 1 then
            -- N41: a stack does not move - the game asks how many in its SplitItem box (seen live)
            result.box = "SplitItem"
            result.stack = stack
            result.next = "a stack of " .. stack .. ": the game " .. (UI.IsOpen("SplitItem") and "opened" or "opens")
                .. " its SplitItem box to ask how many, and nothing is in the offer until it is answered: "
                .. "GK.Gui.Resolve{ split = { amount = n } } (or { split = \"cancel\" }), then GK.Gui.Trade{} reports lastMove"
        else
            result.reached = "not shown yet: the window shows a move when the game gets to it (seconds at a low frame "
                .. "rate); call GK.Gui.Trade{} until lastMove.reached is true or false"
        end
    end
    if options.balance then log[#log + 1] = runOn(UI.Data("Trade"), "BalanceTrade") end
    if options.finish then
        -- The window shows one of two buttons: TradeButton in trade mode, BarterBtn (FinishBarter)
        -- in barter mode - which is what RequestTrade opens. Pressing the hidden TradeButton ran
        -- "ok" and traded nothing (N3 of the third run, seen live at 03:30); the button the window
        -- SHOWS is pressed, as a click, its chain from the catalogue included.
        local d = UI.Data("Trade")
        UI.Controls("Trade")
        local shown = {}
        for _, row in ipairs(UI.LastControls or {}) do
            if row.name == "TradeButton" or row.name == "BarterBtn" then shown[row.name] = row end
        end
        local name = options.barter and "BarterBtn" or (shown.TradeButton and "TradeButton") or (shown.BarterBtn and "BarterBtn") or nil
        local before = num(path(d, "PlayerTraderCharacter.Inventory.Gold"))
        local pressed
        if name then
            pressed = UI.Click("Trade", name)
            if not pressed.ok and pressed.state == nil and tostring(pressed.error):find("^disabled") then pressed.state = tradeState(d) end
            if not pressed.ok and tostring(pressed.error):find("^disabled") then
                if options.move ~= nil or options.balance then
                    -- seen live: the window enables its button only once it shows the move or the
                    -- balance (seconds at a low frame rate, N43)
                    pressed.error = name .. " is still disabled in the call of the move/balance - nothing was traded; "
                        .. "call GK.Gui.Trade{} until lastMove.reached is true, then GK.Gui.Trade{ finish = true } on its own"
                else
                    pressed.error = "the game keeps its " .. name .. " disabled, so nothing was traded; `state` is the window's "
                        .. "own numbers and flags (TradeButtonsEnabled, the offers' gold - an empty offer keeps it off)"
                end
            end
        else
            pressed = runOn(d, "FinishTrade")
            pressed.note = "neither TradeButton nor BarterBtn is on screen: ran the view model's FinishTrade, which has been "
                .. "seen to commit nothing"
        end
        pressed.goldBefore = before
        pressed.check = "the trade commits a moment later: GK.Gui.Read('trade').playerGold, or Osi.GetGold on the server"
        log[#log + 1] = pressed
    end
    for _, entry in ipairs(log) do
        if not entry.ok then return { ok = false, error = entry.error, log = log, lastMove = lastMove } end
    end
    if lastMove and lastMove.reached == false and options.move == nil and options.mode == nil and not options.balance
        and not options.finish then
        return { ok = false, error = tostring(lastMove.item) .. " did not reach the offer: " .. lastMove.why, lastMove = lastMove }
    end
    return { ok = true, log = log, lastMove = lastMove }
end

-- combine: the game's Combine widget (dyeing, and combining two items) ------------------------
-- Opened by using the first item (GK.Gui.ItemDo("use", <dye>): HotBar.UseGameObject). Its data:
-- BaseItem, Ingredients (slots), CharacterInventory (the items it offers), CurrentState
-- (Preparing -> Ready -> Success), FailReason, ResultItem; ToggleItemCommand(item of
-- CharacterInventory) puts an item into the free slot, StartActionCommand combines. Seen live on
-- 2026-11-14 (N21, N26 of the third coverage run): a cobalt dye on leather armour went Preparing
-- -> Ready -> Success and the dye left the inventory. The HotBar's drag and drop commands, and
-- the widget's own drop commands run with the HotBar's item, answer ok and fill nothing.

-- The ingredients' slots are not read: the path to their item was not found live, and a slot read
-- as empty while it was filled would say something false. The state says it: Ready = filled.
local function combineState(data)
    return { base = text(path(data, "BaseItem.Name")), slots = count(get(data, "Ingredients")),
             state = plain(get(data, "CurrentState")), fail = plain(get(data, "FailReason")),
             result = text(path(data, "ResultItem.Name")) }
end

W.combine = W.combine or {}
W.combine.widget = "Combine"
W.combine.about = "combining two items, or dyeing one"
W.combine.read = reader(function(data) return combineState(data) end)

--- The Combine widget: { with = n | "name" } puts an item of its list into the free slot (as a click
--- in it does); { start = true } combines, only when the widget says Ready. Several in one call run
--- in that order, but the widget reads Ready only a frame after an item went in - so start in a call
--- of its own. No options: what the widget holds. A step is ok only when the widget's state moved:
--- `with` answers the state it leaves, the next call tells whether the slot took it.
function UI.Combine(options)
    options = options or {}
    local data = UI.Data("Combine")
    if not data then
        local last = UI.LastItemUse
        local since = last and (Ext.Utils.MonotonicTime() - last.at) or nil
        if last and last.action == "use" and since > 5000 then
            -- N47: the use of an arrow ran ok and opened no window - say so instead of "call again"
            return { ok = false, error = "no Combine window " .. math.floor(since / 1000) .. " s after GK.Gui.ItemDo(\"use\", \""
                .. tostring(last.item) .. "\"): its use opened none, so it is not an item whose use combines (a dye is; "
                .. "an arrow, seen live, is not)" }
        end
        return { ok = false, error = "no Combine window: use the first item - GK.Gui.ItemDo(\"use\", <dye or item>) - and call again a moment later" }
    end
    local log = {}
    if options.with ~= nil then
        local _, objects = slotsOf(get(data, "CharacterInventory"))
        local index = options.with
        if type(index) == "string" then
            for i, object in ipairs(objects or {}) do
                if lower(text(get(object, "Name"))):find(lower(options.with), 1, true) then index = i break end
            end
        end
        local object = type(index) == "number" and objects and objects[index] or nil
        if not object then
            local names = {}
            for i, o in ipairs(objects or {}) do names[i] = text(get(o, "Name")) end
            return { ok = false, error = "no item " .. tostring(options.with) .. " in the Combine list", items = names }
        end
        local result = runOn(data, "ToggleItemCommand", object)
        result.item = text(get(object, "Name"))
        log[#log + 1] = result
        if not result.ok then return { ok = false, error = result.error, log = log } end
    end
    if options.start then
        local now = plain(get(data, "CurrentState"))
        if now ~= "Ready" then
            local state = combineState(data)
            return { ok = false, log = log, combine = state,
                     error = "the Combine window is " .. tostring(now) .. ", not Ready (FailReason " .. tostring(state.fail)
                         .. "): put the second item in with { with = ... } and start in a call of its own" }
        end
        local result = runOn(data, "StartActionCommand")
        result.check = "the window reads Success a moment later and the used item leaves the inventory: GK.Gui.Combine{}"
        log[#log + 1] = result
        if not result.ok then return { ok = false, error = result.error, log = log } end
    end
    local state = combineState(data)
    if options.with == nil and not options.start and state.state == "Failed" then
        return { ok = false, error = "the combination failed: " .. tostring(state.fail), combine = state }
    end
    return { ok = true, log = log, combine = state }
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
--- When the game holds the rest back (EndTheDayState "Unavailable") the answer is a failure with
--- what the HUD itself shows about resting and the camp under `shown` (wish 24: "Unavailable",
--- always, and no reason); the game's own reason is one of those, or outside the HUD (a fight,
--- a dialogue, a level without a camp).
function UI.LongRest()
    local hotbar = UI.Data("HotBar")
    if not hotbar then return { ok = false, error = "no HotBar" } end
    local state = plain(path(hotbar, "CurrentPlayer.EndTheDayState"))
    local out = runOn(hotbar, "CampTravel")
    out.endTheDay = plain(path(hotbar, "CurrentPlayer.EndTheDayState"))
    if out.endTheDay == "Unavailable" then
        local shown = {}
        for _, owner in ipairs({ { hotbar, "" }, { get(hotbar, "CurrentPlayer"), "CurrentPlayer." } }) do
            local ok, properties = pcall(function() return owner[1]:GetAllProperties() end)
            if ok and properties then
                for key, value in pairs(properties) do
                    local name = tostring(key)
                    local kind = type(value)
                    if (name:find("Rest") or name:find("Camp") or name:find("Day") or name:find("Combat") or name:find("Dialog"))
                        and (kind == "boolean" or kind == "number" or kind == "string") then
                        shown[owner[2] .. name] = value
                    end
                end
            end
        end
        out.ok = false
        out.shown = shown
        out.error = "the game holds the long rest back (End the Day: Unavailable, before the call: " .. tostring(state) .. ") - CampTravel ran; "
            .. "`shown` is what the HUD says about resting, the camp, fights and dialogues. A fight, a dialogue or a "
            .. "level without a camp also keeps it, and GK.Stage.State() on the server reads the first two"
    end
    if out.ok then
        -- N38 of the third run: "ok" right after a rest, and no screen or box opened. What opens
        -- comes a frame or more later; ok says CampTravel ran, not that anything opened.
        out.opens = "a frame later, if at all: GK.Gui.Blocking() names the rest screen (rest) or the End the Day box (box)"
    end
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

-- The client's game state passes through "Save" while the game writes a save, and comes back
-- when the save is written: counted here, so that a save is reported once it has been seen to end.
UI.Saves = UI.Saves or { begun = 0, ended = 0 }
if not UI.SavesHooked then
    UI.SavesHooked = true
    Ext.Events.GameStateChanged:Subscribe(function(e)
        if tostring(e.ToState) == "Save" then UI.Saves.begun = UI.Saves.begun + 1
        elseif tostring(e.FromState) == "Save" then UI.Saves.ended = UI.Saves.ended + 1 end
    end)
end

-- An attempted step of a verb, answered once `seen()` names what it led to, or after `within` ms
-- with ok = false and `missing`.
local function observed(attempt, within, seen, missing)
    if not attempt.ok then return attempt end
    return GK.Observe(attempt, function(_, final)
        local result = seen()
        if result then return result end
        if final then return { ok = false, error = missing, attempted = attempt } end
    end, within)
end

--- A named save, one step per call, each answered by what it saw: the save window opened, then
--- its name box opened, then { ok, saved = name } once the game has gone through its Save state
--- and back (the save is written). A step whose effect does not show answers ok = false.
function UI.SaveAs(name)
    name = tostring(name or "GateKit")
    local data = UI.Data("SaveGamePopup")
    if data then
        local begun, ended = UI.Saves.begun, UI.Saves.ended
        local attempt = runOn(data, "DoSaveWithName", name)
        if not attempt.ok then return attempt end
        return GK.Observe(attempt, function(_, final)
            if UI.Saves.ended > ended then return { ok = true, saved = name, did = "saved: the game wrote the save and is back in play" } end
            if final then
                return { ok = false, name = name, attempted = attempt,
                         error = UI.Saves.begun > begun and "the save began and had not ended in 15 s"
                             or "DoSaveWithName ran and the game did not begin to save" }
            end
        end, 15000)
    end
    if not UI.IsOpen("SaveGame") then
        local opened = gameCommand("OpenSaveGameDialogCommand")
        if not opened.ok then opened = UI.Event("OpenNewSaveDialog") end
        return observed(opened, 3000, function()
            if UI.IsOpen("SaveGame") or UI.Data("SaveGamePopup") then
                return { ok = true, waiting = true, did = "the save window is open", next = "call SaveAs again" }
            end
        end, "the save window did not open")
    end
    return observed(UI.Event("OpenNewSaveDialog", "SaveGame"), 3000, function()
        if UI.Data("SaveGamePopup") then
            return { ok = true, waiting = true, did = "the name box is open", next = "call SaveAs again" }
        end
    end, "the name box did not open")
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

--- Loads one save from the load window (the main menu's or the game's): the save whose title is
--- `title` (a save folder's name after its "__": AutoSave_0, QuickSave_0, a name given) and, when
--- several have it, whose time the window shows (TimeString "dd/mm/yyyy hh:mm") is the first of `times`
--- (the likeliest first) that only one of them has.
--- Opens the window first ({ waiting = true }), then selects the save and presses Load.
function UI.LoadSave(title, times)
    local data = UI.Data("LoadGame")
    local function filled()
        local d = UI.Data("LoadGame")
        local n = d and guard(function() return #d.ExistingPlaythroughs end) or 0
        return n > 0
    end
    if not filled() then
        -- the window opens, and fills its list of saves a moment later: answered when it has
        local attempt = data and { ok = true, did = "the load window is open" } or UI.Event("LoadGameOpen")
        return observed(attempt, 5000, function()
            if filled() then return { ok = true, waiting = true, did = "the load window lists its saves", next = "call LoadSave again" } end
        end, "the load window did not list any save")
    end
    data = UI.Data("LoadGame")
    local wanted = {}
    for _, t in ipairs(times or {}) do wanted[t] = true end
    -- the window lists the saves of one playthrough at a time (ExistingSaves); every playthrough's
    -- are read, and the one holding the save is selected before the save
    local titled, timed, owner = {}, {}, {}
    local _, problem = guard(function()
        local playthroughs = data.ExistingPlaythroughs
        for i = 1, #playthroughs do
            local playthrough = playthroughs[i]
            local saves = playthrough.Saves
            for j = 1, #saves do
                local save = saves[j]
                if tostring(save.Title) == tostring(title) then
                    titled[#titled + 1] = save
                    owner[save] = playthrough
                    if wanted[tostring(save.TimeString)] then timed[#timed + 1] = save end
                end
            end
        end
    end)
    if problem then return { ok = false, error = "the load window's saves could not be read: " .. problem } end
    -- one titled save, or the first of the times (in the order given: the likeliest first) that
    -- names exactly one of them
    local pick = #titled == 1 and titled[1] or nil
    for _, t in ipairs(times or {}) do
        if pick then break end
        local at = {}
        for _, s in ipairs(timed) do if tostring(s.TimeString) == t then at[#at + 1] = s end end
        if #at == 1 then pick = at[1] end
    end
    if pick and owner[pick] then guard(function() data.SelectedPlaythrough = owner[pick] end) end
    if not pick then
        local seen = {}
        for _, s in ipairs(titled) do seen[#seen + 1] = text(get(s, "TimeString")) end
        return { ok = false, error = (#titled == 0 and "no save titled " or "no single save titled ") .. tostring(title)
            .. (#titled > 0 and (" at the times asked; its times: " .. table.concat(seen, ", ")) or "") }
    end
    local _, problem = guard(function() data.SelectedSave = pick end)
    if problem then return { ok = false, error = "could not select the save: " .. problem } end
    local result = pressLive("LoadGame", "LoadSaveBtn")
    result.save = saveSummary(pick)
    return result
end

-- The first launch and the "press any key" page ------------------------------------------------

-- The pages the game shows on a first launch (research/studies/startup-videos.md, "The first
-- launch"), registered under the pages' own names: key window:<page>. Each waits for the player;
-- its controls go into the attention row (`look`), and nothing here presses them: a step names
-- the player's way (GK.Gui.Look, Click, Select, Set) and GK.Cheat.Recover() goes through them.
-- What a player can act on in a page, short: GK.Gui.Look's controls with their handles, and
-- whether the page's layout has made and arranged all of them.
local function controlsOf(widget, ui)
    local look = ui.Look(widget, { layout = true })
    local out = {}
    for _, c in ipairs(look.controls or {}) do
        local items
        if c.items then
            items = {}
            for i, it in ipairs(c.items) do items[i] = it.text end
        end
        out[#out + 1] = { handle = c.handle, kind = c.kind, text = c.text or c.of, command = c.command, parameter = c.parameter,
                          value = c.value, min = c.min, max = c.max, items = items, selected = c.selected,
                          enabled = c.enabled }
    end
    return out, look.laidOut == true
end

local SETUP = {
    Eula = "the licence agreement",
    ModBrowserSplashScreen = "Larian's mod policy (terms and privacy)",
    SensitiveSettings = "the explicit-content choice",
    HDRCalibration = "the HDR calibration",
    GammaCalibration = "the gamma calibration (also over the main menu)",
}
for page, about in pairs(SETUP) do
    local window = W[page] or {}
    W[page] = window
    window.widget, window.blocking, window.look, window.policy, window.about = page, true, true, "setup", about
    window.read = reader(function(_, ui)
        local switches = guard(function() return Ext.Utils.GetGlobalSwitches() end)
        local controls, laidOut = controlsOf(page, ui)
        return { controls = controls, laidOut = laidOut, firstLaunch = switches and guard(function() return switches.ShowFirstTimeSetup end) }
    end)
    window.step = stepper(function()
        return { ok = false, stuck = true, did = "left for the player",
                 error = page .. " is answered by its controls (GK.Gui.Look, Click, Select, Set), or GK.Cheat.Recover() goes through it" }
    end)
end
-- Eula waits for the player only while the game's switch is on: its XAML binds its content to
-- ShowFirstTimeSetup and, with it off, raises OpenSplashScreen itself on Loaded.
W.Eula.active = function(data)
    local on = data and guard(function() return data.ShowFirstTimeSetup end)
    return on ~= false
end

-- The "press any key" page: no control; any key or mouse button raises OpenMainMenu (its XAML).
W.SplashScreen = W.SplashScreen or {}
W.SplashScreen.widget, W.SplashScreen.blocking, W.SplashScreen.policy = "SplashScreen", true, "splash"
W.SplashScreen.about = "the page before the main menu"
W.SplashScreen.keys = "any key or mouse button: the page raises OpenMainMenu"
W.SplashScreen.look = true
W.SplashScreen.read = reader(function() return { controls = {}, keys = W.SplashScreen.keys } end)
W.SplashScreen.step = stepper(function(ui)
    if not ui.IsOpen("SplashScreen") then return { done = true, did = "closed" } end
    local result = ui.Event("OpenMainMenu", "SplashScreen")
    result.did = "OpenMainMenu, as its key does"
    return result
end)

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
    Eula = "its controls: GK.Gui.Click", ModBrowserSplashScreen = "its controls: GK.Gui.Set, Click",
    SensitiveSettings = "its controls: GK.Gui.Select, Click", HDRCalibration = "its controls: GK.Gui.Set, Click",
    GammaCalibration = "its controls: GK.Gui.Set, Click", SplashScreen = "any key (gatekit key space); choice = 'pass'",
}

UI.BlockingWindows = blockingWindows
UI.BlockingChoices = CHOICES

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
    split = "confirm", book = "close", gamemenu = "resume", save = "close", load = "close", splash = "pass",
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
    -- a blocking window's step answers it by its own commands only
    local result = Cheat.Step(top.window, options)
    result.window = top.window
    result.widget = top.widget
    return result
end

return true
