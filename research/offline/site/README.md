# site — data for a static search site

`export.py` turns the offline databases into plain JSON files that a static site can fetch: no
server, no database in the browser. The site itself is not written yet; this is its data layer,
and the format is fixed here so a front end can be built against it.

```
python export.py --out <output folder>
python export.py --out <output folder> --structure-only
python export.py --out <output folder> --limit 200 --kinds scene class
```

The output folder must be outside the repository.

## Output

| Path | Content |
|---|---|
| `manifest.json` | when, from which game and Toolkit builds, how many of each kind |
| `search/<kind>.json` | `[[name, id], ...]` sorted by name |
| `search/prefix/<ab>.json` | `[[kind, name, id], ...]` for every entry with a word starting with those two characters — fetch one file per keystroke pair for type-ahead search |
| `scene/<aa>/<name>.json` | a timeline: dialog, actors, track counts, gear changes |
| `class/<aa>/<name>.json` | an engine class: exports, fields, ECS registration, game addresses |
| `enum/<name>.json` | an enumeration with its values |
| `stat/<aa>/<name>.json` | a stats entry |
| `osiris.json` | every Osiris function with its parameters |

`<aa>` is the first two characters of the name, lower-cased, so no folder grows too large for a
static host. The objects are what `../kit/gatekit.py --json` prints for the same thing; their keys
are described in `../kit/README.md`.

## What may be published

The export is derived from the game's files. Larian's text, dialog lines, stats values and
anything read from the executables are theirs. `--structure-only` leaves out localized text,
dialog lines, pseudo-code, text constants and stats values, and keeps names, identifiers, types,
counts and addresses. Whether even that may go online is a decision to make before publishing,
not something this script settles.

## Not exported yet

Dialogs with their flow, root templates and placed objects, the generic object index, the file
catalog and pseudo-code. The first three are large (hundreds of thousands of entries) and need a
paging scheme decided together with the front end.
