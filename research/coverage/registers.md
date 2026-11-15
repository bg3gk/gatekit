# The registers, and how each is counted

Every kind of thing the game is made of is a register, and how far the work has got is counted
per register - never as one overall percentage. Two measures, by the kind of register:

- **Where the game says how many there are** (component types, systems, templates, dialogues),
  progress is N of M. The bound is an orientation, not a score.
- **Where nothing says how many there are** (the engine's functions and globals, anything reached
  by reading code), the measure is how much of what has been written down still carries the
  machine's name (`field_8`, `FUN_14077f580`) rather than one that means something. It needs no
  true total, which is why it works there.

A number belongs with the command that reproduces it; none is kept in this file.

## The record: `gatekit record tally`

The counts of the coverage record, read from the committed files alone (no game, no database).
`--write` appends them to `record/counts.jsonl` with the run they were taken for and the name of
their definition (`definition`), which changes whenever what a count means changes.

| Key | What it counts |
|---|---|
| `types` | the engine's component types, from `coverage.json` |
| `seen`, `never_seen` | types seen alive at least once (`seen/index.json`), and the rest |
| `unseen_mapped` | types Script Extender declares that were never seen alive: the frontier |
| `verified` | types whose layout was checked against the raw memory of live instances |
| `fields` | fields written down in the layouts in the build |
| `fields_named` | fields whose name has evidence behind it: rung `inferred`, `toolkit` or `game` |
| `fields_extender_unchecked` | Script Extender's names that nobody has checked against the game's code (rung `extender`) |
| `fields_named_unknown` | names at rung `unknown` |
| `fields_named_by_rung` | the names on each rung of the ladder |
| `owed` | machine names that are not padding: the fields that still want a reader |
| `systems_described`, `systems_sure` | system descriptions with a non-empty `does`, and those whose `sure` says yes |
| `claims_written`, `claims_checked`, `claims_passed` | the claims of the descriptions, and their verdicts ([systems.md](systems.md)) |

The ladder of rungs: `game` (the game's own member name) and `toolkit` (a Toolkit accessor) are
facts; `inferred` is reasoning from the game's code and carries its evidence; `extender` is Script
Extender's name at that offset; `unknown` says so. Padding (`_Pad5`) is counted apart from the
machine names: it is the absence of a field, not a name nobody found.

Breakdowns of the same measure: `gatekit names tally` (by rung, by namespace),
`gatekit names worst` (where a reader would pay off most), `gatekit systems tally`,
`gatekit leads tally` (the types never seen alive, by class and reachability).

## The game's data and code

- The data the game ships - templates, stats entries, dialogues, timelines, Osiris functions,
  voice lines, localized strings, placed entities, package files: `gatekit data info`, or a count
  with `gatekit data sql`.
- The engine's code - Toolkit-exported classes, game functions, their matches, layouts and ECS
  types: `gatekit symbols stats`; how much of the pseudo-code reads like code:
  `gatekit decomp tally`.

A data register has no ladder of its own yet, so it is counted as enumerated, never as complete.
Some denominators are what can be named rather than what exists - the engine globals are those
Script Extender's `Symbols.h` names - and are said to be so wherever they are quoted.

The `mapped` count of `bg3_coverage_todo` counts a type whose declaration names three fields of forty the same as one that names
all forty. Quoted outside this repository, it goes with the naming counts beside it.
