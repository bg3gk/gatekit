-- GK.Memory: the memory instrument, over the fork's Ext.Debug, on both sides (server and client
-- Lua each have it; each side owns its own watches and freezes). The decompiled code and the running
-- game's memory are one thing from two sides - `module+0xRVA` is a place in the live process - so
-- this reads a component's field by address, watches which instruction writes or reads it, and
-- holds a value fixed. None of it is fair: every verb is a cheat or an observation, and it is never
-- offered to a client that plays. Developer mode only; on a stock Script Extender the verbs raise
-- an error that says so.
--
--   local f = GK.Memory.Field(host, "eoc::HealthComponent", "Hp")  -- { Address, Offset, Type, Size, ... }
--   GK.Memory.Read(f)                                     -- the value now
--   GK.Memory.Watch(f)                                    -- who writes it: a hardware watchpoint
--   ... deal damage ...
--   for _, hit in ipairs(GK.Memory.Hits()) do ... end     -- each hit: the instruction, the value before and after
--   GK.Memory.Freeze(f.Address, f.Type, f.Value)          -- hold it
--   GK.Stage.Clean()                                      -- removes every watch and freeze of both sides
--
-- The extender itself serves the watches (new threads re-armed every tick) and rewrites the frozen
-- values every tick of their side; nothing of this file runs on the tick. Watches and freezes
-- outlive a Lua reset in the process: those of an earlier Lua state of this side are cleared as
-- this file loads, so no hit is read against a slot table that no longer says what it watches.
-- The host turns a hit's module+RVA into a function name with `gatekit decomp at <module+0xRVA>`.

local GK = GK or Mods.GateKit.GK
local Memory = GK.Memory or {}
GK.Memory = Memory

-- Each verb's class is in the registry of verbs (Shared/VerbClasses.json), and GK.Memory.Class
-- carries it once GK.Verbs.Guard has run. None is fair.

local Debug = Ext.Debug

-- True when the fork's memory instrument is present (developer mode, GateKit's build).
function Memory.Available()
    return Debug ~= nil and Debug.GetFieldAddress ~= nil and Debug.WatchWrite ~= nil and Debug.ListWatches ~= nil
end

local function require_available()
    if not Memory.Available() then
        error("GK.Memory needs GateKit's Script Extender (v34-instruments) in developer mode; this build has no Ext.Debug.ListWatches", 2)
    end
end

-- observation: the absolute address of a component's field for an entity, from the fork's own type
-- information. `entity` is a uuid or an entity; `component` is the engine name
-- (eoc::HealthComponent, not Health); `field` is the Lua field name (Hp), a top-level field of the
-- component. Returns { Address, Offset, ComponentAddress, Type, Size, Scalar } or nil.
function Memory.Field(entity, component, field)
    require_available()
    if type(entity) == "string" then entity = Ext.Entity.Get(entity) end
    local f = Debug.GetFieldAddress(entity, component, field)
    -- what the field is of, so that a watch on it can say later that the component has moved
    if type(f) == "table" then pcall(function() f.Entity, f.Component, f.Field = entity, component, field end) end
    return f
end

-- observation: the address of a system's own object (esv::roll::stream::SaveSystem), or nil. What a
-- system keeps outside the components - a manager, a cache - is reached from here with Read.
function Memory.System(name)
    require_available()
    if not Debug.GetSystemAddress then error("this Script Extender build has no Ext.Debug.GetSystemAddress", 2) end
    -- the fork answers a table ({ Address, ... }); the address itself is what Read and Element take
    local found = Debug.GetSystemAddress(name)
    if type(found) == "table" then return found.Address end
    return found
end

-- observation: the address of element `index` (from 0) of an array whose buffer pointer is at
-- `bufferAt`, each element `elementSize` bytes, plus `offset` inside the element; nil when the
-- buffer cannot be read. Field stops at an array: a stream's count is
-- Element(streams.Address, 0, 0x78, 0x70).
function Memory.Element(bufferAt, index, elementSize, offset)
    require_available()
    if type(bufferAt) == "table" then bufferAt = bufferAt.Address end
    local buffer = Debug.ReadValue(bufferAt, "uint64")
    if not buffer or buffer == 0 then return nil end
    return buffer + (index or 0) * elementSize + (offset or 0)
end

-- observation: the scalar at an address read as `type` (int32, float, bool, ...), or nil if the
-- bytes are not readable. With a field table (from Memory.Field) and no type, its own type is used.
function Memory.Read(address, valueType)
    require_available()
    if type(address) == "table" then
        local f = address
        return Debug.ReadValue(f.Address, valueType or f.Type)
    end
    return Debug.ReadValue(address, valueType)
end

-- observation: which loaded module holds an address, as { Module, RVA, Base, Size } - the
-- `module+0xRVA` the decompiler database speaks. nil when the address is in no module.
function Memory.At(address)
    require_available()
    return Debug.ModuleFromAddress(address)
end

-- observation: the absolute address of module+rva, or nil. The inverse of At.
function Memory.FromModule(module, rva)
    require_available()
    return Debug.AddressFromModule(module, rva)
end

-- cheat: write a scalar at an address, bounds-checked against writable data memory (a code page is
-- refused). Returns true if written. Registered as a cheat so a run's log shows it was used.
function Memory.Write(address, type, value)
    require_available()
    if GK.Cheat and GK.Cheat.Log then
        GK.Cheat.Log[#GK.Cheat.Log + 1] = { name = "Memory.Write", t = Ext.Utils.MonotonicTime() / 1000 }
    end
    return Debug.WriteValue(address, type, value)
end

-- What each slot of this side watches: false for a raw address, else the place it follows -
-- { entity, component, field? , offset?, address, size, readWrite, watch } (a field, or an
-- address `offset` bytes into a component).
if not Memory._clearedAtLoad then
    Memory._clearedAtLoad = true
    if Memory.Available() then
        pcall(Debug.ClearWatch)
        pcall(Debug.ReadWatchHits)
        pcall(Debug.ClearFreezes)
    end
    Memory._slots = {}
end

-- Clean removes every watch and freeze. On the server the stage ledger takes the undo; on the
-- client the host's clean of the client (the reset ladder) calls Memory.Clear. The hook is
-- registered when a watch or freeze becomes active and re-arms itself after a clean has consumed
-- it, so the next scene is covered too.
local function ensureClean()
    if Memory._cleanArmed or not (GK.Stage and GK.Stage.OnClean) then return end
    Memory._cleanArmed = true
    GK.Stage.OnClean(function()
        Memory.Clear()
        Memory._cleanArmed = false
        return true
    end, "GK.Memory watches and freezes")
end

-- ---------------------------------------------------------------------------------------
-- A watch that follows its component. A component's storage moves when its entity's set of
-- components changes, and a hardware watch left where it was sits on memory that is another's.
-- One rule, in one place (`look`): a watch armed on a field, or on an address given its owner
-- (entity + component), is looked at on the tick while it exists (GK.Attention.Pollers.memory,
-- registered with the first such watch and gone with the last); when its place has moved, the
-- hits of its slot read so far are held: one stamped with a flush no later than the last look
-- that found the place unmoved stays the field's, any other gets Moved = true and is not the
-- field's (the move falls somewhere after that flush; while a trace runs, or without the tracer's
-- counters, no flush is known and every one is Moved). The watch is armed again at the new place,
-- or, when the component is gone or no slot is free, closed. Either is a memory_watch row. A raw
-- address with no owner is not followed, and Watches says so.

Memory._held = Memory._held or {}      -- hits drained by a look, kept for Hits
Memory._moves = Memory._moves or {}    -- [slot as it was] = { was, now, did, entity, component, field }
Memory._serial = Memory._serial or 0

local function entityOf(e)
    if type(e) == "string" then return Ext.Entity.Get(e) end
    return e
end

local function uuidOf(e)
    local ok, id = pcall(function() return e.Uuid.EntityUuid end)
    return ok and id or (type(e) == "string" and e or nil)
end

-- Where a component's own bytes are: one larger than its inline slot is stored behind a pointer.
local function componentBody(entity, component)
    local ok, at = pcall(Debug.GetComponentAddress, entity, component)
    if not ok or type(at) ~= "table" or not at.Address then return nil end
    local address = at.Address
    if at.InlineSize and at.TotalSize and at.InlineSize < at.TotalSize then
        address = Debug.ReadValue(address, "uint64")
        if not address or address == 0 then return nil end
    end
    return address, at.TotalSize
end

-- Where a followed place is now; nil when its component (or field) is gone.
local function whereNow(of)
    if of.field then
        local ok, f = pcall(Debug.GetFieldAddress, of.entity, of.component, of.field)
        return ok and type(f) == "table" and f.Address or nil
    end
    local body = componentBody(of.entity, of.component)
    return body and body + of.offset or nil
end

local function owned()
    for _, of in pairs(Memory._slots or {}) do if of then return true end end
    return false
end

local look

local function poll()
    if GK.Attention and GK.Attention.Pollers then
        GK.Attention.Pollers.memory = owned() and function() look() end or nil
    end
end

-- This side's command-buffer flush now, the counter a hit is stamped with (Flush); nil while a
-- trace runs (its record is not built again every tick for this) or without the tracer.
local function flushNow()
    if GK.Trace and GK.Trace.State and GK.Trace.State.on then return nil end
    -- the tracer's answer is the extender's own object, not a Lua table
    local ok, flush = pcall(function() return Ext.Entity.GetTrace().Flush end)
    return ok and tonumber(flush) or nil
end

-- The hits of this side drained into the held list. A hit of a `moved` slot stamped with a flush
-- no later than the last look that found the place unmoved was made before the move and stays the
-- field's; any other is marked as not the field's (Moved, Now).
local function hold(moved)
    local hits = Debug.ReadWatchHits()
    if type(hits) ~= "table" then return 0 end
    local n = 0
    for _, hit in ipairs(hits) do
        local m = moved[hit.Slot]
        if m then
            local before = false
            pcall(function() before = m.flush ~= nil and hit.Flush ~= nil and hit.Flush <= m.flush end)
            if before then
                pcall(function() hit.Of = { entity = uuidOf(m.of.entity), component = m.of.component, field = m.of.field, offset = m.of.offset } end)
            else
                n = n + 1
                pcall(function() hit.Moved, hit.Now, hit.Of = true, m.now, nil end)
            end
        end
        Memory._held[#Memory._held + 1] = hit
    end
    return n
end

local function row(payload)
    if GK.EventLog and GK.EventLog.Append then pcall(GK.EventLog.Append, "memory_watch", payload) end
end

--- Looks at every followed watch of this side once (the rule above); answers the moves it found.
look = function()
    local moved = {}
    local flush = flushNow()
    for slot, of in pairs(Memory._slots or {}) do
        if of then
            local now = whereNow(of)
            if now ~= of.address then moved[slot] = { was = of.address, now = now, of = of, flush = of.flush }
            else of.flush = flush end
        end
    end
    if not next(moved) then return moved end
    local held = hold(moved)
    for slot, m in pairs(moved) do
        local of = m.of
        pcall(Debug.ClearWatch, slot)
        Memory._slots[slot] = nil
        local again = m.now and Debug.WatchWrite(m.now, of.size, of.readWrite) or nil
        local what = { slot = slot, was = m.was, now = m.now, entity = uuidOf(of.entity), component = of.component, field = of.field,
                       offset = of.offset, watch = of.watch, heldHits = held }
        if again then
            of.address, of.flush = m.now, flush
            Memory._slots[again] = of
            what.did, what.armed = "rearmed", again
        else
            what.did = "closed"
            what.why = m.now and "no watch slot was free to arm it again" or "its component is gone"
        end
        Memory._moves[slot] = { was = m.was, now = m.now, did = what.did, armed = what.armed, why = what.why,
                                entity = what.entity, component = of.component, field = of.field }
        row(what)
    end
    poll()
    return moved
end

-- observation: arm a hardware watchpoint on an address (size 1, 2, 4 or 8, aligned to its size):
-- writes, or with readWrite also reads. Returns the slot (1-4) or nil when all four of the process
-- are in use (the other side's count too: Watches(true)). A field table may be passed in place of
-- (address, size); `owner` ({ entity, component }) gives a raw address the component it lies in,
-- and the watch then follows it as a field's does. The watch belongs to this side and is removed
-- on GK.Stage.Clean. Arming stops every thread of the game for a moment (tens of ms).
function Memory.Watch(address, size, readWrite, owner)
    require_available()
    local of = nil
    if type(address) == "table" then
        local f = address
        owner = readWrite
        readWrite = size
        address, size = f.Address, f.Size
        if f.Component then of = { entity = f.Entity, component = f.Component, field = f.Field, address = f.Address } end
        if size == nil then
            error("GK.Memory.Watch: the field " .. tostring(f.Field) .. " has no single size (" .. tostring(f.Type)
                .. "): watch one of its parts, Watch(field.Address + n, 4)", 2)
        end
    end
    if type(size) ~= "number" or (size ~= 1 and size ~= 2 and size ~= 4 and size ~= 8) then
        error("GK.Memory.Watch(address, size): size is 1, 2, 4 or 8, not " .. tostring(size), 2)
    end
    if of == nil and owner ~= nil then
        if type(owner) ~= "table" or owner.entity == nil or type(owner.component) ~= "string" then
            error("GK.Memory.Watch(address, size, readWrite, owner): owner is { entity, component }, the engine's name of the component", 2)
        end
        local entity = entityOf(owner.entity)
        local body, total = componentBody(entity, owner.component)
        if not body then error("GK.Memory.Watch: " .. tostring(owner.entity) .. " has no " .. owner.component .. "; nothing was armed", 2) end
        local offset = address - body
        if offset < 0 or (total and offset + size > total) then
            error(string.format("GK.Memory.Watch: 0x%X is not inside %s of %s (0x%X, %s bytes); nothing was armed",
                address, owner.component, tostring(owner.entity), body, tostring(total)), 2)
        end
        of = { entity = entity, component = owner.component, offset = offset, address = address }
    end
    local slot = Debug.WatchWrite(address, size, readWrite and true or false)
    if slot then
        Memory._slots = Memory._slots or {}
        if of then
            Memory._serial = Memory._serial + 1
            of.size, of.readWrite, of.watch, of.flush = size, readWrite and true or false, Memory._serial, flushNow()
        end
        Memory._slots[slot] = of or false
        Memory._moves[slot] = nil
        ensureClean()
        poll()
    end
    return slot
end

-- observation: the watches armed now - this side's, or with `all` both sides' - as
-- { { Slot, Owner ("Server" | "Client"), Address, Size, ReadWrite, Value, Follows | Raw } }; Value
-- is what the next hit's Before will be. A watch of this side says what it follows (Follows =
-- { entity, component, field?, offset? }), or Raw = true: an address with no owner, not followed
-- when storage moves.
function Memory.Watches(all)
    require_available()
    look()
    local list = Debug.ListWatches(all and true or false)
    if type(list) ~= "table" then return list end
    local mine = GK.Side == "client" and "Client" or "Server"
    for _, w in ipairs(list) do
        pcall(function()
            if w.Owner ~= nil and w.Owner ~= mine then return end
            local of = (Memory._slots or {})[w.Slot]
            if of then w.Follows = { entity = uuidOf(of.entity), component = of.component, field = of.field, offset = of.offset }
            else w.Raw = true end
        end)
    end
    return list
end

-- observation: how many hits of this side's watches wait to be read (Hits drains them).
function Memory.Waiting()
    require_available()
    look()
    return #Memory._held + (Debug.WatchPoll() or 0)
end

-- observation: the hits of this side's watches since the last call (at most `max`). Each hit:
-- Slot, Thread; Before (the value before the access: the copy taken at arming and after each hit)
-- and After (read at the hit), Changed (a read leaves it false); Frame and Flush (this side's ECS
-- frame and command-buffer flush, the counters of the entity tracer: a write stamped flush N shows
-- in the trace in flush N + 1); Poll (this side's extender ticks); NextInstruction ({ Module, RVA,
-- Address } of the instruction after the access; Instruction, the older name, is the same) and
-- Return (up to six frames, the first the next instruction).
--
-- A hit of a followed watch carries Of = { entity, component, field?, offset? }, what it is a hit
-- of. A hit that may have come after its component moved carries Moved = true and Now (the
-- place's address then, nil when the component is gone) and no Of: it is not a hit of the field.
function Memory.Hits(max)
    require_available()
    look()
    local hits = Memory._held
    Memory._held = {}
    local fresh = Debug.ReadWatchHits()
    if type(fresh) == "table" then
        for _, hit in ipairs(fresh) do hits[#hits + 1] = hit end
    end
    for _, hit in ipairs(hits) do
        pcall(function()
            local of = not hit.Moved and (Memory._slots or {})[hit.Slot]
            if of then hit.Of = { entity = uuidOf(of.entity), component = of.component, field = of.field, offset = of.offset } end
        end)
    end
    if max and #hits > max then
        -- what is over `max` waits for the next call
        for i = #hits, max + 1, -1 do table.insert(Memory._held, 1, table.remove(hits, i)) end
    end
    return hits
end

-- observation: the followed watches of this side whose place moved since they were armed, by
-- the slot each had: { [slot] = { was, now, did = "rearmed" | "closed", armed?, why?, entity,
-- component, field? } } - a field's watch and one armed on an address with its owner alike.
function Memory.Moved()
    require_available()
    look()
    local out = {}
    for slot, m in pairs(Memory._moves) do out[slot] = m end
    return out
end

-- observation: clear one watchpoint of this side (slot 1-4) or every one; the other side's are
-- never touched. Clearing every one also drops the hits not read yet, so that the next watch's
-- hits are its own. True when something was cleared.
function Memory.ClearWatch(slot)
    if not Memory.Available() then return false end
    local ok = Debug.ClearWatch(slot)
    if slot == nil then
        Memory._slots = {}
        Memory._held, Memory._moves = {}, {}
        pcall(Debug.ReadWatchHits)
    elseif Memory._slots then
        Memory._slots[slot] = nil
    end
    poll()
    return ok
end

-- cheat: hold an address at a value, rewritten by the extender every tick of this side. Returns
-- the slot (1-16). Removed on Clean.
function Memory.Freeze(address, type, value)
    require_available()
    if GK.Cheat and GK.Cheat.Log then
        GK.Cheat.Log[#GK.Cheat.Log + 1] = { name = "Memory.Freeze", t = Ext.Utils.MonotonicTime() / 1000 }
    end
    local slot = Debug.Freeze(address, type, value)
    if slot then ensureClean() end
    return slot
end

-- cheat: stop holding one frozen address of this side (a slot) or every one.
function Memory.Unfreeze(slot)
    if not Memory.Available() then return false end
    return Debug.ClearFreezes(slot)
end

-- observation: the addresses held now - this side's, or with `all` both sides' - as
-- { { Slot, Owner, Address, Size } }.
function Memory.Frozen(all)
    require_available()
    return Debug.ListFreezes(all and true or false)
end

-- cheat: every watch and freeze of this side gone; answers what was there ({ watches, freezes },
-- counts), so that a clean can say it.
function Memory.Clear()
    if not Memory.Available() then return { watches = 0, freezes = 0 } end
    local watches = #(Debug.ListWatches(false) or {})
    local freezes = #(Debug.ListFreezes(false) or {})
    if watches > 0 then Memory.ClearWatch() end
    if freezes > 0 then Debug.ClearFreezes() end
    return { watches = watches, freezes = freezes }
end

return Memory
