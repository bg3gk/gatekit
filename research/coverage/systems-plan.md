# Covering the game's ECS systems: measured, and the plan

Baldur's Gate 3 (4.1.1.7631656) has 934 ECS systems (the same names on server and client). Script
Extender covers 85. A survey on 2026-11-13 measured what covering the rest takes; its table is
`research/coverage/systems/survey.csv` (one row per system: size and where it comes from, class,
factory / vtable / update addresses, request methods, evidence that the update drains them).

## What a system is (for a modder)

A component is data on an entity; a system is code the engine runs every frame, and its own
object holds queues, caches and references. Covering a system means declaring that object so a mod
can read it - and, where the system has a request queue, put a request in it: the game acts on it
next frame, the way it acts on the player's own requests.

## Measured

- **Size.** Systems have no registry size; the factory allocates the object
  (`FUN_1441beb10(heap, SIZE, 0x10, ...)`, bg3.exe+0x41BEB10). Found for 902 of 934 (847 directly,
  55 through a helper). Against the 66 mapped systems whose SE size can be computed: 60 match, 6
  differ (listed in `research/se-fork/TODO.md`).
- **How much is in them.** 190 are the bare base (vtable + world pointer, 0x10 bytes); 380 (42 %)
  are at most 0x20 bytes.

| class | what | count | mapped |
|---|---|---|---|
| a | near-empty: at most 0x20 bytes, no member constructors | 352 | 1 |
| b | moderate: at most 0x100 bytes, a few zero-initialised containers | 380 | 37 |
| c | large or complex | 170 | 45 |
| ? | no size found | 32 | 2 |

- **Queues a mod could use.** 250 systems have a request method whose member the update reads (158
  server, 57 client, 35 shared); 199 of them are not mapped. Evidence per system in the table.
- **Element types are the cost.** macOS symbols name container element types for about 9 % of
  systems; the rest are read from the drain function, about 10-20 minutes a system.

## Plan

1. **Class a, one generated batch (about 350):** the base plus at most two opaque qwords, size
   checked against the allocation. Little value per system, but it makes them visible and counted.
2. **The 20 most useful queues first** (server, with evidence; see the table): spell casts
   (`esv::spell_cast::CastRequestSystem`), statuses, summons, dropping and picking up items, stacks,
   inventories, move-to-target, end turn, template changes, shapeshift, respec, combining, short
   and long rest, cooldowns, temporary hostility, passives, illithid powers, approval. For the mapped
   ones: check that the element structs are complete enough to append to, and try one request live.
3. **Class b with drained queues (134)** in worker batches, element types from the drain code.
4. **Class c (170)** one by one, opaque blocks where they stay unknown.

## Cautions

- Writing to a system's queue is not reading: a wrong element layout corrupts the game's memory.
  Every request path is tried live on a stand copy before it is documented for modders.
- `symbols.py layout <System>` prints system fields 8 bytes low (it treats the base as 8 bytes, it
  is 16) - reported to the decompilation session.
