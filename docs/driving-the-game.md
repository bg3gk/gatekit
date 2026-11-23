# Driving the game

How to connect an AI agent to a copy of the game through GateKit's MCP server, and how to work
with it. Who else "agent" can mean: [AGENTS.md](../AGENTS.md). Every tool, its arguments and its
answer: [contracts/mcp.md](contracts/mcp.md).

## Connecting

The server is `gatekit mcp`, one JSON message per line on stdin and stdout; one server drives one
copy. The repository's `.mcp.json` registers it as `gatekit` for the copy `agent` (or the one
`GATEKIT_INSTANCE` names), from the build already there: run `dotnet build` first, since the
registration never builds by itself.

For a client started elsewhere, or several agents with a copy each:

```bash
gatekit --instance world mcp config                  # print the registration for the copy `world`
gatekit --instance world mcp config --out <folder>   # write <folder>/.mcp.json
gatekit mcp tools                                    # list the tools, nothing started
```

An agent's first call is `bg3_status`: its `instance` names the copy this server drives. Two
agents on two copies cannot disturb each other or the installed game.

For game-logic automation, start the copy with `gatekit --instance world up --headless --save NAME`
and connect MCP to the same `world` copy. Use a drawn run for actions that depend on rendered
frames. The startup modes and their requirements are in [the CLI contract](contracts/cli.md#up).

**A fair client.** Started with `GATEKIT_FAIR=1` in its environment, the server gives the client
only the rows of the event log a player could know (`knows = player`), in `bg3_events` and in
attention alike. The filter is the host's, set by whoever starts the server, never by the client.
What fair means: [api-rules.md](api-rules.md).

**An agent that plays.** For a model that plays the game rather than studies it, serve GateKit
AI's four tools alone: `gatekit --instance <copy> mcp --only ai_`. Its first call is
`ai_session` with the mode (`blind`, what a player at the screen has, or `open`) and the owner's
prompt; without MCP, `mcp --only ai_ tools --format functions` and `mcp call` are the loop around
any model. → [mods/AI/README.md](../mods/AI/README.md)

## The tools

| Group | Tools |
|---|---|
| the game | `bg3_up`, `bg3_down`, `bg3_status`, `bg3_logs`, `bg3_key`, `bg3_screenshot`, `bg3_sound`, `bg3_windows`, `bg3_window` |
| what is happening | `bg3_state`, `bg3_events`, `bg3_wait`, `bg3_dialog`, `bg3_fast_scenes` |
| running code | `bg3_lua`, `bg3_call`, `bg3_api`, `bg3_reload` |
| a clean world | `bg3_checkpoint`, `bg3_reset`, `bg3_pause` |
| the engine's components | `bg3_seen_*`, `bg3_probe*`, `bg3_verify`, `bg3_coverage_todo`, `bg3_recipe`, `bg3_run_report` |
| what is known | `bg3_wiki`, `bg3_names`, `bg3_lead`, `bg3_record`, `bg3_wish` |
| the registry's live layer | `bg3_registry_at`, `bg3_registry_show`, `bg3_registry_note`, `bg3_registry_withdraw` |
| tools of your own | `bg3_plugin_errors`, listed only while a file in `mcp-tools/` failed to load |

`bg3_call` reaches every `GK.*` function by name with JSON arguments, and `bg3_api` lists them
with the comments from their Lua. A tool of your own is a JSON file in `mcp-tools/`, live on the
next `tools/list` without restarting the server ([src/GateKit.Mcp](../src/GateKit.Mcp/README.md)).

## Waiting: the event log and attention

What happens in the game is one stream, the event log; attention is its view of what waits for
the player (a dialogue, a roll, a reaction, a fight) and what changed. `bg3_events` reads the
stream from a cursor (`since`); `bg3_wait` returns the moment an attention item appears, or when
an expression comes true, with the timeout as the last resort. Every answer of a game tool carries
an `attention` note of what the caller has not yet been told. The rows and their kinds:
[contracts/bridge-protocol.md](contracts/bridge-protocol.md).

## Working habits

- **Read the game, do not look at it.** Almost everything a picture could tell is a call that
  returns JSON: the party, the dialogue, whose turn it is. Take a screenshot when a person asks
  for one, or to see something that reads wrong.
- **Wait for a row, not for a time.** A fixed sleep is the usual reason a run is slow and flaky.
- **Reset instead of restarting.** A `bg3_checkpoint` once the game is playable, a `bg3_reset`
  between attempts. Another level or save is a load in the running game (`gatekit reset --save
  NAME`), not a new process; `up` again only for a new Script Extender build, another mod list or
  a game that died.
- **Add what is missing to the toolkit.** The same Lua written twice is a tool; what the game-side
  API lacks is a `GK.*` verb with a comment above it, held to [api-rules.md](api-rules.md).
- **Close what you open.** `bg3_down` closes the copy; a game left running holds it.
- **Know the game's rules.** Where Baldur's Gate 3 differs from D&D 5e: [game-rules.md](game-rules.md).
- **Ask the game's memory and code, not guesses.** Who writes a field (a watch), who calls a
  function and with what (a probe, up and down), which functions run for an action (coverage, an
  action against the same scene idle), what happened before any Lua (the start's timeline): the
  instruments and when each answers: [writing-tests.md](writing-tests.md#the-instruments-at-hand).
