# The GateKit's Script Extender build (2026-11-12)

A fork of [Norbyte/bg3se](https://github.com/Norbyte/bg3se) that lifts limits the GateKit ran
into, as small additive patches that can be offered upstream. It sits on upstream's branch for
the future v34 and is the build the stand loads by default; the stock Script Extender (v33) is
untouched and is loaded when the stand is told `BG3_GATEKIT_SE=stock`.

## Where the fork lives (v34 base; v33 retired on 2026-11-12)

The user's decision: move completely to the branch of the future v34 and drop v33, so that
proposals to Script Extender's author are easy to make later.

| | |
| --- | --- |
| Upstream base | branch `imgui_v1_93` of Norbyte/bg3se at `2ea16d51` (v34 in development, ImGui 1.93.1 WIP), kept as tag `base/v34-imgui_v1_93-2ea16d51` in case upstream rewrites that branch |
| Working tree, branch | `se-fork\bg3se-v34`, `gatekit-v34` — the only working branch; 23 commits on top of the base |
| What is on it | patches 1–11 of the table below, `element.FontSize`, and the coverage session's layouts and debug helpers |
| Patch files | `se-fork\patches\v34\` (one per commit); the imgui backend change: `se-fork\patches\imgui\0001-...-imgui-1.93.diff` |
| Tested build | `se-fork\builds\v34\BG3ScriptExtender.dll` — the stand loads it by default; a DLL goes there only after `se_fork` and `se_fork_ui` passed on it |
| `Ext.Utils.Version()` | 34 |
| Old v33 branches | tags `archive/devkit-v33`, `archive/overlay-v33`; trees `se-fork\bg3se`, `se-fork\bg3se-overlay` are no longer worked in |
| Scenarios on the tested build | all eight passed on the build before the field renames (`api`, `console`, `dialogue`, `overlay`, `smoke`, `stage_fight`, `se_fork`, `se_fork_ui`, not all in one run: `console` and `overlay` were each red once while their owners were editing them); after the renames `se_fork` and `se_fork_ui` were re-run and pass |
| Coverage on this build | components: client 962 of 1651 (58.3 %), server 1204 of 1854 (64.9 %); systems 85 of 934 — see [se-coverage.md](se-coverage.md) |

What the move took: 7 of the 11 patches applied as they were. Console messages were re-done
the v34 way (events are declared in `GameDefinitions\Events.inl` with parameter structs in
`Lua\Shared\SharedEvents.h`); the HDR patch needed one line placed after upstream's new texture
mirroring; the font loader keeps the merge of a second file but drops the glyph-range fix (v34
loads glyphs on demand, a mod's font gets Cyrillic by itself); three draw-list calls changed
their argument order and the entity-system accessor became a reference. The coverage session's
commits applied without conflicts. The unmodified base was built and run first
(`se-fork\builds\v34-base`).

New on this base: `element.FontSize = 17` (this element and its children, in the units
`LoadFont` sizes use; `nil` clears). Sizes of one font file share one font, and changing fonts
no longer rebuilds an atlas.

Keeping up with upstream: `git fetch`, then `git rebase origin/imgui_v1_93` in
`se-fork\bg3se-v34` (or onto `origin/main` once the branch is merged), rebuild, run the
scenarios, refresh `se-fork\builds\v34`. The base is unreleased and changes almost daily.

Names and data follow the game, not Script Extender's earlier guesses (the user's rule,
2026-11-12): where the game's own name of a field is known it is used, and the name Script
Extender had stays as an alias through its `[[bg3::legacy(OldName)]]` attribute, so existing Lua
keeps working. First application: 25 component fields renamed from the game's replicated-field
symbols (`research/coverage/apply_game_names.py`, source `research/coverage/work/fieldmeta-check.json`);
in the game both names read the same data. Open cases are listed in `research/coverage/leads.md`.

Proposals to the author: each one a small topic branch cut from HIS branch with one of the
patch commits on it, never a piece of `gatekit-v34` as a whole. Commit 7 (decorations, canvas,
geometry, styles) has to be split first. Nothing has been pushed and no pull request opened;
that happens only when the user asks.

## Using it

```
se-fork\build.cmd bg3se-v34          build the DLL (about 3 minutes)
se-fork\build.cmd bg3se-v34 fast     the same without whole-program optimisation, for iterating
python stand\gatekit.py up --level GustavX:TUT_Avernus_C    loads se-fork\builds\v34 by default
set BG3_GATEKIT_SE=<folder with a BG3ScriptExtender.dll>     another build for this run
set BG3_GATEKIT_SE=stock                                    the stock Script Extender (v33)
python stand\gatekit.py status                              says which build is in place
python stand\gatekit.py down                                closes the game, removes the build from bin
```

Build times measured on this machine (2026-11-12, other work running alongside): a full build
165-185 s in the normal mode and about 200 s in the fast mode; after changing one Lua library
file (`Debug.inl`) 63 s normal against 41 s fast; after changing the layouts header
(`Coverage.h`, included almost everywhere) 150 s even in the fast mode. A fast-mode DLL passed
`se_fork` and `se_fork_ui`. Switching modes in one tree rebuilds everything once; the DLL that
goes to `se-fork\builds\v34` is always built in the normal mode. The fast mode needed two
things: `/bigobj` (added through `gatekit-build.targets`) and a one-word fix in Script Extender
itself (a constructor declared `inline` but defined in a .cpp; it is a commit on the branch).

The stand puts the DLL and the loader switch into the game's `bin` for a run and removes both
on `down`. `python stand\sefork.py install <folder> | remove | status` does the switch by hand.
With stand instances (`--instance <name>`) each instance has its own `bin`. Under load (several
games at once) set `BG3_GATEKIT_START_TIMEOUT=240`. The names `gatekit` / `GK` / `BG3_GATEKIT_*`
are being renamed to GateKit (`docs\rename-gatekit.md`).

In Lua, test for a feature, not for a version (`Ext.Utils.Version()` is 34 on this build and 33
on the stock one; a feature test also survives the day upstream ships the same thing):

```lua
if Ext.IMGUI.GetClipboardText then ... end          -- functions: nil on stock
if Ext.Events.ConsoleMessage.NextIndex then ... end -- events: Ext.Events.<anything> is never nil;
                                                    -- an unknown name is a stub without NextIndex
local ok, max = pcall(function() return window.LastScrollMax end)  -- properties: reading an
                                                    -- unknown one raises an error on stock
```

## What the fork adds

Each row is one commit on branch `gatekit` in `se-fork\bg3se`, also exported to
`se-fork\patches\000N-*.patch`, with a description written for an upstream pull request.

| # | Commit | Lua surface | Side |
| --- | --- | --- | --- |
| 1 | Clipboard | `Ext.IMGUI.GetClipboardText()` → string or nil; `Ext.IMGUI.SetClipboardText(text)` → bool | client |
| 2 | Window state | on windows and child windows, read-only: `LastScroll`, `LastScrollMax` (`{x, y}`, pixels), `Focused`, `Hovered`. `SetBgAlpha(a)` now lasts until `SetBgAlpha(nil)` (it used to last one frame) | client |
| 3 | Console messages | `Ext.Events.ConsoleMessage` with `e.Index`, `e.Type` (`Debug`, `Info`, `Osiris`, `Warning`, `Error`), `e.Message` — raised for **every** line of the Script Extender console, including Lua errors in other mods' handlers, Osiris errors and the extender's own warnings | both |
| 4 | Osiris names | `Ext.Osiris.GetFunctions()` → `{ { Name, Type, Arity, Params = { { Type, BaseType, Out } } } }`; `Type` is `Event`, `Query`, `Call`, `Database`, `Proc`, `SysQuery`, `SysCall`, `UserQuery` | server |
| 5 | Input field | on `InputText`: `OnHistory(handle, direction)` (Up = -1, Down = 1), `OnCompletion(handle, cursor)` (Tab), `CursorPos` (read and write), `SelectionStart`, `SelectionEnd`, `SetSelection(start, end)`; positions are byte offsets | client |
| 6 | Injected input | `Ext.Input.InjectKey*` now reaches IMGUI windows; new `Ext.Input.InjectText(text)`, `InjectMouseMove(x, y)`, `InjectMouseButton(button, pressed[, x, y])`, `InjectMouseWheel(x, y)`. Also fixes the key code of injected Tab, Return, Escape and letter keys | client |
| 7 | Decorations, canvas, geometry, styles | on every element: `SetBackground(color)`, `SetBackgroundGradient(from, to[, angle])` (CSS angles; honours rounding and alpha), `SetBackgroundRounding(r)`, `SetBackgroundPadding(x[, y])`, `SetBorder(color[, size])`, `SetShadow(color[, size, dx, dy])`, `ClearDecoration()`; read-only `LastRect` (`{x1, y1, x2, y2}`), `LastAvail` (`{w, h}`); `ClearStyle(name)`, `ClearColor(name)`. `parent:AddCanvas(w, h)` with `AddLine`, `AddRect`, `AddRectFilled`, `AddRectGradient`, `AddCircle`, `AddCircleFilled`, `AddTriangle`, `AddTriangleFilled`, `AddBezier`, `AddPolyline`, `AddText`, `Clear`, `GetCommandCount`. `SetSizeConstraints` now lasts and accepts a minimum alone | client |
| 8 | Colours on HDR displays | automatic; `Ext.IMGUI.GetBackendInfo()` → `{ Renderer, SwapChainFormat, RenderTargetViewFormat, RenderTargetFormat, HDR10, PaperWhiteNits }`, `Ext.IMGUI.SetPaperWhite(nits)` | client |
| 9 | Raw access for reverse engineering (developer mode only) | `Ext.Debug.ReadMemory(address, size)`, `WriteMemory(address, bytes)`, `GetModuleInfo()`, `GetComponentAddress(entity, name)`, `GetComponentTypes()`, `GetSystemTypes()` | both |
| 10 | Images, fonts, animation | `SetBackgroundImage(iconOrTexture[, mode, tint, border])` on every element, `mode` = `"Stretch"` (default), `"Tile"`, `"Slice"` (nine slices, `border` in texture pixels); `canvas:AddImage(iconOrTexture, min, max[, tint, uv0, uv1])` → bool. `Ext.IMGUI.LoadFont(name, path, size[, mergePath, mergeFirst, mergeLast])`, `Ext.IMGUI.GetFontNames()`. `AnimateStyle(var, from, to, seconds)`, `AnimateColor(color, from, to, seconds)`, `IsAnimating()` | client |
| 11 | Image files | wherever an image name is accepted (`AddImage`, `AddImageButton`, `SetBackgroundImage`, `canvas:AddImage`), a path with an image extension is a file in the game's data: `"Public/MyMod/GUI/logo.png"` (png, jpg, tga, bmp; a mod's pak or a loose file). `Ext.IMGUI.GetImageSize(name)` → `{ width, height }` or nil, for files, icons and texture resources | client |

Notes for users of these:

- **Console messages.** A mod's own `print` arrives twice: as `Ext.Events.Log` (with module and
  topic) and as `ConsoleMessage` (formatted text). Pick one for such lines. What a
  `ConsoleMessage` handler prints is not sent back to handlers. The log holds 1024 messages
  between ticks; a gap in `Index` means some were dropped. Messages from before a Lua state
  existed (or before a `reset`) are not replayed.
- **Input field.** Handlers run on the Lua event queue, as `OnChange` does. In `OnHistory`, set
  `Text` first, then `CursorPos`; both are applied on the next frame. Up/Down and Tab are taken
  from the field only while a handler is assigned.
- **Injected input** works with the game window in the background — this is how the `se_fork`
  scenario types into a field, presses Up and Tab, with no real keyboard. Injected events the
  UI takes (a field has focus, the mouse is over a window) are not passed on to the game, like
  real ones. Lua `KeyInput` events are still not raised for injected events. Injected mouse
  clicks on IMGUI windows are implemented but not yet covered by the scenario; clicks into the
  game's own UI in the background remain unverified.
- **Decorations** are drawn behind the element they belong to, nested ones included. Tables, table
  rows and cells, tooltips, popups and menus are not decorated. On a window the decoration covers
  the contents area (not the title bar) and has no shadow. Picture: [se-fork-ui.png](se-fork-ui.png).
- **Images** are game textures (by resource GUID), atlas icons (by name) or image files (by path;
  patch 11). On the canvas `uv0` / `uv1` select a part of the image itself (0..1), also for an
  atlas icon. Image files are decoded with stb_image and need the DX11 renderer; on Vulkan they
  are reported as unavailable for now. A file texture stays loaded for 120 frames after its
  last use. `.dds` is not read as a file (use a texture resource for those). Pictures:
  [se-fork-ui-images.png](se-fork-ui-images.png), [se-fork-ui-file.png](se-fork-ui-file.png).
- **Fonts.** `LoadFont` reads a `.ttf` / `.otf` from the game's data (a mod's pak included) at any
  size; the six built-in fonts are 24 to 44 px. A font a mod loads now gets the Cyrillic or
  Turkish glyphs when that is the game's language (it used to get Latin only). The optional
  merge arguments add a code point range from a second file, for an icon font. Bold and italic
  are separate font files loaded under their own names.
- **Animation** moves one style variable or colour of an element from a value to another with
  ease-in/out; the element keeps the end value. The clock starts on the first frame the element
  is drawn after the call, so an element shown later still animates in full.
- **Disabled state** needs no patch: `widget.Disabled = true` exists on stock (it is one of the
  `ItemFlags`); it blocks interaction without greying, add `SetStyle("Alpha", 0.4)` for the look.
- **Colours.** With HDR switched on for the display, the game presents an HDR10 swap chain
  (`DXGI_FORMAT_R10G10B10A2_UNORM`, PQ-encoded) and stock Script Extender writes sRGB values into
  it unchanged: 0.5 is already white. That is the "(2v)^1.85" the overlay session measured; it
  comes from HDR, not from SDR output. The fork converts colours in the DX11 pixel shader
  (sRGB → linear → Rec.2020 → PQ) with sRGB white at 200 nits by default. Measured with a grey
  ramp: at 80 nits (what the stand's window capture maps to white) the eleven steps come out as
  0, 19, 48, 76, 102, 129, 155, 181, 205, 230, 255 against the ideal 0, 26, 51, ... 255; before,
  every step from 0.5 up was white. The stand's screenshots clip above about 80 nits, so call
  `Ext.IMGUI.SetPaperWhite(80)` before a screenshot that is to be compared by colour. How the
  result looks on the HDR display itself was not judged by eye. Vulkan is not converted yet. The
  shader change lives in the imgui fork: `se-fork\patches\imgui\0001-*.diff`, applied by
  `setup-externals.cmd`.
- **Raw access** is for checking guesses about structure layouts in the running game without
  rebuilding: describe the layout as data, read the bytes, compare. An address that is not mapped
  makes `ReadMemory` return nil and `WriteMemory` return false; nothing else is checked, so a
  write to a wrong but valid address corrupts the game. All of it raises an error unless
  `DeveloperMode` is on.
- **Docking** was not available on v33 (ImGui 1.91.9b). Whether the 1.93 base allows it has
  not been looked at yet.

Already possible on stock v33, no patch needed (measured in the running game):

- Wall clock: `Ext.Timer.ClockEpoch()` (Unix seconds), `Ext.Timer.ClockTime()` (a
  `"2026-11-12 11:09:12.84"` string, UTC).
- Members of proxy objects for completion: `Ext.Types.GetObjectType(obj)`,
  `Ext.Types.GetTypeInfo(name)`; components of an entity: `entity:GetAllComponentNames()`.
- Per-name Osiris details once the name is known: `Osi.X.Arities`, `Osi.X.InputArities`,
  `Osi.X:Type(arity)`.
- Raising a console command: `Ext.Events.DoConsoleCommand` is an ordinary event and has
  `:Throw` (not tried here).

## Building

Nothing had to be installed. Used as found: Visual Studio Community 2026 (18.6, toolset v145,
MSVC 14.51), Windows SDK 10.0.26100, cmake 4.4.4, git, Python 3.14.

Downloaded into `se-fork\` with the user's consent:

- The sources: `git clone https://github.com/Norbyte/bg3se` (`se-fork\bg3se`); the working tree
  `se-fork\bg3se-v34` is a git worktree of it on branch `gatekit-v34`.
- The dependencies, in `se-fork\bg3se\External\` and linked into the working tree by directory
  junctions: bcdec, Detours 4.0.1, FastLZ 0.5.0, glm 1.0.3, `Norbyte/lua-dos`, lz4 1.10.0,
  `Norbyte/optick`, protobuf 36.1 (with abseil) and its `protoc`, rapidjson, SDL2 2.30.6
  development files, stb, tinycrypt, Vulkan headers, and the NoesisGUI SDK archive from
  Norbyte's server (plain http; headers only are used; proprietary, never to be redistributed).
  Not fetched: libcurl and ZipLib (only the updater and crash reporter use them).
- ImGui: the working tree has its own `External\imgui`, `Norbyte/imgui` at `0c44880d` (1.93.1
  WIP, the commit upstream's branch pins), with the HDR output change applied
  (`se-fork\patches\imgui\0001-...-imgui-1.93.diff`).

`se-fork\setup-externals.cmd` is the script that fetched the v33 set; it has not been redone for
v34 (the three extra libraries and the ImGui commit were fetched by hand as listed above).

What differs from upstream's instructions, and why:

- protobuf 36 needs abseil at link time and `BG3Extender.vcxproj` does not list it;
  `se-fork\gatekit-build.targets` adds the libraries (and the include path of stb, and `/bigobj`
  for the fast mode) from outside, so the project file stays as upstream has it.
- The project's post-build step copies the DLL to its author's game folder; the build script
  switches that step off.

Only `BG3Extender` and the libraries it links are built. The loader (`DWrite.dll`), the crash
reporter and the debugger are not.

## How the game loads Script Extender, and how the build is swapped

- `bin\DWrite.dll` is the loader: a proxy for the system DWrite that reads
  `bin\ScriptExtenderUpdaterConfig.json` (absent by default), fetches a manifest from
  `bg3se-updates.norbyte.dev`, downloads a signed package into
  `%LOCALAPPDATA%\BG3ScriptExtender\ScriptExtender\<version>_<digest>\`, verifies it and loads
  `BG3ScriptExtender.dll` from there.
- With `"DebugLoadSE": true` in that config the loader loads `bin\BG3ScriptExtender.dll`
  instead, with no signature check. This is the author's own development route and the
  installed loader has it.
- So the stand adds two files to `bin` (`BG3ScriptExtender.dll`, `ScriptExtenderUpdaterConfig.json`)
  and `down` deletes them. No existing file is modified. For such a run the stand also sets
  `"SendCrashReports": false`, so a crash of our build is never uploaded to the author's
  collector; the settings file is restored by `down` as before.
- A game that was just killed keeps the DLL locked for a moment; `sefork.remove()` removes the
  loader switch first (stock loads again at once) and retries the DLL for up to 20 s.

Stand changes: new `stand\sefork.py`; in `stand\gatekit.py` an import, one call in `up()` after
`configure_extender`, one in `down()` after `restore()`, one line in `status`.

## Risks

- **Version drift.** The fork must sit on the commit of the installed stock build; when Script
  Extender updates (`Version.txt` in the cache folder names the commit), rebase `gatekit` onto it
  and rebuild. The externals may need re-pinning (v34: imgui `master`, four more libraries).
- **Game updates.** Script Extender finds engine functions by byte patterns and fixed layouts;
  a game patch needs a new upstream release first. With the fork off, the stock updater handles
  it as usual.
- **Other mods.** The patches add functions, events and properties. Two things change behaviour:
  `SetBgAlpha` now lasts (a mod calling it every frame sees no difference), and injected keys
  can be taken by a focused IMGUI text field instead of reaching the game.
- **Multiplayer** between a fork and a stock build was not examined; the fork is for the stand.
- **Licence.** MIT with the Commons Clause: modify and share freely, do not sell. Players get
  Script Extender only through its signed updater, so a fork reaches developers only; upstream
  pull requests are the way to reach everyone. Nothing has been pushed or proposed upstream.
- **Trust.** The DLL is unsigned and built here from readable sources; the Noesis archive is the
  one download over a non-TLS URL.

## What is left

Sizes: S = under a day with its test, M = one to two days, L = more or uncertain.

| # | Capability | How | Size | Risk |
| --- | --- | --- | --- | --- |
| 7b | Timeline event keys (`ShowWeaponKey.ShowWeapon`, ...) | `TLKeyBaseComponent::Keys` is `[[bg3::hidden]]` in `GameDefinitions\AllSpark.h` while the key structs are declared; check each layout hot with `Ext.Debug.ReadMemory`, then un-hide | S–M | medium |
| 7c | Keyboard / controller UI switch | locate `ecl::EocClient::SetControllerMode` / `SetKeyboardMode` in the shipping exe (notes: `symbols\requests.md`), add to `BinaryMappings.xml`, call on the client thread | L | high; only on the user's explicit yes |
| — | Image files on Vulkan; `.dds` files | create the texture in the Vulkan backend (image, view, sampler, descriptor set); a DDS reader for block-compressed formats | M | medium |
| — | Weave's further requests (`weave-needs-from-se-fork.md` in the sessions' shared notes): ImGui 1.92 with per-element font size, title-bar-only dragging per window, an element engine of our own (`Ext.UI2`), gated network access | each needs the user's decision first; the last three are large | L | — |
| — | HDR conversion for Vulkan | the same maths in the Vulkan backend's shader | S–M | medium |
| — | A faster build configuration for iterating (no whole-program optimisation) | | S | low |
| — | Coverage of the engine itself: 1576 component types and 849 systems have no layout | its own project: [plan.md](plan.md) | L | — |

Commit 7 bundles four capabilities that touch the same functions; split it before offering it
upstream.

## History: the assessment that led to the move (2026-11-12)

Kept for the record; option B below is what was then done in full, and v33 was dropped.

### Moving to ImGui 1.92+

Asked for by Weave: fonts at any size per element (`font-size: 17px`), which ImGui 1.92 makes
cheap (glyphs are rasterised on demand, no atlas rebuild; textures are managed by the backend).

What was found:

- **Upstream is doing this migration right now.** Branch `imgui_v1_93` of Norbyte/bg3se: 18
  commits on top of `main` (v34 in development), the last one dated 2026-11-11 — "IMGUI 1.93
  upgrade", "Remove old font reload logic", "Font storage leak fixes", a new
  `TextureMirror.inl`, 533 lines added and 212 removed in 23 files. It pins
  `Norbyte/imgui` at `0c44880d` (1.93.1 WIP). It is not merged into `main` yet.
- On that branch fonts are sized dynamically (`ImGui::PushFont(font, size)`), every size of a
  font file shares one `ImFont`, and the glyph-range tables are gone — so a mod's font gets
  Cyrillic without the fix in patch 10. There is no per-element font size property yet; on that
  base it is a few lines (`FontSize` on the element, passed to `PushFont`).
- **Our patches against that branch** (dry run, `git cherry-pick -n` of each, nothing kept):
  7 of 11 apply cleanly — clipboard, window state, Osiris names, input field, injected input,
  decorations and canvas, raw memory. 4 conflict: console messages (4 hunks: upstream reworked
  how events are registered), HDR colours (1 hunk in `DX11.inl`), images/fonts/animation
  (2 hunks in the font loader, which upstream rewrote), image files (follows from the previous
  two). Whether the clean ones also compile against 1.93 was not tried; the draw-list calls we
  use still exist there, texture ids became `ImTextureRef`.
- v33, which players have, stays on ImGui 1.91.9b. A fork built from the 1.93 branch would be
  v34-in-development: a different Script Extender than the stock one on this machine.

Options:

| | What | Cost | Risk |
| --- | --- | --- | --- |
| A | Wait for upstream to merge and release v34, then rebase the fork onto it and add `FontSize` | about a day at that time: resolve the 4 conflicts, rebuild, both scenarios; `FontSize` S | low; no date for v34 is known |
| B | Build a second variant of the fork on `imgui_v1_93` now, for Weave to develop against | 1–2 days now, then re-rebasing while upstream changes it (daily at the moment) | medium: an unreleased base, fixes arriving under us; two fork variants to keep |
| C | Stay on v33; get any font size by loading the font under a name per size (`LoadFont("Body@17", path, 17)`) — works today, each new size rebuilds the atlas once. Pick the four upstream commits of 2026-10-30/31 that fix crashes when fonts change (`c944e215`, `e789ae67`, `0e244492`, `420db862`; 47 lines in the backends) into the fork | S | low |
| D | Port ImGui 1.92 into v33 ourselves | 3+ days, and it repeats upstream's 500 lines, to be thrown away at v34 | not worth it |

Recommended: C now, A when v34 is out. B only if Weave cannot wait for per-element sizes.

For the bridge (other sessions' files): `GK.Complete` can now complete `Osi.` from
`Ext.Osiris.GetFunctions()`; the console can take C++-side messages from
`Ext.Events.ConsoleMessage`, use `OnHistory` / `OnCompletion` / `CursorPos` for its input line,
`SetClipboardText` for Copy, `LastScroll` / `LastScrollMax` for Follow, and real time stamps
from `Ext.Timer.ClockTime()`; scenarios can type into IMGUI windows with `Ext.Input.InjectText`.
