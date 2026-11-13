-- The game side of the GateKit bridge (docs/contracts/bridge-protocol.md is the contract).
--
-- The host (the `gatekit` command, the MCP server, scenarios; "the stand" below) writes a
-- command file into "Script Extender/Gwynerva_GateKit/";
-- this file polls it, runs the Lua inside, and writes the answer next to it. Files are the
-- channel because Script Extender offers no socket and typing into its console window is fragile.
--
--   queue_<side>.jsonl     one command per line: {"seq", "id", "code", "ui"}   appended by the stand
--   res_<side>_<id>.json   {"id", "ok", "result" | "error", "log"}           written here, removed by the caller
--   state_<side>.json      heartbeat, once a second; "done" = last seq run   written here
--
-- <side> is "client" or "server": each has its own Lua state and its own files.
-- The queue lets several processes talk to one side at once: every command gets a line of its
-- own and every answer a file of its own, so nothing is overwritten before it is read. The
-- stand appends under a lock with ever-growing `seq` numbers and drops lines this side has
-- reported as done; this side runs, in order, every line whose seq is above the last it ran.
-- (Lua here can neither list a folder nor delete a file, so the stand does all the cleaning.)
--
-- Former channel, still served for a stand that does not know the queue:
--   cmd_<side>.json {"id", "code", "ui"} -> res_<side>.json

-- GateKit's API table. Other code reaches it as Mods.GateKit.GK.
GK = GK or {}

local DIR = "Gwynerva_GateKit/"
local POLL_MS = 150
local BEAT_MS = 1000

local JSON_OPTIONS = {
    Beautify = false,
    StringifyInternalTypes = true,
    IterateUserdata = true,
    AvoidRecursion = true,
}

local function toJson(value)
    local ok, text = pcall(Ext.Json.Stringify, value, JSON_OPTIONS)
    if ok then return text end
    return nil
end

-- Turns whatever a command returned into something the answer file can hold.
local function plain(value)
    if value == nil then return nil end
    local kind = type(value)
    if kind == "string" or kind == "number" or kind == "boolean" then return value end
    local text = toJson(value)
    if text then
        local ok, parsed = pcall(Ext.Json.Parse, text)
        if ok then return parsed end
    end
    return tostring(value)
end

local function gameState()
    local ok, state = pcall(function() return Ext.Utils.GetGameState() end)
    if ok and state ~= nil then return tostring(state) end
    return nil
end

--- Reads the listed components of an entity into a plain table (missing ones are skipped).
function GK.Dump(entity, components)
    local out = {}
    if not entity then return out end
    for _, name in ipairs(components) do
        local ok, value = pcall(function() return entity[name] end)
        if ok and value ~= nil then out[name] = plain(value) end
    end
    return out
end

--- Runs a Lua chunk and returns { ok, result | error, log }. `GK.Log(...)` inside the chunk
--- adds lines to `log`. Used by the file channel, the in-game console and the server relay.
function GK.Run(code)
    local logs = {}
    GK.Log = function(...)
        local parts = {}
        for i = 1, select("#", ...) do parts[i] = tostring((select(i, ...))) end
        logs[#logs + 1] = table.concat(parts, " ")
    end

    local answer = { log = logs }
    local chunk, syntaxError = load(code)
    if not chunk then
        answer.ok = false
        answer.error = "syntax: " .. tostring(syntaxError)
        return answer
    end
    local ok, value = xpcall(chunk, function(err)
        local trace = debug and debug.traceback and debug.traceback(tostring(err), 2)
        return trace or tostring(err)
    end)
    answer.ok = ok
    if ok then answer.result = plain(value) else answer.error = tostring(value) end
    return answer
end

--- An answer as text for people: log lines, then the result or the error.
function GK.Format(answer)
    local lines = {}
    for _, line in ipairs(answer.log or {}) do lines[#lines + 1] = line end
    if not answer.ok then
        lines[#lines + 1] = "ERROR: " .. tostring(answer.error)
    elseif answer.result ~= nil then
        local value = answer.result
        if type(value) == "table" then
            local ok, text = pcall(Ext.Json.Stringify, value, { Beautify = true })
            value = ok and text or tostring(value)
        end
        lines[#lines + 1] = tostring(value)
    else
        lines[#lines + 1] = "(no result)"
    end
    return table.concat(lines, "\n")
end

--- Allows or forbids the game's autosaves.
function GK.AutoSave(allowed)
    return pcall(function() Ext.Utils.GetGlobalSwitches().CanAutoSave = allowed and true or false end)
end

function GK.Start(side)
    local queueFile = DIR .. "queue_" .. side .. ".jsonl"
    local cmdFile = DIR .. "cmd_" .. side .. ".json"
    local resFile = DIR .. "res_" .. side .. ".json"
    local stateFile = DIR .. "state_" .. side .. ".json"

    local function readCommand()
        local text = Ext.IO.LoadFile(cmdFile)
        if not text or text == "" then return nil end
        local ok, cmd = pcall(Ext.Json.Parse, text)
        if ok and type(cmd) == "table" and cmd.id ~= nil then return cmd end
        return nil
    end

    -- The queue's complete lines in order. Reading stops at the first line that does not parse:
    -- the stand may be writing it right now, and nothing after it may run before it.
    local function readQueue()
        local text = Ext.IO.LoadFile(queueFile)
        local out = {}
        if not text or text == "" then return out end
        for line in text:gmatch("[^\n]+") do
            local ok, cmd = pcall(Ext.Json.Parse, line)
            if not ok or type(cmd) ~= "table" or type(cmd.seq) ~= "number" or cmd.id == nil then break end
            out[#out + 1] = cmd
        end
        return out
    end

    -- Test runs must not leave autosaves behind: the game mirrors every save to Steam's cloud
    -- folder and brings it back on the next start. (`GK.AutoSave(true)` turns them on again.)
    GK.AutoSave(false)

    -- Commands left over from before this Lua state started must not run again.
    local stale = readCommand()
    local seen = stale and stale.id or nil
    local done = 0
    for _, cmd in ipairs(readQueue()) do
        if cmd.seq > done then done = cmd.seq end
    end

    local state = { side = side, boot = Ext.Utils.MonotonicTime(), gameState = gameState(), queue = 1, done = done }
    Ext.Events.GameStateChanged:Subscribe(function(e)
        state.gameState = tostring(e.ToState)
    end)

    local function run(cmd, file)
        local answer = GK.Run(cmd.code)
        answer.id, answer.side = cmd.id, side
        local text = toJson(answer)
        if not text then
            text = toJson({ id = cmd.id, side = side, ok = false, error = "the answer could not be written as JSON" })
        end
        Ext.IO.SaveFile(file, text)
    end

    local function dispatch(cmd, file)
        if cmd.ui and Ext.UI and Ext.UI.Defer then
            -- The game's UI (Noesis) may only be touched from its own deferred context.
            Ext.UI.Defer(function() run(cmd, file) end)
        else
            run(cmd, file)
        end
    end

    local nextPoll, nextBeat = 0, 0
    Ext.Events.Tick:Subscribe(function()
        local now = Ext.Utils.MonotonicTime()
        if now >= nextBeat then
            nextBeat = now + BEAT_MS
            state.time = now
            state.gameState = gameState() or state.gameState
            Ext.IO.SaveFile(stateFile, toJson(state) or "{}")
        end
        if now < nextPoll then return end
        nextPoll = now + POLL_MS

        for _, cmd in ipairs(readQueue()) do
            if cmd.seq > done then
                done = cmd.seq
                state.done = done
                dispatch(cmd, DIR .. "res_" .. side .. "_" .. tostring(cmd.id) .. ".json")
            end
        end

        local cmd = readCommand()
        if cmd and cmd.id ~= seen then
            seen = cmd.id
            dispatch(cmd, resFile)
        end
    end)
end
