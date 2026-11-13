# Measured facts (game 4.1.1.7631656, Script Extender v33, 2026-11-12)

## Starting the game

- `bin\bg3_dx11.exe --skip-launcher -continueGame` with `steam_appid.txt` (1086940) next to it
  starts without Steam or the launcher and loads the last save with no input at all.
- If the game was killed instead of quitting, it leaves `%LOCALAPPDATA%\Larian Studios\Baldur's Gate 3\ModCrashSanityCheck`.
  On the next start that folder makes it switch every mod off for the session and stop at
  "press any key" (and `-continueGame` is ignored). Deleting the folder before the start avoids it.
- The studio logo is `Video/Splash_Logo_Larian.bk2` in `Gustav_Video.pak`. An empty loose file at
  `Data\Video\Splash_Logo_Larian.bk2` removes it.
- Time from process start to a playable save (Act 1 save, SSD): 57–91 s over five runs.
  Bridge's first heartbeat (Lua running, still in the menu module): about 24 s with the logo video.
- Stages of that start (seconds from process start): LoadModule 16, LoadMenu 20, menu done 36,
  LoadLevel 41, SwapLevel 62, Running 76. The engine's own start is the first 16-19 s.
- `-testLoadLevel <module>:<level>` (e.g. `GustavX:TUT_Avernus_C`) starts a fresh test game
  straight in that level: no menu, no save; playable after 37-38 s. The player is Astarion.
  It autosaves once on arrival.
- In such a fresh game other installed mods interfere (Ancient Mega Pack opens a mode-choice
  popup), so the stand loads only the base game, the bridge and the mods named for the run.
- Other switches found in the exe, not tried yet: `-loadSaveGame` (exclusive with
  `-continueGame`), `-load`, `-module`, `-testAIStart`, `-storylog`, `-syslog`, `--logPath`,
  `-mediaPath`, `-stats`, `--cpuLimit`, `-modded`.
- Input: `SendKeys` is ignored. `WM_KEYDOWN`/`WM_KEYUP` posted to the game window work while it
  is in the background (Escape, F11 checked). Posted mouse messages are ignored. Real input
  (`keybd_event` / `mouse_event`) needs the window in front; real clicks work in the main menu.
- `Ext.ClientInput.InjectKeyPress("ESCAPE")` is accepted (it appeared to skip a cutscene; not
  checked more closely). `GlobalSwitches.ControllerMode` is read-only from Lua, so switching to
  the controller UI needs another route (`InputManager.DeviceEventInjects` not explored yet).
- `PrintWindow(hwnd, dc, PW_RENDERFULLCONTENT)` captures the game window while it is in the
  background or covered.

- Later runs of the test level were playable after 26-29 s (files cached by Windows).
- Injecting raw input changes into `InputManager.InputInjects` (`controller_a` from device 1, or
  from a made-up device 2) is accepted and consumed but does not switch the UI to controller mode.

- `-loadSaveGame <save folder name>` (the folder under `Savegames\Story`, e.g.
  `dd3dab06-...__HonourMode`) loads that save without any input; it still passes through the
  menu module (Act 1 save: playable after 61 s).
- `-continueGame` loads the newest save, whatever made it - including an autosave of a test game.
- The game mirrors `PlayerProfiles\Public` into Steam's cloud folder
  (`<Steam>\userdata\<id>\1086940\remote\_SAVE_Public` and `_PROFILE_Public`), even when started
  directly, and on the next start restores from there any save missing locally. Deleting a save
  for good means deleting it in both places.
- `GlobalSwitches.CanAutoSave` is writable from Lua on both sides.

## Several games at once (`src/GateKit.Stand`, `BG3_GATEKIT_INSTANCE`)

- The game runs from a clone of its folder: a copy of `bin` plus a `Data` folder made of hard
  links (the `.pak` files) and junctions (the folders). It finds `Data` relative to the exe.
  Nothing of `Launcher`, `DotNetCore`, `DigitalDeluxe` is needed with `--skip-launcher`.
- The profile folder (`...\Larian Studios\Baldur's Gate 3`) is looked up through Windows' known
  folders, and Windows derives those from the process's `%USERPROFILE%`
  (`User Shell Folders\Local AppData` = `%USERPROFILE%\AppData\Local`). A game started with
  `USERPROFILE` pointing elsewhere keeps its whole profile there: mod list, saves, `Mods`,
  `Script Extender\...` (so the bridge's channel too), `LevelCache`, and the graphics driver's
  shader cache. `SHGetKnownFolderPath` was checked directly; no game switch for this was found.
- Script Extender's loader keeps its downloaded builds in `%LOCALAPPDATA%\BG3ScriptExtender`;
  `"CachePath"` in `bin\ScriptExtenderUpdaterConfig.json` points a clone at the existing cache,
  so nothing is downloaded again.
- A game started while another game process runs (any folder) does not use the player profile
  `Public` but `PlayerProfiles\Debug_Client_Profile_1`, with its own `modsettings.lsx` and
  saves; the active profile named in `playerprofiles8.lsf` is ignored then. A fresh debug profile
  gets a mod list with the base game only — which looked like "the mods are off". The stand
  writes the same mod list and saves into `Public` and `Debug_Client_Profile_1..4`. Seen: `_1`
  with one other game running, `_2` with two. This holds for the installed game as well: started
  next to running instances it uses a debug profile of the player's real profile folder, so the
  stand lends those profiles its mod list for the run and puts back what was there on `down`.
- Cloud: `ecl::CloudManager` mirrors the active player profile into Steam's cloud folder as
  `_PROFILE_<name>` and `_SAVE_<name>` (seen for `Public` and `Debug_Client_Profile_1`), both
  ways: saves missing locally come down, saves missing in the cloud go up. All copies of the game
  share that folder, so a clone that talks to Steam passes saves to the installed game and back.
  A file deleted by hand from that folder after Steam has synced it comes back later.
- With an application id in the clone's `bin\steam_appid.txt` that is not the game's, the game
  logs "Failed to initialize the Steam API" and runs normally (as it does without Steam): no
  cloud mirror, nothing written under `<Steam>\userdata`. Playable after 44 s in the test level
  with another game running next to it.
- Memory of one game in the test level: about 2.2 GB working set, 7.6 GB committed.
- Processes are told apart by the exe's full path (`QueryFullProcessImageName`), windows by
  their process id; both games' windows have the same title.

## The UI as an API

- Noesis may only be touched from the UI's context (`Ext.UI.Defer`); from a Tick handler
  Script Extender warns and it can crash.
- `gui::VMDialogue` (data context of the dialogue widget's inner elements) has `BodyText`,
  `SpeakerName` (a localisation handle), `Answers` (`gui::VMDialogueAnswer`: `AnswerIdx`,
  `BodyText`, `Enabled`, `BoundEvent`), `DialogInstanceID`, `ShowAnswers`, `History`.
- `gui::DCDialogue` - like every `gui::DC*` widget context - carries about 140 game commands
  (`ui::DeferredCommand`). `SelectAnswerCommand:Execute(answerViewModel)` chooses an answer.
- The Volo camp dialogue was walked from Lua start to end this way.

- Scenes can be fast-forwarded: `PlayingSpeed`, `JumpToTime`, `JumpToPhase`, `SkippingMode`,
  `IsPaused`, `PlayToEnd` of a live timeline are writable from Lua. `PlayingSpeed = 20` on the
  dialogue timelines walks the Volo camp dialogue (4 choices) in 2.5 s; the story still passes
  every node.
- A dialogue starts from the server with `Osi.QRY_StartDialog(dialog, speaker, player)` once the
  player stands next to the speaker; `Osi.DB_Dialogs` lists the registered speaker/dialogue pairs.
- The Osiris event `EnteredLevel` fires for every object of a level (thousands on load).
- Controller mode, final state for now: no switch in the UI's view models either. The engine
  functions exist - `ls::InputManager::SetControllerMode(EInputDeviceID, bool)` (CoreLib),
  `ecl::EocClient::SetControllerMode(EInputDeviceID)`, `ecl::InputController::SetControllerMode(bool)`
  and a console command handler `ecl::EocClient::CommandInputController` (found with
  the symbol database: `find ControllerMode`, `callers`) - but calling them needs native code.

## Script Extender

- Hot reload: Lua files loose in `<game>\Data\Mods\<Folder>\ScriptExtender\Lua` override the
  same files in a .pak, and `Ext.Debug.Reset()` (called on the server, then the client) reloads
  both Lua states in place in under a second.
- `EnableLogging` + `LogDirectory` in `bin\ScriptExtenderSettings.json` write an "Osiris Runtime"
  log: every story event, rule that fired and fact added or deleted. It does not contain the
  console output; that can be read from the console window's buffer by another process
  (`AttachConsole` + `ReadConsoleOutputCharacter`).
- `Ext.UI.GetStateMachine()` returned nil in game (tried no argument, 0, 1, 65537).

- Mods' Lua (and so the bridge) already runs in the main menu.
- `Ext.System.ClientTimeline.Timelines[id].Components` lists every track of a live timeline as a
  typed object (`aspk::TLShowWeaponComponent`, `TLShowVisualComponent`, `TLShowArmorComponent`,
  `TLMaterialComponent`, ...; 4,684 tracks in the Volo camp scene) with `Actor.UUID`, `ID`,
  `PhaseIndex`, `StartTime`, `EndTime`, `IsActive`, `Enabled`. The timeline gives `PhaseIndex`,
  `TimePlayed`, `Duration`.
- The keys of event tracks (e.g. `ShowWeaponKey.ShowWeapon`) are **not** exposed in v33:
  `TLEventKeyComponent<...>` has no members. The track's `ID` matches the timeline file, so the
  key values can be joined from extracted data until Script Extender exposes them.
- IMGUI windows work in the DX11 game; `Ext.Net` channels work between the bridge's two sides.

### IMGUI styling and layout (measured for the overlay kit)

Measured on GateKit's build of Script Extender on the v34 base (`Ext.Utils.Version()` 34,
ImGui 1.93; `se-fork\builds\v34`) in a 1920 x 1080 picture, 2026-11-12, unless a line says
otherwise.

- A colour (`SetColor`), a style variable (`SetStyle`), a `Font` or a `FontSize` set on a
  window, group, tab or child applies to everything inside it. All four can be taken back:
  `ClearColor(name)`, `ClearStyle(name)`, `Font = ""` (or nil), `FontSize = nil`. So can
  `Size`, `SizeHint`, `ItemWidth`, `TextWrapPos`, `PositionOffset`, `AbsolutePosition` (= nil)
  and `SetBgAlpha(nil)`. (Stock v33 could take none of the first three back.)
- Text sizes: `widget.FontSize = n` is in the units `Ext.IMGUI.LoadFont` sizes use, and is
  drawn at n x (picture height / 1610) pixels - 2/3 in a 1080-line picture. Line heights read
  with `LastRect`: 16 -> 11, 24 -> 16, 28 -> 19, 32 -> 21, 36 -> 24, 40 -> 27, 44 -> 30,
  60 -> 40. The six built-in fonts are one font file loaded at Tiny 24, Small 28, Medium 32
  (Script Extender's own), Default 36, Large 40, Big 44, and `Font = "Large"` draws exactly as
  `FontSize = 40` does. With both `Font` and `FontSize` set the size is `FontSize`'s.
- `Ext.IMGUI.LoadFont(name, path, size)`: one font file is one font at every size. Two names
  loaded from the same file at 32 and at 60 draw at 21 and 40 pixels without a `FontSize` and
  both at whatever `FontSize` says; a load takes under a millisecond and rebuilds nothing.
- With HDR on for the display GateKit's build converts colours in the shader, so they are shown
  as given (research/se-fork/notes.md). (Stock v33 wrote sRGB into the HDR10 picture: v showed as about
  (2v)^1.85, 0.5 already white.)
- IMGUI flags are plain boolean properties: `window.NoTitleBar = true`, `tab.SetSelected = true`.
- `window:SetPos(pos, cond, pivot)` takes a pivot; positions and (with the default `Absolute`
  scaling) sizes are pixels of `Ext.IMGUI.GetViewportSize()`.
- `window:SetSizeConstraints(min[, max])` lasts until called again without arguments and takes
  a minimum alone. (On stock v33 constraints held for one frame and a lone minimum collapsed
  the window.)
- `widget.SameLine = true` still cancels `widget.PositionOffset`: a text after another with
  `SameLine` and an offset of 30 started 5 pixels (the item spacing) after it. A
  `PositionOffset` - even `{ 0, 0 }` - also wins over `AbsolutePosition`, which is then
  ignored. `widget.AbsolutePosition = { x, y }` places an element counted from the top left
  corner of the window (or child window) it is drawn in, title bar included: `{ 50, 60 }` in a
  window at 800, 100 drew at 850, 160. A group still encloses such elements, and what follows
  the group stands below it.
- Script Extender's `load` takes `(text, env)`: `load(text, "=name")` raises "bad argument #2
  to 'load' (table expected, got string)".
- New on the v34 base: `CollapsingHeader.Open` (read only: what was drawn last) and
  `SetOpen(open[, cond])` - the condition defaults to `Once`, so pass `"Always"` to open or
  close it again later; `OnExpand` / `OnCollapse` on headers and trees (raised for the user's
  toggle); `Tree:SetOpen`; `Selectable.OnDoubleClick`; `Speed` on `Drag` / `DragInt` (default
  1; sliders have none); `Step` on `InputInt` / `InputScalar` (nil: no - / + buttons; there is
  no `StepFast`); `InnerWidth` on tables.
- `Ext.IMGUI.GetBackendInfo().Renderer` is `"DX11"` or `"Vulkan"`; pictures from files are
  decoded under DX11 only.
- The JSON a result is returned as writes a table that occurs twice only once; the second
  occurrence comes back as a reference string. A table with nils in it comes back as an
  object keyed by index.
- IMGUI itself keeps a few pixels of a window inside the picture, not its title bar.
- `Ext.IO.LoadFile(path, "data")` reads any file of a mod from the pak or loose
  (`Mods/<Folder>/ScriptExtender/Lua/...`), a missing file gives nil without a word in the log,
  and a read takes about 0.15 ms: polling a handful of files twice a second is free.
- The game lays its HUD out on a canvas of 3840 x 2160 units fitted into the picture
  (`ContentRoot` > widget `Minimap` > ... > `MinimapContainer`, 660 x 700 units, top right);
  the widget's `IsVisible` / `Visibility` follow the HUD being hidden in cutscenes. Readable
  from `Ext.UI.Defer`.
- The game window resized from outside (`SetWindowPos`) changes the viewport at once; the game
  then chose the height itself and did not give it back until restarted.

## Official Toolkit

- Steam app 2934770, `steamapps\common\Baldurs Gate 3 Toolkit`, `Glasses.exe` 4.1.1.6931813.
- The engine is split into 205 DLLs (`EoCClient.dll`, `EoCServer.dll`, `ECS.dll`, `EoCShared.dll`,
  `CoreLib.dll`, ...) that export full C++ names and signatures (about 10,000 in the client alone).
- `Glasses.exe` started directly (no `steam_appid.txt`, no Steam window) opens the editor in
  "USER MODE" with nothing loaded. Menus, read through Windows UI Automation without focusing
  the window: File (Load level..., Unload level, Reload level, New project..., Open project...,
  Save all, Exit), Project (Project Settings..., Convert UI Assets..., Render Equipment Icons,
  Render CC Icons, Open Folder...), Tools (Reload Stats..., Short Name List Viewer), Options.
  No "load savegame" entry. `EoCPlugin.dll` does contain `LoadSaveGame` and `OnPlayModeChanged`.
- The Toolkit writes `log.<time>.txt` next to itself: engine messages with Larian's source file,
  line and function name on every line.
- Driven through UI Automation (`UI Automation from the host`): a project was created (New Project tab,
  `#m_NameBox`, `#m_CreateButton`; it appears as `Data\Projects\<Name>_<guid>` and
  `Data\Mods\<Name>_<guid>`), and a level of the game itself was loaded: the level browser shows
  inherited levels, its tiles carry no names, but typing the exact name into `#m_SearchBox` leaves
  one tile selected and `#m_OpenButton` loads it. `TUT_Avernus_C` took about a minute and 8 GB.
  A full-tree UI Automation search on the loaded editor is very slow and can fail.
- The editor is .NET Framework 4.7.1. `EditorFramework.dll`, `EoCPluginCSharp.dll`,
  `LSFrameworkUI.dll` are plain managed assemblies (readable by reflection:
  `symbols/managed_strings.ps1`); `Glasses.exe` and `EoCPlugin.dll` are mixed C++/CLI.
  `EditorFramework.Plugins.IPlugin` is a full plugin interface (Init/Start/Stop, OnGameStarted,
  OnLevelLoaded, OnModuleLoaded, ...), registered through `PluginServiceBase.AddPlugin`. How the
  host discovers plugins is not known yet.
- The .NET runtime honours `APPDOMAIN_MANAGER_ASM` / `APPDOMAIN_MANAGER_TYPE` for `Glasses.exe`
  although it is a mixed C++/CLI executable: a managed DLL placed in the Toolkit folder is
  loaded before `Main` (this is how `the Toolkit bridge` gets its bridge in).
- `Glasses.exe -project <name> -lvl <level>` opens the project and loads the level by itself.
  Started with no `-project` it shows its project window and does nothing else. `LoadProject` /
  `LoadLevel` called by hand during start-up leave the level load stuck at 97 %.
- Events raised on the editor's game object during such a start, in order: `GameStarted`,
  `ModuleUnloading`/`ModuleUnloaded` (twice), `ModuleLoaded`, `ServerModuleLoaded`,
  `SessionLoaded`, `ClientGameIdle`, `LevelUnload`, `LevelLoad`, `LevelLoaded`.
- Game mode: `Game.ResumeGame()` shows the game's HUD in the editor viewport and raises
  `ClientGameUnpaused`, `GameResumed`; `Game.PauseGame()` raises `ClientGamePaused`, `GamePaused`.
- Controller-mode switching by injected device events (`DeviceEventInjects`, event ids 0-9 from
  device 3) was tried in the game and did nothing either.
- Script Extender does not initialise in the Toolkit (it checks for `bg3.exe` / `bg3_dx11.exe`).
- Not tried yet: a save loaded in the Toolkit.

## Unlocked toolkits

- MoonGlasses (Nexus 12308): build 586 of 2026-02-23 is the current public one; newer builds
  are posted on the team's Discord first.
- BG3 Toolkit Unlocked (Nexus 12021): last updated 2024-09-14, Patch 7 toolkit only.
