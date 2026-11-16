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

--- Every scene of this side: name -> its line. `root` is GateKit's environment (Mods.GateKit).
function AI.SceneLines(root)
    local out = {}
    local ok, scenes = pcall(function() return root.GKScene end)
    if not ok or type(scenes) ~= "table" then return out end
    for name, scene in pairs(scenes) do
        if type(name) == "string" and type(scene) == "table" then out[name] = AI.SceneLine(scene) end
    end
    return out
end
