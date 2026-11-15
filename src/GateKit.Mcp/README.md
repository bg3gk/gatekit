# GateKit.Mcp - the game as tools an agent can call

`gatekit mcp` serves GateKit over the [Model Context Protocol](https://modelcontextprotocol.io):
one JSON message per line on stdin and stdout. An agent starts a game copy, reads it, runs Lua in
it and closes it again, without a screenshot and without the window in front. Every tool, its
arguments and its answer: [docs/contracts/mcp.md](../../docs/contracts/mcp.md).

```bash
gatekit --instance <copy> mcp            # serve on stdio, for one game copy
gatekit mcp tools                        # what it offers, nothing started
gatekit --instance <copy> mcp config     # write the registration for that copy (--out <folder>)
```

One server process serves one copy (`--instance` or `GATEKIT_INSTANCE`). The repository's
`.mcp.json` registers it as `gatekit`, from the build that is already there - it never builds by
itself - and never for the installed game; `bg3_status` says which copy a server drives.

`bg3_call` is the path to everything else: every `GK.*` function of the bridge is callable by name
with JSON arguments, and `bg3_api` lists them with their class and the comments from their Lua. A
function added to `mods/GateKit` is callable once `gatekit bridge reload` has run - no tool of its
own, no restart of the server.

## Tools of your own, without rebuilding

One JSON file per tool, in `mcp-tools/` of the checkout (else the home's, or wherever
`GATEKIT_MCP_TOOLS` points). The folder is read again whenever a file changes: a tool file is live
on the next `tools/list`.

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

- `side` - `server`, `client` or `ui` (default `server`).
- `function` - a path under `GK`, as `bg3_call` takes it (`Party`, `Stage.Fight`).
- `args` - the positional arguments. A string that is exactly `"$name"` is replaced by the call's
  argument of that name (null when it was not given); anything else is passed as written.
- `timeout` - seconds to wait for the answer (default 20).

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
dropped, and `bg3_plugin_errors` (listed only while something failed) says why. A tool may not take
the name of a core tool. `mcp-tools/_example.json` is ignored, as every name starting with `_` is,
and is there to be copied.

A tool file is read, never executed: nothing in `mcp-tools/` can run host code, so a tool file,
live on the next `tools/list`, can do no more than the Lua it sends.

## What it is built on

The official C# SDK, NuGet `ModelContextProtocol`: the handshake, capabilities, `tools/list`,
`tools/call`, `ping` and cancellation are the SDK's, and it serves requests side by side, so `ping`
and a second tool are answered while `bg3_up` is still running. The transport is ours
(`BatchStdioTransport`): it splits a JSON-RPC array on one line into its messages and answers them
as one array, and it skips a line that cannot be a message - a build line, a stray print - instead
of ending the server, answering one that tried to be JSON with a parse error.
