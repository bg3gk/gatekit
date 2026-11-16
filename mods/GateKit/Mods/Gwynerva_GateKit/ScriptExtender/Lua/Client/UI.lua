-- The game's own UI as an API. The UI (Noesis) is built on view models; this file finds them
-- in the visual tree and reads or drives them, so a test can follow a dialogue or press a
-- game command without the mouse and without looking at the screen.
--
-- Everything here must run in the UI's context: `gatekit exec ui ...`, the MCP tool with
-- side "ui", or inside Ext.UI.Defer(function() ... end).

local function typeName(object)
    local ok, name = pcall(function() return tostring(object.Type) end)
    return ok and name or "?"
end

--- Calls visit(element, dataContext) for every element of the visual tree; stop by returning true.
local function walk(visit)
    local stack = { { Ext.UI.GetRoot(), 0 } }
    local visited = 0
    while #stack > 0 do
        local top = table.remove(stack)
        local element, depth = top[1], top[2]
        visited = visited + 1
        if visited > 60000 then return end
        local ok, context = pcall(function() return element.DataContext end)
        if visit(element, ok and context or nil) then return end
        if depth < 80 then
            local n = 0
            pcall(function() n = element.VisualChildrenCount end)
            for i = n, 1, -1 do
                local okc, child = pcall(function() return element:VisualChild(i) end)
                if okc and child then stack[#stack + 1] = { child, depth + 1 } end
            end
        end
    end
end

--- The first view model of a type in the UI ("gui::VMDialogue", "gui::DCHotBar", ...), or nil.
function GK.ViewModel(wanted)
    local found
    walk(function(_, context)
        if context and typeName(context) == wanted then found = context return true end
    end)
    return found
end

--- The distinct view-model types currently in the UI: a map of what can be read right now.
function GK.ViewModels()
    local seen, out = {}, {}
    walk(function(_, context)
        if context then
            local name = typeName(context)
            if not seen[name] then seen[name] = true out[#out + 1] = name end
        end
    end)
    table.sort(out)
    return out
end

local function text(value)
    if type(value) ~= "string" then return value end
    -- Some fields hold a localisation handle instead of the text itself.
    if value:match("^h%x+g%x+g%x+g%x+g%x+$") then
        local ok, resolved = pcall(Ext.Loca.GetTranslatedString, value)
        if ok and resolved and resolved ~= "" then return resolved end
    end
    return value
end

--- A view model's plain properties (strings, numbers, booleans); nested objects are named by type.
function GK.Properties(wanted)
    local model = type(wanted) == "string" and GK.ViewModel(wanted) or wanted
    if not model then return nil end
    local ok, all = pcall(function() return model:GetAllProperties() end)
    if not ok then return nil end  -- not every data context is a reflected object
    local out = {}
    for key, value in pairs(all) do
        if type(value) == "userdata" then
            local okn, n = pcall(function() return #value end)
            out[tostring(key)] = "<" .. typeName(value) .. ">" .. ((okn and type(n) == "number") and ("#" .. n) or "")
        else
            out[tostring(key)] = text(value)
        end
    end
    return out
end

--- The dialogue on screen: { instance, speaker, text, showAnswers, answers = { { n, text, enabled } } },
--- or nil when no dialogue is shown.
function GK.Dialog()
    local model = GK.ViewModel("gui::VMDialogue")
    if not model then return nil end
    local props = model:GetAllProperties()
    local answers = {}
    local list = props.Answers
    local count = 0
    pcall(function() count = #list end)
    for i = 1, count do
        local answer = list[i]:GetAllProperties()
        answers[#answers + 1] = { n = (tonumber(answer.AnswerIdx) or (i - 1)) + 1, text = text(answer.BodyText), enabled = answer.Enabled }
    end
    return {
        instance = props.DialogInstanceID,
        speaker = text(props.SpeakerName),
        text = text(props.BodyText),
        showAnswers = props.ShowAnswers,
        answers = answers,
    }
end

--- Chooses answer number `n` (as numbered by GK.Dialog) in the dialogue on screen.
function GK.Answer(n)
    local model = GK.ViewModel("gui::VMDialogue")
    local context = GK.ViewModel("gui::DCDialogue")
    if not model or not context then error("no dialogue on screen") end
    local list = model:GetAllProperties().Answers
    local count = #list
    for i = 1, count do
        local answer = list[i]
        if (tonumber(answer:GetAllProperties().AnswerIdx) or (i - 1)) + 1 == n then
            context:GetAllProperties().SelectAnswerCommand:Execute(answer)
            return true
        end
    end
    error("the dialogue has no answer " .. tostring(n) .. " (it has " .. count .. ")")
end

-- Every widget's data context ("gui::DC...") carries the same set of common game commands,
-- so when no type is asked for, the first one in the tree will do.
local function commandSource(wanted)
    if wanted then return GK.ViewModel(wanted) end
    local found
    walk(function(_, context)
        if context and typeName(context):find("^gui::DC") then found = context return true end
    end)
    return found
end

-- Why there is no view model to take commands from: said, not a bare nil (wish 13: a command
-- name given as `wanted` answered null, which read as "no such command").
local function noSource(wanted)
    if wanted then
        return "no view model " .. tostring(wanted) .. " in the UI now; `wanted` is a view-model type "
            .. "(GK.ViewModels() lists those on screen), not a command name - GK.Commands() lists the commands"
    end
    return "no gui::DC* data context in the UI yet (is the game past its loading screen?)"
end

--- Text in lower case for matching, beyond ASCII: Latin-1, Greek and Cyrillic capitals too. The
--- game shows names in the player's language, and string.lower leaves a Cyrillic capital as it is, so a name
--- typed in another case never matched (the third coverage run). Bytes that are not such a
--- character are kept as they are.
function GK.Fold(text)
    text = tostring(text or "")
    if not text:find("[\128-\255]") then return text:lower() end
    -- no utf8 library in the game's Lua: the two-byte sequences (U+0080..U+07FF, where all these
    -- capitals are) are read by hand, anything else is kept as it is
    local out, i, n = {}, 1, #text
    while i <= n do
        local b1 = text:byte(i)
        local b2 = i < n and text:byte(i + 1) or nil
        if b1 >= 0xC2 and b1 <= 0xDF and b2 and b2 >= 0x80 and b2 <= 0xBF then
            local c = (b1 - 0xC0) * 64 + (b2 - 0x80)
            if (c >= 0xC0 and c <= 0xDE and c ~= 0xD7) or (c >= 0x391 and c <= 0x3AB and c ~= 0x3A2)
                or (c >= 0x410 and c <= 0x42F) then
                c = c + 0x20
            elseif c >= 0x400 and c <= 0x40F then
                c = c + 0x50
            end
            out[#out + 1] = string.char(0xC0 + c // 64, 0x80 + c % 64)
            i = i + 2
        elseif b1 < 0x80 then
            out[#out + 1] = string.char(b1):lower()
            i = i + 1
        else
            out[#out + 1] = string.char(b1)
            i = i + 1
        end
    end
    return table.concat(out)
end

--- Names of the game commands available (QuickSaveCommand, SwitchWeaponSetCommand, ShortRest,
--- GoToCamp, TogglePhotoModeCommand, ...). `wanted` narrows it to one view-model type; a type that
--- is not on screen is an error saying so.
function GK.Commands(wanted)
    local model = commandSource(wanted)
    if not model then error(noSource(wanted), 2) end
    local out = {}
    for key, value in pairs(model:GetAllProperties()) do
        if type(value) == "userdata" and typeName(value):find("Command", 1, true) then out[#out + 1] = tostring(key) end
    end
    table.sort(out)
    return out
end

--- Executes a game command by name, with an optional parameter, e.g. GK.Command("QuickSaveCommand").
--- Refused when the command is not there or the game's own CanExecute says no. The answer says
--- the command RAN, which is not that it did anything (wishes 13, 16: ShortRest and
--- TogglePhotoModeCommand ran and nothing happened) - read the effect.
function GK.Command(name, parameter, wanted)
    local model = commandSource(wanted)
    if not model then error(noSource(wanted), 2) end
    local command = model:GetAllProperties()[name]
    if not command or not typeName(command):find("Command", 1, true) then
        error("no command " .. tostring(name) .. " on " .. typeName(model) .. "; GK.Commands("
            .. (wanted and string.format("%q", wanted) or "") .. ") lists those there", 2)
    end
    -- a plain Lua value is boxed for the game, as GK.Gui.Click does (D19 of the day run:
    -- GK.Command("TogglePhotoModeCommand", false) failed "expected a light C++ object, got boolean")
    if parameter ~= nil and type(parameter) ~= "userdata" then parameter = Ext.UI.Box(parameter) end
    local known, can = pcall(function() return command:CanExecute(parameter) end)
    if known and can == false then
        error(tostring(name) .. " cannot run now: the game's CanExecute says no (nothing was done)", 2)
    end
    command:Execute(parameter)
    return { ran = name, canExecute = known and can or nil,
             note = "ran is not did: the game may ignore it (a gate on the player, a window, combat) - read the effect" }
end
