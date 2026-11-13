# Weave

A UI framework for Baldur's Gate 3 Script Extender mods. A window is a component: a template,
a script and a style in one `.weave` file, with reactive state, themes shared by every mod, and
hot reload (save the file, the window changes while the game runs). Client side only.

**Who installs it:** players of any mod that draws its windows with Weave, and modders who
write such windows. It needs GateKit's Script Extender build on the v34 base; on another build
`Weave.Mount` returns nil and Weave says why once in the console.

Folder `Gwynerva_Weave`, UUID `8ee3d1c7-0362-41bc-b7c9-b10d85c62f4b`. Declare it under
`Dependencies` in your mod's `meta.lsx` and load it before your mod.

## Using it from another mod

`Weave` is a global of Weave's own Lua environment; from another mod it is
`Mods.GateKitWeave.Weave`. Inside `.weave` scripts it is simply `Weave`.

```lua
local Weave = Mods.GateKitWeave and Mods.GateKitWeave.Weave
if not Weave then return end                       -- Weave is not installed
Weave.RegisterComponent("hello", [[
<template>
  <window id="hello" title="Hello" width="300">
    <text>Clicked {{ count }} times</text>
    <button @click="count++">Click</button>
  </window>
</template>
<script> count = 0 </script>]])
local doc = Weave.Mount("hello") if doc then doc:Open() end
```

Instead of `RegisterComponent`, put `hello.weave` into your mod and call
`Weave.AddRoot("Mods/<YourMod>/ScriptExtender/Lua/ui/")` once.

## What every Weave window shares

- **Theme and text size:** `Weave.Settings.Get/Set/OnChange`; kept in
  `Script Extender/Gwynerva_Weave/settings.json`.
- **Input blocking:** while a Weave window is open the game gets no keys or clicks (both
  switchable); Esc closes the window opened last. Hand-built IMGUI windows take part through
  `Weave.RegisterWindow{ isOpen, close, reset }`; a mod's own hotkey is claimed with `Weave.OnKey`.
- **Rescue:** `Weave.Rescue()` or `!weaverescue` puts every window back on screen.
- **Demo:** `Weave.Demo(true)` or `!weavedemo` opens a window with every element of the kit.
- **Showcase:** `Weave.Showcase(true)` or `!weaveshowcase` opens `ui/showcase.weave`, a dashboard
  of the character you control, read from the running game every second: stat cards, an
  ability chart and a hit-point ring drawn on canvases, equipment and spell icons from the
  game's own atlases, badges, progress bars (`:label` sets the text over a bar) and buttons.
  One file, no pictures of its own; a worked example of a real-looking window.
- **Your copies win:** `Weave.Eject("theme.css")` writes an editable copy to
  `Script Extender/Gwynerva_Weave/ui/`; files there override the built-in ones.
  (Before Weave was its own mod, that folder was `Gwynerva_GateKit/ui/`; move old copies over.)

The Lua API with types: `lua-types/Weave.lua`. Layout: `ScriptExtender/Lua/Client/*.lua` (the
kit, load order in `BootstrapClient.lua`), `ScriptExtender/Lua/ui/` (built-in components and sheets).
