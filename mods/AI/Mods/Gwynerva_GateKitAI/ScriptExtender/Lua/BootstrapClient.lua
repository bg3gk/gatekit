-- GateKit AI: a bridge for an AI agent that plays (client side).
-- `AI` is a global of this mod's Lua environment; other mods reach it as Mods.GateKitAI.AI.

AI = AI or {}
AI.GK = Mods.GateKit and Mods.GateKit.GK
if not AI.GK then
    Ext.Utils.PrintError("[GateKit AI] GateKit is not loaded: the mod depends on it (meta.lsx) and does nothing without it")
    return
end

Ext.Require("Shared/Encode.lua")
Ext.Require("Shared/Scenes.lua")
Ext.Require("Shared/Stand.lua")
Ext.Require("Client/Bridge.lua")
Ext.Require("Client/Tool.lua")
