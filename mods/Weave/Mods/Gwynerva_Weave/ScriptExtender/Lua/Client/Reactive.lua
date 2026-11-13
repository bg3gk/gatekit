-- Weave, reactivity: state that knows who reads it.
--
--   local state = W.reactive({ count = 0, items = {} })
--   W.effect(function() label.Label = "Clicked " .. state.count end)   -- runs now, and again
--   state.count = state.count + 1                                        -- ...after this
--
-- Fine-grained, as in Vue 3 or Solid: reading a key inside an effect subscribes that effect to
-- that key of that table, writing the key schedules exactly its subscribers. Scheduled effects
-- run together once per tick (W.flush), in the order they were created - so a hundred writes
-- cost one update, and what was built first is brought up to date first.
--
-- Plain tables inside reactive ones are reactive too (wrapped when read). Lists work with
-- #, ipairs, pairs, table.insert, table.remove, table.sort. Userdata and tables that have a
-- metatable of their own (game objects, IMGUI widgets) are stored and returned as they are.

Weave = Weave or {}

local W = Weave

local RAW = setmetatable({}, { __mode = "k" })      -- proxy -> the table behind it
local PROXY = setmetatable({}, { __mode = "k" })    -- table -> its proxy
local DEPS = setmetatable({}, { __mode = "k" })     -- table -> { key -> { effect = true } }
local SHAPE = {}                                    -- the key for "which keys there are" (#, pairs)

local active                  -- the effect that is running: reads are recorded for it
local queue, queued = {}, {}
local nextId = 0
local MAX_ROUNDS = 100

local function complain(message)
    local line = "[Weave] " .. message
    if _PW then _PW(line) else print(line) end
end

local function track(raw, key)
    if not active then return end
    local byKey = DEPS[raw]
    if not byKey then byKey = {} DEPS[raw] = byKey end
    local set = byKey[key]
    if not set then set = {} byKey[key] = set end
    if not set[active] then
        set[active] = true
        active.deps[#active.deps + 1] = set
    end
end

local function schedule(effect)
    if not effect.live then return end
    if effect.scheduler then return effect.scheduler(effect) end
    if not queued[effect] then
        queued[effect] = true
        queue[#queue + 1] = effect
    end
end

local function trigger(raw, key)
    local byKey = DEPS[raw]
    local set = byKey and byKey[key]
    if not set then return end
    local list = {}
    for effect in pairs(set) do
        if effect ~= active then list[#list + 1] = effect end
    end
    for _, effect in ipairs(list) do schedule(effect) end
end

local function unwrap(value)
    return RAW[value] or value
end

local reactive

local function wrap(value)
    if type(value) == "table" and not RAW[value] and getmetatable(value) == nil then return reactive(value) end
    return value
end

local META = {
    __index = function(proxy, key)
        local raw = RAW[proxy]
        track(raw, key)
        return wrap(raw[key])
    end,
    __newindex = function(proxy, key, value)
        local raw = RAW[proxy]
        value = unwrap(value)
        local old = raw[key]
        if rawequal(old, value) then return end
        raw[key] = value
        trigger(raw, key)
        if (old == nil) ~= (value == nil) then trigger(raw, SHAPE) end
    end,
    __len = function(proxy)
        local raw = RAW[proxy]
        track(raw, SHAPE)
        return #raw
    end,
    __pairs = function(proxy)
        local raw = RAW[proxy]
        track(raw, SHAPE)
        return function(_, key)
            local nextKey, value = next(raw, key)
            if nextKey ~= nil then track(raw, nextKey) end
            return nextKey, wrap(value)
        end, proxy, nil
    end,
}

--- A reactive view of a plain table (the same view every time for the same table).
reactive = function(raw)
    if RAW[raw] then return raw end
    local proxy = PROXY[raw]
    if not proxy then
        proxy = setmetatable({}, META)
        RAW[proxy], PROXY[raw] = raw, proxy
    end
    return proxy
end
W.reactive = reactive

--- The plain table behind a reactive one (or the value itself): for saving, for Ext.Json.
function W.raw(value)
    return unwrap(value)
end

--- Reads without subscribing: W.untracked(function() return state.count end).
function W.untracked(fn, ...)
    local previous = active
    active = nil
    local ok, a, b, c = pcall(fn, ...)
    active = previous
    if not ok then error(a, 0) end
    return a, b, c
end

local function release(effect)
    for i = #effect.deps, 1, -1 do
        effect.deps[i][effect] = nil
        effect.deps[i] = nil
    end
end

local function run(effect)
    if not effect.live then return end
    release(effect)
    local previous = active
    active = effect
    local ok, problem = xpcall(effect.fn, debug.traceback)
    active = previous
    if not ok then complain((effect.name or "effect") .. ": " .. tostring(problem)) end
end

--- Runs fn now and again whenever something it read changes. opts.name appears in error
--- messages; opts.scheduler(effect) replaces "run with the next flush". Returns the effect;
--- W.stop(effect) ends it.
function W.effect(fn, opts)
    nextId = nextId + 1
    local effect = { fn = fn, deps = {}, live = true, id = nextId, name = opts and opts.name,
        scheduler = opts and opts.scheduler }
    if not (opts and opts.lazy) then run(effect) end
    return effect
end

function W.stop(effect)
    if not effect then return end
    effect.live = false
    release(effect)
end

--- A value worked out from reactive ones, kept until one of them changes:
--- local total = W.computed(function() return state.price * state.count end)  ...  total()
function W.computed(getter, name)
    local box = { dirty = true }
    local effect
    effect = W.effect(function() box.value = getter() end, {
        lazy = true, name = name or "computed",
        scheduler = function()
            if not box.dirty then
                box.dirty = true
                trigger(box, "value")
            end
        end,
    })
    box.effect = effect
    return function()
        if box.dirty then
            box.dirty = false
            run(effect)
        end
        track(box, "value")
        return box.value
    end, effect
end

--- callback(new, old) after what source() returns has changed (not for the first value).
function W.watch(source, callback, name)
    local old, first = nil, true
    return W.effect(function()
        local value = source()
        if first then
            first, old = false, value
            return
        end
        if not rawequal(value, old) or type(value) == "table" then
            local was = old
            old = value
            W.untracked(callback, value, was)
        end
    end, { name = name or "watch" })
end

--- Runs everything that is scheduled. Called by the kit once per tick; call it yourself to see
--- the result of a write at once (tests do).
function W.flush()
    local rounds = 0
    while #queue > 0 do
        rounds = rounds + 1
        if rounds > MAX_ROUNDS then
            local names = {}
            for i = 1, math.min(#queue, 5) do names[i] = queue[i].name or "effect" end
            complain("effects keep triggering each other (" .. table.concat(names, ", ") .. "); stopped for this tick")
            queue, queued = {}, {}
            return false
        end
        local batch = queue
        queue, queued = {}, {}
        table.sort(batch, function(a, b) return a.id < b.id end)
        for _, effect in ipairs(batch) do run(effect) end
    end
    return true
end

--- How many effects are waiting for the next flush (0 when nothing changed).
function W.pending()
    return #queue
end

Ext.Events.Tick:Subscribe(function()
    if #queue > 0 then W.flush() end
end)
