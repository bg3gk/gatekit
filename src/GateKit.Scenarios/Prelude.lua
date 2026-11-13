-- Loaded into every scenario before its own code: the small helpers a scenario needs to say
-- what Python said with `assert x == y, (a, b)`. Everything here is plain Lua; the `game`
-- object and the file helpers come from the host (see README.md).

--- Renders any value as a one-line string, for an assertion message.
function fmt(value, seen)
    local kind = type(value)
    if kind == "string" then return string.format("%q", value) end
    if kind ~= "table" then return tostring(value) end
    seen = seen or {}
    if seen[value] then return "<cycle>" end
    seen[value] = true
    local parts, n = {}, 0
    for i, item in ipairs(value) do
        n = i
        parts[#parts + 1] = fmt(item, seen)
    end
    local named = {}
    for key in pairs(value) do
        if not (type(key) == "number" and key >= 1 and key <= n and key % 1 == 0) then named[#named + 1] = tostring(key) end
    end
    table.sort(named)
    for _, key in ipairs(named) do parts[#parts + 1] = key .. " = " .. fmt(value[key], seen) end
    seen[value] = nil
    return "{" .. table.concat(parts, ", ") .. "}"
end

--- Deep equality (lists and records alike); numbers may differ by `tolerance`.
function same(a, b, tolerance)
    if type(a) == "number" and type(b) == "number" and tolerance then return math.abs(a - b) <= tolerance end
    if a == b then return true end
    if type(a) ~= "table" or type(b) ~= "table" then return false end
    for key, value in pairs(a) do
        if not same(value, b[key], tolerance) then return false end
    end
    for key in pairs(b) do
        if a[key] == nil then return false end
    end
    return true
end

--- Two lists of numbers that agree to `tolerance` (the Python scenarios' `near`).
function near(a, b, tolerance)
    tolerance = tolerance or 0.02
    if type(a) ~= "table" or type(b) ~= "table" then return false end
    for i, value in ipairs(b) do
        if type(a[i]) ~= "number" or math.abs(a[i] - value) > tolerance then return false end
    end
    return true
end

--- assert() that renders whatever it is given: check(cond, "what went wrong", value, ...)
function check(condition, message, ...)
    if condition then return condition end
    local parts = { tostring(message or "check failed") }
    for i = 1, select("#", ...) do parts[#parts + 1] = fmt((select(i, ...))) end
    error(table.concat(parts, ": "), 2)
end

--- check(same(got, wanted), ...), with both sides in the message.
function eq(got, wanted, message)
    if same(got, wanted) then return got end
    error((message or "not equal") .. ": got " .. fmt(got) .. ", wanted " .. fmt(wanted), 2)
end

--- A sorted copy of a list.
function sorted(list, compare)
    local out = {}
    for i, value in ipairs(list) do out[i] = value end
    table.sort(out, compare)
    return out
end

--- The keys of a record, sorted by their text.
function keys(record)
    local out = {}
    for key in pairs(record or {}) do out[#out + 1] = key end
    table.sort(out, function(a, b) return tostring(a) < tostring(b) end)
    return out
end

--- Every value of a list or record, sorted by their text.
function values(record)
    local out = {}
    for _, value in pairs(record or {}) do out[#out + 1] = value end
    table.sort(out, function(a, b) return tostring(a) < tostring(b) end)
    return out
end

--- Whether a list holds a value.
function has(list, wanted)
    for _, value in ipairs(list or {}) do
        if same(value, wanted) then return true end
    end
    return false
end

--- How many entries a record has (lists: #list).
function count(record)
    local n = 0
    for _ in pairs(record or {}) do n = n + 1 end
    return n
end

--- Plain substring search (no patterns).
function contains(text, part)
    return type(text) == "string" and string.find(text, part, 1, true) ~= nil
end

function startswith(text, part) return string.sub(text or "", 1, #part) == part end
function endswith(text, part) return part == "" or string.sub(text or "", -#part) == part end

--- "a, b, c" from a list (empty: `empty`).
function join(list, separator, empty)
    local parts = {}
    for i, value in ipairs(list or {}) do parts[i] = tostring(value) end
    if #parts == 0 then return empty or "" end
    return table.concat(parts, separator or ", ")
end

--- A Lua list literal of strings: lua_list{"a","b"} -> {"a", "b"}
function lua_list(list)
    local parts = {}
    for i, value in ipairs(list) do parts[i] = string.format("%q", tostring(value)) end
    return "{" .. table.concat(parts, ", ") .. "}"
end

-- bytes(text): how many bytes the text takes as UTF-8 (what the game counts; the host's `#`
-- counts characters, so the two differ wherever the text is not plain ASCII). It comes from
-- the host, because this interpreter's string.byte does not know characters above ASCII.

--- A long-bracket Lua string literal, for text handed to the game as code.
function lua_string(text) return "[==[" .. text .. "]==]" end

--- What the Lua expression `expression` reads, once three readings in a row agree: nothing
--- moves by itself any more (the overlay scenario's `settled`).
function settled(expression, side)
    local last, agreed = nil, 0
    for _ = 1, 40 do
        local now = game.lua(side or "client", "return " .. expression)
        agreed = same(now, last) and agreed + 1 or 0
        if agreed >= 3 then return now end
        last = now
        game.sleep(0.15)
    end
    error("the layout never came to rest: " .. fmt(last), 2)
end
