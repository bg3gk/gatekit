# Host CLI contract

Every command of `gatekit` (assembly `gatekit`, `src/GateKit.Cli` and each project's
`CreateCommand()`), what it does to a game copy and what it prints. Sections 1, 2 and 4-7 are the
commands that start and drive a copy (`src/GateKit.Copies`); section 3 is the tests in the game
(`test`, `run`, `src/GateKit.Testing`); section 8 is the rest of the toolkit and its settings. The
bridge's file channel is its own contract:
[bridge-protocol.md](bridge-protocol.md).

Conventions:

- **copy**: the game `gatekit` works on. With no instance named (`--instance NAME`, else
  `GATEKIT_INSTANCE`) it is the **installed game** and the player's real profile; with one it is
  an **instance**, a game copy of its own: a cloned game folder and a private profile (section 5).
- `<state>`: the home's `state` folder (8.1) for the installed game, `<state>/instances/<name>`
  for an instance. Instances also read the installed game's state folder (its backup and links).
- **bridge**: the in-game mods `mods/GateKit` (folder `Gwynerva_GateKit`) and `mods/Weave`, read
  from `GATEKIT_BRIDGE` (8.3).
- Exit codes: unless said otherwise `0` = success, `1` = error: a `CopyException`, a platform
  capability that is missing, or a file the game or another call held, printed to stderr as
  `ERROR: <message>`.

---

## 1. Process-wide behaviour

### 1.1 Command line shape

```
gatekit [--instance NAME | --installed-game] <command> [options] [-- extra game args]
```

`--instance` is an option of the root and may stand before or after the command word. A name is
`^[A-Za-z0-9_-]{1,32}$`; anything else is refused (`instance name 'X': use 1-32 letters, digits,
'_' or '-'`). One process works on one copy.

**The installed game is reached only when it is named.** Without an instance, nothing sends the
installed game a chunk of Lua or writes into it or the player's own profile unless
`--installed-game` (also an option of the root) names the installed game. The refusal comes
before anything is sent or touched: `` `<verb>` would write into the installed
game and the player's own profile, which no option names: name a copy with --instance NAME (or
GATEKIT_INSTANCE), or the installed game with --installed-game; nothing was done `` (exit 1). The
one check is `CopyLayout.RequireNamed` (`src/GateKit.Copies/CopyLayout.cs`):

- It sits in the one sender of Lua chunks, `GameSession.LuaAsync`
  (`src/GateKit.Copies/GameSession.cs`, `<verb>` = `a chunk of Lua for the game`), so every chunk
  for a game is held to it, whoever sends it: a command's, a test's, every MCP tool's (`bg3_lua`,
  `bg3_call`, `bg3_dialog`, `bg3_fast_scenes`, `bg3_sound`, the coverage tools, the plugin tools;
  [mcp.md](mcp.md) section 2) and the deck's (frame caps, mirroring, sound).
- The writing commands call it themselves before they touch a file or send a chunk: `build`,
  `up`, `down`, `exec`, `key`, `link`, `reload` (and `bridge-reload`, through `link`), `watch`,
  `checkpoint`, `reset`, `sound set|mute|unmute` and `sound unfocused on|off`.
- `test` and `run` start the game with `up` or send their chunks to a running one, and
  `systems check` and `sweep run|tour` send chunks, so all of them are refused too.
- `se install` and `se remove` write into the installed game only with `--installed-game`
  (section 4); `gatekit --installed-game mcp` is the server whose tools may reach the installed
  game.

Needs no name, because it sends no chunk and writes into no game: `status`, `doctor`, `game verify`
(it writes only the fingerprint into the state folder, 8.2), `logs`, `crashes`, `firewall`,
`instances` (`list`), `save list|export` and `se status`. `shot` needs none while the bridge does
not answer and the window is photographed; while the client's heartbeat is fresh it asks the game
for the frame, a chunk, and is refused. `sound`, `sound list` and `sound unfocused` without a word
read the volumes and the flag with a chunk (`src/GateKit.Copies/Sound.cs`), and are refused too.
`destroy`, `instances create|rename|destroy` and `save import|copy` work on a named instance only.

### 1.2 Environment variables and folders

The settings and folders of the toolkit are in section 8.1 and [docs/design/home.md](../design/home.md).

---

## 2. Commands of a copy (`src/GateKit.Cli/CopyCommands.cs`, `src/GateKit.Copies/GameCopy.cs`)

### 2.1 Summary table

| # | Command | One line |
|---|---|---|
| 1 | `doctor [--json]` | check that this machine has what GateKit needs; list every location (8.1) |
| 2 | `build` | pack the bridge mods to `.pak`s and install them into the copy's profile |
| 3 | `up` | back up, configure, start the game, wait until playable |
| 4 | `down [--stock]` | close the game, restore saves and mod list |
| 5 | `status` | instance, platform, game processes, Script Extender build, heartbeats |
| 6 | `instances [list\|create\|rename\|destroy]` | the copies that exist and which run; make, rename, delete one |
| 7 | `destroy` | `down`, then delete this copy |
| 8 | `crashes [--json] [--limit N]` | the crash dumps this copy left (3.2) |
| 9 | `save list\|export\|import\|copy` | a copy's saves (2.13) |
| 10 | `exec` | run Lua on client, server or ui; print JSON |
| 11 | `shot` | a picture of the game |
| 12 | `key` | press a key in the game |
| 13 | `sound` | the copy's volumes (8.4) |
| 14 | `link`, `reload`, `bridge-reload` (`bridge reload`), `watch` | hot reload of mods' Lua |
| 15 | `logs` | Script Extender's output, or the Osiris trace |
| 16 | `checkpoint`, `reset` | a restore point of the live world, and the way back to it |
| 17 | `se status\|install\|remove` | which Script Extender build the copy loads (section 4) |
| 18 | `firewall` | which copies the firewall lets listen (8.8) |
| 19 | `test`, `run` | tests in the game (section 3) |
| 20 | `systems tally\|check` | the claims of the systems' descriptions the game checks (3.6) |

### 2.2 `build`

1. If a game process of this copy runs -> `the game is running and holds the mod files open; run `down` first`.
2. Each bridge mod is packed with the packer (8.3, point 3) into `<home>/cache/build/<copy>/<Folder>.pak`
   (the installed game's under `installed`; without a build cache `<state>/build/`), beside its
   place and moved over it, then copied into the copy's `<profile>/Mods/`.
3. `<state>/bridge-built.json` records the bridge source the paks came from.
4. Prints `installed <path>` per pak.

`up` packs by itself when the installed paks are stale: a pak missing, a file of a mod newer than
its pak, or the paks made from another bridge source (another checkout); it prints `bridge pak
rebuilt (its sources were newer than the installed pak)`.

Every copy of a file is written beside the target and moved over it (`FileTree.CopyFile`), so a
pak is there whole or not at all. Nothing of the build is shared between copies. `build` and `up`
of one copy hold the copy's start lock (`<state of the copy>/start.lock`, the home's `SharedFile`
lock) from their first look at the copy to their end; a second `up` or `build` of the same copy
meanwhile is refused at once and changes nothing: `another `up` or `build` of instance NAME is
under way in another process; this `up` changed nothing (the copy's last start: ...). When that
one ends, `status` says whether the game is up.` (exit 1).

### 2.3 `up`

Synopsis:
```
gatekit [--instance NAME] up [--level MODULE:LEVEL] [--save FOLDER] [--mods A,B,...] [--story-log] [--main-menu | --splash] [-- GAME_ARGS...]
```
- `--level`: start a fresh test game, no menu, no save. It must be `MODULE:LEVEL` (one colon, both parts non-empty, no spaces): anything else is refused before the game starts (`level 'X' is not MODULE:LEVEL ...`), since the game given a bare level only sits in the main menu. While the client sits in `Menu` waiting for `Running` (any start), `gatekit` asks every 2 s what holds it (`GK.Gui.Stuck()` on the UI context): the splash screen before the main menu (`splash`) is passed with `GK.Cheat.Recover()` on every start (the event its own page raises on a key, `OpenMainMenu`); the game's own first-launch and licence screens (`firstlaunch`: Eula, Larian's mod policy, SensitiveSettings, HDR/Gamma calibration) are gone through with `GK.Cheat.Recover()` in an instance only (a copy GateKit made for itself), and in the installed game fail the start at once naming the screen. Each screen passed is printed (`  splash screen: SplashScreen raised OpenMainMenu, as its key does`). Anything else that keeps the client in `Menu` for 60 s with nothing moving it on fails the start (`the game stayed in the menu for 60 s (<what holds it>) instead of loading level 'X' ...` / `... loading a save`); the game is left running for a look and `down`.
- `--save`: a save folder name under `Savegames/Story` to load instead of the last.
- `--mods`: comma-separated folder names, empty entries dropped. `--mods ""` gives an empty list (still a bare start); not passing it gives none.
- `--story-log`: also record the Osiris trace (`logs --story`).
- `--main-menu`: see 3.5.
- `--splash`: as `--main-menu`, and the splash screen before the main menu is not passed: the start returns when `GK.Gui.Stuck()` names it (`on the splash screen after N s ...`), and `GK.Cheat.Recover()` on the UI context passes it (the event `OpenMainMenu`). Refused together with `--main-menu`; a main menu met with no splash screen before it fails the start.
- `GAME_ARGS`: extra raw game arguments after `--`.

In order (`GameCopy.UpAsync`):
1. Without an instance, `up` is refused unless `--installed-game` names the installed game (1.1). The copy's start lock is taken (2.2). A game process of **this** copy's `bin` -> `the game is already running (<who started it>); run `down` first`. A level that is not `MODULE:LEVEL` is refused; `--main-menu` with `--level` or `--save` -> `a start in the main menu takes no level and no save`.
2. Instance only: the clone and profile are made ready (2.3.1).
3. `<state>/owner.json` records who starts the copy: GateKit's build, the checkout, the bridge source, the process and the time.
4. A profile that carries the bridge under the folder name `Gwynerva_DevKit` is brought over: its pak deleted, its channel folder merged into `Gwynerva_GateKit`, `<game>/Data/Mods/Gwynerva_DevKit/` removed.
5. The bridge paks are rebuilt when stale (2.2).
6. Installed game only: the backup (2.3.2).
7. With `--save`: the folder must exist under the copy's `Savegames/Story`, else `no save folder 'S' in <saves>; there are: [...]`; the game gets `-loadSaveGame S`. With `--level`: `-testLoadLevel L` (with both: `-testLoadLevel L -loadSaveGame S ...`).
8. In an instance always, and in the installed game when `--level` or `--mods` is given: the mod list is cut down (2.3.3). A copy therefore starts bare - the base game and the bridge mods - unless `--mods` names more; only the installed game keeps the player's own list when nothing is asked.
9. The bridge mods are enabled (2.3.4).
10. Instance: every player profile gets Public's mod list, and Public's saves are merged into the `Debug_Client_Profile_n` folders (2.13: nothing is wiped). Installed game: the debug profiles are lent (2.3.5).
11. The splash video is skipped (2.3.6).
12. Script Extender is configured (2.3.7), and the components' layouts are written for named reads (3.6).
13. The Script Extender build for the run (section 4): the one `GATEKIT_SE` or `se install` names is installed and `"SendCrashReports": false` written into `ScriptExtenderSettings.json` (`Script Extender for this run: <folder>`); with none named an instance keeps the build it has; otherwise stock.
14. Linked mods (`<state>/loose.json`, 2.10) are copied in.
15. Stale channel files of the last start are deleted from `<profile>/Script Extender/Gwynerva_GateKit/`: `state_*.json`, `res_*.json`, `queue_*.jsonl`, `cmd_*.tmp`, `seen_armed_*.json`, `seen_spill_*.json`; `process.txt` gets a new name for the event log.
16. Launch: `<bin>/<exe> --skip-launcher` and the arguments above, or `-continueGame` when there are none (`--level`, `--save`, `--main-menu` and game arguments each drop it); working folder `<bin>`; the environment gets `SDL_WINDOW_NO_ACTIVATION_WHEN_SHOWN=1` unless already set (8.3, point 9).
17. Up to **2 attempts**: the crash marker `<profile>/ModCrashSanityCheck` is deleted; the game is started; within **60 s** a process must appear in this copy's `bin`, else `the game process did not appear`; then `gatekit` waits for the client's heartbeat. While it waits it reads the game process and Script Extender's runtime log of this start (`StartCheck`): the process gone (`the game process ended N s after it started, before the bridge reported ...`) or the log showing the module loaded (`OnGameStateChanged(): Loaded module`) with no bootstrap script of the bridge's folder (`the game loaded its module with the bridge's mod off ...`) ends the wait at once; otherwise the limit is `GATEKIT_START_TIMEOUT` (75 s default) of no progress - the log not growing - and four limits in all, read then as no log at all (`Script Extender wrote no log in <folder>: the extender did not load ...`), the bridge's Lua loaded and silent (with the log's last line), or the module not loaded yet (with the log's last line; a busy machine slows a start). The game is closed; only the mods-off reading starts it once more (`<reading>; restarting`, the second start with a log of its own); any other reading, or the second start's, fails with `the bridge did not report[ in the second start either]: <reading>; the game was closed`.
18. The client is awaited until `Running` (300 s), printing `  <secs> s  <state>` on each new game state; `the game process is gone` if it dies, `client did not reach state Running in 300 s; last: {...}` on timeout.
19. Prints `playable after N s`.

Only the **client** reaching `Running` is awaited, not the server. The game never takes the
foreground (8.3, point 9).

#### 2.3.1 An instance made ready (instance only)

1. The mod list it starts from: the player's own, which is `<state>/backup/modsettings.lsx` of the installed game while that is up, else the real `PlayerProfiles/Public/modsettings.lsx`.
2. The clone is brought up to date (section 5), leaving out the installed game's hot-reload mods.
3. The profile is brought up to date (section 5): the player's mod paks are copied into the copy only when `--mods` names at least one; a bare copy's `Mods` folder holds the bridge paks alone (a pak there can load without being in the list, as an override). `gatekit doctor` marks a copy whose mod list holds anything beyond the base game and the bridge mods (`- not bare`, and a `copy-mods` warning naming those mods).
4. That mod list is copied to the copy's `PlayerProfiles/Public/modsettings.lsx`.
5. Saves from the player's real profile: the one `--save` names, when the copy has none of that name; else, for a start without `--level`, all of them, merged - never over a newer one of the copy's.
6. The copy's fingerprint is recorded (8.2), and a missing firewall rule for the copy is said (`note: no firewall rule for this copy ...`).

Errors on the way -> `instance NAME: <msg>\n(instances live in <root>; GATEKIT_INSTANCES names another folder - writable, on the game's drive)`.

#### 2.3.2 The backup (installed game only, `ProfileBackup`)

If `<state>/backup/` exists, nothing is done (an earlier `up` without `down`: that backup is the
original). Else `<state>/backup.tmp/` gets the saves (`Savegames/Story` -> `Story`), the mod list
(`modsettings.lsx`, which must exist: `<file> does not exist; start the game once normally first`)
and `ScriptExtenderSettings.json` of `<bin>` if there is one, and is renamed to `backup`.

#### 2.3.3 The mod list cut down (`ModSettings.Isolate`)

Every `<node id="ModuleShortDesc"> ... </node>` of `PlayerProfiles/Public/modsettings.lsx` whose
`Folder` is neither `GustavX` nor a folder `--mods` names is removed; a wanted folder that is not
in the list -> `not in the mod list, enable them in the game once first: [...]`. The file is edited
as text, so everything else stays byte for byte. The original comes back from the backup on `down`
(installed game); an instance's list is its own.

#### 2.3.4 The bridge enabled (`ModSettings.EnableBridge`)

When the list holds every bridge mod's UUID, nothing changes. Otherwise any of them that are there
are taken out, and all are inserted, in dependency order (Weave first), at the start of the line
that holds the closing `</children>` of the `Mods` region (the last `</children>` but one); none
-> `modsettings.lsx does not look as expected; the bridge was not enabled`. Each entry is written
with a 24-space indent and `\n` line ends, from the mod's `meta.lsx`; the GateKit bridge's is:

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

#### 2.3.5 Debug profiles lent (installed game only)

Only when any game process runs on the machine: a game started while another one runs uses
`PlayerProfiles/Debug_Client_Profile_<n>` instead of `Public`. For `n=1..4`: if nothing is
recorded yet, the existing folder is moved to `<state>/backup/debug-profiles/<name>` (or a marker
`<name>.absent` when there was none, and `<name>.nocloud` when Steam's cloud had no mirror of it);
then a fresh folder gets a copy of the mod list and, for a start without `--level`, of the saves.
`down` puts them back, and deletes the cloud mirrors `_SAVE_<name>`/`_PROFILE_<name>` of a profile
marked `.nocloud`.

#### 2.3.6 The splash video skipped

`<game>/Data/Video/Splash_Logo_Larian.bk2`: at `up`, when there is none, an empty file is written
(in an instance the links on the way become real folders first, and the place is checked to be
inside the instance). At `down` it is deleted only while it is still empty, and `Data/Video` with
it when that is left empty. A file that is not empty is never touched.

#### 2.3.7 Script Extender configured

`<state>/se-logs/` is emptied, and `<bin>/ScriptExtenderSettings.json` gets `CreateConsole=false`,
`DeveloperMode=true`, `LogRuntime=true`, `EnableLogging=<--story-log>`, `LogDirectory=<state>/se-logs`,
`KeepWindowInBackground=true`, `KeepWindowOffScreen` (true while a deck runs), `ShiftOpensConsole=false`,
and in an instance `CrashDumpDirectory=<state>/crash-dumps` and `LaunchCrashReporter=false`. In an
instance the loader's `ScriptExtenderUpdaterConfig.json` gets `ShiftOpensConsole=false` too,
written as a new file. The installed game's original settings come back from the backup on `down`.

### 2.4 `down`

`gatekit down [--stock]` (`GameCopy.Down`):
1. Kills every game process of this copy's `bin`, waits up to **60 s** until none is left (and for
   the copy's `BG3ScriptExtender.dll` to be let go, up to 30 s), then deletes the crash marker.
2. Installed game: the backup is put back (2.4.1).
3. Installed game, or an instance with `--stock`: the stock Script Extender back (section 4). **An
   instance otherwise keeps the build `se install` gave it** (the line ends `, its Script Extender
   build kept`), so the copy's next start loads that build again. The
   installed game always gets stock back: it is the player's, and the updater, crash reports and
   other mods expect the signed build. A dll still locked after 60 s is a `note:` line after the
   usual one, not an error - the loader's switch is already off, so the next start loads stock anyway.
4. The splash stub is removed (2.3.6).
5. Linked Lua is removed from the game folder and `<state>/loose.json` deleted (2.10).

Prints `instance NAME: game closed` (`, stock Script Extender back` / `, its Script Extender build
kept`) or `game closed, saves and mod list restored`; a copy another checkout started ends with
`(it was started by ...)`.

#### 2.4.1 The backup put back

If `<state>/backup/` is missing, nothing. Else:
- If it has `Story`: the copy's current saves are **moved** to `<state>/saves-after-run/<yyyyMMdd-HHmmss>/` (the newest 3 kept), and `Story` copied back.
- `modsettings.lsx` is copied back.
- Steam's cloud mirrors (`<Steam>/userdata/*/1086940/remote`, of the Steam root and of the game's library): when the backup had `Story`, every save in `_SAVE_Public/Savegames/Story` that is not among the backup's is deleted; `_PROFILE_Public/modsettings.lsx`, where it exists, is overwritten from the backup.
- `ScriptExtenderSettings.json` is copied back into `<bin>` when the backup has one (if there was no original, the run's file stays).
- The debug profiles come back (2.3.5).
- `<state>/backup/` is deleted.

### 2.5 `status`

Prints, in order: `instance: <name>  (<root>)` or `instance: none (the installed game)`;
`platform: <name>`; `processes: [pids]`; `script extender: local build from <source>` or
`script extender: stock`; then `client {gameState, boot, done, queue, age}` and `server ...` from
the last heartbeat whatever its age (`none` without one); last `event kinds: ...`: the registry the
event log's rows are placed by and how the game's differs from the host's build (bridge-protocol.md,
"The event log"); `doctor` gives the same as its `event kinds` check. Exit 0.

### 2.6 `instances`

`instances` (or `instances list`) prints one line per instance folder under the instances root that
holds `game/bin`, sorted: `<name, 16 wide> <running, pid [..] | stopped, 24 wide> <root>`, running
meaning a game process whose executable lies inside the instance. Last `(installed game)` the same
way for the installed game's `bin`. It lists every copy whatever `--instance` names.
`instances create NAME` makes or refreshes an instance (2.3.1) without starting it (`instance NAME
ready in <root>`); `instances rename NAME NEW` moves its clone and records, nothing else changes,
and a deck's label follows it (`instance NAME is now NEW`); `instances destroy NAME` is `destroy`
for that instance. `create` and `rename` refuse a running copy.

### 2.7 `destroy`

Needs an instance (`` `destroy` needs an instance: GATEKIT_INSTANCE or --instance <name> ``).
`down`, then the clone, the private profile and the instance's records are deleted (links removed
as links, never followed), and the instances root too when it is left empty. Prints
`instance NAME deleted`.

### 2.8 `exec`

`gatekit exec {client|server|ui} CODE [--timeout S]` (default 20 s). `CODE` starting with `@` is a
Lua file (read as UTF-8, a BOM dropped). The chunk goes through the bridge (bridge-protocol.md);
lines it logs are printed as `  [<side>] <line>` before the result, then the result as indented
JSON (`null` for nil). A Lua error -> `ERROR: Lua error on <side>:\n<traceback>`; no answer ->
`ERROR: no answer from <side>: <why> (state: {...})`; no game of this copy -> `ERROR: no game is
running for <copy>; nothing was sent`. Side `ui` is the client side with the command flagged
`"ui": true` (the bridge runs it inside `Ext.UI.Defer`). Every chunk is sent after the prelude
`local GK = Mods.GateKit.GK` (8.3, point 4).

### 2.8.1 `probe`

`gatekit probe ID... [--do CODE] [--seconds S] [--note] [--json]` joins the registry and the running game
both ways. Each `ID` is a registry id of a function, looked up in the build table of the
executable the game runs, or a `binary+0xRVA`; each gets a count-only probe
(`Ext.Debug.ProbeFunction`). `--do` is Lua run on the server once the probes are on - the action
to watch; the game then runs `--seconds` (default 3), the probes are read and taken off. For each
function the answer gives its place, how often it was called, and every caller: the registry id
whose function holds the caller's address with the offset inside it (`code:<id>+0x..`), or
`unknown` with the address. A function that was called also gets a line `evidence  watch:...`,
the sentence a `registry note --evidence` takes; the callers the registry does not name are
listed last, as what to read next. Which function holds a caller's address is asked of the
databases as `registry at` asks (a function may lie in several parts); a row's size answers only
where they are not at hand. `--note` writes each unnamed caller into the live layer as
`unplaced::unnamed_<token>` (the token of its fingerprint, rung `unknown`) with the functions it
was seen to call and the question what it is for, so that what is still to read is kept; a caller
an id already names is left as it is. `--json`: `[ { id, address, hits, otherCallers, callers = [
{ id, offset, address, count } ] } ]`. An id with no place in the running executable -> `ERROR`
and nothing is probed; a function the extender cannot probe is named on the error stream and the
rest go on. Needs the Script Extender build with function probes: without it -> `ERROR: this
Script Extender build has no function probes`.

### 2.8.2 `cover`, `cover-fold`, `cover-diff`

The record of coverage: which functions of the running executable run, and from where, while the
game is played. Needs the Script Extender build with coverage counters
(`Ext.Debug.StartCoverage`).

- `gatekit cover start [--min-size N] [--limit N] [--hot N]` puts a counter on the functions of
  the executable the game runs, as the pseudo-code database lists them: thunks and functions
  shorter than `--min-size` bytes (default 64) left out; `--limit` takes that many, spread evenly
  (default: all). The extender refuses an address that its own table of functions does not give
  as a function's start. A function called more often than `--hot` (default 200000) is taken off
  by itself, with the count it reached. Prints `<binary>: <asked> functions asked, <counted>
  counted, <refused> refused`. A second start adds to the first.
- `gatekit cover status` prints `{ Functions, Attached, Hot, Seen, Calls, Pairs, Lost }`.
- `gatekit cover save FILE [--reset]` writes the counts to `FILE`: `F <module> <rva> <calls>
  <hot>` for every function called, `E <module> <rva> <module> <rva> <calls>` for every pair of a
  function and the address that called it, `L <calls lost>`; rvas in hex. `--reset` starts the
  counts again, so that each file holds one stretch of play.
- `gatekit cover stop` takes every counter off; the counts stay for a last `save`.
- `gatekit cover-fold FILE... [--top N] [--where inCombat|inDialog|neither] [--names]` needs no
  game: it adds the files up and reads them against the registry - how many functions ran, how
  many of them an id names, the named ones and the unnamed ones that ran the most (default 30 of
  each), each unnamed one with the number of places it was called from. A function taken off as
  hot counts once, with the most it reached in any file. `--where` takes only the files whose
  state file beside them (the same name, `.json`) says the game was in that situation; `--names`
  puts beside an unnamed function the name the databases give it (slower).
- `gatekit cover-diff FILE... --against FILE... [--top N] [--names]` needs no game: the functions
  that ran in the first set of files and in none of the second - a scene with a mechanic against
  the same scene without it names the mechanic's functions.

### 2.9 `shot`, `key`

- `shot OUT [--width N]`: a picture of the game, taken by the game itself (8.3, point 12), or of
  its window when the bridge cannot; scaled down to `--width` (1150; 0 for full size) when wider;
  `.png` writes PNG, anything else JPEG quality 82. Prints the absolute path. Failures:
  `screenshot failed: ...`, `the game accepted the screenshot but wrote no file in 10 s`.
- `key NAME [--hold S]`: the names of `GameKeys` (`escape`, `enter`, `space`, `tab`, `backspace`,
  `left`, `up`, `right`, `down`, `f1`..`f12`, or one letter or digit); else `unknown key 'X';
  known: ...`. Pressed through the game (8.3, point 11). Prints nothing on success.

### 2.10 `link`, `reload`, `bridge-reload`, `watch` (hot reload)

State: `<state>/loose.json` = `{ "<ModFolder>": "<absolute path of the mod's .../ScriptExtender/Lua>" }`.
Target: `<game>/Data/Mods/<ModFolder>/ScriptExtender/Lua` (loose Lua wins over the same files in a
`.pak`; `Ext.Debug.Reset()` loads it again). Only Lua is copied.

- `link MOD_ROOT`: the full path of `MOD_ROOT`; every `MOD_ROOT/Mods/*/ScriptExtender/Lua`; none -> `no Mods/<Folder>/ScriptExtender/Lua under <root>`; recorded in `loose.json` and copied in (in an instance the links on the way become real folders and the place is checked to be inside the instance); prints `linked: A, B`.
- `reload`: needs linked mods (`nothing is linked; run `link <mod folder>` first`); a call in flight is let finish first (up to 30 s, said once); the linked Lua is copied in and the layouts for named reads written; each side's capture is spilled and `Ext.Debug.Reset()` sent to server and client **without waiting for an answer**; each side's heartbeat `boot` must change within 30 s, else `<side> Lua did not come back after the reset (a Lua error in the mod? see `gatekit logs`)`, then a `return true` round trip. Prints `reloaded in X.X s`.
- `bridge-reload` (also `bridge reload`): links the bridge mods' own roots, then the same reload; prints `bridge Lua reloaded in X.X s (linked: it also survives `reset`; `down` removes the link)`.
- `watch`: prints `watching <mods>` (or `watching nothing (run `link` first)`); every 0.5 s takes the newest modification time of any `*.lua` below the linked sources; on a change prints `HH:mm:ss reloaded in X.X s` or `HH:mm:ss reload failed: <msg>`. Ctrl+C ends it with exit 0.

### 2.11 `logs`

`logs [--story] [--lines N=80] [--grep REGEX]`: the last N non-blank lines, filtered by `--grep`
(case-insensitive), from the log files of this start in `<state>/se-logs/`, oldest file first:
`Extender Runtime *.log` (prints, warnings, Lua errors), or with `--story` `Osiris Runtime *.log`
(the Osiris trace; empty without `up --story-log`). A game started another way has no runtime log
and is read from its Script Extender console window (8.3, point 5); where neither is there the
error says so.

### 2.12 `checkpoint`, `reset` (a clean world without a restart)

- `checkpoint` (`GameSession.CheckpointAsync`): waits for the server's `Running` (60 s); notes the level (`GK.Stage.CurrentLevel()`); remembers every `PlayerProfiles/*/Savegames/Story/*` folder; turns autosaves on with `GK.AutoSave(true)` on both sides; server `Osi.AutoSave()`; polls every 0.5 s up to 40 s for a **new** save folder holding a `.lsv` (`the game made no save for the checkpoint`); autosaves off again on both sides whatever happened; waits 1 s; writes the folder name to `<state>/checkpoint.txt` and the level to `<state>/checkpoint.level.txt`; prints `checkpoint: <name>`.
- `reset [--keep-scenes]` (`GameSession.ResetAsync`, 120 s): the name from this session or `checkpoint.txt` (`no checkpoint to return to; run `checkpoint` first`); notes both `boot`s; sends `Osi.LoadGame("<name>")` to the server without waiting; for client then server waits (poll 0.3 s) until `boot` changed **and** `gameState == "Running"` (`the game closed during the reset`, `<side> did not come back from the reset in 120 s; last: ...`), then a `return true` round trip; then, unless `--keep-scenes`, plays the level's opening scene through (client `GK.FastScenes(20)`; polls `GK.Scenes()` every 0.4 s up to 60 s until a scene that is not `gameplay` has appeared and ended, or none appeared within 3 s; finally `GK.FastScenes(1)`). Prints `world reset in X.X s`.

Loading a save made right after the start gives the world back as it was with no module load,
which a fresh process cannot skip. Depends on the bridge's `GK.AutoSave`,
`GK.Scenes`, `GK.FastScenes`, `GK.Stage.CurrentLevel`.

### 2.13 `save`: a copy's saves, and one copy's save in another

`gatekit --instance X save list` prints every save of the copy, newest first, one line per name
(`<time>  <name>  (<player profile>)`), across its player profiles (Public and the
`Debug_Client_Profile_n` a copy plays as while another game runs; the newest copy of a name wins).
`save export NAME --out DIR` writes that save's folder to `DIR/NAME`. `save import FOLDER` puts a
save folder (one holding a `.lsv`) into every player profile the copy has, Public first, replacing
a save of that name; it refuses the installed game (no `--instance`): its saves are the player's.
`save copy NAME --from A --to B` is export and import in one. A copy then starts from it with
`gatekit --instance B up --save NAME` (`bg3_up` `save`, which takes a save folder name), or
loads it while running (`GK.Gui.LoadLatest` loads the newest). Folder copies only, so the same on
every platform.

**A copy keeps its saves.** No `up` wipes the saves of a copy's player profiles: what the copy
saved under a `Debug_Client_Profile_n` is gathered into Public at `up`, Public's saves are merged
into the debug profiles (a save missing there or older there is copied), and the player's saves
are merged in only where the copy has none newer of the same name.

---

## 3. Tests in the game: `test` and `run` (`src/GateKit.Testing`)

### 3.1 `test`

`test [FOLDER] [-k TEXT] [--clean] [--attach] [--reset RUNG] [--repeat N] [--speed S] [--trace] [--json]` runs the tests of a folder (default: `tests` of the checkout); `run FILE` runs one (3.4). How a test is written - its header `test { ... }`, its steps, its cases and the `game` object - is [docs/writing-tests.md](../writing-tests.md); the runner is `src/GateKit.Testing`. A header field the runner does not know is refused when the file is read, naming the field and the fields it knows (`GameTest.HeaderFields`).

Tests that declare the same start (level, mods, save, main menu) share one game. A start that fails fails every test of its group (`FAIL (game did not start: ...)`); a game that dies inside a group leaves the rest `FAIL (not run: the game closed)`. A `*.lua` that declares no test (neither `test { }` nor `function run(game)`) is not loaded: `test` prints `SKIP  <name>: not replayable: a free-form recipe ... (it declares no `test { }` and no `function run(game)`)` and goes on (with `-k` matching only such files: `ERROR: no tests in <folder> (N file(s) skipped: they declare no test)`), and `run` refuses it with that line. **A running game** (one this copy already has up, e.g. from `bg3_up`) is never started over nor closed: without `--attach` a copy in the level a group declares runs that group (3.3), and in any other level `test` refuses at once (`ERROR: the game is already running in <level>, and no test asked for is declared for that level; pass --attach ... Nothing was stopped`). `test --attach` runs every selected test in that game and leaves it running; a test that declares a level other than the one the game is in (`GK.Stage.CurrentLevel()` on the server, compared with the part after `MODULE:`) is not run: `SKIP  <name> ... NOT RUN: it declares <MODULE:LEVEL> and the running game is in <LEVEL>; ...`, counted apart (`N passed, M failed, K not run`), and a test not run makes the exit code 1 as a failure does - a replay in the wrong level proves nothing either way. When the game does not say its level the test runs. `--attach` with no game: `ERROR: --attach needs a running game (start one with `up`)`. Each test's verdict line (`PASS|FAIL|SKIP  <name>  <seconds> s  <why>`) is printed the moment it is known and written to `<run folder>/<name>/verdict.txt`, beside `failure.jpg` and `console.txt` of a failure; the summary repeats them after a `--` line. A test under `tests/_coverage` of a checkout that has `research/coverage` (a coverage recipe) also leaves a row in `research/coverage/record/replays.jsonl`, passed or failed, and a passed one a `repeated` row in `record/sightings.jsonl` (8.11); a record that cannot be written is said (`note: the replay of <file> was not written into the record: ...`) and the verdict stands.

### 3.2 The tree of tests, steps and the three outcomes

`test [FOLDER] [-k SUBSTR] [--json] ...` reads every `*.lua` of `FOLDER` and of its subfolders at
any depth (`tests/<domain>/<mechanic>/<test>.lua`). A test's id is its path under `FOLDER` without
`.lua`, with `/` between the parts (`combat/attack/melee_miss`; a file of `FOLDER` itself: its
name); `-k TEXT` picks the test whose id is `TEXT` (any case) when there is one - `-k event_log`
is `event_log` alone, not `event_log_load` - and otherwise every test whose id contains it; the verdict lines and the run's output folder (`<run folder>/<id>/`)
use it, and `--repeat` names `<id>#2`. Two tests of one file name in different folders stay
apart. A file whose name starts with `_` is shared code. A subfolder whose `_suite.json`
(schema `tests/suite`: `about`, `read`) says `"read": "named"` is read only when it is the folder
given (`tests/_coverage`: `test tests/_coverage`).

A test ends in one of three outcomes: **pass**; **fail**; **unmet** - a precondition (`need`,
lua-api.md section 19) does not hold here, which says nothing of the thing tested: `UNMET  <id>
<s> s   UNMET: <why>`, no screenshot. A failure inside a named step reads `FAIL: step <name>
(<class>) failed: <why>`. Under each verdict line the test's steps follow, one a line: `step <name>
(<class>) <pass|fail|unmet> <s> s`. The summary counts `N passed, M failed[, U unmet][, K not
run]`. Exit code: 0 when all passed; 1 when one failed or was not run; 2 when none failed and one
or more were unmet. `<run folder>/results.json` (schema `record/test-run`: `exit`, `seconds`,
`slowest`, `waits`, `answers`, `tests = [{ test, outcome, verdict, seconds, waits, answers, steps =
[{ name, class, outcome, seconds, waits, answers, error? }], cases = [{ name, outcome, seconds,
error?, reset?, steps, surprises }] }]`) is written at the end of every run; `--json` prints it
after the summary. `run <file>` prints `UNMET <name>: <why>` and exits 2 the same way.

**Cases.** A test of cases (`case(name, fn)`, docs/writing-tests.md) prints `case <name>: <outcome>
<s> s` under its verdict line, each with its steps; between two cases the ladder runs the test's
rung again (its line is the case's `reset`). `-k <test id>/<case>` runs that one case; a case the
test does not have is refused with its cases named.

**Where the time went.** After the summary the runner prints `slowest steps: <address> <s> s
(waits <s> s, answers <s> s); ...` - the five slowest steps, each by `<test id>/<step>` or
`<test id>/<case>/<step>` - and `time in waits <s> s (<wait> <s> s, ...); in the game's answers <s>
s`: the time inside the waits of `game` by kind, and the time in the game's own answers to the
tests' chunks outside any wait. `results.json` holds the same (`slowest`, `waits`, `answers`, and
per test and step).

`--speed <n|working>` starts every test at that game speed (`working` is `GK.Speed.Working`): the
speed is set and the stage's baseline taken again at it, because the ladder's clean puts the speed
back to the baseline's; it is checked before every test, so a level reload or a restart does not
lose it, and a copy left running gets back the speed it had. One run at `--speed 1` and one at
`--speed working` show a test at both speeds. A test whose header declares
`speed_ceiling = <n>` is never started faster than that, with or without `--speed`; the line
`speed: <n> (the test's ceiling: <n>)` says so, and the test after it gets the run's speed back.
A ceiling names its finding (`ceiling_finding = "<where it is written>"`); without it the test is
refused when it is read. Under `--attach` the speed is held the same way, before every test.

`--trace` traces the entity system through every step on both sides (`GK.Trace`, lua-api.md 16.1):
a trace begins with each step and ends with it, and is written as `trace/<nn>-[<case>-]<step>.json`
in the test's output folder - `{ step, case, outcome, server, client }`, each side `GK.Trace.End`'s
answer, at most 500 changes and 500 system changes a side and step. The event log holds each step's
`trace_step` row, not the changes. A side without the tracer answers why and the step runs as
without `--trace`. An unfiltered trace costs the game time; it is a tool for finding a cause, not
for a run whose times are read.

A copy whose game dies leaves a crash dump (`crashes`, below); a test that fails after it gets
`crash.txt` beside `failure.jpg`: the exception, the faulting `module+0xRVA` and the function there.

`crashes [--limit N] [--json]` lists the dumps of the copy, newest first. `up` of an instance points
GateKit's Script Extender at the copy's `crash-dumps` folder (`CrashDumpDirectory`) and switches the
reporter window off (`LaunchCrashReporter: false`); the extender writes `<pid>-<date>-<time>.dmp`
with a `.txt` naming the exception, the address and the thread, and `crashes` names the function at
the address with `decomp at`. Where none is named, the dump's line (and `unnamed` in `--json`) says
why: an address inside Script Extender's own module (`BG3ScriptExtender.dll`) is in no database of
GateKit's (`decomp` holds the game's binaries), and the line names the `.pdb` beside the build the
copy loads, or in its bin, for a debugger opened on the `.dmp`, or says none was found; for any
other module it is what `decomp at` said. The installed game's settings are left alone. A hung game leaves no
dump: the extender has no call that writes a dump of a game still running.

A test loads a file beside it with `require("_name")`, or one of a sibling folder with
`require("folder/_name")`: only a shared file - one whose name begins with `_` - is required, once
a run of the test. The host's Lua has no `load`, `loadfile` or `dofile`. A test's surprises are
read from the `_suite.json` files of its folder up to the folder given, never above it (a file run
alone: its own folder's).

**Surprises.** After a test the runner lists what the event log holds that the test did not ask
for, one line each under its steps - `surprise: <sort> <what> (<n> rows): <detail>` - and as
`surprises = [{ sort, what, rows, detail }]` of the test in `results.json`. Blocking and
participant surprises are a report and change no outcome; a test without cases whose rows include
unplaced ones fails (`the event log holds rows the host could not place: ...`). The sorts:

- `blocking`: a row whose attention is `begin` (a dialogue, a roll window, a tutorial, the
  game-over box, a fight) of a kind the test never named to `game.events` or
  `game.wait_attention`; `game.dialog` and `game.answer` name the dialogue's kinds. The detail
  says whether it ended inside the test.
- `participant`: a creature named by `who` or `by` of a row that is neither of the party (the
  `party` of a `scope_begin`) nor staged through the ledger (`scope_track`, `roll_pin`).
- `unplaced`: a row the host's registry cannot place.

A test allows a surprise in its header, `test{ surprises = { "combat_started" } }`; a folder
allows it to every test under it with the same list in `_suite.json`. An entry is the kind of a
blocking state, or `participants` for every creature outside the cast. An unplaced row cannot be
allowed.

### 3.3 The reset ladder

`test [FOLDER] [-k TEXT] [--clean] [--attach] [--reset none|fixture|venue|game] [--repeat N]`.
`--repeat N` runs the selection N times in a row in the same game (`name#2`, `name#3` ...). Every
suite ends with a **timing line**: `timing: <runs> runs, <s> s a run in all (run <s> s); fixture held
<n> (<mean> s, <min>-<max>), venue held <n> (...), fixture failed <n> (...)` - the ladder measured by
the toolkit itself. `test tests/_coverage -k sweep_boosts --repeat 2` measures the rungs (with
`--reset venue`, a level load before each); `test tests -k stage_clean_cycles` runs five fixture
cycles (counts, Lua and process memory - `game.process()` - and frame time; `--repeat 20` makes
it a hundred); `test tests -k stage_level_survivors` shows what survives a level load.

One decision, in `ResetLadder` (`src/GateKit.Testing/ResetLadder.cs`), of how a test gets a clean
world, used by `test`, `systems check --clean` and - through its public API (`ResetLadder`,
`GameResetActions`, `SimpleLevel`) - by any other caller such as the sweep's replays. The rungs,
cheapest first:

1. **`fixture`: our own clean-up in the scene** (remove what was added, spawn anew) - instant;
   relied on whenever it can do the job. Its cleanliness is ours to prove, so it is verified every
   time.
2. **`venue`: the level reloaded** (the checkpoint of this level, in the same process) - it costs
   real time even in the sandbox. Near-absolutely clean, because the game rebuilds the level from
   nothing; what survives a level load (lua-api.md 14, "What survives a level load") is restored
   or flagged.
3. **`game`: the game restarted** - the last resort, only when nothing else can do (a new
   extender DLL, a hang, a crash, or state that survives a level load). Absolutely clean.

The ladder starts at the cheapest rung that satisfies the test, verifies, climbs one rung only when
it must, and says which rung held and why.

| rung | what it does |
|---|---|
| `none` | nothing; the stage scope is opened (`GK.Stage.Begin`) |
| `fixture` | server `GK.Stage.Clean()`, then the client's half (an aim given back, a window that holds the game gone through with `GK.Cheat.Recover`, windows opened since the baseline closed, the capture label put back, a test's plain globals forgotten), then `GK.Stage.Verify()` and the client's windows and attention record, again every 0.25 s for up to 4 s - while Verify says `pending` (the game's own work under way: a deletion, a turn resource it refills, a weapon it puts away; lua-api.md 14) for up to 12 s of the game's own time, read from Verify's clock, so the wait holds at any speed - and once more after 0.3 s of settle; a second clean when the first is not clean (what the game had not done yet, or what began after it: a fight a late reaction started). What Verify lists as kept by the game is printed under the reset line (`kept by the game (not dirt): ...`) |
| `venue` | the checkpoint of this level loaded in the same process (`reset`), then what survives a load put back, a new baseline. The scene skip waits out a level's opening dialogue that does not end at 20 times speed, so in such a level a restart costs no more |
| `game` | `down` and `up` with the group's start, a new baseline (and a checkpoint when a later test needs `venue`) |

- A test declares what it **needs**: `test { reset = "none"|"fixture"|"venue"|"game" }`.
  Undeclared: `fixture` when the level is a **simple test level**, `venue` otherwise (or when the
  game does not say). Simple: `GK.Stage.Census()` counts at most `GATEKIT_SIMPLE_LEVEL_CHARACTERS`
  (`simpleLevelCharacters`, default 12) characters in the level besides the party, or the level is
  listed in `GATEKIT_SIMPLE_LEVELS` (`simpleLevels`, comma-separated). `--reset RUNG` raises every
  test's need to at least that rung.
- Before each test the ladder starts at the rung needed and **verifies**; a rung that fails or
  leaves the world dirty climbs one rung, and the line says which held and why:
  `reset before <name>: fixture failed 7.1 s (dirty: ...) -> venue 38.2 s`. The rung is repeated in
  the summary (`[reset: ...]`). `venue` needs a checkpoint made in the same level
  (`<state>/checkpoint.level.txt`, written by `checkpoint`); `game` is never taken in a game this
  command did not start (`--attach`, a copy found running, `systems check`). No rung left: that
  test fails `FAIL (reset: no rung held: ...); the world as it is now is the new baseline, so
  the next test is judged by its own changes; to let the ladder climb: <remedy>` and is not run;
  the world is cleaned once more and the baseline taken anew (`GK.Stage.Begin{ fresh = true }`), so
  one dirty world fails one test, never the tests after it. The remedy names what each failed rung
  lacked: a checkpoint of this level (`checkpoint`), a game this command may restart (no
  `--attach`).
- The first test of a group in a game just started (or reloaded) needs no rung: the scope is
  opened on the untouched world. A checkpoint of the level is made then, once, so that a fixture
  rung that does not hold climbs to a level reload and not to a restart.
- The rungs are those of a test in a level. A test in the main menu (`menu = true`) has no scene
  to clean and no level to reload: it runs as the place is, and only a declared `game` applies.
- **A running copy in the declared level is used**: without `--attach`, a copy already
  in the level a group declares runs that group (`in the copy already running (<level>): not
  started, not closed`); a baseline it kept from earlier tests (`GK.Stage.Begin` answered `kept`)
  makes the first test prepared too, otherwise the world as found is the baseline (`--clean`
  prepares the first one anyway). Afterwards the copy is cleaned once more and left running
  (`after the suite: cleaned`). A copy running in another level is refused, nothing stopped.
  Groups for other levels start their own game after it.
- `--attach` runs every test in the game as it is, with the same ladder: `fixture` always;
  `venue` only when the copy has a checkpoint of the level it is in (made by `checkpoint`; under
  `--attach` the runner makes none itself, as `up` makes none); `game` never. A copy started
  with `up` and attached to has no checkpoint until one is made, so there a fixture rung that does
  not hold ends that test's reset as above.
- What the attention record saw during the test before (a fight that began, a dialogue, a
  death) is printed under `side effects of the test before, taken back: ...`.
- A bridge without the stage ledger (`GK.Stage.Begin` not there): nothing can be cleaned; only
  what a test declares is done.

### 3.4 `run`

`run FILE [--keep] [--attach] [--level MODULE:LEVEL] [--save NAME] [--mods A,B] [-- GAME_ARGS]`.
Loads the test (a file that declares none is refused: `ERROR: <name>: not replayable: ...`) and makes `<state>/runs/<stamp>-<name>/`; `--attach` needs a running game (`--attach needs a running game (start one with `up`)`) and implies `--keep`; otherwise it starts the game as `up` does, with `--level`, `--save` and `--mods` in place of what the test declares. It runs the test as `test` runs one, without the reset ladder, prints `PASS <name>  (output: <dir>)`, `UNMET <name>: <why>` or `FAIL <name>: <why>` with the steps under it, and exits as `test` does; the game is closed afterwards unless `--keep`.

### 3.5 A start in the main menu

`up --main-menu` starts the game without `-continueGame`, waits for the client's `Menu` state, passes the splash screen and goes through the first-launch screens (instances only), as above, and returns when `GK.Gui.Stuck()` names the main menu (`in the main menu after N s`; 180 s of menu without it fails, the game left running); it refuses `--level` and `--save`. A test declares it with `test { menu = true }` (its start key is `<main menu>`), and `test --attach` does not run such a test in a game that is in a level. `bg3_recipe` writes it for `level: "main menu"` and waits for the client's `Menu` instead of the server's `Running`.

### 3.6 Claims the game checks: `systems tally`, `systems check`, the prelude's `expect`

A system's description (`research/coverage/systems/<ns with :: as __>/<Name>.json`,
`research/coverage/systems.md`) has `does`, a list of claims `{"method", "text",
"evidence"}`. A claim becomes checkable by one more key:

```json
"check": {"when": "<recipe: a test in tests/_coverage, without .lua>",
          "expect": [{"appears": {"component": "<engine name>", "side": "server", "as": "E"}},
                     {"gone":    {"component": "...", "on": "E"}},
                     {"equals":  {"component": "...", "field": "<name in its layout>", "value": 5, "on": "E"}},
                     {"changes": {"component": "...", "field": "...", "entity": "<uuid>", "within": 2}}]}
```

or `"check": "not checkable live: <why>"`. Four verbs and no more; `side` defaults to `server`; a
field is read by the name its layout gives it (`GK.Probe.Field`, below). Between `expect.begin` and `expect.finish` every expectation is **watched every tick** in
the game (a one-frame type included), and judged on what that watch saw:
`appears` - some entity held the component at some tick; with `"as": "E"` the first entity that
did not hold it at the start is bound to the name E. `gone` - an entity that held it (at the start
or later) stopped holding it, so "added, then removed inside the recipe" passes. `equals` - at some
tick some holder's field was the value. `changes` - the same entity's field held one value and then
another while it held the component; a new holder alone does not pass. `"on": "E"` judges an
expectation on the entity an earlier `as` bound (an unbound name fails, saying so); `"entity":
"<uuid>"` on that entity. `within` (default 1 s) is how long `finish` gives the watch after the
steps. A verdict is `{verb, ok, detail, entity}`. `gatekit --instance <copy> systems check <system> [--clean]
[--claim N] [--systems DIR] [--recipes DIR]` needs the copy running; for each checkable claim (with
`--clean` after the reset ladder of 3.3 - the recipe's declared need, else a clean in a simple
test level and the level's checkpoint elsewhere, never a restart - printed as `reset before
<system> claim N: <rungs>`) it loads the recipe, calls
`expect.begin(list)` (appears: watched every tick from before the steps, so a one-frame type
counts; gone: holders counted; changes: values read), runs the recipe's `run(game)` (a recipe that
fails is recorded, the expectations are still judged) and `expect.finish(list)`, then writes
beside the claim `"verdict": {"passed": n, "failed": m, "last": "passed"|"failed", "cases": [{"date",
"instance", "game" (Ext.Utils.GameVersion), "gatekit" (its informational version), "ok", "recipe":
"ok"|"failed: ...", "expect": [{"verb", "ok", "detail"}]}]}` (the last 20 cases), every other key of
the file left as it is. It prints `PASS|FAIL|SKIP  <system> claim <n> ...` and `N passed, M failed,
of T claims`; exit 1 when one failed. A claim passes only when every expectation is true; a pass
proves the claim on the cases run, nothing wider. `gatekit systems tally [--ns P] [--json]`
counts, over the descriptions: `systems`, `claims`, `with_a_check`, `not_checkable_live`,
`check_malformed`, `checked` (a verdict exists), `passed` and `failed` (by the last verdict).

In a test the same verbs are `expect.appears{...}`, `expect.gone{...}`, `expect.equals{...}`,
`expect.changes{...}` with the arguments above, and `expect.begin(list)` / `expect.finish(list)`
around steps; each returns a verdict and never throws (a verb on its own watches from that moment
for `within` seconds). `systems tally` and `systems check` default to the checkout they are run in
(a worktree's own `research/coverage/systems`), then to the one the program was built in.

**Named reads.** At `up` and at every `reload`/`bridge-reload` `gatekit` writes the components'
layouts - the registry's types made from layouts (`registry/types`, those with a `survey`; status
from it), else the files of `research/coverage/layouts` - into the bridge's channel as
`layouts.json` (`{"version": 1, "components": {"<engine name>": {"size", "status", "fields":
{"<name>": [offset, "type"]}}}}`; padding and systems left out; every field whatever its rung). The
bridge reads it with `GK.Probe.Layouts([reload])`, `GK.Probe.Field(entity, component, name)` (the
value - a number, a boolean, a handle as an integer, the hex of a type it does not read - and
`{type, offset, status}`, or nil and why) and `GK.Probe.FieldValues(component, name[, max])`
(`{values = {[entity] = value}, holders, error?}`), through the same raw read as `GK.Probe.View`:
a name in a merged layout reaches a running copy at its next bridge reload, no extender rebuilt.

---

## 4. Script Extender switch (`se`, `src/GateKit.Copies/ScriptExtenderSwitch.cs`)

The stock loader `bin/DWrite.dll` loads `bin/BG3ScriptExtender.dll` instead of the signed build in
its cache when `bin/ScriptExtenderUpdaterConfig.json` holds `"DebugLoadSE": true`. Switching is
adding those files and taking them away again; no file of the game or of Script Extender changes.
`up` and `down` use the same switch on the copy's `bin` (2.3 step 13, 2.4 step 3).

- `se status`: `{fork, dll, source, sha256, managed}` as indented JSON. `fork` = `DebugLoadSE` is
  true and the dll is there; `source` comes from the record; `managed` = a record exists.
- `gatekit se install [FOLDER] [--installed-game]` records the build as the default every start
  loads when nothing names one (`<state>/script-extender.json`) and prints `{recorded, into, ...}`.
  Without `FOLDER` it fetches the build pinned in the checkout's `se-release.json` into the home
  (a build already fetched for that tag is reused). It writes into a game only when one is named:
  with `--instance` into that copy's `bin`, with `--installed-game` into the installed game's
  (`into` is that bin; the fields of `se status` follow). Without either nothing of any game changes
  (`into: null`). Writing a build: `FOLDER` may be the dll or a folder holding it (`no
  BG3ScriptExtender.dll in <folder>`); the first install since the last remove refuses a dll it did
  not put there (`<dll> exists and was not put there by GateKit; move it away first`), keeps the
  loader config there was as `<state>/sefork-updater-config.json` and writes the record
  `<state>/sefork.json` `{source, had_config}`; the dll (and a `.pdb` beside it) is copied into
  `bin`, and the loader config becomes the kept one (or `{}`) plus `"DebugLoadSE": true`. The game
  must not run (`the game is running; run `down` first`).
- `se remove [--installed-game]` takes the same names and refuses to run without one: `se remove`
  without `--instance` would change the installed game, so it asks for `--installed-game`. Without
  a record there is nothing to undo. Otherwise the loader config is put back (or deleted) first -
  from then on the stock build loads - then the dll and pdb are deleted, retried for up to 20 s
  (`<dll> is still in use; the stock Script Extender is already switched back, run `gatekit se
  remove` again later`), and the kept config and the record go.
- `--installed-game` together with `--instance` is refused by both.

---

## 5. Instances (`src/GateKit.Copies/Instance.cs`)

Layout:
```
<instances root>/<name>/game/bin     a copy of the game's bin (about 310 MB): own Script Extender settings and build, own logs
<instances root>/<name>/game/Data    a real folder whose entries are links to the game's Data; Data/Mods and Data/Video are real
<instances root>/<name>/home         the private profile on Windows (the game's USERPROFILE); on Linux a Wine prefix, <name>/prefix
<state>/instances/<name>             the copy's records and logs (runs, se-logs, loose.json, owner.json, ...)
```
`<instances root>` = `GATEKIT_INSTANCES`, else `<game's parent>/Baldurs Gate 3 GateKit`. It must be
on the game's drive (hard links). The real game folder and profile are only read. On Windows the
game finds the private profile through `USERPROFILE`, `LOCALAPPDATA` and `APPDATA` of its process.

The clone is brought up to date at every `up` and `instances create` (`SyncGame`):
1. `no game in <game>` unless the real `bin` holds `steam_appid.txt` or a `bg3*.exe`.
2. Every file under the real `bin`, except the folder `db`, `BG3ScriptExtender.dll`, `BG3ScriptExtender.pdb`, `ScriptExtenderUpdaterConfig.json` and `*.log`: `ScriptExtenderSettings.json` only while the clone has none (afterwards it is the copy's own); `steam_appid.txt` *written* as `2147483646`, an app id that is not the game's, so Steam does not mirror the clone's saves into its cloud; every other file copied when it changed.
3. The loader config of the clone, when the Script Extender cache is known and no local build is installed in the clone (no record and no `bin/BG3ScriptExtender.dll`): the real one without `DebugLoadSE`, plus `CachePath` = the real cache, so the clone does not download again; written only when different. With a local build installed the switch writes it (section 4).
4. `Data`: every entry but `Mods` and `Video` is linked - a folder by a folder link (a link to another target is replaced; a real folder the copy made its own is left), a file by a hard link (replaced when it is another file: the game was updated; a failure -> `could not hard-link ... Instances must be on the game's drive; set GATEKIT_INSTANCES to a folder there.`). `Data/Mods` and `Data/Video` are real folders whose entries are linked the same way, except the installed game's hot-reload mods and empty files (the installed game's splash stub).

Before loose files are written under `Data`, every link on the way becomes an empty real folder
(`OwnFolder`), and the place is checked to lie inside the instance, links resolved (`Inside`).

The profile (`SyncProfile`):
- The platform prepares the private home or prefix.
- Seeded once, only what the clone does not have yet, from the real profile: `graphicSettings.lsx`, `PlayerProfiles/playerprofiles8.lsf`, `PlayerProfiles/Public/{UILayout.lsx,config.lsf,profile8.lsf,analyticsUserData.lsf}`.
- `PlayerProfiles/<n>` exists for each of `Public` and `Debug_Client_Profile_1..4`; the saves a copy made under a debug profile are merged into Public, and each debug profile gets Public's `config.lsf` once (the game's options and agreements are per player profile).
- `Mods`: with `--mods` naming any, every `*.pak` of the real `Mods` but the bridge paks is copied when it changed; any other pak of the clone's but the bridge paks is deleted. The bridge paks are built per copy.

`Spread` (at `up`): Public's mod list is copied into each debug profile and Public's saves merged
into theirs. `destroy`: 2.7.

---

## 6. The bridge channel

What `gatekit` writes to the game and reads from it - the files, the queue, the heartbeats, the
answers - is [bridge-protocol.md](bridge-protocol.md). A game of this copy is a process whose
executable lies in this copy's `bin`; other copies' games are never seen, awaited or killed.

## 7. Paths and files

Where everything GateKit keeps lies: [docs/design/home.md](../design/home.md) and section 8.1; a
copy's own files: sections 2, 4 and 5.

---

## 8. `gatekit`, the toolkit

`src/GateKit.Platform` (OS layer), `src/GateKit.Copies` (the copies), `src/GateKit.Cli` (assembly `gatekit`). Commands: `doctor`, `game verify` (8.2) `build up down status instances [list|create NAME|rename NAME NEW|destroy NAME] crashes save destroy exec shot key sound [list|set|mute|unmute|unfocused] link reload watch bridge-reload bridge reload logs checkpoint reset se status|install|remove firewall`, global `--instance NAME` and `--installed-game`. `deck` and `window [list|maximize|grid|label|release|mirror|adopt|fps]` come from `GateKit.Deck` (8.8). `test`, `run` and `systems` (section 3) come from `GateKit.Testing`, which the CLI finds by its `CreateCommand()` classes. `names` comes from `GateKit.Names` and `wiki` from `GateKit.Wiki` (8.9). `symbols`, `decomp`, `registry` and `refs` come from `GateKit.Symbols` (8.15, 8.16, 8.18-8.21, 8.23).

`--instance` is an option of the root and parses before any command. The commands of
`GateKit.Symbols` - `symbols`, `decomp`, `registry`, `refs` - read the home's databases and the
repository and no game copy: given `--instance NAME` they refuse with `` `gatekit <command>` reads
no game copy: --instance NAME means nothing here; leave it out (nothing was done)`` and exit 1.
`GATEKIT_INSTANCE` in the environment is not refused.

### 8.1 Settings

Everything GateKit keeps lives in one **home** folder; its layout and the one exception (the
game copies) are in [docs/design/home.md](../design/home.md). The home is, in this order:

1. `GATEKIT_HOME`;
2. `"home"` in the nearest `gatekit.json` in the working folder or a parent;
3. the same in the program's folder or a parent (a run copy searches the folder it was copied from);
4. the same in `<user config folder>/GateKit/gatekit.json` (Windows `%APPDATA%`, Linux `~/.config`);
5. else the per-user default (`%LOCALAPPDATA%\GateKit`, `~/.local/share/GateKit`), which `doctor` reports as not configured.

`GATEKIT_CONFIG` names a `gatekit.json` that is read before all others. Every key is taken from
the nearest file that has it; a relative path is relative to that file. Comments and trailing
commas are allowed. A folder holding a file `.gatekit-isolated` ends the search upwards: below
it, the settings above it are not seen (the tests' scratch, `<home>/scratch/tests`, has one).

A location's own variable overrides that location alone; nothing requires one once the home is
set. `gatekit doctor` lists every location with its source (variable, gatekit.json, home,
default, discovered, unset), `doctor --json` in the shape `gatekit.doctor/1`. The game and the
Toolkit, when nothing names them, are shown where the tools find them - `discovered`, with the
Steam library they were found in - and `unset` only when no library holds them (saying which were
searched). Every game copy is listed, those the state knows and those the copies' folder holds:
its folder, who started it last and the owner record that says so (the home's state, else a
checkout's leftover state, named as such), or where an owner record was looked for when there is
none; then its mod list (`--json`: `root`, `owner_from`, `owner_looked_in`, `mods`, `mods_from`,
`extra_mods`).

| Location | Override | gatekit.json | In the home |
|---|---|---|---|
| game index | `BG3_INDEX_DB` | - | `db/index/gameindex.sqlite` |
| symbol database | `BG3_SYMBOLS_DB` | - | `db/symbols/symbols.sqlite` |
| pseudo-code work (`decomp.sqlite`) | `BG3_DECOMP_WORK` | - | `db/decomp` |
| external tools | `BG3_DECOMP_TOOLS` | - | `tools` |
| macOS reference symbols | `BG3_MACOS_SYMBOLS` | - | `reference/macos-symbols` |
| state of the copies | `GATEKIT_STATE` | `state` | `state` |
| wiki cache | `GATEKIT_WIKI_CACHE` | - | `cache/wiki` |
| mods unpacked to be read | - | - | `cache/unpack` |
| MCP tool plugins | `GATEKIT_MCP_TOOLS` | - | the checkout's `mcp-tools`, else `mcp-tools` |
| Script Extender source tree | `GATEKIT_SE_FORK`, `BG3SE_SRC` | `seFork` | none |
| Toolkit | `BG3_TOOLKIT` | `toolkit` | none: found in a Steam library (`appmanifest_2934770.acf`) |

A database that a command reads must exist and hold tables; otherwise the command fails with the
path, the setting that moves it and `gatekit doctor`. A command whose answer a database only
enriches says on stderr what it left out; one that draws on several (`data query search`, `about`,
`schema`) and finds none of them fails, naming every path - an empty answer from no database is
never given. Only the build commands create databases, and they say where.

GateKit's own settings:

| Setting | Environment | gatekit.json | Default |
|---|---|---|---|
| game folder | `BG3_GAME` | `game` | Baldur's Gate 3 in a Steam library (Windows: registry `SteamPath`; Linux: `~/.steam/steam`, `~/.local/share/Steam`, Flatpak, Snap; then `steamapps/libraryfolders.vdf` and `appmanifest_1086940.acf`) |
| executable | `GATEKIT_EXE` | `exe` | `bg3_dx11.exe` |
| real profile | `GATEKIT_PROFILE` | `profile` | Windows `%LOCALAPPDATA%\Larian Studios\Baldur's Gate 3`; Linux `<library>/steamapps/compatdata/1086940/pfx/drive_c/users/steamuser/AppData/Local/Larian Studios/Baldur's Gate 3` |
| instances | `GATEKIT_INSTANCES` | `instances` | `<game's parent>/Baldurs Gate 3 GateKit` (on the game's volume: copies are hard links) |
| Script Extender | `GATEKIT_SE` | `scriptExtender` | stock; a folder with `BG3ScriptExtender.dll` (or `stock`) |
| Script Extender's developer mode | `GATEKIT_SE_DEVELOPER_MODE` | - | on; `0` starts the game as a mod's user has it (no developer-only functions of the extender) |
| packer | `GATEKIT_PACKER` | `packer` | `builtin`: LSLib in this process. `divine` uses LSLib's Divine instead |
| Divine | `GATEKIT_DIVINE` | `divine` | none; only `GATEKIT_PACKER=divine` needs it |
| runtime for Divine | `GATEKIT_DOTNET_ROOT` | `dotnetRoot` | none: Divine runs with `DOTNET_ROLL_FORWARD=Major` on the newest installed runtime |
| start timeout | `GATEKIT_START_TIMEOUT` | `startTimeout` | 75 s to the first heartbeat |
| bridge mods | `GATEKIT_BRIDGE` | `bridge` | `<repo>/mods` |
| instance | `GATEKIT_INSTANCE` or `--instance` | `instance` | none (the installed game) |
| Proton (Linux) | `GATEKIT_PROTON` | - | `umu-run` on PATH, else the newest `steamapps/common/Proton*/proton` |


### 8.2 `game verify`: the game's files as their store delivered them (`src/GateKit.Copies/GameVerifier.cs`)

`gatekit [--instance NAME] game verify [--quick] [--json] [--limit N] [--threads N] [--source ID]
[--reference FILE] [--write-reference [--out FILE]]` holds the installed game, or a copy, to the
file list of a **source of invariants** (`IInvariantSource`, `src/GateKit.Copies/Invariants.cs`).
The check itself knows no store. The sources are asked in this order, and the first that applies
is used; `--source` asks only one, `--reference FILE` uses that list without matching. The report
names every source asked and why it was or was not used.

| Source | Applies when | Lists |
|---|---|---|
| `steam` | the installed game is `<library>/steamapps/common/<installdir>` of an app manifest | Steam's depot manifests on this machine (below) |
| `reference` | a list in `<home>/reference/game-files/*.json` holds every executable beside the game's with the same SHA-256 | that list |
| `gog` | never: it names a GOG install (`goggame-<product>.info`) and says it is not implemented, since GOG's per-file lists come from its servers and none is on disk | - |

With no source, the verdict is `no-invariants` and only the fingerprint is recorded; it is never
`clean`.

**A reference list** (`reference/game-files/1`, `schemas/reference/game-files.schema.json`) is
GateKit's own: the build, the source and parts that proved it, every folder, and every file with
its size, SHA-256 and SHA-1 (no hash for an empty file) - hashes and no content, so it can be
published. `--write-reference` writes one to `<home>/reference/game-files/<app>-<build>.json` (or
`--out`) after a full pass of the installed game whose verdict by another source is `clean` or
`extras`; it lists only what the source lists. A quick pass, a copy, a reference list as the proof,
or any other verdict is refused. A file a list gives a SHA-256 for is compared by SHA-256, else by
SHA-1. The Steam executable imports `steam_api64.dll`, so another store's executable is another
file: a list made from Steam matches Steam installs, and serves an install from another store only
when named with `--reference`, its executables then reported as modified.

**Steam's record**, read without a network:

- the app manifest `<library>/steamapps/appmanifest_<app>.acf` of the installed game (the one
  whose `installdir` is the game folder's name): `buildid` and `InstalledDepots` (depot, manifest,
  `dlcappid`). `SharedDepots` install into another app's folder and are named in a note, not held;
- each installed depot's manifest `<depot>_<manifest>.manifest` in `<Steam>/depotcache` (or the
  library's own `depotcache`): every file, folder and link the depot delivers, with its size, SHA-1
  and flags. Format: little-endian sections `uint32 magic, uint32 length, protobuf` -
  `0x71F617D0` payload (`ContentManifestPayload`: `mappings` = filename 1, size 2, flags 3,
  sha_filename 4, sha_content 5, chunks 6, linktarget 7), `0x1F4812BE` metadata (depot 1,
  manifest 2, creation time 3, filenames encrypted 4), `0x1B81B817` signature, `0x32C415AB` end
  (no length); a manifest that starts with a zip header is the same bytes zipped
  (`src/GateKit.Platform/DepotManifest.cs`). A depot whose manifest is in no depot cache is
  `absent`, one the reader refuses `unreadable`, one with encrypted names `encrypted`; none of
  them is guessed. Each depot is a *part* of the source; a reference list is one part.

Every listed entry is checked: a folder must be a folder, a file must have its size,
and its hash when it is hashed. `--quick` hashes only `.exe` and `.dll` files and those the source
flags executable; the full pass hashes every file. Files are hashed in parallel (`--threads`,
default one per processor, at most 16), largest first; progress goes to stderr, in place on a
terminal and every 10 s otherwise. A file several parts list matches when it is any of them (a
note names them). Folder links are followed, so a copy's `Data` is read through its links.

| Finding | What it is |
|---|---|
| modified | a listed file whose `size` or `content` differs, a folder where a file is listed or the reverse (`kind`), a link that points elsewhere (`link`); `userConfig` when the store marks the file as the user's to change |
| missing | a listed file or folder that is not there |
| extra | a file on disk that the source does not list; a folder link whose target is gone is one too, with a note |
| benign | an extra a rule names, listed under its rule with the rule's reason |
| outside | for a copy: listed entries outside `bin` and `Data`, which a copy does not hold; counted, not checked |

The rules (`GameVerifier.Benign`), each matched against the path inside the game folder:

| Rule | Paths | Why |
|---|---|---|
| `game-logs` | `bin/*.log`, `Launcher/*.log` | the game's and its launcher's own logs |
| `script-extender-loader` | `bin/DWrite.dll` | Script Extender's loader; changes no file of the game |
| `script-extender-settings` | `bin/ScriptExtenderSettings.json`, `bin/ScriptExtenderUpdaterConfig.json` | Script Extender's settings |
| `steam-appid` | `**/steam_appid.txt` | names the app to the Steam API; a copy writes a number that is no app |

Anything else is an extra, Script Extender's own build (`BG3ScriptExtender.dll`) included.

| Verdict | When | Exit |
|---|---|---|
| `clean` | nothing modified or missing, every part read, no extra but benign ones | 0 |
| `extras` | as `clean`, with extras | 0 |
| `dirty` | anything modified or missing | 1 |
| `incomplete` | a part was not read (its files then show as extras) | 2 |
| `no-invariants` | no source applies to this install: nothing to hold it to; only the fingerprint is recorded | 2 |

A verdict is said with its mode: `clean (quick)` has not read the packages' bytes.

The text report lists at most `--limit` (50; 0 for all) lines per finding; `--json` prints the
whole report as `game/verify/1` (`schemas/game/verify.schema.json`).

**The fingerprint** (`state/game-fingerprint/1`, `game-fingerprint.json`) is written into the
state folder for the installed game and into the copy's record (`<state>/instances/<name>`) for a
copy; a copy whose record does not exist is read and its record is not created. It holds the
source, app, build, depot manifests or reference list, the size, time written and SHA-256 of every `.exe` and `.dll` beside
the executable, the verdict, its mode, the counts and when. A copy gets one each time it is made or
refreshed (`instances create`, `up`): verdict `unverified`, and under `from` the installed game's
verdict while that still describes the installed game and the copy's executables and libraries
are the same files.

The command reads the game folder, Steam's folders and the reference lists, and writes nothing but
the fingerprint and, when asked, a reference list; it does not start the game.

`gatekit doctor` shows the verdict as the check `game files` (a warning unless `clean` at the
current build with the executables and libraries unchanged since, by size and time). `data build`
and `symbols build` write `game_build` (the Steam build, else the build of the reference list the
game matched) and
`game_verdict` (the fingerprint's verdict while it still matches that game folder and build, else
`unverified`) into the database's `meta` table. A database made without these rows gets them from
evidence instead: `data stamp` (8.7) for an index, `decomp stamp` (8.23) for the pseudo-code
database, which is never made again from scratch. The evidence is the SHA-256 of the files the
database was made from, looked up in the reference lists (`BuildEvidence`,
`src/GateKit.Platform/BuildEvidence.cs`); `game_build` may name several builds, comma-separated,
when the same bytes belong to each.

Doctor gives each database a line `<database> game`: `ok` with the build and the verdict when it
was made from the installed game's build with every listed file as delivered (`clean` or
`extras`: the extras are the `game files` check's to name), else a warning saying the build it was
made from, the game's build, and, for a database with a `binary_meta` table, each game binary's
build or SHA-256.

### 8.3 Bridge mods, packing, window, keys and screenshots

1. **Two bridge mods.** The bridge is `mods/GateKit` + `mods/Weave`. `gatekit` reads each mod's identity and dependencies from its `meta.lsx`, packs each into `<Folder>.pak`, installs both, enables both in the mod list in dependency order (Weave first; if only some are listed they are taken out and appended again), keeps both out of an instance's copied paks, and `bridge-reload` links both. A single mod root (a folder holding `Mods/` itself) also works as `GATEKIT_BRIDGE`.
2. **Script Extender default is stock**: set `GATEKIT_SE`, or `se install` (section 4); Weave and the bridge need GateKit's v34-based build.
3. **Packing**: `IModPacker`, by default `LSLibPacker` — LSLib in this process (the submodule `external/lslib`, compiled by `external/lslib-core`), writing a version 18 package compressed with LZ4 at its fast level, the same thing Divine writes for `-g bg3 -c lz4` and what the game's own packages use. Nothing has to be installed and nothing is native, so `build` works wherever `gatekit` does. A file or folder whose name starts with `.` is left out, as Divine leaves it out, but only when the name is inside the mod root: a repository under `.claude/worktrees` packs normally. A source with no files in it is an error rather than an empty package. `GATEKIT_PACKER=divine` picks `DivinePacker` instead (LSLib's Divine as a process, `GATEKIT_DIVINE`), kept for anyone who would rather use their own copy; it packs such a source from a temp copy, because Divine itself writes an empty package there, and a package of 64 bytes or less is an error.
4. **Prelude** sent with every chunk is `local GK = Mods.GateKit.GK` (no `DK` alias); protocol 0 (one file for every call) is not spoken.
5. **Window and console**: the game window is found by process id and class `SDL_app` (title prefix as fallback), never the Script Extender console. `up` writes `CreateConsole=false`, `ShiftOpensConsole=false` (GateKit's extender build reads it; Shift held at the start otherwise opens the console anyway; in an instance the loader's `ScriptExtenderUpdaterConfig.json` gets the same key, written as a new file) and `LogRuntime=true`, so a game GateKit starts has no console window at all; a classic console window (`ConsoleWindowClass`) of the starting copy that opens anyway is hidden - never closed, which would end the game - by the start's foreground guard, and once more when the start is over (a console hosted in Windows Terminal is not touched) and `logs` reads `<STATE>\se-logs\Extender Runtime *.log` (`logs --story` reads `Osiris Runtime *.log` from the same folder, `up --story-log`). A game started another way has no such file, and is read from its console window by a short-lived copy of `gatekit` (`gatekit __read-console PID`, attached with `AttachConsole`). No PowerShell anywhere.
6. **Options**: `exec --timeout S` (default 20), `shot --width N` (default 1150; `.png` writes PNG, else JPEG quality 82), `reset --keep-scenes`, `instances create NAME` (clone and profile without a start), `instances destroy NAME`, `bridge reload` (alias), `se status|install|remove`.
7. **Output**: `status` prints `platform:` and each heartbeat as `{gameState, boot, done, queue, age}`. Redirected output is UTF-8.
8. **Errors**: a missing `modsettings.lsx` at backup, instance file errors and packing failures are `ERROR:` lines (exit 1).
9. **A start never takes the foreground**: the game is started shown-but-not-activated, and while `up` runs the platform watches the foreground; a game window that grabs it is put at the bottom of the pile and the window the user was typing in gets the keyboard back. The watch stops as soon as the user activates something that is not a game, so it never fights them. On Linux it does nothing (the window manager decides).
10. **Linux (Proton)**: paths, prefixes, launch (`umu-run` or `proton run`, `WINEDLLOVERRIDES="DWrite=n,b"`, `setsid`, no inherited stdio), processes (`/proc/*/cmdline`, Wine's `Z:\` paths mapped back), hard links (`link(2)`) and folder symlinks are implemented. An instance gets its own prefix in `<instance>/prefix`. `key` and `shot` work everywhere through the bridge (points 11 and 12); the console reader says it is not available; `logs --story` works. None of it is tested on a Linux machine.
11. **`key` goes through the game, not the window.** The name is validated against `GameKeys` (key names and Windows virtual-key codes); when the client side of the bridge is answering the key is pressed by `GK.Key(<name>)`, which injects an SDL key event into the game's own input queue. That needs no window, no focus and no operating-system call, so it is the same on every platform. `--hold <s>` (0-30) keeps the key down that long through the bridge (`GK.Key(<name>, nil, <s>)`: InjectKeyDown, InjectKeyUp on the game's timer) and returns once it is up; a test's `game.key(name, hold)` does the same. A hold has no window fallback. Only when the bridge is not there does `gatekit` fall back to `IGamePlatform.PostKey` (Windows `PostMessage`); where neither is available the error names both.
12. **`shot` goes through the game, not the window.** When the client side of the bridge is
    answering, `GK.Shot("Gwynerva_GateKit/shot_<pid>_<ticks>.png")` asks GateKit's Script
    Extender build for the frame the renderer is about to show; the extender copies it out of the
    swap chain before it draws any window of its own, encodes a PNG on a thread of its own and
    renames it into the channel folder. `gatekit` waits up to 10 s for that file, reads it,
    deletes it, and then scales and saves it (`--width`, `.png` or JPEG 82).
    The picture is the game alone — no extender overlay, no console — and the game is neither
    paused nor moved to take it. The window is photographed with `IGamePlatform.CaptureWindow`
    only when the bridge is not there or its renderer cannot read frames (only the DX11 backend
    can; on the Vulkan executable `GK.Shot` returns false and the window is used). Most of its
    time is the encoding of the full-size PNG, none of it on the thread the game renders with.

### 8.4 `sound`

`gatekit [--instance NAME] sound [list]` prints the game's twelve sound categories, one per line -
GateKit's short name, the game's own `ConfigKey`, the volume from 0 to 100, `(muted)` where
`sound mute` is holding one at 0 - and last the state of the game's own mute-when-not-in-front
flag. `RumbleVolume` has no volume (`-`): it is controller vibration, not sound.

| Command | What it does |
|---|---|
| `sound set CATEGORY VOLUME` | one category to 0..100 (a trailing `%` is allowed). Prints what was set. |
| `sound mute [CATEGORY]` | to 0, remembering what it was; without a category the master, which silences the copy. Prints the volume remembered. |
| `sound unmute [CATEGORY]` | puts back what `mute` took away; without a category every category `mute` silenced in this copy. Prints the keys restored. |
| `sound unfocused [on\|off]` | the game's own `MuteSoundWhenNotFocused`; without a word it is only read. |

A category is the game's key (`MusicVolume`), GateKit's short name (`music`) or the Wwise RTPC
name (`RTPC_Volume_Music`), and nothing else: anything with a character that is not a letter, a
digit or `_` is refused before a chunk is built. The twelve keys, in the options screen's order:
`MasterVolume` (master), `FxVolume` (fx), `AmbientVolume` (ambient), `MusicVolume` (music),
`MusicFightVolume` (fight), `CutsceneVolume` (cutscene), `UIVolume` (ui), `VoiceVolume` (voice),
`VoiceVolumeDialog` (dialogue), `VoiceVolumeNarrator` (narrator), `VoiceVolumeOverhead`
(overhead), `RumbleVolume` (rumble).

Everything runs on the client through the bridge (`GK.Sound`, [lua-api.md](lua-api.md) section 8),
so it needs no audio API of the operating system and is the same on every platform; as every
chunk, it reaches the installed game only when that is named (1.1). It changes the copy's
running game only: the game does not publish the slider values behind its options
screen, so GateKit writes the Wwise values underneath them and never the player's own settings.
The options screen therefore keeps showing the player's positions, and nothing is written to a
profile. A volume set this way is lost when the game restarts, and a `reset` leaves it as it is.

### 8.5 Prebuilt databases: `export-release` and `install`

The index and the symbol database can be fetched instead of built. Building is first-class; these
are the alternative. Implemented in `src/GateKit.Release` and
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
  `repository`, `tag`, `manifest`, optional `baseUrl`. With no `tag` (the pin in this repository
  has `null`) both commands refuse and name the build command instead.
- An installed database gets a receipt beside it (`<db>.release.json`). `gatekit doctor` reports
  each database as present or missing and says whether it was built here or installed, and from
  where.

### 8.6 Languages the installation does not have: `data build --localization`

`gatekit data build --localization DIR` (repeatable) reads language packs from a folder shaped
like `Data/Localization` as well as from the game's own: `DIR/*.pak` and `DIR/<language>/*.pak`
for each language in `--languages`. A language the installation was not set to is indexed this
way without touching the installation, from a Steam depot fetched on its own. A language's name
is its folder's name (`Chinese`, `ChineseTraditional`, `BrazilianPortuguese`, `LatinSpanish`).
Implemented in `GamePackages.Find`; tested in `unit/GateKit.GameData.UnitTests/PackageFindTests.cs`.

### 8.7 `data stamp`: the build of an index from its recorded inputs (`src/GateKit.GameData/Index/IndexStamp.cs`)

`gatekit data [--db FILE] stamp [--game DIR | --data DIR] [--localization DIR]... [--jobs N] [--dry-run]`
holds an index to what it says it was made from. Every input of its `inputs` row (each package
with its parts, and the Osiris header, by size and time written) is looked for in the game folder
the index records (or `--game`/`--data`) and the `--localization` folders, and is one of:

| Outcome | When |
|---|---|
| `listed` | found with the recorded size and time, and the SHA-256 of every part is in a reference list under the same path |
| `unlisted` | found as recorded, a part in no reference list |
| `changed` | found with another size or time than recorded |
| `absent` | not found |

`game_build` is the build every listed input belongs to (not written when they share none),
`game_verdict` is `clean` only when every input is listed, else `unverified`, and
`game_evidence` says how many inputs were listed and names the others. The three rows replace
earlier ones in one transaction; `--dry-run` prints them and writes nothing. An index whose
`meta` has no `inputs` row is refused: there is nothing to hold it to. Packages are hashed in
parallel (`--jobs`).

### 8.8 The deck (`src/GateKit.Deck`, `docs/design/deck.md`)

| Command | What it does |
|---|---|
| `deck [--all]` | opens the window that holds every running game, and serves it until it closes. **It takes hold only of the copies this home started**; anything else is listed by `window list` as "not this home's", said once in the deck's own output, and left entirely alone - window, frame rate and sound. `--all` (or `GATEKIT_DECK_ADOPT=all` / `"deckAdopt"`) takes every copy on the machine, and naming a copy with `window maximize|mirror|adopt` takes that one, which the deck also says out loud; `window release NAME` gives it back and takes the name off again. One per machine; a second refuses to start. Windows only (`IGamePlatform.CanHostWindows`); elsewhere it says why not. The window never takes the foreground: `WS_EX_NOACTIVATE`, `SW_SHOWNOACTIVATE`, nothing activating it, and the style dropped only when the user clicks a tile. It wears `assets/brand/logo-round.ico`. |
| `deck close` | closes a running deck; every window goes back to the desktop and no game is touched. |
| `window list [--json]` | every running copy: instance, label, process id, whether this home started it (`mine`), its tile; plus the tiles kept for a game that is away. Works without a deck. |
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
if there is none. Nothing else in GateKit depends on any of it: `up` starts a game the same way with a
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
taking a copy somebody else started - another person, another program, a game being played - is not
something to do by accident: a person who opens a deck for an unrelated reason must see nothing
happen to the game they were playing. Every home keeps its own records of the instances it made
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
or a patched game whose layout the cap does not recognise - is named once in the deck's output,
with what that costs (a budget that does nothing for it), and so is one that cannot be quietened.
A copy is given twenty seconds in its tile before the deck will say any of this, because a game
that is still loading has not refused anything. `tests/_selftest/framecap.lua` is the same guard in the
test suite: it asserts the caps reach every field, that a capped game really draws at the rate it
was given, and that lifting the cap lifts the engine's own background throttle - so a game patch
that moves the offsets is found by `gatekit test` rather than by someone wondering why several
copies are slow.

**Sound follows what you are looking at.** With the same rule: the tile in view is heard and the
rest are muted, through `GK.Sound` (8.4; [lua-api.md](lua-api.md) section 8) and so through the game's own volume settings. With a
grid and nothing clicked, nobody is being watched in particular and no copy is silenced. Each tile's
caption carries a level bar and a mute; touching either pins that copy and the deck stops policing
it, because listening to two fights at once is a legitimate thing to want. `GATEKIT_DECK_SOUND` /
`"deckSound"` is `watched` (the default) or `off`. A copy leaving the deck is unmuted.

The game's side of both is in GateKit's Script Extender build: `Ext.ClientRender.FrameCap()`,
`SetFrameCap(fps)` and `SetLostFocusFrameCap(fps)` write `BaseApp`'s three frame caps and the
microseconds its clock derives from them, with no hook and no patch, and refuse to write anything
unless the engine's own arithmetic still holds at the offsets they know.

Finding the game window also looks through the children of other windows: a game held in a
deck **is** a child window, and `shot`, `key` and the bridge must find it wherever it hangs.

`doctor` has two checks of its own for these: **deck** (running, or none - never a failure) and
**firewall** (whether a rule allows the game to listen, read from the registry).

A firewall rule is held against an executable's **path**, and every instance is a copy in a folder
of its own, so the installed game being allowed says nothing about a copy: Windows asks about each
one separately, in an elevated window over whatever the person was doing. `doctor` therefore reads
every copy, not only the installed game, and `gatekit firewall` lists them and prints the single
line that covers all the ones still missing a rule - one `New-NetFirewallRule` each, named after
its copy. GateKit never writes a firewall rule itself: that is a change to the machine's security,
and the person makes it knowingly or not at all. Making a copy says the same thing before the game
in it first starts, so the dialog is never a surprise.

### 8.9 `names` and `wiki` (`src/GateKit.Names`, `src/GateKit.Wiki`)

Neither needs a game.

**`gatekit names`** works on the layouts of the record (`--coverage DIR`, else `GATEKIT_COVERAGE`,
else the repository this runs from): the registry's types made from layouts when the registry
beside the record holds them, else the layout files `research/coverage/layouts/**/*.json`. `--json` on every
subcommand.

| Subcommand | What it does | Writes |
|---|---|---|
| `tally [--ns PREFIX] [--strict]` | the measure: fields written down, split into **named with evidence** (rung `inferred`, `toolkit` or `game`), **the extender's name, unchecked** (rung `extender`), a name at rung `unknown` (shown only when there is one), and the machine's (padding apart; the rest is owed a name); then names and machine names per rung, strongest first. Counted over layouts whose status is `confirmed` or `declared`, not systems, with at least one field. `--json` keeps `named` (every name that is not a machine's, whatever its rung), `machine`, `padding`, `owed`, `facts`, `by_source`, and adds `named_with_evidence`, `extender_unchecked`, `named_unknown` and `by_rung` (`{rung: {named, machine}}`). A file that cannot be read is left out with a note; `--strict` makes that exit 1 | nothing |
| `worst [--ns] [--min-fields 6] [--top 30] [--by share\|demand] [--demand FILE]` | `share` (default): the components with the largest share of machine names. `demand`: in the types a demand file (`mod demand`, default `<coverage>/demand.jsonl`) lists, every field still carrying a machine's name or a rung `unknown`/`extender` (padding apart), ordered by the mods naming that field (its name or its `legacy` name), then by the mods using the type | nothing |
| `demand report [--demand FILE] [--top 25]` | a demand file joined with the record: the headline counts; the top types by mods (uses, seen, verified, fields named with evidence / written down, open demanded fields, systems naming / writing it); (a) demanded types never seen alive (`seen/index.json`); (b) seen, not verified (`coverage.json`); (c) every demanded field with no evidence-backed name - `machine`, `extender`, `unknown`, `not in the layout` (neither a field nor a nested struct's field carries the name or its legacy name) or `no layout`; (d) demanded types no described system (`systems/**/*.json`: reads, writes, uses) names, and those none writes or adds; (e) the Osiris names by mods, with the data index's kind and arity and every name whose arity the index does not hold (a `Name`/N of a listener against the arity; a literal `Osi.Name(...)` against the in parameters) | nothing |
| `suggest [component] [--ns] [--all] [--known] [--db]` | for the fields still carrying a machine's name: what the symbol database knows at that offset - the game's own member names (`ls::FieldMeta` strings of the executable for type and offset, `fieldmeta.json` under `BG3_MACOS_SYMBOLS` for the names), Toolkit accessors, and Script Extender's own headers (never the header generated from this record). `=` marks a fact that settles the name by itself | nothing |
| `adopt [--ns] [--write] [--db]` | the names such facts settle, each with the evidence it was read from; listed, or written with `--write` | layouts, with `--write` |
| `set <component> <offset> <name> --source S --evidence TEXT [--type] [--legacy] [--struct] [--force] [--dry-run]`, or `set --from FILE` | writes a name. Refused: no evidence (whatever the source); a source outside the ladder; a machine-shaped name unless the source is `unknown`; (the ladder, weakest first: `unknown`, `extender`, `inferred`, `toolkit`, `game`) an offset where no field starts; a name another field has; a weaker source over a stronger one without `--force`. A real name that is replaced is kept as `legacy`. `--from` takes one object or an array (`-` for standard input); a key it does not know is refused; a batch is written whole or not at all | layouts |
| `extender [--ns] [--write] [--db]` | the fields at rung `unknown` whose name (not a machine's) is the one Script Extender's author's headers declare at the same offset of the same type (`se_fields`, never the header generated from this record): listed, or raised to rung `extender` with `--write`, evidence `Script Extender declares <type>::<name> at +0xNN (<header>)`. Then lists the `inferred` names whose evidence cites Script Extender and no code (a test on words: a list for a reader; nothing is demoted) | layouts, with `--write` |
| `take <folder> [--batch extender] [--write] [--db]` | takes candidate layouts (as `symbols se-layouts` writes them) into the record, for types no layout names yet: each becomes a type entry of the registry (`registry/types/<scope>/<Name>.json`, its batch kept in its `survey`), refused when the registry's check refuses the entry; only a record whose registry holds no `types` gets layout files under `layouts/<batch>/` instead (`LayoutStore.Add`). A candidate goes in only with status `declared`, an `ext_name`, every field at rung `extender` (or `unknown` where the extender's own name is a machine's), the size `coverage.json` registers where it knows one, and the checks of `generate` (order, no overlap, alignment, the size covered exactly); every other is printed `REFUSED <file>: <reason>`. Written in the record's shape. Without `--write` nothing is written | registry type entries (or layout files), with `--write` |
| `pending` | what `bg3_recipe` recorded as `fields` in `findings.jsonl` and no layout carries yet. A row whose evidence is `sighting` is never offered | nothing |
| `generate [--tree DIR] [--check] [--out DIR] [--db]` | Script Extender's declarations: `GameDefinitions/Components/Coverage.h` (line feeds; every field commented with its source and evidence; `[[bg3::legacy(Old)]]` where a layout carries one) and the marked block in `Enumerations/ECS.inl` (carriage returns kept, replaced in place on a second run). The tree is `--tree`, else `GATEKIT_SE_FORK`, else `BG3SE_SRC`; a tree inside this repository is refused. Every layout is validated (a field type is sized as a primitive, a container, a nested or external type of the layout, or a struct or enumeration - by its underlying type - of Script Extender's, from the symbol database); one in the build that does not hold together stops the run with exit 1 and nothing written. Types Script Extender's own headers declare are left out | the fork, or `--out` |

A layout is written back in the shape the files already have (two spaces, non-ASCII text as it is,
line feeds, a final newline only where there was one), so a naming changes the lines of that field
and nothing else; the file is replaced whole, and only if nobody changed it meanwhile.

**`gatekit wiki`** reads bg3.wiki under a generic client name and keeps each answer in
`GATEKIT_WIKI_CACHE` (default `<local app data>/GateKit/wiki-cache`), outside the repository: the
text and the rows are the wiki's. `--refresh` asks again; `--json` on every subcommand.

It reads two things. The wiki's **pages**, through its public API. And the wiki's **tables**: it
runs the Cargo extension, which stores one row per spell, action, passive, reaction, condition,
weapon, piece of equipment, item and camp event, most of them with the game's own identifier. The
API's `cargoquery` is closed to an anonymous reader; `Special:CargoExport` is not, and that is what
is read. A table is read whole the first time it is needed (one request for most) and every
question after that is answered from the cache.

| Subcommand | Answer | Exit 1 when |
|---|---|---|
| `card <key>` | the row of the wiki's tables that answers for a stats name (`Target_MistyStep`), a template name or GUID, a status's stack id, or a page title: the table, the page, the identifiers on the row, then every field that holds something - level, school, cost, range, concentration, classes, conditions applied, flags. Tables are searched in the order `actions`, `passives`, `reactions`, `conditions`, `weapons`, `equipment`, `misc_items`, `camp_events` (items first for a GUID), and the search stops at the first table that has it. Several rows when several carry the key | no row answers |
| `rules <Title> [--chars 6000]` | the page read from its markup, as sections in order: level, heading, text. JSON adds, per section, `links` (the titles it links to, in order) and `unsure` (what the wiki's own editors marked with a "verify" template); the text form prints each of those as `[the wiki is not sure: ...]`. The page's `revision` is given, to cite | no such page |
| `tables [name]` | the wiki's tables; with a name, that table's fields with their types | - |
| `table <name> [--where field=text ...] [--fields a,b] [--limit 40]` | rows of one table. A row passes a condition when the field contains the text (a list field: when one of its items does); case is ignored; a field the table does not have is refused with the fields it has. Prints the count, then `--fields` of the first rows (default `page,name,uid,uuid`) | no row passes |
| `snapshot` | reads every table `card` looks in into the cache and prints the row counts | - |
| `find <words>` | titles matching a full-text search, with the words around the match | nothing matches |
| `page <Title> [--chars 6000]` | the rendered page as plain text | no such page |
| `ids <Title>` | the game's identifiers on the page, read from its template parameters: `UID` (stats or template names), `UUID` (GUIDs), and where the page has them `stats` (the stats entry of an item or creature), `stack id` (a condition page usually carries this and no UID), `treasure` (a trader's tables). A page that shows identifiers it does not hold itself is read from its rendering | none found |
| `uid <StatsName> [--chars 6000]` | the page that carries a stats identifier: from the tables, and by a full-text search where they do not have it | no page carries it |

A row is kept tidy: field names with underscores, entities decoded, markup in a text field
flattened to what it says, empty values dropped, file fields (icons) not read. A field that holds
several identifiers, each with a note after a colon, gives the identifiers without the notes.

**The reader is the one place that talks to the wiki, and it is slow on purpose.** bg3.wiki is a
community site with a rate limit of its own. Every request reserves the next free moment in a file
beside the cache - 1.2 s apart for the API, 6 s for a table - so several processes calling `gatekit
wiki` at once queue up rather than arrive together. When the wiki answers "too many requests" the
refusal is remembered beside the cache and **nothing more is asked of that endpoint for fifteen
minutes**: the command says so and exits 1, and what is cached still answers. No caller can turn a
refusal into a loop, and nothing should be written to try.

A wiki that does not answer is said so on stderr, exit 1; what was asked before still answers from
the cache.

**What an answer is worth.** The wiki is what players and its editors know: which things a player
can do, what a thing needs, in what order the game seems to resolve it. That is context for
deciding what to try and for checking a reading of the code against - it is never evidence for a
name, a layout or a claim about what a system does, and a file in this repository cites a page by
title and revision and says what it says in its own words, never in the wiki's (the wiki's text is
CC BY-NC-SA or CC BY-SA; this repository is MIT).

### 8.10 `mod`: what mods use of the engine (`src/GateKit.Mods`)

A static scan of a mod's text, and its files unpacked to be read. Nothing of a mod is executed or loaded; a `.pak` is read with
GateKit's own package code, any other archive or binary is left alone. The extender's component
names are mapped to engine types from the Script Extender source tree (`--tree`, else
`GATEKIT_SE_FORK`, else `BG3SE_SRC`: the component macros, `DEFN_BOOST`, the Lua modules, the events
of `Events.inl`, `stats::FunctorId`), with the record's `ext_name` pairs filling in what the tree
lacks; the data index (`--index`) checks Osiris names and tells the game's own files. Without an
index Osiris names are not checked and goals and overrides are not read (said on stderr).

| Subcommand | What it does | Writes |
|---|---|---|
| `usage <path>... [--json] [--out DIR]` | one document per mod, schema `gatekit.mod-usage/1` (`scanner` = the scanner's version; every list sorted). Lua (comments blanked; a `---@meta` definitions file is counted and not scanned): components in `GetComponent`/`CreateComponent`/`RemoveComponent`/`HasRawComponent`/`GetAllEntitiesWithComponent`/`Subscribe`/`OnChange`/`OnCreate*`/`OnDestroy*`/`Replicate` with a literal name, `x.Name` (a name that is also a member name of the extender's structs or one plain word counts only on a receiver named like an entity, or on the result of a call to an `Entity` function), `x["Name"]`, a helper named for components or proxies, or a string list most of whose names are components; the field right after (`entity.Health.Hp`, written when assigned); `Ext.*` calls the tree defines; `Ext.Events.*`; `Osi.*` names (by the index) with the argument counts of literal calls; `RegisterListener("X", n)`; net channel names; fields read from an `Ext.Stats.Get` result. Stats (`Stats/Generated`): entry types, boosts of `*Boost*` fields (each mapped to its boost component, `create` via `stats`), functors of functor and property fields, values of flag fields. Story goals (`Story/**/*.txt`): Osiris calls by the index. XAML: binding paths and bound commands. Files at a path the game ships (`overrides`). `meta.lsx`: name, UUID, version, folder of each module (a `meta.lsx` at a path the game ships is not the mod's). A file or package that cannot be read is listed under `files.unreadable` | with `--out` |
| `demand <input>... [--each] [--out FILE] [--usage-out DIR]` | inputs are usage documents (`.json`), mod folders or `.pak` files; `--each` takes every subfolder and `.pak` of a folder as one mod (names starting with `_` or `.` left out). Writes a demand file (one JSON object per line): a `source` header (schema `gatekit.mod-demand/1`, scanner, count, the mods' names, UUIDs and versions), then rows `component` (`type`, `ext_name`, `mods`, `mod_names`, `uses` {read, write, create, subscribe}, `via` {lua, stats}, `fields` [{name, mods}]), `osiris` (`called_by`, `from_goals`, `listened_by`, `arities`, `lua_arguments`, `index` {`overloads`: [{kind, arity, out}], fewest parameters first}; a call is judged against the overload of its arity, as Osiris picks it, and an arity no overload has is named beside the ones that exist), `se_api`, `se_event` (`in_se`), `functor`, `boost` - each counted by mods, a mod once per name; most mods first, then name. The same mods give the same bytes, in any order | the demand file (default `<coverage>/demand.jsonl`) |
| `unpack <pak> [--out DIR] [--replace] [--json]` | writes every file of the `.pak` out as plain files to read them, and prints `N files of X.pak unpacked into <folder>` (`--json`: {pak, folder, files}). The default folder is the home's `unpack-cache` (`<home>/cache/unpack/<pak name>`). A mod is untrusted data: nothing of it is run, loaded or installed; a folder inside a game folder (a `bin/bg3*.exe` above it: the installed game or any copy) or a game profile (a `PlayerProfiles` above it) is refused before anything is written, and so is a package entry whose path would leave the folder. A folder that holds something is refused unless `--replace` empties it first | the folder |

`research/coverage/demand.jsonl` is committed: names and counts only, the header says which mods
and which scanner produced it, so anyone can rebuild it from their own collection.

### 8.11 `record`: the ledgers of the coverage record (`src/GateKit.Mcp/RecordTools.cs`, `RecordBook.cs`)

No game needed. The record is `research/coverage` of the checkout the command is run in, else the
one the program was built in (`--coverage DIR` names another); `--json` on every subcommand.

The ledgers are **append-only JSONL**, one object per line, line feeds only, written by the tools
under the record's one cross-process lock (`GateKit.Copies.Record.Lock`, the lock `bg3_seen_drain`
and `bg3_verify` take), every row validated on write against its ledger: a required field missing
or a closed field outside its vocabulary is refused with the field and the allowed values, and
nothing is written (a batch is all or nothing). `seen/index.json` and `coverage.json` are derived
summaries.

| Ledger | A row | Written by |
|---|---|---|
| `record/sightings.jsonl` | `{type, side (client\|server), time (UTC ISO), instance, role, run, stretch, level, label, source (tick\|trace), state (sighted\|repeated), capture, system?}` | `bg3_seen_new` and `bg3_seen_drain` ([mcp.md](mcp.md) 3.9); a passed replay of a coverage recipe (`state: repeated`, `label: replay of <recipe>`) |
| `record/replays.jsonl` | `{recipe, time, instance, by (role), run, game (Ext.Utils.GameVersion), gatekit (informational version), ok, before, most, seconds, via (test\|systems check)}` - `before`/`most`: holders when the watch began and the most at once, from the recipe's `alive` | `test` and `run` for every test under `tests/_coverage` of a checkout that has `research/coverage` (pass or fail; a test not run is not written), and `systems check` for each claim |
| `record/wishes.jsonl` | `{id (W-0001 and on), time, from, text, state (open\|sent\|fixed\|seen_live\|dropped), commit?, note?, was?}` - a change of state is a new row with the same id; the last row of an id is where it stands; `was` is another number the wish is known by (`3`, `D14`, `N41`) | `record wish`, `bg3_wish` |
| `record/counts.jsonl` | `{time, run, definition, types, seen, never_seen, unseen_mapped, verified, fields, fields_named, fields_extender_unchecked, fields_named_unknown, fields_named_by_rung {game, toolkit, inferred, extender, unknown}, owed, systems_described, systems_sure, claims_written, claims_checked, claims_passed}` | `record tally --write` |
| `findings.jsonl` | as `bg3_recipe` writes it ([mcp.md](mcp.md) 3.3): `evidence` (sighting\|repeated\|replayed\|field_value), `reached_by` (play\|cheat\|play_with_cheat_setup), `kind` (finding\|lead), free text only in `note`; a line whose recipe was removed has `recipe: null` and `moved_to` | `bg3_recipe` |

**Role, run and stretch** come from `bg3_seen_start`'s `role`, `run`, `stretch` (kept by that MCP
server for the rest of its life), else `GATEKIT_ROLE`, `GATEKIT_RUN`, `GATEKIT_STRETCH`, else
`unknown`; for `test` and `systems check` from the environment only. `instance` is the copy the
tool host drives (`installed` for the installed game).

| Subcommand | What it does | Exit |
|---|---|---|
| `tally [--run R] [--write]` | the counts now, one row as above: `seen`/`never_seen`/`verified` from the ledger of `bg3_coverage_todo` (`never_seen` is types never observed alive, `unseen_mapped` the frontier group), the fields by `names tally`'s own measure and with its headline (`fields_named` = named with evidence: rungs inferred, toolkit and game; `fields_extender_unchecked` = Script Extender's names nobody has checked; `fields_named_unknown` = other names at rung unknown; `fields_named_by_rung` = names that are not a machine's, per rung; `owed` = machine names, padding apart), the systems' descriptions (`systems_described`: a non-empty `does`; `systems_sure`: `sure` begins with "yes") and the claims by `systems tally`. `definition` (`counts-2` is written; a row of `counts-1` sums every name into `fields_named`, rung unknown included, and counts machine names into the rungs) names what is counted, so a change of definition is never read as a change of the record. `--write` appends the row to `counts.jsonl` | 0 |
| `check` | everything in the record that does not hold together, one line each, then `N problems` or `the record holds together`: a line of a ledger that is not JSON, or a row outside its ledger's fields and vocabulary; a `findings.jsonl` line whose recipe file does not exist (or that names none and has no `moved_to`); a replayable recipe in `tests/_coverage` that no findings line names; a system claim whose `check.when` names no recipe in `tests/_coverage`; a sighting of a type `coverage.json` does not register | 1 with problems, else 0 |
| `yield --run R` | what one run produced, from sightings.jsonl and replays.jsonl rows of that run: `first_seen` (rows `sighted`) by `instance`, `role` and UTC hour, `repeated`, `replays`, `replays_passed`, `replays_pass_share` (null with no replay) | 0 |
| `wish add TEXT [--from F] [--state S] [--commit C] [--note N]` | a new wish under the next id (the highest `W-` id plus one, read under the lock); `--from` defaults to `GATEKIT_ROLE`; prints `<id> <state>` | 1 for a state outside the vocabulary |
| `wish set ID STATE [--commit C] [--note N]` | appends the wish's new state, its text, `from` and `was` carried over, the commit kept unless a new one is given | 1 for an unknown id or state |
| `wish list [--state S]` | the wishes as they stand: `<id>  <state>  <from>: <text>` | 0 |

### 8.12 `json`: GateKit's own data files (`src/GateKit.Json`)

No game needed. The rules are [data-format.md](data-format.md). The tree is `--root DIR`, else the
checkout around the working folder (the first folder up with `schemas/index.json`); its `schemas/`
is used when it has one, else the schemas built into the program. A walk of the tree skips `.git`,
`bin`, `obj`, `external`, `.claude`, `.state`, `.vs` and `node_modules`.

| Subcommand | What it does | Exit |
|---|---|---|
| `check [PATHS] [--any] [--counts] [--json] [--limit N]` | reads every file the registry binds (or the files and folders given; a file named is always read) strictly and holds it to its kind - the binding of its path, else its own `schema` field. One line per problem, `file:line:column: what`, at most `--limit` (20) per file; then `N files, R ledger rows, P problems in F files`. `--any` also reads every other `.json`/`.jsonl` strictly, without a schema. `--counts` tallies instead: kind, count, rule (`syntax`, `duplicate`, `torn`, `empty`, `type`, `null`, `required`, `additional`, `enum`, `const`, `pattern`, `format`, `minLength`, `minimum`, `maximum`, `oneOf`), the place (a pointer with indices as `*`) and one example, then for each ledger `R of N rows do not fit`. `--json`: `{files, rows, problems, counts[], rows_that_do_not_fit[], listed[]}` | 1 with any problem, else 0 |
| `fmt [PATHS] [--check]` | writes the files in the canonical layout (keys in the schema's order; ledgers one compact row per line); a byte order mark is the one fault it mends. `--check` changes nothing and lists the files that differ | 1 with `--check` and a file to change, or a file that does not parse; else 0 |
| `show FILE [POINTER] [--limit N]` | any JSON or JSONL file for a person: an object as aligned `key  value` lines, containers below their key, an array of objects (a ledger's rows) as a table of at most `--limit` (50) rows. `POINTER` (RFC 6901; a ledger's rows are `/0`, `/1`, ...) shows one part | 1 when the pointer names nothing |
| `schema KIND` / `schema --list` | the schema of a kind (`record/sightings` or `record/sightings/1`), or every kind with its version, what it is, and the paths and tools bound to it | 1 for an unknown kind |

### 8.13 `sweep`: plans from the game's data, run with no model in the loop (`src/GateKit.Sweep`)

A large class of component types is reachable by plain enumeration of the game's own data: every
boost a shipping stats entry carries, every status type and status flag, every functor and spell
flag. A sweep builds the list once from the data index and then plays it step after step against a
game copy with the standing capture on - no agent decides anything, so a step costs seconds, not
tool calls.

| Subcommand | What it does | Exit |
|---|---|---|
| `[--instance C] sweep plan <kind> [--out FILE] [--db INDEX]` | builds the plan of `kind` from the data index (tables `stats`, `stat_fields`, inheritance through `using` resolved) and the registered component types of `research/coverage/coverage.json`; writes it to `FILE` (default `<state>/sweep/<kind>.json` - a plan is read from the game's data and is never committed) and prints its counts and carriers. Deterministic: the same index gives the same file, byte for byte | 0; 1 with no index or an unknown kind |
| `--instance C sweep run <plan\|kind> [options]` | runs the plan on copy `C` (started in `--level` when none runs) and prints the run's summary | 0; 1 when the copy cannot be reached |
| `sweep status <state\|kind>` | the summary of a run from its state file: steps, steps per minute, types lit and by which step, hangs, skips, recipes written and confirmed | 0; 1 with no such file |
| `--instance C sweep tour <name\|file>[,...] [--level L] [--speed X] [--limit N] [--run R] [--verify]` | plays a scripted tour of `research/coverage/tours` (`life`, `actions2`, `nautiloid`, `creation`, `world`) with the capture on, `bg3_seen_new` and `bg3_seen_drain` after every step; prints the steps, the minutes, `verified_live_before`/`_after` (coverage.json) and the types newly verified | 0 |

**Kinds.** Each step is one thing of the kind through its cheapest carrier; the order is the
things' names.

| Kind | One step per | Carrier, cheapest first |
|---|---|---|
| `boosts` | boost name in `Boosts`, `DefaultBoosts`, `BoostsOnEquipMainHand`, `BoostsOnEquipOffHand` | an unconditional occurrence before one under `IF(...)`; then a passive (`Osi.AddPassive`), a status (`Osi.ApplyStatus`, a `BOOST` one first), an item (`Osi.TemplateAddTo` and `Osi.Equip` on the dummy); then a shipping entry (no `Test`, `Debug`, `Cheat`, `Dummy`... in its name), fewer side effects (functors on apply or tick, a status type other than `BOOST`), fewer boosts in the same field, the name. Every step carries a `fallback`: the boost text alone through `Osi.AddBoosts(D, text, "GateKitSweep", D)`, run when the carrier made none of the `expects` types rise. Registered `eoc::*BoostComponent` types no entry names get a `boost-direct:` step that tries `Name()`, `Name(1)`, `Name(true)`, `Name(Strength)`, `Name(1,1)` |
| `statuses` | `StatusType` value, then `StatusPropertyFlags` flag | a status with it: shipping, a `BOOST`, fewest side effects and boosts and fields, the name |
| `functors` | functor name in the functor fields (`SpellSuccess`, `SpellProperties`, `OnApplyFunctors`, `TickFunctors`, `StatsFunctors`, ...; target prefixes such as `TARGET:` and `IF(...):` read past) | unconditional and not `AI_ONLY:`; a status's `OnApplyFunctors`, then a spell's `SpellSuccess`/`SpellProperties` (cast by the dummy at a second dummy, `Target` and `Shout` spells first), then the rest |
| `spellflags` | `SpellFlags` flag | a spell with it: shipping, `Target` then `Shout` then the rest, fewest functors |
| `leads` | open lead (`research/coverage/leads.jsonl`) whose instrument is `sweep`, `osiris` or `stage` and whose `how` is concrete; leads that share a way share one step, which lists them in `leads` | "Give a character X (a status/passive...)" or "Use X (a status...)": that entry's carrier step; "Nothing in the game's data carries the boost B()": `Osi.AddBoosts` of `B` in each of 18 argument shapes (`()`, `(1)`, `(10)`, `(true)`, `(Strength)`, `(Attack,10)`, `(Fire)`, `(Tiny)`...), one call each; "Try/Call Osi.NAME" (matched by name only): a curated call with arguments known to be safe - the dummy or the host as the character, an item the step creates where an item is wanted (`SetTeleportTargetOverride` goes from that item to the dummy, never item to item, which crashes the game) - or `blocked` when none is known. Every step is `cheat`. A `how` too vague to run ("catch it with a wide sweep") is counted, not planned |
| `statustypes` | `StatusType` label of the extender's enum (`Ext.Enums.StatusType`, read from the copy named by `--instance`) that no `StatusData` entry has | `Osi.ApplyStatus` of the label itself; checked with `Osi.HasActiveStatus` |
| `surfaces` | `SurfaceType` label of the extender's enum, read the same way | `Osi.CreateSurfaceAtPosition` of `Surface<Label>` under the dummy, first moved to a spot of its own on a ring around the host (16 spots, 7-13 m), so that what one step leaves (the blood of a dummy that died) is never under the next; checked: the surface read under the dummy (`GetSurfaceGroundAt`/`GetSurfaceCloudAt`) is the one created; the note lists the statuses it applied |
| `contexts` | `StatsFunctorContext` event of a passive with `StatsFunctors` | the passive (no `Conditions`, fewest contexts and functors) added to the dummy, then the event staged between the dummy and a second one: an attack either way (`Target_MainHandAttack`), a heal, a status applied or removed, a fight started, an item equipped or drunk, a walk, a darkness cloud, a passive roll. Events with no staging (rests, a level change) are `blocked` |
| `spelltypes` | value of `SpellType`, `SpellAnimationType`, `SpellJumpType`, `HitAnimationType`, `SpellAnimationIntentType` | the lowest-level shipping spell with it, taught to the host (`GK.Stage.Teach`, resources restored) and cast by the host through `GK.Cheat.Cast` (`play_with_cheat_setup`) at the second dummy, itself or a point; checked: the job finished `done` - a refusal or a failed job is a failed check with the reason |
| `interrupts` | `InterruptContext` of an `InterruptData` entry | the interrupt unlocked on the dummy (`AddBoosts UnlockInterrupt(X)`), then provoked by the second dummy made hostile: attacks both ways, a spell, a status, a death, leaving or entering reach |
| `items` | unconditional boost in an armour's or weapon's boost fields or in the boosts of its `PassivesOnEquip`, every `StatsFunctorContext` of those passives, and `StatusOnEquip` | the item with the fewest boosts and passives, created in the dummy's inventory and equipped; checked with `Osi.IsEquipped` |
| `summons` | way of summoning: functor (`Summon`, `Spawn`, `SummonInInventory`, `SpawnInInventory`), arity, lifetime (`Permanent`, `UntilLongRest`, turns), with an AI-helper projectile, a stack, statuses, flags | a status that runs it, else a `Shout`, then a `Target` spell cast by the dummy |

**A step** is `{id, what, carrier {kind, name, field, shipping}, how, undo, reached_by, expects?,
settle, fresh, fallback?}`. `how` and `undo` are server Lua with `D` (the dummy) and `T` (the
target dummy) as locals. `expects` are the component types the plan can name: for a boost, the
component its name maps to by the registered type names alone - `eoc::<Name>BoostComponent`, else
the one component whose stem begins or ends with the name (or that the name begins with) on a word
boundary and is not another boost's name (`ActionResource` ->
`ActionResourceValueBoostComponent`); nothing is hand-listed, and a boost that fits no component or
several (`AC`) expects nothing. `reached_by` is `cheat` for every step but those of `spelltypes`
(`play_with_cheat_setup`: the setup is staged, the cast is the host's own). A step may also carry
`check` (server Lua run after the settle, answering `true`, a string saying what did not happen,
or `{ok, note}`), `leads` (the lead types it tries), `instrument` (theirs) and `blocked` (why it
cannot be tried: recorded, not run). Besides `D` and `T`, `how`, `undo` and `check` have `H`, the
host.

**`sweep run` options.** `--from N` (0-based; default: where the state file stopped), `--limit N`,
`--speed X` (GK.Speed.Set for the run; settle times are divided by it), `--level MODULE:LEVEL`
(default `GustavX:Basic_Level_A`: loads in about 30 s and holds little of its own), `--state FILE`
(default the plan's name with `.state.json`), `--run R` (the record's run id; default
`GATEKIT_RUN`, else `sweep-<kind>-<date>`), `--drain-every N` (5), `--fresh-every N` (10),
`--reset-every N` (0: the world is reset only after a failure that needs it), `--step-timeout S`
(45), `--budget MINUTES`, `--no-recipes`.

**A run** arms the capture (`bg3_seen_start` with role `sweep`, the run, stretch = the kind),
makes a checkpoint, and then for each step: marks the label `sweep:<kind>:<id>` on both sides;
makes sure of the dummies - temporary neutral humans from `GK.Stage.Spawn`, replaced when the last
step was `fresh` (it changes its dummy beyond what `undo` takes back), failed, or `--fresh-every`
steps have passed, and when one is dead or gone; runs `how`; lets `settle` game seconds pass; runs
the `fallback` (label `...:direct`) when `expects` did not rise; asks `bg3_seen_new` (which writes
the sightings, [mcp.md](mcp.md) 3.9); runs `undo`. Every `--drain-every` steps `bg3_seen_drain` moves the
samples and verifies in the tick of capture; the types it records as verified are put on the step.
Each step's outcome `{index, id, ok, error, seconds, new [{type, side, step, by}], verified,
fallback_used, hang, checked, check_note, blocked}` goes into the state file as soon as it is known; `--from` and the state file
resume a run cut short.

**The watchdog.** A step whose calls to the game take longer than `--step-timeout` in all, or a
call the game does not answer, is a hang; the sweep's own waits (`settle`, a dummy's arrival) do
not count toward the limit. After a hang the copy is asked again for 30 s, else closed and started again in `--level`,
the capture re-armed, and the step tried once more; a second hang skips it (`skipped` in the state
file, never tried again by a resumed run). A Lua error in `how` is a failed step, not a hang.

**Recipes.** For every step that lit a type never seen before, `tests/_coverage/sweep_<kind>_<id>.lua`
(`..._direct` when the fallback lit it): a fresh dummy, the step's Lua, every lit type watched from
before and checked with `alive` on its own side, then the dummies removed. When the plan is done the
world is returned to the checkpoint and each recipe replayed once through the test runner
(which writes `replays.jsonl`); `findings.jsonl` gets one row per type: a passed replay is
`evidence: replayed`, `kind: finding`; a failed one `evidence: sighting`, `kind: lead` with the
verdict in `note`; `reached_by` is `cheat`.

**Checks, disagreements and leads.** The summary counts `steps_blocked` and `checks_passed` and
lists `checks_failed` with their notes: a step that ran without an error but whose check failed did
not do what it meant to (a spell refused, a surface gone before it was read). Every drain's
`disagreements` (a type, a field, what the bytes say and what Script Extender says) are kept in the
state file and listed in the summary, never dropped. When a plan's steps name `leads`, the run ends
with one `leads report` per lead whose step ran: `lit` when the type has been seen alive since,
`blocked` when its step was not run or hung, `empty` otherwise; a resumed run does not report a lead
twice (`reported` in the state file).

**Speed.** `--speed` shortens every settle by its factor and so shortens what is bound to game time
with it: a surface can be gone before its check (6 game seconds are 1.5 s at speed 4) and a step
can see fewer of the statuses a surface applies. It suits plans whose effects are instant (boosts,
statuses, passives), not surfaces, ticks or casts.

**Tours** may be joined by commas (`life,world`) and are then played as one, each step labelled
with its tour's name. `world` holds a dialogue (the nearest character the story has a dialogue for,
brought to the host, asked through `GK.Cheat.Talk`) and a trade (the nearest trader, `GK.Cheat.Trade`),
each followed by a world reset. `--verify` ends the tour with `bg3_verify` on both sides with
`record`, so the long-lived types of the level are compared too; the summary then has `verify`, and
`disagreements` holds those of the drains and of the verification together.

### 8.14 `mcp sequence`

`gatekit --instance <copy> mcp sequence <plan.json> [--set NAME=VALUE ...]` runs a plan of tool
calls in one MCP server - one attention cursor, one connection - some of them side by side, and
prints one JSON line per step (`src/GateKit.Mcp/McpSequence.cs`). A step is a call (`tool`,
`arguments`; `background: true` starts it and goes on), a `sleep` (seconds), or an `await` of a
background call: `since` names the step its delay is counted from, `until` lists attention items
to wait for (a `bg3_wait` that ended on something else is started again until each was named; a
missing one fails the plan, exit 1); `expect` is text the answer must hold. `${NAME}` in the plan
is replaced by `--set NAME=VALUE`. An await's line: `rounds[] = { ms (from the `since` step's start
to the wait's end, host clock), attention [{ what, side, since_ms, since_ticks }] }`. The plans of
the live attention proofs are `tests/_selftest/attention/*.json` (`fixture_events.json --set SPEED=1|4|10`,
`dialogue_phases.json`, `roll_speed.json --set FAST=10`). It starts a wait and triggers an event a
moment later from the toolkit alone.

### 8.15 `decomp name`, `decomp names`, `decomp code`: functions named by evidence and our reading of them (`src/GateKit.Symbols/Decomp/FunctionNames.cs`, `Reading.cs`)

`decomp name <module+0xRVA> <name> [--source inferred] --evidence <text> [--evidence-kind <kind>]
[--does <text>] [--coverage] [--dry-run]` gives the function that holds the address a name. The
address may be anywhere inside the function, as `GK.Memory.Hits` reports an instruction; the name is
placed at the function's start, found as `decomp at` finds it. Where the registry holds functions
(`registry/`, 8.16) the name is its entry - `registry/functions/<scope>/<Name>.json` - and the
function's row in the table of the build `decomp stamp` recorded for the binary, with its
fingerprint; where it holds none, a row of `functions.jsonl` of the coverage record (schema
`record/functions`). Our own words and facts, never code of the game.

- `--source` is the rung (`registry/README.md`): `game`, `toolkit`, `confirmed`, `inferred` (the
  default), `extender` or `unknown`; every rung but `unknown` needs `--evidence`. `--evidence-kind`
  says what the sentence is (`watch`, `read`, `pairing`, `game-name`, `toolkit-name`,
  `extender-name`, `text`; by default what the rung implies); an applied rung needs a kind that can
  confirm.
- A machine's name (`FUN_...`, `sub_...`), a name that is no registry id, and a sentence the
  registry refuses (one that names the work, a date or a model, or quotes the decompiler) are refused.
- A weaker rung does not replace a stronger one's name. A rename renames the entry and rewrites
  every citation (`registry rename`); the symbol database's and the decompiler's real names are kept
  in `known_as`. A name without a scope is placed under `unplaced::`.
- `--does` is our sentence on what the function does; left out on a rename, the earlier one stays.

`decomp at` shows the names of the function under the databases' own names: the highest rung as
`named:` (with `does:`, `evidence:`, `guess:`), every other name there below it as `also:` with its
rung, and how they stand together (below); `given_name`, `given_live`, `given_rung`,
`given_others: [{name, rung, live}]` and `standing` with `--json`. `decomp names [text] [--json]`
lists the names, or those whose row holds the text, the highest rung of a function first, a name
in a clash marked so, and ends with `N names by evidence[, N guesses][, N names on the rung
unknown] (N functions)`: a guess and a name on the rung `unknown` are not named by evidence. Both
read the registry and the home's live layer (8.16.1): every live claim of a function is a name of
its own, marked `(live)` - a guess beside an id too - and a live claim not yet committed takes the
place of the registry's row of the same id.

Several ids at one function stand together by the rule of `registry clashes`: with a live claim
among them and the address not settled they are a **clash**; settled `--folded`, or all of them
committed, they are **folded** code; settled `--keep`, **settled** for that id.

`decomp code <id | name | binary+0xRVA> [--raw] [--json] [--binary B] [--all] [--grep T -C 3]`
answers with our reading of the function, made as it is asked for from the database's text and
the registry (its entries, the table of the build the database's binary is stamped with, and the
home's live layer, whose newest claim of an id comes before the committed entry); it is never
stored. An id is resolved through that build's table, or a live claim placed there; a name another
source gives (`known_as`) finds its entry too; anything else is looked up as before (a name in
the database, an address, an address inside a function). The rule of the form: **what is certain is
written into the code, what is a guess is written beside it.**

- A function, a variable or a field on an applied rung (`game`, `toolkit`, `confirmed`) replaces
  the machine's text: a callee's `FUN_<address>` (or the decompiler's own real name of it) becomes
  its id, a parameter or local its name - found by its storage under the decompiler's name of that
  place - and a field `stream->NumRolls /*+0x70*/`.
- One on a shown rung (`inferred`, `extender`, `guess`, `unknown`) leaves the machine's text and
  adds at the end of the line `// <machine's text>: <our reading> [<rung>]`; a shown variable is
  noted on the first line it appears on. A variable known only by the decompiler's temporary
  (`unresolved`) is always shown.
- A field is read only through a variable the entry ties to a structure (`ties`, `decomp vars
  --struct`), only at an offset a field is named at, and only where the variable is declared as an
  integer or a pointer to bytes (a typed pointer's `+ n` counts elements). It is written by name
  only when the tie and the field are both on applied rungs; else beside the code, the tie's rung
  given when it is the shown one.
- Several ids at one address are all shown: the highest rung reads in the code (when applied), the
  others beside it (`the same code as <id>` when folded, `also claimed, a clash: <id>` in a clash),
  and all of them in the header under one line that says how they stand: `N ids share this code
  (the linker folded them)`, `N ids claimed for this code: a clash, not settled - `gatekit registry
  settle <binary+0xRVA> --folded` if the linker folded them, --keep <id> if one is right`, or `N
  ids claimed for this code; settled for <id>`; `standing` (`folded`, `clash`, `settled`) in JSON.
- Above the text: the place and the decompiler's name, then each id there with its rung and
  whether it is applied, `state` when not described, `does`, the guess (`the guess rests on: ...`,
  `it would be confirmed by: ...`, a line each), the evidence, and
  `proven by:` the tests of its evidence (or `no test yet`); `// no entry of the registry` when it
  has none. Below: `// the structures it uses:`, each structure tied or typed with its fields,
  offsets, types and rungs. The hints of the databases (fields of `this` from Script Extender's
  layout and the Toolkit's accessors, a name for an unnamed callee, FixedStrings, offset globals)
  are added after, as comments.

A query that names more than one function, without `--all`, lists them and exits 1; when they lie
one in each binary (the same id in both binaries) it says `add --binary <one> or --binary <other>
to pick one`, else to pick one by its `binary+0xRVA`.

`--raw` prints the database's text untouched (`// binary+0xRVA` between functions when several
are printed). `--json` prints `{functions: [{binary, rva, build, ids: [{id, rung, applied, live}],
raw, annotations: [{line, kind: function|variable|field, machine, ours, id, rung, applied}]}]}`.
`decomp grep` searches the database's own names.

`decomp vars <address> <old=new>... [--struct var=Structure]` names the variables and parameters
of a function already named (`param_1=stream`; `old=` takes a name off); `decomp code` of that
function prints them by those names, in the code or beside it by their rung (above). The
registry keeps a variable by its storage, read from the
decompiler's signature (Windows x64: the first four parameters in `rcx`, `rdx`, `r8`, `r9`, or
`xmm0`-`xmm3` for a float; the rest at `stack:+0x28` on; `local_N` at `stack:-0xN`); a temporary
the pseudo-code gives no storage for is kept as `unresolved` with the decompiler's name until it is
read. `--struct` says which structure a variable points at (a tie of the entry): its named fields
are then listed below the function and read in its own code by the rule above -
`*(long *)(stream + 0x70)` prints as `stream->NumRolls /*+0x70*/` and `(stream + 0x68)` as
`(&stream->Seed /*+0x68*/)` when both are applied, the offset staying beside the name. No other
pointer's accesses are touched: nothing ties them to the structure.

`decomp field <struct> <offset> <name> [--type] [--source inferred] --evidence <text>` names the
field at an offset of a structure that is no component's - a system's manager, an element of its
array, a task of the cursor - in the structure's type entry of the registry (where the registry holds no
types, in `structs.jsonl` of the coverage record, schema `record/structs`); a component's field is named
with `names set`, in its layout. A machine's name (`field_...`) and a
name without evidence are refused. `decomp struct [name]` lists the structures, or one's fields.

### 8.16 `registry`: our reading of the game's code (`src/GateKit.Symbols/Registry`, `registry/README.md`)

`registry fingerprint <binary+0xRVA | id> [--json]` prints the facts about a function that are not
code, read from the home's databases: what it calls (a callee by its registry id, one without by
`fp:` and the hash of the callee's own facts, an import by `import:` and the hash of its name), the
hashes of the text constants it loads, the component types whose index it touches, its slots in
virtual tables, and the token an unnamed id would take from them - all of that does not depend on
the build; and, for the build at hand, its size and the hash of its masked body. The body is hashed
only from a file whose SHA-256 is the one `decomp stamp` recorded for the binary, and only when it
has at least 64 bytes and 16 instructions; otherwise the answer says why there is none.

`registry stamp [--coverage]` gives every function named by evidence the game build its address is
in (the installed game's build from `game verify`, which must be the build `decomp stamp` recorded
for the binary's bytes), its fingerprint, size and body hash: in the registry where it holds
functions, else in the rows of `functions.jsonl`.

`registry import <functions|structs|layouts|all> [--replace] [--dry-run]` carries the coverage
record's own stores into the registry: `functions.jsonl` into `functions/` and the build tables, `structs.jsonl`
and `research/coverage/layouts/**` into `types/` (a layout's fields with their rungs and evidence,
its size, its summary as `about`, its concept, and every other key under `survey`, its batch folder
included). It runs again without harm: an entry already equal is left alone, one that differs is
refused unless `--replace`. Each kind ends with its counts and a round trip - every entry read back
into the store's own form and compared - and the exit code is 1 when anything was refused or did not
come back equal. A name of our own that a row keeps as `legacy` is not carried, and the import says
so.

Where the registry holds an entry kind, `names` and `decomp name`, `names`, `vars`, `field`,
`struct`, `at` and `code` read and write the registry for that kind; the coverage record's files
of that kind are left as they are and are not written.

`registry check [--strict] [--notes N]`: every entry valid against its schema and at the path of its
id, ids unique across the kinds and by case, every build table sorted with each row in its shard and
an entry for each id, `build.json` beside each build, every citation of code and of tests in the
repository resolved (the prefixed mentions `code:<id>` and `test:<path>` in any text file, and the
typed fields of the entries), and the refusals: a sentence that names the work (an agent's role, a
conversation), a date, a time of day, a model or a machine's path, or quotes the game's code (the
decompiler's names, a cast through a pointer); an empty sentence; a name on an applied rung without
evidence that can confirm it; a guess without what it rests on; a variable with storage and a
decompiler's name. Exit 1 on any refusal. Provenance and code inside evidence an import carried in from
the coverage record's stores, and an unconfirmed entry with no question of how the game reaches it, are owed work,
listed and counted (failures with `--strict`); an address where an id belongs, a variable known only
by its temporary, a test's case and step not yet resolvable are notes. CI runs it.

`registry rename <old> <new>` renames an entry: its file moves, its rows in every build table and
every citation of it or its members - in any text of the repository and in the entries' fields - are
rewritten at once; the old id is left nowhere. `gatekit refs <id or test path>` lists every place
that cites it, its members included.

`registry mend` puts every build table back in order after a merge: sorted, each row in the shard of
its id's top scope, a row twice kept once, a merge's marker lines dropped; two different rows for one
id are refused, naming both.

`registry applied <binary> [--build B]` prints, as JSON lines, exactly what may be given to the
decompiler as real names and types for that binary and build: the functions and globals of its table
on an applied rung (`game`, `toolkit`, `confirmed`) with their variables on one, and the types on an
applied rung or with applied fields, those fields only.

`registry reanchor --target <binary> [--source bg3_dx11.exe] [--source-build B] [--target-build B]
[--dry-run]` moves the source binary's table (of `--source-build`, else the build the pseudo-code
database records for it, else its only table) into the target's (of `--target-build`, else the
build recorded for the target) through the pairs of `decomp pair` (`function_pairs`, either
direction): a function with exactly one partner in the target that is a seed or an accepted pair
gets the target's row - that address, the size and body hash read there (`registry fingerprint`'s
rules) - and the rest is listed `for a person` with the reason (no pair above the bar, more than one
partner, the target's table holding the id at another address). One line per function (`moved` with
the pair's state, rule, score and margin and whether the bodies are equal, differ or could not be
compared), then the counts; the target's table and its binary's SHA-256 in `build.json` are written
unless `--dry-run`. Without stored pairs of the two binaries it exits 1.

`registry places FILE [--check]` writes into a JSON data file where the build tables put every
function id the file names: every string under a key `id`, at any depth, is looked up, and the
answer is written under the top-level key `places` as `{ <build>: { <binary>: { <id>: [rva, size] } } }`.
Code that meets raw addresses in the game (the roll log, with the frames above a roll) names them
from such a file and holds no table written by hand. An id with no place is printed (`no place:
<id>`) and left out. `--check` writes nothing and exits 1 when the file's places are not the
registry's.
`registry check` refuses (rule `places`) a JSON file under `mods/` whose top-level `places` are not
what the build tables hold now, so a table moved by a commit or a reanchor is not left stale in
the mod.


`registry signature <id | name | binary+0xRVA> [--limit N]` prints a byte pattern for the start of
a function, for every binary the registry places the id in (or for the one address given): whole
instructions from the function's first byte, one a line as hex with its text after `//`, a byte
that a relocation or another layout would change written `??` (the displacement of a RIP-relative
operand, the target of a near call or jump, an absolute address), taken as far as is needed for
the pattern to be found exactly once in the executable sections of the binary's file - at most
`--limit` bytes (default 48). The first line says `found once, N bytes` or `NOT unique within the
limit: K places`. It is what a hook or a Script Extender mapping is written from. Exit 1 when a
binary's file cannot be read or a pattern is not unique.

`registry questions [scope]` lists every open question of the entries (whose id starts with the
scope): what is not known of a thing, above all how the game reaches it.

#### 8.16.1 The live layer

The home's `state/registry-live/` is shared by every GateKit process on the machine, whatever
checkout or branch it runs from: one file an id, `entries/<kind>/<scope>/<name>.json` (`kind`
`functions`, `types` or `globals`; the id's scopes as folders), changed only under its lock,
written whole, its format the schema `state/registry-live/1`; a settlement is
`settled/<binary>/<build or any>/<rva>.json` (the binary's name escaped as a part of an id's
path, `bg3%2Eexe`). A read sees every claim written before it.

- `registry note <id> [--kind function|type|global] [--at binary+0xRVA] [--rung R] [--does T]
  [--about T] [--evidence kind:T]... [--test path]... [--rests-on T --would-confirm T] [--ask T]...
  [--ask-about reach|conditions|meaning|other] [--field off:name[:type]]... [--who] [--task]` adds a
  claim: the id's newest claim (else its committed entry) with what the note says over it, where the
  function or global is (its start, its build), and who wrote it, when and for which task
  (`GATEKIT_WHO`, `GATEKIT_TASK` by default). A guess without `--rests-on` and `--would-confirm` is
  refused, and so is an entry its schema refuses.
- `registry withdraw <id> [--all] [--who W]` takes claims back out, under the id's lock: the id's
  newest claim (the claim before it is what the id says again), or with `--all` every one, and the
  id's file goes when none is left; `--who` takes only that writer's claims. Gone for every reader
  at once; what a checkout committed stays. It prints each claim withdrawn and what the id says now;
  no claim of the id (of that writer) is refused, exit 1.
- `registry at <binary+0xRVA> [--json]`: the function that holds the address, its names in the
  databases, the committed ids there and the live claims, each marked live or committed.
- `registry show <id or another source's name> [--json]`: the committed entry with its places, and
  the live claims.
- `registry clashes`: two different ids claimed for one function, among the live claims or against
  the committed table, not settled: both kept, the higher rung first. `registry settle
  <binary+0xRVA> (--keep <id> | --folded)` settles one: one id stays, or the ids stand together
  (the linker folded identical code).
- `registry commit` writes the live layer into this checkout's registry in its one canonical form:
  each id's newest claim merged over its entry, its place in the build's table. Who, when and which
  task stay in the live layer. An id in a clash not settled is held (the others are committed), an id
  a settlement dropped is left out, an entry whose new sentences the check refuses is refused, and an
  entry the checkout already holds as it is is not written again. A claim carries the size of the
  one part of the function its address is in, so after writing, the build tables' rows are stamped
  as `registry stamp` does - the whole function's size, its split-off parts included, and the hash
  of its body; where the databases are not at hand the rows stay as the claims gave them and a
  line says so.

The MCP tools `bg3_registry_note`, `bg3_registry_withdraw`, `bg3_registry_at` and `bg3_registry_show` do the same
(`docs/contracts/mcp.md`).

### 8.17 `mcp config`

`gatekit --instance <copy> mcp config [--out <folder>]` prints, or writes as `<folder>/.mcp.json`,
the registration of the MCP server for one game copy: `command` is this build's own program (when
it was started as `dotnet gatekit.dll`, `dotnet` with the assembly as the first argument) and the
arguments are `--instance <copy> mcp`. Without a copy (`--instance` or `GATEKIT_INSTANCE`) it
refuses with exit 1: a registration that names none would drive the installed game. The
repository's own `.mcp.json` starts the server with `dotnet run --no-build`, so it never builds
under running games, and sets `GATEKIT_INSTANCE` to `agent` unless the environment names another.

### 8.18 `symbols describe-systems` (the symbols layer of the systems' descriptions)

`gatekit symbols describe-systems [--out research/coverage/systems] [--ns PREFIX] [--survey CSV]
[--dll bg3_dx11.exe]` writes, for every system of `--survey` (default `<out>/survey.csv`, its
`system` column; without the file every system type of the database) whose name starts with
`--ns`, the file `<out>/<namespace with :: as __>/<Name>.json` (`esv::spell_cast::random` ->
`esv__spell_cast__random/`; characters Windows refuses in a name become `_`). It writes only the
`symbols` key - `{"reads": [...], "writes": [...], "uses": [...], "methods": [{"name", "address", "binary"}], "limits": "..."}`,
what `symbols system NAME` prints: components the methods take as read-only views and as writable
ones, and each exported method (in name order) with `<binary>+0x<rva>` where the function was found
in `--dll`, default `bg3_dx11.exe` (the binary a copy runs; a method not found there has `address` and
`binary` null - never another binary's address). `uses` lists the component operations of the
system, one object each: `{"type", "kind", "function", "site", "depth", "confidence"}`. `kind` is
`add`, `remove`, `get` or `has`: the system's per-frame update (`system_updates` of decomp.sqlite,
in `--dll`; a system with no update entry there is walked in `bg3.exe`) calls a classified helper of
that family and type (`gatekit decomp helpers`) itself (`depth` 1) or in a function it calls
(`depth` 2; not into another system's update, nor into a function named `ecs::`, `ls::`, `std::`).
`function` is the caller, `site` the call instruction (`<binary>+0x<rva>`, read from the binary's
file in the game folder; null when the file is not there or the call is outside the function's
first range), `confidence` the helper's. `kind` `index` (with `site`, `depth`, `confidence` null)
is the weaker fact: a matched game function of an exported method reads the type's index, and no
operation of the type is known. `limits` says in every file what the layer cannot see: operations
the compiler inlined, those deeper than depth 2, and what a method other than the update does (a
`SystemLoad`), so an empty `writes` is not "writes nothing". A file that exists keeps every other key in place
(`does`, `unknown`, `sure`, and any other key inside `symbols`, such as a note); a new file holds
`system`, `namespace` and `symbols`. A file whose text would not change is not written. It prints
`N written (M new), K unchanged, of T systems listed; in <folder>`. A file that is not a JSON
object is an error (exit 1), nothing written to it; `--dll` absent from the database is exit 1.

### 8.19 `symbols se-layouts` (layouts from Script Extender's declarations, for review)

`gatekit symbols se-layouts --out DIR [--coverage research/coverage] [--ns PREFIX]` writes
`DIR/<engine name with :: as .>.json`, in the layout-file shape of `research/coverage/layouts`, for
every type `coverage.json` calls mapped that no layout file under `<coverage>/layouts` names, from
the symbol database's `se_types`/`se_fields` (structs and systems excluded): `component`,
`ext_name`, `size` (the game's registered size, else the declaration's), `oneFrame`, `tag` (no
fields and size at most 1, or a tag kind), `fields` `[{name, offset, type, source, evidence,
legacy?}]` with `source: "extender"` - Script Extender's name for the field at that
offset, which nobody has checked against the game's code - or `"unknown"` where the extender's own
name is a machine's (`field_24`) (fields hidden from Lua and fields with no computed offset left
out and named in `open_questions`; C++ spellings such as `int` written `int32_t`, inside template
arguments too), `status: "declared"`, `from`, `names` (why the rung is `extender`), and an
`open_questions` line when the declaration's size differs from the game's. A type declared only in
`Coverage.h` (the header generated from this record) is not written. It never writes into the
record's layouts: `gatekit names take DIR` checks the files and moves those that hold together. It prints
`N layout files written to DIR; K mapped types already have one; M ... have no declaration`.

### 8.20 `symbols creators` (who adds a component)

`gatekit symbols creators NAME [--dll BINARY]` lists the functions that call an `add` helper of the
component (`ecs_helpers` of decomp.sqlite, `gatekit decomp helpers`), in `bg3_dx11.exe` and then
`bg3.exe` (or only `--dll`), one line each:
`<binary>+0x<rva>  <function name>  (adds through <binary>+0x<helper>)  update of <systems>` - the
last part when a system's per-frame update reaches the function (the walk of `uses`, 8.18). NAME
is the component's full name or the end of it after `::` when only one component ends so. It prints
`<type>: N functions add it, M of them reached from a system's update (depth 2); K systems` to the
error stream. A component no add helper is known of is said (`no add helper of ... is known`),
exit 0; several components ending with NAME are listed; decomp.sqlite without `ecs_helpers` is exit
1 with the command that makes it. What is added inline (no helper) or through a command buffer the
helpers do not cover is not seen.

### 8.21 `symbols digest` (what a system's update does, in facts)

`gatekit symbols digest SYSTEM [--dll BINARY] [--json]` prints, for `bg3_dx11.exe` and `bg3.exe`
(or only `--dll`), what a model needs to describe the system - names, addresses, short conditions
and short texts, never a function body:

| part | what it holds |
|---|---|
| header | the system and its namespace; one line per row of `system_updates` (entry, update, the update's name when it has one, the note) |
| counterparts | the Toolkit function the update is matched to (symbols.sqlite `matches`: method and relation; decomp.sqlite `code_matches`: score and margin, "weak" below 0.6 / 0.2) or "no matched Toolkit function"; the macOS build's `ecs::_private::SystemRegistrationHelper::SystemUpdate<SYSTEM>` (by name: certain) and the class's methods by name |
| operations | the component operations of `uses` (8.18) in call order (the update's own by call site, then those of each function in the order the update calls it), one line each: number, kind, type, the function, the site, `[confidence, depth]` and the guard; identical neighbours are one line `(xN)` |
| calls out | named functions the update calls; Osiris and stats functions called by the update or a function it calls; other systems (their methods, and `TypeIndex<X>_SystemsContext` read: their queue or state) |
| constants, strings | the constants of the guards; texts with a space (logs, asserts), identifiers and FixedStrings the update and its callees refer to, each cut at 60 characters |
| unknowns | unnamed functions the update calls (count, the largest), helpers of no known family, component indices read inline (an operation the list cannot show), fields the record has no name for, types without a layout, operations without a call site, why guards were not extracted |
| measure | digest lines, and the lines of pseudo-code of the functions a reader would otherwise open (the updates and the functions they call, helpers and the engine's `ecs::`, `ls::`, `std::` apart) |

`bg3.exe` is one line when its operations have the same kinds and types as `bg3_dx11.exe`'s.

**The guard** of an operation is read off the pseudo-code around its call (`PseudoGuard`): the
enclosing `if`s, `else` branches (the negation of the chain before), loop conditions, `case`
labels, the negation of an earlier `if` at the same level whose block only returns, breaks or
continues, a braceless `if (c) goto` past the call, and the `&&` conjuncts before a call that sits
inside a condition. It is written over names: `has(T)` for a helper's test of presence or a got
component compared with 0, `T.Field` for a field of a component got by a helper or of a parameter
or local whose structure the pseudo-code declares (named from the record's layouts when the
pseudo-code shows an offset), `valid(h)` for an entity handle that is set, and a local that only
copies such a field stands for it where its last assignment outside a closed block reaches. What
has no name stays as it is: `[lVar4+0x158]`, `T+0xa8`, `uVar3`. `always` means no condition
encloses the call; `(one path; also reached by a jump)` that a label lies on the way, so the
conditions are those of one path; `guard not extracted (...)` says why (the call is not in the
pseudo-code under the helper's name, reached only through jumps). A depth-2 operation carries the
guard of the update's call of its function too. `--json` gives the same as one object (`updates`,
`counterparts`, `binaries.<binary>.operations[{kind, type, function, function_name, site, depth,
confidence, guard {status, text, terms, partial, unnamed_terms}, call_guard}]`, `calls`,
`constants`, `strings`, `unknowns`, `measure`).

`gatekit symbols digest --all [--out DIR] [--ns PREFIX] [--force] [--dll BINARY] [--json]` writes one
file per system with an update entry (whose name starts with `--ns`), `DIR/<namespace with :: as __>/<Name>.txt` (or `.json`), default
`<BG3_DECOMP_WORK>/digests` - outside the repository: a digest is derived from the game's code and
is not committed (an `--out` inside a checkout is said). Each file is written as soon as its system
is done, with `n of N  <system>  <seconds>` on the error stream; a file already there is kept
unless `--force`, so a run that stopped is simply started again. At the end it prints where the
time went (operations walked, guards, counterparts, the rest) and `N digests written to DIR (S
already there, kept) in T s: M operations, guards extracted for K (J naming a component or a
field), U unconditional, X not extracted; L digest lines for R lines of pseudo-code`. A guard's
terms can be the decompiler's locals (`lVar4 != 0`, `[lVar5+0x20]`): "extracted" means a condition
was read, not that it is readable. decomp.sqlite missing, or without `system_updates`, is
exit 1; neither a name nor `--all` is exit 1.

### 8.22 `leads`: what to try next for each type never seen alive (`src/GateKit.Leads`)

`gatekit leads build | next | report | list | tally`, all with `--coverage FOLDER` (default
`GATEKIT_COVERAGE`, else `research/coverage` of the checkout) and `--json`. The file is
`research/coverage/leads.jsonl`, one JSON object per line, sorted by type, LF endings; `build`,
`next` and `report` write it under the coverage record's lock (8.11). No game is needed.

`build [--decomp DB] [--symbols DB] [--index DB] [--no-wiki]` writes one row per type the ledger
calls `unseen` (mapped, never seen alive), plus every row the file already held. It reads
decomp.sqlite (default `BG3_DECOMP_WORK`), symbols.sqlite and the data index (a missing data index
leaves `data` and `wiki` empty and says so); a missing decomp.sqlite is exit 1. The same inputs give
the same file. A rebuild keeps the tries a run reported, `claimed`, `note`, and every field named in
the row's `hand` array; the tries read from the runs' prose are read again each time. A row:

| field | what it is |
|---|---|
| `type`, `side`, `one_frame` | from coverage.json |
| `class` | the first line of `Rules.ClassByName` that matches the name: `online_ugc`, `editor_debug`, `split_screen_controller`, `multiplayer`, `boost`, `cinematic`, `lifecycle`, `gameplay_event`, `gameplay_state`; an entry on a load/save path makes a gameplay class `lifecycle` |
| `reachable` | by class (`solo`, `needs_pad`, `needs_two_copies`, `needs_network`); `unknown` for a solo type nothing is known to add; `not_in_shipping_game` when nothing adds it and no more than two functions of `bg3_dx11.exe` read its index (its registration) |
| `index_users` | functions of `bg3_dx11.exe` that read the type's index (symbols.sqlite `ecs_ids` + `xrefs`) |
| `creators` | up to 12 of `{system, function, name?, binary, site, depth, confidence}`: the adds of the systems' facts (`symbols.uses`, 8.18), then the callers of the type's add helpers in `bg3_dx11.exe`, then the callers of the macOS build's add templates found by name (`confidence: "name"`); `creators_more` counts the rest |
| `entry` | `{kind, detail, depth, function, basis, guard?, condition?}`: see below; `{kind: "unknown"}` when the walk reaches nothing |
| `data` | `{what, where}`: for a boost, the stats entries whose boost fields call it (count, up to five, shipping first); for a functor entry, the entries whose functor fields call it; for an Osiris entry, its story uses; an Osiris call whose name holds the type's own words (`Rules.Core`) is listed as evidence `by name` |
| `wiki` | a bg3.wiki title equal to the display name of the first spell, status or passive of `data` (at most 300 asks per build; cached) |
| `how` | one sentence, from a template per entry kind and class |
| `instrument` | `play`, `gui`, `stage`, `osiris`, `sweep`, `two_copies`, `split_screen`, `network` or `none` (`Rules.InstrumentOf`) |
| `score` | solo 40, pad or two copies 15, unknown 10, network 5; a named entry +25 (+20 through a queue), a system update with a guard +15 (without 8), load/save 12, replication 10; data +15 (+5 when only developers' packages carry it); a wiki page +5; editor/debug -10; a boost nothing carries -20; each failed try -15 with the lead's own instrument, -5 with another |
| `state` | `lit` (seen since: seen/index.json, read at every command; or a `lit` try), `unreachable` (not in the shipping game), `parked` (two tries with the lead's own instrument that found nothing; `parked` says why), `in_work` (`claimed` by `next` after the last try), else `open`; `hand: ["state"]` keeps a state set by hand |
| `attempts` | `{time, run, instance, instrument, outcome, note, source}`; `source` is `report` or `import <file>` |

The entry is found by walking up the direct callers (decomp.sqlite `calls`) of every creator, at
most 4 steps and 400 functions, and stopping at the nearest function whose name says what it is
(`Rules.EntryByName`; a nameless function of `bg3_dx11.exe` is named by the Toolkit function
matched to it, else by the class whose virtual table holds it): `osiris` (an `esv::Osiris*Functions`
call), `message_handler` (a protocol's `ProcessMsg`), `functor`, `status`, `boost`,
`system_update` (a registered update, or the facts of a system), `load_save`, `replication` (the
client's copy made from the server's; used only when nothing else is found). From a system update
the walk goes on through the functions that read that system's index - those that hand it its
requests - when there are at most 8 (`basis: "queue"`); `guard` lists the components the update
reads when there are at most 6. For a `system_update` entry that is not reached through a queue, `condition` is
the guard of the call that creates the type (8.21), of both levels for a depth-2 creator: the
first creating operation of the system, shallowest first, whose guard names something; its place
among the calls of the same helper in its function is that of its site. Only the terms that name
a component (`has(T)`), a field (`T.Field`), a named call's answer (`World::IsEntityAlive()`) or a
loop over a list are kept, followed by `(and N more over the decompiler's locals)`: a comparison of
unnamed locals tells a runner nothing and is a piece of the pseudo-code, which the record does not
carry. `how` of a row with a condition says
"Created by <system>'s update when <condition>: make that hold while it runs".

Tries are imported from `runs.jsonl` (`found` and `open`) and from the stretch bullets of `run-*.md`:
a sentence that names a type, a brace list or a namespace, says it did not appear, and is not about
what to do next, is a try; its words give the instrument (`Osi.` osiris, a window or click gui, a
cheat or `GK.Stage` stage, else play) and the outcome (`blocked` for "could not", "no slot", ...,
else `empty`). The same instrument within 40 minutes counts once.

`next [--instrument X] [--count N]` (default 3) hands out the best open leads (score, then type)
and marks them `in_work` with `claimed: {instance, time, run}`; the copy is the global
`--instance`, else `GATEKIT_INSTANCE`. `report TYPE --outcome lit|empty|blocked --note TEXT
[--instrument X] [--run R]` appends a try (instance and run default to the claim's); when it parks
the lead it adds a wish (`from: "leads"`) to `record/wishes.jsonl` and prints it. An unknown
outcome or instrument, or a type with no lead, is exit 1 and nothing is written. `list [--class]
[--state] [--reachable] [--ns] [--limit 50]` prints best first. `tally` counts by class, state,
reachability, entry and instrument, with the class x state x reachable table and the ceiling: every
lead minus `not_in_shipping_game`.

### 8.23 `decomp` (`src/GateKit.Symbols/Decomp`, `tools/ghidra`)

The subcommands: `run load refresh serve code at name names vars field struct grep systems helpers
tally pair queries match matches fixedstrings dotnet csharp status stamp vtable` (`at`, `name`,
`names`, `vars`, `field`, `struct` and `code`: 8.15). `GateKit.Symbols` has two command factories the command line finds:
`SymbolsCommands.CreateCommand()` (`symbols`) and `DecompCommands.CreateCommand()` (`decomp`, with
`vtable` added from the symbol side). Settings are the environment's: `BG3_DECOMP_WORK` (the work
folder: one folder per binary with its Ghidra project and the decompiler's export `pseudocode.jsonl`
- an export named `functions.jsonl` is renamed by the first `run`, `load` or `refresh` that
meets it - and `decomp.sqlite`;
default `<local app data>/GateKit/decomp`), `BG3_DECOMP_TOOLS` (`ghidra_*`, `jdk-*`), `GHIDRA_HOME`,
`GATEKIT_GHIDRA_SCRIPTS`, `BG3_SYMBOLS_DB`. A work folder that is somewhere else is simply not
seen: `gatekit decomp status` that lists no binary `in database` says "the variable does not point
at the work folder", not "nothing was ever decompiled".

**What the database was made from** (`src/GateKit.Symbols/Decomp/Provenance.cs`). `run` imports a
copy of the binary into its project folder, `<work>/<binary>/<binary>`, replaced whenever its bytes
differ from the binary's; that copy is the record of what the project analysed. Each binary of
`decomp.sqlite` has a row of `binary_meta` (`binary, sha256, size, kind, game_build, verdict,
evidence, stamped`), established from the SHA-256 of that copy:

| kind | verdict | when |
|---|---|---|
| `game` | `clean`, with `game_build` | the bytes are in a reference list (`<home>/reference/game-files`); the evidence names the path, build and list |
| `toolkit` | `toolkit`, no build | the bytes are the installed Toolkit's file of that name; the evidence names it and the Toolkit's Steam build |
| `game` | `unknown`, no build | a file of the game has this name (in a list or in the game's `bin`), and these bytes are in no list |
| `unknown` | `unknown`, no build | none of these (another platform's build, a file from elsewhere); it does not count for the database's build |

A project folder without the copy is recorded with no hash, verdict `unknown`. `meta` holds
`game_build` (the builds every `game` binary belongs to; left out when they share none) and
`game_verdict` (`clean` when every `game` binary is, else `unknown`); a database without a game
binary holds neither. `load` and `refresh` write the row of the binary they wrote, in the same
connection (`refresh` in its copy before the move). **`gatekit decomp stamp [binary]`** writes the
rows of every binary of the database (or one) again, in one transaction of an ordinary journal, and
prints one line per binary and the database's build; it is a command of its own, not part of
`status`, because it hashes every copy and writes, and `status` only reads. `status` prints the rows
(`made from: ...`), or says they were never recorded. Nothing else of the database is touched.

**`gatekit decomp refresh [binary] [--coverage DIR] [--memory 20G] [--cpu N] [--dry-run] [--wait 60]`**
makes the decompiler's project a cache of the databases and the registry: it gives the project what
changed since the last pass and decompiles again what shows it. Of the registry only the applied
set reaches the project (`registry applied`: rungs `game`, `toolkit`, `confirmed`); a name, a field
or a type on a shown rung never does - it is shown beside the code by `decomp code`. `binary`
defaults to `bg3_dx11.exe`, the build a copy runs. It needs what a full pass leaves behind - the
binary's Ghidra project in the work folder and its functions in `decomp.sqlite` - and says how to
make them when they are missing (`run`, then `load`); it never starts a full pass itself. The
pass applies no enumeration and no signature of the registry.

| Step | What happens |
|---|---|
| names of functions and globals | everything `gatekit symbols export --format ghidra --dll <binary>` writes, and each function of the registry on an applied rung at its place in the table of the build the database's binary is stamped with (`binary_meta`), by its id, in the place of any other name of that address (source column `registry <rung>`); compared line by line (kind, name, decorated name) with `<work>/<binary>/names.tsv`, the names the project was last given. A name that is new or different is a rename; a name of `names.tsv` the database does not have is left as it is in the project. Without a table of that build the registry's functions are left out and the command says so |
| structures | every type of the registry's applied set (the registry beside the coverage record: `--coverage`, else `GATEKIT_COVERAGE`, else `research/coverage` of the repository the command runs in or from) becomes a structure: its id, `size`, and per applied field offset, size, kind (`int uint float bool ptr bytes`) and name; a field on a shown rung is left out. A component of the ledger (`coverage.json`, `components[].name` and `inlineSize`) without such a type becomes a structure of its size with nothing in it (one line, a field of size 0 named `_`): a pointer to it still says which component the pseudo-code is looking at, so the helpers of the type return `T *` and its bytes read `p->field_0x10`. A structure the project was given before and is not given now is cleared to its size (`_`, `not given`). A type whose shape is not the same in every build (`Array<...>`, `HashMap<...>`, a struct) is plain bytes up to the next field, and so is a known type the entry leaves less room than it needs; the name stands either way. Compared structure by structure with `<work>/<binary>/types.tsv`. With no record found, structures are left as they are and the command says so |
| first parameters | the functions that take a system first (below), and each applied function of the registry whose first parameter (`reg:rcx`) has an applied type or an applied tie to a structure of this pass: `ApplyThis.java` makes it a pointer to that structure (`<rva> registry <id> <structure> registry <rung>` in `systems.tsv`) |
| nothing changed | prints `<binary>: nothing was renamed since the last pass; the pseudo-code is up to date`, exit 0, Ghidra is not started |
| `--dry-run` | prints how many names and structures changed and a lower bound of the functions that would be decompiled again (the renamed functions and their callers, from the `calls` table); starts nothing, writes nothing |
| the pass | one Ghidra run without its window on the existing project, no analysis: `ApplyNames.java <changed names> rename <touched>`, `ApplyTypes.java <changed structures> <touched>`, `ExportDecomp.java <output> @<touched>`. The scripts decide what is touched, from the program's own references: a renamed function, every function that refers to it or to one of its thunks, every function that refers to a renamed global, every function with a parameter or a return value of a structure that changed. Its files are in `<work>/<binary>/refresh/`, its console output in `console.log`, its duration in `last_refresh.json` |
| the database | `decomp.sqlite` is copied to `decomp.refreshing` beside it, the touched functions are replaced there (row, text, full-text entry, calls; a function Ghidra made for a new name is added), a row goes into `republishes`, and the copy is moved over the database. **The database is never written in place**: a reader sees the old pseudo-code whole or the new whole. Where the operating system will not replace a file that is open (Windows), the move is tried for `--wait` seconds; after that the command exits 1, leaves the database as it was and the copy beside it, and remembers nothing as applied |
| `pseudocode.jsonl` | the same functions are replaced in the binary's export, so a later `load` builds the same database |
| what was applied | only after all of the above: `names.tsv` and `types.tsv` become the whole current export and record. A pass that fails anywhere before that is made again from the same difference |

One refresh runs at a time per work folder (the lock file `republish.lock` there, which every
change of the database takes); a second says so and exits 1. The
decompiler ending with anything but 0 is exit 1 with `nothing was changed`. So is a launch that
ended with 0 but whose scripts did not all run: Ghidra skips a script that does not compile, and
the scripts after it, and still ends with 0. Every script of `tools/ghidra` prints as its last line
`gatekit-script: <Script> done key=value ...` (or `... failed <reason>` when its arguments are
wrong); the part of `console.log` that launch wrote must hold that line for every script the pass
asked for and no `<Script>.java:<line>: error:`, `SCRIPT ERROR: <Script>.java`,
`GhidraScriptLoadException` or "The class could not be found". Otherwise the command prints which
script (`<Script>.java did not load or compile: ...`, `<Script>.java never printed its last line`),
exits 1, swaps nothing and writes none of `names.tsv`, `types.tsv`, `helpers.tsv`, so the next pass
is made from the same difference. `decomp run` checks the same and exits 1. `status` prints the
last refresh of each binary (`refreshed:`).

What it does not do, by construction: it does not name a field through a pointer the decompiler
was never told the type of. `*(int *)(lVar3 + 0x10)` in a system that fetched a component from
storage stays as it is, whatever the record knows about offset 0x10; the structures reach the
functions whose signature carries the type. `gatekit decomp code` adds the field names of
`this + offset` as comments when it prints.

**`gatekit decomp serve [binary] [--idle 30] [--memory 20G] [--cpu N] [--stop]`** keeps the
decompiler open on the binary's project, read-only (`-readOnly`, `DecompServe.java`): nothing it
applies is saved, and the project changes only through `refresh`. It answers over the folder
`<work>/<binary>/serve`: `ready` holds the milliseconds its own opening took; a request is a file
`<id>.request` of tab-separated lines (`type` - a structure's line as `types.tsv` has it; `param
<rva> <index> <structure>` - the parameter becomes a pointer to it; `function <rva>`), answered by
`<id>.answer` (`ok <apply ms> <decompile ms>` then the pseudo-code, or `error <message>`); both are
written whole and moved into place. It ends on `--stop` or after `--idle` minutes without a request.
While it runs, `decomp code` of a function of that binary asks it first, with what the registry
applies to the function (its parameters tied to, or typed by, a structure on an applied rung, with
that structure's applied fields only), and prints the time it took on the error stream; when the
server does not answer within 15 s, or refuses, the cached text is read and the reason is said.
`--ask <binary+0xRVA> [--param <index>=<registry type>]... [--count 3]` times one function decompiled
again by the server already open, the types first (every field of them: the project is open
read-only and the text is not printed), and prints only the milliseconds, the lines and the number
of fields read by name. Its measurements are in `research/coverage/decompiler-kept-open.md`.

**`gatekit decomp tally [--binary B]... [--json] [--jobs N]`** counts how much of a binary reads
like code; it only reads. Default binaries: `bg3_dx11.exe` and `bg3.exe`. Per binary:

| count | what is counted |
|---|---|
| functions | rows of `functions`; `default-named` are `FUN_...` and `thunk_FUN_...`, the rest `named` |
| field accesses | in all pseudo-code of the binary: `->Name` (`by name`; of those, `by a field name of the record` when the name is a field of a structure in `<work>/<binary>/types.tsv`), `->field_8`-like placeholders (`field<N>_`, `_Pad`, `_Base`, `unk`, `padding`) apart, and raw dereferences `(x + 0xNN)` / `(x + NN)` |
| typed functions | functions whose pseudo-code declares a pointer to a structure of `types.tsv` (by the name without its namespace), and how many do so before the body (parameter or return type). `not counted`, with a note, when the project was never given a structure |
| helpers | rows of `ecs_helpers` per family; `typed return` are those `<work>/<binary>/helpers.tsv` lists with a return structure (what the project was last given) |
| virtual tables | `vtabs` of the symbol database; `named` have a symbol, a matched Toolkit table (`vtab_matches`) or a row in decomp.sqlite `vtable_names` |

and once, `digest guards`: the counts `symbols digest --all` left in `<work>/digests/summary.json`
(systems, operations, guards extracted, naming a component, a field or a call, unconditional, not
extracted, and when). That file is written only by a whole pass (no `--ns`, no `--dll`, nothing
kept); without it the line says how to make it.

Then, under `the registry, described against known`, the completeness of our reading (the registry
the command finds, `GATEKIT_REGISTRY` or the one beside the coverage record), one line a kind: how
many are known, how many the registry holds, how many it describes, how many were `looked at and not
understood` (`state: looked_at`), how many were `never looked at` (known and not in the registry, and
those it holds as `unlooked`), and the registry's count by rung, strongest first:

| kind | known | described |
|---|---|---|
| functions, per binary | the binary's rows of `functions` in decomp.sqlite; the registry's are the ids of the binary's table in the build the database is stamped with | `state: described`, or a `does` with no state |
| types | the types Script Extender declares (`se_types` of the symbol database), the components of the coverage ledger and the registry's own | `state: described`, an `about`, or a field named other than a placeholder |
| fields | the fields the registry lists | named, not a placeholder, on a rung above `unknown` (the field's, else its type's) |
| globals | not counted: no database lists the game's globals | `state: described` or an `about` |
| enumerations | `enums` of the symbol database and the registry's types of kind `enum` or `bitfield` | as types, or values listed |

`--json` gives `{binaries: [{binary, functions, fields, typed, helpers, vtables, notes}], guards,
registry: {functions: {<binary>: {...}}, types, fields, globals, enumerations}, seconds}`, each kind
`{known, in_registry, described, looked_at, never_looked_at, by_rung, note}`, with `null` for what
was not counted (`registry` is null with no registry). A binary that is not loaded is exit 1.

**`gatekit decomp pair [--source bg3_macos_x64] [--target bg3_dx11.exe] [--holdout 0.4] [--precision 0.98] [--rounds 6] [--round-score 0.6] [--round-margin 0.3] [--jobs N] [--dry-run] [--misses N]`**
says which function of one build of the game is which function of another, and how far each
answer can be trusted. It writes `function_pairs` (`source_binary, source_rva, target_binary,
target_rva, score, margin, round, state, evidence, rule, votes`; the two binaries' earlier rows are
replaced) and one row of `function_pair_runs` (the bars and the counts they were read from). Both
binaries must be loaded; the symbol database gives a Windows build its type references, the macOS
build needs its file in `<work>/<binary>/` and `BG3_MACOS_SYMBOLS` for them (without, it says so
and goes on without those tokens).

| step | what happens |
|---|---|
| labels | what a function is, in words both builds share: a real name with `struct`/`class`, spaces, quotes and underscores dropped; `helper:<family>:<T>` for a row of `ecs_helpers` (get/has/add/remove) and for a macOS template of that family (`ecs::EntityWorld::GetComponent<T,...>` ...); the macOS name of a system's update entry for the thunk of `system_updates`. Names our own tools made (`FUN_`, `ecs::GetComponent<T>` ...) are not labels |
| type references | per function, the type indices it refers to, as `type|Context`: Windows from `ecs_ids` + `xrefs`; macOS from its symbol table (`ls::TypeId<T, Context>::m_TypeIndex`) and the pointer slots of its `__got` section, found in the pseudo-code as `PTR_m_TypeIndex_<address>` or by name |
| seeds | a label exactly one function of each build carries is a pair (`state` seed, `rule` label) |
| rule `calls` | for every pair made so far (and, where a paired target function only forwards to one nameless function, that function in its place): the callees of both in the order of first mention in the pseudo-code, lined up on the calls that are paired or labelled alike (longest common order). Where one free call of each build stands between the same two lined-up calls, that is a vote for the two being the same; where two or three stand on each side, they vote in order (`rule` order). A pair is proposed when each is the other's most voted, alone; `score` is the share of texts, numbers and type references they have in common, `votes` the callers that agree |
| rule `bag` | every unpaired target function against the unpaired source functions: the bag of a function is its texts and numbers (`CodeMatch.Features`), its type references, the labels of its callees and callers and the pairs among them; `score` is the weight of shared tokens over the weight of all (rare tokens weigh more), `margin` the lead over the runner-up; a pair is proposed when each is the other's best |
| virtual tables | per build the functions in each table's slots, in slot order: a Windows build's from `vtabs`/`vtab_slots` of the symbol database, the macOS build's from every symbol `vtable for X` of its file (the pointers into the code after the two header words). Before each `calls` step a target table is matched to the source table it shares the most paired functions with in the same slot order (at least two; alone at the top, then the nearer in slot count; a tie matches nothing), and a matched table is lined up exactly like a paired caller, so its free slots vote too. The matches of the run with every name go to `vtable_names` (`dll, rva, name, source, source_rva, support, slots, source_slots`), which `tally` counts as named tables |
| rounds | calls, then bag, at most `--rounds` times; a pair is built upon in the next round when it passes the round's own bar (`--round-score`/`--round-margin`; for calls 2 votes or 0.25 in common) |
| the bars | the whole thing is run twice. First with `--holdout` of the target's plain names that the source also has taken away (the same functions every run): every proposal for a held-out function is right or wrong by its name, and per rule the widest bar (score and margin; votes and likeness) is taken at which at least `--precision` of at least 30 held-out proposals are right. Then with every name, and each proposal is `accepted` when it passes its rule's bar, else `candidate`. A rule with no such bar accepts nothing |

It prints the counts, the bars with the held-out names above them and how many were right, a table
of other bars, how many nameless target functions get a name (by seed, bag, calls, order) and how
many were proposed nothing; `--misses N` lists held-out names that were proposed another name
(names only). `--dry-run` stores nothing. Candidates are kept in the table and are never names.
Like `refresh`, it writes a copy of the database (`decomp.pairing`) and moves it over the
database when done, under the same `republish.lock`: a reader never sees half the pairs, and a
refresh running at the same time makes it exit 1 with nothing stored. `helpers --rebuild`
writes `ecs_helpers` the same way (`decomp.helpers`).

Who uses the pairs: `refresh` (below) names a nameless function after the function paired with
it, source `macos-anchor` for the macOS build and `pair <binary>` for a sibling, with the pair's
state, rule, score, margin and evidence in the plate comment - only where neither the symbol
database nor a helper names the address; `helpers --rebuild` gives a helper no rule recognised the
family of the helper of the same type paired with it (confidence medium, basis `paired with ...`).

A name a pair gave is the outcome of pairing and never its evidence: a function whose line in
`<work>/<binary>/names.tsv` has a pair as its source is read as nameless (no label, not in the
held-out set), so a later run cannot confirm what an earlier one concluded; each run names it
again.

**`gatekit decomp queries <survey|map|columns|guards> [--binary bg3_dx11.exe] [--source bg3_macos_x64] [--coverage DIR] [--json]`**
is about the queries a system's update walks and the component pointers it takes from them. An
update does not fetch a component by its type index: it takes a query's description (an unnamed
global, the query's index, times 0x58 plus the world's query table), builds an iterator on the
stack, and a component pointer is then an entry index times the component's size plus the page
of a column. Nothing in that names a type, so the pseudo-code shows a decompiler's local. The
macOS build names every query (`ls::TypeId<ecs::query::spec::Spec<ls::TypeList<...>, ...>,
ecs::QueryRegistry>::m_TypeIndex`), and its `SystemUpdate<S>` and the methods of `S` that one
calls mention the specs a system uses. The macOS side needs the build's file in
`<work>/<source>/` and `BG3_MACOS_SYMBOLS`, as `pair` does; sizes are the ledger's
(`coverage.json`, `components[].inlineSize`).

`queries survey [--system TEXT] [--forms N]` counts the shape and changes nothing. Per system of
`system_updates` it reads the update entry and the updates (the updates' mentions put where the
entry calls them) and the macOS entry with the system's own methods it calls:

| count | what is counted |
|---|---|
| systems | with pseudo-code; that read a query global (`DAT_x * 0x58`); with a macOS `SystemUpdate<S>`; whose macOS side mentions a spec; with both and as many distinct globals as distinct specs |
| query globals | distinct globals, mentions, and multiplications by 0x58 of something that is not an unnamed global (a query this reader does not see) |
| specs | named by the macOS symbol table, and mentioned by these systems |
| values `index * N + base` | every assignment whose whole right side is that shape (a pointer base counts its element size in), by class: the query's own description; a constant is added; the base is a pointer to a structure; the system's macOS side names no spec; no type of that size (stride 8 apart); one type of the system's specs has that size in the ledger; one, but the local takes another stride in the same function; one, but a type of the specs is not sized; several types of that size |

`--forms N` lists the N most frequent forms of the base expression with every local, parameter
and global replaced by a letter (a form, never a line of pseudo-code). `--json` gives the counts
and one row per system (`system, entry, updates, macos_entry, query_globals, specs, spec_types,
spec_types_unsized, other_descriptions, strides[]`).

`queries map [--dry-run] [--rounds 4] [--wait 60] [--show N]` says what each query global is, from
two sources that do not depend on each other, and stores it in `query_globals` (`binary_id, rva,
spec, lists, tags, systems, round, registered, counts`; the binary's earlier rows are replaced),
through a copy moved over the database (`decomp.queries`) under `republish.lock`, like `pair`.

| source | what it gives |
|---|---|
| the order of use | in the two builds of one system's update the queries are used in the same order: where the Windows side reads as many distinct globals as the macOS side mentions distinct specs, the n-th is the n-th (round 0). Globals tied that way then anchor the systems whose counts differ: between two anchors in the same order on both sides, as many unknown globals as unknown specs are tied by position (later rounds). A global that several systems speak of must get the same spec from each - **that agreement is the measured precision** - and a global with two answers is dropped, as is a spec claimed by two globals |
| the registration | the Windows build's own registration of a query: a function sizes the ten groups of the description's component indices (a call with ten constants), stores the type index of each component in order (`TypeIndex<T>_ComponentTypeIdContext`, named by the symbol database) and then takes the query's place in the table (the same global times 0x58). Read from every function that mentions the global (full-text index); `whole` when the ten counts add up to the types stored. It names the types of each group without the macOS build |

The registration is the check of the order: a tied global whose registration stores other types
than its spec's lists hold is dropped (`registered` and `counts` are stored for every whole
registration, tied or not; `spec` only where every system agrees and the registration does not
contradict). It prints the counts, the two agreements and what was dropped; `--show N` lists N
disagreements (specs by name) and N globals without a registration; `--json` gives the same and
the kept globals.

`queries columns [--forms 30]` checks the column rule and decides, value by value, which component
a pointer taken from a column is. The iterator's state is the function's own stack and the
decompiler names a stack local by its offset, so the local that holds the column pointers and the
local that holds the entry index stand a fixed distance apart in every function: 0x50 for the
components every entity of the query has (group 0 of the registration), 0x40 for the optional
ones (group 7); the page of column k is `*(longlong *)S[k]` (`*(longlong *)*S` for k = 0). A value
of that form is `typed` only when its stride is the ledger's size of the k-th type of that group
in exactly one way among the system's queries, and every query of the system has a whole
registration; where the index is not a stack local the group is not read and the type must be
the only fit in either group. Every other value is left alone with its reason. It prints the
table the rule was read from (per distance and form: cases, cases in a system with one query, and
for each of the ten groups how often the stride is the size of its k-th type), the stride check
and the outcomes; `--json` adds one row per value (`system, function, local, column, stride,
group, outcome, type`).

`queries guards --digests DIR` reads the JSON digests of a whole pass (`gatekit symbols digest
--all --force --json --out DIR`) and says where the guards that name nothing stand against what
the typing reaches: operations, those with a guard naming something, the rest by depth (in the
update, in a function it calls), how many of the rest are in a function with a typed value, with
a value of a column's form, in a system with a typed value, how many of those hold a dereference
through an untyped pointer, and what the guards that name nothing are made of: the ceiling of
what typing the column pointers of the updates could name. No step of `refresh` applies these
types.

**`gatekit decomp systems [name] [--rebuild] [--binary bg3.exe]`** builds the binary's rows of
`system_factories` and `system_updates` on first use, or again with `--rebuild`: rows of another
binary do not count, so `--binary bg3_dx11.exe` builds the binary a copy runs even when bg3.exe's are
there. The addresses in the pseudo-code are turned into RVAs with the binary's image base, read
from its PE header when the file is found (game folder, Toolkit folder) and otherwise from its
`FUN_` names (the address less the RVA most functions agree on); a binary that is neither loaded nor
found is exit 1. It prints only the binary asked for. A (re)build that finds no system is exit 1
with a sentence naming the empty input (no functions of the binary, no `calls` rows, no function
mentioning `_SystemsContext`, or none passing a system index), never "0 systems found".

**`gatekit decomp helpers [type] [--rebuild] [--binary bg3_dx11.exe] [--coverage DIR]`** finds the
out-of-line helpers through which the game reaches a component and writes them to `ecs_helpers`
(`dll, rva, family, type, returns, confidence, basis`). A helper is a function of at most 1,600
bytes whose code refers to exactly one type-index global of the symbol database (`ecs_ids` and
`xrefs`, any context), a component's (`ComponentTypeIdContext`, `UncheckedComponentTypeIdContext`,
`OneFrameComponentTypeIdContext`), that is not a system's update entry, update, factory or
constructor (`system_updates`, `system_factories`: build those first) and does not call a helper of
another type. Its family, from what can be read off mechanically:

| family | rule |
|---|---|
| `registration` | it writes the type index |
| `add` | it calls `ecs::ComponentFrameStorageAllocRaw`; the basis says whether the record's size of the type (`--coverage`) is among the arguments |
| `remove` | it returns nothing and calls `ecs::legacy::ImmediateWorldCache::GetChange` |
| `has` | it returns a bool (declared so, or `return CONCAT71(...)`) and calls `GetChange` or `ecs::EntityRef::IsNull` |
| `get` | it returns another value and calls one of those two |
| `unknown` | no rule applies, or one of the above also calls a named function of the game other than a constructor or destructor |

A function no rule recognises, or a get/has/add, that nothing calls directly is left out (a method
called through a table). Each family is then checked against the macOS build, whose templates
carry their names (`ecs::EntityWorld::GetComponent<T>`, `ecs::EntityWorld::HasComponent<T>`,
`ecs::EntityRef::Has<T>`, `ecs::legacy::ImmediateWorldCache::AddComponent<T>` / `RemoveComponent<T>`,
`ecs::EntityCommandBuffer::AccessAddStorage<T>` / `AddComponentUnchecked<T>`, ...): from the
`bg3_macos_x64` pseudo-code when loaded, else `BG3_MACOS_SYMBOLS/symbols-demangled.tsv`. `confidence`
is `high` when the macOS build has the same family of the type (or an add allocates the record's
size), `medium` when only the rule says so, `none` for unknown. `returns` is `component` for a get
or add that returns a value and takes at most 3 parameters (its return type may be set), else
`bool`, `nothing` or `value`. On a (re)build it prints `N helpers of <binary>: <count per family>`
and `checked against <source>: <family> A of B (P %), ...` - B the helpers whose type the macOS
build has any of these templates of, A those it has the same family of; the macOS build keeps few
`has` out of line, so that rate is low by construction. Without `type` it prints one line per
family (helpers, types, high, returning the component); with it, the helpers of the types whose
name contains it: `<binary>+0x<rva>  <name>  [family, confidence; returns ...]  basis`. A helper's
name is the engine's verb in namespace `ecs`: `ecs::GetComponent<T>`, `ecs::HasComponent<T>`,
`ecs::AddComponent<T>`, `ecs::RemoveComponent<T>`, `ecs::RegisterComponent<T>`,
`ecs::ComponentHelper<T>` (unknown). A missing symbol database or binary is exit 1. So is a
(re)build that finds a refresh or a pairing running in the work folder (nothing is written), and a
(re)build whose inputs are empty, with a sentence and `ecs_helpers` left as it was: no `ecs_ids`
rows of the binary in the symbol database (built without a Script Extender tree; build it again
with `BG3SE_SRC`), no `xrefs` to them, or no helper recognised at all - never "0 helpers".

`refresh` has a third step, after the names and the fields:

| Step | What happens |
|---|---|
| component helpers | every row of `ecs_helpers` of the binary at an address the symbol database does not name, as `rva, family, type, name, return structure, basis` - the return structure is the type itself for a helper that returns the component when the record has a layout of the type, else empty - compared line by line with `<work>/<binary>/helpers.tsv`. A helper of `helpers.tsv` that the rows do not hold is handed over with family `-` |
| the pass | `ApplyHelpers.java <changed helpers> <touched>` after `ApplyTypes.java`: names the function in namespace `ecs` (a function with a name of its own that is not one of these is left as it is, refused), puts the family and basis in its plate comment, and for a return structure decompiles the helper once, commits the parameters the decompiler sees and sets the return type to a pointer to the structure `ApplyTypes.java` made (found the same way). Family `-` gives the default name, an undefined return type and an unlocked prototype back. Touched: the helper and every function that refers to it or to its thunks |
| refusals | `ApplyNames.java` and `ApplyHelpers.java` write what Ghidra refused to `<their file>.refused` (rva, name, reason). The command prints them (`<binary>: N names refused by Ghidra (rva, name, reason); the next pass offers them again:` and one line each) and leaves them out of `names.tsv` and `helpers.tsv`, so the next pass tries them again |

`refresh` has a fourth step, the systems' own fields:

| Step | What happens |
|---|---|
| a structure per system | for every system of `system_updates` / `system_factories` of the binary, a structure named as the system, as large as the furthest offset its functions below reach from their first parameter (rounded up to 8, at least 8) and with nothing in it (a line of `types.tsv` like a component known only by size). A system the record already has a structure of keeps that one. Only with a coverage record |
| the functions that take the system first | the update entry the factory stores (always: the engine calls it with the system, the world and the time); the update, when the entry's pseudo-code calls it with `param_1` as its first argument; the constructor, when it stores something at the start of its first parameter (the virtual table). A function two systems share is left out, as is one without pseudo-code or a first parameter. As `rva, role, system, structure, basis`, compared line by line with `<work>/<binary>/systems.tsv` |
| the pass | `ApplyThis.java <changed functions> <touched>` after `ApplyHelpers.java`: commits the parameters the decompiler sees when the function has none, and gives the first one the type pointer to the system's structure (refused, in `<file>.refused`, when there is no function, no structure, no parameter, or the first parameter is not 8 bytes). The pseudo-code then reads `this->field_0x38` where it read `param_1 + 0x38`, and a guard of `symbols digest` reads `System.field_0x38`. Touched: the function itself |

A function of `systems.tsv` that the pass does not list is left as it is in the project. The fields are the decompiler's own
names (`field_0x38`): what a queue or a cache is called is not known from the code, and nothing here
invents it.

The summary says `N names and M structures and K component helpers [and J functions of systems] applied`.
`--dry-run` counts the helpers that changed and their callers.
