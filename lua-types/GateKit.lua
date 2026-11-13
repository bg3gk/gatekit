---@meta
-- LuaLS annotations for the public API of the GateKit bridge mod (global `GK`).
-- Derived from the code of the old repo's stand/bridge/Mods/Gwynerva_GateKit (2026-11-13).
-- Format as Script Extender's ExtIdeHelpers.lua. Internal helpers are not listed: see
-- docs/contracts/lua-api.md.
--
-- Sides: "server" = the server Lua state (Osiris, world); "client" = the client Lua state;
-- "ui" = client code that must run inside Ext.UI.Defer (Noesis); "both" = loaded in both states.
-- The two states are separate: `GK.Focus` exists in both with different members.
--
-- Conventions of the API: every function takes and returns plain values (uuids, numbers, tables)
-- so results travel as JSON. `who` is a character uuid; nil means the host's character (or, in
-- GK.Act, the party member the player controls). Positions are { x, y, z } with y up; north is
-- +z, east is +x.

-- =========================================================================================
-- Shared value types

---A uuid as a string ("character", "item", "template" - the context says which).
---@alias GK.Uuid string
---A position { x, y, z }, y up, north = +z, east = +x.
---@alias GK.Pos number[]
---A compass direction from the viewpoint ("here" when closer than half a metre).
---@alias GK.Compass "N"|"NE"|"E"|"SE"|"S"|"SW"|"W"|"NW"|"here"
---Relation of a character to the party.
---@alias GK.Relation "party"|"ally"|"neutral"|"hostile"
---Which Lua state a piece of code runs in: "ui" is the client inside Ext.UI.Defer.
---@alias GK.Target "client"|"server"|"ui"
---Log level used by the console log.
---@alias GK.LogLevel "Debug"|"Info"|"Warning"|"Error"

---Result of a command that can be refused: `ok = false` plus the reason a player would see.
---@class GK.Failure
---@field ok false
---@field error string

---Answer of GK.Run.
---@class GK.RunAnswer
---@field ok boolean
---@field result any @ the chunk's return value as plain data (when ok)
---@field error string? @ the error with a traceback (when not ok)
---@field log string[] @ lines the chunk wrote with GK.Log(...)

---An entry of the event journal (GK.Note / GK.Events).
---@class GK.EventEntry
---@field n integer
---@field t integer @ Ext.Utils.MonotonicTime() in ms
---@field kind string
---@field data any

-- =========================================================================================
-- The global table

---GateKit's API table: Mods.GateKit.GK from other mods and from host commands.
---Defined by Shared/Bridge.lua on both sides; every other file adds to it.
---@class GK
---@field Act GK.Act @ server: what a player can do
---@field See GK.See @ server: the situation as a player could know it
---@field Stage GK.Stage @ server: setting up situations from code (cheats)
---@field Cheat GK.Cheat @ server: the rule-bending calls, named and recorded
---@field Conditions GK.Conditions @ server: the game's spell condition texts
---@field Focus GK.FocusServer|GK.FocusClientMembers @ markers (server) / calm screen and outlines (client)
---@field GameLog GK.GameLog @ server: the combat log and toasts as a feed
---@field Heard GK.Heard @ server: barks, narrator and dialogue lines as a log
---@field SaveState GK.SaveState @ server: the bridge's memory saved inside the game's save
---@field Speed GK.Speed @ server: game speed by situation
---@field Sound GK.Sound @ client: how loud this copy of the game is
---@field Camera GK.Camera @ client: camera control
---@field Places GK.Places @ client/ui: zone name and map markers
---@field Gui GK.Gui @ client/ui: every game window operated from code
---@field UI GK.UIPanels @ client: panels of other mods in the console overlay
---@field Overlay GK.Overlay @ client: the console key, the HUD button, and Weave's settings passed through
---@field Weave Weave @ client: another name of the Weave mod's table (Mods.GateKitWeave.Weave), set when Weave is loaded
---@field UICatalog GK.UICatalog? @ client: optional catalogue of the game's XAML widgets, loaded from Script Extender/Gwynerva_GateKit/UICatalog.lua when present
---@field Features GK.Features @ both: what this Script Extender build can do
---@field LogSinks (fun(entry: GK.LogEntry))[] @ both: functions called with every console log entry
---@field LogContext string? @ both: while set, console output is attributed to this source (e.g. "Console")
---@field LogRetracted fun() @ client: tells the console that logged lines changed (set by Console.lua)
---@field Log fun(...) @ both: only inside a chunk run by GK.Run - adds a line to its `log`
---@field ConsoleHooks GK.ConsoleHooks @ client
---@field AutoplayState table @ client: internal state of GK.Autoplay (exposed, not a contract)
GK = {}

-- =========================================================================================
-- Shared/Bridge.lua  (both sides)

---Reads the listed components of an entity into a plain table (missing ones are skipped).
---Both sides.
---@param entity EntityHandle|nil
---@param components string[] @ component names, e.g. { "Health", "Transform" }
---@return table<string, any>
function GK.Dump(entity, components) end

---Runs a Lua chunk and returns { ok, result | error, log }. `GK.Log(...)` inside the chunk adds
---lines to `log`. Used by the file channel, the in-game console and the server relay. Both sides.
---@param code string
---@return GK.RunAnswer
function GK.Run(code) end

---An answer as text for people: log lines, then the result or the error. Both sides.
---@param answer GK.RunAnswer
---@return string
function GK.Format(answer) end

---Allows or forbids the game's autosaves (the bridge forbids them at start). Both sides.
---@param allowed boolean
---@return boolean ok @ false when the switch could not be written
function GK.AutoSave(allowed) end

---Starts the file channel of the test stand for one side: polls queue_<side>.jsonl in
---"Script Extender/Gwynerva_GateKit/", runs commands, writes res_<side>_<id>.json and a
---state_<side>.json heartbeat. Called once by each Bootstrap file.
---@param side "client"|"server"
function GK.Start(side) end

-- =========================================================================================
-- Shared/Events.lua  (both sides)

---Adds an entry to the event journal (a short memory of what happened). Both sides.
---@param kind string
---@param data any
function GK.Note(kind, data) end

---Journal entries with a number above `since`.
---@param since integer? @ 0 or nil: everything kept (the last 500)
---@return { last: integer, events: GK.EventEntry[] }
function GK.Events(since) end

-- =========================================================================================
-- Shared/Complete.lua  (both sides)

---Completion for the in-game console: what could follow the dotted name at the end of `text`.
---Tables are listed by walking them; nothing is called.
---@param text string
---@return { base: string, prefix: string, matches: string[], more: integer, hint: string? }
function GK.Complete(text) end

---The longest text every match starts with (compared without case), in the first match's spelling.
---@param matches string[]
---@return string
function GK.CommonPrefix(matches) end

-- =========================================================================================
-- Shared/ConsoleLog.lua  (both sides)

---One entry of the console log.
---@class GK.LogEntry
---@field side "client"|"server"
---@field level GK.LogLevel
---@field source string @ mod name, "Console", "Script Extender" or "(global)"
---@field tag string? @ text of a leading [tag]
---@field text string
---@field t number @ ms
---@field kind "cmd"|"result"|nil

---What this Script Extender build can do beyond the stock one. Test for the feature, never for
---a version.
---@class GK.Features
---@field consoleMessages boolean @ Ext.Events.ConsoleMessage exists
---@field osirisNames boolean @ Ext.Osiris.GetFunctions exists
---@field clipboard boolean @ Ext.IMGUI.SetClipboardText exists
---@field injectText boolean @ Ext.Input.InjectText exists

---Cuts a string to at most `max` bytes without splitting a UTF-8 character. Both sides.
---@param text string
---@param max integer
---@return string
function GK.CutUtf8(text, max) end

---An error text without the part of the traceback that belongs to the GateKit itself.
---@param text string
---@return string
function GK.ShortTrace(text) end

---Adds a line to this side's console log (and calls every GK.LogSinks function). Output of
---print/_P/Ext.Utils.Print*/Ext.Log.* is caught automatically.
---@param level GK.LogLevel?
---@param source string? @ default "(global)"
---@param text any
---@param extra table? @ extra fields merged into the entry (e.g. kind, ticket)
---@return GK.LogEntry
function GK.ConsoleEmit(level, source, text, extra) end

---Sends the log lines still buffered on this side to the client console now. Server only in
---practice (BootstrapServer calls it after a console command); defined on both sides.
function GK.FlushLog() end

-- =========================================================================================
-- Server/Api.lua  (server)

---One item as { uuid, template, name }. Server.
---@param id GK.Uuid?
---@return { uuid: GK.Uuid, template: string?, name: string? }|nil
function GK.Item(id) end

---A character: identity, place, health, flags and what it wears. Server.
---@class GK.CharacterRow
---@field uuid GK.Uuid
---@field name string?
---@field template string?
---@field level string? @ region name
---@field position GK.Pos?
---@field hp integer?
---@field maxHp integer?
---@field inParty boolean
---@field inCombat boolean
---@field unsheathed boolean
---@field equipment table<string, { uuid: GK.Uuid, template: string?, name: string? }> @ by slot name ("Helmet", "Melee Main Weapon", ...)

---A character: identity, place, health, flags and what it wears. Server.
---@param who GK.Uuid? @ nil: the host's character
---@return GK.CharacterRow|nil
function GK.Character(who) end

---Every party member, as GK.Character. Server.
---@return GK.CharacterRow[]
function GK.Party() end

---Puts `count` items of a root template into a character's inventory. The items exist a moment
---later; GK.Find(template, who) returns them. Server.
---@param template string @ root template uuid
---@param count integer? @ default 1
---@param who GK.Uuid?
---@return true
function GK.Give(template, count, who) end

---Items of a root template in a character's inventory (uuids). Server.
---@param template string
---@param who GK.Uuid?
---@return GK.Uuid[]
function GK.Find(template, who) end

---Equips an item (uuid) or the first item of a root template found in the inventory. Server.
---Raises an error when no such item is in the inventory.
---@param item string @ item uuid or root template
---@param who GK.Uuid?
---@return GK.Uuid item @ the item equipped
function GK.Equip(item, who) end

---Takes an item off. Server.
---@param item GK.Uuid
---@param who GK.Uuid?
---@return true
function GK.Unequip(item, who) end

---Draws (true) or sheathes (false) the character's weapons at once. Server.
---@param drawn boolean
---@param who GK.Uuid?
---@return true
function GK.Unsheath(drawn, who) end

---Takes the character's weapons out and waits for the game to agree, rather than returning at
---once and leaving the world to catch up. Prefer this to `GK.Unsheath`. Server.
---@param who GK.Uuid?
function GK.Act.Draw(who) end

---Puts the character's weapons away and waits for the game to agree. Server.
---@param who GK.Uuid?
function GK.Act.Sheathe(who) end

---Moves a character to a position, or next to another object when `x` is a uuid. Server.
---@param x number|GK.Uuid
---@param y number?
---@param z number?
---@param who GK.Uuid?
---@return true
function GK.Teleport(x, y, z, who) end

---Creates a character or item from a root template at a position (default: beside the host).
---Server.
---@param template string
---@param x number?
---@param y number?
---@param z number?
---@return GK.Uuid
function GK.Spawn(template, x, y, z) end

---The dialogues the story has registered, optionally only those whose speaker or dialogue name
---contains `filter`. Server.
---@param filter string?
---@return { speaker: string, dialog: string }[]
function GK.Dialogs(filter) end

---Starts a dialogue between `speaker` and a player character; moves the player next to the
---speaker first. Returns whether the story accepted it; GK.Dialog() (client/ui) shows it a moment
---later. Server.
---@param dialog string
---@param speaker GK.Uuid
---@param who GK.Uuid?
---@return boolean
function GK.StartDialog(dialog, speaker, who) end

---Removes an object created with GK.Spawn. Server.
---@param id GK.Uuid
---@return true
function GK.Remove(id) end

---Records an Osiris event in the event journal (GK.Events) whenever it fires. Server.
---@param name string
---@param arity integer
---@return true
function GK.Watch(name, arity) end

-- =========================================================================================
-- Client/Api.lua  (client)

---The client in a few words: game state, input mode, who is controlled. Client.
---@return { gameState: string, controllerMode: boolean, controlled: GK.Uuid[], liveTimelines: integer, version: { extender: any, game: any } }
function GK.Client() end

---Live timelines (dialogues, cutscenes, gameplay timelines). With `tracks` = true each one also
---lists how many tracks of each kind it has. Client.
---@param tracks boolean?
---@return { id: string, phase: integer, time: number, duration: number, paused: boolean, gameplay: boolean, dialogInstance: any, actors: { actor: string, dummy: string }[], tracks: table<string, integer>? }[]
function GK.Scenes(tracks) end

---The tracks of one kind in a timeline, e.g. GK.Tracks(id, "TLShowWeapon"). Client.
---@param timelineId string
---@param kind string @ substring of the track component's type name
---@return { id: string, actor: string, phase: integer, start: number, finish: number, active: boolean }[]
function GK.Tracks(timelineId, kind) end

---Plays every dialogue and cutscene `speed` times faster (20 is a good value; 1 or nil returns to
---normal). Stays on, also for scenes that start later, until changed. Client.
---@param speed number?
---@return number speed
function GK.FastScenes(speed) end

---The SDL scan code name of a key name: GateKit's own ("escape", "enter", "space", "tab",
---"backspace", "delete", "left", "up", "right", "down", "f1".."f12", one letter or digit) or a
---scan code name already ("ESCAPE", "NUM_1", "KP_ENTER"); nil when there is no such key. Client.
---@param name string
---@return string|nil
function GK.ScanCode(name) end

---Presses a key inside the game's own input system, so it needs neither the window nor the
---operating system and works the same under Proton. The name is GateKit's ("escape", "f11",
---"i", "1") or an SDL scan code name ("ESCAPE", "NUM_1"). Client.
---@param name string
---@param modifiers string? @ an SDL key modifier, e.g. "LShift", "LCtrl"
---@return true
function GK.Key(name, modifiers) end

---Asks the game for a picture of itself: the frame the renderer is about to show, copied out
---before any window of the extender's is drawn over it, so the picture is the game alone and
---taking it neither pauses nor moves anything. `path` is below the Script Extender folder of the
---profile, as `Ext.IO.SaveFile` takes it, and the PNG appears there a few frames later. False
---when the renderer cannot do it (so far only the DX11 one) or when one is already on its way.
---Client.
---@param path string
---@return boolean
function GK.Shot(path) end

---Whether a picture asked for with `GK.Shot` has still to be taken or written. Client.
---@return boolean
function GK.ShotPending() end

--- How fast this copy is allowed to draw, and whether the engine is throttling it for being in
--- the background. Nil where GateKit's Script Extender build cannot reach the engine's caps.
--- @return { frameCap: integer, effectiveFrameCap: integer, lostFocusFrameCap: integer, microsecondsPerFrame: integer, hasFocus: boolean, yieldOnLostFocus: boolean }|nil
function GK.FrameCap() end

--- Caps this copy at `fps` frames a second whether its window is in front or not. 0 lifts the cap
--- and with it the thirty-a-second throttle the engine puts a background copy under.
--- @param fps integer
--- @return boolean
function GK.SetFrameCap(fps) end

-- =========================================================================================
-- Client/Sound.lua  (client side)

---One of the game's twelve sound settings.
---@class GK.SoundCategory
---@field key string @ the game's own ConfigKey, e.g. "MusicVolume"
---@field name string @ GateKit's short name, e.g. "music"
---@field rtpc string? @ the game's Wwise RTPC, e.g. "RTPC_Volume_Music"; absent for RumbleVolume
---@field value number? @ only in GK.Sound.List(): the volume now, 0..100
---@field muted number? @ only in GK.Sound.List(): the volume GK.Sound.Mute remembered

---One of the player's own sound settings, as the engine keeps it.
---@class GK.SoundSetting
---@field key string @ the game's own ConfigKey, e.g. "MusicVolume"
---@field name string @ GateKit's short name, e.g. "music"
---@field configured number? @ the volume the player set, as the engine read it from the profile
---@field value number? @ the volume in effect: what the engine writes to the category's RTPCs (0 while the game silences itself)
---@field default number? @ the game's built-in default for the category
---@field display string? @ the row's name in the options screen, in the game's language

---How loud this copy of the game is. Every copy mixes its own sound, so this is per game and
---needs no audio API of the operating system. Setting a volume writes the game's own Wwise RTPCs
---underneath the options screen, so the player's own settings are never touched and nothing is
---written to a profile; `GK.Sound.Settings` reads those settings instead. Client side.
---@class GK.Sound
---@field Categories GK.SoundCategory[] @ the twelve, in the options screen's order
GK.Sound = {}

---The category whose game key, GateKit name or RTPC name is `category` (case-free), or nil.
---Without a category the master is meant. Client.
---@param category string|GK.SoundCategory|nil
---@return GK.SoundCategory?
function GK.Sound.Category(category) end

---Every sound category with its current volume (0..100; absent for a category with no RTPC).
---Client.
---@return GK.SoundCategory[]
function GK.Sound.List() end

---How loud one category is, 0..100; nil for a category with no RTPC. Client.
---@param category string|nil @ a key ("MusicVolume"), a short name ("music"), an RTPC, or nothing for the master
---@return number?
function GK.Sound.Get(category) end

---Sets one category to `value` (0..100, clamped) and returns what was set. Only this copy of the
---game, and only until something else writes the same RTPC. Client.
---@param category string|nil
---@param value number
---@return number
function GK.Sound.Set(category, value) end

---The master volume: with `value` sets it, without reads it. Client.
---@param value number?
---@return number?
function GK.Sound.Master(value) end

---Silences a category, remembering how loud it was; without one the master, which silences this
---copy. Returns the volume remembered. Client.
---@param category string?
---@return number
function GK.Sound.Mute(category) end

---Puts back what `GK.Sound.Mute` took away: one category, or every muted one. Returns the keys
---restored. Client.
---@param category string?
---@return string[]
function GK.Sound.Unmute(category) end

---The categories held at 0 by `GK.Sound.Mute`, as `{ key = the volume remembered }`. A Lua reset
---empties it and the volumes stay where they were. Client.
---@return table<string, number>
function GK.Sound.Muted() end

---The game's own `MuteSoundWhenNotFocused`: with `on` sets it, without reads it. The engine acts
---on it when the window gains or loses focus, so setting it does not silence a copy that is
---already in the background until focus next changes. Client.
---@param on boolean?
---@return boolean
function GK.Sound.WhenUnfocused(on) end

---The player's own sound settings, as the engine keeps them: one row per category, in the
---options screen's order. Needs GateKit's Script Extender build (the stock one leaves
---`GlobalSwitches.SoundSettings` out of its property map); returns nil there. What
---`GK.Sound.Set` writes is not among these numbers - that goes to the RTPC under the setting.
---Client.
---@return GK.SoundSetting[]?
function GK.Sound.Settings() end

-- =========================================================================================
-- Client/UI.lua  (ui side: must run inside Ext.UI.Defer)

---The first view model of a type in the UI ("gui::VMDialogue", "gui::DCHotBar", ...), or nil. UI.
---@param wanted string
---@return any|nil
function GK.ViewModel(wanted) end

---The distinct view-model types currently in the UI: a map of what can be read right now. UI.
---@return string[]
function GK.ViewModels() end

---A view model's plain properties (strings, numbers, booleans); nested objects are named by
---type ("<Type>#count"). UI.
---@param wanted string|any @ a view-model type name or the view model itself
---@return table<string, any>|nil
function GK.Properties(wanted) end

---The dialogue on screen, or nil when no dialogue is shown. UI.
---@return { instance: any, speaker: string, text: string, showAnswers: boolean, answers: { n: integer, text: string, enabled: boolean }[] }|nil
function GK.Dialog() end

---Chooses answer number `n` (as numbered by GK.Dialog) in the dialogue on screen. Raises an error
---when there is no such dialogue or answer. UI.
---@param n integer
---@return true
function GK.Answer(n) end

---Names of the game commands available (QuickSaveCommand, SwitchWeaponSetCommand, ShortRest,
---GoToCamp, TogglePhotoModeCommand, ...). `wanted` narrows it to one view-model type. UI.
---@param wanted string?
---@return string[]|nil
function GK.Commands(wanted) end

---Executes a game command by name, with an optional parameter, e.g. GK.Command("QuickSaveCommand").
---Raises an error when the view model or command is missing. UI.
---@param name string
---@param parameter any?
---@param wanted string? @ view-model type that carries the command
---@return true
function GK.Command(name, parameter, wanted) end

-- =========================================================================================
-- Client/Stage.lua  (ui side): windows that stop a scene until the player acts

---What waits for the player on screen (empty table when nothing).
---@class GK.BlockingState
---@field roll { state: string, check: string, dc: any, result: any }?
---@field box { title: string, text: string, gameOver: boolean, buttons: string[] }?
---@field dialog true?
---@field trade true?

---What is on screen that waits for the player. UI.
---@return GK.BlockingState
function GK.Blocking() end

---Moves the dice-roll window one phase on, as the player's click does. Returns the phase it was
---in, or nil when no roll is shown. UI.
---@return string|nil
function GK.RollNext() end

---Presses button `n` of the message box on screen (as GK.Blocking().box.buttons lists them).
---Raises an error when there is no box. UI.
---@param n integer
---@return true
function GK.BoxPress(n) end

---One step of getting rid of whatever blocks: advances a roll, closes a trade window, accepts an
---ordinary message box. A "game over" box is reported, never pressed. UI.
---@return string|nil did @ what it did, nil when nothing blocks
function GK.Unblock() end

---Turns autoplay on or off: the client reacts in the tick something appears (rolls rolled
---through, boxes accepted, dialogue answers chosen). UI.
---@param on boolean?
---@param options { answers: boolean?, pick: "first"|"second"|"third"|"last"? }?
---@return GK.AutoplayInfo
function GK.Autoplay(on, options) end

---@class GK.AutoplayInfo
---@field on boolean
---@field acted string[] @ the last 20 actions
---@field busy boolean @ something that waits for the player is on screen
---@field blocking GK.BlockingState

---Autoplay state. UI.
---@return GK.AutoplayInfo
function GK.AutoplayInfo() end

---A control found on screen.
---@class GK.ControlRow
---@field n integer
---@field name string?
---@field kind string
---@field text string?
---@field enabled boolean?
---@field checked boolean?
---@field selected boolean?
---@field context string?
---@field command string?
---@field pressed "command"|"selected"|"toggled"|nil @ set on the row GK.Press returns
---@field fitting integer? @ set on the row GK.Press returns

---The controls on screen. `filter`: part of a name or text, or a table. UI.
---@param filter string|{ name: string?, text: string?, kind: string?, any: string?, hidden: boolean?, limit: integer? }|nil
---@return GK.ControlRow[]
function GK.Controls(filter) end

---Presses a control: by name, by part of its text, or by { name, text, n }. A toggle or radio
---button without a command is switched instead. Raises an error when nothing fits. UI.
---@param what string|{ name: string?, text: string?, kind: string?, n: integer? }
---@return GK.ControlRow
function GK.Press(what) end

-- =========================================================================================
-- Client/Camera.lua  (client)

---The player's camera from code. Degrees: 0 = north = +z, 90 = east = +x.
---@class GK.Camera
---@field Offset number @ 45: the camera looks towards RotationY - Offset
---@field Speed number @ degrees a second (180)
---@field MaxSeconds number @ longest turn (1)
---@field MinTurn number @ turns smaller than this are skipped (25)
GK.Camera = {}

---@return { rotation: number, looking: number, pitch: number, distance: number, target: GK.Pos }|{ error: string }
function GK.Camera.State() end

---Turns the camera to look that way (the game eases into it). Client.
---@param degrees number
---@param options { now: boolean?, min: number? }?
---@return { ok: true, from: number?, to: number?, seconds: number?, rotation: number?, skipped: string? }|{ error: string }
function GK.Camera.Look(degrees, options) end

---Turns the camera to look from the character it follows towards `to`. Client.
---@param to GK.Pos|GK.Uuid
---@return table
function GK.Camera.Face(to) end

-- =========================================================================================
-- Client/Places.lua  (ui side)

---Where the party is and where the journal points. UI side.
---@class GK.Places
GK.Places = {}

---The zone name the HUD shows ("" when the place has none).
---@return string
function GK.Places.Zone() end

---Every marker the map has (quest objectives, traders, waypoints).
---@return { name: string?, quest: string?, pos: GK.Pos?, radius: number? }[]
function GK.Places.Markers() end

-- =========================================================================================
-- Focus (two halves)

---Server half of GK.Focus: shows whoever watches where the agent is looking, with the game's own
---effects (move, ground, target, object) or the violet outline. One marker at a time.
---@class GK.FocusServer
---@field Enabled boolean
---@field MaxSeconds number @ a marker never outlives this (30)
---@field GlanceSeconds number @ 2.5
---@field Effects table<string, string> @ effect resource uuids by kind (move, ground, target, object)
---@field ForKind table<string, string> @ GK.Act job kind -> marker kind
---@field Outlines boolean @ outline instead of target/object rings
---@field Generation integer
---@field Current any
GK.Focus = {}

---Takes the current marker off the screen. Server.
function GK.Focus.Clear() end

---Marks `aim` with a marker of `kind`. Server.
---@param kind "move"|"ground"|"target"|"object"|"walk"|"aim"
---@param aim GK.Uuid|GK.Pos
---@param options { seconds: number?, job: integer? }? @ job: the GK.Act job the marker belongs to
function GK.Focus.Show(kind, aim, options) end

---A glance at `aim` (what the agent examines or weighs): a ring for GlanceSeconds. Does not
---replace the marker of a running action. Server.
---@param aim GK.Uuid|GK.Pos
function GK.Focus.Glance(aim) end

---Markers on (true) or off (false). Server.
---@param on boolean
---@return { enabled: boolean }
function GK.Focus.SetEnabled(on) end

---@return { enabled: boolean, current: any }
function GK.Focus.State() end

---Called by GK.Act when a job starts: marks what the job aims at. Server. (Called by GK.Act, not
---by users.)
---@param job table
function GK.Focus.JobStarted(job) end

---Called by GK.Act when a job ends: its marker goes. Server.
---@param job table
function GK.Focus.JobFinished(job) end

---Client half of GK.Focus (same global name, separate Lua state): a calm screen while an agent
---plays, and the agent's pulsing outline. Members below exist on the client only.
---@class GK.FocusClientMembers
---@field HumanPause number @ seconds the calm waits after a real mouse movement (10)
---@field Calm boolean
---@field OutlineColor number[] @ { r, g, b } violet
---@field OutlineDim number
---@field OutlinePulse number
---@field OutlinePlayer integer
---@field Outlined table<GK.Uuid, true>
---@field PulseTick fun()

---Client: calm mode on (true) or off (false); returns the state. Off gives the pointer back at once.
---@param on boolean
---@return { calm: boolean, yieldedToHuman: boolean, outlined: GK.Uuid[], picking: boolean? }
function GK.Focus.SetCalm(on) end

---Client: the agent's pulsing violet outline on a character or an item (on = true) or off.
---@param id GK.Uuid
---@param on boolean
---@return boolean
function GK.Focus.Outline(id, on) end

---Client: takes every outline of ours away.
---@return true
function GK.Focus.ClearOutlines() end

-- NOTE: GK.Focus.State() on the client returns { calm, yieldedToHuman, outlined, picking }
-- (see SetCalm); on the server { enabled, current }. LuaLS holds one definition per field, so the
-- annotation above is the server's.

-- =========================================================================================
-- Server/Speed.lua  (server)

---Game speed by situation. The engine's time scale (GlobalSwitches DeltaTimeScale) follows what
---is going on. Server (the host decides). A load or Lua reset turns it off.
---@class GK.Speed
---@field Profile GK.SpeedProfile
---@field Status table @ internal state table (exposed, not a contract)
GK.Speed = {}

---Scales per situation.
---@class GK.SpeedProfile
---@field exploration number @ walking around (1)
---@field dialogue number @ voiced lines are real time (1)
---@field ownTurn number @ a party member's turn (1)
---@field allyTurn number @ an allied NPC's turn (2)
---@field enemyTurn number @ an enemy's turn (3)
---@field combat number @ in combat, between turns (1)

---The engine's current time scale. Server.
---@return number|nil
function GK.Speed.Get() end

---Starts following the situation; `profile` overrides scales of GK.Speed.Profile. Server.
---@param profile GK.SpeedProfile|table?
---@return GK.SpeedState
function GK.Speed.Enable(profile) end

---Back to the game's pace (1) and stops following. Server.
---@return GK.SpeedState
function GK.Speed.Disable() end

---A fixed scale (stops following), clamped to 0.05..10. Server.
---@param scale number
---@return GK.SpeedState
function GK.Speed.Set(scale) end

---A temporary scale over whatever the situation says. Server.
---@param scale number
---@param seconds number? @ default 5
---@return GK.SpeedState
function GK.Speed.Boost(scale, seconds) end

---A situation of your own: the highest priority whose test passes wins; test = nil removes it. Server.
---@param name string
---@param priority number?
---@param test (fun(state: table): boolean)?
---@param scale number?
---@return true
function GK.Speed.Rule(name, priority, test, scale) end

---@class GK.SpeedState
---@field enabled boolean
---@field situation string
---@field scale number
---@field engine number|nil
---@field active GK.Uuid? @ character whose turn it is
---@field dialogs integer
---@field profile GK.SpeedProfile

---@return GK.SpeedState
function GK.Speed.State() end

-- =========================================================================================
-- Server/Heard.lua  (server)

---What was said around the party: barks, narrator lines, dialogue lines. Capture runs from the
---bridge's start. Server.
---@class GK.Heard
---@field Lines table<integer, GK.HeardLine>
---@field Next integer
---@field Keep integer @ 300
---@field Narrator integer @ -666: the narrator's speaker index
GK.Heard = {}

---@class GK.HeardLine
---@field n integer
---@field t number
---@field speaker string?
---@field text string
---@field dialog string?
---@field automated boolean?
---@field d number? @ distance from the party
---@field heard boolean

---Lines numbered above `n`. Server.
---@param n integer?
---@param options { all: boolean? }? @ all: also lines spoken out of the party's hearing
---@return { last: integer, lines: GK.HeardLine[] }
function GK.Heard.Since(n, options) end

---Polls the dialogue manager once (done every 150 ms by the capture). Server.
function GK.Heard.Poll() end

---Starts the capture (runs from the bridge's start). Server.
---@return true
function GK.Heard.Start() end

---Pauses the capture. Server.
---@return true
function GK.Heard.Stop() end

-- =========================================================================================
-- Server/GameLog.lua  (server)

---What happened, as a player would have noticed: damage, healing, statuses, downs, deaths,
---casts, fights, turns, items, gold, quests, rolls, approval, level-ups. Server.
---@class GK.GameLog
---@field Entries table<integer, GK.GameLogEntry>
---@field Next integer
---@field Keep integer @ 500
---@field Hp table<GK.Uuid, number>
---@field Kinds string[] @ damage, heal, status, unstatus, down, death, revive, cast, fight, turn, item, lost, gold, quest, roll, approval, level
---@field On table<string, fun(...)> @ Osiris event handlers by event (looked up at fire time)
---@field Registered table<string, boolean>
GK.GameLog = {}

---@class GK.GameLogEntry
---@field n integer
---@field t number
---@field kind string
---@field text string
---@field fair boolean @ a party member is involved or the party perceives it
---@field who string?
---@field item string?

---Entries after number `n`. Server.
---@param n integer?
---@param options { all: boolean?, kinds: table<string, boolean>? }? @ all: also what the party did not perceive
---@return { last: integer, entries: GK.GameLogEntry[] }
function GK.GameLog.Since(n, options) end

---Quest title lookup used by the log (internal-ish, exposed).
GK.GameLog.QuestTitle = nil

-- =========================================================================================
-- Server/SaveState.lua  (server)

---The bridge's own memory travels inside the game's save (PersistentVars of the bridge mod,
---key "GKState"): GK.See.Memory, GK.GameLog, GK.Heard, GK.Act.Narration, GK.Cheat.Log, Focus.Enabled.
---Written on the "Save" game state, restored on SessionLoaded. Server.
---@class GK.SaveState
---@field Key string @ "GKState"
---@field Version integer
---@field Written table?
---@field Restored table?
GK.SaveState = {}

---The tables as they are now (what a save would carry). Server.
---@return table
function GK.SaveState.Snapshot() end

---Writes them into PersistentVars now. Server.
---@return integer|nil bytes
function GK.SaveState.Write() end

---Puts back what the loaded save carried. Server.
---@return table
function GK.SaveState.Restore() end

---What was written and restored last. Server.
---@return { written: table?, restored: table? }
function GK.SaveState.Info() end

-- =========================================================================================
-- Server/Conditions.lua  (server)

---The game's spell condition texts, evaluated before a cast. Three-valued: ok = true | false |
---nil (nil: the game decides). Server.
---@class GK.Conditions
---@field Functions table<string, fun(ctx: table, args: table): boolean|nil> @ modelled condition functions
GK.Conditions = {}

---@class GK.ConditionResult
---@field ok boolean|nil
---@field failed string? @ the parts that are false
---@field unknown string[]? @ functions not modelled
---@field text string? @ the condition text (from Check)

---Evaluates any condition text. Server.
---@param text string
---@param source GK.Uuid?
---@param target GK.Uuid?
---@param subject GK.Uuid?
---@return GK.ConditionResult
function GK.Conditions.Eval(text, source, target, subject) end

---Checks a spell's RequirementConditions and (with a target uuid) TargetConditions. Server.
---@param who GK.Uuid
---@param spell string @ stats name
---@param target GK.Uuid?
---@return GK.ConditionResult
function GK.Conditions.Check(who, spell, target) end

-- =========================================================================================
-- Server/Cheat.lua  (server)

---Everything that bends the game's rules, in one place, recorded. For setting up tests, never
---for an agent that plays. Each cheat also exists as the underlying function (GK.Stage.*, GK.Teleport,
---GK.Give...); this table only names them. Server.
---@class GK.Cheat
---@field Log { name: string, t: number }[]
---@field Teleport fun(...) @ GK.Teleport
---@field Visit fun(target: GK.Uuid, who: GK.Uuid?, party: boolean?)
---@field UseFrom fun(item: GK.Uuid, who: GK.Uuid?)
---@field Spawn fun(spec: string|GK.StageSpawnSpec): GK.Uuid[]
---@field Give fun(template: string, count: integer?, who: GK.Uuid?)
---@field Kill fun(who: GK.Uuid?)
---@field Slay fun(radius: number?, inFight: boolean?)
---@field Damage fun(amount: number, who: GK.Uuid?, kind: string?)
---@field Heal fun(who: GK.Uuid?, hp: number?)
---@field Restore fun(who: GK.Uuid?)
---@field Resurrect fun(who: GK.Uuid?)
---@field Immortal fun(on: boolean, who: GK.Uuid?)
---@field Protect fun(on: boolean)
---@field Guard fun(on: boolean)
---@field Status fun(status: string, who: GK.Uuid?, turns: integer?)
---@field Unstatus fun(status: string, who: GK.Uuid?)
---@field Teach fun(spell: string, who: GK.Uuid?)
---@field Lock fun(item: GK.Uuid, locked: boolean)
---@field Faction fun(name: string, who: GK.Uuid?)
---@field Fight fun(enemies: GK.Uuid[], who: GK.Uuid?, mortal: boolean?)
---@field EndCombat fun(who: GK.Uuid?)
---@field EndTurnOf fun(who: GK.Uuid?)
---@field Cast fun(spell: string, target: GK.Uuid|GK.Pos, who: GK.Uuid?)
---@field Talk fun(speaker: string, dialog: string?, who: GK.Uuid?)
---@field StartDialog fun(dialog: string, speaker: GK.Uuid, who: GK.Uuid?): boolean
---@field OnStage fun(id: GK.Uuid, on: boolean)
---@field Level fun(name: string)
---@field Camp fun(go: boolean?)
---@field LongRest fun()
---@field Unblock fun(): any
---@field Experience fun(amount: integer, who: GK.Uuid?)
GK.Cheat = {}

---{ name = "what it does" } for every cheat. Server.
---@return table<string, string>
function GK.Cheat.List() end

---The cheats called since the last Reset: { { name, t } }. Server.
---@return { name: string, t: number }[]
function GK.Cheat.Used() end

---Clears the record of used cheats. Server.
---@return true
function GK.Cheat.Reset() end

-- =========================================================================================
-- Server/Stage.lua  (server): putting a situation together from code (all cheats)

---@class GK.Stage
---@field Scene { created: GK.Uuid[], factions: table, statuses: table } @ what the current scene changed (for Reset)
---@field Templates table<string, string> @ short names -> root template uuids (goblin, longsword, potion, chest...)
---@field Factions table<string, string> @ "enemy" | "neutral" | "ally" -> faction uuid
---@field TriggerKinds table<string, string> @ trigger kind -> Script Extender component name
GK.Stage = {}

---Spawn specification.
---@class GK.StageSpawnSpec
---@field template string @ short name from GK.Stage.Templates, or a root template
---@field count integer? @ default 1
---@field at GK.Pos?
---@field near GK.Uuid? @ default: the host
---@field distance number? @ default 3
---@field faction "enemy"|"neutral"|"ally"|string? @ characters only
---@field equipment string[]? @ templates to put on
---@field items string[]? @ templates to put into the inventory
---@field level integer?

---The position of an object as { x, y, z }. Server.
---@param who GK.Uuid?
---@return GK.Pos
function GK.Stage.Position(who) end

---Creates characters or items. Equipment is put on a moment later: GK.Stage.Ready(uuids) tells when. Server.
---@param spec string|GK.StageSpawnSpec
---@return GK.Uuid[]
function GK.Stage.Spawn(spec) end

---Whether every one of these objects exists as an entity yet. Server.
---@param ids GK.Uuid[]
---@return boolean
function GK.Stage.Ready(ids) end

---Gives a character things and puts them on. Server.
---@param equipment string[]
---@param who GK.Uuid?
function GK.Stage.Outfit(equipment, who) end

---Changes whose side a character is on; Reset puts it back. Server.
---@param name "enemy"|"neutral"|"ally"|string
---@param who GK.Uuid?
function GK.Stage.Faction(name, who) end

---Marks the tutorials as seen for the host's user. Server.
function GK.Stage.Quiet() end

---What state the game is in, for a scene that seems stuck. Server.
---@return { gameState: string, level: string?, hostDead: boolean, inCombat: boolean, inDialog: boolean }
function GK.Stage.State() end

---The party's characters (uuids). Server.
---@return GK.Uuid[]
function GK.Stage.Party() end

---Makes the party unkillable (or mortal again). Server.
---@param on boolean
function GK.Stage.Protect(on) end

---Starts a fight between `enemies` and a character (default: the host); the party is made
---unkillable unless `mortal`. Server.
---@param enemies GK.Uuid[]
---@param who GK.Uuid?
---@param mortal boolean?
function GK.Stage.Fight(enemies, who, mortal) end

---The fight a character is in, or nil. Server.
---@param who GK.Uuid?
---@return { guid: string, round: integer, active: GK.Uuid, order: { uuid: GK.Uuid, name: string, initiative: number, team: any, player: boolean, hp: number, maxHp: number, acted: boolean, dead: boolean }[] }|nil
function GK.Stage.Combat(who) end

---Ends the turn of a character (default: whoever's turn it is in the host's fight). Server.
---@param who GK.Uuid?
function GK.Stage.EndTurn(who) end

---Ends the fight a character is in without anybody dying. Server.
---@param who GK.Uuid?
function GK.Stage.EndCombat(who) end

---Turn-based mode outside a fight. Server.
---@param on boolean
---@param who GK.Uuid?
function GK.Stage.TurnBased(on, who) end

---A character casts a spell on a target (uuid) or at a position. The character must know the spell. Server.
---@param spell string
---@param target GK.Uuid|GK.Pos
---@param who GK.Uuid?
function GK.Stage.Cast(spell, target, who) end

---Adds a spell to a character. Server.
---@param spell string
---@param who GK.Uuid?
function GK.Stage.Teach(spell, who) end

---Uses an item (uuid, or a template name of something in the inventory). Server.
---@param item string
---@param who GK.Uuid?
function GK.Stage.Use(item, who) end

---Applies a status for a number of turns (-1: forever). Server.
---@param status string
---@param who GK.Uuid?
---@param turns integer?
function GK.Stage.Status(status, who, turns) end

---@param status string
---@param who GK.Uuid?
function GK.Stage.Unstatus(status, who) end

---The statuses on a character. Server.
---@param who GK.Uuid?
---@return { id: string, turns: integer }[]
function GK.Stage.Statuses(who) end

---Deals damage of a type (default "Bludgeoning"). Server.
---@param amount number
---@param who GK.Uuid?
---@param kind string?
function GK.Stage.Damage(amount, who, kind) end

---Sets hit points (default: to the maximum). Server.
---@param who GK.Uuid?
---@param hp number?
function GK.Stage.Heal(who, hp) end

---@param who GK.Uuid?
function GK.Stage.Kill(who) end

---@param who GK.Uuid?
function GK.Stage.Resurrect(who) end

---@param on boolean
---@param who GK.Uuid?
function GK.Stage.Immortal(on, who) end

---Turns a character into the shape of another template. Server.
---@param shape string
---@param who GK.Uuid?
function GK.Stage.Transform(shape, who) end

---@param who GK.Uuid?
function GK.Stage.Untransform(who) end

---A surface on the ground: GK.Stage.Surface("SurfaceFire", 3). Server.
---@param kind string
---@param radius number?
---@param at GK.Pos?
---@param turns integer?
function GK.Stage.Surface(kind, radius, at, turns) end

---@param item GK.Uuid
---@param locked boolean
function GK.Stage.Lock(item, locked) end

---Asks for a long rest as the player would. Server.
function GK.Stage.LongRest() end

---Restores the party as a long rest does, at once. Server.
---@param who GK.Uuid?
function GK.Stage.Restore(who) end

---@param go boolean? @ false: leave the camp
function GK.Stage.Camp(go) end

---Moves the party to another level ("WLD_Main_A", "SCL_Main_A" ...). Server.
---@param name string
function GK.Stage.Level(name) end

---The level the host is in. Server.
---@return string
function GK.Stage.CurrentLevel() end

---Row of GK.Stage.Scout.
---@class GK.ScoutRow
---@field uuid GK.Uuid
---@field name string?
---@field template string?
---@field distance number
---@field x number
---@field y number
---@field z number
---@field hp number?
---@field onStage boolean
---@field enemy boolean
---@field dead boolean
---@field locked boolean

---What is on the host's level: a tally without options.kind, else the list. Kinds: "characters",
---"enemies", "offstage", "dead", "containers", "doors", "locked", "traders". Server.
---@param options { kind: string?, radius: number?, limit: integer?, name: string? }?
---@return table<string, integer>|GK.ScoutRow[]
function GK.Stage.Scout(options) end

---The trigger zones of the host's level: counts per kind, or the list for a kind. Server.
---@param kind string?
---@param options { radius: number?, limit: integer?, name: string? }?
---@return table<string, integer>|{ uuid: GK.Uuid, name: string, distance: number, x: number, y: number, z: number, enterEvent: string?, leaveEvent: string? }[]
function GK.Stage.Triggers(kind, options) end

---Items of the level that do something when used, by kind ("Door", "OpenClose", "Book", "Ladder"...). Server.
---@param kind string?
---@param options { limit: integer?, scan: integer? }?
---@return table<string, integer>|{ uuid: GK.Uuid, name: string, template: string, distance: number }[]
function GK.Stage.Usables(kind, options) end

---A character is put next to an item and uses it. Server.
---@param item GK.Uuid
---@param who GK.Uuid?
function GK.Stage.UseItem(item, who) end

---Other things done to an item: "pickup", "drop", "throw", "attack", "equip", "unequip", "lock",
---"unlock", "destroy", "move". Server.
---@param action string
---@param item GK.Uuid
---@param target GK.Uuid|GK.Pos?
---@param who GK.Uuid?
function GK.Stage.ItemAction(action, item, target, who) end

---A short fight that plays itself for `turns` turns (default 6), then ends. Server.
---@param enemies GK.Uuid[]
---@param turns integer?
---@param who GK.Uuid?
function GK.Stage.Skirmish(enemies, turns, who) end

---@return { running: boolean, turns: integer, started: boolean }
function GK.Stage.SkirmishState() end

---Picks a lock (tool defaults to the character's thieves' tools). Server.
---@param item GK.Uuid
---@param who GK.Uuid?
---@param tool GK.Uuid?
function GK.Stage.PickLock(item, who, tool) end

---Disarms a trap (armed, discovered, disarmable). Server.
---@param trap GK.Uuid
---@param who GK.Uuid?
---@param tool GK.Uuid?
function GK.Stage.Disarm(trap, who, tool) end

---Kills every enemy around. Server.
---@param radius number?
---@param inFight boolean?
function GK.Stage.Slay(radius, inFight) end

---Keeps the party alive whatever happens (or stops doing so). Stays on through Reset. Server.
---@param on boolean
function GK.Stage.Guard(on) end

---Puts a character next to an object at once; `party` brings the whole party. Server.
---The destination comes first and the traveller second, as in `GK.Teleport`: `Visit(chest)`
---sends the host to the chest. The other order is refused by name; to move an object to
---somebody, which is a different act, use `GK.Stage.Bring`.
---@param target GK.Uuid
---@param who GK.Uuid?
---@param party boolean?
function GK.Stage.Visit(target, who, party) end

---Moves an object to a character (default: the host) at once: the other half of `Visit`,
---for staging a situation around somebody rather than sending somebody to it. Server.
---@param what GK.Uuid
---@param to GK.Uuid?
function GK.Stage.Bring(what, to) end

---Brings a character the story keeps off stage into the level (and takes it off again). Server.
---@param id GK.Uuid
---@param on boolean?
function GK.Stage.OnStage(id, on) end

---The dialogues the story has registered for a speaker. Server.
---@param speaker string?
---@return { speaker: string, dialog: string }[]
function GK.Stage.Dialogs(speaker) end

---Starts a dialogue with a speaker; the host is put next to it. Returns the dialogue started, or nil. Server.
---@param speaker string
---@param dialog string?
---@param who GK.Uuid?
---@return string|nil
function GK.Stage.Talk(speaker, dialog, who) end

---Takes away what holds a scene up on the server side and says what it did. Server.
---@return any
function GK.Stage.Unblock() end

---Removes an object from the game (characters are taken off the stage first). Server.
---@param id GK.Uuid
function GK.Stage.Remove(id) end

---Ends the host's fight, removes what the scene created, takes its statuses off and puts factions
---back. Returns how many objects were removed. Server.
---@return integer
function GK.Stage.Reset() end

-- =========================================================================================
-- Server/Situation.lua  (server): GK.See - the honest reading API

---Options shared by the readers of GK.See.
---@class GK.SeeOptions
---@field radius number? @ metres, default 25 (some readers 30)
---@field who GK.Uuid? @ the viewpoint; default: the party
---@field all boolean? @ no perception filter (debugging, tests); same as scope "all"
---@field scope "seen"|"known"|"all"? @ seen (default): what the party perceives now; known: plus remembered; all: omniscient
---@field limit integer? @ rows per list

---The situation as a player could know it. Reads only. Filtered by what the party perceives.
---@class GK.See
---@field EyeHeight number @ 1.6
---@field FixtureRange number @ 30
---@field CameraRange number @ 16
---@field Scopes table<string, string>
---@field Memory table @ [level] = { [uuid] = { row, t, character } }
---@field Skills string[]
---@field AbilityOf table<string, string>
---@field CheckHandles table
GK.See = {}

---Character row of GK.See.Around.
---@class GK.SeeCharacterRow
---@field id GK.Uuid
---@field name string?
---@field rel GK.Relation
---@field d number
---@field dir GK.Compass
---@field pos GK.Pos
---@field hp number?
---@field maxHp number?
---@field statuses table?
---@field dead boolean?
---@field downed boolean?
---@field inCombat boolean?
---@field talk boolean?
---@field trade boolean?
---@field loot boolean?
---@field remembered boolean?
---@field ago number?
---@field perceived boolean?

---Object row of GK.See.Around.
---@class GK.SeeObjectRow
---@field id GK.Uuid
---@field name string?
---@field kind string @ door, container, ladder, portal, usable, gear, consumable, book, key, valuable, item
---@field d number
---@field dir GK.Compass
---@field dy number
---@field pos GK.Pos
---@field locked boolean?
---@field open boolean?
---@field trap boolean?
---@field owned boolean?
---@field value number?
---@field remembered boolean?
---@field perceived boolean?

---Display name of a character or item (the game's language). Server.
---@param id GK.Uuid|any @ uuid or entity
---@return string|nil
function GK.See.Name(id) end

---Position of a uuid or entity as { x, y, z }, or nil. Server.
---@param id GK.Uuid|any
---@return GK.Pos|nil
function GK.See.Position(id) end

---Compass direction from one point to another (0 = +z = north). Server.
---@param from GK.Pos
---@param to GK.Pos
---@return GK.Compass
function GK.See.Compass(from, to) end

---The uuid of a character or item whose uuid starts with `prefix` (8+ characters), on the host's
---level; nil when none or more than one. Server.
---@param prefix string
---@return GK.Uuid|nil
function GK.See.Resolve(prefix) end

---The party's uuids, the host first. Server.
---@return GK.Uuid[]
function GK.See.Members() end

---Whether nothing solid stands between two points (the target's own body is allowed for). Server.
---@param from GK.Pos
---@param to GK.Pos
---@param slack number?
---@param target any? @ entity whose own body counts as clear
---@return boolean
function GK.See.Clear(from, to, slack, target) end

---Whether character `viewer` perceives `target` (engine sight test, or a clear line within sight range). Server.
---@param viewer GK.Uuid
---@param target GK.Uuid|any
---@param isCharacter boolean?
---@return boolean
function GK.See.Perceives(viewer, target, isCharacter) end

---Whether any party member perceives `target`. Server.
---@param target GK.Uuid|any
---@param isCharacter boolean?
---@param members GK.Uuid[]?
---@return boolean
function GK.See.PartyPerceives(target, isCharacter, members) end

---Whether `id` is a party member on stage. Server.
---@param id GK.Uuid|any
---@return boolean
function GK.See.IsMember(id) end

---Relation of a character to the party. Server.
---@param id GK.Uuid
---@param to GK.Uuid?
---@return GK.Relation
function GK.See.Relation(id, to) end

---A character's statuses. `all` includes the technical ones. Server.
---@param id GK.Uuid
---@param all boolean?
---@return { id: string, name: string, turns: integer }[]
function GK.See.Statuses(id, all) end

---A character's passives. Server.
---@param id GK.Uuid
---@return table[]
function GK.See.Passives(id) end

---What an item is to a player (door, container, ladder, portal, usable, gear, consumable, book,
---key, valuable, item; nil = scenery). Server.
---@param entity any
---@param id GK.Uuid?
---@return string|nil
function GK.See.Classify(entity, id) end

---Resolves the scope ("seen" | "known" | "all") an options table asks for. Server.
---@param options GK.SeeOptions?
---@return "seen"|"known"|"all"
function GK.See.Scope(options) end

---Whether the party saw an object earlier (it is in the memory of the current level). Server.
---@param id GK.Uuid
---@return boolean
function GK.See.Remembers(id) end

---Forgets what the party saw (all levels, or one). Server.
---@param level string?
function GK.See.Forget(level) end

---Characters and objects near the party, nearest first. Server.
---@param options (GK.SeeOptions|{ kinds: string[]?, characters: boolean?, objects: boolean?, scenery: boolean? })?
---@return { at: GK.Pos, level: string, characters: GK.SeeCharacterRow[], objects: GK.SeeObjectRow[] }
function GK.See.Around(options) end

---One character or object, as the Examine window shows it. Refused for something the party does
---not perceive unless options.all. Server.
---@param id GK.Uuid
---@param options GK.SeeOptions?
---@return table|GK.Failure
function GK.See.Entity(id, options) end

---A character's action resources: { action, bonus, reaction, movement, slots = { [level] = {left,max} }, other }; each value is { left, max }. Server.
---@param id GK.Uuid
---@return { action: number[], bonus: number[], reaction: number[], movement: number[], slots: table<integer, number[]>, other: table<string, number[]> }
function GK.See.Resources(id) end

---A spell or action row of GK.See.Actions.
---@class GK.SeeActionRow
---@field id string
---@field name string
---@field level integer?
---@field type string
---@field costs table
---@field range number?
---@field area any
---@field concentration boolean?
---@field ready boolean
---@field why string? @ why not ready
---@field target { inRange: boolean, inSight: boolean, d: number }?

---What a character can do now (from its spell book). Server.
---@param who GK.Uuid
---@param target (GK.Uuid|GK.Pos)?
---@param options { all: boolean?, filter: string? }?
---@return GK.SeeActionRow[]
function GK.See.Actions(who, target, options) end

---Chance that d20 + bonus reaches dc (advantage = 1, disadvantage = -1). Server.
---@param bonus number
---@param dc number
---@param advantage integer?
---@return number
function GK.See.Chance(bonus, dc, advantage) end

---A character's check bonuses. Server.
---@param id GK.Uuid
---@return { abilities: table<string, number>, skills: table<string, number>, proficiency: number, initiative: number, spellAbility: string?, spellDC: number?, spellAttack: number? }
function GK.See.Bonuses(id) end

---Maps a check name as the game shows it (any language) to the key in See.Bonuses. Server.
---@param name string
---@return string|nil
function GK.See.CheckKey(name) end

---For every party member: the bonus for a check and, with a DC, the chance to pass - best first. Server.
---@param check string
---@param dc number?
---@return table[]
function GK.See.WhoBest(check, dc) end

---Odds of the checks in a dialogue's answers (tags as the dialogue shows them). Server.
---@param tags string[]
---@param speaker GK.Uuid?
---@param dc number?
---@return table
function GK.See.CheckOdds(tags, speaker, dc) end

---What changes a roll of `who`. kind: "attack" | "save" | "skill" | "ability". Server.
---@param who GK.Uuid
---@param kind "attack"|"save"|"skill"|"ability"
---@param what string
---@param target GK.Uuid?
---@return { advantage: integer, dice: string[], average: number, sources: table }
function GK.See.RollMods(who, kind, what, target) end

---The odds of a spell or action against a target. options.advantage = 1 / -1 applies one. Server.
---@param who GK.Uuid
---@param spell string
---@param target GK.Uuid|GK.Pos?
---@param options { advantage: integer? }?
---@return { kind: "attack"|"save"|"none", bonus: number?, ac: number?, dc: number?, save: string?, saveBonus: number?, chance: number?, damage: { dice: string, average: number, type: string }?, expected: number? }
function GK.See.Odds(who, spell, target, options) end

---Whether a character can walk to a point (or an object), and how far. Server.
---@param who GK.Uuid
---@param to GK.Pos|GK.Uuid
---@return { reachable: boolean, length: number?, straight: number?, movement: number?, withinMovement: boolean? }
function GK.See.Path(who, to) end

---How far a character can walk in each of the eight compass directions (only ground the party
---can see). Server.
---@param options (GK.SeeOptions|{ range: number?, step: number? })?
---@return { at: GK.Pos, ways: table<GK.Compass, { reach: number, pos: GK.Pos, path: number, dy: number, seen: boolean }> }
function GK.See.Ways(options) end

---The characters that could notice the party. Server.
---@param options (GK.SeeOptions|{ hostileOnly: boolean? })?
---@return table[]
function GK.See.Watchers(options) end

---Which characters would see someone standing at `point`. Server.
---@param point GK.Pos|GK.Uuid
---@param options GK.SeeOptions?
---@return { id: GK.Uuid, name: string, rel: GK.Relation, d: number }[]
function GK.See.Exposure(point, options) end

---The fight the party is in, or nil. Server.
---@param options GK.SeeOptions?
---@return { round: integer, active: { id: GK.Uuid, name: string, rel: GK.Relation }, order: table[], actions: GK.SeeActionRow[]? }|nil
function GK.See.Combat(options) end

---Whose turn is open, cheaply. Server.
---@return { inFight: boolean, round: integer?, turn: { id: GK.Uuid, name: string, rel: GK.Relation }[]? }
function GK.See.Turn() end

---For a party member in a fight: every perceived hostile with the best things to do to it. Server.
---@param who GK.Uuid
---@param actions GK.SeeActionRow[]?
---@param movement number?
---@return { id: GK.Uuid, name: string, d: number, ac: number, hp: number, options: { id: string, name: string, chance: number, expected: number, damage: table, move: number }[] }[]
function GK.See.Targets(who, actions, movement) end

---What the party carries, per member. Server.
---@param who GK.Uuid?
---@return { id: GK.Uuid, name: string, items: table[] }[]
function GK.See.Inventory(who) end

---Whether a character can flee the fight now (the hot bar's Flee button). Server.
---@param who GK.Uuid?
---@return { can: boolean, why: string[], enemyAt: number?, need: number? }
function GK.See.Flee(who) end

---Every party member with level, class, experience, health, resources, statuses, and the party's gold. Server.
---@return table
function GK.See.Party() end

---The story's turn counters on screen (countdowns). Server.
---@return { name: string, text: string?, turnsLeft: number, seconds: number, duration: number }[]
function GK.See.Timers() end

---Where a character is, in words a person reads. Server.
---@param who GK.Uuid?
---@return string
function GK.See.Place(who) end

---The situation in one table. Server.
---@param options GK.SeeOptions?
---@return { level: string, mode: "exploration"|"combat"|"dialogue"|"turn-based", gameState: string, active: string?, party: { name: string, hp: number, maxHp: number, dead: boolean }[], round: integer? }
function GK.See.Status(options) end

-- =========================================================================================
-- Server/Act.lua  (server): what a player can do, and nothing more

---Commands start a job and return at once: `{ job = n, ... }`, or `{ ok = false, error }` when a
---player could not do it. GK.Act.Job(n) tells how it went.
---@class GK.ActStarted
---@field job integer
---@field length number? @ Move: path length
---@field goal GK.Pos? @ Move
---@field warning string? @ Move
---@field spell string? @ Cast: the name actually cast
---@field distance number? @ Cast
---@field range number? @ Cast
---@field walk number? @ metres walked first (Attack/Use/...)
---@field tool string? @ Lockpick/Disarm
---@field carried boolean? @ UseCarried

---Immediate result of a command that needs no job.
---@class GK.ActDone
---@field ok true
---@field ui "EndTurn"|"Flee"|nil @ the UI button the client must press (viaUi / Flee)
---@field taken GK.Uuid[]? @ Take
---@field controlled boolean? @ Select
---@field said string? @ Say

---@alias GK.ActResult GK.ActStarted|GK.ActDone|GK.Failure

---A job's state.
---@class GK.ActJob
---@field job integer
---@field kind string
---@field who GK.Uuid
---@field status "running"|"done"|"failed"
---@field result any
---@field error string?
---@field seconds number
---@field step string?

---What a player can do, under the same rules (walk, perceive, turn, dice, costs). Server.
---@class GK.Act
---@field JobList table<integer, table> @ internal job table (last 50)
---@field NextJob integer
---@field Reach number @ metres from an object's centre a character interacts from (1.6)
---@field TalkReach number @ 2.5
---@field MeleeReach number @ 2.1
---@field SelectedOnly boolean @ commands go to the selected character only (default true)
---@field Narration table @ immersive lines (kept in saves)
---@field Ordered table
---@field StuckCache table
---@field On table<string, fun(...)> @ Osiris event handlers by event
---@field Registered table<string, boolean>
---@field Tick fun()
---@field ThreatNote fun(...): any
---@field CarriedBy fun(item: GK.Uuid): GK.Uuid|nil
GK.Act = {}

---A job's state, or { ok = false, error = "no such job" }. Server.
---@param n integer
---@return GK.ActJob|GK.Failure
function GK.Act.Job(n) end

---The recent jobs, newest first (up to 10). Server.
---@return GK.ActJob[]
function GK.Act.Jobs() end

---Whether a character ended its turn this round through GK.Act.EndTurn. Server.
---@param who GK.Uuid
---@return boolean
function GK.Act.HasEnded(who) end

---Why `who` cannot act now (nil = it can). Server.
---@param who GK.Uuid
---@param anyTime boolean?
---@return string|nil
function GK.Act.CannotAct(who, anyTime) end

---The party member the player controls now. Server.
---@return GK.Uuid
function GK.Act.Selected() end

---Takes `amount` of a resource ("Movement", "ActionPoint", "SpellSlot" with `level`). Returns how much was taken. Server.
---@param who GK.Uuid
---@param name string
---@param amount number
---@param level integer?
---@return number
function GK.Act.Charge(who, name, amount, level) end

---A point next to an object that the character can walk to, each proven by the pathfinder. Server.
---@param who GK.Uuid
---@param target GK.Uuid|GK.Pos
---@param within number?
---@param loose boolean?
---@return table|nil
function GK.Act.ApproachPoint(who, target, within, loose) end

---For a goal the character cannot walk to: the nearest point it can walk to and the jumps and
---teleports ready now. Server.
---@param who GK.Uuid
---@param goal GK.Pos
---@return table
function GK.Act.Alternatives(who, goal) end

---Whether the pathfinder cannot start from where the character stands. Server.
---@param who GK.Uuid
---@return boolean
function GK.Act.Stuck(who) end

---Same, for a known position. Server.
---@param who GK.Uuid
---@param p GK.Pos
---@return boolean
function GK.Act.StuckNow(who, p) end

---A short step (at most 3 m) onto the nearest free tile when the character is stuck. Server.
---@param who GK.Uuid
---@return { job: integer, to: GK.Pos }|GK.Failure
function GK.Act.Unstick(who) end

---Who would get an attack of opportunity if `who` walked to `goal` ({ names }, empty when safe). Server.
---@param who GK.Uuid
---@param goal GK.Pos
---@return string[]
function GK.Act.Provokes(who, goal) end

---Who would threaten a character standing at `goal` (a warning, not a refusal). Server.
---@param who GK.Uuid
---@param goal GK.Pos
---@return string[]
function GK.Act.Threats(who, goal) end

---Walk to a point or next to an object (never teleports). Server.
---@param who GK.Uuid?
---@param to GK.Pos|GK.Uuid
---@param options { partial: boolean?, speed: "Run"|"Walk"|"Sprint"?, provoke: boolean?, note: string? }?
---@return GK.ActResult
function GK.Act.Move(who, to, options) end

---The stats name of `spell` cast with a slot of `level`, or nil. Server.
---@param spell string
---@param level integer
---@return string|nil
function GK.Act.Upcast(spell, level) end

---Cast a spell or action from the character's spell book. Server.
---@param who GK.Uuid?
---@param spell string
---@param target (GK.Uuid|GK.Pos)?
---@param options { upcast: integer?, target2: GK.Uuid|GK.Pos?, walk: boolean? }?
---@return GK.ActResult
function GK.Act.Cast(who, spell, target, options) end

---The main-hand weapon attack. Server.
---@param who GK.Uuid?
---@param target GK.Uuid
---@param options { mode: "melee"|"ranged"? }?
---@return GK.ActResult
function GK.Act.Attack(who, target, options) end

---@param who GK.Uuid?
---@param point GK.Pos
---@return GK.ActResult
function GK.Act.Jump(who, point) end

---@param who GK.Uuid?
---@param target GK.Uuid
---@return GK.ActResult
function GK.Act.Shove(who, target) end

---@param who GK.Uuid?
---@return GK.ActResult
function GK.Act.Hide(who) end

---@param who GK.Uuid?
---@return GK.ActResult
function GK.Act.Dash(who) end

---@param who GK.Uuid?
---@return GK.ActResult
function GK.Act.Disengage(who) end

---@param who GK.Uuid?
---@param target GK.Uuid
---@return GK.ActResult
function GK.Act.Help(who, target) end

---@param who GK.Uuid?
---@param item GK.Uuid
---@param target GK.Uuid|GK.Pos
---@return GK.ActResult
function GK.Act.Throw(who, item, target) end

---Use an item the party carries (a potion, a scroll, a note): no walking. Server.
---@param who GK.Uuid?
---@param item GK.Uuid
---@return GK.ActResult
function GK.Act.UseCarried(who, item) end

---Walk up and use: open/close a door or chest, pull a lever, climb a ladder, read, drink. Server.
---@param who GK.Uuid?
---@param item GK.Uuid
---@return GK.ActResult
function GK.Act.Use(who, item) end

---Walk up and take it. Server.
---@param who GK.Uuid?
---@param item GK.Uuid
---@return GK.ActResult
function GK.Act.PickUp(who, item) end

---Walk up and open a corpse's or container's loot window. Server.
---@param who GK.Uuid?
---@param corpse GK.Uuid
---@return GK.ActResult
function GK.Act.Loot(who, corpse) end

---The items inside a container or corpse the party has open or stands at. Server.
---@param who GK.Uuid?
---@param container GK.Uuid
---@return { id: GK.Uuid, name: string, count: integer }[]|GK.Failure
function GK.Act.Contents(who, container) end

---Take one item (or all, item = nil) from an open container. Server.
---@param who GK.Uuid?
---@param container GK.Uuid
---@param item GK.Uuid?
---@return GK.ActResult
function GK.Act.Take(who, container, item) end

---Walk up and ask to talk, as clicking on the NPC does. Server.
---@param who GK.Uuid?
---@param npc GK.Uuid
---@return GK.ActResult
function GK.Act.Talk(who, npc) end

---Makes a party member the one the player controls. Server.
---@param who GK.Uuid
---@return GK.ActResult
function GK.Act.Select(who) end

---Ends the selected character's turn; `viaUi` only checks and answers { ok, ui = "EndTurn" }. Server.
---@param who GK.Uuid?
---@param viaUi boolean?
---@return GK.ActResult
function GK.Act.EndTurn(who, viaUi) end

---Flee the fight: the check; the client presses the button (answer carries ui = "Flee"). Server.
---@param who GK.Uuid?
---@return GK.ActResult
function GK.Act.Flee(who) end

---The turn-based mode button. Server.
---@param on boolean?
---@param who GK.Uuid?
---@return GK.ActResult
function GK.Act.TurnBased(on, who) end

---Hide / stop hiding. Server.
---@param on boolean?
---@param who GK.Uuid?
---@return GK.ActResult
function GK.Act.Sneak(on, who) end

---@param who GK.Uuid?
---@param item GK.Uuid
---@return GK.ActResult
function GK.Act.Equip(who, item) end

---@param who GK.Uuid?
---@param item GK.Uuid
---@return GK.ActResult
function GK.Act.Unequip(who, item) end

---Whether this Script Extender build can queue lock picking and disarming safely. Server.
---@return boolean
function GK.Act.CanPickLocks() end

---Picks a lock as the context menu does (needs Thieves' Tools). Server.
---@param who GK.Uuid?
---@param item GK.Uuid
---@return GK.ActResult
function GK.Act.Lockpick(who, item) end

---Disarms a discovered trap with a Trap Disarm Toolkit. Server.
---@param who GK.Uuid?
---@param trap GK.Uuid
---@return GK.ActResult
function GK.Act.Disarm(who, trap) end

---Immersive mode: a line the acting character says (shown as a notification) and a plain reason;
---both kept in GK.Act.Narration. Changes nothing in the world. Server.
---@param text string
---@param why string?
---@param who GK.Uuid?
---@return GK.ActDone|GK.Failure
function GK.Act.Say(text, why, who) end

---Stops what the character is doing (walk, cast queue); running jobs fail with "stopped". Server.
---@param who GK.Uuid?
---@return { ok: true }
function GK.Act.Stop(who) end

-- =========================================================================================
-- Client/Windows.lua + Popups.lua + WindowReaders.lua  (ui side): GK.Gui

---A named window of the registry GK.Gui.Windows. Add your own entries.
---@class GK.GuiWindow
---@field widget string @ the widget (x:Name) that is open while the window is
---@field page string? @ the page of a shared widget (the character panel)
---@field open string|fun(ui: GK.Gui): boolean, string? @ a game input event, or a function that opens it
---@field close string|fun(ui: GK.Gui): boolean, string?|boolean? @ event, function, or true to close by CloseWidget
---@field about string? @ one line for people
---@field needs string? @ what must be true for it to open
---@field reopen boolean?
---@field blocking boolean? @ waits for the player (popups)
---@field policy string? @ key in the Resolve policy
---@field active (fun(data: any): boolean)?
---@field read (fun(data: any, ui: GK.Gui): table)? @ plain summary of what the player sees
---@field step (fun(ui: GK.Gui, options: table): table)? @ one step through the window

---Every window of the game, operated from code - opened, read, clicked through, closed - without
---the mouse and window focus. UI side (inside Ext.UI.Defer). Results are { ok, ... } or
---{ ok = false, error } and never throw for a game-side problem.
---@class GK.Gui
---@field Hud table<string, boolean> @ widgets always present while playing
---@field Events string[] @ the 74 game input event names
---@field Windows table<string, GK.GuiWindow> @ registry of named windows
---@field AutoState table<string, table>
---@field QuietState { on: boolean, closed: integer }
---@field DefaultPolicy table @ what a player in a hurry does
---@field Text fun(value: any): string|nil @ readable text of a Noesis value
---@field PressBox fun(event: string): table
GK.Gui = {}

---The element of an open widget (top-most of that name), or nil. UI.
---@param name string @ widget name or window name
---@return any|nil
function GK.Gui.Widget(name) end

---The DataContext (view model) of an open widget, or nil. UI.
---@param name string
---@return any|nil
function GK.Gui.Data(name) end

---The open widgets. UI.
---@return { name: string, data: string, file: string?, hud: boolean }[]
function GK.Gui.Open() end

---The page a widget shows, for widgets with pages. UI.
---@param name string
---@return string|nil
function GK.Gui.Page(name) end

---Whether a widget, or a named window, is open. UI.
---@param name string
---@return boolean
function GK.Gui.IsOpen(name) end

---Raises a game input event (one of GK.Gui.Events, or any the game knows) as its key would. UI.
---@param name string
---@param from string? @ widget whose CustomEvent to use
---@return { ok: boolean, event: string?, known: boolean?, error: string? }
function GK.Gui.Event(name, from) end

---Opens a window of GK.Gui.Windows by name, or raises the game input event of that name. UI.
---@param name string
---@return { ok: boolean, widget: string?, already: boolean?, error: string? }
function GK.Gui.Show(name) end

---Closes an open widget or named window. UI.
---@param name string
---@return { ok: boolean, already: boolean?, via: string?, error: string? }
function GK.Gui.Close(name) end

---Closes every window that is not part of the HUD (optionally keeping one). UI.
---@param keep string?
---@return table
function GK.Gui.CloseAll(keep) end

---An element by x:Name inside an open widget, template parts included. UI.
---@param name string
---@param element string
---@return any|nil
function GK.Gui.Find(name, element) end

---A control row of GK.Gui.Controls.
---@class GK.GuiControlRow
---@field n integer
---@field name string?
---@field kind string
---@field text string?
---@field enabled boolean?
---@field catalogued boolean?
---@field runs string[]?

---The controls of an open widget. filter: part of a name or text, or a table. UI.
---@param name string
---@param filter string|{ name: string?, text: string?, data: string?, all: boolean? }|nil
---@return GK.GuiControlRow[]|GK.Failure
function GK.Gui.Controls(name, filter) end

---Clicks a control of an open widget as a player does (the XAML chain). UI.
---@param name string
---@param what string|integer|table @ name, part of its text, filter table, or number in GK.Gui.Controls
---@param which integer? @ the n-th match (default 1)
---@return table
function GK.Gui.Click(name, what, which) end

---Selects an item of a list (ListBox, tab strip...) of an open widget. UI.
---@param name string
---@param list string @ the list's x:Name
---@param item integer|string @ 1-based number or part of its text
---@return table
function GK.Gui.Select(name, list, item) end

---What a key does in a widget ("UIAccept", "UICancel", "UITakeAll"...). UI.
---@param name string
---@param event string
---@return table
function GK.Gui.Input(name, event) end

---Runs a command of a widget's data by path; string/number/boolean parameters are boxed. UI.
---@param name string
---@param path string
---@param parameter any?
---@return table
function GK.Gui.Run(name, path, parameter) end

---Any view model (or Noesis object) as plain values. UI.
---@param object any
---@param options { depth: integer?, items: integer? }?
---@return any
function GK.Gui.Snapshot(object, options) end

---The data of an open widget: the window's own reader or plain values. UI.
---@param name string
---@param options { path: string?, depth: integer?, items: integer?, raw: boolean? }?
---@return table
function GK.Gui.Read(name, options) end

---One step through a window that has a stepper (e.g. "levelup"). UI.
---@param name string
---@param options table?
---@return table
function GK.Gui.Step(name, options) end

---Runs GK.Gui.Step on a timer until done/stuck/limit; state is kept in GK.Gui.AutoState[name]. UI.
---@param name string
---@param options { limit: integer?, every: integer? }?
---@return { ok: true, started: true }
function GK.Gui.Auto(name, options) end

---The view model of an item of the selected character by part of its name, or nil. UI.
---@param text string
---@return any|nil
function GK.Gui.Item(text) end

---Does to an item what the player does: "equip", "unequip", "use", "drop". UI.
---@param action "equip"|"unequip"|"use"|"drop"
---@param text string
---@return table
function GK.Gui.ItemDo(action, text) end

---Tutorials off (on = false turns them back on). UI.
---@param on boolean?
---@return table
function GK.Gui.Quiet(on) end

---Names what is wrong: "gameover", "firstlaunch", "mainmenu" (second result: the screen), or nil. UI.
---@return string|nil
---@return table?
function GK.Gui.Stuck() end

---One step out of a dead end (game over, main menu, first-launch screens). Call again until Stuck() is nil. UI.
---@return table
function GK.Gui.Recover() end

---The named windows and whether each is open. UI.
---@return { name: string, widget: string, page: string?, about: string?, needs: string?, open: boolean }[]
function GK.Gui.List() end

---The top-most window that waits for the player right now, with its summary and choices. UI.
---@return { ok: boolean, window: string?, widget: string?, summary: table?, choices: table?, policy: string?, all: { window: string, widget: string }[] }
function GK.Gui.Blocking() end

---ONE step through whatever blocks, by a policy table. A field may also be a table of step options. UI.
---@param policy { dialogue: string|integer?, roll: "throw"|"reroll"?, reaction: "decline"|"react"|integer?, box: "affirmative"|"negative"|integer|string?, tutorial: "close"?, loot: "takeall"|"close"?, trade: "close"?, pickpocket: "close"?, rest: "rest"|"cancel"?, reward: "accept"|integer?, learn: "close"|"learn"?, split: "confirm"|"cancel"?, book: "close"?, gamemenu: "resume"?, save: "close"?, load: "close"? }?
---@return table
function GK.Gui.Resolve(policy) end

---@return table
function GK.Gui.QuickSave() end

---@return table
function GK.Gui.QuickLoad() end

---A named save: opens the save window's name box when not open yet (answers { ok, waiting = true } until then). UI.
---@param name string
---@return table
function GK.Gui.SaveAs(name) end

---Loads the newest save (opens the load window first). UI.
---@return table
function GK.Gui.LoadLatest() end

---Trade actions: move one item, balance, finish. Several in one call run in that order. UI.
---@param options { move: integer|string?, side: "player"|"trader"|"playerOffer"|"traderOffer"?, balance: boolean?, finish: boolean?, barter: boolean? }?
---@return { ok: boolean, log: table[]?, error: string? }
function GK.Gui.Trade(options) end

---Asks for a long rest as the HUD's Long Rest button does. UI.
---@return table
function GK.Gui.LongRest() end

---Generated catalogue of the game's XAML widgets (Client/UICatalog.lua; do not edit).
---@class GK.UICatalog
---@field widgets table<string, { name: string, file: string, variant: string, root: string, context: string, dataTypes: string[], controls: table[] }>
---@field library table
GK.UICatalog = {}

-- =========================================================================================
-- Client/Overlay.lua  (client)

---GateKit's part of the in-game windows: the console's open/close key and the HUD button, kept in
---"Script Extender/Gwynerva_GateKit/overlay.json". theme, font, blockKeys and blockMouse are Weave's
---(Weave.Settings); these functions pass them through, so callers see one set of settings.
---@class GK.Overlay
GK.Overlay = {}

---A setting's value: GK.Overlay.Get("font"). Keys: theme, font, key, blockKeys, blockMouse, hudButton, hudX, hudY.
---@param key string
---@return any
function GK.Overlay.Get(key) end

---fn(key, value) after a setting changed.
---@param fn fun(key: string, value: any)
function GK.Overlay.OnChange(fn) end

---Makes a hand-built window part of the overlay: while isOpen() the game's input is held back and
---Esc calls close(). `reset` returns it to its default place (GK.OverlayRescue). Same as Weave.RegisterWindow.
---@param window { isOpen: fun(): boolean, close: fun(), reset: fun()? }
function GK.Overlay.Register(window) end

---@class GK.OverlayState
---@field theme string
---@field font string
---@field key string
---@field blockKeys boolean
---@field blockMouse boolean
---@field hudButton boolean
---@field hudX integer
---@field hudY integer
---@field themes string[]
---@field fonts string[]
---@field capturingKey boolean
---@field settingsOpen boolean
---@field anyOpen boolean
---@field has { files: boolean }
---@field hud { visible: boolean, found: boolean, x: number?, y: number?, hovered: boolean }

---Changes overlay settings. `settings = true|false` opens or closes the settings window;
---`defaults = true` puts everything back; `rescue = true` runs GK.OverlayRescue. Client.
---@param opts { theme: string?, font: string?, key: string?, blockKeys: boolean?, blockMouse: boolean?, hudButton: boolean?, hudX: number?, hudY: number?, settings: boolean?, defaults: boolean?, rescue: boolean? }?
---@return GK.OverlayState
function GK.OverlaySet(opts) end

---The overlay as data. Client.
---@return GK.OverlayState
function GK.OverlayState() end

---Every Weave window (the console included) returns to its default place and size: Weave.Rescue(). Also `!gkrescue`. Client.
---@return true
function GK.OverlayRescue() end

---Opens (true), closes (false) or toggles (nil) the overlay settings window. False when Weave cannot run. Client.
---@param open boolean?
---@return boolean
function GK.OverlaySettings(open) end

-- =========================================================================================
-- Client/Console.lua  (client)

---Hook lists other parts of the overlay register into.
---@class GK.ConsoleHooks
---@field built fun(window: any, tabBar: any, tabs: table)[] @ called whenever the console window was built
---@field second fun()[] @ called once a second while the window is open

---Evaluates code on a side without a word in the log and hands the outcome to
---`callback(ok, text, value)`. Client (server answers arrive asynchronously).
---@param side "client"|"server"
---@param code string
---@param callback fun(ok: boolean, text: string, value: any)
function GK.ConsoleAsk(side, code, callback) end

---Runs code as the console does: echoes it, runs it on `target`, logs the result. `@name.lua`
---runs a file from the Script Extender folder. Client.
---@param target GK.Target?
---@param code string
---@return integer ticket @ the number the echo and result entries carry
function GK.ConsoleRun(target, code) end

---Pins an expression evaluated once a second on `side`. Client.
---@param code string
---@param side "client"|"server"?
function GK.ConsoleWatch(code, side) end

---Removes one pinned expression (by index), or all when nil. Client.
---@param index integer?
function GK.ConsoleUnwatch(index) end

---The pinned expressions with their latest values. Client.
---@return { side: string, code: string, value: string? }[]
function GK.ConsoleWatches() end

---Adds an item to the console's menu bar. Client.
---@param group string @ the menu
---@param label string
---@param target GK.Target
---@param code string
---@param tip string?
function GK.ConsoleAction(group, label, target, code, tip) end

---Restarts Lua on both sides, as the Script Extender console's `reset` does. Client.
function GK.ConsoleReload() end

---The event journal of both sides, merged by time. Client.
---@param opts { limit: integer?, text: string? }?
---@return { side: string, n: integer, t: number, kind: string, data: any }[]
function GK.ConsoleEvents(opts) end

---Opens or closes the console (what the key does). Client.
---@return boolean open
function GK.ToggleConsole() end

---Log entries as data. Client.
---@param opts { since: integer?, side: "client"|"server"?, level: GK.LogLevel?, source: string?, text: string?, kind: "cmd"|"result"?, ticket: integer?, limit: integer? }?
---@return { last: integer, total: integer, entries: { n: integer, side: string, level: GK.LogLevel, source: string, tag: string?, text: string, count: integer, kind: string?, ticket: integer?, target: string?, clock: string? }[] }
function GK.ConsoleLog(opts) end

---The log as the window shows it now (filters applied), as text. Client.
---@return string
function GK.ConsoleOutput() end

---Everything about the window: open, typed text, filters, counters. Client.
---@return table
function GK.ConsoleState() end

---Changes the window as a user would. Client.
---@param opts { open: boolean?, target: GK.Target?, editor: boolean?, input: string?, cursor: integer?, scroll: "top"|"end"|number?, focus: boolean?, follow: boolean?, view: "color"|"select"?, sides: { client: boolean?, server: boolean? }?, levels: table<GK.LogLevel, boolean>?, source: string?, text: string?, key: string?, blockKeys: boolean?, blockMouse: boolean?, font: string?, tab: "Log"|"Watch"|"Settings"? }
---@return table state @ GK.ConsoleState()
function GK.ConsoleSet(opts) end

---Runs what is in the input line, as Enter does. Client.
---@return integer|nil ticket
function GK.ConsoleSubmit() end

---Completes the input line as Tab does and returns the new text. Client.
---@return string
function GK.ConsoleTab() end

---What the Copy all button does. Client.
---@return { lines: integer, bytes: integer, clipboard: boolean, file: string }
function GK.ConsoleCopy() end

---@return string[]
function GK.ConsoleHistory() end

---@return true
function GK.ConsoleClear() end

-- =========================================================================================
-- Client/Panels.lua  (client): GK.UI - panels of other mods in the console overlay

---An action of a panel element: a function (runs on the client) or a table naming where a piece
---of Lua runs; in such code `value` is what the element was set to.
---@alias GK.PanelAction (fun(value: any): any)|{ server: string? }|{ client: string? }|{ ui: string? }

---Options of a panel element.
---@class GK.PanelElementOpts
---@field sameLine boolean?
---@field tip string?

---What a panel builder function may call.
---@class GK.PanelBuilder
local PanelBuilder = {}

---Puts the next element on the same line as the previous one.
---@return GK.PanelBuilder
function PanelBuilder:SameLine() end

---A line (or several) of text. Give a function to have it re-read once a second.
---@param text string|fun(): string
---@param opts GK.PanelElementOpts?
---@return GK.PanelBuilder
function PanelBuilder:Text(text, opts) end

---A dividing line, with a title if given.
---@param title string?
---@return GK.PanelBuilder
function PanelBuilder:Separator(title) end

---A button.
---@param label string
---@param action GK.PanelAction
---@param opts GK.PanelElementOpts?
---@return GK.PanelBuilder
function PanelBuilder:Button(label, action, opts) end

---A checkbox. `value`: the state, or a function returning it (re-read once a second).
---@param label string
---@param value boolean|fun(): boolean
---@param action fun(on: boolean)|table
---@param opts GK.PanelElementOpts?
---@return GK.PanelBuilder
function PanelBuilder:Toggle(label, value, action, opts) end

---A one-line text field; `action(text)` runs on Enter.
---@param label string
---@param value string|fun(): string
---@param action fun(text: string)|table
---@param opts GK.PanelElementOpts?
---@return GK.PanelBuilder
function PanelBuilder:Input(label, value, action, opts) end

---A drop-down list; `action(option, index)` runs on a choice.
---@param label string
---@param options string[]
---@param value string|fun(): string
---@param action fun(option: string, index: integer)|table
---@param opts GK.PanelElementOpts?
---@return GK.PanelBuilder
function PanelBuilder:Select(label, options, value, action, opts) end

---A block of "name: value" lines, re-read once a second.
---@param source (fun(): table)|{ server: string }
---@param opts GK.PanelElementOpts?
---@return GK.PanelBuilder
function PanelBuilder:Values(source, opts) end

---One expression with its value beside it, re-evaluated once a second on `side`.
---@param code string
---@param side "client"|"server"?
---@return GK.PanelBuilder
function PanelBuilder:Watch(code, side) end

---How another mod puts its own tab (or window) into the GateKit overlay.
---@class GK.UIPanels
GK.UI = {}

---Registers (or replaces) a panel: a tab of the GateKit window named `name`, filled by
---`builder(p)`. opts.window = true makes it a window of its own. Client.
---@param name string
---@param builder fun(p: GK.PanelBuilder)
---@param opts { window: boolean? }?
---@return string name
function GK.UI.Panel(name, builder, opts) end

---Takes a panel out of the overlay. Client.
---@param name string
---@return boolean
function GK.UI.Remove(name) end

---Opens (or closes) a panel that is a window of its own. Client.
---@param name string
---@param on boolean?
---@return boolean
function GK.UI.Open(name, on) end

---The names of the registered panels, in order. Client.
---@return string[]
function GK.UI.Panels() end

---Does what a click would: presses a button, flips a toggle, enters a value, chooses an option. Client.
---@param name string
---@param label string
---@param value any?
---@return true
function GK.UI.Press(name, label, value) end

---A panel as data. Client.
---@param name string
---@return { name: string, last: { label: string, ok: boolean, text: string? }?, elements: { kind: string, label: string, value: any }[], window: boolean }|nil
function GK.UI.Read(name) end

-- =========================================================================================
-- Console commands and net channels registered by the mod (not Lua API, but part of the contract)
--
-- Script Extender console commands registered: `!gk` (toggle the console), `!gkui` (overlay
-- settings), `!gkrescue` (bring every window back). Weave registers `!weaverescue`, `!weavedemo`.
-- Net channels (ModuleUUID): GK_Run, GK_Result, GK_Complete, GK_Completions, GK_Reset, GK_Log,
-- GK_Echo, GK_Focus.
