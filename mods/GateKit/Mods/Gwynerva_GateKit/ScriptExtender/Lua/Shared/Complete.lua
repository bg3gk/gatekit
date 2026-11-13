-- Completion for the in-game console: what could follow the name being typed.
--
--   GK.Complete("GK.Pa")  ->  { base = "GK.", prefix = "Pa", matches = { "Party" }, more = 0 }
--
-- Works on the dotted name at the end of the text. Tables are listed by walking them; nothing
-- is called. `Osi` resolves its functions by name on demand and cannot be listed on the stock
-- Script Extender, so there the GateKit lists nothing rather than guessing; on the GateKit's own
-- build (docs/se-fork.md) the server lists them, with the kind and parameter types of the
-- first match in `hint`.

local MAX_MATCHES = 40

local meta = getmetatable(_G)
local real = meta and type(meta.__index) == "table" and meta.__index or _G

local KEYWORDS = { "and", "break", "do", "else", "elseif", "end", "false", "for", "function", "goto", "if", "in",
    "local", "nil", "not", "or", "repeat", "return", "then", "true", "until", "while" }

local function root(name)
    if name == "GK" then return GK end
    if name == "Mods" then return Mods end
    local value = rawget(real, name)
    if value == nil then value = _G[name] end
    return value
end

local function keysOf(value)
    local keys = {}
    local kind = type(value)
    if kind == "table" then
        local seen = {}
        local depth = 0
        while type(value) == "table" and depth < 4 do
            for key in pairs(value) do
                if type(key) == "string" and not seen[key] then seen[key] = true keys[#keys + 1] = key end
            end
            -- a mod's Ext is a thin table over the real one: follow __index tables
            local m = getmetatable(value)
            value = m and type(m.__index) == "table" and m.__index or nil
            depth = depth + 1
        end
    elseif kind == "userdata" then
        pcall(function()
            for key in pairs(value) do
                if type(key) == "string" then keys[#keys + 1] = key end
            end
        end)
    end
    return keys
end

-- Osiris names: only the GateKit's Script Extender build can list them (Ext.Osiris.GetFunctions,
-- server side). Read once the story is loaded, then kept.
local osiris

local function osirisNames()
    if osiris then return osiris end
    local ok, all = pcall(function() return Ext.Osiris.GetFunctions() end)
    if not ok or type(all) ~= "table" or #all == 0 then return nil end
    local names, info = {}, {}
    for _, f in ipairs(all) do
        local name = f.Name
        if type(name) == "string" then
            local params = {}
            for i, p in ipairs(f.Params or {}) do
                params[i] = (p.Out and "out " or "") .. tostring(p.Type)
            end
            local signature = tostring(f.Type) .. " " .. name .. "(" .. table.concat(params, ", ") .. ")"
            if info[name] then
                info[name] = info[name] .. "  |  " .. signature
            else
                info[name] = signature
                names[#names + 1] = name
            end
        end
    end
    osiris = { names = names, info = info }
    return osiris
end

function GK.Complete(text)
    local expr = text:match("[%a_][%w_%.:]*$") or ""
    local out = { base = "", prefix = expr, matches = {}, more = 0 }
    if expr == "" then return out end

    local parts = {}
    for part in (expr .. "\0"):gmatch("([^%.:%z]*)[%.:%z]") do parts[#parts + 1] = part end
    local prefix = table.remove(parts) or ""
    out.prefix = prefix
    out.base = expr:sub(1, #expr - #prefix)

    local candidates, info
    local listed = #parts == 1 and parts[1] == "Osi" and osirisNames() or nil
    if listed then
        candidates, info = listed.names, listed.info
    elseif #parts == 0 then
        candidates = keysOf(real)
        candidates[#candidates + 1] = "GK"
        for _, word in ipairs(KEYWORDS) do candidates[#candidates + 1] = word end
    else
        local value = root(parts[1])
        for i = 2, #parts do
            if value == nil then break end
            local ok, nextValue = pcall(function() return value[parts[i]] end)
            value = ok and nextValue or nil
        end
        if value == nil then return out end
        candidates = keysOf(value)
    end

    local lower = prefix:lower()
    local seen, exact, loose = {}, {}, {}
    for _, key in ipairs(candidates) do
        if not seen[key] and key:match("^[%a_][%w_]*$") then
            seen[key] = true
            if key:sub(1, #prefix) == prefix then
                exact[#exact + 1] = key
            elseif key:sub(1, #prefix):lower() == lower then
                loose[#loose + 1] = key
            end
        end
    end
    table.sort(exact)
    table.sort(loose)
    for _, key in ipairs(loose) do exact[#exact + 1] = key end
    for i, key in ipairs(exact) do
        if i > MAX_MATCHES then out.more = #exact - MAX_MATCHES break end
        out.matches[i] = key
    end
    -- what the first match is, where that is known (Osiris: kind and parameter types)
    if info and out.matches[1] then out.hint = GK.CutUtf8(info[out.matches[1]], 300) end
    return out
end

--- The longest text every match starts with (compared without case), in the first match's spelling.
function GK.CommonPrefix(matches)
    local first = matches[1]
    if not first then return "" end
    local length = #first
    for i = 2, #matches do
        local other = matches[i]
        local n = 0
        while n < length and n < #other and first:sub(n + 1, n + 1):lower() == other:sub(n + 1, n + 1):lower() do n = n + 1 end
        length = n
    end
    return first:sub(1, length)
end
