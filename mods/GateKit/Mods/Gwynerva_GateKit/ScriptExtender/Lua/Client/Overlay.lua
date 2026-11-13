-- The overlay: GateKit's part of the windows inside the game.
--
--   the open / close key    one key for the console; rebindable
--   the HUD button          a small button under the minimap; can be turned off
--   the settings window     ui/overlay.weave: the key, the HUD button, and Weave's own settings
--
-- What every Weave window of every mod shares - theme, text size, input blocking, Esc, the
-- rescue - belongs to the Weave mod (Weave.Settings, Weave.RegisterWindow, Weave.Rescue). The
-- GK.Overlay* functions below still answer for all of it, so callers see one set of settings;
-- the Weave ones are passed through to Weave.
--
-- GateKit's own settings are kept in "Script Extender/Gwynerva_GateKit/overlay.json".
-- Without a Script Extender Weave can run on (Mount returns nil) there is no settings window
-- and no HUD button; the settings, the key and input blocking work all the same.

local DIR = "Gwynerva_GateKit/"
local FILE = DIR .. "overlay.json"
local UI = Weave
local now = Ext.Utils.MonotonicTime

-- This file's own settings; theme, font, blockKeys, blockMouse are Weave's.
local DEFAULTS = {
    key = "F11",
    hudButton = true,
    hudX = 0,             -- the HUD button's offset from its place under the minimap, in pixels
    hudY = 0,
}
local WEAVE_KEYS = { theme = true, font = true, blockKeys = true, blockMouse = true }
local O = {}
for key, value in pairs(DEFAULTS) do O[key] = value end

local function readJson(file)
    local ok, text = pcall(Ext.IO.LoadFile, file)
    if not ok or not text or text == "" then return nil end
    local parsed, value = pcall(Ext.Json.Parse, text)
    return parsed and type(value) == "table" and value or nil
end

do
    -- Before the overlay had its own file these lived in the console's settings.
    local saved = readJson(FILE) or readJson(DIR .. "console.json") or {}
    for key, default in pairs(DEFAULTS) do
        if type(saved[key]) == type(default) then O[key] = saved[key] end
    end
    -- Before Weave was a mod of its own, its four settings were kept here: carried over once.
    if not UI.Settings.Saved() then
        local carried = {}
        for key in pairs(WEAVE_KEYS) do
            if saved[key] ~= nil then carried[key] = saved[key] end
        end
        if next(carried) then UI.Settings.Set(carried) end
    end
end

local dirty, nextSave = false, 0
local listeners = {}
local capturingKey = false
local settings                -- the settings document, once asked for
local hud                     -- the HUD button's document
local hudState = { found = false, visible = false, hovered = false }

-- GateKit's own Weave components (overlay.weave, hud.weave) are found next to this file.
UI.AddRoot("Mods/Gwynerva_GateKit/ScriptExtender/Lua/Client/ui/")

GK.Overlay = {}

--- A setting's value: GK.Overlay.Get("font").
function GK.Overlay.Get(key)
    if WEAVE_KEYS[key] then return UI.Settings.Get(key) end
    return O[key]
end

--- fn(key, value) after a setting changed (GateKit's or Weave's).
function GK.Overlay.OnChange(fn) listeners[#listeners + 1] = fn end

--- Makes a hand-built window part of the overlay: while isOpen() the game's input is held
--- back (as the settings say) and Esc calls close(). Passed on to Weave.RegisterWindow.
function GK.Overlay.Register(window) UI.RegisterWindow(window) end

local FONT_LABELS = { { value = "", label = "As Script Extender sets it" } }
for _, name in ipairs(UI.Fonts) do FONT_LABELS[#FONT_LABELS + 1] = { value = name, label = name } end

local function syncSettings()
    if not settings or not settings.state then return end
    local state = { themes = UI.Themes(), fonts = FONT_LABELS, capturing = capturingKey }
    for key in pairs(DEFAULTS) do state[key] = O[key] end
    for key in pairs(WEAVE_KEYS) do state[key] = UI.Settings.Get(key) end
    settings:Set(state)
end

local function notify(key, value)
    syncSettings()
    for _, fn in ipairs(listeners) do pcall(fn, key, value) end
end

UI.Settings.OnChange(notify)

local function apply(key, value)
    if O[key] == value then return end
    O[key] = value
    dirty = true
    notify(key, value)
end

--- Changes overlay settings: any of theme, font, key, blockKeys, blockMouse, hudButton, hudX,
--- hudY; `settings = true | false` opens or closes the settings window; `defaults = true` puts
--- everything back. Returns GK.OverlayState().
function GK.OverlaySet(opts)
    opts = opts or {}
    local weave = {}
    for key in pairs(WEAVE_KEYS) do weave[key] = opts[key] end
    weave.defaults = opts.defaults
    if next(weave) then UI.Settings.Set(weave) end
    if opts.defaults then
        for key, value in pairs(DEFAULTS) do apply(key, value) end
    end
    if opts.key ~= nil and tostring(opts.key) ~= "" then apply("key", tostring(opts.key)) end
    if opts.hudButton ~= nil then apply("hudButton", opts.hudButton and true or false) end
    for _, key in ipairs({ "hudX", "hudY" }) do
        if tonumber(opts[key]) then apply(key, math.floor(tonumber(opts[key]) + 0.5)) end
    end
    if opts.settings ~= nil then GK.OverlaySettings(opts.settings) end
    if opts.rescue then GK.OverlayRescue() end
    return GK.OverlayState()
end

--- The overlay as data: the settings, the themes there are, where the HUD button is.
function GK.OverlayState()
    local out = { themes = UI.Themes(), fonts = UI.Fonts, capturingKey = capturingKey,
        settingsOpen = settings ~= nil and settings:IsOpen() or false, anyOpen = UI.Blocking(), has = UI.Has(),
        hud = { visible = hudState.visible, found = hudState.found, x = hudState.x, y = hudState.y,
            hovered = hudState.hovered } }
    for key in pairs(DEFAULTS) do out[key] = O[key] end
    for key in pairs(WEAVE_KEYS) do out[key] = UI.Settings.Get(key) end
    return out
end

--- The way back when a window cannot be found or grabbed: every Weave window (the console
--- included) returns to its default place and size. Also `!gkrescue` (or `!weaverescue`) in
--- the Script Extender console.
function GK.OverlayRescue()
    return UI.Rescue()
end

-- --- The settings window ------------------------------------------------------------------

local function status(text)
    if settings and settings.state then settings:Set("status", text) end
end

local function buildSettings()
    -- The window is ui/overlay.weave; these are the functions its template calls.
    settings = UI.Mount("overlay", { env = {
        set = function(key, value) GK.OverlaySet({ [key] = value }) end,
        captureKey = function()
            capturingKey = true
            syncSettings()
        end,
        ejectTheme = function()
            status("Written: " .. UI.Eject("theme.css") .. " - edit it, the windows follow.")
        end,
        openDemo = function() UI.Demo(true) end,
        rescue = function()
            GK.OverlayRescue()
            status("Every window is back at its default place.")
        end,
        defaults = function()
            GK.OverlaySet({ defaults = true })
            status("Defaults restored.")
        end,
    } })
    syncSettings()
end

--- Opens (true), closes (false) or toggles (nil) the overlay settings window. False when Weave
--- cannot run on this Script Extender.
function GK.OverlaySettings(open)
    if not settings then buildSettings() end
    if not settings then return false end
    if open == nil then return settings:Toggle() end
    return settings:Open(open)
end

-- The demo's "Settings" menu item (and any other mod asking Weave for settings) opens this one.
UI.Settings.SetWindow(GK.OverlaySettings)

-- --- The HUD button -----------------------------------------------------------------------
-- The game lays its HUD out on a fixed canvas (3840 x 2160 units) scaled to the screen; the
-- minimap sits in a container at the top right. Its size is read from the game's own UI tree
-- (read only), so the button stays under the minimap at any resolution and UI scale, and
-- disappears with it in dialogues, cutscenes and menus. Noesis may only be touched from
-- Ext.UI.Defer; nothing is ever written to it.

local function findNamed(element, name, depth, budget)
    if budget.left <= 0 then return nil end
    budget.left = budget.left - 1
    local count = 0
    pcall(function() count = element.VisualChildrenCount end)
    local children = {}
    for i = 1, count do
        local ok, child = pcall(function() return element:VisualChild(i) end)
        if ok and child then
            local childName
            pcall(function() childName = child:GetProperty("Name") end)
            if childName == name then return child end
            children[#children + 1] = child
        end
    end
    if depth <= 1 then return nil end
    for _, child in ipairs(children) do
        local found = findNamed(child, name, depth - 1, budget)
        if found then return found end
    end
    return nil
end

local function number(element, property)
    local ok, value = pcall(function() return element:GetProperty(property) end)
    value = ok and tonumber(value) or nil
    if value and value == value and value > 0 then return value end     -- not NaN
    return nil
end

local function measureMinimap()
    local found = false
    pcall(function()
        local root = Ext.UI.GetRoot()
        local content = root and root:Find("ContentRoot")
        if not content then return end
        local widget = findNamed(content, "Minimap", 1, { left = 80 })
        if not widget then return end
        local visible = true
        pcall(function() visible = widget:GetProperty("IsVisible") ~= false end)
        pcall(function() visible = visible and tostring(widget:GetProperty("Visibility")) == "Visible" end)
        local container = findNamed(widget, "MinimapContainer", 8, { left = 300 })
        if not container then return end
        local canvasW, canvasH = number(widget, "ActualWidth"), number(widget, "ActualHeight")
        local w, h = number(container, "ActualWidth"), number(container, "ActualHeight")
        if not (canvasW and canvasH and w and h) then return end
        pcall(function() visible = visible and container:GetProperty("IsVisible") ~= false end)
        hudState.canvas = { canvasW, canvasH }
        hudState.minimap = { w, h }
        hudState.minimapVisible = visible
        found = true
    end)
    hudState.found = found
end

local function placeHud()
    local show = O.hudButton and hudState.found and hudState.minimapVisible
    if show and not hud then
        hud = UI.Mount("hud", { env = {
            toggle = function() if GK.ToggleConsole then GK.ToggleConsole() end end,
            settings = function() GK.OverlaySettings(true) end,
            -- a click on the button must not also be a click into the world behind it
            hover = function(on) hudState.hovered = on end,
        } })
    end
    if not hud or not hud.window then return end
    if hudState.window ~= hud.window then       -- built anew: it has to be placed again
        hudState.window, hudState.placed, hudState.visible = hud.window, nil, false
    end
    if not show then
        if hudState.visible then hud:Close() hudState.visible = false hudState.hovered = false end
        return
    end
    local viewport = UI.Backend.viewport()
    -- the canvas is fitted into the picture whole: the tighter of the two directions decides
    local scale = math.min(viewport[1] / hudState.canvas[1], viewport[2] / hudState.canvas[2])
    local x = math.floor(viewport[1] - hudState.minimap[1] * scale / 2 + O.hudX + 0.5)
    -- the game's own round buttons hang over the container's lower edge by about 60 units
    local y = math.floor((hudState.minimap[2] + 64) * scale + O.hudY + 0.5)
    if hudState.placed ~= x .. ":" .. y then
        hudState.placed = x .. ":" .. y
        hudState.x, hudState.y = x, y
        UI.Backend.setPos(hud.window, { x, y }, { 0.5, 0 })
    end
    if not hudState.visible then
        -- shown without doc:Open(), which would bring it to the front and take the keyboard
        hudState.visible = true
        UI.Backend.setOpen(hud.window, true)
        hud.open = true
    end
end

-- --- Per frame ----------------------------------------------------------------------------

local function save()
    local ok, text = pcall(Ext.Json.Stringify, O, { Beautify = true })
    if ok then pcall(Ext.IO.SaveFile, FILE, text) end
end

local nextHud = 0
Ext.Events.Tick:Subscribe(function()
    local t = now()
    if dirty and t >= nextSave then
        nextSave = t + 1500
        dirty = false
        save()
    end
    if t >= nextHud then
        nextHud = t + 500
        if O.hudButton then
            Ext.UI.Defer(measureMinimap)       -- answers a frame later; placeHud uses the last answer
        end
        local ok, problem = pcall(placeHud)
        if not ok then print("[GateKit] hud: " .. tostring(problem)) end
    end
end)

Ext.Events.Shutdown:Subscribe(save)

-- --- Keys and mouse -----------------------------------------------------------------------
-- Weave holds keys and clicks back while a window is open and closes the top one on Esc.
-- GateKit claims its own key (and every key while one is being captured) before that.

UI.OnKey(function(e, key, down)
    if capturingKey then
        if down and not e.Repeat then
            capturingKey = false
            if key ~= "ESCAPE" then apply("key", key) end
            syncSettings()
        end
        e:PreventAction()
        return true
    end
    if key == O.key then
        if down and not e.Repeat and GK.ToggleConsole then GK.ToggleConsole() end
        e:PreventAction()
        return true
    end
    return false
end)

Ext.Events.MouseButtonInput:Subscribe(function(e)
    if e.Pressed and hudState.hovered and hudState.visible then e:PreventAction() end
end)

Ext.RegisterConsoleCommand("gkui", function() GK.OverlaySettings() end)
Ext.RegisterConsoleCommand("gkrescue", function() GK.OverlayRescue() end)
