-- GK.UI: how another mod puts its own tab (or window) into the GateKit overlay.
--
--   Ext.OnNextTick(function()
--       local kit = Mods.GateKit
--       local UI = kit and kit.GK and kit.GK.UI
--       if not UI then return end                    -- the GateKit is not installed: do nothing
--       UI.Panel("My Mod", function(p)
--           p:Button("Dump state", function() return MyMod.Dump() end)
--           p:Toggle("Debug messages", function() return MyMod.Debug end, function(on) MyMod.Debug = on end)
--           p:Button("Heal the party", { server = "return Mods.MyMod.HealParty()" })
--       end)
--   end)
--
-- The unit of extension is the panel: a name and a function that fills it. The function is
-- called whenever the overlay window is built, so nothing has to be kept alive by the mod;
-- registering again under the same name replaces the panel. After a Lua reset every mod's
-- scripts run again and register again - that is all "surviving a reset" takes.
--
-- An action is either a function (runs here, on the client) or a table naming where a piece
-- of Lua runs: { server = "code" }, { client = "code" }, { ui = "code" }. In such code `value`
-- is what the element was set to (true/false, the text, the chosen option).
-- What an action returns (or the error it raised) is shown at the bottom of the panel and
-- goes to the console log under the panel's name.

GK.UI = GK.UI or {}

local GREY = { 0.60, 0.60, 0.60, 1 }
local CYAN = { 0.45, 0.85, 1.0, 1 }

local panels, order = {}, {}
local window, tabBar, tabs    -- the overlay, once built

local function show(value)
    if value == nil then return nil end
    if type(value) == "table" then
        local ok, text = pcall(Ext.Json.Stringify, value, { Beautify = true, StringifyInternalTypes = true,
            IterateUserdata = true, AvoidRecursion = true })
        return ok and text or tostring(value)
    end
    return tostring(value)
end

local function literal(value)
    if type(value) == "string" then return string.format("%q", value) end
    return tostring(value)
end

local function report(panel, label, ok, text)
    panel.last = { label = label, ok = ok, text = text }
    if text ~= nil then
        GK.ConsoleEmit(ok and "Info" or "Error", panel.name, label .. ": " .. tostring(text))
    end
    if panel.lastWidget then
        local line = label .. (text ~= nil and (": " .. tostring(text)) or ": done")
        panel.lastWidget.Label = GK.CutUtf8(line, 1500)
        panel.lastWidget:SetColor("Text", ok and GREY or { 1.0, 0.42, 0.40, 1 })
    end
end

--- Runs an element's action with the element's new value.
local function act(panel, label, action, value)
    if action == nil then return end
    if type(action) == "function" then
        local ok, result = xpcall(action, function(err) return GK.ShortTrace(debug.traceback(tostring(err), 2)) end, value)
        if ok then report(panel, label, true, show(result)) else report(panel, label, false, tostring(result)) end
        return
    end
    local side = action.server and "server" or action.ui and "ui" or "client"
    local code = "local value = " .. literal(value) .. "\n" .. tostring(action.server or action.ui or action.client)
    local function done(ok, text) report(panel, label, ok, text ~= "(no result)" and text or nil) end
    if side == "ui" then
        Ext.UI.Defer(function() GK.ConsoleAsk("client", code, done) end)
    else
        GK.ConsoleAsk(side, code, done)
    end
end

--- A value given either as itself or as a function returning it.
local function current(source)
    if type(source) ~= "function" then return source end
    local ok, value = pcall(source)
    return ok and value or nil
end

-- --- What a builder function may call ------------------------------------------------------

local Builder = {}
Builder.__index = Builder

local function place(self, widget, opts)
    if self.sameLine or (opts and opts.sameLine) then widget.SameLine = true end
    self.sameLine = false
    if opts and opts.tip then widget:Tooltip():AddText(tostring(opts.tip)) end
    return widget
end

local function register(self, element)
    local panel = self.panel
    panel.elements[#panel.elements + 1] = element
    if element.label then panel.byLabel[element.label] = element end
    return element
end

--- Puts the next element on the same line as the previous one.
function Builder:SameLine()
    self.sameLine = true
    return self
end

--- A line (or several) of text. Give a function to have it re-read once a second.
function Builder:Text(text, opts)
    local widget = place(self, self.parent:AddText(show(current(text)) or ""), opts)
    widget.TextWrapPos = 0
    register(self, { kind = "text", widget = widget, source = text,
        get = function() return show(current(text)) end,
        refresh = type(text) == "function" and function(e)
            local now = show(current(text)) or ""
            if e.shown ~= now then widget.Label = now e.shown = now end
        end or nil })
    return self
end

--- A dividing line, with a title if given.
function Builder:Separator(title)
    if title then self.parent:AddSeparatorText(tostring(title)) else self.parent:AddSeparator() end
    self.sameLine = false
    return self
end

--- A button. `action`: a function, or { server | client | ui = "lua" }.
function Builder:Button(label, action, opts)
    local panel = self.panel
    local widget = place(self, self.parent:AddButton(label .. "##" .. panel.id .. "_" .. #panel.elements), opts)
    local element = register(self, { kind = "button", label = label, widget = widget })
    element.press = function() act(panel, label, action, nil) end
    widget.OnClick = element.press
    return self
end

--- A checkbox. `value`: the state, or a function returning it (then it follows the mod's
--- own state, re-read once a second). `action(on)` runs when it is flipped.
function Builder:Toggle(label, value, action, opts)
    local panel = self.panel
    local widget = place(self, self.parent:AddCheckbox(label .. "##" .. panel.id .. "_" .. #panel.elements,
        current(value) and true or false), opts)
    widget:SetColor("FrameBg", { 0.30, 0.30, 0.34, 1 })
    local element = register(self, { kind = "toggle", label = label, widget = widget })
    element.get = function() return widget.Checked end
    element.press = function(on)
        if on == nil then on = not widget.Checked end
        widget.Checked = on and true or false
        act(panel, label, action, widget.Checked)
    end
    widget.OnChange = function() act(panel, label, action, widget.Checked) end
    if type(value) == "function" then
        element.refresh = function()
            local on = current(value) and true or false
            if widget.Checked ~= on then widget.Checked = on end
        end
    end
    return self
end

--- A one-line text field; `action(text)` runs on Enter.
function Builder:Input(label, value, action, opts)
    local panel = self.panel
    self.parent:AddText(label)
    local widget = place(self, self.parent:AddInputText("##" .. panel.id .. "_" .. #panel.elements,
        tostring(current(value) or "")), { sameLine = true, tip = opts and opts.tip })
    widget.EnterReturnsTrue = true
    widget.ItemWidth = (opts and opts.width) or 320
    if opts and opts.hint then widget.Hint = tostring(opts.hint) end
    local element = register(self, { kind = "input", label = label, widget = widget })
    element.get = function() return widget.Text end
    element.press = function(text)
        if text ~= nil then widget.Text = tostring(text) end
        act(panel, label, action, widget.Text)
    end
    widget.OnChange = function() act(panel, label, action, widget.Text) end
    return self
end

--- A drop-down list. `value`: the option selected (or a function returning it).
--- `action(option, index)` runs on a choice.
function Builder:Select(label, options, value, action, opts)
    local panel = self.panel
    local list = {}
    for i, option in ipairs(options) do list[i] = tostring(option) end
    local function indexOf(option)
        for i, known in ipairs(list) do if known == tostring(option) then return i end end
        return nil
    end
    self.parent:AddText(label)
    local widget = place(self, self.parent:AddCombo("##" .. panel.id .. "_" .. #panel.elements),
        { sameLine = true, tip = opts and opts.tip })
    widget.Options = list
    widget.ItemWidth = (opts and opts.width) or 240
    widget.SelectedIndex = (indexOf(current(value)) or 1) - 1
    local element = register(self, { kind = "select", label = label, widget = widget, options = list })
    element.get = function() return list[widget.SelectedIndex + 1] end
    local function chosen()
        local index = widget.SelectedIndex + 1
        if type(action) == "function" then
            act(panel, label, function() return action(list[index], index) end, nil)
        else
            act(panel, label, action, list[index])
        end
    end
    element.press = function(option)
        local index = type(option) == "number" and option or indexOf(option)
        if not index or not list[index] then error("panel " .. panel.name .. ": " .. label .. " has no option " .. tostring(option)) end
        widget.SelectedIndex = index - 1
        chosen()
    end
    widget.OnChange = chosen
    if type(value) == "function" then
        element.refresh = function()
            local index = indexOf(current(value))
            if index and widget.SelectedIndex ~= index - 1 then widget.SelectedIndex = index - 1 end
        end
    end
    return self
end

local function valueLines(value)
    if type(value) ~= "table" then return show(value) or "" end
    local keys = {}
    for key in pairs(value) do keys[#keys + 1] = key end
    table.sort(keys, function(a, b) return tostring(a) < tostring(b) end)
    local lines = {}
    for i, key in ipairs(keys) do
        local item = value[key]
        if type(item) == "table" then
            local ok, text = pcall(Ext.Json.Stringify, item, { Beautify = false, StringifyInternalTypes = true,
                IterateUserdata = true, AvoidRecursion = true })
            item = ok and text or tostring(item)
        end
        lines[i] = tostring(key) .. ":  " .. tostring(item)
    end
    return table.concat(lines, "\n")
end

--- A block of "name: value" lines, re-read once a second. `source`: a function returning a
--- table (runs on the client), or { server = "lua returning a table" }.
function Builder:Values(source, opts)
    local widget = place(self, self.parent:AddText("..."), opts)
    widget.TextWrapPos = 0
    local element = register(self, { kind = "values", label = opts and opts.label, widget = widget, text = "" })
    local function set(text)
        text = GK.CutUtf8(text, 3000)
        element.text = text
        if element.shown ~= text then widget.Label = text element.shown = text end
    end
    element.get = function() return element.text end
    element.refresh = function()
        if type(source) == "function" then
            local ok, value = pcall(source)
            set(ok and valueLines(value) or ("ERROR: " .. tostring(value)))
        else
            local side = source.server and "server" or "client"
            GK.ConsoleAsk(side, tostring(source.server or source.client), function(ok, text, value)
                set((ok and value ~= nil) and valueLines(value) or text)
            end)
        end
    end
    element.refresh()
    return self
end

--- One expression with its value beside it, re-evaluated once a second on `side`
--- ("client", the default, or "server").
function Builder:Watch(code, side)
    side = side == "server" and "server" or "client"
    local name = place(self, self.parent:AddText((side == "server" and "S  " or "C  ") .. code))
    name:SetColor("Text", CYAN)
    local widget = self.parent:AddText("...")
    widget.SameLine = true
    local element = register(self, { kind = "watch", label = code, widget = widget, text = "" })
    element.get = function() return element.text end
    element.refresh = function()
        GK.ConsoleAsk(side, "return " .. code, function(_, text)
            text = GK.CutUtf8(text, 400)
            element.text = text
            if element.shown ~= text then widget.Label = "=  " .. text element.shown = text end
        end)
    end
    element.refresh()
    return self
end

-- --- Panels ---------------------------------------------------------------------------------

local function destroy(panel)
    if panel.container then pcall(function() panel.container:Destroy() end) end
    panel.container, panel.lastWidget = nil, nil
    panel.elements, panel.byLabel = {}, {}
end

local function construct(panel)
    if not window then return end
    destroy(panel)
    if panel.opts.window then
        local own = Ext.IMGUI.NewWindow(panel.name)
        own.Closeable = true
        own.Open = panel.open or false
        own:SetSize({ 520, 420 }, "FirstUseEver")
        panel.container = own
    else
        panel.container = tabBar:AddTabItem(panel.name .. "##dk_panel_" .. panel.id)
        tabs[panel.name] = panel.container
    end
    local builder = setmetatable({ panel = panel, parent = panel.container, sameLine = false }, Builder)
    local ok, problem = xpcall(panel.builder, function(err) return debug.traceback(tostring(err), 2) end, builder)
    if not ok then
        local text = "this panel's builder failed:\n" .. GK.ShortTrace(tostring(problem))
        panel.container:AddText(text):SetColor("Text", { 1.0, 0.42, 0.40, 1 })
        GK.ConsoleEmit("Error", panel.name, text)
    end
    panel.container:AddSeparator()
    panel.lastWidget = panel.container:AddText(panel.last and (panel.last.label .. ": " .. tostring(panel.last.text or "done")) or "")
    panel.lastWidget.TextWrapPos = 0
    panel.lastWidget:SetColor("Text", GREY)
end

GK.ConsoleHooks.built[#GK.ConsoleHooks.built + 1] = function(builtWindow, builtTabBar, builtTabs)
    window, tabBar, tabs = builtWindow, builtTabBar, builtTabs
    for _, name in ipairs(order) do construct(panels[name]) end
end

GK.ConsoleHooks.second[#GK.ConsoleHooks.second + 1] = function()
    for _, name in ipairs(order) do
        for _, element in ipairs(panels[name].elements) do
            if element.refresh then pcall(element.refresh, element) end
        end
    end
end

local counter = 0

--- Registers (or replaces) a panel: a tab of the GateKit window named `name`, filled by
--- `builder(p)`. `opts.window = true` makes it a window of its own instead, opened with
--- GK.UI.Open(name) or from the overlay's Panels menu.
function GK.UI.Panel(name, builder, opts)
    name = tostring(name)
    if type(builder) ~= "function" then error("GK.UI.Panel(name, builder): builder must be a function") end
    local panel = panels[name]
    if panel then
        destroy(panel)
    else
        counter = counter + 1
        panel = { name = name, id = counter, elements = {}, byLabel = {} }
        panels[name] = panel
        order[#order + 1] = name
        if opts and opts.window then
            GK.ConsoleAction("Panels", name, "client", ("GK.UI.Open(%q)"):format(name), "Opens this mod's window")
        end
    end
    panel.builder, panel.opts = builder, opts or {}
    construct(panel)
    return name
end

--- Takes a panel out of the overlay.
function GK.UI.Remove(name)
    local panel = panels[name]
    if not panel then return false end
    destroy(panel)
    panels[name] = nil
    if tabs then tabs[name] = nil end
    for i, known in ipairs(order) do if known == name then table.remove(order, i) break end end
    return true
end

--- Opens (or closes) a panel that is a window of its own.
function GK.UI.Open(name, on)
    local panel = panels[name]
    if not panel or not panel.opts.window then return false end
    panel.open = on ~= false
    if panel.container then panel.container.Open = panel.open end
    return panel.open
end

--- The names of the registered panels, in order.
function GK.UI.Panels()
    local out = {}
    for i, name in ipairs(order) do out[i] = name end
    return out
end

local function find(name, label)
    local panel = panels[name]
    if not panel then error("no panel " .. tostring(name)) end
    if not panel.container then GK.ConsoleSet({}) end   -- builds the overlay window, and with it the panel
    local element = panel.byLabel[label]
    if not element then error("panel " .. name .. " has no element " .. tostring(label)) end
    return panel, element
end

--- Does what a click would: presses a button, flips a toggle (or sets it to `value`), enters
--- `value` into an input, chooses `value` (an option or its number) in a list. For tests and
--- assistants: the panel can be driven without the mouse and without a screenshot.
function GK.UI.Press(name, label, value)
    local _, element = find(name, label)
    if not element.press then error(name .. ": " .. label .. " cannot be pressed") end
    element.press(value)
    return true
end

--- A panel as data: { name, last = { label, ok, text }, elements = { { kind, label, value } } }.
--- `last` is the outcome of the latest action (a server action's arrives a moment later).
function GK.UI.Read(name)
    local panel = panels[name]
    if not panel then return nil end
    if not panel.container then GK.ConsoleSet({}) end
    local elements = {}
    for i, element in ipairs(panel.elements) do
        local value
        if element.get then value = element.get() end
        elements[i] = { kind = element.kind, label = element.label, value = value }
    end
    return { name = name, last = panel.last, elements = elements, window = panel.opts.window or false }
end
