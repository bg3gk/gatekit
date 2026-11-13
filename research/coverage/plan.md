# Taking Script Extender's coverage of the game to 100 %

The numbers are in [se-coverage.md](se-coverage.md) (rewritten by `research/coverage/collect.py`
on every collection); the full index is `research/coverage/coverage.json`. This page is the plan.

## What "covered" is measured against

The game registers its entity-component system at start: every component type and every system,
by name. Script Extender sees all of them, whether it has a layout or not, and the GateKit build
lists them (`Ext.Debug.GetComponentTypes()`, `GetSystemTypes()`). That list is the denominator.

Measured on game 4.1.1.7631656, Script Extender v33 (2026-11-12):

| | Covered | Total | % |
| --- | ---: | ---: | ---: |
| Component types | 1071 | 2647 | 40.5 |
| Systems | 85 | 934 | 9.1 |

Of the 1576 component types without a layout, 404 are tags (no data: a one-line declaration
each) and 1172 carry data — 67,089 bytes of structure in all; 970 of them are 64 bytes or
smaller, 36 are larger than 256 bytes.

Not in the index yet, to be added as further categories: static data and resource types
(`Ext.StaticData`, `Ext.Resource`), stats, the UI's view models, engine functions Lua can call,
and — inside covered components — which fields are verified rather than merely declared.
"100 %" of components and systems is therefore a first milestone, not the whole game.

## The index

`coverage.json` holds one entry per component and system per side: name, index, sizes, whether
Script Extender maps it, and `verified` — empty until a test has confirmed the layout in the
running game; a later collection keeps it. Rules for keeping it honest:

- an entry becomes "mapped" only by being in the build that was measured, never by hand;
- "verified" is set by a scenario that read real values through the layout and found them
  right (and, for writable fields, that a write took effect), with the date and the scenario;
- names that come from the Toolkit's exported accessors are facts; names inferred from how a
  field is used are marked as inferred in the comment of that field.

## The loop for one type

1. Hypothesis from the decompilation (`kit\gatekit.py`, `symbols\`): fields with offsets, types
   and — where the Toolkit exports an accessor — real names.
2. The hypothesis as data: `{ name, offset, type }` rows, no C++ yet.
3. Hot check in the running game: `Ext.Debug.GetComponentAddress(entity, name)`, then
   `Ext.Debug.ReadMemory`, on many live entities. Values must be plausible, agree with what
   covered neighbours say, and change when the game changes them. No rebuild, well under a
   second per type, several game instances in parallel (`gatekit.py --instance`).
4. Confirmed layouts are written into Script Extender's headers in batches, built, and the
   scenario that confirmed them is recorded in `verified`.

Reading is safe. Writing through an unconfirmed layout is not; hot checks read only.
`Ext.Debug.WriteMemory` exists for the cases that need it (confirming that a field is the one
the game reads back), on a throw-away game.

## Order

1. The 404 tags: mechanical, no reverse engineering, about +15 points of component coverage.
2. Components the user's mods need first (timelines, weapon and equipment visibility).
3. Small data components (2–64 bytes, 970 types), grouped by namespace so related ones are
   checked on the same entities.
4. The large ones and the systems. Systems are objects with state and methods rather than
   plain data; each needs a decision on what to expose.

The size of this is a project of its own (thousands of types, each with a check in the game),
to be run as its own session with many workers; it should not be started as a side effect.

## Clean inputs

Checked 2026-11-12:

- `bin\bg3.exe` and `bin\bg3_dx11.exe` carry a valid Larian Studios signature, so the code that
  is decompiled and measured is unmodified.
- No native mod loader: no plugin folders in `bin`, and the unsigned DLLs there are the game's
  own libraries (`Osiris.dll`, `SDL2.dll`, `bink2w64.dll` by RAD, ...) and Script Extender's
  loader `DWrite.dll`.
- Loose files under `Data\` (`Editor`, `Mods\Gustav*`, `Mods\Shared*`, `Public`, `Generated`,
  `Projects`, dated 2026-11-07) are what the official Toolkit installs, plus the GateKit's test
  project. They are data, not code, and do not change which types the engine registers.
- The measuring game ran in a stand instance with only the base game and the GateKit bridge in
  its mod list.
- To tighten: that instance's `Mods` folder still holds copies of the user's mod paks. They are
  not in the mod list, but a pak that overrides files without being listed would still load.
  An instance used for this work should start with an empty `Mods` folder (bridge only).

## After the pilot (2026-11-12): the plan for the full run

The pilot's measurements are in [se-coverage.md](se-coverage.md), "Pilot". What they change:

- Names will be inferred, not taken from the Toolkit (accessors exist for 1.4 % of what is
  missing). The rule stays: every field's comment says where the name comes from.
- The hypothesis step is offline and parallel (no game needed): about 28,000 tokens and
  2 minutes of wall clock per small component with three workers side by side.
- The check step is cheap per type but needs the component to be alive; 60 % of the data
  components are not alive in an idle level. Driving the game into the right situations is the
  bottleneck, and it is what game instances are for.
- A batch has about 6 minutes of fixed overhead (build, restart, check), so batches are large.

### Stages

| # | Stage | Types | How | Workers / instances | Rough cost |
| --- | --- | ---: | --- | --- | --- |
| 0 | Tools before scale | — | `decomp.py code` accepts any address inside a function; "callers of the users" and a virtual table by address in `symbols.py`; resolving global FixedStrings from the running game; a capture that samples every tick so one-frame and short-lived components are caught; a library of situations (fight, dialogue, trade, long rest, character creation, level change, second player); a build without whole-program optimisation | 1 session, 1 instance; requests to the symbol-base and fork sessions | half a day |
| 1 | Tags | 399 | generated from the registry (name rule + collision check), one batch; verified as registered-as-tag and mapped, presence on live holders where there are any | 1 worker, 1 instance | an hour; +15 points of component coverage |
| 2 | What the user's mods need: timelines, weapon and equipment visibility, unsheathing, dummies | 132 (31 of them tags) | hypotheses by namespace; checks inside dialogues and cutscenes (the stand's dialogue scenarios), with weapons drawn and sheathed | 3 workers, 2 instances | about 4 million tokens; a day |
| 3 | Small components, 2–64 bytes | 956 | by namespace, 6–8 per worker task, 50–100 per batch; first what is alive at rest (about 430), then by situation | 6 workers offline, 2 instances (one idle level for checks, one driven through situations) | about 30 million tokens; 2–3 days |
| 4 | Large components, over 64 bytes | 202 | one or two per worker task; nested structures become shared types | 4 workers, 2 instances | about 15 million tokens; 2 days |
| 5 | Systems | 849 | size from the factory, members from constructor / load / unload; checked with `Ext.Debug.GetSystemAddress`; per system a decision what is worth exposing (queues of requests first) | 6 workers, 1 instance | about 35 million tokens; 3–4 days; the least certain estimate (one system measured) |
| 6 | New categories of the index | — | static data and resource types, stats, UI view models, callable engine functions, and field-level `verified` for the 1071 types Script Extender already had (`check.py` raw stage runs on them unchanged) | to be planned after stage 3 | — |

Expected yield, from the pilot: about four of five components confirmed on the first pass
(14 of 17); the rest wait for a situation or stay documented hypotheses. "100 %" will
therefore be reached in two senses at different times: every type *declared with a checked
size and types* (stages 1–5), and every type *verified on live data* (needs the situation
library to reach the rare ones, e.g. multiplayer-only, photo mode, UGC download results).

### Limits

- Memory: 32 GB in the machine, about 8 GB per game. Two instances for this work, a third only
  while no other session runs a game. Offline workers need no game; their limit is tokens.
- One check process per instance (the bridge has one command channel per side).
- Total for stages 1–5: roughly 85–100 million tokens and one to two weeks of wall clock,
  dominated by stages 3 and 5.
- Every batch stays its own commit on `gatekit` with the same description shape as `a9cfddf1`;
  nothing is pushed.

### Decision needed from the user before starting

Which run to start: (a) stages 0–2 only (tools, all tags, the mods' area: 3 workers, 2
instances, about 5 million tokens) and then report; or (b) the whole of stages 0–5 at the
scale above.

## State after stages 0–2 (2026-11-12)

Numbers and findings: [se-coverage.md](se-coverage.md), "Stages 0–2".

| Stage | Done | Left |
| --- | --- | --- |
| 0 tools | tick capture, situations (weapons, equipment, potion, dialogue), holders / counts / system address in `Ext.Debug`, the symbol-base commands workers asked for | situations for fights, rests, character creation, shape-shifting, thrown weapons, a second player; a build without whole-program optimisation (every batch still costs 3.5–4.5 minutes of build); resolving global FixedStrings |
| 1 one-byte types | all 398 declared, 76 seen alive | the rest become verified as situations reach them; one-byte types with data are found by a non-zero byte on the server |
| 2 timelines, weapons, equipment | 31 tags; 53 hypotheses of 104 data types, 26 of them confirmed and built | 27 hypotheses waiting (13 for a situation, 9 seen only empty, 4 for nested types, 1 without meanings); 51 data types without a hypothesis yet (mostly one-frame timeline events, notifications, character creation, thrown and improvised weapons) |

What the measurements change for stages 3–5:

- Tasks follow systems, not sizes: a family around one system cost 10,000 tokens per type,
  unrelated types of the same size 20,000–37,000. Stage 3 should be cut by namespace/system.
- The count of "tags" in the plan was an upper bound; the real split between tags and one-byte
  data is known only for types seen on the server.
- The share confirmed on the first pass was one half (26 of 53), not four fifths as in the
  pilot: the pilot's types were picked among those alive at rest. Coverage by declaration and
  coverage verified on live data will keep diverging until the situation library grows, so
  that library is the next thing to build.
