# Working in this repository

What an AI should know about this project before changing it: how it is built, where each thing
lives, and the rules it holds itself to. Useful to a person in a hurry too. The nearest
`AGENTS.md` to the file you are editing wins; `CLAUDE.md` points here.

"Agent" means two different things around GateKit, and this file is only the first:

- **an agent working on GateKit** — you, if you are reading this to change the code;
- **an agent using GateKit** to drive the game from outside → [docs/driving-the-game.md](docs/driving-the-game.md);
- **an agent playing inside the game**, a character or a side in combat →
  [docs/design/ai-controllers.md](docs/design/ai-controllers.md).

## What this is

GateKit drives Baldur's Gate 3 from outside: a **host** on your computer (C#) talks to a **mod
inside the game** (Lua) through a file channel, so a test, a command line or an AI agent can
read and change anything in a running game.

What the work is ultimately aimed at - making the engine's decompiled code ours rather than the
machine's, and helping Script Extender reach the whole game - is in
[docs/design/what-this-is-for.md](docs/design/what-this-is-for.md). It is not in the README on
purpose: the README says what the toolkit does today.

```
you / a scenario / an MCP client
        │
    gatekit (src/GateKit.Cli)
        │  Stand: starts the game, instances, backups, reset
        │  BridgeClient: a queue of Lua commands
        ▼
 queue_<side>.jsonl  ──►  the GateKit mod in the game (mods/GateKit)
 res_<side>_<id>.json ◄──  runs the Lua, answers in JSON
```

## Where things live

| If you are changing… | Look in | Its contract |
|---|---|---|
| how the host talks to the game | `src/GateKit.Bridge`, `mods/GateKit/.../Shared/Bridge.lua` | `docs/contracts/bridge-protocol.md` + `schemas/bridge/` |
| starting the game, instances, backups | `src/GateKit.Stand`, `src/GateKit.Platform` | `docs/contracts/cli.md` |
| commands of `gatekit` | `src/GateKit.Cli` and each project's `CreateCommand()` | `docs/contracts/cli.md` |
| publishing or fetching a built database | `src/GateKit.Release`, `index-release.json`, `publish/map/` | `docs/design/what-we-publish.md` |
| tools an agent sees | `src/GateKit.Mcp` | `docs/contracts/mcp.md` |
| the `GK.*` API inside the game | `mods/GateKit/.../Lua/` | `docs/contracts/lua-api.md`, `lua-types/GateKit.lua` |
| in-game windows | `mods/Weave` | `lua-types/Weave.lua`, `mods/Weave/README.md` |
| tests and examples | `scenarios/*.lua`, `tests/` | |

**The contracts in `docs/contracts/` are the source of truth.** Change the contract in the same
commit as the code, or do not change the behaviour.

## Rules

1. **English only** — code, comments, documentation, commit messages. Localized text comes from
   the game's own data, never a hardcoded table. See `CONTRIBUTING.md`.
2. **Name things as the game does.** Where the game has its own name for a field or a function,
   that name wins; other names stay as aliases.
3. **Nothing of Larian's is committed**: no game data, binaries, decompiled code, fonts or
   textures. Anything derived from the game is generated on the user's own machine.
4. **Cross-platform.** Operating-system specifics go behind `IGamePlatform` in
   `src/GateKit.Platform`; nothing else may use Windows-only APIs. No PowerShell, no `.cmd`.
5. **No machine-specific paths.** Settings come from environment variables, `gatekit.json` or
   discovery; `gatekit doctor` explains what is missing.
6. **Prefer a programmatic bridge to a screenshot.** Anything that can be read or pressed
   through the game's own code should be, not through the window.
7. Warnings are errors (`Directory.Build.props`). Keep it that way.
8. **Never sign as yourself.** Whichever assistant you are, you are not a commit's co-author by
   name: the trailer is `Co-Authored-By: GateKit Thrall <...>` from `.github/thrall-identity`.
   The hook rewrites it and CI refuses it otherwise.
9. **Sign what goes out under the bot's name.** Drafts you write for a pull request, commit,
   issue or discussion end with the line in [.github/thrall.md](.github/thrall.md) — last, after
   any trailers. A person reads every draft before it is posted, and text written by a person as
   themselves does not carry the line.

## Checking your work

```bash
dotnet build                       # the whole solution
dotnet test                        # unit tests, no game needed
./gatekit --instance <yours> test  # scenarios against a real game
```

**Never touch the installed game or an instance that is not yours.** Always pass
`--instance <name>`; it clones the game folder with hard links, with its own profile, saves and
mod list. Close every game you start (`gatekit --instance <name> down`).

Scenarios need GateKit's own Script Extender build (`gatekit se install`, or `GATEKIT_SE`
pointing at a build folder); on the stock extender the bridge and Weave stay silent.
