-- AI.SceneLine, AI.SceneLines (both sides): the state of whatever keeps a table under the global
-- `GKScene` of GateKit's Lua environment, read with no list of names: any table there is a scene,
-- its plain fields are its state, its functions are what can be called on it.

local FIRST = { "armed", "ready", "awaiting", "stage", "fired", "done" }
local SKIP = { log = true }
local MAX_WORDS = 8

local function words(t, prefix, out, depth)
    local keys, seen = {}, {}
    if depth == 0 then
        for _, k in ipairs(FIRST) do
            if t[k] ~= nil then keys[#keys + 1] = k; seen[k] = true end
        end
    end
    local rest = {}
    for k in pairs(t) do
        if type(k) == "string" and not seen[k] and not SKIP[k] then rest[#rest + 1] = k end
    end
    table.sort(rest)
    for _, k in ipairs(rest) do keys[#keys + 1] = k end
    for _, k in ipairs(keys) do
        local v = t[k]
        local kind = type(v)
        if kind == "boolean" then
            if v then out[#out + 1] = prefix .. k end
        elseif kind == "number" then
            out[#out + 1] = prefix .. k .. "=" .. tostring(math.floor(v * 10 + 0.5) / 10)
        elseif kind == "string" then
            local text = v:gsub("%s+", " ")
            if #text > 48 then text = text:sub(1, 45) .. "..." end
            out[#out + 1] = prefix .. k .. "=" .. text
        elseif kind == "table" and depth < 2 then
            words(v, prefix .. k .. ".", out, depth + 1)
        end
    end
end

--- One scene's state in a line: its true flags by name, its texts and numbers as key=value, the
--- fields named ready, awaiting, stage, fired, done first; tables two levels deep are flattened
--- (`who.raphael.seen`); `log` is left out. At most eight words, with a count of the rest.
function AI.SceneLine(scene)
    local out = {}
    words(scene, "", out, 0)
    if #out > MAX_WORDS then
        local more = #out - MAX_WORDS
        for i = #out, MAX_WORDS + 1, -1 do out[i] = nil end
        out[#out + 1] = "+" .. more
    end
    return #out > 0 and table.concat(out, " ") or "(no state)"
end

--- The names of the functions a scene offers, sorted.
function AI.SceneCalls(scene)
    local out = {}
    for k, v in pairs(scene) do
        if type(k) == "string" and type(v) == "function" then out[#out + 1] = k end
    end
    table.sort(out)
    return out
end

--- The one table of scenes of this side. A scene armed by this mod runs in GateKit's environment
--- (Shared/Stand.lua), a scene armed by `gatekit exec` in the global one (GK.Run loads a chunk
--- with no environment of its own); GateKit's environment reads a name it has not from the global
--- one (its metatable's __index), so the first `GKScene` made in GateKit's environment would hide
--- the global one. A table made there is moved into the global one, which GateKit's environment
--- then reads. (A `load` in a mod's file defaults to that mod's environment, not the global one.)
function AI.SceneTable()
    local gk = Mods and Mods.GateKit
    if type(gk) ~= "table" then return nil end
    local meta = getmetatable(gk)
    local GLOBAL = meta and type(meta.__index) == "table" and meta.__index or nil
    if not GLOBAL then return gk.GKScene end
    local own, shared = rawget(gk, "GKScene"), rawget(GLOBAL, "GKScene")
    if type(own) == "table" and own ~= shared then
        if type(shared) == "table" then
            for name, scene in pairs(own) do if shared[name] == nil then shared[name] = scene end end
        else
            rawset(GLOBAL, "GKScene", own)
        end
        rawset(gk, "GKScene", nil)
    end
    return gk.GKScene
end

--- What a scene's call answered, read alike on both sides: a text, a number or a flag is said; a
--- table `{ ok = false, error }` (or `why`) is a refusal; another table says its `answer` text.
--- `ok, said, error`; `said` at most AI.SaidBytes (16000) bytes, cut with "..." past it.
AI.SaidBytes = 16000
local function capped(text)
    if #text <= AI.SaidBytes then return text end
    local last = AI.SaidBytes - 3
    while last > 0 and text:byte(last + 1) and text:byte(last + 1) >= 0x80 and text:byte(last + 1) < 0xC0 do last = last - 1 end
    return text:sub(1, last) .. "..."
end
function AI.SceneSaid(result)
    local kind = type(result)
    if kind == "string" or kind == "number" or kind == "boolean" then return true, capped(tostring(result)) end
    if kind ~= "table" then return true, nil end
    local why = result.error or result.why or (result.ok == false and "refused") or nil
    if why then return false, nil, tostring(why) end
    return true, type(result.answer) == "string" and capped(result.answer) or nil
end

--- Every scene of this side: name -> its line.
function AI.SceneLines()
    local out = {}
    local ok, scenes = pcall(AI.SceneTable)
    if not ok or type(scenes) ~= "table" then return out end
    for name, scene in pairs(scenes) do
        if type(name) == "string" and type(scene) == "table" then out[name] = AI.SceneLine(scene) end
    end
    return out
end
