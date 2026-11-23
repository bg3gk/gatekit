# Bridge protocol

How the host (the `gatekit` command, the MCP server, tests, any other process) talks to the
bridge mod running inside the game. It is a contract: the host side (`src/GateKit.Bridge`) and
the game side (`mods/GateKit/Mods/Gwynerva_GateKit/ScriptExtender/Lua/Shared/Bridge.lua`) may each be rewritten, as
long as both keep to this page. JSON Schemas for every file: [`schemas/bridge/`](../../schemas/bridge/), strict (no field they do not
declare), bound to the file names by `schemas/index.json` (data-format.md).

## Why files

Script Extender gives a mod's Lua no sockets; it can read and write files under its own folder
(`Ext.IO.LoadFile` / `Ext.IO.SaveFile`, relative to the Script Extender folder in the game's
profile) and nothing else. Lua there can neither list a folder nor delete a file. Files also work
the same on every platform, including from Linux into a Wine prefix.

## The channel folder

`<Script Extender folder>/Gwynerva_GateKit/`, where the Script Extender folder is
`<profile>/Script Extender/` and `<profile>` is the game's profile folder:

| Platform | Profile folder |
|---|---|
| Windows | `%LOCALAPPDATA%\Larian Studios\Baldur's Gate 3` |
| Linux, Proton | `<steamapps>/compatdata/1086940/pfx/drive_c/users/steamuser/AppData/Local/Larian Studios/Baldur's Gate 3` (or the prefix `gatekit` created) |
| Instance (any platform) | the instance's own profile folder (see `docs/contracts/cli.md`, instances) |

Each game side — `server` and `client` — has its own Lua state and its own files.

## Files

| File | Written by | Read by | Content |
|---|---|---|---|
| `queue_<side>.jsonl` | host (append, under a lock) | game | one command per line |
| `queue_<side>.lock` | host | host | lock target only; its content means nothing |
| `res_<side>_<id>.json` | game | host, then deleted by the host | the answer to one command |
| `state_<side>.json` | game, once a second | host | heartbeat |
| `ask_<side>_<id>.json` | game | host, then deleted by the host | one question from the game |
| `tell_<side>_<id>.json` | host | game | the answer to one question |
| `serve_<side>.json` | host, once a second | game | which questions a host is listening for |
| `events_<side>.json` | game, when a boot or a segment begins | host | the head of the event log: boot and last segment |
| `events_<side>_kinds.json` | game, at the start of each Lua state | host | the event registry this side loaded, verbatim (empty when it could not read one) |
| `events_<side>_<boot>_<n>.jsonl` | game, in the tick a row is added | host (deletes the files of older boots) | rows of the event log |

### A command line (`schemas/bridge/command.schema.json`)

```json
{"seq": 1763026000123456, "id": "4120-1763026000123456789-1", "code": "return GK.Party()", "ui": false, "t": 1763026000.12}
```

- `seq` — integer, strictly greater than every `seq` written before to this side's queue. The
  host uses `max(last seq in the file + 1, microseconds since 1970)`, so it keeps growing even if
  the file is lost. Microseconds fit a Lua double exactly.
- `id` — string of `[A-Za-z0-9._-]`, unique per command; it names the answer file.
- `code` — a Lua chunk. The host prepends `local GK = Mods.GateKit.GK` so the chunk sees
  the bridge's API whatever mod context it runs in.
- `ui` — run inside the UI's own deferred context (`Ext.UI.Defer`); required for anything that
  touches Noesis (`Ext.UI.*`). Client side only. A client chunk that names `GK.Gui.`, `GK.Cheat.`
  or `Ext.UI.` is run there without it.
- `class` — optional, `fair`, `cheat` or `observation`: the class of a test's step. A verb the
  chunk calls that the class does not allow fails the command with `a fair step called the cheat
  GK.<verb>` (or `an observation step called the ...`), also when the chunk caught it
  ([lua-api.md](lua-api.md#the-class-of-a-verb-and-steps-sharedverbslua-sharedverbclassesjson)). Absent: nothing is refused.
- `t` — seconds since 1970 when written; used only by the host to drop lines nobody can still wait for.

### An answer (`schemas/bridge/answer.schema.json`)

```json
{"id": "4120-1763026000123456789-1", "side": "server", "ok": true, "result": [...], "log": ["..."]}
```

`ok: false` carries `error` (text, with a Lua traceback) instead of `result`. `log` holds the lines
the chunk passed to `GK.Log(...)`. `result` is whatever the chunk returned, converted to JSON
(userdata and entities through Script Extender's JSON stringifier).

An empty Lua table is written `[]`, whether it was meant as a list or as a record with no field
set: Lua has one kind of table. The reader decides. Every host reader of a `result` goes through
`GateKit.Bridge.LuaTable` (`AsFields`, `Field`, `AsItems`), which takes `[]` as the empty form of
what it expects and refuses a table of the other kind with a message naming what came.

### The heartbeat (`schemas/bridge/state.schema.json`)

```json
{"side": "client", "boot": 123456.7, "time": 130000.2, "gameState": "Running", "queue": 1, "done": 1763026000123456}
```

- `boot` — monotonic time at which this Lua state started; it changes on every reset/reload.
- `time` — monotonic time of this beat. The file's modification time tells the host how fresh it is.
- `gameState` — the engine's game state name (`Menu`, `LoadSession`, `Running`, ...).
- `queue` — protocol version the bridge speaks: `1`, the command queue of this page. The question
  channel ([The other direction](#the-other-direction-the-game-asks)) is specified and not built;
  a bridge that serves it announces `2`.
- `done` — the highest `seq` this side has started.
- `tick` — the side's clock (`GK.Ticks()`) at the beat: its progress, in which the host counts its
  timeouts ([Observed answers](#observed-answers)). The server's is its tick count; the client's is
  its frames counted by their time at the nominal rate.

## Rules

**Host, sending** (all under an exclusive lock on `queue_<side>.lock`, held for milliseconds):
1. Read the queue; drop lines with `seq <= done` (from the heartbeat) or older than 10 minutes.
2. Append the new line (or rewrite the file when something was dropped). One write per line;
   a line is never split across writes.
3. Retry any file operation the game blocks (Windows sharing violations) for up to 2 s.

**Game, every 150 ms** (`POLL_MS`):
1. Read the whole queue. Parse line by line; **stop at the first line that does not parse** —
   the host may be writing it, and nothing after it may run before it.
2. Run, in order, every line with `seq > done`; set `done` before running, so a command that
   crashes the Lua state is not run again.
3. Write `res_<side>_<id>.json` for each.

**On start of a Lua state** (game start, `reset`, hot reload of the bridge): `done` becomes the
highest `seq` already in the queue. Commands queued while no Lua state was alive are dropped,
never run late. A caller that sends across a reset must wait for a new `boot` first.

**Host, waiting**: poll `res_<side>_<id>.json` (every 100 ms); on success delete it. Unclaimed
answers older than 10 minutes are deleted by the next sender.

Any number of host processes may talk to the same side at once; ordering between processes is
the order in which they took the lock.

## The other direction: the game asks

Everything above is the host telling the game what to do. The game can also ask the host a
question and wait for the answer — this is how a character can be played by something outside
the game, such as an AI agent, while the game runs normally (`docs/design/ai-controllers.md`).

Not built yet: neither the bridge nor the host writes or reads these files, and the heartbeat says
`queue: 1`. This section is the specification the first implementation keeps to.

It is the mirror of the queue, with one difference that is not arbitrary: **one file per
question, not one queue file.** Lua in the game cannot list a folder, so commands coming in must
share one file it can name; the host can list a folder, so questions going out are a file each.
Every file then has exactly one writer, and neither side needs the other's lock.

### A question (`schemas/bridge/ask.schema.json`)

```json
{"id": "s-1763026000123-7", "kind": "turn", "deadline": 20, "payload": { "...": "..." }}
```

- `id` — unique per question; it names the answer file.
- `kind` — what is being asked. The host answers only the kinds it announces; an unknown kind is
  answered `{"ok": false, "error": "..."}` so the game can fall back at once.
- `deadline` — seconds the game will wait before it gives up and falls back. The host must answer
  within it or not at all.
- `payload` — the question itself, defined per kind.

### An answer (`schemas/bridge/tell.schema.json`)

```json
{"id": "s-1763026000123-7", "ok": true, "result": { "...": "..." }}
```

Shaped like a command's answer: `ok` with `result`, or `ok: false` with `error`.

### Who is listening (`schemas/bridge/serve.schema.json`)

```json
{"tick": 412, "kinds": ["turn"], "agent": "claude"}
```

The host writes this once a second while it serves questions. `tick` grows by one each time: the
game cannot read a file's timestamp, so it watches `tick` change. If it has not changed for three
seconds of game time, the game treats the host as gone, stops asking and falls back — the game
must never hang because nothing is listening.

### Rules

**Game, asking**: write `ask_<side>_<id>.json`, then poll for `tell_<side>_<id>.json` every
150 ms until `deadline`. On a timeout, or with no host listening, fall back — for a turn, that
means letting the engine's own AI play it. Never block the game thread waiting.

**Host, serving**: list `ask_<side>_*.json`, read each, answer into `tell_<side>_<id>.json`,
then delete the question file. Answers the game never collected are deleted after ten minutes,
as unclaimed command answers are. Exactly one host should serve a given kind on one instance; if
two do, both may answer and the game takes the first.

The game keeps serving commands while it waits: an agent can be asked for a turn and inspect the
game through `exec` in the same moment.

## The event log

Each side keeps one append-only log of what happened (`Shared/EventLog.lua`, [lua-api.md](lua-api.md#the-event-log-gkeventlog-sharedeventloglua-both-sides)) and
writes it to the channel folder.

```json
{"seq": 41, "tick": 3120, "t": 195329100, "kind": "trade", "knows": "player", "key": "window:trade", "attention": "begin", "payload": {"window": "trade", "widget": "Trade"}}
```

- A row (`schemas/events/row.schema.json`): `seq` from 1 within one boot, `tick` the side's
  tick count, `t` the game process's monotonic milliseconds (one clock for both sides), `kind`,
  `knows` (who could know it: `player` or `omniscient`), and as the kind needs `key`,
  `attention`, `scope`, `payload`, and `class` on a kind whose entry has `classPerRow` (the class
  of the verb that made the row: `job_done` carries its job's).
- **The registry** (`mods/GateKit/.../Lua/Shared/EventKinds.json`, `schemas/events/kinds`) is
  the one place the families and kinds are declared: per family whether it is always on, on by
  default, or on only when switched on; per kind its family, its class (`fair`, `cheat`,
  `observation`), who could know it, whether it waits for an answer (`attention` begin/hold, and
  the kind that `ends` it) and its payload's fields. The kinds `step` and `step_ended` (family
  `note`) mark where a test's step begins and ends. The mod reads it at the start of each Lua
  state (a game start, a `reset`, a `bridge-reload`), refuses a row of any other kind at once, and
  copies the text it read to `events_<side>_kinds.json` before the head of the new boot. Adding a
  family is one entry in the registry and the code that writes its rows; the store, the bounds,
  the counters, the merge, the readers and the filters take it as it is.
- **The host follows the game's registry.** It places a side's rows by that side's
  `events_<side>_kinds.json`, read again whenever the file changes, so an edited registry works
  after a `bridge-reload` without rebuilding the host. The copy embedded in `GateKit.Bridge` is
  what the host was built with: it places the rows where the game wrote no registry (a log read
  with no game, an empty copy) and a kind only it has. `gatekit status` and `doctor` name the
  registry in use and how the game's differs from the built one (kinds and families added,
  removed, changed); a `test` run says it at its start when they differ. That is information, not
  a fault.
- **A row is never left out for the registry.** One the host cannot place - a kind in neither
  registry, a `knows`, `attention` or `class` other than its kind declares - is kept and marked
  `unplaced` (why) and said: per kind in `bg3_events`' `unplaced`, once per kind in attention
  (`what = unplaced`, `kind`, `error`), and a test whose rows include one fails. A fair stream
  takes an unplaced row as omniscient and leaves it out; that it did is said the same way, with
  `withheld`. A payload field the kind does not declare is said and kept.
- **Families**: always on - `state`, `attention`, `game`, `scope`, `log`, `note`; on for a check -
  `osiris` (the events GK.Watch listens to), `status`, `entity`, `surface`, `component`
  (`GK.EventLog.On(names, { seconds, components })`, at most 600 s; off at the end of the stage
  scope that switched them on, when their time is up, or with `GK.EventLog.Off`). `family_on` and
  `family_off` rows say when.
- **Bounds**: a side keeps at most 32 segments and 4 MB of rows; the oldest segment goes whole
  (its file emptied, then deleted by the host), and a `dropped` row says how many rows and bytes of
  which families went and the oldest seq kept. A row that still waits for an answer is never lost:
  it stays in memory and in the head's `open`. A reader asking from a seq that went is told
  (`dropped` in `GK.EventLog.Since`, in `bg3_events` and as an attention item `dropped`), never
  given a silent gap. The head's `counts` give rows, bytes and drops per family. The registry is a
  file of its own beside the head, not a field of it: the head is rewritten in every tick the log
  changed, the registry once per Lua state.
- A **boot** is one Lua state of the side; its number is one above the previous boot's on this
  game copy (read from the head at start), so it is unique across resets, loads and restarts.
- **Segments**: rows `(n-1)*rows+1 .. n*rows` of a boot are `events_<side>_<boot>_<n>.jsonl`, one
  row per line. The game rewrites the last segment whole in the tick a row is added; a full
  segment is written once more and never again. The write is in place (truncated, then written),
  so a reader may find the file torn, empty or cut at a line's end. A segment of a boot never
  loses rows, and the head is written after its segment: a read that is torn, or ends before the
  head's `seq` (a full segment: before its last row), is read again a moment later, and a read
  with fewer rows than the reader's last read of that file is a write in progress - the last read
  stands.
- **The head** `events_<side>.json` (`schemas/events/head.schema.json`): `{side, boot, segment,
  first, seq, rows, started, tick, process, open, counts, on}`, written when a boot starts and in
  every tick the log changed: the segments kept (`first` .. `segment`), the side's tick count then,
  the rows that still wait (`open`), the per-family counters and the switchable families on now.
- The host deletes the files of a side's boots older than the one before the current one; the
  game never deletes (Lua cannot).
- **What outlives a Lua state.** A boot reads the previous boot's rows (`GK.EventLog.Previous`:
  the fixtures' open rows, the stage's strays, the ledger's data undos) only when both ran in the same
  game process: the head's `process` is the text of `process.txt` in the channel, which `up` writes
  when it starts the game, and a previous head whose `process` differs - or a channel with no
  `process.txt` while the previous head names one - is of a world that is gone, and nothing of it
  is carried. With no `process.txt` on either side (a game the host did not start) the previous
  boot is taken as the same process.

### One stream

The two sides are where a row is written, not something a reader chooses: the host
(`GateKit.Bridge.EventStream`) merges the current boots of both into one stream and gives every
row its `side`. The order: `t` first - the client's and the server's Lua states run in one game
process and read the same monotonic clock, so `t` is comparable across sides; at the same
millisecond the server's row first (the server decides what the client then shows); within a side
`tick`, then `seq`, so a side's own order is kept. The sides' `tick`s are each a count of its own
Lua state's ticks and are never compared with each other. A cursor is one text,
`client:<boot>.<seq>,server:<boot>.<seq>`; a side whose boot changed is read from its start.
Attention (below) is one view over the same stream. A host serving a client as fair
(`GATEKIT_FAIR=1` for the MCP server, set by whoever starts it) gives it `knows = player` rows
only, in the stream and in attention; the filter is the host's, never the client's.

## Protocol 0

The bridge also reads a single `cmd_<side>.json` (`{"id","code","ui"}`) and answers it in a single
`res_<side>.json`, for a caller that does not know the queue. The C# host never writes it.

## Observed answers

A chunk may return a table marked with `GK.Observe(result, check, within)`. The game side then
does not write the answer at once: it calls `check(ticks, final)` once a tick of its side (in the
context the chunk ran in, the UI's for a `ui` chunk), `ticks` being the side's ticks since the
observation began, until it returns a table. Every limit is counted in the side's clock
(`GK.Ticks()`): `within` is milliseconds of a running game converted at the nominal rate of
thirty ticks a second (`GK.LimitTicks`), and when that many ticks have passed `check` is called
with `final = true` and must answer. On the server the clock is its own tick count. A client ticks
once a frame at the renderer's rate (144 a second and more in the background), so its clock counts
each frame by its time at the nominal rate, a frame at most 100 ms: a game that stands still runs
no tick and eats nothing of the limit, a frame that hangs eats at most three ticks. A check with nothing to say at its limit is answered `{ ok = false, error = "the event
never came: ...", attempted }`. The table, with `waitedTicks` and `waitedMs` (the real time, for
reading only) added, is the answer's `result`. A verb that waits on another verb's observation
follows it with `GK.Follow(answer)`, the same loop. Other commands run meanwhile; nothing is held
up. Only a table returned directly by the chunk is waited on: `return GK.Gui.Click(...)` waits,
`local r = GK.Gui.Click(...) return { r.ok }` answers what was attempted.

The host counts its own timeout the same way: in the side's ticks as its heartbeat reports them
(`tick`), at the same nominal rate, so a timeout that covers `within` in seconds covers it in
ticks. It gives up at once on a side whose `boot` changed (the command was dropped) and, as a guard
against a hang, on a side whose `tick` has not moved for 60 s or the timeout, whichever is longer.
Without a heartbeat that carries `tick` the timeout is the host's clock. A limit of the host's own
waits (`game.wait`, `wait_row`, `wait_attention`, `answer`) follows the server's ticks, and the
client's clock only when no server counts: never the client's rows' `tick`, which counts frames.

## Attention

What waits for the player, and what changed, is a view of the event log
(`mods/GateKit/.../Shared/Attention.lua`): the rows whose kind has an attention class.

- **Waiting**: a `begin` or `hold` row opens the state named by its `key`, a later row of the same
  key replaces it, an `end` or `release` row closes it. The states: the blocking windows of GK.Gui
  (key `window:<name>`; kinds `dialogue`, `roll`, `reaction`, `box`, `reward`, `trade`, `loot`,
  `rest`, `tutorial`, `learn`, `split`, `book`, `menu`, `save`, `load`, `gameover` for the
  game-over box, `setup` for a page of the first launch - `Eula`, `ModBrowserSplashScreen`,
  `SensitiveSettings`, `HDRCalibration`, `GammaCalibration`, the gamma page over the main menu too,
  each under its own name (`window:Eula`) with `controls` (what a player can act on, as
  `GK.Gui.Look` names it: handle, kind, text, command, parameter, value, items) and
  `firstLaunch` (the switch `ShowFirstTimeSetup`) in the row (`bg3_events`, `game.wait_row`; the
  host's compact attention item keeps `window` and `choices` of them) - `splash` for the "press any key" page
  (`window:SplashScreen`, no control, `keys`), `window` for a blocking window the vocabulary does
  not name). A page's controls are made and arranged by Noesis's layout over several frames (the
  page, then a list's items, then what an item holds): its row is written in the tick the layout has
  arranged the page and every control it shows (`GK.Gui.Look`'s `laidOut`; at most 4 s); a page gone
  before that was never drawn and has no row. `Eula` waits only while the switch
  `ShowFirstTimeSetup` is on (the registry entry's `active`: with it off, Eula's XAML raises
  `OpenSplashScreen` itself on Loaded), so an ordinary start has no `setup` row. Where an open
  dialogue stands (key `dialogue:phase`: `dialogue_choice`, `dialogue_continue`, and
  `dialogue_line` as a quiet `hold`), `film`, the client's game state (key `state`: `loading`,
  `saving`, `menu`; Loads, below), the main menu's camera descent (key `menu:descent`:
  `menu_descent`), and `combat_started` (server, key `combat`, while the party fights).
- **Told**: every `begin` (its kind), every `end` (`<kind>_ended`, or the end the kind names:
  `combat_ended`), and the `news` kinds: `death`, `downed`, `combat_elsewhere`,
  `dialogue_started`, `dialogue_ended_server`, `level_started`, `level_unloading`,
  `tutorial_shown`, `trade_requested`, `job_done` (a GK.Act job; `job`, `kind`, `status`,
  `error`). `hold` and `release` rows change what waits without being told. A `roll` and its
  `roll_ended` carry the roll's numbers ([lua-api.md](lua-api.md#gkrolls-rolls-decided-in-advance-serverrollslua)), and the host keeps them in what
  it tells: `check`, `skill`, `dc`, `advantage`, `modifiers`, `bonus`, then `natural`, `total`,
  `success`, `critical`.
- **How long ago**: `since_ms` is the side's clock from the row to the last row of the log, plus
  the host's time since the last segment was written; `since_ticks` is the side's ticks from the
  row to the last row.

**Loads.** The key `state` is the client's alone (`Client/Attention.lua`): its state machine passes
every load, while the server's does not exist at the start and sits `Uninitialized` in the menu;
the server writes no `state` rows (its states stay `game_state` rows). Each condition is a state
of the client's machine, never a wait:

- **A load begins** when the client enters `StartLoading` (or, with nothing open, any state that
  is not `Menu`, `PrepareRunning`, `Running`, `Paused` or `Save`): `loading` with `state`, and
  `from`, the state it left (`Menu`, `Running`). Every kind of load passes `StartLoading`: the
  start (`InitMenu`, `StartLoading`, `Init`, `LoadModule`, `LoadMenu`, `StopLoading`, `Menu`); a
  save's load or a new game from the menu (`Menu`, `StartLoading`, `StartServer`, `Idle`,
  `InitNetwork`, `InitConnection`, `StartLoading` again, `Idle`, `UnloadLevel`, `UnloadSession`,
  `Idle`, `LoadSession`, `LoadLevel`, `SwapLevel`, `StopLoading`, `PrepareRunning`); a save's load
  in a session (`Running`, `StartLoading`, `Idle`, `UnloadLevel`, `UnloadSession`, `Idle`,
  `LoadSession`, ..., `PrepareRunning`); a return to the menu (`Running`, `StartLoading`,
  `Disconnect`, `UnloadLevel`, `UnloadSession`, `LoadMenu`, `StopLoading`, `Menu`). Every state
  between is inside the same load: no row of its own here.
- **A load ends** when the loading screen is hidden: the client enters `Menu` or `PrepareRunning`
  (the timeline's `LoadingScreenHidden` is in that tick), or `Running` with a load still open:
  `loading_ended` with `to`. `PrepareRunning` lasts about a second, or as long as a new game's
  opening film (`film` waits meanwhile); `Running` follows it.
- `Menu` opens `menu` (`menu_ended` with `to` when it is left); `Save` opens `saving`
  (`saving_ended` with `to`).
- **Through a reset of the client's Lua.** A save's load, a new game and a return to the menu
  start a new Lua state of the client inside the load (at `Idle` after `UnloadSession`, or in
  `LoadMenu`); the start's first Lua runs in `LoadModule`, after `StartLoading`. A new boot opens
  again what its machine is in. When the previous boot of the same game process left that same
  state open, the new row carries `continues` = `{ boot, seq }` of the row that told it, and the
  host does not tell it again to a caller whose cursor stood at that boot at or past that seq
  (`AttentionReader.ToldBefore`); a caller that never saw that row is told this one. When it left
  open a state that is over now, the new boot opens it (with `continues`) and ends it at once, so
  that the end is told. With no previous row - the start: no Lua exists at `StartLoading` -
  `loading` carries `startedMs`, the time the client entered `StartLoading` in the clock of
  `Ext.StartupVideos.Timeline()` (ms since the process was created, GateKit's Script Extender
  build), and `from` read there. A caller is so told exactly one `loading` and one
  `loading_ended` per load.
- **The main menu ready.** On the "press any key" page's key, on the start and after every return
  to the menu, the menu's accept event plays the timelines the menu level's cinematic arena
  trigger binds to it - the game's `MainMenu/MM_Accept` (`d0fbd7c8-89e5-b1cf-5528-55fb12deb971`)
  and `MM_Accept_Temple`, or a mod's own. That trigger is the first one the menu's timeline
  system marks played (`ClientTimelinePlayedMainMenuTrigger`, its events'
  `ClientTimelineMainMenuActiveEvent.Event`) after the client enters `Menu`; the open event's is
  played while the menu loads. When one of its timelines goes `PlaybackState` 0 -> 2:
  `menu_descent` (key `menu:descent`, `timeline` = that one, `playback`); when the first one that
  played is back at rest, the camera is: `menu_descent_ended` (`timeline`, `playback`; `to`
  instead when the menu was left before). A menu level whose trigger binds no timeline to the
  accept event, or one that never comes to rest (a looping timeline), leaves `menu:descent`
  unopened or open; the menu works either way, nothing of the game waits for it.
  `TimelineData` has no replication event, so the accept's timelines are read every tick, only
  while the client is in `Menu`.

Hooks: Osiris listeners on the server; `Ext.Events.GameStateChanged` on the client; on the client one look
per tick at the names of ContentRoot's widgets and the registry's full check of the blocking
windows when that list changes, else every 200 ms; the film flag every 500 ms.

**The pages of the interface** are rows of the event log, not attention: each widget under
ContentRoot that appears is `ui_page`, each that goes `ui_page_ended` (family `state`), payload
`{ page, widget, viewModel }` - `page` its x:Name or, without one, its view model's name
(`gui::DCHDRCalibration` -> `HDRCalibration`), as `GK.Gui` names it. They come from the same look
at ContentRoot's names: a state read each tick, told when it changes; every page, the HUD's and the
main menu's too. A boot tells the pages open at its start; a page shown twice is two rows.

**The loading screen's look** is rows of the event log too, not attention, read after the fact
from the start's timeline of GateKit's Script Extender build (`Ext.StartupVideos.Timeline(since)`):
the loading screen runs while the client's Lua is reset, or before it exists. At each boot of the
client's Lua and every 15 ticks after, the timeline's rows since the last ones imported (kept in
`timeline_imported.json` of the channel with the game process's name) become `loading_wallpaper`
- `{ what = "set", images, slot, fadeIn, fadeOut }` when the slideshow (`gui::LSImageSequence`) is
made ready, its images in the order it will show them; `{ what = "shown", image, index, count,
look }` when the wallpaper on screen changes (`look` `sequence` or `single`); `{ what = "gone",
image, index, count, look }` when its element leaves the interface (the widget went; it outlives
`LoadingScreenHidden` by seconds) - and `hint_shown`
`{ key, index, count }` (family `state`). Every wallpaper element is followed, the game's and a mod's:
each `loading_wallpaper` row names its `owner` (the x:Name of its nearest widget: `root` for the
game's own loading screen), `ownerData` and `visible`. Each carries `ms`, the timeline's time (milliseconds
since the game process was created), and `row`, the timeline's row; the row's own `t` is when it
was imported. The loading bar's progress is not imported: it moves tens of times a load and the
game's totals run past 100%; `gatekit timeline` shows it. A state that
begins and ends with an engine component is recorded from the component's own creation and
destruction (`Ext.Entity.OnCreate` / `OnDestroy`, in the tick it happens) and is marked
`hooked`: the reaction prompt, `code:ecl::interrupt::PlayerDecisionComponent` (it exists only while
the player is asked; ReactionDecisionPopup itself never leaves the tree). The widget scan does not
open or close a hooked state; a window a hook closed is not opened again by the scan for 500 ms.
`GK.Attention.Costs()` says what the hooks and the log cost on a side. The hooks are registered
again at every bootstrap, so a reset or a load starts a new boot of the log.

The host (`GateKit.Bridge.AttentionReader`) folds both sides' attention rows in the stream's order
into one view: one waiting state per key whichever side reports it - a `begin` of a key already
open with the same kind from the other side, and an `end` of a key already closed, are the same
state told twice and are left out. A caller is told, after each game call, everything waiting and
every told row it was not told before (a new boot counts from its start; a first look tells only
what waits). A caller's cursor never goes back within a boot: what it was told stays told. `AttentionWait` ends a wait on
the first told row the caller has not been told (with `from`, the caller's cursor: what happened
between its previous call and the wait counts; without it, what appears after the wait began), on
a condition, or on its timeout.
