# How GateKit works

A **host** on your computer (`gatekit`, C#) drives a **mod inside the game** (Lua) through a
channel of files, in a **copy** of the game of its own. Everything GateKit keeps lives in one
**home**.

```
   you · a test · an MCP client
                │
   the host: gatekit ──── the home: databases, builds, state
                │  files in the copy's profile
   Baldur's Gate 3 (a copy), GateKit's Script Extender
       the GateKit mod: GK.*        Weave: windows
```

## The host and the channel

Script Extender gives a mod's Lua no sockets, only files under its own folder, so files are the
channel. The host appends commands to a queue in the game's profile; the mod runs each and writes
its answer to a file of its own. Any number of processes may talk to one game at once. The game
has two Lua states, each with its own channel: the **server** owns the world (characters, items,
dialogue), the **client** what is shown (interface, camera); `ui` is the client inside the
interface's own context. The whole protocol: [contracts/bridge-protocol.md](contracts/bridge-protocol.md).

## The mod

**The GateKit mod** (`mods/GateKit`) is the channel, the in-game console and the `GK.*` API: the
party, scenes and dialogues, the engine's components, staging a scene, the game's speed, the
interface. The API's reference is [contracts/lua-api.md](contracts/lua-api.md).
**[Weave](../mods/Weave/README.md)** is separate and usable on its own: in-game windows written as
`.weave` files. Both need **GateKit's build of Script Extender**
([bg3gk/bg3se](https://github.com/bg3gk/bg3se)), which `gatekit se install` fetches; on the stock
extender the mods load and the bridge stays silent.

## The home

One folder holds what GateKit keeps: the databases, the Script Extender builds, the copies'
state, caches, downloads and backups. It is named by `"home"` in `gatekit.json` (found from the
working folder upwards, so every checkout under it shares it) or by `GATEKIT_HOME`. A checkout
keeps nothing of its own. `gatekit doctor` lists every location and where it came from. The
layout: [design/home.md](design/home.md).

## Copies

A **copy** is a folder beside the installed game, made of hard links and folder links, with a
profile, saves, mod list, Script Extender settings and logs of its own. In prose it is a copy,
never a "stand"; the option that names one is `--instance <name>`. The first `up` with a new
name makes it, `gatekit instances` lists them, `gatekit destroy` deletes one. Nothing a copy does touches the installed game or another copy, and several run at
once.

- **A copy starts bare**: the base game and GateKit's own mods, nothing else unless `up --mods`
  names it.
- **The installed game is written only when a command names it**: without `--instance` (or
  `GATEKIT_INSTANCE`) every command that writes - `up`, `down`, `build`, `link`, `reload`, `watch`,
  `exec`, `key`, `checkpoint`, `reset`, a change of `sound`, `se install` and `se remove` - refuses
  in one sentence unless `--installed-game` is given. With it, `up` backs up the player's saves and
  mod list and `down` puts them back. Commands that only read (`status`, `doctor`, `game verify`,
  `logs`) need neither.
- **`gatekit game verify`** holds an install, or a copy, to a source of invariants - Steam's own
  manifests, or a reference list of a build's files - and lists what is modified, missing or
  extra. With no source there is no verdict of clean. Every database records the game build it
  was made from, and `doctor` warns when it differs.

The copies are the one thing outside the home: hard links cannot cross volumes.

## The deck

`gatekit deck` is one window that holds every running copy, a tile each, so no game window is
thrown over your work. The design: [design/deck.md](design/deck.md).

## The event log and attention

Each side of the game keeps one append-only log of what happened; the host merges both into one
stream, ordered by the game's clock. The families of rows are data in one registry, which the
host reads from the running game, and a row the host cannot place is kept and marked, never
dropped. **Attention** is a view of the same stream: what waits for the player (a dialogue, a roll,
a fight) and what changed. A caller waits for a row, not for a time.
[contracts/bridge-protocol.md](contracts/bridge-protocol.md), sections "The event log" and
"Attention".

## Venues and the reset ladder

A test runs in a **venue**, preferably the **sandbox**. Between tests the world is made clean
again by the cheapest reset rung that holds, verified each time: `fixture`, then `venue`, then
`game`. Each word is defined in [writing-tests.md](writing-tests.md), "Words".

## Fair, cheat, observation

Every verb of the API is **fair** (what a player could do, the player's way), a **cheat** (it sets
the world directly) or an **observation** (it changes nothing), and its namespace says which. The
rules: [api-rules.md](api-rules.md).

## What needs no game

Databases built on your machine from your own copy of the game: the **game index** (the data in
the `.pak` files: templates, stats, dialogues, timelines, localization), the **symbol database**
(the engine's C++ names, from the official Toolkit's DLLs, matched into the game's executables)
and the **pseudo-code database** (the game decompiled with Ghidra); beside them the **wiki
reader** and, in a running game, the **memory instrument** (which instruction wrote a field;
`gatekit decomp at` names its function) and the **entity tracer** (which components changed
between two steps, each change stamped with the engine's frame and command-buffer flush). None of
the databases is in this repository,
because it is derived from Larian's files; the tools that build it are ours.
[offline-databases.md](offline-databases.md)
