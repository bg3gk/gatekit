# Host CLI contract (derived from the Python code of the old repo)


> **What this is.** A record of the Python stand this toolkit grew out of, read out of its
> code line by line during the move to C#, so that nothing it did was lost in the port.
> It describes **that** program, not the one in this repository: the `file:line` references
> are to a repository that is not published, and "Proposed" marks what was then a
> suggestion. For what the toolkit does today, read the program's own `--help`, `AGENTS.md`
> and the code. The one document here that is a live contract is `bridge-protocol.md`.

Source of truth: the old repository at commit `0cc2327` ("symbols: state and next steps before the GateKit migration"). Everything below is read from the code, not from the docs. References are `file:line`, paths relative to that repo. "Proposed" marks anything that is a design suggestion for the unified `gatekit` CLI and not existing behaviour.

Conventions:

- "default game" = no instance selected; the stand works on the installed game and the player's real profile. "instance" = a cloned game folder + private profile (see `instances.py`).
- `STATE` = `<repo>\.state` for the default game, `<repo>\.state\instances\<name>` for an instance (`stand/gatekit.py:50-51,77`). `SHARED_STATE` is always `<repo>\.state`.
- "stand" = `stand/gatekit.py` (+ `instances.py`, `sefork.py`). "bridge" = the in-game Lua mod `Gwynerva_GateKit` (`stand/bridge`).
- Exit codes: unless said otherwise `0` = success, `1` = error (`StandError` printed to stderr as `ERROR: <message>`, or an uncaught Python exception with traceback, also exit 1), `2` = argparse usage error.

---

## 1. Process-wide behaviour of `stand/gatekit.py`

### 1.1 Command line shape

```
python stand/gatekit.py [--instance NAME] <command> [options] [-- extra game args]
```

- `--instance NAME` is not an argparse option. `_instance_name()` (`gatekit.py:60-68`) scans `sys.argv[1:]` (only the part before a literal `--`), and if `--instance` is present anywhere there **and** `__name__ == "__main__"`, it sets `os.environ["BG3_GATEKIT_INSTANCE"]=NAME` and deletes the two tokens from `sys.argv`. So it works before or after the command word. It only works when run as a script: a process that `import gatekit` (the MCP server, `situation.py`, coverage scripts) must use the environment variable. `--instance` as the last token (no value) is not consumed and argparse then fails with exit 2.
- Instance selection happens at **import time** (`gatekit.py:71-78`). Failure (`InstanceError`, i.e. bad name) -> `sys.exit("ERROR: ...")` -> stderr, exit 1. Valid name = `^[A-Za-z0-9_-]{1,32}$` (`instances.py:29`).
- With an instance: `GAME, PROFILE, STATE = INSTANCE.game, INSTANCE.profile, INSTANCE.state`, and `sefork.use(GAME/"bin", STATE)` rebinds the Script Extender switcher to the instance's `bin`.

### 1.2 Environment variables (all of them, by file)

| Variable | Read in | Default / meaning |
|---|---|---|
| `BG3_GAME` | `gatekit.py:55`, `sefork.py:21`, `gameindex.py:51`, `symbols.py:46`, `decomp.py:39` | Installed game folder. Default `C:\Program Files (x86)\Steam\steamapps\common\Baldurs Gate 3` |
| `BG3_EXE` | `gatekit.py:81` | Game exe name inside `bin`. Default `bg3_dx11.exe` |
| `BG3_GATEKIT_INSTANCE` | `gatekit.py:68`, `situation.py:84,751`, `agent_run.py:235` | Instance name (same as `--instance`) |
| `BG3_GATEKIT_INSTANCES` | `instances.py:141,154` | Folder holding all instances. Default `<game parent>\Baldurs Gate 3 GateKit` (`<Steam>\steamapps\common\Baldurs Gate 3 GateKit`) |
| `BG3_GATEKIT_TOOLS` | `gatekit.py:90`, `symbols.py:48` | Folder with `lslib\Packed\Tools\Divine.exe` and `dotnet\`. Default: a tools folder on the machine it ran on |
| `BG3_GATEKIT_START_TIMEOUT` | `gatekit.py:108` | Seconds from process start to first bridge heartbeat. Default 75. (`levels_survey.py` uses 240, `tour.py` 300.) |
| `BG3_GATEKIT_SE` | `gatekit.py:963-967`, `sefork.py:137`, `agent_run.py:142` | Folder with a built `BG3ScriptExtender.dll`; or the word `stock` (case-insensitive) to force the stock Script Extender. Unset -> `se-fork\builds\v34` if it contains `BG3ScriptExtender.dll`, else stock. `up` writes the resolved value back into `os.environ` |
| `BG3_DEVKIT_*` | `gatekit.py:40-42` | Legacy aliases: **every** `BG3_DEVKIT_<X>` in the environment is copied to `BG3_GATEKIT_<X>` with `setdefault` (so the new name wins) before anything else is read. Only `gatekit.py` does the generic copy; `symbols.py:48` reads `BG3_DEVKIT_TOOLS` explicitly; the other modules do not know it |
| `BG3_TOOLKIT` | `toolkit.py:31`, `symbols.py:52`, `decomp.py:40` | Official Toolkit ("Glasses") folder. Default `C:\Program Files (x86)\Steam\steamapps\common\Baldurs Gate 3 Toolkit` |
| `BG3_INDEX_DB` | `kit/gatekit.py:36`, `uicatalog.py:252` | Path of `gameindex.sqlite` (default `<repo>\index\gameindex.sqlite`) |
| `BG3_SYMBOLS_DB` | `kit/gatekit.py:37` | Path of `symbols.sqlite` (default `<repo>\symbols\symbols.sqlite`) |
| `BG3_DECOMP_WORK` | `kit/gatekit.py:38`, `decomp.py:37`, `symbols.py:672,1070,1586,1608` | Folder holding `decomp.sqlite`; default `%LOCALAPPDATA%\GateKit\decomp` |
| `BG3_DECOMP_TOOLS` | `decomp.py:38`, `macos_symbols.py:131` | Ghidra/JDK/ilspy folder; default `%LOCALAPPDATA%\GateKit\tools` |
| `BG3_INDEX_WORK` | `gameindex.py:52-53` | Unpack work folder; default `%LOCALAPPDATA%\GateKit\index-work` |
| `BG3_DIVINE` | `gameindex.py:2264,2327` | Path to `Divine.exe` (default none: `--divine` is then required) |
| `BG3_LSFDUMP` | `gameindex.py:935` | Path to native `.lsf` reader `lsfdump.exe` |
| `BG3SE_SRC` | `symbols.py:49` | Script Extender source tree (default: autodetected under `<TOOLS>\bg3se-src\bg3se-main` or `<TOOLS>\bg3se`) |
| `LOCALAPPDATA` | `gatekit.py:56` (hard `os.environ[...]`, KeyError if missing), kit/index/symbols/decomp (`or Path.home()`), `situation.py`, `agent_run.py` | Profile root; the toolkit root `%LOCALAPPDATA%\GateKit` is `local_folder()` (falls back to legacy `GwynervaDevKit` if only that exists, `kit/gatekit.py:26`) |
| `WINDIR` | `toolkit.py:38` | for `csc.exe`; default `C:\Windows` |
| `USERPROFILE`, `LOCALAPPDATA`, `APPDATA` | set **for the game child process** by `Instance.environment()` (`instances.py:235-239`) | pointing into `<instance>\home` |
| `GK_AGENT_STRICT` (`=1`) | `situation.py:76` | refuse omniscient `look ... all` |
| `GK_AGENT_CAMERA` (`=0` off) | `situation.py:77` | camera follows moves (default on) |
| `GK_AGENT_FOCUS` (`=0` off) | `situation.py:81` | visual focus rings (default on) |
| `GK_AGENT_STATE` | `situation.py:84`, `agent_run.py:164` | name of the cursor/id files; default = instance name or `default` |
| `GK_AGENT_CLI` | `situation.py:1325`, `agent_play.py` | command name printed in help / refusal messages |
| `COVERAGE_ONLY`, `COVERAGE_SITUATIONS`, `COVERAGE_LIBRARY`, `COVERAGE_FAST`, `COVERAGE_LEVEL`, `COVERAGE_SPEED`, `COVERAGE_RESUME`, `COVERAGE_SE_TREE` | `se-fork/coverage/*.py` | see section 9 |

### 1.3 Windows-only mechanisms the platform layer must replace

`ctypes.WinDLL("psapi")` process enumeration (`running_games`, `gatekit.py:124`), `taskkill /F /PID` (`:162`), `msvcrt.locking` byte lock (`:577`), `AttachConsole`/`ReadConsoleOutputCharacterW` (`:391`), PowerShell `window.ps1` (`PrintWindow`, `PostMessage`; `:182`), NTFS junctions + hard links (`instances.py`), `DETACHED_PROCESS|CREATE_NEW_PROCESS_GROUP` (`:984`), `PermissionError` as the "file is open by the game" signal (`_retry`, `:561`), `%LOCALAPPDATA%` profile layout, Steam `userdata\*\1086940\remote` cloud mirror (`:210`).

---

## 2. Stand commands (`stand/gatekit.py`, argparse at `main()` `gatekit.py:1121-1257`)

Every command below runs inside `try: ... except StandError` (`:1177-1256`) -> stderr `ERROR: msg`, return 1. `FileNotFoundError`, `OSError`, `AssertionError` (outside `run`), `InstanceError` (from `destroy`) etc. are **not** caught and produce a Python traceback (exit 1).

Proposed names are for the unified CLI. "Proposed" = new; where the verb already exists it is kept as is (top-level verbs of the stand stay top-level).

### 2.1 Summary table

| # | Current command | Proposed unified name | One line |
|---|---|---|---|
| 1 | `build` | `gatekit build` (keep) | pack the bridge mod to a `.pak` and install it into the profile |
| 2 | `up` | `gatekit up` (keep) | back up, configure, start the game, wait until playable |
| 3 | `down` | `gatekit down` (keep) | kill the game, restore saves/mod list, remove helpers |
| 4 | `status` | `gatekit status` (keep) | print instance, pids, SE build, heartbeats |
| 5 | `instances` | `gatekit instances` (keep) | list instances and whether running |
| 6 | `destroy` | `gatekit destroy` (keep) | down + delete the instance |
| 7 | `exec` | `gatekit exec` (keep) | run Lua on client/server/ui, print JSON |
| 8 | `shot` | `gatekit shot` (keep) | screenshot of the game window |
| 9 | `key` | `gatekit key` (keep) | post a key to the game window |
| 10 | `link` | `gatekit link` (keep) | register a mod's Lua for hot reload |
| 11 | `reload` | `gatekit reload` (keep) | copy linked Lua in, reset Lua states |
| 12 | `logs` | `gatekit logs` (keep) | SE console text, or Osiris trace with `--story` |
| 13 | `watch` | `gatekit watch` (keep) | reload on every change of linked Lua |
| 14 | `test` | `gatekit test` (keep) | run a folder of scenarios, sharing sessions |
| 15 | `bridge-reload` | `gatekit bridge reload` (Proposed alias; keep `bridge-reload`) | link + reload the bridge's own Lua |
| 16 | `checkpoint` | `gatekit checkpoint` (keep) | save a restore point of the live world |
| 17 | `reset` | `gatekit reset` (keep) | load the checkpoint without restart |
| 18 | `run` | `gatekit run` (keep) | `up`, run one scenario, `down` |
| 19 | `_console` (hidden) | none (Proposed: internal, replaced by the platform layer's "read SE console") | child process that dumps the console buffer |

Plus the two shims `stand/devkit.py` and `kit/devkit.py` (see 2.20), and `stand/sefork.py` as a separate CLI (section 3).

### 2.2 `build`

Synopsis: `gatekit.py build`. No options.

Steps (`build_bridge`, `gatekit.py:191-205`):
1. If a game process of this stand's folder runs -> `StandError "the game is running and holds the mod files open; run `down` first"`.
2. `<STATE>\build\Gwynerva_GateKit.pak` parent dir created; old pak deleted.
3. Runs `<TOOLS>\lslib\Packed\Tools\Divine.exe -g bg3 -a create-package -s <repo>\stand\bridge -d <pak> -c lz4` with `DOTNET_ROOT=<TOOLS>\dotnet`, output captured. If the pak does not exist afterwards -> `StandError "Divine did not produce the bridge pak:\n" + stdout + stderr`. (Divine's own exit code is not checked.)
4. `PROFILE\Mods\` created (`mkdir parents`), pak copied (`copy2`) to `PROFILE\Mods\Gwynerva_GateKit.pak`.
5. Prints `installed <path>`.

Files touched: `.state\build\Gwynerva_GateKit.pak` (written), `<profile>\Mods\Gwynerva_GateKit.pak` (written). Exit 0/1.

`up` calls `build_bridge()` itself when the installed pak is missing or any file under `stand\bridge` is newer than it (`:938-942`), printing `bridge pak rebuilt (its sources were newer than the installed pak)`.

### 2.3 `up`

Synopsis:
```
gatekit.py up [--level MODULE:LEVEL] [--save FOLDER] [--mods A,B,...] [--story-log] [-- GAME_ARGS...]
```
Arguments (`launch_options`, `:1125-1132`):
- `--level` (default None): start a fresh test game, no menu, no save.
- `--save` (None): save folder name under `Savegames\Story` to load instead of the last.
- `--mods` (None): comma separated folder names; empty entries dropped (`mods = [m for m in split(",") if m]`). `--mods ""` gives `[]` (still triggers isolation); not passing it gives None.
- `--story-log` flag: also record Osiris trace.
- `game_args` (`nargs="*"`): extra raw game arguments after `--`.

`up(extra_args, timeout=300, level, mods, story_log, save)` (`gatekit.py:919-1001`), in order:
1. If `game_pids()` (processes of **this** `bin` folder) -> `StandError "the game is already running; run `down` first"`.
2. Instance only: `prepare_instance(save, all_saves=not level)` (see 2.3.1).
3. `retire_former_bridge()` (`:900`): deletes `<profile>\Mods\Gwynerva_DevKit.pak`; moves/merges `<profile>\Script Extender\Gwynerva_DevKit\` into `...\Gwynerva_GateKit\` (renames if the new does not exist; else moves only missing items and removes the old folder); removes `<game>\Data\Mods\Gwynerva_DevKit\`.
4. Bridge pak rebuild if stale (as in 2.2).
5. Default game only: `backup()` (2.3.2).
6. If `--save`: requires `<SAVES>\<save>` to be a directory else `StandError "no save folder ... there are: [...]"` (the listing `SAVES.iterdir()` raises if SAVES is missing). Then `extra_args = ["-loadSaveGame", save, *extra_args]`.
7. If `--level`: `extra_args = ["-testLoadLevel", level, *extra_args]` (so with both, the order is `-testLoadLevel L -loadSaveGame S ...`).
8. If `--level` or `--mods` given (mods not None): `isolate_mods(mods or [])` (2.3.3). `--level` alone therefore means "base game + bridge only".
9. `enable_bridge()` (2.3.4).
10. Instance: `INSTANCE.spread()` (copies `Public\modsettings.lsx` and `Public\Savegames\Story` into the 4 `Debug_Client_Profile_n` folders). Default game: `lend_debug_profiles(with_saves=not level)` (2.3.5).
11. `skip_splash(True)` (2.3.6).
12. `configure_extender(story_log)` (2.3.7).
13. Script Extender build choice (`:963-968`): `BG3_GATEKIT_SE=stock` -> unset the variable; unset and `se-fork\builds\v34\BG3ScriptExtender.dll` exists -> set variable to that folder. Then `sefork.apply_for_run(SE_SETTINGS)`: variable unset -> `sefork.remove()` (stock); set -> `sefork.install(folder)` (section 3), writes `"SendCrashReports": false` into `ScriptExtenderSettings.json` (indent 4, utf-8 no BOM), prints `Script Extender for this run: <folder>`.
14. If any mods are linked (`.state\loose.json` non-empty) -> `sync_loose()`.
15. Deletes stale channel files in `<profile>\Script Extender\Gwynerva_GateKit\`: `state_*.json`, `res_*.json`, `queue_*.jsonl`, `cmd_*.tmp` (errors ignored). (`queue_*.lock` and `cmd_*.json` are left.)
16. Launch args: `[EXE, "--skip-launcher", *(extra_args if extra_args is not None else ["-continueGame"])]`. Note `--level` makes `extra_args` non-None, so `-continueGame` is dropped; with only `--save`, `-continueGame` is dropped too.
17. Up to **2 attempts** (`:981-998`): `clear_crash_marker()` (deletes `<profile>\ModCrashSanityCheck`); `Popen(args, cwd=<bin>, env=INSTANCE.environment() or inherited, creationflags=DETACHED_PROCESS|CREATE_NEW_PROCESS_GROUP)`; waits up to **60 s** (poll 0.5 s) for the process to appear in `game_pids()` else `StandError "the game process did not appear"`; waits up to `BRIDGE_START_TIMEOUT` (75 s default) for a fresh `state_client.json` (mtime <= 5 s old and parseable); if none: `kill_game()`; attempt 1 prints `mods were off for this start (a pak changed); restarting` and retries; attempt 2 raises `StandError "the bridge mod did not load in two starts; is Script Extender installed?"`.
18. `game.wait_state("Running", "client", timeout=300, since=started)`: polls every 0.5 s; prints `  <secs> s  <state>` on each new `gameState`; raises `"the game process is gone"` if the process dies, `"client did not reach state Running in 300 s; last: {...}"` on timeout.
19. Prints `playable after N s`; returns a `Game`.

CLI wrapper prints nothing more. Exit 0 / 1.

Notes: the 300 s (`timeout`) is not exposed on the CLI. Only the **client** reaching `Running` is awaited, not the server.

#### 2.3.1 `prepare_instance` (`gatekit.py:312-336`) - instance mode only
1. `kept = <repo>\.state\backup\modsettings.lsx` (the player's own list if the default stand is currently up) else the real `Public\modsettings.lsx`.
2. `shared_loose` = keys of `<repo>\.state\loose.json` (default stand's hot-reload mods; skipped when linking Data\Mods into the clone).
3. `INSTANCE.sync_game(skip_mods=shared_loose, se_fork_active=sefork.RECORD.exists(), se_cache=%LOCALAPPDATA%\BG3ScriptExtender)` (see section 4).
4. `INSTANCE.sync_profile(REAL_PROFILE, "Gwynerva_GateKit.pak")` (section 4).
5. Copies `kept` to `<instance profile>\PlayerProfiles\Public\modsettings.lsx` (parent must exist: `sync_profile` creates `PlayerProfiles\Public`).
6. Saves: `--save S` and `<real Public>\Savegames\Story\S` exists -> copytree only that save; else if `--level` was **not** given (`all_saves`) -> copytree the entire real `Story` folder; with `--level` no saves are copied.
7. `STATE.mkdir`.
Errors (`InstanceError`, `OSError`) -> `StandError "instance NAME: <msg>\n(instances live in <root>; BG3_GATEKIT_INSTANCES names another folder - writable, on the game's drive)"`.

#### 2.3.2 `backup()` (default game only, `:216-229`)
If `.state\backup\` exists: return (an earlier `up` without `down` -> the old copy is the original). Else builds `.state\backup.tmp\` (old tmp removed) containing: copytree of `<profile>\PlayerProfiles\Public\Savegames\Story` -> `Story` (if exists); `modsettings.lsx` (copy2, **must exist** else FileNotFoundError traceback); `ScriptExtenderSettings.json` (if exists, from `<bin>`); then renames `backup.tmp` -> `backup`.

#### 2.3.3 `isolate_mods(keep)` (`:526-544`)
Reads `PlayerProfiles\Public\modsettings.lsx` (utf-8); removes every `<node id="ModuleShortDesc"> ... </node>` block (regex `[ \t]*<node id="ModuleShortDesc">.*?</node>\r?\n`, DOTALL) whose `id="Folder"` attribute value is not in `{"GustavX"} | keep`; raises `StandError "not in the mod list, enable them in the game once first: [...]"` if a wanted folder (including `GustavX`) is absent; rewrites file (utf-8). The bridge's node is also removed here (re-added next step). The original comes back from the backup on `down` (default game) or is irrelevant (instance).

#### 2.3.4 `enable_bridge()` (`:547-556`)
If the file text contains `b1d5c0de-7e57-4b1d-9e0a-6d6576646b69` -> nothing. Otherwise finds the last `</children>` (`last`), then the `</children>` before it (`mods_end`) (the closer of the `Mods` region's children); if none -> `StandError "modsettings.lsx does not look as expected; the bridge was not enabled"`. Inserts at the start of the line holding `mods_end` this exact block (24-space indent, `\n` line ends):

```xml
                        <node id="ModuleShortDesc">
                            <attribute id="Folder" type="LSString" value="Gwynerva_GateKit"/>
                            <attribute id="MD5" type="LSString" value=""/>
                            <attribute id="Name" type="LSString" value="GateKit Bridge"/>
                            <attribute id="PublishHandle" type="uint64" value="0"/>
                            <attribute id="UUID" type="guid" value="b1d5c0de-7e57-4b1d-9e0a-6d6576646b69"/>
                            <attribute id="Version64" type="int64" value="36028797018963968"/>
                        </node>
```
(`BRIDGE_NODE`, `:95-103`; bridge UUID `:94`.) The file is written utf-8.

#### 2.3.5 `lend_debug_profiles(with_saves)` (default game only, `:265-290`)
Only when `running_games()` (any BG3 process on the PC) is non-empty. Reason: a game started while another one runs uses `PlayerProfiles\Debug_Client_Profile_<n>` instead of `Public`. For `n=1..4`: if no record yet, the existing folder is moved to `.state\backup\debug-profiles\<name>` (or `<name>.absent` marker if none existed; `<name>.nocloud` marker if no Steam cloud mirror for it existed); then a fresh `PlayerProfiles\<name>\` is created with a copy of `modsettings.lsx` and, if `with_saves` and saves exist, `Savegames\Story` copy. Undone by `return_debug_profiles` in `restore()` (also deletes the cloud mirror folders `_SAVE_<name>`/`_PROFILE_<name>` if marked `.nocloud`).

#### 2.3.6 `skip_splash(on)` (`:433-444`)
`<game>\Data\Video\Splash_Logo_Larian.bk2`: on -> if absent, creates `Data\Video` (for an instance, first `own_in_instance`, replacing junctions on the path by real folders and checking the path is inside the instance) and writes a 0-byte file. Off (`down`) -> if present **and size 0**, delete it and remove `Data\Video` if empty. A non-empty file is never touched.

#### 2.3.7 `configure_extender(story_log)` (`:343-355`)
Reads `<bin>\ScriptExtenderSettings.json` (utf-8-sig; missing/invalid -> `{}`), deletes and recreates `<STATE>\se-logs\`, sets `CreateConsole=true`, `DeveloperMode=true`, `EnableLogging=<story_log>`, `LogDirectory=<STATE>\se-logs`, writes `json.dumps(indent=4)` as utf-8 (no BOM). (Original comes back from backup on `down` in the default game; instances keep their own file.)

### 2.4 `down`

Synopsis: `gatekit.py down`. (`down()`, `:1004-1010`)
1. `kill_game()`: `taskkill /F /PID` each pid whose exe folder equals this stand's `bin` (case-insensitive normalized); waits up to **20 s** (poll 0.5 s) until none; then `clear_crash_marker()` (rmtree `<profile>\ModCrashSanityCheck`).
2. Default game only: `restore()` (2.4.1).
3. `sefork.remove()` (section 3).
4. `skip_splash(False)`.
5. `unlink_all()` (2.10): removes loose Lua copies and deletes `.state\loose.json`.
Prints `instance NAME: game closed` or `game closed, saves and mod list restored`. Never raises a `StandError` itself (but `sefork.remove` can raise `RuntimeError` -> traceback if the dll stays locked 20 s).

#### 2.4.1 `restore()` (`:232-262`)
If `.state\backup\` missing -> return. Else:
- If backup has `Story`: if `SAVES` exists, it is **moved** to `.state\saves-after-run\<YYYYmmdd-HHMMSS>\` (keeps only the newest 3 folders, older rmtree'd); then `Story` copied back to `SAVES`. (If backup had no `Story`, the current SAVES are left alone.)
- `modsettings.lsx` copied back to `Public\modsettings.lsx`.
- Steam cloud mirrors (`<Steam>\userdata\*\1086940\remote`, where `<Steam>` = `SOURCE_GAME.parent.parent.parent`): if backup had `Story`, every directory in `_SAVE_Public\Savegames\Story` whose name is not among the backup's save names is rmtree'd; `_PROFILE_Public\modsettings.lsx` (if exists) is overwritten from the backup.
- `ScriptExtenderSettings.json` copied back into `<bin>` if present in the backup (note: if the original did not exist, the run's file stays).
- `return_debug_profiles(.state\backup\debug-profiles)`.
- `.state\backup\` rmtree'd.

### 2.5 `status`

Prints, in order (`:1185-1192`): `instance: <name>  (<root>)` or `instance: none (the installed game)`; `processes: [pids]`; `script extender: local build from <source>` or `script extender: stock` (from `sefork.status()`); then `client <heartbeat dict or None>` and `server <...>` using `Game.state(side, 1e9)` (so a stale heartbeat is shown, not hidden). Dict printed with Python `repr`. Exit 0.

### 2.6 `instances`

Prints one line per instance directory under the instances root that contains `game\bin` (`instances.names`, `instances.py:290`), sorted: `f"{name:16} {'running, pid [..]' | 'stopped':24} {root}"`, running = any BG3 process whose exe path is inside the instance (`Instance.inside`, realpath-based). Last line `(installed game)  running, pid [...]|stopped  <SOURCE_GAME>` where running means a process in `<SOURCE_GAME>\bin`. Works without an instance selected; with `BG3_GATEKIT_INSTANCE` set it still lists all.

### 2.7 `destroy`

Requires an instance (`StandError "`destroy` needs an instance: BG3_GATEKIT_INSTANCE or --instance <name>"`). `down()` then `INSTANCE.destroy()` (`instances.py:282`): `remove_tree(root)` (junction-safe: links removed as links, never followed), `remove_tree(state)`, remove the instances root folder if it became empty. Prints `instance NAME deleted`.

### 2.8 `exec`

Synopsis: `gatekit.py exec {client|server|ui} CODE`. `CODE` starting with `@` = path to a Lua file (read `utf-8-sig`, because PowerShell writes a BOM). Timeout fixed at 20 s (no option).

Calls `Game().lua(side, code, 20)` (protocol in section 6), prints log lines emitted by the chunk as `  [<side>] <line>` (to stdout, before the result), then the result as `json.dumps(result, ensure_ascii=False, indent=1)` (`null` for nil). Lua error -> stderr `ERROR: Lua error on <side>:\n<traceback>`, exit 1. No answer in 20 s -> `ERROR: no answer from <side> in 20 s (state: {...})`.
Side `ui` = the client side with the command flagged `"ui": true` (bridge runs it inside `Ext.UI.Defer`).
The code actually sent is prefixed with `local GK = Mods.GateKit.GK or Mods.GateKit.DK; local DK = GK\n` (`:662`) - the C# port must send the same preamble (it binds the bridge API table to `GK` for any chunk).

### 2.9 `shot`, `key`

- `shot OUT`: `Game.shot(OUT, width=1150)` -> runs `powershell -NoProfile -ExecutionPolicy Bypass -File stand\window.ps1 -Out <abs path> -Width 1150 [-ProcessId <first pid>]`. The script finds the visible top-level window whose title starts with `Baldur's Gate 3 (` (and pid if given), uses `PrintWindow(hwnd, dc, 2)` (works while the window is behind other windows; the window is not moved or focused), scales down to width 1150 only if the window is wider, saves JPEG quality 82; output text `"<w> x <h> -> <path>"`. Success is judged by the file existing; otherwise `StandError "screenshot failed: <script output>"`. Parent folder of OUT is created. Prints the absolute path. The CLI exposes no width option (the MCP tool does).
- `key NAME`: virtual-key code from `KEYS` (`escape 1B, enter 0D, space 20, tab 09, backspace 08, left 25, up 26, right 27, down 28, f1..f12 = 0x6F+n`) or the code point of a single upper-cased letter/digit; else `StandError "unknown key ...; known: ..., or one letter/digit"`. Runs `window.ps1 -PostKey <vk> [-ProcessId]` which posts `WM_KEYDOWN`, sleeps 80 ms, posts `WM_KEYUP` to the window (no focus change). Result must contain `posted` else `StandError "key not sent: <output>"`. Prints nothing on success.
  (`window.ps1` also has `-Key`/`-ClickX/-ClickY` foreground real-input modes that the CLI never calls; mouse clicks via PostMessage are ignored by the game.)

### 2.10 `link`, `reload`, `bridge-reload`, `watch` (hot reload)

State: `.state\loose.json` = `{ "<ModFolder>": "<absolute path of the mod's ...\ScriptExtender\Lua source>" }`.
Target: `<game>\Data\Mods\<ModFolder>\ScriptExtender\Lua` (loose Lua wins over the same files in a `.pak`; `Ext.Debug.Reset()` reloads). Only Lua is copied.

- `link MOD_ROOT` (`link()`, `:479`): `MOD_ROOT.resolve()`; finds `MOD_ROOT\Mods\*\ScriptExtender\Lua`; none -> `StandError "no Mods\<Folder>\ScriptExtender\Lua under <root>"`; updates `loose.json` (indent 1), calls `sync_loose()`; prints `linked: A, B`.
- `sync_loose()` (`:493`): for each entry: (instance: `own_in_instance(target.parent)` replaces any junction on the path with real folders and verifies the path is inside the instance) rmtree target (ignore errors) then `copytree(source, target)`.
- `reload` (`Game.reload`, `:853`, timeout 30 s fixed): needs linked mods (`StandError "nothing is linked; run `link <mod folder>` first"`); `sync_loose()`; remembers each side's heartbeat `boot`; sends `Ext.Debug.Reset()` to server then client **without waiting for an answer** (no `ui`); per side waits (poll 0.2 s) until the heartbeat `boot` differs, else `StandError "<side> Lua did not come back after the reset (a Lua error in the mod? see the Script Extender console)"`; then `lua(side, "return true")` round trip. Prints `reloaded in X.X s`.
- `bridge-reload`: `link(<repo>\stand\bridge)` (registers `Gwynerva_GateKit`) then the same reload; prints `bridge Lua reloaded in X.X s (linked: it also survives `reset`; `down` removes the link)`.
- `watch`: prints `watching <mods>` (or `watching nothing (run `link` first)`); every 0.5 s computes the newest mtime of any `*.lua` below the linked sources (`tree_stamp`); on change prints `HH:MM:SS reloaded in X.X s` or `HH:MM:SS reload failed: <msg>`. Ctrl+C ends with exit 0. Runs forever otherwise.

### 2.11 `logs`

`logs [--story] [--lines N=80] [--grep REGEX]`. Output written as UTF-8 bytes to stdout (`errors=replace`), joined with `\n`, trailing `\n`.
- default: `read_console` (`:375`): requires a running game (`StandError "the game is not running"`); spawns `python gatekit.py _console <pid>` (see 2.19) and returns the last N non-blank lines (regex, case-insensitive, applied per line) of the SE console window's screen buffer. Needs `CreateConsole=true` (written by `up`).
- `--story`: `read_story_log` (`:358`): concatenates **all** files in `<STATE>\se-logs\` ordered by mtime (read utf-8, `errors=replace`), non-blank lines filtered by `--grep`, last N. Empty when `up --story-log` was not used. Needs no running game.

### 2.12 `checkpoint`, `reset` (clean world without restart)

- `checkpoint` (`Game.checkpoint`, `:743`, timeout 40): `wait_state("Running","server",60)`; remembers all `PlayerProfiles\*\Savegames\Story\*` folders; calls `GK.AutoSave(true)` on both sides; server `Osi.AutoSave()`; polls every 0.5 s up to 40 s for a **new** save folder containing `*.lsv` (`StandError "the game made no save for the checkpoint"`); in `finally` `GK.AutoSave(false)` on both sides; sleeps 1.0 s; writes the folder name to `<STATE>\checkpoint.txt`; prints `checkpoint: <name>`.
- `reset` (`Game.reset`, `:768`, timeout 120, skip_intro True): name from the in-memory value or `checkpoint.txt` (`StandError "no checkpoint to return to; call game.checkpoint() first"`); records both `boot`s; sends `Osi.LoadGame("<name>")` to the server without waiting; for client then server waits (poll 0.3 s) until `boot` changed **and** `gameState == "Running"` (errors: `"the game closed during the reset"`, `"<side> did not come back from the reset in 120 s; last: ..."`), then a `return true` round trip; then `skip_scenes()` (client `GK.FastScenes(20)`; polls `GK.Scenes()` every 0.4 s up to 60 s until a scene that is not `gameplay` has appeared and ended or none appeared within 3 s; finally `GK.FastScenes(1)`). Prints `world reset in X.X s`.
Depends on bridge Lua functions `GK.AutoSave`, `GK.Scenes`, `GK.FastScenes`.

### 2.13 `test`

`test [FOLDER] [-k SUBSTR] [--clean]`; `FOLDER` default `<repo>\stand\scenarios`. `run_suite` (`:1049`):
- Loads every `*.py` (sorted) whose stem contains `-k` (if given) and which defines `run`. A scenario module may define `LEVEL`, `MODS` (list), `SAVE`, `CLEAN` (bool), `LAUNCH_ARGS` (ignored by `test`).
- Grouping key `(LEVEL, tuple(MODS or ()), MODS is None, SAVE)`; one game session per group: `up(level=..., mods=None if own_mods else list(mods), save=...)`. Start failure -> all scenarios of the group `FAIL (game did not start: ...)`.
- With `CLEAN` (or `--clean`) and no `SAVE`: one `checkpoint()` after start; before each such scenario (after the first) a `reset()`.
- Output folder `.state\runs\<ts>-suite\<scenario>\`; on `StandError/AssertionError` a `failure.jpg` screenshot and `console.txt` (last 200 lines) are attempted.
- Always `down()` at the end of each group. If the game dies mid group, remaining scenarios are `FAIL (not run: the game closed)`.
- Prints a table `PASS|FAIL  <name:24> <secs> s [verdict]` and `N passed, M failed, T s in all; output: <dir>`. Exit 1 if any failed, else 0. No scenario -> `ERROR: no scenarios in <folder>`.
Scenario contract: a Python file with `def run(game)` where `game` is the `Game` object (methods in section 6.4).
**Ported**: scenarios are Lua now, run on the host by an embedded interpreter (MoonSharp) in `src/GateKit.Scenarios`; the nine Python scenarios live as `scenarios/*.lua` (api, channel, console, dialogue, overlay, se_fork, se_fork_ui, smoke, stage_fight), joined by `deck` (that reading and driving a game never depends on its window being seen) and
`framecap` (that the engine's frame caps are still where GateKit thinks they are). The declaration at the top of a file (`scenario { level =, mods =, save =, clean = }`) takes the place of the module constants `LEVEL/MODS/SAVE/CLEAN`; `LAUNCH_ARGS` is not carried over (`run -- GAME_ARGS` does it). `src/GateKit.Scenarios/README.md` has the `game` API and why MoonSharp.

### 2.14 `run`

`run SCENARIO [--keep] [--attach] [launch options]` where launch options are those of `up` (`--level --save --mods --story-log -- GAME_ARGS`).
`run_scenario` (`:1013`): loads the module; creates `.state\runs\<YYYYmmdd-HHMMSS>-<stem>\` (non-`exist_ok`, `FileExistsError` possible within one second); `--attach` requires a running game (`StandError "--attach needs a running game (start one with `up`)"`) and implies `--keep`; otherwise `up(args or module.LAUNCH_ARGS, level=--level or module.LEVEL, mods=--mods if given else module.MODS, save=--save or module.SAVE)` (**`--story-log` is parsed but never passed** - dead option here). `game.out = <run folder>`; `module.run(game)`; prints `PASS <stem>  (output: <dir>)` or `FAIL <stem>: <msg>` (caught: `StandError`, `AssertionError`) -> exit 1. `finally: down()` unless keep. Other exceptions propagate (traceback) but `down()` still runs.

### 2.15 `_console PID` (hidden, `gatekit.py:1150,1220`; `_dump_console:391`)

Child process of `logs`: `FreeConsole`, `AttachConsole(pid)`, opens `CONOUT$`, reads rows `0..cursorY` with `ReadConsoleOutputCharacterW`, writes rows (right-stripped) joined by `\n` to stdout as UTF-8. Failure messages on stderr (`AttachConsole failed, error N (is CreateConsole on?)`), exit 1. Not a user command; the Linux/Proton layer needs a different source of the SE console text (SE can log to file; to be designed).

### 2.16 `stand/devkit.py`, `kit/devkit.py` (shims, effectively dead)

Both only re-run / re-export `gatekit.py` for the pre-rename name. Not to be ported (`stand/devkit.py:1-15`, `kit/devkit.py:1-10`).

---

## 3. Script Extender switcher (`stand/sefork.py`, own CLI)

Usage text (`sefork.py:8-10`): `python stand/sefork.py install <folder with BG3ScriptExtender.dll> | remove | status` (bad usage -> prints docstring, exit 2). Proposed: `gatekit se install FOLDER|remove|status`. `up`/`down` call the same code with the stand's `bin` (via `sefork.use`).

Idea: the stock loader `bin\DWrite.dll` loads `bin\BG3ScriptExtender.dll` instead of the cached signed build when `ScriptExtenderUpdaterConfig.json` contains `"DebugLoadSE": true`.

- `status()` (`:49`) -> `{fork: bool (DebugLoadSE true AND dll exists), dll, source, sha256, managed}` printed as JSON indent 1. `source` comes from `.state\sefork.json`; `managed` = record exists.
- `install(folder)` (`:69`): `folder` may be the dll file or a folder containing it (`FileNotFoundError "no BG3ScriptExtender.dll in <folder>"`). First install since last remove: refuses if `bin\BG3ScriptExtender.dll` exists and was not placed by us (`RuntimeError`); creates `.state`; copies an existing `ScriptExtenderUpdaterConfig.json` to `.state\sefork-updater-config.json`; writes `.state\sefork.json` `{"source","had_config"}`. Copies dll (and a sibling `.pdb`) to `bin`; writes the config = saved config (or `{}`) + `"DebugLoadSE": true` (indent 4). Game must not run (not checked).
- `remove()` (`:103`): no record -> status only. Else restores the saved config (or deletes the written one), then deletes dll and pdb retrying `PermissionError` for up to 20 s (else `RuntimeError` telling to re-run), deletes saved config and record. Returns status.
- `apply_for_run(se_settings)` (`:133`): see `up` step 13.

State files: `<STATE>\sefork.json`, `<STATE>\sefork-updater-config.json` (per instance in instance mode; module default is `<repo>\.state`, the CLI form without `use()` always works on the default game and `BG3_GAME`).

---

## 4. Instances (`stand/instances.py`)

Layout (docstring `instances.py:1-17`, code `:144-160`):
```
<instances root>\<name>\game\bin     copy of the real bin
<instances root>\<name>\game\Data    real folder; entries are links to the real Data
<instances root>\<name>\home         USERPROFILE of the game process
   home\AppData\Local\Larian Studios\Baldur's Gate 3     = PROFILE of the instance
<repo>\.state\instances\<name>       instance STATE (runs, logs, records, se-logs, loose.json ...)
```
`<instances root>` = `BG3_GATEKIT_INSTANCES` or `<SOURCE_GAME parent>\Baldurs Gate 3 GateKit`. Legacy: if `<root>\<name>` does not exist but `<SOURCE_GAME parent>\Baldurs Gate 3 DevKit\<name>` does (and the env var is unset) that old location is used (`:153-155`). Must be on the same drive as the game (hard links).

`sync_game(skip_mods, se_cache, se_fork_active)` (`:164`), run at every `up`:
1. Error `InstanceError "no game in <SOURCE_GAME>"` unless `bin\steam_appid.txt` exists or some `bg3*.exe`.
2. For every file under real `bin` (recursive): skip if first path component lowercased in `{db, bg3scriptextender.dll, bg3scriptextender.pdb, scriptextenderupdaterconfig.json}` or suffix `.log` or is a directory; `ScriptExtenderSettings.json` is copied only if the target does not exist; `steam_appid.txt` is *written* as the text `2147483646` (a non-BG3 app id, so Steam does not mirror the clone's saves into its cloud); every other file is `copy2`'d only when size or integer mtime differ (`_copy_if_changed`).
3. `ScriptExtenderUpdaterConfig.json` in the clone: when `se_cache` given and no fork active: copy of the real one (utf-8-sig) minus `DebugLoadSE`, plus `"CachePath": "%LOCALAPPDATA%\\BG3ScriptExtender"` (the real cache), written indent 4 only if different. (When a fork is active, `sefork.install` writes it.)
4. `Data`: for each entry of real `Data` except `Mods`, `Video`: directory -> NTFS junction to the real dir (an existing link to another target is replaced; a real folder is left); file -> hard link (replaced if it is a different file; failure -> `InstanceError "could not hard-link ... Instances must be on the game's drive; set BG3_GATEKIT_INSTANCES ..."`). `Data\Mods` and `Data\Video` are real folders in the clone, their entries linked the same way, except: names in `skip_mods` (inside `Mods`), and zero-byte files (the default game's splash stub).
`own_folder(path)` (`:215`): along `path` relative to `<clone>\Data`, any junction is `rmdir`'d and replaced by a real empty folder (used before loose files are written). `inside(path)` (`:228`): realpath prefix test against the instance root.

`sync_profile(real_profile, bridge_pak)` (`:241`):
- Creates `home\AppData\Local`, `...\Roaming`, `home\Documents`.
- Seeds once (only if absent in the clone) from the real profile: `graphicSettings.lsx`, `PlayerProfiles/playerprofiles8.lsf`, `PlayerProfiles/Public/{UILayout.lsx,config.lsf,profile8.lsf,analyticsUserData.lsf}`.
- For each of `Public`, `Debug_Client_Profile_1..4`: creates `PlayerProfiles\<n>` and **deletes** its `Savegames\Story`.
- `Mods`: copies (copy2 if changed) every `*.pak` of the real `Mods` except the bridge pak; deletes clone paks not in the real set (except the bridge pak). The bridge pak is built per instance.

`spread()` (`:271`): copies `Public\modsettings.lsx` into each `Debug_Client_Profile_n`, replaces their `Savegames\Story` with a copy of Public's.
`environment()` (`:235`): `os.environ` + `USERPROFILE=<home>`, `LOCALAPPDATA=<home>\AppData\Local`, `APPDATA=<home>\AppData\Roaming` (the game derives its profile folder from `USERPROFILE`).
`destroy()` (`:282`): see 2.7.

---

## 5. Toolkit commands (`stand/toolkit.py`)

Separate CLI, `python stand/toolkit.py <cmd>`; Windows-only (Larian's editor "Glasses.exe"). **Not instance aware** (always `<repo>\.state\toolkit`). Proposed: `gatekit toolkit <cmd>`. Errors: `ToolkitError` -> stderr `ERROR: msg`, exit 1.

Constants (`toolkit.py:29-38`): `TOOLKIT` = `BG3_TOOLKIT` (default Steam path above), `EXE = TOOLKIT\Glasses.exe`, channel `.state\toolkit\`, bridge source `stand\toolkit\Bridge.cs`, assembly `GateKit.Toolkit`, built to `.state\build\GateKit.Toolkit.dll`, installed (while running) as `TOOLKIT\GateKit.Toolkit.dll`, compiler `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`.

| Command | Behaviour |
|---|---|
| `build` | `csc /nologo /target:library /optimize+ /out:.state\build\GateKit.Toolkit.dll /r:System.Web.Extensions.dll stand\toolkit\Bridge.cs`; failure -> `ToolkitError "the bridge did not compile:\n..."`; prints `built <path>` |
| `up [--project NAME] [--level NAME] [--save FOLDER]` | refuse if `Glasses.exe` runs (`tasklist /FO CSV /NH /FI "IMAGENAME eq Glasses.exe"`); rebuild if DLL missing or older than `Bridge.cs`; copy DLL to the Toolkit folder; wipe and recreate `.state\toolkit\`; start `Glasses.exe [-project P] [-lvl L] [-savegameLvl S]` with cwd = Toolkit folder, env `APPDOMAIN_MANAGER_ASM="GateKit.Toolkit, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null"`, `APPDOMAIN_MANAGER_TYPE=GateKit.Manager`, `GATEKIT_TOOLKIT_CHANNEL=<.state\toolkit>`, detached. Waits: process appears 30 s; `state.ready` 120 s (prints `  X s  editor ready, bridge answering`); with `--project`: state `project == P` and an event `SessionLoaded` or `ClientGameIdle` (timeout 400 s; prints `... project P open`); with `--level`: `hasLevel` and `level == L` and event `LevelLoaded`/`LevelStarted` (400 s); with `--save`: `state.fromSave` (400 s). `ToolkitError "timed out after N s waiting for <what>; state: ..."` or `"the Toolkit closed while waiting for ..."` |
| `down` | `taskkill /F /IM Glasses.exe` (**every** Glasses process, not only ours), waits up to 20 s, deletes `TOOLKIT\GateKit.Toolkit.dll`; prints `Toolkit closed, bridge removed from its folder` |
| `state` | prints JSON `{"processes": [pids], "state": <state.json or null>}` (ignores freshness: max_age 1e9) |
| `play` | `eval "EditorFramework.FrameworkClass.Instance.Game.ResumeGame()"` |
| `edit` | `eval "EditorFramework.FrameworkClass.Instance.Game.PauseGame()"` |
| `eval EXPR` | single quotes in `EXPR` are replaced by double quotes (shell-quoting workaround); `evaluate(expr, timeout=60)`; prints JSON of the value (`ensure_ascii=False, indent=1`). Special expressions understood by the in-editor bridge: `Type.Member.Method("text",1,true)`, generic calls, `:members <expr>`, `:types <text>` |

Toolkit channel protocol (`evaluate`, `toolkit.py:75`): write `{"id","expr"}` to `cmd.tmp` then `os.replace` to `cmd.json`; poll `res.json` every 0.1 s for `{"id","ok","value"|"error"}`; error -> `ToolkitError`; process gone -> `"the Toolkit is not running"`; timeout -> `"no answer in N s (state: ...)"`. `state.json` fields used: `ready`, `project`, `level`, `hasLevel`, `fromSave`, `events` (list of `"<n> <Name>"`), `lastEvent`, `editorMode`, `gameUpdating`.

Dead code in `toolkit.py`: `open_project`, `load_level`, `settled`, `wait_event`, `mark`, `PROJECTS/LEVELS/GAME` consts partly used - `open_project` (`:186`), `load_level` (`:195`), `settled` (`:203`) are not called by the CLI or the MCP server (the comment at `:123-126` explains that opening is done by launch switches because calling them while starting hangs the editor at 97 %).

---

## 6. The bridge channel and `Game` object (the protocol the C# host must speak)

Folder: `<profile>\Script Extender\Gwynerva_GateKit\` (`CHANNEL`, `gatekit.py:85`). The in-game side is `stand/bridge/Mods/Gwynerva_GateKit/ScriptExtender/Lua/Shared/Bridge.lua` (SE `Ext.IO` paths are relative to `...\Script Extender\`, bridge uses `DIR = "Gwynerva_GateKit/"`). Schemas exist in the new repo under `schemas/bridge`.

### 6.1 Files

| File | Writer | Content |
|---|---|---|
| `state_<side>.json` | bridge, every 1000 ms (`BEAT_MS`) | `{"side","boot","gameState","queue":1,"done","time"}`. `boot` = `Ext.Utils.MonotonicTime()` at Lua state start (changes after `Ext.Debug.Reset()` / level load); `gameState` = `tostring(Ext.Utils.GetGameState())`; `done` = highest `seq` run; `time` = monotonic ms of the beat |
| `queue_<side>.jsonl` | host | one JSON object per line `{"id","code","ui","seq","t"}` |
| `queue_<side>.lock` | host | byte lock file (see below) |
| `res_<side>_<id>.json` | bridge | `{"id","side","ok","result"\|"error","log":[...]}`; deleted by the host after reading |
| `cmd_<side>.json`, `res_<side>.json`, `cmd_<side>_<id>.tmp` | legacy | used only when the bridge heartbeat lacks `queue` (old bridges before 2026-11-13) |

`<side>` is `client` or `server` (`ui` is the client side with `"ui": true`). The bridge polls the queue every 150 ms (`POLL_MS`), runs lines whose `seq` is greater than the last done in order; stops reading the queue at the first unparsable line (a line being written).

### 6.2 Host send algorithm (`Game._send`, `gatekit.py:655-698`)
- id = `"{pid}-{time_ns}-{counter}"`.
- code = `"local GK = Mods.GateKit.GK or Mods.GateKit.DK; local DK = GK\n" + code`.
- Legacy bridge (state exists but `queue` falsy): write `cmd_<side>_<id>.tmp`, `os.replace` onto `cmd_<side>.json` (retry on `PermissionError` 40 x 50 ms); result read from `res_<side>.json` matched by id.
- Queue: create folder; take the file lock on `queue_<side>.lock` (open `a+b`, lock 1 byte at offset 0 non-blocking, retry every 10 ms for 30 s, else `StandError "the command queue queue_<side>.jsonl stays locked"`); read current lines; `done` = `state_<side>.json.done` (read ignoring age); keep lines with `seq > done` and age (`t`) < 600 s; `seq = max(last_seen_seq + 1, time_ns // 1000)` (microseconds, monotonic across lost files, exact as Lua double); `t = time.time()`; if nothing was dropped, only append the new line (retry 40 x 50 ms on PermissionError), else rewrite the whole file; then delete `res_<side>_*.json` older than 600 s.
- Wait (`Game.lua`, `:700`): poll every 100 ms for `res_<side>_<id>.json` (decoded utf-8 with `errors=replace`), match `id`, unlink it, print `  [<side>] <line>` for each `log` entry, `ok == false` -> `StandError("Lua error on <side>:\n<error>")`; return `result`. Timeout -> `StandError("no answer from <side> in <t> s (state: <heartbeat>)")`. Default timeout 20 s.
- Heartbeat freshness (`Game.state`): file mtime within `max_age` (default 5 s) and valid JSON, else None.

### 6.3 What the bridge does with the chunk (`GK.Run`, `Bridge.lua:75`)
`load(code)`; syntax error -> `{ok:false,error:"syntax: ..."}`; runs under `xpcall` with traceback; result converted to plain JSON via `Ext.Json.Stringify` (options Beautify=false, StringifyInternalTypes, IterateUserdata, AvoidRecursion) then parsed back; non-serializable -> `tostring`. `GK.Log(...)` inside the chunk collects lines into `log`. `ui:true` -> executed in `Ext.UI.Defer`. At start the bridge calls `GK.AutoSave(false)` (autosaves off; `GK.AutoSave(true)` turns them on) and ignores commands already in the queue/`cmd` file from before.

### 6.4 `Game` object API (scenario / internal API; `gatekit.py:615-892`)
`state(side, max_age=5)`, `wait_state(wanted, side, timeout, since)`, `lua(side, code, timeout=20)`, `client()`, `server()`, `ui()` (`ui` wraps side `"ui"`), `checkpoint()`, `reset()`, `skip_scenes(appear=3, timeout=60)`, `fast_scenes(speed=20)` (`GK.FastScenes(<float>)` on client), `dialog()` (`ui`: `return GK.Dialog()`), `answer(n, timeout=15)` (ui `GK.Answer(n)`, polls 0.3 s until `GK.Dialog()` changes else `StandError "the dialogue did not change after answer N: ..."`), `wait(side, expression, timeout=30, every=0.3)` (polls `return (<expr>)` until truthy; `StandError "timed out waiting on <side> for: <expr>"`), `reload(timeout=30)`, `key(name)`, `shot(out, width=1150)`, attributes `channel`, `out`, `checkpoint_name`.

### 6.5 Process helpers
`running_games()`: all processes named `bg3_dx11.exe` or `bg3.exe` (`PROCESS_NAMES`, `:105`), any folder. `game_pids()`: those whose exe folder equals `EXE.parent` (normcase). Instance isolation relies on this: other instances' games are never seen, awaited or killed.

---

## 7. Offline data tools

### 7.1 `kit/gatekit.py` (offline queries over three SQLite DBs)

Usage `python kit/gatekit.py [--json] <command> ...`. `--json` accepted both globally and after the subcommand (`default=argparse.SUPPRESS` on subparsers so either position works). Databases (opened read-only: `file:<path>?mode=ro`): `index` (`BG3_INDEX_DB`, default `<repo>\index\gameindex.sqlite`), `symbols` (`BG3_SYMBOLS_DB`, default `<repo>\symbols\symbols.sqlite`), `decomp` (`<BG3_DECOMP_WORK or %LOCALAPPDATA%\GateKit\decomp>\decomp.sqlite`). A missing required DB -> `SystemExit("<path> does not exist; build it first (see the README of the <name> tool)")` (exit 1, message on stderr). Optional DBs are silently skipped.

Proposed: `gatekit data query <cmd>` (new group; verbs unchanged), or flat `gatekit search|about|scene|track|hook|sql|schema`. The same functions back the `bg3-offline` MCP server (see `mcp.md`).

| Command | Args | Behaviour / output (function) |
|---|---|---|
| `search TEXT...` | words joined by space; `--limit N` default 10 | `search(text, limit)` (`:77`): per kind up to `limit` rows by `LIKE '%text%'` (escaped), kinds: timeline, dialog, root template, placed object, stat, osiris, object, text (loca English), class, function, ecs type, layout, enum, enum value. Each row gets `kind` and `more` (the follow-up command string, e.g. `gatekit scene X`). Text mode: grouped by kind, `label[:110]  -> more`; `nothing found` on stderr if empty |
| `about WHAT...` | | `about()` (`:226`) routes: `binary+0xRVA` regex `^([\w.]+?)\+(?:0x)?([0-9a-fA-F]+)$` -> `about_address`; localization handle `h........g....g....g....g............` -> text/dialogs/objects; uuid -> `about_object`; contains `::` and symbols DB present -> `about_class` (returned only if exports/ecs/layout/type_name non-empty); index present -> `about_object` by key/name; else `{"kind":"search","query","results":search()}` |
| `scene NAME...` | | `scene()` (`:257`): exact timeline name (non-shadowed first) else unique LIKE candidate; if 0 or >=2 candidates -> `{"kind":"scene","error":"no exact match","candidates":[...]}`; otherwise dialog, actors, tracks, gear_changes, story_goals (FTS5 `texts_fts`) |
| `track NAME...` | | `track()` (`:295`) |
| `hook NAME...` | name part or `bg3.exe+0xRVA` | `hook()` (`:320`) |
| `sql DATABASE QUERY` | `DATABASE` in {decomp,index,symbols} (argparse `choices`); `--limit N` default 500 | `run_sql`: executes one statement (read-only connection), `fetchmany(limit)`; result `{columns, rows, truncated: len==limit}`; text mode prints header and tab-separated rows |
| `schema [--write]` | | `schema()` (`:360`): per DB, tables/views (excluding fts shadow tables) with columns, SQL comments (`-- ...`) as column meaning, row counts. `--write` writes `kit\SCHEMA.md` (utf-8, `\n`) and prints `wrote ...` on stderr |

Inconsistency: `--limit` is accepted by `about/scene/track/hook` but only `search` and `sql` use it.
Text output (`show`/`fmt`): nested dict/list pretty printer; ints in keys containing `rva` or named `offset`/`game_offset` shown as `0x...`; lists cut at 60 items with `... N more (use --json for everything)`. `--json`: `json.dump(result, indent=1, ensure_ascii=False, default=str)`.
Exit code 0 (SystemExit message -> 1).

### 7.2 `index/gameindex.py` (builds and queries the data index)

`python index/gameindex.py [--db PATH] <command>`; `--db` default `<repo>\index\gameindex.sqlite`. Proposed: `gatekit data <command>` (`gatekit data build` ...). Output is text to stdout (UTF-8, `errors=replace`). Query commands (all created by `q()`, `gameindex.py:2283`) share `--package P` and `--limit N` (default 100) in addition to their own. Missing DB -> `SystemExit("<db> does not exist; run:  python gameindex.py build")`.

`build` (`:1077`): options `--game` (BG3_GAME), `--divine` (BG3_DIVINE), `--dotnet DIR` (sets `DOTNET_ROOT` for Divine), `--work` (BG3_INDEX_WORK / `%LOCALAPPDATA%\GateKit\index-work`), `--languages L...` (default English), `--jobs N` (cpu-1), `--force`, `--all-keys`, `--scenes-only`, `--lsfdump PATH` (or BG3_LSFDUMP), `--no-lsfdump`, `--keep-lsf`, `--skip-unpack`, `--lsx-root DIR` (repeatable). Steps: (unless `--skip-unpack`) `unpack()` (`:943`): require `<game>\Data` and Divine; list `*.pak` in `Data`, `Data\Localization`, `Data\Localization\<lang>`; per package `Divine -a list-package` into `<work>\lists\<pak>.txt` (+ `.stamp` of `size:mtime:len(EXTRACT)`; reuse when fresh), extract `.loca` files to `<work>\loca\<pak>`, extract resource/text files (`\.(lsf|lsx|txt|lua|khn|div|anc|ann|anm|clc|cln|clm|itemscript|charscript|gamescript|xaml|json|xml)$`, or the scenes-only regex) into `<work>\lsx\<pak>` and convert `.lsf` either with native `lsfdump <lsx> <lsx> <jobs> delete` or Divine `convert-resources` in parallel; `.extracted` marker files for freshness; then read loose `Data\Mods\*\Story\RawFiles\story_header.div`; build `gameindex.building` with schema + FTS, then `os.replace` to the final DB (atomic); `ANALYZE`; prints size/seconds and the `info` output. Hours-long (Divine invoked as `Divine -g bg3 -l warn ...`).
Query commands: `info`, `survey [--all-files]`, `hidden-weapons|hidden-actors|hidden-armor [--name S] [--actor X] [--count]`, `template NAME [--gear]`, `timeline NAME`, `tracks TYPE [--hides]`, `text TEXT [--lang L] [--where]`, `dialog NAME [--tree] [--lang L=English]`, `chain NAME [--depth 5] [--fan 12]`, `voice TEXT... [--dialog] [--speaker] [--lang English]`, `check-mod PATH [--divine P] [--dotnet D] [--examples 5] [--json]`, `files [TEXT...] [--ext E] [--summary]`, `find TEXT... [--node N] [--show] [--full]`, `refs VALUE`, `stat NAME`, `osiris NAME [--kind K] [--uses]`, `grep TEXT [--kind K] [--raw]`, and `sql QUERY` (read-only connection, tab separated with header). 21 verbs in total (build, info, survey, 3 hidden-*, template, timeline, tracks, text, dialog, chain, voice, check-mod, files, find, refs, stat, osiris, grep, sql).

### 7.3 `symbols/symbols.py` (Toolkit/game binary symbol DB)

`python symbols/symbols.py [--db PATH] <command>`, DB default `<repo>\symbols\symbols.sqlite`. Windows only for `build` (undecorates names with the system `dbghelp.dll` via ctypes; uses `capstone` for xrefs, optional). Proposed: `gatekit symbols <command>`.
- `build [--toolkit DIR] [--recursive] [--game DIR] [--bg3se DIR] [--jobs N] [--exports-only] [--no-pseudo-code] [--check-matching]`: reads PE export/import tables of every `*.dll|*.exe` in the Toolkit folder, plus `<game>\bin\bg3.exe|bg3_dx11.exe`; builds tables (dlls, symbols, strings, type_names, vtables, matches, functions, calls, xrefs, ecs_ids, se_*, accessors, enums...); consumes the Script Extender source tree (patterns, layouts, enums) and `decomp.sqlite` pairs as seeds; writes `symbols.building` then replaces `symbols.sqlite`.
- Query verbs sharing `query()` options `--dll --kind --limit(200) --mangled --full`: `find TEXT+ [--anywhere]`, `classes TEXT+`, `class NAME`, `strings TEXT+`, `types TEXT+`, `game TEXT* [--unnamed]`, `vtable NAME`, `se TEXT* [--report]`, `ecs TEXT+ [--context C]`, `users TEXT+ [--per 25] [--callers [DEPTH]] [--per-callers 6]`, `system NAME`; plus `layout NAME`, `fields NAME`, `enum NAME` (`--limit 200`), `callers TEXT`, `callees TEXT`, `savemodel NAME`, `replication NAME [--code] [--dll]`, `signature TEXT [--limit 5]`, `export [--format json|ghidra|x64dbg|ida] [--dll] [--out FILE]`, `importers TEXT [--limit]`, `stats`. 23 commands in total (build + 22 query/export verbs; counted from `main()` `symbols.py:1913-2003`).

### 7.4 `symbols/decomp/decomp.py` (Ghidra pseudo-code DB)

`python symbols/decomp/decomp.py <command>`; work folder `BG3_DECOMP_WORK` (`%LOCALAPPDATA%\GateKit\decomp`), tools `BG3_DECOMP_TOOLS` (`...\GateKit\tools`: `ghidra_*` with `support\launch.bat`, `jdk-*`, `ilspycmd`). Proposed: `gatekit decomp <command>` (or `gatekit symbols decomp ...`). Commands (`decomp.py:1138-1206`): `run BINARY [--memory 20G] [--cpu N] [--range FIRST-LAST] [--fresh]` (Ghidra headless; copies the exe into `<work>\<name>\`, runs `AnalyzeHeadless` with scripts `ApplyNames.java` / `ExportDecomp.java` from `symbols/decomp/ghidra`, writes `functions.jsonl`, `ghidra.log`, `console.log`, `last_run.json`; may call `symbols.py export --format ghidra`; hours for `bg3.exe`), `load [BINARY]` (jsonl -> `decomp.sqlite`), `code NAME [--all --plain --grep T -C 3 --limit 40]`, `grep TEXT [--binary --lines 3 --raw --word --limit 50]`, `systems [NAME] [--rebuild] [--binary bg3.exe]`, `match [--binary --jobs --source]`, `matches TEXT [--all --limit 60]`, `fixedstrings [TEXT] [--rebuild --binary --limit]`, `vtable NAME`, `dotnet [--dotnet DIR] [--only S] [--timeout 900] [--fresh]` (ILSpy decompile of Toolkit managed assemblies, loads C# into the DB), `csharp TEXT [--file --lines --raw --limit]`, `status`. 12 commands. Exit = the command's return or 0. The Ghidra/JDK tooling is Windows-launcher based (`launch.bat`).

### 7.5 `stand/uicatalog.py`

`python stand/uicatalog.py <extract|build|show WIDGET>` (no argparse; any other input prints the docstring and exits 0). Proposed: `gatekit ui catalog extract|build|show`.
- `extract`: needs a running stand game (uses `se-fork/coverage/probe.attach()`, i.e. the **"coverage" instance via clean_stand**); for every `.xaml` path in `gameindex.sqlite` (`files.ext='xaml'`) runs `Ext.IO.LoadFile(<path>, "data")` on the client and writes it to `%LOCALAPPDATA%\GateKit\xaml\<path>`; prints `N of M XAML files written to <dir>`.
- `build`: parses the XAML under that folder; writes **two repo files**: `stand\bridge\Mods\Gwynerva_GateKit\ScriptExtender\Lua\Client\UICatalog.lua` (header says game version 4.1.1.7631656) and `docs\ui-catalog.md`; English texts resolved from `gameindex.sqlite` (`BG3_INDEX_DB`). Prints counts.
- `show WIDGET`: **also runs `build()` first (rewriting both repo files)**, then prints the widget entry JSON (cut to 20000 chars).
No XAML -> `sys.exit("no XAML files in ...: run `python stand/uicatalog.py extract` with a stand game up")`.

---

## 8. Agent CLI (`stand/situation.py`) and friends

`python stand/situation.py <command> [args]` (`situation.py:1322 main`); proposed `gatekit agent <command>`. Talks to the game through `gatekit.Game` (instance from `BG3_GATEKIT_INSTANCE`), calling bridge functions `GK.See.*`, `GK.Act.*`, `GK.Gui`... Output is compact text for language models. On any uncaught exception prints one line `error: <first line>` (+ `(the game did not answer in time ...)` when message contains `no answer`) and exits 1; no argument / `help` prints the module docstring, exit 0; unknown command prints docstring, exit 2.

Behaviour shared by all: `who=<id>` argument selects a party member first; ids are the first 8 uuid characters, remembered in `%LOCALAPPDATA%\GateKit\agent-ids-<GK_AGENT_STATE|instance|default>.json`; after `do wait wait-turn look status answer dialog roll resolve combat heard events read` the output gets appended `heard:` and `happened:` sections (cursors in `agent-heard-<x>.json`, `agent-events-<x>.json` - files named by replacing `agent-ids` in the ids file name); calls slower than 3 s are logged to `%LOCALAPPDATA%\GateKit\agent-slow-calls.log`; notes in `agent-notes-<x>.json`, narration log `agent-narration-<x>.md`; labelled saves in `%LOCALAPPDATA%\GateKit\agent-saves\<instance|default>\<slug>\{agent.json,game\<folder>}`.

Commands (37 words handled in `main`, plus `help`): `status, look [radius] [seen|known|all] [kinds=a,b] , combat [full], party, examine ID, actions [who] [target] [all], path ID|x,y,z, inventory [who], jobs, read ID, note TEXT | note edit N TEXT | note del N, notes [word], place, say LINE why= at=, narration, rules, markers, ways [range], watchers, exposure ID, contents ID, dialog, answer N, popup, resolve CHOICE|JSON, roll [cancel], journal, do COMMAND [args] (move cast attack jump shove hide dash disengage help throw use pickup loot take talk endturn turnbased sneak equip unequip stop select flee lockpick disarm), heard, events [all], wait-turn [secs], wait [secs], speed [scale|auto], save [label], saves, load LABEL [notes=restore], focus [on|off]`. `look ... all` is refused when `GK_AGENT_STRICT=1`. `rules` prints `docs\agent-rules.md`. Save/load write copies of game save folders under the profile `PlayerProfiles\<profile>\Savegames\Story\GK-<slug>__<slot>`.

Related scripts (not stand commands; one line each):
- `stand/agent_play.py`: wrapper allowing only a whitelist of situation commands (`""`, help, status, look, combat, party, inventory, events, markers, jobs, rules, say, narration, read, note, notes, place, examine, actions, path, ways, watchers, exposure, contents, dialog, answer, roll, popup, resolve, journal, heard, do, wait, wait-turn, speed, save, saves, load - note: `focus` is not on the list), sets `GK_AGENT_STRICT=1`; unknown -> `unknown command: X (run: <GK_AGENT_CLI|play> help)`, exit 2.
- `stand/agent_run.py`: builds an agent run folder `%LOCALAPPDATA%\GateKit\agent-runs\run-<time>` (BRIEF.md, play wrapper, wishes.md) from `stand/agent/wrapper.md`, tasks, addons; options `--task --addon --immersive --prompt --prompt-file --save --limit(250) --until --open --instance(default agent) --dir --print --run --model`; `--run` launches the `claude` CLI headless.
- `stand/ui.py`: windows from Python over `GK.Gui`: `list, open W, close W, close-all, read W [depth [path]], controls W [filter], click W CONTROL, input WIDGET EVENT, step, auto W, blocking, resolve, resolve-all, recover, check`.
- `stand/parallel_check.py`: proves two instances run side by side (`--names A B`, `--level`, `--keep`, `--no-suite`).
- `stand/situation_smoke.py`: read-only smoke test of every reading command (`GK_AGENT_STATE=smoke`).
- `stand/rename_gatekit.py`: one-off DevKit->GateKit rename (`--apply`, `--hold FILE`) - dead after the rename.
- `perf/*.py`: measurement tools (lua.py, startup_profile.py, graphics_bench.py, idle_cost.py, reload_bench.py, stack_sampler.py, ...). Out of scope; they load the stand via `startup_profile.load_stand`.

---

## 9. Coverage tools (`se-fork/coverage/*.py`, one line each)

All run on the instance "coverage" through `clean_stand.py` (except offline ones).
- `clean_stand.py [stand command...]`: same CLI as `gatekit.py`, default `BG3_GATEKIT_INSTANCE=coverage`; refuses `--instance` and the installed game; wraps `Instance.sync_profile` so the instance's `Mods` holds only the bridge pak; patches `Game._send` with a 40 x 50 ms PermissionError retry; before `up` kills leftover copies of the same instance via PowerShell `Get-CimInstance Win32_Process` and taskkill. Exit = `gatekit.main()`.
- `check.py`: scenario (`run(game)`) validating layouts in `layouts/` against the live game; env `COVERAGE_ONLY, COVERAGE_SITUATIONS, COVERAGE_LIBRARY, COVERAGE_FAST`; writes `work/check-results.json`, updates `coverage.json`.
- `collect.py`: scenario collecting registered components/systems into `coverage.json` and `docs/se-coverage.md`; run as a script it only rebuilds the report offline.
- `probe.py SIDE COMPONENT... [--max 16] [--json]`: raw memory view of live component instances; `attach()` is the shared "get the coverage game" helper.
- `seen.py start [label]|mark L|drain|summary|context C|show C`: library of seen components.
- `tour.py TOUR [steps] [limit]|--list|--auto`: plays tours from `tours/*.json`, env `COVERAGE_LEVEL, COVERAGE_SPEED, COVERAGE_RESUME`; may restart the game via `clean_stand.py`.
- `capture.py [components...] --names-file --situations --out --census`: samples short-lived components.
- `situations.py` (library, no CLI), `dialog_plan.py NAME | --rank TEXT [N]`, `todo.py [alive|waiting] [--json] [--prefix P]`, `levels_survey.py [LEVEL...]` (instance "survey", writes `levels.json`), `author_check.py SIDE ENGINE EXT FIELD,.. [--max N]`.
- Offline: `generate.py [--check]` (layouts -> SE header), `quarantine.py [rounds]`, `promote.py [folders]`, `offline_check.py [folders]`, `defaults_check.py [folders]`, `compare_macos.py`, `fieldmeta_check.py`, `apply_game_names.py TREE [--apply]`, `tags.py`, `tag_evidence.py`, `savefields.py NAME|--build|--stats`, `macos_symbols.py [--grep T|--demangle]`, `macos_index.py build|component|system|names`, `macos_defaults.py show|build`, `wiki.py find|page|ids|uid` (bg3.wiki reader; needs network), `graphics_bench.py` (forwards to `perf/graphics_bench.py`), `promote`.
Dead reference: `situations.py` docstring mentions `census.py` which does not exist. `se-fork/coverage/work/*.py` are throw-away scratch scripts with absolute paths on one machine (not part of the contract).

---

## 10. Paths and files (what the Linux/Proton platform layer must reproduce)

`P` = profile root = `%LOCALAPPDATA%\Larian Studios\Baldur's Gate 3` (instance: `<instance>\home\AppData\Local\Larian Studios\Baldur's Gate 3`). `G` = game folder (instance: `<instance>\game`). `B` = `G\bin`.

| Path | Who | What is done to it |
|---|---|---|
| `G\bin\bg3_dx11.exe` (`BG3_EXE`) | `up` | launched `--skip-launcher [args]`, cwd `B`; its folder defines which processes are "ours" |
| `G\Data\*.pak`, `Data\Localization\...` | index build | read only (Divine list/extract) |
| `G\Data\Mods\<Folder>\ScriptExtender\Lua\` | `link/reload/up` | created by rmtree+copytree from mod sources; removed on `down` (empty parents removed) |
| `G\Data\Mods\Gwynerva_DevKit\` | `retire_former_bridge` | deleted if present |
| `G\Data\Video\Splash_Logo_Larian.bk2` | `up/down` | 0-byte stub created / removed (only if still 0 bytes; `Video` removed if empty) |
| `G\Data\Mods\story_header.div` (via `Data\Mods\*\Story\RawFiles`) | index build | read |
| `B\ScriptExtenderSettings.json` | `up/down` | rewritten (CreateConsole, DeveloperMode, EnableLogging, LogDirectory, SendCrashReports=false for forks); backed up and restored (default game) |
| `B\ScriptExtenderUpdaterConfig.json` | `sefork` / `sync_game` | `DebugLoadSE=true` added/removed; saved to `.state\sefork-updater-config.json`; in instances `CachePath` set |
| `B\BG3ScriptExtender.dll/.pdb` | `sefork` | copied in for forks, deleted on remove |
| `B\DWrite.dll` (stock SE loader) | none | untouched; relied upon |
| `B\steam_appid.txt` | `sync_game` (instance) | content forced to `2147483646` |
| `%LOCALAPPDATA%\BG3ScriptExtender` | instance `CachePath` | read (SE build cache) |
| `P\Mods\*.pak` | `build`, `sync_profile` | `Gwynerva_GateKit.pak` written; in instances other paks copied from the real profile |
| `P\Mods\Gwynerva_DevKit.pak` | `retire_former_bridge` | deleted |
| `P\PlayerProfiles\Public\modsettings.lsx` | `up/down` | edited (isolate_mods, enable_bridge); backup/restore |
| `P\PlayerProfiles\Public\Savegames\Story\<save>` | `up/down/checkpoint/agent` | backup+restore (default); instance: copied in; checkpoint saves appear; agent `save/load` copy folders in/out |
| `P\PlayerProfiles\Debug_Client_Profile_1..4\` | `lend_debug_profiles`, `spread`, `sync_profile` | parked/replaced/copied (see 2.3.5, section 4) |
| `P\PlayerProfiles\{playerprofiles8.lsf,Public\{UILayout.lsx,config.lsf,profile8.lsf,analyticsUserData.lsf}}`, `P\graphicSettings.lsx` | `sync_profile` | read from real profile, seeded once into instances |
| `P\ModCrashSanityCheck\` | `clear_crash_marker` | rmtree before every start and after kill |
| `P\Script Extender\Gwynerva_GateKit\` | host+bridge | the channel (section 6); also the mods' saved UI settings live here; old `Gwynerva_DevKit` folder merged in |
| `<Steam>\userdata\*\1086940\remote\_SAVE_Public\Savegames\Story\*`, `..._PROFILE_Public\modsettings.lsx`, `_SAVE_/_PROFILE_Debug_Client_Profile_n` | `restore/return_debug_profiles` | surplus saves deleted, modsettings overwritten, mirrors of debug profiles deleted (Steam cloud mirror of a **non-instance** game) |
| `<repo>\stand\bridge\...` | `build`, `bridge-reload`, `bg3_api` | read (pack to pak; Lua copied; API scanned) |
| `<repo>\.state\backup\` (`Story\`, `modsettings.lsx`, `ScriptExtenderSettings.json`, `debug-profiles\`) | `backup/restore` | created/consumed |
| `.state\saves-after-run\<ts>\` | `restore` | last 3 kept |
| `.state\build\Gwynerva_GateKit.pak`, `.state\build\GateKit.Toolkit.dll` | build | written |
| `.state\loose.json` | link | hot-reload registry |
| `.state\se-logs\` | `up --story-log` | wiped at each `up`; SE writes the Osiris trace here |
| `.state\checkpoint.txt` | checkpoint | save folder name |
| `.state\runs\<ts>-<name>\...` | run/test | per run output (`failure.jpg`, `console.txt`) |
| `.state\shots\mcp-HHMMSS.jpg` | MCP `bg3_screenshot` | written |
| `.state\sefork.json`, `.state\sefork-updater-config.json` | sefork | install record |
| `.state\toolkit\{cmd.json,cmd.tmp,res.json,state.json}` | toolkit | channel |
| `.state\instances\<name>\` | instance | STATE of an instance (all the above inside it, incl. its own `loose.json`, `backup` unused) |
| `<instances root>\<name>\{game,home}` | instances | created/updated/destroyed |
| `<Toolkit>\Glasses.exe`, `<Toolkit>\GateKit.Toolkit.dll` | toolkit | launched / copied in and removed |
| `<TOOLS>\lslib\Packed\Tools\Divine.exe`, `<TOOLS>\dotnet\` | build, index | executed |
| `%LOCALAPPDATA%\GateKit\{decomp,index-work,tools,xaml,venv,agent-*}` | data tools | work/cache folders (legacy name `GwynervaDevKit` honoured when only it exists) |
| `<repo>\index\gameindex.sqlite`, `<repo>\symbols\symbols.sqlite` | data tools | built/read |
| `<repo>\stand\bridge\...\Client\UICatalog.lua`, `<repo>\docs\ui-catalog.md` | uicatalog | generated |
| `stand\window.ps1`, `stand\uia.ps1` | `shot`, `key` | PowerShell helper scripts (`uia.ps1` is not referenced by the Python in scope) |

Platform notes for Proton: the same trees exist inside the Wine prefix (`drive_c/users/steamuser/AppData/Local/Larian Studios/Baldur's Gate 3`); hard links/junctions become symlinks/bind-style links; "process pid by exe folder" needs `/proc/*/exe` (Wine shows `.../bg3_dx11.exe` as `wine64-preloader`, so match by command line/`WINEPREFIX`); screenshot/key delivery need a different mechanism; SE console needs the log file.

---

## 11. Machine-specific defaults (hardcoded)

| Value | File:line |
|---|---|
| `C:\Program Files (x86)\Steam\steamapps\common\Baldurs Gate 3` (game; env `BG3_GAME`) | `stand/gatekit.py:55`, `stand/sefork.py:21`, `index/gameindex.py:51`, `symbols/symbols.py:46`, `symbols/decomp/decomp.py:39` |
| `C:\Program Files (x86)\Steam\steamapps\common\Baldurs Gate 3 Toolkit` (env `BG3_TOOLKIT`) | `stand/toolkit.py:31`, `symbols/symbols.py:52`, `symbols/decomp/decomp.py:40` |
| a tools folder on the machine it ran on (Divine/.NET/Ghidra root; env `BG3_GATEKIT_TOOLS`) | `stand/gatekit.py:90`, `symbols/symbols.py:48` (also derived Divine path `lslib\Packed\Tools\Divine.exe` `gatekit.py:91`) |
| `C:\Windows` + `Microsoft.NET\Framework64\v4.0.30319\csc.exe` | `stand/toolkit.py:38` |
| Steam app id `1086940` (cloud folder) | `stand/gatekit.py:213` |
| fake app id `2147483646` | `stand/instances.py:44` |
| `<game parent>\Baldurs Gate 3 GateKit` and legacy `Baldurs Gate 3 DevKit` | `stand/instances.py:141,153` |
| `%LOCALAPPDATA%\Larian Studios\Baldur's Gate 3` (profile) | `stand/gatekit.py:56` |
| `%LOCALAPPDATA%\BG3ScriptExtender` | `stand/gatekit.py:325` |
| `%LOCALAPPDATA%\GateKit` (+ legacy `GwynervaDevKit`) | `kit/gatekit.py:26-30`, `gameindex.py:43-47`, `symbols.py:39-43`, `decomp.py:30-34`, `uicatalog.py:42`, `situation.py:84,107,750`, `agent_run.py:54`, `macos_symbols.py:131` |
| `bg3_dx11.exe` (`BG3_EXE`), process names `bg3_dx11.exe`, `bg3.exe` | `gatekit.py:81,105` |
| window title prefix `Baldur's Gate 3 (` (Toolkit: `Glasses `) | `stand/window.ps1` param `-Title` |
| bridge folder `Gwynerva_GateKit`, uuid `b1d5c0de-7e57-4b1d-9e0a-6d6576646b69`, former `Gwynerva_DevKit` | `gatekit.py:93,94,897` |
| `the old repository\...` examples in docstrings and `.mcp.json`-style registration text | `stand/mcp_server.py:6`, `kit/README.md:52`, many `se-fork/coverage` docstrings and `work/*.py` |
| `se-fork\builds\v34` default Script Extender build | `stand/gatekit.py:79` |
| `bg3se` build path `E:\...\se-fork\bg3se\x64\Game Release` | docstrings only (`clean_stand.py`, `collect.py`) |
| Game version string `4.1.1.7631656` | `stand/uicatalog.py` header string (generated Lua) |
| Bridge `Config.json` `"RequiredVersion": 33` | `stand/bridge/Mods/Gwynerva_GateKit/ScriptExtender/Config.json` (SE v34 fork is the default; value looks stale) |

---

## 12. Things that look dead, inconsistent or surprising

1. `run --story-log` is accepted but never forwarded (`gatekit.py:1252` vs `run_scenario`).
2. `stand/devkit.py`, `kit/devkit.py`, `stand/rename_gatekit.py`, `toolkit.open_project/load_level/settled`, `se-fork/coverage/graphics_bench.py` (forwarder), reference to missing `census.py` are dead or legacy.
3. `uicatalog show` rewrites two repository files (side effect on a "read" command).
4. `--instance` is parsed outside argparse and only for `__main__`; importing modules rely on the environment variable (so the MCP server cannot take `--instance`).
5. `kit` CLI `--limit` is accepted but ignored by most commands.
6. `backup()` crashes with a traceback (not `StandError`) if `modsettings.lsx` is missing; same for several `OSError`s; `destroy` can leave a traceback.
7. `window.ps1`'s foreground modes (`-Key`, `-Click*`) and `stand/uia.ps1` are unused by the Python CLI.
8. `agent_play.py`'s whitelist omits `focus`, which situation.py supports.
9. `down` for the default game kills only processes of its own `bin` folder, but `toolkit down` kills every `Glasses.exe`.
10. `restore()` leaves a run's `ScriptExtenderSettings.json` in place if the original did not exist.

---

## 13. Proposed unified command tree (Proposed, not existing behaviour)

```
gatekit [--instance NAME] <command>          # --instance becomes a real global option; env BG3_GATEKIT_INSTANCE stays
  build | up | down | status | instances | destroy        # stand verbs, unchanged
  exec | shot | key | logs | link | reload | watch         # unchanged
  checkpoint | reset | test | run | bridge-reload           # unchanged (bridge-reload also as `bridge reload`)
  se install|remove|status                                 # stand/sefork.py
  toolkit build|up|eval|state|play|edit|down               # stand/toolkit.py
  agent <situation.py commands>                            # stand/situation.py (+ `agent run` = agent_run.py, `agent play` = whitelist wrapper)
  ui <list|open|close|read|controls|click|...>             # stand/ui.py ; `ui catalog extract|build|show` = uicatalog.py
  data build|info|survey|...|sql                           # index/gameindex.py verbs
  data query search|about|scene|track|hook|sql|schema      # kit/gatekit.py (both `sql` forms stay distinguishable)
  symbols build|find|class|...|stats                       # symbols/symbols.py
  decomp run|load|code|grep|...|status                     # symbols/decomp/decomp.py
  coverage <tool>                                          # se-fork/coverage entry points (low priority)
  mcp serve [--offline]                                    # stand/mcp_server.py, kit/mcp_server.py
```
Drop on purpose: `_console` (replaced by the platform layer), the two `devkit.py` shims, `rename_gatekit.py`, `run --story-log` dead flag (or wire it through).

---

## 14. The C# stand (`gatekit`, implemented)

`src/GateKit.Platform` (OS layer), `src/GateKit.Stand` (the stand), `src/GateKit.Cli` (assembly `gatekit`). Commands: `build up down status instances [list|create NAME|destroy NAME] destroy exec shot key sound [list|set|mute|unmute|unfocused] link reload watch bridge-reload bridge reload logs checkpoint reset se status|install|remove`, global `--instance NAME`. `deck` and `window [list|maximize|grid|label|release|mirror|adopt|fps]` come from `GateKit.Deck` (14.4). `test` and `run` (scenarios) come from `GateKit.Scenarios`, which the CLI finds by its `CreateCommand()` classes.

### 14.1 Settings

Each value: environment variable, else `gatekit.json`, else a default found on the machine. No machine paths are built in.

| Setting | Environment | gatekit.json | Default |
|---|---|---|---|
| game folder | `BG3_GAME` | `game` | Baldur's Gate 3 in a Steam library (Windows: registry `SteamPath`; Linux: `~/.steam/steam`, `~/.local/share/Steam`, Flatpak, Snap; then `steamapps/libraryfolders.vdf` and `appmanifest_1086940.acf`) |
| executable | `GATEKIT_EXE` (`BG3_EXE` still read) | `exe` | `bg3_dx11.exe` |
| real profile | `GATEKIT_PROFILE` | `profile` | Windows `%LOCALAPPDATA%\Larian Studios\Baldur's Gate 3`; Linux `<library>/steamapps/compatdata/1086940/pfx/drive_c/users/steamuser/AppData/Local/Larian Studios/Baldur's Gate 3` |
| instances | `GATEKIT_INSTANCES` | `instances` | `<game's parent>/Baldurs Gate 3 GateKit` (shared with the old stand) |
| state folder | `GATEKIT_STATE` | `state` | `<repo>/.state` when run from the repository, else `<local app data>/GateKit/state` |
| Script Extender | `GATEKIT_SE` | `scriptExtender` | stock; a folder with `BG3ScriptExtender.dll` (or `stock`) |
| packer | `GATEKIT_PACKER` | `packer` | `builtin`: LSLib in this process. `divine` uses LSLib's Divine instead |
| Divine | `GATEKIT_DIVINE` | `divine` | none; only `GATEKIT_PACKER=divine` needs it |
| runtime for Divine | `GATEKIT_DOTNET_ROOT` | `dotnetRoot` | none: Divine runs with `DOTNET_ROLL_FORWARD=Major` on the newest installed runtime |
| start timeout | `GATEKIT_START_TIMEOUT` | `startTimeout` | 75 s to the first heartbeat |
| bridge mods | `GATEKIT_BRIDGE` | `bridge` | `<repo>/mods` |
| instance | `GATEKIT_INSTANCE` or `--instance` | `instance` | none (the installed game) |
| Proton (Linux) | `GATEKIT_PROTON` | - | `umu-run` on PATH, else the newest `steamapps/common/Proton*/proton` |

Every `GATEKIT_X` may also be spelled `BG3_GATEKIT_X` or `BG3_DEVKIT_X` (the old names; the new one wins). `gatekit.json` = `GATEKIT_CONFIG`, else the first `gatekit.json` in the working folder or a parent, else `<user config folder>/GateKit/gatekit.json` (comments and trailing commas allowed).

### 14.2 Deliberate differences from the Python stand

1. **Two bridge mods.** The bridge is now `mods/GateKit` + `mods/Weave`. The stand reads each mod's identity and dependencies from its `meta.lsx`, packs each into `<Folder>.pak`, installs both, enables both in the mod list in dependency order (Weave first; if only some are listed they are taken out and appended again), keeps both out of an instance's copied paks, and `bridge-reload` links both. A single mod root (the old `stand/bridge`) still works as `GATEKIT_BRIDGE`.
2. **Script Extender default is stock**: the old repository's `se-fork/builds/v34` does not exist here; set `GATEKIT_SE` (Weave and the bridge need GateKit's v34-based build).
3. **Packing**: `IModPacker`, by default `LSLibPacker` — LSLib in this process (the submodule `external/lslib`, compiled by `external/lslib-core`), writing a version 18 package compressed with LZ4 at its fast level, the same thing Divine wrote for `-g bg3 -c lz4` and what the game's own packages use. Nothing has to be installed and nothing is native, so `build` works wherever the stand does. A file or folder whose name starts with `.` is left out, as Divine left it out, but only when the name is inside the mod root: a repository under `.claude/worktrees` packs normally. A source with no files in it is an error rather than an empty package. `GATEKIT_PACKER=divine` picks `DivinePacker` instead (LSLib's Divine as a process, `GATEKIT_DIVINE`), kept for anyone who would rather use their own copy; it packs such a source from a temp copy, because Divine itself writes an empty package there, and a package of 64 bytes or less is an error.
4. **Prelude** sent with every chunk is `local GK = Mods.GateKit.GK` (no `DK` alias); the legacy single-file protocol (0) is not spoken.
5. **Window and console**: the game window is found by process id and class `SDL_app` (title prefix as fallback), never the Script Extender console. `up` writes `CreateConsole=false` and `LogRuntime=true`, so a game of the stand's has no console window at all and `logs` reads `<STATE>\se-logs\Extender Runtime *.log` (`logs --story` reads `Osiris Runtime *.log` from the same folder, `up --story-log`). A game started another way has no such file, and is read from its console window by a short-lived copy of `gatekit` (`gatekit __read-console PID`, attached with `AttachConsole`), replacing `_console`. No PowerShell anywhere.
6. **Options added**: `exec --timeout S` (default 20), `shot --width N` (default 1150; `.png` writes PNG, else JPEG quality 82), `reset --keep-scenes`, `instances create NAME` (clone and profile without a start), `instances destroy NAME`, `bridge reload` (alias), `se status|install|remove`.
7. **Output**: `status` adds `platform:` and prints each heartbeat as `{gameState, boot, done, queue, age}` instead of a Python dict. Redirected output is UTF-8.
8. **Errors**: a missing `modsettings.lsx` at backup, instance file errors and packing failures are `ERROR:` lines (exit 1), not tracebacks.
9. **Not carried over**: the legacy instance location `Baldurs Gate 3 DevKit`, the `devkit.py` shims, `_console`.
10. **A start never takes the foreground**: the game is started shown-but-not-activated, and while `up` runs the platform watches the foreground; a game window that grabs it is put at the bottom of the pile and the window the user was typing in gets the keyboard back. The watch stops as soon as the user activates something that is not a game, so it never fights them. On Linux it does nothing yet (the window manager decides).
11. **Linux (Proton)**: paths, prefixes, launch (`umu-run` or `proton run`, `WINEDLLOVERRIDES="DWrite=n,b"`, `setsid`, no inherited stdio), processes (`/proc/*/cmdline`, Wine's `Z:\` paths mapped back), hard links (`link(2)`) and folder symlinks are implemented. An instance gets its own prefix in `<instance>/prefix`. `key` and `shot` work everywhere through the bridge (points 12 and 13); the console reader says it is not available; `logs --story` works. Untested on a Linux machine so far.
12. **`key` goes through the game, not the window.** The name is still validated against `GameKeys` (the same names and virtual-key codes as before), but when the client side of the bridge is answering the key is pressed by `GK.Key(<name>)`, which injects an SDL key event into the game's own input queue. That needs no window, no focus and no operating-system call, so it is the same on every platform. Only when the bridge is not there does the stand fall back to `IGamePlatform.PostKey` (Windows `PostMessage`); where neither is available the error names both.
13. **`shot` goes through the game, not the window.** When the client side of the bridge is
    answering, `GK.Shot("Gwynerva_GateKit/shot_<pid>_<ticks>.png")` asks GateKit's Script
    Extender build for the frame the renderer is about to show; the extender copies it out of the
    swap chain before it draws any window of its own, encodes a PNG on a thread of its own and
    renames it into the channel folder. The stand waits up to 10 s for that file, reads it,
    deletes it, and then scales and saves it exactly as before (`--width`, `.png` or JPEG 82).
    The picture is the game alone — no extender overlay, no console — and the game is neither
    paused nor moved to take it. The window is photographed with `IGamePlatform.CaptureWindow`
    only when the bridge is not there or its renderer cannot read frames (so far only the DX11
    backend can; on the Vulkan executable `GK.Shot` returns false and the window is used). It
    costs about 1.5 s against the window path's 0.3 s, nearly all of it the full-size PNG, and
    none of it on the thread the game renders with.

### 14.2.1 `sound` (new, has no Python counterpart)

`gatekit [--instance NAME] sound [list]` prints the game's twelve sound categories, one per line -
GateKit's short name, the game's own `ConfigKey`, the volume from 0 to 100, `(muted)` where
`sound mute` is holding one at 0 - and last the state of the game's own mute-when-not-in-front
flag. `RumbleVolume` has no volume (`-`): it is controller vibration, not sound.

| Command | What it does |
|---|---|
| `sound set CATEGORY VOLUME` | one category to 0..100 (a trailing `%` is allowed). Prints what was set. |
| `sound mute [CATEGORY]` | to 0, remembering what it was; without a category the master, which silences the copy. Prints the volume remembered. |
| `sound unmute [CATEGORY]` | puts back what `mute` took away; without a category every category this instance muted. Prints the keys restored. |
| `sound unfocused [on\|off]` | the game's own `MuteSoundWhenNotFocused`; without a word it is only read. |

A category is the game's key (`MusicVolume`), GateKit's short name (`music`) or the Wwise RTPC
name (`RTPC_Volume_Music`), and nothing else: anything with a character that is not a letter, a
digit or `_` is refused before a chunk is built. The twelve keys, in the options screen's order:
`MasterVolume` (master), `FxVolume` (fx), `AmbientVolume` (ambient), `MusicVolume` (music),
`MusicFightVolume` (fight), `CutsceneVolume` (cutscene), `UIVolume` (ui), `VoiceVolume` (voice),
`VoiceVolumeDialog` (dialogue), `VoiceVolumeNarrator` (narrator), `VoiceVolumeOverhead`
(overhead), `RumbleVolume` (rumble).

Everything runs on the client through the bridge (`GK.Sound`, `docs/contracts/lua-api.md`), so it
needs no audio API of the operating system and is the same on every platform. It changes the
instance's running game only: the game does not publish the slider values behind its options
screen, so GateKit writes the Wwise values underneath them and never the player's own settings.
The options screen therefore keeps showing the player's positions, and nothing is written to a
profile. A volume set this way is lost when the game restarts, and a `reset` leaves it as it is.

### 14.3 Prebuilt databases: `export-release` and `install`

The index and the symbol database can be fetched instead of built. Building stays exactly as it
was and stays first-class; these are the alternative. Implemented in `src/GateKit.Release` and
added to both command groups, so the two databases behave identically. See
[docs/design/what-we-publish.md](../design/what-we-publish.md) and `publish/map/README.md`.

| Command | What it does |
|---|---|
| `gatekit data export-release --out DIR` | compresses the index (Zstandard) into `DIR/index.sqlite.zst`, adding its entry to `DIR/manifest.json` |
| `gatekit symbols export-release --out DIR` | the same for `symbols.sqlite.zst` |
| `gatekit data install` | fetches the index from the pinned release, checks every sha256, unpacks it to `--db` |
| `gatekit symbols install` | the same for the symbol database |

- `export-release` takes `--level` (Zstandard level, default 3), `--workers` (compression threads,
  0 for one) and `--part-mb` (default 1900). A database larger than one part is written as one
  Zstandard stream across `NAME.zst.001`, `NAME.zst.002`, …; the manifest lists them in order and
  `install` joins them back. GitHub refuses a release asset over 2 GB, which is what the default
  part size is for.
- `manifest.json` (`manifestVersion` 1) holds, per database: `database`, `fileName`, `size` and
  `sha256` of the unpacked file, `compression`, `compressedSize`, `parts` (name, size, sha256),
  `source` (the game or Toolkit build it was read from), `schemaVersion` (a digest of the
  database's own schema), `created`, and the database's own `meta` table with anything naming a
  machine left out. A manifest of a later version is refused rather than half read.
- `install` takes `--force` (replace a database that is already there) and `--from` (a folder, a
  `file://` URL or an `http(s)://` base URL instead of the pinned release). It checks each part's
  size and sha256 before unpacking, and the sha256 of the finished database before moving it into
  place; nothing is left behind by an install that fails a check. It prints where the data came
  from, that it is Larian's material rearranged, and the withdrawal promise.
- The pin is `index-release.json` at the repository root, shaped like `se-release.json`:
  `repository`, `tag`, `manifest`, optional `baseUrl`. With no `tag` — today's state, since
  nothing has been published — both commands refuse and name the build command instead.
- An installed database gets a receipt beside it (`<db>.release.json`). `gatekit doctor` reports
  each database as present or missing and says whether it was built here or installed, and from
  where.

### 14.4 Measured on Windows (instance `port`, `--level GustavX:TUT_Avernus_C`, SE v34 build, 2026-11-13)

| Step | Python stand | `gatekit` |
|---|---|---|
| `up`, clone already there | 40.7 s (playable 40 s) | 41.8 s (playable 41 s; two mods) |
| `up`, first (clone made) | - | 72.3 s |
| `exec server` | 0.26 s | 0.30 s |
| `checkpoint` | 4.0 s | 4.0 s |
| `reset` (with the intro skipped) | 13.6 s | 14.0 s |
| `shot` | 1.0 s | 0.33 s |
| `key` | 0.78 s | 0.28 s |
| `logs` | 0.32 s | 0.25 s |
| `down` | 1.4 s | 0.7-1.2 s |
| `build` (both mods) | - | 1.0 s |
| `bridge-reload` | - | 0.8 s |

### 14.4 The deck (`src/GateKit.Deck`, `docs/design/deck.md`)

| Command | What it does |
|---|---|
| `deck [--all]` | opens the window that holds every running game, and serves it until it closes. **It takes hold only of the copies this stand started**; anything else is listed by `window list` as "not this stand's", said once in the deck's own output, and left entirely alone - window, frame rate and sound. `--all` (or `GATEKIT_DECK_ADOPT=all` / `"deckAdopt"`) takes every copy on the machine, and naming a copy with `window maximize|mirror|adopt` takes that one, which the deck also says out loud; `window release NAME` gives it back and takes the name off again. One per machine; a second refuses to start. Windows only (`IGamePlatform.CanHostWindows`); elsewhere it says why not. The window never takes the foreground: `WS_EX_NOACTIVATE`, `SW_SHOWNOACTIVATE`, nothing activating it, and the style dropped only when the user clicks a tile. It wears `assets/brand/logo-round.ico`. |
| `deck close` | closes a running deck; every window goes back to the desktop and no game is touched. |
| `window list [--json]` | every running copy: instance, label, process id, whether this stand started it (`mine`), its tile; plus the tiles kept for a game that is away. Works without a deck. |
| `window maximize NAME` | that copy fills the deck and keeps its place while it restarts. |
| `window grid` | everything back in the grid. |
| `window label NAME [TEXT]` | names a copy (no text takes the name away). Kept in the state folder, so it survives a deck's restart and can be set before the game exists. Works without a deck. |
| `window release NAME` | the copy gets its own window back and leaves the deck, and is left alone afterwards (a deck that took it again next tick would make this useless). `window maximize NAME` asks for it back. |
| `window mirror NAME` | that tile is drawn from the frames the copy shares on the graphics card instead of from its window, and its window is put out of sight (`GATEKIT_DECK_CONCEAL`). Needs GateKit's Script Extender build and the DirectX 11 executable; a copy that cannot share says so and keeps its own window. |
| `window adopt NAME` | that tile takes the copy's own window again, and the window comes back where it was. |
| `window fps NAME [N]` | how fast one copy may draw, in front or not; `0` is no cap, which also lifts the throttle the engine puts a background copy under. With no number it says what that copy's caps are. Needs no deck (it goes through the bridge), but a deck's own budget overrules it on its next change of layout. Needs GateKit's Script Extender build. |

The deck and the command line find each other through `<STATE>\deck\`: `deck.json` (the deck's pid
and window, rewritten while it lives; a record older than 10 s is nobody), `tiles.json` (what is
where), `labels.json` (names people gave), `requests\*.json` (what `window ...` asks of a deck,
taken and deleted on its next tick, every 400 ms). The three that move tiles need a deck and say so
if there is none. Nothing in the stand depends on any of it: `up` starts a game the same way with a
deck or without one, and the deck notices the game by itself.

A tile's content comes from an `ITileBacking`, chosen per tile and switchable while the deck runs:
`adopt` (the game's own window, taken into the tile with `SetParent`) and `mirror` (the frames the
game copies into a shared texture on the graphics card, its window never seen). The grid, the
tiles, the commands and the API know nothing about either. `GATEKIT_DECK_BACKING` /
`"deckBacking"` is the default for a new tile and is **`adopt`**; `GATEKIT_DECK_CONCEAL` /
`"deckConceal"` is what a mirrored copy's window is made to do - `offscreen` (the default: past
every screen, a tool window, so on no screen and in no taskbar), `hidden` (BG3 keeps rendering,
but GateKit's window fallbacks then cannot find it, since they look for a visible window), or
`none`. `docs/design/deck.md` has what mirroring costs and reaches, measured.

`mirror` needs the game's side of it: `Ext.ClientRender.MirrorEnable(enabled)` and
`Ext.ClientRender.MirrorInfo()` in GateKit's Script Extender build, which only the DirectX 11
backend implements. The deck asks over the bridge, like everything else; a copy that answers no
keeps its own window and the tile says why.

**Whose games a deck takes.** A deck adopts a window, caps a frame rate and mutes the sound, so
taking a copy somebody else started - another person, another session, a game being played - is not
something to do by accident: a person who opens a deck for an unrelated reason must see nothing
happen to the game they were playing. Every stand keeps its own records of the instances it made
(`<STATE>\instances\<name>`, written by `up` and removed by `destroy`), and that - not the shared
instance folders - is what tells a copy of ours from anyone else's. `Adoption` is the rule, beside
`FrameBudget` and `SoundFocus` and checked the same way.

**The frame budget.** A copy whose window is not in front throttles itself to thirty frames a
second - the engine does that on purpose - and in a deck at most one copy is ever in front, while a
mirrored copy never is. The deck turns that knob round: the tile in view (held open, or the one
with the keyboard in a grid) draws as fast as it likes and the others share a floor, which is what
makes several copies affordable on an ordinary machine. `GATEKIT_DECK_FPS` / `"deckFps"` is `off`,
a floor (`10`, the default), or a floor and a cap for the tile in view (`10/60`). One copy on its
own is never capped. A copy leaving the deck gets back the throttle it had before the deck touched
it, so nothing is left drawing flat out for nobody. `FrameBudget` is the whole rule, as a function
from the tiles to a cap each, with no game in it.

**Neither fails silently.** A copy that answers "no" to a frame cap - the stock Script Extender,
or a patched game whose layout the cap no longer recognises - is named once in the deck's output,
with what that costs (a budget that does nothing for it), and so is one that cannot be quietened.
A copy is given twenty seconds in its tile before the deck will say any of this, because a game
that is still loading has not refused anything. `scenarios/framecap.lua` is the same guard in the
test suite: it asserts the caps reach every field, that a capped game really draws at the rate it
was given, and that lifting the cap lifts the engine's own background throttle - so a game patch
that moves the offsets is found by `gatekit test` rather than by someone wondering why several
copies are slow again.

**Sound follows what you are looking at.** With the same rule: the tile in view is heard and the
rest are muted, through `GK.Sound` (12.4) and so through the game's own volume settings. With a
grid and nothing clicked, nobody is being watched in particular and no copy is silenced. Each tile's
caption carries a level bar and a mute; touching either pins that copy and the deck stops policing
it, because listening to two fights at once is a legitimate thing to want. `GATEKIT_DECK_SOUND` /
`"deckSound"` is `watched` (the default) or `off`. A copy leaving the deck is unmuted.

The game's side of both is in GateKit's Script Extender build: `Ext.ClientRender.FrameCap()`,
`SetFrameCap(fps)` and `SetLostFocusFrameCap(fps)` write `BaseApp`'s three frame caps and the
microseconds its clock derives from them, with no hook and no patch, and refuse to write anything
unless the engine's own arithmetic still holds at the offsets they know.

Finding the game window now also looks through the children of other windows: a game held in a
deck **is** a child window, and `shot`, `key` and the bridge must find it wherever it hangs.

`doctor` gains two checks: **deck** (running, or none - never a failure) and **firewall** (whether a
rule already allows the game to listen, read from the registry).

A firewall rule is held against an executable's **path**, and every instance is a copy in a folder
of its own, so the installed game being allowed says nothing about a copy: Windows asks about each
one separately, in an elevated window over whatever the person was doing. `doctor` therefore reads
every copy, not only the installed game, and `gatekit firewall` lists them and prints the single
line that covers all the ones still missing a rule - one `New-NetFirewallRule` each, named after
its copy. GateKit never writes a firewall rule itself: that is a change to the machine's security,
and the person makes it knowingly or not at all. Making a copy says the same thing before the game
in it first starts, so the dialog is never a surprise.
