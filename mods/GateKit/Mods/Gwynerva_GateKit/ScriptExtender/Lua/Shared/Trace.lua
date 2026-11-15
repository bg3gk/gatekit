-- GK.Trace: the entity tracer of GateKit's Script Extender, as a family of the event log. Each side
-- (server, client) has a tracer of its own. Between Begin and End the tracer records what changed
-- in the entity system - a component created, destroyed, replicated, modified - per entity and
-- component, with the ECS frame and command-buffer flush of each change and, with `systems`, the
-- system whose update changed it. Step cuts the record into the steps of a test: what was recorded
-- since the last cut becomes rows of the `trace` family (trace_change, trace_system, trace_step),
-- each carrying the step's name, and the tracer starts again empty.
--
--   GK.Trace.Begin{ entities = { uuid, ... }, components = { "Health", ... }, systems = true }
--   GK.Trace.Step("the attack")       -- what came before is the previous step's
--   ... the attack ...
--   GK.Trace.End()                    -- the last step's rows; the tracer off
--
-- Filters are the fork's, applied at the source: `entities` (uuids or entities; none: all),
-- `components` (engine names or Lua labels; none: all). `systems` needs both filters (the fork's
-- rule) and costs milliseconds a tick per component watched. `modifications` records writes to a
-- component (off by default: the engine marks a modified component once a flush). Observation: the
-- tracer changes nothing in the world. Developer mode only, GateKit's Script Extender build.

local GK = GK or Mods.GateKit.GK
local Trace = GK.Trace or {}
GK.Trace = Trace

local L = GK.EventLog
local ROWS_PER_STEP = 500        -- trace_change rows a step writes at most; the rest are counted
local SYSTEM_ROWS_PER_STEP = 500 -- trace_system rows a step writes at most
local EVENTS_PER_CHANGE = 16     -- events of one entity's component kept in its row

Trace.State = Trace.State or { on = false }
local S = Trace.State

local function try(fn, ...)
    local ok, a = pcall(fn, ...)
    if ok then return a end
end

--- Whether the fork's tracer with filters is present (developer mode, the instruments build).
function Trace.Available()
    return Ext.Entity.SetupTracing ~= nil and Ext.Entity.GetTrace ~= nil and Ext.Entity.EnableTracing ~= nil
end

local function uuidOf(entity)
    return try(function() return entity.Uuid.EntityUuid end)
end

local function entityOf(value)
    if type(value) == "string" then return try(Ext.Entity.Get, value) end
    return value
end

-- The fork's flag masks as names: of a component's change, and of an entity.
local CHANGE_FLAGS = { { 1, "Create" }, { 2, "Destroy" }, { 4, "Replicate" }, { 8, "OneFrame" }, { 16, "ReplicatedComponent" }, { 32, "Modify" } }
local ENTITY_FLAGS = { { 1, "Create" }, { 2, "Destroy" }, { 4, "Immediate" }, { 8, "Dead" }, { 16, "Ignore" } }
local function flagNames(mask, names)
    mask = tonumber(mask) or 0
    local out = {}
    for _, pair in ipairs(names) do
        if math.floor(mask / pair[1]) % 2 == 1 then out[#out + 1] = pair[2] end
    end
    return table.concat(out, "|")
end

--- Begins a trace on this side: options { entities, components, systems, modifications, limit
--- (events kept per entity and component, at most 4096), step (the first step's name), quiet (only
--- the trace_step row of each step goes to the log; the changes are in the answers) }. Switches
--- the `trace` family on for as long as the trace runs (at most 600 s of a running game, or the
--- stage scope). Answers { ok, side, frame, flush } or { ok = false, error }.
function Trace.Begin(options)
    options = options or {}
    if not Trace.Available() then
        return { ok = false, error = "GK.Trace needs GateKit's Script Extender (v34-instruments) in developer mode: no Ext.Entity.SetupTracing" }
    end
    local entities = {}
    for _, e in ipairs(options.entities or {}) do
        local entity = entityOf(e)
        if not entity then return { ok = false, error = "GK.Trace.Begin: no entity " .. tostring(e) .. " on this side" } end
        entities[#entities + 1] = entity
    end
    local setup = {
        TrackECB = true, TrackImmediateWorldCache = true, TrackReplication = true,
        TrackModifications = options.modifications and true or false,
        Entities = entities, Components = options.components or {},
        EventLimit = math.min(4096, math.max(1, tonumber(options.limit) or 16)),
        TrackSystems = options.systems and true or false,
    }
    local ok, problem = pcall(Ext.Entity.SetupTracing, setup)
    if not ok then return { ok = false, error = "the tracer refused its setup: " .. tostring(problem) } end
    pcall(Ext.Entity.ClearTrace)
    Ext.Entity.EnableTracing(true)
    if L then L.On("trace", { seconds = 600 }) end
    S.on, S.step, S.options, S.last = true, options.step, options, nil
    -- a trace goes with the scene: the server's clean ends it (the ledger), the client's the reset
    -- ladder's clean of the client
    if GK.Stage and GK.Stage.OnClean then
        GK.Stage.OnClean(function() if S.on then Trace.End() end return true end, "GK.Trace's trace")
    end
    local now = try(Ext.Entity.GetTrace) or {}
    if L then
        L.Append("trace_begin", { step = S.step, entities = #entities, components = options.components,
                                  systems = setup.TrackSystems, modifications = setup.TrackModifications,
                                  frame = now.Frame, flush = now.Flush })
    end
    return { ok = true, side = L and L.Side, frame = now.Frame, flush = now.Flush }
end

-- What the tracer holds now, as rows of the step that ends: written to the event log (at most
-- ROWS_PER_STEP changes and SYSTEM_ROWS_PER_STEP system rows; the rest counted) and answered.
local function cut()
    local trace = try(Ext.Entity.GetTrace)
    if not trace then return { ok = false, error = "the tracer gave nothing (GetTrace)" } end
    local summary = { ok = true, step = S.step, entities = 0, changes = 0, written = 0, systems = 0, systemsWritten = 0,
                      frame = trace.Frame, flush = trace.Flush, droppedSystems = trace.DroppedSystemChanges,
                      systemMicroseconds = trace.SystemTrackingMicroseconds, rows = {} }
    for handle, log in pairs(trace.Entities or {}) do
        summary.entities = summary.entities + 1
        local entity = log.Entity or handle
        local uuid = uuidOf(entity)
        for _, c in pairs(log.Components or {}) do
            summary.changes = summary.changes + 1
            if summary.written < ROWS_PER_STEP then
                summary.written = summary.written + 1
                local events = {}
                for i, e in ipairs(c.Events or {}) do
                    if i > EVENTS_PER_CHANGE then break end
                    events[#events + 1] = { flags = flagNames(e.FlagMask, CHANGE_FLAGS), frame = e.Frame, flush = e.Flush, order = e.Order }
                end
                local row = { step = S.step, entity = tostring(entity), uuid = uuid, component = tostring(c.Name),
                              flags = flagNames(c.FlagMask, CHANGE_FLAGS), count = c.Count, frame = c.Frame, flush = c.Flush,
                              lastFrame = c.LastFrame, lastFlush = c.LastFlush, events = events,
                              droppedEvents = (c.DroppedEvents or 0) > 0 and c.DroppedEvents or nil,
                              entityFlags = flagNames(log.FlagMask, ENTITY_FLAGS) }
                summary.rows[#summary.rows + 1] = row
                if L and not S.options.quiet then L.Append("trace_change", row) end
            end
        end
    end
    for _, s in ipairs(trace.SystemChanges or {}) do
        summary.systems = summary.systems + 1
        if summary.systemsWritten < SYSTEM_ROWS_PER_STEP then
            summary.systemsWritten = summary.systemsWritten + 1
            local row = { step = S.step, entity = tostring(s.Entity), uuid = uuidOf(s.Entity), component = tostring(s.Component),
                          system = tostring(s.SystemName), thread = s.Thread, frame = s.Frame, flush = s.Flush }
            summary.rows[#summary.rows + 1] = row
            if L and not S.options.quiet then L.Append("trace_system", row) end
        end
    end
    if L then
        L.Append("trace_step", { step = S.step, entities = summary.entities, changes = summary.changes, written = summary.written,
                                 systems = summary.systems, systemsWritten = summary.systemsWritten,
                                 droppedSystems = summary.droppedSystems, frame = summary.frame, flush = summary.flush })
    end
    pcall(Ext.Entity.ClearTrace)
    S.last = S.step
    return summary
end

--- Ends the step under way and begins step `name`: what the tracer recorded since the last cut
--- becomes rows of the step that ends (trace_change, trace_system, trace_step) and the answer:
--- { ok, step, entities, changes, written, systems, systemsWritten, frame, flush, rows }.
--- A change carries the step under way when the engine applies it, at the flush after the Lua that
--- caused it: Step(name) names what follows it, and an action and the Step or End that closes its
--- step go in separate chunks.
function Trace.Step(name)
    if not S.on then return { ok = false, error = "no trace runs on this side (GK.Trace.Begin)" } end
    local summary = cut()
    S.step = name
    return summary
end

--- Ends the trace: the last step's rows (as Step) and the tracer off.
function Trace.End()
    if not S.on then return { ok = false, error = "no trace runs on this side (GK.Trace.Begin)" } end
    local summary = cut()
    pcall(Ext.Entity.EnableTracing, false)
    pcall(Ext.Entity.ClearTrace)
    S.on, S.step = false, nil
    if L then
        L.Append("trace_end", { frame = summary.frame, flush = summary.flush })
        L.Off("trace", "the trace ended")
    end
    return summary
end

--- Whether a trace runs on this side: { on, step, last, options }: `step` the step under way (nil
--- when no trace runs), `last` the step that ended last, `options` Begin's while the trace runs.
function Trace.Status()
    return { on = S.on, step = S.on and S.step or nil, last = S.last, options = S.on and S.options or nil }
end

return Trace
