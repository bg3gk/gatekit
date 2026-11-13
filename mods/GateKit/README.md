# GateKit bridge mod

The game side of GateKit: a Script Extender mod that lets the `gatekit` command, the MCP
server and scenarios run Lua inside Baldur's Gate 3 and read the answers (through files, see
`docs/contracts/bridge-protocol.md`), plus the in-game developer console (F11 or `!gk`).

**Development only.** Installed by modders and by GateKit itself on a test game; do not ship
it with a mod or play with it. It needs the [Weave](../Weave/README.md) mod (a dependency in
`meta.lsx`, loaded before it) and GateKit's Script Extender build.

Folder `Gwynerva_GateKit`, UUID `b1d5c0de-7e57-4b1d-9e0a-6d6576646b69`, Lua mod table
`Mods.GateKit`; the API table is `Mods.GateKit.GK` (types: `lua-types/GateKit.lua`).

## How other mods use it

Commands sent by the host run with `local GK = Mods.GateKit.GK` in front. A mod can add
its own panel to the console, and do nothing when GateKit is absent:

```lua
Ext.OnNextTick(function()
    local GK = Mods.GateKit and Mods.GateKit.GK
    if not (GK and GK.UI) then return end
    GK.UI.Panel("My Mod", function(p)
        p:Button("Count party", { server = "return #GK.Party()" })
    end)
end)
```

## Files it keeps

Under `Script Extender/Gwynerva_GateKit/`: the command channel (`queue_*`, `res_*`, `state_*`),
`console.json`, `overlay.json` (the console key, the HUD button). Theme, text size and input
blocking are Weave's (`Script Extender/Gwynerva_Weave/settings.json`).

`UICatalog.lua` (optional): the game's widgets and what their buttons do, generated from the
game's own XAML. It is not part of this repository; `gatekit ui-catalog` will generate it from
your installed game (not written yet). When present in this folder it is loaded at start and
`GK.Gui.Click` replays the XAML chains of buttons; without it `GK.Gui` still works.

Console commands: `!gk` (console), `!gkui` (settings window), `!gkrescue` (windows back on screen).
