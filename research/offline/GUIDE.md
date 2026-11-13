# Guide to the offline databases — for people and for language models

Three SQLite files describe Baldur's Gate 3 without the game running:

| Database | Built by | Holds |
|---|---|---|
| `index` | `index/gameindex.py build` | the game's data: every file, timelines, dialogs, templates, placed objects, stats, Osiris, text |
| `symbols` | `symbols/symbols.py build` | the game's code: names, virtual tables, struct layouts, ECS types, what was found in `bg3.exe` |
| `decomp` | `symbols/decomp/decomp.py run`, `load` | pseudo-code of functions, searchable |

`kit/SCHEMA.md` lists every table and column. `kit/gatekit.py` is one entry point over all three;
`kit/mcp_server.py` offers the same to a model over MCP. Add `--json` to any `gatekit` command for
machine-readable output.

## Start here

| You have | Ask |
|---|---|
| a word, no idea what it is | `gatekit search <word>` |
| a uuid | `gatekit about <uuid>` — the object, its attributes, everything that refers to it |
| a text handle (`h1234...`) | `gatekit about <handle>` |
| a class name (`ecl::TLPreviewDummy`) | `gatekit about <class>` — exports, fields, ECS registration, game addresses |
| an address (`bg3.exe+0x1899090`) | `gatekit about <address>` — function, names, callers, callees, pseudo-code |
| a scene name | `gatekit scene <name>` |
| a track type (`TLShowWeapon`) | `gatekit track <name>` |
| a function you want to hook | `gatekit hook <name>` then `symbols.py signature <address>` |
| anything else | `gatekit sql index|symbols|decomp "SELECT ..."` |

## Recipes

**Which scenes hide an actor's weapons?**
`gameindex.py hidden-weapons` (all 618), `--actor S_Player_Astarion`, `--name CAMP_`.
SQL: `SELECT timeline, actor_uuid, start, end FROM v_tracks WHERE type = 'TLShowWeapon' AND hides > 0`.

**What does a scene do, step by step?**
`gatekit scene NAME` for actors and gear changes; `gameindex.py dialog NAME --tree` for the flow of
the dialog with its text, flag checks and flag changes; `timeline_phases.dialog_node` ties a phase
of the timeline to a node of the dialog.

**Where is this template / character / flag used?**
`gameindex.py refs <uuid>` — objects, dialog speakers, timeline keys, stats and story goals that
mention it. `gameindex.py template NAME --gear` — scenes it takes part in.

**From an item to its model and textures.**
`gameindex.py chain WPN_Longsword` — stat, root template and its parents, visual resource, `.gr2`
file, materials.

**Which story code reacts to an event?**
`gameindex.py osiris TemplateEquipped --uses` — goals that use it, by role (trigger, condition,
action). `gameindex.py grep "DB_Dialogs"` searches the text of goals, stats and scripts.

**What are the fields of a component, and at which offsets?**
`symbols.py fields ecl::TLPreviewDummy` — fields read from the Toolkit's accessors (with bit
numbers for flags) and Script Extender's layout below them. `symbols.py layout NAME` for the
layout alone, `symbols.py enum NAME` for an enumeration.

**Which system reads or writes a component?**
`symbols.py system ecl::EquipmentVisualsSystem` — components by access, methods, virtual table.
`symbols.py types "ecl::TLPreviewDummy" --kind query` — ECS queries of the game that touch a type.
`symbols.py users ecl::TLPreviewDummy` — functions of the game that use the type's index.

**Where is a function in the game?**
`symbols.py game <name>` — found functions with how they were found. `gatekit hook <name>` adds
size, callers and virtual table slots. `symbols.py signature bg3.exe+0x...` prints a byte pattern
that stays valid across patches. `symbols.py export --format x64dbg|ida|ghidra` writes all known
names for a debugger.

**What does a function do?**
`decomp.py code <name or address>`; `decomp.py grep <text>`.

## Things that go wrong if you do not know them

- **Two kinds of address.** `EoCClient.dll+0x...` and other Toolkit DLL addresses are addresses in
  the editor. Only `bg3.exe+0x...` and `bg3_dx11.exe+0x...` are addresses in the game. They are
  different builds; an offset from one means nothing in the other.
- **`shadowed = 1`.** When two mods or packages provide the same timeline or dialog, the earlier
  one is marked shadowed. Filter with `shadowed = 0` (the views already do).
- **On/off tracks spell out only `False`.** A `TLShowWeapon` key without the `ShowWeapon`
  attribute means "shown". Use `tracks.hides` / `tracks.shows`, or `track_keys.value`.
- **A timeline actor is not a character.** It is a slot. Who fills it comes from the dialog's
  speaker list (`v_actor_objects`), and that can be a group such as `GROUP_Players`.
- **A match is a claim with evidence, not a fact.** `matches.method` says how a game function was
  identified and `relation` compares sizes. `method = 'vtable'` is the function in the matching
  slot of the class's virtual table. For the other methods, `relation = 'inlined'` means the game
  function is much larger and is probably a caller the wanted code was inlined into.
- **One game address, several names.** The linker folds identical functions together, so a
  getter such as "return the field at +0x38" has one address and many names. All are listed.
- **Not finding a function in the game does not mean it is not there.** The game is linked with
  heavy inlining; many functions the Toolkit exports have no body of their own in the game, and
  many others are simply not identified yet.
- **Offsets come from three places.** `accessors.offset` is read from Toolkit code (editor build),
  `accessors.game_offset` from game code when the accessor was found there, `se_fields.offset` is
  computed from Script Extender's struct definitions. `se_fields.checked = 1` marks an offset
  confirmed by a field Script Extender names after its offset. When they agree, trust them; when
  only one exists, verify before relying on it.
- **Times in `track_keys` are as written in the data.** Whether a hidden span is seen depends on
  the dialog branch that plays.
- **The index describes the unmodded game.** Only the packages in the game's `Data` folder are
  read, not the mods in the user profile.

## Vocabulary

- **Timeline** — the staging of a dialogue or cutscene: phases, actors, tracks with keys.
- **Track** (`EffectComponent`) — one aspect of one actor over time; its type starts with `TL`.
- **Dialog** — the conversation graph: nodes, speakers, lines, flag checks.
- **Root template** — the definition objects are made from; **placed object** (entity) — one
  instance in a level or among the globals.
- **Stat** — an entry of the stats files (`WPN_Longsword`): numbers and rules of an item, spell, status.
- **Osiris** — the story scripting language; a **goal** is one script file.
- **ECS** — the engine's entity-component-system: **components** hold data, **systems** run each
  frame, **queries** name the components a system looks at.
- **Dummy** (`ecl::TLPreviewDummy`) — the copy of a character a scene shows instead of the real one.
- **Toolkit** — Larian's official editor; its DLLs export the engine's C++ names.
- **Script Extender (SE)** — Norbyte's bg3se, the Lua and Osiris extension most mods build on.
