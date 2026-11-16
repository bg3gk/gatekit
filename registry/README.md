# The registry of the game's code

Our reading of the game's machine code: one file for each function, type and global we have
looked at, and one table for each build of where each of them is. It is the source of truth;
the symbol and decompiler databases, the decompiler's projects and every name laid over
pseudo-code are derived from it. It holds facts and our own sentences, never code of the game
([docs/design/what-we-publish.md](../docs/design/what-we-publish.md)). The commands are
`gatekit registry` ([docs/contracts/cli.md](../docs/contracts/cli.md), "registry").

```
registry/functions/<scope as folders>/<Name>.json     schema registry/function
registry/types/<scope as folders>/<Name>.json         schema registry/type
registry/globals/<scope as folders>/<Name>.json       schema registry/global
registry/builds/<build>/build.json                    the build and the SHA-256 of each binary
registry/builds/<build>/<binary>/<top scope>.jsonl    id, rva, size, body hash; sorted by id
```

## The id

The id is the name as the game writes it. Scope is separated by `::`; a member - a field of a
type, a parameter or local of a function - follows its entity after a dot:

| | |
|---|---|
| a function, a type | `esv::RollSystem::RollDice`, `eoc::HealthComponent` |
| a member | `eoc::HealthComponent.Hp`, `esv::RollSystem::RollDice.total` |
| an overload, only where two clash | `ecl::Transform::SetTranslate(Vector3f*)` |
| a template's instance | `ls::Array<int>::Add` |
| a piece the compiler made of a function | `esv::RollSystem::RollDice@thunk` |
| a function private to a file, a lambda | under the function or type that owns it |
| a thing with no name yet | `<owner scope>::unnamed_<token>`, the token from its fingerprint, never from its address |
| a thing whose owner is not known, or whose owner is known and whose name there is ours | `unplaced::<Name>`, the second with the owner's scope in its field `owner` |

`::`, `.` and `@` separate only outside `<...>` and `(...)`; an operator's symbol is part of its
name (`ls::Vector::operator[]`). One id has one file, across the three kinds: a second entry for an
id that exists is refused with the file that holds it. Several ids may share one address (the
linker folds identical code); the id is the key, never the address. When a thing is renamed,
`gatekit registry rename` moves its file and rewrites every citation, the live layer's claims and
the bare id in the data files that hold places (`registry places`); the old id is left nowhere.
`gatekit registry remove` takes an entry out, with its rows and its claims, and is refused while
anything cites it. The names other sources give the same thing (Script Extender's, the Toolkit's,
the decompiler's) are kept in `known_as`, and `registry show` and `registry at` find an entry by
them.

Whose a name is - the game's, the Toolkit's or ours - is never written by hand: it is read from
the entry's evidence (a `game-name`, a `toolkit-name`, neither), and `registry show` says it. An
id carries no mark of it, so a name a store turns out to hold later costs no renaming. An entry
under `unplaced::` whose owner is known says so in `owner` (`esv::CombatLogProtocol`);
`registry questions <scope>` lists it under that scope, and a rename into the owner's scope drops
the field.

## The file of an id

The id's scope parts become folders and its last part the file name, `.json` after it. Each part
is written by one reversible rule (`RegistryId.EscapePart`):

| in the id | in the file name |
|---|---|
| `::` inside template arguments | `..` |
| `<` `>` `*` space | `[` `]` `^` `+` |
| `% . [ ] ^ + / \ : ? " \| @`, a control character, anything outside ASCII | `%` and two upper-case hexadecimal digits for each byte of its UTF-8 |
| a name Windows reserves (`CON`, `NUL`, `COM1`, ...) | its first letter as `%XX` |
| everything else | itself |

A piece follows its function's file name after `@`: `RollDice@thunk.json`. A relative path longer
than 160 characters is refused.

Names in one folder that differ only by case - the namespace `esv::status` beside the class
`esv::Status` - would be one name on a disk that ignores case. Of such names the one that sorts
first by its characters' codes keeps its name, and each other has every letter whose case differs
from that one's written as `%XX`: `esv/Status/` and `esv/%73tatus/`. The rule depends on the set
of names, never on which was written first; a write or a removal that changes the set moves the
others to where the rule puts them, the live layer keeps its files the same way, and the check
holds every file to it (`CasePaths`).

## Rungs of certainty: what is applied and what is shown

Every name and type carries a rung of certainty, strongest first (not the reset rungs of a test,
[docs/writing-tests.md](../docs/writing-tests.md)). Only an applied rung may be given to the
decompiler as a real name or type (`gatekit decomp refresh`, `decomp serve`), or written into the
code `gatekit decomp code` prints; a shown one is written beside the code as a comment.

| rung | | what puts it there |
|---|---|---|
| `game` | applied | the game's own name: a `game-name` evidence (the macOS build's symbols, FieldMeta, a type name the engine keeps), tied to the thing |
| `toolkit` | applied | the Toolkit's exported name: a `toolkit-name` evidence, tied to the thing |
| `confirmed` | applied | a name of ours with a `read` and a `watch` together, or a `test` |
| `inferred` | shown | everything else: a reading alone, a watch alone, a counter of reach, a pairing, a store's name that nothing ties |
| `extender` | shown | Script Extender's author's name, unchecked |
| `guess` | shown | supposed: what it rests on and what would confirm it |
| `unknown` | shown | nothing backs it |

The rung is computed from the kinds of evidence an entry, a field or a value itself carries
(`Rungs.Ceiling`), by every writer and by the check alike. A store's name is tied to a function by
the store's own match or pair, or by an instrument's evidence of the function in the entry (a
read, a watch, a test); a type, a field or a value carries its name itself. `reach` - a counter
showed the code running in a situation - says that it ran, not what it does, and lifts nothing; a
`pairing`, an `extender-name` and a `text` lift nothing either. A rung asked above what the
evidence carries is lowered by `registry note`, `registry commit` and `registry tidy` with a line
that says why, never refused; a `confirmed` name whose evidence holds a store's tied name takes
the store's rung; a rung below is left as its writer put it. The check refuses an applied rung
with no evidence that can confirm at all, and notes one that stands above its evidence.
`gatekit registry applied <binary>` lists exactly the applied set of a build. A thing not
understood is `state: looked_at`; a thing never looked at, `unlooked`.

## What the tools do by themselves

A writer gives what it saw; the tools add what they can find out, each act a printed line and a
line of the journal `registry digest` reads:

- **Where a name comes from.** `registry note`, `registry commit` and `registry tidy` look the
  thing up in the stores - the Toolkit's exports and their matches to the executables, the macOS
  build's names and the stored pairs, the type names the executable keeps - by its name and by its
  address, and write the `toolkit-name` or `game-name` evidence with the store and the tie
  (`match`, `pair`, or `name` alone). An evidence of that kind written by hand is left to speak
  alone. A different name a store ties to the function is kept under `known_as` and proposed once.
- **One function under two ids.** At commit an `unnamed_*` id at the address of a named one, with
  one body, becomes the named entry: its evidence and its questions of reach are kept, its
  citations follow.
- **A sentence said twice** in one entry, or one of its members, is kept once.
- **A field's own evidence.** `registry note --field` takes `--field-rung` and `--field-evidence`;
  the type's evidence is not copied into its fields.
- **A correction.** `registry note --amend` puts a sentence in the place of the entry's newest of
  its kind. A commit merges a claim once: what is written into the entry afterwards stands.

## Citing code and tests

In running text - a sentence of evidence, a `does`, a document, a comment - code is cited as
`code:<id>` and a test as `test:<path under tests/ without the extension>[/<case>[/<step>]]`,
so that one plain search finds every mention. A field whose kind the schema fixes (an evidence
item's `concerns` or `test`, a tie between entries) holds the bare id or path.
`gatekit refs <id or path>` lists every place that cites one.

A machine address in running text: where the registry names the function that starts there, the
id is written (`code:<id>`), and a commit turns the address into it. A place with no id of its
own - a call site inside a function, a callee with no name, a global, a constant - may be written
as an address, in the one form that carries its binary and build,
`bg3_dx11.exe@25605617+0x1BC3C2B`: without them an address goes stale unseen at the next patch. A
commit and `registry tidy` write that form for an address that lacks it, from the tables of the
binary the entry is placed in; `registry at` reads it; `registry reanchor` to another build of the
binary moves it with its function when the body is equal and marks it stale otherwise.
`registry check` never refuses an address, and `registry digest` counts them: turned into ids,
written with binary and build, stale, not placed. `registry check` refuses a `code:`
citation that is not a registry id, names no entry, or names a member its entry does not have, and
a `test:` citation whose path names no test under `tests/`; the case and step after a test's path
are not checked.

## Fingerprints

What finds a function again after a patch: the facts that do not depend on the build - what it
calls (by id, or by the hash of the callee's own facts), the hashes of the text constants and
imports it refers to, the component types it touches, its slots in virtual tables - live in the
entry; its size and the hash of its masked body live in the build's table. Texts of the binary are
kept only as hashes. The body hash is a SHA-256 of the function's bytes with every byte a
relocation or a move would change masked, taken only for a body of at least 64 bytes and 16
instructions, where the instruction sequences are too many to try one by one.

## Another build or binary

`gatekit registry reanchor --source <binary> --target <binary>` moves a build table through the
pairs `gatekit decomp pair` stored between the two: a function with exactly one partner above the
pair's measured bar (a seed or an accepted pair) gets a row in the target's table, with the target's
size and body hash; a body that differs from the source's is said, not refused. A function with no
such pair, with more than one partner, or that the target's table already holds elsewhere is listed
for a person and left out.

## Merging

The tables are cut by the top level of the id's scope and kept sorted, one row a line, so that two
branches adding different entries merge. `gatekit registry mend` puts a table a merge left untidy
back in order and refuses two different addresses for one id.
