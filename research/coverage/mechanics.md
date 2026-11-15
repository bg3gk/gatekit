# The map of mechanics: what a namespace is to a player, and what it needs

`mechanics.json` beside this file is the map; this file says what it is for, how an entry is
written, and what may and may not go into it.

## Why

The machine code and the game's data say how a thing works; neither says what it is for, who can
do it, or what has to be true first. A runner told to make a namespace's types appear needs that:
which level has a trader, which spell a player can cast at all. bg3.wiki says what players can do,
what a thing needs and in what order the game seems to resolve it. The map keeps that knowledge
**joined to the engine** and **apart from evidence**, so that what a namespace waits on can be read
in one place.

## The join

A wiki page carries no engine name. What ties one to the other runs through the game's data, and
each step belongs to a source that can be checked:

    a never-seen type
      -> the system that creates it            symbols, a systems file          (code)
      -> the functor, spell type, Osiris call
         or flag that feeds that system        pseudo-code                      (code)
      -> the stats entries that use it         gatekit data sql / stat          (data)
      -> which of those a player can do,
         as what, at what level, needing what  gatekit wiki card / table / rules (wiki)

The wiki's identifiers for actions, passives, reactions and items join the data index nearly
always; a condition joins by its stack id far more often than by a UID. The last arrow is the only
one the wiki supplies.

## An entry

`mechanics.json` is one object; a key is a short name of the mechanic in the players' words. Every
field says where it came from, because the fields are not worth the same:

| Field | What | Source |
|---|---|---|
| `namespaces` | the engine namespaces whose types this mechanic lights | symbols, the ledger |
| `systems` | the systems that create those types, where one is known | symbols, a systems file |
| `bridge` | what in the game's data feeds the system: a functor, a `SpellType`, an Osiris call, a flag | code and data; `null` where nobody has read it |
| `entries` | stats names, template names or GUIDs to stage it with | the data index - each checked with `gatekit data stat` or `find` |
| `wiki` | page titles, with the revision read where one was recorded | a citation - never the page's text |
| `needs` | what must be true first: level, class, party size, act and place, an NPC, a second player | the wiki, in our words |
| `reachable` | `yes`, `no` (cut, unobtainable, NPC-only, scripted), or `unknown` | the wiki |
| `agreement` | `agrees`, `differs`, `silent`, or `not compared` - the wiki against a described system | a comparison somebody made; say against what |
| `tried` | what runs did, in a line each, with the run file | the record |
| `open` | what is not known, plainly | - |

## What it is not

- **Not evidence.** Nothing here names a field, backs a layout or proves what a system does.
  `needs` and `reachable` are what players report; a runner checks them by going there. A wiki
  statement becomes knowledge the way everything else does: written as a claim with `expect`,
  replayed, and then it rests on the replay ([systems.md](systems.md)).
- **Not the wiki.** The wiki's text is CC BY-NC-SA 4.0 or CC BY-SA 4.0 and this repository is MIT.
  An entry cites a page by title and says what it says in our own words; it never copies a
  sentence, a table or a description. The rows of the wiki's tables stay in the reader's cache,
  outside the repository. Game identifiers are the game's, and every one here is looked up in
  GateKit's own index.
- **Not for the whole engine.** The wiki has nothing on behaviour scripts (`ls::anubis`), the mod
  pipeline (`ls::ugc`), navigation, rendering fades and previews, and close to nothing on the
  interface - the hotbar, the inventory, the main menu, tooltips, saving, cutscenes. For those the
  code and the game are all there is.

## Keeping it

- Whoever writes naming hints for a namespace adds or corrects its entry; whoever describes a
  system fills `systems`, `bridge` and `agreement`; a run writes `tried`, and `needs` when a
  set-aside taught something.
- A `differs` is a lead, not something to smooth over.
- A page changes. `rev` is the revision that was read; an entry whose pages have moved on is read
  again before it is trusted, and the wiki's own "verify" marks (`gatekit wiki rules` shows them)
  go into `open`.
- An identifier the index does not know is a stale or mistyped page, or a patch the index has not
  been rebuilt for: say which, and do not stage it.

The query that would generate `bridge` and `entries` for every stats-driven mechanic is an open
item of [../open-items.md](../open-items.md).
