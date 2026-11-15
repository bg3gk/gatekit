# MCP contract

Every tool of `gatekit mcp`, the MCP server of the toolkit (`src/GateKit.Mcp`): what it does to
the copy the server works on, what it answers and how it fails. Section 1 is the core tools,
section 2 the server itself, sections 3-5 the coverage loop, attention and the event log. The
exact argument schema of every tool is what `gatekit mcp tools` lists, written in its source file.

Every failed call answers `isError` with one text block `<kind>: <message>`: `CopyError` for what a
copy refuses or cannot do (`CopyException`, a missing platform capability), `KeyError` and
`ValueError` for arguments, else the exception's type name (`ToolHost.Error`). Results are text; a
value that is not a string is indented JSON (one space, nothing escaped; `Results.Json`).

---

## 1. Core tools (`src/GateKit.Mcp/CoreTools.cs`)

#### `bg3_up`

- **Arguments**: `level` (`MODULE:LEVEL`), `save` (a folder name under `Savegames/Story`), `mods` (folder names), `story_log`, `main_menu`, `game_args`.
- **Calls**: `GameCopy.UpAsync` with those, as `gatekit up` (cli.md 2.3).
- **Result**: everything `up` printed (state changes `  12.3 s  Running`, `Script Extender for this run: ...`, `bridge pak rebuilt ...`, `playable after N s`), or `playable` when nothing was printed.
- **Errors**: whatever `up` refuses (already running, a level without its module, the bridge never reported, ...) as `CopyError`.

#### `bg3_down`

- **Calls**: `GameCopy.Down` (cli.md 2.4).
- **Result**: the line `down` prints: `game closed, saves and mod list restored`, or for an instance `instance NAME: game closed` and what happened to its Script Extender build.

#### `bg3_status`

- **Calls**: the copy's game processes and last heartbeats; with a game running, the client's `GK.Gui.FilmPlaying()` (5 s).
- **Result**: JSON `{instance, drives, channel, processes, client, server, client_alive, film}` (2.5). A heartbeat is shown as `{side, boot, time, gameState, queue, done}` only while it is fresh and a game of this copy runs; otherwise `{gameState: null, stale: "last heartbeat written ...; ..."}`, or null when there is none.
- **Errors**: none for a missing game.

#### `bg3_lua`

- **Arguments**: `side` (`client`, `server`, `ui`), `code`, `timeout` (s, default 20).
- **Calls**: `GameSession.LuaAsync` (cli.md 2.8). `ui` is the client with `ui: true`.
- **Result**: the lines the chunk logged (`  [<side>] <line>` per `GK.Log`), then a newline, then the result as JSON (`null` for nil); without log lines the JSON alone. A code argument is always a chunk, never a file.
- **Errors**: `CopyError: Lua error on <side>:\n<traceback>`; `CopyError: no answer from <side>: ...`; `CopyError: no game is running for <copy>; nothing was sent`.

#### `bg3_wait`

- **Arguments**: `side` (`client`, `server`, `ui`; default client), `expression`, `until_attention` (default true), `timeout` (s, default 30).
- **Calls**: section 4: the wait ends on a new attention item, or when `return (<expression>)` is truthy by Lua's own truth (only `nil` and `false` are false), whichever comes first.
- **Result**: the expression's value when it came true; otherwise `{endedBy: attention|timeout, attention: [...], waitedMs}`.
- **Errors**: `ValueError: nothing to wait for: ...` without an expression and with `until_attention` off; `CopyError: timed out waiting on <side> for: <expression>` when an expression was given; Lua errors as `bg3_lua`.

#### `bg3_state`

- **Calls**: client `return { client = GK.Client(), scenes = GK.Scenes() }`, server `return GK.Party()` (20 s each).
- **Result**: JSON: the client table's keys, plus `party`.

#### `bg3_events`

- **Arguments**: `since` (the `next` of an earlier answer), `kinds`, `family`, `limit` (default 200).
- **Result**: section 5: `{rows, next, more?, dropped?, unplaced?, counts, on}`. Reads the event log's files; asks nothing of the game.

#### `bg3_logs`

- **Arguments**: `lines` (default 60), `grep`, `story`.
- **Calls**: `GameCopy.Logs` (cli.md 2.11).
- **Result**: the lines joined by `\n`, or `(nothing)`.

#### `bg3_reload`

- **Arguments**: `mod_root`.
- **Calls**: with `mod_root`, `GameCopy.Link` (its full path); then `GameCopy.ReloadAsync` (cli.md 2.10).
- **Result**: `reloaded <A, B> in X.X s`.
- **Errors**: `CopyError: no Mods/<Folder>/ScriptExtender/Lua under ...`, `nothing is linked ...`, `<side> Lua did not come back after the reset ...`.

#### `bg3_dialog`

- **Arguments**: `answer` (a number as listed; 0 or absent only reads).
- **Calls**: on `ui`, `GK.Dialog()`; with `answer`, `GK.Answer(n)` and then `GK.Dialog()` every 0.3 s until the dialogue changed (15 s).
- **Result**: the dialogue as JSON, or `no dialogue`.
- **Errors**: `CopyError: the dialogue did not change after answer N: ...`.

#### `bg3_fast_scenes`

- **Arguments**: `speed` (default 20).
- **Calls**: client `GK.FastScenes(<speed>)`.
- **Result**: `scenes now play at x<speed>`.

#### `bg3_checkpoint`

- **Calls**: `GameSession.CheckpointAsync` (cli.md 2.12).
- **Result**: `checkpoint saved: <save folder name>`.
- **Errors**: `CopyError: the game made no save for the checkpoint` etc.

#### `bg3_reset`

- **Calls**: `GameSession.ResetAsync` (120 s, the opening scene played through; cli.md 2.12).
- **Result**: `world reset in X.X s; a standing capture (bg3_seen_start) does not survive it - start it again if one was running`.
- **Errors**: `CopyError: no checkpoint to return to...`, `<side> did not come back from the reset ...`.

#### `bg3_key`

- **Arguments**: `key`, `hold` (0 to 30 s).
- **Calls**: `GameCopy.Key` (cli.md 2.9): through the game (`GK.Key`), else the window's own key messages; with `hold`, `GK.Key(key, nil, hold)` and a wait until it is up.
- **Result**: `pressed <key>`; with `hold`, `held <key> for <s> s, released`.
- **Errors**: `CopyError: unknown key ...` / `key not sent: ...` / `a hold is 0 to 30 seconds ...` / `a held key needs the bridge ...` (the window's own key messages only tap).

#### `bg3_sound`

- Section 2.1.

#### `bg3_screenshot`

- **Arguments**: `width` (default 1150).
- **Calls**: `GameCopy.Shot` into `<state>/shots/mcp-HHmmss.jpg` (cli.md 2.9).
- **Result**: an image block (`image/jpeg`, base64) and a text block `saved: <path>`. The file stays on disk (calls in the same second overwrite it).
- **Errors**: `CopyError: screenshot failed: ...`.

#### `bg3_call`

- **Arguments**: `side` (`client`, `server`, `ui`), `function` (a path under `GK`, e.g. `Stage.Fight`; a leading `GK.` is dropped), `args` (an array), `timeout` (s, default 20).
- **Calls**: every dot-separated part of `function` must be an identifier (`CopyError: not a function path: ...`). The Lua walks `GK` by the path (`GK.<path> is not a function on this side`) and passes the arguments as `Ext.Json.Parse([=[<json>]=])`, with a long-bracket level 1..7 the payload does not contain, unpacked with `table.unpack(a, 1, <n>)`.
- **Result**: the call's return value (a string as it is, anything else as JSON).
- **Errors**: the Lua error text.

#### `bg3_api`

- **Arguments**: `filter` (any text, case-insensitive, matched against each entry's JSON).
- **Calls**: no game: reads every bridge mod `up` would install (`GATEKIT_BRIDGE`, default `<repo>/mods`, so Weave as well as GateKit) - each function of its Lua with the `---` comment above it (`BridgeVerbs.Read`) and its class from the mod's `Shared/VerbClasses.json`.
- **Result**: JSON list of `{"function":"GK.X.Y","parameters":"a, b","where":"Server/Foo.lua","side":"server","class":"fair","about":"...","why"?}`. `side` is where to call it: `server` (Server/), `ui` (the client files lua-api.md marks UI: Client/UI, Stage, Windows, WindowReaders, Popups, Places), `client` (the other client files), `client or server (each its own copy)` (Shared/). `class` is `fair`, `cheat`, `observation`, or `unclassed`.

---

## 2. The server (`src/GateKit.Mcp`)

`gatekit mcp` serves the copies' tools over stdio; `gatekit mcp tools` lists what it offers without
starting anything. The instance comes from the same settings as every other command (`--instance`,
`GATEKIT_INSTANCE`, `BG3_GATEKIT_INSTANCE`); one process serves one instance. The repository's
`.mcp.json` registers it as `gatekit`. `src/GateKit.Mcp/README.md` is the guide for modders.

**The installed game is reached only when it is named** ([cli.md](cli.md) 1.1, where the rule and
its one check stand). Without an instance, every tool that sends the game a chunk of Lua -
`bg3_lua`, `bg3_call`, `bg3_dialog`, `bg3_fast_scenes`, `bg3_sound`, `bg3_state`, `bg3_wait` with
an expression, the coverage tools that read the game (`bg3_pause`,
`bg3_seen_*`, `bg3_probe*`, `bg3_verify`), the plugin tools - and every tool that goes through a writing
command (`bg3_up`, `bg3_down`, `bg3_reload`, `bg3_key`, `bg3_checkpoint`, `bg3_reset`) answers
`CopyError` with that refusal, unless the server was started as `gatekit --installed-game mcp`.
`bg3_screenshot` is refused while the bridge answers, as `shot` is. What sends no chunk works
without a name: `bg3_status` (its `film` is then `null`), `bg3_events`, `bg3_logs`, `bg3_api`,
`bg3_wait` without an expression, `bg3_windows`, and the tools that need no game.

Built on the official C# SDK (NuGet `ModelContextProtocol` 2.4.0, which ships a `net10.0` target):
the handshake, capabilities, `tools/list`, `tools/call`, `ping` and cancellation are the SDK's. The
**transport** is GateKit's own (`BatchStdioTransport`), because batches are not in the SDK - the
spec version 2025-06-18 has none - and 2.2 keeps them.

### 2.1 Sound and windows

The core tools of section 1: `bg3_up`, `bg3_down`, `bg3_status`, `bg3_lua`, `bg3_wait`,
`bg3_state`, `bg3_events`, `bg3_logs`, `bg3_reload`, `bg3_dialog`, `bg3_fast_scenes`,
`bg3_checkpoint`, `bg3_reset`, `bg3_key`, `bg3_screenshot`, `bg3_call`, `bg3_api`, and the MCP
half of `gatekit sound`
([cli.md](cli.md) 8.4, [docs/design/deck.md](../design/deck.md), section "Sound"):

- **`bg3_sound`** - `{action: list|set|mute|unmute|unfocused, category?, volume?, on?}`: how loud
  this copy of the game is, so an agent watching several runs can quieten the ones it is not
  listening to. `list` (the default) gives the twelve categories with their volumes (0 to 100) and
  the game's own mute-when-not-in-front flag; `set` takes `category` and `volume`; `mute` silences
  a category, remembering how loud it was, and without one the master, which silences the copy;
  `unmute` puts it back; `unfocused` reads the flag, or sets it with `on`. Each answer is a
  sentence: `<category> set to <v>`, `muted <category>; it
  was at <v>, which unmute puts back`, `unmuted <keys> (...)` or `nothing to unmute: ...`. A `volume` outside 0..100
  and a `category` that is not a name of the game's or of GateKit's are `CopyError`s before
  anything reaches the game. It changes the running copy only, never the player's own settings.

And two for the MCP half of `gatekit window ...`
([docs/design/deck.md](../design/deck.md), [cli.md](cli.md) 8.8):

- **`bg3_windows`** - every running copy: instance, label, process id, its tile, and the tiles kept
  for a game that is away. No arguments. Needs no deck.
- **`bg3_window`** - `{action: maximize|grid|label|release|mirror|adopt|fps, instance?, label?, fps?}`:
  hold one copy open over the whole deck, put everything back in the grid, name a copy, give a copy
  its own window back, draw a copy from the frames it shares (`mirror`) or from its own window
  again (`adopt`), or say how fast a copy may draw (`fps`, 0 for no cap, no number to read it).
  `maximize`, `grid`, `release`, `mirror` and `adopt` need a running deck and say `CopyError: no
  deck is running` when there is none; `label` is kept in the state folder either way, and `fps`
  goes straight to the game through the bridge, so it needs no deck but does need GateKit's Script
  Extender build.

Neither touches a game. Reading a copy, driving it and photographing it work the same whether it is
in a tile, behind another window, out of view or in no deck at all.

### 2.2 Transport and calls

| What | How |
|---|---|
| a batch | an array is split into its messages, each handed to the session; the answers are collected back into one array, written when the last one is ready, in the order of the requests. A batch of notifications alone is answered with nothing; an empty array is an `InvalidRequest`. |
| a byte-order mark, a line that is no message | tolerated wherever a line starts. A line that cannot be a message (a build line, a stray print) is skipped; one that tried to be JSON (it starts with `{` or `[`) is answered with a parse error instead of being swallowed. |
| a long call | requests are served side by side: `ping`, `tools/list` and a second tool are answered while `bg3_up` or `bg3_reset` runs. Each call gets a `GameCopy` object of its own with its own output buffer, so two calls never mix their log lines. |
| plugin errors | the tools folder is re-read by modification time and an entry is dropped as soon as its file loads, is removed, or stops clashing. |

### 2.3 Not offered

- **Tools for Larian's Toolkit** (the editor): there is no Toolkit bridge.
- **Agent-play tools** of their own: every `GK.*` function (`GK.Act`, `GK.See`, ...) is callable
  through `bg3_call`, and `bg3_api` lists it.
- **An offline server**: the lookups over the databases are the CLI's (`data query`, `symbols`,
  `decomp`; [cli.md](cli.md) section 8).

### 2.4 Behaviour

1. **`serverInfo`** is `gatekit` with the assembly's version. Tool names are what an agent's
   prompts depend on.
2. **The protocol version** is negotiated by the SDK: it answers the newest version both sides know.
3. **Error text**: `<kind>: <message>` with `isError` (the kinds are at the top). A **missing
   required argument** is refused, for every tool, before or as the tool asks for it, with
   `CopyError: <tool> needs '<name>', which the call did not give. It takes:
   <every argument>. Nothing was done.` - the names a schema lists in `required` are checked before
   the tool runs; others when the tool asks for them. An unknown argument is refused the same way
   (`... does not take 'x' ... It takes: ...`). `gatekit mcp call` with arguments that are not one
   JSON object prints `ERROR: the arguments of <tool> are not JSON (<where>). What arrived: ...` and
   an example call, exit code 2, instead of a parser stack trace.
4. **`bg3_lua` does not read files.** The CLI's `@file.lua` is a CLI convenience; over MCP a code
   argument is only ever a chunk.
5. **`bg3_wait` uses Lua's own truth** (only `nil` and `false` are false), so waiting for `0` or an
   empty table finishes.
6. **Plugin tools are data, not code**: one JSON file per tool in `mcp-tools/`
   (`GATEKIT_MCP_TOOLS`), each a description plus one bridge call or Lua chunk, re-read when its
   modification time changes. A file that fails to load is named by `bg3_plugin_errors`, a tool
   listed only while there is such a file; a tool whose name is taken is skipped and named there
   too. The README has the format.
7. **`bg3_reload`'s `mod_root`** is resolved to a full path.
8. **`bg3_api`** reads the bridge mods `up` would install (`GATEKIT_BRIDGE`, default
   `<repo>/mods`), so it covers Weave as well as GateKit.

### 2.5 `bg3_status` names the copy

`bg3_status` answers with `instance`: the game copy this server drives, `null` for the installed
game. With several agents each driving a copy, it is how one checks that its tools reach its own.
`drives` says the same in words (`instance <name>` or `the installed game (no --instance)`) and
`channel` is the bridge folder the tools read and write. A side's heartbeat is shown as it is only
when a game of this copy runs and the heartbeat is fresh (5 s); otherwise the side reads
`{"gameState": null, "stale": "last heartbeat written <time>; no game of this copy is running"}`
(or "... has not answered for over 5 s" while the game runs), so a heartbeat left in a channel by
a game that has closed never reads as a live one. `film` is `true` while the game plays a recorded
film (`GK.Gui.FilmPlaying()`: the engine's video manager, read through Ext.Debug - such a film
otherwise reads as an endless StartLoading), `false` when it does not, `null` when the client
cannot say or no game runs.

Every Lua call (`bg3_lua`, `exec`, and every tool built on them) is refused at once, with nothing
queued, when no game process of this copy runs and its channel has no fresh heartbeat: `CopyError:
no game is running for <instance X | the installed game (no --instance)>; nothing was sent`. A call
that names no copy is for the installed game, refused unless it is named (above), and goes to the
installed game's own channel, which no copy writes (each copy has its own profile), so it never
reaches a copy.

---

## 3. The coverage loop (`CoverageTools.cs`): mapping the engine

The tools an agent maps the engine with, all through this server.
The Lua they use is part of the bridge (`Shared/Probe.lua`: `GK.Probe`, `GK.Seen`), and the data
work around it is C# (`Ledger.cs`, `Survey.cs`).

| Tool | What it does |
|---|---|
| `bg3_pause` | stops the world while an agent thinks; `GK.Pause` |
| `bg3_seen_start` / `_mark` / `_new` / `_drain` / `_status` | the standing capture: which types have holders, and their bytes. A reset, Lua reload or level change stops it; `_drain` then asks `GK.Seen.Status()` per side, drains the sides that run, and names the others under `not_running` (`no capture is running on <side> - ...; call bg3_seen_start`) instead of failing. A side that gives no answer at all (the server in the main menu stays in `UnloadLevel`) does not fail any of the five: `_drain` names it under `not_running` (`not drained: no answer from server ...`) and drains the other; `_start`, `_mark` and `_status` answer `{ gone }` for it, `_new` lists it under `gone` |
| `bg3_coverage_todo` | the ledger of the unseen (3.2) |
| `bg3_probe` | the per-offset survey of one component |
| `bg3_probe_fields` | raw bytes beside what Script Extender returns for the same instance |
| `bg3_recipe` | a confirmed finding writes `tests/_coverage/<name>.lua`, a test `gatekit test` replays (3.4), and a line in `findings.jsonl` |
| `bg3_run_report` | a run's hand-over and what it spent, in `runs.jsonl` |
| `bg3_wish` / `bg3_record` | the ledgers of the record (3.9): wishes under one id scheme; the counts, a run's yield, the record's own check |

### 3.1 What `bg3_pause` actually is

Baldur's Gate 3 has no pause a mod can press. `GK.Pause` is the engine's own time scale
(`ls::GlobalSwitches::DeltaTimeScale`) at its floor of 0.001, which the engine itself clamps:
one real second buys one millisecond of game time. Everything is frozen for practical purposes
and not mathematically stopped; the frame loop still runs, so the bridge still answers; sound is
mixed in real time and does not slow with it. The tool says so in its own description, because an
agent that believes it has a true pause will write a finding that depends on one. It stands as a
`GK.Speed` rule of the highest priority, so Speed's half-second poll cannot put the pace back
under it.

### 3.2 The groups of the ledger

Besides mapped / waiting / alive / dead - which say nothing about where work is left - the ledger
has the two groups that do:

- **`unseen`** - mapped, with a layout read off the game's code, but **no live instance has ever
  been observed**. The layout is a reading nothing has tested. This is the frontier.
- **`unverified`** - observed alive, but the fields have never been checked against the raw
  memory of the same instance.

`bg3_coverage_todo {"group": "unseen", "namespaces": true}` is what a run picks its theme from:
a namespace is usually one system, so a run that takes one has a theme and an end.

### 3.3 Evidence, enforced by the schema

`bg3_recipe` requires `evidence` (`sighting`, `repeated`, `replayed` or `field_value`) and
`reached_by` (`play`, `cheat` or `play_with_cheat_setup`), closed vocabularies validated before
anything is written (`GateKit.Copies.Record`); what they cannot say goes in `note`, as free text. A
`sighting` claim is recorded as a **lead**, not a finding, saying so in its answer. `cheat` is a
cause that is staged (an Osi call, a `GK.Stage` or `GK.Cheat` call, a request queued the way no
player can); `play_with_cheat_setup` is a staged setup and then the player's own action. A
sighting is one appearance; a finding needs the action
repeated, or a value forced, or the code read. How it was reached is part of the record, because
a component seen in ordinary play is a stronger fact than one that only appears after a cheat.

### 3.4 A recipe is a test that replays

`bg3_recipe` writes `tests/_coverage/<name>.lua` in the checkout the server runs from, and
`gatekit --instance <copy> test tests/_coverage -k <name>` replays it. A recipe written as free Lua for the game (`Osi.GetHostCharacter()` at the top
level), which the test runner cannot read on the host, is skipped by `test` as not replayable
([cli.md](cli.md) 3.1).

It takes, besides `component`, `name`, `evidence`, `reached_by`, `claim`, `fields`, `open`:

- `component` (required): the engine's full name of a component type. Refused, with nothing
  written, when it has no `::`, ends in `::` or `System`, or - when the checkout has
  `research/coverage/coverage.json` - is not one of its types; the refusal names up to six types
  it probably meant (under it as a namespace, or holding a word of `name`): a recipe of a bare
  namespace such as `esv` would fail every replay.

- `level` (required): `MODULE:LEVEL` the replay starts in, as `bg3_up` takes it; refused
  without the colon.
- `side`: `client` or `server`, where the component lives; read off an `esv::` / `ecl::` name
  when left out, refused for any other name without it.
- `steps` (required, non-empty), in order, each one of:
  `{"side": "client"|"server"|"ui", "lua": "...", "timeout"?: s}` - a chunk run there (an error
  fails the replay); `{"key": "<name>", "hold"?: s}` - a key, as `bg3_key` (`hold`: seconds it stays down,
  0-30, written as `game.key(name, hold)`; refused without `key`); `{"side": ..., "until":
  "<expression>", "timeout"?: s}` - waited for until truthy (default 30 s); `{"wait": s}` - a
  pause. Any step may carry `wait` (seconds let pass after it, 0-600) and `note` (a comment
  above it). An unknown field, two actions in one step, or `lua`/`until` without a side is
  refused with the step's number, and nothing is written.
- `within`: seconds the component may take to appear after the last step (default 10).

There is no `lua` argument: Lua goes into `steps` (the refusal for an unknown argument names what
is taken).

The file is the comment header (component, claim, evidence and how reached, level, what is
unresolved, the date and the replay line), then `test { level = ... }` and
`function run(game)`: wait for the server to be `Running`; `watch(side, component)` (from before
the first step, every tick, so a component that lives one frame is seen); the steps as
`game.lua` / `game.key` / `game.wait` / `game.sleep`; and `alive(side, component, within)`,
which **fails the test unless the component was held by some entity on its side** since the
watch began. The answer carries `written`, `replay` (the command line), `recorded_as` and `note`.

**A type already held before the steps fails the replay**: its being held after them proves
nothing, whatever the steps did. `watch` keeps
the holders it saw at the start; `alive(side, name, seconds)` fails with `<type> was already held
by N entities on <side> before the steps ...` when N > 0. A recipe of a type that persists passes
`bg3_recipe` `may_exist: true`, written as `alive(side, name, seconds, { may_exist = true })`: the
replay then passes only when the most holders in one tick, or the holders at the end, rise above
the count at the start, or an entity holds it that did not at the start (`... before the steps and
the steps added none` otherwise). `alive` returns `new` beside `before`, `most`, `now`, `ticks`,
`first`, and leaves what it saw in `RECIPE_ALIVE` for the runner, which writes it into
`replays.jsonl` ([cli.md](cli.md) 8.11).

A recipe that carries the comment `-- unreplayed since rewrite` was written from its own steps by
hand and carries it until it is replayed live.

### 3.5 `bg3_names` and `bg3_wiki` (no game needed)

`bg3_names` is `gatekit names` as a tool ([cli.md](cli.md) 8.9): `action` is `tally` (the
default), `worst`, `suggest` (with `component`, or a namespace `prefix`; `all`), `adopt` (`write:
true` writes), `set` (`names`: one object or an array of `{component, offset, name, source,
evidence, struct?, type?, legacy?}`; `write: false` only checks; `force`), or `pending`. Every
answer is JSON. The same refusals apply, and a refused batch writes nothing.

`bg3_wiki` is `gatekit wiki` as a tool ([cli.md](cli.md) 8.9): `text` with `action` `find`
(the default), `page`, `ids`, `uid`, `card`, `rules`, `table` or `tables` (which needs no `text`).
`chars` cuts a page; `refresh` asks the wiki again. `card` answers an array of `{table, page, ids,
row}`. `rules` answers `{title, revision, sections: [{level, heading, text, links, unsure}]}` with
each section's text cut to 400 characters, or with `section` only the sections whose heading
contains it, whole. `table` takes the table's name as `text`, `where` as `"field=text;
field=text"`, `fields` (comma-separated, default `page,name,uid,uuid`) and `limit` (default 30),
and answers `{rows, shown}`. The pacing and the fifteen quiet minutes after a refusal are the
reader's and apply here as they do on the command line; a refusal comes back as the tool's error.

### 3.6 `bg3_registry_note`, `bg3_registry_withdraw`, `bg3_registry_at`, `bg3_registry_show` (no game needed)

The registry's live layer as tools ([cli.md](cli.md) 8.16.1), for agents
that find code while they work in the game. `bg3_registry_note` is `registry note`: `id` with
`kind`, `at`, `rung`, `does`, `about`, `evidence` (strings `kind:sentence`), `tests`, `rests_on`,
`would_confirm`, `ask` with `ask_about`, `fields` (`offset:name[:type]`), `who`, `task`; it answers
what `bg3_registry_show` answers for the id. `bg3_registry_withdraw` is `registry withdraw`: `id`,
`all` (default false), `who`; it answers `{id, withdrawn: [...], now?}`, `now` the claim the id
says after it, and an id with no claim (of that writer) is the tool's error. `bg3_registry_at` takes `address` and answers
`{address, function?, decompiler?, symbols?, committed: [...], live: [...]}`; `bg3_registry_show`
takes `id` (or a name another source gives the thing) and answers `{id, committed?, file?, places?,
live: [...]}`. Every item of `committed` and `live` carries `id`, `live`, `rung?`, `says?`, and a live
one `who`, `when`, `task`, `at?`. A refusal - a guess without its reason, an entry its schema refuses
- comes back as the tool's error.

### 3.7 `bg3_lead`: no stretch without a lead (no game needed)

`bg3_lead` is `gatekit leads` as a tool ([cli.md](cli.md) 8.22): `action` is `tally` (the
default), `next` (`instrument`, `count`, `instance`), `report` (`type`, `outcome` lit|empty|blocked,
`note`; `instrument`, `instance`, `run`), `show` (`type`: the row in full), `list` (`class`,
`state`, `reachable`, `prefix`, `limit`, default 30) or `build` (`wiki`, default true). `instance`
defaults to `GATEKIT_INSTANCE` (the server's own copy). Every answer is JSON: a row, an array of
rows, the tally, or `{lead, wish}` for a report (`wish` is null unless the report parked the lead).
The refusals are the command's, and a refused report writes nothing.

### 3.8 A capture survives a load

`GK.Seen.Start` (`bg3_seen_start`) **arms** the capture: its options are kept in
`<channel>/seen_armed_<side>.json` and the layouts it was handed in `seen_specs_<side>.json`. At
every change of game state the capture **spills** what it has queued and the types it has met into
`seen_spill_<side>.json` (each sample with the level it was taken in, on the server). When a level
or savegame load resets the Lua state, the bridge's bootstrap starts an armed capture again at once
- before the level's first tick - with the same options, layouts and label; `GK.Seen.Status`
says `Resumed` and lists the `Loads` it went through. `bg3_seen_drain` takes the spilled samples
first (with their own level), and `GK.Seen.Types` includes the types met before the load.
`GK.Seen.Stop` disarms it. `up` deletes the armed and spill files of a copy (a new start is not
re-armed; drain before closing). What it misses: what the engine creates and removes between the
last tick of the old Lua state and the first of the new one.

### 3.9 The ledgers: who saw what, when, and the wishes

The record a run leaves is queried through append-only ledgers in `research/coverage/record/`
(their rows and vocabularies: [cli.md](cli.md) 8.11).

- **`bg3_seen_start`** takes `role`, `run` and `stretch` beside its options; this server keeps them
  for every row it writes afterwards, and starts a new *capture* (instance and start time). Not
  given: `GATEKIT_ROLE` / `GATEKIT_RUN` / `GATEKIT_STRETCH`, else `unknown`.
- **`bg3_seen_new`** and **`bg3_seen_drain`** write `sightings.jsonl`: a type on a side the index
  did not hold before the call, with no `sighted` row yet, is `sighted`; a type already known is
  `repeated` at most once per run and side, and never in a capture that already wrote a row for it
  ("seen again" means in a later capture). `source` is `tick` (the standing capture); `trace` is
  reserved for the tracer. `seen/index.json` is written too; a drain never lowers a type's
  `samples` there (the raw sample files are not committed, so a fresh checkout holds fewer than the
  record counts). Both tools write to `ToolHost.CoverageRoot` when the host names one (tests), else
  to `research/coverage` of the repository.
- **`bg3_run_report`** takes `run`, `stretch` and `role` from the same source as `bg3_seen_start`,
  not from the run's name: a `run` given is kept as `name`.
- **`bg3_wish`** `{"action": "add", "text", "from"?, "state"?, "note"?, "commit"?}` answers the
  new row (its id, W-0001 and on); `{"action": "update", "id", "state", "commit"?, "note"?}`
  appends the new state; `{"action": "list", "state"?}` lists them as they stand.
- **`bg3_record`** `{"action": "tally"}` (default), `{"action": "yield", "run"?}` and
  `{"action": "check"}` answer what `gatekit record tally`, `yield` and `check` print, as JSON
  (`check`: `{ok: true}` or `{ok: false, problems: [...]}`).

### 3.10 The comparison in the tick of capture

For every type Script Extender has a layout for, the standing capture compares the raw bytes of a
sample against the fields read through the extender **in the tick of capture**, and records the
types that agree as `verified`:

- `bg3_seen_start` hands every layout `bg3_verify` would compare (status confirmed or declared, not
  held, not a system) to each side first, 120 at a time, with `GK.Seen.AddSpecs(specs, reset)`;
  the capture then reads each sample of such a type through Script Extender in the tick it takes
  the bytes. Its answer carries `layouts` per side: how many types the bridge holds, or why none
  were handed over (a bridge without `AddSpecs`: `bridge-reload`; the capture starts anyway).
- `bg3_seen_drain` compares those samples with `LayoutCheck.Compare` - the same comparison as
  `bg3_verify`: a row counts only if Script Extender gave the component, the bytes cover the type,
  and at least one field could be compared (a type with no fields counts by being present) - and
  writes the types that agreed into `coverage.json` as `verified` (`by: bg3_seen_drain`,
  `stage: ... read in the tick of capture`), only for a mapped row of that side not verified
  before. The answer adds `agreed_in_the_tick_of_capture`, `recorded_as_verified` (per side) and
  up to 20 `disagreements`; a disagreeing type is not recorded.

`bg3_verify` uses the same `Compare`, which refuses to count an instance where no field could be
compared.

## 4. Attention and event-ended waits

- **Every answer of a game tool** (`bg3_lua`, `bg3_call`, `bg3_state`, `bg3_events`, `bg3_dialog`,
  `bg3_key`, `bg3_screenshot`, `bg3_status`, `bg3_checkpoint`, `bg3_reset`, `bg3_reload`,
  `bg3_fast_scenes`, `bg3_sound` and the plugin tools) gets one more text block when anything waits
  in the game or changed since the caller's last call: `attention: [{seq?, what, window?,
  choices?, waiting?, side, since_ms, ...}]` (docs/contracts/bridge-protocol.md, "Attention").
  The tool's own blocks are unchanged; with nothing to say there is no block. Nothing is read when
  the client's heartbeat is older than 15 s.
- **`bg3_wait`**: `expression` and `side` are optional (side default `client`), and it takes
  `until_attention` (default true). It ends the moment a new attention item appears (the event
  log's files are read every 25 ms; no call into the game), when the expression is truthy (asked every 0.3 s),
  or on the timeout. An expression that came true answers its value; an expression that timed out
  fails; otherwise the answer is `{endedBy: "attention" | "timeout", attention: [...], waitedMs}`.
  The wait starts from the caller's own cursor: an event that came after the caller's previous
  call and that it has not been told (a prompt answered between two waits, a fight that started
  and ended between calls) ends the wait at once. Items carry `since_ticks` beside `since_ms`
  (bridge-protocol.md).
- **Where a dialogue stands** (client, read every tick while one is open): `dialogue_choice`
  `{answers: [{n, text, tags?, enabled?}], speaker, text, canTrade?, ready: true}` the moment the
  answers show, and again (new seq) for every new set of answers; `dialogue_continue` when it waits
  for a click; while a line is spoken a waiting entry `dialogue_line` with `ready: false` and no
  event. A roll out of the dialogue is `roll` (with `check, skill, dc, advantage, modifiers,
  bonus`; its `roll_ended` adds `natural, total, success, critical`), a trade `trade`, its end
  `dialogue_ended`. A trade window, a game-over box, the load window and a quick load each end a
  running `bg3_wait` a frame or two after the game shows them.
- **One view**: the items of both sides are one list in the event stream's order, each with its
  `side`; a waiting state both sides report (the game state under the key `state`) is one item
  (bridge-protocol.md, "One stream"). The attention block and `bg3_wait`'s items are held to
  `common/attention`: a misfit is said in the block, and fails `bg3_wait`.

## 5. `bg3_events`: the event log as one stream

Observation. `{since?, kinds?, family?, limit?}` -> `{rows, next, more?}` (`schemas/events/answer`,
bound to the tool in `schemas/index.json`; an answer that does not fit fails the call). `rows` are
both sides' rows merged in the stream's order, each with `side` (bridge-protocol.md, "One
stream"); `next` is the cursor to pass as `since`; `limit` (default 200) cuts the answer and sets
`more`. It reads the log's files and asks nothing of the game. `GK.Events` stays inside the game as the
journal view of one side's log.
