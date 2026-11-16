# The home

Everything GateKit keeps lives in one folder, the home, found through one setting. How the home
is found and which variable overrides which location: [cli.md, "Settings"](../contracts/cli.md#settings).
`gatekit doctor` shows every location as resolved; `src/GateKit.Home/GateKitHome.cs` is the only
code that computes them.

## Layout

```
<home>/db/index/gameindex.sqlite        the game index
<home>/db/symbols/symbols.sqlite        the symbol database
<home>/db/decomp/                       decomp.sqlite, a Ghidra project and state files per binary
<home>/tools/                           Ghidra, a JDK, ilspycmd
<home>/se-builds/<build>/               Script Extender builds (the pinned release is fetched here)
<home>/reference/macos-symbols/         reference files of the macOS build
<home>/reference/game-files/            reference lists: every file of one build, with its hashes (cli.md, `game verify`)
<home>/downloads/                       downloads; downloads/mods/ holds mods kept for study, never loaded
<home>/mods/library/                    the mod files game copies run with
<home>/mods/sets/<name>.json            a named mod set: which mods, in what order, why
<home>/play/characters/  <home>/play/saves/
<home>/state/                           the toolkit's state, shared by every checkout
<home>/mcp-tools/                       MCP tool plugins (a checkout's own mcp-tools/ comes first)
<home>/cache/wiki/                      pages read from bg3.wiki
<home>/cache/run/                       copies of long-running programs
<home>/cache/build/<copy>/              the bridge mods packed for one game copy
<home>/cache/index-work/
<home>/cache/registry-check/            what `registry check` took from each file, by its content (cli.md, `registry check`)
<home>/cache/unpack/<pak>/              a mod's .pak unpacked to be read (`gatekit mod unpack`), never loaded
<home>/scratch/  <home>/backups/
```

State is what cannot be rebuilt: who started a copy, its checkpoint, its backup of the player's
mod list, run output. Cache is what a command rebuilds when it is missing. A checkout keeps
nothing of its own; a `.state` folder in a checkout is reported by `doctor` and not read.

## State

```
state/script-extender.json              the Script Extender build a run loads when nothing names one
state/game-fingerprint.json             the installed game's build and verdict (cli.md, `game verify`)
state/deck/                             the deck: deck.json, tiles.json, labels.json, requests/, deck.log
state/sweep/                            sweep plans and their state files
state/registry-live/                    the registry's live layer: entries/ (one file an id, its claims
                                        with who, when, task), settled/ (clashes settled); cli.md, "The live layer"
state/calls/journal.jsonl               one line a call of the program: its command's words, never its
                                        arguments; how long it took and its start; `gatekit calls`
                                        reads it, GATEKIT_CALLS=0 writes none
state/instances/<copy>/                 one game copy: owner.json, bridge-built.json, checkpoint.txt,
                                        sefork.json, se-logs/, runs/, shots/, loose.json,
                                        game-fingerprint.json
```

Files several builds share (`script-extender.json`, `deck/deck.json`, `deck/labels.json`) are
changed under a cross-process lock (`<file>.lock`), written beside the file and moved over it,
and carry `"version"`. A build refuses a file of a newer version and leaves it alone; the first
write of a newer build upgrades an older one. `src/GateKit.Home/SharedFile.cs` does this.

Everything under `instances/<copy>/` and `cache/build/<copy>/` belongs to that copy alone, so
builds from different checkouts can drive different copies at the same time. A copy's
`owner.json` names the build, checkout and bridge sources that started it last; `up` of a running
copy refuses and names that owner, `down` says whose copy it stopped, and other commands attach
(the bridge protocol version check guards against an incompatible bridge). Its paks are packed
again when `bridge-built.json` names other bridge sources than the build that starts it.

A copy's mod list is its own profile's `PlayerProfiles/Public/modsettings.lsx`; `doctor` shows it.

## The exception: game copies

The copies of the game (`instances/<copy>/game`, the copy's profile) stay beside the game, in
`<game's parent>/Baldurs Gate 3 GateKit` or wherever `GATEKIT_INSTANCES` names. A copy is made of
hard links into the game's files, and hard links cannot cross volumes.

## Coverage of the resolver

It covers every location GateKit reads or writes on its own behalf, on Windows and Linux, with a
configured home or none. It does not cover the game's own folders (the profile, Script Extender's
build cache), which belong to the game; the coverage record in `research/coverage`, which is
content of the checkout (`GATEKIT_COVERAGE` points elsewhere); or Steam's library discovery,
which is in `src/GateKit.Platform`.
