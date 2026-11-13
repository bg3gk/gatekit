# MCP contract (derived from the Python code of the old repo)


> **What this is.** A record of the Python stand this toolkit grew out of, read out of its
> code line by line during the move to C#, so that nothing it did was lost in the port.
> It describes **that** program, not the one in this repository: the `file:line` references
> are to a repository that is not published, and "Proposed" marks what was then a
> suggestion. For what the toolkit does today, read the program's own `--help`, `AGENTS.md`
> and the code. The one document here that is a live contract is `bridge-protocol.md`.

Source: the old repository at commit `0cc2327`. "Proposed" marks suggestions for the C# port. Two servers exist; both speak MCP (protocol version echoed, default `2024-11-05`) as JSON-RPC over stdio, one JSON message per line, standard library only:

| Registered name (`.mcp.json`) | Command | `serverInfo.name` / version actually answered | File |
|---|---|---|---|
| `bg3` | `python stand/mcp_server.py` | `bg3-gatekit` / `0.1.0` | `stand/mcp_server.py` (+ `stand/mcp_tools/*.py`) |
| `bg3-offline` | `python kit/mcp_server.py` | `bg3-gatekit-offline` / `0.1` | `kit/mcp_server.py` |

(The names `bg3` / `bg3-offline` exist only in `.mcp.json`, the READMEs and `claude mcp add bg3 -- ...`; the servers themselves answer the `serverInfo` names above. All tool names are in the `bg3_*` family; the two servers have **no overlapping tool names**.)

Counts: server `bg3`: 17 core + 5 toolkit + 5 coverage plugin + 25 agent plugin = **52 tools** listed by default (+1 `bg3_plugin_errors` only while a plugin fails to load; `_example.py` would offer `bg3_example_gold` only if copied to a name not starting with `_`). Server `bg3-offline`: **7 tools**. Total 59.

---

## 1. Server `bg3` (`stand/mcp_server.py`)

### 1.1 Process and protocol behaviour

- Start: `python stand/mcp_server.py`. Proposed: `gatekit mcp serve` (and `gatekit mcp serve --offline` for the second server).
- IO (`main()`, `:470-484`): reads stdin as UTF-8 with BOM tolerated (`utf-8-sig`); one JSON object per line; blank lines skipped; **lines that are not valid JSON are silently skipped** (`except ValueError: continue`); writes replies as UTF-8 `\n`-terminated lines (`ensure_ascii=False`) and flushes after each. `sys.stdout` is replaced by `sys.stderr` so stray prints cannot corrupt the protocol. Processing is strictly sequential, single-threaded: while a long tool (e.g. `bg3_up`, 40-90 s; `bg3_tour`, minutes) runs, nothing else, not even `ping`, is answered.
- A JSON array (batch) as a line makes `handle()` raise `AttributeError` (`list` has no `get`); `main` only catches `ValueError`, so the server **dies**.
- `handle(message)` (`:438-467`):
  - `initialize` -> `{"protocolVersion": <params.protocolVersion or "2024-11-05">, "capabilities": {"tools": {}}, "serverInfo": {"name": "bg3-gatekit", "version": "0.1.0"}}` (echoes the client's version).
  - `tools/list` -> `{"tools": TOOLS + TOOLKIT_TOOLS + plugin_tools()}` (no pagination, no `nextCursor`).
  - `tools/call` -> see 1.3.
  - `ping` -> `{}`.
  - any other method **without** an `id` (a notification such as `notifications/initialized`) -> no reply.
  - any other method with an `id` -> `{"jsonrpc":"2.0","id":<id>,"error":{"code":-32601,"message":"unknown method <method>"}}`.
  - Replies are `{"jsonrpc":"2.0","id":<id>,"result":<result>}`. (An `initialize`/`tools/list`/`tools/call`/`ping` message with a missing `id` still produces a reply with `"id": null`.)
- No resources, prompts, logging, progress notifications or `listChanged` capability. Tool-list changes (plugins) become visible only because clients re-request `tools/list`.

### 1.2 Instance selection

`GAME = gatekit.Game()` at import; `gatekit` selects the instance **at import** from `BG3_GATEKIT_INSTANCE` (the legacy `BG3_DEVKIT_INSTANCE` is aliased to it), because `--instance` is parsed only when `gatekit.py` runs as a script. One server process = one instance (or the installed game when unset). All tools (`bg3_up`, `bg3_down`, screenshots into `<STATE>\shots`, logs, ...) act on that instance. Other env variables (`BG3_GAME`, `BG3_GATEKIT_SE`, `BG3_GATEKIT_START_TIMEOUT`, `GK_AGENT_*`) are inherited by the server and its `up()` calls. `.mcp.json` launches the server with the client's environment; to use an instance, set `env` in the MCP server config.

### 1.3 `tools/call` flow and error behaviour

`handle` takes `params = message["params"]` and, inside one `try`:
1. `owners = load_plugins()` (mtime check, see 1.4).
2. If `params.name` is a plugin tool: stdout redirected to stderr; `text(module.call(GAME, name, arguments or {}))`.
3. elif name == `bg3_plugin_errors` -> `text(_plugin_errors or "no errors")` (callable even when not listed).
4. elif name starts with `bg3_toolkit_` -> `call_toolkit(name, args)`: success -> `text(...)`; `ToolkitError` -> `{"content":[{"type":"text","text":"ToolkitError: <msg>"}],"isError":true}`; an unknown suffix raises `StandError("unknown tool ...")`.
5. else `call(name, args)` (core tools); unknown -> `StandError("unknown tool <name>")`.

Any exception (a `KeyError` for a missing required argument, `StandError`, `TypeError`, ...) -> `{"content":[{"type":"text","text":"<ExceptionClassName>: <message>"}],"isError":true}`; the server continues. E.g. a missing `side` gives the text `KeyError: 'side'`. There is **no JSON-Schema validation** of arguments; types are whatever JSON delivered (`int()`/`float()` casts happen per tool).

Result helper `text(value)` (`:250`): `str` -> as is; anything else -> `json.dumps(value, ensure_ascii=False, indent=1)`; wrapped as `{"content":[{"type":"text","text":...}]}`. The only non-text result is `bg3_screenshot` (image content). `structuredContent` is never used.

### 1.4 Plugin mechanism (`stand/mcp_tools/*.py`, `mcp_server.py:376-435`, example in `_example.py`)

- Every `stand/mcp_tools/*.py` whose file name does **not** start with `_` is a plugin, processed sorted by file name (`coverage.py` before `situation.py`).
- A plugin module must define `TOOLS` (a **list** of `{name, description, inputSchema}`) and a callable `call(game, name, args)`; otherwise it fails with `TypeError: needs a list TOOLS and a function call(game, name, args)`. `game` is the server's single `gatekit.Game()`. `call` returns text or anything JSON can carry (through `text()`); an exception becomes the tool's error answer.
- Naming rule (documented, not enforced): names start with `bg3_`. A plugin tool whose name equals a core/toolkit tool name, or one already provided by an earlier plugin, is **skipped** and the file gets the error `tool name '<n>' is already taken` (other tools of that file still load).
- Reload: `load_plugins()` runs on **every** `tools/list` and `tools/call`; per file it compares `st_mtime` with the cached stamp and re-executes the file when it differs (`spec_from_file_location("mcp_tools_<stem>")`, stdout redirected to stderr during import); a vanished file is dropped from the cache. Modules imported *by* a plugin (`situation`, `probe`, `seen`, `tour`) are NOT reloaded.
- Failures never kill the server: the exception is stored as `"<ExceptionType>: <message>"` in `_plugin_errors[<file name>]` and the plugin is dropped. Quirk: an entry is removed only when that file later loads successfully (a deleted file or a name clash leaves a stale entry).
- Synthetic tool `bg3_plugin_errors` (`inputSchema` `{"type":"object","properties":{}}`) is appended to `tools/list` only while `_plugin_errors` is non-empty; its description is `Plugin files in stand/mcp_tools that failed to load: <file>: <error>; ...`; calling it returns the dict as JSON text or `no errors`.
- Both shipped plugins insert their own folders into `sys.path` (`se-fork/coverage` for `coverage.py`, `stand` for `situation.py`) and import `probe`/`seen`/`tour` or `situation`/`gatekit` lazily inside `call`.
- Proposed (C#): reload-on-change is optional; the *tool names, schemas and result shapes* below are what must match.

### 1.5 Timing notes

Tool calls run inline on the read loop. Long calls: `bg3_up` (start wait up to ~375 s worst case), `bg3_toolkit_up` (up to ~400 s), `bg3_tour` (minutes), `bg3_agent_do` (waits for a job up to 90 s), `bg3_agent_load` (up to 180 s), `bg3_reset` (120 s). Default per-call Lua timeout 20 s.

### 1.6 Core tools (17; `TOOLS`, `stand/mcp_server.py:94-247`, dispatched by `call` `:256`)


#### `bg3_up`

- **Calls**: `gatekit.up(game_args or None, level=level, mods=mods, story_log=bool(story_log))` (`stand/mcp_server.py:257`) with stdout captured. NOTE: there is no `save` argument (the CLI has `--save`); `mods: []` (empty array) is not None and so still triggers mod isolation; the 300 s start timeout is fixed. Blocks the whole server for ~40-90 s (see 1.5).
- **Result**: Text: everything `up` printed (state transitions `  12.3 s  Running`, `Script Extender for this run: ...`, `bridge pak rebuilt ...`, `playable after N s`), or `playable` if nothing was printed.
- **Errors**: Any `StandError` (already running, bridge never loaded, ...) -> isError.
- **Definition** (verbatim `tools/list` entry):
```json
{
  "name": "bg3_up",
  "description": "Start Baldur's Gate 3 directly (no Steam window, launcher, logo video or menu) and wait until the last save is loaded and playable. Takes about a minute. Backs up the saves and mod list first; bg3_down restores them.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "level": {
        "type": "string",
        "description": "MODULE:LEVEL, e.g. GustavX:TUT_Avernus_C. Starts a fresh test game straight in that level (about 40 s) instead of the last save; only the base game, the bridge and `mods` are loaded."
      },
      "mods": {
        "type": "array",
        "items": {
          "type": "string"
        },
        "description": "Mod folder names to load besides the base game. Omit without `level` to keep the player's own mod list."
      },
      "story_log": {
        "type": "boolean",
        "description": "Also record the Osiris trace for bg3_logs story=true."
      },
      "game_args": {
        "type": "array",
        "items": {
          "type": "string"
        },
        "description": "Extra game arguments."
      }
    }
  }
}
```

#### `bg3_down`

- **Calls**: `gatekit.down()` (`:263`).
- **Result**: Text `game closed, saves and mod list restored` (the same text for an instance).
- **Errors**: Exceptions -> isError.
- **Definition** (verbatim `tools/list` entry):
```json
{
  "name": "bg3_down",
  "description": "Close the game and restore the saves and mod list to what they were before bg3_up.",
  "inputSchema": {
    "type": "object",
    "properties": {}
  }
}
```

#### `bg3_status`

- **Calls**: `gatekit.game_pids()`, `Game.state('client'|'server', 1e9)`, `Game.state('client')` (5 s freshness).
- **Result**: JSON text: `{"processes":[pid...],"client":<last heartbeat or null>,"server":<...>,"client_alive":bool}`.
- **Errors**: Does not raise for a missing game.
- **Definition** (verbatim `tools/list` entry):
```json
{
  "name": "bg3_status",
  "description": "Whether the game runs and what state each side (client, server) last reported (Menu, LoadLevel, Running, ...).",
  "inputSchema": {
    "type": "object",
    "properties": {}
  }
}
```

#### `bg3_lua`

- **Calls**: `Game.lua(side, code, args.get('timeout', 20))` (`:270`). `ui` side = client with `ui:true`. The code gets the `GK`/`DK` preamble (see cli.md 2.8).
- **Result**: Text: log lines printed by the chunk (`  [<side>] <line>` per `GK.Log` call) joined by newlines, then a newline, then `json.dumps(result, ensure_ascii=False, indent=1)` (`null` for nil). Without log lines only the JSON.
- **Errors**: Lua error -> isError with `StandError: Lua error on <side>:\n<traceback>`; no answer in `timeout` s -> `StandError: no answer from <side> in N s (state: ...)`.
- **Definition** (verbatim `tools/list` entry):
```json
{
  "name": "bg3_lua",
  "description": "Run a Lua chunk inside the game through Script Extender and return what it returns, as JSON. side 'client' has visuals, UI and Ext.System.ClientTimeline; side 'server' has Osi.* and game rules. `GK.Log(...)` prints lines; `GK.Dump(entity, {\"Comp\", ...})` reads components into plain tables. Server helpers (who = character uuid, default the host): GK.Character(who), GK.Party(), GK.Give(template, count, who), GK.Find(template, who), GK.Equip(itemOrTemplate, who), GK.Unequip(item, who), GK.Unsheath(bool, who), GK.Teleport(x, y, z, who) or GK.Teleport(targetUuid), GK.Spawn(template[, x, y, z]), GK.Remove(uuid), GK.Watch(osirisEvent, arity), GK.Events(since). Client helpers: GK.Client(), GK.Scenes(withTracks), GK.Tracks(timelineId, \"TLShowWeapon\"), GK.Key(\"ESCAPE\"), GK.Events(since). Side 'ui' is the client inside the UI context, required for: GK.Dialog(), GK.Answer(n), GK.Commands(), GK.Command(name, param), GK.ViewModels(), GK.Properties(type) and anything under Ext.UI. The window does not need focus.",
  "inputSchema": {
    "type": "object",
    "required": [
      "side",
      "code"
    ],
    "properties": {
      "side": {
        "type": "string",
        "enum": [
          "client",
          "server",
          "ui"
        ]
      },
      "code": {
        "type": "string"
      },
      "timeout": {
        "type": "number",
        "description": "Seconds to wait for the answer (default 20)."
      }
    }
  }
}
```

#### `bg3_wait`

- **Calls**: `Game.wait(side, expression, args.get('timeout', 30))` - polls `return (<expression>)` every 0.3 s. The schema enumerates only client/server, but `ui` would also work in the code.
- **Result**: Text: the truthy value (a string verbatim, anything else as indented JSON).
- **Errors**: Timeout -> `StandError: timed out waiting on <side> for: <expression>`; Lua errors as above.
- **Definition** (verbatim `tools/list` entry):
```json
{
  "name": "bg3_wait",
  "description": "Poll a Lua expression on one side until it is truthy; returns its value. Fails on timeout.",
  "inputSchema": {
    "type": "object",
    "required": [
      "side",
      "expression"
    ],
    "properties": {
      "side": {
        "type": "string",
        "enum": [
          "client",
          "server"
        ]
      },
      "expression": {
        "type": "string"
      },
      "timeout": {
        "type": "number",
        "description": "Seconds (default 30)."
      }
    }
  }
}
```

#### `bg3_state`

- **Calls**: Client: `return { client = GK.Client(), scenes = GK.Scenes() }`; server: `return GK.Party()` (20 s each).
- **Result**: JSON text `{"client":..., "scenes":..., "party":...}` (the client table's keys spread, plus `party`).
- **Errors**: Bridge function missing / game not running -> isError.
- **Definition** (verbatim `tools/list` entry):
```json
{
  "name": "bg3_state",
  "description": "The game in one call, no screenshot needed: client (game state, controller mode, controlled character), live scenes (dialogues, cutscenes: phase, time, actors) and every party member (name, level, position, health, in combat, weapons drawn, equipment by slot).",
  "inputSchema": {
    "type": "object",
    "properties": {}
  }
}
```

#### `bg3_events`

- **Calls**: Client `return GK.Events(<since_client>)` and server `return GK.Events(<since_server>)` (ints, default 0).
- **Result**: JSON text `{"client":<GK.Events result>,"server":<...>}`; each has `last` to pass back as `since_*`.
- **Errors**: isError on failure.
- **Definition** (verbatim `tools/list` entry):
```json
{
  "name": "bg3_events",
  "description": "What happened in the game since event number `since` (0: everything kept, up to 500 per side): game state changes and Osiris events such as DialogStarted, DialogEnded, Equipped, Unequipped, TemplateAddedTo, CombatStarted, EnteredLevel. Returns `last` to pass as `since` next time. More events: bg3_lua server `GK.Watch(\"EventName\", arity)`.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "since_client": {
        "type": "integer"
      },
      "since_server": {
        "type": "integer"
      }
    }
  }
}
```

#### `bg3_logs`

- **Calls**: `gatekit.read_story_log` if `story` else `gatekit.read_console` with `lines=int(args.get('lines',60))`, `grep`. Console reading spawns `gatekit.py _console <pid>`.
- **Result**: Text: the lines joined by `\n`, or `(nothing)`.
- **Errors**: No game -> `StandError: the game is not running` (console only).
- **Definition** (verbatim `tools/list` entry):
```json
{
  "name": "bg3_logs",
  "description": "Text of the Script Extender console: mods' prints, warnings, Lua errors. With story=true the Osiris trace instead (every story event, rule fired and fact changed; needs bg3_up with story_log=true).",
  "inputSchema": {
    "type": "object",
    "properties": {
      "lines": {
        "type": "integer",
        "description": "How many of the last lines (default 60)."
      },
      "grep": {
        "type": "string",
        "description": "Only lines matching this regular expression."
      },
      "story": {
        "type": "boolean"
      }
    }
  }
}
```

#### `bg3_reload`

- **Calls**: `gatekit.link(Path(mod_root))` when `mod_root` is given (no `.resolve()`, unlike the CLI) else the already linked names; then `Game.reload()` (30 s).
- **Result**: Text `reloaded <A, B> in X.X s`.
- **Errors**: `StandError: no Mods\<Folder>\ScriptExtender\Lua under ...`, `nothing is linked ...`, `<side> Lua did not come back after the reset ...`.
- **Definition** (verbatim `tools/list` entry):
```json
{
  "name": "bg3_reload",
  "description": "Hot reload: copy a mod's Lua into the running game and restart Lua in place (about a second, no game restart). Pass mod_root once (the folder holding Mods\\<Folder>\\ScriptExtender\\Lua) to link a mod; later calls reload everything linked. Check bg3_logs afterwards for Lua errors.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "mod_root": {
        "type": "string"
      }
    }
  }
}
```

#### `bg3_dialog`

- **Calls**: `Game.answer(int(answer))` if `answer` truthy (so `0`/absent = read only) else `Game.dialog()`; both via `ui` side (`GK.Answer`, `GK.Dialog`).
- **Result**: Text: the dialogue table as indented JSON, or `no dialogue`.
- **Errors**: `StandError: the dialogue did not change after answer N: ...`.
- **Definition** (verbatim `tools/list` entry):
```json
{
  "name": "bg3_dialog",
  "description": "The dialogue on screen: speaker, the line being said, and the numbered answers the player can choose. Pass `answer` to choose one first; the result is then the dialogue after it moved on ('no dialogue' when it ended). Lines without choices show a single answer such as 'Continue' - choose 1 to advance.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "answer": {
        "type": "integer",
        "description": "Answer number to choose, as listed."
      }
    }
  }
}
```

#### `bg3_fast_scenes`

- **Calls**: `Game.fast_scenes(float(speed or 20))` -> client `GK.FastScenes(<float>)`.
- **Result**: Text `scenes now play at x<speed:g>`.
- **Errors**: isError on failure.
- **Definition** (verbatim `tools/list` entry):
```json
{
  "name": "bg3_fast_scenes",
  "description": "Play dialogues and cutscenes `speed` times faster (default 20) so they reach the next choice almost at once; nothing is skipped, the story still passes every node. Stays on for later scenes too. speed 1 returns to normal.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "speed": {
        "type": "number"
      }
    }
  }
}
```

#### `bg3_call`

- **Calls**: `call_function(side, function, args or [], float(timeout or 20))` (`:331`). Validates that every dot-separated part of `function` (after stripping a leading `GK.`) is a Python identifier, else `StandError: not a function path: ...`. Generates Lua: walk `GK` by the path; error `GK.<path> is not a function on this side`; arguments passed as `Ext.Json.Parse([=[<json>]=])` using a long-bracket level 1..7 that the payload does not contain, `table.unpack(a, 1, <n>)`.
- **Result**: Text of the call's return value (string verbatim, otherwise indented JSON).
- **Errors**: isError with the Lua error text.
- **Definition** (verbatim `tools/list` entry):
```json
{
  "name": "bg3_call",
  "description": "Call any function of the bridge by name with JSON arguments and get its result: {side: 'server', function: 'Stage.Fight', args: [...]} calls GK.Stage.Fight(...). A function added to the bridge's Lua is callable this way at once (after `gatekit.py reload`), with no tool of its own. Side 'ui' for functions that touch the game's UI. bg3_api lists what there is.",
  "inputSchema": {
    "type": "object",
    "required": [
      "side",
      "function"
    ],
    "properties": {
      "side": {
        "type": "string",
        "enum": [
          "client",
          "server",
          "ui"
        ]
      },
      "function": {
        "type": "string",
        "description": "Path under GK, e.g. Party or Stage.Fight."
      },
      "args": {
        "type": "array",
        "description": "Positional arguments, as JSON values."
      },
      "timeout": {
        "type": "number",
        "description": "Seconds (default 20)."
      }
    }
  }
}
```

#### `bg3_api`

- **Calls**: `bridge_api(filter)` (`:354`): pure file scan, no game needed. Reads every `*.lua` under `stand\bridge\Mods\Gwynerva_GateKit\ScriptExtender\Lua` (sorted), matches `^function\s+(GK(?:\.\w+)+)\s*\(([^)]*)\)`, takes the preceding `---` comment lines as `about`; `filter` = case-insensitive substring of the entry's JSON.
- **Result**: JSON text: list of `{"function":"GK.X.Y","parameters":"a, b","where":"Server/Foo.lua","about":"..."}`.
- **Errors**: -
- **Definition** (verbatim `tools/list` entry):
```json
{
  "name": "bg3_api",
  "description": "The bridge's functions (GK.*) as written in its Lua source: name, parameters, the comment above each, and the file (its folder tells the side: Server, Client, Shared). Works without a running game. `filter` narrows by any text.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "filter": {
        "type": "string"
      }
    }
  }
}
```

#### `bg3_checkpoint`

- **Calls**: `Game.checkpoint()` (40 s).
- **Result**: Text `checkpoint saved: <save folder name>`.
- **Errors**: `StandError: the game made no save for the checkpoint` etc.
- **Definition** (verbatim `tools/list` entry):
```json
{
  "name": "bg3_checkpoint",
  "description": "Save the running game's world as it is now, so bg3_reset can return to it. Use right after bg3_up, before changing anything. The save is the stand's own and disappears with bg3_down.",
  "inputSchema": {
    "type": "object",
    "properties": {}
  }
}
```

#### `bg3_reset`

- **Calls**: `Game.reset()` (120 s, skip_intro true).
- **Result**: Text `world reset in X.X s`.
- **Errors**: `StandError: no checkpoint to return to...`, `<side> did not come back from the reset ...`.
- **Definition** (verbatim `tools/list` entry):
```json
{
  "name": "bg3_reset",
  "description": "Bring back the world of the last bg3_checkpoint without restarting the game: about 12 s instead of a 30-45 s restart. Positions, items, gold, flags and story state return to the checkpoint; Lua state of mods is restarted.",
  "inputSchema": {
    "type": "object",
    "properties": {}
  }
}
```

#### `bg3_key`

- **Calls**: `Game.key(key)` (PostMessage via window.ps1).
- **Result**: Text `pressed <key>`.
- **Errors**: `StandError: unknown key ...` / `key not sent: ...`.
- **Definition** (verbatim `tools/list` entry):
```json
{
  "name": "bg3_key",
  "description": "Press a key in the game; works while the game window is in the background. Names: escape, enter, space, tab, backspace, left, up, right, down, f1..f12, or one letter or digit. Mouse clicks cannot be sent this way - drive the UI with bg3_lua.",
  "inputSchema": {
    "type": "object",
    "required": [
      "key"
    ],
    "properties": {
      "key": {
        "type": "string"
      }
    }
  }
}
```

#### `bg3_screenshot`

- **Calls**: `Game.shot(<STATE>\shots\mcp-HHMMSS.jpg, int(width or 1150))`, then base64 of the file.
- **Result**: NOT text: `{"content":[{"type":"image","data":"<base64 jpeg>","mimeType":"image/jpeg"}]}`. The file stays on disk (same-second calls overwrite).
- **Errors**: `StandError: screenshot failed: ...`.
- **Definition** (verbatim `tools/list` entry):
```json
{
  "name": "bg3_screenshot",
  "description": "Screenshot of the game window. Works while the window is in the background or covered by other windows; it is not moved or focused.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "width": {
        "type": "integer",
        "description": "Scale down to this width in pixels (default 1150)."
      }
    }
  }
}
```

### 1.7 Toolkit tools (5, `TOOLKIT_TOOLS`, `stand/mcp_server.py:24-68`, dispatched by `call_toolkit` `:71`)

All are always listed (the Toolkit need not run); `ToolkitError` is mapped as described in 1.3. Windows-only functionality (the official editor `Glasses.exe`; see cli.md section 5).


#### `bg3_toolkit_up`

- **Calls**: `toolkit.up(project, level, save)` (stdout captured). Timeouts as in cli.md section 5. The schema has no timeout.
- **Result**: Text: what `toolkit.up` printed (`  12.3 s  editor ready, bridge answering` ...) or `editor ready`.
- **Errors**: `ToolkitError` -> isError with text `ToolkitError: <msg>`.
- **Definition**:
```json
{
  "name": "bg3_toolkit_up",
  "description": "Start the official BG3 Toolkit (the level editor, 'Glasses') directly, with the GateKit bridge inside it, optionally opening a project (a mod, by name) and loading a level (the game's own levels work, e.g. TUT_Avernus_C). The editor answers after about 20 s; a level takes one to two minutes more and several GB of memory. The Toolkit and the game should not run at the same time on a 32 GB machine.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "project": {
        "type": "string"
      },
      "level": {
        "type": "string"
      },
      "save": {
        "type": "string",
        "description": "A save to load on top of the level (untested)."
      }
    }
  }
}
```

#### `bg3_toolkit_eval`

- **Calls**: `toolkit.evaluate(expression, float(timeout or 60))`. Unlike the CLI, single quotes are NOT converted to double quotes.
- **Result**: `text(value)`: a string result verbatim, else indented JSON.
- **Errors**: `ToolkitError: <msg>` (editor error text, `the Toolkit is not running`, `no answer in N s`).
- **Definition**:
```json
{
  "name": "bg3_toolkit_eval",
  "description": "Evaluate an expression inside the Toolkit against its own .NET objects, on its UI thread, and return the result. Form: Namespace.Type.Member.Method(\"text\", 1, true); generic calls like EditorFramework.MacrosHelper.GetPlugin<EoCPlugin.LevelPlugin>(); ':members <expr>' lists members of a type or value, ':types <text>' lists loaded types. Useful roots: EditorFramework.FrameworkClass.Instance.Game (ResumeGame, PauseGame, IsEditorModeActive, LoadModule, ...), LSFrameworkPlugin.FrameworkPlugin.LevelManager, EditorFramework.MacrosHelper.GetPlugin<LSFrameworkPlugin.ProjectPlugin>(). Do not call LoadProject or LoadLevel while the editor is still starting: it hangs the load.",
  "inputSchema": {
    "type": "object",
    "required": [
      "expression"
    ],
    "properties": {
      "expression": {
        "type": "string"
      },
      "timeout": {
        "type": "number",
        "description": "Seconds (default 60)."
      }
    }
  }
}
```

#### `bg3_toolkit_state`

- **Calls**: `toolkit.pids()`, `toolkit.state(1e9)`.
- **Result**: JSON text `{"processes":[...],"state":<state.json or null>}`.
- **Errors**: -
- **Definition**:
```json
{
  "name": "bg3_toolkit_state",
  "description": "The Toolkit's state: running or not, project, level, editor mode or game mode, and the numbered list of what the editor announced (ModuleLoaded, LevelLoaded, GameResumed, ...).",
  "inputSchema": {
    "type": "object",
    "properties": {}
  }
}
```

#### `bg3_toolkit_mode`

- **Calls**: `toolkit.play()` or `toolkit.edit()` (evaluates `Game.ResumeGame()` / `Game.PauseGame()`), then sleeps 2 s.
- **Result**: JSON text `{"editorMode":..,"gameUpdating":..,"level":..}` from the (<=5 s fresh) state.
- **Errors**: `ToolkitError`.
- **Definition**:
```json
{
  "name": "bg3_toolkit_mode",
  "description": "Switch the Toolkit between editing and playing: 'play' starts the game inside the editor on the loaded level, 'edit' pauses it and returns to the editor.",
  "inputSchema": {
    "type": "object",
    "required": [
      "mode"
    ],
    "properties": {
      "mode": {
        "type": "string",
        "enum": [
          "play",
          "edit"
        ]
      }
    }
  }
}
```

#### `bg3_toolkit_down`

- **Calls**: `toolkit.down()`.
- **Result**: Text `Toolkit closed`.
- **Errors**: -
- **Definition**:
```json
{
  "name": "bg3_toolkit_down",
  "description": "Close the Toolkit and remove the bridge file from its folder.",
  "inputSchema": {
    "type": "object",
    "properties": {}
  }
}
```

### 1.8 Plugin `coverage.py` (5 tools, `stand/mcp_tools/coverage.py`)

Thin wrappers over `se-fork/coverage` (`probe.py`, `seen.py`, `tour.py`) and the bridge's `GK.Stage`. `call(game, name, args)` imports `probe`, `seen`, `tour` first (an import failure makes every tool of the file an isError); an unknown name raises `KeyError: '<name>'`. They use the server's own `GAME` (whatever instance the server was started for), not `probe.attach()`.


#### `bg3_scout`

- **Calls**: server Lua `return GK.Stage.Scout(<lua table of args>)` (60 s). The table is built by `probe.lua_value` of the non-None arguments, and is passed **only if `kind` is given** (without `kind` the tally form is called).
- **Result**: The Lua result as JSON text (tally object, or a list of `{uuid,name,template,distance,position,hp}`).
- **Errors**: isError on Lua errors.
- **Definition**:
```json
{
  "name": "bg3_scout",
  "description": "What the current level offers for a scene, from the game's own entities. Without `kind`: a tally (characters, enemies, offstage, dead, containers, doors, locked, traders). With `kind`: the list, nearest first - uuid, name, template, distance, position, hp.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "kind": {
        "type": "string",
        "enum": [
          "characters",
          "enemies",
          "offstage",
          "dead",
          "containers",
          "doors",
          "locked",
          "traders"
        ]
      },
      "radius": {
        "type": "number",
        "description": "metres from the host"
      },
      "name": {
        "type": "string",
        "description": "part of the template or display name"
      },
      "limit": {
        "type": "integer"
      }
    }
  }
}
```

#### `bg3_unblock`

- **Calls**: `tour.unblock(game)` (`se-fork/coverage/tour.py`).
- **Result**: A list of strings describing what was done (JSON text).
- **Errors**: isError.
- **Definition**:
```json
{
  "name": "bg3_unblock",
  "description": "Removes whatever holds the game up: walks out of a dialogue, raises dead party members, leaves forced turn-based mode, marks tutorials as seen. Returns what it did.",
  "inputSchema": {
    "type": "object",
    "properties": {}
  }
}
```

#### `bg3_probe`

- **Calls**: `probe.render(probe.dump(game, side, component, max or 16))`.
- **Result**: Plain text table of the component's memory layout per 4/8-byte part.
- **Errors**: isError.
- **Definition**:
```json
{
  "name": "bg3_probe",
  "description": "Raw memory of live instances of a component type (engine name, e.g. esv::unsheath::CombatJoiningComponent), summarised per 4/8-byte part: pointers, entity handles, value ranges, floats. Works on types Script Extender has no layout for. Read-only.",
  "inputSchema": {
    "type": "object",
    "required": [
      "side",
      "component"
    ],
    "properties": {
      "side": {
        "type": "string",
        "enum": [
          "client",
          "server"
        ]
      },
      "component": {
        "type": "string"
      },
      "max": {
        "type": "integer",
        "description": "instances to read (default 16)"
      }
    }
  }
}
```

#### `bg3_seen`

- **Calls**: `seen.start(game, label or 'idle')` -> `capturing`; `mark` -> `marked`; `drain` -> `seen.drain(game)` result; `show` -> for each side the library samples of `component` rendered with `probe.render`. NOTE: `mark` needs `label` (KeyError otherwise), `show` needs `component`.
- **Result**: Text per action as listed (`show` returns `not in the library` when empty).
- **Errors**: KeyError / isError.
- **Definition**:
```json
{
  "name": "bg3_seen",
  "description": "The standing capture of component types that come to life. action: 'start' (begin, on both sides), 'mark' (name what happens from now on, with `label`), 'drain' (move new samples into the library se-fork/coverage/seen), 'show' (the library's samples of `component`).",
  "inputSchema": {
    "type": "object",
    "required": [
      "action"
    ],
    "properties": {
      "action": {
        "type": "string",
        "enum": [
          "start",
          "mark",
          "drain",
          "show"
        ]
      },
      "label": {
        "type": "string"
      },
      "component": {
        "type": "string"
      }
    }
  }
}
```

#### `bg3_tour`

- **Calls**: `tour.run(game, name, steps)` plays `se-fork/coverage/tours/<name>.json` (may take minutes and may restart the game).
- **Result**: JSON text: list of `{label, seconds, new, problems}` per step.
- **Errors**: isError.
- **Definition**:
```json
{
  "name": "bg3_tour",
  "description": "Plays a tour (se-fork/coverage/tours/<name>.json): short situations one after another with the standing capture on; returns, per step, which component types appeared for the first time.",
  "inputSchema": {
    "type": "object",
    "required": [
      "name"
    ],
    "properties": {
      "name": {
        "type": "string"
      },
      "steps": {
        "type": "array",
        "items": {
          "type": "string"
        },
        "description": "only these steps"
      }
    }
  }
}
```

### 1.9 Plugin `situation.py` (25 tools, `stand/mcp_tools/situation.py`)

The agent API: the situation as a player perceives it and a player's own commands (honest actions only; cheats live in `GK.Cheat`, reachable only through `bg3_lua`). All wrap `stand/situation.py` (the same functions as the agent CLI, cli.md section 8). Common behaviour of `call()` (`:202-304`):

- First `situation.patient_send(gatekit)` (monkeypatches `Game._send` with a 40 x 50 ms `PermissionError` retry, once per process).
- A fresh `Ids(game)` object per call and `ids.save()` in `finally` (short 8-char ids live in `%LOCALAPPDATA%\GateKit\agent-ids-<GK_AGENT_STATE|instance|default>.json`, shared with the CLI). Any id/point argument is resolved with `ids.full(...)`: 8-char short id, full uuid, `x,y,z`, or a unique prefix; unknown 6-35 hex ids are looked up in the game (`See.Resolve`).
- Unlike the CLI, MCP results do **not** get the appended `heard:` / `happened:` sections (use `bg3_agent_heard` / `bg3_agent_events`).
- Results: strings from `render_*` are returned verbatim; dicts/lists become indented JSON text via `text()`. Errors (Lua, timeouts, `KeyError` for missing required args) -> isError `<ExceptionClass>: <message>`.
- An unknown tool name falls out of the `if` chain to `raise KeyError(name)`.

The shared schema fragments of the source (`ID`, `POINT_OR_ID`, `WHO`) are inlined in the definitions below.


#### `bg3_agent_status`

- **Calls**: `situation.status(game)`
- **Result**: dict -> JSON text
- **Definition**:
```json
{
  "name": "bg3_agent_status",
  "description": "One look at the situation: level, mode (exploration, combat, dialogue, turn-based), whose turn, party health, and what on screen waits for the player (a roll, a message box, a dialogue).",
  "inputSchema": {
    "type": "object",
    "properties": {}
  }
}
```

#### `bg3_agent_look`

- **Calls**: `render_look(look(game, radius or 25, scope or 'seen'), ids)`
- **Result**: plain text (note: `scope` 'all' of the CLI is not in the schema)
- **Definition**:
```json
{
  "name": "bg3_agent_look",
  "description": "Characters and objects around the party, nearest first, as the party perceives them: name, relation (party/ally/neutral/hostile), distance, compass direction (north = +z), health, statuses, can talk / trade / be looted; doors (open/closed/locked), containers, ladders, items, usable things, discovered traps. scope 'known' adds what was seen earlier (last known place).",
  "inputSchema": {
    "type": "object",
    "properties": {
      "radius": {
        "type": "number",
        "description": "metres, default 25"
      },
      "scope": {
        "type": "string",
        "enum": [
          "seen",
          "known"
        ]
      }
    }
  }
}
```

#### `bg3_agent_combat`

- **Calls**: `render_combat(combat(game), ids)`
- **Result**: plain text
- **Definition**:
```json
{
  "name": "bg3_agent_combat",
  "description": "The fight: round, whose turn, the turn order with positions, distances, health, armour class, statuses, concentration and (party) resources; on a party member's turn also its options: actions and spells with cost, range and whether they are ready, and the movement left.",
  "inputSchema": {
    "type": "object",
    "properties": {}
  }
}
```

#### `bg3_agent_party`

- **Calls**: `render_party(party(game), ids)`
- **Result**: plain text
- **Definition**:
```json
{
  "name": "bg3_agent_party",
  "description": "Party members: level, class, race, hit points, armour class, abilities, experience and level-up available, action resources and spell slots, statuses, weapon; and the gold.",
  "inputSchema": {
    "type": "object",
    "properties": {}
  }
}
```

#### `bg3_agent_examine`

- **Calls**: `examine(game, ids.full(id))`
- **Result**: dict -> JSON text
- **Definition**:
```json
{
  "name": "bg3_agent_examine",
  "description": "One character or object in full, the way the Examine window shows it.",
  "inputSchema": {
    "type": "object",
    "required": [
      "id"
    ],
    "properties": {
      "id": {
        "type": "string",
        "description": "an id from a listing (8 characters) or a whole uuid"
      }
    }
  }
}
```

#### `bg3_agent_actions`

- **Calls**: `render_actions(actions(game, who, target, all), ids)`
- **Result**: plain text
- **Definition**:
```json
{
  "name": "bg3_agent_actions",
  "description": "What a party member can do now: its actions and spells with cost, range, area, concentration, whether it is ready (and why not); with a target, also whether the target is in range and in sight.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "who": {
        "type": "string",
        "description": "which party member acts (id); default: the controlled one"
      },
      "target": {
        "type": "string",
        "description": "an id, or a point 'x,y,z'"
      },
      "all": {
        "type": "boolean",
        "description": "also those not ready"
      }
    }
  }
}
```

#### `bg3_agent_path`

- **Calls**: `path(game, ids.full(to), who)`
- **Result**: dict -> JSON text
- **Definition**:
```json
{
  "name": "bg3_agent_path",
  "description": "Whether a party member can walk to a point or object, and the path length (in a fight: whether it fits the movement left).",
  "inputSchema": {
    "type": "object",
    "required": [
      "to"
    ],
    "properties": {
      "to": {
        "type": "string",
        "description": "an id, or a point 'x,y,z'"
      },
      "who": {
        "type": "string",
        "description": "which party member acts (id); default: the controlled one"
      }
    }
  }
}
```

#### `bg3_agent_stealth`

- **Calls**: `exposure(game, ids.full(at))` if `at` else `watchers(game)`
- **Result**: list/dict -> JSON text
- **Definition**:
```json
{
  "name": "bg3_agent_stealth",
  "description": "Who could notice the party: perceived characters with their sight range, field of view, facing (degrees, 0 = north) and which members they see now; and how hidden each member is (sneaking, light). With `at` (a point or id): who would see a character standing there.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "at": {
        "type": "string",
        "description": "an id, or a point 'x,y,z'"
      }
    }
  }
}
```

#### `bg3_agent_dialog`

- **Calls**: `render_dialog(answer(game, int(answer)))` if `answer` else `render_dialog(dialog(game))`
- **Result**: plain text
- **Definition**:
```json
{
  "name": "bg3_agent_dialog",
  "description": "The dialogue on screen: speaker, line and the numbered answers. With `answer`: chooses it and returns the next line (or nothing when the dialogue ended).",
  "inputSchema": {
    "type": "object",
    "properties": {
      "answer": {
        "type": "integer"
      }
    }
  }
}
```

#### `bg3_agent_roll`

- **Calls**: `roll_cancel(game)` if `cancel` else `roll(game)`
- **Result**: dict -> JSON text
- **Definition**:
```json
{
  "name": "bg3_agent_roll",
  "description": "The dice-roll window (a skill check in a dialogue, a lock...): rolls and reads the result; with cancel=true closes it without rolling (when the check looks beyond the character).",
  "inputSchema": {
    "type": "object",
    "properties": {
      "cancel": {
        "type": "boolean"
      }
    }
  }
}
```

#### `bg3_agent_journal`

- **Calls**: `journal(game)` (opens and closes the journal window)
- **Result**: dict -> JSON text
- **Definition**:
```json
{
  "name": "bg3_agent_journal",
  "description": "The journal: quests and their objectives.",
  "inputSchema": {
    "type": "object",
    "properties": {}
  }
}
```

#### `bg3_agent_do`

- **Calls**: maps `command` through `situation.COMMANDS` (name -> (Lua function, argument names)); positional values are taken from the arguments in that order, `to`/`target` interchangeable, ids resolved via `Ids.full`, trailing None dropped; `endturn`+`all` -> `['all']`; `provoke`/`walk` passed as options; then `act(game, command, *values, who=, **extra)` -> `GK.Act.<Fn>` and waits for the job (90 s)
- **Result**: dict (job end state or refusal) -> JSON text; an unknown command raises inside `act`. `COMMANDS` also contains `flee`, `lockpick`, `disarm` which the schema description does not list
- **Definition**:
```json
{
  "name": "bg3_agent_do",
  "description": "A player's command, waited for. Commands: move <to> [partial: true = as far as the movement allows], cast <spell> [target] [upcast], attack <target>, jump <to>, shove <target>, hide, dash, disengage, help <target>, throw <item> <target>, use <target> (doors, chests, levers, ladders, books), pickup <target>, loot <target> (a corpse), take <container> [item], talk <target>, endturn, turnbased (on: true/false; works outside a fight too), sneak (on), equip <item>, unequip <item>, select, stop. Commands go to the selected character; `who` selects one first. A refusal says why, as the game would (out of range, not your turn, provokes an attack of opportunity, no path - then with the nearest walkable point and the jump/teleport spells that could cross the gap).",
  "inputSchema": {
    "type": "object",
    "required": [
      "command"
    ],
    "properties": {
      "command": {
        "type": "string"
      },
      "target": {
        "type": "string",
        "description": "an id, or a point 'x,y,z'"
      },
      "to": {
        "type": "string",
        "description": "an id, or a point 'x,y,z'"
      },
      "item": {
        "type": "string",
        "description": "an id from a listing (8 characters) or a whole uuid"
      },
      "container": {
        "type": "string",
        "description": "an id from a listing (8 characters) or a whole uuid"
      },
      "spell": {
        "type": "string",
        "description": "the id from bg3_agent_actions"
      },
      "upcast": {
        "type": "integer"
      },
      "partial": {
        "type": "boolean"
      },
      "on": {
        "type": "boolean"
      },
      "provoke": {
        "type": "boolean",
        "description": "walk out of an enemy's reach anyway (attack of opportunity)"
      },
      "walk": {
        "type": "boolean",
        "description": "cast: walk into range first"
      },
      "all": {
        "type": "boolean",
        "description": "endturn: every party member whose turn is open"
      },
      "who": {
        "type": "string",
        "description": "which party member acts (id); default: the controlled one"
      }
    }
  }
}
```

#### `bg3_agent_heard`

- **Calls**: `render_heard(heard(game)) or 'nothing new'`
- **Result**: plain text
- **Definition**:
```json
{
  "name": "bg3_agent_heard",
  "description": "What was said around the party since the last call: barks over characters' heads, the narrator's lines (a tablet read aloud, a thought), dialogue lines - with the speaker and its distance. Only what the party could hear.",
  "inputSchema": {
    "type": "object",
    "properties": {}
  }
}
```

#### `bg3_agent_popup`

- **Calls**: `resolve(game, policy)` if `policy` else `popup(game) or 'nothing waits'`
- **Result**: dict -> JSON text or plain
- **Definition**:
```json
{
  "name": "bg3_agent_popup",
  "description": "The window that waits for the player (loot, message box, tutorial, reward, reaction...), with its summary and choices; with `policy` one step through it, e.g. {\"loot\": \"takeall\"}, {\"box\": \"affirmative\"}, {\"reaction\": \"decline\"}.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "policy": {
        "type": "object"
      }
    }
  }
}
```

#### `bg3_agent_events`

- **Calls**: `render_events(events(game)) or 'nothing new'`
- **Result**: plain text
- **Definition**:
```json
{
  "name": "bg3_agent_events",
  "description": "What happened since the last call - the game log: damage, healing, statuses gained and lost (curses, poison, blessings), downs and deaths, spells cast, fights and turns, items and gold received or lost, journal updates, roll results, companions' approval, level-ups. Only what the party was involved in or perceived.",
  "inputSchema": {
    "type": "object",
    "properties": {}
  }
}
```

#### `bg3_agent_inventory`

- **Calls**: `render_inventory(inventory(game, who), ids)`
- **Result**: plain text
- **Definition**:
```json
{
  "name": "bg3_agent_inventory",
  "description": "What the party carries, per member: items with ids (for examine, use, equip), counts, equipped flags, values; bags with their contents.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "who": {
        "type": "string",
        "description": "which party member acts (id); default: the controlled one"
      }
    }
  }
}
```

#### `bg3_agent_ways`

- **Calls**: `render_ways(ways(game, None, float(range or 24)))`
- **Result**: plain text
- **Definition**:
```json
{
  "name": "bg3_agent_ways",
  "description": "How far the selected character can walk in each of the 8 compass directions over ground the party sees (north = +z): exits, corridors, ledges at a glance.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "range": {
        "type": "number"
      }
    }
  }
}
```

#### `bg3_agent_wait_turn`

- **Calls**: `wait_turn(game)` (90 s)
- **Result**: dict -> JSON text
- **Definition**:
```json
{
  "name": "bg3_agent_wait_turn",
  "description": "In a fight: waits until a party member's turn is open (or the fight is over, or a window waits for the player).",
  "inputSchema": {
    "type": "object",
    "properties": {}
  }
}
```

#### `bg3_agent_say`

- **Calls**: `glance(game, ids.full(at))` if `at`; then `call(game,'server','Act.Say', text, why, who)`
- **Result**: dict -> JSON text. (The CLI additionally appends to the narration log file; the MCP path does not)
- **Definition**:
```json
{
  "name": "bg3_agent_say",
  "description": "Immersive mode (docs/agent-immersive.md): a line the acting character says, in character, shown in the game as a notification, and a short plain reason; both kept in the narration log. at=<id> marks what the line is about with a ring for a moment.",
  "inputSchema": {
    "type": "object",
    "required": [
      "text"
    ],
    "properties": {
      "text": {
        "type": "string"
      },
      "why": {
        "type": "string"
      },
      "who": {
        "type": "string",
        "description": "which party member acts (id); default: the controlled one"
      },
      "at": {
        "type": "string"
      }
    }
  }
}
```

#### `bg3_agent_notes`

- **Calls**: `add` -> `render_notes([note(game, text)])`; `edit`/`delete` by note number `n` on `agent-notes-<x>.json` (`no note #n` if missing); `list` -> `render_notes(rows, find)`
- **Result**: plain text
- **Definition**:
```json
{
  "name": "bg3_agent_notes",
  "description": "The agent's own notes, kept between commands and sessions; each note gets the time and the place in words by itself. action: add (text), edit (n, text), delete (n), list (find = a word).",
  "inputSchema": {
    "type": "object",
    "required": [
      "action"
    ],
    "properties": {
      "action": {
        "type": "string",
        "enum": [
          "add",
          "edit",
          "delete",
          "list"
        ]
      },
      "text": {
        "type": "string"
      },
      "n": {
        "type": "integer"
      },
      "find": {
        "type": "string"
      }
    }
  }
}
```

#### `bg3_agent_wait`

- **Calls**: `wait(game, float(seconds or 3))`
- **Result**: dict -> JSON text
- **Definition**:
```json
{
  "name": "bg3_agent_wait",
  "description": "Lets game time pass (seconds), returning early when a dialogue, roll or fight needs the player.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "seconds": {
        "type": "number"
      }
    }
  }
}
```

#### `bg3_agent_speed`

- **Calls**: `speed(game, scale)`
- **Result**: dict -> JSON text
- **Definition**:
```json
{
  "name": "bg3_agent_speed",
  "description": "Game speed: a number (the engine's own time scale, 0.001..10; 1 = normal), 'auto' (fast walking and enemy turns, normal dialogue and your turns), or nothing to read it.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "scale": {
        "type": "string"
      }
    }
  }
}
```

#### `bg3_agent_save`

- **Calls**: `save_game(game, label)` (QuickSave command via ui, waits up to 40 s for the game's save folder, copies it to `%LOCALAPPDATA%\GateKit\agent-saves\<instance|default>\<slug>\game\<folder>` + `agent.json`)
- **Result**: dict -> JSON text
- **Definition**:
```json
{
  "name": "bg3_agent_save",
  "description": "Save the game (the player's quick save) under a label, with your notes as they are; saving over an old label replaces it. The game refuses in dialogues and scenes.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "label": {
        "type": "string"
      }
    }
  }
}
```

#### `bg3_agent_load`

- **Calls**: no `label`: `render_saves(saves(game))`; else `load_game(game, label, 'restore' if restore_notes else 'keep')` (copies the save back into every profile as `GK-<slug>__<slot>`, sets `checkpoint_name`, `Game.reset(timeout=180, skip_intro=False)`)
- **Result**: plain text / dict -> JSON text
- **Definition**:
```json
{
  "name": "bg3_agent_load",
  "description": "Load a labelled save (or one of the game's own by its folder name); without a label, list the saves. Your notes stay (with a note about the load) unless restore_notes=true.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "label": {
        "type": "string"
      },
      "restore_notes": {
        "type": "boolean"
      }
    }
  }
}
```

#### `bg3_agent_focus`

- **Calls**: `focus(game, on)`
- **Result**: dict -> JSON text
- **Definition**:
```json
{
  "name": "bg3_agent_focus",
  "description": "The visual focus, for whoever watches the game: the game's own rings mark what a command aims at and what you examine, and the game's pointer stays quiet while you play in the background. on=true/false switches it; nothing reads it. On by default.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "on": {
        "type": "boolean"
      }
    }
  }
}
```

### 1.10 Differences between MCP tools and the CLI (for the port)

- `bg3_up` has no `save`; CLI `up` has `--save`.
- `bg3_agent_look` schema allows `scope` seen|known; the CLI also accepts `all` (and `kinds=`, `who=`).
- `bg3_agent_say`/`bg3_agent_notes` skip CLI-only bookkeeping (the narration log file is written only by the CLI `say`).
- `bg3_toolkit_eval` does not replace `'` with `"`.
- `bg3_screenshot` returns an image block; the CLI prints the path.

---

## 2. Server `bg3-offline` (`kit/mcp_server.py`)

Offline lookups over the three SQLite databases (`index`, `symbols`, `decomp`); no game, no instance, no env variable besides the database locations (`BG3_INDEX_DB`, `BG3_SYMBOLS_DB`, `BG3_DECOMP_WORK`; cli.md 7.1). Each tool wraps a function of `kit/gatekit.py` (the ones the `kit/gatekit.py` CLI uses).

### 2.1 Protocol behaviour (`main()`, `:81-113`)

- stdin is read as byte lines (`raw.strip()`) and `json.loads`ed; blank lines and invalid JSON are skipped. **No BOM handling** (a BOM makes the first line unparsable and it is skipped), unlike the `bg3` server.
- `initialize` -> `{"protocolVersion": <client's or "2024-11-05">, "capabilities":{"tools":{}}, "serverInfo":{"name":"bg3-gatekit-offline","version":"0.1"}}`.
- `tools/list` -> `{"tools": describe()}` (order below). `tools/call` -> `call(params.name, params.arguments)`. `ping` -> `{}`. Other methods with an `id` -> error `-32601` `unknown method <m>`; without id -> ignored.
- Replies: `{"jsonrpc":"2.0","id":<id>,"result":...}` as UTF-8 bytes + `\n`, flushed.
- Any exception while handling (unknown tool -> `ValueError: unknown tool <name>`, missing argument -> `KeyError: 'text'`, `sqlite3.Error`, a non-existent database name -> `KeyError`) -> result `{"content":[{"type":"text","text":"error: <ExceptionClass>: <message>"}],"isError":true}` (note the `error: ` prefix, unlike the `bg3` server).
- `SystemExit` raised inside a tool (missing database file: `<path> does not exist; build it first (see the README of the <name> tool)`) is **not** an error result: the answer is normal text `{"error": "<message>"}`.
- A JSON array line makes `msg.get(...)` raise `AttributeError` outside the `try`, ending the server.

### 2.2 Result format

`call()` (`:68-78`): the function result is serialised **compactly** with `json.dumps(result, ensure_ascii=False, default=str)` (no indentation); if longer than `LIMIT = 60000` characters it is cut to 60000 and suffixed with `\n... truncated: <N> characters in all; narrow the request or use bg3_sql`. Always exactly one text block.

### 2.3 Schema generation (`describe()`, `:58-65`)

Every tool gets `{"type":"object","properties":{<param>:{"type":<t>,"description":<d>}},"required":[<required params>]}`: `required` is always present (possibly an empty list) and there is no `additionalProperties`.

### 2.4 Tools (7)


#### `bg3_search`

- **Calls**: `gatekit.search(text, int(limit or 10))`
- **Result**: list of `{kind, ..., more}` objects (see cli.md 7.1).
- **Definition** (verbatim output of `describe()`):
```json
{
  "name": "bg3_search",
  "description": "Find anything by a part of its name: timelines, dialogs, templates, placed objects, stats, Osiris functions, localized text, C++ classes and functions, ECS types, struct layouts, enumerations. Each result names the call that gives the details.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "text": {
        "type": "string",
        "description": "part of a name"
      },
      "limit": {
        "type": "integer",
        "description": "results per kind (default 10)"
      }
    },
    "required": [
      "text"
    ]
  }
}
```

#### `bg3_about`

- **Calls**: `gatekit.about(what)`
- **Result**: dict with `kind` in {address, text, object, class, search}.
- **Definition** (verbatim output of `describe()`):
```json
{
  "name": "bg3_about",
  "description": "Everything known about one thing. Accepts a uuid (object of the game data: attributes and everything that refers to it), a localization handle, a C++ class name with namespace such as ecl::TLPreviewDummy (exports, fields with offsets, ECS registration, addresses in the game), an address such as bg3.exe+0x1899090 (function, names, callers, callees, pseudo-code when decompiled), or an exact object name.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "what": {
        "type": "string",
        "description": "uuid, handle, class name, binary+0xRVA or object name"
      }
    },
    "required": [
      "what"
    ]
  }
}
```

#### `bg3_scene`

- **Calls**: `gatekit.scene(name)`
- **Result**: dict `kind: scene` (or `error: no exact match` + `candidates`).
- **Definition** (verbatim output of `describe()`):
```json
{
  "name": "bg3_scene",
  "description": "A dialogue or cutscene timeline by name: its dialog, the actors with who they are, every track type with counts, the time spans where weapons, clothing or the whole actor are hidden or the model is replaced, and the story goals that mention it.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "name": {
        "type": "string",
        "description": "timeline name, e.g. CAMP_VoloOperation"
      }
    },
    "required": [
      "name"
    ]
  }
}
```

#### `bg3_track`

- **Calls**: `gatekit.track(name)`
- **Result**: dict `kind: track`.
- **Definition** (verbatim output of `describe()`):
```json
{
  "name": "bg3_track",
  "description": "A timeline track type such as TLShowWeapon across data and code: how many scenes use it, the shape of its keys, the engine classes and functions that implement it, and what of that was found in the game executable.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "name": {
        "type": "string",
        "description": "track type, e.g. TLShowWeapon"
      }
    },
    "required": [
      "name"
    ]
  }
}
```

#### `bg3_hook`

- **Calls**: `gatekit.hook(name)`
- **Result**: dict `kind: hook` with `functions`, or `toolkit_only` + `note`.
- **Definition** (verbatim output of `describe()`):
```json
{
  "name": "bg3_hook",
  "description": "Where a function is in bg3.exe: address, how it was identified and how far to trust that, size, callers, virtual table slots, and the command that prints a byte signature. Give a part of the qualified name or bg3.exe+0xRVA. When the function was not found in the game, lists the Toolkit declarations instead.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "name": {
        "type": "string",
        "description": "part of a qualified function name, or bg3.exe+0xRVA"
      }
    },
    "required": [
      "name"
    ]
  }
}
```

#### `bg3_sql`

- **Calls**: `gatekit.run_sql(database, query)` with limit 500
- **Result**: `{columns, rows, truncated}`. Read-only because the connection is `mode=ro`; a statement that writes fails with `sqlite3.OperationalError` -> isError.
- **Definition** (verbatim output of `describe()`):
```json
{
  "name": "bg3_sql",
  "description": "Run one read-only SQL query (SQLite) on a database: 'index' (game data), 'symbols' (code) or 'decomp' (pseudo-code). Call bg3_schema first to see tables and columns. At most 500 rows are returned.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "database": {
        "type": "string",
        "description": "index, symbols or decomp"
      },
      "query": {
        "type": "string",
        "description": "a SELECT statement"
      }
    },
    "required": [
      "database",
      "query"
    ]
  }
}
```

#### `bg3_schema`

- **Calls**: `gatekit.schema(database or None)`
- **Result**: `{<db>: {path, tables: {<name>: {type, rows, columns:[{name,type,comment}]}}}}` or `{<db>: {missing: <path>}}`.
- **Definition** (verbatim output of `describe()`):
```json
{
  "name": "bg3_schema",
  "description": "Tables, columns with their meaning, and row counts of the databases.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "database": {
        "type": "string",
        "description": "index, symbols or decomp; omit for all"
      }
    },
    "required": []
  }
}
```

---

## 3. Surprises and inconsistencies

1. Registration names (`bg3`, `bg3-offline`) differ from the `serverInfo.name` the servers answer (`bg3-gatekit`, `bg3-gatekit-offline`); version strings differ (`0.1.0` vs `0.1`).
2. Error text format differs: `bg3` -> `ExceptionClass: msg`; `bg3-offline` -> `error: ExceptionClass: msg`; `SystemExit` in offline is a *non-error* `{"error": ...}` result.
3. `bg3_up` lacks `save`; `bg3_wait` schema omits `ui`; `bg3_agent_look` omits `all`; `bg3_agent_do`'s description omits `flee`, `lockpick`, `disarm`, which the code supports.
4. No argument validation (the JSON Schema is advisory only); a missing required argument shows up as `KeyError: '<name>'`.
5. A batch JSON-RPC array crashes both servers; the BOM is tolerated only by `bg3`.
6. Long tool calls block the loop (no concurrency, no progress notifications, no cancellation, `ping` unanswered meanwhile).
7. Plugin error bookkeeping can show stale errors; modules imported by plugins are not hot reloaded.
8. `.mcp.json` registers `python stand/mcp_server.py` and `python kit/mcp_server.py` (relative to the project); older docs still say `claude mcp add bg3 -- python the old repository\stand\mcp_server.py`.

---

## 4. The C# server (`src/GateKit.Mcp`, implemented)

`gatekit mcp` serves the stand over stdio; `gatekit mcp tools` lists what it offers without
starting anything. The instance comes from the same settings as every other command (`--instance`,
`GATEKIT_INSTANCE`, `BG3_GATEKIT_INSTANCE`); one process serves one instance. The repository's
`.mcp.json` registers it as `gatekit`. `src/GateKit.Mcp/README.md` is the guide for modders.

Built on the official C# SDK (NuGet `ModelContextProtocol` 2.4.0, which ships a `net10.0` target):
the handshake, capabilities, `tools/list`, `tools/call`, `ping` and cancellation are the SDK's. The
**transport** is GateKit's own (`BatchStdioTransport`), because batches are not in the SDK - the
spec dropped them in 2025-06-18 - and section 3 asks for them.

### 4.1 What is implemented

The 17 core tools of section 1.6, with the names, schemas and result texts written there:
`bg3_up`, `bg3_down`, `bg3_status`, `bg3_lua`, `bg3_wait`, `bg3_state`, `bg3_events`, `bg3_logs`,
`bg3_reload`, `bg3_dialog`, `bg3_fast_scenes`, `bg3_checkpoint`, `bg3_reset`, `bg3_key`,
`bg3_screenshot`, `bg3_call`, `bg3_api`.

One more that the old server had no counterpart for, the MCP half of `gatekit sound`
(`docs/contracts/cli.md` 14.2.1, `docs/design/deck.md`, section "Sound"):

- **`bg3_sound`** - `{action: list|set|mute|unmute|unfocused, category?, volume?, on?}`: how loud
  this copy of the game is, so an agent watching several runs can quieten the ones it is not
  listening to. `list` (the default) gives the twelve categories with their volumes (0 to 100) and
  the game's own mute-when-not-in-front flag; `set` takes `category` and `volume`; `mute` silences
  a category, remembering how loud it was, and without one the master, which silences the copy;
  `unmute` puts it back; `unfocused` reads the flag, or sets it with `on`. A `volume` outside 0..100
  and a `category` that is not a name of the game's or of GateKit's are `StandError`s before
  anything reaches the game. It changes the running copy only, never the player's own settings.

And two for the MCP half of `gatekit window ...`
(`docs/design/deck.md`, `docs/contracts/cli.md` 14.4):

- **`bg3_windows`** - every running copy: instance, label, process id, its tile, and the tiles kept
  for a game that is away. No arguments. Needs no deck.
- **`bg3_window`** - `{action: maximize|grid|label|release|mirror|adopt|fps, instance?, label?, fps?}`:
  hold one copy open over the whole deck, put everything back in the grid, name a copy, give a copy
  its own window back, draw a copy from the frames it shares (`mirror`) or from its own window
  again (`adopt`), or say how fast a copy may draw (`fps`, 0 for no cap, no number to read it).
  `maximize`, `grid`, `release`, `mirror` and `adopt` need a running deck and say `StandError: no
  deck is running` when there is none; `label` is kept in the state folder either way, and `fps`
  goes straight to the game through the bridge, so it needs no deck but does need GateKit's Script
  Extender build.

Neither touches a game. Reading a copy, driving it and photographing it work the same whether it is
in a tile, behind another window, out of view or in no deck at all.

### 4.2 The fixes of section 3

| Fixed | How |
|---|---|
| 3.3 `bg3_up` lacked `save` | `save` is in the schema and passed to `UpOptions.Save`, as the CLI's `--save`. |
| 3.3 `bg3_wait` omitted `ui` | the `side` enum is `client`, `server`, `ui` - the sides `bg3_lua` and `bg3_call` take. |
| 3.3 `bg3_agent_do`, `bg3_agent_look` | not ported (4.3): the agent tools belong to a paused session, and their arguments are fixed when they come back. |
| 3.5 a batch killed the server | an array is split into its messages, each handed to the session; the answers are collected back into one array, written when the last one is ready, in the order of the requests. A batch of notifications alone is answered with nothing; an empty array is an `InvalidRequest`. |
| 3.5 the BOM | tolerated wherever a line starts. A line that cannot be a message (a build line, a stray print) is skipped; one that tried to be JSON (it starts with `{` or `[`) is answered with a parse error instead of being swallowed. |
| 3.6 a long call blocked the loop | requests are served side by side: `ping`, `tools/list` and a second tool are answered while `bg3_up` or `bg3_reset` runs. Each call gets a stand of its own with its own output buffer, so two calls never mix their log lines. |
| 3.7 stale plugin errors | the tools folder is re-read by modification time and an entry is dropped as soon as its file loads, is removed, or stops clashing. |

### 4.3 Left out, and why

- **The 5 Toolkit tools (1.7)**: there is no C# Toolkit bridge yet, so `bg3_toolkit_up`, `_eval`,
  `_state`, `_mode`, `_down` are not offered. They return when `GateKit.Toolkit` exists.
- **The 25 agent tools (1.9) and the 5 coverage tools (1.8)**: they belong to the paused agent-play
  and coverage sessions and wait for `GK.Act`/`GK.See` and `se-fork/coverage` to be ported. Nothing
  of them is lost meanwhile: every `GK.*` function they wrapped is callable through `bg3_call`, and
  `bg3_api` lists it.
- **The `bg3-offline` server (section 2)**: `GateKit.GameData` and `GateKit.Symbols` hold no
  databases yet; when they do, the 7 tools belong to those projects' own `CreateCommand()` and a
  second server, as before.

### 4.4 Deliberate differences

1. **`serverInfo`** is `gatekit` with the assembly's version, not `bg3-gatekit` / `0.1.0`. Tool
   names are unchanged, which is what an agent's prompts depend on.
2. **The protocol version** is negotiated by the SDK (it answers the newest version both sides
   know) instead of echoing whatever the client sent.
3. **Error text**: still `<kind>: <message>` with `isError`, and still `StandError: ...` /
   `KeyError: 'side'` for the two an agent meets; other failures carry the C# exception's name
   where Python carried its class.
4. **`bg3_lua` does not read files.** The CLI's `@file.lua` is a CLI convenience; over MCP a code
   argument is only ever a chunk.
5. **`bg3_wait` uses Lua's own truth** (only `nil` and `false` are false), so waiting for `0` or an
   empty table finishes. Python's `if value:` also called `0`, `""`, `[]` and `{}` false.
6. **Plugin tools are data, not code** (1.4 was a Python import): one JSON file per tool in
   `mcp-tools/` (`GATEKIT_MCP_TOOLS`), each a description plus one bridge call or Lua chunk,
   re-read when it changes. `bg3_plugin_errors` behaves as in 1.4. The README has the format.
7. **`bg3_reload`'s `mod_root`** is resolved to a full path (the Python MCP server left it
   relative, unlike its own CLI).
8. **`bg3_api`** reads the bridge mods the stand would install (`GATEKIT_BRIDGE`, default
   `<repo>/mods`), so it covers Weave as well as GateKit.

### 4.5 Checked against a real game (instance `mcp`, SE v34 build, 2026-11-13)

Driven over the protocol itself, by a client speaking JSON-RPC on the server's stdio:
`initialize` (negotiated 2025-06-18), `tools/list` (17), `bg3_api`, `bg3_status`,
`bg3_up --level GustavX:TUT_Avernus_C` (playable after 52 s, the bridge pak rebuilt on the way),
`bg3_state`, `bg3_lua` (log line and result), `bg3_call` (`GK.Client`), `bg3_checkpoint`,
a two-request batch answered as one array, `bg3_reset` (14.1 s) with a `ping` answered in 0.00 s
while it ran, `bg3_screenshot` (an image block), `bg3_logs`, `bg3_down`.


---

## 5. The coverage loop (`CoverageTools.cs`): mapping the engine without the old Python stand

Eleven tools the old server did not have. They exist because mapping the engine
(`docs/design/coverage-runs.md`) was done by `research/coverage/*.py`, every one of which
imports the first repository's Python stand, so no agent could run the loop through this server
alone. The **method is unchanged** - `seen.py`, `probe.py`, `todo.py` and `author_check.py`
already knew how; what moved is where it lives. The Lua those tools injected before every call
is now part of the bridge (`Shared/Probe.lua`: `GK.Probe`, `GK.Seen`), and the data work around
it is C# (`Ledger.cs`, `Survey.cs`).

| Tool | Replaces | What it does |
|---|---|---|
| `bg3_pause` | nothing | stops the world while an agent thinks; `GK.Pause` |
| `bg3_seen_start` / `_mark` / `_new` / `_drain` / `_status` | `seen.py start/mark/drain` | the standing capture: which types have holders, and their bytes |
| `bg3_coverage_todo` | `todo.py` | the ledger of the unseen, with two groups `todo.py` never named |
| `bg3_probe` | `probe.py` | the per-offset survey of one component |
| `bg3_probe_fields` | `author_check.py` | raw bytes beside what Script Extender returns for the same instance |
| `bg3_recipe` | nothing | a confirmed finding writes a replayable `scenarios/coverage/*.lua` and a line in `findings.jsonl` |
| `bg3_run_report` | nothing | a run's hand-over and what it spent, in `runs.jsonl` |

### 5.1 What `bg3_pause` actually is

Baldur's Gate 3 has no pause a mod can press. `GK.Pause` is the engine's own time scale
(`ls::GlobalSwitches::DeltaTimeScale`) at its floor of 0.001, which the engine itself clamps:
one real second buys one millisecond of game time. Everything is frozen for practical purposes
and not mathematically stopped; the frame loop still runs, so the bridge still answers; sound is
mixed in real time and does not slow with it. The tool says so in its own description, because an
agent that believes it has a true pause will write a finding that depends on one. It stands as a
`GK.Speed` rule of the highest priority, so Speed's half-second poll cannot put the pace back
under it.

### 5.2 Two groups the ledger gained

`todo.py` grouped types as mapped / waiting / alive / dead, and on this record that reads
"2,647 types, 0 dead" - which says nothing about where work is left. The two groups that do:

- **`unseen`** (541) - mapped, with a layout read off the game's code, but **no live instance has
  ever been observed**. The layout is a reading nothing has tested. This is the frontier.
- **`unverified`** (1,127) - observed alive, but the fields have never been checked against the
  raw memory of the same instance.

`bg3_coverage_todo {"group": "unseen", "namespaces": true}` is what a run picks its theme from:
a namespace is usually one system, so a run that takes one has a theme and an end.

### 5.3 Honesty, enforced by the schema

`bg3_recipe` requires `evidence` (`repeated`, `value forced`, `code read` or `sighting only`) and
`reached_by` (`play` or `cheat`), and records a `sighting only` claim as a **lead**, not a
finding, saying so in its answer. A sighting is one appearance; a finding needs the action
repeated, or a value forced, or the code read. How it was reached is part of the record, because
a component seen in ordinary play is a stronger fact than one that only appears after a cheat.

### 5.4 What is not ported yet

`seen.py drain` also compared, for every type Script Extender has a layout for, the raw bytes of
a sample against the fields read through the extender **in the tick of capture**, and recorded the
types that agreed as `verified` in `coverage.json`. That batch comparison needs `generate.py`'s
reading of the layout files (offsets and C++ types) and is not ported; `bg3_probe_fields` does
the same comparison one type at a time, by eye. Until it is ported, the `verified live` count
(841) only grows by hand.

### 5.5 Checked against a real game (instance `world`, SE v34 build, 2026-11-13)

`bg3_pause` (1.0 -> 0.001 -> 1.0), `bg3_seen_start` (both sides), `bg3_seen_status` (667 client
types in 19 ticks), `bg3_seen_new` (1,264 types, all already in the committed record: the filter
works), `bg3_seen_drain` (1,141 client and 1,099 server samples written, index unchanged at
2,066), `bg3_probe` on `eoc::MovementComponent` (the survey named a three-float direction at
+0x00/+0x04/+0x08), `bg3_probe_fields` on the same type (the extender's `Direction` equals those
bytes), `bg3_recipe`, `bg3_run_report`, and `bg3_coverage_todo` in every group against the
numbers `todo.py` prints.
