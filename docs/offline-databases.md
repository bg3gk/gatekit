# The offline databases

What GateKit knows about the game without the game running: three SQLite databases built on your
own machine, the wiki read through a cache, and other people's mods read as text. Where each
file lives is in [design/home.md](design/home.md); `gatekit doctor` says whether each database is
there, whether it was built here or fetched, and which build of the game it was made from. Add
`--json` to a command for an answer a program can read; an MCP client gets the same through the
tools ([contracts/mcp.md](contracts/mcp.md)).

| Database | Holds | Made by |
|---|---|---|
| the game index (`gameindex.sqlite`) | the game's data: every file in its packages, timelines, dialogs, templates, placed objects, stats, Osiris, localized text, voice lines | `gatekit data build`, or fetched with `gatekit data install` |
| the symbol database (`symbols.sqlite`) | facts about the game's code: names, addresses, sizes, virtual tables, layouts, ECS types, calls, text constants | `gatekit symbols build`, or fetched with `gatekit symbols install` |
| the decompiler database (`decomp.sqlite`) | pseudo-code of the game's and the Toolkit's functions, searchable | `gatekit decomp run` and `decomp load`; never published |

Nothing of these is in the repository: each is derived from the game and made where the game is
([design/what-we-publish.md](design/what-we-publish.md)).

## Asking across all three

| You have | Ask |
|---|---|
| a word, and no idea what it is | `gatekit data query search <word>` |
| a uuid, a text handle, a class name or an address | `gatekit data query about <it>` - the object and everything that refers to it; a class's exports, fields and ECS registration; a function's names, callers and pseudo-code |
| a scene | `gatekit data query scene <name>` - its dialog, actors and gear changes |
| a track type (`TLShowWeapon`) | `gatekit data query track <name>` - in the data and in the code |
| a function to hook | `gatekit data query hook <name>` - where it is and what is around it |
| anything else | `gatekit data query sql <index\|symbols\|decomp> "SELECT ..."` (read-only), with `gatekit data query schema` for the tables |

## The game index

Read from the game's own packages in its `Data` folder - the unmodded game, never the mods of a
profile. `gatekit data build` builds it (English by default, `--languages` for more); it is
rebuilt only when a package changed.

```
gatekit data stat WPN_Longsword          a stats entry with the fields it inherits
gatekit data dialog <name> --tree        a dialog's flow, text, flag checks and flag changes
gatekit data chain WPN_Longsword         a stat down to its template, visual, model and materials
gatekit data refs <uuid>                 everything that mentions a value
gatekit data osiris TemplateEquipped     an Osiris event and the story goals that use it
gatekit data check-mod <folder or .pak>  a mod's references that lead nowhere, and what it overrides
```

What to know when reading it:

- When two packages provide the same timeline or dialog, the earlier one is marked `shadowed`;
  the views (`v_tracks`, `v_actor_objects`) leave shadowed rows out.
- An on/off track spells out only `False`: a `TLShowWeapon` key without the attribute means
  "shown". `tracks.hides` and `tracks.shows` say it plainly.
- A timeline's actor is a slot, not a character. Who fills it comes from the dialog's speaker list
  (`v_actor_objects`), and that can be a group such as `GROUP_Players`.

## The symbol database

The game ships with almost no names in it. The official Toolkit ships the same engine as DLLs that
export complete C++ declarations, and Script Extender's source describes the layout of many engine
types and the byte patterns it finds things by. `gatekit symbols build` reads the Toolkit, the game's
executables and a Script Extender source tree (`--bg3se`; the home's fork by default) and matches
the game's functions to the Toolkit's by text constants, direct calls and virtual tables, in
rounds until nothing new turns up. It is rebuilt after every update of the game or the Toolkit:
every address changes.

```
gatekit symbols find ShowWeapon                         functions by name
gatekit symbols fields ecl::TLPreviewDummy              fields from the Toolkit's accessors and Script Extender's layout
gatekit symbols system ecl::EquipmentVisualsSystem      one ECS system: registration, components, methods, virtual table
gatekit symbols users ecl::TLPreviewDummy --callers 2   game functions that touch a type, and who calls them
gatekit symbols digest esv::camp::EndTheDaySystem       what a system's update does, as facts, no pseudo-code
gatekit symbols signature <binary>+0xRVA                a byte pattern for a hook
gatekit symbols export --format ghidra --out names.tsv  every known name, for a debugger or disassembler
```

What to know when reading it:

- **Two kinds of address.** `EoCClient.dll+0x...` and the other Toolkit addresses are addresses in
  the editor. Only `bg3.exe+0x...` and `bg3_dx11.exe+0x...` are addresses in the game; the two are
  different builds (Vulkan and DirectX 11), and an offset from one means nothing in the other.
- **A match is a claim with evidence.** Each pair records how it was found (`method`) and how the
  two sizes compare (`relation`): `contains` means the game function is much larger and probably
  has the Toolkit function inlined into it. A pair marked `~` was found by comparing pseudo-code.
- **Not found is not absent.** The game is linked with heavy inlining: many Toolkit functions have
  no body of their own in it. Only direct calls are followed, and that is not how the ECS runs its
  systems.
- **One address, several names.** The linker folds identical functions, so a getter has one
  address and the names of every class that has one.
- **Offsets come from three places**: `accessors.offset` from the Toolkit's code,
  `accessors.game_offset` from the game's when the accessor was found there, `se_fields.offset`
  computed from Script Extender's declarations (`checked = 1` where a field Script Extender names
  after its offset confirms it). Where only one exists, verify before relying on it.
- **Script Extender's data is as old as its source tree**: `gatekit symbols se <text> --report`
  says which of its patterns still match.

The macOS build keeps the game's own member names for some fields; files read from it live in the
home's `reference/macos-symbols`, and `symbols replication` and `symbols savemodel` use them when
they are there.

## The decompiler database

Ghidra, run without its window, analyses a binary with the names the symbol database knows and
decompiles every function into `decomp.sqlite`, with full-text search. It needs Ghidra and the JDK
it asks for in the home's `tools`; the projects and the database live in the home's `db/decomp`.
A full run over the game executable is a long job, counted in hours; everything else here is
shorter by an order of magnitude or more.

```
gatekit decomp run bg3.exe                    analyse and decompile
gatekit decomp load                           put finished output into the database
gatekit decomp refresh                        give the project what changed - the registry's confirmed set - and decompile again what shows it
gatekit decomp serve                          keep the decompiler open; `decomp code` asks it for typed text
gatekit decomp code esv::RollSystem::RollDice our reading of a function, by registry id, name or address
gatekit decomp code <binary>+0xRVA --raw      the decompiler's text untouched (--json: the text and the annotations)
gatekit decomp grep "ShowWeapon"              names and pseudo-code
gatekit decomp at <binary>+0xRVA              the function at an address and its names, no pseudo-code
gatekit decomp fixedstrings Sneaking          the game's global FixedStrings, by text or address
gatekit decomp dotnet                         the editor's managed assemblies as C# (needs ilspycmd)
```

The two tools feed each other. From nothing: `symbols build`; `decomp run` and `decomp load` for
the game and the Toolkit DLLs; `decomp match`, which pairs Toolkit and game functions by their
pseudo-code; `symbols build` again, which starts from those pairs too; then `decomp refresh` to
put the new names into the pseudo-code. The decompiler's project is a cache: only what the registry
holds on an applied rung of certainty reaches it, and `refresh` gives it what changed.

`decomp at` is how an address from anywhere becomes a function: a hit of the memory instrument
(`GK.Memory.Hits`), a debugger, a crash. It answers with the function that contains the address
and where it starts, its signature and its names from both databases and from the registry -
never its code.

## Our reading: the registry

What we have understood of the code is not in these databases: it is in the repository's
[registry](../registry/README.md), one file for each function, type and global keyed by its name
as the game writes it, with a table for each build of where each one is. The databases are derived
and rebuilt; the registry is written by hand and read by `decomp at`, `decomp names`,
`decomp code` and `names`. `decomp code` lays it over the pseudo-code as it is asked for: what an
applied rung of certainty holds is written into the code, what a shown rung holds beside it, with
a header of what the function does and what proves it ([contracts/cli.md](contracts/cli.md),
"`decomp code`"). Every process on one machine shares what it finds before it is committed,
through the home's live layer:

```
gatekit registry note esv::RollSystem::RollDice --at <binary>+0xRVA --rung guess \
    --rests-on "..." --would-confirm "..."      write a name or a guess, seen by every process at once
gatekit registry withdraw esv::RollSystem::RollDice
                                                take the newest claim back, for every process at once
gatekit registry at <binary>+0xRVA              what is known there, committed and live
gatekit registry show esv::RollSystem::RollDice what is known of an id
gatekit registry commit                         the live layer into this checkout, provenance left behind
gatekit registry check                          the registry's rules, also run in CI
gatekit registry fingerprint <address or id>    the facts that find a function again after a patch
```

The decompiler writes each binary's pseudo-code to `pseudocode.jsonl` in its project folder before
`decomp load` puts it into `decomp.sqlite`.

The pseudo-code is C-like text reconstructed from machine code: identified functions carry their
names and parameter types, everything else is `FUN_<address>`, `param_1` and offsets from a
pointer. It is derived from the game's binaries and stays on the machine that made it.

## The build each database was made from

`gatekit game verify` holds an installed game - or a copy, with `--instance` - to a source of
invariants, file by file: Steam's own manifests on this machine, or a reference list of every file
of one build with its hashes. It lists what is modified, missing or extra and records a
fingerprint; with no source it gives no verdict of clean. It never writes into the game.

```
gatekit game verify                     every file hashed
gatekit game verify --quick             sizes, and hashes of the executables and libraries only
gatekit game verify --write-reference   after a clean verdict from another source, write this build's reference list
```

Every database records the game build it was made from and that build's verdict (`game_build`,
`game_verdict`): `data build` and `symbols build` write them from the game's fingerprint, an index
built without them is stamped from its inputs with `gatekit data stamp`, and the pseudo-code with
`gatekit decomp stamp` from the binaries its projects imported. `gatekit doctor` warns when a
database was made from another build than the game that is installed.

## The wiki

bg3.wiki as context: what players and modders know about a spell, a status, an item or a rule.
Answers are cached in the home (`cache/wiki`), so a question asked once is answered again without
a network.

```
gatekit wiki find "opportunity attack"   titles that match
gatekit wiki rules "Attack roll"         a page as its sections, the wiki's "needs verifying" marks listed
gatekit wiki uid Shout_Rage              the page of a spell, status or passive by its stats name
gatekit wiki card Shout_Rage             the same thing as fields, from the wiki's tables
```

A spell's, action's, passive's or item's page carries the game's own identifier, which ties it to
the index. The wiki says what players can do and what a thing needs; it is not a source of layouts
or of the game's own member names - a name taken from it is inferred, never the game's.

## Mods as a source

Other people's mods show which parts of the engine are worth knowing and how they are reached. They
are read by a static scan of their text - Lua, stats, story goals, XAML, the file list - and never
run, loaded or installed. Mods kept for study live in the home's `downloads/mods`.

```
gatekit mod unpack <pak>                  a .pak written out as plain files, outside any game folder
gatekit mod usage <folder or .pak>        what one mod uses: components, Ext calls, events, Osiris, stats, boosts, XAML bindings
gatekit mod demand --each <folder>        the same counted over every mod in a folder, as a demand file
gatekit names demand report               the demand joined with the coverage record: how far it is from right on those types
```

`mod usage` maps the extender's names to the engine's types through the Script Extender source
tree and checks Osiris names and game files against the index.
