# Open work

What is known to be missing, deferred or unproven, so it is not rediscovered. Designed-but-not-built
features live in [`docs/design/`](../docs/design/); this is the shorter, scruffier list.

## Not ported from the old Python toolkit

- **The official Toolkit bridge** (`stand/toolkit.py` + `toolkit/Bridge.cs` in the old
  repository): starting Larian's editor with a project and a level, evaluating against its own
  objects, switching between editing and playing. The MCP tools `bg3_toolkit_*` are absent for
  the same reason (`docs/contracts/mcp.md` §4.3).
- **The offline MCP server** (`bg3-offline`): `GateKit.GameData` and `GateKit.Symbols` have the
  queries, but nothing exposes them as MCP tools yet.
- **The agent-play and coverage plugin tools** (25 + 5 in the old server). Reachable meanwhile
  through `bg3_call` and `bg3_api`; they belong to sessions that were paused for the migration.
- **The research tools in `research/` are still Python** and still need the old stand to run.
  Porting them is open work, not a regression — see `research/coverage/README.md`.

## Designed, waiting to be built

- **A search site over the index** that is also its API — one URL per object, a `.json` twin for
  agents, cross-links instead of queries, rendered to static files and served from the index
  repository. [`docs/design/map-site.md`](../docs/design/map-site.md).
- **AI controllers**: characters played by an agent in combat.
  [`docs/design/ai-controllers.md`](../docs/design/ai-controllers.md).

## Unproven

- **Linux.** The whole host builds and unit-tests on Linux, and the game-data index was run
  there; nothing has driven a real game under Proton. WSL2 was tried and cannot do it
  (`research/notes/`). It needs a Linux install on hardware.
- **Linux has not been tried, but neither `key` nor `shot` needs the window any more.** Both go
  through the bridge: the key into the game's own input queue, the picture out of the swap chain
  in GateKit's Script Extender build. `LinuxProtonPlatform` still refuses the window fallbacks,
  which is now only what happens without the bridge. See `docs/design/deck.md`, "On Linux".
- **Only the DX11 renderer can hand its frames over.** The Vulkan backend of the extender has no
  capture, so `gatekit shot` on `bg3.exe` falls back to the window and is Windows-only there, and
  a copy on `bg3.exe` cannot be mirrored in a deck either. The work is the same shape: copy the
  presented swap-chain image into a host buffer in the backend's present hook, and for mirroring
  share an image with `VK_KHR_external_memory_win32` (or a dmabuf on Linux) the way
  `Extender/Client/IMGUI/FrameShare.inl` shares a D3D11 texture. The game's own
  `ls::RenderFrameSystem::Screenshot` would cover both renderers at once, but nothing in the
  extender reaches that object yet (`gatekit symbols find Screenshot`).
- **Mirroring on an HDR game shown on an SDR screen.** The deck passes an HDR10 frame through as
  it is and puts the tile's swap chain in that colour space; where the screen cannot take it, the
  tile's colours are approximate and it says so. The fix is a pixel shader in the deck doing the
  PQ→sRGB conversion the screenshot path already does in `DX11.inl`; the drawing path has no
  shader at all today, deliberately (`src/GateKit.Deck/README.md`). Untested: the machine this was
  built on has an HDR screen, so only the pass-through was seen working.

  **It does not need an SDR screen to be checked**, which is the thing that was not obvious: the
  extender already does this conversion when it writes a screenshot (`ToRGBA8` in `DX11.inl`), so
  the deck's shader can be checked against `GK.Shot`'s PNG of the same frame, pixel by pixel, on
  any screen. A still main menu makes the two frames comparable. Narrow in practice - it only
  bites when the game is on an HDR screen and the deck on an SDR one at the same time.
- **A deck killed outright leaves a mirrored copy's window where it put it.** Closing the deck
  properly undoes it, as does `window adopt`; `taskkill` on the deck does not, exactly as it does
  not give an adopted window back. With `GATEKIT_DECK_CONCEAL=hidden` the window cannot then be
  found again either, since GateKit looks for a visible window - which is why `offscreen` is the
  default.
- **The game's own options screen overwrites a frame cap.** `BaseApp::UpdateGraphicSettingsFromConfig`
  re-applies `FrameCapEnabled` / `FrameCapFPS` into the same field a cap is written to, so changing
  any graphics setting drops it. Nothing watches for that; a deck sets it again on its next change
  of layout, and `gatekit window fps` has to be repeated by hand.
- **The flaky scenario test is named and defused.**
  `ScenarioTests.Scenarios_that_need_the_same_start_share_one_game` failed about once in ten runs
  while real games were running, and not once in forty-five on a quiet machine. It counted
  *process starts*, and `Stand.UpAsync` legitimately starts a second time when the bridge's first
  heartbeat does not arrive. The test now asserts the grouping it means — the levels started, with
  consecutive repeats collapsed — and allows an extra start only when the stand said why, so an
  unexplained one still fails. That the retry is the cause remains the likeliest reading rather
  than a proven one; the test will now say so the next time it happens.
- **`se install` has never fetched anything**, because the fork has no release yet. The failure
  path (no such release) is tested; the success path is not.

## Smaller

- `GK.UI` and `GK.Focus` were not renamed or split when the bridge was divided into two mods
  (points 8 and 10 of `docs/contracts/lua-api.md`).
- Weave's hot reload keeps a window's old size; a changed height needs `Weave.Rescue()`.
- Weave draws background and frame colours about 1.45 times brighter than written; the showcase
  compensates by hand, which the framework should do instead.
- The `.cmd` files in `research/se-fork/` break the no-shell-scripts rule; they build the fork
  with MSVC and probably belong in the fork's own repository.
- Screenshots for `docs/curiosities.md` were left behind in the migration: they are pictures of
  a running game, and need retaking and a decision about game imagery.
