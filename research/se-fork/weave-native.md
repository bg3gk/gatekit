# Weave's native element engine: a proposal for the Script Extender fork

Status: **proposal, nothing is built.** It describes what GateKit's build of Script Extender
([se-fork.md](se-fork.md), the v34 base) would have to provide so that Weave ([weave.md](weave.md)) can stop
drawing with IMGUI's widgets. Working name of the Lua module: `Ext.UI2`.

## Why

Weave already does the styling, the layout and the reactivity itself. What still comes from
IMGUI's widgets is what limits it:

| Limit today | Cause |
| --- | --- |
| Layout is a frame late and placed with `AbsolutePosition` | Lua reads `LastRect` after the frame was drawn |
| No mixed text in a line (a bold word, a link, an icon inside a paragraph) | an IMGUI text widget is one run |
| No `overflow`, `z-index`, `transform` on any element | widgets draw themselves, in order, unclipped |
| `margin`, `padding`, `border` only where IMGUI has a style variable for them | there is no box model |
| A list reordered in the middle is built anew | widgets can only be appended |
| A window is dragged by any empty place | IMGUI's window behaviour |
| Transitions would need Lua every frame | nothing interpolates on the render thread |

What is **not** to be rewritten: the DX11 / Vulkan output, the font atlas (every language the
game ships), the draw list, the text editor (`InputText`: caret, selection, IME, clipboard).
The engine draws *on* IMGUI's draw list and reuses those.

## Shape

A retained tree of nodes owned by C++, described from Lua, laid out and painted by C++ every
frame. Lua runs only when something changes (Weave's reactivity) and when an event arrives.

```
Lua (event queue)                         C++ (render thread)
Weave: template, cascade, reactivity      every frame, per root:
   |  create / remove / move nodes           1. layout   (only if dirty)
   |  set style numbers, text                2. animate  (running transitions)
   v                                         3. paint    (draw list, clip, z, transform)
Ext.UI2  ----- command buffer ----->         4. input    (hit test -> events)
         <---- event queue ---------
```

Commands from Lua are queued and applied at the start of the next frame, as IMGUI object
changes are today; nothing in Lua ever waits for the render thread.

### Nodes

| Kind | Is |
| --- | --- |
| `root` | a window of the overlay: position, size, open; draggable by a node marked as its handle (the title bar), resizable by its edges; kept inside the picture by the engine |
| `box` | a rectangle with children: the element of the box model |
| `text` | runs of text, each with its own colour / font / size / link flag; wrapped by the engine to the width it is given |
| `image` | a texture (a game resource, an atlas icon, a file) with a fit mode |
| `input` | a line or an area of editable text: IMGUI's `InputText` placed in the node's rectangle |
| `canvas` | the retained command list that exists today |

Form controls beyond `input` (checkbox, slider, combo, table) are not node kinds: Weave builds
them from boxes and text, as a web page does, and ships them as components. Until those
exist a Weave window may mix: IMGUI-backed elements inside a native root are out of scope.

### Style

Weave resolves CSS, variables and utility classes; a node gets plain numbers:

- box model: `margin`, `padding`, `border` (4 sides, colour), `radius` (4 corners);
- size: `width`, `height`, `min-*`, `max-*` in pixels, percent of the parent or `auto`; `aspect`;
- layout of children: `display` (block, flex, grid, none), flex (`direction`, `wrap`, `justify`,
  `align`, `gap`), grid (`columns` as tracks, `gap`), per child `grow`, `shrink`, `basis`,
  `span`, `alignSelf`, `order`; `position` (static, absolute with insets);
- paint: `background` (colour, gradient with stops, image + mode), `shadow` (list), `outline`,
  `opacity`, `clip` (overflow hidden / scroll, with scroll offset and scrollbars), `z`,
  `transform` (translate, scale, rotate, origin), `cursor`;
- text: `font`, `size`, `color`, `align`, `lineHeight`, `wrap`, `ellipsis`;
- per state: a second and third set for *hovered* and *pressed*, applied by the engine with no
  round trip to Lua - this is what `:hover` and `:active` become;
- `transition`: properties, duration, easing; the engine interpolates when a number changes.

### Lua surface (sketch)

```lua
local root = Ext.UI2.CreateRoot({ id = "my-window", x = 80, y = 120, width = 620, height = 480 })
local box = root:Add("box", { ... style ... })       -- returns a node
local label = box:Add("text", { runs = { { text = "Hello, " }, { text = "Tav", bold = true } } })
box:Insert(node, index)   node:Remove()   node:Move(parent, index)        -- real reordering
node:Set({ background = { 0.1, 0.1, 0.1, 1 }, padding = { 8, 12, 8, 12 } })  -- any subset
node:SetHover({ background = ... })   node:SetPressed({ ... })
node:Transition({ "background", "opacity" }, 0.15, "ease-out")
node.OnClick, .OnRightClick, .OnHoverEnter, .OnHoverLeave, .OnFocus, .OnBlur, .OnWheel,
node.OnDragStart / .OnDrag / .OnDrop, input.OnChange / .OnSubmit       -- queued to Lua
node.Rect, node.ContentSize, root.Size, root.Position                   -- read-only, last frame
root.Open, root:Focus(), root:Destroy()
Ext.UI2.Measure(text, font, size, width) -> w, h                        -- synchronous, for Lua-side decisions
```

### Frame order

1. apply the queued commands; mark what they touched dirty;
2. layout of dirty subtrees: measure text, then flex / grid. The reference is
   `stand/bridge/.../Client/Kit/Layout.lua` - about 150 lines of pure functions over numbers,
   already tested in the game; the engine's version adds margin, padding, percent, min / max,
   shrink and absolute positioning;
3. advance transitions;
4. paint back to front by `z`, pushing a clip rectangle per clipping node and transforming
   the vertices of a transformed subtree after they were emitted;
5. hit test front to back through the inverse transforms; update hovered / pressed; queue
   events; tell the game's input layer whether the overlay took the mouse and the keyboard
   (today's blocking rules of the overlay settings).

### What will not be there without extra render passes

Blur and `backdrop-filter`, blend modes, clipping to a rounded or rotated outline (a clip is an
axis-aligned rectangle), sharp text under scale / rotation.

## On Weave's side

`Client/Kit/BackendImgui.lua` holds everything that touches an IMGUI object behind
`GK.Weave.Backend`; its header lists the functions a backend provides. A second file,
`BackendNative.lua`, implements the same list over `Ext.UI2` and is chosen when
`Ext.UI2` exists. Templates, scripts and styles do not change; what changes for authors is
that weave.md's "Not there yet" list goes away.

## Order of work in the fork (each step useful by itself)

1. `root`, `box`, `text` (one run), colours, padding, block layout, clipping, click / hover
   events - enough for a static panel; scenario: build, read `Rect`, click by injected mouse.
2. flex and grid, percent, min / max; hover / pressed style sets; transitions.
3. text runs and wrapping, `Measure`; `image`; gradients and shadows (reuse of patch 7).
4. `input` over `InputText`; scroll containers with scrollbars; drag handle and resize of roots.
5. `transform`, `z`; drag and drop.

Depends on: textures from files under Vulkan too (DX11 has them, patch 11). Fonts of any size
without atlas rebuilds are there since the move to the v34 base (ImGui 1.93, `FontSize`).
