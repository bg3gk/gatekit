-- The click chain of a control, read from the game's own XAML at run time.
--
-- D20 of the 2026-11-14 day run: GK.Gui.Click on CharacterLevelUp's finishLevelUpButton answered
-- ok and the level-up did not apply. The button's own Command is PlayDummyVFX (a visual effect);
-- what finishes the level-up is in its b:Interaction.Triggers - a TimerTrigger on Click that runs
-- FinishLevelUp 1400 ms later. GK.Gui.Click knows such chains only from GK.UICatalog (generated,
-- not shipped), and the live element cannot tell them: its attached `Triggers` property reads as
-- a NoesisApp.TriggerCollection that Script Extender hands out as a plain DependencyObject, not a
-- collection (a fork wish: se-fork TODO, "Noesis FreezableCollection as a collection").
--
-- The XAML itself is part of the game's data and Ext.IO.LoadFile(path, "data") reads it from the
-- user's own install, so the chain is read where the game reads it: the widget's page (and the
-- shared library pages), the element of that x:Name, its EventTrigger / TimerTrigger on Click and
-- their InvokeCommandAction / ChangePropertyAction. The result has GK.UICatalog's shape, so
-- GK.Gui.Click runs it the same way. Nothing of the XAML is kept beyond this Lua state.

local UI = GK.Gui
if not UI then return false end

UI.XamlCache = UI.XamlCache or {}

local PAGES = { "Mods/MainUI/GUI/Pages/%s.xaml", "Mods/MainUI/GUI/Pages/%s_k.xaml", "Public/Game/GUI/Widgets/%s.xaml",
                "Mods/MainUI/GUI/Widgets/%s.xaml" }
local LIBRARY = { "Public/Game/GUI/Library/CCLib_k.xaml", "Public/Game/GUI/Library/CCLib.xaml",
                  "Mods/MainUI/GUI/Library/CCLib.xaml", "Mods/MainUI/GUI/Library/CCLib_k.xaml",
                  "Mods/MainUI/GUI/Library/Templates.xaml", "Mods/MainUI/GUI/Library/Templates_k.xaml",
                  "Mods/MainUI/GUI/Library/Common.xaml", "Mods/MainUI/GUI/Library/Common_k.xaml" }

local function load(path)
    local cached = UI.XamlCache[path]
    if cached ~= nil then return cached or nil end
    local ok, text = pcall(Ext.IO.LoadFile, path, "data")
    text = ok and type(text) == "string" and text ~= "" and text or nil
    UI.XamlCache[path] = text or false
    return text
end

local function attr(tag, name)
    return tag:match("%s" .. name:gsub("([%.%:])", "%%%1") .. "%s*=%s*\"([^\"]*)\"")
end

-- "{Binding FinishLevelUp}" / "{Binding Path=X, ...}" -> "FinishLevelUp"; a literal stays a literal.
local function binding(value)
    if not value then return nil end
    local inner = value:match("^{Binding%s*(.-)}$")
    if not inner then return nil end
    local path = inner:match("Path%s*=%s*([%w%._%[%]]+)") or inner:match("^([%w%._%[%]]+)")
    local element = inner:match("ElementName%s*=%s*([%w_]+)")
    return path or "", element
end

local function parameterOf(value)
    if value == nil then return nil end
    local path, element = binding(value)
    if path then
        return { binding = { source = element and ("element:" .. element) or "self", path = path } }
    end
    if value:match("^{") then return { multi = true } end
    if value == "True" or value == "False" then return { bool = value == "True" } end
    return { literal = value }
end

local function actionsIn(block)
    local actions = {}
    for tag in block:gmatch("<b:[%w]+Action[^>]*>") do
        local kind = tag:match("^<b:([%w]+)")
        if kind == "InvokeCommandAction" then
            local path, element = binding(attr(tag, "Command"))
            if path then
                actions[#actions + 1] = { ["do"] = "command",
                                          command = { source = element and ("element:" .. element) or "self", path = path },
                                          parameter = parameterOf(attr(tag, "CommandParameter")) }
            end
        elseif kind == "ChangePropertyAction" then
            local target = attr(tag, "TargetName")
            local value = attr(tag, "Value")
            if target and value and not value:match("^{") then
                actions[#actions + 1] = { ["do"] = "set", target = target, property = attr(tag, "PropertyName"), value = value }
            end
        end
    end
    return actions
end

-- The text of the element named `name`, from its opening tag to its closing tag.
local function elementText(text, name)
    local at = text:find("x:Name%s*=%s*\"" .. name:gsub("%p", "%%%0") .. "\"")
    if not at then return nil end
    local open = text:sub(1, at):match(".*()<[%w:%.]+")
    if not open then return nil end
    local tagName = text:match("^<([%w:%.]+)", open)
    local headEnd = text:find(">", at, true)
    if not headEnd then return nil end
    if text:sub(headEnd - 1, headEnd) == "/>" then return text:sub(open, headEnd) end
    -- the matching close, counting nested elements of the same tag
    local depth, pos = 1, headEnd + 1
    local pattern = "<(/?)" .. tagName:gsub("%p", "%%%0") .. "[%s>/]"
    while depth > 0 do
        local s, e, slash = text:find(pattern, pos)
        if not s then return text:sub(open) end
        local close = text:find(">", e, true) or e
        if slash == "/" then
            depth = depth - 1
        elseif text:sub(close - 1, close) ~= "/>" then
            depth = depth + 1
        end
        pos = close + 1
        if depth == 0 then return text:sub(open, close) end
    end
end

local function chainOf(text, name)
    local element = text and elementText(text, name)
    if not element then return nil end
    local head = element:match("^<[^>]*>") or ""
    local entry = { name = name, source = "xaml" }
    local commandPath, commandElement = binding(attr(head, "Command"))
    if commandPath then
        entry.command = { source = commandElement and ("element:" .. commandElement) or "self", path = commandPath }
        entry.parameter = parameterOf(attr(head, "CommandParameter"))
    end
    local triggers = element:match("<b:Interaction%.Triggers>(.-)</b:Interaction%.Triggers>")
    if triggers then
        entry.click = {}
        for trigger, body in triggers:gmatch("(<b:%w+Trigger[^>]*>)(.-)</b:%w+Trigger>") do
            local event = attr(trigger, "EventName")
            if event == "Click" then
                local delay = 0
                if trigger:find("^<b:TimerTrigger") then
                    delay = (tonumber(attr(trigger, "MillisecondsPerTick")) or 0) * (tonumber(attr(trigger, "TotalTicks")) or 1)
                end
                local actions = actionsIn(body)
                if #actions > 0 then entry.click[#entry.click + 1] = { delay = delay, actions = actions } end
            end
        end
        if #entry.click == 0 then entry.click = nil end
    end
    if not entry.command and not entry.click then return nil end
    return entry
end

--- The click chain of control `name` of widget `widget` from the game's XAML, in GK.UICatalog's
--- shape ({ command, parameter, click = { { delay, actions } } }), or nil.
function UI.XamlChain(widget, name)
    if not (widget and name) then return nil end
    local key = widget .. "/" .. name
    local cached = UI.XamlCache[key]
    if cached ~= nil then return cached or nil end
    local found
    for _, pattern in ipairs(PAGES) do
        found = chainOf(load(pattern:format(widget)), name)
        if found then found.file = pattern:format(widget) break end
    end
    if not found then
        for _, path in ipairs(LIBRARY) do
            found = chainOf(load(path), name)
            if found then found.file = path break end
        end
    end
    UI.XamlCache[key] = found or false
    return found
end

return true
