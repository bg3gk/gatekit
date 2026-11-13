# offline — what the C# ports did not take

The old repository's offline tools — an index of the game's own data, a symbol database, and one
query interface over both — are now `src/GateKit.GameData` and `src/GateKit.Symbols`, reached
through `gatekit data` and `gatekit query`. Every command of the old `kit/gatekit.py` (`search`,
`about`, `scene`, `track`, `hook`, `sql`, `schema`) has a port, and its MCP server became
`src/GateKit.Mcp`. Those files were left behind on purpose.

These four were not covered by the ports, so they came along:

| | |
|---|---|
| [`GUIDE.md`](GUIDE.md) | how to *ask* those databases: recipes, pitfalls, the vocabulary of the game's own data. Written to be handed to a language model as context. The command names in it are the old Python ones — read `gatekit query --help` for what to type now; the thinking is what matters |
| [`studies/`](studies) | questions answered with the databases alone, written up. `damage-preview.md` takes another author's mod apart offline and works out why it can stall |
| [`site/`](site) | `export.py` turns the databases into plain JSON a static site can fetch — no server, no database in the browser. The site was never written; this fixes the format so a front end can be built against it. It still imports the old `kit/gatekit.py`, so it needs the old repository (see [`../oldrepo.py`](../oldrepo.py)); porting it onto `gatekit query --json` is small work nobody has done |
| [`lsf/`](lsf) | `lsfdump`, a small Rust program that reads Larian's binary `.lsf` format directly. It was written because converting every `.lsf` to XML with LSLib's Divine took 219 seconds and 9 GB of temporary files; it did the same in 48 seconds. **Superseded**: the C# index links LSLib and reads `.lsf` in process, without either detour. It is kept because an independent reader of that format, with its measurements, is worth more than the hour it would take to write again |

`site/export.py` also carries a question nobody has answered: the export is derived from Larian's
files, and `--structure-only` leaves out their text, dialog lines and stats values while keeping
names, identifiers, types and addresses. Whether even that may be published is a decision to
make before publishing, not something the script settles.
