# Data a mod keeps in the save

What the Script Extender offers for it, what mods do, and what GateKit would add. Read from the
extender's source (the fork's base) and from mods on disk; nothing here was run yet.

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

## What mods do

- Mod Configuration Menu has a store with three backends; `modvar` keeps the value in the save
  through `Ext.Vars`. It queues reads and writes until `SessionLoaded`. No version, no migration.
- Wrappers of the same shape in Volition Cabinet and in Trials of Tav. Several large mods are
  still on `PersistentVars` (AI Allies, Combat Extender, Transmog Enhanced).

## What GateKit would add

A store over one mod variable per namespace: opened at bootstrap with a version, defaults and a
migration; `get`, `set`, `update` by path; every write assigns the variable again; a value is
checked before it is written (only what the extender can save), so nothing is lost in silence.
What it cannot give: a read before `SessionLoaded`, data of a removed mod past the next save,
number keys kept as numbers.

## The proof asked for: three books of Act 3

Not done. Found so far: the Tharchiate Codex is the template
`BOOK_Wizards_Tome_Cursed_TharchiateCypher_A`, with an effect and a status of its own; the
Annals of Karsus has two candidate templates not yet told apart; the Necromancy of Thay was not
found by name. Next: the data chain of each candidate, then the test - place at random spots,
write through the store, save, remove, load, place again from the store, check against a second
record kept another way.
