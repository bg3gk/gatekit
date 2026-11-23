-- GK.EventLog: the one record of what happened on this side (server or client), append-only.
--
-- Every row: { seq, tick, t, kind, knows, key?, attention?, scope?, payload? }
--   seq        1, 2, 3 ... within one boot of this side's Lua state
--   tick       this side's tick count (bridge ticks) when it was written
--   t          the game process's monotonic milliseconds (the same clock on both sides)
--   kind       a kind of the registry, Shared/EventKinds.json; Append refuses any other
--   knows      who could know it: "player" (perceived through the game itself) or "omniscient"
--   key        the waiting state a row opens or closes (attention rows)
--   attention  what the row is to an agent: "begin" opens `key` and is news, "hold" opens it
--              quietly, "end" closes it and is news, "release" closes it quietly, "news" is news
--   scope      the stage scope open when it was written (the seq of its scope_begin row)
--   payload    what the kind carries
--
-- The families and kinds are data, in one registry (Shared/EventKinds.json, schemas/events/kinds)
-- that the host reads too. Views read the log, nothing keeps a copy: GK.Attention (the rows that
-- wait), GK.GameLog (the game family), the stage scope and the fixtures (scope rows), GK.Events (the
-- journal in its old shape). The rows go to the bridge's folder as segments of SEGMENT rows,
-- events_<side>_<boot>_<n>.jsonl; events_<side>.json is the head (docs/contracts/
-- bridge-protocol.md, "The event log"). The host reads those files; nothing is asked of the game.
--
-- Kept small by design: only the always-on families are written without being switched on. The
-- others (osiris, status, entity, surface, component) are written only while switched on
-- (GK.EventLog.On), for a check: they go off when its stage scope ends, when their time is up, or
-- with GK.EventLog.Off. The store is bounded by rows and by bytes; the oldest segments go, a
-- `dropped` row says how many rows of which families, rows that still wait for an answer stay (in
-- memory and in the head), and a reader asking since a seq that went is told.

GK.EventLog = GK.EventLog or {}
local L = GK.EventLog

local REGISTRY = "Mods/Gwynerva_GateKit/ScriptExtender/Lua/Shared/EventKinds.json"
-- the registry this side loaded, as it was read: the host places the rows by it
local LOADED = "Gwynerva_GateKit/events_" .. (Ext.IsServer() and "server" or "client") .. "_kinds.json"

--- Reads the registry: L.Families (name -> { always, default, about }) and L.Kinds (name -> {
--- family, class, knows, knowsPerRow, attention, ends, payload }), and copies its text into the
--- channel (events_<side>_kinds.json). A registry that cannot be read is an error: the log does
--- not run on a vocabulary of its own making.
function L.LoadRegistry(text)
    text = text or Ext.IO.LoadFile(REGISTRY, "data")
    if not text or text == "" then error("GK.EventLog: the registry " .. REGISTRY .. " cannot be read") end
    local registry = Ext.Json.Parse(text)
    if type(registry) ~= "table" or registry.schema ~= "events/kinds/1" then
        error("GK.EventLog: " .. REGISTRY .. " is not an events/kinds/1 registry")
    end
    for name, kind in pairs(registry.kinds) do
        if not registry.families[kind.family] then error("GK.EventLog: kind " .. name .. " names no family of the registry") end
    end
    L.Families, L.Kinds = registry.families, registry.kinds
    pcall(Ext.IO.SaveFile, LOADED, text)
end

--- Adds another mod's families and kinds to the registry of this side: `registry` is a table in
--- the registry file's own shape, { families = { name = { always, default, about } }, kinds = {
--- name = { family, class, knows, attention, ends, payload } } }. A name the registry already has
--- with another family (a kind) is refused, and nothing of the call is added; the same entry given
--- again changes nothing. The merged registry is written to the channel (events_<side>_kinds.json),
--- the file the host places this side's rows by. Answers { ok, families, kinds } - how many were
--- added - or { ok = false, error }.
function L.Register(registry)
    if type(registry) ~= "table" then return { ok = false, error = "a registry is a table { families, kinds }" } end
    local families, kinds = registry.families or {}, registry.kinds or {}
    for name, kind in pairs(kinds) do
        if type(kind) ~= "table" or not (families[kind.family] or L.Families[kind.family]) then
            return { ok = false, error = "kind " .. tostring(name) .. " names no family of the registry" }
        end
        local known = L.Kinds[name]
        if known and known.family ~= kind.family then
            return { ok = false, error = "kind " .. tostring(name) .. " is in the registry already, in the family " .. tostring(known.family) }
        end
    end
    local addedFamilies, addedKinds = 0, 0
    for name, family in pairs(families) do
        if not L.Families[name] then
            L.Families[name] = family
            addedFamilies = addedFamilies + 1
            if family.default and L.Switched then L.Switched[name] = {} end
        end
    end
    for name, kind in pairs(kinds) do
        if not L.Kinds[name] then
            L.Kinds[name] = kind
            addedKinds = addedKinds + 1
        end
    end
    if addedFamilies + addedKinds > 0 then
        pcall(function()
            Ext.IO.SaveFile(LOADED, Ext.Json.Stringify({ schema = "events/kinds/1", families = L.Families, kinds = L.Kinds }, { Beautify = false }))
        end)
    end
    return { ok = true, families = addedFamilies, kinds = addedKinds }
end

if not L.Kinds then
    local ok, problem = pcall(L.LoadRegistry)
    if not ok then
        Ext.Utils.PrintError("[GateKit] " .. tostring(problem))
        L.Families, L.Kinds = { log = { always = true, about = "" } }, {}
        -- an empty copy: the host places this boot's rows by its own registry and says so
        pcall(Ext.IO.SaveFile, LOADED, "")
    end
end

local SEGMENT = 128               -- rows per file
local KEEP_SEGMENTS = 32          -- at most this many segments are kept (memory and disk)
local KEEP_BYTES = 4 * 1024 * 1024 -- and at most this many bytes of rows
local DIR = "Gwynerva_GateKit/"
local SIDE = Ext.IsServer() and "server" or "client"

L.Side = SIDE
L.Rows = L.Rows or {}
L.Seq = L.Seq or 0
L.First = L.First or 1            -- the oldest seq kept
L.Ticks = L.Ticks or 0
L.Open = L.Open or {}             -- the waiting view: key -> the row that opened it
L.Switched = L.Switched or {}     -- family -> { untilTick?, scope?, options? } while switched on
L.Lines = L.Lines or {}           -- the JSON text of every kept row, by seq
L.SegmentBytes = L.SegmentBytes or {}
L.Bytes = L.Bytes or 0
L.Counts = L.Counts or {}         -- family -> { rows, bytes, dropped }
L.Pending = L.Pending or nil      -- rows dropped since the last `dropped` row
L.Dirty = L.Dirty or false
L.Cost = L.Cost or { rows = 0, writes = 0, ms = 0, max = 0 }

if not L.DefaultsSet then
    L.DefaultsSet = true
    for name, family in pairs(L.Families) do
        if family.default then L.Switched[name] = {} end
    end
end

local function now() return Ext.Utils.MonotonicTime() end

local function headPath() return DIR .. "events_" .. SIDE .. ".json" end
local function segmentPath(boot, n) return DIR .. "events_" .. SIDE .. "_" .. tostring(boot) .. "_" .. tostring(n) .. ".jsonl" end
local function segmentOf(seq) return math.floor((seq - 1) / SEGMENT) + 1 end

local function stringify(value)
    return Ext.Json.Stringify(value, { Beautify = false, StringifyInternalTypes = true, AvoidRecursion = true })
end

-- The head of the previous Lua state of this side: its boot and last segment. A boot is a number
-- one above the previous one, so it is unique on this game copy across restarts as well.
if not L.Boot then
    local ok, text = pcall(Ext.IO.LoadFile, headPath())
    local head = ok and text and text ~= "" and select(2, pcall(Ext.Json.Parse, text)) or nil
    if type(head) == "table" and tonumber(head.boot) then
        L.PreviousHead = { boot = math.floor(tonumber(head.boot)), segment = tonumber(head.segment) or 0,
                           first = tonumber(head.first) or 1, process = head.process }
    end
    L.Boot = (L.PreviousHead and L.PreviousHead.boot or 0) + 1
    -- The start of the game process this state runs in, as the host named it when it started the
    -- game (process.txt, written by `up`). A previous log of another process is of a world that
    -- is gone: nothing of it is carried (L.Previous).
    local named, process = pcall(Ext.IO.LoadFile, DIR .. "process.txt")
    L.Process = named and type(process) == "string" and process ~= "" and process or nil
end

local function counts(family)
    local c = L.Counts[family]
    if not c then
        c = { rows = 0, bytes = 0, dropped = 0 }
        L.Counts[family] = c
    end
    return c
end

-- The head: which boot, which segments, what still waits (the open rows, so the waiting view
-- outlives the drop of their segment), and the per-family counters.
local function writeHead()
    local open = {}
    for _, row in pairs(L.Open) do open[#open + 1] = row end
    table.sort(open, function(a, b) return a.seq < b.seq end)
    local families = {}
    for name, c in pairs(L.Counts) do families[name] = { rows = c.rows, bytes = c.bytes, dropped = c.dropped } end
    local on = {}
    for name in pairs(L.Switched) do on[#on + 1] = name end
    table.sort(on)
    local head = { side = SIDE, boot = L.Boot, segment = math.max(1, segmentOf(math.max(1, L.Seq))), first = segmentOf(L.First),
                   seq = L.Seq, rows = SEGMENT, started = L.Started, tick = L.Ticks, process = L.Process, open = open, counts = families, on = on }
    pcall(Ext.IO.SaveFile, headPath(), stringify(head))
end

local function flush(segment)
    local first, last = (segment - 1) * SEGMENT + 1, math.min(segment * SEGMENT, L.Seq)
    local lines = {}
    for seq = math.max(first, L.First), last do
        lines[#lines + 1] = L.Lines[seq]
    end
    if #lines == 0 then return end
    local started = Ext.Timer.MicrosecTime()
    pcall(Ext.IO.SaveFile, segmentPath(L.Boot, segment), table.concat(lines, "\n") .. "\n")
    local spent = (Ext.Timer.MicrosecTime() - started) / 1000
    local cost = L.Cost
    cost.writes, cost.ms = cost.writes + 1, cost.ms + spent
    if spent > cost.max then cost.max = spent end
end

-- The oldest segment goes: its rows leave memory (a row that still waits stays, and is in the
-- head), its file is emptied, and what went is counted for the next `dropped` row.
local function retireOldest()
    local segment = segmentOf(L.First)
    local last = math.min(segment * SEGMENT, L.Seq)
    local pending = L.Pending or { rows = 0, bytes = 0, families = {} }
    for seq = L.First, last do
        local row, line = L.Rows[seq], L.Lines[seq]
        if row then
            local family = L.Kinds[row.kind].family
            local bytes = line and #line or 0
            pending.rows, pending.bytes = pending.rows + 1, pending.bytes + bytes
            pending.families[family] = (pending.families[family] or 0) + 1
            counts(family).dropped = counts(family).dropped + 1
            if not (row.key and L.Open[row.key] == row) then L.Rows[seq] = nil end
        end
        L.Lines[seq] = nil
    end
    L.Bytes = L.Bytes - (L.SegmentBytes[segment] or 0)
    L.SegmentBytes[segment] = nil
    pcall(Ext.IO.SaveFile, segmentPath(L.Boot, segment), "")
    L.First = last + 1
    pending.first = L.First
    L.Pending = pending
end

--- Appends a row and answers it. `kind` must be in the registry (an error at once otherwise);
--- options.key names the waiting state of an attention row; options.knows = "omniscient" marks a
--- row of a kind the registry lets say so per row (knowsPerRow) that this time the party did not
--- perceive; options.class is the class of the verb that made a row of a classPerRow kind. A row
--- of a family that is off writes nothing (nil).
function L.Append(kind, payload, options)
    local spec = L.Kinds[kind]
    if not spec then error("GK.EventLog.Append: kind " .. tostring(kind) .. " is not in the registry (" .. REGISTRY .. ")", 2) end
    local family = L.Families[spec.family]
    if not family.always and not L.Switched[spec.family] then return nil end
    local started = Ext.Timer.MicrosecTime()
    local seq = L.Seq + 1
    if seq > 1 and segmentOf(seq) ~= segmentOf(seq - 1) then
        -- the segment is full: written once more as a whole, then never again
        flush(segmentOf(seq - 1))
        L.HeadDirty = true
    end
    L.Seq = seq
    local row = { seq = seq, tick = L.Ticks, t = now(), kind = kind, knows = spec.knows }
    if options and options.knows == "omniscient" and spec.knowsPerRow then row.knows = "omniscient" end
    if options and options.class and spec.classPerRow then row.class = options.class end
    if options and options.key then row.key = options.key end
    if spec.attention then row.attention = spec.attention end
    if L.Scope then row.scope = L.Scope end
    if payload ~= nil and (type(payload) ~= "table" or next(payload) ~= nil) then row.payload = payload end
    local ok, text = pcall(stringify, row)
    if not ok then
        row.payload = { unwritable = tostring(text) }
        text = stringify(row)
    end
    L.Rows[seq] = row
    L.Lines[seq] = text
    local segment = segmentOf(seq)
    L.SegmentBytes[segment] = (L.SegmentBytes[segment] or 0) + #text
    L.Bytes = L.Bytes + #text
    local c = counts(spec.family)
    c.rows, c.bytes = c.rows + 1, c.bytes + #text
    -- the bound: whole segments go, never the one being written
    while segmentOf(L.First) < segment and (segment - segmentOf(L.First) + 1 > (L.KeepSegments or KEEP_SEGMENTS) or L.Bytes > (L.KeepBytes or KEEP_BYTES)) do
        retireOldest()
    end
    L.Dirty = true
    L.Cost.rows = L.Cost.rows + 1
    -- the waiting view
    if row.key then
        local was = L.Open[row.key]
        -- a waiting row kept past its segment's drop goes once it is replaced or answered
        if was and was.seq < L.First then L.Rows[was.seq] = nil end
        if spec.attention == "begin" or spec.attention == "hold" then
            L.Open[row.key] = row
            L.HeadDirty = true
        elseif spec.attention == "end" or spec.attention == "release" then
            L.Open[row.key] = nil
            L.HeadDirty = true
        end
    end
    local listeners = L.Listeners
    if listeners then
        for _, fn in pairs(listeners) do pcall(fn, row) end
    end
    L.Cost.appendMs = (L.Cost.appendMs or 0) + (Ext.Timer.MicrosecTime() - started) / 1000
    return row
end

--- A row by its seq, while it is kept.
function L.Row(seq) return L.Rows[seq] end

local function matches(row, filter)
    if not filter then return true end
    local spec = L.Kinds[row.kind]
    if filter.kinds and not filter.kinds[row.kind] then return false end
    if filter.family and spec.family ~= filter.family then return false end
    if filter.scope and row.scope ~= filter.scope then return false end
    if filter.attention and not row.attention then return false end
    if filter.knows and row.knows ~= filter.knows then return false end
    return true
end

--- The rows after `seq` (0 or nil: every row kept), in order: { boot, seq = the last, first =
--- the oldest kept, rows, dropped? }. filter: { kinds = { name = true }, family, scope,
--- attention = true, knows, limit }. `dropped` (the number of rows after `seq` that are gone)
--- is there when rows the caller asked for went: never a silent gap.
function L.Since(seq, filter)
    seq = seq or 0
    local out = {}
    local limit = filter and filter.limit
    for s = math.max(seq + 1, L.First), L.Seq do
        local row = L.Rows[s]
        if row and matches(row, filter) then
            out[#out + 1] = row
            if limit and #out >= limit then break end
        end
    end
    local result = { boot = L.Boot, seq = L.Seq, first = L.First, rows = out }
    if seq + 1 < L.First then result.dropped = L.First - seq - 1 end
    return result
end

--- The rows of the previous Lua state of this side in this game process, read back from its files
--- (nil when there is none, or when it ran in another process: its world is gone, and what it
--- staged with it). For what must outlive a reload: the fixtures' open rows, the stage's strays.
function L.Previous()
    local head = L.PreviousHead
    if not head or head.segment < 1 then return nil end
    if (L.Process or head.process) and head.process ~= L.Process then return nil end
    if L.PreviousRows then return L.PreviousRows end
    local rows = {}
    for n = head.first or 1, head.segment do
        local ok, text = pcall(Ext.IO.LoadFile, segmentPath(head.boot, n))
        if ok and text then
            for line in text:gmatch("[^\n]+") do
                local fine, row = pcall(Ext.Json.Parse, line)
                if fine and type(row) == "table" then rows[#rows + 1] = row end
            end
        end
    end
    L.PreviousRows = rows
    return rows
end

-- ---------------------------------------------------------------------------------------
-- Families switched on and off by name

L.Hooks = L.Hooks or {}   -- family -> { on = fn(options), off = fn() }, registered by a side's files

local function names(families)
    local list = type(families) == "table" and families or (families and { families } or {})
    for _, name in ipairs(list) do
        local family = L.Families[name]
        if not family then error("GK.EventLog: no family " .. tostring(name) .. " in the registry", 3) end
        if family.always then error("GK.EventLog: family " .. name .. " is always on", 3) end
    end
    return list
end

--- Switches families on (a name or a list of the registry's families). A family not on by default
--- stays on for options.seconds (default 60, at most 600) and goes off when the stage scope open
--- now ends; options.components names the component types of the `component` family. Answers the
--- families on now.
function L.On(families, options)
    options = options or {}
    local list = names(families)
    local seconds = math.min(600, math.max(1, tonumber(options.seconds) or 60))
    local turned = {}
    for _, name in ipairs(list) do
        local default = L.Families[name].default
        -- the time a family stays on is of a running game: counted in this side's ticks
        local state = { scope = not default and L.Scope or nil, untilTick = not default and GK.Ticks() + GK.LimitTicks(seconds * 1000) or nil, options = options }
        if not L.Switched[name] then
            turned[#turned + 1] = name
            local hook = L.Hooks[name]
            if hook and hook.on then
                local ok, problem = pcall(hook.on, options)
                if not ok then error("GK.EventLog.On(" .. name .. "): " .. tostring(problem), 2) end
            end
        end
        L.Switched[name] = state
    end
    if #turned > 0 then L.Append("family_on", { families = turned, seconds = seconds, scope = L.Scope }) end
    L.HeadDirty = true
    return L.SwitchedOn()
end

--- Switches families off (nil: every family that is not on by default). Answers the families on now.
--- How much of the log this Lua state keeps (memory and the channel's files): `segments` of 128
--- rows (default 32, at most 256) and `bytes` (default 4 MB, at most 32 MB); nil puts a bound back
--- to its default. A scenario that writes more rows than the default keeps (a story change of
--- level with the osiris family on: ~5,000) raises it so its first rows are not retired before it
--- is read. Answers the bounds now. W3 round 4 of the start-zone night.
function L.Keep(segments, bytes)
    L.KeepSegments = segments and math.max(2, math.min(256, math.floor(segments))) or nil
    L.KeepBytes = bytes and math.max(65536, math.min(32 * 1024 * 1024, math.floor(bytes))) or nil
    return { segments = L.KeepSegments or KEEP_SEGMENTS, bytes = L.KeepBytes or KEEP_BYTES }
end

function L.Off(families, why)
    local list = families and names(families) or nil
    if not list then
        list = {}
        for name in pairs(L.Switched) do if not L.Families[name].default then list[#list + 1] = name end end
    end
    local turned = {}
    for _, name in ipairs(list) do
        if L.Switched[name] then
            L.Switched[name] = nil
            turned[#turned + 1] = name
            local hook = L.Hooks[name]
            if hook and hook.off then pcall(hook.off) end
        end
    end
    table.sort(turned)
    if #turned > 0 then L.Append("family_off", { families = turned, why = why }) end
    L.HeadDirty = true
    return L.SwitchedOn()
end

--- The families on now, by name.
function L.SwitchedOn()
    local out = {}
    for name in pairs(L.Switched) do out[#out + 1] = name end
    table.sort(out)
    return out
end

--- The stage scope `scope` ended (GK.Stage.Clean, a new Begin): the families it switched on go off.
function L.ScopeEnded(scope)
    local list = {}
    for name, state in pairs(L.Switched) do
        if state.scope and state.scope == scope then list[#list + 1] = name end
    end
    if #list > 0 then L.Off(list, "the scope ended") end
end

-- ---------------------------------------------------------------------------------------
-- Writing, once per tick (called by the bridge's tick)

function L.Tick()
    L.Ticks = L.Ticks + 1
    local late
    for name, state in pairs(L.Switched) do
        if state.untilTick and GK.Ticks() >= state.untilTick then late = late or {}; late[#late + 1] = name end
    end
    if late then L.Off(late, "its time was up") end
    if L.Pending then
        local p = L.Pending
        L.Pending = nil
        L.Append("dropped", { rows = p.rows, bytes = p.bytes, families = p.families, first = p.first })
    end
    if L.Dirty then
        L.Dirty = false
        flush(segmentOf(L.Seq))
        writeHead()
        L.HeadDirty = false
    elseif L.HeadDirty then
        L.HeadDirty = false
        writeHead()
    end
end

--- What the log costs and holds: { rows, writes, ms (writing files), appendMs (making rows),
--- max, perWriteMs, ticks, perTickMs, seq, boot, first, bytes, families = { name = { rows, bytes,
--- dropped } }, on }.
function L.Costs()
    local c = L.Cost
    local families = {}
    for name, n in pairs(L.Counts) do families[name] = { rows = n.rows, bytes = n.bytes, dropped = n.dropped } end
    local spent = c.ms + (c.appendMs or 0)
    return { rows = c.rows, writes = c.writes, ms = c.ms, appendMs = c.appendMs or 0, max = c.max,
             perWriteMs = c.writes > 0 and c.ms / c.writes or 0, ticks = L.Ticks, perTickMs = L.Ticks > 0 and spent / L.Ticks or 0,
             seq = L.Seq, boot = L.Boot, first = L.First, bytes = L.Bytes, families = families, on = L.SwitchedOn() }
end

-- ---------------------------------------------------------------------------------------
-- The journal in its first shape, kept as a view: GK.Note(kind, data) and GK.Events(since).

--- A caller's own mark in the log (kind `note`).
function GK.Note(kind, data)
    L.Append("note", { note = tostring(kind), data = data })
end

--- A step of a test begins (kind `step`): what the host writes before it runs the step's body,
--- so any row after it and before its `step_ended` belongs to the step. Answers the row's seq.
function GK.Step(name, class, case)
    local row = L.Append("step", { name = tostring(name), class = class, case = case })
    return row and row.seq
end

--- A step of a test ended (kind `step_ended`): outcome pass, fail or unmet; ms on the host; the
--- case it ran in, if the test has cases.
function GK.StepEnded(name, class, outcome, ms, why, case)
    local row = L.Append("step_ended", { name = tostring(name), class = class, outcome = outcome, ms = ms, error = why, case = case })
    return row and row.seq
end

--- The journal: rows after `since` as { n, t, kind, data } - an Osiris row under its event's
--- name, a note under its own, every other row under its kind. { last, events, dropped? }.
function GK.Events(since)
    local out = {}
    local got = L.Since(since)
    for _, row in ipairs(got.rows) do
        local p = row.payload
        if row.kind == "osiris" then out[#out + 1] = { n = row.seq, t = row.t, kind = p.event, data = p.args }
        elseif row.kind == "note" then out[#out + 1] = { n = row.seq, t = row.t, kind = p.note, data = p.data }
        else out[#out + 1] = { n = row.seq, t = row.t, kind = row.kind, data = p } end
    end
    return { last = L.Seq, events = out, dropped = got.dropped }
end

if not L.Subscribed then
    L.Subscribed = true
    L.Started = now()
    writeHead()
    Ext.Events.GameStateChanged:Subscribe(function(e)
        pcall(L.Append, "game_state", { from = tostring(e.FromState), to = tostring(e.ToState) })
    end)
end

return L
