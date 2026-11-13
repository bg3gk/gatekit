## Pilot, 2026-11-12: what one type costs

The full loop (hypothesis from the decompilation → raw check on live instances → header →
build → check through Script Extender → `verified`) was run on 23 types: 5 tags, 17 components
with data (2 to 64 bytes) and 1 system. Inputs were clean: both executables carry a valid
Larian signature, `bin` holds no native mod, and the game ran in the stand instance `coverage`
whose `Mods` folder holds the GateKit bridge only (`research/coverage/clean_stand.py` enforces it).

| | Tried | Verified | Left as hypothesis |
| --- | ---: | ---: | --- |
| Tags | 5 | 5 | — |
| Components with data | 17 | 14 | `ls::SplineComponent` (the game registers 24 bytes, the code allocates 16), `eoc::item::ISDoorClosedComponent` (types fit, no meaning established), `ecl::TimelineShapeShiftComponent` (alive only for moments after a level loads) |
| Systems | 1 | 0 | `ecl::unsheath::VisualStateSystem`: size 0x138 and every member's role established offline, and the live object read with the new `Ext.Debug.GetSystemAddress` matches it slot for slot; not declared in Script Extender yet (what to expose is a decision per system) |

The 19 verified types are commit `a9cfddf1` on branch `gatekit` (`se-fork/patches/0010-*.patch`);
coverage of component types went from 1071 to 1090 of 2647 (40.5 → 41.2 %).

### Time and tokens

| Step | Measured |
| --- | --- |
| Hypothesis from the decompilation, per component | 4–17 minutes of a worker's own estimate (median 7; the 8–12 byte ones 4–6, the 48–64 byte ones 12–17); three workers in parallel finished 17 components and the system in 12.5 minutes of wall clock; 507,000 tokens in all, about 28,000 per type |
| The system | 14 minutes, 11 listings read; its share of the tokens is about twice a small component's |
| Probe of live instances (`probe.py`) | 2.2 s for 12 types |
| Raw + mapped check of 22 layouts on both sides (`check.py`) | under half a minute |
| Build after a header change | 265 s (every translation unit is rebuilt) |
| Game restart on the new build | 47 s to playable |
| A tag | seconds: one generated line, no reverse engineering |

One batch therefore costs about 6 minutes of fixed overhead (build + restart + check) whatever
its size, so batches should be large (50–100 types).

### How much the decompilation can name

- **Toolkit accessors name almost nothing of what is missing.** Of the 1172 components with data
  that had no layout, the Toolkit exports field accessors for 16 (1.4 %) and any symbol at all
  for 121 (10 %). In the pilot one field of one type had a Toolkit name (`ls::SplineComponent`,
  `Template` at +8 — and the game agreed with that offset).
- **Structure is always recoverable, meaning mostly.** For all 17 components the workers
  established size, field boundaries and types from the functions that create, fill and save
  the component, and the live check contradicted none of the types. Of the 34 fields of the 14
  verified components, 33 got a name with stated evidence and 1 stayed `field_0`. Several names
  are weak (`State2`, `SlotFlags2`: the second of a pair whose difference was not resolved);
  the comment on the field says so.
- **Every name is "inferred"**, with the function address and what the code does there in the
  field's comment. That is the honest label for this whole project: Toolkit names are the
  exception.

### Do the Toolkit's offsets hold in the game?

Partly — they are a hypothesis, not a fact. Where a Toolkit accessor and Script Extender's
(game) layout name the same field of the same class, 105 of 147 offsets agree (71 %): 16
classes agree entirely, 11 do not, and those are the large editor-heavy ones (`ecl::Character`
0 of 19, `ls::ConstructionFilling` 0 of 5; `esv::Character` 12 of 19). Of the 408 accessors the
symbol base located in the game's own code, 368 (90 %) have the same offset as in the Toolkit.

### What is alive to check against

A layout can only be checked on a component that exists. In the idle test level
(`TUT_Avernus_C`, nothing happening):

| Missing types | All | Alive at rest | One-frame |
| --- | ---: | ---: | ---: |
| Tags | 404 | 40 | 172 |
| With data | 1172 | 434 (231 of them singletons, mostly request queues) | 218 |

So about 60 % of the components with data need the game to be doing something (a fight, a
dialogue, trade, character creation, a second player) or exist for one frame only. More types
are alive in the first seconds after a level loads than later (`ecl::EocCameraBehavior` 17 → 1,
`ecl::TimelineShapeShiftComponent` 10 → 0), which a check started right after loading can use.
This, not the reverse engineering, is the expensive part of the remaining work.

### What the pilot built

- `research/coverage/clean_stand.py` — the stand with a bridge-only `Mods` folder.
- `research/coverage/lua/probe.lua`, `probe.py` — read-only view of live instances of any
  component, mapped or not: per 8-byte slot whether it is a live entity handle, a pointer into
  the executable, a heap pointer or a value; per 4 bytes the range as integer and float.
- `research/coverage/layouts/**.json` — one layout per type: fields with offset, type, where the
  name comes from and the evidence; `status` hypothesis or confirmed.
- `research/coverage/check.py` — the scenario with the two stages; writes `verified`.
- `research/coverage/generate.py` — writes `GameDefinitions/Components/Coverage.h` and the names
  into `Enumerations/ECS.inl` from the confirmed layouts; `static_assert`s pin the sizes.
- `Ext.Debug.GetSystemAddress(name)` in the GateKit build, for systems.

### Traps found

- A new C++ namespace can hide one Script Extender's code uses unqualified: `ecl::level` hid
  `bg3se::level` (a layout may set `cpp_namespace`).
- A component's Script Extender name must also be added to `ExtComponentType` in
  `Enumerations/ECS.inl`; the property-map generator does not do it.
- Script Extender returns `nil` for the null entity handle and drops trailing `nil`s of arrays.
- Padding bytes hold stack garbage that can look like values or handles.
- For components stored behind a pointer the registered size is not always the size of the
  allocation (`ls::SplineComponent`).
- What slowed the workers: `symbols.py users` lists mostly registration and generic helpers
  (the systems that use a component reach it through queries), caller addresses that are not
  function starts are refused by `decomp.py code`, no command for a virtual table by address,
  global `FixedString`s cannot be resolved offline, `decomp.py grep` misses substrings.

Not done in the pilot: `check.py` was not run on stock Script Extender (its first step is the
skip), no write test was made, and the stand's other scenarios were not re-run on this build.

## Stages 0–2, 2026-11-12: tools, one-byte types, timelines and unsheathing

Approved by the user as "stages 0–2". Component coverage went from 1090 to 1514 of 2647 types
(41.2 → 57.2 %); 116 types are verified on live data (40 with fields, 76 tags).

| Batch (commit on `gatekit`) | Types | Verified live |
| --- | ---: | ---: |
| `a9cfddf1` pilot | 19 | 18 |
| `b52e5bf6` one-byte types declared as tags | 398 | 72 so far |
| `470cf265` timelines and unsheathing | 26 | 26 |
| `802ffe71`, `da6f27dd` `Ext.Debug.GetSystemAddress`, `GetComponentHolders`, `GetComponentCounts` | — | — |

### What "a tag" turned out to mean

The registry gives a tag one byte and cannot tell it from a component with one byte of data;
of the one-byte types Script Extender already had, about a third are declared with a field.
Measured on those known types: **on the server the byte of a tag is always 0; on the client
it holds whatever was in memory** (replication does not write it). So a non-zero byte on the
server proves data, and nothing else can be proved by looking. One of the 399 missing
one-byte types failed that test and is a real component: `eoc::stats::MusicalInstrumentComponent`
(the stats `InstrumentType`: 5 on a lute, 6 on a lyre). The other 398 are declared as tags;
35 were alive at rest and showed zero or garbage, 363 were not alive. A tag counts as
verified only when a live holder gave a non-nil `entity.<Name>`; situations raised that from
31 to 76. Two more leads came out of the same table and are not acted on yet: Script Extender
declares `eoc::PickingStateComponent` as a tag, but on the server its byte is 255 on all 312
characters; and `eoc::ActiveComponent` is declared as a component although its byte behaves
like a tag's.

### Catching what lives for a frame

`Ext.Debug.GetComponentHolders(name, max)` walks the storage classes, so it is cheap enough
to call every tick for hundreds of types. `Probe.CaptureStart` (lua/probe.lua) samples new
holders on `Ext.Events.Tick` and reads them raw and through Script Extender in the same tick;
`capture.py` prints the samples like the probe, and `check.py` runs the capture while
`situations.py` plays: drawing and sheathing weapons, taking a weapon off and on and switching
the weapon set, drinking a potion, a dialogue. What each situation brought to life that an idle
level does not have (component types, client / server): weapons 7 / 9, equipment 2 / 19,
dialogue 62 / 32. Still out of reach without new situations: fights, rests, character
creation, shape-shifting, thrown and improvised weapons, a second player.

### Cost, second measurement

| Worker task | Types | Wall clock | Tokens | Per type |
| --- | ---: | ---: | ---: | ---: |
| timelines, 4–48 bytes | 14 | 19 min | 277,000 | 20,000 |
| timeline singletons, 16–144 bytes | 13 | 20 min | 289,000 | 22,000 |
| timelines, 120–592 bytes | 8 | 16 min | 294,000 | 37,000 |
| unsheath family, 1–64 bytes | 18 | 7 min | 186,000 | 10,000 |

A family that shares one system is three times cheaper per type than unrelated types of the
same size, so tasks should follow systems, not sizes. Of 53 hypotheses, 26 were confirmed on
live data the same day; 13 need a situation that creates them, 9 were seen only empty (their
member boundaries are untested), 4 use nested Script Extender types the generator cannot size
yet, 1 has no meanings.

### What workers found useful, and what other projects do

- The Toolkit's DLLs in the decompilation base (real names, typed signatures) settled several
  components that the game's stripped code could not; `ls::FieldMeta<Type Component::*, offset>`
  type names in the game give member types and offsets for the 118 components that have them.
- The systems that use a component reach it through a query: `symbols.py users "<query Spec>"`
  and `users <Component> --callers` (added by the symbol-base session on request, with
  `decomp.py code` for any address, `--grep`, `vtable`, `systems`).
- Looked up how others do it (user's instruction): the macOS reimplementation
  `tdimino/bg3se-macos` takes sizes from the decompiled `AddComponent` instantiations and
  field offsets from `GetComponent<T>` ones, and has a `ProbeStruct` / `HexDump` debug API
  like ours; it describes no rule for telling tags from one-byte data. Its notes say the
  macOS binary keeps mangled C++ names for many functions and type-id symbols — a second
  source of names worth examining if the user wants to obtain that build.

### Leads about layouts Script Extender already has (not verified, not changed)

- `CombatCameraParams`: the use in `bg3.exe+0x1F21DB0` suggests the last three floats are
  ObstructionBufferTime, MinChangeCameraTime, MinAngle — a different order from the header.
- `TLLookAtAutomationConfig::EyeLookAtBone` is declared `uint8_t`; the code copies a
  FixedString at that offset.
- `eoc::PickingStateComponent` (declared as a tag) holds 255 on the server.

## Scenes, levels and the v34 base, 2026-11-12 (evening)

**Base.** By the user's decision the fork moved to the author's v34 branch; coverage now lives
on branch `coverage-v34` (worktree `se-fork/bg3se-v34-coverage`, last commit `a9092a7e`), and
the numbers at the top of this page are collected on that build. None of the types added here
is declared by the author's v34 headers. The v33 branch `devkit` is frozen at `e0f2e866`.

**Staging scenes.** `GK.Stage` (bridge, server side; [stage.md](stage.md)) spawns characters
with gear and a faction, starts and drives fights turn by turn, applies statuses and damage,
kills and raises, makes surfaces, and removes the scene again. `situations.py` uses it for
two new situations, `fight` and `surface`. What a staged fight brought to life that nothing
before had: 87 component types on the client, 145 on the server.

| Batch | Types | How confirmed |
| --- | ---: | --- |
| `e0f2e866` unsheath reasons, weapon-set event | 5 | captured in a staged fight |
| `a9092a7e` timeline events, doors, equipment request | 11 | dialogue and fight captures, live doors |

Layouts in the project now: 403 tags and 56 components with fields in the build; 65 more
hypotheses with fields and one system waiting. Verified on live data: 148 types (was 116).

**Third measurement of the cost** (workers by system, with captured samples where any):

| Worker task | Types | Wall clock | Tokens | Per type |
| --- | ---: | ---: | ---: | ---: |
| client timeline events | 21 | 12 min | 253,000 | 12,000 |
| server timelines | 18 | 18 min | 266,000 | 15,000 |
| weapons, equipment, doors | 15 | 12 min | 261,000 | 17,000 |

Of these 54 hypotheses 11 were confirmed the same evening: only types that some situation
makes alive can be. The other 43 are mostly one-frame requests of cutscene placement, screen
fades, disarming, improvised weapons, character creation and the thrown-weapon dummy - each
needs its own situation (a cutscene with a fade, a disarm, a throw, the creation screen).

**Levels.** Eight levels were started cold, all load, including the three acts
([levels.md](levels.md)). The player is always Astarion alone with the story not advanced.

**The macOS reimplementation** (`tdimino/bg3se-macos`, MIT, cloned read-only into
`se-fork/reference/bg3se-macos`): its tables give ARM64 sizes for 1357 components, taken
from decompiled `AddComponent<T>`; 1120 are types the Windows game registers too and 1000 of
those have the same size (`compare_macos.py`; the 120 differences are mostly components
stored behind a pointer, which they measured as 8 bytes, and structures 8 bytes shorter on
ARM64). Its field layouts are parsed from Script Extender's Windows headers, so it adds no
field knowledge, and it has no rule for telling tags from one-byte data (its tag list is
Script Extender's). What it does point to: its notes count 726,413 symbols in the macOS
executable, local function names included. That executable is not in the repository; it is
inside Steam's macOS depot of the game (depot 1419660, one depot of 156 GB), so getting it
means downloading one file from that depot with the user's Steam login.

**Leads added** (not acted on): the game's string type inside components is 24 bytes;
`eoc::timeline::HideEquipmentComponent` mirrors the `HiddenDuringCinematic` boost
(confirmed, on the curiosities page); a door's closing animation is given up on after 10 s.

**Trouble met.** A dead party means a "game over" window and a *server* paused while the client
runs - nothing staged works until restart, so fights protect the party. Tutorial windows
pause the game too (`GK.Stage.Quiet`). `Osi.RequestDelete` does not delete characters;
they are spawned as temporary and taken off stage first. Starting a game while two others run
took up to 290 s instead of 43.

## The macOS executable: names for the same build (2026-11-12, night)

Fetched from the user's own Steam library with DepotDownloader (only the executable part of
depot 1419660: 502 MB, not the 156 GB of data; the user signed in themselves). It is the
**same game version as the Windows build measured here, 4.1.1.7631656**, and it keeps its
symbol table. It stays in `se-fork/reference/macos-game` and is never committed or shared.

Read with `research/coverage/macos_symbols.py` (parses the Mach-O file itself; names in
`se-fork/reference/macos-symbols/symbols.tsv` and, demangled, `symbols-demangled.tsv`):

| | Count |
| --- | ---: |
| symbols in all | 766,507 |
| C++ symbols | 433,751 |
| named C++ functions | 262,265 |
| of them in `ls` / `ecs` / `esv` / `eoc` / `ecl` / `gui` | 83,213 / 41,324 / 10,474 / 7,180 / 6,384 / 8,562 |
| methods of `*System` classes | 4,266, in 624 classes |
| registered systems with at least one named symbol | 933 of 934 |

What it gives and what it does not:

- **Method names of the systems**, with their signatures: an update method carries its
  `ecs::WorldView<...>` - the exact list of components that method reads and writes - e.g.
  `ecl::unsheath::VisualStateSystem::ProcessVisualRequests(ecs::WorldView<ecl::unsheath::RefreshWeaponVisualsRequestOneFrameComponent, ecl::unsheath::VisualStateComponent, ...>)`.
- **Member types** through template instantiations, e.g. a system's request map as
  `ls::HashMap<EntityHandle, ls::DynamicArray<ls::Variant<esv::unsheath::ScriptSystem::PushRequest, PopRequest>>>`:
  the kind of every container and the names of nested request structs.
- Every one of 400 unmapped components checked is mentioned in at least 21 symbols (median 58).
- **Field names, for replicated members** (corrected: an earlier version of this page said
  there were none). The symbols include `ls::FieldMeta<Type Class::*, &Class::m_Name>` for
  every member the game replicates: 705 members of 229 classes, 195 of them registered
  components, with the member's own name and C++ type (enums by name, e.g.
  `eoc::unsheath::EState`). The Windows build has the same `FieldMeta` with the member's
  *offset* instead of its name, for 118 of those classes; together they say "the member at
  this offset is called so". Members that are not replicated have no name anywhere.
- The addresses are ARM64 ones. To use the names on the Windows executable the functions must
  be paired across the two builds; that is handed to the symbol-base session (pairing by
  pseudo-code similarity, as it already does for the Toolkit).

### What the macOS names were used for the same night

Tools: `macos_symbols.py` (the symbol table), `macos_index.py` (system method → components it
writes and reads, from `ecs::WorldView<...>` signatures: 1287 methods; 607 of the 1117
unmapped component types have at least one named method, 411 a named writer),
`macos_defaults.py` (disassembly of `ecs::ComponentOps<T>::DefaultConstructComponents`, which
exists by name for all 2647 component types - the same number the Windows game registers).

- **Tags against one-byte data, settled offline** (`tag_evidence.py`). The default constructor
  writes into the new component exactly when the type has members. Of the 403 one-byte types
  declared as tags here: 203 are empty structs for certain, 27 hold a byte of data and are now
  declared as components with a field, 173 give no evidence (one-frame types are never
  default-constructed; their function is a three-instruction stub). The method agrees with the
  live game where the two can be compared (`eoc::PickingStateComponent`: 255 on the server,
  and its constructor stores a byte).
- **Real names applied**: `RoomPortal.ConnectedRooms` (was the inferred `Rooms`),
  `CrowdsBehaviour.State`, `TrapDisarmingAnimationState.AnimationRequest`. A new source label,
  "game", marks such names as facts. 45 more unmapped components have real member names; a
  worker is building their layouts.
- **Script Extender's own names held against the game's** (`fieldmeta_check.py`, 36 members
  where the type identifies the member): 6 the same word, 28 a different word (e.g.
  `eoc::inventory::ContainerComponent`: game `Slots`, extender `Items`), and two members the
  extender still calls `field_0` are `Tags` (`eoc::InteractionFilterComponent`) and
  `ZoneEntities` (`eoc::interrupt::ZoneParticipantComponent`). Listed in `work/fieldmeta-check.json`.
- **An offline check that turned out weak** (`defaults_check.py`): comparing layouts with the
  constructor's stores contradicts none of 115 layouts - but it also caught none of 52
  layouts spoiled on purpose, because constructors write in coarse blocks. It is kept as a
  coarse sanity test and promotes nothing.

### State at the end of 2026-11-12

Component types covered: 1543 of 2647 (58.3 %), collected on the v34 build of branch
`coverage-v34` at `f84682c8`; verified on live data: 161. In the layout files: 376 tags and
96 components with fields are in the build; 97 hypotheses and one system wait. The fields of
the confirmed layouts by where their name comes from: 63 the game's own, 171 inferred, 62
unknown (`field_<offset>`).

The last batch (`f84682c8`, 13 types) used a method worth making routine: for a replicated
component the Windows replication serializer (the handler registered for the type's
replication index; virtual table slot 2, or slot 1 when there is no writer) shows every
replicated member's offset and width, and the macOS serializer's type list gives their names
in the same order. A worker did 45 such components in 20 minutes (252,000 tokens, 5,600 per
type); 13 were alive to confirm. The registry marks 787 component types as replicated, 240
of them without a layout when this project began - the natural next batch.

Rule adopted the same evening (user, via the fork session): the game's own names and data
come first; Script Extender's earlier names stay only as `[[bg3::legacy]]` aliases. From this
project's comparison the fork session renamed 25 fields of the author's components.

Live facts gathered for the fork session about types the author's headers may have wrong
(`work/suspects.json`): `eoc::PickingStateComponent`, declared a tag, holds 255 / 2 / 0 on
312 server entities; `eoc::ActiveComponent`, declared with a field, is all zero on the server
and garbage on the client, as a tag is.
