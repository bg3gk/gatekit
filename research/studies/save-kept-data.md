# Data a mod keeps in the save

What the Script Extender offers for it, what mods do, and what GateKit adds: `GK.Store`
(`mods/GateKit/.../Lua/Server/Store.lua`; [docs/contracts/lua-api.md](../../docs/contracts/lua-api.md),
section 20). Sources: the extender's source, mods on disk, and the tests named below.

## What the extender gives

- `Ext.Vars`: variables of a mod (`RegisterModVariable(modUuid, name, options)`,
  `GetModVariables(modUuid)`) and of an entity (`RegisterUserVariable(name, options)`,
  `entity.Vars.<name>`). Persistent by default, server side by default.
- A value is nil, a boolean, a number, a string or a table nested to 64 levels, with string or
  number keys. No size limit is stated or coded. A function or a userdata inside a table makes
  the whole variable saved as nothing, with only a line in the log.
- It is written into a region of the save of the extender's own (`ScriptExtenderSave`) as the
  game saves, and restored as the save loads, before `SessionLoaded`. A variable must be
  registered at bootstrap on every Lua reset or the saved value is not restored. The first safe
  read is `SessionLoaded`.
- A mod no longer loaded: its variables are read and then dropped at the next save. The older
  `PersistentVars` table is the opposite - it is saved again and survives - but it is deprecated.
- Writing a field inside a stored table does not mark it changed: it is neither saved nor sent.
  The variable must be assigned again or marked dirty.
- Number keys of a table that is not a plain array come back as strings.
- A number does not come back as it went in: the save keeps a fraction as a 32-bit float and a
  whole number through a double, so one third and 9007199254740993 both change
  (`test:store/kept/round_trip` holds the numbers that would).

## What mods do

- Mod Configuration Menu has a store with three backends; `modvar` keeps the value in the save
  through `Ext.Vars`. It queues reads and writes until `SessionLoaded`. No version, no migration.
- Wrappers of the same shape in Volition Cabinet and in Trials of Tav. Several large mods are
  still on `PersistentVars` (AI Allies, Combat Extender, Transmog Enhanced).

## What GateKit adds: `GK.Store`

A store is a named table kept in one mod variable (`GKStore` of GateKit's own mod; another mod's
store in a variable of that mod, opened at its bootstrap).

- `Open` declares a version, defaults and a migration; `Get` and `Set` go by path; `Names` lists
  the stores the save holds; `Drop` takes one out; `Check` says whether a value can be kept.
- Every write assigns the variable again, so a nested field changed is saved.
- A value is checked before it is written, and what the extender would lose in silence is refused
  with the path of the offending part: a function, a userdata, a number key in a table that is no
  plain list, a table nested deeper than 64 levels, a number that is not finite, and a table that
  holds itself (it has no end to write; the store refuses it before the extender is asked).
  `test:_selftest/store/refused` holds each refusal.
- A number is written as a text and read back, so it is kept to the last bit.
- What it cannot give: a read before `SessionLoaded`; the data of a removed mod past the next save.

## What is shown

- `test:store/kept/round_trip`: what a store holds when the game saves is what it holds after
  that save is loaded - a list, nested tables, fractions, a whole number past 2^53, texts in
  Cyrillic and Chinese letters - compared inside the game value by value; a store made after the
  save is not there after the load.
- `research/demos/three_books.lua` (the proof asked for): three books are placed at random spots
  near the host, each spot is written through the store, the books are taken out of the world,
  and the game is saved with no book in it; after the load the books are placed again from the
  store alone and each stands where a marker left in the world, kept by the game's own save,
  shows the first placing put it. The books: the Tharchiate Codex
  (`UN_BOOK_LOW_TharchiateCypher`; its parent `BOOK_Wizards_Tome_Cursed_TharchiateCypher_A` is a
  plain book), Caution Before the Seelie (`UNI_BOOK_LOW_SightOfTheSeelie`) and the Necromancy of
  Thay (`UNI_FOR_ThayanTome_Unsocketed`). The Annals of Karsus is an item placed in the city's
  level (`S_LOW_KarsusNotes`) whose root is a generic cursed tome: it cannot be had in the
  sandbox.

## Not shown

`GK.Store.Open` with `version`, `defaults` and `migrate`; a save made before the store existed
giving the defaults; `GK.Store.Drop`; another mod's store. Each has no test.
