# Lua API contract: index and split proposal

Source: the old repo's bridge mod, `stand/bridge/Mods/Gwynerva_GateKit/ScriptExtender/Lua/`
(read 2026-11-13, derived from code, not from docs). Sections 1-6 describe that old code; the
split into `mods/GateKit` and `mods/Weave` is done, and section 7 says what changed. Annotations: `lua-types/GateKit.lua`
(global `GK`) and `lua-types/Weave.lua` (the Weave kit; `GK.Weave` in the old code).

Sides: **S** server Lua state, **C** client Lua state, **UI** client code that must run inside
`Ext.UI.Defer`, **both** loaded into both states. The two states are separate Lua worlds; a file
that exists on both sides (`GK.Focus`) is two different tables with the same name.

## 1. Globals the mod defines

| Global | Defined in | Notes |
| --- | --- | --- |
| `GK` | `Shared/Bridge.lua` (`GK = GK or DK or {}`) | the API table; every other file adds to it |
| `DK` | `Shared/Bridge.lua` (`DK = GK`) | former name, same table; to be removed (docs/rename-gatekit.md). `PREFIX` strings still define `local DK = GK` for console code |
| `GK.Weave` | every `Client/Kit/*.lua` (`GK.Weave = GK.Weave or {}`) | Weave hangs off `GK`; becomes its own global in the split |
| `GK.UI` | `Client/Panels.lua` | NOT Weave: the "panels of other mods in the console" API. Easy to confuse with `GK.Gui` (game windows) and `GK.Weave` |
| `GK.Log` | `Shared/Bridge.lua`, only while `GK.Run` runs a chunk | per-call collector, not a stable function |
| `GK.LogContext` | set/cleared by BootstrapServer and Console | attribution of console output |
| `Mods.GateKit.PersistentVars` | `Server/SaveState.lua` | key `GKState` (version 1) holds the bridge's memory in saves |

No other bare globals are assigned (checked by scanning every top-level assignment). Console commands
registered: `dk` (`!dk` toggles the console; the file header says `!gk` but only `dk` is registered),
`gkui`, `gkrescue`. Net channels (all on the mod's `ModuleUUID`): `GK_Run`, `GK_Result`,
`GK_Complete`, `GK_Completions`, `GK_Reset` (console <-> server), `GK_Log`, `GK_Echo` (log relay),
`GK_Focus` (server -> client outline commands). File channel for the stand:
`Script Extender/Gwynerva_GateKit/{queue,res,state,cmd}_<side>.*`.

Load order (from `BootstrapServer.lua` / `BootstrapClient.lua`):
- Server: Shared/Bridge, Events, ConsoleLog, Complete, Server/Api, Stage, Speed, Situation, Conditions, Act, Cheat, Heard, GameLog, Focus, SaveState, then `GK.Start("server")`.
- Client (one file, 27 lines): Shared (same four), Client/Api, Sound, UI, Stage, UICatalog, Windows, WindowReaders, Popups, Camera, Places, Focus, `GK.Start("client")`, then Kit/Xml, Css, Reactive, Expr, BackendImgui, Dom, Component, Utilities, Layout, then Overlay, Console, Panels.

## 2. Per file: public names, side, dependencies

"Uses" lists other bridge files (through `GK.*`) whose names the file calls. Counts are annotated
functions in the type files.

### Shared (both)

| File | Public names | Uses |
| --- | --- | --- |
| `Shared/Bridge.lua` | `GK`, `DK`, `GK.Dump`, `GK.Run`, `GK.Format`, `GK.AutoSave`, `GK.Start` (the file channel of the stand), `GK.Log` (transient) | none |
| `Shared/Events.lua` | `GK.Note`, `GK.Events` (journal of 500 entries; subscribes GameStateChanged) | none |
| `Shared/ConsoleLog.lua` | `GK.LogSinks`, `GK.Features`, `GK.CutUtf8`, `GK.ShortTrace`, `GK.ConsoleEmit`, `GK.FlushLog`, `GK.EchoStats` (debug) | reads `GK.LogContext` |
| `Shared/Complete.lua` | `GK.Complete`, `GK.CommonPrefix` | `GK.CutUtf8` |

### Server (S)

| File | Public names | Uses |
| --- | --- | --- |
| `Server/Api.lua` | `GK.Item`, `Character`, `Party`, `Give`, `Find`, `Equip`, `Unequip`, `Unsheath`, `Teleport`, `Spawn`, `Dialogs`, `StartDialog`, `Remove`, `Watch` (and registers 11 Osiris journal listeners) | Shared/Events (`GK.Note`) |
| `Server/Stage.lua` | `GK.Stage` (+ `.Scene`, `.Templates`, `.Factions`, `.TriggerKinds`, about 55 functions) | Api (`Find`, `Dialogs`, `StartDialog`, `Watch`) |
| `Server/Speed.lua` | `GK.Speed` (`Enable`, `Disable`, `Set`, `Get`, `Boost`, `Rule`, `State`, `Profile`) | none (engine global switches) |
| `Server/Situation.lua` | `GK.See` (about 45 members) | late-bound `GK.Act`, `GK.Stage`, `GK.Conditions`; Osiris/engine |
| `Server/Conditions.lua` | `GK.Conditions` (`Eval`, `Check`, `Functions`) | `GK.See` |
| `Server/Act.lua` | `GK.Act` (about 45 functions + job table) | `GK.See`, `GK.Focus`, `GK.Note`, `GK.Conditions`, `GK.Stage` |
| `Server/Cheat.lua` | `GK.Cheat` (29 named cheats + `List`, `Used`, `Reset`, `Log`) | `GK.Stage`, `GK.Teleport`, `GK.Give`, `GK.StartDialog` (late-bound wrappers) |
| `Server/Heard.lua` | `GK.Heard` (`Since`, `Poll`, `Start`, `Stop`) | `GK.See` |
| `Server/GameLog.lua` | `GK.GameLog` (`Since`, `Kinds`, state tables) | `GK.See` (fairness filter) |
| `Server/Focus.lua` | server half of `GK.Focus` (`Show`, `Glance`, `Clear`, `SetEnabled`, `State`, `JobStarted`, `JobFinished`, settings) | `GK.See`, called by `GK.Act`; sends channel `GK_Focus` |
| `Server/SaveState.lua` | `GK.SaveState` (`Snapshot`, `Write`, `Restore`, `Info`) | `GK.See`, `GameLog`, `Heard`, `Act`, `Cheat`, `Focus` (reads/writes their state tables); `Mods.GateKit.PersistentVars` |
| `BootstrapServer.lua` | channels `GK_Run`, `GK_Complete`, `GK_Reset`; `GK.Start("server")` | `GK.Run/Format/ShortTrace/CutUtf8/FlushLog/Complete` |

### Client (C / UI)

| File | Public names | Uses |
| --- | --- | --- |
| `Client/Api.lua` (C) | `GK.Client`, `Scenes`, `Tracks`, `FastScenes`, `ScanCode`, `Key`, `Shot`, `ShotPending`, `FrameCap`, `SetFrameCap` | none |
| `Client/Sound.lua` (C) | `GK.Sound` (`Categories`, `Category`, `List`, `Get`, `Set`, `Master`, `Mute`, `Unmute`, `Muted`, `WhenUnfocused`, `Settings`) | none (`Ext.Audio`, engine global switches) |
| `Client/UI.lua` (UI) | `GK.ViewModel`, `ViewModels`, `Properties`, `Dialog`, `Answer`, `Commands`, `Command` | none |
| `Client/Stage.lua` (UI) | `GK.Blocking`, `RollNext`, `BoxPress`, `Unblock`, `Autoplay`, `AutoplayInfo`, `Controls`, `Press` | `GK.ViewModel`, `GK.Key`; line 13 `local GK = GK or Mods.GateKit.GK` |
| `Client/UICatalog.lua` | `GK.UICatalog` (generated data, do not edit) | none |
| `Client/Windows.lua` (UI) | `GK.Gui` core: `Widget`, `Data`, `Open`, `Page`, `IsOpen`, `Event`, `Show`, `Close`, `CloseAll`, `Find`, `Controls`, `Click`, `Select`, `Input`, `Run`, `Snapshot`, `Read`, `Step`, `Auto`, `Item`, `ItemDo`, `Quiet`, `Stuck`, `Recover`, `List`; tables `Hud`, `Events`, `Windows` | `GK.UICatalog` |
| `Client/WindowReaders.lua` (UI) | no new functions; adds `.read` to `GK.Gui.Windows` entries | `GK.Gui` |
| `Client/Popups.lua` (UI) | `GK.Gui.Blocking`, `Resolve`, `DefaultPolicy`, `Trade`, `LongRest`, `QuickSave`, `QuickLoad`, `SaveAs`, `LoadLatest`; adds blocking-window entries (dialogue, roll, reaction, box, tutorial, loot, trade, pickpocket, rest, reward, learn, split, book, gamemenu, savename) | `GK.Gui` |
| `Client/Camera.lua` (C) | `GK.Camera` (`State`, `Look`, `Face`) | none |
| `Client/Places.lua` (UI) | `GK.Places` (`Zone`, `Markers`) | `GK.ViewModel` |
| `Client/Focus.lua` (C) | client half of `GK.Focus` (`SetCalm`, `State`, `Outline`, `ClearOutlines`); channel `GK_Focus` | none |
| `Client/Overlay.lua` (C) | `GK.Overlay` (`Get`, `OnChange`, `Register`), `GK.OverlaySet`, `OverlayState`, `OverlayRescue`, `OverlaySettings`; **also defines `GK.Weave.Demo`**; console commands `gkui`, `gkrescue`; key/mouse input blocking; the HUD button | **`GK.Weave`** (Mount, AnyOpen, CloseTop, Themes, Fonts, SetTheme, SetFont, Has, ResetWindows, Eject), `GK.ToggleConsole`, files `Kit/ui/overlay.weave`, `hud.weave`, `demo.weave` |
| `Client/Console.lua` (C) | `GK.ConsoleAsk/Run/Watch/Unwatch/Watches/Action/Reload/Events/Log/Output/State/Set/Submit/Tab/Copy/History/Clear`, `GK.ToggleConsole`, `GK.ConsoleHooks`, `GK.LogRetracted`; console command `dk` | `GK.Overlay`, `GK.OverlaySet/Settings`, **`GK.Weave`** (`Color`, `Skin`, `OnRestyle`, `KeepOnScreen`), Shared log/complete, channels to the server; `PREFIX` with `Mods.GateKit.GK` |
| `Client/Panels.lua` (C) | `GK.UI` (`Panel`, `Remove`, `Open`, `Panels`, `Press`, `Read`), builder methods `SameLine`, `Text`, `Separator`, `Button`, `Toggle`, `Input`, `Select`, `Values`, `Watch` | `GK.ConsoleAsk`, `ConsoleEmit`, `ConsoleHooks`, `ConsoleAction`, `ConsoleSet`, `CutUtf8`, `ShortTrace` |
| `BootstrapClient.lua` | `GK.Start("client")` | |

### Weave kit (C), `Client/Kit/`

| File | Public names (on `GK.Weave`) | Uses |
| --- | --- | --- |
| `Xml.lua` | `ParseXml` | none |
| `Css.lua` | `ParseColor`, `ParseGradient`, `ParseLength`, `ParseCss`, `ParseSelectors`, `ParseInline`, `Matches`, `Cascade`, `Resolve`, `Holds`, `Substitute`, `ChildrenOf`, `RegisterPseudo`, `RegisterFont`, `RegisterProperty`, `Fonts`, `FontSizes`, `BaseFontSize`, `FontSizeFor`, `ParseTracks`, `Kinds`, `FontFiles` | none |
| `Reactive.lua` | `reactive`, `raw`, `untracked`, `effect`, `stop`, `computed`, `watch`, `flush`, `pending` | none |
| `Expr.lua` | `transpile`, `Globals`, `scope`, `compile`, `eval`, `parseFor` | none |
| `BackendImgui.lua` | `Backend` (about 60 renderer functions: the seam) | `Internal.report/splitList` from Dom |
| `Dom.lua` | `Version`, `Mount`, `Document`, `Documents`, `AddRoot`, `Define`, `Source`, `Eject`, `Has`, `Themes`, `Var`, `Color`, `Skin`, `OnRestyle`, `AnyOpen`, `CloseTop`, `KeepOnScreen`, `ResetWindows`, `SetTheme`, `Theme`, `SetFont`, `Font`, `Dump`, `Fire`, `RegisterTag`, classes Element and Document | Css, Backend, optionally `GK.ShortTrace`; **hard-coded** `Gwynerva_GateKit/ui/` and `Mods/Gwynerva_GateKit/.../Client/Kit/ui/` |
| `Component.lua` | `RegisterComponent`, `Bindings`, `SameTree`, `Internal.*`, script environment (`computed`, `watch`, `effect`, `props`, `state`, `emit`, `onMounted`, `onUnmounted`, `useStorage`, `el`, `doc`, `t`) | Expr, Css, Dom, optionally `GK.ShortTrace`; **hard-coded** `Gwynerva_GateKit/storage/` |
| `Utilities.lua` | `RegisterUtility`, `RegisterVariant`, `Utilities`, `ApplyUtilities`, `UtilitySheet` | Css |
| `Layout.lua` | `RegisterLayout`, `LayoutModes`, `Stretchable`, `StretchableY`, `LayoutWidth` | Dom/Backend |
| `ui/*.weave`, `*.css` | built-in components and sheets: `demo.weave`, `demo-card.weave`, `overlay.weave`, `hud.weave`, `theme.css`, `utilities.css`, `weave.png` | `demo.weave` calls `GK.OverlaySettings`; `overlay.weave` expects the env `Overlay.lua` passes |

Weave kit file order matters (Xml, Css, Reactive, Expr, BackendImgui, Dom, Component, Utilities, Layout);
`BackendImgui.lua` calls Dom helpers lazily because it loads first.

## 3. Internal (not part of the contract, not in the type files)

- **Shared:** `Bridge.lua` locals `toJson`, `plain`, `gameState`, the queue/cmd readers and `run/dispatch` inside `GK.Start`; `Complete.lua` `root`, `keysOf`, osiris listing; `ConsoleLog.lua` echo/dedupe/relay locals (`join`, `claimed`), `GK.EchoStats`.
- **Server/Api:** `SLOTS`, `try`, `host`, `displayName`.
- **Server/Stage:** all `local` helpers (`try`, spawn/equip plumbing, level/trigger scans). `Stage.Scene` is state (exposed so reloads keep it).
- **Server/Situation (`GK.See`):** every `local` (round, rpos, facingOf, nameOf/posOf are exposed as `See.Name`/`See.Position`; `entityOf`, `has`, `useKinds`, `passivesOf` internal); `See.PartyPerceives`, `See.IsMember`, `See.Classify`, `See.CheckKey`, `See.AbilityOf`, `See.CheckHandles` are exposed aliases of locals, kept in the contract only where the annotation lists them.
- **Server/Act:** `try`, `bool`, `now`, `fail`, `newJob`, `finish`, `view`, `resolveWho`, `needPerceived`, `planWalk`, `spellRow`, `tick`, `inCombat`, `hisTurn`, `Act.On` handlers and the Osiris listeners, `Act.JobList/NextJob/Ordered/StuckCache/Registered/Tick/ThreatNote/CarriedBy` (exposed state / late-bound hooks, not for callers).
- **Server/Conditions:** the parser (`compile`, `eval`, `conjuncts`, `deparse`); `C.Functions` is exposed but is a table of modelled condition functions, extend it only inside the file.
- **Server/GameLog, Heard:** `record`, `add`, `name`, `bare`, `questTitle`, `Log.On`, `Log.Registered`, `Log.QuestText`, `Heard.Seen`.
- **Server/Speed:** `clamp`, `write`, `read`, `apply`, `listen`, `poll`, `update` (exposed as `Speed.Update`), `Speed.Status` (state).
- **Server/Focus:** `try`, `hostile`, `isObject`, marker plumbing; `Focus.Channel`, `Focus.Generation`, `Focus.Current`.
- **Server/SaveState:** `vars`, `levelName`, `numeric`, `count`, `remembered`.
- **Client/Api, UI, Stage:** `uuid`, `walk`, `typeName`, `text`, `commandSource`, `properties`, tick handler of `GK.FastScenes`, autoplay tick (`GK.AutoplayState` is state, exposed).
- **Client/Windows, Popups, WindowReaders:** `guard`, `typeOf`, `contentRoot`, `widgets`, `eventSource`, `get`, `path`, `count`, `item`, `reader`, `stepper`, `runOn`, `gameCommand`, `blockingWindows`, `CHOICES`, `FIRST_LAUNCH`, the per-window `read`/`step` functions (reachable as `GK.Gui.Windows.<name>.read/step`; the registry is public, the individual readers are not listed), `GK.Gui.PressBox`, `GK.Gui.Text`, `GK.Gui.AutoState`, `GK.Gui.QuietState`.
- **Client/Camera, Places, Focus:** `behavior`, `angleOf`, `shortest`, `get`, `text`, `player`, `setOutline`, `setPicking`, `setCursorText`, `onTick`, `pulseTick` (`Focus.PulseTick`); the Camera tunables are public fields.
- **Client/Overlay:** `readJson`, `apply`, `known`, `syncSettings`, `buildSettings`, `anyOpen`, `closeTop`, HUD placement (`findNamed`, `number`, `measureMinimap`, `placeHud`), key capture, Tick/KeyInput/MouseButtonInput subscriptions.
- **Client/Console:** everything not prefixed `GK.` (`build`, `applyTab`, `submit`, `tint`, `setFont`, `showOverlay`, `pollEvents`, `buildEventsTab`, log storage, filters, completion plumbing). `GK.LogRetracted` is a hook set by the console for ConsoleLog; treat as internal.
- **Client/Panels:** `show`, `literal`, `report`, `act`, `current`, `place`, `register`, `construct`, `destroy`, `find`; `Builder` is the object passed to builders (annotated as `GK.PanelBuilder`).
- **Weave kit:** `Weave.Internal` (`I.*`: `loadComponent`, `changed`, `mountRoot`, `unmount`, `buildNodes`, `report`, `splitList`, `varsFor`), `Weave.OnFrame`, `Weave.Resolve`, `Weave.Cascade`, `Weave.Substitute`, `Weave.Holds`, `Weave.ChildrenOf`, `Weave.ParseSelectors`, `Weave.ParseInline`, `Weave.ParseGradient`, `Weave.ParseTracks`, `Weave.Kinds`, `Weave.UtilitySheet`, `Weave.Utilities`, `Weave.ApplyUtilities`, `Weave.SameTree`; `Element:_relabel/_restyle/_draw`, `Document:_resize/_place/_capture/_build/_sheets/_load/_rebuild/_restyleAll`; every `Backend.*` function (they are the renderer seam, annotated only as `WeaveBackend` with the four entry points other code reads). The type file lists the engine-level parsers (`ParseXml`, `ParseCss`, `ParseColor`, `ParseLength`, `Matches`, `FontSizeFor`, `BaseFontSize`, `compile`, `eval`, `scope`, `transpile`, `parseFor`) as "usable on their own"; treat them as advanced, version-4 surface.

## 4. Proposed split

### mods/Weave (UI framework for other mods, client only)

- `Client/Kit/Xml.lua`, `Css.lua`, `Reactive.lua`, `Expr.lua`, `BackendImgui.lua`, `Dom.lua`, `Component.lua`, `Utilities.lua`, `Layout.lua`.
- Built-in resources: `ui/theme.css`, `ui/utilities.css`, `ui/demo.weave`, `ui/demo-card.weave`, `ui/weave.png`.
- From `Client/Overlay.lua`: `Weave.Demo` (move next to the kit).
- Needs a small bootstrap of its own: the kit currently needs only `Ext`, no other GateKit file. Everything it reads from `GK` is `GK.ShortTrace` (soft, with a fallback) and the namespace itself.

### mods/GateKit (dev bridge)

- `BootstrapServer.lua`, `BootstrapClient.lua` (which should `Ext.Require` the Weave mod's files or depend on it).
- All of `Shared/` (Bridge, Events, ConsoleLog, Complete).
- All of `Server/` (Api, Stage, Speed, Situation, Conditions, Act, Cheat, Heard, GameLog, Focus, SaveState).
- Client: `Api`, `UI`, `Stage`, `UICatalog`, `Windows`, `WindowReaders`, `Popups`, `Camera`, `Places`, `Focus`, `Console`, `Panels`, and (after decoupling, below) `Overlay`.
- `Kit/ui/overlay.weave` and `Kit/ui/hud.weave` (GateKit's own Weave components, mounted by Overlay.lua).

## 5. Coupling that blocks or complicates the split

1. **Weave hangs off `GK`.** Every Kit file starts `GK.Weave = GK.Weave or {}`; with GateKit absent `GK` is nil and loading fails. Weave must define its own global (`Weave`), and `GK.Weave` can remain as an alias set by GateKit. All consumers (`Overlay.lua`, `Console.lua`, `demo.weave`, docs, the stand's Python) use the `GK.Weave.*` spelling.
2. **`Client/Overlay.lua` is half Weave, half GateKit.** It owns theme and font choice and persists them (`overlay.json`), the open/close key, input blocking (`Weave.AnyOpen` / `Weave.CloseTop` decide when the game gets no keys or clicks), the HUD button and the settings window (`overlay.weave`), and it defines `GK.Weave.Demo`. If Weave is split out without it, other mods' Weave windows would not block game input, would not close on Esc and would have no theme/font setting. Recommended: move theme/font persistence, input blocking, the rescue function and `Demo` into Weave; keep the key, the HUD button and the console handle in GateKit and let Weave expose hooks (`AnyOpen`, `CloseTop`, `ResetWindows`, `OnRestyle` already exist; a registration for "windows that count as open" is `GK.Overlay.Register`, which would need to become `Weave.Overlay.Register`). `GK.Overlay` is used by `Console.lua` (`Get`, `Register`) and annotated under GateKit for now.
3. **GateKit depends on Weave (one way), and the dependency is hard.** `Console.lua` uses `GK.Weave.Color`, `Skin`, `OnRestyle`, `KeepOnScreen` at load time (line 225, 1043, 1287, 1382) to theme a hand-built IMGUI window; `Overlay.lua` mounts `overlay` and `hud` documents and reads `UI.Themes()`, `UI.Fonts` at load time (`FONT_LABELS` loop). GateKit therefore cannot load without Weave. A split with an optional Weave would need guards around these (the console is *not* a Weave component, it only borrows the theme).
4. **Weave reaches back into GateKit's folder names.** `Dom.lua` hard-codes the user folder `Gwynerva_GateKit/ui/` and the built-in root `Mods/Gwynerva_GateKit/ScriptExtender/Lua/Client/Kit/ui/`; `Component.lua` hard-codes `Gwynerva_GateKit/storage/`; `demo.weave` references `Mods/Gwynerva_GateKit/.../Kit/ui/weave.png` and calls `GK.OverlaySettings`. All must be parameterised or renamed for the new mod folder; the user's editable copies in `Script Extender/Gwynerva_GateKit/ui/` would be orphaned by a rename (decide: keep the folder name, or migrate).
5. **`GK.ShortTrace` is a soft dependency of Weave** (`Component.lua:699`, `Dom.lua:570`, guarded by `GK.ShortTrace and ...`): safe, but Weave then prints raw tracebacks when GateKit is absent. Give Weave its own trimming.
6. **Console's built-in `PREFIX` and other code hard-code the mod table name**: `"local GK = Mods.GateKit.GK or Mods.GateKit.DK; local DK = GK"` in `BootstrapServer.lua`, `Client/Console.lua`, `Client/Panels.lua`; `local GK = GK or Mods.GateKit.GK` in `Client/Stage.lua` and `Server/Stage.lua`; `Mods.GateKit.PersistentVars` in `Server/SaveState.lua`. These names change with the mod's folder/module name; the header of `Panels.lua` also documents `Mods.GateKit.GK.UI` as the entry point for other mods. A Weave consumer would instead use the Weave mod's table.
7. **`GK.Overlay.Register` is how the console takes part in input blocking**; if Overlay moves into Weave the console must register with Weave, which makes GateKit's console depend on that Weave API.
8. **Two `GK.Focus` tables** (server markers, client calm/outlines) joined only by the `GK_Focus` net channel and a shared name; the annotation file models them as one class with side notes. Consider `GK.Focus` (server) and `GK.FocusView` (client) in the new code.
9. **Late-bound cycles on the server**: `See` calls `Act`, `Stage`, `Conditions`; `Act` calls `See`, `Focus`, `Stage`, `Conditions`; `Focus` calls `See` and is called by `Act`; `SaveState` reads the state tables of six modules. They are all `GK.X` lookups at call time, so file order is not a constraint, but they must stay in one mod (GateKit); no split among them.
10. **Name overload** `UI`: in `Overlay.lua` the local `UI` is `GK.Weave`, in `Windows.lua`/`Popups.lua`/`WindowReaders.lua` it is `GK.Gui`, and the public `GK.UI` is the panels API. Worth renaming `GK.UI` (e.g. `GK.Panels`) when the contract is cut.
11. **The Python stand and docs call `GK.*` through the file channel** (`Gwynerva_GateKit/queue_*`); the channel directory name is part of the contract with the stand, independent of Lua names.

## 6. Counts

- `lua-types/GateKit.lua`: 291 annotated functions (including methods of the panel builder and the Cheat table as members, about 40 of the Stage/Cheat/See entries have no `---` comment in the code and were described from their bodies).
- `lua-types/Weave.lua`: 94 annotated functions (Element and Document methods included).

## 7. The split as done (2026-11-13)

Layout: `mods/GateKit/Mods/Gwynerva_GateKit/` (folder, UUID `b1d5c0de-...` and mod table
unchanged, so the host prelude `local GK = Mods.GateKit.GK` keeps working) and
`mods/Weave/Mods/Gwynerva_Weave/` (new mod, UUID `8ee3d1c7-0362-41bc-b7c9-b10d85c62f4b`, mod table
`Gwynerva_Weave`). GateKit's `meta.lsx` lists Weave under `Dependencies`; Weave loads first.

- **Weave stands alone** (point 1, 5). Its files define `Weave` in Weave's own Lua environment
  (`Mods.GateKitWeave.Weave` from elsewhere; under Script Extender every mod's globals live in
  its own table, so there is no truly global `Weave`). GateKit's client bootstrap sets
  `GK.Weave = Weave` when Weave is loaded and otherwise loads the bridge without console,
  overlay and HUD (one error line). Weave trims tracebacks itself (`Weave.Internal.shortTrace`).
  Kit files: `Lua/Client/{Xml,Css,Reactive,Expr,BackendImgui,Dom,Component,Utilities,Layout}.lua`.
- **Overlay split** (points 2, 3, 7). New `Weave/.../Lua/Client/Shell.lua`: `Weave.Settings`
  (`Get`, `Set`, `OnChange`, `State`, `Saved`, `SetWindow`, `Open`; theme, font, blockKeys,
  blockMouse in `Script Extender/Gwynerva_Weave/settings.json`), `Weave.RegisterWindow`,
  `Weave.Blocking`, `Weave.Rescue`, `Weave.OnKey`, `Weave.Demo`, input blocking and Esc, console
  commands `weaverescue`, `weavedemo`. GateKit's `Client/Overlay.lua` keeps the console key, the
  HUD button and the settings window (`overlay.weave`, `hud.weave` in `GateKit/.../Lua/Client/ui/`,
  found through `Weave.AddRoot`); `GK.Overlay*` pass Weave's four settings through, so their
  contract is unchanged; `GK.Overlay.Register` calls `Weave.RegisterWindow`. The four settings are
  carried over once from an old `overlay.json`.
- **Paths** (point 4). Weave's user folder is `Script Extender/Gwynerva_Weave/ui/`, storage
  `Gwynerva_Weave/storage/`, built-in root `Mods/Gwynerva_Weave/ScriptExtender/Lua/ui/` (also
  `weave.png`). User copies in the old `Gwynerva_GateKit/ui/` are not migrated (Lua cannot list a
  folder). `demo.weave`'s menu item calls `Weave.Settings.Open` instead of `GK.OverlaySettings`.
- **DK removed** (point 6): no `DK` global, prefixes are `local GK = Mods.GateKit.GK`,
  the console command is `gk` (matching the file header).
- **UICatalog** is not shipped. `BootstrapClient.lua` loads
  `Script Extender/Gwynerva_GateKit/UICatalog.lua` when present (TODO: `gatekit ui-catalog` will
  write it); without it `GK.UICatalog` is nil and `GK.Gui` uses an empty catalogue.
- Not done: renaming `GK.UI` (point 10) and splitting `GK.Focus` (point 8).

## 8. `GK.Sound` (added 2026-11-13)

`Client/Sound.lua`, client side only, because sound belongs to the client: every running copy of
the game mixes its own, which is what makes per-copy volume possible at all.

The game holds its sliders in `ls::GlobalSwitches::SoundSetting SoundSettings[12]`
(`{ float ConfigValue[3]; FixedString ConfigKey; HashSet<FixedString> RTPCs; ... }`), and a
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
  draw `ConfigValue` either - with 11/22/33 written into the master entry's three floats it still
  showed 100 on every slider, so it reads the settings store. What it writes was not established;
  see `research/se-fork/upstream/sound-settings.md`.)
- **The last writer wins.** The player moving a slider, or the game applying its settings,
  overwrites what GateKit set. GateKit does not fight for it.
- **Nothing survives a restart**, and `GK.Sound.Muted()` is emptied by a Lua reset - the volumes
  then stay where they were, so `Unmute` after a reset restores nothing.
- **`Settings()` and `List()` answer different questions.** `List()` reads the RTPCs - what this
  copy sounds like, GateKit's own writes included. `Settings()` reads the engine's twelve
  `SoundSetting`s - what the player chose, which GateKit never touches. Its three numbers are the
  engine's three floats: `configured` is `ConfigValue[0]`, the volume the player set;
  `value` is `ConfigValue[1]`, the one the engine sends to the RTPCs; `default` is
  `ConfigValue[2]`, the game's own default (100 for all twelve). Seen live with the master at 37
  and music at 42 in the profile: `37 37 100` and `42 42 100`, and after
  `GK.Sound.Set("music", 10)` the mixer read 10 while the setting stayed 42.
  `display` is the row's name in the options screen, in the game's language.
- **`configured` and `value` come apart when the game silences itself.** With
  `MuteSoundWhenNotFocused` on and the window behind another one, `MasterVolume` and
  `CutsceneVolume` read `configured = 100, value = 0`, and their RTPCs read 0 with them: the
  engine mutes by moving the value in effect, never the player's own. A scenario that wants to
  know whether a copy is audible reads `value`, not `configured`.
- `MuteSoundWhenNotFocused` reads and writes, but the engine acts on it when the window gains or
  loses focus: setting it does not silence a copy that is already in the background until focus
  next changes.

## 9. `GK.FrameCap` and `GK.SetFrameCap` (added 2026-11-13)

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

The offsets came from the Toolkit, which exports `BaseApp::GetFrameCap`,
`BaseApp::SetLostFocusFrameCap` and `ls::GameClock::SetFrameCap` by name - each two or three
instructions long - and were then found in the game, which is a shorter build of the same engine,
by the engine's own arithmetic: the one place where a sixteen-bit cap and a sixty-four-bit
microsecond count satisfy `microseconds == 1000000.0f / cap`. That identity is checked before
anything is written, so a game whose layout has moved is not written to at all.

What follows:

- **The engine honours a cap set from outside its options screen.** Measured on a real game in a
  background window: 30.1 frames a second before, 156.9 after `GK.SetFrameCap(0)`; caps of 10 and
  20 answered with 9.9 and 17.7.
- **The bridge answers on the game's own tick**, so a cap of `n` costs up to `1/n` of a second of
  latency. A copy at ten frames a second is fine; two frames a second is not a sensible floor.
- **The game's own options overwrite it.** Changing a graphics setting makes the engine re-apply
  `FrameCapEnabled` / `FrameCapFPS` from the graphic config, which is the same field.
- Nothing survives a restart.
