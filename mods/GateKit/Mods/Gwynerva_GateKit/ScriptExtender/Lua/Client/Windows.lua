-- GK.Gui: every window of the game (GK.UI is the overlay's own panels, Client/Panels.lua), operated from code - opened, read, clicked through, closed -
-- without the mouse and without window focus. UI side (`ui` in the stand: the code runs inside
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
--   GK.Gui.Run(name, command, param)   runs a command of the widget's data (strings are boxed)
--   GK.Gui.Read(name, options)         the widget's data as plain values (see GK.Gui.Snapshot)
--   GK.Gui.Item(text) / GK.Gui.ItemDo(action, text)   an item by name; equip, unequip, use, drop
--   GK.Gui.Windows                     the registry of named windows - add your own entries
--
-- Waiting is the caller's job (a widget appears a frame or more after the event): poll
-- GK.Gui.IsOpen, or use stand/ui.py from Python, which waits for you.

GK.Gui = GK.Gui or {}
local UI = GK.Gui
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

-- The open widgets, top-most last.
local function widgets()
    local root = contentRoot()
    local out = {}
    if not root then return out end
    local count = guard(function() return root.VisualChildrenCount end) or 0
    for i = 1, count do
        local element = guard(function() return root:VisualChild(i) end)
        if element and typeOf(element) == "ls.UIWidget" then
            local name = guard(function() return element.Name end)
            if name == nil or name == "" then
                -- a page without x:Name (the first-launch screens: HDR calibration ...): named by
                -- its data type, "gui::DCHDRCalibration" -> "HDRCalibration" (the page's file)
                local data = typeOf(guard(function() return element.DataContext end))
                local short = data:match("::DC(%w+)$") or data:match("%.DC(%w+)$")
                if short and catalog.widgets[short] then name = short end
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
            local command = resolve(entry.command, control, widget)
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
                row.enabled = guard(function() return element.IsEnabled end)
                -- what the control stands for: the type of its data ("ls.VMSpellSelector" ...)
                local context = guard(function() return element.DataContext end)
                row.data = context and typeOf(context) or nil
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
                local lower = function(value) return tostring(value or ""):lower() end
                local fits = (not filter.data or lower(row.data):find(lower(filter.data), 1, true))
                    and (not filter.name or lower(row.name):find(lower(filter.name), 1, true))
                    and (not filter.text or lower(row.text):find(lower(filter.text), 1, true))
                    and (not filter.any or lower(row.name):find(lower(filter.any), 1, true)
                         or lower(row.text):find(lower(filter.any), 1, true))
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
    return { ok = true, widget = widgetName(name), controls = out }
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
            if r.name == what then exact[#exact + 1] = r end
        end
        row = exact[which or 1] or (#exact == 0 and rows[which or 1]) or nil
    end
    if not row then return { ok = false, error = "no control " .. tostring(what) .. " in " .. tostring(name) } end
    if row.enabled == false then return { ok = false, error = "disabled: " .. tostring(row.name or row.text) } end
    local widget = UI.Widget(name)
    local entry = catalogControl(entryOf(widgetName(name)), row.name)
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
    return { ok = true, control = row.name or row.text, steps = log, delayed = delayed > 0 and delayed or nil }
end

--- Selects an item of a list (ListBox, tab strip ...) of an open widget, as a click on the item
--- would: `list` is the list's x:Name, `item` its number (1-based) or part of its text.
function UI.Select(name, list, item)
    local widget = UI.Widget(name)
    if not widget then return { ok = false, error = "not open: " .. tostring(name) } end
    local box = findElement(widget, list)
    if not box then return { ok = false, error = "no list " .. tostring(list) .. " in " .. tostring(name) } end
    local items = guard(function() return box.Items end) or guard(function() return box.ItemsSource end)
    local count = items and guard(function() return #items end) or 0
    local index = type(item) == "number" and item or nil
    if not index and type(item) == "string" then
        for i = 1, count do
            local value = guard(function() return items[i] end)
            local text = value and (guard(function() return value.Name end) or guard(function() return value.Title end)) or nil
            if type(text) == "string" and text:lower():find(item:lower(), 1, true) then index = i break end
        end
    end
    if not index or index < 1 or index > count then
        return { ok = false, error = "no item " .. tostring(item) .. " (the list has " .. count .. ")" }
    end
    local _, problem = guard(function() box.SelectedIndex = index - 1 end)
    if problem then return { ok = false, error = problem } end
    return { ok = true, selected = index, of = count }
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
            UI.LastDelayed = {}
            runChain(input, control, widget, log, widgetName(name), input.name)
            return { ok = true, steps = log }
        end
    end
    return { ok = false, error = tostring(name) .. " has no input binding for " .. tostring(event) }
end

--- Runs a command of a widget's data by path ("FinishLevelUp", "CurrentPlayer.SomeCommand");
--- a string, number or boolean parameter is boxed for the game.
function UI.Run(name, path, parameter)
    local data = UI.Data(name)
    if not data then return { ok = false, error = "not open: " .. tostring(name) } end
    local command = follow(data, path)
    if command == nil then return { ok = false, error = "no command " .. tostring(path) } end
    local kind = type(parameter)
    if kind == "string" or kind == "number" or kind == "boolean" then parameter = Ext.UI.Box(parameter) end
    local _, problem = guard(function() command:Execute(parameter) end)
    if problem then return { ok = false, error = problem } end
    return { ok = true }
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
-- GK.Gui.Step(window, options) runs it once; GK.Gui.Auto(window, options) repeats it in the game
-- (every options.every ms, default 400) until the window says done, closes, or options.limit
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
                        local run = ui.Run("CharacterLevelUp", "TogglePassive", passive)
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
                if allowed and (not options.feat or name:lower():find(options.feat:lower(), 1, true)) then pick = feat break end
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
                local run = ui.Run("CharacterLevelUp", "SelectAbility", best)
                return { did = "ability " .. tostring(guard(function() return best.Ability end)), ok = run.ok, error = run.error }
            end
        end
    end
    return { did = "nothing", stuck = true, error = "level-up not complete and no missing choice found" }
end

function UI.Step(name, options)
    local window = W[name]
    if not window or not window.step then return { ok = false, error = "no step for " .. tostring(name) } end
    if not UI.IsOpen(window.widget) then return { ok = false, done = true, error = "not open: " .. tostring(name) } end
    local result, problem = guard(window.step, UI, options or {})
    if not result then return { ok = false, error = problem } end
    if result.ok == nil then result.ok = not result.error end
    return result
end

UI.AutoState = UI.AutoState or {}

function UI.Auto(name, options)
    options = options or {}
    local state = { running = true, steps = 0, log = {}, started = Ext.Utils.MonotonicTime() }
    UI.AutoState[name] = state
    local limit, every = options.limit or 40, options.every or 400
    local function tick()
        Ext.UI.Defer(function()
            if UI.AutoState[name] ~= state or not state.running then return end
            local result = UI.Step(name, options)
            state.steps = state.steps + 1
            state.log[#state.log + 1] = tostring(result.did or result.error)
            if result.done or result.stuck or state.steps >= limit then
                state.running = false
                state.result = result
                return
            end
            Ext.Timer.WaitFor(result.wait or every, tick)
        end)
    end
    tick()
    return { ok = true, started = true }
end

-- Items: what the player does to an item in the inventory, from code. The view model of an item
-- is found by (part of) its name in the selected character's inventory or equipment, and the
-- character panel's own command runs with it - the same command a click or a drag runs.

local function itemVMs(data)
    local out = {}
    local player = guard(function() return data.CurrentPlayer end)
    local character = player and guard(function() return player.SelectedCharacter end)
    local inventory = character and guard(function() return character.Inventory end)
    local slots = inventory and guard(function() return inventory.Slots end)
    for i = 1, count(slots) do
        local object = guard(function() return item(slots, i).Object end)
        if object then out[#out + 1] = object end
    end
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
    local wanted = tostring(text):lower()
    for _, object in ipairs(itemVMs(data)) do
        local name = readable(guard(function() return object.Name end)) or ""
        if name:lower():find(wanted, 1, true) then return object, name end
    end
end

-- Checked 2026-11-13 on the tutorial ship: EquipItemCommand does nothing when run (the game's
-- XAML never binds it); equipping is what a double-click does - UseGameObject on the item.
local ITEM_COMMANDS = { equip = "UseGameObject", unequip = "UnequipItemCommand", use = "UseGameObject",
                        drop = "DropGameObject" }

--- Does to an item what the player does: "equip", "unequip", "use" (drink, read, open), "drop".
--- The character panel need not be open (the HUD's data carries the same commands).
function UI.ItemDo(action, text)
    local command = ITEM_COMMANDS[action]
    if not command then return { ok = false, error = "unknown action " .. tostring(action) } end
    local object, name = UI.Item(text)
    if not object then return { ok = false, error = "no item like " .. tostring(text) } end
    local source = UI.IsOpen("CharacterPanel") and "CharacterPanel" or "HotBar"
    local result = UI.Run(source, command, object)
    result.item = name
    result.via = source .. "." .. command
    return result
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
                if not done and UI.Run(tutorial.widget, command).ok then done = true end
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
function UI.Quiet(on)
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

--- What holds the game outside play: "gameover" (the "party defeated" box), "mainmenu", or nil.
-- The screens a copy of the game shows on its first way to the main menu, and the button that
-- moves on (the EULA: the user's standing consent of 2026-11-13 covers the stand's copies).
local FIRST_LAUNCH = {
    { widget = "Eula", button = "AcceptBtn" },
    { widget = "SensitiveSettings", button = "NextButton" },
    -- the calibration screens page through their pictures with "Next"; their last button closes
    -- the widget, which is what is done here at once
    { widget = "HDRCalibration", close = true },
    { widget = "GammaCalibration", close = true },
}

function UI.Stuck()
    local box = UI.Data("Dialog_box")
    if box and guard(function() return box.IsGameOver end) then return "gameover" end
    for _, screen in ipairs(FIRST_LAUNCH) do
        if UI.Widget(screen.widget) then return "firstlaunch", screen end
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
--- (the stand's checkpoint). From the main menu: "Continue"; first-launch screens are clicked through. Returns what it did; call again until GK.Gui.Stuck()
--- is nil and the game state is Running (a load takes seconds).
function UI.Recover()
    local stuck, screen = UI.Stuck()
    if stuck == "firstlaunch" then
        local result = screen.close and UI.Close(screen.widget) or UI.Click(screen.widget, screen.button)
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
