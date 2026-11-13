-- Weave: a UI framework for Script Extender mods (client side only).
-- `Weave` is a global of this mod's Lua environment (as every mod's globals are, under Script
-- Extender); other mods reach it as Mods.GateKitWeave.Weave. Scripts inside .weave files
-- see it as `Weave` too.

Weave = {}

-- File order matters: BackendImgui calls Dom's helpers lazily because it loads first.
Ext.Require("Client/Xml.lua")
Ext.Require("Client/Css.lua")
Ext.Require("Client/Reactive.lua")
Ext.Require("Client/Expr.lua")
Ext.Require("Client/BackendImgui.lua")
Ext.Require("Client/Dom.lua")
Ext.Require("Client/Component.lua")
Ext.Require("Client/Utilities.lua")
Ext.Require("Client/Layout.lua")
Ext.Require("Client/Shell.lua")
