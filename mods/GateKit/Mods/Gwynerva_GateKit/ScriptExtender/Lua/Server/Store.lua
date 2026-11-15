-- GK.Store: data of any shape that a mod keeps inside the game's save.
--
-- A store is a named table that travels with the save: what is set before a save is there after
-- that save is loaded, and a save made before the store existed gives the store's defaults. It
-- stands on the Script Extender's mod variables (Ext.Vars), the least a mod can do to the save -
-- one variable in the extender's own region of it, nothing of the game's touched - and adds what
-- every mod otherwise writes by hand or forgets:
--
--   - a value is checked before it is written. The extender saves a table with a function or a
--     userdata inside as nothing at all and only logs it; a table with number keys that is not a
--     plain list comes back with those keys as strings. Both are refused here, with the path of
--     the offending value, so what is read is what was written;
--   - a write of a nested field is saved. The extender does not notice a field changed inside a
--     stored table; every Set here assigns the variable anew;
--   - a version and a migration: a store opened with a newer version than the save holds has its
--     data passed through `migrate` once.
--
--   GK.Store.Open(name, { version, defaults, migrate, mod })   declare a store (optional)
--   GK.Store.Get(name, path)          a copy of the value at the path, or of the whole store
--   GK.Store.Set(name, path, value)   write (nil deletes); the whole store without a path
--   GK.Store.Names()                  the stores the save holds
--   GK.Store.Check(value)             whether a value can be kept: { ok } or { ok = false, error, at }
--
-- A path is a list of keys ({ "books", 2, "at" }) or a dotted text ("books.2.at": a part of
-- digits only is a list index). Server side. A store of GateKit's own mod needs no declaration;
-- another mod passes its uuid as `mod` and must open the store while its bootstrap script runs,
-- on every start, because the extender restores only variables that are registered by then.
-- The first safe read is after the session has loaded (Ext.Events.SessionLoaded).
--
-- What cannot be had: a read before the session has loaded; the data of a mod that is no longer
-- loaded past the next save (the extender drops it); functions, userdata, cyclic tables.

GK.Store = GK.Store or {}
local Store = GK.Store

--- Each verb's class, for the fair/cheat/observation split: a write goes into the save.
Store.Class = { Open = "observation", Get = "observation", Names = "observation", Check = "observation", Set = "cheat", Drop = "cheat" }

local MOD = ModuleUUID          -- the bridge mod's own uuid: a global while the bootstrap runs
Store.Mod = MOD
local OWN = "GKStore"          -- the one variable of GateKit's own mod: { [name] = { _v, data } }
local MAX_DEPTH = 64
Store.Opened = Store.Opened or {}      -- [mod .. "/" .. name] = { version, defaults, migrate, mod }

-- registered as the bootstrap runs, on every start: the extender restores it from the save
pcall(function() Ext.Vars.RegisterModVariable(MOD, OWN, { Server = true, Client = false, Persistent = true, SyncToClient = false }) end)

local function isList(t)
    local n = 0
    for _ in pairs(t) do n = n + 1 end
    for i = 1, n do if t[i] == nil then return false end end
    return true
end

local function check(value, at, depth, seen)
    local kind = type(value)
    if kind == "nil" or kind == "boolean" or kind == "string" then return true end
    if kind == "number" then
        if value ~= value or value == math.huge or value == -math.huge then return false, "a number that is not finite", at end
        return true
    end
    if kind ~= "table" then return false, "a " .. kind .. " cannot be kept in a save", at end
    if depth > MAX_DEPTH then return false, "nested deeper than " .. MAX_DEPTH, at end
    if seen[value] then return false, "a table that holds itself", at end
    seen[value] = true
    local list = isList(value)
    for k, v in pairs(value) do
        local here = at .. (at == "" and "" or ".") .. tostring(k)
        if type(k) == "number" then
            if not list then return false, "a number key in a table that is no plain list (it would come back as text): use text keys", here end
        elseif type(k) ~= "string" then
            return false, "a key that is a " .. type(k), here
        end
        local ok, why, where = check(v, here, depth + 1, seen)
        if not ok then return false, why, where end
    end
    seen[value] = nil
    return true
end

--- Whether a value can be kept in a store: { ok = true }, or { ok = false, error, at } with the
--- path of the first part that cannot.
function Store.Check(value)
    local ok, why, at = check(value, "", 1, {})
    if ok then return { ok = true } end
    return { ok = false, error = why, at = at }
end

local function copy(value)
    if type(value) ~= "table" then return value end
    local out = {}
    for k, v in pairs(value) do out[k] = copy(v) end
    return out
end

-- The save keeps a number of a table as a 32-bit float (one third comes back as 0.3333333432...,
-- 1e300 as infinity) and a whole number through a double. So that what is read is what was
-- written, a number the save would change is kept as a text: "#f:" and the number in hexadecimal
-- (exact), "#i:" and a whole number beyond 2^53; a text that begins with "#" is kept with "#s:"
-- before it. Whole numbers within 2^53 stay numbers.
local EXACT = 2 ^ 53
local function encode(value)
    local kind = type(value)
    if kind == "table" then
        local out = {}
        for k, v in pairs(value) do out[k] = encode(v) end
        return out
    elseif kind == "number" then
        if math.type(value) == "integer" then
            if value > EXACT or value < -EXACT then return "#i:" .. tostring(value) end
            return value
        end
        if value == math.floor(value) and value <= EXACT and value >= -EXACT then return "#f:" .. string.format("%a", value) end
        return "#f:" .. string.format("%a", value)
    elseif kind == "string" and value:sub(1, 1) == "#" then
        return "#s:" .. value
    end
    return value
end

local function decode(value)
    local kind = type(value)
    if kind == "table" then
        local out = {}
        for k, v in pairs(value) do out[k] = decode(v) end
        return out
    elseif kind == "string" and value:sub(1, 1) == "#" then
        local tag, rest = value:sub(1, 3), value:sub(4)
        if tag == "#f:" then return tonumber(rest) end
        if tag == "#i:" then return math.tointeger(tonumber(rest)) or tonumber(rest) end
        if tag == "#s:" then return rest end
    elseif kind == "number" and math.type(value) == "float" and value == math.floor(value) and value <= EXACT and value >= -EXACT then
        return math.tointeger(value) or value      -- a whole number comes back from the save as a float
    end
    return value
end

local function keys(path)
    if path == nil then return {} end
    if type(path) == "table" then return path end
    local out = {}
    for part in tostring(path):gmatch("[^.]+") do out[#out + 1] = part:match("^%d+$") and tonumber(part) or part end
    return out
end

local function vars(mod)
    local ok, v = pcall(Ext.Vars.GetModVariables, mod)
    -- `not v`, never `v == nil`: comparing the extender's variables object with nil raises in its Lua
    if not ok or not v then error("GK.Store: the variables of mod " .. tostring(mod) .. " cannot be read (is the session loaded, is the mod loaded?)", 3) end
    return v
end

-- The record of a store, { _v, data }, read from the save's variables; nil when the save has none.
local function record(mod, name)
    local v = vars(mod)
    if mod == MOD then
        local all = v[OWN]
        local rec = type(all) == "table" and all[name] or nil
        return type(rec) == "table" and { _v = rec._v, data = decode(rec.data) } or rec
    end
    local rec = v[name]
    return type(rec) == "table" and { _v = rec._v, data = decode(rec.data) } or rec
end

local function write(mod, name, rec)
    if type(rec) == "table" then rec = { _v = rec._v, data = encode(rec.data) } end
    local v = vars(mod)
    if mod == MOD then
        local all = copy(type(v[OWN]) == "table" and v[OWN] or {})
        all[name] = rec
        v[OWN] = all            -- assigned anew: the extender saves what is assigned, not what is changed inside
    else
        v[name] = rec
    end
end

local function opened(name, mod)
    return Store.Opened[(mod or MOD) .. "/" .. name] or { version = 1, defaults = {}, mod = mod or MOD }
end

-- The store's data as it stands: the save's, migrated when the store was opened with a newer
-- version; the defaults when the save holds none.
local function data(name, mod)
    local o = opened(name, mod)
    local rec = record(o.mod, name)
    if type(rec) ~= "table" or rec.data == nil then return copy(o.defaults), o, false end
    local d, from = copy(rec.data), rec._v or 1
    if from < o.version and o.migrate then
        d = o.migrate(d, from, o.version)
        local ok = Store.Check(d)
        if not ok.ok then error(("GK.Store: the migration of %s from %d gave %s at %s"):format(name, from, ok.error, ok.at), 3) end
        write(o.mod, name, { _v = o.version, data = d })
    end
    return d, o, true
end

--- Declares a store: its version (a whole number, default 1), the defaults a save without the
--- store gives, `migrate(data, from, to)` for a save that holds an older version, and `mod`, the
--- uuid of the mod whose variable it is (default GateKit's own). Another mod's store must be
--- opened while that mod's bootstrap script runs. { ok, name, version, saved } - `saved` says
--- whether the save holds the store (false before the session has loaded).
function Store.Open(name, options)
    if type(name) ~= "string" or name == "" then error("GK.Store.Open(name, options): name is a text, got " .. tostring(name), 2) end
    options = options or {}
    local mod = options.mod or MOD
    local defaults = options.defaults or {}
    local ok = Store.Check(defaults)
    if not ok.ok then error(("GK.Store.Open: the defaults cannot be kept: %s at %s"):format(ok.error, ok.at), 2) end
    if mod ~= MOD then
        local done, why = pcall(Ext.Vars.RegisterModVariable, mod, name, { Server = true, Client = false, Persistent = true, SyncToClient = false })
        if not done then error("GK.Store.Open: the variable could not be registered for mod " .. tostring(mod) .. ": " .. tostring(why), 2) end
    end
    Store.Opened[mod .. "/" .. name] = { version = options.version or 1, defaults = copy(defaults), migrate = options.migrate, mod = mod }
    local saved = false
    pcall(function() saved = record(mod, name) ~= nil end)
    return { ok = true, name = name, version = options.version or 1, saved = saved }
end

--- A copy of the value at `path` of the store `name` (the whole store without a path); nil when
--- nothing is there. `mod`: the uuid of another mod's store.
function Store.Get(name, path, mod)
    local d = data(name, mod)
    for _, key in ipairs(keys(path)) do
        if type(d) ~= "table" then return nil end
        d = d[key]
    end
    return copy(d)
end

--- Writes `value` at `path` of the store `name` (nil takes the key out; without a path the whole
--- store is replaced, and must be a table). Tables on the way are made. The value is checked
--- first: { ok = false, error, at } and nothing written when it cannot be kept. { ok, name }.
function Store.Set(name, path, value, mod)
    if type(name) ~= "string" or name == "" then error("GK.Store.Set(name, path, value): name is a text, got " .. tostring(name), 2) end
    -- the value alone first: a table that holds itself cannot even be copied
    local first = Store.Check(value)
    if not first.ok and first.error:find("holds itself", 1, true) then
        local prefix = {}
        for _, key in ipairs(keys(path)) do prefix[#prefix + 1] = tostring(key) end
        prefix[#prefix + 1] = first.at
        return { ok = false, error = first.error, at = table.concat(prefix, ".") }
    end
    local d, o = data(name, mod)
    local path_ = keys(path)
    if #path_ == 0 then
        if type(value) ~= "table" then error("GK.Store.Set: a whole store is a table, got " .. type(value) .. "; nothing was written", 2) end
        d = copy(value)
    else
        local at = d
        for i = 1, #path_ - 1 do
            local key = path_[i]
            if type(at[key]) ~= "table" then at[key] = {} end
            at = at[key]
        end
        at[path_[#path_]] = copy(value)
    end
    local ok = Store.Check(d)
    if not ok.ok then return { ok = false, error = ok.error, at = ok.at } end
    write(o.mod, name, { _v = o.version, data = d })
    return { ok = true, name = name }
end

--- The stores of GateKit's own mod that the save holds: { { name, version } }, by name.
function Store.Names()
    local all = vars(MOD)[OWN]
    local out = {}
    for name, rec in pairs(type(all) == "table" and all or {}) do out[#out + 1] = { name = name, version = type(rec) == "table" and rec._v or nil } end
    table.sort(out, function(a, b) return a.name < b.name end)
    return out
end

--- Takes a store of GateKit's own mod out of the save's variables altogether: it is no longer
--- among Names(), and Get gives its defaults. { ok, dropped } - `dropped` false when there was none.
function Store.Drop(name)
    local v = vars(MOD)
    local all = copy(type(v[OWN]) == "table" and v[OWN] or {})
    local had = all[name] ~= nil
    all[name] = nil
    v[OWN] = all
    return { ok = true, dropped = had }
end
