-- GK.Verbs: the class of every verb of the bridge, and the rule a step of a test is held to.
--
-- The classes are data, in one registry (Shared/VerbClasses.json, schemas/bridge/verbs) that the
-- host reads too: fair (a player could do it through the game itself), cheat, observation. Each
-- namespace also carries them as a table, GK.<Namespace>.Class = { Verb = class } (GK.Act.Class,
-- GK.Memory.Class ...).
--
-- A chunk the host sends with a class (a step's: the command's `class`, docs/contracts/
-- bridge-protocol.md) runs inside GK.Verbs.Within(class): a verb called from the chunk itself is
-- refused when the class does not allow it - in a fair step a cheat ("a fair step called the cheat
-- GK.Act.Attack"), in an observation step anything but an observation; a cheat step allows all.
-- What a verb calls inside itself is not judged, nor what runs later on a tick or a timer, nor
-- what has no entry (raw Osi and Ext). A refusal caught by the chunk's own pcall still fails it.
-- Every call of a GK.Cheat function, from anywhere, is recorded in GK.Cheat.Log.

GK.Verbs = GK.Verbs or {}
local V = GK.Verbs

local REGISTRY = "Mods/Gwynerva_GateKit/ScriptExtender/Lua/Shared/VerbClasses.json"
local CLASSES = { fair = true, cheat = true, observation = true }
local ALLOWS = { fair = { fair = true, observation = true }, observation = { observation = true } }

V.Wrapped = V.Wrapped or setmetatable({}, { __mode = "k" })   -- wrapper -> the function it guards
V.Scope = nil                                                  -- the class of the chunk running now
local depth = 0                                                -- verbs entered from the chunk

--- Reads the registry (its text; default: the mod's file): V.Verbs (path -> { class, why }) and
--- V.Namespaces (namespace -> { class, why }). One that cannot be read is an error.
function V.Load(text)
    text = text or Ext.IO.LoadFile(REGISTRY, "data")
    if not text or text == "" then error("GK.Verbs: the registry " .. REGISTRY .. " cannot be read") end
    local registry = Ext.Json.Parse(text)
    if type(registry) ~= "table" or registry.schema ~= "bridge/verbs/1" then
        error("GK.Verbs: " .. REGISTRY .. " is not a bridge/verbs/1 registry")
    end
    V.Verbs, V.Namespaces = registry.verbs or {}, registry.namespaces or {}
    return true
end

--- The class of a path under GK ("Act.Attack" or "GK.Act.Attack") and why, or nil.
function V.Of(path)
    path = tostring(path):gsub("^GK%.", "")
    local entry = V.Verbs and V.Verbs[path]
    if not entry then
        local namespace = path:match("^(.*)%.[^%.]+$")
        entry = namespace and V.Namespaces and V.Namespaces[namespace]
    end
    if not entry then return nil end
    return entry.class, entry.why
end

--- Every classed path, or those whose path or class contains `filter`: { { verb, class, why } }.
function V.List(filter)
    local out = {}
    for path, entry in pairs(V.Verbs or {}) do
        if not filter or path:find(filter, 1, true) or entry.class == filter then
            out[#out + 1] = { verb = path, class = entry.class, why = entry.why }
        end
    end
    table.sort(out, function(a, b) return a.verb < b.verb end)
    return out
end

-- What a verb needs of the Script Extender beyond the stock build, as the registry says it
-- (`needs`: a path under Ext that must be there, "Events.Roll"). One place answers for every
-- such verb, in one sentence.
-- Whether the Script Extender has what a path under Ext names. A function or a table is there
-- when it is not nil. An event cannot be asked that way - Ext.Events answers for any name - so
-- an event ("Events.Roll") is there when the type of its parameters is among the extender's types.
local function reach(path)
    local event = tostring(path):match("^Events%.([%w_]+)$")
    if event then
        for _, side in ipairs({ "esv", "ecl", "lua" }) do
            local name = (side == "lua" and "lua::" or side .. "::lua::") .. event .. "EventParams"
            local ok, info = pcall(Ext.Types.GetTypeInfo, name)
            if ok and info ~= nil then return true end
        end
        return nil
    end
    local at = Ext
    for part in tostring(path):gmatch("[^%.]+") do
        local ok, value = pcall(function() return at[part] end)
        if not ok or value == nil then return nil end
        at = value
    end
    return at
end

--- Whether this Script Extender build has what `path` names under Ext: a function
--- ("Debug.AddProbe"), or an event ("Events.Roll").
function V.Has(path) return reach(path) ~= nil end

--- What the verb at `path` needs that this Script Extender build does not have, or nil when it
--- needs nothing or has it: { needs, error }.
local function lacking(path)
    path = tostring(path):gsub("^GK%.", "")
    local entry = V.Verbs and V.Verbs[path]
    if not (entry and entry.needs) or reach(entry.needs) ~= nil then return nil end
    return { needs = entry.needs, error = "GK." .. path .. " needs Ext." .. entry.needs
        .. ", which this Script Extender build does not have: GateKit's own build has it (gatekit se status)" }
end
--- (the check itself is a local function: the wrapper of every verb calls it, and must not call a wrapped verb)
function V.Lacks(path) return lacking(path) end


--- Every verb that needs something of the Script Extender: { { verb, needs, has } }.
function V.Needs()
    local out = {}
    for path, entry in pairs(V.Verbs or {}) do
        if entry.needs then out[#out + 1] = { verb = path, needs = entry.needs, has = reach(entry.needs) ~= nil } end
    end
    table.sort(out, function(a, b) return a.verb < b.verb end)
    return out
end

local function an(class) return class == "observation" and "an" or "a" end

local function wrap(path, fn, class)
    local cheatName = path:match("^Cheat%.(.+)$")
    local function record()
        if cheatName and cheatName ~= "List" and cheatName ~= "Used" and cheatName ~= "Reset" then
            local cheat = GK.Cheat
            cheat.Log = cheat.Log or {}
            cheat.Log[#cheat.Log + 1] = { name = cheatName, t = Ext.Utils.MonotonicTime() / 1000 }
        end
    end
    local wrapper = function(...)
        local lacks = lacking(path)
        if lacks then return { ok = false, error = lacks.error, needs = lacks.needs } end
        local scope = V.Scope
        if scope == nil or depth > 0 then
            record()
            return fn(...)
        end
        local allows = ALLOWS[scope]
        if allows and not allows[class] then
            local message = an(scope) .. " " .. scope .. " step called the " .. class .. " GK." .. path
            V.Refused = V.Refused or message
            error(message, 2)
        end
        record()
        depth = 1
        local result = table.pack(pcall(fn, ...))
        depth = 0
        if not result[1] then error(result[2], 0) end
        return table.unpack(result, 2, result.n)
    end
    V.Wrapped[wrapper] = fn
    return wrapper
end

local function tableOf(parts, last)
    local t = GK
    for i = 1, last do
        t = type(t) == "table" and rawget(t, parts[i]) or nil
    end
    return type(t) == "table" and t or nil
end

local function guard(path, class)
    local parts = {}
    for part in path:gmatch("[^%.]+") do parts[#parts + 1] = part end
    local owner = tableOf(parts, #parts - 1)
    local name = parts[#parts]
    local fn = owner and rawget(owner, name)
    if type(fn) == "function" and not V.Wrapped[fn] then owner[name] = wrap(path, fn, class) end
    -- a namespace's table of classes: GK.<Namespace>.Class (not on deeper tables, which hold hooks)
    if owner and #parts == 2 then
        local classes = rawget(owner, "Class")
        if type(classes) ~= "table" then classes = {}; owner.Class = classes end
        classes[name] = class
    end
end

--- Puts the class check on every function the registry names that this side has, and on every
--- function of a namespace it names as a whole; sets GK.<Namespace>.Class. Done again for
--- functions defined since (a file loaded later, one reloaded). Answers how many it guards.
function V.Guard()
    if not V.Verbs then V.Load() end
    for path, entry in pairs(V.Verbs) do guard(path, entry.class) end
    for namespace, entry in pairs(V.Namespaces or {}) do
        local owner = GK[namespace]
        if type(owner) == "table" then
            for name, fn in pairs(owner) do
                if type(fn) == "function" and not (V.Verbs[namespace .. "." .. name]) then guard(namespace .. "." .. name, entry.class) end
            end
        end
    end
    local n = 0
    for _ in pairs(V.Wrapped) do n = n + 1 end
    return n
end

--- Runs fn() with the verbs it calls held to `class` (nil: nothing is held). Answers ok, value,
--- refused - refused is the first refusal, also when the chunk caught it.
function V.Within(class, fn)
    if class ~= nil and not CLASSES[class] then
        return false, "the class of a step is fair, cheat or observation, not " .. tostring(class)
    end
    if class ~= nil then V.Guard() end
    local before, beforeDepth = V.Scope, depth
    V.Scope, V.Refused, depth = class, nil, 0
    local ok, value = pcall(fn)
    local refused = V.Refused
    V.Scope, V.Refused, depth = before, nil, beforeDepth
    return ok, value, refused
end

if not V.Verbs then
    local ok, problem = pcall(V.Load)
    if not ok then
        Ext.Utils.PrintError("[GateKit] " .. tostring(problem))
        V.Verbs, V.Namespaces = {}, {}
    end
end

return V
