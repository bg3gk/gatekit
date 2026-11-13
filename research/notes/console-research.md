# In-game consoles and overlays: what to take (2026-11-12)

What comparable tools do well, and what of it the GateKit console takes. "Read" means the page or
source was fetched for this survey; "snippet" means only a search summary was seen (Nexus pages
refuse automated readers).

## Techniques taken

| # | Technique | Seen in | In the GateKit console |
| --- | --- | --- | --- |
| 1 | The input is its own pinned line under the log; the log never touches it. Enter runs, focus returns to the input, the command is echoed as `> cmd` in its own colour. | Dear ImGui demo console; Cyber Engine Tweaks (CET) | yes |
| 2 | The log follows the tail only while the reader wants it; running a command jumps to the end. | Dear ImGui demo | yes, as a Follow switch (the scroll position cannot be read from Lua) |
| 3 | A cap on kept lines and on drawn lines. | BetterConsole (Starfield); CET's log has none and suffers | yes: 3000 kept per side, the last 250 matching drawn |
| 4 | Level toggles (debug / info / warning / error), a text filter with negative terms, a per-source list, "this context only". | Chrome DevTools | yes: levels, client/server, per-mod and per-`[tag]` source list, text filter with `-term` |
| 5 | Repeated identical lines collapse into one with a counter. | Chrome DevTools | yes |
| 6 | The log survives a reload. | Chrome "preserve log" | yes: it is kept across `Ext.Debug.Reset()` (hot reload) |
| 7 | History that survives restarts, without duplicates, re-run moves an entry to the end. | CET (`persistent_console`), Console Tweaks (Skyrim, snippet), Dear ImGui demo | yes, in `console.json`; buttons and a list instead of arrow keys (see limits) |
| 8 | Completion: one match is inserted, several extend to the common prefix and are listed. | Dear ImGui demo | yes, on Tab, and as clickable suggestions while typing |
| 9 | Client / server switch always visible next to the prompt. | Script Extender console (`client` / `server`) | yes, plus the UI context |
| 10 | `!cmd args` runs a registered console command; `reset` reloads Lua. | Script Extender console | yes: `Reload Lua` in the menu; `!cmd args` in the input line |
| 11 | Pinned expressions re-evaluated all the time. | Chrome live expressions | yes: Watch tab, once a second |
| 12 | One-click buttons for frequent actions, in named groups, each with a one-line description. | ToyBox (Owlcat games) | yes: Read / Do menus with tooltips; mods add their own tabs (`GK.UI`) |
| 13 | The toggle key is rebindable in the overlay itself ("press a key" button); the current key is always shown. | CET Bindings; ToyBox inline binding | yes: Settings tab |
| 14 | Settings, open panels, window size and position saved to a JSON file beside the mod's data. | CET (`persistent.json`, `layout.ini`), imgui-console | yes, `console.json` |
| 15 | Every command and its output is also written to a file that survives a crash. | BetterConsole; CET per-mod log files | yes: `console_transcript.log` |
| 16 | Output can be selected and copied; a "copy" button. | BetterConsole, CET (each line is a read-only edit field), Console Tweaks | yes: Select view is one read-only edit field; `Copy all` selects everything for Ctrl+C |
| 17 | While the overlay is open the game does not get the keys and clicks. | CET, ToyBox (Unity Mod Manager window) | yes, a switch in Settings, on by default |
| 18 | Multi-line editing in the game instead of `--[[ ... ]]--`. | Script Extender console (as the thing to improve on) | yes: Editor switch; `@file.lua` runs a file |
| 19 | Results as text a program can read, not only pixels. | none of the above; DevTools protocol in spirit | yes: `GK.Console*` functions |

## Considered, not taken

- **Docking and several windows** (CET shows console and game log as separate dockable windows).
  Script Extender's IMGUI has no docking; tabs do the job.
- **Registering the key through MCM** (BG3 Mod Configuration Menu: conflict detection,
  developer-only bindings). It would make the bridge depend on another mod; the console keeps its
  own one-key setting. Worth revisiting if the GateKit ever ships to players.
- **Forced "bind a key" dialog on first start** (CET). F11 is a sane default and `!gk` in the
  Script Extender console always opens the window.

## The unit of extension: what other mods register

| Framework | Unit | Registration | Optional dependency |
| --- | --- | --- | --- |
| BG3 MCM (read) | A declarative blueprint (`MCM_blueprint.json`: tabs, sections, typed settings), discovered, not registered; plus an escape hatch `MCM.InsertModMenuTab(name, callback)` that hands over a raw IMGUI tab | file next to `meta.lsx` | `"Optional": true` in the blueprint; the mod nil-checks `MCM` |
| SKSE Menu Framework (read) | A named page under the mod's section, or a free window; the page is an immediate-mode draw callback | `SetSection("Mod")`, `AddSectionItem("Name", Render)`, `AddWindow(Render)` | `if (!SKSEMenuFramework::IsInstalled()) return;` |
| Cyber Engine Tweaks (read) | A draw callback: the mod draws its own ImGui windows | `registerForEvent('onDraw', fn)`, `registerHotkey` / `registerInput` | CET is the host, not optional |
| Native Settings UI, Cyberpunk (read) | A path tree of tab / subcategory / typed widget with a callback each | `addTab`, `addSubcategory`, `addSwitch`, `addRangeInt`, `addSelectorString`, `addButton` | `GetMod("nativeSettings")` and return if nil |
| BetterConsole, Starfield (snippet) | A mod menu injected through a C header (`betterapi.h`) | not verified | recommended optional |
| ToyBox (from memory, page not fetched) | none: a monolith, extended by forking | - | - |

Two families: *draw callbacks* (the mod gets the raw toolkit: maximum freedom, nothing the host
can read back) and *declared widgets with callbacks* (less freedom; the host knows every
element). The GateKit takes the second, as `GK.UI.Panel(name, builder)` with typed elements:
because the host knows the elements, a test or an assistant can press them and read them
(`GK.UI.Press`, `GK.UI.Read`) without a screenshot, which a draw callback could never give.
From the first family it keeps the cheap part: the guard is three lines in the mod, no file
format, no dependency. A blueprint file (MCM's way) can be layered on top later.

## Sources

- Dear ImGui demo, `ExampleAppConsole` / `ExampleAppLog` — read —
  https://raw.githubusercontent.com/ocornut/imgui/master/imgui_demo.cpp
- Cyber Engine Tweaks: usage, configuration, and `src/overlay/` (`Overlay.cpp`,
  `widgets/Console.cpp`, `LogWindow.cpp`, `Bindings.cpp`, `Settings.cpp`) — read —
  https://wiki.redmodding.org/cyber-engine-tweaks/console/usage ,
  https://github.com/maximegmd/CyberEngineTweaks
- RedHotTools (hot reload and inspectors for Cyberpunk 2077) — read —
  https://github.com/psiberx/cp2077-red-hot-tools
- BetterConsole for Starfield — read on a mirror —
  https://allmods.net/starfield-mods/starfield-user-interface/betterconsole-console-replacer-and-mod-menu-v1-0/
  (original: https://www.nexusmods.com/starfield/mods/3683)
- Skyrim: Console Tweaks https://www.nexusmods.com/skyrimspecialedition/mods/69244 , Better
  Console https://www.nexusmods.com/skyrimspecialedition/mods/5687 , ImGui console overlay
  https://www.nexusmods.com/skyrimspecialedition/mods/40190 — snippets only. More Informative
  Console, ConsoleUtil and the SKSE menu frameworks could not be read and are not relied on.
- ToyBox for Owlcat games (Bag of Tricks page) — read —
  https://github.com/xADDBx/ToyBox-Wrath/wiki/Bag-of-Tricks
- BG3 Script Extender, `Docs/API.md` (console section) and the sources in
  `BG3Extender/Extender/Shared/Console.cpp`, `LuaScripts/Libs/Logger.lua`,
  `Extender/Client/IMGUI/IMGUI.cpp`, `Extender/Client/SDL.cpp` — read —
  https://github.com/Norbyte/bg3se
- BG3 Mod Configuration Menu — read — https://github.com/AtilioA/BG3-MCM ,
  https://raw.githubusercontent.com/AtilioA/BG3-MCM/main/wiki.md
- SKSE Menu Framework example — read —
  https://raw.githubusercontent.com/Thiago099/SKSE-Menu-Framework-2-Example/main/src/UI.cpp
- Native Settings UI — read — https://github.com/justarandomguyintheinternet/CP77_nativeSettings
- Chrome DevTools console reference — read —
  https://developer.chrome.com/docs/devtools/console/reference

## What Script Extender v33 allows (measured in the game)

- `Ext.Events.Log` exists and fires for every `print`, `_P`, `Ext.Utils.Print*`, `Ext.Log.*` call
  made from a mod's own environment, with the mod's uuid, the level and the arguments.
- Messages Script Extender writes itself (a Lua error inside another mod's event handler, Osiris
  errors, its own warnings) go straight to the console window from C++ and raise no Lua event.
- An IMGUI text field has no history or completion callback. While a field is being edited,
  Script Extender swallows key events, so Lua never sees the arrow keys; Tab can be seen (with
  `AllowTabInput` it arrives as a tab character), Enter too (`EnterReturnsTrue`).
- There is no clipboard function in Lua. Ctrl+V / Ctrl+C / Ctrl+A work inside IMGUI text fields,
  read-only ones included.
- A window's scroll position can be set but not read; its size and position can be read.
- Keys injected with `Ext.ClientInput.InjectKey*` and window messages posted to a background game
  window do not reach IMGUI. Only real keyboard input does.
- Key and mouse events are cancellable from Lua (`e:PreventAction()`).

## On the GateKit's Script Extender build (v34 base, measured 2026-11-12)

The limits above are the stock v33. The fork ([se-fork.md](se-fork.md)) lifts them and the
console uses each where it is present, falling back otherwise:

- `OnHistory` / `OnCompletion` / `CursorPos` on the input field: Up / Down history and Tab at
  the cursor (techniques 7 and 8 as Dear ImGui's demo console does them).
- `Ext.IMGUI.SetClipboardText`: `Copy all` is one click (technique 16).
- `LastScroll` / `LastScrollMax`: Follow switches off when the reader scrolls up and on again
  at the end (technique 2 as in the Dear ImGui demo).
- `Ext.Events.ConsoleMessage`: the extender's own lines reach the log. Measured: the console is
  one per process, every line is delivered to both Lua states with the same `Index`, a plain
  `print` has the type `Osiris`, and nothing says which side a line came from. Each side
  therefore recognises its own prints, the server sends the numbers of its own to the client,
  and the client logs only lines neither side claimed.
- `Ext.Osiris.GetFunctions()`: `Osi.` names complete, with kind and parameter types.
- `Ext.Input.InjectText` / `InjectKeyPress`: the scenario types into the line, presses Tab, Up,
  Down and Enter with the game window in the background.
