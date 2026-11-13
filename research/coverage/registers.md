# Everything the game is made of, and how far we have got

One table of every kind of thing Baldur's Gate 3 contains, so that progress can be seen at once.

**There is no overall percentage here, and there should not be.** Two measures, chosen by which
kind of register it is:

- **Where the game tells us how many there are** - component types, systems, templates, dialogues -
  progress is plainly **N of M**. The bound is an orientation, not a score.
- **Where nothing tells us how many there are** - the engine's functions, its globals, anything
  reached by reading code - there is no honest denominator, so we measure something else: **how
  much of what we have written down is still the machine's name rather than ours**. `field_8`,
  `_Pad5`, `FUN_14077f580` are the machine's. That number needs no knowledge of the true total,
  which is exactly why it works here.

**Every number below was measured on 2026-11-13**, not estimated: the code side from
`symbols.sqlite` and `coverage.json`, the data side by counting rows in `gameindex.sqlite`
(`gatekit data sql`), the naming by `research/coverage/naming.py`. None of it needs a running game.

## The registers

### Code: the engine itself

| Register | How many | Where the number comes from | What "covered" means here | Where we are |
|---|---:|---|---|---|
| ECS component types | **2,647** | the game's own component registry | seen alive → fields declared → verified against raw memory → every field named | **2,101 seen**, 995 verified |
| ECS systems | **934** | the registry (`systems/survey.csv`) | what the system reads, writes and when it runs | 0 described |
| ECS queries | **4,841** | the registry | which components each selects | not started |
| Engine globals (`Type** ls__thing`) | **28** ⚠ | Script Extender's `Symbols.h` | reachable from Lua → bytes named | 4 reachable; `ls::GlobalSwitches` 0x15B8 bytes, mostly unnamed |
| Toolkit-exported classes | **8,099** | the Toolkit DLLs' exports | matched to a game address | 7,289 matched by calls |
| Game functions | **393,159** | `.pdata` of `bg3.exe` | named | ~10,400 matched |

### Data: what the game ships

| Register | How many | What "covered" could mean | State |
|---|---:|---|---|
| Templates | **25,560** | reachable and spawnable through the API | enumerated |
| Stats entries | **16,593** | fields understood | enumerated |
| — of them spells | 4,806 | | |
| — statuses | 4,692 | | |
| — passives | 1,879 | | |
| — characters | 1,598 | | |
| — armour / weapons | 963 / 561 | | |
| Dialogues | **9,386** | branches readable and walkable | enumerated |
| — dialogue nodes | 187,213 | | |
| Timelines | **10,055** | tracks understood | enumerated |
| — tracks | 3,217,182 | | |
| — track types | 34 | each type's effect known | the weapon-hiding ones done |
| Osiris functions | **1,319** | callable and understood | enumerated |
| Osiris types | 39 | | |
| Voice lines | **199,815** | | enumerated |
| Localized strings | **469,468** | | enumerated |
| Placed entities | **63,187** | | enumerated |
| Files in the packages | **1,041,954** | | enumerated |

⚠ = the denominator is **what we can name, not what exists** (see below).

## Where there is no bound: how much is still the machine's name

Measured by `research/coverage/naming.py` over the committed layout files. This is the number to
drive down, and it needs no denominator of everything that exists.

| | fields | |
|---|---:|---|
| written down in total | **3,783** | |
| carry a name of ours | 2,482 | 65.6 % |
| still the machine's | 1,301 | 34.4 % |
| — of which padding | 536 | not a name we failed to find: the absence of a field |
| **owed a name** | **765** | **20.2 %** - the real debt |

And where each name came from, which is the ladder that actually matters:

| Source | Fields | | What it is |
|---|---:|---|---|
| `game` | 371 | 9.8 % | the game's own member name, from the macOS build's FieldMeta - **a fact** |
| `toolkit` | 87 | 2.3 % | a Toolkit accessor's name - **a fact** |
| `inferred` | 2,396 | 63.3 % | worked out by reading the code - reasoning, and the bulk of the work |
| `unknown` | 929 | 24.6 % | we do not know, and the record says so instead of guessing |

**Only 12.1 % of our field names are established fact.** Nearly two thirds are inferred from
reading the code - defensible, and not the same thing. That distinction is already in every layout
file, and it is worth more than any percentage of components.

### Where the debt is worst

| Namespace | Machine names | |
|---|---:|---|
| `eoc::notification` | 93 of 140 | 66 % |
| `ecl::character_creation` | 48 of 94 | 51 % |
| `esv::ai` | 43 of 90 | 48 % |
| `esv::zone` | 33 of 44 | 75 % |
| `esv::patrol` | 23 of 28 | 82 % |
| `eoc::thrown` | 17 of 18 | 94 % |
| `ls::EditorCameraBehavior` | 19 of 19 | 100 % |

`python research/coverage/naming.py worst` lists the components where a reader would pay off most.

**Two of these point at the same place as the frontier.** `eoc::notification` is both the largest
never-seen namespace (22 types) and the worst-named (93 of 140 fields), and
`ecl::character_creation` is both untouched by any run and half machine names. So the two measures
agree on where to go next, which is a good sign that neither is an artefact.

## Two things that keep the table honest

**A register whose ladder is undefined says "enumerated", not 100 %.** The index already holds all
199,815 voice lines; printing them as complete would inflate the table with an achievement that
cost nothing. A component's ladder is real and has rungs we are climbing - *seen alive* → *fields
declared* → *verified against raw memory* → *every field named*. Most data registers have no such
ladder yet, and say so.

**Some denominators are "what we can name", not "what exists".** The 28 engine globals are the ones
Script Extender's `Symbols.h` names; the engine may hold more that nobody has found. Reaching 28 of
28 would mean *we covered everything we knew to look for* - a real achievement, and a different
claim from completeness. Those are marked ⚠ and counted apart. The code-side registers taken from
the game's own registry, and the function count from `.pdata`, are exact.

## The one that still needs deciding

A component counts as `mapped` whether three of its forty fields are named or all forty are. **So
"2,507 of 2,647 mapped" means 95 % of component types have a declaration, not that 95 % of the
engine is understood.** The naming table above is the honest version of that sentence, and it says
20 % of the fields we have written down are still owed a name.

Nothing here needs the `mapped` count to change. But if it is ever quoted outside this repository,
it should be quoted with the naming numbers beside it, or it will be read as something it is not.

## What is not counted yet

- **Levels and regions**, **surfaces**, **boosts**, **flags and tags**, **the UI's own widgets**:
  enumerable from the index or the catalogue, not yet counted.
- **Script Extender's Lua API surface** - how much of the engine it exposes at all - enumerable
  from the fork's headers.
- **The engine's functions**: 393,159 exist and about 10,400 are matched to a name. The machine-name
  measure should be extended to cover them and the pseudo-code, once a decompilation pass exists
  (`decomp.sqlite` is empty on this machine).
- Per-register ladders for the data side.
