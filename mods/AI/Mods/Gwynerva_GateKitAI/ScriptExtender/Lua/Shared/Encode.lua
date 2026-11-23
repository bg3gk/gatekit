-- AI.Encode, AI.NowKeys, AI.SessionFile (both sides): the answers' JSON text, and the session's
-- file of the copy.
--
--   AI.Encode(value, order)    JSON text on one line with the keys in a fixed order, UTF-8 as it is
--   AI.EncodeWith(v, order, object)   the same for any value: `object` makes an empty table `{}`
--   AI.NowKeys                 the keys of `now`, in their order
--   AI.SessionFile.Read()      the session as kept in the copy: { mode, prompt, promptFrom, stand, armed }
--   AI.SessionFile.Write(t)    those fields of `t` written over what the file holds

AI.NowKeys = { "where", "fight", "countdown", "goals", "can", "party", "waiting", "window", "ways", "near", "heard", "events", "rolls", "scenes", "route", "notes", "prompt", "more" }
local KEYS = AI.NowKeys

local function round1(v) return math.floor(v * 10 + 0.5) / 10 end

local ESCAPES = { ['"'] = '\\"', ["\\"] = "\\\\", ["\n"] = "\\n", ["\r"] = "\\r", ["\t"] = "\\t" }

local function quote(s)
    return '"' .. s:gsub('[%c"\\]', function(c) return ESCAPES[c] or string.format("\\u%04x", c:byte()) end) .. '"'
end

local function encode(value, order, object)
    local kind = type(value)
    if kind == "string" then return quote(value) end
    if kind == "number" then
        if value == math.floor(value) and math.abs(value) < 1e15 then return string.format("%d", value) end
        return tostring(round1(value))
    end
    if kind == "boolean" then return value and "true" or "false" end
    if kind ~= "table" then return "null" end
    if #value > 0 or next(value) == nil then
        if object then return "{}" end
        local parts = {}
        for i, v in ipairs(value) do parts[i] = encode(v) end
        return "[" .. table.concat(parts, ",") .. "]"
    end
    local keys, seen = {}, {}
    for _, k in ipairs(order or {}) do
        if value[k] ~= nil then keys[#keys + 1] = k; seen[k] = true end
    end
    local rest = {}
    for k in pairs(value) do
        if type(k) == "string" and not seen[k] then rest[#rest + 1] = k end
    end
    table.sort(rest)
    for _, k in ipairs(rest) do keys[#keys + 1] = k end
    local parts = {}
    for _, k in ipairs(keys) do parts[#parts + 1] = quote(k) .. ":" .. encode(value[k], k == "now" and KEYS or nil, k == "now" or k == "more") end
    return "{" .. table.concat(parts, ",") .. "}"
end

AI.EncodeWith = encode

--- JSON text of `value` on one line: an object's keys in `order` first and the rest sorted, a
--- `now` field in the order of AI.NowKeys, text as UTF-8 (only quotes, backslashes and control
--- characters escaped).
function AI.Encode(value, order) return encode(value, type(order) == "table" and order or KEYS, true) end

-- ---------------------------------------------------------------------------------------
-- The session's file: written by whichever side runs the session (the client while no level is
-- loaded, the server in a level), read by both. A load of a save restarts Lua; the file stays.

AI.SessionFile = AI.SessionFile or {}
local F = AI.SessionFile
F.Path = "Gwynerva_GateKitAI/session.json"
F.Fields = { "mode", "prompt", "promptFrom", "stand", "armed" }

function F.Read()
    local ok, text = pcall(Ext.IO.LoadFile, F.Path)
    local kept = ok and text and text ~= "" and select(2, pcall(Ext.Json.Parse, text)) or nil
    return type(kept) == "table" and kept or {}
end

function F.Write(fields)
    local kept = F.Read()
    for _, k in ipairs(F.Fields) do
        if fields[k] ~= nil then kept[k] = fields[k] end
        if fields[k] == false then kept[k] = nil end
    end
    pcall(Ext.IO.SaveFile, F.Path, Ext.Json.Stringify(kept))
    return kept
end
