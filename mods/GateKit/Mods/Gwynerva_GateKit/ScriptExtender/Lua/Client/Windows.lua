-- GK.Gui: every window of the game (GK.UI is the overlay's own panels, Client/Panels.lua), operated from code - opened, read, clicked through, closed -
-- without the mouse and without window focus. UI side (`ui` in `gatekit`: the code runs inside
-- Ext.UI.Defer).
--
-- How the game's UI is built (found 2026-11-13, game 4.1.1.7631656):
--   * Every window is a widget (ls.UIWidget) directly under the "ContentRoot" element; its Name is
--     the x:Name of its XAML page ("JournalQuest", "CharacterPanel", "CharacterLevelUp" ...).
--     A widget that is not open is not in the tree at all, so the list of open windows is one
--     short loop.
--   * Windows open and close through game input events: a widget's DataContext has CustomEvent,
--     and CustomEvent:Execute(Ext.UI.Box("OpenJournalQuests")) does what the key does. The 74
--     event names the game's own buttons use are in GK.Gui.Events. A widget closes itself with
--     its own CustomEvent("CloseWidget").
--   * A button's click is often a chain written in the XAML (several commands, property
--     changes, and steps delayed by timers - the level-up "Accept" button runs FinishLevelUp
--     1.4 s after the click). GK.UICatalog (generated from the game's XAML, optional: see
--     BootstrapClient.lua) holds those chains; GK.Gui.Click runs them when it is there.
--
-- Everything returns a result table and never throws for a game-side problem: { ok, ... } or
-- { ok = false, error = "..." }. Every Noesis access is guarded.
--
--   GK.Gui.Open()                      the open widgets: { { name, data, file } }
--   GK.Gui.IsOpen(name)                a widget name, or a window name of GK.Gui.Windows
--   GK.Gui.Show(window)                opens a window by name ("journal", "map", "inventory", ...)
--                                     or raises a game input event by its name ("ToggleSpells")
--   GK.Gui.Close(name)                 closes a widget or window; GK.Gui.CloseAll() closes every
--                                     window that is not part of the HUD
--   GK.Gui.Controls(name, filter)      the controls of an open widget, with what a click runs
--   GK.Gui.Click(name, control, n)     clicks a control the way a player does (the XAML chain)
--   GK.Gui.Input(name, event)          what a key does in that widget ("UIAccept", "UICancel" ...)
--   GK.Gui.Read(name, options)         the widget's data as plain values (see GK.Gui.Snapshot)
--   GK.Gui.Item(text) / GK.Gui.ItemDo(action, text)   an item by name; equip, unequip, use, drop
--   GK.Gui.Windows                     the registry of named windows - add your own entries
--
-- Every GK.Gui verb is fair or an observation (Shared/VerbClasses.json). What reaches past the
-- controls a player has lives in GK.Cheat on this side, under the same kind of name:
--   GK.Cheat.Run(name, command, param) runs any command of the widget's data (strings are boxed)
--   GK.Cheat.Set(name, path, value)    sets any property of the widget's data and reads it back
--   GK.Cheat.Step / Auto / Resolve     a window's choices made by its step function
--   GK.Cheat.Quiet / Recover / AimBest
--   GK.Cheat.Mark(name, control, opts) tints a control's text with a brush of the game's theme for
--                                     a time of the game: what an agent is about to press, shown
--
-- Waiting is the caller's job (a widget appears a frame or more after the event): poll
-- GK.Gui.IsOpen.

GK.Gui = GK.Gui or {}
local UI = GK.Gui
GK.Cheat = GK.Cheat or {}
local Cheat = GK.Cheat
local catalog = GK.UICatalog or { widgets = {}, library = {} }

-- The widgets that are always there while playing: the HUD. Not "windows".
UI.Hud = {
    HudIndicator = true, OverheadInfo = true, Minimap = true, TargetInfo = true, TurnModeInfo = true,
    CombatLog = true, CombatantsOverlay = true, WorldTooltips = true, HotBar = true, PlayerPortraits = true,
    CursorText = true, PassiveRoll = true, AlwaysOnTopOverlay = true, Overlay = true, WorldContextMenu = true,
    NotificationWidget = true, DragAndDropPreview = true, ScreenFade = true, CrossplayOverlay = true,
    PinnedTooltips = true, ActionRadials = false,
}

-- The game input events its own UI raises through CustomEvent (from the XAML, 2026-11-13).
UI.Events = {
    "CharacterAssignOpen", "CloseAccountManagement", "CloseAll", "CloseCompanionsPanel", "CloseCondition",
    "CloseConnectivityMenu", "CloseDialogueHistory", "CloseFriendsList", "CloseGameMenu", "CloseGameMenuWidget",
    "CloseInsertWidget", "CloseInventoryPanel", "CloseLearnSpells", "CloseMessageBox", "ClosePanelSelect",
    "ClosePartyPanel", "ClosePlayerPortraits", "CloseRadials", "CloseSelectionFlyOut", "CloseWidget",
    "ConnectivityMenuOpen", "EditCustom", "LoadGameClose", "LoadGameOpen", "OpenAccountManagement",
    "OpenActionRadials", "OpenActionRadialsAndCallAllies", "OpenActionRadialsEnd", "OpenAlchemyPanel", "OpenCredits",
    "OpenCrossplayProfile", "OpenDialogueHistory", "OpenDifficultySettings", "OpenExamineSurfaces", "OpenFriendsList",
    "OpenGammaCalibration", "OpenHDR", "OpenInventory", "OpenJournalQuests", "OpenMainMenu", "OpenModBrowser",
    "OpenModBrowserDownload", "OpenModBrowserInstalled", "OpenMoreInfo", "OpenNewSaveDialog", "OpenOptions",
    "OpenPlayerPortraits", "OpenSensitiveSettings", "OpenSettings", "OpenSignUp", "OpenSpellbook",
    "OpenSplashScreen", "OpenTadpoleTree", "RemovePanelState", "SaveGameClose", "ShowInterrupts", "ShowJournalMap",
    "ShowSplashScreen", "SplitItemToggle", "ToggleAlchemy", "ToggleCharacterPanel", "ToggleCompanionsPanel",
    "ToggleGameMenu", "ToggleIllithid", "ToggleInterrupts", "ToggleInventory", "ToggleJournalDialogues",
    "ToggleJournalInspiration", "ToggleJournalMap", "ToggleJournalQuests", "ToggleJournalTutorial",
    "TogglePartyPanel", "ToggleShortcutMenu", "ToggleSpells",
}
local isEvent = {}
for _, name in ipairs(UI.Events) do isEvent[name] = true end

-- Named windows. `widget`: the widget that is open while the window is (one widget can host
-- several windows: the character panel shows the inventory, the spellbook, the sheet ...);
-- `open`: a game input event, or a function(UI) that opens it; `close` (optional): an event or a
-- function; the default is the widget's own CloseWidget. Extend freely: GK.Gui.Windows.x = {...}.
UI.Windows = UI.Windows or {}
local W = UI.Windows
W.journal = W.journal or { widget = "JournalQuest", open = "ToggleJournalQuests", about = "quests and their objectives" }
W.quests = W.quests or W.journal
W.map = W.map or { widget = "JournalMap", open = "ShowJournalMap", close = "CloseAll", about = "the world map and waypoints" }  -- its own close button raises CloseAll
W.dialogues = W.dialogues or { widget = "JournalDialogues", open = "ToggleJournalDialogues", about = "past dialogues" }
W.inspiration = W.inspiration or { widget = "JournalInspiration", open = "ToggleJournalInspiration", about = "backgrounds and inspiration" }
W.tutorials = W.tutorials or { widget = "JournalTutorial", open = "ToggleJournalTutorial" }
W.inventory = W.inventory or { widget = "CharacterPanel", page = "ToggleInventory", open = "OpenInventory", about = "inventory of the selected character" }
W.spellbook = W.spellbook or { widget = "CharacterPanel", page = "ToggleSpells", open = "OpenSpellbook", about = "spells of the selected character" }
W.character = W.character or { widget = "CharacterPanel", open = "ToggleCharacterPanel", about = "character sheet" }
W.alchemy = W.alchemy or { widget = "CharacterPanel", page = "ToggleAlchemy", open = "OpenAlchemyPanel" }
W.reactions = W.reactions or { widget = "CharacterPanel", page = "ToggleInterrupts", open = "ToggleInterrupts", about = "reaction settings" }
W.illithid = W.illithid or { widget = "TadpolePowersTree", open = "OpenTadpoleTree" }
W.party = W.party or { widget = "PartyPanel", open = "TogglePartyPanel" }
W.companions = W.companions or { widget = "CompanionsPanel", open = "ToggleCompanionsPanel", needs = "camp: companions waiting there" }
W.gamemenu = W.gamemenu or { widget = "GameMenu", open = "ToggleGameMenu", close = "CloseGameMenu" }
W.options = W.options or { widget = "GameOptions", open = "OpenOptions" }
W.history = W.history or { widget = "DialogueHistory", open = "OpenDialogueHistory", close = "CloseDialogueHistory", needs = "a dialogue on screen" }
W.load = W.load or { widget = "LoadGame", open = "LoadGameOpen", close = "LoadGameClose" }
W.save = W.save or { widget = "SaveGame", open = "OpenNewSaveDialog", close = "SaveGameClose" }
W.surfaces = W.surfaces or { widget = "Examine", open = "OpenExamineSurfaces", needs = "a surface under the cursor" }
W.levelup = W.levelup or {
    widget = "CharacterLevelUp", about = "level-up of the selected character (needs a level available)",
    open = function(ui)
        -- StartLevelUp needs the character's view model as its parameter: without it it does nothing
        local hotbar = ui.Data("HotBar")
        if not hotbar then return false, "no HotBar" end
        local player = hotbar.CurrentPlayer
        local character = player and player.SelectedCharacter
        if not character then return false, "no selected character" end
        hotbar.StartLevelUp:Execute(character)
        return true
    end,
}

local function guard(f, ...)
    local ok, a, b = pcall(f, ...)
    if ok then return a, b end
    return nil, tostring(a)
end

local function typeOf(object)
    local ok, name = pcall(function() return tostring(object.Type) end)
    return ok and name or "?"
end

local function contentRoot()
    return guard(function() return Ext.UI.GetRoot():Find("ContentRoot") end)
end

-- The open widgets, top-most last. Most are children of ContentRoot; the main menu is not (D10
-- of the 2026-11-14 day run: in the main menu only CrossplayOverlay was listed), so the rest of
-- the UI tree is walked too - every ls.UIWidget wherever it hangs, never looking inside one, within
-- a budget so a deep tree costs a bounded walk.
local function widgetElements()
    local found = {}
    local function add(element) found[#found + 1] = element end
    local content = contentRoot()
    local count = content and guard(function() return content.VisualChildrenCount end) or 0
    for i = 1, count do
        local element = guard(function() return content:VisualChild(i) end)
        if element and typeOf(element) == "ls.UIWidget" then add(element) end
    end
    local root = guard(function() return Ext.UI.GetRoot() end)
    local budget = 2500
    local function walk(element, depth)
        if budget <= 0 or depth > 8 then return end
        budget = budget - 1
        if typeOf(element) == "ls.UIWidget" then add(element) return end
        -- ContentRoot's widgets are listed above (by name: Noesis userdata are not compared)
        if depth > 0 and guard(function() return element.Name end) == "ContentRoot" then return end
        local n = guard(function() return element.VisualChildrenCount end) or 0
        for i = 1, n do
            local child = guard(function() return element:VisualChild(i) end)
            if child then walk(child, depth + 1) end
        end
    end
    if root then walk(root, 0) end
    return found
end

local function widgets()
    local out = {}
    for _, element in ipairs(widgetElements()) do
        if element then
            local name = guard(function() return element.Name end)
            if name == nil or name == "" then
                -- a page without x:Name (the first-launch screens: HDR calibration ...): named by
                -- its data type, "gui::DCHDRCalibration" -> "HDRCalibration" (the page's file)
                local data = typeOf(guard(function() return element.DataContext end))
                local short = data:match("::DC(%w+)$") or data:match("%.DC(%w+)$")
                -- named by its data even when the catalog has no page of that name: the options
                -- widget (gui::DCOptions, whose pages are VideoOptions, AudioOptions ...) was dropped
                -- from GK.Gui.Open and could not be reached by Controls or Click (D24 of the day run)
                if short then name = short elseif data ~= "" and data ~= "nil" and data ~= "?" then name = data end
            end
            if name then out[#out + 1] = { name = name, element = element } end
        end
    end
    return out
end

local function widgetName(name)
    local window = W[name]
    if window then return window.widget end
    return name
end

--- The element of an open widget (the top-most one of that name), or nil.
function UI.Widget(name)
    name = widgetName(name)
    local found
    for _, w in ipairs(widgets()) do
        if w.name == name then found = w.element end
    end
    return found
end

--- The DataContext of an open widget (its view model), or nil.
function UI.Data(name)
    local element = UI.Widget(name)
    return element and guard(function() return element.DataContext end)
end

local function entryOf(name)
    for _, entry in pairs(catalog.widgets) do
        if entry.name == name and entry.variant == "k" then return entry end
    end
    return catalog.widgets[name]
end

--- The open widgets: { { name, data, file, hud } }.
function UI.Open()
    local out = {}
    for _, w in ipairs(widgets()) do
        local entry = entryOf(w.name)
        out[#out + 1] = { name = w.name, data = typeOf(guard(function() return w.element.DataContext end)),
                          file = entry and entry.file or nil, hud = UI.Hud[w.name] or false }
    end
    return out
end

--- The page a widget shows, for widgets with pages (the character panel: "ToggleInventory",
--- "ToggleSpells", "ToggleInterrupts", "ToggleAlchemy" - its Metadata, which its XAML switches on).
function UI.Page(name)
    local element = UI.Widget(name)
    local page = element and guard(function() return element:GetProperty("Metadata") end)
    return page ~= nil and tostring(page) or nil
end

--- Whether a widget, or a named window, is open (for a window on a page of a shared widget:
--- whether that page is the one shown).
function UI.IsOpen(name)
    local window = W[name]
    if window and window.page then
        return UI.Widget(window.widget) ~= nil and UI.Page(window.widget) == window.page
    end
    return UI.Widget(name) ~= nil
end

-- A widget whose DataContext carries CustomEvent: the given one, else the first that has it.
local function eventSource(preferred)
    local candidates = {}
    if preferred then candidates[1] = UI.Widget(preferred) end
    for _, name in ipairs({ "Minimap", "HotBar", "PlayerPortraits", "Overlay" }) do candidates[#candidates + 1] = UI.Widget(name) end
    for _, w in ipairs(widgets()) do candidates[#candidates + 1] = w.element end
    for _, element in ipairs(candidates) do
        local command = guard(function() return element.DataContext.CustomEvent end)
        if command then return command end
    end
end

--- The game's own input events, from its input configuration (PlayerProfiles/Default/inputconfig.json
--- in the game's data, read through Ext.IO): { [event name] = { keys bound } }, filtered by a part of
--- the name. N36 of the third run: no command was found for the player's weapon toggle - its input
--- event is the player's own way to sheathe, and GK.Gui.Event raises it.
function UI.InputEvents(filter)
    local text = guard(function() return Ext.IO.LoadFile("PlayerProfiles/Default/inputconfig.json", "data") end)
    if not text or text == "" then return { ok = false, error = "the game's inputconfig.json could not be read" } end
    local ok, config = pcall(Ext.Json.Parse, text)
    if not ok or type(config) ~= "table" then return { ok = false, error = "inputconfig.json does not read" } end
    local out, n = {}, 0
    local wanted = filter and tostring(filter):lower()
    for name, keys in pairs(config) do
        if type(name) == "string" and (not wanted or name:lower():find(wanted, 1, true)) then
            out[name] = keys
            n = n + 1
        end
    end
    return { ok = true, events = out, count = n,
             note = "GK.Gui.Event(name) raises one as its key would (a widget with CustomEvent must be open: the HUD in play)" }
end

--- Raises a game input event (one of GK.Gui.Events, or any the game knows) as its key would.
function UI.Event(name, from)
    local command = eventSource(from)
    if not command then return { ok = false, error = "no widget with CustomEvent is open (main menu or loading?)" } end
    local _, problem = guard(function() command:Execute(Ext.UI.Box(name)) end)
    if problem then return { ok = false, error = problem } end
    return { ok = true, event = name, known = isEvent[name] or false }
end

--- Opens a window of GK.Gui.Windows by name, or raises the game input event of that name.
function UI.Show(name)
    local window = W[name]
    if window == nil then
        return UI.Event(name)
    end
    if UI.IsOpen(name) and not window.reopen then
        return { ok = true, already = true, widget = window.widget }
    end
    if type(window.open) == "function" then
        local done, problem = guard(window.open, UI)
        if not done then return { ok = false, error = problem or "could not open " .. name } end
        return { ok = true, widget = window.widget }
    end
    local result = UI.Event(window.open)
    result.widget = window.widget
    return result
end

--- Closes an open widget or named window: its own close event, else its CloseWidget, else the
--- game's CloseAll. Returns what it did.
function UI.Close(name)
    local window = W[name]
    local widget = widgetName(name)
    local element = UI.Widget(widget)
    if not element then return { ok = true, already = true } end
    if window and window.close then
        if type(window.close) == "function" then
            local done, problem = guard(window.close, UI)
            return { ok = done and true or false, error = problem, via = "function" }
        end
        local result = UI.Event(window.close, widget)
        result.via = window.close
        return result
    end
    local command = guard(function() return element.DataContext.CustomEvent end)
    if command then
        local _, problem = guard(function() command:Execute(Ext.UI.Box("CloseWidget")) end)
        if not problem then return { ok = true, via = "CloseWidget" } end
    end
    local result = UI.Input(widget, "UICancel")
    if result.ok then result.via = "UICancel"; return result end
    result = UI.Event("CloseAll")
    result.via = "CloseAll"
    return result
end

--- Closes every open widget that is not part of the HUD (`keep`: names to leave open).
function UI.CloseAll(keep)
    local closed = {}
    for _, w in ipairs(widgets()) do
        if not UI.Hud[w.name] and not (keep and keep[w.name]) then
            local result = UI.Close(w.name)
            closed[#closed + 1] = { name = w.name, ok = result.ok, via = result.via, error = result.error }
        end
    end
    return { ok = true, closed = closed }
end

-- Resolving what the XAML says ------------------------------------------------------------

-- Walks a property path ("CurrentPlayer.SelectedCharacter") from an object.
local function follow(object, path)
    if object == nil then return nil end
    if path == nil or path == "" then return object end
    for part in path:gmatch("[^%.]+") do
        local index = part:match("^%[(%d+)%]$")
        local value = guard(function()
            if index then return object[tonumber(index) + 1] end
            -- `obj.Type` is the Noesis type name; a view-model property of that name is read
            -- through GetProperty
            if part == "Type" then return object:GetProperty("Type") end
            return object[part]
        end)
        if value == nil then return nil end
        object = value
    end
    return object
end

local function findElement(scope, name)
    if not scope or not name then return nil end
    local found = guard(function() return scope:Find(name) end)
    if found then return found end
    -- Find() looks in the namescope; a template part is reached by walking
    local stack, n = { scope }, 0
    while #stack > 0 and n < 20000 do
        local element = table.remove(stack)
        n = n + 1
        if guard(function() return element.Name end) == name then return element end
        local count = guard(function() return element.VisualChildrenCount end) or 0
        for i = 1, count do
            local child = guard(function() return element:VisualChild(i) end)
            if child then stack[#stack + 1] = child end
        end
    end
end

--- An element by x:Name inside an open widget, template parts included (a tree walk when the
--- namescope does not have it).
function UI.Find(name, element)
    local widget = UI.Widget(name)
    return widget and findElement(widget, element) or nil
end

-- The object a binding of the catalogue points at, seen from a control inside a widget.
local function resolve(binding, control, widget)
    if not binding then return nil end
    local source = binding.source or "self"
    local base
    if source == "widget" then
        base = guard(function() return widget.DataContext end)
    elseif source == "self" then
        base = control and guard(function() return control.DataContext end) or guard(function() return widget.DataContext end)
    elseif source:sub(1, 8) == "element:" then
        local target = source:sub(9) == "self" and control or findElement(widget, source:sub(9))
        return follow(target, binding.path)
    else
        -- an ancestor of another type: the control's own context is the best guess
        base = control and guard(function() return control.DataContext end)
    end
    local value = follow(base, binding.path)
    if value == nil and source == "self" then
        -- a command bound in a template often lives on the widget's context instead
        value = follow(guard(function() return widget.DataContext end), binding.path)
    end
    return value
end

local function parameterOf(spec, control, widget)
    if spec == nil then return nil end
    if spec.multi then return nil end  -- only the live control holds a MultiBinding's value
    if spec.literal ~= nil then return Ext.UI.Box(spec.literal) end
    if spec.bool ~= nil then return Ext.UI.Box(spec.bool) end
    if spec.self then return control and guard(function() return control.DataContext end) end
    if spec.binding then return resolve(spec.binding, control, widget) end
    return nil
end

-- One action of a chain: { do = "command" | "set" | "other", ... }.
local function perform(action, control, widget, log)
    if action["do"] == "command" then
        local command = resolve(action.command, control, widget)
        if command == nil then
            log[#log + 1] = "no command " .. tostring(action.command and action.command.path)
            return false
        end
        local parameter = parameterOf(action.parameter, control, widget)
        local _, problem = guard(function() command:Execute(parameter) end)
        log[#log + 1] = (problem and "failed " or "ran ") .. tostring(action.command.path) .. (problem and (": " .. problem) or "")
        return not problem
    elseif action["do"] == "set" then
        local target = findElement(widget, action.target)
        if not target then log[#log + 1] = "no element " .. tostring(action.target); return false end
        local _, problem = guard(function() target:SetProperty(action.property, action.value) end)
        log[#log + 1] = (problem and "could not set " or "set ") .. tostring(action.target) .. "." .. tostring(action.property)
        return not problem
    end
    return false
end

-- `whichWidget` and `whichControl` let a delayed step find its elements again: an element taken
-- in one UI call is not valid in a later one (Script Extender ties it to the call).
local function runChain(entry, control, widget, log, whichWidget, whichControl)
    local direct = entry.command and true or false
    if entry.command then
        -- The control's own command gets the parameter the live control holds - what a click
        -- passes, including values only bindings can make (a MultiBinding's pair) - and the
        -- catalogue's literal only when the control holds none.
        local live = control and guard(function() return control.CommandParameter end)
        local kind = type(live)
        if live ~= nil and kind ~= "userdata" then live = guard(function() return Ext.UI.Box(live) end) end
        if live ~= nil then
            -- the path the XAML names is resolved from the control's DataContext; a binding whose
            -- source is an ancestor's (the reaction prompt's ReactButton: InterruptReactCommand
            -- lives on the popup's view model, not on the row's) is not found that way, and the
            -- live control's own Command - what a click runs - is taken instead
            local command = resolve(entry.command, control, widget) or (control and guard(function() return control.Command end))
            local _, problem = guard(function() command:Execute(live) end)
            log[#log + 1] = (problem and "failed " or "ran ") .. tostring(entry.command.path) .. (problem and (": " .. problem) or "")
        else
            perform({ ["do"] = "command", command = entry.command, parameter = entry.parameter }, control, widget, log)
        end
    end
    local delayed = 0
    for _, trigger in ipairs(entry.click or {}) do
        if (trigger.delay or 0) <= 0 then
            for _, action in ipairs(trigger.actions) do perform(action, control, widget, log) end
        else
            delayed = math.max(delayed, trigger.delay)
            Ext.Timer.WaitFor(trigger.delay, function()
                Ext.UI.Defer(function()
                    local now = UI.Widget(whichWidget)
                    local log2 = UI.LastDelayed or {}
                    if not now then
                        log2[#log2 + 1] = "+" .. trigger.delay .. "ms: " .. tostring(whichWidget) .. " is closed"
                    else
                        local again = whichControl and findElement(now, whichControl) or nil
                        local steps = {}
                        for _, action in ipairs(trigger.actions) do perform(action, again, now, steps) end
                        for _, step in ipairs(steps) do log2[#log2 + 1] = "+" .. trigger.delay .. "ms: " .. step end
                    end
                    UI.LastDelayed = log2
                end)
            end)
        end
    end
    return direct, delayed
end

-- Controls -----------------------------------------------------------------------------------

local UNKNOWN = "ls::TranslatedStringRepository::s_HandleUnknown"

-- The game's own automation name of an element (the attached property
-- ls:AttachedProperties.AutomationId, e.g. "HotBarSlot_Spell_Projectile_FireBolt"), read from the
-- element's stored dependency values; nil when it has none.
local function automationIdOf(element)
    local values = guard(function() return element:DependencyProperties() end)
    local id = type(values) == "table" and values.AutomationId or nil
    return type(id) == "string" and id ~= "" and id or nil
end
UI.AutomationIdOf = automationIdOf

-- A control's automation names: its own first, then those inside it (a hotbar slot is the list
-- item "HotbarSlotIndex_3" holding the grid "HotBarSlot_Spell_Projectile_FireBolt"), eight levels down.
local function automationIdsWithin(element)
    local out = {}
    local own = automationIdOf(element)
    if own then out[1] = own end
    local level = { element }
    for _ = 1, 8 do
        local nextLevel = {}
        for _, e in ipairs(level) do
            local n = guard(function() return e.VisualChildrenCount end) or 0
            for i = 1, math.min(n, 12) do
                local child = guard(function() return e:VisualChild(i) end)
                if child then
                    local id = automationIdOf(child)
                    if id and #out < 4 then out[#out + 1] = id end
                    nextLevel[#nextLevel + 1] = child
                end
            end
        end
        level = nextLevel
    end
    return out
end

local function hasAutomationId(row, id)
    for _, a in ipairs(row.automationIds or {}) do if a == id then return true end end
    return false
end

local function loca(handle)
    if not handle then return nil end
    local ok, text = pcall(Ext.Loca.GetTranslatedString, handle)
    return ok and text ~= "" and text ~= UNKNOWN and text or nil
end

-- A text as the player reads it: localisation handles ("h...g...") and resource ids
-- ("ResStr_4154032322", the names of origin characters) are translated.
local function readable(value)
    if type(value) ~= "string" then return value end
    if value == UNKNOWN then return nil end
    if value:match("^h%x+g%x+g%x+g%x+g%x+$") or value:match("^ResStr_%d+$") then return loca(value) or value end
    return value
end
UI.Text = readable

-- A control of the widget's own page, else of the shared library pages (templates the widget
-- uses: the spell buttons of the level-up window live in CCLib_k.xaml).
local function catalogControl(entry, name)
    if not name then return nil end
    for _, control in ipairs(entry and entry.controls or {}) do
        if control.name == name then return control end
    end
    -- only a name defined once in all the library pages: a common name ("TabRoot", "panel")
    -- belongs to many templates, and the wrong chain must never run
    if not UI.LibraryNames then
        local index = {}
        for key, library in pairs(catalog.library or {}) do
            if not key:find("_c$") then
                for _, control in ipairs(library.controls or {}) do
                    if control.name then
                        local seen = index[control.name]
                        index[control.name] = seen == nil and control or false
                    end
                end
            end
        end
        UI.LibraryNames = index
    end
    return UI.LibraryNames[name] or nil
end

--- The controls of an open widget: { { n, name, kind, text, enabled, catalogued, runs } } in tree
--- order. filter: part of a name or text, or { name, text, data (part of the type of the control's
--- data, e.g. "SpellSelector"), all = true (also unnamed and hidden controls) }.
function UI.Controls(name, filter)
    local widget = UI.Widget(name)
    if not widget then return { ok = false, error = "not open: " .. tostring(name) } end
    if type(filter) == "string" then filter = { any = filter } end
    filter = filter or {}
    local entry = entryOf(widgetName(name))
    local rows = {}
    local stack, n = { widget }, 0
    while #stack > 0 and n < 60000 do
        local element = table.remove(stack)
        n = n + 1
        local hidden = false
        if not filter.all then
            local visibility = guard(function() return tostring(element.Visibility) end)
            hidden = visibility == "Collapsed" or visibility == "Hidden"
        end
        if not hidden then
            local typeName = typeOf(element)
            local elementName = guard(function() return element.Name end)
            if elementName == "" then elementName = nil end
            local known = catalogControl(entry, elementName)
            local isControl = typeName:find("Button", 1, true) or typeName:find("CheckBox", 1, true)
                or typeName:find("ListBoxItem", 1, true) or known
            if isControl and (elementName or filter.all or filter.data) and element ~= widget then
                local row = { name = elementName, kind = typeName, catalogued = known ~= nil }
                local autos = automationIdsWithin(element)
                if #autos > 0 then row.automationId = autos[1]; row.automationIds = #autos > 1 and autos or nil end
                row.enabled = guard(function() return element.IsEnabled end)
                -- what the control stands for: the type of its data ("ls.VMSpellSelector" ...)
                local context = guard(function() return element.DataContext end)
                row.data = context and typeOf(context) or nil
                -- what a slot or a spell button stands for, in the game's own words: the action
                -- (spell) id of its data, e.g. Throw_Throw (D13 of the day run: a hotbar slot was
                -- only reachable by its position among every control of the bar)
                if context then row.action = UI.ActionOf(context) end
                row.command = guard(function() return element.Command end) ~= nil
                row.text = known and loca(known.text) or nil
                if not row.text then
                    local content = guard(function() return element.Content end)
                    if type(content) == "string" then row.text = content end
                end
                if known then
                    local runs = {}
                    if known.command then runs[#runs + 1] = known.command.path end
                    for _, t in ipairs(known.click or {}) do
                        for _, a in ipairs(t.actions) do
                            if a["do"] == "command" then
                                runs[#runs + 1] = (t.delay and t.delay > 0 and ("+" .. t.delay .. "ms ") or "") .. tostring(a.command.path)
                                    .. (a.parameter and a.parameter.literal and ("(" .. a.parameter.literal .. ")") or "")
                            end
                        end
                    end
                    row.runs = runs
                end
                local lower = GK.Fold
                local fits = (not filter.automationId or row.automationId == filter.automationId or hasAutomationId(row, filter.automationId))
                    and (not filter.data or lower(row.data):find(lower(filter.data), 1, true))
                    and (not filter.name or lower(row.name):find(lower(filter.name), 1, true))
                    and (not filter.text or lower(row.text):find(lower(filter.text), 1, true))
                    and (not filter.action or row.action == filter.action)
                    and (not filter.any or lower(row.name):find(lower(filter.any), 1, true)
                         or lower(row.text):find(lower(filter.any), 1, true)
                         or (row.automationId and lower(row.automationId):find(lower(filter.any), 1, true)))
                if fits then
                    row.n = #rows + 1
                    row.element = element
                    rows[#rows + 1] = row
                end
            end
            local count = guard(function() return element.VisualChildrenCount end) or 0
            for i = count, 1, -1 do
                local child = guard(function() return element:VisualChild(i) end)
                if child then stack[#stack + 1] = child end
            end
        end
    end
    -- elements stay out of what goes back to the caller
    local out = {}
    for i, row in ipairs(rows) do
        local copy = {}
        for k, v in pairs(row) do if k ~= "element" then copy[k] = v end end
        out[i] = copy
    end
    UI.LastControls = rows
    -- Nothing to press may mean something lies over the widget (the tutorial box over character
    -- creation): that is said, not answered with an empty list (N7 of the third run).
    if #rows == 0 and UI.Blocking then
        local blocking = guard(UI.Blocking)
        if type(blocking) == "table" and blocking.widget and blocking.widget ~= widgetName(name) then
            return { ok = false, widget = widgetName(name), controls = out, coveredBy = blocking.window,
                     error = tostring(widgetName(name)) .. " shows no controls while " .. tostring(blocking.window)
                         .. " (" .. tostring(blocking.widget) .. ") waits over it; GK.Gui.Resolve() takes it away ("
                         .. tostring(blocking.choices or "its choices: GK.Gui.Blocking()") .. ")" }
        end
    end
    return { ok = true, widget = widgetName(name), controls = out }
end

local ACTION_NAMES = { "PrototypeID", "PrototypeId", "SpellId", "SpellID", "ActionId", "ActionID", "Id", "ID" }

--- Whether `text` names a spell of the game's stats (a hotbar action is one).
local function isSpell(text)
    if type(text) ~= "string" or text == "" or not text:find("_", 1, true) then return false end
    local stat = guard(function() return Ext.Stats.Get(text) end)
    return stat ~= nil and guard(function() return stat.SpellType end) ~= nil
end

--- The spell a control's data stands for: a property of its view model that names one, or of a
--- view model it holds (a hotbar slot holds its action; D19 of the day run: ls.VMHotBarSlot has
--- no PrototypeID of its own). Every value offered is checked against the game's spell stats, so a
--- name is never guessed.
function UI.ActionOf(context, depth)
    depth = depth or 0
    for _, property in ipairs(ACTION_NAMES) do
        local value = guard(function() return context[property] end)
        if isSpell(value) then return value end
    end
    local ok, properties = pcall(function() return context:GetAllProperties() end)
    if not ok or type(properties) ~= "table" then return nil end
    for _, value in pairs(properties) do
        if isSpell(value) then return value end
    end
    if depth < 1 then
        for _, value in pairs(properties) do
            if type(value) == "userdata" then
                local found = UI.ActionOf(value, depth + 1)
                if found then return found end
            end
        end
    end
    return nil
end

-- A slot's item name, as the player reads it (the throw list of the Throw action, items on the bar).
local function slotItemName(context)
    local object = guard(function() return context.Object end) or guard(function() return context.Item end)
        or guard(function() return context.Action.Item end)
    local name = object and (guard(function() return object.Name end) or guard(function() return object.DisplayName end))
    return readable(name)
end

-- The client's targeting now: the spell the controlled character previews (or runs), or nil.
local function targetingSpell()
    for _, e in ipairs(GK.Controlled()) do
        local ic = guard(function() return e.ClientCharacter.InputController end)
        local task = ic and (guard(function() return ic.PreviewTask end) or guard(function() return ic.RunningTask end))
        if task then
            return guard(function() return task.Spell.OriginatorPrototype end) or tostring(guard(function() return task.TaskType end)), task
        end
    end
end
UI.TargetingSpell = targetingSpell

--- Uses the hotbar slot that holds action `id` (its spell id, e.g. "Throw_Throw", or part of an
--- item's name - the throw list's items), as a click on the slot does: the hotbar's own
--- UseSlotCommand with the slot. What follows is the game's targeting, which GK.Gui.Aim points.
--- Answers what it saw: the targeting of that action began (ok), or nothing changed.
function UI.UseSlot(id, widget)
    widget = widget or "HotBar"
    -- every control, hidden ones included (D26 of the day run: the slot was in the listing and
    -- not found by the click)
    local listing = UI.Controls(widget, { data = "HotBarSlot", all = true })
    if listing.ok == false then return listing end
    local row, held = nil, {}
    local wanted = GK.Fold(tostring(id))
    for _, r in ipairs(UI.LastControls or {}) do
        if r.action then held[#held + 1] = r.action end
        if not row and (r.action == id or r.automationId == id) then row = r end
    end
    if not row then
        -- by the game's automation name: "HotBarSlot_Spell_<spell>", "HotBarSlot_Item_<uuid>"
        -- (the throw list holds the items in reach by their uuid)
        for _, r in ipairs(UI.LastControls or {}) do
            for _, auto in ipairs(r.automationIds or { r.automationId }) do
                if #tostring(id) > 3 and auto:sub(-#tostring(id)) == tostring(id) then row = r; break end
            end
            if row then break end
        end
    end
    if not row then
        for _, r in ipairs(UI.LastControls or {}) do
            local name = slotItemName(guard(function() return r.element.DataContext end))
            if name and GK.Fold(name):find(wanted, 1, true) then row = r; row.itemName = name; break end
        end
    end
    if not row then
        return { ok = false, did = "nothing", error = "no slot of " .. widget .. " holds " .. tostring(id)
            .. (#held > 0 and ("; it holds " .. table.concat(held, ", ", 1, math.min(#held, 40))) or "; no slot names its action") }
    end
    local data = UI.Data(widget)
    local command = data and guard(function() return data.UseSlotCommand end)
    local slot = guard(function() return row.element.DataContext end)
    if not (command and slot) then return { ok = false, did = "nothing", error = widget .. " has no UseSlotCommand or the slot no data" } end
    local known, can = pcall(function() return command:CanExecute(slot) end)
    if known and can == false then
        return { ok = false, did = "nothing", saw = "the slot is disabled: the game's CanExecute says no",
                 hint = "no resource left (action, slot, charge), not this character's turn, or a cooldown" }
    end
    local beforeSpell = targetingSpell()
    -- what the targeting holds: an item picked from the throw list changes what it aims with
    local function held()
        local seen = UI.Aiming and UI.Aiming() or {}
        local names = {}
        for _, a in ipairs(seen.affected or {}) do names[#names + 1] = tostring(a.uuid) end
        return tostring(seen.spell) .. "|" .. tostring(seen.state) .. "|" .. table.concat(names, ",")
    end
    local heldBefore = held()
    local _, problem = guard(function() command:Execute(slot) end)
    if problem then return { ok = false, did = "nothing", error = problem } end
    local action = row.action or row.itemName or row.automationIds and row.automationIds[#row.automationIds] or row.automationId
    local result = { ok = true, did = "used the slot of " .. tostring(action), slot = action }
    return GK.Observe(result, function(_, final)
        local spell = targetingSpell()
        if spell and (spell == row.action and spell ~= beforeSpell or spell ~= beforeSpell) then
            result.saw = "the targeting of " .. tostring(spell) .. " began"
            result.targeting = spell
            return result
        end
        if spell and held() ~= heldBefore then
            result.saw = "the targeting of " .. tostring(spell) .. " took it"
            result.targeting = spell
            return result
        end
        if spell and spell == row.action then
            result.saw = "the targeting of " .. tostring(spell) .. " is on"
            result.targeting = spell
            return result
        end
        if final then
            return { ok = false, did = result.did, saw = "nothing changed: no targeting began (it shows " .. tostring(spell or "none") .. ")",
                     hint = "an action that needs no aim casts at once - read the effect; else the slot may need a turn or a resource" }
        end
    end, 1200)
end

-- Effects: what a verb saw, not what it attempted ---------------------------------------------
--
-- The generic rule (docs/contracts/lua-api.md, "Observed answers"): an acting verb takes a small
-- picture of the window before it acts - which widgets are open, the plain values of the widget's
-- view model (numbers, flags, texts, the counts of its lists), the state of the control it pressed
-- (enabled, visible, checked, selected) - and looks again every 100 ms after: `ok` only when the
-- picture changed within the wait (`saw` names what changed), else ok = false with
-- saw = "nothing changed". A command whose own CanExecute says no is not run at all.

local function scalarsOf(object, out, prefix, depth)
    local ok, properties = pcall(function() return object:GetAllProperties() end)
    if not ok or type(properties) ~= "table" then return end
    local n = 0
    for key, value in pairs(properties) do
        n = n + 1
        if n > 400 then break end
        local kind, name = type(value), prefix .. tostring(key)
        if kind == "boolean" or kind == "number" or kind == "string" then
            out[name] = value
        elseif kind == "userdata" then
            local typeName = typeOf(value)
            if not typeName:find("Command", 1, true) then
                local count = guard(function() return #value end)
                if type(count) == "number" then
                    out[name .. "#"] = count
                elseif depth > 0 then
                    scalarsOf(value, out, name .. ".", depth - 1)
                end
            end
        end
    end
end

local function controlState(element)
    if not element then return {} end
    return { enabled = guard(function() return element.IsEnabled end),
             visibility = tostring(guard(function() return element.Visibility end)),
             checked = guard(function() return element.IsChecked end),
             selected = guard(function() return element.IsSelected end) }
end

--- A picture of a widget's state, for UI.Changed: { open, data, control, present }.
function UI.Picture(name, controlName)
    local open = {}
    for _, w in ipairs(widgets()) do open[#open + 1] = w.name end
    local picture = { open = table.concat(open, ","), data = {}, control = {} }
    local element = name and UI.Widget(name)
    local data = element and guard(function() return element.DataContext end)
    if data then scalarsOf(data, picture.data, "", 0) end
    if element and controlName then picture.control = controlState(findElement(element, controlName)) end
    picture.present = element ~= nil
    -- which controls the widget shows: a page turned inside one widget changes nothing else
    if element and UI.Shown then picture.shown = UI.Shown(element) end
    return picture
end

local function setOf(text)
    local out = {}
    for name in tostring(text):gmatch("[^,]+") do out[name] = (out[name] or 0) + 1 end
    return out
end

--- What changed between two pictures: a short list of words ({} = nothing).
function UI.Changed(before, after)
    local out = {}
    if before.present and not after.present then out[#out + 1] = "the window closed" end
    if before.shown and after.shown and before.shown ~= after.shown then out[#out + 1] = "the controls shown changed" end
    if before.open ~= after.open then
        local a, b = setOf(before.open), setOf(after.open)
        for name in pairs(b) do if not a[name] then out[#out + 1] = name .. " opened" end end
        for name in pairs(a) do if not b[name] and #out < 6 then out[#out + 1] = name .. " closed" end end
    end
    for key, value in pairs(after.data) do
        if #out >= 6 then break end
        if before.data[key] ~= value then out[#out + 1] = key .. ": " .. tostring(before.data[key]) .. " -> " .. tostring(value) end
    end
    for key, value in pairs(before.data) do
        if #out >= 6 then break end
        if after.data[key] == nil and value ~= nil and after.present then out[#out + 1] = key .. " gone" end
    end
    for key, value in pairs(after.control) do
        if before.control[key] ~= value and #out < 8 then
            out[#out + 1] = "control " .. key .. ": " .. tostring(before.control[key]) .. " -> " .. tostring(value)
        end
    end
    return out
end

--- Wraps an acting verb's answer so the bridge answers with what it saw (GK.Observe): looks
--- every third tick for a change of the picture; `within` ms of a running game (default 1500,
--- longer for a chain with a delayed step) before it says nothing changed.
function UI.ObserveEffect(result, name, controlName, before, within, hint, seen)
    if not result.ok then return result end
    local lastLook = -1000
    return GK.Observe(result, function(ticks, final)
        if ticks - lastLook < 3 and not final then return nil end
        lastLook = ticks
        local after = UI.Picture(name, controlName)
        local changed = UI.Changed(before, after)
        if #changed > 0 then
            result.saw = table.concat(changed, "; ")
            if seen then seen(result) end
            return result
        end
        if not final then return nil end
        local out = { ok = false, did = result.did or table.concat(result.steps or {}, "; "), saw = "nothing changed",
                      hint = hint or "read the window (GK.Gui.Read) or run the window's own command (GK.Cheat.Run)" }
        for k, v in pairs(result) do if out[k] == nil and k ~= "ok" and k ~= "note" then out[k] = v end end
        return out
    end, within or 1500)
end

--- Clicks a control of an open widget: by name, by part of its text, by a filter table as
--- GK.Gui.Controls takes ({ data = "SpellSelector" }), or by number in GK.Gui.Controls(name). The chain the game's XAML runs on a click is run; a control the
--- catalogue does not know runs its own Command with its own parameter. `which`: the n-th
--- match when several fit (default 1).
function UI.Click(name, what, which)
    -- A handle of GK.Gui.Look names one control, whatever it is called or not called; an item's
    -- handle ("list#3") is a selection.
    local handled
    if type(what) == "string" and which == nil then
        local hit, number = UI.Handle(name, what)
        if hit and number then return UI.Select(name, hit.handle, number) end
        if hit and hit.kind == "list" then
            return { ok = false, error = tostring(what) .. " is a list: GK.Gui.Select(window, list, item) chooses in it" }
        end
        if hit then
            handled = { name = hit.name, kind = hit.type, element = hit.element, text = hit.handle,
                        enabled = guard(function() return hit.element.IsEnabled end), toggle = hit.kind == "toggle",
                        radio = hit.type:find("RadioButton", 1, true) ~= nil }
        end
    end
    local listing = handled and { ok = true } or UI.Controls(name, (type(what) == "string" or type(what) == "table") and what or nil)
    if not listing.ok then return listing end
    local rows = UI.LastControls
    local row
    if handled then
        row = handled
    elseif type(what) == "number" then
        UI.Controls(name)  -- an index counts every control, not only the ones a filter kept
        rows = UI.LastControls
        row = rows[what]
    else
        local exact = {}
        for _, r in ipairs(rows) do
            if r.name == what or r.automationId == what or hasAutomationId(r, what) then exact[#exact + 1] = r end
        end
        row = exact[which or 1] or (#exact == 0 and rows[which or 1]) or nil
    end
    if not row then return { ok = false, error = "no control " .. tostring(what) .. " in " .. tostring(name) } end
    if row.enabled == false then
        -- What the control stands for says why it is off (N23 of the third run: "disabled:
        -- AddAbility" and nothing more - no points left, or a cap): the flags and numbers of its data.
        local state = {}
        local context = guard(function() return row.element.DataContext end)
        local ok, properties = pcall(function() return context:GetAllProperties() end)
        if ok and properties then
            local n = 0
            for key, value in pairs(properties) do
                local kind = type(value)
                if n < 40 and (kind == "boolean" or kind == "number" or (kind == "string" and #value < 60)) then
                    state[tostring(key)] = value
                    n = n + 1
                end
            end
        end
        return { ok = false, error = "disabled: " .. tostring(row.name or row.text)
                     .. (next(state) and " - `state` is what its data holds (a count at 0 or a flag off is usually why)" or ""),
                 state = next(state) and state or nil, data = row.data }
    end
    local widget = UI.Widget(name)
    local entry = catalogControl(entryOf(widgetName(name)), row.name)
    if not (entry and (entry.command or entry.click)) and row.name and UI.XamlChain then
        -- the chain from the game's own XAML when GK.UICatalog does not have it (D20)
        entry = UI.XamlChain(widgetName(name), row.name)
    end
    -- the control's own command, asked first: a command the game turns down is not pressed
    local own = guard(function() return row.element.Command end)
    if own then
        local parameter = guard(function() return row.element.CommandParameter end)
        if type(parameter) ~= "userdata" and parameter ~= nil then parameter = guard(function() return Ext.UI.Box(parameter) end) end
        local known, can = pcall(function() return own:CanExecute(parameter) end)
        if known and can == false then
            return { ok = false, did = "nothing", saw = "the button is disabled: its command's CanExecute says no",
                     control = row.name or row.text, hint = "a choice is missing, or the game does not allow it now" }
        end
    end
    local before = UI.Picture(name, row.name)
    local log = {}
    local delayed = 0
    local toggled = false
    if entry and (entry.command or entry.click) then
        UI.LastDelayed = {}
        local _, later = runChain(entry, row.element, widget, log, widgetName(name), row.name)
        delayed = later
    else
        local command = guard(function() return row.element.Command end)
        if command then
            local parameter = guard(function() return row.element.CommandParameter end)
            if type(parameter) ~= "userdata" and parameter ~= nil then parameter = Ext.UI.Box(parameter) end
            local _, problem = guard(function() command:Execute(parameter) end)
            log[#log + 1] = problem and ("failed: " .. problem) or "ran its Command"
        elseif row.kind:find("ListBoxItem", 1, true) then
            guard(function() row.element.IsSelected = true end)
            log[#log + 1] = "selected"
        elseif row.toggle or guard(function() return row.element.IsChecked end) ~= nil then
            -- A toggle with no command: a click writes its IsChecked, and what is bound to the flag
            -- follows (a panel's Visibility, a flag of the data). A radio button is chosen, never
            -- unchosen, by a click.
            local checked = guard(function() return row.element.IsChecked end)
            guard(function() row.element.IsChecked = row.radio and true or not checked end)
            log[#log + 1] = row.radio and "chosen" or "toggled"
            toggled = true
        else
            return { ok = false, error = "nothing to run on " .. tostring(row.name or row.text) }
        end
    end
    local result = { ok = true, control = row.name or row.text, steps = log, delayed = delayed > 0 and delayed or nil,
                     chain = entry and (entry.source or "catalog") or nil }
    if toggled then
        -- a toggle that hides itself by what it switches is still the control the picture reads
        return UI.ObserveEffect(result, name, row.name, before, 1500,
            "the toggle's IsChecked was written and nothing followed: on a Script Extender build that does not hand out a nullable flag the write is lost",
            function(answer)
                if type(what) ~= "string" then return end
                local now = UI.Handle(name, what) or UI.Handle(name, what:sub(1, 1) == "~" and what:sub(2) or ("~" .. what))
                if not now then return end
                answer.value = guard(function() return now.element.IsChecked end)
                if now.handle ~= what then answer.handle = now.handle end
            end)
    end
    if not (entry and (entry.command or entry.click)) then
        -- D20 of the day run: a control whose click chain is in neither GK.UICatalog nor the
        -- game's XAML runs only its own Command; the observation below says if that did anything.
        result.note = "only the control's own Command ran: no click chain was found for it"
    end
    return UI.ObserveEffect(result, name, row.name, before, delayed > 0 and (delayed + 1200) or 1500,
        result.note and "the game's XAML may do more on a click than this control's own command: GK.Cheat.Run(window, command) runs a command of the window" or nil)
end

-- How many items a collection of the game's UI holds. A view model's list counts with `#`; a
-- Noesis collection (a ListBox's Items or ItemsSource) has no length operator and answered
-- "the list has 0" for the races of character creation (N6 of the third run) although indexing
-- it worked. So: `#`, then Count, then the items themselves, read until the first gap.
local function length(collection)
    if collection == nil then return nil end
    local n = guard(function() return #collection end)
    if type(n) == "number" then return n end
    n = guard(function() return collection.Count end)
    if type(n) == "number" then return n end
    local i = 0
    while i < 1024 and guard(function() return collection[i + 1] end) ~= nil do i = i + 1 end
    return i
end

-- Look: any widget as a player meets it ---------------------------------------------------------
--
-- One walk of a widget's visual tree finds everything a player could act on, by what each element
-- is and never by a table of names: buttons, toggles (check boxes, radio buttons, toggle buttons),
-- lists (list boxes, combo boxes, tab strips) with their items, sliders and text fields. Each gets
-- a handle that the acting verbs take: its x:Name, or for an unnamed control the handle of what
-- holds it and its kind ("hairPanel/LSButton"); a repeated handle is numbered in tree order
-- ("Increase[3]"). A control inside an item of a list is held by that item ("classList#4/LSButton"),
-- and an item itself is "<list>#<number>".

local ITEM_TEXTS = { "Name", "Title", "DisplayName", "Label", "Header", "Text", "Description" }
local ITEM_IDS = { "Guid", "UUID", "Id", "ID" }
local NULL_GUID = "00000000-0000-0000-0000-000000000000"

local function kindOf(typeName)
    if typeName:find("ListBoxItem", 1, true) or typeName:find("ComboBoxItem", 1, true) or typeName:find("TabItem", 1, true) then return "item" end
    if typeName:find("ListBox", 1, true) or typeName:find("ComboBox", 1, true) or typeName:find("TabControl", 1, true) then return "list" end
    if typeName:find("Slider", 1, true) then return "slider" end
    if typeName:find("TextBoxView", 1, true) then return nil end  -- the inside of a text box
    if typeName:find("TextBox", 1, true) then return "text" end
    if typeName:find("CheckBox", 1, true) or typeName:find("RadioButton", 1, true) or typeName:find("ToggleButton", 1, true) then return "toggle" end
    if typeName:find("RepeatButton", 1, true) then return nil end  -- a part of a scroll bar or a slider
    if typeName:find("Button", 1, true) then return "button" end
    return nil
end

-- The text of a text block: its Text, or the game's own formatted source (ls:TextBlockFormatter).
local function blockText(element)
    local text = guard(function() return element.Text end)
    if type(text) ~= "string" or text == "" or text == "[ForceUpdate]" then
        local values = guard(function() return element:DependencyProperties() end)
        text = type(values) == "table" and values.SourceText or nil
    end
    if type(text) ~= "string" or text == "" or text == "[ForceUpdate]" then return nil end
    return readable(text)
end

-- The texts a player reads on an element: its text blocks, in tree order.
local function textsWithin(element, most)
    local out, stack, n = {}, { element }, 0
    while #stack > 0 and n < 300 and #out < (most or 2) do
        local e = table.remove(stack)
        n = n + 1
        local visibility = guard(function() return tostring(e.Visibility) end)
        if visibility ~= "Collapsed" and visibility ~= "Hidden" then
            if typeOf(e):find("TextBlock", 1, true) then
                local text = blockText(e)
                if text then out[#out + 1] = text end
            end
            local count = guard(function() return e.VisualChildrenCount end) or 0
            for i = count, 1, -1 do
                local child = guard(function() return e:VisualChild(i) end)
                if child then stack[#stack + 1] = child end
            end
        end
    end
    return out
end

-- What a view model calls itself: the first of its naming properties that reads as a text.
local function dataText(data)
    if type(data) ~= "userdata" then return type(data) == "string" and readable(data) or nil end
    for _, property in ipairs(ITEM_TEXTS) do
        local value = readable(guard(function() return data[property] end))
        if type(value) == "string" and value ~= "" then return value end
    end
    -- a spell's view model names it under Spell.Name (a translated string)
    local spellName = readable(guard(function() return data.Spell.Name end))
    if type(spellName) == "string" and spellName ~= "" then return spellName end
    -- a view model that has no name of its own is called by the property named as it is
    -- (ls.VMAbility.Ability, ls.VMSkill.Skill)
    local own = typeOf(data):match("VM(%w+)$")
    local value = own and guard(function() return data[own] end)
    if value ~= nil and type(value) ~= "userdata" and tostring(value) ~= "" then return tostring(readable(value)) end
    return nil
end

local function dataId(data)
    if type(data) ~= "userdata" then return nil end
    for _, property in ipairs(ITEM_IDS) do
        local value = guard(function() return data[property] end)
        if (type(value) == "string" and value ~= "" and value ~= NULL_GUID) or type(value) == "number" then return value end
    end
    return nil
end

-- The plain values of a view model: what tells one unnamed item from another (a colour, an index).
local function dataScalars(data)
    local ok, properties = pcall(function() return data:GetAllProperties() end)
    if not ok or type(properties) ~= "table" then return nil end
    local out, n = {}, 0
    for key, value in pairs(properties) do
        local kind = type(value)
        if n < 24 and (kind == "boolean" or kind == "number" or (kind == "string" and #value < 80 and value ~= NULL_GUID)) then
            if kind == "string" then value = readable(value) end
            out[tostring(key)] = value
            n = n + 1
        end
    end
    return next(out) and out or nil
end

local function shortType(typeName) return (typeName:match("([%w_]+)$") or typeName) end

-- The walk. Rows keep their elements; UI.Look strips them.
local function survey(widget, options)
    options = options or {}
    local rows, seen, budget = {}, {}, 60000
    local widgetData = typeOf(guard(function() return widget.DataContext end))
    local function handleFor(base)
        seen[base] = (seen[base] or 0) + 1
        return seen[base] == 1 and base or (base .. "[" .. seen[base] .. "]")
    end
    -- `holder`: the list item the element lies in ({ list = row, n }), kept below nested lists
    local function walk(element, anchor, list, inItem, under, hidden, holder)
        if budget <= 0 then return end
        budget = budget - 1
        if not hidden then
            local visibility = guard(function() return tostring(element.Visibility) end)
            if visibility == "Collapsed" or visibility == "Hidden" then
                if not options.all then
                    -- an item that is not shown keeps its place in its list: the numbers are the list's own
                    if list and kindOf(typeOf(element)) == "item" then
                        list.containers[#list.containers + 1] = element
                        list.hiddenItems = list.hiddenItems or {}
                        list.hiddenItems[#list.containers] = true
                    end
                    return
                end
                -- what is not shown is numbered apart, under "~", so that the handles of what is
                -- shown are the same with and without it
                hidden = true
                if anchor:sub(1, 1) ~= "~" then anchor = "~" .. anchor end
            end
        end
        local typeName = typeOf(element)
        if typeName:find("ScrollBar", 1, true) then return end
        local name = guard(function() return element.Name end)
        if name == "" then name = nil end
        local kind = element ~= widget and kindOf(typeName) or nil
        if kind == "item" then
            if list then
                list.containers[#list.containers + 1] = element
                anchor, inItem = list.handle .. "#" .. #list.containers, true
                holder = { list = list, n = #list.containers }
            else
                kind = "button"  -- an item outside any list is pressed like a button
            end
        end
        -- the parts of a combo box's own template (the toggle that opens its drop-down) are the
        -- combo box itself: one control, the list with its items and the one chosen
        if (kind == "toggle" or kind == "button") and list and not inItem and list.type:find("ComboBox", 1, true) then kind = nil end
        local made
        if kind and kind ~= "item" then
            made = { kind = kind, type = typeName, name = name, element = element, hidden = hidden or nil, holder = holder,
                     handle = handleFor(name and ((hidden and "~" or "") .. name) or (anchor .. "/" .. shortType(typeName))) }
            if options.under and (name == options.under or made.handle == options.under) then under = true end
            made.under = under
            if kind == "list" then
                made.containers = {}
                list, inItem = made, false
            end
            rows[#rows + 1] = made
            anchor = made.handle
        elseif name and not inItem then
            anchor = (hidden and "~" or "") .. name
        end
        if options.under and name == options.under then under = true end
        local count = guard(function() return element.VisualChildrenCount end) or 0
        for i = 1, count do
            local child = guard(function() return element:VisualChild(i) end)
            if child then walk(child, anchor, list, inItem, under, hidden, holder) end
        end
    end
    walk(widget, widgetName(guard(function() return widget.Name end) or "widget"), nil, false, not options.under, nil, nil)
    return rows, widgetData
end

-- The handles of the controls a widget shows, as one text (UI.Picture compares two of them).
function UI.Shown(widget)
    local handles = {}
    for i, row in ipairs(survey(widget)) do handles[i] = row.handle end
    return table.concat(handles, ",")
end

-- The items of a list: its ItemsSource or Items where they count, else the item elements it shows.
local function listItems(row)
    local box = row.element
    local items, count = nil, 0
    for _, source in ipairs({ "ItemsSource", "Items" }) do
        local candidate = guard(function() return box[source] end)
        local n = length(candidate)
        if type(n) == "number" and n > count then items, count = candidate, n end
        if count > 0 then break end
    end
    local out = {}
    if count > 0 and guard(function() return items[1] end) == nil then
        -- The list hands its source out as a bare object that counts and cannot be indexed, while
        -- the view model hands the same object out as a collection: it is found there by its address.
        local address = tostring(items):match("%((%x+)%)")
        local context = guard(function() return box.DataContext end)
        local ok, properties = pcall(function() return context:GetAllProperties() end)
        if address and ok and type(properties) == "table" then
            for _, value in pairs(properties) do
                if type(value) == "userdata" and tostring(value):match("%((%x+)%)") == address
                    and guard(function() return value[1] end) ~= nil then
                    items = value
                    break
                end
            end
        end
    end
    -- a source that still cannot be indexed (a filtered or sorted view) is read by what it shows
    if count > 0 and guard(function() return items[1] end) ~= nil then
        for i = 1, count do out[i] = guard(function() return items[i] end) end
        return out, "source"
    end
    for i, container in ipairs(row.containers or {}) do
        -- an item written in the XAML is its own element; a generated one stands for its data
        local own = guard(function() return container.Name end)
        out[i] = (own == nil or own == "") and guard(function() return container.DataContext end) or container
    end
    return out, "shown", count
end

local function describeItem(item, container, n, detail)
    local out = { n = n }
    local isElement = type(item) == "userdata" and guard(function() return item.VisualChildrenCount end) ~= nil
    if isElement then
        local elementName = guard(function() return item.Name end)
        out.name = elementName ~= "" and elementName or nil
        local texts = textsWithin(item, 2)
        out.text = texts[1]
        out.value = texts[2]
        out.enabled = guard(function() return item.IsEnabled end)
    else
        out.text = dataText(item)
        out.id = dataId(item)
        out.type = type(item) == "userdata" and typeOf(item) or nil
        if type(item) ~= "userdata" and item ~= nil then out.text = tostring(readable(item)) end
        if not out.text and container then out.text = textsWithin(container, 1)[1] end
        if container then out.enabled = guard(function() return container.IsEnabled end) end
        if detail and type(item) == "userdata" then out.data = dataScalars(item) end
    end
    if out.enabled == true then out.enabled = nil end
    return out
end

-- Whether a toggle is on, and where that was read: its own IsChecked, a nullable flag that reads
-- true, false or nil (neither) on GateKit's Script Extender and nil whatever it holds on a build
-- that does not hand it out; when it is nil, the flag `Value` of the view model the toggle stands
-- for (a tick box of the game's settings binds its IsChecked to it), when that view model is not
-- the widget's own.
local function toggleState(element, widgetData)
    local checked = guard(function() return element.IsChecked end)
    if type(checked) == "boolean" then return checked, "IsChecked" end
    local context = guard(function() return element.DataContext end)
    if type(context) == "userdata" and typeOf(context) ~= widgetData then
        local value = guard(function() return context.Value end)
        if type(value) == "boolean" then return value, "data.Value", context end
    end
    return nil
end

-- The address a Noesis object prints with: the same object reached two ways compares by it (the
-- userdata themselves do not compare).
local function addressOf(object)
    if type(object) ~= "userdata" and type(object) ~= "table" then return nil end
    return tostring(object):match("%((%x+)%)")
end

-- The name of the command a control is bound to: the property of its data (else of the widget's
-- data) that holds the same command. The control's stored value is read, not its Command
-- property, which hands out the command's interface at another address. `names` caches each
-- data's properties for one look.
local function commandName(element, widget, names)
    local values = guard(function() return element:DependencyProperties() end)
    local address = type(values) == "table" and addressOf(values.Command) or nil
    if not address then return nil end
    for _, data in ipairs({ guard(function() return element.DataContext end), guard(function() return widget.DataContext end) }) do
        local key = addressOf(data)
        if key then
            local byAddress = names[key]
            if not byAddress then
                byAddress = {}
                local ok, properties = pcall(function() return data:GetAllProperties() end)
                for name, value in pairs(ok and type(properties) == "table" and properties or {}) do
                    local at = addressOf(value)
                    if at then byAddress[at] = tostring(name) end
                end
                names[key] = byAddress
            end
            if byAddress[address] then return byAddress[address] end
        end
    end
    return nil
end

local function describeRow(row, widgetData, options)
    local element = row.element
    local out = { handle = row.handle, kind = row.kind, type = row.type, hidden = row.hidden }
    local enabled = guard(function() return element.IsEnabled end)
    if enabled == false then out.enabled = false end
    local context = guard(function() return element.DataContext end)
    local contextType = context and typeOf(context) or nil
    if contextType and contextType ~= widgetData and contextType ~= "ui::DCWidget" then
        out.data = contextType
        out.of = dataText(context)
        -- a number its view model holds as its value (an ability's score: ls.VMAbility.Value)
        local held = guard(function() return context.Value end)
        -- (a negative one is a placeholder: a background bonus button's row reads -1)
        if type(held) == "number" and held >= 0 then out.ofValue = held end
        if options.detail then out.dataValues = dataScalars(context) end
    end
    if row.kind == "list" then
        local items, from, total = listItems(row)
        local selected = guard(function() return element.SelectedIndex end)
        out.count = #items
        -- a list read by what it shows may hold more than it has drawn
        if from == "shown" and total and total > #items then out.notShown = total - #items end
        if type(selected) == "number" and selected >= 0 then
            out.selected = selected + 1
            if items[selected + 1] ~= nil then out.value = describeItem(items[selected + 1], row.containers[selected + 1], selected + 1).text end
        end
        local cap = options.items or 12
        out.items = {}
        local shown = 0
        for i = 1, #items do
            local hiddenItem = from == "shown" and row.hiddenItems and row.hiddenItems[i]
            if not hiddenItem or options.all then
                shown = shown + 1
                if #out.items < cap then
                    local described = describeItem(items[i], from == "shown" and row.containers[i] or nil, i, options.detail)
                    described.hidden = hiddenItem or nil
                    out.items[#out.items + 1] = described
                end
            end
        end
        out.count = shown
        if shown > #out.items then out.more = shown - #out.items end
    else
        local content = guard(function() return element.Content end)
        out.text = type(content) == "string" and content ~= "" and readable(content) or textsWithin(element, 1)[1]
        if row.kind == "toggle" then
            -- the toggle's own IsChecked when it reads, else the flag of the toggle's own data
            local checked, from = toggleState(element, widgetData)
            if type(checked) == "boolean" then
                out.value = checked
                if from ~= "IsChecked" then out.valueFrom = from end
            else
                out.unread = "IsChecked"
            end
        elseif row.kind == "slider" then
            out.value = guard(function() return element.Value end)
            out.min = guard(function() return element.Minimum end)
            out.max = guard(function() return element.Maximum end)
        elseif row.kind == "text" then
            out.value = guard(function() return element.Text end)
        end
        local command = guard(function() return element.Command end)
        if command then
            local parameter = guard(function() return element.CommandParameter end)
            if type(parameter) == "string" or type(parameter) == "number" or type(parameter) == "boolean" then out.parameter = parameter end
            local boxed = parameter
            if type(boxed) ~= "userdata" and boxed ~= nil then boxed = guard(function() return Ext.UI.Box(boxed) end) end
            local known, can = pcall(function() return command:CanExecute(boxed) end)
            if known and can == false then out.enabled = false end
        end
        out.command = command ~= nil and (options.widget and commandName(element, options.widget, options.names) or true) or nil
    end
    if row.holder then out["in"] = row.holder.list.handle .. "#" .. row.holder.n end
    return out
end

-- The lists whose items each stand for a choice inside them (a list of settings, each item a
-- combo box, a tick box, a slider bound to the item's own data): the controls are the choices, and
-- the list is only where they stand. Items that hold only buttons (or nothing: a heading) do not
-- count. Returns the set of such lists' rows.
local CHOICE = { list = true, toggle = true, slider = true, text = true }
local function containerLists(rows)
    local holds, bound = {}, {}
    for _, row in ipairs(rows) do
        local h = row.holder
        if h and h.list ~= row and CHOICE[row.kind] then
            holds[h.list] = holds[h.list] or {}
            holds[h.list][h.n] = true
            local container = h.list.containers[h.n]
            local own = addressOf(guard(function() return container.DataContext end))
            if own and own == addressOf(guard(function() return row.element.DataContext end)) then
                bound[h.list] = bound[h.list] or {}
                bound[h.list][h.n] = true
            end
        end
    end
    local out = {}
    for list, items in pairs(holds) do
        local all = next(items) ~= nil
        for n in pairs(items) do
            if not (bound[list] and bound[list][n]) then all = false break end
        end
        if all then out[list] = true end
    end
    return out
end

--- Everything a player could act on in an open widget: { ok, widget, controls = { { handle, kind
--- ("button", "toggle", "list", "slider", "text"), type, text, value, enabled (false only), data and
--- of (the view model the control stands for, when it is not the widget's own), command (the name
--- of the data's command it is bound to, e.g. "CustomEvent", or true when no data names it),
--- parameter, in (the list item it lies in, "Options#2"), and for a list: count, selected, value,
--- items = { { n, text, id, name, enabled } } } } }. A combo box is one list (its opener is not a
--- control of its own); a list whose items each hold a choice bound to the item's own data (a
--- list of settings) is left out for those choices, each naming its item in `in`. options: under (a handle or
--- an element's x:Name: only what lies inside it), kind, find (part of a handle or a text), items
--- (how many items of a list are written out, default 12), detail (the plain values of each
--- control's and item's data), all (hidden controls too; their handles are numbered apart), layout
--- (also `laidOut`: true once Noesis's layout has arranged the widget and every control shown -
--- its ActualWidth is stored - so no control is still to be made).
function UI.Look(name, options)
    local widget = UI.Widget(name)
    if not widget then return { ok = false, error = "not open: " .. tostring(name) } end
    if type(options) == "string" then options = { find = options } end
    options = options or {}
    local rows, widgetData = survey(widget, options)
    local out = {}
    local wanted = options.find and GK.Fold(options.find)
    local folded = containerLists(rows)
    local describe = setmetatable({ widget = widget, names = {} }, { __index = options })
    for _, row in ipairs(rows) do
        if row.under and not folded[row] and (not options.kind or options.kind == row.kind) then
            local described = describeRow(row, widgetData, describe)
            local fits = not wanted or GK.Fold(described.handle):find(wanted, 1, true)
                or (described.text and GK.Fold(tostring(described.text)):find(wanted, 1, true))
                or (described.of and GK.Fold(tostring(described.of)):find(wanted, 1, true))
            if fits then out[#out + 1] = described end
        end
    end
    local answer = { ok = true, widget = widgetName(name), data = widgetData, controls = out }
    if options.layout then
        -- Noesis stores an element's ActualWidth when its layout pass has arranged it; a page's
        -- inner elements (a list's items, a combo box inside an item) are made and arranged in
        -- later passes than the page itself
        local function arranged(element)
            local values = guard(function() return element:DependencyProperties() end)
            return type(values) == "table" and values.ActualWidth ~= nil
        end
        answer.laidOut = arranged(widget)
        for _, row in ipairs(rows) do
            if not answer.laidOut then break end
            if not row.hidden and not arranged(row.element) then answer.laidOut = false end
        end
    end
    return answer
end

--- The row of a handle of GK.Gui.Look: { kind, type, name, handle, element, containers }, and for an
--- item's handle ("list#3") the list's row and the item's number; nil and why when nothing has it.
function UI.Handle(name, handle)
    local widget = UI.Widget(name)
    if not widget then return nil, "not open: " .. tostring(name) end
    local listHandle, number = tostring(handle):match("^(.*)#(%d+)$")
    local rows = survey(widget, { all = tostring(handle):sub(1, 1) == "~" })
    for _, row in ipairs(rows) do
        if row.handle == handle then return row end
        if listHandle and row.handle == listHandle and row.kind == "list" then return row, tonumber(number) end
    end
    return nil, "no control " .. tostring(handle) .. " in " .. tostring(name) .. " (GK.Gui.Look lists the handles)"
end

--- The text an open widget shows, as a player reads it: every visible TextBlock in the tree's
--- order - its Text, or the Runs of its Inlines joined (a number written in a Run of a sentence) -
--- with nothing a hidden element holds. { ok, widget, texts = { ... } }.
function UI.Text(name)
    local widget = UI.Widget(name)
    if not widget then return { ok = false, error = "not open: " .. tostring(name) } end
    local out, budget = {}, 20000
    local function walk(element)
        budget = budget - 1
        if budget <= 0 then return end
        if guard(function() return element.IsVisible end) == false then return end
        local text = guard(function() return element.Text end)
        if type(text) == "string" then
            if text ~= "" then out[#out + 1] = text end
        else
            local inlines = guard(function() return element.Inlines end)
            if inlines then
                local parts = {}
                local n = guard(function() return #inlines end) or 0
                for i = 1, n do
                    local t = guard(function() return inlines[i].Text end)
                    if type(t) == "string" then parts[#parts + 1] = t end
                end
                local joined = table.concat(parts)
                if joined ~= "" then out[#out + 1] = joined end
            end
        end
        local n = guard(function() return element.VisualChildrenCount end) or 0
        for i = 1, n do
            local child = guard(function() return element:VisualChild(i) end)
            if child then walk(child) end
        end
    end
    walk(widget)
    return { ok = true, widget = widgetName(name), texts = out }
end

-- The brush of the game's theme a mark tints with (Public/Game/GUI/Theme/DefaultTheme.Colours.xaml:
-- ProgressionBarColor, #6600FF). The theme's brushes are frozen: a mark replaces a text's brush,
-- it never changes a brush's colour, which other controls share.
Cheat.MarkBrush = "ProgressionBarColor"

--- Shows what is about to be pressed: every element of the control `handle` (GK.Gui.Look's
--- handles) that has a Foreground takes the theme's brush `options.brush` (Cheat.MarkBrush) for
--- `options.seconds` (1) of the game's time - the client's ticks, so a faster game shortens it.
--- The control keeps that brush until the window rebuilds it (a pressed answer's list does); an
--- element of one UI call is not valid in a later one, so it is not put back.
--- { ok, marked = n, seconds, done = function: true once the time has passed }.
function Cheat.Mark(name, handle, options)
    options = options or {}
    local row, problem = UI.Handle(name, handle)
    if not row then return { ok = false, error = problem } end
    local key = options.brush or Cheat.MarkBrush
    local brush = guard(function() return row.element:Resource(key, true) end)
    if not brush or guard(function() return brush.Color end) == nil then
        return { ok = false, error = "the theme has no brush " .. tostring(key) }
    end
    local marked = 0
    local function walk(element, depth)
        if depth > 16 then return end
        if guard(function() return element.Foreground end) ~= nil then
            local _, failed = guard(function() element:SetProperty("Foreground", brush) end)
            if not failed then marked = marked + 1 end
        end
        local n = guard(function() return element.VisualChildrenCount end) or 0
        for i = 1, n do
            local child = guard(function() return element:VisualChild(i) end)
            if child then walk(child, depth + 1) end
        end
    end
    walk(row.element, 0)
    if marked == 0 then return { ok = false, error = "nothing in " .. tostring(handle) .. " has a text to tint" } end
    local seconds = tonumber(options.seconds) or 1
    local state = { left = seconds }
    local subscription
    subscription = Ext.Events.Tick:Subscribe(function(e)
        local dt = guard(function() return e.Time.DeltaTime end) or 1 / 30
        state.left = state.left - dt
        if state.left <= 0 then
            state.done = true
            Ext.Events.Tick:Unsubscribe(subscription)
        end
    end)
    return { ok = true, marked = marked, seconds = seconds, done = function() return state.done == true end }
end

-- A control found again after an act that may have hidden or shown it: by its handle, else by the
-- same handle with or without the "~" of what is not shown.
local function controlAgain(name, handle)
    local row = UI.Handle(name, handle)
    if row then return row end
    handle = tostring(handle)
    return (UI.Handle(name, handle:sub(1, 1) == "~" and handle:sub(2) or ("~" .. handle)))
end

--- Sets a control of an open widget to a value, as the player's hand on it would: a slider to a
--- number inside its range, a toggle to true or false (through its own command when it has one), a
--- text field to a text, a list to an item (as GK.Gui.Select). Answers what the control reads
--- afterwards: { ok, before, after }; a slider answers once its data holds the value, naming the
--- number that took it (`held = { Gamma = 1.2 }`), or at the limit with what its data holds (`holds`).
function UI.Set(name, handle, value)
    local row, problem = UI.Handle(name, handle)
    if not row then return { ok = false, error = problem } end
    local element = row.element
    if guard(function() return element.IsEnabled end) == false then
        return { ok = false, error = "disabled: " .. tostring(handle) }
    end
    if row.kind == "list" then return UI.Select(name, handle, value) end
    if row.kind == "slider" then
        if type(value) ~= "number" then return { ok = false, error = "a slider takes a number" } end
        local low, high = guard(function() return element.Minimum end), guard(function() return element.Maximum end)
        if (low and value < low) or (high and value > high) then
            return { ok = false, error = "outside the slider's range", min = low, max = high }
        end
        -- The view model takes a slider's value through its binding a frame later: the answer waits
        -- until a number of the slider's data (else the widget's) reads the value, and names it
        -- (`held`); a slider bound to nothing answers at the limit with the numbers its data holds.
        -- (an element is valid only in the UI call that found it: each look finds the slider again)
        local function numbers()
            local out = {}
            local now = UI.Handle(name, handle)
            local own = now and guard(function() return now.element.DataContext end)
            local sources = { own or false, UI.Data(name) or false }
            for i = 1, 2 do
                local data = sources[i]
                local ok, properties = pcall(function() return data:GetAllProperties() end)
                for key, v in pairs(ok and type(properties) == "table" and properties or {}) do
                    if type(v) == "number" and out[tostring(key)] == nil then out[tostring(key)] = v end
                end
                if next(out) then break end
            end
            return out
        end
        local held = numbers()
        local before = guard(function() return element.Value end)
        local _, failed = guard(function() element.Value = value end)
        local after = guard(function() return element.Value end)
        local result = { ok = failed == nil and type(after) == "number" and math.abs(after - value) < 1e-4, before = before, after = after, error = failed }
        if not result.ok then return result end
        return GK.Observe(result, function(_, final)
            local now = numbers()
            for key, v in pairs(now) do
                -- a number that moved to the value; one that already held it when the slider did too
                if math.abs(v - value) < 1e-4 and (held[key] == nil or math.abs(held[key] - value) >= 1e-4
                        or (type(before) == "number" and math.abs(before - value) < 1e-4)) then
                    result.held = { [key] = v }
                    return result
                end
            end
            if final then
                result.holds = now
                result.note = "no number of the slider's data took the value"
                return result
            end
        end, 1500)
    elseif row.kind == "toggle" then
        local state, from, context = toggleState(element, typeOf(UI.Data(name)))
        if from == "data.Value" then
            -- The toggle's state is the flag of its data: a click writes it through the binding and
            -- then runs the toggle's chain. The same two things are done here, in that order.
            if state == (value == true) then return { ok = true, before = state, after = state, already = true, from = from } end
            local _, failed = guard(function() context.Value = value == true end)
            if failed then return { ok = false, error = failed, from = from } end
            local pressed = UI.Click(name, handle)
            -- the view model takes the write a frame later: the flag is read again until it holds
            local result = { ok = true, before = state, from = from, steps = pressed and pressed.steps }
            return GK.Observe(result, function(ticks, final)
                local now = UI.Handle(name, handle)
                local after = now and toggleState(now.element, typeOf(UI.Data(name)))
                if after ~= (value == true) and not final then return nil end
                result.ok, result.after = after == (value == true), after
                return result
            end, 1500)
        end
        local before = state == true
        if before == (value == true) then return { ok = true, before = before, after = before, already = true } end
        local pressed = UI.Click(name, handle)
        if type(pressed) ~= "table" or (pressed.ok == false and pressed.error) then return pressed end
        -- A toggle may hide itself by what it switches (a colour swatch is not shown while its panel
        -- is): it is found again under either form of its handle, and the answer names the one it has.
        local result = { ok = true, before = before, steps = pressed.steps }
        return GK.Observe(result, function(ticks, final)
            local now = controlAgain(name, handle)
            local after = now and guard(function() return now.element.IsChecked end)
            if after ~= (value == true) and not final then return nil end
            result.ok, result.after = after == (value == true), after
            if now and now.handle ~= handle then result.handle = now.handle end
            if type(after) ~= "boolean" then
                result.error = now and "the toggle's IsChecked does not read: this Script Extender build does not hand out a nullable flag"
                    or ("no control " .. tostring(handle) .. " after the press")
            end
            return result
        end, 1500)
    elseif row.kind == "text" then
        local before = guard(function() return element.Text end)
        local _, failed = guard(function() element.Text = tostring(value) end)
        local after = guard(function() return element.Text end)
        return { ok = failed == nil and after == tostring(value), before = before, after = after, error = failed }
    end
    return { ok = false, error = tostring(handle) .. " is a " .. tostring(row.kind) .. ": GK.Gui.Click presses it" }
end

--- Selects an item of a list (ListBox, tab strip ...) of an open widget, as a click on the item
--- would: `list` is the list's handle (GK.Gui.Look) or x:Name, `item` its number (1-based), part of
--- its text, its id (a Guid), or the item itself (a userdata read from the view model, e.g.
--- GK.Gui.Data(w):GetProperty("SelectableRaces")[i]).
function UI.Select(name, list, item)
    local widget = UI.Widget(name)
    if not widget then return { ok = false, error = "not open: " .. tostring(name) } end
    -- the list a handle names first: an x:Name may also name a hidden copy of it (creation's
    -- subTabsSpellsList), whose selection changes nothing shown
    local row = UI.Handle(name, list)
    local box = row and row.kind == "list" and row.element or nil
    if not box then
        row = nil
        box = findElement(widget, list)
        if box and kindOf(typeOf(box)) ~= "list" then box = nil end
    end
    if not box then return { ok = false, error = "no list " .. tostring(list) .. " in " .. tostring(name) } end
    if type(item) ~= "userdata" then
        -- by what GK.Gui.Look shows: the same items, the same numbers; a list whose source cannot
        -- be counted takes the item its shown element stands for
        row = row or UI.Handle(name, list)
        if row and row.kind == "list" then
            local items, from = listItems(row)
            local index = type(item) == "number" and item or nil
            if not index and type(item) == "string" then
                local wanted = GK.Fold(item)
                for pass = 1, 2 do
                    for i = 1, #items do
                        local described = describeItem(items[i], from == "shown" and row.containers[i] or nil, i)
                        local exact = described.id == item or described.name == item or (described.text and GK.Fold(described.text) == wanted)
                        if exact or (pass == 2 and described.text and GK.Fold(described.text):find(wanted, 1, true)) then index = i break end
                    end
                    if index then break end
                end
            end
            if not index or index < 1 or index > #items then
                return { ok = false, error = "no item " .. tostring(item) .. " (the list has " .. #items .. ")" }
            end
            if from == "shown" and row.hiddenItems and row.hiddenItems[index] then
                return { ok = false, error = "item " .. index .. " of " .. tostring(row.handle) .. " is not shown: a player cannot choose it now" }
            end
            if guard(function() return box.IsEnabled end) == false then
                return { ok = false, error = "disabled: " .. tostring(row.handle) }
            end
            local shownAs = #row.containers == #items and row.containers[index] or nil
            if shownAs and guard(function() return shownAs.IsEnabled end) == false then
                return { ok = false, error = "item " .. index .. " of " .. tostring(row.handle) .. " is disabled" }
            end
            local before = UI.Picture(name)
            local selectedBefore = guard(function() return box.SelectedIndex end)
            local _, failed = guard(function() box.SelectedIndex = index - 1 end)
            if guard(function() return box.SelectedIndex end) ~= index - 1 then
                local chosen = items[index]
                if guard(function() return chosen.VisualChildrenCount end) ~= nil then
                    _, failed = guard(function() chosen.IsSelected = true end)
                else
                    _, failed = guard(function() box.SelectedItem = chosen end)
                end
            end
            -- The list is read again by its handle: the game's own handler of a selection may
            -- rebuild the list, and the element held here then answers for a list that is gone.
            local again = UI.Handle(name, row.handle)
            local now = again and guard(function() return again.element.SelectedIndex end)
            local result = { ok = true, selected = index, of = #items, was = selectedBefore and selectedBefore + 1 or nil,
                             reads = now and now + 1 or nil, warning = failed,
                             text = describeItem(items[index], from == "shown" and row.containers[index] or nil, index).text }
            if now == index - 1 then return result end
            -- a list that does not keep its selection (a row of buttons) is judged by what the window shows
            return UI.ObserveEffect(result, name, nil, before, 1500,
                "the list does not read the item as selected and the window shows no change")
        end
    end
    -- The items are wherever a count can be read: a list bound to a view model (the races of
    -- character creation) may hand out an Items that has no length, while its ItemsSource - the
    -- same collection GK.Gui.Read counts - has one. The first source that counts more than
    -- nothing wins; a source with no length at all never hides the next.
    if type(item) == "userdata" then
        local _, problem = guard(function() box.SelectedItem = item end)
        if problem then return { ok = false, error = problem } end
        if guard(function() return box.SelectedItem end) == nil then
            return { ok = false, error = "the list did not take that item (it is not one of its items?)" }
        end
        return { ok = true, selected = "item" }
    end
    local items, count = nil, 0
    for _, source in ipairs({ "Items", "ItemsSource" }) do
        local candidate = guard(function() return box[source] end)
        local n = length(candidate)
        if type(n) == "number" and (items == nil or n > count) then items, count = candidate, n end
        if count > 0 then break end
    end
    if items == nil then
        return { ok = false, error = "the list " .. tostring(list) .. " has neither Items nor ItemsSource to read" }
    end
    local index = type(item) == "number" and item or nil
    if not index and type(item) == "string" then
        for i = 1, count do
            local value = guard(function() return items[i] end)
            local text = value and (guard(function() return value.Name end) or guard(function() return value.Title end)) or nil
            text = readable(text)
            if type(text) == "string" and GK.Fold(text):find(GK.Fold(item), 1, true) then index = i break end
        end
    end
    if not index or index < 1 or index > count then
        return { ok = false, error = "no item " .. tostring(item) .. " (the list has " .. count .. ")" }
    end
    local _, problem = guard(function() box.SelectedIndex = index - 1 end)
    -- What the list reads afterwards decides, not whether the setter raised: the game's own handler
    -- of a selection can fail after the selection is made (N22 of the third run: "Param 3: expected a
    -- light C++ object, got nil" while the origin did change).
    if guard(function() return box.SelectedIndex end) == index - 1 then
        return { ok = true, selected = index, of = count, warning = problem }
    end
    -- a list that will not take an index may still take the item itself
    local chosen = guard(function() return items[index] end)
    if chosen == nil then
        return { ok = false, error = "item " .. index .. " of " .. count .. " could not be read, and the list did not take the index"
            .. (problem and (": " .. problem) or "") }
    end
    local _, second = guard(function() box.SelectedItem = chosen end)
    if guard(function() return box.SelectedIndex end) ~= index - 1 and second then
        return { ok = false, error = problem or second }
    end
    return { ok = true, selected = index, of = count, warning = second or problem }
end

--- What a key does in a widget: runs its input binding for that event ("UIAccept", "UICancel",
--- "UITakeAll", "UIMessageBoxY" ...).
function UI.Input(name, event)
    local widget = UI.Widget(name)
    if not widget then return { ok = false, error = "not open: " .. tostring(name) } end
    local entry = entryOf(widgetName(name))
    if not entry then return { ok = false, error = "widget not in the catalogue: " .. tostring(name) } end
    for _, input in ipairs(entry.inputs or {}) do
        if input.event == event and (input.command or input.click) then
            local log = {}
            local control = input.name and findElement(widget, input.name) or nil
            local before = UI.Picture(name, input.name)
            UI.LastDelayed = {}
            local _, delayed = runChain(input, control, widget, log, widgetName(name), input.name)
            return UI.ObserveEffect({ ok = true, steps = log, did = "the " .. tostring(event) .. " binding" }, name, input.name, before,
                (delayed or 0) > 0 and (delayed + 1200) or 1500)
        end
    end
    return { ok = false, error = tostring(name) .. " has no input binding for " .. tostring(event) }
end

-- Whether a value of a widget's data is a command (something Run can execute).
local function isCommand(value)
    if type(value) ~= "userdata" then return false end
    if typeOf(value):find("Command", 1, true) then return true end
    return guard(function() return value.CanExecute end) ~= nil
end

local function describe(value)
    if type(value) == "userdata" then return typeOf(value) end
    return type(value) .. " " .. tostring(value)
end

--- Runs a command of a widget's data by path ("FinishLevelUp", "CurrentPlayer.SomeCommand");
--- a string, number or boolean parameter is boxed for the game. A command takes one parameter
--- (the game's ICommand does); several values go as the one object the command expects. A path that leads to a value
--- rather than a command is refused, naming GK.Cheat.Set.
function Cheat.Run(name, path, parameter)
    local data = UI.Data(name)
    if not data then return { ok = false, error = "not open: " .. tostring(name) } end
    local command = follow(data, path)
    if command == nil then return { ok = false, error = "no command " .. tostring(path) } end
    if not isCommand(command) then
        return { ok = false, error = tostring(path) .. " is not a command but a value (" .. describe(command)
            .. "); GK.Cheat.Set(name, path, value) sets a property" }
    end
    local kind = type(parameter)
    if kind == "string" or kind == "number" or kind == "boolean" then parameter = Ext.UI.Box(parameter) end
    -- The game's own CanExecute first: a command it turns down ran "ok" and did nothing (N26 of
    -- the third run, the Combine widget's commands). A command that cannot be asked is run.
    local known, can = pcall(function() return command:CanExecute(parameter) end)
    if known and can == false then
        return { ok = false, error = tostring(path) .. " cannot run now: the game's CanExecute says no for that parameter (nothing was done)" }
    end
    local before = UI.Picture(name)
    local _, problem = guard(function() command:Execute(parameter) end)
    if problem then return { ok = false, error = problem } end
    -- ran is not did: the answer waits to see the window change
    return UI.ObserveEffect({ ok = true, ran = path, did = "ran " .. tostring(path), canExecute = known and can or nil },
        name, nil, before, 1500, "the command ran and the window shows no change: read what it should have changed")
end

--- Sets a property of a widget's data by path ("FieldOfView", "SelectedRace",
--- "CurrentPlayer.Something") through the view model's SetProperty, as a binding of the window
--- would, and reads it back: { ok, before, after }. ok is false when the value read back is not
--- the one set - the game recomputes some properties from others and keeps its own.
function Cheat.Set(name, path, value)
    local data = UI.Data(name)
    if not data then return { ok = false, error = "not open: " .. tostring(name) } end
    local parentPath, property = tostring(path):match("^(.*)%.([^%.]+)$")
    if not property then parentPath, property = "", tostring(path) end
    local owner = follow(data, parentPath)
    if owner == nil then return { ok = false, error = "no " .. tostring(parentPath) .. " in " .. tostring(name) } end
    local before = guard(function() return owner:GetProperty(property) end)
    if before == nil then before = guard(function() return owner[property] end) end
    if before == nil then return { ok = false, error = "no property " .. tostring(path) .. " in " .. tostring(name) } end
    local _, problem = guard(function() owner:SetProperty(property, value) end)
    if problem and type(value) ~= "userdata" then
        -- some properties take only a boxed value
        local boxed = guard(function() return Ext.UI.Box(value) end)
        if boxed ~= nil then _, problem = guard(function() owner:SetProperty(property, boxed) end) end
    end
    if problem then return { ok = false, error = tostring(path) .. ": " .. problem } end
    local after = guard(function() return owner:GetProperty(property) end)
    if after == nil then after = guard(function() return owner[property] end) end
    local same = after == value or (type(after) == "number" and type(value) == "number" and math.abs(after - value) < 1e-4)
    local function show(v) return type(v) == "userdata" and typeOf(v) or v end
    if type(value) == "userdata" then
        -- two handles on one game object need not compare equal in Lua: not checked
        return { ok = true, before = show(before), after = show(after), checked = false }
    end
    if not same then
        return { ok = false, before = show(before), after = show(after),
                 error = "set, but " .. tostring(path) .. " reads " .. tostring(show(after)) .. " afterwards: the game keeps its own value" }
    end
    return { ok = true, before = show(before), after = show(after) }
end

-- Reading --------------------------------------------------------------------------------

local function plain(value, depth, options, seen)
    local kind = type(value)
    if kind ~= "userdata" then
        if kind == "string" then return readable(value) end
        -- JSON has no NaN or infinity: such numbers go back as text
        if kind == "number" and (value ~= value or value == math.huge or value == -math.huge) then return tostring(value) end
        if kind == "table" or kind == "function" then return tostring(value) end
        return value
    end
    local typeName = typeOf(value)
    -- commands are left out; a view model that only has "Command" in its name (ls.GameCommand,
    -- the buttons of a message box) is data and stays
    if typeName:find("^Noesis::.*Command") or typeName == "ui::DeferredCommand" then return nil end
    -- a collection (Noesis::BaseCollection) has a length and no Type
    local count = typeName == "?" and guard(function() return #value end) or nil
    if depth <= 0 then
        if type(count) == "number" then return { count = count } end
        return "<" .. typeName .. ">"
    end
    if type(count) == "number" then
        -- a table that is both a list and a record does not survive JSON: the items go apart
        local items = {}
        for i = 1, math.min(count, options.items or 8) do
            local item = plain(guard(function() return value[i] end), depth - 1, options, seen)
            items[#items + 1] = item == nil and "nil" or item
        end
        return { count = count, items = items }
    end
    local all = guard(function() return value:GetAllProperties() end)
    if type(all) ~= "table" then return "<" .. typeName .. ">" end
    local out = { ["$type"] = typeName }
    for key, inner in pairs(all) do
        local k = tostring(key)
        if not (options.only and not options.only[k]) then
            local v = plain(inner, depth - 1, options, seen)
            if v ~= nil then out[k] = v end
        end
    end
    return out
end

--- Any view model (or Noesis object) as plain values: properties, collections as
--- { count, items = { ... } }, commands left out. options: depth (default 2), items (default 8).
function UI.Snapshot(object, options)
    options = options or {}
    return plain(object, options.depth or 2, options, {})
end

--- The data of an open widget: the window's own reader (GK.Gui.Windows[x].read) when it has one,
--- else plain values (see GK.Gui.Snapshot). options: path ("CurrentPlayer.PartyInventory"), depth,
--- items, raw = true (plain values even when a reader exists).
function UI.Read(name, options)
    options = options or {}
    local data = UI.Data(name)
    if not data then return { ok = false, error = "not open: " .. tostring(name) } end
    local window = W[name]
    if window and window.read and not options.raw and not options.path then
        local summary, problem = guard(window.read, data, UI)
        if summary ~= nil then return { ok = true, widget = widgetName(name), summary = summary } end
        return { ok = false, error = problem or "the reader of " .. name .. " returned nothing" }
    end
    if options.path then
        data = follow(data, options.path)
        if data == nil then return { ok = false, error = "no " .. options.path } end
    end
    return { ok = true, widget = widgetName(name), path = options.path, data = UI.Snapshot(data, options) }
end

-- Steps: windows that ask the player for choices get a `step` function that makes ONE missing
-- choice (the first allowed option, or what `options.pick` prefers) and says what is left.
-- GK.Cheat.Step(window, options) runs it once; GK.Cheat.Auto(window, options) repeats it in the
-- game (every options.every ms, default 400) until the window says done, closes, or options.limit
-- steps (default 40) pass. GK.Gui.AutoState[window] tells how far it is.

local function count(collection)
    return guard(function() return #collection end) or 0
end

local function item(collection, i)
    return guard(function() return collection[i] end)
end

-- Level-up: the order a player goes through it - spell picks of every selector, passives
-- (fighting styles, invocations), feat, then Accept. The subclass is preselected by the game.
W.levelup.step = function(ui, options)
    local data = ui.Data("CharacterLevelUp")
    if not data then return { done = true, did = "closed" } end
    if guard(function() return data.IsLevelUpComplete end) then
        local result = ui.Click("CharacterLevelUp", "finishLevelUpButton")
        if result.ok and result.note then
            -- the click's own command is the cosmetic part: the level is finished by FinishLevelUp
            result = Cheat.Run("CharacterLevelUp", "FinishLevelUp")
            return { did = "accept (FinishLevelUp: the click chain is not known here)", ok = result.ok, error = result.error, wait = 3000 }
        end
        return { did = "accept", ok = result.ok, error = result.error, wait = 3000 }
    end
    local details = guard(function() return data.ClassProgressionDetails end)
    -- spells: each selector is a step in the list on the left; its spell buttons appear when selected
    local selectors = {}
    for _, list in ipairs({ "SpellSelectors", "SubSpellSelectors" }) do
        local collection = details and guard(function() return details[list] end)
        for i = 1, count(collection) do selectors[#selectors + 1] = item(collection, i) end
    end
    for index, selector in ipairs(selectors) do
        if guard(function() return selector.IsComplete end) == false then
            local title = guard(function() return selector.Title end)
            local picked = ui.Click("CharacterLevelUp", { data = "SpellSelector", text = nil }, index)
            local added = guard(function() return selector.AddedCount end) or 0
            local button = ui.Click("CharacterLevelUp", "SelectAvailableSpell", added + 1)
            return { did = "spell for " .. tostring(title), ok = button.ok, error = button.error or picked.error }
        end
    end
    -- passives offered by a selector (fighting style, invocations ...)
    for _, list in ipairs({ "NotSubPassiveSelectors", "SubPassiveSelectors" }) do
        local collection = details and guard(function() return details[list] end)
        for i = 1, count(collection) do
            local selector = item(collection, i)
            if guard(function() return selector.IsComplete end) == false then
                local available = guard(function() return selector.Available end)
                for k = 1, count(available) do
                    local passive = item(available, k)
                    if guard(function() return passive.IsSelected end) ~= true then
                        local run = Cheat.Run("CharacterLevelUp", "TogglePassive", passive)
                        return { did = "passive", ok = run.ok, error = run.error }
                    end
                end
            end
        end
    end
    -- a feat, when the level gives one (options.feat: part of its name)
    if guard(function() return data.CanSelectFeat end) then
        local chosen = guard(function() return data.SelectedFeat end)
        if chosen == nil then
            local feats = guard(function() return data.SelectableFeats end)
            local pick
            for i = 1, count(feats) do
                local feat = item(feats, i)
                local name = guard(function() return feat.Name end) or ""
                local allowed = guard(function() return feat.IsRequirementsMet end) ~= false
                    and guard(function() return feat.IsAlreadyTaken end) ~= true
                if allowed and (not options.feat or GK.Fold(name):find(GK.Fold(options.feat), 1, true)) then pick = feat break end
            end
            if pick then
                local _, problem = guard(function() data.SelectedFeat = pick end)
                return { did = "feat " .. tostring(guard(function() return pick.Name end)), ok = problem == nil, error = problem }
            end
        end
        -- the feat's own choices: ability points ("+2 to one or +1 to two"), its passives, its spells
        local feat = guard(function() return data.SelectedFeatDetails.FeatDetails end)
        if feat and guard(function() return feat.IsAbilitySelectionComplete end) == false then
            local abilities = guard(function() return feat.AbilitySelection end)
            -- the highest ability that can still grow (options.ability: its name, e.g. "Dexterity")
            local best, bestValue
            for i = 1, count(abilities) do
                local ability = item(abilities, i)
                local name = tostring(guard(function() return ability.Ability end))
                local value = guard(function() return ability.Value end) or 0
                -- CanIncrease belongs to point-buy and reads false here; the cap is 20
                local canGrow = value + (guard(function() return ability.Improvement end) or 0) < 20
                if canGrow and (options.ability and name == options.ability or (not options.ability and (not bestValue or value > bestValue))) then
                    best, bestValue = ability, value
                end
            end
            if best then
                local run = Cheat.Run("CharacterLevelUp", "SelectAbility", best)
                return { did = "ability " .. tostring(guard(function() return best.Ability end)), ok = run.ok, error = run.error }
            end
        end
    end
    return { did = "nothing", stuck = true, error = "level-up not complete and no missing choice found" }
end

function Cheat.Step(name, options)
    local window = W[name]
    if not window or not window.step then return { ok = false, error = "no step for " .. tostring(name) } end
    if not UI.IsOpen(window.widget) then return { ok = false, done = true, error = "not open: " .. tostring(name) } end
    local result, problem = guard(window.step, UI, options or {})
    if not result then return { ok = false, error = problem } end
    if result.ok == nil then result.ok = not result.error end
    return result
end

UI.AutoState = UI.AutoState or {}

function Cheat.Auto(name, options)
    options = options or {}
    local state = { running = true, steps = 0, log = {}, started = Ext.Utils.MonotonicTime() }
    UI.AutoState[name] = state
    local limit, every = options.limit or 40, options.every or 400
    local function tick()
        Ext.UI.Defer(function()
            if UI.AutoState[name] ~= state or not state.running then return end
            local result = Cheat.Step(name, options)
            state.steps = state.steps + 1
            state.log[#state.log + 1] = tostring(result.did or result.error)
            if result.done or result.stuck or state.steps >= limit then
                state.running = false
                state.result = result
                return
            end
            -- The step's `wait` is the longest pause before the next step; a change the attention
            -- record sees first (the prompt closed, the box went, the dialogue moved to a roll)
            -- starts the next step at once. Polled every 50 ms of game timers.
            local mark = GK.Attention and GK.Attention.Seq or nil
            local deadline = Ext.Utils.MonotonicTime() + (result.wait or every)
            local function pause()
                if UI.AutoState[name] ~= state then return end
                if (mark and GK.Attention.Seq ~= mark) or Ext.Utils.MonotonicTime() >= deadline then
                    state.early = (state.early or 0) + ((mark and GK.Attention.Seq ~= mark) and 1 or 0)
                    return tick()
                end
                Ext.Timer.WaitFor(50, pause)
            end
            Ext.Timer.WaitFor(50, pause)
        end)
    end
    tick()
    return { ok = true, started = true }
end

-- Items: what the player does to an item in the inventory, from code. The view model of an item
-- is found by (part of) its name in the selected character's inventory or equipment, and the
-- character panel's own command runs with it - the same command a click or a drag runs.
-- Containers carried in the inventory are looked into (N46 of the third run: herbs, extracts and
-- arrows live in the alchemy pouch, a container item, and were not found), two levels deep.

local function containerItems(object, out, depth)
    if depth > 2 or guard(function() return object.IsContainer end) ~= true then return end
    local inventories = guard(function() return object.Inventories end)
    for k = 1, count(inventories) do
        local slots = guard(function() return item(inventories, k).Slots end)
        for i = 1, count(slots) do
            local inner = guard(function() return item(slots, i).Object end)
            if inner then
                out[#out + 1] = inner
                containerItems(inner, out, depth + 1)
            end
        end
    end
end

local function itemVMs(data)
    local out = {}
    local player = guard(function() return data.CurrentPlayer end)
    local character = player and guard(function() return player.SelectedCharacter end)
    local inventory = character and guard(function() return character.Inventory end)
    local slots = inventory and guard(function() return inventory.Slots end)
    local containers = {}
    for i = 1, count(slots) do
        local object = guard(function() return item(slots, i).Object end)
        if object then
            out[#out + 1] = object
            containers[#containers + 1] = object
        end
    end
    -- what is inside comes after everything the character carries loose, so a loose item wins
    for _, object in ipairs(containers) do containerItems(object, out, 1) end
    local equipment = character and guard(function() return character.Equipment end)
    if equipment then
        local all = guard(function() return equipment:GetAllProperties() end) or {}
        for _, slot in pairs(all) do
            local object = type(slot) == "userdata" and guard(function() return slot.Item end)
            if object then out[#out + 1] = object end
        end
    end
    return out
end

--- The view model of an item of the selected character (inventory or equipment) by part of its
--- name as the player reads it, or nil.
function UI.Item(text)
    local data = UI.Data("HotBar")
    if not data then return nil end
    local wanted = GK.Fold(text)
    for _, object in ipairs(itemVMs(data)) do
        local name = readable(guard(function() return object.Name end))
            or readable(guard(function() return object.NameAlchemy end)) or ""
        if GK.Fold(name):find(wanted, 1, true) then return object, name end
    end
end

-- Checked 2026-11-13 on the tutorial ship: EquipItemCommand does nothing when run (the game's
-- XAML never binds it); equipping is what a double-click does - UseGameObject on the item.
local ITEM_COMMANDS = { equip = "UseGameObject", unequip = "UnequipItemCommand", use = "UseGameObject",
                        drop = "DropGameObject" }

--- Does to an item what the player does: "equip", "unequip", "use" (drink, read, open), "drop".
--- The character panel need not be open (the HUD's data carries the same commands). ok says the
--- game's command ran, not what it did: that shows a moment later.
function UI.ItemDo(action, text)
    local command = ITEM_COMMANDS[action]
    if not command then return { ok = false, error = "unknown action " .. tostring(action) } end
    local object, name = UI.Item(text)
    if not object then
        return { ok = false, error = "no item like " .. tostring(text) .. " in the selected character's inventory, "
            .. "equipment or the containers it carries" }
    end
    -- N48 of the third run: a Combine window left open (in Success) stayed, and a later dye's use
    -- answered ok with the old window still there - a use starts from no Combine window.
    local closed
    if action == "use" and UI.Widget("Combine") then
        local data = UI.Data("Combine")
        local state = data and guard(function() return tostring(data.CurrentState) end)
        local result = UI.Close("Combine")
        closed = "Combine (" .. tostring(state) .. ")" .. (result.ok and "" or (": not closed - " .. tostring(result.error)))
    end
    local source = UI.IsOpen("CharacterPanel") and "CharacterPanel" or "HotBar"
    -- what the item and the windows look like before: an effect is a change of either (N47, D6:
    -- an arrow's use and a mallet's equip answered ok and did nothing)
    local itemBefore = {}
    scalarsOf(object, itemBefore, "", 0)
    local before = UI.Picture(nil)
    local result = GK.Unobserved(Cheat.Run(source, command, object))
    result.item = name
    result.via = source .. "." .. command
    result.closedFirst = closed
    if not result.ok then return result end
    result.did = "the game ran " .. command .. " on " .. tostring(name)
    UI.LastItemUse = { action = action, item = name, at = Ext.Utils.MonotonicTime() }
    local lastLook = -1000
    return GK.Observe(result, function(ticks, final)
        if ticks - lastLook < 3 and not final then return nil end
        lastLook = ticks
        local again = UI.Item(name)
        if action == "equip" or action == "unequip" then
            -- only the item's own equipped state counts: a flag that blinks (IsInUse) is not an equip
            local state = again and guard(function() return tostring(again.Equipped) end)
            local equipped = state ~= nil and state ~= "NotEquipped"
            if again and equipped == (action == "equip") then
                result.saw = "the item is " .. (equipped and ("equipped (" .. state .. ")") or "not equipped")
                return result
            end
            if not final then return nil end
            return { ok = false, did = result.did, item = name, via = result.via,
                     saw = "nothing changed: the item is " .. (equipped and "still equipped" or "not equipped"),
                     hint = action == "equip" and "the item may not be wearable by this character (proficiency, slot), or in a fight armour cannot change; GK.Cheat.Equip asks the game directly" or nil }
        end
        local changed = UI.Changed(before, UI.Picture(nil))
        if not again then
            changed[#changed + 1] = "the item is no longer carried (used up, dropped or moved)"
        else
            local itemAfter = {}
            scalarsOf(again, itemAfter, "", 0)
            for key, value in pairs(itemAfter) do
                if itemBefore[key] ~= value and #changed < 6 then
                    changed[#changed + 1] = "item " .. key .. ": " .. tostring(itemBefore[key]) .. " -> " .. tostring(value)
                end
            end
        end
        if #changed > 0 then
            result.saw = table.concat(changed, "; ")
            return result
        end
        if not final then return nil end
        return { ok = false, did = result.did, item = name, via = result.via, saw = "nothing changed",
                 hint = action == "equip" and "the item may not be wearable by this character (proficiency, slot), or in a fight armour cannot change"
                     or "nothing the game shows changed: the use may need a target or a window" }
    end, 2000)
end

-- Tutorials: off at the source, and whatever still pops up closed as it appears ---------------

-- The ways the game shows a tutorial, and how each goes away (the first that works).
local TUTORIALS = {
    { widget = "ModalTutorial", run = { "FinishTutorial" } },
    { widget = "TutorialPopup", run = {} },
}

local function closeTutorials()
    local closed = {}
    for _, tutorial in ipairs(TUTORIALS) do
        if UI.Widget(tutorial.widget) then
            local done = false
            for _, command in ipairs(tutorial.run) do
                if not done and Cheat.Run(tutorial.widget, command).ok then done = true end
            end
            if not done then UI.Close(tutorial.widget) end
            closed[#closed + 1] = tutorial.widget
        end
    end
    -- side tutorials: the notification widget's list of hints
    local notifications = UI.Data("NotificationWidget")
    if notifications and UI.Find("NotificationWidget", "SideTutorials") then
        local hide = guard(function() return notifications.HideUnifiedTutorialCommand end)
        if hide and guard(function() return UI.Find("NotificationWidget", "SideTutorials").VisualChildrenCount end) or 0 > 0 then
            guard(function() hide:Execute(nil) end)
            closed[#closed + 1] = "side tutorial"
        end
    end
    return closed
end

UI.QuietState = UI.QuietState or { on = false, closed = 0 }

--- Tutorials off (on = false turns them back on): the game's own switch (GlobalSwitches
--- ShowTutorials, what the options menu sets), and every half second whatever tutorial still
--- shows is closed. GK.Gui.QuietState counts what was closed.
function Cheat.Quiet(on)
    local state = UI.QuietState
    state.on = on ~= false
    local switches = Ext.Utils.GetGlobalSwitches()
    guard(function() switches.ShowTutorials = not state.on end)
    if state.on then guard(function() switches.ShowTutorialsAnswered = true end) end
    if state.on and not state.ticking then
        state.ticking = true
        local function tick()
            if not state.on then state.ticking = false return end
            Ext.UI.Defer(function()
                local closed = closeTutorials()
                state.closed = state.closed + #closed
                if #closed > 0 then state.last = closed end
            end)
            Ext.Timer.WaitFor(500, tick)
        end
        tick()
    end
    return { ok = true, on = state.on, closed = state.closed }
end

-- Game over and the main menu: back into the game ------------------------------------------

--- What holds the game outside play: "film", "gameover" (the "party defeated" box), "firstlaunch",
--- "splash" (the screen before the main menu), "mainmenu", or nil; second result: the screen.
-- The screens a copy of the game shows on its first way to the main menu, and the button that
-- moves on. The owner's standing consent to the game's own agreements covers GateKit's copies
-- (research/open-items.md, "Decided here").
local FIRST_LAUNCH = {
    { widget = "Eula", button = "AcceptBtn" },
    -- Larian's mod policy (terms and privacy): both boxes ticked, then Accept.
    { widget = "ModBrowserSplashScreen", check = { "LarianModTermsCheckbox", "LarianModPrivacyCheckbox" },
      button = "AcceptButton", command = "LarianModPolicyAcceptCommand" },
    { widget = "SensitiveSettings", button = "NextButton" },
    -- the calibration screens page through their pictures with "Next"; their last button closes
    -- the widget, which is what is done here at once
    { widget = "HDRCalibration", close = true },
    { widget = "GammaCalibration", close = true },
}

-- The screens every start of the game passes on its way to the main menu, and the game input
-- event each one's own page raises on a key or a click (Pages/SplashScreen.xaml: any key or
-- mouse button raises CustomEvent "OpenMainMenu"). No agreement is involved: what a player's
-- key does, in the installed game as in a copy.
local PASS_THROUGH = {
    { widget = "SplashScreen", event = "OpenMainMenu" },
}

--- Whether the game is playing a recorded film (a video, not an engine scene: the one at a new
--- game, the one after character creation). Nothing of the UI shows it - the client only sits in
--- StartLoading - so it is read where the engine keeps it: ecl::GameControl's movie player
--- (+0x190) and its ls::VideoManager (+0x38), whose playing flag is at +0x10
--- (ls::VideoManager::SetPlaying, ecl::GameControl::HasMovie). GameControl is reached through the
--- camera system, which holds it at +0x128 in bg3_dx11.exe (ecl::CameraSystem::SnapFade,
--- bg3_dx11.exe+0x1E14B40; the Toolkit's build has it at +0x138). Returns true or false, or nil
--- and why when it cannot be read (no GateKit extender, no client world yet).
function UI.FilmPlaying()
    local debug = Ext.Debug or {}
    if not (debug.GetSystemAddress and debug.ReadMemory) then return nil, "needs GateKit's extender (Ext.Debug.GetSystemAddress)" end
    local system = guard(function() return debug.GetSystemAddress("ecl::CameraSystem") end)
    if not (system and system.Address) then return nil, "no ecl::CameraSystem in the client world yet" end
    local function pointer(address, offset)
        local bytes = debug.ReadMemory(address + offset, 8)
        return bytes and string.unpack("<i8", bytes) or nil
    end
    local control = pointer(system.Address, 0x128)
    if not control or control == 0 then return nil, "the camera holds no GameControl" end
    local player = pointer(control, 0x190)
    if not player or player == 0 then return false end
    local video = pointer(player, 0x38)
    if not video or video == 0 then return false end
    local flag = debug.ReadMemory(video + 0x10, 1)
    if not flag then return nil, "the video manager could not be read" end
    return flag:byte() ~= 0
end

function UI.Stuck()
    if UI.FilmPlaying() == true then return "film" end
    local box = UI.Data("Dialog_box")
    if box and guard(function() return box.IsGameOver end) then return "gameover" end
    for _, screen in ipairs(FIRST_LAUNCH) do
        if UI.Widget(screen.widget) then return "firstlaunch", screen end
    end
    for _, screen in ipairs(PASS_THROUGH) do
        if UI.Widget(screen.widget) then return "splash", screen end
    end
    if UI.Widget("MainMenu") then return "mainmenu" end
    return nil
end

-- Presses a button of a message box by the input event it is bound to ("UIAccept", "UICancel").
local function pressBox(event)
    local box = UI.Data("Dialog_box")
    local actions = box and guard(function() return box.Actions end)
    for i = 1, count(actions) do
        local action = item(actions, i)
        if guard(function() return action.BoundEvent end) == event then
            local command = guard(function() return action.ActionCommand end)
            local parameter = guard(function() return action.ActionCommandParameter end)
            local _, problem = guard(function() command:Execute(parameter) end)
            return { ok = problem == nil, error = problem, pressed = readable(guard(function() return action.Name end)) }
        end
    end
    return { ok = false, error = "no button for " .. tostring(event) }
end
UI.PressBox = pressBox

--- One step out of a dead end without restarting the game. On "game over" the server is paused
--- and nothing can be raised; the way out is the game's own: "Load game", then the newest save
--- (the copy's checkpoint). From the main menu: "Continue"; first-launch screens are clicked through. Returns what it did; call again until GK.Gui.Stuck()
--- is nil and the game state is Running (a load takes seconds).
function Cheat.Recover()
    local stuck, screen = UI.Stuck()
    if stuck == "film" then
        -- The game's own skip: Escape (as a player would). A film whose end is held by GK.Pause
        -- (bg3_pause) does not end at all - its fade waits for game time - so the answer says so.
        local _, problem = guard(function() return GK.Key("ESCAPE") end)
        return { ok = problem == nil, error = problem, did = "film: Escape pressed to skip it",
                 note = "if bg3_pause holds the game, unpause it: the film's end waits for game time; ask GK.Gui.Stuck() again" }
    elseif stuck == "splash" then
        local result = UI.Event(screen.event, screen.widget)
        result.did = "splash screen: " .. screen.widget .. " raised " .. screen.event .. ", as its key does"
        return result
    elseif stuck == "firstlaunch" then
        -- Boxes that must be ticked before the button means anything: ticked, never unticked.
        local ticked = {}
        for _, name in ipairs(screen.check or {}) do
            local box = UI.Find(screen.widget, name)
            if not box then
                return { ok = false, error = "first launch: " .. screen.widget .. " has no " .. name, did = "first launch: " .. screen.widget }
            end
            if guard(function() return box.IsChecked end) ~= true then
                local command = guard(function() return box.Command end)
                if command then
                    local parameter = guard(function() return box.CommandParameter end)
                    if type(parameter) ~= "userdata" and parameter ~= nil then parameter = Ext.UI.Box(parameter) end
                    guard(function() command:Execute(parameter) end)
                end
                if guard(function() return box.IsChecked end) ~= true then guard(function() box.IsChecked = true end) end
            end
            ticked[#ticked + 1] = name .. (guard(function() return box.IsChecked end) == true and "" or " (did not take)")
        end
        local result = screen.close and UI.Close(screen.widget) or UI.Click(screen.widget, screen.button)
        if not result.ok and screen.command then
            -- the button's own command, read off the widget's view model
            local widget = UI.Widget(screen.widget)
            local command = widget and guard(function() return widget.DataContext[screen.command] end)
            local _, problem = guard(function() command:Execute(nil) end)
            if command and not problem then
                result = { ok = true, control = screen.command, steps = { "ran " .. screen.command } }
            end
        end
        result.ticked = #ticked > 0 and ticked or nil
        result.did = "first launch: " .. screen.widget
        return result
    elseif stuck == "gameover" then
        -- "Load game" opens the load window over the game; the newest save loads from there
        -- (GK.Gui.LoadLatest, Popups.lua). The main menu way works too but leads a fresh copy
        -- of the game through its first-launch screens.
        if UI.Widget("LoadGame") and UI.LoadLatest then
            local result = UI.LoadLatest()
            result.did = "game over: load the newest save"
            return result
        end
        local result = pressBox("UIAccept")  -- "Load game"
        result.did = "game over: load window"
        return result
    elseif stuck == "mainmenu" then
        local menu = UI.Data("MainMenu")
        local continue = menu and guard(function() return menu.ContinueGameCommand end)
        if not continue then return { ok = false, error = "the main menu has no Continue (no save?)" } end
        local _, problem = guard(function() continue:Execute(nil) end)
        return { ok = problem == nil, error = problem, did = "main menu: continue (latest save)" }
    end
    return { ok = true, did = "nothing holds the game" }
end

local function clientState()
    local ok, state = pcall(function() return tostring(Ext.Utils.GetGameState()) end)
    return ok and state or nil
end

-- The new-game settings: the page NewGameSettings.xaml, whose widget is named DMSettings.
local function newGameSettings()
    return UI.Widget("DMSettings") or UI.Widget("NewGameSettings")
end

--- A new game from the main menu, the way a player starts one: the main menu's New Game button
--- (StartGameCommand) opens the new-game settings, whose Confirm runs StartNewGame with the preset
--- selected in its list. One step per call, and each answers what it saw: ok once the settings
--- are open, then ok once the game has left the menu (character creation loads); otherwise
--- ok = false with what was seen. Commands run here, in the UI's context: run from a timer they
--- do nothing at all.
function UI.NewGame()
    local settings = newGameSettings()
    if settings then
        local confirm = findElement(settings, "ConfirmBtn")
        if not confirm then return { ok = false, error = "the new-game settings have no ConfirmBtn" } end
        local command = guard(function() return confirm.Command end)
        local parameter = guard(function() return confirm.CommandParameter end)
        if not command then return { ok = false, error = "ConfirmBtn has no command" } end
        if parameter == nil then return { ok = false, error = "no preset is selected in the new-game settings" } end
        local _, problem = guard(function() command:Execute(parameter) end)
        if problem then return { ok = false, error = "Confirm failed: " .. problem } end
        return GK.Observe({ ok = true, did = "new game: settings confirmed" }, function(_, final)
            local state = clientState()
            if state and state ~= "Menu" then
                return { ok = true, did = "new game: settings confirmed; the game left the menu (" .. state .. ")", state = state }
            end
            if final then
                return { ok = false, error = "Confirm was pressed and the game stayed in the menu", state = state,
                         settings = newGameSettings() ~= nil }
            end
        end, 3000)
    end
    local menu = UI.Data("MainMenu")
    if not menu then return { ok = false, error = "neither the main menu nor the new-game settings are open" } end
    local start = guard(function() return menu.StartGameCommand end)
    if not start then return { ok = false, error = "the main menu has no StartGameCommand" } end
    local _, problem = guard(function() start:Execute(nil) end)
    if problem then return { ok = false, error = "New Game failed: " .. problem } end
    return GK.Observe({ ok = true, did = "new game: main menu's New Game" }, function(_, final)
        if newGameSettings() then
            return { ok = true, did = "new game: main menu's New Game; the settings are open (call again to confirm)" }
        end
        if final then
            return { ok = false, error = "New Game was pressed and the new-game settings did not open", state = clientState() }
        end
    end, 3000)
end

-- Tooltips: read as a chain, opened, pinned and closed by handle -------------------------------
--
-- How the game shows a tooltip. An element (or an inline of a text: a keyword in a description)
-- carries its tooltip in the UI library's own ToolTip property, an ls.LSTooltip. A hover of the
-- pointer sets the tooltip's IsOpen after the library's delay, and the tooltip is then drawn in a
-- popup layer above the canvas, outside every widget; for an inline the game's tooltip also records
-- which inline and which text block it belongs to (Inline, InlineParent) and makes its content
-- from them. The input event UIPinTooltip moves the open tooltip into the list of pinned ones
-- (ls.UIWidget.PinnedTooltipList, shown by the widget PinnedTooltips as ls.LSElementCopy elements):
-- a pinned tooltip stays, takes the pointer, and a link inside it opens the next tooltip, which
-- can be pinned in turn. A pinned tooltip's PlacementTarget is the element it was called from, so
-- the chain is read from where each target lies.

local function addressOf(object)
    return object ~= nil and tostring(object):match("%((%x+)%)") or nil
end

-- The top of the UI tree: popup layers hang there, beside the canvas that holds the widgets.
local function topVisual()
    local element, steps = guard(function() return Ext.UI.GetRoot() end), 0
    while element and steps < 16 do
        local parent = guard(function() return element.VisualParent end)
        if not parent then break end
        element, steps = parent, steps + 1
    end
    return element
end

-- The tooltips a hover holds open: every tooltip element of a popup layer.
local function hoverTips()
    local out = {}
    local function walk(element, depth)
        if depth > 6 then return end
        local typeName = typeOf(element)
        if typeName == "ls.UICanvas" then return end
        if typeName:find("Tooltip", 1, true) or typeName:find("ToolTip", 1, true) then out[#out + 1] = element return end
        for i = 1, guard(function() return element.VisualChildrenCount end) or 0 do
            local child = guard(function() return element:VisualChild(i) end)
            if child then walk(child, depth + 1) end
        end
    end
    local top = topVisual()
    if top then walk(top, 0) end
    return out
end

-- The pinned tooltips: the list every widget may keep (the game's PinnedTooltips widget does).
-- Each is { shown, listed }: the widget draws a copy of its own (an ls.LSElementCopy in its tree,
-- which holds the texts) for every element of the list (which holds the PlacementTarget); the two
-- are paired by the content they share, else by their order.
local function pinnedTips()
    local out, holder = {}, nil
    for _, open in ipairs(UI.Open()) do
        local widget = UI.Widget(open.name)
        local list = widget and guard(function() return widget.PinnedTooltipList end)
        local count = list and guard(function() return #list end) or 0
        if count > 0 then
            holder = widget
            local shown, stack, budget = {}, { widget }, 4000
            while #stack > 0 and budget > 0 do
                local e = table.remove(stack)
                budget = budget - 1
                if typeOf(e):find("ElementCopy", 1, true) then
                    shown[#shown + 1] = e
                else
                    for i = guard(function() return e.VisualChildrenCount end) or 0, 1, -1 do
                        local child = guard(function() return e:VisualChild(i) end)
                        if child then stack[#stack + 1] = child end
                    end
                end
            end
            local taken = {}
            for i = 1, count do
                local listed = guard(function() return list[i] end)
                local content = listed and addressOf(guard(function() return listed.Content end))
                local pair
                for k, e in ipairs(shown) do
                    if not taken[k] and content and addressOf(guard(function() return e.Content end)) == content then pair = k break end
                end
                if not pair and shown[i] and not taken[i] then pair = i end
                if pair then taken[pair] = true end
                if listed then out[#out + 1] = { shown = pair and shown[pair] or listed, listed = listed } end
            end
        end
    end
    return out, holder
end

-- The text of a run of inlines, and the inlines among them that carry a tooltip of their own.
local function readInlines(list, acc, links, block)
    for i = 1, guard(function() return #list end) or 0 do
        local run = guard(function() return list[i] end)
        if run then
            local start = #acc
            local text = guard(function() return run.Text end)
            if type(text) == "string" then acc[#acc + 1] = text end
            local inner = guard(function() return run.Inlines end)
            if inner then readInlines(inner, acc, links, block) end
            if guard(function() return run.ToolTip end) ~= nil then
                links[#links + 1] = { inline = run, block = block, text = table.concat(acc, "", start + 1) }
            end
        end
    end
end

-- What a tooltip shows: its texts in tree order, each under the name of the part it lies in, and
-- its links - the inlines and elements inside it that carry a tooltip.
local function readTip(element)
    local texts, links, hints, budget = {}, {}, {}, 4000
    local into = texts
    local function walk(e, part, depth)
        if budget <= 0 then return end
        budget = budget - 1
        local visibility = guard(function() return tostring(e.Visibility) end)
        if visibility == "Collapsed" or visibility == "Hidden" then return end
        local name = guard(function() return e.Name end)
        if type(name) == "string" and name ~= "" then part = name end
        -- the key hint the game's tooltip template draws above a tooltip ("T Pin") is not its content
        if name == "uiHintContainer" then
            into = hints
            for i = 1, guard(function() return e.VisualChildrenCount end) or 0 do
                local child = guard(function() return e:VisualChild(i) end)
                if child then walk(child, part, depth + 1) end
            end
            into = texts
            return
        end
        if typeOf(e):find("TextBlock", 1, true) then
            local acc = {}
            local inlines = guard(function() return e.Inlines end)
            if inlines then readInlines(inlines, acc, links, e) end
            local text = table.concat(acc)
            if text == "" then text = guard(function() return e.Text end) or "" end
            text = tostring(text):gsub("%s+", " "):gsub("^ ", ""):gsub(" $", "")
            if text ~= "" and text ~= "[ForceUpdate]" then into[#into + 1] = { part = part, text = readable(text) } end
        elseif depth > 0 and guard(function() return e.ToolTip end) ~= nil then
            -- an element with a tooltip of its own (a cost, a property, a status icon): named by
            -- the first text it shows, which the walk below adds after it
            links[#links + 1] = { element = e, part = part, textAt = #into + 1, from = into }
        end
        for i = 1, guard(function() return e.VisualChildrenCount end) or 0 do
            local child = guard(function() return e:VisualChild(i) end)
            if child then walk(child, part, depth + 1) end
        end
    end
    walk(element, nil, 0)
    for _, link in ipairs(links) do
        if link.element then
            local row = link.from[link.textAt]
            link.text = row and row.text or link.part
            link.from = nil
        end
    end
    local hint = {}
    for i, row in ipairs(hints) do hint[i] = row.text end
    return texts, links, #hint > 0 and table.concat(hint, " ") or nil
end

-- What a tooltip is about: the view model it was given (an action, an item, an ability ...).
local function aboutOf(content, detail)
    if type(content) ~= "userdata" then
        if content == nil then return nil end
        return { text = readable(tostring(content)) }
    end
    return { type = typeOf(content), of = dataText(content), id = dataId(content),
             spell = guard(UI.ActionOf, content), values = detail and dataScalars(content) or nil }
end

-- The open tooltips in order: the pinned ones as the game lists them, then what a hover holds.
-- Each entry keeps its element and the elements of its links; stripTips takes them off.
local function tipChain(detail)
    local chain, numberOf = {}, {}
    local pinned, holder = pinnedTips()
    local function add(element, isPinned, listed)
        local n = #chain + 1
        local texts, links, hint = readTip(element)
        local entry = { n = n, handle = "tip#" .. n, pinned = isPinned, type = typeOf(element), element = element,
                        about = aboutOf(guard(function() return element.Content end), detail), hint = hint,
                        title = texts[1] and texts[1].text or nil, texts = texts, links = {}, linkRefs = links, listed = listed }
        local data = guard(function() return element.Data end)
        if type(data) == "userdata" then entry.data = typeOf(data) end
        local context = guard(function() return element:DependencyProperties().Context end)
        if type(context) == "string" and context ~= "" then entry.context = context end
        for k, link in ipairs(links) do
            local carrier = link.inline or link.element
            local described = { handle = entry.handle .. "/link#" .. k, text = readable(link.text) }
            local tag = guard(function() return carrier:DependencyProperties().TagTooltip end)
            if type(tag) == "string" and tag ~= "" then described.tag = tag end
            local linked = guard(function() return carrier.DataContext end)
            if type(linked) == "userdata" then
                described.data = typeOf(linked)
                described.of = dataText(linked)
            end
            entry.links[k] = described
        end
        chain[n] = entry
        local address = addressOf(element)
        if address then numberOf[address] = n end
    end
    for _, copy in ipairs(pinned) do add(copy.shown, true, copy.listed) end
    for _, tip in ipairs(hoverTips()) do add(tip, false) end
    -- the parent: the tooltip whose tree holds the element this one was called from
    for _, entry in ipairs(chain) do
        local element = entry.listed or entry.element
        local target = guard(function() return element.PlacementTarget end) or guard(function() return element.InlineParent end)
        local first, steps = target, 0
        while target and steps < 80 do
            local above = numberOf[addressOf(target) or ""]
            if above and above ~= entry.n then entry.parent = "tip#" .. above break end
            if typeOf(target) == "ls.UIWidget" then
                entry.owner = { widget = guard(function() return target.Name end),
                                element = typeOf(first) .. "#" .. tostring(guard(function() return first.Name end) or "") }
                break
            end
            target, steps = guard(function() return target.VisualParent end), steps + 1
        end
    end
    return chain, holder
end

local function stripTips(chain)
    local out = {}
    for i, entry in ipairs(chain) do
        local copy = {}
        for key, value in pairs(entry) do
            if key ~= "element" and key ~= "linkRefs" and key ~= "listed" then copy[key] = value end
        end
        out[i] = copy
    end
    return out
end

--- The open tooltips as a chain: { ok, tips = { { n, handle ("tip#1"), pinned, type, about = { type,
--- of, id, spell, values (options.detail) } (the view model the tooltip was given), data (the type
--- of the tooltip's own data), context, hint (the key hint above it), title, texts = { { part,
--- text } }, links = { { handle ("tip#1/link#2"), text, tag, data, of } }, parent (the handle of
--- the tooltip it was called from) or owner = { widget, element } (where in a window it was called
--- from) } } }. Pinned ones come first, in the game's order; what a hover holds open comes last.
function UI.Tips(options)
    return { ok = true, tips = stripTips((tipChain(type(options) == "table" and options.detail))) }
end

-- The element that carries the tooltip of a control: the control, else the first element inside
-- it that has one (a hotbar slot's tooltip sits on a grid inside the slot's button), else the
-- nearest above it.
local function carrierOf(element)
    if guard(function() return element.ToolTip end) ~= nil then return element end
    local queue, head = { element }, 1
    while queue[head] and head < 600 do
        local e = queue[head]
        head = head + 1
        if head > 2 and guard(function() return e.ToolTip end) ~= nil then return e end
        for i = 1, guard(function() return e.VisualChildrenCount end) or 0 do
            local child = guard(function() return e:VisualChild(i) end)
            if child then queue[#queue + 1] = child end
        end
    end
    local above, steps = guard(function() return element.VisualParent end), 0
    while above and steps < 6 and typeOf(above) ~= "ls.UIWidget" do
        if guard(function() return above.ToolTip end) ~= nil then return above end
        above, steps = guard(function() return above.VisualParent end), steps + 1
    end
    return nil
end

--- Calls up a tooltip as a hover does, with no pointer: GK.Gui.Tip(window, control) for a control
--- of an open widget (a handle of GK.Gui.Look or an x:Name), GK.Gui.Tip("tip#1/link#2") for a link
--- inside an open tooltip. The tooltip's own IsOpen is set, which is what the UI library sets when
--- its hover delay has passed; another tooltip a hover held open is closed first, as it is when
--- the pointer moves on. Answers { ok, tip = <the entry of GK.Gui.Tips>, open = how many stand }
--- when the tooltip stands open with its content, ok = false and what was seen otherwise.
function UI.Tip(name, handle)
    local carrier, inline, block
    local tipHandle, linkNumber = tostring(name):match("^(tip#%d+)/link#(%d+)$")
    if tipHandle then
        local link
        for _, entry in ipairs((tipChain())) do
            if entry.handle == tipHandle then link = entry.linkRefs[tonumber(linkNumber)] end
        end
        if not link then return { ok = false, error = "no link " .. tostring(name) .. " (GK.Gui.Tips lists the open tooltips and their links)" } end
        inline, block, carrier = link.inline, link.block, link.inline or link.element
    else
        local row, problem = UI.Handle(name, handle)
        local element = row and row.element
        -- an item of a list ("list#3") is the element the list shows for it
        if row and type(problem) == "number" then element, problem = row.containers and row.containers[problem], nil end
        if not element then
            local widget = UI.Widget(name)
            element = widget and findElement(widget, handle)
        end
        if not element then return { ok = false, error = problem or ("no control " .. tostring(handle) .. " in " .. tostring(name)) } end
        carrier = carrierOf(element)
        if not carrier then return { ok = false, error = tostring(handle) .. " carries no tooltip" } end
    end
    local tip = guard(function() return carrier.ToolTip end)
    if type(tip) ~= "userdata" then
        return { ok = true, did = "read", tip = { about = { text = readable(tostring(tip)) }, title = readable(tostring(tip)) },
                 saw = "the tooltip is a plain text and opens no window of its own here" }
    end
    local wanted = addressOf(tip)
    for _, open in ipairs(hoverTips()) do
        if addressOf(open) ~= wanted then guard(function() open.IsOpen = false end) end
    end
    if inline then
        guard(function() tip.Inline = inline end)
        guard(function() tip.InlineParent = block end)
    end
    local _, failed = guard(function() tip.IsOpen = true end)
    if failed then return { ok = false, error = "the tooltip did not open: " .. failed } end
    -- the tooltip builds its content over a few frames: it is answered when two looks read the same
    local texts = -1
    return GK.Observe({ ok = true, did = "opened" }, function(ticks, final)
        local chain = tipChain()
        for _, entry in ipairs(chain) do
            if not entry.pinned and addressOf(entry.element) == wanted and (entry.title or entry.about) then
                if #entry.texts ~= texts and not final then texts = #entry.texts return nil end
                return { ok = true, did = "opened", tip = stripTips(chain)[entry.n], open = #chain }
            end
        end
        if not final then return nil end
        return { ok = false, did = "set the tooltip's IsOpen", saw = "no tooltip stands open",
                 hint = "the control's tooltip may be switched off (ToolTipService.IsEnabled) or have nothing to show" }
    end, 1500)
end

-- The key the profile binds to an input event, from the input scheme's table of raw inputs (each
-- raw input lists the events bound to it): the scan code's name and its modifier, or nil and why.
local function keyOfEvent(eventName)
    local manager = guard(function() return Ext.Input.GetInputManager() end)
    if not manager then return nil, "the input manager is not there" end
    local id
    for _, definition in pairs(manager.InputDefinitions) do
        if tostring(definition.EventName) == eventName then id = definition.EventID break end
    end
    if not id then return nil, "the game has no input event " .. eventName end
    for _, mapping in ipairs(manager.InputScheme.RawToBinding) do
        local plain = guard(function() return Ext.Types.Serialize(mapping) end)
        for _, bound in ipairs(plain and plain.Bindings or {}) do
            local binding = bound.Binding
            if bound.InputIndex == id and binding and binding.DeviceId == 0 then
                local code = GK.ScanCode(tostring(binding.InputId))
                if code then return code, binding.Modifiers and binding.Modifiers[1] and tostring(binding.Modifiers[1]) or nil end
            end
        end
    end
    return nil, "no key is bound to " .. eventName .. " in the profile"
end

local function pinnedCount(chain)
    local n = 0
    for _, entry in ipairs(chain) do if entry.pinned then n = n + 1 end end
    return n
end

--- Pins the tooltip a hover holds open, as the player's key does: the key the profile binds to the
--- game's input event UIPinTooltip, pressed in the game's own input. Answers { ok, pinned = <the
--- entry>, open } when the game's list of pinned tooltips grew and the pinned one shows its texts,
--- ok = false and what was seen otherwise (nothing open, or a tooltip the game does not pin).
function UI.PinTip()
    local before = tipChain()
    local open
    for _, entry in ipairs(before) do if not entry.pinned then open = entry end end
    if not open then return { ok = false, did = "nothing", error = "no tooltip is open: GK.Gui.Tip(window, control) calls one up" } end
    local key, modifier = keyOfEvent("UIPinTooltip")
    if not key then return { ok = false, did = "nothing", error = modifier } end
    local had = pinnedCount(before)
    local pressed, problem = pcall(GK.Key, key, modifier)
    if not pressed then return { ok = false, did = "nothing", error = "the key was not pressed: " .. tostring(problem) } end
    local did = "pressed " .. key .. (modifier and ("+" .. modifier) or "")
    -- The pinned copy is drawn a few frames after the list grew: it is answered when it shows its
    -- texts. No pointer leaves the tooltip that was pinned, so it is closed here as a pointer
    -- moving off would close it.
    local closed = false
    return GK.Observe({ ok = true, did = did }, function(ticks, final)
        local chain = tipChain()
        if pinnedCount(chain) > had then
            if not closed then
                closed = true
                for _, tip in ipairs(hoverTips()) do guard(function() tip.IsOpen = false end) end
                if not final then return nil end
            end
            local last
            for _, entry in ipairs(chain) do if entry.pinned then last = entry end end
            if last and #last.texts == 0 and not final then return nil end
            for _, entry in ipairs(chain) do if not entry.pinned and not final then return nil end end
            return { ok = true, did = did, pinned = stripTips(chain)[last.n], open = #chain }
        end
        if not final then return nil end
        return { ok = false, did = did, saw = "nothing was pinned", tips = stripTips(chain),
                 hint = "the game pins only a tooltip that says it can be (CanBePinned)" }
    end, 1500)
end

--- Closes tooltips: GK.Gui.CloseTips() every one - what a hover holds open is closed as when the
--- pointer leaves, the pinned ones by the command the game runs on a click beside them or on the
--- menu key (RequestClearPinnedTooltipsCommand); GK.Gui.CloseTips("tip#3") the one a hover holds
--- open. One pinned tooltip among several is not closed apart: the game takes the ones pinned
--- after a pinned tooltip away on a click on it, by a parameter its panel makes in code. Answers
--- { ok, closed, tips } when they are gone.
function UI.CloseTips(which)
    local chain, holder = tipChain()
    local had = #chain
    if had == 0 then return { ok = true, did = "nothing", already = true, tips = {} } end
    local wanted
    if which ~= nil then
        for _, entry in ipairs(chain) do if entry.handle == which then wanted = entry end end
        if not wanted then return { ok = false, did = "nothing", error = "no tooltip " .. tostring(which) .. " (GK.Gui.Tips lists them)" } end
        if wanted.pinned and pinnedCount(chain) > 1 then
            return { ok = false, did = "nothing", error = "a pinned tooltip is not closed apart from the others: GK.Gui.CloseTips() closes all",
                     hint = "the game removes the tooltips pinned after one on a click on it (ls.LSElementPanel.ClickCommand, RemovePinnedTooltipCommand with a parameter made in code)" }
        end
        if wanted.pinned then wanted = nil end
    end
    local did = {}
    for _, entry in ipairs(chain) do
        if not entry.pinned and (not wanted or wanted == entry) then
            guard(function() entry.element.IsOpen = false end)
            did[#did + 1] = "closed " .. entry.handle
        end
    end
    if holder and not wanted then
        local command = guard(function() return holder.RequestClearPinnedTooltipsCommand end)
        if not command then return { ok = false, did = table.concat(did, "; "), error = "the widget of the pinned tooltips has no command to close them" } end
        local _, failed = guard(function() command:Execute(nil) end)
        if failed then return { ok = false, did = table.concat(did, "; "), error = failed } end
        did[#did + 1] = "ran RequestClearPinnedTooltipsCommand"
    end
    local expected = wanted and had - 1 or 0
    local result = { ok = true, did = table.concat(did, "; ") }
    return GK.Observe(result, function(ticks, final)
        local now = tipChain()
        if #now <= expected then
            return { ok = true, did = result.did, closed = had - #now, tips = stripTips(now) }
        end
        if not final then return nil end
        return { ok = false, did = result.did, saw = (#now) .. " tooltips still stand", tips = stripTips(now) }
    end, 1500)
end

--- The named windows and whether each is open.
function UI.List()
    local out = {}
    for name, window in pairs(W) do
        out[#out + 1] = { name = name, widget = window.widget, page = window.page, about = window.about,
                          needs = window.needs, open = UI.IsOpen(name) }
    end
    table.sort(out, function(a, b) return a.name < b.name end)
    return out
end

return true
