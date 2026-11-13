-- Weave, expressions: what stands in {{ }}, in :attribute="", #if="" and @event="".
--
-- They are Lua. On top of Lua a thin layer of familiar signs is understood and translated:
--
--   !x   a != b   a === b   a && b   a || b   a ?? b   a ? b : c   a?.b
--   count++   count += 2   null   items.length   `Hello, ${name}!`
--
-- It is a translation of signs, not JavaScript: lists count from 1, 0 and "" are true, and
-- strings are joined with .. or with a `template ${string}`. Plain Lua works beside it.
--
-- An expression is compiled once per text (W.compile) and evaluated against a scope: a chain
-- of tables looked up in turn - loop variables, the component's state, its computed values,
-- its properties, its functions, then a fixed list of safe globals. Nothing else is reachable
-- from a template: not Ext, not Osi, not the game.

Weave = Weave or {}
local W = Weave

-- --- Tokens ---------------------------------------------------------------------------------

local OPERATORS = {      -- longest first
    "===", "!==", "...", "?.", "??", "&&", "||", "!=", "==", "~=", "<=", ">=", "++", "--", "+=", "-=", "*=", "/=",
    "..", "//", "::", "<<", ">>",
}
local KEYWORDS = {}
for word in ("and break do else elseif end false for function goto if in local nil not or repeat return then true until while"):gmatch("%a+") do
    KEYWORDS[word] = true
end

local transpile

-- `text ${expr} text` -> ("text " .. tostring(expr) .. " text")
local function templateString(body)
    local parts, pos = {}, 1
    while pos <= #body do
        local open = body:find("${", pos, true)
        if not open then
            parts[#parts + 1] = string.format("%q", body:sub(pos))
            break
        end
        if open > pos then parts[#parts + 1] = string.format("%q", body:sub(pos, open - 1)) end
        local depth, close = 1, nil
        for i = open + 2, #body do
            local char = body:sub(i, i)
            if char == "{" then depth = depth + 1
            elseif char == "}" then
                depth = depth - 1
                if depth == 0 then close = i break end
            end
        end
        if not close then error("${ is never closed in a template string", 0) end
        parts[#parts + 1] = "__str(" .. transpile(body:sub(open + 2, close - 1)) .. ")"
        pos = close + 1
    end
    if #parts == 0 then return '""' end
    return "(" .. table.concat(parts, " .. ") .. ")"
end

local function tokenize(text)
    local tokens, pos, length = {}, 1, #text
    while pos <= length do
        local char = text:sub(pos, pos)
        if char:match("%s") then
            pos = pos + 1
        elseif char:match("[%a_]") then
            local word = text:match("^[%w_]+", pos)
            tokens[#tokens + 1] = word
            pos = pos + #word
        elseif char:match("%d") or (char == "." and text:sub(pos + 1, pos + 1):match("%d")) then
            local number = text:match("^0[xX]%x+", pos) or text:match("^%d*%.?%d*[eE][+-]?%d+", pos) or text:match("^%d*%.?%d*", pos)
            tokens[#tokens + 1] = number
            pos = pos + #number
        elseif char == '"' or char == "'" or char == "`" then
            local stop = pos + 1
            while stop <= length do
                local c = text:sub(stop, stop)
                if c == "\\" then stop = stop + 1
                elseif c == char then break end
                stop = stop + 1
            end
            if stop > length then error("a string is never closed: " .. text:sub(pos, pos + 20), 0) end
            if char == "`" then
                tokens[#tokens + 1] = templateString(text:sub(pos + 1, stop - 1))
            else
                tokens[#tokens + 1] = text:sub(pos, stop)
            end
            pos = stop + 1
        else
            local found
            for _, operator in ipairs(OPERATORS) do
                if text:sub(pos, pos + #operator - 1) == operator then found = operator break end
            end
            found = found or char
            tokens[#tokens + 1] = found
            pos = pos + #found
        end
    end
    return tokens
end

-- --- Rewriting ------------------------------------------------------------------------------

local OPEN = { ["("] = ")", ["["] = "]", ["{"] = "}" }
local CLOSE = { [")"] = "(", ["]"] = "[", ["}"] = "{" }

local function isName(token)
    return token ~= nil and token:match("^[%a_][%w_]*$") ~= nil and not KEYWORDS[token]
end

local function isValue(token)      -- something an access chain can end with
    if token == nil then return false end
    return isName(token) or token:match("^%d") ~= nil or token:match("^%.%d") ~= nil
        or (#token > 1 and token:match("^[\"'(]") ~= nil)
end

local function matchingOpen(tokens, at)
    local depth = 0
    for i = at, 1, -1 do
        if CLOSE[tokens[i]] then depth = depth + 1
        elseif OPEN[tokens[i]] then
            depth = depth - 1
            if depth == 0 then return i end
        end
    end
    error("brackets do not match", 0)
end

-- Where the access chain that ends at `last` begins: a.b[c](d).e -> the index of `a`.
local function chainStart(tokens, last)
    local i = last
    while true do
        local token = tokens[i]
        if token == ")" or token == "]" then
            i = matchingOpen(tokens, i)
            local before = tokens[i - 1]
            if isName(before) or before == ")" or before == "]" then i = i - 1 else return i end
        elseif isValue(token) then
            local before = tokens[i - 1]
            if (before == "." or before == "?.") and i > 2 then i = i - 2 else return i end
        else
            return i + 1
        end
    end
end

local function splice(tokens, from, to, replacement)
    local out = {}
    for i = 1, from - 1 do out[#out + 1] = tokens[i] end
    for _, token in ipairs(replacement) do out[#out + 1] = token end
    for i = to + 1, #tokens do out[#out + 1] = tokens[i] end
    return out
end

local function slice(tokens, from, to)
    local out = {}
    for i = from, to do out[#out + 1] = tokens[i] end
    return out
end

local SIMPLE = { ["==="] = "==", ["!=="] = "~=", ["!="] = "~=", ["&&"] = "and", ["||"] = "or", ["??"] = "or",
    ["!"] = "not", ["null"] = "nil", ["undefined"] = "nil" }

-- cond ? a : b at the top level of a token list -> __if(cond, function() return a end, ...)
local function ternary(tokens)
    -- inside brackets first, each comma-separated part on its own
    local out, i = {}, 1
    while i <= #tokens do
        local token = tokens[i]
        if OPEN[token] then
            local depth, close = 0, nil
            for j = i, #tokens do
                if OPEN[tokens[j]] then depth = depth + 1
                elseif CLOSE[tokens[j]] then
                    depth = depth - 1
                    if depth == 0 then close = j break end
                end
            end
            if not close then error("brackets do not match", 0) end
            out[#out + 1] = token
            local part, level = {}, 0
            for j = i + 1, close do
                local inner = tokens[j]
                if j == close or ((inner == "," or inner == ";") and level == 0) then
                    for _, done in ipairs(ternary(part)) do out[#out + 1] = done end
                    if j < close then out[#out + 1] = inner end
                    part = {}
                else
                    if OPEN[inner] then level = level + 1 elseif CLOSE[inner] then level = level - 1 end
                    part[#part + 1] = inner
                end
            end
            out[#out + 1] = tokens[close]
            i = close + 1
        else
            out[#out + 1] = token
            i = i + 1
        end
    end
    tokens = out
    local question, depth = nil, 0
    for j, token in ipairs(tokens) do
        if OPEN[token] then depth = depth + 1
        elseif CLOSE[token] then depth = depth - 1
        elseif token == "?" and depth == 0 then question = j break end
    end
    if not question then return tokens end
    local colon, pending
    depth, pending = 0, 0
    for j = question + 1, #tokens do
        local token = tokens[j]
        if OPEN[token] then depth = depth + 1
        elseif CLOSE[token] then depth = depth - 1
        elseif depth == 0 and token == "?" then pending = pending + 1
        elseif depth == 0 and token == ":" then
            if pending == 0 then colon = j break end
            pending = pending - 1
        end
    end
    if not colon then error("? without :", 0) end
    -- the condition starts after an assignment or `return`, if there is one before it
    local start = 1
    for j = question - 1, 1, -1 do
        if tokens[j] == "=" or tokens[j] == "return" then start = j + 1 break end
    end
    local result = slice(tokens, 1, start - 1)
    local function add(list) for _, token in ipairs(list) do result[#result + 1] = token end end
    add({ "__if", "(" })
    add(slice(tokens, start, question - 1))
    add({ ",", "function", "(", ")", "return" })
    add(ternary(slice(tokens, question + 1, colon - 1)))
    add({ "end", ",", "function", "(", ")", "return" })
    add(ternary(slice(tokens, colon + 1, #tokens)))
    add({ "end", ")" })
    return result
end

--- Translates the sugar to plain Lua. Text without sugar comes back meaning the same.
transpile = function(text)
    local tokens = tokenize(text)
    -- signs that are one-to-one
    for i, token in ipairs(tokens) do
        if SIMPLE[token] and not (token == "!" and tokens[i + 1] == "=") then tokens[i] = SIMPLE[token] end
    end
    -- signs that need to know what stands before them
    local i = 1
    while i <= #tokens do
        local token = tokens[i]
        if token == "?." then                                  -- a?.b -> (a or __nil).b
            local start = chainStart(tokens, i - 1)
            local chain = slice(tokens, start, i - 1)
            table.insert(chain, 1, "(")
            chain[#chain + 1] = "or"
            chain[#chain + 1] = "__nil"
            chain[#chain + 1] = ")"
            chain[#chain + 1] = "."
            tokens = splice(tokens, start, i, chain)
            i = start + #chain
        elseif token == "length" and tokens[i - 1] == "." and tokens[i + 1] ~= "(" and tokens[i + 1] ~= "=" then
            local start = chainStart(tokens, i - 2)            -- a.b.length -> #(a.b)
            local chain = slice(tokens, start, i - 2)
            table.insert(chain, 1, "(")
            table.insert(chain, 1, "#")
            chain[#chain + 1] = ")"
            tokens = splice(tokens, start, i, chain)
            i = start + #chain
        elseif token == "++" or token == "--" then             -- a.b++ -> a.b = a.b + 1
            local start = chainStart(tokens, i - 1)
            local chain = slice(tokens, start, i - 1)
            local replacement = slice(tokens, start, i - 1)
            replacement[#replacement + 1] = "="
            for _, part in ipairs(chain) do replacement[#replacement + 1] = part end
            replacement[#replacement + 1] = token == "++" and "+" or "-"
            replacement[#replacement + 1] = "1"
            tokens = splice(tokens, start, i, replacement)
            i = start + #replacement
        elseif token == "+=" or token == "-=" or token == "*=" or token == "/=" then
            local start = chainStart(tokens, i - 1)            -- a += b -> a = a + (b)
            local chain = slice(tokens, start, i - 1)
            local stop = #tokens
            for j = i + 1, #tokens do
                if tokens[j] == ";" then stop = j - 1 break end
            end
            local replacement = slice(tokens, start, i - 1)
            replacement[#replacement + 1] = "="
            for _, part in ipairs(chain) do replacement[#replacement + 1] = part end
            replacement[#replacement + 1] = token:sub(1, 1)
            replacement[#replacement + 1] = "("
            for j = i + 1, stop do replacement[#replacement + 1] = tokens[j] end
            replacement[#replacement + 1] = ")"
            tokens = splice(tokens, start, stop, replacement)
            i = start + #replacement
        else
            i = i + 1
        end
    end
    local hasQuestion = false
    for _, token in ipairs(tokens) do
        if token == "?" then hasQuestion = true break end
    end
    if hasQuestion then
        -- statement by statement, so `a = c ? 1 : 2; b = 3` works
        local out, part = {}, {}
        for j = 1, #tokens + 1 do
            local token = tokens[j]
            if token == nil or token == ";" then
                for _, done in ipairs(ternary(part)) do out[#out + 1] = done end
                if token then out[#out + 1] = token end
                part = {}
            else
                part[#part + 1] = token
            end
        end
        tokens = out
    end
    return table.concat(tokens, " ")
end
W.transpile = transpile

-- --- Scopes ---------------------------------------------------------------------------------

local GLOBALS = {
    math = math, string = string, table = table, tostring = tostring, tonumber = tonumber, type = type,
    ipairs = ipairs, pairs = pairs, select = select, next = next, unpack = table.unpack, print = print,
    __nil = setmetatable({}, { __newindex = function() end }),
    __str = function(value) return value == nil and "" or tostring(value) end,
    __if = function(condition, yes, no)
        if condition then return yes() end
        return no()
    end,
}
--- Functions every expression can call: W.Globals.clamp = function(...) ... end
W.Globals = GLOBALS

local current                 -- the scope the running expression looks names up in
local ENV = setmetatable({}, {
    __index = function(_, name)
        local scope = current
        while scope do
            local value = scope.vars[name]
            if value ~= nil then return value end
            if scope.get then
                value = scope.get(name)
                if value ~= nil then return value end
            end
            scope = scope.parent
        end
        return GLOBALS[name]
    end,
    __newindex = function(_, name, value)
        local scope, last = current, nil
        while scope do
            if scope.vars[name] ~= nil or (scope.has and scope.has(name)) then
                if scope.set then scope.set(name, value) else scope.vars[name] = value end
                return
            end
            last, scope = scope, scope.parent
        end
        if last and last.set then last.set(name, value) elseif last then last.vars[name] = value end
    end,
})

--- A scope: `vars` are looked up first (reactive tables are read through, so reads subscribe);
--- get / has / set(name[, value]) reach whatever else belongs to it; `parent` comes next.
function W.scope(vars, parent, access)
    local scope = { vars = vars or {}, parent = parent }
    if access then scope.get, scope.has, scope.set = access.get, access.has, access.set end
    return scope
end

local compiled = {}

--- Compiles an expression (or statements, for events) once per text.
--- Returns { text, lua, fn, statement } or nil, "why not".
function W.compile(text)
    local cached = compiled[text]
    if cached ~= nil then
        if cached then return cached end
        return nil, compiled["!" .. text]
    end
    local ok, lua = pcall(transpile, text)
    local fn, problem, statement
    if ok then
        fn, problem = load("return " .. lua, ENV)
        if not fn then
            fn = load(lua, ENV)
            statement = fn ~= nil
        end
    else
        problem = lua
    end
    if not fn then
        compiled[text] = false
        compiled["!" .. text] = tostring(problem)
        return nil, tostring(problem)
    end
    cached = { text = text, lua = lua, fn = fn, statement = statement or false }
    compiled[text] = cached
    return cached
end

--- Evaluates a compiled expression in a scope. Returns ok, value | problem.
function W.eval(expression, scope)
    local previous = current
    current = scope
    local ok, value = pcall(expression.fn)
    current = previous
    return ok, value
end

--- "item in items", "item, i in items", "(item, i) in items", "n in 5" ->
--- { item = "item", index = "i" | nil, source = compiled } or nil, why.
function W.parseFor(text)
    local names, source = text:match("^%s*%(?%s*([%w_,%s]-)%s*%)?%s+in%s+(.-)%s*$")
    if not names or source == "" then return nil, "#for takes: item in list, or item, i in list" end
    local item, index = names:match("^([%a_][%w_]*)%s*,?%s*([%w_]*)$")
    if not item then return nil, "#for takes: item in list, or item, i in list" end
    local expression, problem = W.compile(source)
    if not expression then return nil, problem end
    return { item = item, index = index ~= "" and index or nil, source = expression }
end
