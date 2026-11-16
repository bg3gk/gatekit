Ext.Require("Shared/Bridge.lua")
Ext.Require("Shared/Verbs.lua")
Ext.Require("Shared/EventLog.lua")
Ext.Require("Shared/Attention.lua")
Ext.Require("Shared/ConsoleLog.lua")
Ext.Require("Shared/Complete.lua")
Ext.Require("Shared/Probe.lua")
Ext.Require("Shared/Memory.lua")
Ext.Require("Shared/Trace.lua")
Ext.Require("Client/Api.lua")
Ext.Require("Client/Sound.lua")
Ext.Require("Client/UI.lua")
Ext.Require("Client/Stage.lua")

-- GK.UICatalog: the game's widgets and what their controls do, generated from the game's own
-- XAML. It is not shipped (it is derived from Larian's files); without it GK.Gui works, but
-- GK.Gui.Click cannot replay the XAML chains of a button and Controls lists less.
-- No command generates it yet (research/open-items.md); a file at
-- <Script Extender>/Gwynerva_GateKit/UICatalog.lua that sets GK.UICatalog = { ... } is loaded.
do
    local FILE = "Gwynerva_GateKit/UICatalog.lua"
    local read, text = pcall(Ext.IO.LoadFile, FILE)
    if read and text and text ~= "" then
        -- Script Extender's load is load(text, env): this mod's globals, so the file sees GK.
        local ok, chunk, problem = pcall(load, text, _ENV)
        if not ok then problem = chunk end
        ok = ok and chunk ~= nil
        if ok then ok, problem = pcall(chunk) end
        if not ok then Ext.Utils.PrintWarning("[GateKit] " .. FILE .. " could not be loaded: " .. tostring(problem)) end
    end
end

Ext.Require("Client/Windows.lua")
Ext.Require("Client/XamlChains.lua")
Ext.Require("Client/Pointer.lua")
Ext.Require("Client/WindowReaders.lua")
Ext.Require("Client/Popups.lua")
Ext.Require("Client/Camera.lua")
Ext.Require("Client/Places.lua")
Ext.Require("Client/Focus.lua")
Ext.Require("Client/Aim.lua")
Ext.Require("Client/Act.lua")
Ext.Require("Client/Attention.lua")
GK.Verbs.Guard()
GK.Start("client")

-- Weave is a mod of its own (Gwynerva_Weave, a dependency in meta.lsx); GateKit calls it only
-- through its public API. GK.Weave stays as another name for it.
Weave = Mods.GateKitWeave and Mods.GateKitWeave.Weave
if Weave then
    GK.Weave = Weave
    Ext.Require("Client/Overlay.lua")
    Ext.Require("Client/Console.lua")
    Ext.Require("Client/Panels.lua")
else
    -- The bridge itself (GK.*, the file channel) works without it; the console does not.
    Ext.Utils.PrintError("[GateKit] the Weave mod (Gwynerva_Weave) is not loaded: "
        .. "no console, overlay or HUD button. Enable Weave before GateKit.")
end
