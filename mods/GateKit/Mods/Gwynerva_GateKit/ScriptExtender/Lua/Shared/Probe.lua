-- Probe: read-only inspection of live component memory, for working out structure layouts,
-- and Seen: a standing capture of whatever comes to life while the game is driven. Together
-- they are how the engine is mapped - a type is noticed when it first has holders, its bytes
-- are kept, and a layout hypothesis is read off the per-offset survey the host prints.
--
-- It lives in the bridge, so an agent reaches all of it through the MCP tools alone.
--
-- Needs the GateKit build of Script Extender in developer mode (Ext.Debug.ReadMemory,
-- GetComponentAddress, GetComponentHolders, GetComponentCounts). On the stock extender
-- GK.Probe.Available() says so and the calls answer with that reason instead of raising.
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

-- The bridge loads this file at startup on both sides, so nothing here may raise: on the stock
-- extender, or with developer mode off, GetModuleInfo is missing or refuses. Classify then
-- cannot name an address in the executable, and every entry point answers through Available().
local module
do
    local ok, info = pcall(function() return Ext.Debug.GetModuleInfo() end)
    module = ok and info or { Base = 0, Size = 0 }
end

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
        elseif not address then
            -- held, and no memory of its own to read: a type whose presence is all there is
            -- (D17 of the 2026-11-14 day run: eoc::heal::MaxIncomingComponent had a holder and
            -- the probe said "nothing alive carries it", because only readable instances counted)
            out.Instances[#out.Instances + 1] = { Entity = Ext.Utils.HandleToInteger(entity), Hex = "", NoData = true, Slots = {} }
        end
    end
    return out
end

-- Probe.View: a layout read against the bytes, with no C++ at all
--
-- A layout hypothesis - this field is a float at 0, that one a bool at 4 - is written down long
-- before anybody declares it in the Script Extender fork, and until then an agent testing it
-- reads hex. That is the slow half of the only loop that matters here: name a field, look again,
-- was I right. Everything needed to close it is already in the game: Probe reads the raw bytes,
-- and the hypothesis says what sits where.
--
-- So the layout is applied here, in Lua. A component the extender has never heard of reads as
-- { Timer = 5.0, TimerStarted = true, field_8 = 0 }, and the loop becomes: edit the hypothesis,
-- `bridge-reload`, look. Declarations in the fork are then written once per batch of findings
-- that are already known to be right, and one restart validates many instead of one per field.
--
-- Every field also carries its own bytes, because a field read under the wrong type is the thing
-- this is most likely to be wrong about, and the hex is what shows it.

--- How many bytes a field type takes, and how to unpack it. Unknown types are not guessed at:
--- they come back as bytes, which is honest and still useful.
local FORMATS = {
    ["bool"] = { 1, "B" }, ["uint8_t"] = { 1, "B" }, ["int8_t"] = { 1, "b" },
    ["uint16_t"] = { 2, "<I2" }, ["int16_t"] = { 2, "<i2" },
    ["uint32_t"] = { 4, "<I4" }, ["int32_t"] = { 4, "<i4" },
    ["uint64_t"] = { 8, "<I8" }, ["int64_t"] = { 8, "<i8" },
    ["float"] = { 4, "<f" }, ["double"] = { 8, "<d" },
    -- A handle and a pointer are both a word; what they mean is Probe.Classify's business.
    ["EntityHandle"] = { 8, "<I8" },
    -- FixedString is an index into the game's string table, not the string.
    ["FixedString"] = { 4, "<I4" },
}

--- The bytes of one field, as hex, and its value where the type is one we read.
local function readField(bytes, field)
    local at = (field.Offset or 0) + 1
    local format = FORMATS[field.Type]
    local width = format and format[1]
    -- An array or anything else: the bytes up to the next field, or the end.
    if not width then width = (field.Width and field.Width > 0) and field.Width or 8 end
    if at < 1 or at + width - 1 > #bytes then
        return { Type = field.Type, Offset = field.Offset, Error = "outside the component" }
    end
    local raw = bytes:sub(at, at + width - 1)
    local out = { Type = field.Type, Offset = field.Offset, Hex = hex(raw) }
    if format then
        local ok, value = pcall(string.unpack, format[2], raw)
        if ok then
            if field.Type == "bool" then value = value ~= 0 end
            -- A float that is not a number has no JSON to be written as, and it used to vanish
            -- from the answer - leaving a field that looks unread rather than one that holds
            -- 0xffc00000. Bytes read as the wrong type land here constantly, so it is said.
            if type(value) == "number" and (value ~= value or value == math.huge or value == -math.huge) then
                out.Value, out.NotANumber = nil, tostring(value)
            else
                out.Value = value
            end
            if field.Type == "EntityHandle" then out.Points = Probe.Classify(select(1, string.unpack("<i8", raw))) end
        else
            out.Error = tostring(value)
        end
    end
    return out
end

--- Probe.View(name, fields, max): up to `max` live instances of the component, each read through
--- the given layout. `fields` = { { Name, Offset, Type, Width }, ... }; Width is only needed for
--- a type this does not know, and defaults to the bytes up to the next field.
---
--- Nothing here asks Script Extender for the component: it reads the memory the entity holds, so
--- it works for a component the extender has never declared. That is the point of it.
function Probe.View(name, fields, max)
    -- Fill in the width of the types we do not know from where the next field starts, so an
    -- array or an unknown struct shows all of its bytes rather than an arbitrary eight.
    local ordered = {}
    for i, field in ipairs(fields or {}) do ordered[i] = field end
    table.sort(ordered, function(a, b) return (a.Offset or 0) < (b.Offset or 0) end)

    local out = { Name = name, Instances = {} }
    for _, entity in ipairs(Probe.Holders(name, max or 4)) do
        local address, total = body(entity, name)
        local bytes = address and total > 0 and Ext.Debug.ReadMemory(address, total)
        out.TotalSize = total
        if bytes then
            for i, field in ipairs(ordered) do
                if not FORMATS[field.Type] and not field.Width then
                    local nextAt = ordered[i + 1] and ordered[i + 1].Offset or total
                    field.Width = math.max(1, (nextAt or total) - (field.Offset or 0))
                end
            end
            local values = {}
            for _, field in ipairs(ordered) do values[field.Name] = readField(bytes, field) end
            out.Instances[#out.Instances + 1] = {
                Entity = Ext.Utils.HandleToInteger(entity),
                Address = address, Hex = hex(bytes), Fields = values,
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

-- Named reads: a field by its name, from the record's layouts ------------------------------------
--
-- The host writes the layouts of research/coverage/layouts into the channel (layouts.json) at
-- `up` and at every bridge reload, so a field reads by the name the record gives it - through the
-- same raw read as Probe.View, with no extender rebuilt. A name reaches a running copy when the
-- layouts are merged and the bridge is reloaded.

local LAYOUTS_FILE = "Gwynerva_GateKit/layouts.json"

--- The layouts the host handed over: { [engine name] = { size, status, fields = { [name] = { offset, type } } } }.
--- Read once and kept; `reload` reads the file again. nil and why when there is none.
function Probe.Layouts(reload)
    if Probe.LayoutCache and not reload then return Probe.LayoutCache end
    local text = Ext.IO.LoadFile(LAYOUTS_FILE)
    if not text or text == "" then return nil, "no " .. LAYOUTS_FILE .. " (the host writes it at `up` and `bridge-reload`)" end
    local ok, data = pcall(Ext.Json.Parse, text)
    if not ok or type(data) ~= "table" or type(data.components) ~= "table" then return nil, LAYOUTS_FILE .. " does not read" end
    Probe.LayoutCache = data.components
    return data.components
end

--- The fields of `component` as Probe.View takes them, or nil and why.
local function layoutFields(component)
    local layouts, why = Probe.Layouts()
    if not layouts then return nil, why end
    local layout = layouts[component]
    if not layout then return nil, "no layout for " .. tostring(component) .. " in the record" end
    local fields = {}
    for name, at in pairs(layout.fields or {}) do
        fields[#fields + 1] = { Name = name, Offset = at[1], Type = at[2] }
    end
    table.sort(fields, function(a, b) return a.Offset < b.Offset end)
    for i, field in ipairs(fields) do
        if not FORMATS[field.Type] then
            local nextAt = fields[i + 1] and fields[i + 1].Offset or layout.size or (field.Offset + 8)
            field.Width = math.max(1, nextAt - field.Offset)
        end
    end
    return fields, layout
end

--- One field of one entity's component, by the record's name: the value (a number, a boolean, an
--- entity handle as an integer, or the hex of a type not read here), and what it was read as.
--- nil and why when it cannot be read.
function Probe.Field(entity, component, name)
    if type(entity) == "string" then entity = Ext.Entity.Get(entity) end
    if type(entity) == "number" then entity = Ext.Utils.IntegerToHandle(entity) end
    if not entity then return nil, "no such entity" end
    local fields, layout = layoutFields(component)
    if not fields then return nil, layout end
    local wanted
    for _, field in ipairs(fields) do if field.Name == name then wanted = field end end
    if not wanted then return nil, "the layout of " .. component .. " has no field " .. tostring(name) end
    local address, total = body(entity, component)
    if not address then return nil, "the entity does not carry " .. component end
    local bytes = Ext.Debug.ReadMemory(address, math.max(total or 0, wanted.Offset + (wanted.Width or 8)))
    if not bytes then return nil, "its memory could not be read" end
    local read = readField(bytes, wanted)
    if read.Error then return nil, read.Error end
    local value = read.Value
    if value == nil then value = read.NotANumber or read.Hex end
    return value, { type = wanted.Type, offset = wanted.Offset, status = layout.status }
end

--- `name` of `component` on every holder (up to `max`, default 64): { values = { [entity integer as
--- text] = value }, holders = n, error = why when the layout or a read fails }.
function Probe.FieldValues(component, name, max)
    local out = { values = {}, holders = 0 }
    for _, entity in ipairs(Probe.Holders(component, max or 64)) do
        out.holders = out.holders + 1
        local value, why = Probe.Field(entity, component, name)
        if value ~= nil then
            out.values[tostring(Ext.Utils.HandleToInteger(entity))] = value
        elseif not out.error then
            out.error = why
        end
    end
    return out
end


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
            if not address and not entry.NoDataSeen then
                -- held with no memory to read (see Probe.Dump): one row says so, and for a type
                -- with a layout it carries whether Script Extender gives it, which is all there is
                -- to verify of it
                entry.NoDataSeen = true
                kept = kept + 1
                entry.Kept = entry.Kept + 1
                local row = { Name = name, Entity = handle, Hex = "", Size = 0, NoData = true, Tick = state.Ticks, Label = label }
                local spec = state.Specs and state.Specs[name]
                if spec then
                    local known, component = pcall(function() return entity[spec.ExtName] end)
                    row.Present = known and component ~= nil
                end
                state.Queue[#state.Queue + 1] = row
            elseif address and total and total > 0 then
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
                    -- { [engine name] = { ExtName = ..., Fields = { ... } } }: given here, or handed
                    -- over beforehand in batches by Seen.AddSpecs (a thousand layouts are too
                    -- much for one chunk)
                    Specs = options.Specs or ProbeSeenSpecs,
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

-- ---------------------------------------------------------------------------------------------
-- The bridge's own names. `Probe` stays as the table this file has always defined (recipes and
-- older notes call it that); GK.Probe and GK.Seen are what the host and the MCP tools use.

GK.Probe = Probe
GK.Seen = GK.Seen or {}
local Seen = GK.Seen

--- Why the probe cannot work here, or nil when it can. Saying so once is better than a stack
--- trace per call: without GateKit's own extender build in developer mode none of this exists.
function Probe.Available()
    local lib = Ext.Debug or {}
    for _, needed in ipairs({ "ReadMemory", "GetComponentAddress", "GetComponentHolders", "GetComponentCounts" }) do
        if lib[needed] == nil then
            return "Ext.Debug." .. needed .. " is missing: GateKit's own Script Extender build in developer mode is needed"
        end
    end
    if module.Size == 0 then
        return "Ext.Debug.GetModuleInfo refused: developer mode is off"
    end
    return nil
end

-- A capture that survives a load ------------------------------------------------------------------
--
-- The capture lives in the game's Lua state, and every level or savegame load resets that state,
-- so the types the engine makes around a load were never seen (D25 of the 2026-11-14 day run:
-- esv::script::SavegameLoadStarted, the anubis reload types, the camp's load-time changes...).
-- So a started capture is ARMED in a file of the channel, and what it has gathered is SPILLED
-- to another whenever the game state changes; when the bridge loads again - at bootstrap, before
-- the level's first tick - an armed capture starts again by itself, with the same options and
-- layouts, under the label it had. Drain gives the spilled samples first. Seen.Stop disarms it.
-- What it still misses: what the engine creates and removes between the last tick before the
-- Lua state goes and the first tick after the new one starts (the reset itself).

local captureSide = Ext.IsServer and Ext.IsServer() and "server" or "client"
local ARMED = "Gwynerva_GateKit/seen_armed_" .. captureSide .. ".json"
local SPILL = "Gwynerva_GateKit/seen_spill_" .. captureSide .. ".json"
local SPECS = "Gwynerva_GateKit/seen_specs_" .. captureSide .. ".json"

local function readJson(path)
    local text = Ext.IO.LoadFile(path)
    if not text or text == "" then return nil end
    local ok, value = pcall(Ext.Json.Parse, text)
    return ok and value or nil
end

local function writeJson(path, value)
    local ok, text = pcall(Ext.Json.Stringify, value)
    if ok then Ext.IO.SaveFile(path, text) end
end

--- Moves what the capture has queued, and the types it has met, into the spill file.
function Seen.Spill(reason)
    local state = ProbeSeenState
    if not state then return 0 end
    local spill = readJson(SPILL) or { Rows = {}, Types = {}, Loads = {} }
    spill.Rows, spill.Types, spill.Loads = spill.Rows or {}, spill.Types or {}, spill.Loads or {}
    -- the level the samples were taken in, which a drain after a level load would get wrong
    local level
    pcall(function() level = GK.Stage and GK.Stage.CurrentLevel and GK.Stage.CurrentLevel() end)
    for _, row in ipairs(state.Queue) do
        row.Level = row.Level or level
        spill.Rows[#spill.Rows + 1] = row
    end
    for name, entry in pairs(state.Types) do
        local had = spill.Types[name]
        if not had or (entry.Max or 0) > (had.Max or 0) then
            spill.Types[name] = { Max = entry.Max, First = entry.First, Label = (had and had.Label) or entry.Label }
        end
    end
    spill.Loads[#spill.Loads + 1] = { reason = tostring(reason), ticks = state.Ticks, label = state.Label }
    writeJson(SPILL, spill)
    local moved = #state.Queue
    state.Queue = {}
    return moved
end

Seen.Start = function(options)
    local why = Probe.Available()
    if why then return { Started = false, Error = why } end
    Probe.SeenStart(options)
    -- armed: a load does not end it (the options are kept here; the layouts in SPECS)
    local armed = {}
    for k, v in pairs(options or {}) do if k ~= "Specs" then armed[k] = v end end
    armed.Armed = true
    writeJson(ARMED, armed)
    return { Started = true, Label = (options or {}).Label or "idle", Survives = "level and savegame loads (re-armed at bootstrap)" }
end
Seen.Mark = function(label)
    local ok = Probe.SeenMark(label)
    local armed = readJson(ARMED)
    if armed then armed.Label = label; writeJson(ARMED, armed) end
    return ok
end

--- Layouts to read through Script Extender in the tick a sample is taken, so the host can compare
--- the two readings on drain ({ [engine name] = { ExtName = ..., Fields = { ... } } }). Merged
--- into what was handed over before; a capture already running takes them at once. `reset` true
--- starts the set afresh. Returns how many types are held.
function Seen.AddSpecs(specs, reset)
    if reset or ProbeSeenSpecs == nil then ProbeSeenSpecs = {} end
    for name, spec in pairs(specs or {}) do ProbeSeenSpecs[name] = spec end
    writeJson(SPECS, ProbeSeenSpecs)   -- for a capture re-armed after a load
    if ProbeSeenState then ProbeSeenState.Specs = ProbeSeenSpecs end
    local n = 0
    for _ in pairs(ProbeSeenSpecs) do n = n + 1 end
    return n
end
--- The queue, the spilled samples (from before a load) first.
Seen.Drain = function(maxRows)
    local spill = readJson(SPILL)
    if spill and #(spill.Rows or {}) > 0 then
        local take = math.min(maxRows or 150, #spill.Rows)
        local rows, rest = {}, {}
        for i = 1, #spill.Rows do if i <= take then rows[#rows + 1] = spill.Rows[i] else rest[#rest + 1] = spill.Rows[i] end end
        spill.Rows = rest
        writeJson(SPILL, spill)
        local queued = ProbeSeenState and #ProbeSeenState.Queue or 0
        return { Rows = rows, Left = #rest + queued, Running = ProbeSeenState ~= nil and ProbeSeenState.Subscription ~= nil,
                 Ticks = ProbeSeenState and ProbeSeenState.Ticks or 0, Spilled = true }
    end
    return Probe.SeenDrain(maxRows)
end

--- The types that appeared since the last call, each with enough about it to decide whether to
--- stop and close in: the tick it first had a holder, the label it appeared under, and the most
--- holders seen at once. Probe.SeenNew returns bare names (the old tours only counted them);
--- a run needs the label, because the label is what a recipe is written from.
function Seen.New()
    local state, out = ProbeSeenState, {}
    if state == nil then return out end
    for name, entry in pairs(state.Types) do
        if not entry.Reported then
            entry.Reported = true
            out[#out + 1] = { Name = name, First = entry.First, Label = entry.Label, Max = entry.Max }
        end
    end
    return out
end
--- The types met, those of before a load included; read after a full drain, the spill is cleared.
Seen.Types = function()
    local out = Probe.SeenTypes()
    local spill = readJson(SPILL)
    if spill then
        for name, entry in pairs(spill.Types or {}) do
            local now = out[name]
            if not now or (entry.Max or 0) > (now.Max or 0) then out[name] = { Max = entry.Max, First = entry.First, Label = entry.Label } end
        end
        if #(spill.Rows or {}) == 0 then Ext.IO.SaveFile(SPILL, "") end
    end
    return out
end

--- Stops the capture and disarms it (a load no longer starts it again).
Seen.Stop = function()
    Ext.IO.SaveFile(ARMED, "")
    return Probe.SeenStop()
end

--- What the capture is doing now, without taking anything out of it: the cheap poll.
function Seen.Status()
    local state = ProbeSeenState
    if state == nil then return { Running = false, Reason = Probe.Available() } end
    local types, queued = 0, #state.Queue
    for _ in pairs(state.Types) do types = types + 1 end
    local spill = readJson(SPILL)
    return { Running = state.Subscription ~= nil, Ticks = state.Ticks, Label = state.Label,
             Types = types, Queued = queued + (spill and #(spill.Rows or {}) or 0), Errors = state.Errors,
             Resumed = state.Resumed, Loads = spill and spill.Loads or nil }
end

-- Every change of game state spills, so a load that resets the Lua state loses nothing gathered.
if not Seen.SpillSubscribed then
    Seen.SpillSubscribed = true
    pcall(function()
        Ext.Events.GameStateChanged:Subscribe(function(e)
            if ProbeSeenState and ProbeSeenState.Subscription then
                pcall(Seen.Spill, tostring(e.FromState) .. " to " .. tostring(e.ToState))
            end
        end)
    end)
end

-- At bootstrap: an armed capture starts again at once, before the level's first tick.
do
    local armed = readJson(ARMED)
    if type(armed) == "table" and armed.Armed and ProbeSeenState == nil and Probe.Available() == nil then
        ProbeSeenSpecs = readJson(SPECS)
        armed.Specs = ProbeSeenSpecs
        local ok = pcall(Probe.SeenStart, armed)
        if ok and ProbeSeenState then
            ProbeSeenState.Resumed = { at = Ext.Utils.MonotonicTime(), label = armed.Label }
        end
    end
end

return Probe
