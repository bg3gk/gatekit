# decomp — pseudo-code of the game and the Toolkit

`decomp.py` runs Ghidra without its window over a binary, applies the names the symbol database
knows, decompiles every function and keeps the result in a SQLite file with full-text search.

The pseudo-code is derived from the game's binaries. It stays on your machine: nothing of it is in
this repository and it is not for publishing.

## What you need

| Thing | Where from | Size |
|---|---|---|
| Ghidra 12.2 or newer | github.com/NationalSecurityAgency/ghidra/releases | 570 MB |
| The JDK Ghidra asks for (JDK 25 for Ghidra 12) | adoptium.net | 140 MB |
| `../symbols.sqlite`, built | `python ../symbols.py build` | |

Unpack both archives into one folder, `%LOCALAPPDATA%\GateKit\tools` by default
(`BG3_DECOMP_TOOLS` to change it). Nothing is installed into the system.

Output goes to `%LOCALAPPDATA%\GateKit\decomp` (`BG3_DECOMP_WORK` to change it): per binary
a Ghidra project, a log and `functions.jsonl`; and `decomp.sqlite` for all of them. Ghidra's own
settings, cache and temporary files are kept in a `ghidra-user` folder there, not in your profile.

## Running

```
python decomp.py run bg3.exe            analyse and decompile the game executable
python decomp.py run EoCClient.dll      a Toolkit DLL
python decomp.py load                   put finished output into decomp.sqlite
python decomp.py status                 what has been run and loaded
python decomp.py code ecl::TLPreviewDummy::TLPreviewDummy
python decomp.py code bg3.exe+0x1899090
python decomp.py code bg3.exe+0x200523F --grep TypeIndex -C 2
python decomp.py grep "VisibilityFlags" [--binary bg3.exe] [--lines 5] [--word]
python decomp.py systems EquipmentVisuals [--rebuild]
python decomp.py vtable bg3.exe+0x5629560
```

`code` takes a function name, `binary+0xRVA`, or an absolute address as Ghidra prints it
(`bg3.exe+0x14189ae00` for `FUN_14189ae00`). An address inside a function is accepted: the
function that contains it is printed, with a note on standard error. An address that is a
jump stub (`jmp target`, as the destructors referred to by registration code are) is followed to
the function it leads to. A name that fits several functions prints them all with binary, address,
size and signature: the Toolkit's, the game's and the macOS build's versions of one function are
three entries. With `--grep TEXT` only
the lines that contain the text are printed, numbered, with `-C` lines around each. `--grep "a|b"`
prints the lines that contain either text. Functions Ghidra left as `FUN_...` although their
address is exported (its demangler gives up on some long names: 855 functions of the Toolkit DLLs)
get the exported name when a binary is loaded, so `code Class::Method` finds them.

`grep` finds a word anywhere inside a longer name: `grep GroundMaterial` finds
`GroundMaterialComponent` and `GetUnderlyingGroundMaterial`. The index stores whole tokens, so
the tokens containing each word are looked up first (one to two seconds per word, at most 400
tokens per word, the most frequent first); `--word` skips that and matches whole tokens only.
Several words must all occur in the function.

`systems` reads from the pseudo-code where the game creates each ECS system: the function that
adds it to the world (the factory), the constructor it calls, and the virtual table the
constructor stores. The result is kept in the table `system_factories`; `symbols.py system`
prints it together with the slots of the table. It is an inference from text Ghidra produced:
when one function creates several systems, constructor and table are left empty. Measured on
`bg3.exe`: 930 records for 929 of the 963 systems, a table for 617 of them, three seconds. 144
systems share 25 tables between them (one table serves 27 systems): that is a base class's table,
the system itself overrides nothing. The tables are short (five to eleven slots of small
functions), so the per-frame work of a system is not simply "slot N"; which function does it has
not been worked out yet.

`vtable` is `symbols.py vtable` under a second name.

`systems` also finds each system's per-frame update. The factory stores one function pointer per
system: the engine's update entry for it (in the macOS build that function is named
`ecs::_private::SystemRegistrationHelper::SystemUpdate<System>`; the Windows one has the same
body). The entry builds the system's views and calls the update; when it calls several functions
on the system they are listed as steps, and when the compiler folded the update into the entry
the entry itself is the update. Measured on `bg3.exe`: an update for 856 of 913 systems (691
through an entry, 105 folded, 60 with several steps), 938 functions in all. `symbols.py system`
prints it, and `users` / `callers` / `callees` / `vtable` mark these functions
`~ per-frame update of <System> [factory]`. The shape was compared with the named macOS function
on `ecl::EquipmentVisualsSystem`; there is no independent check of the rest.

## The macOS and Linux builds as sources of names

The macOS build of the game (same version, 4.1.1.7631656) keeps a symbol table: about 481,000
function names with parameter types, 165,000 names of globals, 22,548 virtual tables; no DWARF.
Its x86-64 half is cut out of the universal file and decompiled like any other binary
(`decomp.py run <path>`: 4 h 41 min, 482,390 functions, 75 not decompiled, 1.2 GB of output), so
`decomp.py code ecl::TLPreviewDummy::SetShouldShowWeapon` shows two versions: the Toolkit's and
the game's own macOS one, both with names. Neither the file nor anything made from it is in the
repository.

Names do not carry over to `bg3.exe` by themselves, and the obvious automatic ways fall short.
Structure layouts differ between the two platforms (`TLPreviewDummy.VisibilityFlags` is at +0xA0
on macOS and +0xA8 on Windows), and the Windows build inlines where the macOS one does not
(293,000 functions against 481,000). Measured against the names the Toolkit gives:

| Way | Pairs | Agreement |
|---|---|---|
| `decomp.py match --source bg3_macos_x64` (pseudo-code, default thresholds) | 9,552 | 84% |
| the same at score >= 0.50, margin >= 0.20 | 2,533 | 92% |
| constants of the machine code (`asmfeatures.py`) | about 7,000 | 88% |
| unique text constants, then the call graph (`graphmatch.py`) | 16,197 | 78% |

None of these is good enough to show as names, so none is wired into the commands; the pairs
are not kept. What the macOS build is used for today: reading a function with real names next
to its Windows counterpart, the field names in its symbols (`symbols.py replication`), and
confirming shapes such as the systems' update entries. `asmfeatures.py` and `graphmatch.py` are
the experiments' code, kept for the next attempt (evidence from several sources at once).

The universal macOS file has two halves, and each has its own addresses for the same functions.
The coverage project's symbol tables (`se-fork/reference/macos-symbols`) were read from the arm64
half; the pseudo-code here is of the x86-64 half. `decomp.py code bg3_macos_x64+0x<address>`
takes either: an address that is not a function start of the x86-64 half but is a function of
the arm64 half is translated through the function's name (275,700 functions; the table is built
on first use, twelve seconds, and kept in the work folder). Absolute addresses (0x1...) and
offsets from 0x100000000 are both accepted.

The native Linux build (same version; 44,377 function names, 66,120 names of globals, full
relocations, no DWARF) has been downloaded and looked at, not used yet.

## Global FixedStrings

```
python decomp.py fixedstrings Sneaking            by part of the text
python decomp.py fixedstrings bg3.exe+0x6250530   by the address of a global
python decomp.py fixedstrings --rebuild           read the pseudo-code again (half a minute)
```

The game compares names of statuses, flags, sounds and the like as `FixedString` values kept in
globals, so pseudo-code is full of `x == DAT_146...` with no text in sight. Every such string
has a registry entry that a small function fills on first use (text pointer, length, link into
one list), and the registry turns each entry into a FixedString when the game starts. `fixedstrings`
reads those functions and records three kinds of addresses: `entry` (the entry itself), `value`
(the FixedString, entry + 0x18) and `pointer` (a global holding the address of the value, the
form most code uses: `*DAT_146250530`). `code` then writes the text after the first mention of
such a global: `*DAT_146250530 /* -> FixedString "RTPC_Sneaking" */`.

Measured on `bg3.exe`: 5,429 entries are linked into the list, 4,707 got their text (4,308
pointer globals). The other 722 are filled by shared functions that receive the text as an
argument; those are not resolved. The offset 0x18 and the meaning of the fields are read from the
pseudo-code and not checked in the running game.

A global that is added to a pointer and that no function of the binary ever assigns is a member
offset the compiler kept as data (serializers read their first member as `DAT_x + object`).
`code` writes its value after it: `/* never assigned; zero-filled data, so 0 */`, or the value
stored in the file. "Never assigned" is judged from the pseudo-code of the whole binary.

The Toolkit's DLLs keep their FixedStrings differently; `fixedstrings --binary EoCClient.dll`
finds none there yet.

## Pairing Toolkit functions with game functions by pseudo-code

```
python decomp.py match                               compute and store the pairs (under a minute)
python decomp.py matches TLPreviewDummy:: [--all]    pairs for Toolkit functions by name
python decomp.py matches bg3.exe+0x189AE00           the pair of one game function
```

`symbols.py` pairs functions through text constants, calls and virtual tables, and stops where a
chain breaks. `match` (`codematch.py`) compares the functions themselves. The features of a
function are the numbers in its pseudo-code (field offsets, masks, sizes, magic values), pairs of
consecutive numbers, and its text constants; addresses, 0 and 1 are left out, and the declaration
is skipped because its types differ between the builds. Rare features weigh more. A Toolkit
function's candidates are the game functions that share a rare feature with it; the score is the
weight of shared features over the weight of all features of both; `margin` is the lead over
the runner-up. A pair is stored when each side is the other's best. Functions under 64 bytes are
left out.

Measured on `bg3.exe` against eleven Toolkit DLLs (features 25 s, pairing 20 s), checked against
the pairs `symbols.py` has (found without pseudo-code):

| Thresholds | Pairs | Known pairs: agree / differ | Game functions without a pair before | of them named |
|---|---|---|---|---|
| score >= 0.10, margin >= 0.05 (everything stored) | 30,422 | 7,918 / 38 (99.5%) | 21,967 | 3,798 |
| **score >= 0.15, margin >= 0.10 (shown by default)** | 25,281 | 7,679 / 16 (99.8%) | 17,216 | 2,653 |
| score >= 0.30, margin >= 0.20 | 18,768 | 5,744 / 2 | 12,731 | 1,278 |

"Named" counts pairs whose Toolkit function has an exported name; the others still tie an
unnamed game function to its Toolkit counterpart, whose callees and types are named.

Where the pairs show up: `code` writes `/* ~ Name [pseudo-code 0.81] */` after an unnamed
`FUN_...`; `symbols.py` prints `~ Name [pseudo-code 0.81]` in `users`, `callers`, `callees`,
`vtable` and `system`. The `~` marks an inference: the check above says how often it agrees
with independent evidence, not that a given pair is right. A low score with a clear margin is
common for a function the game inlined other code into. The pairs are not written into
`symbols.sqlite` and are not fed back into Ghidra yet.

`run` options: `--memory 20G` (Java heap), `--cpu N`, `--range FIRST-LAST` (hex RVAs: decompile
part of a binary, to split a run or continue one), `--fresh` (analyse again from the start).
A second `run` on the same binary reuses the analysed project and only decompiles. For a game
executable it first applies the names found since (after `decomp.py match` and a new
`symbols.py build`): functions that still have no name get one, decorated names are demangled
into full signatures, functions already named are left alone. Measured on `bg3.exe`: 2,008
functions named (97 failed, 12,832 were named already), 39 minutes, 235,151 functions, 15 not
decompiled. The comment above each such function says where its name comes from, for example
`[toolkit match by pseudo-code]`.

For a game executable, `run` first writes the names known for it
(`symbols.py export --format ghidra`) and applies them before analysis: functions found through
the Toolkit get their decorated name, which Ghidra turns into a full signature with parameter
types; ECS index globals, virtual tables and Script Extender's symbols get labels. Toolkit DLLs
need none of this: their exports already carry the names.

## The editor's managed code

109 of the Toolkit's binaries are .NET assemblies: the editor's windows, plugins and tools.
`decomp.py dotnet` decompiles Larian's own ones to C# with ILSpy and loads them into the same
database; third-party libraries are skipped by name.

```
python decomp.py dotnet --dotnet D:\tools\dotnet8        decompile and load (needs ilspycmd, below)
python decomp.py csharp "LoadLevel"                       search, with matching lines
python decomp.py csharp Glasses/Glasses/GlassesApp.cs --file    print one file
```

It needs the `ilspycmd` package from nuget.org (4 MB): unpack the `.nupkg` (it is a zip) into
`<tools>\ilspycmd\<version>\`. It runs on a .NET 8 runtime; no SDK is required. One assembly
gets `--timeout` seconds (900 by default); mixed C++/CLI assemblies such as `EoCPlugin.dll` can
take far longer and are then kept as far as they got.

Measured here: 57 assemblies, 30,678 C# files, 1.95 million lines; about 50 minutes, most of it
on `EoCPlugin.dll`, which was stopped by hand with 8,422 files written. The files also lie as
plain text under `<work>\dotnet\<Assembly>\`.

`../toolkit.md` is a reading of that code: how the editor loads plugins, its command line and the
calls that open a project, load a level and switch to game mode.

## How long it takes

| Binary | Functions | Analysis and decompilation | Output |
|---|---|---|---|
| `ECS.dll` (0.9 MB) | 1,946 | 50 s analysis, 20 s decompilation | 4.6 MB |
| `bg3.exe` (107 MB) | 235,053 (4 not decompiled) | 81 min: 48 min analysis, 31 min decompilation | 570 MB of `functions.jsonl`, about 400 MB in `decomp.sqlite` |
| `bg3_dx11.exe` (99 MB) | 234,768 (12 not decompiled) | 88 min | |
| `EoCServer.dll` | 60,295 (12) | 29 min | |
| `EoCShared.dll` | 60,534 (4) | 22 min | |
| `EoCClient.dll` | 51,667 (3) | 16 min | 136 MB |
| `EoC.dll` | 36,119 (0) | 16 min | |
| `GameEngine.dll` | 34,190 (2) | 10 min | 87 MB |
| `NoesisGameUI.dll` | 25,717 (4) | 13 min | |
| `CoreLib.dll` | 11,339 (0) | 5 min | |
| `GameNet.dll`, `EoCNet.dll`, `LSDialogs.dll`, `AllSparkCore.dll` | 12,041 (0) | 5.5 min together | |

The eleven Toolkit DLLs above took 117 minutes one after another with `--cpu 8 --memory 16G`:
291,902 functions, 25 not decompiled. With `ECS.dll` and `bg3.exe` loaded as well,
`decomp.sqlite` is 887 MB and the work folder 6.9 GB.

Measured on a 12-thread machine with a Java heap of about 25 GB. The run needs the "Create Address
Tables" analyzer switched off (the script does it): with it on, Ghidra ran out of memory twice
at about 44 minutes even with a 25 GB heap.

## What the pseudo-code is and is not

It is C-like text that Ghidra reconstructs from machine code. Functions the symbol database
identified have their real names and parameter types; calls to them read as calls by name;
everything else is `FUN_<address>`, `DAT_<address>`, `param_1`, `local_28`, and field accesses are
offsets from a pointer. There are no original comments or local variable names, templates and
inlined functions are expanded in place, and a function Ghidra cannot make sense of is stored
with its error instead of code.

The more the symbol database knows, the better this reads: rebuild it, then
`run --fresh` for a full pass or `run` for decompilation only.

## Files

| File | Does |
|---|---|
| `decomp.py` | runs Ghidra, loads output, answers `code`, `grep`, `systems`, `match`, `matches` |
| `codematch.py` | features of a function's pseudo-code and the pairing by them |
| `asmfeatures.py` | constants, calls and text references of functions read from machine code (PE and Mach-O); experimental |
| `graphmatch.py` | pairing two builds along their call graphs; experimental |
| `ghidra/ApplyNames.java` | Ghidra script: names and comments before analysis; turns off analyzers that cost hours and add little |
| `ghidra/ExportDecomp.java` | Ghidra script: decompiles all functions in parallel, one JSON object per line |

## Known limits

- Ghidra's launcher is a batch file and fails on paths with parentheses, so the binary is copied
  into the work folder before import.
- Struct types are not applied yet: fields show as offsets. `../symbols.py fields CLASS` and
  `layout CLASS` give the names to read them with.
- `bg3_dx11.exe` is the same code built for DirectX 11 (`bg3.exe` is the Vulkan build). It is decompiled
  and matched like `bg3.exe`; with its pseudo-code pairs as seeds it has 37,825 functions matched
  (5,935 with a declaration) against 24,927 before.
