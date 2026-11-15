# Lua API contract

What the bridge mod (`mods/GateKit`) defines inside the game under the table `GK`, and what it
uses of the Weave mod (`mods/Weave`) it depends on. The signature and the comment of every public
function are in the annotation files `lua-types/GateKit.lua` (the table `GK`) and
`lua-types/Weave.lua` (the table `Weave`); this document says what the functions do and how they
work together. How the host reaches the functions - the file channel, the event log's files, the
classes of a command - is [bridge-protocol.md](bridge-protocol.md).

Sides: **S** server Lua state, **C** client Lua state, **UI** client code that must run inside
`Ext.UI.Defer`, **both** loaded into both states. The two states are separate Lua worlds; a file
that exists on both sides (`GK.Focus`) is two different tables with the same name.

## 1. The mod: globals, console commands, channels, load order

The bridge is `mods/GateKit/Mods/Gwynerva_GateKit/`: module UUID
`b1d5c0de-7e57-4b1d-9e0a-6d6576646b69`, mod table `GateKit`, Script Extender version 33 or later
(`ScriptExtender/Config.json`). Under Script Extender every mod's globals live in a table of its
own, so code outside the mod reaches them as `Mods.GateKit.<name>`; the host's prelude and the
console's are `local GK = Mods.GateKit.GK`.

| Global | Defined in | Notes |
| --- | --- | --- |
| `GK` | `Shared/Bridge.lua` (`GK = GK or {}`) | the API table; every other file adds to it |
| `Probe` | `Shared/Probe.lua` | the table `GK.Probe` is; `GK.Probe` and `GK.Seen` are the names the host and the tools use |
| `Weave` | `BootstrapClient.lua`, client only | `Mods.GateKitWeave.Weave` when the Weave mod is loaded, else nil; `GK.Weave` is the same table (section 4) |
| `GK.Log` | `Shared/Bridge.lua`, set by each `GK.Run` | adds lines to the `log` of the chunk being run; not a stable function |
| `GK.LogContext` | set and cleared by `BootstrapServer.lua` and `Client/Console.lua` | attributes console output |
| `Mods.GateKit.PersistentVars` | `Server/SaveState.lua` | the key `GKState` (version 1) holds the bridge's memory in a save |

Console commands (typed as `!<name>` in Script Extender's console, client side): `gk` toggles
the GateKit console, `gkui` opens its settings window, `gkrescue` puts its windows back in place.

Net channels, all on the mod's `ModuleUUID`:

| Channel | Direction | Carries |
| --- | --- | --- |
| `GK_Run` -> `GK_Result` | client -> server -> client | a console command (`kind = "run"`) or a quiet evaluation (`"watch"`, `"ask"`) and its answer |
| `GK_Complete` -> `GK_Completions` | client -> server -> client | the console's completion of a name |
| `GK_Reset` | client -> server | the console's "Reload Lua": `Ext.Debug.Reset()` on the server |
| `GK_Ask` -> `GK_Answer` | client -> server -> client | a client function asking the server for what only it knows (`GK.Gui.Aiming`: how a character stands to the party) |
| `GK_Log`, `GK_Echo` | server -> client | the server's log lines for the console |
| `GK_Focus` | server -> client | the outlines and the calm screen of `GK.Focus` |

The file channel of the host is the folder `Script Extender/Gwynerva_GateKit/`: `queue_<side>.jsonl`
in, `res_<side>_<id>.json` out, `state_<side>.json` the heartbeat, and the single-command form
`cmd_<side>.json` answered in `res_<side>.json`; the event log is `events_<side>.json` and
`events_<side>_<boot>_<n>.jsonl` ([bridge-protocol.md](bridge-protocol.md)). The folder's name is
part of the contract with the host, independent of any Lua name.

Load order (`BootstrapServer.lua`, `BootstrapClient.lua`):

- Server: `Shared/` Bridge, Verbs, EventLog, Attention, ConsoleLog, Complete, Probe; `Server/` Api,
  Stage, Speed, Pause, Situation, Conditions, Act, Cheat; `Shared/` Memory, Trace; `Server/` Heard,
  Attention, EventFamilies, GameLog, Focus, SaveState, Fixtures, Rolls; then `GK.Verbs.Guard()`,
  `GK.Start("server")` and the net channels of the console.
- Client: `Shared/` Bridge, Verbs, EventLog, Attention, ConsoleLog, Complete, Probe, Memory, Trace;
  `Client/` Api, Sound, UI, Stage; the UI catalogue when present (section 7); `Client/` Windows,
  XamlChains, WindowReaders, Popups, Camera, Places, Focus, Aim, Act, Attention; then
  `GK.Verbs.Guard()` and `GK.Start("client")`; then, only when Weave is loaded, `Client/` Overlay,
  Console, Panels.

The files look each other up through `GK.<Table>` when a function is called, not when a file
loads, so the order constrains only what a file does as it loads.

## 2. The files and what each defines

The tables and functions each file adds to `GK`; the signatures are in `lua-types/GateKit.lua`.

### Shared (both)

| File | Public names |
| --- | --- |
| `Shared/Bridge.lua` | `GK`, `GK.Dump`, `GK.Run`, `GK.Format`, `GK.AutoSave`, `GK.Start` (the file channel of the host), `GK.Log` (per chunk); the observed answers: `GK.Observe`, `GK.Unobserved`, `GK.Follow`, `GK.Later`, `GK.Ticks`, `GK.LimitTicks`, `GK.TickMs`, `GK.Clock`; a hit read from the game: `GK.HitOutcome`, `GK.HitSaves`, `GK.HitAttack`, `GK.HitRow` |
| `Shared/Verbs.lua` | `GK.Verbs` (`Load`, `Of`, `List`, `Guard`, `Within`; section 19) |
| `Shared/EventLog.lua` | `GK.EventLog` (`LoadRegistry`, `Append`, `Row`, `Since`, `Previous`, `On`, `Off`, `SwitchedOn`, `ScopeEnded`, `Tick`, `Costs`, `Families`, `Kinds`; section 15), `GK.Note`, `GK.Step`, `GK.StepEnded`, `GK.Events` |
| `Shared/Attention.lua` | `GK.Attention` (`Push`, `Set`, `State`, `Mark`, `Since`, `Tick`, `Costs`, `Waiting`; section 11) |
| `Shared/ConsoleLog.lua` | `GK.LogSinks`, `GK.Features`, `GK.CutUtf8`, `GK.ShortTrace`, `GK.ConsoleEmit`, `GK.FlushLog` |
| `Shared/Complete.lua` | `GK.Complete`, `GK.CommonPrefix` |
| `Shared/Probe.lua` | `GK.Probe` (reading engine objects and their layouts), `GK.Seen` (the capture: `Start`, `Mark`, `AddSpecs`, `Drain`, `New`, `Types`, `Stop`, `Status`, `Spill`) |
| `Shared/Memory.lua` | `GK.Memory` (section 16) |
| `Shared/Trace.lua` | `GK.Trace` (section 16.1) |

### Server (S)

| File | Public names |
| --- | --- |
| `Server/Api.lua` | `GK.Item`, `Character`, `Party`, `InDialog`, `Pos`, `Give`, `Find`, `Equip`, `Unequip`, `Unsheath`, `Teleport`, `Spawn`, `Dialogs`, `StartDialog`, `Remove`, `Watch`, `Watched` |
| `Server/Stage.lua` | `GK.Stage`: the stage scope and its ledger (section 14), the staging verbs (spawn, status, damage, heal, surface, lock, flag, fact, level ...), and the tables `Templates`, `Factions`, `TriggerKinds` |
| `Server/Speed.lua` | `GK.Speed` (`Enable`, `Disable`, `Set`, `Get`, `Boost`, `Rule`, `State`, `Clock`, `Profile`, `Working`, `Class`; section 18) |
| `Server/Pause.lua` | `GK.Pause` (`Set`, `State`, `While`) |
| `Server/Situation.lua` | `GK.See`: what a character perceives and how the scene stands (`Perception`, `Perceives`, `PartyPerceives`, `Relation`, `Around`, `Entity`, `Watchers`, `Exposure`, `Combat`, `Targets`, `Odds`, `Path` ...) |
| `Server/Conditions.lua` | `GK.Conditions` (`Eval`, `Check`) |
| `Server/Act.lua` | `GK.Act` on the server: the fair verbs `Select`, `EndTurn`, `Flee`, `TurnBased`, `Lockpick`, the jobs (`Job`, `Jobs`, `HasEnded`), what blocks an action (`CannotAct`, `Stuck`, `Provokes`, `Threats` ...), and the fair names of the scripted actions, which answer with both routes (section 19); `GK.Cheat`'s scripted actions (`Move`, `Cast`, `Attack` ... section 19) |
| `Server/Cheat.lua` | `GK.Cheat`: the catalogue of named cheats beside the scripted actions, `List`, `Used`, `Reset` |
| `Server/Fixtures.lua` | `GK.Fixture` (`Fight`, `Talker`, `Trader`, `Loot`, `Lock`, `Trap`, `Down`, `Box`, `Clear`, `Made`, `Ready`, `ProvokeReady`, `WhyNoThreat`, `RunOp`, `Defaults`, `Track`; section 12) |
| `Server/Rolls.lua` | `GK.Rolls` (section 17) |
| `Server/Heard.lua` | `GK.Heard` (`Since`, `Poll`, `Start`, `Stop`) |
| `Server/GameLog.lua` | `GK.GameLog` (`Since`, `Kinds`): writes and reads the `game` family of the event log |
| `Server/EventFamilies.lua` | no public names: writes the detailed families of the event log (section 15) |
| `Server/Attention.lua` | no public names: the server's hooks of `GK.Attention` |
| `Server/Focus.lua` | the server's `GK.Focus` (`Show`, `Glance`, `Clear`, `SetEnabled`, `State`, `JobStarted`, `JobFinished`); sends on `GK_Focus` |
| `Server/SaveState.lua` | `GK.SaveState` (`Snapshot`, `Write`, `Restore`, `Info`): the bridge's state tables in `Mods.GateKit.PersistentVars` |
| `BootstrapServer.lua` | the console's channels; `GK.Start("server")` |

### Client (C / UI)

| File | Public names |
| --- | --- |
| `Client/Api.lua` (C) | `GK.Client`, `Controlled`, `ControlledSet`, `Scenes`, `Tracks`, `FastScenes`, `ScanCode`, `Key`, `Shot`, `ShotPending`, `FrameCap`, `SetFrameCap` (section 9) |
| `Client/Sound.lua` (C) | `GK.Sound` (section 8) |
| `Client/UI.lua` (UI) | `GK.ViewModel`, `ViewModels`, `Properties`, `Dialog`, `Answer`, `Fold`, `Commands`, `Command` |
| `Client/Stage.lua` (UI) | `GK.Blocking`, `RollNext`, `BoxPress`, `Unblock`, `Autoplay`, `AutoplayInfo`, `Controls`, `Press` |
| `Client/Windows.lua` (UI) | `GK.Gui`: `Widget`, `Data`, `Open`, `Page`, `IsOpen`, `InputEvents`, `Event`, `Show`, `Close`, `CloseAll`, `Find`, `Controls`, `ActionOf`, `UseSlot`, `Picture`, `Changed`, `ObserveEffect`, `Click`, `Select`, `Input`, `Snapshot`, `Read`, `Item`, `ItemDo`, `FilmPlaying`, `Stuck`, `NewGame`, `List`, the registry of windows `Windows`; the client's `GK.Cheat` window verbs `Run`, `Set`, `Step`, `Auto`, `Quiet`, `Recover` |
| `Client/XamlChains.lua` (UI) | `GK.Gui.XamlChain`: a control's click chain read from the game's XAML (section 11) |
| `Client/WindowReaders.lua` (UI) | no new functions; adds `read` to the entries of `GK.Gui.Windows` |
| `Client/Popups.lua` (UI) | `GK.Gui.Blocking`, `Resolve`, `DialoguePhase`, `RollBonuses`, `AddRollBonus`, `Trade`, `Combine`, `LongRest`, `QuickSave`, `QuickLoad`, `SaveAs`, `LoadLatest`; the entries of the windows that wait for the player (dialogue, roll, reaction, box, tutorial, loot, trade, pickpocket, rest, learn ...) |
| `Client/Camera.lua` (C) | `GK.Camera` (`State`, `Look`, `Face`) |
| `Client/Places.lua` (UI) | `GK.Places` (`Zone`, `Markers`) |
| `Client/Focus.lua` (C) | the client's `GK.Focus` (`SetCalm`, `State`, `Outline`, `ClearOutlines`, `Aim`, `Confirm`, `ClearAim`); listens on `GK_Focus` |
| `Client/Aim.lua` (UI) | `GK.Gui.Aiming`, `Aim`, `FirstClickPicked`, `Confirm`, `CancelAim`; `GK.Cheat.AimBest` (section 11) |
| `Client/Act.lua` (UI) | the client's `GK.Act`: `Cast`, `Attack`, `Press`, `Draw`, `Sheathe` (section 19) |
| `Client/Attention.lua` (C) | the client's pollers of `GK.Attention` (`Pollers.windows`, `Pollers.film`, `OnHook`) |
| `Client/Overlay.lua` (C) | `GK.Overlay` (`Get`, `OnChange`, `Register`), `GK.OverlaySet`, `OverlayState`, `OverlayRescue`, `OverlaySettings`; the console key, the HUD button, the settings window; console commands `gkui`, `gkrescue` |
| `Client/Console.lua` (C) | the GateKit console: `GK.ConsoleAsk`, `ConsoleRun`, `ConsoleWatch`, `ConsoleUnwatch`, `ConsoleWatches`, `ConsoleAction`, `ConsoleReload`, `ConsoleEvents`, `ConsoleLog`, `ConsoleOutput`, `ConsoleState`, `ConsoleSet`, `ConsoleSubmit`, `ConsoleTab`, `ConsoleCopy`, `ConsoleHistory`, `ConsoleClear`, `ToggleConsole`, `ConsoleHooks`; console command `gk` |
| `Client/Panels.lua` (C) | `GK.UI` (`Panel`, `Remove`, `Open`, `Panels`, `Press`, `Read`): another mod's own tab in the GateKit console; the builder's methods `SameLine`, `Text`, `Separator`, `Button`, `Toggle`, `Input`, `Select`, `Values`, `Watch` (`GK.PanelBuilder`) |
| `Client/ui/overlay.weave`, `hud.weave` | GateKit's own Weave components: the settings window and the HUD button |
| `BootstrapClient.lua` | the UI catalogue's loading (section 7), `GK.Weave`, `GK.Start("client")` |

## 3. What is not part of the contract

What `lua-types/GateKit.lua` and `lua-types/Weave.lua` do not annotate is internal: every `local`,
and the fields a file exposes only so that a reload keeps them or another bridge file reaches
them - among them `GK.Stage.Scene`, `GK.Act.JobList` and the Osiris listeners of `GK.Act.On`,
`GK.Speed.Time`, `GK.Rolls.Seen`, `GK.Heard.Seen`, `GK.Focus.Channel` and `Generation`,
`GK.Focus.PulseTick`, `GK.Gui.PressBox`, `GK.EchoStats`, and the table of modelled condition
functions `GK.Conditions.Functions` (extended only inside `Server/Conditions.lua`). The registry of
windows `GK.Gui.Windows` is public; the reader and the stepper of each window
(`GK.Gui.Windows.<name>.read`, `.step`) are not. Of Weave, `Weave.Internal` and every
`Weave.Backend` function (the renderer's seam) are internal.

## 4. Weave, the mod GateKit depends on

Weave is `mods/Weave/Mods/Gwynerva_Weave/`: a UI framework for Script Extender mods, client side
only (module UUID `8ee3d1c7-0362-41bc-b7c9-b10d85c62f4b`, mod table `GateKitWeave`). GateKit's
`meta.lsx` lists it under `Dependencies`, so Weave loads first. Its files define the global
`Weave` in Weave's own environment; other mods reach it as `Mods.GateKitWeave.Weave`. Its
annotations are `lua-types/Weave.lua`, its own description `mods/Weave/README.md`.

- **The shell** (`Client/Shell.lua`) is what every Weave window of every mod shares:
  `Weave.Settings` (`Get`, `Set`, `OnChange`, `State`, `Saved`, `SetWindow`, `Open`; the theme,
  the font, `blockKeys`, `blockMouse`, kept in `Script Extender/Gwynerva_Weave/settings.json`),
  `Weave.RegisterWindow` (a hand-built IMGUI window taking part in input blocking and Esc),
  `Weave.Blocking`, `Weave.Rescue`, `Weave.OnKey`, `Weave.Demo`, `Weave.Showcase`; console
  commands `weaverescue`, `weavedemo`, `weaveshowcase`. While a Weave window is open the game gets
  no keys or clicks (each switchable), and Esc closes the window opened last.
- **Paths.** The user's own copies of components are looked for in
  `Script Extender/Gwynerva_Weave/ui/`, components' storage is `Gwynerva_Weave/storage/`, the
  built-in root is `Mods/Gwynerva_Weave/ScriptExtender/Lua/ui/`. GateKit adds its own root,
  `Mods/Gwynerva_GateKit/ScriptExtender/Lua/Client/ui/`, with `Weave.AddRoot`.
- **GateKit's overlay** (`Client/Overlay.lua`) keeps the console key, the HUD button and the
  settings window, in `Script Extender/Gwynerva_GateKit/overlay.json`; `GK.Overlay*` answer for
  Weave's four settings too, passing them through to `Weave.Settings`, and
  `GK.Overlay.Register` is `Weave.RegisterWindow`. Weave's settings found in `overlay.json` while
  Weave has saved none of its own are written to Weave's file.
- **Without Weave** the client bootstrap prints one error line and loads the bridge without
  `Client/Overlay.lua`, `Console.lua` and `Panels.lua`: `GK.*` and the file channel work; the
  console, the overlay and the HUD button do not exist.
- Weave trims its own tracebacks and reads nothing of `GK`.

## 5. Couplings inside the bridge

- **The console borrows Weave's theme.** `Client/Console.lua` is a hand-built IMGUI window, not a
  Weave component; it reads `Weave.Color`, `Skin`, `OnRestyle` and `KeepOnScreen`, and takes part
  in input blocking through `GK.Overlay.Register`. `Client/Overlay.lua` mounts `overlay.weave`
  and `hud.weave` and reads Weave's themes and fonts as it loads.
- **Two `GK.Focus` tables**: the server's (markers) and the client's (the calm screen, outlines),
  joined by the `GK_Focus` channel and a shared name; the annotation file models them as one class
  with notes of the side.
- **Three names of windows**: `GK.Gui` is the game's windows, `GK.UI` the panels of other mods in
  the GateKit console, `GK.Weave` the Weave mod. Inside `Client/Windows.lua`, `Popups.lua`,
  `WindowReaders.lua` and `Aim.lua` the local `UI` is `GK.Gui`; inside `Client/Overlay.lua` it is
  `Weave`.
- **Late-bound lookups on the server**: `GK.See` calls `GK.Act`, `GK.Stage`, `GK.Conditions`;
  `GK.Act` calls `GK.See`, `GK.Focus`, `GK.Stage`, `GK.Conditions`; `GK.Focus` calls `GK.See` and
  is called by `GK.Act`; `GK.SaveState` reads and writes the state tables of `GK.GameLog`,
  `GK.Heard`, `GK.Act`, `GK.Cheat` and `GK.Focus`. Every one is a lookup at call time.
- **The mod table's name is written into code**: `local GK = Mods.GateKit.GK` is the prelude of
  the console (`BootstrapServer.lua`, `Client/Console.lua`) and of `Client/Stage.lua` and
  `Server/Stage.lua`; `Client/Panels.lua` documents `Mods.GateKit.GK.UI` as the entry point for
  other mods.

## 6. Annotations

`lua-types/GateKit.lua` and `lua-types/Weave.lua` are in the format of Script Extender's
`ExtIdeHelpers.lua`, for LuaLS. Every function either file annotates is part of this contract,
with the side it runs on.

## 7. The UI catalogue

`GK.UICatalog` is the game's widgets and what their controls do, derived from the game's own
XAML. It is not shipped, since it is derived from Larian's files. `BootstrapClient.lua` loads
`Script Extender/Gwynerva_GateKit/UICatalog.lua` when it is present (a file that sets
`GK.UICatalog = { ... }`); GateKit has no command that writes it. Without it `GK.Gui` works, and
`GK.Gui.Click` reads a button's click chain from the game's XAML itself
(`Client/XamlChains.lua`, section 11).

## 8. `GK.Sound`

`Client/Sound.lua`, client side only, because sound belongs to the client: every running copy of
the game mixes its own, which is what makes per-copy volume possible at all.

The game holds its sliders in twelve `ls::GlobalSwitches::SoundSetting`, the array
`SoundSettings[12]` (`{ float ConfigValue[3]; FixedString ConfigKey; HashSet<FixedString> RTPCs; ... }`), and a
setting pushes `ConfigValue[1]` into its Wwise RTPCs, which is what the mixer reads. `GK.Sound`
writes those RTPCs through `Ext.Audio.SetRTPC(nil, name, value)`, the same values the sliders end
up setting, with the game's own names: the twelve `ConfigKey` strings and the eleven
`RTPC_Volume_*` names, read out of the game executable's own string table and verified live.

**Stock Script Extender does not publish `SoundSettings`** - it is absent from the property map,
so `Ext.Utils.GetGlobalSwitches().SoundSettings` raises *property does not exist*. GateKit's
build maps it, and `GK.Sound.Settings()` reads it; on the stock build that function returns nil
rather than raising.

| Function | What it does |
|---|---|
| `GK.Sound.Categories` | the twelve, in the options screen's order: `{ key, name, rtpc }` each |
| `GK.Sound.Category(c)` | the entry whose key, short name or RTPC is `c` (case-free); nothing means the master |
| `GK.Sound.List()` | each category with its current `value` (0..100) and `muted` |
| `GK.Sound.Get(c)` / `Set(c, v)` | read or write one category; `Set` clamps to 0..100 |
| `GK.Sound.Master(v)` | the master slider, read without `v` |
| `GK.Sound.Mute(c)` / `Unmute(c)` | to 0 remembering the value / put it back; `Unmute()` restores all |
| `GK.Sound.Muted()` | `{ key = the volume remembered }` for what is held at 0 |
| `GK.Sound.WhenUnfocused(on)` | the engine's own `MuteSoundWhenNotFocused`, read without `on` |
| `GK.Sound.Settings()` | the player's own settings: `{ key, name, value, configured, default, display }` each, in the same order; nil on a build that does not map `SoundSettings` |

Keys and RTPCs: `MasterVolume`/`RTPC_Volume_MAIN` (master), `FxVolume`/`RTPC_Volume_SFX` (fx),
`AmbientVolume`/`RTPC_Volume_Ambient`, `MusicVolume`/`RTPC_Volume_Music`,
`MusicFightVolume`/`RTPC_Volume_Music_Fight` (fight), `CutsceneVolume`/`RTPC_Volume_Cinematics`,
`UIVolume`/`RTPC_Volume_UI`, `VoiceVolume`/`RTPC_Volume_VO_Master` (voice),
`VoiceVolumeDialog`/`RTPC_Volume_VO_Dialog` (dialogue),
`VoiceVolumeNarrator`/`RTPC_Volume_VO_Narrator`, `VoiceVolumeOverhead`/`RTPC_Volume_VO_Overhead`,
and `RumbleVolume`/`RTPC_Rumble`, which is controller vibration rather than sound - `GK.Sound`
does not write it, and `Categories` carries no `rtpc` for it, but the engine does route it
through an RTPC like the rest (seen in `SoundSettings[12].RTPCs`, which GateKit's build maps).

What follows from writing the RTPCs rather than the settings:

- **The options screen does not move**, and nothing is ever written back to the profile's
  `config.lsf`: a volume set here cannot leak into a player's own settings. (The screen does not
  draw `ConfigValue` either: it reads the settings store. Which store it writes is not known;
  `research/se-fork/upstream/sound-settings.md`.)
- **The last writer wins.** The player moving a slider, or the game applying its settings,
  overwrites what GateKit set. GateKit does not fight for it.
- **Nothing survives a restart**, and `GK.Sound.Muted()` is emptied by a Lua reset - the volumes
  then stay where they were, so `Unmute` after a reset restores nothing.
- **`Settings()` and `List()` answer different questions.** `List()` reads the RTPCs - what this
  copy sounds like, GateKit's own writes included. `Settings()` reads the engine's twelve
  `SoundSetting`s - what the player chose, which GateKit never touches. Its three numbers are the
  engine's three floats: `configured` is `ConfigValue[0]`, the volume the player set;
  `value` is `ConfigValue[1]`, the one the engine sends to the RTPCs; `default` is
  `ConfigValue[2]`, the game's own default (100 for all twelve). After `GK.Sound.Set` the mixer
  reads the new value while the setting keeps the player's.
  `display` is the row's name in the options screen, in the game's language.
- **`configured` and `value` come apart when the game silences itself.** With
  `MuteSoundWhenNotFocused` on and the window behind another one, `MasterVolume` and
  `CutsceneVolume` read `configured = 100, value = 0`, and their RTPCs read 0 with them: the
  engine mutes by moving the value in effect, never the player's own. A test that wants to
  know whether a copy is audible reads `value`, not `configured`.
- `MuteSoundWhenNotFocused` reads and writes, but the engine acts on it when the window gains or
  loses focus: setting it does not silence a copy that is already in the background until focus
  next changes.

## 9. `GK.FrameCap` and `GK.SetFrameCap`

`Client/Api.lua`, client side, over `Ext.ClientRender.FrameCap()`, `SetFrameCap(fps)` and
`SetLostFocusFrameCap(fps)` in GateKit's Script Extender build.

A copy of the game whose window is not the one in front draws at **thirty frames a second**. That
is not the machine giving up: the engine throttles itself on purpose, and it matters to GateKit
more than to anyone else, because at most one of several copies can ever be in front and a
mirrored copy never is.

| Function | What it does |
|---|---|
| `GK.FrameCap()` | `{ frameCap, effectiveFrameCap, lostFocusFrameCap, microsecondsPerFrame, hasFocus, yieldOnLostFocus }`, or nil where the build cannot reach them |
| `GK.SetFrameCap(fps)` | caps this copy at `fps` whether its window is in front or not; `0` lifts the cap and the background throttle with it |

How it works, and why nothing is hooked: `BaseApp` keeps three sixteen-bit caps - asked for, in
force, and the one to fall back to when focus is lost - and its clock holds one number derived
from the cap in force, the microseconds a frame must last. Zero means "no cap" at every step, so
`lostFocusFrameCap = 0` turns the background throttle off by itself; and a cap that is to hold in
the background has to be written to both caps, since the smaller of the two wins while the window
is not in front. `GK.SetFrameCap` does both.

The Toolkit exports `BaseApp::GetFrameCap`, `BaseApp::SetLostFocusFrameCap` and
`ls::GameClock::SetFrameCap` by name, each two or three instructions long; the game, a
shorter build of the same engine, holds the same fields, and they are found in it by the engine's
own arithmetic: the one place where a sixteen-bit cap and a sixty-four-bit microsecond count
satisfy `microseconds == 1000000.0f / cap`. That identity is checked before anything is written,
so a game whose layout has moved is not written to at all.

What follows:

- **The engine honours a cap set from outside its options screen**, in a background window too:
  `GK.SetFrameCap(0)` lifts the background throttle, and a cap of `n` holds the copy near `n`.
- **The bridge answers on the game's own tick**, so a cap of `n` costs up to `1/n` of a second of
  latency. A copy at ten frames a second is fine; two frames a second is not a sensible floor.
- **The game's own options overwrite it.** Changing a graphics setting makes the engine re-apply
  `FrameCapEnabled` / `FrameCapFPS` from the graphic config, which is the same field.
- Nothing survives a restart.

## 10. No `true` for nothing

These functions refuse what cannot be done and answer with what was done; every answer is truthy,
so `if GK.Give(...)` holds whenever something was done.

| Function | Refused (an error, nothing done) | Answers |
|---|---|---|
| `GK.Give(template, count, who)` (S) | a template that is not a root template the game has (`Ext.Template.GetRootTemplate`) | `{template, name, count, to, before, note}`: `before` is what the inventory held; the new items are queued |
| `GK.Stage.Teach(spell, who)` (S) | a name that is not in the stats, or is not `SpellData` | `{spell, who, new}`: `new` false when the character knew it |
| `GK.Command(name, parameter, wanted)` (UI) | no such command on that view model; the game's own `CanExecute` saying no | `{ran, canExecute, note}`: the command RAN, which is not that the game did anything (photo mode with a dialogue open, a short rest out of reach) - read the effect. `canExecute` is nil when the command could not be asked |
| `GK.Commands(wanted)` (UI) | a `wanted` view model that is not on screen, so that a command name given in its place is not read as "no such command" | the names of the commands |
| `GK.Stage.Damage(amount, who, kind, anyway)` (S) | a target without a `Health` component; the last party member standing, without `anyway` | observed (section 11): `{who, asked, kind, hpBefore, hpAfter, dealt, row, hit, note?}` - the hit points from the game's `Health` (the game applies damage at once), `row` the seq of the game's own `game_hit` row of the hit and `hit` the damage it says; `dealt = 0` carries a note (a resistance, an immunity, temporary hit points or immortality absorbed it), and no `game_hit` row within the limit says so in `note` |
| `GK.Stage.Heal(who, hp)` (S) | a target without a `Health` component | observed: `{who, asked, hpBefore, hpAfter, note?}` once the game's `Health` holds what was asked (the game sets hit points a tick later); a `note` when at the limit it holds something else |

`GK.Stage.Clean()` says in `did` every change of its ledger it takes back - the line the undo gives
(`hit points of <id> set back to <hp> (were <hp>)`), else `undone: <what>` - and every hit point
of the baseline it puts back; a value asked for once in a clean is not asked again by a later step
of it.

## 11. Aiming at the world, and answers that are what was observed

### Aiming (Client/Aim.lua, UI side)

The engine's picking (`ecl::PickingHelper::DoPick`) casts its ray from `ls::Mouse` - the operating
system's pointer, which injected SDL motion never moves; `SetTargetOverride` is read by neither the
picking nor the spell targeting. A hotbar action's targeting is a client task
(`ecl::CharacterTask_Spell`, the input controller's PreviewTask) that takes each frame whatever the
picking holds. So the aim is written into the picking helper's result right after the game's own
`PickingHelper` system ran (`Ext.Entity.OnSystemPostUpdate`): the character, item or corpse handle
and its position, or a ground point, with the "picked in UI" flag cleared. Everything after is the
game's: the task's target, its preview (trajectory, area, outlines, the cursor's texts), and on a
left click (press and release in different frames) the cast request.

| Function | Does | Answers (observed) |
|---|---|---|
| `GK.Gui.UseSlot(id)` | the hotbar's `UseSlotCommand` with the slot holding `id`: a spell id, an AutomationId or its tail (`HotBarSlot_Item_<uuid>`, `HotBarSlot_ItemName_<name>`), or part of an item's name - the throw list's items too | ok when the targeting of that action began or took the item; else `saw = "nothing changed: no targeting began"`; a slot whose command says no is not pressed |
| `GK.Gui.Aim(target)` | aims at an entity uuid or `{x, y, z}` while a targeting is active | `GK.Gui.Aiming()` once the game's targeting holds the aim; ok = false and what it holds otherwise. The same aim as the one held, made again without a break, answers at once and reads the same. An invalid aim is answered with its reason (the task writes its line a moment after the view model turns invalid) |
| `GK.Gui.Aiming()` | reads the targeting | `{mode, spell, shape, aim = {entity, name, position}, aimedByUs, valid, reason, lines, hit_chance, base_hit_chance, advantages, disadvantages, damage_min, damage_max, damage_types, affected = {{uuid, name, relation, hit_chance}}, warning}` |
| `GK.Gui.Confirm()` | the left click (press, release two ticks later) | ok when the cast started: the server wrote the targets into the caster's cast of that action (`eoc::spell_cast::StateComponent.Targets`), `cast = {spell, guid, targets = {{entity, position}}}`; the aim is given back then. ok with `picked = uuid` and no cast when the click was the first of a two-click action (Throw: the object) - the task adds `NeedsPreviewToActivate` to its flags with the object as its result, drops the object from its own `Target`, and stays open for where it goes (an ordinary one-click attack on a creature adds the flag too, but its `Target` keeps the creature: that click waits for its cast); the aim stays held. ok = false: nothing aimed or an aim the game says is not valid (`did = "nothing"`, `saw` with its `reason`), the targeting still open after the click (the aim stays held), or closed with no cast of it within a second |
| `GK.Gui.CancelAim{keep}` | gives the pointer back and leaves the targeting with the hotbar's `ActionCancelCommand` whenever the caster holds a task (a spell's targeting, or a walk the game made of it) or the server holds a preview cast of it (`keep = true`: only the pointer) | ok when the targeting of the action in hand closed (the game's idle targeting that follows - the main-hand attack under a pointer over a hostile - does not count against it) and the server holds no preview cast of the caster on two looks ten ticks apart (`GK.Stage.PreviewCasts`, asked over `GK_Ask`); ok = false with `left` (the spells whose preview the server keeps) otherwise. A target action aimed at a point of the ground becomes a walk (`mode = "MoveTo"`) and leaves the server's preview behind, which no cancel ends: `GK.Stage.Verify` names it |
| `GK.Cheat.AimBest{want, avoid, candidates, targets}` (a cheat: every character within 30 m counts, perceived or not) | tries each enemy, the middle of every pair and of all, reads `affected` (or the hit chance, `want = "best chance"`) for each, keeps the best | `{best, ranking = {{aim, value, enemies, avoided, valid, affected, hit_chance}}}`; no click |

- `shape` is the spell's own numbers from its stats (`SpellType`, `TargetRadius`, `AreaRadius`,
  `ExplodeRadius`, `Range`, `Angle`, `Shape` ...) and a `kind`: single, several targets, point,
  radius, radius (around the caster), cone, line.
- `affected` is the game's own highlight: the set of `code:ecl::spell_preview::HighlightsComponent` on
  the preview entity of the spell being aimed (the outlines a player sees). Nothing is computed
  here; with no preview entity the list is empty and `affectedNote` says so. `relation` (self,
  party, ally, neutral, enemy) is the server's `GK.See.Relation`, asked over the `GK_Ask` channel.
  The outline follows what the party is shown: a creature with the `Invisibility` component that
  is not a party member is left out, at any distance, whatever the members' sight.
- `GK.See.Perception{target, who, radius, dead}` (server, observation, a player's knowledge) is
  that rule as a reading: the creatures the party is shown, nearest first, each with `sight` - the
  members whose sight holds it now (`ServerViewshedParticipant.CanSee` on the creature: the
  member's sight range, a clear line, not invisible, in every direction). A hidden creature is
  never named, placed or counted; `{target = uuid}` answers `perceived = false` for it as for a
  uuid that is not there. A hireling waiting off the level (on stage, no level) is not listed.
  The same rule, for a character or an item, is `GK.See.PartyPerceives(uuid)`: every row of the
  event log that says whether the party knows it (`knows`: `game_hit`, `weapon_state`, the game
  log, the fixtures) reads it, and so do `GK.See.Around`, `Entity`, `Watchers`, `Exposure`, `Combat`
  and `Targets`. `GK.See.Perceives(member, uuid)` is one member's sight only.
- `valid` is the hotbar's `ActiveTask.CanExecute`; `reason` the task's own text lines
  (`TaskInfo.Texts`: "Not enough resources", "Invalid target" ...). `lines` = `{text, context,
  template}`: the line with its argument put in (`"80%"`) and the game's template (`"[1]%"`).
- `hit_chance` (a number, 1-99 or 100 for a sure thing), `base_hit_chance` (one roll's chance,
  before advantage and disadvantage: `HitChanceDesc.BaseHitChance`), `advantages`,
  `disadvantages` (the game's own names of each, "Paralysed", "Poisoned") - present only when the
  task shows a hit chance line (the line's own TextContext byte 0x80, `ecl::TextLine` +0x10):
  attack rolls and saving throws alike. `damage_min`,
  `damage_max` (after the target's resistances) and `damage_types` - present when the aim is on a
  creature and the action deals damage. Each `affected` entry under the aim has its `hit_chance`
  too (the game shows a chance for the creature under the aim only). All of it is the player's
  view model, the cursor text's own source: `CurrentPlayer.UIData.HitChanceDesc`
  (`TotalHitChance`, `Advantages`, `Disadvantages`, `ShowDescription`),
  `DamagePreviewWithResistances` (`Min`, `Max`) and `ObservableDamagesPreview` (`DamageType`),
  which the engine fills when the hovered target changes
  (`gui::ViewModelProvider::OnUpdateOverheadTarget`) - the aim written into the picking counts, no extender change is
  needed. The view model follows a new target some frames (about 100 ms at 30 frames a second)
  after the task's lines, and whether a target out of reach can be walked to is judged later
  still - past an obstacle the task first gives the straight line's verdict and judges the walk
  to a place with a clear line a few picking updates later - so `GK.Gui.Aim` answers a new aim
  after `settle` updates of the game's picking system (default 20), which are not the frames drawn
  (`previewWaitedTicks`; a recorded gap: no event of the verdict is known), and the same aim made
  again at once. The picking is followed by one subscription to the game's `PickingHelper` system,
  made on the game's own event for a level running (GameStateChanged to Running) on the main
  thread, the earlier one dropped first; the bridge's tick watches it only until its first update.
  The character aimed for is the client's controlled one, kept from `ClientControl`'s creation and
  destruction (`GK.Controlled()`, Client/Api.lua);
  `GK.Gui.Aiming()` read on its own right after the hovered target changed may still show the
  previous one. `hitChanceNote` says when the task shows a chance and the view model gave none.
  The task's own line keeps the number elsewhere: `TaskInfo.Texts[i]` is `ecl::TextLine`
  (0x14 bytes: a `ls::TranslatedString` - handle at +0, argument-string handle at +8 - and the
  TextContext byte at +0x10, 0x80 = HitChance, 0x04 = Warning), exactly as Script Extender declares it; the
  argument ("80", a decimal string, type 3) lives in a `ls::TranslatedArgumentString` that the
  `ls::TranslatedStringRepository` keeps under the argument-string handle (a counter),
  which Script Extender does not expose.
- `AimBest{want = "best chance"}` ranks single targets by `hit_chance` (an aim with none ranks
  last); the default `want` is the most enemies. `ranking` rows carry `hit_chance`.
- `GK.Focus.Aim` answers ok = false and names GK.Gui.Aim; `GK.Focus.Confirm` is a GK.Cheat.Cast on
  the server.

The player's path differs from the server-driven `GK.Cheat.Cast` of the same spell in what the
game creates: on the client `ecl::spell_cast::TargetingComponent`, the
`ecl::spell_preview::*` components, `code:eoc::spell_cast::SyncTargetingComponent` and
`code:ecl::PlayerTargetEntityChangedOneFrameComponent`, and on the server
`code:esv::spell_cast::ClientInitiatedComponent`, `esv::spell_cast::MovementInfoComponent` and
`code:eoc::spell_cast::PreviewEndEventOneFrameComponent` - none of which the `GK.Cheat` path creates.

The subscription to the `PickingHelper` system: one made as the bridge loads at game start never
fires, and one made or dropped from an `Ext.UI.Defer` callback (the render thread) can crash the
game - so it is made, and the earlier one dropped, on the main thread on GameStateChanged to
Running (and at load when a level runs); made again at most three times a level when no update
comes in a second. Until it fires, `GK.Gui.Aim` answers ok = false and says so.

Limits: one local player (picking helper 1).

### Observed answers

`GK.Observe(result, check, within)` (Shared/Bridge.lua; bridge-protocol.md) lets a verb answer
with what it saw; `check(ticks, final)` gets the side's ticks since the observation began, and
`within` (ms of a running game) is counted in those ticks (`GK.LimitTicks`). A verb that waits on
another's observation follows it with `GK.Follow(answer)` - a function that looks once a call and
returns the final answer or nil - the loop the file channel runs; `GK.Ticks()` is the side's tick
count. The rule for the window verbs: a picture of the window before acting - the open
widgets, the plain values of the widget's view model (flags, numbers, texts, list counts), the
pressed control's enabled / visibility / checked / selected - and again every third tick after; `ok`
only when it changed (`saw` lists what), else `ok = false, did, saw = "nothing changed", hint`.
A command whose own `CanExecute` says no is not run (`saw = "the button is disabled ..."`). `ok`
keeps its name and its meaning of "it worked"; fields are added, none renamed.

| Verb | `ok` means |
|---|---|
| `GK.Gui.Click` | the click chain from GK.UICatalog, else from the game's own XAML (`chain = "xaml"`), ran and its effect was observed; the control's own command is asked first |
| `GK.Cheat.Run` | the command ran and its effect was observed |
| `GK.Gui.Input` | the binding ran and its effect was observed |
| `GK.Gui.ItemDo` | equip/unequip: the item's own `Equipped` changed; use/drop: the item or the windows changed |
| `GK.Gui.UseSlot` | the hotbar's UseSlotCommand ran and the targeting began |
| `GK.Cheat.Unequip` | the item left the slot; refused when it is not equipped |
| `GK.Act.Select`, `EndTurn` (server way), `TurnBased`, `GK.Cheat.Sneak(false)` | observed: the character controlled; not its turn; the mode; SNEAKING gone |
| `GK.Act.EndTurn(viaUi)`, `Flee` | a check only, and `did` says so |
| `GK.Stage.ItemAction` | a table (truthy): pickup, drop, equip, unequip, lock, unlock, destroy observed |

`GK.Gui.Select` and `GK.Cheat.Set` read back what they set; GK.Cheat's walking, casting and
using verbs are jobs that end on the game's events.

A button's command may not be its `Command`: `finishLevelUpButton`'s `Command` is
`PlayDummyVFX`, and FinishLevelUp is in the button's `b:Interaction.Triggers` (a TimerTrigger,
1400 ms after the click). `Client/XamlChains.lua` reads such a chain from the game's XAML
(`Ext.IO.LoadFile(path, "data")`, the user's own install; nothing kept). The live element cannot
tell the chain: its attached `Triggers` reads as a `NoesisApp.TriggerCollection` that Script
Extender hands out as a plain DependencyObject.

### AutomationId

`GK.Gui.Controls` rows carry `automationId` (the control's own `ls:AttachedProperties.AutomationId`)
and `automationIds` (its own and up to three inside it, eight levels down: a hotbar slot is
`HotbarSlotIndex_3` holding `HotBarSlot_Spell_Projectile_FireBolt`), read from the element's stored
dependency values (`element:DependencyProperties().AutomationId`). Filters take `automationId`;
`GK.Gui.Click(widget, id)` and `GK.Gui.UseSlot(id)` accept one as an address.

### Attention

`GK.Attention` (Shared/Attention.lua; hooks in Server/Attention.lua and Client/Attention.lua) is a
view of the event log (section 15): `Push(what, fields)` appends a news row, `Set(key, item | nil,
ended)` a row that opens, changes or closes the waiting state `key` (`item.what` is its kind),
`Waiting` reads the open states as items `{ what, at, seq, tick, key, ...payload }` (a view of
`GK.EventLog.Open`, not writable), `State()`, `Since(seq)` and `Mark()` read the rows a caller is
told, `Costs()` says what the hooks and the log cost. What the host does with it:
bridge-protocol.md, "Attention"; mcp.md section 4. Observation.

## 12. `GK.Fixture`: scenes staged in the empty test level

`Server/Fixtures.lua`. A fight, a talker, a trader, loot, a lock and a trap built next to the party in
`GustavX:Basic_Level_A` instead of loading an act. Staging is a cheat; what a caller does in the
scene afterwards is GK.Act / GK.Gui. Every fixture answers
`{ fixture, entities = { { id, role, name, at } }, howto, rule, cannot }`: `howto` is one line for the
caller, `rule` the game's rule that makes the scene work, `cannot` what the empty level cannot host.

| Fixture | What it makes | How it is used | Seen in the game |
|---|---|---|---|
| `Fight{ enemies, provoke, caster, who, distance, mortal }` | armed hostiles in sight (the fight starts by the game's rule); the member made unkillable (hit points stop at 1) unless `mortal` | GK.Gui.UseSlot + Aim + Confirm (scripted: GK.Cheat.Attack / Cast), GK.Act.EndTurn | combat_started, turn order, enemies survive a round |
| `Fight{ provoke = true }` | a melee enemy inside the member's reach that flees on its turn (COMMAND_FLEE); the member's melee weapon drawn and its opportunity attack set to ask | GK.Act.EndTurn, then GK.Gui.Resolve{reaction = 'react' / 'decline'} | the reaction prompt; the attack hit |
| `Fight{ caster = true }` | a caster that casts `Defaults.casterSpell` at the member on its turn; the member taught Shield, Hellish Rebuke, Counterspell (slots given), all set to ask | GK.Act.EndTurn | Counterspell and Hellish Rebuke prompts |
| `Talker{ dialog = 'greeting' / 'roll' / resource }` | an NPC owning the dialog through a `DB_Dialogs` fact, as a level's story gives one | GK.Cheat.Talk; GK.Answer(1) opens the roll of `'roll'` | dialogue, roll |
| `Trader{ treasure, gold }` | a trader (SetCanTrade, the trade treasure generated once the first opening began) and gold for the party | GK.Cheat.Trade; GK.Gui.Trade | trade window, goods and gold on both sides |
| `Loot{}` | a chest filled from a treasure table and a corpse | GK.Cheat.Use (chest), GK.Cheat.Loot (corpse) | loot window |
| `Lock{ dc }` | a locked chest with a lock DC, thieves' tools if the member has none | GK.Act.Lockpick | the roll window; unlocked |
| `Trap{ hidden }` | an armed, spotted mine, a disarm toolkit if missing | GK.Cheat.Disarm | the roll window |
| `Down{ who, raise }` | the member at 0 hit points; alone, raised again at once (a party of one at 0 is game over) | - | downed |
| `Box(text)` | the story's message box | GK.Gui.Resolve{box = 'affirmative'} | box |

- `GK.Fixture.Clear()` undoes everything newest first (deletes what was made, goods it generated even
  in the party's packs, takes back spells, boosts, gold, interrupt settings, weapon sets, dialogue
  facts, immortality): `{ undone, failed }`. `GK.Fixture.Made()` lists what is staged: the fixture rows
  not undone. Every staged thing is one `scope_track` row of the event log
  (`GK.Stage.Track{ kind, entity, fixture, op, note }`) whose undo is data in the row (`op`); the
  stage scope's Clean and GK.Fixture.Clear undo it once, whoever is first, and write a `scope_undo`
  row. When the Lua state starts again (a bridge reload, a load) the fixture rows the previous one
  left open are read back from its log files and carried into the new log (`carried` names the
  row they came from), so nothing staged is lost between staging and Clear.
- `GK.Fixture.Ready(fixture)`: whether the fixture's delayed work (treasure, locks) is done;
  `GK.Fixture.ProvokeReady()`: `true`, `false, why` or `nil` (still checking); `GK.Fixture.WhyNoThreat(who)`:
  why a member threatens nobody (no melee weapon, ranged set in hand, sheathed, no threat range,
  reaction spent) or nil.
- `GK.Fixture.Defaults`: every template, faction, dialog, treasure, DC and tool, each with the index
  query that chose it. Dummies are dressed and armed by their own templates (an Equipment set and a
  visual of their own; female where the choice is free): enemy Humans_Female_Guard_Melee (longsword,
  scale mail, shield), caster Goblins_Female_Caster (staff), talker Humans_Female_Civilian and, for
  the counting-house check, Humans_Female_CountingHouseGuard_Melee, trader Dwarves_Female_Civilian.

What starts a fight: an incapacitated enemy (PARALYZED) neither starts a fight by sight nor joins
one through Osi.EnterCombat - nothing happens and nothing is said; nor does an enemy out of sight
(40 m). Armed, able hostiles within sight start it in the same second in Basic_Level_A. What a
reaction needs: the
interrupt set to ask (InterruptPreferences bits Ask = 1, Enabled = 2), the melee weapon set in
hand and drawn, and a threat range - which the test party's starting gear in Basic_Level_A does
not give until the melee weapon is equipped again; the enemy must leave the reach by its own
movement (a fear's flight) - a move ordered by script (CharacterMoveToPosition) provokes nothing.
A dialogue is named to Osiris by its dialog resource id (timelines.dialog_resource), not by the
dialog file's uuid (dialogs.uuid): with the latter the story's checks fail without a word.

What the empty level cannot host: the camp and long rest (no camp region), story state (dialogs
whose roots check act flags fall through), crimes (no owners or witnesses), doors (no walls),
navigation beyond the platform.

## 13. Dialogue phases, the roll, and steps that end early

- `GK.Gui.DialoguePhase()` (UI): `{ phase = "choice" | "continue" | "line", answers, speaker, text,
  instance, canTrade, set }` or nil. GK.Attention records a `dialogue_choice` (with the answers and
  the game's tags) or `dialogue_continue` each time a new one shows; a line being spoken is only a
  waiting `dialogue_line` (`ready = false`). The dialogue step of GK.Gui waits ("waiting for the
  answers to show", `pending = "choice"`) instead of skipping a line while answers are in the list
  but not shown yet, and every skip says `pending = "line"`.
- The roll step skips the dice animation the way a player's click does: in IntroductionAnimation,
  StartRoll or StopRoll it runs `UserTriggeredNextPhaseCommand(true)` - ActiveRoll.xaml's "Skip on
  Click" (UIAccept), which the game answers with its own `*_Skipped` result sounds. The roll and its
  result are the game's; only the animation is cut. `GK.Cheat.Step("roll", { watch = true })` lets it
  play. The continue step answers `result`, `success`, `dc`, `check`, `modifiers`, `boosts`,
  `advantage`, read before the window goes. No game option or
  global switch shortens the animation (GlobalSwitches has `WeightedRolls` - karmic dice, which
  changes outcomes and is left alone - and `ActiveCustomDice`, a skin).
- `GK.Cheat.Auto(name, options)`: the pause after a step (its `wait`) ends at once when the client's
  attention record changes (a prompt closed, a box went), else after `wait`; the state
  counts `early`. The other `wait` hints of the composite steps (Trade, Combine, LongRest) are the
  pause a caller takes before the next step - no step sleeps inside the game. `GK.Gui.Trade`'s
  "not shown yet" window (TRADE_SHOW_MS) stays a clock: a move raises no attention item. The 50 ms
  defers of NewGame and of a click chain run a command after the call has answered (a load would
  cut the answer) and are not waits; the XAML's own TimerTrigger delays are the game's.

## 14. The stage scope: GK.Stage.Begin, Clean, Verify, Track (Server/Stage.lua)

A test stages a scene and leaves it; the next test must find the world as it was. The stage ledger keeps
what was changed, and a baseline finds what was changed behind its back. The ledger is rows of the
event log (section 15): `scope_begin` opens a scope, whose id is that row's seq
(`GK.EventLog.Scope`) and which every row written in it carries as `scope`; every change is a
`scope_track` row, its undo a `scope_undo` row, every Clean a `scope_clean` row and every Verify a
`scope_verify` row - so "what happened in this scope" is `GK.EventLog.Since(0, { scope = id })`,
and `bg3_events` gives the same as one stream. Cheat (the staging); Verify is observation.

| Function (S) | Answers |
|---|---|
| `GK.Stage.Begin(options)` | opens the scope: the world now is the baseline. A baseline of this level already there is kept (`kept = true`) unless `options.fresh`; `options.radius` (default 30 m) is how far around the host objects are watched; `options.families` switches families of the event log on for the scope (off at its Clean). An object a scope staged (a tracked spawn or item, not a fixture's) that no Clean removed - the scope replaced by this Begin, or an earlier Lua state's, read from the previous state's rows, which repeat what that state had itself inherited (`scope_strays`) - is never part of a baseline: it is left out of this one and tracked in this scope, so Verify names it and Clean removes it (`strays`, also in the `scope_begin` row). `{ kept, level, counts = { characters, items, surfaces }, party, near, scope, strays?, ms }` |
| `GK.Stage.Clean()` | takes everything back to the baseline. `{ did, removed = { characters, items, surfaces }, notUndone, sideEffects, ms }`; a non-empty `notUndone` means the caller reloads the level; the families the scope switched on go off |
| `GK.Stage.Verify()` | `{ clean, differences, pending, kept, busy, ms }` against the baseline; `pending`: only what is a moment's matter (deletions the game has not done yet, boosts undone that have not left, the game's own work under way - below) - ask again in a moment. A boost added through Osiris to a party member or a staged creature is named with its text and cause, and whether the ledger tracks it ("tracked and still in force", "undone and not gone yet") or not ("added outside the ledger: not tracked, and Clean does not take it off"). Of a party member each action resource that differs is named with its amount and the baseline's, and so are cooldowns, the drawn or sheathed weapon, concentration and the fight it is in. `kept`: the components the game made and keeps, at their default - not dirt, each with its reason; `busy`: the passing components each member holds |
| `GK.Stage.GameKeeps()` | the data of `Server/GameKeeps.json` (schema `bridge/game-keeps`): `kept` and `passing` (below) |
| `GK.Stage.RestoreMember(id, was, now)` | what Clean does to a party member's cooldowns, action resources and weapon (below); the lines it did |
| `GK.Stage.NoteBorn(id)`, `GK.Stage.Unborn(id, fn)` | a character created by `Osi.CreateAt` and asked to be deleted (`Osi.RequestDeleteTemporary`) in the same server tick closes the game a moment later, with no error and no dump (reproduced with raw Osiris calls in one chunk; a tick later the same deletion is fine). `Stage.Spawn` and the fixtures note the tick an object is created in; `Unborn` answers true and runs `fn(id)` on a later tick when `id` was created in this one. `Stage.Remove`, Clean and `GK.Fixture.Clear` delete through it, so a scene staged and cleaned in one chunk is safe; a test's own raw `CreateAt` and delete in one chunk is not |
| `GK.Stage.StoryBoosts(id)` | `{ { text, cause } }`: the boosts of an object added through Osiris (`Osi.AddBoosts`; the boost's cause type is Story), `text` as `Initiative(3)` |
| `GK.Stage.Census()` | `{ level, characters, items, party }`: what is in the level besides the party (the host's test of a simple level) |
| `GK.Stage.Track{ kind, entity, undo, note, fixture, op }` | the ledger's public hook, for staging outside Stage.lua (GK.Fixture, GK.Rolls, a test's own): `kind` is `"spawn"`, `"status"`, `"item"`, `"surface"` or `"other"`; Clean calls `undo` under pcall, newest first (it returns true, or false and why - listed in `notUndone`); a spawn or item without `undo` is removed; `fixture` and `op` are a fixture's name and its undo as data (`op = { op = "boost", who, text, cause }` takes a boost off: Verify knows such a boost as the ledger's); Verify counts a tracked spawn or item still there. Returns the `scope_track` row |
| `GK.Stage.Open(filter)` | the track rows not undone, oldest first: all, `"scope"` (this scope's) or `"fixture"` |
| `GK.Stage.UndoTrack(row)` | undoes one track row once: `ok, why` |
| `GK.Stage.OnClean(fn, what)` | `Track{ kind = "other", undo = fn, note = what }` |
| `GK.Stage.Flag(flag, on, who)` | sets (or with `on == false` clears) a story flag, global when `who` is nil; Clean puts the old value back |
| `GK.Stage.Fact(db, ...)` | adds an Osiris fact; Clean deletes it again (one that was there before is left) |
| `GK.Stage.ExistedBefore(id)` | whether the object was there at the baseline |
| `GK.Stage.Refill{ who, action, bonus, movement, reaction, slots }` | gives a creature (default: the host) its turn's resources back inside a turn: each named `true` (to its maximum) or a number (that amount; `slots`: every level of SpellSlot); none named: action, bonus, movement and reaction. Each write is a `scope_track` row with the earlier amount as data (`op = "resource"`), the first of a key in a scope winning, so Clean puts back what was there before the scope - through a bridge reload too. Answers once the server reads the amounts back, with a `stage_refill` row: `{ ok, who, changed = { { name, level?, from, to } }, ticks }`. Cheat |
| `GK.Stage.WriteResource(entity, guid, index, amount, max)` | the one writer of action resources: the stage's restore, Refill and the party's inspiration go through it |
| `GK.Stage.ResourceId(name)` | an action resource definition's guid by its name in the game's static data (`ActionPoint`, `Movement`, `InspirationPoint` ...) |
| `GK.Stage.PreviewCasts()` | the server's preview casts (`IsPreview` in a spell cast state's options) by caster: `{ uuid = { spell ... } }`. Verify names a party member's as dirt: no aim is open between tests |
| `GK.Stage.Spawn{ ..., rotation }`, `GK.Stage.Yaw(who)` | `rotation`: the facing, the yaw in degrees or `{ pitch, yaw, roll }`, set with `Osi.ToTransform`; `Yaw` reads the facing back from the transform's quaternion |

What Clean does, in order: a dialogue a party member is in is asked to stop; the party's fights are
ended; the ledger is undone newest first (every GK.Stage verb that changes something that existed
registers itself: Spawn, Status, Unstatus, Teach, Damage, Heal, Kill, Resurrect, Immortal, Transform,
Surface, Lock, ItemAction, UseItem, Visit, Bring, OnStage, TurnBased, Guard, Faction; LongRest,
Camp and Level register as not undoable); objects that were not there at the baseline are removed
(characters deleted as temporary, items deleted - summons, loot, thrown things, given items); the
party is put back (life, statuses, passives, equipment and then spells - compared as a multiset by
prototype and source, the equipment's own left to it - tags, faction, immortality,
cooldowns, action resources, the weapon, place, hit points; a character that joined is taken
out); objects within the radius that moved, changed hands, died or lost hit points are put back;
new surfaces are cleared around the party and the spots the stage used; the game speed and the
party guard are set back. It reads the attention view of the event log since the baseline - or
the last clean - and returns it as `sideEffects`; Verify lists anything waiting for the player that
was not waiting at the baseline.

**After an action: what the game gives back and what Clean does.** Read in Basic_Level_A with a
fair melee attack in and out of a fight (`test:_selftest/clean/attack_in_fight`); bg3.wiki,
"Resources": a resource that recharges each turn, used outside combat, recharges almost at once.

| What | The game by itself | Clean |
|---|---|---|
| action resources whose recharge (`ActionResource.ReplenishType` of the game's data) is `Default` (each turn) or `Combat` | fills them when a fight ends (the tick it ends) and when a turn starts; out of combat a spent one is filled a round later, while the member holds `code:esv::action_resource::CooldownTrackingComponent` | leaves them to the game while a refill is coming (in a fight, or that component held) and says so; otherwise writes the baseline's amount |
| every other action resource (`Rest`, `ShortRest`, `FullRest`, `ExhaustedRest`, `Never`) | nothing until a rest | writes the baseline's amount (`ActionResources`, replicated) |
| cooldowns not in the baseline | at their own time | `Osi.RestoreParty`, then resources and hit points set back to the baseline |
| the drawn weapon | draws it when a fight starts, puts it away when the fight ends; out of combat an attack draws it and the game puts it away when the attack's animation has concluded | a weapon drawn now while a fight or an `esv::unsheath::*` passing component is under way is left to the game, which puts it away when that ends: Clean waits for the game's own event of it, `code:esv::unsheath::StateChangedEventOneFrameComponent` to `Sheathed` (the `event:weapon_state` row), Verify meanwhile `pending`, and draws it again (`Osi.SetWeaponUnsheathed`) a tick after it when it was drawn at the baseline; after 900 server ticks without the event Verify says it never came and does not count it `pending`. Otherwise `Osi.SetWeaponUnsheathed` at once. A baseline taken while the game is at work holds where it is going: no weapon state, and a turn resource it is filling at its maximum |
| experience | a kill gives it; enough of it adds `code:eoc::exp::CanLevelUpComponent` | writes `Experience` (total and current) and `AvailableLevel` back and takes the level-up off when the baseline had none |
| concentration | ends with the status of its spell | the status is taken off with the other statuses |
| the fight | ends on `Osi.EndCombat` while no hostile is in sight | ends every party member's fight first |
| relations | an attack on a neutral creature adds entries of value 0 (hostile) to a table of the server's relation component, and every creature of that faction staged later is the party's enemy; the fight's end does not take them away | the tables of plain values are in the baseline; Clean deletes what was added and writes back what changed (`GK.Stage.RestoreRelations`), Verify names a table that differs. The game's names of these tables are not known (`field_40` ...) |

**Kept and passing components** (`Server/GameKeeps.json`, the reason with each entry). A component
a party member gained since the baseline is not dirt when it is one of these:
- **kept** - the engine creates it at a creature's first use of a system, never removes it, and
  it holds its default: established by seeing it created by the game's own system (not by a
  GateKit verb), kept through later uses, and holding the same value on two unlike creatures. Each
  Verify reads it again (`empty`: a list field that must be empty; `default`: field values); at
  its default it is listed under `kept` ("kept by the game: ..."), holding anything else it is a
  difference that says what. `code:esv::boost::StoryRequestsComponent` (first `Osi.AddBoosts`),
  `code:esv::unsheath::DefaultComponent` (first time out of a fight) and `code:esv::death::KillerComponent`
  (first kill; empty once the killed are gone) are such.
- **passing** - the engine removes it by itself a moment later; every `*OneFrameComponent`, and
  those listed (an animation, a sheathing, a cast, a resource's recharge under way). Verify names
  it as the game still at work and counts it `pending`, so the ladder asks again; it is dirt only
  if it is still there when the ladder stops asking.

**What Clean cannot undo** (it says so, and the ladder reloads the level): an object of the
baseline that was destroyed or deleted; a status of a party member that ended (its remaining time is
not known); a character created as not temporary (taken off stage, not deleted); a long rest, the
camp, a level change; story flags and Osiris facts set by raw Osiris calls instead of
`GK.Stage.Flag`/`Fact`; a boost added by a raw `Osi.AddBoosts` (Verify names it, so a test that
adds one by hand cannot pass clean; GK.Rolls.Pin and GK.Fixture add theirs through the ledger); quest and journal state, dialogue memory, exploration; the game-over box (a load
is the way out).

**A bridge reload inside a scope.** What the ledger holds as data outlives the Lua state: the fixtures'
rows and every row with an `op` (Refill's resource writes, GK.Rolls' undos) are carried into the new
state's log, and the spawns and items it tracked are carried as strays. A scope the earlier state
left open (begun and not cleaned) is otherwise lost - its baseline and the undos that were functions
- and the new state writes a `scope_lost` row with what could not be carried; `GK.Stage.Verify`
answers not clean, naming it, and a `Begin` that is not `fresh` takes no baseline (answering
`kept = true, lost`), so the ladder climbs to a level load, whose fresh `Begin` ends it.

The client's half is the host's (`GameResetActions` in `src/GateKit.Testing/ResetLadder.cs`):
`GK.Gui.CancelAim`, `GK.Cheat.Recover` for a window that holds the game, `GK.Gui.Close` for widgets
opened since the baseline, the capture label (`GK.Seen.Mark`) put back, the client's attention record
read. Globals it keeps in the client state: `GKStageWindows`, `GKStageLabel`, `GKStageAttention`.

### What survives a level load

A level load (`reset`, the reset rung `venue`) is meant to be near-absolutely clean: the game rebuilds the
level from its save. What lives outside the level does not come from the save.
`test:_selftest/stage_level_survivors` changes each value, loads the checkpoint, reads it again
and prints the table; a survivor found later is a row of that test and of this table.

| What | Where it lives | Survives? | How the ladder handles it |
|---|---|---|---|
| party hit points, items in the bag, statuses, passives, place, action resources | the level's save | no (measured) | nothing to do |
| Lua globals and every GK table (the stage baseline, the ledger, watches) on both sides | the Lua states, reset by the load | no (measured) | the host carries what it needs (below) and opens a new baseline |
| engine global switches (`Ext.Utils.GetGlobalSwitches()`, e.g. AiEnableSwarm) | the engine, outside the level | **yes** (measured) | recorded in the baseline (`GK.Stage.OutOfLevel`), put back by Clean, compared by Verify, carried by the host across a load and set again (`GK.Stage.RestoreOutOfLevel`) |
| game speed (DeltaTimeScale) | an engine switch | the engine keeps it; GK.Speed sets it back to 1 when its file loads (measured) | as the switches, by GK.Speed's own state (enabled, engine) |
| frame cap (`GK.FrameCap`) | the client renderer | no (measured) | recorded on the client at Begin (`GKStageFrameCap`), put back by the ladder's clean and after a load - not judged by its verify, because a deck (of any checkout, which this one may not see) sets every copy's cap itself |
| story flags, Osiris databases, time of day | the save | supposed no (not measured: the flag and database tried did not exist in the level) | `GK.Stage.Flag`/`Fact` undo theirs at Clean |
| tutorials marked as seen (`GK.Stage.Quiet`), achievements | the player profile | supposed yes | harmless to tests; not restored |
| graphics, sound and input settings, mod settings, the extender's own state and hooks | the profile, the process | supposed yes | not touched by tests; only a restart resets them |
| an armed capture (`GK.Seen`) | files in the bridge's channel | yes by design (re-armed at bootstrap) | the capture label is put back by the clean |
| a deck's frame-rate floor | the deck, outside the game | yes (it is not the game's) | set again by the deck; Verify may then see the frame cap differ |

## 15. The event log: GK.EventLog (Shared/EventLog.lua, both sides)

One append-only record per side of everything the bridge notices; the other streams are views of
it and keep no copy. A row is `{ seq, tick, t, kind, knows, key?, attention?, scope?, payload? }`
(`schemas/events/row.schema.json`); the registry, the families, the bounds, the files and how the
host reads them as one stream: bridge-protocol.md, "The event log". The reading functions, `On`,
`Off`, `GK.Note`, `GK.Step` and `GK.StepEnded` are observations; `Append` is a cheat (it can write a
row of any kind, the record every observation reads).

| Function | Answers |
|---|---|
| `GK.EventLog.Append(kind, payload, options)` | the new row, or nil when its family is off; `kind` must be in the registry (an error otherwise); `options.key` names the waiting state of an attention row, `options.knows = "omniscient"` marks a row of a `knowsPerRow` kind the party did not perceive, `options.class` is the class of the verb that made a row of a `classPerRow` kind (`job_done`: its job's verb) |
| `GK.EventLog.Since(seq, filter)` | `{ boot, seq, first, rows, dropped? }`: the rows after `seq` that are kept; `dropped` counts the asked-for rows that went; `filter = { kinds = { name = true }, family, scope, attention = true, knows, limit }` |
| `GK.EventLog.On(families, options)` / `Off(families)` | switch families on (`options.seconds`, default 60, at most 600; `options.components` for `component`) or off; the families on now |
| `GK.EventLog.SwitchedOn()` | the families on now |
| `GK.EventLog.Row(seq)` | one row while it is kept |
| `GK.EventLog.Previous()` | the rows of this side's previous Lua state, read back from its files |
| `GK.EventLog.Costs()` | `{ rows, writes, ms, max, perWriteMs, seq, boot, first, bytes, families = { name = { rows, bytes, dropped } }, on }` |
| `GK.EventLog.Families`, `GK.EventLog.Kinds` | the registry as read at start (Shared/EventKinds.json), whose text is copied to the channel for the host |
| `GK.Note(kind, data)` | a row of kind `note`, payload `{ note = kind, data }` |
| `GK.Step(name, class)` / `GK.StepEnded(name, class, outcome, ms, error)` | the rows a test's step writes as it begins and ends (kinds `step`, `step_ended`); their seq |
| `GK.Events(since)` | the journal view: `{ last, events = { { n, t, kind, data } }, dropped? }` - an `osiris` row under its event's name, a `note` under its own, any other row under its kind |
| `GK.Watch(name, arity)` | every firing of the Osiris event becomes a row of kind `osiris`, payload `{ event, args }`, while the `osiris` family is on; once per name |

Attention classes: `begin` opens the waiting state `key` and is news, `hold` opens it quietly,
`end` closes it and is news, `release` closes it quietly, `news` is told and waits for nothing;
every `begin` and `hold` names the kind that ends it. Server/EventFamilies.lua writes the detailed
families: `status` from Osiris StatusApplied / StatusRemoved, `entity`, `surface` and `component`
from the engine's component creation and destruction (`Ext.Entity.OnCreateDeferred` /
`OnDestroy`, subscribed only while on).

## 16. `GK.Memory`: the memory instrument (Shared/Memory.lua)

The decompiled code and the running game's memory are one thing from two sides: `module+0xRVA` is a
place in the live process. `GK.Memory` reads a component's field by address, watches which instruction
writes or reads it, and holds a value fixed. It is built on the fork's `Ext.Debug` additions (below),
needs GateKit's Script Extender in developer mode, and exists on both sides: each side owns its own
watches and freezes, and the four watch slots are shared by the process. Every verb is a **cheat** or
an **observation**, never fair (section 19; `GK.Memory.Class` carries them), and the cheats
(`Write`, `Freeze`) are recorded in `GK.Cheat.Log`. The server's watches and freezes are removed on
`GK.Stage.Clean` (registered through `GK.Stage.OnClean`), the client's by the reset ladder's clean of
the client (`GK.Memory.Clear`). The extender serves the watches and rewrites the frozen values every
tick of their side by itself; the only Lua on the tick is the look of a followed watch (below), from
the attention pollers, while one exists. Watches and freezes outlive a Lua
reset in the process; those of an earlier Lua state of a side are cleared as that side's file loads.

| Verb | Class | What it does |
|---|---|---|
| `GK.Memory.Available()` | observation | whether the fork's instrument is present (developer mode, the memory build) |
| `GK.Memory.Field(entity, component, field)` | observation | `{ Address, Offset, ComponentAddress, Type, Size, Scalar }` for a field, from the fork's type information. `entity` is a uuid, an entity or a handle; `component` is the **engine** name (`eoc::HealthComponent`), as `Ext.Debug.GetComponentAddress` takes it; `field` is the Lua field name (`Hp`) |
| `GK.Memory.System(name)` | observation | the address of a system's own object (`esv::roll::stream::SaveSystem`), or nil: what a system keeps outside the components is reached from here with `Read` |
| `GK.Memory.Path(object, path)` | observation | where a value below an object's top level lies and how wide it is: `object` is any reference to an object of the game Lua holds (a component, a system's manager, a struct inside one), `path` the way down as Lua writes it (`"Streams[2].State.NumRolls"`, indexes from 1). `{ Address, Size, Offset, ObjectAddress, Path }`, the shape `Watch`, `Read` and `Freeze` take, or nil and why. Needs the Script Extender build with `Ext.Debug.GetPropertyAddress`; an array kind that cannot say where its elements lie and a property that is no plain field answer nil or size 0 |
| `GK.Memory.Element(bufferAt, index, elementSize[, offset])` | observation | the address inside element `index` (from 0) of an array whose buffer pointer is at `bufferAt` (an address or a field table): `Field` stops at an array |
| `GK.Memory.Moved()` | observation | the followed watches whose place moved since they were armed, by the slot each had: `{ [slot] = { was, now, did = "rearmed" \| "closed", armed?, why?, entity, component, field? } }` |
| `GK.Memory.Read(address, type)` | observation | the scalar at an address read as `type` (a field table may be passed for its own address and type) |
| `GK.Memory.At(address)` | observation | `{ Module, RVA, Base, Size }`: which loaded module holds the address |
| `GK.Memory.FromModule(module, rva)` | observation | the absolute address of `module+rva` |
| `GK.Memory.Write(address, type, value)` | cheat | write a scalar, refused on a code page; corrupts state if the field is wrong |
| `GK.Memory.Watch(address, size[, readWrite[, owner]])` | observation | arm a hardware watchpoint on writes, or with `readWrite` on reads too (size 1/2/4/8, the address aligned to its size); a field table may stand for `(address, size)`. `owner = { entity, component }` gives a raw address the component it lies in (refused when it is not inside it); such a watch and a field's follow their component (below). Returns a slot 1-4 or nil when all four of the process are taken. Arming stops every thread of the game for a moment (tens of ms) |
| `GK.Memory.Watches([all])` | observation | the watches armed now, this side's or with `all` both sides': `{ { Slot, Owner, Address, Size, ReadWrite, Value, Follows \| Raw } }`; `Value` is what the next hit's `Before` will be; a watch of this side carries `Follows = { entity, component, field?, offset? }`, or `Raw = true` for an address with no owner, which is not followed |
| `GK.Memory.Waiting()` | observation | how many hits of this side wait to be read |
| `GK.Memory.Hits([max])` | observation | this side's hits since the last call (**drains** them; over `max`, the rest wait for the next call). Each: `{ Slot, Owner, Thread, Before, After, Changed, Frame, Flush, Poll, Time, NextInstruction = {Module, RVA, Address}, Instruction (the same), Return = { ... }, Of?, Moved?, Now? }`: `Of` names what a followed watch's hit is of, `Moved` (with `Now`, and no `Of`) a hit that may have come after its component moved. `Before` is the value before the access, `Changed` is false for a read; `Frame` and `Flush` are the side's ECS frame and command-buffer flush, the entity tracer's counters (a write stamped flush N shows in the trace in flush N + 1); `Poll` counts the extender's ticks. The host names `NextInstruction` with `gatekit decomp at <Module>+0x<RVA>` |
| `GK.Memory.ClearWatch([slot])` | observation | clear one watchpoint of this side or all of them (all: the hits not read yet are dropped too); the other side's are never touched |
| `GK.Memory.Freeze(address, type, value)` | cheat | hold an address at a value, rewritten by the extender every tick of this side; a slot 1-16 |
| `GK.Memory.Unfreeze([slot])` | cheat | stop holding one address of this side, or every one |
| `GK.Memory.Frozen([all])` | observation | the addresses held now, this side's or both: `{ { Slot, Owner, Address, Size } }` |
| `GK.Memory.Clear()` | cheat | every watch and freeze of this side gone; answers `{ watches, freezes }`, the counts there were |

What a watch can and cannot say:

- **A field with no single size** (a vector, a transform) is refused with the reason; watch one of
  its parts, `Watch(field.Address + n, 4)`. `Field` reaches top-level fields of a component only.
- **A watch follows its component when it knows it**: one armed on a field table, or on an address
  with its `owner`. When the entity's set of components changes, its storage moves and a hardware
  watch left in place sits on memory that is another's. Such a watch is looked at once a tick of its
  side while it exists, and by `Hits`, `Watches`, `Waiting` and `Moved` (one rule, in one place):
  moved, the hits of its slot so far are held; one stamped with a flush (`Flush`) no later than the
  last look that found its place unmoved stays the field's, any other carries `Moved = true` and
  `Now` and is never a hit of the field (while a trace runs no flush is read, and every such hit is
  `Moved`). The watch is armed again at the new place, or closed when its component is gone or no
  slot is free - a `memory_watch` row either way (family `memory`: `did`, `slot`, `armed`, `was`,
  `now`, `why`, `entity`, `component`, `field` or `offset`, `watch`, `heldHits`). A raw address
  with no owner is never followed and says so in `Watches` (`Raw`).
- **The instruction named is the one after the access** (`NextInstruction`): the access is the
  instruction before it.

The fork side, all under `Ext.Debug` (developer mode only): `GetFieldAddress`, `ModuleFromAddress`,
`AddressFromModule`, `ReadValue`, `WriteValue`, `WatchWrite`, `ListWatches`, `ReadWatchHits`,
`ClearWatch`, `Freeze`, `ClearFreezes`, `ListFreezes`. Reads and the watchpoint are bounds-checked
against committed memory (`VirtualQuery`); a write is refused on a code page.

The host names an address with `gatekit decomp at <module+0xRVA>` (the `decomp` group), which gives
the function's name from the decompiler database and the symbol database, for an address from
`GK.Memory` or any external tool (a debugger, Cheat Engine). Marked observation.

### 16.1 `GK.Trace`: the entity tracer as rows of the event log (Shared/Trace.lua, both sides)

Each side has a tracer of its own (GateKit's Script Extender, developer mode). Between `Begin` and
`End` it records what changed in the entity system, per entity and component - created, destroyed,
replicated, modified - with the ECS frame and command-buffer flush of each change, and with
`systems` the system whose update changed it. `Step(name)` cuts the record: what was recorded since
the last cut becomes rows of the `trace` family carrying the step that ends, and the tracer starts
again empty. A change carries the step under way when the engine applies it - at the flush after
the Lua that caused it - so `Step(name)` names what follows it, and an action and the `Step` or
`End` that closes its step go in separate chunks (in one chunk the action's changes fall into the
next step, or after `End` into none). Every verb is an observation: the tracer changes nothing in
the world.

| Verb | What it does |
|---|---|
| `GK.Trace.Available()` | whether the fork's tracer with filters is present |
| `GK.Trace.Begin{ entities, components, systems, modifications, limit, step }` | `entities` (uuids or entities; none: all) and `components` (engine names or Lua labels; none: all) filter at the source; `systems` (needs both filters) names the changing system and costs milliseconds a tick per component watched; `modifications` records writes (the engine marks a modified component once a flush); `limit` events kept per entity and component (default 16, at most 4096); `step` the first step's name; `quiet` writes only each step's `trace_step` row to the log (the changes are in the answers). Switches the `trace` family on (at most 600 s of a running game, or the stage scope). `{ ok, side, frame, flush }` |
| `GK.Trace.Step(name)` | ends the step under way and begins `name`: `{ ok, step, entities, changes, written, systems, systemsWritten, frame, flush, rows }` |
| `GK.Trace.End()` | the last step's rows, as `Step`, and the tracer off |
| `GK.Trace.Status()` | `{ on, step, last, options }`: `step` the step under way (nil when no trace runs), `last` the step that ended last, `options` Begin's while the trace runs |

Rows (`Shared/EventKinds.json`): `trace_begin`; `trace_change` (one per entity and component of the
step: `component`, `flags` and per event `flags`, `frame`, `flush`, `order` - `order` is the change's
place among everything logged in that flush; `entityFlags`); `trace_system` (`component`, `system`,
`thread`, `frame`, `flush`); `trace_step` (the counts, and how many of them became rows: at most 500
changes and 500 system rows a step); `trace_end`. A change made outside every system update (an
`Osi` call from Lua) is recorded with no system. The server's clean ends a trace the scene left
running (`GK.Stage.OnClean`), the reset ladder's clean of the client the client's.

## 17. `GK.Rolls`: rolls decided in advance (Server/Rolls.lua)

A test must not wait on luck. A pin is the game's own pair of boosts
`MinimumRollResult(<kind>,n);MaximumRollResult(<kind>,n)` on the roller (added by `Osi.AddBoosts`,
cause `GKPin`): it fixes the natural die of that creature's own rolls of that kind, and nothing
else - the modifiers, the difficulty and the game's verdict stay the game's. Initiative is not a
roll kind; its pin is the boost `Initiative(n)`, and `n` is a bonus added to the initiative roll,
not the die (+-100 decides the order). Every pin is a track row of the stage ledger whose undo is
data (`op = { op = "boost", who, text, cause }`, GK.Fixture's `boost` undo), so it outlives a reload of
the bridge, `GK.Stage.Clean` and `GK.Fixture.Clear` take it off, and a pin left from a scope that a
fresh `Begin` replaced stays a fixture row until `GK.Fixture.Clear`. `GK.Rolls.Class` names each verb's class.

| Verb | Class | What it does |
|---|---|---|
| `GK.Rolls.Pin{ who, kind, n, once, karmic }` | cheat | `who`: any creature with a boosts container (default the host); `kind`: one of `Kinds()` (an unknown one is an error that lists them); `n`: the die, 1..20 (a damage kind: every die of the roll, at least 1; Initiative: any whole number). A pin of the same who and kind is replaced; one of the same `n` made in this scope is kept as it is (`kept` in its `roll_pin` row: taking a boost off removes every boost of its text and cause, the new one too) - its boost added again when it is not on and this Lua state never asked for it; a pin row of an earlier scope or a previous Lua state (GK.Fixture carries those across a restart of the game, where a fresh level has no such boost) is undone and the pin made anew, so that this scope's Clean takes it off; a new boost whose text is still leaving is added once it has left. A pin of any kind but Initiative switches Karmic Dice off for the scope (below), unless `karmic = true`. Refused outside a stage scope ("call GK.Stage.Begin() first"). Writes a `roll_pin` row. The answer is observed: `{ ok, who, kind, n, once, boost, track, inForce = true, karmic, ticks }` once the boost is on the creature and the roll manager no longer weights (`karmic = "switched off for the scope"` when this pin switched it), else `ok = false` after 90 server ticks (about 3 s of a running game; the limit of every observed answer of `GK.Rolls` is counted in ticks, so that a stall of the game is no failure, with 30 s of real time as a guard) |
| `GK.Rolls.Unpin{ who, kind }` | cheat | takes the pins of `who` (default the host) of `kind` (default every kind) off; observed until the boosts have left: `{ ok, removed, ticks }` (`removed = 0`: there was none) |
| `GK.Rolls.Pins()` | observation | what is pinned now, read from the boosts of every creature pinned in this Lua state or named by an open ledger row: `{ { who, kind, n, once, boost, track, tracked } }`; `tracked = false`: the row is undone and the boost has not left yet |
| `GK.Rolls.Kinds()` | observation | every value of the game's `Ext.Enums.StatsRollType` except `None` and its end marker `Sentinel`, sorted, then `Initiative` |
| `GK.Rolls.Karmic()` | observation | `{ on, option, mode, saved }`: Karmic Dice (below) |
| `GK.Rolls.SetKarmic(on)` | cheat | writes the game's option for the length of the scope (Clean writes the old value back, from data in the ledger row, `op = "karmic"`, so a bridge reload keeps the undo; refused outside a scope); observed until the roll manager follows: `{ ok, on, was, mode, ticks }`. `WriteKarmic`, `WriteStreams`, `WriteInspiration` are the undos themselves: they write and track nothing |
| `GK.Rolls.Finished(who, take)` | observation | the oldest finished roll of `who` that no RollResult event has taken yet (GK.GameLog joins it to its `game_roll` row); the event comes the next tick, and a finished roll older than 5 ticks is dropped, so that it is never joined to a later roll. Its `advantage` (advantage, disadvantage, both), `discarded` (the other d20: the game draws two for every roll), `total`, `critical` and `modifiers` come from the roll's request - alive for a passive roll, read in its destruction for an active one, whose request is gone before its finished event |
| `GK.Rolls.Streams()` | observation | the server's roll streams, read from the roll manager: `{ base, frame, mode, streams = { { type, seed, rolls, frame, failDebt, successDebt, player, combat, cast } }, saved }` - `base` its base seed, each stream's `type` one of `Ext.Enums.RollStreamType` (Generic, Dialog, PlayerSpell, ...), `rolls` the dice drawn from it (two for every d20 roll); `saved` the same read from the roll streams component. A load sets `base` to the clock in seconds and makes the streams anew |
| `GK.Rolls.SetStreams(list)` | cheat | writes `seed` and `rolls` of the streams `list` names (`{ { type, player, combat, seed, rolls } }`, the shape of `Streams().streams`, or a whole `Streams()` answer - so an earlier reading rewinds them) in the roll streams component, which the game draws from (a write to the roll manager is not honoured); a stream's dice are a function of its seed and count, so the same rolls come again. Clean writes the old values back (data in the ledger row, `op = "streams"`), or reports the stream gone. Refused outside a scope. `{ ok, written = { { type, player, combat, seed, rolls, was } }, missing }` (`missing`: named streams that do not exist, such as a Dialog stream before the first dialogue check) |
| `GK.RollLog.Available()` | observation | whether this Script Extender build throws the roll events the roll log is written from (GateKit's build with them, developer mode). The family `dice` (`GK.EventLog.On("dice")`) then writes a `dice_thrown` row for every roll the game's roll manager throws - a check, an attack, a save, damage, a heal, initiative, a die of a stats expression, by any creature: `{ who, kind, stream, streamId, dieType, count, bonus, dice, natural, discarded, total, critical, dc, advantage, forced, origin, what, caller, cause, causeId, request }`. `origin` is the registry id of the code that asked and `what` the roll it asks for; `cause` says why the roll was asked - `status applied`, `status event`, `spell`, `request` (a roll the roll system makes from a request: `request = { context, fromScript, subject, dialog }` says which), `dialogue throw`, `damage at a position`, `initiative` - from the frame of the system that decided a roll was needed, `causeId` its registry id; an address no named asker holds gives `origin = "unknown"` with `caller`, and the row is still written. A roll asked through one of the roll manager's own entry functions is named by the frame above it. `forced` marks an outcome Karmic Dice decided before the dice. On a build without the events, switching the family on is refused and says why |
| `GK.RollLog.Origin(module, rva)` | observation | the named asker an address of an executable belongs to, `{ id, what, entry }`, or nil |
| `GK.RollLog.Forget(id)` | observation | leaves one named asker or cause out of the log's tables until the family `dice` is turned off (the scope's end does it), so that its rolls are written as rolls asked from a place nobody has named: `origin = "unknown"` (or `cause = "unknown"`) with the address in `caller`. Nothing in the game changes. Without an id nothing is left out. `{ ok, forgotten }` |
| `GK.RollLog.Origins()` | observation | the askers known for each executable: `{ [module] = { { id, what, rva, size } } }`; the ids and words are in `Server/RollOrigins.json`, the places are written into it from the registry's build tables by `gatekit registry places`, for every executable the registry places them in |
| `GK.Rolls.Next(spec)` | observation | the dice the stream `spec` names (`type`, and `player`, `combat` when several share the type) gives to its next roll, computed from its seed and count with the game's generator and not drawn: `{ ok, type, player, combat, seed, rolls, dice }`. `dice` (default 2: the pair the game draws for every d20 roll, the first kept unless advantage or disadvantage chooses) and `faces` (default 20) describe the roll; `rolls` computes from another count. A roll that Karmic Dice forces, a die a reroll boost throws again and a die replaced by a guaranteed-outcome boost are not these |
| `GK.Rolls.SetNext(spec)` | cheat | chooses the natural die of the next roll drawn from the stream `spec` names: moves its count forward to the nearest one at which the first die is `die` and, with `both`, every die of the roll (so that advantage and disadvantage keep it too); written through `SetStreams`, so Clean puts the count back. One roll is chosen - the roll after it draws what the stream gives where it is left; other streams and the bounds a boost puts on a die are untouched. `{ ok, type, player, combat, seed, rolls, was, dice }`, or `{ ok = false, error }` when the stream does not exist yet or no count near gives the die. Refused outside a scope |
| `GK.Rolls.Rig(spec)` | cheat | decides one throw at the moment it is made, through the extender's roll event, for the length of the scope. `spec` names the roll by what the game says of it - `who` (the roller), `kind` (the roll's type as the game names it, or `Attack` / `Damage` for every type that ends so), `stream` (the roll stream's type); a field left out matches any roll - and which of the rolls that fit: `nth` (default 1, the next) and `times` (how many from there, default 1; 0: every one until `Unrig` or Clean). The throw gets `natural` (the die that counts: the total follows, and the critical by the game's rule) or `total`. Every other roll is as the game throws it: of four checks asked in one tick with the third rigged, three are the stream's own. One rig decides a roll; the earliest set wins. A `roll_rigged` row (scope, cheat, omniscient) says what the game had thrown. `{ ok, id }`, or `{ ok = false, error }` on a build without the roll event. Refused outside a scope |
| `GK.Rolls.Unrig(id)` | cheat | takes a rig off, every rig without an id: `{ ok, removed }` |
| `GK.Rolls.Rigs()` | observation | the rigs in force: `{ { id, who, kind, stream, nth, times, natural, total, seen, decided } }` |
| `GK.Rolls.CanRig()` | observation | whether this Script Extender build raises the roll event (`Ext.Events.Roll`) |
| `GK.Rolls.Inspiration()` | observation | the party's inspiration points, the `InspirationPoint` action resource (its definition looked up by name, `GK.Stage.ResourceId`): `{ points, max, holders = { { uuid?, entity, party, points } } }` - every entity that keeps the resource with its amount; `max` is the definition's `MaxValue` - the resource's own `MaxAmount` grows as points are given and is not the cap. The pool is the party's: one entity of the party's own (`PartyComposition`, `PartyView`; no uuid, `party = true`) holds it, not a character, and it holds at most `max` (4): six points given, four held. Who of a party of two spends it in a roll is not read yet |
| `GK.Rolls.SetInspiration(n, { past })` | cheat | writes the points (0..max; above the game's ceiling only with `past = true`) on every entity that keeps the resource, and its `MaxAmount` when lower, for the length of the scope (Clean writes both back, from data in the ledger row, `op = "inspiration"`; refused outside a scope): `{ ok, points, was }`. A party that never had a point has no row of the resource (a fresh level): one point is given through `Osi.GiveInspirationPoints`, which makes the party entity's row; the answer is then observed, `{ ok, points, was = 0, made = true, ticks }`, and Clean writes 0 back (the row stays, as the game keeps it). `Osi.GiveInspirationPoints` only adds (a negative amount does nothing) and is not undone by Clean |

**When a pin binds, and the gap of `once`.** The die of a roll is decided when the roll is made: a
passive roll when it is asked for (the game then waits its passive delay before it finishes), an
active roll when the dice are thrown in its window. The boost system takes a boost about 3 server
ticks after `Osi.AddBoosts` and lets it go about 3-4 ticks after `Osi.RemoveBoosts`; the server
ticks about 30 times a second at any speed. So `Pin` answers only once its boost is on, and
`once = true` - accepted for the kinds the roll system finishes as rolls of their own (checks and
saving throws; an attack's or a damage roll's die is part of a hit and passes through no event the
bridge reads, and Initiative is no roll) - asks for the removal in the same tick as the game's
finished-roll event (`code:esv::roll::RollFinishedEventOneFrameComponent`, one frame) of the holder's
roll of that kind. A roll of that kind by the holder decided before the boost has left is pinned
too: one asked for while the pinned roll was still waiting its passive delay, or within the
`ticks` of its `roll_unpin` row after it finished. The `roll_unpin` row is written when the boost
has left, so a test that asks for its next roll after that row gets a free die.

**Karmic Dice** is the game's option `GlobalSwitches.WeightedRolls` (the profile's gameplay
setting; an engine switch that outlives a level load, so the stage baseline records it and Clean
and Verify cover it). The server's roll manager (`Ext.System.ServerRollSave.RollManager`) follows
it within a tick: `RollModeType` is `WeightedRandom` when on, `Random` when off. The roll streams
component's `RollModeType` (`saved`) does not follow the option and does not tell the mode. Karmic
Dice overrides a pin: on a roll it weights (a dialogue check, by bg3.wiki also attack rolls and
targets' saves; not a check outside a dialogue) it forces a success with a die above the pin once
failures have built its debt: a dialogue check pinned at 1 came out 17. So `Pin` switches it off for the scope; `Pin{ karmic = true }` leaves it, for a test of Karmic
Dice itself. A test that needs plain unpinned dice calls `SetKarmic(false)` in its scope and records
`Karmic()` of before in its report.

**Rows.** `roll_pin` (`who, kind, n, once, boost, track`) and `roll_unpin` (`who, kind, n, why =
once | unpin | replaced, track, asked, rolled, ticks, stuck`) are scope rows, cheat, omniscient; the
undo of Clean is its `scope_undo` row. Every roll the game finishes is a `game_roll` row (GK.GameLog,
from Osiris `RollResult` joined to the finished-roll event a tick before): `who, kind, ability, skill,
dc, advantage, modifiers` (from the roll's metadata while the roll still exists - a passive roll),
`bonus, natural, total, success, critical, active`, `pinned` (the `n` of a pin of that roller and
kind open when the roll finished) and `inspired` (an inspiration point was spent on it). A roll the
party does not perceive (its roller neither a member nor seen by one) is omniscient. The roll
window's rows (Client/Attention.lua): `roll` opens once the window waits for the throw - its roller
is set and its introduction is over (`WaitForStart`, or a later state when the introduction was
skipped; at most 4 s) - with `who, check, skill, pure, context, dc, target, advantage, modifiers,
boosts, bonus, inspiration`; `roll_ended` closes it with the last reading: `dice, natural` (the
window's FinalResult), `total` (the die and the listed bonus, none when the bonus is a range - the
window's own ResultNumber counts up while it animates), `success, critical` (a natural 20 or 1),
`added` (the bonuses added in the window, `{ name, spell, dice, value, by }`, `value` the bonus
die once thrown), `inspiration` (points left), `rerolls` (times thrown again), `state`. The host
keeps the roll fields in the attention it tells (`common/attention`).

**A check in a dialogue.** The dialogue goes on by the verdict of its
window. A failed throw may be thrown again for one inspiration point (`GK.Gui.Resolve{ roll =
'reroll' }`); the pin decides the second throw too, and the server finishes one roll for the whole
window - the last throw, `inspired`. A bonus added in the window (`GK.Gui.RollBonuses`,
`AddRollBonus`: Guidance from a party member who knows it, the roller among them) throws a die of
its own that the d20's pin does not decide; the server's `bonus` is the listed modifiers and that
die. Once given, Guidance stays on the roller as the status GUIDANCE and adds its die to the next
checks by itself, listed as a modifier of value 0.

An attack roll is pinned with kind `Attack`: a fair melee attack of the host pinned at 1 misses
and at 19 hits (`test:_selftest/clean/attack_in_fight`). Not verified in the game through
`GK.Rolls`: a damage roll, Initiative's effect on a turn order, a roll with advantage. An
active roll's `game_roll` row has no `modifiers` (the roll is gone when its RollResult comes); its
window's rows list them.

## 18. `GK.Speed`: the working speed and the game's clock (Server/Speed.lua)

`GK.Speed.Set(scale)` takes any positive scale (the engine's switch has no cap; a direct write is
honoured as written, measured exactly up to 32); from 16 the answer carries `warning`: the game is
known to break there (a downed member being raised fires the party-defeated check from 16, a turn
starts before the previous hit lands from 24). `Set("working")` sets `GK.Speed.Working`, the one
constant for the project's working speed (8). `GK.Speed.Clock()`: `{ ticks, time, delta, real,
scale }` from the last server tick - the game's own tick count, its game time in seconds, the game
seconds of that tick (the scale lengthens a tick; the server ticks about 30 times a second at any
speed), the monotonic milliseconds of it. `GK.Speed.Class` names each verb's class (setting the
scale is a cheat; reading it is observation).

## 19. The class of a verb, and steps (Shared/Verbs.lua, Shared/VerbClasses.json)

Every function under `GK` is **fair** (a player could do it through the game itself: it rolls,
pays its costs and passes the game's own checks), **cheat** (it sets the world, bends a rule, runs
code, or is not shown to do neither) or **observation** (it reads, and changes nothing a player
could notice). The classes are data in one registry, `Shared/VerbClasses.json` (schema
`bridge/verbs`), read by the mod and by the host: `verbs` maps a path under `GK` (`Act.Select`,
`Key`, `Attention.Pollers.film`) to `{ class, why? }`; `namespaces` gives the class of every
function of a table it names that `verbs` does not (`Cheat`: the catalogue made at run time).
`bg3_api` shows each function's `class` and `why`. A unit test holds the registry to the functions
the mod's Lua defines, both ways (a function without a class fails it, and so does a class without
a function), and each namespace to the classes it may hold:

| Namespace | May hold |
|---|---|
| `GK.Act`, `GK.Gui` | fair, observation |
| `GK.Cheat`, `GK.Stage`, `GK.Fixture`, `GK.Speed`, `GK.Memory`, `GK.See` | cheat, observation |
| `GK.Probe`, `GK.Seen`, `GK.Conditions`, `GK.GameLog`, `GK.Heard` | observation |

Where an action has a fair and a scripted form, both carry the same name in their own namespace.
`GK.Cheat.Move`, `Cast`, `Attack`, `Jump`, `Shove`, `Hide`, `Dash`, `Disengage`, `Help`, `Throw`,
`Use`, `UseCarried`, `PickUp`, `Loot`, `Take`, `Talk`, `Trade`, `Equip`, `Unequip`, `Sneak`,
`Disarm`, `Stop`, `Draw`, `Sheathe` (Server/Act.lua) are the scripted actions: server commands through Osiris that
skip the player's path (a raw `Osi.UseSpell` attack hits whatever the armour class and the die,
and spends no action; `GK.Cheat.Cast` and `Attack` take the skipping options off the cast, and
then it rolls and the game spends the action).
`GK.Act.<the same name>` on the server answers `{ ok = false, error, fair, cheat }`: the player's
route (`GK.Gui.UseSlot`, `Aim`, `Confirm`; `GK.Gui.ItemDo`; `GK.Gui.Resolve`) where one exists, and
`GK.Cheat.<name>`; nothing is done. `GK.Cheat.Charge`, `Unstick` and `Say` have no fair twin. The
fair verbs of `GK.Act` are `Select`, `EndTurn`, `TurnBased` and `Lockpick` on the server, and
`Attack`, `Cast`, `Draw`, `Sheathe` and `Press` on the UI side (Client/Act.lua). `GK.Act.Draw()` and
`Sheathe()` press the key the profile binds to the game's input event `ToggleCombatMode` (read from
the input scheme, never assumed) for the controlled character and answer when its replicated
`code:eoc::unsheath::StateComponent` changes (its replication event): `{ ok, did, who, key, from, to,
ticks }`. `GK.Act.Press(event)` presses the key bound to any input event of the game (its
`EventName`): `{ ok, event, key, modifier }`, that the key went in. `GK.Act.Cast(spell, target)` uses
the hotbar slot, aims (`GK.Gui.Aim`, a uuid or a point) and clicks (`GK.Gui.Confirm`), answers what
the game refused as it says it (the targeting then left as a right click does), and otherwise
answers when the client's cast entity of the caster (`SpellCastIsCasting.Cast`, which exists from the
start of the targeting) receives the game's `code:eoc::spell_cast::FinishedEventOneFrameComponent` - the
cast over as the player sees it, after the server resolved the hits: `{ ok, spell, caster, target,
cast, result, outcomes = { { target, outcome, flags } }, shown, ticks }` (`outcomes` from the cast's
`RollsComponent`; `shown` what the cursor showed); `GK.Act.Attack(target, { spell })` is the
main-hand attack. Its component subscriptions are made once at load on the main thread and record
only while a verb waits: a verb's answer is looked at in the UI's deferred context, on the render
thread, and an `Ext.Entity.Unsubscribe` from there once stopped the game (Script Extender's
assertion `global_ != -1`). Every hit the game resolves is a `game_hit` row (Server/Act.lua, from
`esv::hit::HitResultEventOneFrameComponent`): `who, by, spell, outcome` (`miss`, `critical miss`,
`hit`, `critical`, `none` for a hit with no attack roll), `damage, damageType, cause, flags` - a miss
writes no damage and no Osiris event, so this row is where it shows - and `saves`, the saving
throws the hit asked of its target (`HitDesc.ConditionRolls`: `{ ability, dc, natural, total,
critical }`); the hit's flag `SavingThrow` says the target saved; and `attack`, the hit's attack
roll from the same list (`{ kind, ac, natural, total, critical }`, `ac` its Difficulty: the target's
armour class; `natural` the die after a pin bounded it). A spell's save is resolved inside
the hit, not by the roll system: no `esv::roll` finished event names it. Every change of a creature's
weapon state is a `weapon_state` row (`who, from, to`, from
`code:esv::unsheath::StateChangedEventOneFrameComponent`): after a fight it comes in the tick nothing holds
the weapon out any more. On the
client `GK.Cheat.Run`, `Set` (any command or property of a view model), `Step`, `Auto`, `Quiet`,
`Recover` and `AimBest` are the window verbs that reach past a player's controls. Every call of a
`GK.Cheat` function is recorded in `GK.Cheat.Log` (`GK.Cheat.Used()`). A `job_done` row carries the
class of the verb that made the job (`class`, kind `classPerRow`).

| Function | Class | Answers |
|---|---|---|
| `GK.Verbs.Of(path)` | observation | the class of a path (`"Act.Select"` or `"GK.Act.Select"`) and its `why`, or nil |
| `GK.Verbs.List(filter)` | observation | `{ { verb, class, why } }`, those whose path contains `filter` or whose class is it |
| `GK.Verbs.Guard()` | cheat | puts the check on every function the registry names on this side, sets `GK.<Namespace>.Class = { Verb = class }`; how many functions it guards |
| `GK.Verbs.Within(class, fn)` | cheat | runs `fn` with the verbs it calls held to `class`: ok, value, refused |
| `GK.Verbs.Load(text)` | cheat | reads the registry (default: the mod's file) |

**Steps.** A chunk the host sends with a class (the command's `class`, bridge-protocol.md) runs
held to it: a verb called from the chunk itself that the class does not allow fails the chunk -
in a **fair** step a cheat ("a fair step called the cheat GK.Cheat.Attack"), in an
**observation** step anything but an observation ("an observation step called the fair
GK.Gui.Click"); a **cheat** step allows all. What a verb calls inside itself is not judged, nor
what runs later on a tick or a timer, nor raw `Osi` and `Ext`. A refusal the chunk catches with
`pcall` still fails it. Outside a step nothing is held.

A test (src/GateKit.Testing) names its steps:

| Global | What it does |
|---|---|
| `step(name, class, fn)` | runs `fn` as a step of class fair, cheat or observation; every `game.lua`/`server`/`client`/`ui`/`wait` it sends carries the class, and the host's own `game.*` functions are held to it too (`checkpoint`, `reset`, `reload`, `skip_scenes`, `fast_scenes`, `burst` are cheats, `answer` and `key` fair, the rest observation). Answers what `fn` returns. A failure stops the test there as `step <name> (<class>) failed: <why>`. Steps do not nest. Its begin and end are rows `step` and `step_ended` of the game's event log (on the server, else on the client), so any row between them belongs to it |
| `need(condition, why)` | answers `condition` when it holds; otherwise the test ends as **unmet: why** - neither a pass nor a failure of the thing tested (cli.md, `test`) |

A test that calls neither runs with nothing held.
