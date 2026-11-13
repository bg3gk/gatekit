# Stand, MCP, Toolkit: what is left (written 2026-11-12, when these directions were closed)

On 2026-11-12 the user closed every direction except decompilation and Script Extender
coverage. The stand, the MCP server and the Toolkit bridge stay as they are — working tools for
those two — and get fixes only when the two need them. This is the list of what was open.

State at closing: `stand\gatekit.py` (start, Lua in the game, hot reload, scenarios, instances,
checkpoint / reset), `stand\mcp_server.py` (20 tools), `stand\toolkit.py` with
`stand\toolkit\Bridge.cs`, bridge mod `Gwynerva_GateKit`. The suite on the v34 build: 8 of 8
(`gatekit.py --instance gk test`, 2026-11-12). Nothing is committed to git.

## Not finished

- **`test --clean`** passes 8 of 8 (2026-11-12, instance `gk`, v34 build). Two scenarios failed
  after a reset at first; the cause was the level's opening scene, which plays again after every
  load (15 s in the tutorial level) and during which the game ignores `Osi.EnterCombat`. `reset()`
  now plays it through at twenty times the speed before returning (`game.skip_scenes()`), and the
  coverage session made its fight staging retry. A reset takes 14–15 s with that. Not tried on
  levels larger than the tutorial or with scenarios that change the mod set.
- **A save loaded in the Toolkit** does not work yet: ignored in a foreign project, starts and
  hangs at 0 % in `GustavDev` (4 GB of memory were free at the time). Retry with 20 GB free.
  `docs\toolkit.md`.
- **Nothing drives the game inside the Toolkit** (no Lua there); the engine's functions are
  exported by name from its DLLs, so a native helper could call them.
- **Keyboard / controller switch** from code: not possible from Lua (three ways tried,
  `docs\facts.md`); needs the native call listed in `research\se-fork\TODO.md`, on the user's explicit yes.
- **Mouse clicks in the background** are ignored by the game; use `GK.Command` / `GK.Answer`.
- **From the load-speed report** (`research\perf\load-speed.md`), not taken: loading the `research\perf\fastexists`
  prototype into stand games (LoadModule 13.0 -> 10.2 s), minimum graphics for instances.
- **MCP tools not exercised through the server itself:** `bg3_checkpoint`, `bg3_reset`,
  `bg3_dialog`, `bg3_fast_scenes`, `bg3_toolkit_mode` (the functions under them were).
- **The stand borrows tools** from `%BG3_GATEKIT_TOOLS%` (Divine, .NET, the Python
  environment): `BG3_GATEKIT_TOOLS`. A clean machine needs a setup step that does not exist.

## Left over from the rename to GateKit (`docs\rename-gatekit.md`)

Remove once nothing uses them: the `DK = GK` alias in `Shared\Bridge.lua`; the shims
`stand\devkit.py` and `kit\devkit.py`; the old environment variable names read as fallbacks;
the old instances folder `...\Baldurs Gate 3 DevKit` (console, coverage, perf, sefork, survey);
the flag name `__DK_print_wrapped` in `Shared\ConsoleLog.lua`; `stand\rename_gatekit.py`; the
backups `.state\pre-rename` and `.state\pre-rename-2`. The repository folder
`<old repo>` itself was not renamed.

## Housekeeping

- Instance `perf` exists under both instance roots; its session is archived.
- `perf\` in the repository (start-up profiler, stack sampler, reload bench, the `fastexists`
  prototype) has no owner now.
- The mod `<the weapon-visibility mod>` was not touched and is to be reworked in sessions of its own.
