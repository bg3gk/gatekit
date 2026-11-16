---@meta
-- LuaLS annotations for the public API of the GateKit bridge mod (global `GK`), whose code is
-- mods/GateKit/Mods/Gwynerva_GateKit/ScriptExtender/Lua. Format as Script Extender's
-- ExtIdeHelpers.lua. Internal helpers are not listed: see
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
---@alias GK.Pos number[]|{ x: number, y: number, z: number } @ { x, y, z } or { x = .., y = .., z = .. }: every server function that takes a position takes both (GK.Pos)
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

---An entry of the journal view of the event log (GK.Events).
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
---@field Act GK.Act @ server: what a player does (fair) and readings
---@field Verbs GK.Verbs @ both: the class of every verb (Shared/VerbClasses.json)
---@field See GK.See @ server: the situation as a player could know it
---@field Stage GK.Stage @ server: setting up situations from code (cheats)
---@field Cheat GK.Cheat @ server: the rule-bending calls, named and recorded
---@field Conditions GK.Conditions @ server: the game's spell condition texts
---@field Focus GK.FocusServer|GK.FocusClientMembers @ markers (server) / calm screen and outlines (client)
---@field GameLog GK.GameLog @ server: the combat log and toasts as a feed
---@field Heard GK.Heard @ server: barks, narrator and dialogue lines as a log
---@field SaveState GK.SaveState @ server: the bridge's memory saved inside the game's save
---@field Speed GK.Speed @ server: game speed by situation
---@field Rolls GK.Rolls @ server: rolls decided in advance (cheats), Karmic Dice, the rolls the game finishes
---@field Memory GK.Memory @ server: the memory instrument (read, watch, freeze a field)
---@field Sound GK.Sound @ client: how loud this copy of the game is
---@field Camera GK.Camera @ client: camera control
---@field Wake GK.Wake @ server: a creature or a place kept awake and drawn far from the party (cheats)
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

---Marks `result` as observed later: `check(ticks, final)` is called once a tick of this side
---(`ticks`: the side's ticks since the observation began) until it returns the final table; at
---`within` ms of a running game, counted in ticks (GK.LimitTicks), it is called with final = true.
---Both sides.
---@generic T: table
---@param result T
---@param check fun(ticks: integer, final: boolean): table|nil
---@param within number? @ ms of a running game (default 1500)
---@return T
function GK.Observe(result, check, within) end

---Takes the observation off a value: the value (a copy without it), its check, its within.
---@return any value, function? check, number? within
function GK.Unobserved(value) end

---An observation that only waits: answers once `ready()` returns a table, or `timeout()` at the limit.
---@param ready fun(): table|nil
---@param within number? @ ms of a running game
---@param timeout (fun(): table)?
---@return table
function GK.Later(ready, within, timeout) end

---Follows a value that may be observed to its end, one look a call: the function returns the final
---answer (with waitedTicks and waitedMs) or nil while it waits. The loop the file channel runs.
---@return fun(): table|any|nil
function GK.Follow(value) end

---This side's tick count (GK.EventLog.Ticks): the clock every limit of the bridge is counted in.
---@return integer
function GK.Ticks() end

---A limit in ms of a running game as this side's ticks, at the nominal thirty a second (at least one).
---@param ms number
---@return integer
function GK.LimitTicks(ms) end

---The outcome of a hit from its effect flags: "critical miss", "miss", "critical", "hit" or "none".
---@param flags table<string, boolean>
---@return string
function GK.HitOutcome(flags) end

---Client: the characters this client controls, kept from ClientControl's creation and destruction.
---@return EntityHandle[]
function GK.Controlled() end

---Starts the file channel of the host for one side: polls queue_<side>.jsonl in
---"Script Extender/Gwynerva_GateKit/", runs commands, writes res_<side>_<id>.json and a
---state_<side>.json heartbeat. Called once by each Bootstrap file.
---@param side "client"|"server"
function GK.Start(side) end

-- =========================================================================================
-- Shared/EventLog.lua  (both sides)

---A row of the event log (schemas/events/row.schema.json).
---@class GK.EventRow
---@field seq integer @ 1, 2, 3 ... within one boot of this side's Lua state
---@field tick integer @ this side's tick count when it was written
---@field t integer @ Ext.Utils.MonotonicTime() in ms
---@field kind string @ a kind of the registry (GK.EventLog.Kinds)
---@field knows "player"|"omniscient" @ who could know it
---@field key string? @ the waiting state an attention row opens or closes
---@field attention "begin"|"hold"|"end"|"release"|"news"|nil
---@field scope integer? @ the stage scope open when it was written
---@field payload table?

---The one append-only record of what happened on this side. Observation.
---@class GK.EventLog
---@field Kinds table<string, { family: string, class: string, knows: string, knowsPerRow: boolean?, attention: string?, ends: string?, payload: table<string, string> }> @ the registry's kinds (Shared/EventKinds.json)
---@field Families table<string, { always: boolean?, default: boolean?, about: string }> @ the registry's families
---@field Seq integer @ the last seq
---@field Boot integer @ this boot of the side's Lua state, one above the previous one
---@field Open table<string, GK.EventRow> @ the waiting view: key -> the row that opened it
GK.EventLog = {}

---Appends a row; `kind` must be in the vocabulary. A detail kind whose family is off writes nothing.
---@param kind string
---@param payload table?
---@param options { key: string? }?
---@return GK.EventRow?
function GK.EventLog.Append(kind, payload, options) end

---The rows after `seq`, filtered. Observation.
---@param seq integer? @ 0 or nil: every row kept in memory
---@param filter { kinds: table<string, boolean>?, family: string?, scope: integer?, attention: boolean?, knows: string?, limit: integer? }?
---@return { boot: integer, seq: integer, first: integer, rows: GK.EventRow[], dropped: integer? }
function GK.EventLog.Since(seq, filter) end

---Switches families on for a check (at most 600 s; off when the stage scope that switched them ends). Observation.
---@param families string|string[]
---@param options { seconds: number?, components: string[]? }?
---@return string[] on @ the families on now
function GK.EventLog.On(families, options) end

---Switches families off (nil: every family not on by default). Observation.
---@param families string|string[]|nil
---@return string[] on
function GK.EventLog.Off(families) end

---A row by its seq while it is kept in memory.
---@param seq integer
---@return GK.EventRow?
function GK.EventLog.Row(seq) end

---The rows of the previous Lua state of this side, read back from its files.
---@return GK.EventRow[]?
function GK.EventLog.Previous() end

---What the log costs: rows, writes, ms, max, perWriteMs, seq, boot, kept.
---@return table
function GK.EventLog.Costs() end

---A caller's own mark in the log: a row of kind `note`, payload { note = kind, data }. Both sides.
---@param kind string
---@param data any
function GK.Note(kind, data) end

---The journal view of the log: rows after `since` as { n, t, kind, data }, an Osiris row under its
---event's name and a note under its own. Observation.
---@param since integer? @ 0 or nil: every row kept
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
---later; GK.Find(template, who) returns them. A template the game does not have is an error, and
---nothing is given. Server.
---Give(who, template[, count]) is understood too.
---@param template string @ root template uuid, Name_uuid, or a short name of GK.Stage.Templates
---@param count integer? @ default 1
---@param who GK.Uuid?
---@return { template: string, name: string?, count: integer, to: GK.Uuid, before: integer?, note: string } @ before: how many the inventory held; the new ones are queued, not yet there
function GK.Give(template, count, who) end

---Whether a character is in a dialogue, from its ServerCharacter.InDialog flag (Osi.IsInDialog
---where the game has it - this one does not). nil and why when neither can be read: a check that
---cannot run is never "not in a dialogue". Server.
---@param who GK.Uuid
---@return boolean|nil, string?
function GK.InDialog(who) end

---A position in either form, as { x, y, z }; nil and why for anything else. Server.
---@param p any
---@return number[]|nil, string?
function GK.Pos(p) end

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

---Draws the controlled character's weapon with the player's own toggle: the key bound to the
---game's input event ToggleCombatMode, pressed in the game's input system. Answers when the
---client's weapon state shows it drawn: `{ ok, did, who, key, from, to, ticks }`. UI side.
---@return table
function GK.Act.Draw() end

---Puts the controlled character's weapon away with the player's own toggle; answers as `Draw`. UI side.
---@return table
function GK.Act.Sheathe() end

---Presses the key the profile binds to one of the game's input events ("ToggleWeaponSet",
---"ToggleCombatMode" ...): `{ ok, event, key, modifier }` - that the key went in, not what the
---game did with it. UI side.
---@param event string
---@return table
function GK.Act.Press(event) end

---Takes the character's weapons out by a server order (Osi.SetWeaponUnsheathed) and waits for
---the game to agree. Server.
---@param who GK.Uuid?
function GK.Cheat.Draw(who) end

---Puts the character's weapons away by a server order and waits for the game to agree. Server.
---@param who GK.Uuid?
function GK.Cheat.Sheathe(who) end

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

---Records an Osiris event in the event log whenever it fires (kind `osiris`, payload { event,
---args }); once per event name. Observation. Server.
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
---@param hold number? @ seconds to keep the key down (released on a timer); GK.Key(name, hold) works too. A press alone is a tap: moving or turning the camera needs a hold
---@return true|{ key: string, held: number }
function GK.Key(name, modifiers, hold) end

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

---Text in lower case for matching, Latin-1, Greek and Cyrillic capitals included (string.lower
---leaves "Зелье" as it is); every name match of GK.Gui uses it. Client.
---@param text any
---@return string
function GK.Fold(text) end

---Names of the game commands available (QuickSaveCommand, SwitchWeaponSetCommand, ShortRest,
---GoToCamp, TogglePhotoModeCommand, ...). `wanted` narrows it to one view-model type; one that is
---not on screen (or a command name given in its place) is an error saying so. UI.
---@param wanted string? @ a view-model type, e.g. gui::DCCharacterLevelUp - not a command name
---@return string[]
function GK.Commands(wanted) end

---Executes a game command by name, with an optional parameter, e.g. GK.Command("QuickSaveCommand").
---Raises an error when the view model or command is missing, or when the game's own CanExecute
---says no. The answer says the command RAN, not that the game did anything: a command can be
---ignored by a gate the game keeps (photo mode with a dialogue open, a short rest out of reach) -
---read the effect. UI.
---@param name string
---@param parameter any?
---@param wanted string? @ view-model type that carries the command
---@return { ran: string, canExecute: boolean?, note: string } @ canExecute nil: the command could not be asked
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

---A fixed scale (stops following): any positive number, or "working" for GK.Speed.Working. From 16
---the answer's `warning` says the game is known to break there. Anything else is an error. Server.
---@param scale number|"working"
---@return GK.SpeedState
function GK.Speed.Set(scale) end

---The project's working speed (8): what tests run at besides 1.
---@type number
GK.Speed.Working = 8

---Each verb's class: "cheat" (setting the scale) or "observation".
---@type table<string, "cheat"|"observation">
GK.Speed.Class = {}

---@class GK.SpeedClock
---@field ticks integer @ the server's own tick count
---@field time number @ the game time in seconds
---@field delta number @ the game seconds of the last tick (the scale lengthens a tick)
---@field real integer @ Ext.Utils.MonotonicTime() of the last tick, ms
---@field scale number|nil

---The game's clock as the last server tick gave it. Two readings give ticks, game time and real
---time between them. Server. Observation.
---@return GK.SpeedClock
function GK.Speed.Clock() end

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
---@field warning string? @ Set: the game is known to break at this scale
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
-- Server/Rolls.lua  (server)

---Rolls decided in advance: the game's MinimumRollResult/MaximumRollResult boosts (Initiative: the
---Initiative boost) on the roller, each a track row of the stage ledger. docs/contracts/lua-api.md 17.
-- =========================================================================================
-- Shared/Memory.lua  (both sides, each owning its own watches; GateKit's Script Extender in developer mode)

---A field's place in memory (GK.Memory.Field).
---@class GK.MemoryField
---@field Address integer
---@field Offset integer
---@field ComponentAddress integer
---@field Type string
---@field Size integer? @ nil for a field with no single size (a vector, a transform)
---@field Scalar boolean

---A hit of a watch (GK.Memory.Hits).
---@class GK.MemoryHit
---@field Slot integer
---@field Owner "Server"|"Client"
---@field Thread integer
---@field Before any @ the value before the access
---@field After any
---@field Changed boolean @ false for a read
---@field Frame integer @ the side's ECS frame (the tracer's counter)
---@field Flush integer @ the side's command-buffer flush (a write stamped N shows in the trace in N + 1)
---@field Poll integer @ the side's extender ticks
---@field Time number
---@field NextInstruction { Module: string, RVA: integer, Address: integer } @ the instruction after the access
---@field Instruction { Module: string, RVA: integer, Address: integer } @ the same, the older name
---@field Return table[]
---@field Of { entity: string, component: string, field: string?, offset: integer? }? @ what a followed watch's hit is of
---@field Moved boolean? @ may have come after its component moved: not a hit of the field (no Of)
---@field Now integer? @ the place's address then, nil when its component is gone

---The memory instrument: read, watch the writer of, and hold a field. docs/contracts/lua-api.md, section 16.
---@class GK.Memory
---@field Class table<string, "cheat"|"observation">
GK.Memory = {}

---@return boolean
function GK.Memory.Available() end
---@param entity string|EntityHandle
---@param component string @ the engine name, eoc::HealthComponent
---@param field string
---@return GK.MemoryField
function GK.Memory.Field(entity, component, field) end
---@param name string @ a system's engine name
---@return integer?
function GK.Memory.System(name) end
---Where a value below an object's top level lies and how wide it is ("Streams[2].State.NumRolls"). Observation.
---@param object any @ a reference to an object of the game
---@param path string
---@return { Address: integer, Size: integer, Offset: integer?, ObjectAddress: integer?, Path: string }?, string?
function GK.Memory.Path(object, path) end
---@return integer
function GK.Memory.Element(bufferAt, index, elementSize, offset) end
---The followed watches whose place moved, by the slot each had.
---@return table<integer, { was: integer, now: integer?, did: "rearmed"|"closed", armed: integer?, why: string?, entity: string, component: string, field: string? }>
function GK.Memory.Moved() end
---@return any
function GK.Memory.Read(address, type) end
---@return { Module: string, RVA: integer, Base: integer, Size: integer }?
function GK.Memory.At(address) end
---@return integer
function GK.Memory.FromModule(module, rva) end
---A cheat: writes a scalar; refused on a code page.
function GK.Memory.Write(address, type, value) end
---Arms a watch on writes (with readWrite, on reads too; size 1, 2, 4 or 8). Returns the slot 1-4 or nil.
---A field's watch, and an address's given its owner, follow their component when its storage moves.
---@param address integer|GK.MemoryField
---@param size integer?
---@param readWrite boolean?
---@param owner { entity: GK.Uuid|any, component: string }? @ the component a raw address lies in
---@return integer?
function GK.Memory.Watch(address, size, readWrite, owner) end
---The watches armed now: this side's, or both with `all`; this side's say what they follow, or Raw.
---@param all boolean?
---@return { Slot: integer, Owner: string, Address: integer, Size: integer, ReadWrite: boolean, Value: any, Follows: { entity: string, component: string, field: string?, offset: integer? }?, Raw: boolean? }[]
function GK.Memory.Watches(all) end
---How many hits of this side wait to be read.
---@return integer
function GK.Memory.Waiting() end
---Drains this side's hits since the last call.
---@return GK.MemoryHit[]
function GK.Memory.Hits(max) end
---Clears one watch of this side, or all of them.
---@return boolean
function GK.Memory.ClearWatch(slot) end
---A cheat: holds an address at a value, rewritten by the extender every tick of this side.
---@return integer?
function GK.Memory.Freeze(address, type, value) end
---A cheat: lets one frozen address of this side go, or every one.
function GK.Memory.Unfreeze(slot) end
---@param all boolean?
---@return { Slot: integer, Owner: string, Address: integer, Size: integer }[]
function GK.Memory.Frozen(all) end
---A cheat: every watch and freeze of this side gone.
---@return { watches: integer, freezes: integer }
function GK.Memory.Clear() end

-- =========================================================================================
-- Shared/Trace.lua  (both sides, each its own tracer; GateKit's Script Extender in developer mode)

---@class GK.TraceOptions
---@field entities (GK.Uuid|EntityHandle)[]? @ only these (none: all)
---@field components string[]? @ engine names or Lua labels (none: all)
---@field systems boolean? @ the system that changed a component (needs both filters)
---@field modifications boolean? @ writes to a component
---@field limit integer? @ events kept per entity and component (16; at most 4096)
---@field step string? @ the first step's name

---@class GK.TraceStep
---@field ok boolean
---@field step string?
---@field entities integer
---@field changes integer
---@field written integer
---@field systems integer
---@field systemsWritten integer
---@field frame integer
---@field flush integer
---@field rows table[] @ the payloads of the step's trace_change and trace_system rows

---The entity tracer as rows of the event log's `trace` family. docs/contracts/lua-api.md 16.1.
---@class GK.Trace
GK.Trace = {}
---@return boolean
function GK.Trace.Available() end
---@param options GK.TraceOptions?
---@return { ok: boolean, side: string?, frame: integer?, flush: integer?, error: string? }
function GK.Trace.Begin(options) end
---@param name string @ the step that begins
---@return GK.TraceStep
function GK.Trace.Step(name) end
---@return GK.TraceStep
function GK.Trace.End() end
---Whether a trace runs: `step` the step under way (nil when none runs), `last` the step that ended last.
---@return { on: boolean, step: string?, last: string?, options: GK.TraceOptions? }
function GK.Trace.Status() end

---@class GK.Rolls
---@field Class table<string, "cheat"|"observation"> @ each verb's class
GK.Rolls = {}

---@class GK.RollPinSpec
---@field who GK.Uuid? @ any creature with boosts (default the host)
---@field kind string @ one of GK.Rolls.Kinds()
---@field n integer @ the die (1..20; a damage kind: every die, at least 1; Initiative: a bonus)
---@field once boolean? @ the pin goes after the holder's next finished roll of the kind (checks and saving throws)
---@field karmic boolean? @ true: Karmic Dice is left as it is (it forces successes above the pin); else switched off for the scope

---@class GK.RollPin
---@field ok boolean
---@field who GK.Uuid
---@field kind string
---@field n integer
---@field once boolean?
---@field boost string @ the boost text added
---@field track integer @ seq of its scope_track row
---@field inForce boolean @ the boost is on the creature
---@field karmic string? @ "switched off for the scope" when the pin switched Karmic Dice off
---@field ticks integer? @ server ticks from the call until it was
---@field error string?

---Pins the natural die of who's own rolls of kind at n. Refused outside a stage scope, for an
---unknown kind (the error lists the kinds), for once on a kind the roll system does not finish as
---a roll of its own. A pin of the same who and kind is replaced (of the same n: kept). Switches
---Karmic Dice off for the scope unless spec.karmic. Observed: answers once the boost is on and the
---roll manager no longer weights. Server. Cheat.
---@param spec GK.RollPinSpec
---@return GK.RollPin
function GK.Rolls.Pin(spec) end

---Takes the pins of who (default the host) and kind (default every kind) off; observed until the
---boosts have left. Server. Cheat.
---@param spec { who: GK.Uuid?, kind: string? }?
---@return { ok: boolean, who: GK.Uuid, kind: string?, removed: integer, ticks: integer?, error: string? }
function GK.Rolls.Unpin(spec) end

---What is pinned now, read from the creatures' boosts; tracked = false: undone, not gone yet.
---Server. Observation.
---@return { who: GK.Uuid, kind: string, n: integer, once: boolean?, boost: string, track: integer?, tracked: boolean }[]
function GK.Rolls.Pins() end

---The roll kinds: the game's StatsRollType without None and Sentinel, then Initiative. Server. Observation.
---@return string[]
function GK.Rolls.Kinds() end

---@class GK.Karmic
---@field on boolean @ the roll manager rolls WeightedRandom
---@field option boolean? @ the game's option, GlobalSwitches.WeightedRolls
---@field mode string? @ the roll manager's RollModeType (follows the option)
---@field saved string? @ the roll streams component's RollModeType (does not follow it)

---Karmic Dice now. Server. Observation.
---@return GK.Karmic
function GK.Rolls.Karmic() end

---Switches Karmic Dice for the length of the stage scope (Clean writes the old value back);
---observed until the roll manager follows. Server. Cheat.
---@param on boolean
---@return GK.Karmic|{ ok: boolean, was: boolean, ticks: integer? }
function GK.Rolls.SetKarmic(on) end

---The party's inspiration points (the InspirationPoint action resource). Server. Observation.
---@return { points: integer?, max: integer?, holders: integer }
function GK.Rolls.Inspiration() end

---Sets the inspiration points (0..max, the resource definition's MaxValue) for the length of the
---stage scope (Clean writes the old value back); refused outside a scope. Server. Cheat.
---@param n integer
---@return { ok: boolean, points: integer?, was: integer?, max: integer?, error: string? }
function GK.Rolls.SetInspiration(n) end

---The server's roll streams: the roll manager's base seed and every stream's seed, count of dice
---drawn and debts (`saved`: the same of the roll streams component). Server. Observation.
---@return { base: integer?, frame: integer?, mode: string?, streams: { type: string, seed: integer, rolls: integer, frame: integer, failDebt: number, successDebt: number, player: string?, combat: string?, cast: string? }[], saved: table? }
function GK.Rolls.Streams() end

---Writes the seed and count of the named roll streams for the stage scope (Clean writes them back);
---an earlier Streams() reading rewinds the dice. Server. Cheat.
---@param list { type: string, player: string?, combat: string?, seed: integer?, rolls: integer? }[]|table
---@return { ok: boolean, written: table[], missing: table[]? }
function GK.Rolls.SetStreams(list) end

---The dice a roll stream gives to its next roll, computed from its seed and count and not drawn
---(dice: how many, default the pair of a d20 roll; faces: default 20; rolls: another count). Server. Observation.
---@param spec { type: string, player: string?, combat: string?, dice: integer?, faces: integer?, rolls: integer? }
---@return { ok: boolean, type: string?, player: string?, combat: string?, seed: integer?, rolls: integer?, dice: integer[]?, error: string? }
function GK.Rolls.Next(spec) end

---Chooses the natural die of the next roll drawn from a roll stream, for the stage scope: moves the
---stream's count to the nearest one that gives `die` (with `both`, on every die of the roll). Server. Cheat.
---@param spec { type: string, player: string?, combat: string?, die: integer, both: boolean?, dice: integer?, faces: integer? }
---@return { ok: boolean, type: string?, player: string?, combat: string?, seed: integer?, rolls: integer?, was: integer?, dice: integer[]?, error: string? }
function GK.Rolls.SetNext(spec) end

---The oldest finished roll of who not yet joined to a RollResult (take removes it). Server. Observation.
---@param who GK.Uuid
---@param take boolean?
---@return { who: GK.Uuid, kind: string, ability: string?, skill: string?, dc: integer, natural: integer, bonus: integer, total: integer?, critical: string?, advantage: string?, modifiers: { name: string, value: integer }[]?, pinned: integer?, tick: integer }?
function GK.Rolls.Finished(who, take) end

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
---casts, fights, turns, items, gold, quests, rolls, approval, level-ups: the `game` family of the
---event log (kinds game_<kind>). Observation. Server.
---@class GK.GameLog
---@field Saved GK.GameLogEntry[] @ the entries a loaded save carried, before this boot's rows
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

---Entries after the row `n` (0: the saved ones too). Observation. Server.
---@param n integer?
---@param options { all: boolean?, kinds: table<string, boolean>? }? @ all: also what the party did not perceive
---@return { last: integer, entries: GK.GameLogEntry[], dropped: integer? }
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

---Everything that bends the game's rules, recorded in Log on every call: a catalogue naming
---GK.Stage.*, GK.Teleport, GK.Give ...; the scripted actions of Server/Act.lua (Move, Cast,
---Attack ... - the same names as GK.Act's); on the client the window verbs that reach past a
---player's controls (Run, Set, Step, Auto, Quiet, Recover, AimBest).
---@class GK.Cheat
---@field Log { name: string, t: number }[]
---@field Class table<string, "cheat"|"observation">
---@field Logged table
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
-- Shared/Verbs.lua  (both): the class of every verb, and the rule a step is held to

---@alias GK.VerbClass "fair"|"cheat"|"observation"

---@class GK.Verbs
---@field Verbs table<string, { class: GK.VerbClass, why: string? }> @ Shared/VerbClasses.json's verbs
---@field Namespaces table<string, { class: GK.VerbClass, why: string? }>
---@field Scope GK.VerbClass? @ the class of the chunk running now
GK.Verbs = {}

---The class of a path under GK ("Act.Select" or "GK.Act.Select") and why, or nil.
---@param path string
---@return GK.VerbClass?, string?
function GK.Verbs.Of(path) end

---Every classed path, or those whose path contains `filter` or whose class it is.
---@param filter string?
---@return { verb: string, class: GK.VerbClass, why: string? }[]
function GK.Verbs.List(filter) end

---Puts the class check on every registered function of this side; sets GK.<Namespace>.Class.
---@return integer
function GK.Verbs.Guard() end

---Runs fn with the verbs it calls held to `class`: ok, value, refused (the first refusal).
---@param class GK.VerbClass?
---@param fn fun(): any
---@return boolean, any, string?
function GK.Verbs.Within(class, fn) end

---Reads the registry (default: the mod's Shared/VerbClasses.json).
---@param text string?
---@return true
function GK.Verbs.Load(text) end

---A row of kind `step`: a test's step begins. Its seq.
---@param name string
---@param class GK.VerbClass
---@return integer?
function GK.Step(name, class) end

---A row of kind `step_ended`: outcome pass, fail or unmet; ms on the host. Its seq.
---@param name string
---@param class GK.VerbClass
---@param outcome "pass"|"fail"|"unmet"
---@param ms number
---@param why string?
---@return integer?
function GK.StepEnded(name, class, outcome, ms, why) end

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

---Creates characters or items. Equipment is put on a moment later: GK.Stage.Ready(uuids) tells when.
---When the game creates nothing the error says why it can tell: no such root template in this
---game (a short name of GK.Stage.Templates included), or a template that exists and a place it
---would not create at. Each point is moved to the game's nearest valid position within 4 m
---(Osi.FindValidPosition) - a ring at the centre's height lies inside hills - and refused when there
---is none; `exact = true` in the spec creates at the point as given. Server.
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
---@return { gameState: string, level: string?, hostDead: boolean, inCombat: boolean, inDialog: boolean? } @ inDialog nil: it cannot be read (GK.InDialog)
function GK.Stage.State() end

---The party's characters (uuids). Server.
---@return GK.Uuid[]
function GK.Stage.Party() end

---Makes the party unkillable (or mortal again). Server.
---@param on boolean
function GK.Stage.Protect(on) end

---Starts a fight between `enemies` and a character (default: the host); the party is made
---unkillable unless `mortal`. Without `enemies` it raises an error naming it; with the host in a
---dialogue it refuses (the game starts no fight then). The fight may begin a moment later:
---EnterCombat is repeated for up to 15 s. Server.
---@param enemies GK.Uuid[]
---@param who GK.Uuid?
---@param mortal boolean?
---@return { enemies: integer, inCombat: boolean, note: string } @ inCombat: already, at the time of the call
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

---A character casts a spell on a target (uuid) or at a position. The character must know the spell.
---Cast(who, spell, target), the order of GK.Act.*, is understood. A name that is not a spell, or a
---target the game will not take (a position for a spell that wants a character), is an error that
---says so; nothing is cast. The answer is { asked, known, note } - asked, not done: whether it was cast
---shows in GK.Events; a spell GK.Stage.Teach gave a moment ago is cast once the book has it. Server.
---@param spell string
---@param target GK.Uuid|GK.Pos
---@param who GK.Uuid?
function GK.Stage.Cast(spell, target, who) end

---Adds a spell to a character. A name that is not a spell in the game's stats is an error, and
---nothing is taught. Server.
---@param spell string @ a stats name, e.g. Projectile_FireBolt
---@param who GK.Uuid?
---@return { spell: string, who: GK.Uuid, new: boolean } @ new: the character did not know it before
function GK.Stage.Teach(spell, who) end

---Uses an item (uuid, or a template name of something in the inventory). Server.
---@param item string
---@param who GK.Uuid?
function GK.Stage.Use(item, who) end

---Applies a status for a number of turns (-1: forever). Status(who, status, turns), the order of
---GK.Act.*, and Status(status, turns) are understood; a form that cannot be read either way is
---refused with the signature. Unstatus takes the same orders. Server.
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

---Deals damage of a type (default "Bludgeoning"). Damage(who, amount) is understood too; any
---other form is refused with the signature. Damage that could down the last party member standing
---is refused unless `anyway` (the game-over box follows, pauses the game and stays after healing;
---a reset to the checkpoint or a load takes it away); GK.Stage.Protect() first avoids it. Server.
---@param amount number
---@param who GK.Uuid?
---@param kind string?
---@param anyway boolean? @ down the last party member standing all the same
---@return { who: GK.Uuid, asked: number, kind: string, hpBefore: number, hpAfter: number, dealt: number, row: integer?, hit: number?, note: string? } @ observed: `row` is the game's own game_hit row of the hit
function GK.Stage.Damage(amount, who, kind, anyway) end

---Sets hit points (default: to the maximum). Heal(hp, who) is understood too. Answers once the
---game holds them (it sets them a tick later). Server.
---@param who GK.Uuid?
---@param hp number?
---@return { who: GK.Uuid, asked: number, hpBefore: number, hpAfter: number, note: string? }
function GK.Stage.Heal(who, hp) end

---Kills a character. Refused for the last party member standing unless `anyway` (as Damage). Server.
---@param who GK.Uuid?
---@param anyway boolean?
function GK.Stage.Kill(who, anyway) end

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

---Locks (locked false: unlocks) an item without a key, with the key "NOKEY". The game applies it
---on a later tick: `locked` is the same-tick reading, and a `note` says so when it differs from
---`asked`. Server.
---@param item GK.Uuid
---@param locked boolean? @ default true
---@return { asked: boolean, locked: boolean, note: string? }
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
---@return { queued: true, picked: false, locked: boolean, note: string }|false, string? @ queued, not picked: the roll is the client's; GK.Act.Lockpick reports the end
function GK.Stage.PickLock(item, who, tool) end

---Disarms a trap (armed, discovered, disarmable). Server.
---@param trap GK.Uuid
---@param who GK.Uuid?
---@param tool GK.Uuid?
---@return { queued: true, disarmed: false, note: string }|false, string? @ queued, not disarmed; GK.Cheat.Disarm reports the end
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

---Takes away what holds a scene up on the server side and says what it did; what holds it and is
---the client's (a dialogue) is named as a line starting "NOT DONE:". Server.
---@return string[]
function GK.Stage.Unblock() end

---Removes an object from the game (characters are taken off the stage first). Server.
---@param id GK.Uuid
function GK.Stage.Remove(id) end

---Ends the host's fight, removes what the scene created, takes its statuses off and puts factions
---back. Returns how many objects were removed. Server.
---@return integer
function GK.Stage.Reset() end

---Opens the stage scope: the world now is the baseline GK.Stage.Clean returns to (a baseline of
---this level is kept unless options.fresh). Server.
---@param options? { fresh?: boolean, radius?: number }
---@return { kept: boolean, level: string, counts: table, party: integer, near: integer, ms: number }
function GK.Stage.Begin(options) end

---Takes everything back to the baseline; `did` says every change taken back, notUndone what could not be. Server.
---@return { did: string[], removed: table, notUndone: string[], sideEffects: string[], ms: number }
function GK.Stage.Clean() end

---Compares the world with the baseline. A boost added through Osiris to a party member or a staged
---creature is named, and whether the ledger tracks it; every action resource, cooldown, the drawn
---weapon, concentration and fight of a party member are named when they differ; `pending`: only a
---moment's matter is left (deletions not done, boosts undone and not gone, the game's own work
---under way); `kept`: components the game made and keeps at their default (Server/GameKeeps.json),
---not dirt; `busy`: the passing components each member holds. Server.
---@return { clean: boolean, differences: string[], pending: boolean, kept: string[], busy: string[], ms: number }
function GK.Stage.Verify() end

---Gives a creature its turn's resources back inside a turn (a cheat, through the ledger): each of
---action, bonus, movement, reaction, slots true (to its maximum) or a number; none named: the four
---turn resources. Answers once the server reads the amounts back (a `stage_refill` row).
---@param spec { who: string?, action: boolean|number?, bonus: boolean|number?, movement: boolean|number?, reaction: boolean|number?, slots: boolean|number? }?
---@return { ok: boolean, who: string, changed: { name: string, level: integer?, from: number, to: number }[], ticks: integer?, error: string? }
function GK.Stage.Refill(spec) end

---The one writer of action resources: amount (and MaxAmount) of one level of a resource, replicated.
---@return boolean
function GK.Stage.WriteResource(entity, guid, index, amount, max) end

---An action resource definition's guid by its name (ActionPoint, Movement, InspirationPoint ...).
---@param name string
---@return string?
function GK.Stage.ResourceId(name) end

---The server's preview casts by caster uuid: { uuid = { spell ... } }.
---@return table<string, string[]>
function GK.Stage.PreviewCasts() end

---The facing of an object about the vertical axis, in degrees (0..360).
---@return number?
function GK.Stage.Yaw(who) end

---The data of Server/GameKeeps.json: the components the game keeps (`kept`: how each is read, its
---default, why) and those it removes by itself (`passing`). Server.
---@return { kept: table<string, { lua: string, empty: string?, default: table?, why: string }>, passing: table<string, { why: string }> }
function GK.Stage.GameKeeps() end

---Puts back on a party member, from its baseline `was`, the cooldowns, every action resource and
---the drawn weapon; a turn resource with a refill coming and a weapon the game is putting back are
---left to the game. Used by Clean. Server.
---@param id GK.Uuid
---@param was table
---@param now table
---@return string[]
function GK.Stage.RestoreMember(id, was, now) end

---Puts the game's relation tables (the server's relation component) back as a baseline held them:
---entries added are deleted, changed and lost ones written back. Used by Clean. Server.
---@param was table
---@return string[]
function GK.Stage.RestoreRelations(was) end

---Notes that the stage or a fixture created `id` in this tick. Server.
---@param id GK.Uuid
function GK.Stage.NoteBorn(id) end

---When `id` was created in this tick, runs fn(id) on a later tick and answers true (a character
---created and deleted in one tick closes the game). Server.
---@param id GK.Uuid
---@param fn fun(id: GK.Uuid)
---@return boolean
function GK.Stage.Unborn(id, fn) end

---The boosts of an object added through Osiris (cause type Story): { { text, cause } }, text as
---"Initiative(3)". Server. Observation.
---@param id GK.Uuid
---@return { text: string, cause: string }[]
function GK.Stage.StoryBoosts(id) end

---What is in the level besides the party. Server.
---@return { level: string, characters: integer, items: integer, party: integer }
function GK.Stage.Census() end

---The ledger's public hook: what is registered here is undone by GK.Stage.Clean. Server.
---@param spec { kind: "spawn"|"status"|"item"|"surface"|"other", entity?: GK.Uuid, undo?: fun(): boolean?, string?, note?: string }
---@return boolean
function GK.Stage.Track(spec) end

---A test's own undo, run by GK.Stage.Clean. Server.
---@param fn fun(): boolean?, string?
---@param what? string
---@return boolean
function GK.Stage.OnClean(fn, what) end

---Sets (on ~= false) or clears a story flag; Clean puts the old value back. Server.
---@param flag string
---@param on? boolean
---@param who? GK.Uuid
---@return integer?
function GK.Stage.Flag(flag, on, who) end

---Adds an Osiris fact; Clean deletes it again. Server.
---@param db string
---@return boolean
function GK.Stage.Fact(db, ...) end

---Whether an object was there when the baseline was taken. Server.
---@param id GK.Uuid
---@return boolean
function GK.Stage.ExistedBefore(id) end

-- =========================================================================================
-- Server/Situation.lua  (server): GK.See - the fair reading API

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

---Whether member `viewer`'s own sight holds `target` now (the sight system's set of its observers). Server.
---@param viewer GK.Uuid
---@param target GK.Uuid|any
---@return boolean
function GK.See.Perceives(viewer, target) end

---Whether the party is shown `target`, by GK.See.Perception's one rule (on the host's level, on
---stage, a member or without an Invisibility component). Every row's `knows` reads it. Server.
---@param target GK.Uuid|any
---@param isCharacter boolean?
---@param members GK.Uuid[]?
---@return boolean
function GK.See.PartyPerceives(target, isCharacter, members) end

---Whom the party perceives, by the game's own state, as a player knows it: a creature is perceived
---unless it carries the Invisibility component and is not a party member (then the client does not
---show it); `sight` lists the members whose sight holds it now (the sight system's set:
---ServerViewshedParticipant.CanSee). Lists only the perceived, nearest first; never names a hidden
---one. options: target (uuid: one answer), who (a member: whose sight counts), radius (30), dead.
---Server, observation.
---@param options { target: GK.Uuid?, who: GK.Uuid?, radius: number?, dead: boolean? }?
---@return { ok: boolean, perceived: boolean|{ uuid: GK.Uuid, name: string?, rel: string, d: number, sight: GK.Uuid[] }[], uuid: GK.Uuid?, sight: GK.Uuid[]?, error: string? }
function GK.See.Perception(options) end

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
-- Server/Act.lua  (server): GK.Act, what a player does (fair or observation), and GK.Cheat's
-- scripted actions of the same names (Osiris commands that skip rolls, costs or checks).
-- docs/contracts/lua-api.md section 19.

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

---What GK.Act.<name> answers for an action not yet built on the player's own path: nothing is done.
---@class GK.ActRefusal
---@field ok false
---@field error string @ names the player's route and the scripted twin
---@field fair string @ the player's route (GK.Gui.UseSlot, Aim, Confirm ...)
---@field cheat string @ "GK.Cheat.<name>"

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

---What a player does through the game itself, and readings; every verb fair or observation. An
---action not built on the player's path answers `GK.ActRefusal`. Server.
---@class GK.Act
---@field Class table<string, "fair"|"observation"> @ each verb's class (Shared/VerbClasses.json)
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
function GK.Cheat.Charge(who, name, amount, level) end

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
function GK.Cheat.Unstick(who) end

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
function GK.Cheat.Move(who, to, options) end

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
function GK.Cheat.Cast(who, spell, target, options) end

---The main-hand weapon attack. Server.
---@param who GK.Uuid?
---@param target GK.Uuid
---@param options { mode: "melee"|"ranged"? }?
---@return GK.ActResult
function GK.Cheat.Attack(who, target, options) end

---@param who GK.Uuid?
---@param point GK.Pos
---@return GK.ActResult
function GK.Cheat.Jump(who, point) end

---@param who GK.Uuid?
---@param target GK.Uuid
---@return GK.ActResult
function GK.Cheat.Shove(who, target) end

---@param who GK.Uuid?
---@return GK.ActResult
function GK.Cheat.Hide(who) end

---@param who GK.Uuid?
---@return GK.ActResult
function GK.Cheat.Dash(who) end

---@param who GK.Uuid?
---@return GK.ActResult
function GK.Cheat.Disengage(who) end

---@param who GK.Uuid?
---@param target GK.Uuid
---@return GK.ActResult
function GK.Cheat.Help(who, target) end

---@param who GK.Uuid?
---@param item GK.Uuid
---@param target GK.Uuid|GK.Pos
---@return GK.ActResult
function GK.Cheat.Throw(who, item, target) end

---Use an item the party carries (a potion, a scroll, a note): no walking. Server.
---@param who GK.Uuid?
---@param item GK.Uuid
---@return GK.ActResult
function GK.Cheat.UseCarried(who, item) end

---Walk up and use: open/close a door or chest, pull a lever, climb a ladder, read, drink. Server.
---@param who GK.Uuid?
---@param item GK.Uuid
---@return GK.ActResult
function GK.Cheat.Use(who, item) end

---Walk up and take it. Server.
---@param who GK.Uuid?
---@param item GK.Uuid
---@return GK.ActResult
function GK.Cheat.PickUp(who, item) end

---Walk up and open a corpse's or container's loot window. Server.
---@param who GK.Uuid?
---@param corpse GK.Uuid
---@return GK.ActResult
function GK.Cheat.Loot(who, corpse) end

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
function GK.Cheat.Take(who, container, item) end

---Walk up and ask to talk, as clicking on the NPC does. Server.
---@param who GK.Uuid?
---@param npc GK.Uuid
---@return GK.ActResult
function GK.Cheat.Talk(who, npc) end

---Makes a party member the one the player controls. Server.
---@param who GK.Uuid
---@return GK.ActResult
function GK.Act.Select(who) end

---Ends the selected character's turn; `viaUi` only checks and answers { ok, ui = "EndTurn" }. Server.
---@param who GK.Uuid?
---@param viaUi boolean?
---@return GK.ActResult
function GK.Act.EndTurn(who, viaUi) end

---Opens the trade window with a trader as the player's Trade does: walks up to it, asks the game
---(Osi.RequestTrade) and is done when the character carries CharacterTrading. Refused for a dead,
---hostile or non-trading character (Osi.CanTrade); GK.Stage.Scout{kind = "traders"} lists those
---that trade. A trader the party has not seen is refused with its distance: GK.Act walks only to
---what the party sees (as every GK.Act call on an object). Then GK.Gui.Trade on the UI side. Server.
---@param who GK.Uuid?
---@param trader GK.Uuid
---@return GK.ActResult @ a job; its result is { trading = true, ui = "Trade" }
function GK.Cheat.Trade(who, trader) end

---Flee the fight: the check only - nothing is done. When allowed the answer is { ok, allowed, fled =
---false, ui = "Flee", note }: GK.Command("Flee") on the UI presses the button, and the game may
---still keep the fight, so read Osi.IsInCombat afterwards. Server.
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
function GK.Cheat.Sneak(on, who) end

---Equips an item the character carries: refused at once for an item that is not worn or
---wielded (no Equipable component; Osi.Equip ignores those); otherwise a job that ends when the
---game shows it equipped, or fails after 3 s saying the game did not.
---@param who GK.Uuid?
---@param item GK.Uuid
---@return GK.ActResult
function GK.Cheat.Equip(who, item) end

---@param who GK.Uuid?
---@param item GK.Uuid
---@return GK.ActResult
function GK.Cheat.Unequip(who, item) end

---Whether this Script Extender build can queue lock picking and disarming safely. Server.
---@return boolean
function GK.Act.CanPickLocks() end

---Picks a lock as the context menu does (needs Thieves' Tools). The answer's `waits` says what the
---job waits for (the walk, the dice window on the client); GK.Act.Job(n).step says it while it
---runs, and a failure says whether the engine ever took the attempt up. Server.
---@param who GK.Uuid?
---@param item GK.Uuid
---@return GK.ActResult
function GK.Act.Lockpick(who, item) end

---Disarms a discovered trap with a Trap Disarm Toolkit. Server.
---@param who GK.Uuid?
---@param trap GK.Uuid
---@return GK.ActResult
function GK.Cheat.Disarm(who, trap) end

---Immersive mode: a line the acting character says (shown as a notification) and a plain reason;
---both kept in GK.Act.Narration. Changes nothing in the world. Server.
---@param text string
---@param why string?
---@param who GK.Uuid?
---@return GK.ActDone|GK.Failure
function GK.Cheat.Say(text, why, who) end

---Stops what the character is doing (walk, cast queue); running jobs fail with "stopped". Server.
---@param who GK.Uuid?
---@return { ok: true }
function GK.Cheat.Stop(who) end

---What GK.Act.Cast and Attack answer on the UI side once the game has finished the cast.
---@class GK.ActCastDone
---@field ok boolean
---@field did string
---@field spell string
---@field caster GK.Uuid?
---@field target GK.Uuid?
---@field cast string? the cast's guid, as GK.Gui.Confirm saw it start
---@field result string? the game's SpellCastFailReason of the finished cast ("None": it went through)
---@field outcomes { target: GK.Uuid?, outcome: "miss"|"critical miss"|"hit"|"critical"|"none", flags: string }[]?
---@field shown { hit_chance: integer?, damage_min: integer?, damage_max: integer?, advantages: string[]?, disadvantages: string[]? }? what the cursor showed
---@field ticks integer client ticks the verb waited
---@field saw string?
---@field error string?

---UI side: the hotbar's slot of `spell`, the aim at `target` (a uuid or a point), the click,
---answered when the game has finished the cast (its finished-cast event on the client). What the
---game refuses is answered and nothing is done. Fair. On the server: refused, naming this.
---@param spell string
---@param target GK.Uuid|number[]
---@param options table?
---@return GK.ActCastDone|GK.ActRefusal
function GK.Act.Cast(spell, target, options) end
---UI side: GK.Act.Cast with the main-hand attack (`options.spell` for another attack action). Fair.
---On the server: refused, naming this.
---@param target GK.Uuid|number[]
---@param options { spell: string? }?
---@return GK.ActCastDone|GK.ActRefusal
function GK.Act.Attack(target, options) end

---The actions of GK.Act not built on the player's own path: each answers `GK.ActRefusal` naming the
---route through GK.Gui and GK.Cheat.<same name>. Nothing is done. Server.
---@return GK.ActRefusal
function GK.Act.Jump() end
---@return GK.ActRefusal
function GK.Act.Shove() end
---@return GK.ActRefusal
function GK.Act.Hide() end
---@return GK.ActRefusal
function GK.Act.Dash() end
---@return GK.ActRefusal
function GK.Act.Disengage() end
---@return GK.ActRefusal
function GK.Act.Help() end
---@return GK.ActRefusal
function GK.Act.Throw() end
---@return GK.ActRefusal
function GK.Act.Move() end
---@return GK.ActRefusal
function GK.Act.Use() end
---@return GK.ActRefusal
function GK.Act.UseCarried() end
---@return GK.ActRefusal
function GK.Act.PickUp() end
---@return GK.ActRefusal
function GK.Act.Loot() end
---@return GK.ActRefusal
function GK.Act.Take() end
---@return GK.ActRefusal
function GK.Act.Talk() end
---@return GK.ActRefusal
function GK.Act.Trade() end
---@return GK.ActRefusal
function GK.Act.Equip() end
---@return GK.ActRefusal
function GK.Act.Unequip() end
---@return GK.ActRefusal
function GK.Act.Sneak() end
---@return GK.ActRefusal
function GK.Act.Disarm() end
---@return GK.ActRefusal
function GK.Act.Stop() end

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

---The controls of an open widget. filter: part of a name or text, or a table. When it shows none
---while another window waits over it (the tutorial box over character creation) the answer is a
---failure naming that window (`coveredBy`) rather than an empty list. UI.
---@param name string
---@param filter string|{ name: string?, text: string?, data: string?, all: boolean? }|nil
---@return GK.GuiControlRow[]|GK.Failure
function GK.Gui.Controls(name, filter) end

---Clicks a control of an open widget as a player does (the XAML chain). A disabled control is a
---failure with `state`: the flags, numbers and short texts of the control's data, which usually
---show why (a count at 0, a flag off). UI.
---@param name string
---@param what string|integer|table @ name, part of its text, filter table, or number in GK.Gui.Controls
---@param which integer? @ the n-th match (default 1)
---@return table
function GK.Gui.Click(name, what, which) end

---Selects an item of a list (ListBox, tab strip...) of an open widget. UI. The items are counted
---from the list's Items, else its ItemsSource (the view model's collection), and an item's text is
---read as the player reads it, so a race or a class can be chosen by its name.
---@param name string
---@param list string @ the list's x:Name
---The items are counted by `#`, else Count, else by reading them until the first gap (a Noesis
---collection has no length operator). An item read from the view model selects that item.
---What the list reads afterwards decides: a game handler that fails after the selection is made
---gives ok with `warning`.
---@param item integer|string|userdata @ 1-based number, part of its text, or the item itself
---@return table
function GK.Gui.Select(name, list, item) end

---What a key does in a widget ("UIAccept", "UICancel", "UITakeAll"...). UI.
---@param name string
---@param event string
---@return table
function GK.Gui.Input(name, event) end

---Runs a command of a widget's data by path; string/number/boolean parameters are boxed. A
---command takes ONE parameter (the game's ICommand). A path that leads to a value instead of a
---command is refused, naming GK.Cheat.Set; a command the game's CanExecute turns down is refused.
---ok means it ran, not that it did anything - read the effect. UI.
---@param name string
---@param path string
---@param parameter any?
---@return table
function GK.Cheat.Run(name, path, parameter) end

---Sets a property of a widget's data by path through the view model's SetProperty, and reads it
---back: ok is false when the game keeps its own value. A userdata value is not compared
---(`checked = false`). UI.
---@param name string
---@param path string @ "FieldOfView", "SelectedRace", "CurrentPlayer.Something"
---@param value any
---@return { ok: boolean, before: any, after: any, checked: boolean?, error: string? }
function GK.Cheat.Set(name, path, value) end

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
function GK.Cheat.Step(name, options) end

---Runs GK.Cheat.Step on a timer until done/stuck/limit; state is kept in GK.Gui.AutoState[name]. UI.
---@param name string
---@param options { limit: integer?, every: integer? }?
---@return { ok: true, started: true }
function GK.Cheat.Auto(name, options) end

---The view model of an item of the selected character by part of its name, or nil. UI.
---@param text string
---@return any|nil
function GK.Gui.Item(text) end

---Does to an item what the player does: "equip", "unequip", "use", "drop". The item is found in the
---selected character's inventory, its equipment and the containers it carries (the alchemy pouch).
---ok says the game's command ran, not what it did (`did`): that shows a moment later. A "use"
---closes a Combine window left open first (`closedFirst`). UI.
---@param action "equip"|"unequip"|"use"|"drop"
---@param text string
---@return table
function GK.Gui.ItemDo(action, text) end

---Tutorials off (on = false turns them back on). UI.
---@param on boolean?
---@return table
function GK.Cheat.Quiet(on) end

---Replaced by GK.Gui.Aim: answers ok = false and names it (the picking helper's TargetOverride is
---read by neither the picking nor the spell targeting). Client.
---@param target string|number[]
---@param player integer?
function GK.Focus.Aim(target, player) end

---Confirms the pending targeting: the spell the hotbar is targeting (or `spell`), cast at `target`
---(uuid or {x, y, z}), else at the aim of GK.Focus.Aim, else at the pointer's pick - asked of the
---server as GK.Cheat.Cast, then the client's targeting is left with Escape. Client.
---@param target string|number[]?
---@param spell string?
function GK.Focus.Confirm(target, spell) end

---The bonuses the roll window offers (the roller's and nearby party members', the roller among
---them): { ok, bonuses = { {n, name, spell, dice, value, by, from, selected, off} } } - spell: the
---giver's spell (PrototypeID), dice: "1d4", value: { low, high } it adds, by: the giver's uuid. UI.
function GK.Gui.RollBonuses() end

---Adds offered bonus `which` (its n, part of its name, or its spell) through the window's AddModifierCommand. UI.
---@param which integer|string
function GK.Gui.AddRollBonus(which) end

---Gives aiming back to the pointer (TargetOverride cleared). Client.
---@param player integer?
function GK.Focus.ClearAim(player) end

---Uses the hotbar slot holding `id` - a spell id ("Throw_Throw"), an AutomationId or its tail
---("HotBarSlot_Item_<uuid>"), or part of an item's name (the throw list's items too) - through the
---hotbar's UseSlotCommand, as a click on it. Answers what it saw: ok when the targeting of that
---action began or took the item. UI.
---@param id string
---@param widget string? default "HotBar"
function GK.Gui.UseSlot(id, widget) end

---A new game from the main menu, one step per call: the main menu's New Game, then the new-game
---settings' Confirm (with the preset its list has selected). Each step answers what it saw: ok once
---the settings are open, then ok (with `state`) once the game has left the menu; ok = false with
---what was seen otherwise. Fair. UI.
---@return table
function GK.Gui.NewGame() end

---The game's own input events, from its input configuration, whose name holds `filter`:
---{ ok, events = { [name] = keys }, count }. Raise one with GK.Gui.Event(name) - e.g. the player's
---own weapon toggle, which no window command does. UI.
---@param filter string?
function GK.Gui.InputEvents(filter) end

---Whether a recorded film (a video: new game, end of character creation) is playing; nil and why
---when that cannot be read. Read from the engine's video manager, not the UI. UI.
---@return boolean|nil
---@return string?
function GK.Gui.FilmPlaying() end

---Names what is wrong: "film" (a recorded film plays; Recover presses Escape), "gameover",
---"firstlaunch", "splash" (the screen before the main menu), "mainmenu" (second result: the screen),
---or nil. Observation. UI.
---@return string|nil
---@return table?
function GK.Gui.Stuck() end

---One step out of a dead end (game over, main menu, first-launch screens, the splash screen). Call
---again until Stuck() is nil. The splash screen is passed with the event its own page raises on a
---key (OpenMainMenu). Fair. UI.
---First-launch screens: the EULA, Larian's mod policy (ModBrowserSplashScreen: both boxes ticked, then
---Accept; the answer lists `ticked`), sensitive settings, the calibrations.
---@return table
function GK.Cheat.Recover() end

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

---A named save, one step per call: the save window, its name box, the save. Each step answers what
---it saw: { ok, waiting = true } once the window or the box is open, then { ok, saved = name } once
---the game has gone through its Save state and back (the save is written, within 15 s); ok = false
---with what was seen otherwise. Fair. UI.
---@param name string
---@return table
function GK.Gui.SaveAs(name) end

---Loads the newest save (opens the load window first). UI.
---@return table
function GK.Gui.LoadLatest() end

---The game's Combine window (dyeing, combining two items), opened by using the first item:
---GK.Gui.ItemDo("use", <dye>). { with = n | "name" } puts an item of its list into the free slot;
---{ start = true } combines, only when the window reads Ready - a frame after `with`, so in a call
---of its own; no options: what it holds. Answers carry `combine = { base, slots, state, fail,
---result }`; state goes Preparing -> Ready -> Success. Seen live: a dye on leather armour. UI.
---@param options { with: integer|string?, start: boolean? }?
---@return { ok: boolean, log: table[]?, combine: table?, error: string? }
function GK.Gui.Combine(options) end

---Trade actions: set the mode, move one item, balance, finish. Several in one call run in that
---order. { mode = "trade" | "barter" } runs the view model's SetTradeMode (0 Trade, 1 Barter).
---The player's side is read from PlayerTraderCharacter.Inventory. `finish` presses the button the
---window shows, as the player does: TradeButton, or BarterBtn in barter mode (what GK.Cheat.Trade
---opens). A disabled button is an error with the window's numbers and flags under `log[n].state`,
---and nothing was traded. Seen live: move, balance, then finish sold an item.
---A move answers `moved = false`: the window shows it when the game gets to it - seen live about
---3 s later at a frame cap of 10. Call GK.Gui.Trade{} until `lastMove.reached` is true (it went
---across) or false (`why`: refused, or a SplitItem box waits); "not shown yet" means poll again.
---A stack does not move: the game asks how many in its SplitItem box (the move's answer names it
---under `box`/`next`; GK.Cheat.Step("split", { amount = n }) answers it). A new move is refused while
---the last one is not settled. Names match in any case, Cyrillic included (GK.Fold). UI.
---@param options { mode: "trade"|"barter"|0|1?, move: integer|string?, side: "player"|"trader"|"playerOffer"|"traderOffer"?, balance: boolean?, finish: boolean?, barter: boolean? }?
---@return { ok: boolean, log: table[]?, lastMove: { item: string, reached: boolean|string, why: string?, waiting: string?, waitedMs: number }?, error: string? }
function GK.Gui.Trade(options) end

---Asks for a long rest as the HUD's Long Rest button does. When the game's End the Day state is
---"Unavailable" afterwards, ok is false and `shown` holds what the HUD says about resting, the
---camp, fights and dialogues. ok says CampTravel ran: what it opens comes a frame later (`opens`
---says where to look - live, GK.Gui.Blocking() then named the End the Day box). UI.
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


---Aims the active targeting (a hotbar action, throw, jump, an item's use, a move) at an entity uuid
---or a point {x, y, z}, as the pointer over it would: written into the picking helper's result after
---the game's own picking. Answers with GK.Gui.Aiming() once the game's targeting holds the aim. UI.
---@param target string|number[]
---@param options table? { within = ms, settle = ms }
function GK.Gui.Aim(target, options) end

---The targeting now: { ok, mode, spell, shape, aim = { entity, name, position }, aimedByUs, valid,
---reason, lines = { { text, context, template } }, hit_chance, base_hit_chance, advantages,
---disadvantages, damage_min, damage_max, damage_types, affected = { { uuid, name, relation,
---hit_chance } }, warning }. `affected` is the game's own area highlight
---(ecl::spell_preview::HighlightsComponent); hit_chance and damage are what the cursor text shows
---(the player's view model), absent when it shows none; base_hit_chance is one roll's chance,
---before advantage and disadvantage. UI.
function GK.Gui.Aiming() end

---The left click on the aim. ok when the cast of the action started (the server wrote its targets
---into the cast): { ok, did = "clicked", spell, aim, affected, saw, cast = { spell, guid, targets =
---{ { entity, position } } } }. ok with `picked = uuid` (no cast) when the click was the first of a
---two-click action (Throw: the object) and the targeting waits for the second; the aim stays
---held. ok = false: nothing aimed, an aim the game calls invalid (did =
---"nothing", saw with the game's reason), the targeting still open after the click, or closed with
---no cast of it. UI.
---@param options table? { within = ms }
function GK.Gui.Confirm(options) end

---Gives the pointer back and leaves the targeting (the hotbar's ActionCancelCommand); ok when the
---targeting of the action in hand closed; keep = true only gives the pointer back. UI.
---@param options table? { keep = boolean }
function GK.Gui.CancelAim(options) end

---Tries the aims a player would try (each enemy, middles of pairs and of all) for the action in
---hand, reads the game's `affected` for each and leaves the aim on the best:
---{ ok, best, ranking }. No click. UI.
---@param options table? { want = "most enemies"|"best chance", avoid = "party"|"allies", candidates = n, targets = { uuid|{x,y,z} } }
function GK.Cheat.AimBest(options) end

---The click chain of a control from the game's own XAML, in GK.UICatalog's shape, or nil. UI.
---@param widget string
---@param name string
function GK.Gui.XamlChain(widget, name) end

---Marks a verb's answer as observed: the bridge calls check(elapsedMs, final) every tick until it
---returns the final table (final = true after `within` ms). Both sides.
---@param result table
---@param check fun(elapsed: number, final: boolean): table?
---@param within number? ms, default 1500
---@return table
function GK.Observe(result, check, within) end

---What waits for the player and what changed: a view of the event log (GK.EventLog), fed by hooks.
---Push(what, fields), Set(key, item, ended), Waiting (key -> item, read-only), State(), Since(seq),
---Mark(), Costs(). Observation. Both sides.
GK.Attention = {}

---The roll log: `dice_thrown` rows of the family `dice`, written from the roll events of GateKit's
---Script Extender build.
GK.RollLog = {}

---Whether this Script Extender build throws the roll events the log is written from. Server. Observation.
---@return boolean
function GK.RollLog.Available() end

---The named asker an address of an executable belongs to, or nil. Server. Observation.
---@param module string
---@param rva integer
---@return { id: string, what: string, entry: boolean? }?
function GK.RollLog.Origin(module, rva) end

---Leaves one named asker or cause out of the log's tables until the family `dice` is turned off,
---so that its rolls are written as rolls of an unknown origin. Without an id: nothing is left out.
---Server. Observation.
---@param id string?
---@return { ok: boolean, forgotten: string[] }
function GK.RollLog.Forget(id) end

---The askers known for each executable. Server. Observation.
---@return table<string, { id: string, what: string, rva: integer, size: integer }[]>
function GK.RollLog.Origins() end

---Decides one throw at the moment it is made, for the stage scope. Server. Cheat.
---@param spec { who: string?, kind: string?, stream: string?, nth: integer?, times: integer?, natural: integer?, total: integer? }
---@return { ok: boolean, id: integer?, error: string? }
function GK.Rolls.Rig(spec) end

---Takes a rig off (every rig without an id). Server. Cheat.
---@param id integer?
---@return { ok: boolean, removed: integer }
function GK.Rolls.Unrig(id) end

---The rigs in force. Server. Observation.
---@return { id: integer, who: string?, kind: string?, stream: string?, nth: integer, times: integer, natural: integer?, total: integer?, seen: integer, decided: integer }[]
function GK.Rolls.Rigs() end

---Whether this Script Extender build raises the roll event a rig acts through. Server. Observation.
---@return boolean
function GK.Rolls.CanRig() end

---The causes of a roll known for each executable. Server. Observation.
---@return table<string, { id: string, cause: string, rva: integer, size: integer }[]>
function GK.RollLog.Causes() end

---Data of any shape that a mod keeps inside the game's save. Server.
GK.Store = {}

---Declares a store: its version, defaults, migration and whose mod's variable it is. Server. Observation.
---@param name string
---@param options { version: integer?, defaults: table?, migrate: (fun(data: table, from: integer, to: integer): table)?, mod: string? }?
---@return { ok: boolean, name: string, version: integer, saved: boolean }
function GK.Store.Open(name, options) end

---A copy of the value at a path of a store (the whole store without a path), or nil. Server. Observation.
---@param name string
---@param path (string|(string|integer)[])?
---@param mod string?
---@return any
function GK.Store.Get(name, path, mod) end

---Writes a value at a path of a store; nil takes the key out. Checked first: nothing is written when it cannot be kept. Server. Cheat.
---@param name string
---@param path (string|(string|integer)[])?
---@param value any
---@param mod string?
---@return { ok: boolean, name: string?, error: string?, at: string? }
function GK.Store.Set(name, path, value, mod) end

---The stores of GateKit's own mod that the save holds. Server. Observation.
---@return { name: string, version: integer? }[]
function GK.Store.Names() end

---Whether a value can be kept in a store. Server. Observation.
---@param value any
---@return { ok: boolean, error: string?, at: string? }
function GK.Store.Check(value) end

---Takes a store of GateKit's own mod out of the save's variables altogether. Server. Cheat.
---@param name string
---@return { ok: boolean, dropped: boolean }
function GK.Store.Drop(name) end

---The chance (0..1) that a spell's roll succeeds for a caster against a target, as the game itself works it out. Nothing is rolled. Server. Observation.
---@param spec { who: string?, spell: string, target: string, set: ("Melee"|"Ranged")? }
---@return { ok: boolean, chance: number?, set: string?, was: string?, error: string?, needs: string? }
function GK.Rolls.Chance(spec) end

---Whether this Script Extender build has what a path under Ext names: a function ("Debug.AddProbe") or an event ("Events.Roll"). Both sides. Observation.
---@param path string
---@return boolean
function GK.Verbs.Has(path) end

---Moves the game's camera to `at` (an entity's uuid, a point {x, y, z}, or a list of two framed together) the way the game moves it: the camera behaviour's targets for an entity, its destination for a point, in a fight and outside. options.glide: glide past the game's snap distance (50 m) and put it back; options.hold: seconds before the camera returns by itself. The answer says `away` (the length of the move), `far` (past the snap distance) and `drawn` (every entity focused is drawn on the client; `notDrawn` and a `warning` name those that are not; a point has no `drawn`); a far move with no glide says `faded` (the game's own fade to black came) with `blackTicks` and `fadeTicks`, and answers when the fade has ended. A thing beyond the camera's leash (the switches' MaxDistFromChar) raises it until the return (`leashRaised`). Client: refuses a far creature's stub. Server: sends the server's point of every uuid to the client (channel GK_Camera) and answers the client's answer with `serverTicks`; options.wake = "one" (GK.Wake.One) or "place" (GK.Wake.Place) wakes the thing first and waits until the client holds it whole (`wake`, `woke`, `wakeTicks`) - a cheat, refused inside a fair step - and what it woke stays awake until GK.Camera.Return, a newer focus or GK.Stage.Clean. Fair. Observed once the camera settled; a row `camera_focused`.
---@param at string|number[]|(string|number[])[]
---@param options { glide: boolean?, hold: number?, wake: ("one"|"place")? }?
---@return { ok: boolean, at: number[]?, distance: number?, ticks: integer?, how: ("targets"|"destination")?, snapped: boolean?, fight: boolean?, off: number?, onScreen: table?, far: boolean?, away: number?, drawn: boolean?, notDrawn: string[]?, warning: string?, faded: boolean?, blackTicks: integer?, fadeTicks: integer?, leashRaised: boolean?, wake: string?, woke: table?, wakeTicks: integer?, serverTicks: integer?, stage: string?, error: string? }
function GK.Camera.Focus(at, options) end

---Brings the camera back to the controlled character by the game's own restore (in a fight the combat target's TargetMode 1 with its Position reset; outside, the behaviour's targets and the distance the first focus saved). options.glide as Focus's. From past the snap distance with no glide the game fades, and the answer comes when the fade has ended (`faded`, `blackTicks`). Puts back a leash the focus raised (`leashRestored`); on the server it then releases what the focus woke (`released`: GK.Wake.Release's answer). Client, or server through GK_Camera. Fair. Observed once the camera settled; a row `camera_returned`.
---@param options { glide: boolean? }?
---@return { ok: boolean, at: number[]?, distance: number?, restoredDistance: number?, ticks: integer?, how: string?, far: boolean?, away: number?, faded: boolean?, blackTicks: integer?, fadeTicks: integer?, leashRestored: boolean?, released: table?, serverTicks: integer?, error: string? }
function GK.Camera.Return(options) end

---@class GK.Wake
---@field HelperTemplate string @ the template of the helper GK.Wake.Place spawns: a name of GK.Stage.Templates or a root template
GK.Wake = {}

---Keeps the creature `who` awake and drawn wherever the party is (Osi.SetForceUpdate: an anchor of itself and of nobody else). Tracked by the stage's ledger; a row `wake_kept`. A wait that ran out says which side's and after how many ticks, and the creature is let go. Server. Cheat. Observed once it is active on the server and whole on the client.
---@param who string
---@return { ok: boolean, who: string?, name: string?, awake: boolean?, drawn: boolean?, was: { awake: boolean }?, distance: number?, ticks: integer?, awakeTicks: integer?, clientTicks: integer?, error: string? }
function GK.Wake.One(who) end

---Wakes the space around `at` (a point {x, y, z} or an entity's uuid) as a party member standing there would: a helper character put into the party's view, off stage and never drawn when it spawned asleep (`hidden`; `seen` says what a watcher could see of it). options.helper: a creature the caller spawned, in place of one spawned here; an origin, a player's character and a creature of the level's own cast are refused. options.template: the helper's template. Tracked by the stage's ledger; a row `wake_place`. Server. Cheat. Observed once the creatures within the manager's waking distance are active and the client holds whole those it is sent.
---@param at string|number[]
---@param options { helper: string?, template: string? }?
---@return { ok: boolean, helper: string?, at: number[]?, hidden: boolean?, seen: string?, radius: number?, count: integer?, woke: { who: string, name: string?, distance: number, drawn: boolean }[]?, wokeCount: integer?, drawnCount: integer?, alreadyAwake: integer?, asleep: table[]?, ticks: integer?, wakeTicks: integer?, clientTicks: integer?, error: string? }
function GK.Wake.Place(at, options) end

---Lets go what GK.Wake holds: a creature kept by GK.Wake.One, a helper of GK.Wake.Place, or with nil all of them. The undo is done at once; a row `wake_released`. `stayed` are the creatures another anchor keeps awake. The client is not waited for. Server. Cheat. Observed once the creatures nobody else reaches have lost Active on the server.
---@param handle string?
---@return { ok: boolean, released: string[]?, slept: { who: string, name: string? }[]?, sleptCount: integer?, stayed: { who: string, name: string?, why: string }[]?, stillAwake: string[]?, ticks: integer?, error: string? }
function GK.Wake.Release(handle) end

---Whether an entity (a uuid) or the space around a point {x, y, z} is awake on the server and whole on the client, how far the nearest character of the party's view is, and which rule keeps it awake: "kept up" (GK.Wake.One), "party", "place" (a helper of GK.Wake.Place) or "none". Of a point `characters`, `awake` and `whole` are counts within the manager's waking distance. Server; the client is asked. Observation. Observed.
---@param at string|number[]
---@return { ok: boolean, who: string?, at: number[]?, name: string?, character: boolean?, awake: (boolean|integer)?, whole: (boolean|integer)?, drawn: boolean?, rule: ("kept up"|"party"|"place"|"none")?, anchor: string?, anchorKind: ("party"|"place")?, distance: number?, characters: integer?, radii: { wake: number, sleep: number, source: string }?, ticks: integer?, error: string? }
function GK.Wake.State(at) end

---The activation manager's distances as the game holds them now: a character is awake within `wake` metres of an anchor and asleep beyond `sleep`. Read from the manager's own object (needs GateKit's Script Extender build); otherwise ok = false with the measured ones and source = "measured". Server. Observation.
---@return { ok: boolean, wake: number, sleep: number, itemWake: number?, itemSleep: number?, lowItemWake: number?, lowItemSleep: number?, source: string, error: string? }
function GK.Wake.Radii() end

---Where a point or an entity (its feet) is on the screen, from the camera's view and projection matrices: u, v from 0 to 1 from the top left corner. Refuses a far creature's stub (its client position is stale). Client. Observation.
---@param at string|number[]
---@return { u: number?, v: number?, inFront: boolean?, inside: boolean?, error: string? }
function GK.Camera.OnScreen(at) end

---@class GK.Say
---@field Styles table<string, { color: string?, bold: boolean?, italic: boolean?, prefix: string?, suffix: string?, accent: string?, effect: string?, sound: string? }> @ the looks of a line by name; `default` is the toolkit's own
---@field Effects table<string, string> @ short effects of the game's own by name (effect resource uuids)
---@field Sounds table<string, string> @ sound resources of the game's own by name
GK.Say = {}

---Shows `text` over the head of the creature `who` for about three seconds: a status of the toolkit's own (GK_SAY_<slot>) named with the text in the game's markup. options.style: a name of GK.Say.Styles or a table of its fields; any field may also be given in options. The caller's < > & are shown as written. Tracked by the stage's ledger; a row `said_overhead`. Server. Cheat. Observed once the status is on the creature.
---@param who string
---@param text string
---@param options { style: (string|table)?, color: string?, bold: boolean?, italic: boolean?, prefix: string?, suffix: string?, accent: string?, effect: (string|false)?, sound: string? }?
---@return { ok: boolean, who: string?, text: string?, status: string?, slot: integer?, handle: string?, markup: string?, ticks: integer?, error: string? }
function GK.Say.Line(who, text, options) end

---The line a creature shows now (within ~3 s), or nil. Server. Observation.
---@param who string
---@return { text: string, status: string, slot: integer, active: boolean }?
function GK.Say.Shown(who) end

---A short effect of the game's own on a creature or an item (a uuid) or at a point: options.effect (a name of GK.Say.Effects or an effect resource; default "orb" on an object, "spectral" at a point), options.sound, options.scale. The effects end by themselves. Server. Cheat.
---@param at string|number[]
---@param options { effect: string?, sound: string?, scale: number? }?
---@return { ok: boolean, effect: string?, at: (string|number[])?, error: string? }
function GK.Say.Accent(at, options) end
