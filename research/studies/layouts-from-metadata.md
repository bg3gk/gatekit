# Layouts from the engine's own metadata

Whether the layout of a component could be derived from what the engine describes about itself,
instead of being read and confirmed field by field. Measured with
`gatekit symbols meta-layouts [--ns PREFIX] [--top N]`, which holds the layout derived from the
metadata alone against every layout of the registry confirmed in a running game (a type entry made
from a layout whose survey status is `confirmed`). No game is needed.

## Claim

The metadata the toolkit reads today gives a small part of a component's layout, and what it gives
is right. On the registry as it stands:

| | fields | share |
|---|---|---|
| fields of layouts confirmed in the game (padding apart) | 2386 in 771 types | |
| types the metadata places anything in | 22 types | 2.9 % of the types |
| offset given by the metadata | 92 | 3.9 % |
| name settled by the metadata alone (offset and one name) | 14 | 0.6 % |
| size given by both, and equal | 46 of 47 | 97.9 % |
| a field the metadata places where the layout starts none | 1 | |

Where both say something they agree, with two exceptions the command lists:
`ecl::sound::AutoFoleyParentComponent` at +0x8, a `bool` in the metadata and a `uint32_t` in the
layout (the layout reads three bytes of padding with it, or the metadata's type is narrower than the
storage; the game's code that writes it decides), and `ls::FogVolumeComponent` at +0x90, where the
metadata places `NoiseRotation` and the layout starts no field.

So the metadata is a check on a layout, not a source of one: it cannot derive the other 96 % of the
offsets, and 99 % of the names.

## What it stands on

- **What counts as metadata** is what `names suggest` and `names adopt` read
  (`SymbolText.FieldFacts`): the `ls::FieldMeta<Type Class::*, offset>` strings of `bg3_dx11.exe`
  (type and offset, for the fields the engine replicates), the macOS build's `fieldmeta.json` (names
  and types, no offsets), and the Toolkit's accessors (name and offset, the offset the game's when
  the accessor's function is matched to the game). A fact settles a field when it gives one name at
  an offset read in the game's build.
- **Why so little.** FieldMeta exists for the replicated members of a component only; the macOS
  names cover 19 of the 771 types (69 names, each already a field of its layout); the Toolkit's
  accessors describe editor-facing classes (`ecl::Item`, triggers, `ls::FogVolumeComponent`) more
  than the components mods read.
- **The agreement on names is not independent.** The 14 names the metadata settles were written into
  the registry from the same metadata (`names adopt`; their evidence is the FieldMeta string), so
  their agreement shows the reading is applied consistently, not that the metadata was checked
  against the game. The offsets and the sizes are independent: the layouts' offsets were read back
  in a running game.
- **Sizes** are compared only where both types are spelled as a primitive, a handle, a GUID, a
  dynamic array or a vector (`SizeOf` in the query); the 47 such fields are the whole comparison.

## What is not known

- Whether a sequential layout built from the macOS field order and the Windows ABI's sizes and
  alignments would place the fields FieldMeta does not: the macOS names are too few here (19 types)
  for the attempt to say much.
- Other metadata the engine keeps that the toolkit does not read yet: the save model's field lists
  (`savefields.json`, names without offsets), the property maps of the editor's reflection, the
  default constructors' writes (sizes and initial values). Each could raise the share; none is
  measured.
- Whether the two disagreements are errors of the layout or of the metadata.
