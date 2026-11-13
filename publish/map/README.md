# map

Everything GateKit knows about Baldur's Gate 3, in one place: the game's own content, the
engine's symbols, and our map of its components. Prebuilt, so nobody spends an afternoon
building what someone else already built.

**Taking this down.** Open an issue and say so, and the releases are deleted and this
repository goes private the same day. If that reaches nobody, GitHub's own
[report form](https://support.github.com/contact/report-content) does it without us.

## What this is

Three maps of the same game, built by [GateKit](https://github.com/bg3gk/gatekit) and published
here as release assets because they are too large for git and because every modder who wants one
would otherwise spend the same minutes building the same file:

| Database | What it holds |
|---|---|
| `index` | the game's own content, read out of the `.pak` files: dialogue lines, stats, root templates, timelines, localized strings, and where each of them lives |
| `symbols` | facts about the engine's binaries: exported names, addresses, sizes, structure layouts, the call graph and string literals |

## What this is not

- **It contains no code of the game.** No executable, no library, no shader, no script of Larian's.
- **It contains no decompiled output.** GateKit can produce pseudo-code from the game's
  executables; that stays on the machine that produced it and is never published. The `symbols`
  database holds names, addresses, sizes, layouts, call edges and string literals — facts *about*
  the code, not the code.
- **It contains no art, audio, video, fonts or textures.** The index records that a resource
  exists and what it is called; it does not carry the resource.
- **It is not a way to play the game**, and it is useless without a legally owned copy of
  Baldur's Gate 3.

## Where it came from

Baldur's Gate 3 is made by [Larian Studios](https://larian.com/) and all of the content in the
`index` database is theirs. GateKit read the files of an ordinary, legally owned installation and
rearranged what it found so that it can be queried: nothing is added, nothing is interpreted,
nothing is improved. Each release says which build of the game and of the Toolkit it was read
from, in its manifest and on its release page.

The database schemas, the tools and the manifest format belong to the GateKit project and are
under its licence; the game's content inside the `index` database belongs to Larian Studios and
is published here only under the promise at the top of this page.

## How to use it

```bash
gatekit data install        # the index, into BG3_INDEX_DB or <local>/index/gameindex.sqlite
gatekit symbols install     # the symbol database
gatekit data query search "Astarion"
```

`install` reads the release pinned in GateKit's own `index-release.json`, fetches every part,
checks each one's sha256, joins them, unpacks them and checks the sha256 of the finished database
before it puts it anywhere. `--force` replaces a database that is already there; `--from` reads
the assets from a folder or another URL instead.

**Building it yourself stays a first-class path and needs no download at all:** `gatekit data
build` reads your own installation and takes a few minutes. Nothing in GateKit assumes the data
was fetched. `gatekit doctor` says which database is present and whether it was built here or
installed.

## The assets of a release

| File | What it is |
|---|---|
| `manifest.json` | what each database is, what it weighs, its checksums and what it was built from |
| `index.sqlite.zst` | the index, compressed with Zstandard |
| `symbols.sqlite.zst` | the symbol database, compressed with Zstandard |

A database larger than one release asset (GitHub refuses a file over 2 GB) is cut into numbered
parts — `index.sqlite.zst.001`, `index.sqlite.zst.002`, … — which are one Zstandard stream
written across several files. Joining the parts in order and decompressing them gives the
database back; `gatekit data install` does exactly that.

## The manifest

`manifest.json` is the only thing a reader needs to verify a release by hand:

```json
{
  "manifestVersion": 1,
  "databases": [
    {
      "database": "index",
      "fileName": "gameindex.sqlite",
      "size": 3472297984,
      "sha256": "<of the unpacked database>",
      "compression": "zstd",
      "compressedSize": 889000000,
      "parts": [ { "name": "index.sqlite.zst", "size": 889000000, "sha256": "<of this file>" } ],
      "source": "Baldur's Gate 3 Patch8_HotFix10, localizations: English, read 2026-11-12 14:48:35",
      "schemaVersion": "0f3a…",
      "created": "2026-11-13T09:00:00Z",
      "meta": { "built": "…", "packages": "…", "languages": "…" }
    }
  ]
}
```

- `sha256` on the entry is the finished database; `sha256` on a part is that file as it is served.
- `source` is the build of the game or the Toolkit the database was read from, in the words the
  database itself kept.
- `schemaVersion` is a digest of the database's own schema. Two databases with the same one answer
  the same queries, whichever release they came from.
- `meta` is what the database's own `meta` table says, with anything naming a particular machine
  left out.

Verifying by hand, with the standard tools:

```bash
sha256sum index.sqlite.zst                 # must match the part in manifest.json
cat index.sqlite.zst.* | zstd -d -o gameindex.sqlite
sha256sum gameindex.sqlite                 # must match "sha256" of the entry
```

## The schema

Ask the database itself — it carries its own documentation:

```bash
gatekit data schema                 # tables and columns of the index, with the meaning of each
gatekit symbols stats               # what the symbol database holds
sqlite3 gameindex.sqlite .schema    # or read it directly
```

Every table's columns carry `-- comments` in the schema text, and the `meta` table says when and
from what the file was built. The schema is GateKit's, and it changes with GateKit: a release is
usable by any build whose reader understands its `schemaVersion`.

## Nothing is committed here

This repository holds this page, GateKit's licence notice and nothing else. Every database travels as a release
asset, never in the tree, because an asset can be deleted in one click — which is the point. The
`.gitignore` refuses databases and compressed assets outright, so one cannot be committed by
accident.
