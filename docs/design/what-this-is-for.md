# What this is for

Not in the README on purpose. The README says what GateKit does today; this says what the work is
aimed at, which is a longer thing and not a promise to anyone.

## The aim

Two halves of one job:

1. **Take the black box apart, without taking anything out of it.** What a decompiler hands back is
   labels a machine invented: `FUN_14077f580`, `DAT_181a170d0`, `field_8`. Replacing one of those
   with a name that says what the thing does **does not make the code ours** - the engine is
   Larian's and stays Larian's. It makes it *legible*. What we produce is a description of someone
   else's engine; the description is ours, the thing described is not, and the difference is the
   whole of it. The end state is an account of how Baldur's Gate 3 works in which nothing is called
   by a number.
2. **Help Norbyte's Script Extender reach the whole game.** The intercepting layer between C++ and
   Lua is what lets anyone else act on that knowledge. A field we understand but nothing can read
   from Lua is a private fact; a field declared in the extender is a tool in everyone's hands.

The first without the second is a notebook. The second without the first is a layer over things
nobody can name. They are the same work from two ends.

This is also why rule 3 of [AGENTS.md](../../AGENTS.md) - nothing of Larian's is ever committed -
is not in tension with any of it. We publish **what we measured and what we called it**, never the
code we read to find out. A name is ours to give. The function it names is not ours at all.

## Why it matters past this year

Games stop being patched. Servers go away. What survives a game is what people can still read and
still change about it: the documentation of how it works, and a layer through which it can be
driven. That is the whole argument, and it does not need dressing up.

## How we know we are getting there

Two measures, and deliberately **no single percentage** - see
[`research/coverage/registers.md`](../../research/coverage/registers.md).

**For the first half: how much is still the machine's label.** Among the fields we have written
down, how many still carry a label the machine invented rather than a name that says something. It needs no denominator of everything that
exists, which is why it works where nothing can tell us the true total.

As of 2026-11-13: **3,783 fields written down, 765 still owed a name (20.2 %)** once padding is set
aside. And the part that matters more than the total - where each name came from:

| | | |
|---|---:|---|
| the game's own member name, or a Toolkit accessor | 458 | **12.1 %, established fact** |
| worked out by reading the code | 2,396 | 63.3 %, reasoning |
| we do not know | 929 | 24.6 %, and the record says so |

`python research/coverage/naming.py` recomputes it; `naming.py worst` says where a reader would pay
off most.

**For the second half: how much of the engine the extender can reach.** 2,647 component types
exist; 2,101 have been seen alive and 995 verified against raw memory. Of the 28 engine globals in
the extender's own headers, **four** reach Lua by a getter at all - and `ls::GlobalSwitches` alone
is 0x15B8 bytes, mostly unnamed. That second number is the one nothing in the ledger was counting
until now, because the ledger counts component types and those are not component types.

## What follows from it

- **An unresolved field stays unresolved.** A confident wrong name travels into other people's mods
  and into the extender, where it is worse than a missing one. `unknown` is a legitimate answer and
  is 24.6 % of what we have.
- **The game's own name wins.** Where the game has a name for a field - from the macOS build's
  `FieldMeta`, or a Toolkit accessor - that name is used, and anyone else's becomes an alias. A
  fact beats a good guess.
- **Confirm live before touching anything of the author's.** A suspicion found by reading code is
  not acted on until it has been checked against a running game (`bg3_probe_fields`). This has
  been the rule since before it was written down.
- **Findings are meant to leave.** The fork exists to feed the extender, not to replace it. Work
  that holds up goes upstream with its evidence and a recipe that replays, so that it can be
  checked rather than believed.
