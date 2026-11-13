Ext.Require("Shared/Bridge.lua")
Ext.Require("Shared/Events.lua")
Ext.Require("Shared/ConsoleLog.lua")
Ext.Require("Shared/Complete.lua")
Ext.Require("Shared/Probe.lua")
Ext.Require("Server/Api.lua")
Ext.Require("Server/Stage.lua")
Ext.Require("Server/Speed.lua")
Ext.Require("Server/Pause.lua")
Ext.Require("Server/Situation.lua")
Ext.Require("Server/Conditions.lua")
Ext.Require("Server/Act.lua")
Ext.Require("Server/Cheat.lua")
Ext.Require("Server/Heard.lua")
Ext.Require("Server/GameLog.lua")
Ext.Require("Server/Focus.lua")
Ext.Require("Server/SaveState.lua")
GK.Start("server")

-- The in-game console (a client window) asks the server to do things through these channels.
local PREFIX = "local GK = Mods.GateKit.GK\n"

--   GK_Run     { ticket, code, kind = "run" | "watch" | "ask" }  ->  GK_Result { ticket, kind, ok, text[, value] }
local result = Ext.Net.CreateChannel(ModuleUUID, "GK_Result")
Ext.Net.CreateChannel(ModuleUUID, "GK_Run", function(msg, sender)
    -- "run": a command typed into the console. "watch" and "ask": quiet evaluations.
    local kind = msg.kind
    if kind == "run" then GK.LogContext = "Console" end
    local answer = GK.Run(PREFIX .. tostring(msg.code))
    GK.LogContext = nil
    local text = GK.Format(answer)
    if not answer.ok then text = GK.ShortTrace(text) end
    if kind == "watch" then text = GK.CutUtf8(text, 400) end
    if kind == "run" then GK.FlushLog() end
    local reply = { ticket = msg.ticket, kind = kind, ok = answer.ok, text = GK.CutUtf8(text, 20000) }
    if kind == "ask" and answer.ok then reply.value = answer.result end
    result:SendToClient(reply, sender)
end)

--   GK_Complete  { id, text }  ->  GK_Completions { id, base, prefix, matches }
local completions = Ext.Net.CreateChannel(ModuleUUID, "GK_Completions")
Ext.Net.CreateChannel(ModuleUUID, "GK_Complete", function(msg, sender)
    local found = GK.Complete(tostring(msg.text))
    found.id = msg.id
    completions:SendToClient(found, sender)
end)

--   GK_Reset: the console's "Reload Lua" button. The client restarts itself afterwards.
Ext.Net.CreateChannel(ModuleUUID, "GK_Reset", function()
    Ext.Debug.Reset()
end)
