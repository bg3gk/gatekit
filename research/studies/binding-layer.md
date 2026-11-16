# The binding layer written from the registry

The question: a Script Extender finds the game's functions by byte patterns written by hand in
its `BinaryMappings.xml`. How much of that table could the registry write by itself, so that a
function the registry names needs no pattern written by hand, and a patch of the game needs a
re-anchoring and a regeneration instead of a search by a person?

Measured on game build 25605617, both binaries (`bg3_dx11.exe`, `bg3.exe`), with
`gatekit registry mappings` (contract: [docs/contracts/cli.md](../../docs/contracts/cli.md)).

## What the fork's table is

The table of our fork holds 85 patterns that lead to 116 symbols. In each binary:

| | Symbols |
|---|---|
| land (the pattern is found exactly once) | 103 of 116 |
| of those, at the start of a function | 69 |
| of those, elsewhere: data, or a place inside a function | 34 |
| do not land for this tool | 13 - their patterns are found 2 to 8 times; the table picks one by a condition, which the tool does not read |

So two thirds of what the table leads to are plain functions, and the rest are globals and
places inside functions reached through an instruction that refers to them.

## What the registry can write

- Of the 69 functions the table leads to, **67 have a pattern of their own start that is found
  once** within 64 bytes: the registry could write their mappings itself. The table reaches most
  of them today through a pattern at a place of USE (a call site) and not at the function's
  start; a pattern of the start is the simpler mapping.
- Of every function the registry places in a binary, **230 of 249 get a mapping from the
  registry alone**: one run of whole instructions from the start that is found exactly once in
  BOTH binaries, a byte that differs between them left open. For the one function compared by
  eye (the chance of a spell's roll) the pattern written by the tool is byte for byte the one
  written by hand in the fork.
- The 19 with no mapping are short functions and thunks whose first 64 bytes are shared with
  others (the functions that build a check's text, two Lua overloads, three small rollers).
  They need what the hand-written table uses: a pattern at a place of use and an instruction
  that refers to the function. The tool does not write that form yet.

## What was taken into the registry

Before this, the registry named 17 of the 103 places the table lands on (13 of them under the
table's own name). `registry mappings adopt` noted the other **52 functions** under the table's
names at rung `extender`, each with its mapping as evidence and a question of what it does; for
26 of them the tool found the name in a store by itself (the Toolkit's exports, the macOS
symbols). The registry is now a superset of the table's functions, and `emit` can write them.

Four of those names differ from what a store holds (the digest lists them for a person); they
are left under the table's names, the store's name beside.

## What this says, and what it does not

- The pattern side of the binding layer is mechanical for nine functions in ten: a name in the
  registry is enough to hook the function in both binaries. After a patch the registry's
  fingerprints re-anchor the names and the mappings are written again; nobody searches bytes.
- Not measured: a real patch. The two binaries of one build stand in for two builds here; they
  differ in layout (the same function sits at another address, with other displacements) and
  not in source. How many patterns survive a change of the source is unknown until a patch
  comes.
- Not built: mappings for data and for places inside a function (34 of the table's symbols),
  the form that reaches a function through an instruction that refers to it, the table's
  conditions, and the other half of a binding - the declaration of the function's type on the
  extender's side, which is still written by hand.
- The check takes about 80 seconds: it scans both binaries for every pattern. It is run on a
  change of the table, not in a loop.
