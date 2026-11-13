# symbols — names, layouts and addresses for the engine's code

The game ships as one executable with almost no names in it. Two other things do carry names:

- the **Baldur's Gate 3 Toolkit** ships the same engine cut into about two hundred DLLs that call
  each other through export tables, and those exports are complete C++ declarations;
- the **Script Extender** source describes the layout of a thousand engine types and keeps the
  byte patterns it uses to find things in the game.

`symbols.py` reads the Toolkit, the game executables and a Script Extender source tree and puts
what they say into one SQLite file:

| What | Count (Toolkit 4.1.1.6931813, game 4.1.1.7631656) |
|---|---|
| Toolkit exports with undecorated declarations | 170,472 in 105 binaries; 8,099 classes |
| Virtual tables the Toolkit exports, slot by slot | 6,932 tables, 112,375 slots |
| Fields of classes, read from accessors (`GetX`, `SetX`, `IsX`) | 8,363 fields in 1,543 classes, 126 of them single bits of a flags field |
| Struct layouts from Script Extender | 4,981 types, 20,979 fields, 18,158 with an offset |
| Enumerations from Script Extender | 308 with 5,259 values |
| Functions of `bg3.exe` | 393,159 (66,193 of them leaf functions) |
| Virtual tables found in `bg3.exe` by shape | 15,153; 1,059 recognised as a Toolkit table |
| Type names the game keeps as text | 927 systems, 3,296 components, 4,841 queries |
| ECS registration records (where each type's index lives) | 17,480 |
| Symbols Script Extender's patterns locate | 91 |
| **Toolkit functions found in `bg3.exe`** | **37,708, of them 5,902 with a full declaration** |
| Toolkit functions found in `bg3_dx11.exe` (the DirectX 11 build) | 37,825, of them 5,935 with a full declaration |
| Direct calls, both sides | 5.3 million caller-callee pairs |

Nothing from the game, the Toolkit or Script Extender is stored in this repository. Everyone
builds the database on their own machine; `symbols.sqlite` is in `.gitignore`.

## Building from scratch

Needs Windows and Python 3.9 or newer. Sources, each optional except the first:

| Source | Found through | Without it |
|---|---|---|
| Toolkit, from Steam | `--toolkit`, `BG3_TOOLKIT`, Steam's default folder | nothing to build |
| The game | `--game`, `BG3_GAME`, Steam's default folder | no game addresses |
| Script Extender source (github.com/Norbyte/bg3se) | `--bg3se`, `BG3SE_SRC`, `%BG3_GATEKIT_TOOLS%\bg3se-src\bg3se-main` | no layouts, enumerations, SE symbols or ECS records |
| `capstone` Python package | importable by the Python that runs the build | no `xrefs` table, no `signature` command |
| Pairs by pseudo-code, `code_matches` in `decomp.sqlite` | `BG3_DECOMP_WORK`, `%LOCALAPPDATA%\GateKit\decomp` | a third fewer matched functions |

The Script Extender tree used here is the fork's branch `gatekit-v34` (commit `42571697`, on top
of the author's `origin/imgui_v1_93` at `2ea16d51`), the line that becomes v34. The fork's working
tree belongs to other work, so the three things the build reads were taken out of the branch
without switching it:

```
git -C se-fork\bg3se archive gatekit-v34 BG3Extender/GameHooks/BinaryMappings.xml BG3Extender/GameDefinitions BG3Extender/IdeHelpers/ExtIdeHelpers.lua | tar -x -C <folder>
python symbols.py build --bg3se <folder>
```

Against the v33 release tree used before, `BinaryMappings.xml` and `ExtIdeHelpers.lua` are the
same and no component or system layout changed: 3,304 types, 16,422 fields, 91 symbols and the
ECS records come out identical. Only the IMGUI enumerations differ (309 enumerations instead of
308, 43 more values).

The fork adds layouts of its own on top of the author's: at `42571697` the coverage project's
`Components/Coverage.h` brings 1,677 types (4,981 against the author's 3,304). The database is
rebuilt from the branch whenever a batch of layouts lands there.

```
python symbols.py build
```

`capstone` is the only third-party package. To keep it out of your system Python:

```
python -m venv %LOCALAPPDATA%\GateKit\venv
%LOCALAPPDATA%\GateKit\venv\Scripts\python -m pip install capstone
%LOCALAPPDATA%\GateKit\venv\Scripts\python symbols.py build
```

Options: `--recursive` also reads the Qt plugin folders; `--exports-only` reads export and import
tables and nothing else (4 seconds); `--check-matching` measures the matching (below);
`--jobs N` processes for disassembly; `--db FILE`, before the command, puts the database elsewhere;
`--no-pseudo-code` ignores the pairs found by pseudo-code.

The two tools feed each other, so the full order from nothing is: `symbols.py build`, then
`decomp/decomp.py run` and `load` for the game and the Toolkit DLLs (hours, see
`decomp/README.md`), `decomp/decomp.py match` (a minute), `symbols.py build` again (it now starts
from the pseudo-code pairs as well), and `decomp/decomp.py run bg3.exe` + `load bg3.exe` once
more to put the new names into the pseudo-code (half an hour).

A full build takes about 75 seconds without capstone and a little over three minutes with it, and gives a
660 MB file. Rebuild after every update of the game or the Toolkit: all addresses change.

## Asking

```
python symbols.py class ecl::EquipmentVisualsSystem     every exported member of a class
python symbols.py classes ecl:: Timeline                classes by name; all words must match
python symbols.py find ShowWeapon [--anywhere]          functions by name (or by parameter types too)
python symbols.py vtable ecl::TLPreviewDummy            slots of a class's virtual tables
python symbols.py vtable bg3.exe+0x56C0B18              the table at an address, also one inside it
python symbols.py fields ecl::TLPreviewDummy            fields with offsets and bit numbers
python symbols.py layout ecl::EquipmentVisualsComponent Script Extender's struct
python symbols.py enum ItemSlot                         an enumeration; or enumerations that have a label
python symbols.py system ecl::EquipmentVisualsSystem    an ECS system from every angle
python symbols.py ecs EquipmentVisual                   ECS types: context and where the index is kept
python symbols.py replication eoc::RequestedRollComponent [--code]   a replicated component in one answer
python symbols.py savemodel esv::crowds::DetourIdlingComponent       how a component is written to a savegame
python symbols.py types "ecl::TLPreviewDummy" --kind query   type names of the game
python symbols.py users ecl::TLPreviewDummy             game functions that touch a type, table or symbol
python symbols.py users ecl::TLPreviewDummy --callers 2 and who calls those functions, two steps up
python symbols.py game TLPreviewDummy [--unnamed]       Toolkit functions found in the game
python symbols.py se FixedString [--report]             what Script Extender's patterns locate
python symbols.py strings ShowWeapon --dll bg3.exe      text constants and who uses them
python symbols.py callers / callees <name or bg3.exe+0xRVA>
python symbols.py signature bg3.exe+0x1899220           a byte pattern for a hook (needs capstone)
python symbols.py export --format x64dbg|ida|ghidra|json [--out FILE]
python symbols.py importers GetShouldShowWeapon         which Toolkit binaries import an export
python symbols.py stats
```

Common options: `--dll NAME`, `--kind`, `--limit N` (200), `--mangled`, `--full`. Commands about
the game look at `bg3.exe` unless told `--dll bg3_dx11`.

Reading the output:

```
bg3.exe+0x1899090  =  EoCClient.dll+0xA9D1E0  [vtable user 1]  public: ecl::TLPreviewDummy::TLPreviewDummy(void)
```
the function at this offset of the game is this Toolkit function; in brackets, how it was found
and how much evidence there is. `contains` instead of `=` means the game function is much larger
and probably has the Toolkit function inlined into it; `part of` means much smaller.

```
  +0x0A8  get ShouldShowWeapon : bool  [bit 1, mask 0x2]
```
a field read from an accessor: offset, what the accessor does, name, type, and for flags the bit.

`users` also takes the text of an ECS query exactly as `types --kind query` prints it (with its
`struct`/`class` words, and cut short with `...`): that leads from a query to the functions that
run it.

`users` mostly lists registration code and generic get/add helpers: the logic of a system reaches
a component through those. `--callers [DEPTH]` prints who calls each of them (largest callers
first, `--per-callers N` of them); a function nobody calls directly is shown with the virtual
table slot it sits in.

`vtable <binary+0xRVA>` prints the table recorded at that address when the database was built;
for an address no table was recorded at, it reads pointers from the file until the first value
that is not a code address, so the end of such a table is a guess. A slot that points at a tiny
shared function may carry the name of an unrelated function that has the same bytes.

The compiler splits many functions of the game into several parts, each listed separately in
the executable (99,826 such parts in `bg3.exe`). `users`, `callers` and `callees` fold the parts
into the function they belong to; the link is exact, taken from the unwind information. A database
built before this table existed gets it added on the first query (a few seconds).

`system <Name>` also prints where the game creates the system, its constructor and its virtual
table when `decomp/decomp.py systems` has been run (see `decomp/README.md`). That part is read
from pseudo-code and is an inference to check.

`replication <Component>` puts together what is known about a replicated component: the global
its replication index is kept in, the functions that register it with the handler objects they
create and those objects' virtual tables (slots 1 and 2 of the larger tables are the serializer
functions; `--code` prints their pseudo-code), and its fields. The game names its replicated
collection fields itself: its type names contain `ls::FieldMeta<Type Class::*, offset>`, where
the member pointer is the field's offset (159 such fields of 118 classes in `bg3.exe`). The
field names come from the macOS build (`se-fork/reference/macos-symbols/fieldmeta.json`, made by
the coverage project; optional) and are attached to an offset when exactly one field of the class
has that type. Fields that are not collections have a name from macOS but no offset here.

`savemodel <Component>` answers how a component gets into a savegame. The format is binary: a
component is copied into a plain "model" structure, `ls::model::game::<ns>::vN::<Name>`, and
arrays of models are written out. There are no attribute strings (a model is identified by its
name, size and a schema hash), but the macOS symbols name the members of 275 of the 1,043 models
in the template arguments of their visitors (`ls::model::Spec<Model>::_Field_m_<Name>`); the
coverage project collected them into `se-fork/reference/macos-symbols/savefields.json`, and the
command prints them in declaration order when that file is there. The rest comes from the
pseudo-code of the macOS build: the named
functions that save and load the component (`ls::savegame::Framework::Ops<Component, Loader>::Save`
and `::Load` - read them with `decomp.py code` to see which component offset goes to which
model offset), the model the component is copied into, every registered version of that model
with the name written in the save, its size and alignment, and the converter from the version
before. Component offsets in those functions are the macOS ones.
Under each model the command also lists the functions of `bg3.exe` that read the model's type
index; the one that allocates component storage is the Windows loader, which copies every model
offset into a component offset - that is where Windows offsets for the named members come from
(the coverage workers found this way in).

## How things are found in the game

The Toolkit is an editor build of the same source, a few months older than the game, and it is
linked differently: the game inlines aggressively, the Toolkit DLLs do not. The body of one
function therefore differs between the two, and a small Toolkit function often has no body of its
own in the game. The matching relies on what survives that.

1. **Text constants** (`strings`). A constant that exactly one function refers to on each side
   ties the two functions together. Pairs that conflict with another pair are dropped.
2. **Direct calls** (`calls`). If two matched functions make the same number of direct calls with
   every import and every matched callee in the same position, the n-th callee of one is the n-th
   callee of the other.
3. **Virtual tables** (`vtable`). A virtual method cannot be inlined away: its address is in a
   table. Tables are found in the game by shape (an address code refers to that holds pointers to
   function starts). A Toolkit table and a game table are the same table when they have the same
   number of slots, every already matched slot agrees, and no other game table fits. All their
   slots are then paired, which lets tables that inherit those methods be recognised in turn.
4. **Users of a table** (`vtable user`). When exactly one function takes the address of a matched
   table on each side, they are the same function (a constructor or destructor).

The four run in rounds until nothing new turns up. Each pair stores its method, its evidence and
`relation`, a comparison of sizes; only pairs of similar size are used as seeds for further
matching.

**How far to trust it.** `build --check-matching` hides a fifth of the text-matched pairs, runs
the other methods and compares. In the build described here: by calls, 56 hidden functions were
found again and all 56 were right (50 and 49 for `bg3_dx11.exe`); by virtual tables, 44 of 44
(45 of 45). So what is reported has held up wherever it could be checked, and the reach is
modest. Check a function in a disassembler before hooking it all the same.

**Identical functions share an address.** The linker folds functions with the same code, so one
game address often carries several Toolkit names (a getter that returns the field at +0x38 is
the same function for every class that has one). All names are kept.

## Layouts

A type that declares a virtual method starts with an 8-byte vtable pointer; a class derived from
one does not get a second. (Before 2026-11-13 the pointer was not counted, and every system
printed its fields 8 bytes too low.) Checked against the sizes the game allocates for its
systems (the coverage project read them from each factory): of 67 systems with a computed size,
60 match; the 7 that do not are most likely layouts of Script Extender's that are out of date.

Two independent sources, shown together by `fields`:

- **Accessors.** A method exported as `GetX`, `IsX` or `SetX` whose body is one access to
  `[this + offset]` gives the offset, width and type of field `X`; `mov; shr n; and 1` gives the
  bit of a flag. These are offsets in the Toolkit build. When the accessor was also found in the
  game, the offset is read there too (`game_offset`): of 408 such fields, 368 have the same
  offset in both builds.
- **Script Extender's structs.** Field order and types come from the headers. Offsets are
  computed with ordinary alignment rules for as long as every type so far has a known size.
  Script Extender names fields it has not identified after their offset (`field_98`), and those
  serve as check points: of 2,541, the computed offset agreed at 2,082; at the other 459 the
  count was re-synchronised to the name (`~` in the output). 3,030 fields have no offset because
  an unknown-size type comes before them.

Where both sources cover a class they have agreed: for `ecl::TLPreviewDummy` every accessor
offset equals the computed one, and the accessors name fields Script Extender leaves unnamed.

## Tables

`kit/SCHEMA.md` lists every table and column with row counts. In short:

| Table | Row |
|---|---|
| `dlls` | one binary (`origin` toolkit / game) |
| `symbols`, `imports` | one export, one import |
| `functions` | one entry of a binary's function list: start, size, leaf |
| `chunks` | a later part of a split function and the function it belongs to |
| `vtable_slots` | a slot of an exported Toolkit table |
| `vtabs`, `vtab_slots`, `vtab_matches` | tables found by shape, their slots, Toolkit-game pairs |
| `strings`, `string_refs` | text constants and the functions using them |
| `type_names`, `ecs_ids` | ECS type names and registration records of the game |
| `calls` | caller-callee pairs |
| `xrefs` | game functions touching known globals (ECS indices, tables, SE symbols) |
| `matches` | Toolkit function found in a game executable |
| `accessors` | fields read from accessors |
| `se_types`, `se_fields`, `enums`, `enum_values`, `lua_classes`, `lua_fields` | from Script Extender |
| `se_symbols`, `se_mappings` | what Script Extender's patterns locate, and a report per pattern |

## What is not here

- **Most game functions have no name.** 393,159 entries of the function list (99,826 of them later parts of split functions), 37,708 functions matched, 5,902 with a
  declaration. The numbers include the pairs `decomp/decomp.py match` finds by comparing
  pseudo-code (10,489 taken as seeds at score >= 0.30 and lead >= 0.20, method `pseudo-code`) and
  what calls and virtual tables then reach from them; without pseudo-code (`build
  --no-pseudo-code`, or no `decomp.sqlite`) it is 24,973 and 4,050. Non-virtual methods of the
  classes this project cares about are still thin: four methods of
  `ecl::EquipmentVisualsSystem`, and of `ecl::TLPreviewDummy` the constructor, the virtual
  methods, `SetShouldShowWeapon` and `RefreshEquipment`. `users <type> --callers` narrows the
  search for the rest; `decomp.py matches <name>` lists weaker pairs that were not taken as seeds.
- **Only 1,059 of 15,153 game tables are recognised.** A table needs at least one already matched
  slot. System classes, whose virtual methods refer to no text, mostly stay unrecognised.
- **Only direct calls.** Calls through a virtual table or a function pointer are not followed,
  and that is how the ECS runs its systems.
- **No run-time type information** in the game or the Toolkit, so a table cannot be found by
  class name. The slot order known from the Toolkit is a guide to the game's table, not a
  guarantee: an editor build can have extra virtual methods.
- **ECS index numbers are not in the file.** `ecs_ids` gives the address where a type's index is
  stored; the number is assigned when the game starts.
- **Script Extender's data is as old as its source tree.** The mappings file is pinned to an
  older game build (most patterns still match; `se --report` lists each), and the headers are
  the tree's current ones, not necessarily those of the installed Script Extender.
- **Text constants are those loaded by `lea reg, [rip+x]`**, at least five printable ASCII
  characters long. Wide strings and strings reached through tables are missed.
- **583 exports with unreadable names** (`??@<hash>@`): the compiler replaces a decorated name
  longer than 4,096 characters with a hash.
- **109 of the Toolkit's binaries are .NET assemblies.** They have no export tables and are
  listed in `dlls` with `managed = 1` only.
- Third-party libraries (Qt, FBX SDK, Noesis, PhysX) are included as they are.

The file versions in `meta` are the raw four-number version resources (`4.69.31.813` for build
4.1.1.6931813): Larian packs the build number into the last three fields.

## Files

| File | Does |
|---|---|
| `symbols.py` | the build and every command |
| `pe.py` | PE format: exports, imports, functions, text references, calls, tables |
| `match.py` | virtual table matching and its check |
| `sepatterns.py` | Script Extender's pattern language, applied to a file on disk |
| `layouts.py` | structs, enumerations and Lua classes from the Script Extender source |
| `accessors.py` | fields from the bodies of trivial accessors |
| `disasm.py` | per-function facts with capstone |
| `signature.py` | byte signatures for hooks |
| `decomp/` | pseudo-code with Ghidra; see its README |
