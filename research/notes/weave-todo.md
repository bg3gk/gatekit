# Weave: what is not done

Written on 2026-11-12 when the Weave session closed. The language as it is: [weave.md](weave.md).
The proposal for the fork's own element engine: [weave-native.md](weave-native.md). Code:
`stand\bridge\Mods\Gwynerva_GateKit\ScriptExtender\Lua\Client\Kit\`. Contract test:
`stand\scenarios\overlay.py` (passes on the v34 fork build; run it after every change).

## Waiting for the Script Extender fork

These need C++ in the fork (the fork session's work); Weave's side is small once they exist.

- [ ] **The element engine** (`Ext.UI2`, weave-native.md): nothing is built. It is what brings
      mixed text in one line, `overflow`, `z-index`, `transform`, a box model for every
      element, lists reordered in place, layout in the same frame as drawing, and windows
      dragged by the title bar only. On Weave's side: `Kit\BackendNative.lua` implementing the
      contract in the header of `BackendImgui.lua`, chosen when `Ext.UI2` exists.
- [ ] **Dragging a window by its title bar only** - the user asked for it. Either the element
      engine, or a per-window switch in the fork (`window.MoveFromTitleBarOnly`); the global
      ImGui switch would change other mods' windows.
- [ ] **Pictures from files under Vulkan and `.dds`**: DX11 only today
      (`GK.Weave.Has().files`); under Vulkan a picture from a file is left out with one line
      in the console.
- [ ] **Network, gated** - the user asked whether it can be added. Only with: off by default,
      hosts declared by the mod, the player's consent per mod and host, HTTPS request /
      response with limits, every request logged. Needs the user's explicit go-ahead in the
      fork session; it would not be accepted upstream.
- [ ] Blur, `backdrop-filter`, blend modes, clipping to a rounded or rotated outline: need
      extra render passes; not planned.

## Weave itself

- [ ] **A container that stops being flex / grid rebuilds its window** (positions, widths and
      the anchor's room are not taken back in place). v34 can do it: `AbsolutePosition = nil`
      returns an element to the normal flow.
- [ ] **A property added with `RegisterProperty` without `remove`** still rebuilds the window
      when it stops applying.
- [ ] **`#for` with an order changed in the middle builds the whole list anew** (IMGUI can only
      append). Appending and removing keep the rest. Goes away with the element engine.
- [ ] **Selectors**: sibling combinators (`+`, `~`), `:not()` with a list, `:has`, `:is`.
- [ ] **Values**: `min()` / `max()` mixing pixels with `%` (reported, not computed); `calc()`
      inside colours and grid tracks.
- [ ] **Layout**: grid rows (`grid-template-rows`, `grid-row`), `row-reverse` /
      `column-reverse`, stretching heights in a row, absolute positioning against anything but
      the direct parent container.
- [ ] **Transitions** cover colours and single numbers (opacity, rounding). Sizes and positions
      do not glide; there are no `@keyframes`.
- [ ] **Gradients** have two stops (what the fork draws); no `via-*`, no radial gradients.
- [ ] **`font-family` without `font-size` draws at 32**, not at the size around it.
- [ ] **The wrong-build path was never run in the game**: `GK.Weave.Mount` returning nil with
      one console line when Script Extender is not the v34 fork build. The stand only loads
      v34. `GK.Weave.Skin` (used by the console) is not refused on a wrong build; its errors
      are only caught.
- [ ] **State of nested components across a hot reload** is kept by position in the template
      (`path/tag:index` or the tag's `id`); a component moved in the template loses its state.
- [ ] **`useStorage`** writes on every change of the table; no debounce.
- [ ] **Colours under Vulkan with HDR**: the fork converts in the DX11 shader only; not
      measured on Vulkan.
- [ ] **Form controls from Weave's own boxes** (checkbox, slider, combo, table as components)
      - needed before a native root can do without IMGUI's widgets.
- [ ] **A second preset** when Tailwind 5 exists: presets are named (`wind4`); a new one is
      added beside it, the old one is not edited.
- [ ] **A compatibility statement** for mod authors (what never changes) once mods depend on
      Weave. Today nothing is promised: `GK.Weave.Version` is 4 and syntax was changed freely.

## Outside Weave's files (other sessions own them)

- [ ] `Client\Console.lua`: can now take a text size back with `window.Font = ""` instead of
      setting Medium; it builds its window by hand and takes only the theme from Weave.
- [ ] `Client\Panels.lua` (`GK.UI.Panel`): mods' own tabs still use hard-coded greys and a
      builder of their own; could be themed with `GK.Weave.Skin` / `Color`, or panels could be
      Weave components.
- [ ] `research\se-fork\notes.md`: two stale lines (the overlay scenario failing in a separate stand
      instance - fixed; `Ext.Utils.Version()` being 33 in both builds - it is 34). Sent to the
      fork session.
- [ ] `stand\gatekit.py` `watch`: `tree_stamp()` looks at `*.lua` only, so it does not react to
      `.weave` and `.css` of a linked mod.
- [ ] The alias `DK = GK` in `Shared\Bridge.lua` is temporary (the master session removes it);
      nothing in Weave uses `DK` any more.

## Decisions on record (the user's, 2026-11-12)

- Weave is the name of the UI language; directives use `#`, bindings `:`, events `@`;
  expressions are Lua with thin JS-like sugar; utility classes follow Tailwind 4 names.
- The product is GateKit (`GK.*`, mod `Gwynerva_GateKit`); stock Script Extender v33 is not a
  target, the v34 base with the fork's patches is the minimum.
- An own element engine on ImGui's draw list is wanted - not from scratch, not RmlUi.
- Subtasks run on Opus or lower; only the main sessions stay on the top model.
