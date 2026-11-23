# The startup video

What the game shows before its main menu, which code shows it, and what a mod can change there.

## One clip, two logos

The game has one startup video, `Video/Splash_Logo_Larian.bk2` in `Gustav_Video.pak`. It shows
Larian's logo and then NVIDIA GameWorks' (about 3.2 s and 3.4 s, 6.8 s in all). No other startup
video is in the game's paks. Its sound is not in the file: it is the Wwise event
`Splash_Logo_Larian` (bank `VIDEO_INIT`, 4.76 s); which code posts it was not found. The two logos
are one entry of the game's playlist and cannot be hidden apart through the game's own path.

## When and by what

The client shows it in the game state `StartLoading`, which `code:ecl::EocClient::Load` enters
with a playlist of one name, the code constant `strSplashLogoLarian`: the name is not read from
any data file. Entering the state makes one `ecl::LoadStateMovie` per playlist entry;
`code:ecl::LoadStateMovie::Start` calls `code:ecl::GameControl::ShowMovie` (a list of names played
in turn, the same path Osiris `MoviePlay` takes), which fills the client's player
(`ecl::VideoManager`) and calls `code:ecl::VideoManager::NextVideo`. The file is found by
`code:unplaced::FindVideoFile`: each loaded mod from the last to the first
(`<mod>/Localization/<language>/Video/<name>`, built by `code:ls::VideoManager::GetVideoPath`),
then `Video/<name>`. Only `.bk2` is tried. The stage ends when the player stops or the clip is
skipped (the startup clip is skippable); the game then goes on to `Init`, `InitMenu`,
`LoadModule`, `LoadMenu` and `Menu`.

All of this happens before any mod's Lua exists: Script Extender's first Lua event
(`ModuleLoadStarted`) and its runtime log come with `LoadModule`, after the clip's last frame.
A mod's Lua therefore cannot hide, replace or add a startup video; what reads a mod's wishes has
to be native and run before `StartLoading`.

Measured with `gatekit --instance <copy> up --main-menu --startup-videos` and a series of
`gatekit shot` pictures with times against the creation time of Script Extender's runtime log.

## The playlist, and the moment it can be changed

`code:ecl::EocClient::Load` reads `modsettings.lsx` and refreshes the available mods, then makes
the state with `code:ecl::GameStateStartLoading::MakeSimple`. The state carries its playlist at
+0x18: entries `{name, count}` (capacity +0x20, count +0x24), a flat array of every name (+0x28,
capacity +0x30, count +0x34) and the progress total (+0x38). `code:ecl::GameStateMachine::Update`
makes a new state current, calls every listener of the client's state events - Script Extender's
among them - and only then the state's `Enter`
(`code:ecl::GameStateStartLoading::Enter`). At Script Extender's own `StartLoading` callback the
playlist is therefore in place and not yet used: native code there can rewrite it. Only the
first start's `StartLoading` carries the one entry `Splash_Logo_Larian`; the ones of a new game
and of a lobby carry none.

Each entry becomes its own `ecl::LoadStateMovie` in the loading screen's queue
(`code:ls::LoadingScreenManager::QueueLoadingState`; the queue is run by
`code:ls::LoadingScreenManager::Update`); an entry takes the next `count` names of the flat array.
`code:ecl::GameStateStartLoading::CanExit` lets the game go on as soon as the current load state is
ready, so with several entries the game leaves `StartLoading` after the first video and loads
under the next ones (the main menu came 22 s sooner, the videos over the loading screen). Several
names in one entry play in turn through `ShowMovie`'s queue on black, the state is ready when the
player stops, and one key press skips the rest: GateKit's Script Extender build writes the composed
list that way.

Entering the state takes about 35 ms before the first frame (`RefreshPackFiles` included), and
nothing of the client's loading runs while the videos play: the client leaves `StartLoading` in
the tick the last video stops, then `Init`, `LoadModule` and `LoadMenu` follow; the server stays
`Uninitialized` until a session. Read with `Ext.StartupVideos.Timeline()` after a start with
`gatekit --instance <copy> up --main-menu --startup-videos`.

At that moment the mod list is read but the modules are not loaded yet
(`LoadOrderedModules` fills during `LoadModule`), and `code:unplaced::FindVideoFile` searches the
mods' folders only once a base module is set: a startup video is found only at `Video/<name>.bk2`.
The sound follows the file's name: the player posts the Wwise event named as the file without its
extension (read on macOS only), so the empty `Splash_Logo_Larian.bk2` keeps the logo's sound
event and a video of another name brings its own event or none.

`ecl::GameControl::ShowMovie` has no session check and the client's player exists in the main
menu, so the game's own path can show a playlist again without a restart; nothing exposed to Lua
calls it, and Osiris `MoviePlay` needs a session.

## The start, step by step

From the process's creation to the camera at rest on the main menu, each stage marked by a state or an event
of the game (times are one start's, ms since the process began; `Ext.StartupVideos.Timeline()`):

| # | Stage | Marked by | Entered by |
|---|---|---|---|
| 1 | the extender loaded | `ExtenderLoaded` (~0.7-1.9 s) | the process |
| 2 | the application starts | nothing of the game's | - |
| 2a | Script Extender's own start (~2.3-2.7 s of CPU, nothing read) | `Step ScriptExtender::Initialize End` | the extender's symbol mapping, inside the loader |
| 2b | the engine's start (~1.8 s): the game's paks mounted (53 MB), the English and the player's localization, the window, the renderer (~1.1-1.4 s, nothing read), fonts, input, profiles, the graphic settings | `Step` rows | `code:BaseApp::Start`, `code:ls::PackedFileManager::LoadPackFiles`, `code:ls::TranslatedStringRepository::LoadFromContextFolder`, `code:App::LoadAndResaveGraphicsSettings` |
| 2c | the game's start (~0.8-1 s): the server made, the sound (Wwise), both Bink players and the hints loaded (241 MB), then the mod list and every module's `meta.lsx` read | `Step App::InitGame`, `App::InitializeResourceManager` | `code:App::InitGame`, `code:App::InitializeResourceManager`, `code:ecl::EocClient::Load` (its last call) |
| 3 | the server's machine | `Unknown -> Uninitialized` (~4.6-7.2 s); it stays until a session | - |
| 4 | `InitMenu`, the game's interface built (~1-1.4 s) | client state | `code:ecl::EocClient::Load` queues it first through `code:ecl::GameStateMachine::SetTargetState`; `code:ecl::GameStateInitMenu::Enter` |
| 5 | `StartLoading` entered, the loading screen shown | client state, `PlaylistWritten`, `LoadingScreenShown` | `code:ecl::GameStateStartLoading::Enter` (about 35 ms) |
| 6 | the startup videos | `VideoStarted`, `LoadState` of `ecl::LoadStateMovie` | `ecl::LoadStateMovie::Start` |
| 7 | the videos stop or are skipped | `VideoStopped` and `StartLoading -> Init` in one tick | `code:ecl::GameStateStartLoading::CanExit`: the movie's `IsReady` (the player stopped, or its skip flag) and the save manager not saving (`code:ecl::SavegameManager::m_ptr` +0xf9) |
| 8 | `Init` (~0.3-0.9 s) | client state | - |
| 8a | `LoadPsoCache`, on the Vulkan executable (`bg3.exe`) only: one loading segment more | client state | - |
| 9 | `LoadModule` (~9-13 s); the first Lua runs | client state | - |
| 10 | `LoadMenu` (~15-20 s) | client state | - |
| 11 | `StopLoading` (~0.6 s) | client state | `code:ecl::GameStateStopLoading::Exit` empties the loading screen's queue |
| 12 | `Menu`, the loading screen hidden in the same tick | client state, `LoadingScreenHidden` | `code:ecl::GameStateMenu::Enter` creates the main menu's component |
| 13 | the interface's `GE.OnStateMainMenu` | UI event | Keyboard.xaml goes to its Eula state; Eula.xaml, loaded with no first-time setup, raises `OpenSplashScreen` |
| 14 | the "press any key" page | UI state `SplashScreen` pushed | - |
| 15 | the camera's descent onto the menu's scene, on the page's key | the timeline `MainMenu/MM_Accept` (9.5 s) starts: its `eoc::TimelineDataComponent.PlaybackState` 0 -> 2, its scene camera becomes the active one | `ecl::timeline::MainMenuTimelineSystem`, `ecl::TimelineSystem`'s update |
| 16 | the camera at rest, the main menu ready | `MM_Accept`'s `PlaybackState` 2 -> 0 | `ecl::TimelineSystem`'s update |

Stage 2c runs on the game thread: `code:ls::GameThread::DoWork` calls `code:App::InitGame` once,
through the application's virtual table, before its frame loop, and the thread's starter waits for
it. Inside `code:ecl::EocClient::Load`, `modsettings.lsx` is read by
`code:ecl::ModManagerClient::LoadModSettingsFile`, right after `InitMenu` is queued.

The menu's scene is the level `SYS_Menuscreen_Act3_B`, loaded in `LoadMenu`. Its timelines start
under the loading screen, early in `LoadMenu`: `MM_Open`, whose camera stands still in front of the
"press any key" page, and the looping `MM_Open_Boats`. The descent is not a fade and removes no
component; on a return to the menu from a session `MM_Open` starts again in `LoadMenu`, and the
descent plays again on the page's key, once per visit of the page. Read with `Ext.Entity.GetAllEntitiesWithComponent('TimelineData')`, each entity's
`ClientTimelineLoaded.Timeline` and `TimelineData.PlaybackState`, before and after the page's key.

The timeline also records the interface's states (`Loading`, `Movie`, `Eula`, `SplashScreen`,
`MainMenu`) from the interface's update, the client machine's fade phase (`ecl::GameStateMachine`
+0x28: none, fading out, black, fading in) and, on every state row, what the process has read and
the CPU it has used. The heavy part of a start is `LoadMenu` (about 6 GB read). The fade a far
camera move makes (`GK.Camera.Focus`) is another mechanism: it leaves the machine's fade phase
alone, which a save's load moves as a level's start does.

**Nothing loads while the startup videos play.** The client stays in `StartLoading` and the
server in `Uninitialized`; the time from the process's start to the menu grew by the clip's length
(6.3 s on average against 6.8 s, three starts each), and the work after `StartLoading` took the
same time with and without the clip (29.7 s and 29.8 s). During a 60 s clip the process read 4.4 MB
(the clip itself 3.5 MB) on about one core; in the 3.6 s after it, 115 MB on two cores and more.

**The game never cuts a clip short.** Neither the state machine's update nor the loading screen's
has a timer: a 60 s clip held `StartLoading` for 60.0 s and `Init` came in the tick it stopped.
Only the player's stop, a skip key or a save in progress (which holds it longer) decide.

**The start screens come once per process.** Only `code:ecl::EocClient::Load` gives `StartLoading`
a video, and its one caller is `code:App::InitGame`, which runs once: it makes the server, the
entity system, the sound and video players and the hint list; every other maker of the state - a level's or a save's load (`code:Eoc::StartGameInternal`),
a return to the main menu (`code:ecl::EocClient::ReturnToMainMenu`), a new game, a lobby - gives it
none, and a return to the menu from a session reloads no module, then shows the "press any key"
page again (stage 14) but no video. A level's load from a save goes `Running`, `StartLoading`,
`Idle`, `UnloadLevel`, `UnloadSession`, `Idle`, `LoadSession`, `LoadLevel`, `SwapLevel`,
`StopLoading`, `PrepareRunning`, `Running` (about 8 s), with the screen faded out and in by the
client machine's fade field (0 none, 1 fading out, 2 black, 3 fading in).

## The loading screen

One widget, `Public/Game/GUI/Widgets/LoadingScreen.xaml`, shown by the interface's `Loading`
state; its view model is `gui::DCLoadingScreen`. The percentage is the loading screen manager's
segments: `code:ecl::GameStateStartLoading::Enter` sets how many (`ProgressTotal`),
`code:ecl::GameStateMachine::Update` counts one done each time it leaves a loading state, and
within a segment the state's ticks (`code:ecl::GameStateLoad::TickLoad`) give the fraction;
`code:ui::DCLoadingScreen::Update` turns them into the value the XAML prints. It measures segments,
not bytes or time. A state counts as a segment when its virtual "is a load" answers true: `Init`,
`LoadModule`, `LoadMenu` and every `GameStateLoad`; `InitMenu`, `StartLoading`, `StopLoading`,
`Menu`, `Idle` and `StartServer` do not; on the Vulkan executable `LoadPsoCache` counts too, and
the total is one more. The first start counts three segments against a total
of two, so the bar stands at 100% through the whole of `LoadMenu` (`ls::LoadingScreenManager`
read every tick from `LoadModule` on). The spinner beside it is the shared `BusyElement` template, an image turning
once a second, bound to nothing.

The wallpaper is chosen by the XAML, not by code: `code:gui::DCLoadingScreen::OnGameStateChanged`
gives it the level being loaded and a loading state (initial, character creation, lobby,
tutorial, level), and the XAML maps Act 1, Act 2 and Act 3 levels to sets of images shown in a
shuffled slideshow (`code:gui::LSImageSequence::OnLoaded`, 20 s each), and the start, the creche,
the tutorial and character creation to one image each. The tips are the entries of
`Public/Game/Hints/Hints.lsx`, read once when the engine starts (`code:App::InitializeResourceManager`,
`code:ecl::HintManager::Visit`), before any mod's Lua; every 10 s the widget asks for another, and
`code:gui::DCLoadingScreen::OnAdvanceHintCommand` draws one at random that suits the keyboard or the
controller - the same pool in every act.

The list is loaded through the game's general file loader: `code:App::InitializeResourceManager`
empties the manager and calls `code:ls::FileFormatIO::LoadFile` on it with the hint file's path;
the loader opens the file as LSX or LSF and hands it to the manager's `code:ecl::HintManager::Visit`,
which replaces the list with the file's entries (`ecl::Hint`, one per node: its key, its text, the
keyboard and controller flags, a wiki page, an internal flag). A second load into the same manager
replaces the list again. The manager's global stays readable from Lua in the main menu (its count
and each entry's flags match the file's nodes in order). The index of the hint shown is not kept:
`code:gui::DCLoadingScreen::OnAdvanceHintCommand` draws it and passes the entry's text on.

What the screen shows for each destination (the view model's `LoadingState` and `LevelName`, read
live; 9 triggers give 7 looks):

| Destination | LoadingState | LevelName | Wallpaper |
|---|---|---|---|
| the first start | Initial | - | the initial art |
| a new game's character creation | CharacterCreation | `SYS_CC_I` | the key art |
| the prologue | Tutorial | `TUT_Avernus_C` | the tutorial art |
| the creche | Level | `CRE_Main_A` | the creche art |
| Act 1, Act 2 | Level | `WLD_Main_A`, `SCL_Main_A` | that act's slideshow |
| Act 3, and every level the XAML does not name | Level | `BGO_Main_A`, `CTY_Main_A`, ... | the Act 3 slideshow |
| a return to the main menu | that of the level left | the level left | that level's |
| a lobby (by the code; not seen) | MultiplayerLobby | a `SYS_Menuscreen_*` level | the key art |

The widget is made anew at every load, and each slideshow starts a new shuffle when it is loaded
(`code:gui::LSImageSequence::OnLoaded`); each next image is set by its storyboard's clock, with no
code of the game run for it.

A mod changes the screen without replacing a file through the game's own merge of mods' interface
(`code:ui::ModManager::RegisterMods` and the calls after it in `code:gui::GameUI::UpdateFromUI`), at
the end of `LoadModule`; the loading screen made earlier keeps the game's look until `LoadMenu`
makes it anew, and GateKit's Script Extender build runs the same merge at the first `StartLoading`.
(Taken back: this merge was named after the macOS build's `ProcessLoadingModsFromUI`; on dx11 that
has no function of its own, its body is inside `UpdateFromUI`, and it runs before `LoadMenu`.)
- a key redefined in the mod's `GUI/Resources/Resources.xaml` replaces the game's: `BusyElement`
  (the spinner of every waiting widget) does; the wallpaper templates do not, being local to the
  widget's own template;
- a state `Loading` with `ModType="Extend"` in the mod's state machine adds the mod's own page over
  the screen, bound to the same view model (`LoadingState`, `LevelName`, `Progress`): a mod's own
  wallpapers per destination and its own progress indicator;
- the mod's own images are shown once listed in its `GUI/metadata.lsf`; vector paths, gradients and
  storyboards render (a `Polygon` does not).

The main menu's scene is a level named by a module's `meta.lsx` (`MenuLevelName`; the last loaded
module wins, `code:ls::ModManager::GetMenuLevel`): a mod that names another level gets that scene,
and the splash page, the menu and loading a save work on it. The menu's timelines are bound to the
menu's events (open, accept, new game) by the level's own cinematic triggers, and
`code:ecl::timeline::MainMenuTimelineSystem::LEGACY_RequestMainMenuTimline` asks for one without
waiting for it: nothing of the menu waits for the camera's descent.

The studied mods change the loading screen only by shadowing those files (Better Hints, Goon's Loading Hints,
Aether's loading screen, Immersive Loading Screens): images by path, the XAML whole, the hint list
whole, with its own localization. Two such mods do not merge: the last file wins, and two hint
mods with the same handles fight over the localization. GateKit's Script Extender build loads
each enabled mod's own `Public/<Folder>/Hints/Hints.lsx` through the same
`code:ls::FileFormatIO::LoadFile` at the first `StartLoading`, before any hint is drawn, and
composes the entries into the game's list by `HintKey` (its `Ext.Hints`).

## What a mod can change at the start, and when

What the game reads from its start to the main menu falls in three groups (the timeline's
`FileOpened` rows before the first state, then the module loader's steps):

- **Merged per module by the game**: localization (a later handle wins), stats, root templates,
  resource banks, sound banks, texture atlases, virtual textures, speakers, effects, and the
  interface's states (a mod's state machine marks a state `ModType="Extend"` to add to it or
  `"Override"` to replace it). Two mods compose here without anything of ours.
- **Keyed lists read once from one path**: the hints (`Public/Game/Hints/Hints.lsx`), the sound
  preload list (`ls::SoundPreloadConfig`, a `ls::FileFormatIO` like the hint manager), the input
  configuration, and the single files the game finds with `code:ls::ModManager::GetDataFile`
  (the last module that has the path wins). A mod can only replace such a file whole.
- **Whole documents and images**: the widgets' XAML, images, fonts, cursors, shaders, videos -
  replaceable by path only (the pak's priority, `GetDataFile`'s search from the last module).

The game's own redirects of a path are the pak's priority and that search; it has no alias table.

The hint list is read once per process (`Hints.lsx` is never read again; a list shortened in
memory in the main menu was the one drawn from in two later loads), and the loading screen draws
from the list in memory every 10 s, so a change of the list from Lua is seen at the next draw -
but the first start's loading screen draws before any Lua exists. The wallpapers' sets are hard
lists in `LoadingScreen.xaml`, one `ls:LSImageSequence` per act with its images in its own
`Resources`; the slideshow collects only those (`code:Noesis::ResourceDictionary::EnumKeyValues`
does not walk merged dictionaries), so a mod that adds one image must ship the whole XAML, and of
two such mods the last wins.

## How fast a clip plays

The game's pump, `code:bik::BinkManager::Update`, runs once per drawn frame: it returns while
`code:unplaced::BinkWait` says the next frame is not due, and shows at most one frame. Its catch-up loop
(`code:unplaced::BinkShouldSkip`) never drops a frame of the game's clips: they open with no Bink sound
track (the sound is Wwise), and Bink skips only with one. A clip therefore lasts at least its
frames divided by the copy's drawn frames per second, and at least its own length.

`BinkWait` reads its clock at every call: the due time is the frames since its base times
`FrameRateDiv`·1000/`FrameRate`, scaled by Bink's 16.16 speed factor, against Bink's timer since
its start. Both the rate in the handle and the factor change a playing clip (the factor doubled,
a 5 s clip took 10 s); the rate is also what the game's sound seek at the clip's middle frame uses,
the factor is not. Faster than the drawn frames no lever goes. The game's speed does not change a
clip; a lower frame cap slows it.

The catch-up loop can be switched on for a clip that is playing: sound on (`BINK` +0x40) and one
track (+0x12C) written after `Play`, with the speed factor small, the pump moves five frames per
drawn frame (Bink drops at most four in a row), and a 5 s clip played in under 2 s. The track
behind +0x1B8 was never made, so the fields must be put back, and the clock restarted (+0x644 = 0),
before the clip closes: a clip closed with them left killed the game, and by the code a pause, a
skip or a close while they are set does the same. The game's sound seek at the middle frame is
mostly jumped over. It is a cheat with a crash on the wrong moment, not a way the game plays. `code:unplaced::BinkGoto` is called by the game only to start a clip: the
load moves to frame 2, `Play` back to frame 1 and restarts the clock.

## The "press any key" page

After the menu loads, the UI state `SplashScreen` shows the "press any key" page; it holds no
video. `SkipSplashScreen` (`ls::GlobalSwitches`, copied into the page's view model
`gui::VMGameData`) closes that page and nothing else: set at the page, the main menu opens at once.

## The first launch

The game shows its first-launch pages while the global switch `ShowFirstTimeSetup` is on. It is
loaded from the profile's `config.lsf` (`code:App::CopyValuesFromConfig`) and turned off only when
the main menu's page loads (`code:gui::DCMainMenu::OnCheckFirstTimeSetupValueCommand`, which also
clears the calibration flags and writes `config.lsf` and the graphic settings), so a player who
quits before the main menu sees the pages again. Set from Lua at the first Lua
(`Ext.Utils.GetGlobalSwitches().ShowFirstTimeSetup = true`), the same start shows them.

The pages are states of the interface under `GameRoot`, each entered and the previous one left in
one tick:

| Page | View model | What it changes |
|---|---|---|
| `Eula` | `gui::DCWidget` | nothing; with the switch off it raises `OpenSplashScreen` on loading, else `AcceptBtn` raises `OpenSensitiveSettings` |
| `SensitiveSettings` | `gui::DCFirstTimeSetup` | the explicit-content choice (`SENSITIVE_CONTENT`): "No" turns `ShowGenitals` and `ShowExplicitRomanceScenes` off and writes `config.lsf` on `NextButton`, which raises `OpenHDR` with an HDR display, else `OpenGammaCalibration` |
| `HDRCalibration` | `gui::DCHDRCalibration` | the HDR display's calibration. The first slider sets `HDRMaxNits` in `graphicSettings.lsx`; `AcceptButton` advances to brightness and contrast, then opens the splash screen |
| `GammaCalibration` | `gui::DCGammaCalibration` | the slider (0.5-1.5) sets `Gamma`; `AcceptButton` writes `SDRGamma` and raises `OpenSplashScreen` |
| `SplashScreen` | - | any key raises `OpenMainMenu` |

`ShowGammaCalibration` in `graphicSettings.lsx` opens the gamma page over the main menu without the
first-launch switch; there its `AcceptButton` closes the page. The mod policy page
(`ModBrowserSplashScreen`) shows while `WasLarianModPolicyAccepted` is off, which only its accept
command turns on (`code:gui::DCModBrowser::GetShowModManagerSplash` reads it).

## How the startup video is skipped

Without code, the only way is a file at the name the game looks for: an empty
`Data/Video/Splash_Logo_Larian.bk2`, a short black clip, or the same path in an override pak.
Every such mod writes the same path, so the last one wins. GateKit's Script Extender build hides
every startup video with its own switch (`StartupVideos`), which `gatekit up` sets; it writes no file.

Without the menu, a level is reached by the game's own `-testLoadLevel` (as `up --level` passes it):
`code:ecl::EocClient::Load` starts the game on that level directly, and the client never loads the
menu's level. A save's load (`-loadSaveGame`) is requested in `Init` and started from `Menu`, so it
pays `LoadMenu` first; the argument is read in the system's ANSI code page, so a save whose folder
name holds other letters is not found by it.
