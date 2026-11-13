# symbols / decomp / index: state and next steps (paused 2026-11-13 for the GateKit migration)

## State

Nothing is running. All work is committed; the databases are local and not in the repository.

- `symbols.sqlite` (660 MB) is built from the Toolkit 4.1.1.6931813, the game 4.1.1.7631656 and
  the Script Extender fork `gatekit-v34` at `42571697` (archived to
  `<work folder>\bg3se-v34`), with the pseudo-code pairs as seeds.
  bg3.exe: 37,708 functions matched, 5,902 with a declaration; bg3_dx11.exe: 37,825 and 5,935.
  Layouts: 4,981 types, 20,979 fields, 18,158 with an offset (vtable pointer counted since 998fdc9).
- `decomp.sqlite` (about 1.8 GB, in `<work folder>\decomp`): pseudo-code of
  bg3.exe, bg3_dx11.exe, the x86-64 half of the macOS build, and twelve Toolkit DLLs
  (EoCClient, GameEngine, EoCShared, EoCServer, CoreLib, NoesisGameUI, AllSparkCore, LSDialogs,
  EoC, EoCNet, GameNet, ECS, AnubisFramework); the editor's C#; Toolkit-game pairs by pseudo-code;
  system factories and per-frame updates; global FixedStrings.
- `index/gameindex.sqlite` (3.5 GB): the game data index; untouched since 2026-11-12.
- Not used yet: the native Linux build (`se-fork/reference/linux-game/bin/bg3`, same version,
  44,377 function names, 66,120 names of globals, full relocations).

## What the migration has to carry over

- Tools that read binaries: `symbols.py` (PE exports, .pdata, calls, vtables, string refs,
  matching), `pe.py` (incl. `chunk_parents`: split functions via chained unwind info), `disasm.py`
  and `asmfeatures.py` (capstone -> Iced), `match.py`, `layouts.py` (Script Extender headers),
  `sepatterns.py` (BinaryMappings.xml).
- Ghidra stays an external tool driven by `decomp/decomp.py` and the two Java scripts
  (`decomp/ghidra/ApplyNames.java`, `ExportDecomp.java`); the pseudo-code database and its
  commands (`code`, `grep`, `match`, `matches`, `systems`, `fixedstrings`, `vtable`) are what the
  coverage workers use daily.
- `index/gameindex.py` and the Rust `index/lsf/lsfdump` (LSF reader) -> LSLib as a library.
- Commands the coverage project depends on: `symbols.py users --callers`, `system`, `layout`,
  `replication`, `savemodel`, `vtable <address>`, `callers/callees`; `decomp.py code` with
  addresses inside functions, jump stubs and arm64 macOS addresses.

## Next steps (after the migration)

1. Rebuild `symbols.sqlite` whenever the coverage project reports a new fork commit.
2. Use the Linux build: names of globals and relocations as anchors; its inlining is closer to
   Windows than the macOS build's.
3. Names from the macOS build: automatic transfer reached only 84% (92% at strict thresholds,
   2,533 pairs) - try combining pseudo-code, call graph and ECS type sets as joint evidence
   before showing any of it.
4. The 7 systems whose computed size differs from the game's allocation (listed in the commit
   message of 998fdc9's report to the coverage session): check Script Extender's layouts.
