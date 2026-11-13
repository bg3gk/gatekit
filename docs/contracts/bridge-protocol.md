# Bridge protocol

How the host (the `gatekit` command, the MCP server, scenarios, any other process) talks to the
bridge mod running inside the game. It is a contract: the host side (`src/GateKit.Bridge`) and
the game side (`mods/GateKit/Mods/Gwynerva_GateKit/ScriptExtender/Lua/Shared/Bridge.lua`) may each be rewritten, as
long as both keep to this page. JSON Schemas for every file: [`schemas/bridge/`](../../schemas/bridge/).

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
| Linux, Proton | `<steamapps>/compatdata/1086940/pfx/drive_c/users/steamuser/AppData/Local/Larian Studios/Baldur's Gate 3` (or the prefix the stand created) |
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
  touches Noesis (`Ext.UI.*`). Client side only.
- `t` — seconds since 1970 when written; used only by the host to drop lines nobody can still wait for.

### An answer (`schemas/bridge/answer.schema.json`)

```json
{"id": "4120-1763026000123456789-1", "side": "server", "ok": true, "result": [...], "log": ["..."]}
```

`ok: false` carries `error` (text, with a Lua traceback) instead of `result`. `log` holds the lines
the chunk passed to `GK.Log(...)`. `result` is whatever the chunk returned, converted to JSON
(userdata and entities through Script Extender's JSON stringifier).

### The heartbeat (`schemas/bridge/state.schema.json`)

```json
{"side": "client", "boot": 123456.7, "time": 130000.2, "gameState": "Running", "queue": 1, "done": 1763026000123456}
```

- `boot` — monotonic time at which this Lua state started; it changes on every reset/reload.
- `time` — monotonic time of this beat. The file's modification time tells the host how fresh it is.
- `gameState` — the engine's game state name (`Menu`, `LoadSession`, `Running`, ...).
- `queue` — protocol version the bridge speaks; `2` is this page (`1` had no question channel).
- `done` — the highest `seq` this side has started.

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

## Legacy (protocol 0)

Bridges built before 2026-11-13 read a single `cmd_<side>.json` (`{"id","code","ui"}`) and write
a single `res_<side>.json`; they announce no `queue` in the heartbeat. The Python stand still
speaks it to such games. The C# host does not need to.
