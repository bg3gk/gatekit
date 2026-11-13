# How GateKit works

Four pieces, each replaceable, talking through written-down contracts.

```
   you · a scenario · an MCP client · an AI agent
                      │
   ┌──────────────────┴───────────────────────────────────┐
   │  the host:  gatekit                                  │
   │    Stand     starts the game, instances, backups     │
   │    Bridge    a queue of Lua commands                 │
   │    GameData  an index of the game's own files        │
   │    Symbols   a database built from the Toolkit       │
   └──────────────────┬───────────────────────────────────┘
                      │  files in the game's profile
   ┌──────────────────┴───────────────────────────────────┐
   │  Baldur's Gate 3                                     │
   │    Script Extender (GateKit's build)                 │
   │      the GateKit mod   GK.*  — reads and acts        │
   │      Weave             windows for any mod           │
   └──────────────────────────────────────────────────────┘
```

## The host and the game speak through files

Script Extender gives a mod no sockets: Lua inside the game can read and write files under its
own folder, and nothing else. So that is the channel. The host appends a command to
`queue_<side>.jsonl`; the mod polls it six times a second, runs the Lua, and writes the answer
to a file of its own. Any number of processes may talk to one game at once, which is what lets
a scenario, a watcher and an agent share a game.

Files also cross boundaries that sockets do not: from Windows into a Wine prefix, from a
container into a game. The whole protocol is one page: **[contracts/bridge-protocol.md](contracts/bridge-protocol.md)**.

**Server and client are separate.** The game runs two Lua states — the server owns the world
(characters, items, dialogue), the client owns what you see (UI, camera, scenes). They have
separate channels; `exec server` and `exec client` choose one. A third, `ui`, is the client
inside the UI's own context, needed for anything that touches the game's Noesis interface.

## Instances: a game of your own

`--instance <name>` makes a clone of the game folder beside the installed one. Files are hard
links and folders are junctions (symlinks on Linux), so a clone costs a few hundred megabytes
rather than 150 GB, but it has its own profile, saves, mod list, Script Extender settings and
logs. Nothing it does touches your installation or another instance, and several can run at
once.

Without an instance the stand works on your installed game — and then it is careful: it copies
your saves, mod list and extender settings away before starting, and puts them back on `down`.
It kills the game rather than quitting it, so a test can never be saved over your playthrough.

## The deck: every game in one window

Several games at once are useful and were hard to live with: each new one threw its window over
your work and took the keyboard, and Script Extender opened a console window per copy. Neither
happens now — a game of the stand's is started without ever taking the foreground, and the
extender writes what it prints to a log file that `logs` reads.

`gatekit deck` goes further: one window that holds them all, a tile each with its instance name,
its label and its process id. It finds the games by itself, so a deck opened later adopts the
games already running, and a game started without one simply keeps its own window.
`gatekit window list|maximize|grid|label|release` (and the same over MCP) names the copies and
rearranges them — an agent can hold its run in view while a person moves the rest about.

None of this changes how a game is read: `shot`, `key`, `exec` and the bridge work on a copy that
is behind another window, out of view or in no deck at all. That rule is older than the deck and
the deck does not get to break it. The design is
**[design/deck.md](design/deck.md)**, the build is [src/GateKit.Deck](../src/GateKit.Deck).

## Starting fast, and going back

`up` starts the executable directly: no Steam, no launcher, no logo video, no menu. With
`--level` the game boots into a test level in about 40 seconds; with `--save` it loads a named
save. The bridge's first heartbeat is how the host knows the game is ready.

Restarting for every test would be slow, so the world can be rewound instead: `checkpoint`
saves it, `reset` loads that save back and plays the level's opening scene through at twenty
times speed — about 14 seconds, against a 40-second restart.

## Hot reload

`link` points the stand at a mod's Lua, `reload` copies it into the game's loose-file folder and
restarts both Lua states in place: about a second from a saved file to the new behaviour
running. `watch` does it whenever a file changes. The bridge's own Lua reloads the same way
(`bridge-reload`), and survives a `reset`.

## What runs inside the game

**The GateKit mod** is a development mod: the channel, the in-game console, and the `GK.*` API —
the party and their equipment, scenes and dialogues, the engine's components, staging a fight,
the game's speed, and an agent-facing layer that only does what a player could do
([contracts/lua-api.md](contracts/lua-api.md)).

**[Weave](../mods/Weave)** is separate and useful on its own: a window is a `.weave` file with a
template, a script and a style, with reactive state and themes, reloaded live. Any mod may
depend on it; it does not need GateKit.

Both need **GateKit's build of Script Extender** ([bg3gk/bg3se](https://github.com/bg3gk/bg3se)),
a fork of Norbyte's that exposes far more of the engine's own components, under the game's own
names. `gatekit se install` fetches it; the stock extender loads the mods but most of the API
stays silent.

## What needs no game at all

Two databases, built on your machine from your own copy:

- **the game's data** — timelines, dialogues, templates, stats, localization — read straight out
  of the `.pak` files;
- **symbols** from the official Toolkit, which ships the engine split into DLLs that export
  their C++ names, which is as close to source as there legally is.

Neither is in this repository, and neither can be: they are Larian's. The tools that build them
are ours.
