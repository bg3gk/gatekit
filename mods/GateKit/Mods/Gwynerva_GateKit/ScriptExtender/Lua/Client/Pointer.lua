-- GK.Cheat.Pointer: a pointer inside the game, for research. UI side.
--
-- A research instrument: it finds what in the interface answers a hover or a press at a place,
-- so that the answer can be given a handle, a command or an event. Nothing that ships - a verb, a
-- test, a recipe - may rest on it: a place on the screen is not an address of anything.
--
-- How the game's interface learns where the pointer is. The window manager's update
-- (sdl::SDLWindowManager::Update) takes each mouse motion event, finds its window by the event's
-- window id and writes the event's position into the engine's own mouse (ls::Mouse, its position
-- the first two floats); every motion event, whatever its window, then raises the input manager's
-- motion event, on which the interface reads that position and gives it to the UI library, whose
-- hit test, hover, tooltips and clicks follow. A motion event injected by Script Extender names no
-- window, so the position is not taken from it. The instrument therefore writes the position into
-- ls::Mouse itself (GK.Memory.Write) and injects the motion event that makes the interface read it;
-- a button event injected after it is pressed where the game believes the pointer is. The real
-- mouse is not touched. A real motion inside the game's window overwrites the position.
--
-- ls::Mouse is reached through the pointer the window manager's update keeps at a fixed place of
-- the executable; the place is the one of the build named below, and another build is refused.

local Probe = GK.Cheat or {}
GK.Cheat = Probe
local UI = GK.Gui

local MOUSE = { module = "bg3_dx11.exe", rva = 0x614fb70, update = 0x57AAA0 }
local EVENTS = { "PreviewMouseMove", "PreviewMouseDown", "PreviewMouseUp", "PreviewMouseWheel" }
local CONTROLS = { "Button", "ListBoxItem", "ComboBoxItem", "TabItem", "CheckBox", "Slider", "TextBox", "ComboBox" }

local State = Probe.PointerState or { subscriptions = {}, log = {} }
Probe.PointerState = State

local function guard(f, ...)
    local ok, a, b = pcall(f, ...)
    if ok then return a, b end
    return nil, tostring(a)
end

local function typeOf(object)
    local ok, name = pcall(function() return tostring(object.Type) end)
    return ok and name or "?"
end

local function addressOf(object)
    return object ~= nil and tostring(object):match("%((%x+)%)") or nil
end

local function label(element)
    local name = guard(function() return element.Name end)
    return typeOf(element) .. ((type(name) == "string" and name ~= "") and ("#" .. name) or "")
end

-- The engine's mouse: the address of ls::Mouse, or nil and why.
local function mouse()
    if not (GK.Memory and GK.Memory.Available and GK.Memory.Available()) then
        return nil, "the memory instrument is not there (GateKit's Script Extender in developer mode)"
    end
    local at = guard(GK.Memory.FromModule, MOUSE.module, MOUSE.rva)
    local address = at and guard(GK.Memory.Read, at, "uint64")
    if type(address) ~= "number" or address == 0 then return nil, "ls::Mouse was not found where this build keeps its pointer" end
    local owner = guard(GK.Memory.At, address)
    if owner and owner.Module then return nil, "the place read does not hold a pointer to ls::Mouse: another build of the game?" end
    return address
end

-- What lies under a point of the frame: the element the UI library's own hit test finds, the named
-- elements above it, the nearest control, the nearest element with a tooltip, and its widget.
local function under(px, py)
    local root = guard(function() return Ext.UI.GetRoot() end)
    local hit = root and guard(function() return root:HitTest({ X = px, Y = py }) end)
    if not hit then return { hit = false } end
    local out = { hit = label(hit), path = {} }
    local element, steps = hit, 0
    while element and steps < 80 do
        local typeName = typeOf(element)
        local name = guard(function() return element.Name end)
        if typeName == "ls.UIWidget" then out.widget = name break end
        if type(name) == "string" and name ~= "" and #out.path < 8 then out.path[#out.path + 1] = label(element) end
        if not out.control then
            for _, kind in ipairs(CONTROLS) do
                if typeName:find(kind, 1, true) then
                    out.control = label(element)
                    out.data = guard(function() return tostring(element.DataContext.Type) end)
                    out.command = guard(function() return element.Command end) ~= nil or nil
                    break
                end
            end
        end
        if not out.tooltipOn and guard(function() return element.ToolTip end) ~= nil then out.tooltipOn = label(element) end
        element, steps = guard(function() return element.VisualParent end), steps + 1
    end
    return out
end

-- A point of the frame that the hit test gives to an element or to something inside it: the frame
-- is tried on a grid, and the point nearest to the middle of those that hit is taken.
local function pointOf(element)
    local root = guard(function() return Ext.UI.GetRoot() end)
    local wanted = addressOf(element)
    if not (root and wanted) then return nil end
    local width, height = root.ActualWidth, root.ActualHeight
    local inside, hits, sumX, sumY = {}, {}, 0, 0
    local columns, rows = math.floor(width / 16), math.floor(height / 16)
    for row = 1, rows do
        for column = 1, columns do
            local x, y = (column - 0.5) * width / columns, (row - 0.5) * height / rows
            local hit = guard(function() return root:HitTest({ X = x, Y = y }) end)
            local key = hit and addressOf(hit)
            if key then
                if inside[key] == nil then
                    inside[key] = false
                    local e, steps = hit, 0
                    while e and steps < 80 do
                        if addressOf(e) == wanted then inside[key] = true break end
                        e, steps = guard(function() return e.VisualParent end), steps + 1
                    end
                end
                if inside[key] then
                    hits[#hits + 1] = { x, y }
                    sumX, sumY = sumX + x, sumY + y
                end
            end
        end
    end
    if #hits == 0 then return nil end
    local middleX, middleY = sumX / #hits, sumY / #hits
    local best, least
    for _, point in ipairs(hits) do
        local distance = (point[1] - middleX) ^ 2 + (point[2] - middleY) ^ 2
        if not least or distance < least then best, least = point, distance end
    end
    return best[1], best[2], #hits
end

local function listen()
    local root = guard(function() return Ext.UI.GetRoot() end)
    State.log = {}
    if not root then return end
    for _, event in ipairs(EVENTS) do
        local index = guard(function()
            return root:Subscribe(event, function(_, args)
                local source = guard(function() return args.Source end)
                local row = { event = event, source = source and label(source) or nil }
                local last = State.log[#State.log]
                if last and last.event == row.event and last.source == row.source then
                    last.times = (last.times or 1) + 1
                elseif #State.log < 40 then
                    State.log[#State.log + 1] = row
                end
            end)
        end)
        if index then State.subscriptions[#State.subscriptions + 1] = index end
    end
end

local function unlisten()
    local root = guard(function() return Ext.UI.GetRoot() end)
    for _, index in ipairs(State.subscriptions) do
        if root then guard(function() root:Unsubscribe(index) end) end
    end
    State.subscriptions = {}
end

local function tipTitles()
    local out = {}
    local tips = UI.Tips and guard(UI.Tips)
    for _, tip in ipairs(tips and tips.tips or {}) do
        out[#out + 1] = tip.handle .. (tip.pinned and " pinned" or "") .. ": " .. tostring(tip.title or (tip.about and tip.about.type))
    end
    return out
end

--- A pointer inside the game: GK.Cheat.Pointer{ at = { x, y } (fractions of the frame) | widget =
--- name, handle = a handle of GK.Gui.Look (the point is found by hit tests), do = "move" (default)
--- | "press" | "release" | "click" | "leave", button = 1 (left) | 2 | 3, wait = ms }. It writes
--- the game's own mouse position and injects the motion and button events; the real mouse is not
--- used. Answers what the interface reported back after the wait: { ok, did, at = { x, y, px, py },
--- under = { hit, path, control, data, command, tooltipOn, widget } before and `after` the act,
--- events (the UI library's routed events seen at the root, with their sources), opened and closed
--- widgets, tips (the open tooltips), changed (the widget's picture before and after, as
--- GK.Gui.Changed tells it) }. A research instrument, a cheat: nothing may rest on it.
function Probe.Pointer(options)
    options = options or {}
    local act = options["do"] or "move"
    local address, problem = mouse()
    if not address then return { ok = false, error = problem } end
    local root = guard(function() return Ext.UI.GetRoot() end)
    if not root then return { ok = false, error = "the interface has no root" } end
    local width, height = root.ActualWidth, root.ActualHeight
    local px, py, points
    if act == "leave" then
        px, py = -1, -1
    elseif type(options.at) == "table" then
        px, py = math.floor((options.at[1] or options.at.x) * width), math.floor((options.at[2] or options.at.y) * height)
    elseif options.widget and options.handle then
        local row, number = UI.Handle(options.widget, options.handle)
        if not row then return { ok = false, error = number } end
        local element = type(number) == "number" and row.containers and row.containers[number] or row.element
        local x, y, count = pointOf(element)
        if not x then return { ok = false, error = "no point of the frame hits " .. tostring(options.handle) .. ": it is covered or not drawn" } end
        px, py, points = math.floor(x), math.floor(y), count
    else
        return { ok = false, error = "where: at = { x, y } in fractions of the frame, or widget and handle" }
    end
    local before = act ~= "leave" and under(px, py) or nil
    local widgetName = before and before.widget
    local picture = widgetName and UI.Picture(widgetName) or UI.Picture()
    local tipsBefore = tipTitles()
    local button = options.button or 1
    unlisten()
    listen()
    GK.Memory.Write(address, "float", px + 0.0)
    GK.Memory.Write(address + 4, "float", py + 0.0)
    GK.Memory.Write(address + 16, "uint8", act == "leave" and 0 or 1)
    Ext.Input.InjectMouseMove(px, py)
    local looks, held = 0, false
    if act == "release" then Ext.Input.InjectMouseButton(button, false, px, py) end
    local result = { ok = true, did = act, at = { x = px / width, y = py / height, px = px, py = py, points = points }, under = before }
    return GK.Observe(result, function(ticks, final)
        looks = looks + 1
        -- the press a look after the motion, the release two looks after the press: a press and a
        -- release in one frame are not a click to the game
        if (act == "press" or act == "click") and looks == 2 and not held then
            Ext.Input.InjectMouseButton(button, true, px, py)
            held = true
        end
        if act == "click" and held and looks == 4 then
            Ext.Input.InjectMouseButton(button, false, px, py)
            held = false
        end
        if not final then return nil end
        if act == "click" and held then Ext.Input.InjectMouseButton(button, false, px, py) end
        unlisten()
        local after = widgetName and UI.Picture(widgetName) or UI.Picture()
        result.events = State.log
        result.changed = UI.Changed(picture, after)
        result.after = act ~= "leave" and under(px, py) or nil
        result.tipsBefore = tipsBefore
        result.tips = tipTitles()
        return result
    end, options.wait or 900)
end

return true
