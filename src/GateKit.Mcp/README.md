# GateKit.Mcp — the game as tools an agent can call

`gatekit mcp` serves the test stand over the [Model Context Protocol](https://modelcontextprotocol.io):
one JSON message per line on stdin and stdout. An AI agent starts the game, reads it, runs Lua in
it and closes it again, without a screenshot and without the window in front.

The contract is [`docs/contracts/mcp.md`](../../docs/contracts/mcp.md); it also records every place
where this server deliberately differs from the Python one it replaces.

```bash
gatekit mcp                      # serve on stdio (what an MCP client runs)
gatekit mcp tools                # list what it offers, nothing started
gatekit --instance mcp mcp       # serve on a game copy of your own
```

The instance comes from the same settings as every other command: `--instance`, `GATEKIT_INSTANCE`
or `BG3_GATEKIT_INSTANCE`. One server process serves one instance.

The repository's `.mcp.json` registers this server as `gatekit`, so anyone who opens the folder in
an MCP client that reads that file gets it.

## The tools

18 of them, 17 with the names the old server used:

| | |
|---|---|
| the game | `bg3_up`, `bg3_down`, `bg3_status`, `bg3_logs`, `bg3_key`, `bg3_screenshot`, `bg3_sound` |
| what is happening | `bg3_state`, `bg3_events`, `bg3_dialog`, `bg3_fast_scenes` |
| running code | `bg3_lua`, `bg3_wait`, `bg3_call`, `bg3_api`, `bg3_reload` |
| a clean world | `bg3_checkpoint`, `bg3_reset` |

`bg3_call` is the zero-setup path to everything else: every `GK.*` function of the bridge is
callable by name with JSON arguments, and `bg3_api` lists them with the comments from their Lua.
A function added to `mods/GateKit` is callable the moment `gatekit bridge reload` has run — no tool
of its own, no restart of this server.

## Tools of your own, without rebuilding

The old server loaded `stand/mcp_tools/*.py` and re-imported them when they changed. The same idea
here, without a Python in the loop: **one JSON file per tool**, in `mcp-tools/` next to the
solution (or wherever `GATEKIT_MCP_TOOLS` points). The folder is read again whenever a file
changes, so a tool is live on the next `tools/list` — an agent can write one mid-session.

A tool is what an agent reads about it plus one bridge call:

```json
{
  "name": "bg3_gold",
  "description": "How much gold a party member carries.",
  "inputSchema": {"type": "object", "properties": {"who": {"type": "string"}}},
  "side": "server",
  "function": "Character",
  "args": ["$who"]
}
```

* `side` — `server`, `client` or `ui` (default `server`).
* `function` — a path under `GK`, as `bg3_call` takes it (`Party`, `Stage.Fight`).
* `args` — the positional arguments. A string that is exactly `"$name"` is replaced by the call's
  argument of that name (null when it was not given); anything else is passed as written.
* `timeout` — seconds to wait for the answer (default 20).

For more than one call, write the Lua instead; the call's arguments arrive in the table `args`:

```json
{
  "name": "bg3_gold",
  "description": "How much gold a party member carries.",
  "side": "server",
  "lua": "local who = GK.Character(args.who); return who and who.Gold or 0"
}
```

A file may also hold an array of such tools. One that is wrong never stops the server: it is
dropped and `bg3_plugin_errors` (listed only while something failed) says why. A tool may not take
the name of a core tool.

This stays data on purpose. A tool file is read, not executed: nothing in `mcp-tools/` can run host
code, so a tool written by an agent mid-session is no more dangerous than the Lua it sends.
`mcp-tools/_example.json` is ignored (names starting with `_` are), and is there to be copied.

## What this is built on

The official C# SDK, NuGet **`ModelContextProtocol` 2.4.0** (it ships a `net10.0` target). The
handshake, capabilities, `tools/list`, `tools/call`, `ping` and cancellation are the SDK's.

The **transport is ours** (`BatchStdioTransport`), for the two things the Python server got wrong
(`docs/contracts/mcp.md`, section 3):

* **Batches.** A JSON-RPC array on one line killed the Python server; the SDK's own stdio transport
  does not carry batches either, since the MCP spec dropped them in 2025-06-18. Ours splits an
  array into its messages, hands each to the session, and collects the answers back into one array
  written when the last one is ready. A batch of notifications alone is answered with nothing, as
  JSON-RPC asks.
* **Noise.** A line that cannot be a message (a build line, a stray print) is skipped instead of
  ending the server; one that tried to be JSON is answered with a parse error.

Concurrency needed no work: the SDK serves requests side by side, so `ping`, `tools/list` and a
second tool are answered while `bg3_up` or `bg3_reset` is still running. Every call gets a stand of
its own with its own output buffer, so two calls never mix their log lines.
