-- GK.Attention: what waits for the player, or changed - a view of the event log (GK.EventLog),
-- kept current by hooks, not found by asking.
--
-- A model that plays cannot be interrupted between its tool calls; what it can have is an answer
-- that says, every time, "a dialogue is waiting", "the fight started", "Shadowheart died". Those
-- are rows of the event log whose kind has an attention class (GK.EventLog.Kinds): a "begin" row
-- opens a waiting state under its key, an "end" row closes it, a "news" row is told and waits for
-- nothing; "hold" and "release" open and close quietly (a dialogue line being spoken). The host
-- reads the rows from the log's files after every game call, and its wait (bg3_wait) watches them.
--
--   GK.Attention.Push(what, fields)       a news row; returns its seq
--   GK.Attention.Set(key, item | nil)     a waiting state on (item = { what, window?, set?, ... })
--                                         or off; a change of it is a row
--   GK.Attention.Waiting                  key -> { what, at, seq, tick, ... }: the open states
--   GK.Attention.State() / Since(seq)     the view in the shape the hooks and tests read
--
-- Hooks live in Server/Attention.lua and Client/Attention.lua; pollers run here every tick.

GK.Attention = GK.Attention or {}
local A = GK.Attention
local L = GK.EventLog

A.Seq = A.Seq or 0      -- the seq of the last row an agent is told (begin, end, news)
A.Boot = L.Boot

local function now() return Ext.Utils.MonotonicTime() end

--- A row as an item: { seq, what = kind, at = t, tick, key?, ...payload }.
local function item(row)
    if not row then return nil end
    local out = { seq = row.seq, what = row.kind, at = row.t, tick = row.tick, key = row.key }
    for k, v in pairs(row.payload or {}) do
        if out[k] == nil then out[k] = v end
    end
    return out
end
A.Item = item

-- The open waiting states, read through the log's own view (GK.EventLog.Open): no copy is kept.
A.Waiting = setmetatable({}, {
    __index = function(_, key) return item(L.Open[key]) end,
    __newindex = function() error("GK.Attention.Waiting is a view of the event log: use GK.Attention.Set", 2) end,
    __pairs = function()
        local key
        return function()
            local row
            key, row = next(L.Open, key)
            if key == nil then return nil end
            return key, item(row)
        end
    end,
})

local TOLD = { begin = true, ["end"] = true, news = true }

L.Listeners = L.Listeners or {}
L.Listeners.attention = function(row)
    if TOLD[row.attention] then A.Seq = row.seq end
end

--- A news row of kind `what` (a news kind of GK.EventLog.Kinds). Returns its seq. `options` as
--- GK.EventLog.Append takes them.
function A.Push(what, fields, options)
    local row = L.Append(what, fields, options)
    return row and row.seq or A.Seq
end

local function same(row, new)
    if row == nil or new == nil then return row == new end
    local p = row.payload or {}
    return row.kind == new.what and p.window == new.window and p.set == new.set
end

--- A waiting state on (item: { what, window?, set?, choices?, ... }) or off (nil). `what` is a
--- begin or hold kind; a new `set` or another `what` is a change, written as a row. Off writes the
--- kind's end row (its `ends`), with `ended` as its payload beside the opening row's window.
function A.Set(key, new, ended)
    local old = L.Open[key]
    if same(old, new) then return end
    if new then
        local payload = {}
        for k, v in pairs(new) do if k ~= "what" and k ~= "at" then payload[k] = v end end
        L.Append(new.what, payload, { key = key })
    elseif old then
        local payload = { opened = old.seq, window = old.payload and old.payload.window or nil }
        for k, v in pairs(ended or {}) do payload[k] = v end
        L.Append(L.Kinds[old.kind].ends, payload, { key = key })
    end
end

local function waitingList()
    local list = {}
    for key, row in pairs(L.Open) do list[#list + 1] = item(row) end
    table.sort(list, function(a, b) return a.seq < b.seq end)
    return list
end

--- The view as a whole: { side, boot, seq, now, tick, waiting, events } - events are the last 64
--- rows an agent is told.
function A.State()
    local rows = L.Since(L.Seq - 512, { attention = true }).rows
    local events = {}
    for i = math.max(1, #rows - 63), #rows do
        if TOLD[rows[i].attention] then events[#events + 1] = item(rows[i]) end
    end
    return { side = L.Side, boot = L.Boot, seq = L.Seq, now = now(), tick = L.Ticks, waiting = waitingList(), events = events }
end

--- The last seq of the log: a caller remembers it and asks GK.Attention.Since(seq) later.
function A.Mark() return L.Seq end

--- What waits now, and the rows an agent is told after `seq`: { seq, waiting, events }.
function A.Since(seq)
    local out = {}
    for _, row in ipairs(L.Since(seq, { attention = true }).rows) do
        if TOLD[row.attention] then out[#out + 1] = item(row) end
    end
    return { seq = L.Seq, waiting = waitingList(), events = out }
end

-- Pollers: functions a side registers (GK.Attention.Pollers[name] = function(now)) that look at
-- something cheap every tick; the expensive ones throttle themselves.
A.Pollers = A.Pollers or {}
A.Cost = A.Cost or { ticks = 0, ms = 0, max = 0 }

local function micro() return Ext.Timer.MicrosecTime() end

--- Called by the bridge every tick: runs the pollers.
function A.Tick(t)
    local started = micro()
    for name, poll in pairs(A.Pollers) do
        local ok, problem = pcall(poll, t)
        if not ok and not A.Failed then
            A.Failed = name .. ": " .. tostring(problem)
            Ext.Utils.PrintWarning("[GateKit] attention poller " .. A.Failed)
        end
    end
    local spent = (micro() - started) / 1000
    local cost = A.Cost
    cost.ticks, cost.ms = cost.ticks + 1, cost.ms + spent
    if spent > cost.max then cost.max = spent end
end

--- What the hooks cost: { ticks, ms, max, perTickMs, uiLooks, uiMs, uiMax, perUiLookMs, log }.
function A.Costs()
    local c = A.Cost
    return { ticks = c.ticks, ms = c.ms, max = c.max, perTickMs = c.ticks > 0 and c.ms / c.ticks or 0,
             uiLooks = c.uiLooks, uiMs = c.uiMs, uiMax = c.uiMax,
             perUiLookMs = c.uiLooks and c.uiLooks > 0 and c.uiMs / c.uiLooks or nil, log = L.Costs() }
end

-- Loads, saves and the menus, on both sides: the game's own state machine.
local function onState(to)
    to = tostring(to)
    if to == "Running" or to == "Paused" then
        A.Set("state", nil)
    elseif to == "Menu" or to == "InitMenu" or to == "LoadMenu" then
        A.Set("state", { what = "menu", state = to })
    else
        A.Set("state", { what = to:find("Save", 1, true) and "saving" or "loading", state = to })
    end
end

if not A.StateSubscribed then
    A.StateSubscribed = true
    Ext.Events.GameStateChanged:Subscribe(function(e) pcall(onState, e.ToState) end)
end

return A
