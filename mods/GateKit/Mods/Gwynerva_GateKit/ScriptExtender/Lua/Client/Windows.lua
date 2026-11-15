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
function UI.ObserveEffect(result, name, controlName, before, within, hint)
    if not result.ok then return result end
    local lastLook = -1000
    return GK.Observe(result, function(ticks, final)
        if ticks - lastLook < 3 and not final then return nil end
        lastLook = ticks
        local after = UI.Picture(name, controlName)
        local changed = UI.Changed(before, after)
        if #changed > 0 then
            result.saw = table.concat(changed, "; ")
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
    local listing = UI.Controls(name, (type(what) == "string" or type(what) == "table") and what or nil)
    if not listing.ok then return listing end
    local rows = UI.LastControls
    local row
    if type(what) == "number" then
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
        elseif guard(function() return row.element.IsChecked end) ~= nil then
            local checked = guard(function() return row.element.IsChecked end)
            guard(function() row.element.IsChecked = not checked end)
            log[#log + 1] = "toggled"
        else
            return { ok = false, error = "nothing to run on " .. tostring(row.name or row.text) }
        end
    end
    local result = { ok = true, control = row.name or row.text, steps = log, delayed = delayed > 0 and delayed or nil,
                     chain = entry and (entry.source or "catalog") or nil }
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

--- Selects an item of a list (ListBox, tab strip ...) of an open widget, as a click on the item
--- would: `list` is the list's x:Name, `item` its number (1-based), part of its text, or the item
--- itself (a userdata read from the view model, e.g. GK.Gui.Data(w):GetProperty("SelectableRaces")[i]).
function UI.Select(name, list, item)
    local widget = UI.Widget(name)
    if not widget then return { ok = false, error = "not open: " .. tostring(name) } end
    local box = findElement(widget, list)
    if not box then return { ok = false, error = "no list " .. tostring(list) .. " in " .. tostring(name) } end
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
