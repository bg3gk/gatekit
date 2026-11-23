# Measured facts

Facts about the game, Script Extender and the official Toolkit that GateKit is built on, each with
the command that shows it again. `<copy>` is a game copy of your own (`--instance`); a Lua line is
run in that copy with `gatekit --instance <copy> exec <client|server> '<code>'`.

## Starting the game

- `bin/bg3_dx11.exe --skip-launcher -continueGame` starts without Steam's launcher and loads the
  newest save - whatever made it, an autosave of a test game included - with no input at all.
  `gatekit --instance <copy> up` starts the game this way.
- `-testLoadLevel <module>:<level>` starts a fresh test game straight in that level: no menu, no
  save. The player is Astarion alone, at the level's start point, with the story not advanced; the
  game autosaves once on arrival. The client goes `Init`, `StartServer` and on to the level, with no
  `LoadMenu` and no `Menu` (`code:ecl::EocClient::Load` queues them only without `-testLoadLevel`,
  `-testAIStart` or `-load`). `gatekit --instance <copy> up --level GustavX:Basic_Level_A`, then
  `gatekit --instance <copy> timeline --kind State`.
- `-loadSaveGame <folder>` (a folder under `Savegames/Story`) loads that save with no input; it
  still passes through the menu module and the menu's level: the save's load starts from `Menu`.
  The name is read in the system's ANSI code page: a folder name that is not ASCII is not found, and
  the game then sits in its main menu; the name after the save's unique id (`<id>__<name>`) is
  matched too. `gatekit --instance <copy> up --save <folder>`.
- The main menu's level is the last loaded module's `MenuLevelName` (`meta.lsx`); a module that
  names a small level makes `LoadMenu` short. `test:startup/menu/scene` shows the rule.
- Without `-continueGame` the game stays in its main menu, and mods' Lua - the bridge's too -
  already runs there. `gatekit --instance <copy> up --main-menu`, then `gatekit --instance <copy> status`.
- A game that was killed instead of quitting leaves `ModCrashSanityCheck` in its profile folder;
  on the next start that folder switches every mod off for the session, stops at "press any key"
  and makes the game ignore `-continueGame`. `up` deletes it before every start.
- The studio logo is `Video/Splash_Logo_Larian.bk2` in `Gustav_Video.pak`, the game's only
  startup video (Larian's logo, then NVIDIA GameWorks'). GateKit's Script Extender build hides it,
  and the mods' startup videos, with `"StartupVideos": false` in `ScriptExtenderSettings.json`;
  `up` writes that unless `--startup-videos` is given.
- The game's switch `SkipSplashScreen`, set from Lua before the main menu
  (`Ext.Utils.GetGlobalSwitches().SkipSplashScreen = true` at the client's `LoadModule`), closes the
  "press any key" page as it opens. `gatekit --instance <copy> up --main-menu` sets it, then
  `gatekit --instance <copy> timeline --kind UIState`.

## Levels a test can start in

Each loads cold with `gatekit --instance <copy> up --level GustavX:<Level>`.

| Level | What it is |
|---|---|
| `Basic_Level_A` | the engine's basic level, nothing placed: the sandbox |
| `TestLevel_Tom` | a developer's test level with a few items |
| `WLD_Campfire_E` | an Act 1 camp |
| `END_NetherBrainArenas_E` | an arena of the finale |
| `SYS_CC_I` | character creation; the party is the four creation dummies |
| `WLD_Main_A` | Act 1, with its whole population |
| `SCL_Main_A` | Act 2, with its whole population |
| `CTY_Main_A` | Act 3, the Lower City, with its whole population |

The first five are small enough to stage a scene in; the act levels bring every character of the
act. Whether a level counts as a simple test level for the reset ladder is decided by
`GK.Stage.Census()`: `gatekit --instance <copy> exec server 'return GK.Stage.Census()'`.

## Game copies

- The game runs from a clone of its folder: a copy of `bin` and a `Data` folder of hard links
  (the `.pak` files) and junctions (the folders); it finds `Data` relative to the executable.
  Nothing of `Launcher`, `DotNetCore` or `DigitalDeluxe` is needed with `--skip-launcher`.
  `gatekit --instance <copy> up` makes such a clone on its first start.
- The profile folder (`Larian Studios/Baldur's Gate 3`) is found through Windows' known folders,
  which follow the process's `USERPROFILE`. A game started with `USERPROFILE` pointing elsewhere
  keeps its whole profile there: mod list, saves, `Mods`, Script Extender's folder (so the
  bridge's channel), `LevelCache` and the graphics driver's shader cache. No game switch does
  this. Every copy runs so.
- A game started while another game process runs does not use the player profile `Public` but
  `PlayerProfiles/Debug_Client_Profile_<n>`, with its own mod list and saves, whatever
  `playerprofiles8.lsf` names; a fresh debug profile has the base game only. The same holds for the
  installed game started next to running copies. `gatekit --instance <copy> save` lists a copy's
  saves across these profiles.
- The game mirrors the active player profile into Steam's cloud folder
  (`<Steam>/userdata/<id>/1086940/remote/_SAVE_<profile>` and `_PROFILE_<profile>`) both ways, even
  when started directly: a save missing locally comes down, one missing in the cloud goes up, and
  a file deleted from that folder by hand comes back. With an application id in `bin/steam_appid.txt`
  that is not the game's, the game logs "Failed to initialize the Steam API" and runs normally,
  with no mirror. Every copy carries such an id.
- Script Extender's loader keeps its downloaded builds in `%LOCALAPPDATA%/BG3ScriptExtender`;
  `"CachePath"` in `bin/ScriptExtenderUpdaterConfig.json` points a copy at that cache.
- Both games' windows have the same title; processes are told apart by the executable's full
  path, windows by their process id. `gatekit instances`.

## Input

- Key messages posted to the game window (`WM_KEYDOWN`, `WM_KEYUP`) work while it is behind other
  windows; `SendKeys` and posted mouse messages are ignored; real input needs the window in front.
  `gatekit --instance <copy> key escape`.
- The engine has functions that switch controller mode -
  `ls::InputManager::SetControllerMode`, `ecl::EocClient::SetControllerMode`,
  `ecl::InputController::SetControllerMode` - and no view model of the interface exposes one.
  `gatekit symbols find SetControllerMode`.

## The interface

- Noesis may only be touched from the interface's context (`Ext.UI.Defer`); from a tick handler
  Script Extender warns, and it can crash.
- `gui::VMDialogue` has `BodyText`, `SpeakerName` (a localisation handle), `Answers`
  (`gui::VMDialogueAnswer`: `AnswerIdx`, `BodyText`, `Enabled`, `BoundEvent`), `DialogInstanceID`,
  `ShowAnswers` and `History`. Every `gui::DC*` widget context carries the game's commands
  (`ui::DeferredCommand`); `SelectAnswerCommand:Execute(answer)` chooses an answer. A dialogue is
  walked from start to end this way: `gatekit --instance <copy> test tests -k dialogue`.
- A dialogue starts from the server with `Osi.QRY_StartDialog(dialog, speaker, player)` once the
  player stands next to the speaker; `Osi.DB_Dialogs` lists the registered speaker and dialogue
  pairs. `gatekit query search QRY_StartDialog` shows its signature.
- A live timeline's `PlayingSpeed`, `JumpToTime`, `JumpToPhase`, `SkippingMode`, `IsPaused` and
  `PlayToEnd` are writable from Lua; at `PlayingSpeed = 20` a dialogue still passes every node of
  its story. `game.fast_scenes(20)` in a test.
- `Ext.System.ClientTimeline.Timelines[id].Components` lists every track of a live timeline as a
  typed object (`aspk::TLShowWeaponComponent`, `TLShowVisualComponent`, `TLShowArmorComponent`,
  ...) with `Actor.UUID`, `ID`, `PhaseIndex`, `StartTime`, `EndTime`, `IsActive`, `Enabled`; the
  timeline gives `PhaseIndex`, `TimePlayed` and `Duration`. A track's `ID` matches the timeline
  file in the game's data: `gatekit query scene <name>`.
- `Ext.UI.GetStateMachine()` answers nil in a game: `exec client 'return Ext.UI.GetStateMachine()'`.

## Script Extender

- Lua files loose in `<game>/Data/Mods/<Folder>/ScriptExtender/Lua` override the same files in a
  `.pak`, and `Ext.Debug.Reset()` on the server and then the client reloads both Lua states in
  place in under a second. `gatekit link <mod-root>`, then `gatekit --instance <copy> reload`.
- `EnableLogging` and `LogDirectory` in `bin/ScriptExtenderSettings.json` write an "Osiris
  Runtime" log: every story event, every rule that fired, every fact added or deleted. It does not
  hold the console's output. `up --story-log`, then `gatekit --instance <copy> logs --story`.
- `load` takes `(text, env)`: `load(text, "=name")` raises "bad argument #2 to 'load' (table
  expected, got string)". `exec client 'return pcall(load, "return 1", "=name")'`.
- The JSON a result is returned as writes a table that occurs twice only once; the second
  occurrence comes back as a reference string, and a table with nils in it comes back as an object
  keyed by index. `exec client 'local t = {} return { t, t }'`.
- `Ext.IO.LoadFile(path, "data")` reads any file of a mod, from its `.pak` or loose; a missing file
  gives nil without a word in the log. `exec client 'return Ext.IO.LoadFile("Mods/none.txt", "data")'`.
- `Ext.IMGUI.GetBackendInfo().Renderer` is `"DX11"` or `"Vulkan"`; pictures from files are decoded
  under DX11 only. `exec client 'return Ext.IMGUI.GetBackendInfo()'`.

### IMGUI in GateKit's build

What Weave's backend stands on; `gatekit --instance <copy> test tests -k se_fork_ui` and `-k overlay`
check it.

- A colour (`SetColor`), a style variable (`SetStyle`), a `Font` or a `FontSize` set on a window,
  group, tab or child applies to everything inside it, and all four can be taken back:
  `ClearColor(name)`, `ClearStyle(name)`, `Font = ""`, `FontSize = nil`. So can `Size`,
  `SizeHint`, `ItemWidth`, `TextWrapPos`, `PositionOffset`, `AbsolutePosition` and
  `SetBgAlpha(nil)`.
- `FontSize = n` is in the units `Ext.IMGUI.LoadFont` sizes use and is drawn at
  n x (picture height / 1610) pixels. The six built-in fonts are one font file loaded at Tiny 24,
  Small 28, Medium 32, Default 36, Large 40 and Big 44; `Font = "Large"` draws as `FontSize = 40`
  does, and with both set the size is `FontSize`'s. One file loaded once is one font at every size.
- `window:SetSizeConstraints(min[, max])` lasts until it is called again without arguments, and
  takes a minimum alone.
- IMGUI flags are plain boolean properties (`window.NoTitleBar = true`). `window:SetPos(pos, cond,
  pivot)` takes a pivot; positions and sizes are pixels of `Ext.IMGUI.GetViewportSize()`.
- `widget.SameLine = true` cancels `widget.PositionOffset`, and a `PositionOffset` - even
  `{ 0, 0 }` - wins over `AbsolutePosition`, which then counts from the top left corner of the
  window it is drawn in, title bar included.

## The official Toolkit

- Steam app 2934770. Its engine is split into DLLs (`EoCClient.dll`, `EoCServer.dll`, `ECS.dll`,
  `CoreLib.dll`, ...) that export full C++ names and signatures; the symbol database is read from
  them. `gatekit symbols classes ecl::` after `gatekit symbols build`.
- The editor is .NET Framework: `EditorFramework.dll`, `EoCPluginCSharp.dll` and `LSFrameworkUI.dll`
  are plain managed assemblies, `Glasses.exe` and `EoCPlugin.dll` mixed C++/CLI.
  `gatekit symbols managed-strings <assembly>`, `gatekit decomp dotnet`.
- Script Extender does not start in the Toolkit: it checks for `bg3.exe` or `bg3_dx11.exe`.
