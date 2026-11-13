# Where the game's start-up time goes, and what can be done about it (2026-11-12)

Game 4.1.1.7631656 (Patch 8), `bg3_dx11.exe` unless said otherwise, the GateKit's Script Extender
build (v34 branch), started by the stand in the instance `perf`. Machine: Ryzen 5 5600X (6 cores,
12 threads), 32 GB, NVMe SSD, NVIDIA; Windows Defender's real-time protection is **off** on it.

**Read this first: the conditions.** The machine was never idle. Other sessions ran their own
games (0 to 3 at a time), compilers and an eight-thread decompiler; "others" below is how many
cores the rest of the machine used on average during a run (the profiler records it). Wall-clock
seconds therefore vary by a factor of two between identical runs; processor seconds and bytes
read vary much less. Compare only rows taken under similar load. A cold file cache could not be
produced (no administrator rights, and the `.pak` files are held open by other games), so every
number is with a warm cache except where noted. All raw runs are in `perf/runs/` (not in git).

The short version:

- **For tests:** do not restart the process. `Osi.LoadGame` of a save made at the start of the
  session brings back a clean world in **12–14 s** against 37–45 s for a fresh start under the
  same load. A level change inside the process takes 11 s (small level) to 37–58 s (Act 1).
- **For players:** there is no single "GTA Online" bug in Patch 8. The one clearly wasteful
  thing found is that the game asks Windows 90,000–150,000 times per load whether a loose file
  exists before reading the packed one. A 250-line DLL that answers those questions from memory
  cut `LoadModule` by 22 % in a test start and by half when loading a modded save (measured in
  the stand's clone, where each question costs twice what it costs in an installed game). That
  is seconds, not a "several times faster" patch; the rest of a load is real work that is
  mostly serial, driver shader compilation, and reading 5–20 GB from disk.

## Tools (all in `perf/`)

| Tool | What it does |
| --- | --- |
| `startup_profile.py` | Starts the game (`--level`, `--save`, `--menu`, `--player-mods`, `--runs N`, `-- <game args>`), samples the process 4 times a second, reads the game's own log and prints the stages with seconds, processor time, bytes read; `--report [labels]` tabulates recorded runs with medians; `--sampler` attaches the stack sampler; `--inject DLL` loads a DLL at start |
| `stack_sampler.py` | Sampling profiler without administrator rights or symbols: suspends a thread just long enough to copy its registers and stack top, unwinds afterwards from the modules' unwind tables; also catches the path of a file-system call in progress |
| `stack_report.py` | Names (symbols database: Toolkit matches, the game's own log strings, DLL exports), tables by thread / inclusive / own time, categories (`categories.json`), `--parallelism`, `--tree NAME`, `--wall` |
| `reload_bench.py` | Reloads inside the running game: `save-load`, `load <save>`, `level <name>...`, timed from the game's log, with a check that the world came back as saved |
| `graphics_bench.py` | Settings as they are against the minimum: time, bytes read, memory, idle processor use, window size, whether the game kept the file |
| `idle_cost.py` | Processor use of a game that is only standing there; minimised window; which engine switches Script Extender exposes |
| `exists_cost.py` | Cost of one "does this file exist" question in a folder |
| `fastexists/` | The prototype DLL (source, `build.cmd`); `inject.py` loads it into a test game |
| `run_events.py`, `lua.py` | The game's log lines of a recorded run; Lua in the perf instance's game |

## 1. Stages of a start

Stages are the client's states from `bin\network.*.log`; "to LoadModule" is everything from
process creation to the client entering `LoadModule` (engine start, `InitMenu`, `Init`,
connection to the local server). Seconds; medians where n > 1.

### Straight into a level: `-testLoadLevel GustavX:TUT_Avernus_C`, bridge mod only

| Runs | Other games / others' cores | to LoadModule | LoadModule | LoadSession | LoadLevel + Swap | PrepareRunning | Playable | CPU s | Read |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 (sampler attached) | 0 / 6.1 | 7.8 | 10.1 | 4.4 | 1.5 | 5.0 | **28.8** | 125 | 5.9 GB |
| 2 | 0 / 5.4–6.6 | 9.4 | 15.8 | 2.4 | 2.6 | 11.6 | **41.8** (38.7, 44.9) | 122 | 5.0 GB |
| 4 (prototype DLL loaded but off) | 1 / 6.5–7.0 | 10.1 | 13.0 | 3.9 | 2.0 | 11.6 | **40.9** (30.4–43.1) | 120 | 4.7 GB |
| 1 (first start of a new instance) | 1 / 9.1 | 14.1 | 21.0 | 5.3 | 3.2 | 25.8 | 69.3 | 193 | 6.2 GB |
| 1 (`bg3.exe`, Vulkan, first Vulkan start, sampler) | 2 / 9.2 | 23.3 | 22.0 | 4.5 | 33.4 | 21.6 | 104.8 | 245 | 4.6 GB |

`docs/facts.md` has 26–29 s for the same start on a quieter day, which agrees with the first row.
The server's `LoadModule` runs beside the client's and ends 2–3 s later (12.1 s in the first row).

The first 5 s (process creation to the first client state) are one thread: 4.3 s of processor
time in the first row, of which Script Extender's own start is 1.6–1.9 s, NVIDIA's libraries
(`_nvngx.dll`, `nvapi64`) 1.3 s, and 308 MB are read.

Memory at the end: 2.1–2.2 GB in use, 7.5–7.6 GB committed.

### To the main menu (bridge only), one run, 1 other game / 7.7 cores

Process start to first state 7.9 s, `InitMenu` 1.4, `LoadModule` 13.9, `LoadMenu` 16.2 (6.3 GB
read: the menu's scene), **menu after 40.2 s**; 91 processor seconds; 1.7 GB in use, 6.9 GB
committed.

### The player's path: `-loadSaveGame` of an Act 1 Honour-mode save with the player's 11 mods (252 MB of paks)

| Run | Other games / others' cores | LoadModule (menu's + game's) | LoadLevel | PrepareRunning | Playable | CPU s | Read |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 1 / 7.4 | 26.0 | 23.5 | 16.2 | **99.7** | 256 | 17.9 GB |
| 2 | 2 / 9.7 | 24.6 | 71.6 | 14.6 | **157.4** | 267 | 16.0 GB |

Memory at the end: 3.6–4.1 GB in use, 8.7–8.8 GB committed. (`docs/facts.md`: 61 s for this
path on a quieter day, without the eleven mods.) The same two runs with the prototype are in
section 4.

## 2. What is inside `LoadModule`

Four starts were recorded with the stack sampler. Processor seconds *at work* (samples whose
top frame is a wait or a yield are left out; the worker pool spins between jobs and that is not
work):

| | DX11, 0 other games | DX11, 1 other game | DX11, 1 other game | Vulkan, 2 other games |
| --- | ---: | ---: | ---: | ---: |
| Window (server `LoadModule`), s | 12.1 | 49.6 | 32.2 | 24.7 |
| At work, CPU s | 31.3 ¹ | 31.3 | 18.0 | 55.7 |
| Loose-file existence checks (`GetFileAttributesW`) | 5.6 | 6.6 | 2.1 | 6.9 |
| Graphics driver (`nvgpucomp64`: shader / pipeline compilation) | 2.4 | 0 | 0.1 | 23.4 |
| Job: resource banks (`Content`: visuals, materials, animations, sounds) | 3.9 | 3.7 | 2.8 | 4.2 |
| Job: static data objects (`ls::ModdableFilesLoader::LoadModuleObjects`) | 2.9 | 2.4 | – ² | 2.4 |
| Job: root templates and timeline templates (`ls::ModManager::LoadRootTemplates`) | 1.4 | 1.1 | 1.2 | 1.2 |
| Job not identified (`bg3.exe+0x18B8C60`) | 1.0 | 1.4 | 1.2 | 1.6 |
| Job: localization | 0.3 | 0.5 | – | 0.6 |
| Job: stats (`Stats/Generated`, `DivinityStatsSystem::Init`) | – | – | – | 0.2 |
| Other work on the worker pool | 5.1 | 5.5 | 3.1 | 5.7 |
| Not attributed (stack did not unwind to a known root) | 8.5 ¹ | 9.9 | 7.4 | 9.6 |

¹ One sample of 9.8 s in that recording was an artefact of the sampler (fixed afterwards) and is
taken out here. ² Not separated in that recording.

What this says:

- **It is mostly one thread at a time.** In the quiet start, over the 19 s recorded, exactly one
  thread was at work for 8.2 s, two for 4.7 s, three for 2.1 s, four or more for 2.2 s. The
  module's loaders are queued as jobs on the worker pool but run one after another; the pool's
  other threads spin (`SwitchToThread`) and wait, which is where the "2–4 cores" of processor use
  during loading come from.
- **Loose-file checks are the largest single item that is not real work:** 12–21 % of the work.
  Before the file manager opens a file it has not opened yet, it asks Windows whether a loose
  file of that name exists and is not a folder (`bg3.exe+0x41A08A0`, called from the file open at
  `+0x4191480`), and only then looks in the packages. Counted exactly by the prototype's
  measuring mode: **93,540 questions in one start into the test level, 89,515 answered "no such
  file"**; summed over all threads they took 11.8 s of thread time in that run (clone, loaded
  machine). Loading the modded Act 1 save asks 153,000. What is asked about (caught by the
  sampler): `Public\...\TimelineTemplates\<uuid>\*.lsf`, `Scripts\anubis\...\*.ann` / `*.anc`,
  resource banks `...\[PAK]_*\_merged.lsf`, `Localization\...\Soundbanks\*.lsf`, level `meta.lsf`,
  GUI `*.DDS` and `*.xaml`, `*.wem`. One question costs 35 µs in the installed game's `Data` and
  72 µs in a stand instance (whose `Data\Public` and others are junctions) — `perf/exists_cost.py`.
- **Driver shader compilation is occasionally the largest item of all.** NVIDIA's compiler took
  23 s of processor time in the first Vulkan start of the instance and 34 s in one of the DX11
  starts (that recording is not in the table: its figures include waits), and nothing in the
  other DX11 starts. The driver caches compiled shaders per user profile; a stand instance has a
  profile of its own, so its first start pays this. Why one later DX11 start paid it again is
  not known.
- **Text stats are not a cost:** 0.2 s. Caching parsed stats would gain nothing.
- **Story (Osiris)** is not loaded in `LoadModule` but in `LoadSession` (2.4–5.3 s in total on
  both sides); its inside was not broken down. The engine switches `DisableStoryPatching` and
  `ForceStoryPatching` exist (both `false`) but are read-only from Lua, so their effect was not
  measured.
- **Disk:** `LoadModule` reads only about 100 MB. The reading is in `PrepareRunning`: 3.4–4.8 GB
  (virtual-texture tiles `*.gts`, textures, sounds), during which the process is mostly waiting
  (8.4 processor seconds at work against 20 waiting in a 10 s window). That stage took 3.6 to
  25.8 s for the same level, depending on what else used the disk and cache.
- **Network:** nothing waits. In an instance Steam and Discord fail to initialise at 2.9 s and
  return at once; UPnP discovery finishes in the background at 7.1 s. No PlayFab line appears in
  the log. (The installed game with Steam running was not profiled.)

## 3. For tests

### Reloading without leaving the process — the recommendation

| What | Seconds | Conditions | Clean world? |
| --- | ---: | --- | --- |
| Fresh start into `TUT_Avernus_C` (for comparison) | 37–45 | 0–1 other games | yes |
| `Osi.AutoSave()` (after `CanAutoSave = true` on both sides) | 2.6–4.7 | | – |
| `Osi.LoadGame("<save folder name>")`, level `TUT_Avernus_C` | 14.0, 14.0, 26.4 | 2 other games | yes |
| the same | 12.0, 12.2, 12.6, 12.8, 13.0, 28.3 | 1 other game | yes |
| `Osi.TeleportPartiesToLevelWithMovie("WLD_Main_A", "", "")` from the test level | 57.9 first (22.7 GB read), 37.2 second | 2 other games | no (same session) |
| back to `TUT_Avernus_C` | 10.8 | 2 other games | no |
| Fresh start into `WLD_Main_A` (coverage session's figure) | 131 | 2–3 other games | yes |

`Osi.LoadGame` skips `LoadModule` altogether: unload 3.3 s, server `LoadSession` 4.7–6 s,
`LoadLevel` 1.2 s, `PrepareRunning` 2.5–3.5 s; 28–35 processor seconds, 0.45–0.5 GB read. "Clean
world" was checked each time: gold added, the host teleported 4 m and an object variable set
before the load were all gone after it, and the bridge answered on both sides. The save's folder
name is `<character>-<digits>__AutoSave_<n>` under whichever player profile the game uses
(`reload_bench.py` finds it by comparing the folders before and after).

So: one start per test session, an autosave once the level is up, then `Osi.LoadGame` between
scenarios instead of `down` / `up` — about three times faster and less sensitive to other games
(it reads a tenth of the data). A level change serves scenarios that need another level and do
not need a clean world.

Not tried: re-entering the `-testLoadLevel` path from inside the game (the function is
`esv::LoadProtocol::LoadModuleAndLevel`; it needs a native call, and `Osi.LoadGame` already gives
what tests need); loading a save from the main menu through UI commands.

### Minimum graphics and a small window (`research/perf/graphics_bench.py`, runs alternated)

| | Settings as they are | Everything at minimum |
| --- | ---: | ---: |
| Playable, s (2 runs each; 1–2 other games, others 8–9 cores) | 54.0, 67.4 | 45.8, 39.4 |
| `PrepareRunning`, s | 12.9, 12.9 | 6.7, 8.8 |
| `LoadModule`, s | 21.3, 15.9 | 20.8, 14.0 |
| Read from disk | 4.8 GB | 2.7–3.2 GB |
| Processor seconds | 152 | 130–145 |
| Memory in use / committed | 2.1 / 7.5 GB | 2.1 / 7.0 GB |
| Idle processor use | 2.1–2.5 cores | 2.3 cores |
| Window | 1280 × 720 | 1040 × 420 |

The settings are applied (less is read, half a gigabyte less is committed, the window changes),
but: the game makes the window 1040 × 420, not the 640 × 360 asked for; it rewrites
`graphicSettings.lsx` at start and drops the keys that were set to 0; a frame cap of 30 does not
lower the idle processor use; memory in use does not change. Worth having for the smaller read
(the last stage is shorter), not a large gain. No setting that switches rendering off was found
(nor any headless or dedicated-server mode: no such strings in the executable, no public
mention).

### A game that is only standing there (`perf/idle_cost.py`)

2.2–2.6 cores, window in the background; 2.4 minimised; the same after restoring. The stacks
show the worker pool: threads cycling through `SwitchToThread` and short waits. With
`--cpuLimit 4` on the command line the process has 79 threads instead of 88–90 and idles at
1.7 cores; its effect on the start time could not be separated from the load that day (one run).
`YieldOnLostFocus` is not among the switches Script Extender exposes, so it was not tried.
Suspending an idle game's process (which would bring it to zero) was not tried either.

### A pool of warm games on this machine

- Memory: 7.5 GB committed and 2.1 GB in use per game in the test level (8.8 and 3.6–4.1 in
  Act 1). With 32 GB and a 24 GB page file the commit limit is 55 GB; four games plus everything
  else left 1.4 GB of it at one point today.
- Processor: every idle game takes 2.2–2.6 of 12 threads. Three idle games leave a starting one
  a third of the machine, which is what the coverage session's 43 / 96 / 290 s (alone / with one
  / with two more) shows.
- So: **two games, three at most**, never start two at once (a start wants 3–6 cores for half a
  minute), and prefer one long-lived game reset with `Osi.LoadGame` to several short-lived ones.

### What to change in the stand (for the stand session; nothing in `stand/` was touched)

1. A reset that does not restart: after `up --level`, set `CanAutoSave`, `Osi.AutoSave()`, remember
   the new save folder; `game.reset()` = `Osi.LoadGame(name)` + wait for the client's `Running`
   + wait for the bridge. `test` then needs one start per level instead of one per group of
   scenarios. Measured: 12–14 s against 37–45 s.
2. `Stage.Level` works as written (verified both ways); document 11–58 s.
3. Optional: load `perf/fastexists` into stand games (it needs a way in: `perf/inject.py`, or a
   line in the Script Extender build) — `LoadModule` 13.0 → 10.2 s, 14 processor seconds less
   per start.
4. Optional: minimum graphics for instances (`research/perf/graphics_bench.py` has the keys): shorter
   `PrepareRunning`, 0.5 GB less committed.
5. Do not expect a frame cap or a minimised window to free the processor.

## 4. For players

### What others have done

Sources: [bg3se issue 458](https://github.com/Norbyte/bg3se/issues/458),
[bg3se issue 217](https://github.com/Norbyte/bg3se/issues/217),
[Hotfix 28 notes](https://baldursgate3.game/news/hotfix-28-now-live_125),
[ositools](https://github.com/Norbyte/ositools) (its options `OptimizeHashing`,
`DisableModValidation`), [the GTA Online discussion](https://news.ycombinator.com/item?id=26296339).

- Patch 7's Hotfix 27 made starts take minutes with mods; the community fixed it with an early
  return patched into one function of the executable (bg3se issue 458, Nexus mod 13071), and
  Larian fixed it in Hotfix 28. What the function did was never said in public.
- Divinity: Original Sin 2's Script Extender has `OptimizeHashing` / `DisableModValidation`
  against a mod-hashing slowdown that grew with the number of mods. bg3se has no such option.
- bg3se issue 217: a story merge repeated on every load (8 minutes) when the save's mod set did
  not match — the game's own logic.
- No public analysis of what `LoadModule` does; no public list of the switches used here.
- GTA Online (2021): found by exactly this method — stack sampling without symbols, then a DLL
  hook. That game had one quadratic parser eating minutes. This one does not.

### The prototype: `perf/fastexists`

A DLL that replaces the game's import of `GetFileAttributesW` (in memory; no file is changed)
with a function that knows every path under `Data` — one walk of the tree on its own thread at
load (0.6–1.7 s, beside the game's start), kept current by directory change notifications, also
below junctions — and answers "no such file" itself. Existing paths, paths outside `Data` and
anything under `\ScriptExtender\` (hot-reloaded files) go to Windows as before. If it cannot
watch a folder or change events are lost, it stops answering. `FASTEXISTS=measure` only counts
and times; `FASTEXISTS=off` loads it and does nothing (for A/B runs).

| Path | Without | With | Notes |
| --- | --- | --- | --- |
| Level start, client `LoadModule`, s | 12.8, 13.2, 12.2, 15.2 (median 13.0) | 10.3, 10.0, 10.1, 10.9 (median 10.2) | 4 alternated pairs, 1 other game |
| …server `LoadModule`, s | 15.5 | 12.1 | medians |
| …processor seconds for the whole start | 120 | 106 | medians |
| …playable, s | 40.9 | 38.3 | medians; the last stage's noise is larger than the gain |
| Modded Act 1 save, `LoadModule` (both), s | 26.0, 24.6 | 11.4, 12.3 | 2 alternated pairs |
| …processor seconds | 256, 267 | 211, 214 | |
| …playable, s | 99.7, 157.4 | 92.9, 86.8 | the 157 had a 72 s `LoadLevel` under two other games |
| Questions answered from memory | – | 89,286 of 93,745; 141,227 of 153,229 | |

Checked with it loaded: the game reaches `Running` in the test level and from the modded save,
and the bridge answers on both sides. **Not checked:** the stand's scenario suite, a long play
session, multiplayer, Vulkan, a save made with it loaded (it does not touch saving).

**What a player would get — an estimate, not a measurement.** All of the above is in a stand
clone, where one question costs 72 µs; in the installed game's folder it costs 35 µs on this
machine, so expect about half the gain: 1–2 s of a level load, 5–7 s of a modded save load
started from the desktop. With an antivirus that inspects every file query (Defender's real-time
protection is off here) the questions are dearer and the gain larger; that was not measured.
The DLL could not be tried in the installed game: this project writes into the memory of
throw-away test games only.

**Risks.** It changes no game logic, so saves and multiplayer are not involved. A mod that
ships loose files under `Data` is seen by the walk; mods in `%LOCALAPPDATA%` paks are outside
`Data` and never touched. A file created while the game runs is added through the change
notification — there is a window of milliseconds in which the game could be told "not there";
Script Extender's hot-reload folder is exempt for that reason, and a mod manager writing into
`Data` while the game runs is not something that happens. Game updates: the hook is on an
import, there is no address in it. The right home for it would be Script Extender itself (it
already sits on this file-open path) rather than a second injected DLL; nothing was proposed or
published.

### Everything else, and why there is no drastic patch

| Part of a load | Share | Can it go? |
| --- | --- | --- |
| Loose-file questions | 12–21 % of `LoadModule`'s work | Yes — the prototype |
| Module loaders running one at a time | most of `LoadModule`'s wall time | Only inside the engine: the jobs share state |
| Driver shader compilation | 0 on most starts, 20–35 processor seconds on some | Not the game's; the driver's cache normally covers it |
| Reading 5 GB (level) to 18–20 GB (Act 1 save) | `LoadLevel`, `PrepareRunning`, `LoadMenu` | Disk and file cache; lower texture detail reads 35–43 % less |
| Engine start to first state, 5–8 s on one thread | fixed | Script Extender's own 1.6–1.9 s is the only part visible from outside |
| Stats parsing | 0.2 s | Nothing to gain |
| Story | inside `LoadSession`, 2–5 s | Not examined |
| Network at start | no waits seen (instance, Steam off) | – |

So the honest answer: **a sharp speed-up for players is not available from outside the engine.**
An unmodded Patch 8 load is not broken; it is long because it does a lot, mostly on one thread,
and reads a lot. Seconds can be taken off (the prototype; texture detail), not a multiple. The
case that *is* worth a tool is the one the Hotfix 27 story showed: when some mod set or patch
makes loads take minutes, `stack_sampler.py` + `stack_report.py` will show where within one
run — that is now a five-minute job.

## 5. Not measured

- Cold file cache (first start after a reboot); any HDD.
- The installed game with Steam running, cloud sync, the launcher; the player's path *to the
  menu* with the player's mods (that run stalled waiting for the bridge and was not looked into).
- The prototype in the installed game, under an active antivirus, in a long session, in
  multiplayer, with the stand's scenarios.
- `NoStoryPatch` / `AlwaysPatchStory` / `SlowHDDMode` / `YieldOnLostFocus` / `FileLoadingLog`:
  the names are in the executable; Script Extender exposes only `DisableStoryPatching` and
  `ForceStoryPatching`, read-only. Where the game reads them from was not found.
- What `LoadSession` and `LoadLevel` do inside (a recording exists for the test level; only
  `LoadModule` and `PrepareRunning` were analysed). `LoadLevel` of Act 1 is 23–72 s of the
  player's path and deserves the same treatment.
- `--cpuLimit` on start time (one run, under load); suspending idle games; more than one start
  with a cold driver shader cache; why one DX11 start recompiled shaders.
- A quarter to a third of `LoadModule`'s processor time is "not attributed": stacks that did
  not unwind to a known root (the copy is limited to 32 KB of stack).
- Only 25,000 of 392,000 functions of `bg3_dx11.exe` have names; pseudo-code exists for
  `bg3.exe` only, so the Vulkan recording was used to name the jobs.
