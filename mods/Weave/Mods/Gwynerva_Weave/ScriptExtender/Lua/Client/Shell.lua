-- Weave, the shell: what every Weave window of every mod shares, whoever mounted it.
--
--   theme, text size     one choice for all windows, kept in "Script Extender/Gwynerva_Weave/settings.json"
--   input blocking       while a Weave window is open the game gets no keys / clicks (each switchable)
--   Esc                  closes the window opened last
--   rescue               every window back to its default place and size (`!weaverescue`)
--   the demo             Weave.Demo(true): every element of the kit in one window
--   the showcase         Weave.Showcase(true): a live dashboard of the controlled character
--
-- Hand-built IMGUI windows (not Weave documents) take part through Weave.RegisterWindow.
-- A mod that wants a key of its own before Weave blocks input claims it with Weave.OnKey.
-- Weave has no settings window of its own; a mod that has one offers it with
-- Weave.Settings.SetWindow(fn), and the demo's "Settings" menu item opens it.

Weave = Weave or {}
local UI = Weave
local now = Ext.Utils.MonotonicTime

local FILE = "Gwynerva_Weave/settings.json"
local DEFAULTS = {
    theme = "gate",
    font = "",            -- "" = Script Extender's own; or one of Weave.Fonts
    blockKeys = true,
    blockMouse = true,
}

local S = {}
for key, value in pairs(DEFAULTS) do S[key] = value end

local saved = false           -- whether a settings file was there when the game started
do
    local ok, text = pcall(Ext.IO.LoadFile, FILE)
    if ok and text and text ~= "" then
        local parsed, value = pcall(Ext.Json.Parse, text)
        if parsed and type(value) == "table" then
            saved = true
            for key, default in pairs(DEFAULTS) do
                if type(value[key]) == type(default) then S[key] = value[key] end
            end
        end
    end
end

local dirty, nextSave = false, 0
local listeners = {}
local windows = {}            -- { isOpen = fn, close = fn, reset = fn }
local keyClaims = {}          -- fn(e, key, down) -> true when the key is theirs
local settingsWindow          -- fn(open) of the mod that offers a settings window

local function known(list, value)
    for _, item in ipairs(list) do
        if item == value then return true end
    end
    return false
end

local function apply(key, value)
    if S[key] == value then return end
    S[key] = value
    dirty = true
    if key == "theme" then UI.SetTheme(value) end
    if key == "font" then UI.SetFont(value) end
    for _, fn in ipairs(listeners) do pcall(fn, key, value) end
end

UI.Settings = {}

--- A setting's value: Weave.Settings.Get("theme"). Keys: theme, font, blockKeys, blockMouse.
function UI.Settings.Get(key) return S[key] end

--- fn(key, value) after a setting changed.
function UI.Settings.OnChange(fn) listeners[#listeners + 1] = fn end

--- The settings as data, with the themes and fonts there are.
function UI.Settings.State()
    local out = { themes = UI.Themes(), fonts = UI.Fonts, saved = saved, anyOpen = UI.Blocking(), has = UI.Has() }
    for key in pairs(DEFAULTS) do out[key] = S[key] end
    return out
end

--- Changes settings: any of theme, font, blockKeys, blockMouse; `defaults = true` puts them
--- all back first. Unknown themes and fonts are ignored. Returns Weave.Settings.State().
function UI.Settings.Set(opts)
    opts = opts or {}
    if opts.defaults then
        for key, value in pairs(DEFAULTS) do apply(key, value) end
    end
    if opts.theme ~= nil and known(UI.Themes(), opts.theme) then apply("theme", opts.theme) end
    if opts.font ~= nil and (opts.font == "" or known(UI.Fonts, opts.font)) then apply("font", opts.font) end
    for _, key in ipairs({ "blockKeys", "blockMouse" }) do
        if opts[key] ~= nil then apply(key, opts[key] and true or false) end
    end
    return UI.Settings.State()
end

--- True when the settings came from the file (false on a first start: a mod may then carry
--- over choices it kept itself before Weave was a mod of its own).
function UI.Settings.Saved() return saved end

--- A mod offers its settings window: fn(open) opens (true), closes (false) or toggles (nil) it.
function UI.Settings.SetWindow(fn) settingsWindow = fn end

--- Opens the settings window a mod offered. False when none was offered.
function UI.Settings.Open(open)
    if not settingsWindow then return false end
    local ok, result = pcall(settingsWindow, open == nil and true or open)
    return ok and result ~= false
end

-- --- Windows that take part ------------------------------------------------------------------

--- Makes a hand-built window take part: while isOpen() the game's input is held back (as the
--- settings say), Esc calls close(), Weave.Rescue() calls reset() when given.
function UI.RegisterWindow(window) windows[#windows + 1] = window end

--- Is any Weave window (document or registered) open?
function UI.Blocking()
    if UI.AnyOpen() then return true end
    for _, window in ipairs(windows) do
        local ok, open = pcall(window.isOpen)
        if ok and open then return true end
    end
    return false
end

local function closeTop()
    if UI.CloseTop() then return true end
    for i = #windows, 1, -1 do
        local ok, open = pcall(windows[i].isOpen)
        if ok and open then pcall(windows[i].close) return true end
    end
    return false
end

--- The way back when a window cannot be found or grabbed: every Weave window returns to its
--- default place and size. Also `!weaverescue` in the Script Extender console.
function UI.Rescue()
    UI.ResetWindows()
    for _, window in ipairs(windows) do
        if window.reset then pcall(window.reset) end
    end
    return true
end

--- fn(e, key, down) sees every key before Weave blocks it; returning true claims the key (fn
--- decides itself whether to call e:PreventAction()). For a mod's own open / close key.
function UI.OnKey(fn) keyClaims[#keyClaims + 1] = fn end

-- --- The demo document --------------------------------------------------------------------

local builtins = {}           -- name -> the mounted document of a built-in window
local function builtin(name, open)
    builtins[name] = builtins[name] or UI.Mount(name)
    local doc = builtins[name]
    if not doc then return false end
    if open == nil then return doc:Toggle() end
    return doc:Open(open)
end

--- Opens (true), closes (false) or toggles (nil) Weave's demo window (ui/demo.weave), the
--- worked example of every element. False when Weave cannot run on this Script Extender.
function UI.Demo(open) return builtin("demo", open) end

--- Opens (true), closes (false) or toggles (nil) the showcase (ui/showcase.weave): a dashboard
--- of the controlled character drawn from live game data - stat cards, a chart, icon tiles,
--- badges and progress bars in one file. False when Weave cannot run on this Script Extender.
function UI.Showcase(open) return builtin("showcase", open) end

-- --- Saving, keys and mouse -----------------------------------------------------------------
-- While a text box is being edited Script Extender keeps the keys to itself; these handlers
-- see every other key. A key press is kept from the game, its release is always let through,
-- so a key held while a window opens cannot get stuck.

local function save()
    local ok, text = pcall(Ext.Json.Stringify, S, { Beautify = true })
    if ok then pcall(Ext.IO.SaveFile, FILE, text) end
end

Ext.Events.Tick:Subscribe(function()
    if not dirty then return end
    local t = now()
    if t >= nextSave then
        nextSave = t + 1500
        dirty = false
        saved = true
        save()
    end
end)

Ext.Events.Shutdown:Subscribe(function()
    if dirty then save() end
end)

Ext.Events.KeyInput:Subscribe(function(e)
    local key = tostring(e.Key)
    local down = e.Event == "KeyDown"
    for _, fn in ipairs(keyClaims) do
        local ok, claimed = pcall(fn, e, key, down)
        if ok and claimed then return end
    end
    if S.blockKeys and down and UI.Blocking() then
        if key == "ESCAPE" and not e.Repeat then closeTop() end
        e:PreventAction()
    end
end)

Ext.Events.MouseButtonInput:Subscribe(function(e)
    if e.Pressed and S.blockMouse and UI.Blocking() then e:PreventAction() end
end)

Ext.Events.MouseWheelInput:Subscribe(function(e)
    if S.blockMouse and UI.Blocking() then e:PreventAction() end
end)

Ext.RegisterConsoleCommand("weaverescue", function() UI.Rescue() end)
Ext.RegisterConsoleCommand("weavedemo", function() UI.Demo() end)
Ext.RegisterConsoleCommand("weaveshowcase", function() UI.Showcase() end)

UI.SetTheme(S.theme)
UI.SetFont(S.font)
