-- GateKit AI: a bridge for an AI agent that plays (server side).
-- `AI` is a global of this mod's Lua environment; other mods and GateKit's host reach it as
-- Mods.GateKitAI.AI. It is built from GateKit's verbs: without GateKit the mod loads nothing.

AI = AI or {}
AI.GK = Mods.GateKit and Mods.GateKit.GK
if not AI.GK then
    Ext.Utils.PrintError("[GateKit AI] GateKit is not loaded: the mod depends on it (meta.lsx) and does nothing without it")
    return
end

Ext.Require("Shared/Scenes.lua")
Ext.Require("Server/Bridge.lua")
Ext.Require("Server/Mode.lua")
Ext.Require("Server/Say.lua")
Ext.Require("Server/Plan.lua")
Ext.Require("Server/Now.lua")
Ext.Require("Server/Step.lua")
