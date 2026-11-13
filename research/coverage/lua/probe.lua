-- Probe: read-only inspection of live component memory, for working out structure layouts.
-- Needs the GateKit build of Script Extender in developer mode (Ext.Debug.ReadMemory,
-- GetComponentAddress). Loaded by research/coverage/probe.py in front of every call; it
-- defines the table and keeps no state, so loading it again is harmless.
--
--   Probe.Holders(name, max)        entities that carry the component (handles as integers)
--   Probe.Dump(name, max)           raw bytes of up to `max` instances, with every 8-byte
--                                   slot classified (see Probe.Classify)
--   Probe.Read(address, size)       hex of any readable memory, or nil
--   Probe.Classify(value)           what a 64-bit value looks like: "zero", "entity" (a live
--                                   entity handle on this side), "module" (points into the
--                                   game executable; Rva is given), "heap" (points to
--                                   readable memory outside the executable) or "value"
--
-- Nothing here writes to the game.

Probe = {}

local module = Ext.Debug.GetModuleInfo()

local function hex(bytes)
    return (bytes:gsub(".", function(c) return string.format("%02x", c:byte()) end))
end

function Probe.Read(address, size)
    local bytes = Ext.Debug.ReadMemory(address, size)
    return bytes and hex(bytes) or nil
end

function Probe.Classify(value)
    if value == 0 then
        return { Kind = "zero" }
    end
    if value >= module.Base and value < module.Base + module.Size then
        return { Kind = "module", Rva = value - module.Base }
    end
    -- User-mode pointers on x64 Windows: canonical, above the first 64 KiB, 8-byte aligned
    -- for anything the engine allocates.
    if value > 0x10000 and value < 0x7FFFFFFFFFFF and value % 8 == 0
        and Ext.Debug.ReadMemory(value, 8) ~= nil then
        return { Kind = "heap" }
    end
    -- A handle keeps its salt and type in the upper half; a small integer is never one, although
    -- IsAlive() may say yes to it.
    if value >= 0 and value < 0x100000000 then
        return { Kind = "value" }
    end
    local alive, handle = pcall(function()
        local entity = Ext.Utils.IntegerToHandle(value)
        return entity:IsAlive() and entity or nil
    end)
    if alive and handle then
        return { Kind = "entity" }
    end
    return { Kind = "value" }
end

-- Where the component's own bytes are: components larger than their inline slot are stored
-- behind a pointer.
local function body(entity, name)
    local at = Ext.Debug.GetComponentAddress(entity, name)
    if at == nil then return nil end
    local address = at.Address
    if at.InlineSize < at.TotalSize then
        local pointer = Ext.Debug.ReadMemory(address, 8)
        if pointer == nil then return nil end
        address = string.unpack("<i8", pointer)
    end
    return address, at.TotalSize, at.InlineSize
end

Probe.Body = body

function Probe.Holders(name, max)
    if Ext.Debug.GetComponentHolders then  -- walks storage classes: fast, and sees one-frame components
        return Ext.Debug.GetComponentHolders(name, max)
    end
    local found = {}
    for _, entity in ipairs(Ext.Entity.GetAllEntities()) do
        if Ext.Debug.GetComponentAddress(entity, name) ~= nil then
            found[#found + 1] = entity
            if max and #found >= max then break end
        end
    end
    return found
end

-- A value read through Script Extender's own layout, in a form that survives JSON and can be
-- compared with raw bytes: entities as integers, arrays and other tables as their length.
local plain
plain = function(value)
    local kind = type(value)
    if kind == "number" or kind == "boolean" or kind == "string" or kind == "nil" then
        return value
    end
    if kind == "userdata" then
        local ok, integer = pcall(Ext.Utils.HandleToInteger, value)
        if ok and integer then return { Entity = integer } end
        local okLength, length = pcall(function() return #value end)
        if okLength then
            local items = {}
            pcall(function()
                for i = 1, math.min(length, 16) do items[i] = plain(value[i]) end
            end)
            return { Length = length, Items = items }
        end
        -- maps and sets: the number of entries
        local okCount, count = pcall(function()
            local n = 0
            for _ in pairs(value) do n = n + 1 end
            return n
        end)
        if okCount then return { Count = count } end
    end
    if kind == "table" then
        local items = {}
        for i, item in ipairs(value) do items[i] = plain(item) end
        return { Items = items }
    end
    return { Other = tostring(value) }
end

-- Probe.Mapped(engineName, extName, fields, max): for up to `max` holders of the component,
-- the raw bytes and the same fields read as entity.<extName>.<field>. Present is false where
-- Script Extender does not give the component although the entity carries it.
function Probe.Mapped(engineName, extName, fields, max)
    local out = {}
    for _, entity in ipairs(Probe.Holders(engineName, max or 16)) do
        local address, total = body(entity, engineName)
        local bytes = address and total > 0 and Ext.Debug.ReadMemory(address, total)
        -- an unmapped name raises an error instead of returning nil
        local known, component = pcall(function() return entity[extName] end)
        if not known then component = nil end
        local row = { Entity = Ext.Utils.HandleToInteger(entity), Hex = bytes and hex(bytes) or "",
                      Present = component ~= nil, Values = {} }
        if component ~= nil then
            for _, field in ipairs(fields) do
                local ok, value = pcall(function() return component[field] end)
                if ok then row.Values[field] = plain(value) else row.Values[field] = { Error = tostring(value) } end
            end
        end
        out[#out + 1] = row
    end
    return out
end

-- Probe.MappedMany(specs, max): Probe.Mapped for many types in one call;
-- specs = { { Name = engine name, ExtName = ..., Fields = {...} }, ... } -> { [Name] = rows }
function Probe.MappedMany(specs, max)
    local out = {}
    for _, spec in ipairs(specs) do
        out[spec.Name] = Probe.Mapped(spec.Name, spec.ExtName, spec.Fields or {}, max)
    end
    return out
end

function Probe.Dump(name, max)
    local out = { Name = name, Instances = {} }
    for _, entity in ipairs(Probe.Holders(name, max or 16)) do
        local address, total, inline = body(entity, name)
        out.TotalSize, out.InlineSize = total, inline
        local bytes = address and total > 0 and Ext.Debug.ReadMemory(address, total)
        if bytes then
            local slots = {}
            for offset = 0, total - 8, 8 do
                local slot = Probe.Classify(string.unpack("<i8", bytes, offset + 1))
                slot.Offset = offset
                slots[#slots + 1] = slot
            end
            local others = {}
            for _, other in pairs(entity:GetAllComponentNames()) do others[#others + 1] = other end
            out.Instances[#out.Instances + 1] = {
                Entity = Ext.Utils.HandleToInteger(entity),
                Address = address, Hex = hex(bytes), Slots = slots, ComponentCount = #others,
            }
        end
    end
    return out
end

-- Capture: sampling over ticks, for components that live for a frame or a moment
-- (one-frame events, requests, things that exist only while something happens).
-- Needs Ext.Debug.GetComponentHolders / GetComponentCounts of the GateKit build.
--
--   Probe.CaptureStart(specs, perType)   specs: { { Name = engine name, ExtName = ..., Fields = {...} }, ... }
--                                        (ExtName and Fields are optional); every tick, new holders
--                                        of each type are sampled until `perType` samples are kept:
--                                        raw bytes and, when ExtName is given, the fields read
--                                        through Script Extender in the same tick
--   Probe.CaptureCensus()                also records, every tick, how many entities carry each
--                                        component type (the highest count and the first tick seen)
--   Probe.CaptureResult()                { Ticks, Samples = { [name] = { {Entity, Hex, Tick, Present, Values} } },
--                                          Seen = { [name] = { Max, FirstTick } } }
--   Probe.CaptureStop()
--
-- The state survives between calls in the global ProbeCaptureState.

function Probe.CaptureStop()
    local state = ProbeCaptureState
    if state and state.Subscription then
        Ext.Events.Tick:Unsubscribe(state.Subscription)
        state.Subscription = nil
    end
end

local function sampleType(state, spec)
    local kept = state.Samples[spec.Name]
    if kept == nil then kept = {}; state.Samples[spec.Name] = kept end
    if #kept >= state.PerType then return end
    for _, entity in ipairs(Ext.Debug.GetComponentHolders(spec.Name, state.PerType * 4)) do
        local handle = Ext.Utils.HandleToInteger(entity)
        local key = spec.Name .. "#" .. tostring(handle)
        if not state.Taken[key] then
            state.Taken[key] = true
            local address, total = body(entity, spec.Name)
            local bytes = address and total > 0 and Ext.Debug.ReadMemory(address, total)
            local row = { Entity = handle, Hex = bytes and hex(bytes) or "", Tick = state.Ticks, Values = {} }
            if spec.ExtName then
                local known, component = pcall(function() return entity[spec.ExtName] end)
                row.Present = known and component ~= nil
                if row.Present then
                    for _, field in ipairs(spec.Fields or {}) do
                        local ok, value = pcall(function() return component[field] end)
                        if ok then row.Values[field] = plain(value) else row.Values[field] = { Error = tostring(value) } end
                    end
                end
            end
            kept[#kept + 1] = row
            if #kept >= state.PerType then return end
        end
    end
end

function Probe.CaptureStart(specs, perType)
    Probe.CaptureStop()
    local state = { Specs = specs or {}, PerType = perType or 8, Samples = {}, Taken = {}, Seen = {},
                    Ticks = 0, Census = false, Errors = {} }
    ProbeCaptureState = state
    state.Subscription = Ext.Events.Tick:Subscribe(function()
        state.Ticks = state.Ticks + 1
        for _, spec in ipairs(state.Specs) do
            local ok, problem = pcall(sampleType, state, spec)
            if not ok and #state.Errors < 5 then state.Errors[#state.Errors + 1] = tostring(problem) end
        end
        if state.Census then
            for name, count in pairs(Ext.Debug.GetComponentCounts()) do
                local seen = state.Seen[name]
                if seen == nil then
                    state.Seen[name] = { Max = count, FirstTick = state.Ticks }
                elseif count > seen.Max then
                    seen.Max = count
                end
            end
        end
    end)
    return #state.Specs
end

function Probe.CaptureCensus()
    if ProbeCaptureState == nil then Probe.CaptureStart({}, 0) end
    ProbeCaptureState.Census = true
    return true
end

function Probe.CaptureResult()
    local state = ProbeCaptureState
    if state == nil then return nil end
    return { Ticks = state.Ticks, Samples = state.Samples, Seen = state.Seen, Errors = state.Errors,
             Running = state.Subscription ~= nil }
end

-- Seen: a standing capture of everything that comes to life. While it runs, every component
-- type that has holders is counted each tick, and its first few distinct instances are
-- sampled (raw bytes) - whatever the game is doing, with no list prepared in advance.
--
--   Probe.SeenStart(options)   options: Specs ({ [engine name] = { ExtName, Fields } }: such types are
--                              also read through Script Extender in the tick they are sampled),
--                              PerLabel (samples kept per type and label, default 3),
--                              PerType (cap per type, default 12), MaxBytes (default 1024),
--                              Label (the first label)
--   Probe.SeenMark(label)      names what happens from now on ("fight", "dialogue with X")
--   Probe.SeenDrain(maxRows)   takes up to maxRows queued samples out:
--                              { Rows = { {Name, Entity, Hex, Size, Tick, Label} }, Left = n }
--   Probe.SeenTypes()          { [name] = { Max, First = tick, Label = label it first appeared under } }
--   Probe.SeenStop()
--
-- The state lives in the global ProbeSeenState and is lost when the Lua state is (a world
-- reset): drain before, start again after.

function Probe.SeenStop()
    local state = ProbeSeenState
    if state and state.Subscription then
        Ext.Events.Tick:Unsubscribe(state.Subscription)
        state.Subscription = nil
    end
    return state and state.Ticks or 0
end

local function seenSample(state, name, entry)
    local label = state.Label
    local kept = entry.PerLabel[label] or 0
    for _, entity in ipairs(Ext.Debug.GetComponentHolders(name, 24)) do
        if kept >= state.PerLabel or entry.Kept >= state.PerType then break end
        local handle = Ext.Utils.HandleToInteger(entity)
        if not entry.Taken[handle] then
            entry.Taken[handle] = true
            local address, total = body(entity, name)
            if address and total and total > 0 then
                local bytes = Ext.Debug.ReadMemory(address, math.min(total, state.MaxBytes))
                if bytes and not entry.Bytes[bytes] then
                    entry.Bytes[bytes] = true
                    kept = kept + 1
                    entry.Kept = entry.Kept + 1
                    local row = { Name = name, Entity = handle, Hex = hex(bytes), Size = total,
                                  Tick = state.Ticks, Label = label }
                    -- a type Script Extender has a layout for is also read through it, in this
                    -- same tick: the two readings are compared outside (seen.py)
                    local spec = state.Specs and state.Specs[name]
                    if spec then
                        local known, component = pcall(function() return entity[spec.ExtName] end)
                        row.Present = known and component ~= nil
                        if row.Present then
                            row.Values = {}
                            for _, field in ipairs(spec.Fields or {}) do
                                local ok, value = pcall(function() return component[field] end)
                                if ok then row.Values[field] = plain(value) else row.Values[field] = { Error = tostring(value) } end
                            end
                        end
                    end
                    state.Queue[#state.Queue + 1] = row
                end
            end
        end
    end
    entry.PerLabel[label] = kept
end

function Probe.SeenStart(options)
    Probe.SeenStop()
    options = options or {}
    local state = { PerLabel = options.PerLabel or 3, PerType = options.PerType or 12,
                    MaxBytes = options.MaxBytes or 1024, Label = options.Label or "idle",
                    Specs = options.Specs,  -- { [engine name] = { ExtName = ..., Fields = { ... } } }
                    Types = {}, Queue = {}, Ticks = 0, Errors = {} }
    ProbeSeenState = state
    state.Subscription = Ext.Events.Tick:Subscribe(function()
        state.Ticks = state.Ticks + 1
        local ok, problem = pcall(function()
            for name, count in pairs(Ext.Debug.GetComponentCounts()) do
                if count > 0 then
                    local entry = state.Types[name]
                    if entry == nil then
                        entry = { Max = 0, First = state.Ticks, Label = state.Label, Kept = 0, Taken = {}, Bytes = {},
                                  PerLabel = {}, LastCount = -1, LastLabel = "" }
                        state.Types[name] = entry
                    end
                    if count > entry.Max then entry.Max = count end
                    -- holders are walked again only when their number differs from the last
                    -- look or the label is new
                    if entry.Kept < state.PerType and (entry.PerLabel[state.Label] or 0) < state.PerLabel
                        and (count ~= entry.LastCount or entry.LastLabel ~= state.Label) then
                        entry.LastCount, entry.LastLabel = count, state.Label
                        seenSample(state, name, entry)
                    end
                end
            end
        end)
        if not ok and #state.Errors < 5 then state.Errors[#state.Errors + 1] = tostring(problem) end
    end)
    return true
end

function Probe.SeenMark(label)
    if ProbeSeenState == nil then return false end
    ProbeSeenState.Label = label
    return true
end

function Probe.SeenDrain(maxRows)
    local state = ProbeSeenState
    if state == nil then return { Rows = {}, Left = 0, Running = false } end
    local rows, queue = {}, state.Queue
    local take = math.min(maxRows or 150, #queue)
    for i = 1, take do rows[i] = queue[i] end
    local rest = {}
    for i = take + 1, #queue do rest[#rest + 1] = queue[i] end
    state.Queue = rest
    return { Rows = rows, Left = #rest, Running = state.Subscription ~= nil, Ticks = state.Ticks, Errors = state.Errors }
end

--- The names of the types that appeared since the last call (a cheap per-step question; the
--- samples themselves are fetched by SeenDrain when convenient).
function Probe.SeenNew()
    local state = ProbeSeenState
    local out = {}
    if state == nil then return out end
    for name, entry in pairs(state.Types) do
        if not entry.Reported then
            entry.Reported = true
            out[#out + 1] = name
        end
    end
    return out
end

function Probe.SeenTypes()
    local state = ProbeSeenState
    if state == nil then return {} end
    local out = {}
    for name, entry in pairs(state.Types) do
        out[name] = { Max = entry.Max, First = entry.First, Label = entry.Label }
    end
    return out
end
