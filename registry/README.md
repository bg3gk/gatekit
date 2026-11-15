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
| a thing whose owner is not known | `unplaced::<Name>` |

`::`, `.` and `@` separate only outside `<...>` and `(...)`; an operator's symbol is part of its
name (`ls::Vector::operator[]`). One id has one file, across the three kinds: a second entry for an
id that exists is refused with the file that holds it. Several ids may share one address (the
linker folds identical code); the id is the key, never the address. When a thing is renamed,
`gatekit registry rename` moves its file and rewrites every citation; the old id is left nowhere.
The names other sources give the same thing (Script Extender's, the Toolkit's, the decompiler's)
are kept in `known_as`, and `registry show` and `registry at` find an entry by them.

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

A piece follows its function's file name after `@`: `RollDice@thunk.json`. Two ids that differ only
by case are refused, since a file system without case would give them one file; a relative path
longer than 160 characters is refused.

## Rungs of certainty: what is applied and what is shown

Every name and type carries a rung of certainty, strongest first (not the reset rungs of a test,
[docs/writing-tests.md](../docs/writing-tests.md)). Only an applied rung may be given to the
decompiler as a real name or type (`gatekit decomp refresh`, `decomp serve`), or written into the
code `gatekit decomp code` prints; a shown one is written beside the code as a comment.

| rung | | what puts it there |
|---|---|---|
| `game` | applied | the game's own name: the macOS build's symbols, FieldMeta, a type name the engine keeps |
| `toolkit` | applied | the Toolkit's exported name |
| `confirmed` | applied | ours, with an instrument's evidence (a watch, a read) or a test |
| `inferred` | shown | reasoning from the code, or a pairing |
| `extender` | shown | Script Extender's author's name, unchecked |
| `guess` | shown | supposed: what it rests on and what would confirm it |
| `unknown` | shown | nothing backs it |

An entry on an applied rung carries evidence of a kind that can confirm: `watch`, `read`,
`game-name`, `toolkit-name` or `test`; a `pairing`, an `extender-name` or a `text` alone does not
put it above the line. `gatekit registry applied <binary>` lists exactly the applied set of a
build. A thing not understood is `state: looked_at`; a thing never looked at, `unlooked`.

## Citing code and tests

In running text - a sentence of evidence, a `does`, a document, a comment - code is cited as
`code:<id>` and a test as `test:<path under tests/ without the extension>[/<case>[/<step>]]`,
so that one plain search finds every mention. A field whose kind the schema fixes (an evidence
item's `concerns` or `test`, a tie between entries) holds the bare id or path. An address
`binary+0xRVA` appears only in the tables under `builds/` and in what an instrument prints.
`gatekit refs <id or path>` lists every place that cites one. `registry check` refuses a `code:`
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
