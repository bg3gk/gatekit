# In-game console: what is left (written 2026-11-12, when this direction was closed)

On 2026-11-12 the user closed every direction except decompilation and Script Extender
coverage. The console and the `GK.UI` panels stay as they are, as working tools for those two;
the stand session repairs the bridge if it breaks. This is the list of what was open.

State at closing: `Client\Console.lua`, `Client\Panels.lua`, `Shared\ConsoleLog.lua`,
`Shared\Complete.lua` in the bridge mod `Gwynerva_GateKit`; scenario `stand\scenarios\console.py`;
documentation in `docs\api.md` (console and panels) and `research\notes\console-research.md`. On the
GateKit's Script Extender build (v34) the console uses everything the fork offers for it:
Up / Down history and Tab at the cursor (`OnHistory`, `OnCompletion`, `CursorPos`), one-click
`Copy all` (`SetClipboardText`), Follow that pauses on scrolling up (`LastScroll`), the
extender's own lines in the log (`Ext.Events.ConsoleMessage`), `Osi.` name completion
(`Ext.Osiris.GetFunctions`). `gatekit.py --instance console test -k console` passes on v34.

## Not finished

- **Never tried with a real keyboard and mouse.** Every check typed and pressed keys through
  `Ext.Input.InjectText` / `InjectKeyPress` with the game window in the background. Worth five
  minutes by hand: Up / Down, Tab in the middle of a line, the mouse wheel against Follow,
  Ctrl+V after `Copy all`.
- **After a world reset (`test --clean`) the scenario failed now and then.** Two causes were
  found and fixed on 2026-11-12, see "Fixed last" below; the scenario then passed after 5 resets
  of 5, but the failure was intermittent before the fix too (2 of 5 runs), so a longer series
  is the proof still owed. A sixth run was cut short by the stand, not the console:
  `PermissionError` in `os.replace(cmd_server.tmp, cmd_server.json)` in `gatekit.py` `_send`,
  the game holding the file at that moment; `_send` does not retry. (Fixed 2026-11-13: the
  stand now appends to a command queue and retries file operations the game blocks.)
- **Which side a C++ line came from is unknown.** The extender's console is one per process and
  `ConsoleMessage` carries no side, so such lines are filed under the client, and the whole
  claim exchange between the sides (`GK_Echo`, the 1.2 s wait, late and early claims) exists
  only to tell a mod's own print from a C++ line. A `Side` / `Source` field on the event in
  the fork would replace all of it with one comparison. Not asked of the fork session.
- **The extender's timing notices** ("Dispatching event Tick (...) took 9.8 ms") are filed as
  Debug. On a busy machine there are several a second, most of them about the bridge's own
  `Shared\Bridge.lua:152` tick, and they eat the 3000 kept lines. Collapsing them into one
  counted entry per call site, or dropping the bridge's own, was not done.
- **Real time stamps.** Entries carry `Ext.Utils.MonotonicTime()` and the window shows minutes
  and seconds since the process started, not the wall clock. The fork has what is needed
  (`research\se-fork\TODO.md`, Housekeeping); not taken.
- **The branches for the stock Script Extender v33** (the `can.*` checks in `Console.lua`, the
  "skipped (not in this build)" parts of the scenario, the sticky-input workaround, the Select
  view as the only way to copy) are still there. v33 was dropped on 2026-11-12, so they can be
  removed; they were left because they cost nothing and a stock run was last green on that day.
- **`GK.UI` panels**: a blueprint file on top of `GK.UI.Panel` (as BG3 MCM does it) and docking
  were considered and not taken (`research\notes\console-research.md`). `stand\examples\vsws_panel.lua`
  is an example only; the mod `<the weapon-visibility mod>` was not touched and does not register a panel.
- **Left over from the rename**: the lines `local GK = ... or Mods.GateKit.DK; local DK = GK`
  at the top of `Console.lua` and `Panels.lua` go together with the `DK = GK` alias in
  `Shared\Bridge.lua`. The window title "GateKit Console" is also its IMGUI id, so the size and
  position saved under the old title are not picked up.

## Waiting for the user's decision

- Whether the console is ever shipped to players. If so: register the toggle key through MCM
  instead of the own one-key setting, and remove the developer-only menus.
- Whether to ask for the `Side` field on `ConsoleMessage` in the fork (see above); it would
  be one more patch to carry.

## Fixed last (2026-11-12)

- **A claim that overtakes its line.** Right after a save is loaded the client's frames are
  slow, and the server's message "line N is mine" could arrive before the client was handed
  line N. The claim was dropped, and the line appeared a second time as a "Script Extender"
  entry 1.2 s later. The client now remembers such claims (`GK.EchoStats.early` counts them;
  seen as 2 after one reset).
- **The flood check of the scenario** counted all new entries, timing notices included, so on
  a busy machine it could stop before the last flood lines had arrived and then find fewer
  than 8 warnings. It now waits for the warnings themselves.
- The flag `__DK_print_wrapped` is now `__GK_print_wrapped`.
