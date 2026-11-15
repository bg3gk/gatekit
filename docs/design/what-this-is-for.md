# What this is for

The README says what GateKit does; this says what the work is aimed at, which is a longer thing
and not a promise to anyone.

## The aim

Two halves of one job:

1. **Take the black box apart, without taking anything out of it.** What a decompiler hands back is
   labels a machine invented: `FUN_14077f580`, `DAT_181a170d0`, `field_8`. Replacing one of those
   with a name that says what the thing does **does not make the code the project's** - the engine
   is Larian's and stays Larian's. It makes it *legible*. What the project produces is a
   description of someone else's engine; the description is the project's, the thing described is
   not, and the difference is the whole of it. The end state is an account of how Baldur's Gate 3
   works in which nothing is called by a number.
2. **Help Norbyte's Script Extender reach the whole game.** The intercepting layer between C++ and
   Lua is what lets anyone else act on that knowledge. A field that is understood but that nothing
   can read from Lua is a private fact; a field declared in the extender is a tool in everyone's
   hands.

The first without the second is a notebook. The second without the first is a layer over things
nobody can name. They are the same work from two ends.

This is also why rule 3 of [AGENTS.md](../../AGENTS.md) - nothing of Larian's is ever committed -
is not in tension with any of it. What is published is **what was measured and what it was
called**, never the code read to find it out. A name is the project's to give; the function it
names is not the project's at all.

## Why it matters

Games stop being patched. Servers go away. What survives a game is what people can still read and
still change about it: the documentation of how it works, and a layer through which it can be
driven.

## How progress is measured

Two measures, and deliberately **no single percentage**: the engine has no known total to divide
by.

**For the first half: how much is still the machine's label.** Among the fields written down, how
many still carry a label the machine invented rather than a name that says something,
and where each name came from: the game's own member name or a Toolkit accessor (a fact), worked
out by reading the code (reasoning), Script Extender's name not yet checked against the game's
code (somebody else's reading), or nothing yet. It needs no denominator of everything that exists,
which is why it works where nothing can tell the true total. `gatekit names tally` computes it;
`gatekit names worst` says where a reader would pay off most.

**For the second half: how much of the engine the extender can reach** - component types seen
alive and verified against raw memory, of all the game registers; and, counted apart, the engine
globals the extender's headers name and how many of them reach Lua at all. The globals are not
component types, so nothing that counts component types sees them.

## What follows from it

- **An unresolved field stays unresolved.** A confident wrong name travels into other people's mods
  and into the extender, where it is worse than a missing one. `unknown` is a legitimate answer.
- **The game's own name wins.** Where the game has a name for a field - from the macOS build's
  `FieldMeta`, or a Toolkit accessor - that name is used, and anyone else's is kept as a
  cross-reference (`known_as`). A fact beats a good guess.
- **Confirm live before touching anything of the author's.** A suspicion found by reading code is
  not acted on until it has been checked against a running game (`bg3_probe_fields`).
- **Findings are meant to leave.** The fork exists to feed the extender, not to replace it. Work
  that holds up goes upstream with its evidence and a test that shows it, so that it can be checked
  rather than believed.
