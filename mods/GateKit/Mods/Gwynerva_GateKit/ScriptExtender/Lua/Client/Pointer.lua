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
-- hit test, hover, tooltips and clicks follow. GateKit's Script Extender names the game's window on
-- the mouse events it injects, so an injected motion is taken as the pointer's own and a button
-- event injected after it is pressed where the game believes the pointer is: the instrument injects
-- events and writes nothing. A motion to (-1, -1) is the pointer leaving the frame. The real mouse
-- is not touched; a real motion inside the game's window moves the pointer again.
--
-- A build that injects events without the window's name does not move the pointer: it is told
-- apart by the `Injected` field of the input events (GK.Features.injectedInput), which the same
-- build brings, and the instrument refuses there.

local Probe = GK.Cheat or {}
GK.Cheat = Probe
local UI = GK.Gui

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
--- | "press" | "release" | "click" | "leave", button = 1 (left) | 2 | 3, wait = ms }. It injects
--- the motion and button events, which the game takes as its own pointer's ("leave" is a motion to
--- (-1, -1)); the real mouse is not used and no memory is written. Answers what the interface reported back after the wait: { ok, did, at = { x, y, px, py },
--- under = { hit, path, control, data, command, tooltipOn, widget } before and `after` the act,
--- events (the UI library's routed events seen at the root, with their sources), opened and closed
--- widgets, tips (the open tooltips), changed (the widget's picture before and after, as
--- GK.Gui.Changed tells it) }. A research instrument, a cheat: nothing may rest on it.
function Probe.Pointer(options)
    options = options or {}
    local act = options["do"] or "move"
    if not (GK.Features and GK.Features.injectedInput) then
        return { ok = false, error = "GK.Cheat.Pointer needs a Script Extender build whose injected mouse events name the game's window: "
            .. "GateKit's own build has it (gatekit se status)" }
    end
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
