-- Catches what Lua prints on this side, so the in-game console can show it.
--
-- What is caught:
--   * every print / _P / _PW / _PE / Ext.Utils.Print* / Ext.Log.* call made by a mod's own code:
--     Script Extender raises Ext.Events.Log for those, with the mod's uuid and the level;
--   * print / _P / _D / _DS ... called from code that runs in the global environment
--     (the Script Extender console, chunks run through GK.Run): the globals are wrapped here.
-- What is not, on the stock Script Extender: anything it writes from C++ - a Lua error inside
-- another mod's event handler, Osiris errors, its own warnings. No Lua event exists for those;
-- read them with `gatekit.py logs`. The GateKit's own build (docs/se-fork.md) has the event, and
-- those lines are caught too, as source "Script Extender" (part 3 below).
--
--   GK.ConsoleEmit(level, source, text[, extra])   add a line yourself
--   GK.LogSinks                                    functions called with every entry
--
-- An entry: { side, level = "Debug"|"Info"|"Warning"|"Error", source = <mod name>,
--             tag = <text of a leading [tag]> | nil, text, t = <ms> , kind = nil|"cmd"|"result" }

local SIDE = Ext.IsServer() and "server" or "client"
local MAX_TEXT = 6000

GK.LogSinks = GK.LogSinks or {}

--- Cuts a string to at most `max` bytes without splitting a UTF-8 character.
function GK.CutUtf8(text, max)
    if #text <= max then return text end
    local cut = max
    -- step back over continuation bytes (10xxxxxx) so the cut lands on a character boundary
    while cut > 0 do
        local byte = text:byte(cut + 1)
        if not byte or byte < 0x80 or byte >= 0xC0 then break end
        cut = cut - 1
    end
    return text:sub(1, cut)
end

--- An error text without the part of the traceback that belongs to the GateKit itself
--- (everything from the xpcall that ran the chunk downwards).
function GK.ShortTrace(text)
    local cut = text:find("\n[^\n]*%[C%+%+ Code%]: in function 'xpcall'")
    return cut and text:sub(1, cut - 1) or text
end

local names = {}
local function modName(uuid)
    if not uuid then return "(global)" end
    local known = names[uuid]
    if known then return known end
    local name
    pcall(function() name = Ext.Mod.GetMod(uuid).Info.Name end)
    name = (name and name ~= "") and name or tostring(uuid)
    names[uuid] = name
    return name
end

function GK.ConsoleEmit(level, source, text, extra)
    text = tostring(text)
    local entry = extra or {}
    entry.side = SIDE
    entry.level = level or "Info"
    entry.source = source or "(global)"
    if #text > MAX_TEXT then
        local full = #text
        text = GK.CutUtf8(text, MAX_TEXT) .. "\n... (" .. (full - MAX_TEXT) .. " more bytes cut)"
    end
    entry.text = text
    entry.tag = text:match("^%s*%[([^%]\r\n]+)%]")
    if entry.tag and #entry.tag > 40 then entry.tag = nil end
    entry.t = Ext.Utils.MonotonicTime()
    for _, sink in ipairs(GK.LogSinks) do
        local ok = pcall(sink, entry)
        if not ok then break end
    end
    return entry
end

local function join(args, count)
    local parts = {}
    for i = 1, count do parts[i] = tostring(args[i]) end
    return table.concat(parts, "\t")
end

-- What this Script Extender build can do beyond the stock one (docs/se-fork.md). Test for the
-- feature, never for a version.
GK.Features = GK.Features or {}
do
    local function has(probe)
        local ok, value = pcall(probe)
        return ok and value and true or false
    end
    -- Ext.Events.<any name> is never nil: an unknown name is a stub without NextIndex
    GK.Features.consoleMessages = has(function() return Ext.Events.ConsoleMessage.NextIndex ~= nil end)
    GK.Features.osirisNames = has(function() return Ext.Osiris.GetFunctions ~= nil end)
    GK.Features.clipboard = has(function() return Ext.IMGUI.SetClipboardText ~= nil end)
    GK.Features.injectText = has(function() return Ext.Input.InjectText ~= nil end)
end

-- With console messages available, a line a mod prints arrives twice: as a Log event (or
-- through the wrapped globals) and, a tick later, as a console message. The first one knows
-- the mod and the level, so the second is dropped: texts seen here are remembered for a moment.
local echoes = {}     -- text -> { count, until }
local ECHO_MS = 3000

local function squeeze(text)
    return (tostring(text):gsub("%s+", " "):gsub("^ ", ""):gsub(" $", ""))
end

local function expectEcho(text)
    if not GK.Features.consoleMessages then return end
    local key = squeeze(text)
    local echo = echoes[key]
    if not echo then echo = { count = 0 } echoes[key] = echo end
    echo.count = echo.count + 1
    echo.till = Ext.Utils.MonotonicTime() + ECHO_MS
end

local function isEcho(text)
    local key = squeeze(text)
    local echo = echoes[key]
    if not echo then return false end
    echo.count = echo.count - 1
    if echo.count <= 0 then echoes[key] = nil end
    return echo.till >= Ext.Utils.MonotonicTime()
end

-- 1. Mods' own output.
Ext.Events.Log:Subscribe(function(e)
    local message = e.Message or {}
    local count = 0
    for index in pairs(message) do
        if type(index) == "number" and index > count then count = index end
    end
    local extra
    if e.Topic and e.Topic ~= "" then extra = { topic = tostring(e.Topic) } end
    local text = join(message, count)
    expectEcho(text)
    GK.ConsoleEmit(tostring(e.Level), modName(e.Module and tostring(e.Module)), text, extra)
end)

-- 2. Output of code running in the global environment. A mod's file sees its own environment
-- as _G; the real global table is what that environment falls back to.
local meta = getmetatable(_G)
local real = meta and type(meta.__index) == "table" and meta.__index or nil
if real and not rawget(real, "__GK_print_wrapped") then
    rawset(real, "__GK_print_wrapped", true)
    local function wrap(name, level, describe)
        local original = rawget(real, name)
        if type(original) ~= "function" then return end
        rawset(real, name, function(...)
            local ok, text = pcall(describe, ...)
            if ok then
                expectEcho(text)
                GK.ConsoleEmit(level, GK.LogContext or "(global)", text)
            end
            return original(...)
        end)
    end
    local function plain(...) return join({ ... }, select("#", ...)) end
    wrap("print", "Info", plain)
    wrap("Print", "Info", plain)
    wrap("_P", "Info", plain)
    wrap("_PW", "Warning", plain)
    wrap("_PE", "Error", plain)
    wrap("_D", "Info", function(value) return Ext.DumpExport(value) end)
    wrap("_DS", "Info", function(value)
        return Ext.Json.Stringify(value, {
            Beautify = true, StringifyInternalTypes = true, IterateUserdata = true,
            AvoidRecursion = true, LimitDepth = 1, LimitArrayElements = 3,
        })
    end)
end

-- 3. With the GateKit's Script Extender build: every line of the Script Extender console,
-- which adds what C++ writes (a Lua error inside another mod's event handler, Osiris errors,
-- the extender's own warnings). The stock build has no such event.
--
-- Measured: that console is one per process. Every line reaches the client's and the server's
-- Lua with the same Index, whichever side caused it, and a plain print has the type "Osiris".
-- So each side recognises the lines it printed itself (they are in the log already, with the
-- mod and the level), the server tells the client their numbers, and the client adds a line
-- only when neither side claimed it. Which side such a line came from is not known: it is
-- filed under the client, where it is caught.
if GK.Features.consoleMessages then
    local LEVEL = { Debug = "Debug", Info = "Info", Warning = "Warning", Error = "Error", Osiris = "Info" }
    local HOLD_MS = 1200          -- how long the client waits for the server to claim a line
    local claimed = {}            -- server: numbers of its own lines, not yet sent
    local pending, order = {}, {} -- client: lines nobody has claimed yet, by number
    local channel

    -- When both sides print the same text, each takes the first such line for its own, which
    -- may be the same line. The client notices (the server claims a number the client took
    -- itself) and gives the server's claim to the other line with that text: one waiting
    -- already, or, as a credit, the next to arrive.
    local own = {}                -- client: number -> { key, at } of lines it claimed itself
    local credits = {}            -- client: text -> { count, till }
    local shown = {}              -- client: number -> { entry, at } of lines logged a moment ago
    local early = {}              -- client: number -> time of a claim that came before its line
    local stats = { late = 0, unsent = 0, early = 0 }
    GK.EchoStats = stats

    -- The server claimed a line the client had taken for its own: its claim goes to the other
    -- line with that text.
    local function passOn(key)
        for _, other in ipairs(order) do
            local line = pending[other]
            if line and line.key == key then pending[other] = nil return end
        end
        local credit = credits[key] or { count = 0 }
        credit.count = credit.count + 1
        credit.till = Ext.Utils.MonotonicTime() + ECHO_MS
        credits[key] = credit
    end

    if SIDE == "server" then
        channel = Ext.Net.CreateChannel(ModuleUUID, "GK_Echo")
    else
        Ext.Net.CreateChannel(ModuleUUID, "GK_Echo", function(msg)
            for _, index in ipairs(msg.indices or {}) do
                local taken = own[index]
                local late = shown[index]
                if pending[index] then
                    pending[index] = nil
                elseif late then
                    -- the claim came after the wait was over (the server was busy, or could not
                    -- send yet): the line is in the log already and is taken back
                    shown[index] = nil
                    late.entry.retracted = true
                    stats.late = stats.late + 1
                    if GK.LogRetracted then pcall(GK.LogRetracted, late.entry) end
                elseif taken then
                    passOn(taken.key)
                else
                    -- the claim overtook the line: the server's tick and its message got in
                    -- before this side was handed the line (seen right after a save is loaded,
                    -- when the client's frames are slow). Remembered until the line comes.
                    early[index] = Ext.Utils.MonotonicTime()
                end
            end
        end)
    end

    Ext.Events.ConsoleMessage:Subscribe(function(e)
        local text = tostring(e.Message or ""):gsub("[\r\n]+$", "")
        local index = tonumber(e.Index)
        if not index then return end
        local mine = isEcho(text)
        if SIDE == "server" then
            if mine then claimed[#claimed + 1] = index end
            return
        end
        local now = Ext.Utils.MonotonicTime()
        local key = squeeze(text)
        if early[index] then
            early[index] = nil
            stats.early = stats.early + 1
            if mine then passOn(key) end
        elseif mine then
            own[index] = { key = key, at = now }
        elseif text ~= "" then
            local credit = credits[key]
            if credit then
                credit.count = credit.count - 1
                if credit.count <= 0 then credits[key] = nil end
                if credit.till >= now then return end
            end
            local level = LEVEL[tostring(e.Type)] or "Info"
            -- the extender's timing notices ("Dispatching event Tick (...) took 3.2 ms") come as
            -- warnings, dozens a minute: filed as Debug so the Debug box hides them
            if level == "Warning" and text:find("^Dispatching .+ took [%d%.]+ ms$") then level = "Debug" end
            pending[index] = { level = level, text = text, key = key, at = now }
            order[#order + 1] = index
        end
    end)

    local nextSend = 0
    Ext.Events.Tick:Subscribe(function()
        local now = Ext.Utils.MonotonicTime()
        if next(echoes) ~= nil then
            for key, echo in pairs(echoes) do
                if echo.till < now then echoes[key] = nil end
            end
        end
        if SIDE == "server" then
            if #claimed > 0 and now >= nextSend then
                nextSend = now + 100
                local batch = claimed
                -- nobody to tell yet (menu, loading): the numbers wait, up to a limit
                if pcall(function() channel:Broadcast({ indices = batch }) end) then
                    claimed = {}
                else
                    stats.unsent = stats.unsent + 1
                    if #claimed > 5000 then claimed = {} end
                end
            end
            return
        end
        if now >= nextSend then
            nextSend = now + 1000
            for index, taken in pairs(own) do
                if now - taken.at > 2 * ECHO_MS then own[index] = nil end
            end
            for index, late in pairs(shown) do
                if now - late.at > 30000 then shown[index] = nil end
            end
            for index, at in pairs(early) do
                if now - at > 30000 then early[index] = nil end
            end
            for key, credit in pairs(credits) do
                if credit.till < now then credits[key] = nil end
            end
        end
        while order[1] do
            local index = order[1]
            local line = pending[index]
            if line and now - line.at < HOLD_MS then break end
            table.remove(order, 1)
            if line then
                pending[index] = nil
                shown[index] = { entry = GK.ConsoleEmit(line.level, "Script Extender", line.text, { cpp = true }), at = now }
            end
        end
    end)
end

-- The server's lines travel to the client, where the console window lives.
if SIDE == "server" then
    local channel = Ext.Net.CreateChannel(ModuleUUID, "GK_Log")
    local queue, dropped = {}, 0
    local MAX_QUEUE, BATCH, EVERY_MS = 2000, 60, 200

    GK.LogSinks[#GK.LogSinks + 1] = function(entry)
        if #queue >= MAX_QUEUE then
            table.remove(queue, 1)
            dropped = dropped + 1
        end
        queue[#queue + 1] = entry
    end

    local function flush(everything)
        while #queue > 0 do
            local batch = {}
            for i = 1, math.min(BATCH, #queue) do batch[i] = queue[i] end
            local sent = pcall(function() channel:Broadcast({ entries = batch, dropped = dropped }) end)
            if not sent then return end  -- nobody to send to yet (menu, loading): keep the lines
            dropped = 0
            local rest = {}
            for i = #batch + 1, #queue do rest[#rest + 1] = queue[i] end
            queue = rest
            if not everything then return end
        end
    end

    --- Sends the waiting lines now (the console calls it before a command's result goes out,
    --- so what the command printed arrives first).
    function GK.FlushLog() pcall(flush, true) end

    local nextFlush = 0
    Ext.Events.Tick:Subscribe(function()
        if #queue == 0 then return end
        local now = Ext.Utils.MonotonicTime()
        if now < nextFlush then return end
        nextFlush = now + EVERY_MS
        flush(false)
    end)
    Ext.Events.Shutdown:Subscribe(function() pcall(flush, true) end)
end
