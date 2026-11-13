# Driving the game

GateKit exists partly so an AI agent can work on a Baldur's Gate 3 mod the way a person does:
start the game, change a mod's Lua, see what happened, and say whether it worked. This page is
how to connect one and what it can then do.

> One of three pages about agents: this one is for an agent **using** GateKit to drive the game.
> [AGENTS.md](../AGENTS.md) is for an agent **changing GateKit itself**;
> [design/ai-controllers.md](design/ai-controllers.md) is for an agent **playing inside the
> game**, a character or a whole side in combat.

## Connecting

The repository registers the server in `.mcp.json`, so a client that reads it finds `gatekit`
with no setup. For Claude Code:

```bash
claude mcp add gatekit -- dotnet run --project src/GateKit.Cli -- mcp
```

Any MCP client works the same way: the command is `gatekit mcp`, speaking over stdin and stdout.
`gatekit mcp tools` lists the tools without starting anything, which is the quickest check that
the build and the settings are sound.

**Give each agent its own game.** `GATEKIT_INSTANCE=<name>` in the server's environment, and
that agent gets a clone of the game with its own saves and mod list. Two agents with different
instance names cannot disturb each other, or you.

## The tools

| | |
|---|---|
| `bg3_up` / `bg3_down` | start the game into a level or a save; close it and put everything back |
| `bg3_status` / `bg3_state` | is it running, and the game in one call: party, equipment, scene, controls |
| `bg3_lua` / `bg3_call` / `bg3_api` | run Lua inside the game; call any `GK.*` function by name; list what there is to call |
| `bg3_wait` | poll an expression until it is true, instead of sleeping and hoping |
| `bg3_events` | what happened since you last asked |
| `bg3_dialog` / `bg3_fast_scenes` | the dialogue on screen and its answers; play scenes faster so they reach the choice |
| `bg3_checkpoint` / `bg3_reset` | save the world, and bring it back in about 14 seconds |
| `bg3_reload` | a mod's Lua into the running game, about a second |
| `bg3_logs` | the extender's console and the Osiris trace |
| `bg3_key` / `bg3_screenshot` | press a key, take a picture — both while the window is behind others |
| `bg3_sound` | how loud this copy is: the volume of each category, mute and unmute, so several runs are not one wall of noise |

`docs/contracts/mcp.md` is the exact reference: every schema, every result shape.

## How to work with it

**Read the game, do not look at it.** A screenshot is the slowest and least reliable thing in
this toolkit. Almost everything a picture could tell you is a function call that returns JSON:
who is in the party, what they carry, what the dialogue says, whose turn it is. Take a picture
when a person asked for one, or when something looks wrong and you want to see it.

**Wait on conditions, not on time.** `bg3_wait` exists so you do not sleep five seconds and hope.
A fixed sleep is the usual reason an agent's run is flaky.

**Reset instead of restarting.** Put a `bg3_checkpoint` right after the game is playable, and a
`bg3_reset` between attempts. Three times faster than starting again, and the world is exactly
as it was.

**Add what you need.** If you find yourself writing the same Lua repeatedly, add a tool: a JSON
file in `mcp-tools/` naming a bridge function and its arguments, live on the next `tools/list`
without restarting the server (`src/GateKit.Mcp/README.md`). If the thing you need does not
exist in the game-side API at all, it belongs in the bridge mod as a `GK.*` function, with a
comment above it — `bg3_api` reads those comments, so documenting it is what makes it visible.

**Close what you open.** `bg3_down` restores the saves and the mod list. A game left running
holds the instance and the next run will tell you so.

## Playing the game, honestly

There is a second layer meant for an agent that plays rather than tests: what a character can
see and hear, what it may legally do, and the odds it would be shown. It only does what a player
could do — no teleporting, no reading what the character cannot perceive, no spending what it
does not have. Cheats are a separate namespace, so an agent cannot reach them by accident.

That layer is being rebuilt as part of the move to this repository; until it lands, reach it
through `bg3_call` and `bg3_api`. The design that will govern it, including characters played by
an agent during combat, is [docs/design/ai-controllers.md](design/ai-controllers.md).
