# What the coverage number may claim

**Status: a recommendation, not yet decided.** It changes what GateKit tells people about itself,
so the owner settles it. Nothing here is built.

GateKit says it has mapped 2,507 of 2,647 component types. Read aloud that is "95 % of the
engine", and it is not. It means 95 % of component **types have a declaration** — three fields
named out of forty counts the same as forty out of forty — and `field_8`, `field_9` and `_Pad`
are everywhere in the layouts. The claim is true as written and misleading as heard, which is the
worst combination a number can have.

A second thing the number cannot say: **it only counts the ECS.** The ledger, the standing
capture, the probe and the layout check all reach instances through `GetComponentHolders`, so
everything the engine keeps outside the entity system is invisible to every one of them — and
invisible in a way the ledger cannot report, because the ledger counts component types and what
is missing is not a component type. The sound work put a size on the part we can already see:
28 engine globals in the fork's `Symbols.h`, four of which reach Lua at all, `ls::GlobalSwitches`
alone 0x15B8 bytes and mostly unnamed.

## The recommendation

**One. Build the globals register, apart from the component count.** The list is enumerable
offline from `symbols.sqlite`, exactly as `coverage.json` is built from the ECS registry. It is
small, additive and breaks nothing.

**Two. Do not blend the two.** 28 globals folded into 2,647 types would round away to nothing
while standing for some of the largest unmapped surfaces in the engine. A single averaged
percentage would hide precisely what the measure was built to reveal. Two numbers, with a
headline that names both.

**Three. The unit cannot be the same, and that is the honest part.** A component type is counted
by sightings: has anything ever carried it. A global is always there, once, from the moment the
process starts, so "seen" says nothing about it. What means something is **named bytes over total
bytes** — and that measure, applied back to components, would cut the headline figure sharply.

**Four, and this is the part with a deadline. Settle it before publication, not after.** Nothing
has been published yet, so today this is a choice about what to claim. After publication it
becomes a correction downwards of a number other people have quoted, which costs trust we will
not have to spend if we never spend it. The cheap moment is now.

What to publish instead of one percentage: the counts, plainly, and let the reader add them up.

- component types seen alive, of all registered;
- component types with a declaration, of those seen;
- fields named, of fields known, in the layouts we wrote;
- engine globals reached, of those the symbols list.

Four honest numbers read as work in progress, which is what GateKit is. One rounded percentage
reads as a finished claim, which it is not.

## What it needs that does not exist

A way to read memory **without naming a component**. `Ext.Debug.ReadMemory` already reads
anything; what is missing is saying *where* — an address and a size, the address being
`Ext.Debug.GetModuleInfo().Base` plus the global's RVA, found from the symbol database or by a
byte-pattern scan where the Toolkit does not hand it over. The probe and the watcher both take a
component name today, which is why the one engine field known to move reliably on this machine —
`MasterVolume` dropping to nothing when the window goes behind another — is the one field they
cannot watch.
