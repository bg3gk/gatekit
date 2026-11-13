# symbols — reading the engine's code

The game ships as one executable with almost no names in it. Two other things do carry names: the
official **Toolkit** ships the same engine cut into about two hundred DLLs whose export tables are
complete C++ declarations, and **Ghidra** can turn the shipping executable into pseudo-code that
those names can be applied to. These notes are how that was done and what it gave.

| | |
|---|---|
| [`symbols.md`](symbols.md) | how the symbol database is built: export tables, demangled declarations, field offsets, vtable slots, and how a Toolkit function is matched to its address in the shipping game. Was `symbols/README.md` in the old repository |
| [`TODO.md`](TODO.md) | where this work stopped and what it would take to go on |
| [`requests.md`](requests.md) | engine functions the test stand asked about, answered from the database — addresses, signatures, callers. Nothing in it was tried in a running game |
| [`toolkit.md`](toolkit.md) | getting a program channel into the official Toolkit ("Glasses"), read from its managed code with ILSpy |
| [`decomp/`](decomp) | the pseudo-code tooling: `decomp.py` runs Ghidra without its window, applies the names the symbol database knows, decompiles every function and keeps the result in SQLite with full-text search. `ghidra/*.java` are the scripts it runs inside Ghidra; `codematch.py`, `graphmatch.py`, `asmfeatures.py` match functions between builds |

## What is not here

**The databases.** `symbols.sqlite` and `decomp.sqlite` (about 1.8 GB) are derived from Larian's
binaries and are built on your own machine from your own copy of the game and Toolkit. Nothing of
their content is committed, here or anywhere.

**The builder, `symbols/symbols.py`.** It is being rewritten in C# as `src/GateKit.Symbols`; the
Python stayed in the old repository so the port has one source of truth. If that port stops short
of what the Python did, the old file is the thing to go back for — `docs/contracts/cli.md`
describes its commands. The decompilation tooling above is **not** part of that port and is kept
here in full.
