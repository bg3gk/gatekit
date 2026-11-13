-- GK.Focus (client side): a calm screen while an agent plays.
--
-- An agent plays with the game window in the background: the real mouse is never moved and the
-- window never takes focus. The game still runs its pointer logic for a mouse that is not there:
-- the picking helper aims at screen point (0, 0), the path preview and the "out of reach" text
-- jump to the top left corner, and the cursor text follows whatever the empty pointer touches.
-- For whoever watches that reads as a cursor flying around. Calm mode switches that pointer
-- logic off while the agent plays: the picking helper is deactivated every tick and the
-- CursorText widget is collapsed. Nothing is written to the operating system's cursor.
--
-- Where the agent looks is shown by the server side of GK.Focus with the game's own markers
-- (the destination beam, the target circle, the map ping) and by GK.Camera.
--
-- A person can take over at any time: when the real mouse moves over the game window the calm
-- pauses for Focus.HumanPause seconds (and for as long as the mouse keeps moving).

local Focus = GK.Focus or {}
GK.Focus = Focus

Focus.HumanPause = 10      -- seconds the calm waits after a real mouse movement over the window
Focus.Calm = Focus.Calm or false

local function try(fn, ...)
    local ok, result = pcall(fn, ...)
    if ok then return result end
end

local function now() return Ext.Utils.MonotonicTime() / 1000 end

-- The CursorText widget; Noesis is touched only from Ext.UI.Defer.
local function findCursorText()
    local root = try(Ext.UI.GetRoot)
    if not root then return nil end
    local stack, visited = { { root, 0 } }, 0
    while #stack > 0 and visited < 20000 do
        local top = table.remove(stack)
        local element, depth = top[1], top[2]
        visited = visited + 1
        if try(function() return element.Name end) == "CursorText" then return element end
        if depth < 60 then
            local n = try(function() return element.VisualChildrenCount end) or 0
            for i = n, 1, -1 do
                local child = try(function() return element:VisualChild(i) end)
                if child then stack[#stack + 1] = { child, depth + 1 } end
            end
        end
    end
end

local function setCursorText(visible)
    Ext.UI.Defer(function()
        local widget = Focus.CursorText
        if not (widget and try(function() return widget.Name end) == "CursorText") then
            widget = findCursorText()
            Focus.CursorText = widget
        end
        if widget then
            try(function() widget.Visibility = visible and "Visible" or "Collapsed" end)
        end
    end)
end

local function setPicking(active)
    local helper = try(Ext.UI.GetPickingHelper, 1)
    if helper then try(function() helper.Activated = active end) end
end

local function mousePosition()
    local helper = try(Ext.UI.GetPickingHelper, 1)
    local p = helper and try(function() return helper.WindowCursorPos end)
    return p and { p[1], p[2] } or nil
end

local function pausedForHuman()
    local p = mousePosition()
    if p and (p[1] ~= 0 or p[2] ~= 0) then
        local last = Focus.LastMouse
        if last and (last[1] ~= p[1] or last[2] ~= p[2]) then
            Focus.HumanUntil = now() + Focus.HumanPause
        end
        Focus.LastMouse = p
    end
    return Focus.HumanUntil and now() < Focus.HumanUntil
end

local function onTick()
    if not Focus.Calm then return end
    if pausedForHuman() then
        if not Focus.Yielded then
            Focus.Yielded = true
            setPicking(true)
            setCursorText(true)
        end
        return
    end
    if Focus.Yielded then
        Focus.Yielded = false
        setCursorText(false)
    end
    setPicking(false)
    -- the widget may be rebuilt (a loading screen, a menu): collapse it again now and then
    local t = now()
    if not Focus.TextCheckedAt or t - Focus.TextCheckedAt > 2 then
        Focus.TextCheckedAt = t
        setCursorText(false)
    end
end

--- Calm mode on (true) or off (false); returns the state. Off gives the pointer back at once.
function Focus.SetCalm(on)
    on = on and true or false
    if on and not Focus.Subscription then
        Focus.Subscription = Ext.Events.Tick:Subscribe(onTick)
    end
    if Focus.Calm ~= on then
        Focus.Calm = on
        Focus.Yielded = false
        Focus.TextCheckedAt = nil
        if on then
            setPicking(false)
            setCursorText(false)
        else
            setPicking(true)
            setCursorText(true)
        end
    end
    return Focus.State()
end

function Focus.State()
    local outlined = {}
    for id in pairs(Focus.Outlined or {}) do outlined[#outlined + 1] = id end
    return { calm = Focus.Calm, yieldedToHuman = Focus.Yielded or false, outlined = outlined,
             picking = try(function() return Ext.UI.GetPickingHelper(1).Activated end) }
end

-- ---------------------------------------------------------------------------------------
-- The agent's outline: a violet, slowly pulsing contour around what the agent aims at.
--
-- The engine draws the outline a player sees when the mouse is over a character or an item
-- (ls::OutlineComponent: one colour per player, drawn by ecl::OutlineSystem). The agent gets an
-- entry of its own (player id Focus.OutlinePlayer, which no real player has, so the game's own
-- hover outline keeps its entry), coloured Focus.OutlineColor and pulsed every tick between
-- Focus.OutlineDim and full brightness. Off empties our entry again. Purely visual, client side.

Focus.OutlineColor = { 0.70, 0.25, 1.00 }   -- violet
Focus.OutlineDim = 0.35                      -- the darkest point of the pulse, as a share
Focus.OutlinePulse = 1.1                     -- pulses per second
Focus.OutlinePlayer = 99
Focus.Outlined = Focus.Outlined or {}        -- uuid -> true

local function withoutOurs(players)
    local keep = {}
    for _, p in ipairs(players or {}) do
        if p.PlayerId ~= Focus.OutlinePlayer then
            keep[#keep + 1] = { Color = { p.Color[1], p.Color[2], p.Color[3] }, PlayerId = p.PlayerId }
        end
    end
    return keep
end

local function setOutline(id, color)
    local e = try(Ext.Entity.Get, id)
    if not e then return false end
    if not e.Outline then
        if not try(function() return e:CreateComponent("Outline") end) then return false end
    end
    local outline = e.Outline
    if not outline then return false end
    local players = withoutOurs(outline.Players)
    if color then players[#players + 1] = { Color = color, PlayerId = Focus.OutlinePlayer } end
    return try(function() outline.Players = players; return true end) or false
end

local function pulseTick()
    if not next(Focus.Outlined) then return end
    local t = now()
    local wave = 0.5 + 0.5 * math.sin(t * Focus.OutlinePulse * 2 * math.pi)
    -- a slight drift of hue between violet and magenta makes the contour "float"
    local drift = 0.5 + 0.5 * math.sin(t * 0.37 * 2 * math.pi)
    local k = Focus.OutlineDim + (1 - Focus.OutlineDim) * wave
    local c = Focus.OutlineColor
    local color = { (c[1] + 0.15 * drift) * k, c[2] * k, (c[3] - 0.1 * drift) * k }
    for id in pairs(Focus.Outlined) do
        if not setOutline(id, color) then Focus.Outlined[id] = nil end
    end
end

--- The agent's pulsing violet outline on a character or an item (on = true) or off (false).
function Focus.Outline(id, on)
    if type(id) ~= "string" or id == "" then return false end
    if on then
        if not Focus.PulseSubscription then
            Focus.PulseSubscription = Ext.Events.Tick:Subscribe(function() GK.Focus.PulseTick() end)
        end
        if not setOutline(id, Focus.OutlineColor) then return false end
        Focus.Outlined[id] = true
    else
        Focus.Outlined[id] = nil
        setOutline(id, nil)
    end
    return true
end

--- Takes every outline of ours away.
function Focus.ClearOutlines()
    for id in pairs(Focus.Outlined) do
        Focus.Outlined[id] = nil
        setOutline(id, nil)
    end
    return true
end

Focus.PulseTick = pulseTick

-- The server side of GK.Focus says what to outline: { id, on } or { clear = true }.
Ext.Net.CreateChannel(ModuleUUID, "GK_Focus", function(msg)
    if type(msg) ~= "table" then return end
    if msg.clear then GK.Focus.ClearOutlines() end
    if msg.id then GK.Focus.Outline(msg.id, msg.on) end
end)

return Focus
