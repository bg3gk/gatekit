# Getting a program channel into the official Toolkit (Glasses)

Read from the Toolkit's managed code, decompiled to C# with ILSpy (Toolkit 4.1.1.6931813,
2026-11-12). Nothing here was tried in a running Toolkit; what is a reading of the code and what
is a suggestion is marked.

The decompiled sources are searchable:

```
set BG3_DECOMP_WORK=<work folder>\decomp
python <old repo>\symbols\decomp\decomp.py csharp "LoadLevel"            search, with matching lines
python <old repo>\symbols\decomp\decomp.py csharp Glasses/Glasses/GlassesApp.cs --file   print a file
```

and lie as plain files in `<work folder>\decomp\dotnet\<Assembly>\`: 57
assemblies, 30,678 files, 1.95 million lines. `EoCPlugin.dll` (mixed C++/CLI) is only partly
there — its decompilation was stopped after 45 minutes with 8,422 files written.
ILSpy's command-line package (`ilspycmd` 9.1, from nuget.org, 4 MB) runs on the .NET 8 runtime in
`%BG3_GATEKIT_TOOLS%\dotnet`; no SDK was needed.

## 1. How Glasses loads plugins — and how to get one in

**It does not discover plugins.** `Glasses.GlassesApp.InitPlugins()`
(`Glasses/Glasses/GlassesApp.cs`, about lines 290–410) is one long list of

```csharp
TryAddPlugin<TimelinePlugin>(() => new TimelinePlugin(selectionManager, sidebarService));
```

for 81 plugin classes, each constructed in code. There is no folder scan, no
configuration file naming assemblies, no `Assembly.Load` anywhere in the managed code. A managed
DLL dropped next to `Glasses.exe` is never looked at.

`TryAddPlugin` asks the workflow whether the plugin may load:

```csharp
bool ShouldLoadPlugin(string name) =>
    workflowService.GetPluginVisibility(name) != Visibility.Disabled;
bool TryAddPlugin<TPlugin>(Func<TPlugin> create) where TPlugin : IPlugin =>
    ShouldLoadPlugin(typeof(TPlugin).Name) && pluginServ.AddPlugin(create());
```

That is also how the retail Toolkit is "locked". `GlassesApp.CreatePublicWorkflow()` builds a
workflow named `Public` that disables a list of plugins by class name (`TimelinePlugin`,
`ScriptPlugin`, `ConstellationPlugin`, `TEAnalyzerPlugin`, `VoiceBark`, ... ) and of resource and
template types. `WorkflowService.Prepare()` loads other workflows from `Data\Editor\Workflows\`
and adds a full one named `Dev` only when `FileAccess.Instance.IsBuildExternal` is false, and
that flag comes from native code (`FrameworkPlugin.BuildType == EBuildType.External`). The retail
install has no `Data\Editor\Workflows` folder at all.

**Adding a plugin from inside is one call.** Once code of ours runs in the process:

```csharp
var plugins = FrameworkClass.Instance.ServiceManager.GetService<PluginService>();  // or MacrosHelper.GetService<PluginServiceBase>()
plugins.AddPlugin(new GateKitPlugin());     // calls Init() and adds it; Start() is called by the service start-up
```

`EditorFramework.Services.PluginServiceBase.AddPlugin(IPlugin)` only checks for a duplicate,
calls `plugin.Init()` and appends to the list; from then on the plugin receives every callback
the service fans out. Derive from `LSToolFramework.PluginBase` (in `EditorFramework.dll`) rather
than implementing `EditorFramework.Plugins.IPlugin` directly: the interface has about forty
members and `PluginBase` gives all but `Init`, `Start`, `Stop` an empty body. Callbacks worth
having: `OnGameStarted`, `OnGameExits`, `OnGamePaused`, `OnGameUnpaused`, `OnLevelLoading`,
`OnLevelLoaded`, `OnLevelStarted`, `OnLevelUnloaded`, `OnModuleLoaded`, `OnModuleUnloading`.
If the plugin is added after start-up, call its `Start()` and `PostStart()` yourself.

**Getting our code into the process without replacing Larian's files** — suggestions, in order of
how little they disturb:

1. **AppDomainManager through the environment** (suggestion, to be tried first). The .NET
   Framework 4 runtime reads two environment variables when it starts a process:
   `APPDOMAIN_MANAGER_ASM` (full assembly name) and `APPDOMAIN_MANAGER_TYPE` (type name). It
   loads that assembly and calls `InitializeNewDomain` on the type before `Main` runs. The
   assembly must be found by normal probing, which in practice means our DLL sits in the Toolkit
   folder — a new file, nothing of Larian's changed — and the stand starts `Glasses.exe` with
   the two variables set for that process only. `InitializeNewDomain` should do nothing but start
   a watcher that waits until `FrameworkClass.Instance.ServiceManager` has a `PluginService` and
   the main window exists, then adds the plugin on the UI thread
   (`System.Windows.Application.Current.Dispatcher.Invoke`). Unknown until tried: whether the
   runtime honours the variables for this executable, which is mixed C++/CLI.
2. **Glasses.exe.config.** The same manager can be named in the `<runtime>` section
   (`appDomainManagerAssembly`, `appDomainManagerType`). This edits a file Larian ships (a text
   configuration file, restored by Steam's "verify files"), so only if the first way fails.
3. **Native injection plus the CLR hosting interface**: start the process, inject a small native
   DLL, and from it call `ICLRRuntimeHost::ExecuteInDefaultAppDomain` to run a static method of
   our managed DLL. No Larian file touched, more moving parts, and anti-virus tools dislike it.
4. A profiler (`COR_ENABLE_PROFILING`, `COR_PROFILER`, `COR_PROFILER_PATH`) is the documented
   way to get a native DLL loaded by the runtime from environment variables alone; it is the
   heaviest of the four and only worth it if everything else is refused.

**There is already a one-way channel out of the Toolkit**, switched on in the retail build:
`EoCUI.Automation.AutomationPlugin` (it is not among the plugins the public workflow disables).
On every editor state change it sends one UDP datagram to `127.0.0.1:6666` with JSON such as

```json
{"MessageType":"EditorStateChanged","Info":["LevelLoaded","WLD_Main_A"]}
```

`MessageType` is `EditorStateChanged` or `StoryEditorStateChanged` (story build stopped); the
first element of `Info` is one of `GameStarted`, `GameExits`, `GamePaused`, `GameUnpaused`,
`ClientGamePaused`, `ClientGameUnpaused`, `LevelLoaded` (followed by the level name),
`LevelStarted`, `LevelTemplateLoaded`, `LevelUnloaded`, `ModuleLoaded`, `ModuleUnloading`,
`ModuleUnloaded`, `PreProjectSelectionWindowShow`. Listening on that port costs nothing and tells
the stand when the editor is ready, when a level finished loading and when game mode starts —
without any code of ours inside. There is no listener in the Toolkit: commands cannot be sent in
this way.

## 2. Command line

`FrameworkClass.Init(string[] args)` (`EditorFramework/EditorFramework/FrameworkClass.cs:261`)
turns the arguments into a dictionary: a token starting with `-` is a key (without the dash); if
the next token does not start with `-` it is the value. So the form is `-key value` or `-flag`.

| Switch | Read in | Effect |
|---|---|---|
| `-gameProject NAME` | `WorkflowService.HandleArguments` | selects the game project by name |
| `-workflow NAME` | same | selects a workflow by name; `Public` selects the public one. Only workflows that were loaded can be selected, and retail loads none but `Public` |
| `-project NAME` | same | the mod (project) to open; `MainProject` means the game project's main one. With this set, `ProjectPlugin.OpenRequest()` opens it without showing the project window, provided a project of that name exists |
| `-lvl NAME` | same; used in `EoCPluginClass` after the module loads | the level to load on start: `LevelPlugin.LoadLevel(name, sandboxMode: false)` |
| `-savegameLvl NAME` | `EoCPluginClass` (about line 5785) | after the level, sends the server a "load game" command for that save (message 181, `eocnet::ServerCommand::Loadgame`) |
| `-SetCfg VALUE` | `FrameworkClass` (line 557) | overrides a configuration value |
| `-modEnv dev` | `AuthenticationViewModel` | mod.io development environment |
| `!SaveCfgsInCurrentDirectory` (no dash) | `GlassesApp.Init` | keep editor configuration next to the executable |
| `silenceWin32Exceptions` (no dash) | `GlassesApp` | swallow one class of UI automation exceptions |
| build switches | `BuildCommandManager.Initialize` | run a build command and exit the interactive flow: `-buildStory`, `-buildLevels`, `-buildLevelsNoGrid`, `-buildHLODs`, `-cleanHLODs`, `-logHLODs`, `-buildProbes`, `-buildStaticPhysics`, `-buildPortraits`, `-buildPSOsCache`, `-buildWorldMaps`, `-compileBlueprints`, `-compileDialogs`, `-generateTimelines`, `-generateVirtualTextures`, `-generatePlatformData`, `-exportUpdateSoundResources`, `-serializeResourcesToHoudiniFile`, `-syncAudioBanks`, `-thumbnailGenerator`, `-validateDialogTexts`; with `-commit MESSAGE`, `-c CHANGELIST`, and per-command options such as `-levels`, `-dialogs`, `-file` |

There is no switch for "start in game mode". A project and level can be given
(`Glasses.exe -project MyMod -lvl WLD_Main_A`), and a save loaded on top (`-savegameLvl`).
Whether the build switches do anything useful under the public workflow is not known; several of
them need plugins it disables.

`Program.Main` refuses to start a second editor ("Can't run 2 editor instances at the same time!")
and warns when the game is running.

## 3. Managed calls

Reach everything from `EditorFramework.FrameworkClass.Instance` (a static singleton).
`EditorFramework.MacrosHelper.GetService<T>()` and `GetPlugin<T>()` are shorthand for
`Instance.ServiceManager.GetService<T>()` and `GetService<PluginServiceBase>().GetPlugin<T>()`.
Call from the UI thread.

| To do | Call | Where |
|---|---|---|
| Open a project (mod) | `MacrosHelper.GetPlugin<LSFrameworkPlugin.ProjectPlugin>().LoadProject(string name, bool forceLoad)` | `LSFrameworkPlugin/LSFrameworkPlugin/ProjectPlugin.cs:305` |
| Open the project a save belongs to | `ProjectPlugin.LoadSaveGameProject(string name)` | same file, line 362 |
| Show the project window | `ProjectPlugin.OpenProject(object, EventArgs)`; `OpenRequest()` is the start-up path that honours `-project` | lines 289, 153 |
| Load a level | `MacrosHelper.GetPlugin<EoCPlugin.LevelPlugin>().LoadLevel(string levelName, bool sandboxMode)` → `bool` | `EoCPlugin/EoCPlugin/LevelPlugin.cs:592` |
| Unload the level | `LevelPlugin.UnloadLevel(object, EventArgs)`; or `FrameworkPlugin.LevelManager.UnloadLevel()` | `LevelPlugin.cs:815`, `MLevelManager.cs:216` |
| Current level | `FrameworkPlugin.LevelManager.HasActiveLevel()`, `GetActiveLevelName()`; `FrameworkClass.Instance.Game.CurrentLevel` | |
| Enter game mode | `FrameworkClass.Instance.Game.ResumeGame()` | `IGame`; implemented by `EoCPlugin.EoCPluginClass.ResumeGame()`, line 2978 |
| Back to editor mode | `FrameworkClass.Instance.Game.PauseGame()` | `EoCPluginClass.PauseGame()`, line 2944 |
| Which mode | `Game.IsEditorModeActive()`, `Game.IsGameUpdating()`, `Game.IsLoading()`, `Game.IsLoadedFromSavegame()` | `IGame` |
| Load a save | `EoCPlugin.BrowserPluginHelper.LoadSaveGame(string saveGame)` (override of `ProjectPluginCallbackWrapper.LoadSaveGame`) | `EoCPlugin/EoCPlugin/BrowserPluginHelper.cs:162` |
| Load a module | `Game.LoadModule(string module)` | `EoCPluginClass.LoadModule`, line 3149 |
| Story build | `Game.StartStoryBuild(bool claimWorkerTicket, bool doReload)`, `StopStoryBuild(bool, bool)` | |
| Reload stats | `EoCPluginClass.ReloadStats()` | line 4029 |
| Events | `Game.GameStarted`, `GameExits`, `GamePaused`, `GameResumed`, `LevelLoading`, `LevelLoaded`, `LevelStarted`, `LevelUnload`, `ModuleLoaded`, `ModuleUnloaded`, `SessionLoaded`, `EndFrame` (every frame — a place to poll a command file) | `EditorFramework/LSToolFramework/IGame.cs` |

"Game mode" in this editor is not a separate start: the game is running inside the editor the
whole time, paused. `ResumeGame()` un-pauses client and server
(`ecl::EocClient::SetPaused(false)`, `esv::EocServer::SetPaused(false)`), clears the
"editor mode active" flag and gives the render window the focus; `PauseGame()` does the reverse
and, if the script debugger is paused, continues it first. `ResumeGame()` returns at once when
editor mode is not active.

`LoadSaveGame` and the `-savegameLvl` path do the same thing: take a free network message of
type 181 from the game client, fill it with `eocnet::ServerCommand::Loadgame(name, modSettings)`
and send it to the host.

## 4. Script Extender inside the Toolkit

Not supported. `BG3Extender/Extender/Shared/dllmain.cpp:42`:

```cpp
bool ShouldInitializeExtender()
{
    if (GetModuleHandleW(L"bg3.exe") == NULL && GetModuleHandleW(L"bg3_dx11.exe") == NULL) {
        return false;
    }
```

and `GameHooks/DataLibrariesBG3Game.cpp` registers only those two modules with the symbol mapper.
Even past that check it could not work as it is: every byte pattern in `BinaryMappings.xml` is
written for the game executable, where the engine is one image, while in the Toolkit the same
code is spread over `EoCClient.dll`, `EoCServer.dll`, `GameEngine.dll`, `CoreLib.dll` and others
with different code generation.

The other side of that: in the Toolkit the engine's functions do not need finding. They are
exported by name, so a native DLL inside the Glasses process can call
`GetProcAddress(GetModuleHandle("EoCClient.dll"), "?SetControllerMode@EocClient@ecl@@QEAAXW4EInputDeviceID@ls@@@Z")`
directly. `python symbols.py find NAME --mangled` prints the decorated names. A plugin in the
Toolkit can therefore do natively what in the game needs the addresses in `requests.md`.

**Decision (user, 2026-11-12):** since Script Extender does not support the Toolkit, the GateKit
provides that support itself — its own plugin inside Glasses, not a port of Script Extender.

## A plugin with a file channel, in outline

1. `GateKit.Toolkit.dll`, .NET Framework 4.7.1, references `EditorFramework.dll`,
   `LSFrameworkPlugin.dll`, `EoCPlugin.dll` from the Toolkit folder (reference only, do not copy).
2. An `AppDomainManager` subclass whose `InitializeNewDomain` starts the watcher described in
   section 1.
3. `class GateKitPlugin : LSToolFramework.PluginBase`: in `Start()` subscribe to
   `FrameworkClass.Instance.Game.EndFrame` and, in the handler, look for a command file, run the
   command through the calls of section 3 and write a result file — the same file protocol the
   game bridge uses. The level and game callbacks become events in a state file.
4. The stand starts `Glasses.exe -project <mod> -lvl <level>` with the two environment variables
   set, and listens on UDP 6666 for `LevelLoaded` to know when to begin.
