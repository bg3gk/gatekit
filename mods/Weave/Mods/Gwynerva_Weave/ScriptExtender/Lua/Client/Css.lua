-- Weave, part 2: style sheets. A CSS-like language for IMGUI.
--
--   @theme gate { --bg: #17120e; --accent: #c8a45c; }      named sets of variables
--   :root { --gap: 8; }                                     variables every theme shares
--   window { background: var(--bg); padding: 10 12; }       rules: selectors { property: value; }
--   button.primary:hover, #run { background: var(--accent); }
--
-- Selectors: tag, .class, #id, * and their compounds (button.primary#run); "a b" (b anywhere
-- inside a), "a > b" (b directly inside a); lists with commas; [name], [name=value], [name^=v],
-- [name$=v], [name*=v] against the template's attributes; :not(compound), :first-child,
-- :last-child, :only-child, :nth-child(odd | even | 3 | 2n+1), :empty, :disabled, :enabled
-- (more: Weave.RegisterPseudo). :hover, :active, :selected / :checked are not matched: they
-- pick the colour slot IMGUI uses for that state. The most specific rule wins (id, then class /
-- attribute / pseudo-class, then tag), later rules win ties; an element's style="" beats them all.
--
-- Rules nest, as in CSS: "&" stands for the parent's selector, without it the rule is for what
-- is inside the parent:
--   .card { color: #fff; &:first-child { color: red; } .title { font: +1; } & > text { wrap: on; }
--           @media (min-width: 700) { padding: 16; } }
--
-- Lengths are pixels (12 or 12px), 10vw / 10vh (percent of the game's picture), 50% where
-- there is something to take a percentage of, and calc(), min(), max(), clamp() over them.
--
-- Rules can depend on size, as on the web:
--   @media (min-width: 700) { .side { display: block; } }            the window's width / height
--   @container (max-width: 400) { .card { font: -1; } }              the nearest ancestor with
--                                                                     `container-type: size`
-- with min-width, max-width, min-height, max-height joined by "and". Sizes are the units of
-- the template's width="" (pixels at the default scaling).
--
-- What each property turns into is decided by Weave.Resolve at the end of this file.

Weave = Weave or {}
local UI = Weave

-- --- Colours ------------------------------------------------------------------------------
-- Colours written in a style sheet mean what they mean in a browser, and IMGUI is given them
-- as written: GateKit's build of Script Extender converts them for an HDR picture itself.
-- raw(r, g, b, a) passes numbers through without the clamping to 0..1.

local function clamp(s)
    return math.max(0, math.min(1, s))
end

local function trim(text)
    return (text:match("^%s*(.-)%s*$"))
end

-- Splits "a, f(b, c), d" at the top-level commas.
local function splitArgs(text)
    local out, depth, start = {}, 0, 1
    for i = 1, #text do
        local char = text:sub(i, i)
        if char == "(" then depth = depth + 1
        elseif char == ")" then depth = depth - 1
        elseif char == "," and depth == 0 then
            out[#out + 1] = trim(text:sub(start, i - 1))
            start = i + 1
        end
    end
    out[#out + 1] = trim(text:sub(start))
    return out
end

local NAMED = {
    transparent = { 0, 0, 0, 0 }, black = { 0, 0, 0, 1 }, white = { 1, 1, 1, 1 },
    red = { 1, 0, 0, 1 }, green = { 0, 0.5, 0, 1 }, blue = { 0, 0, 1, 1 }, yellow = { 1, 1, 0, 1 },
    gray = { 0.5, 0.5, 0.5, 1 }, grey = { 0.5, 0.5, 0.5, 1 }, orange = { 1, 0.65, 0, 1 },
}
local TRANSPARENT = NAMED.transparent

-- A colour as written -> { r, g, b, a } in sRGB 0..1 (as a browser would understand it), or nil.
local function srgb(value)
    value = trim(value)
    local hex = value:match("^#(%x+)$")
    if hex then
        if #hex == 3 or #hex == 4 then hex = hex:gsub(".", "%0%0") end
        if #hex ~= 6 and #hex ~= 8 then return nil end
        local function part(i) return tonumber(hex:sub(i, i + 1), 16) / 255 end
        return { part(1), part(3), part(5), #hex == 8 and part(7) or 1 }
    end
    if NAMED[value:lower()] then
        local c = NAMED[value:lower()]
        return { c[1], c[2], c[3], c[4] }
    end
    local name, inside = value:match("^([%a%-]+)%((.*)%)$")
    if not name then return nil end
    name = name:lower()
    local args = splitArgs(inside)
    if name == "rgb" or name == "rgba" then
        local r, g, b, a = tonumber(args[1]), tonumber(args[2]), tonumber(args[3]), tonumber(args[4] or 1)
        if not (r and g and b and a) then return nil end
        return { r / 255, g / 255, b / 255, a }
    elseif name == "alpha" then           -- alpha(colour, 0.5): the same colour, that opaque
        local c, a = srgb(args[1] or ""), tonumber(args[2])
        if not (c and a) then return nil end
        return { c[1], c[2], c[3], a }
    elseif name == "mix" then             -- mix(a, b, 0.25): a quarter of the way from a to b
        local a, b, f = srgb(args[1] or ""), srgb(args[2] or ""), tonumber(args[3] or 0.5)
        if not (a and b and f) then return nil end
        local out = {}
        for i = 1, 4 do out[i] = a[i] + (b[i] - a[i]) * f end
        return out
    elseif name == "lighten" or name == "darken" then
        local c, f = srgb(args[1] or ""), tonumber(args[2] or 0.1)
        if not (c and f) then return nil end
        local to = name == "lighten" and 1 or 0
        return { c[1] + (to - c[1]) * f, c[2] + (to - c[2]) * f, c[3] + (to - c[3]) * f, c[4] }
    end
    return nil
end

--- A colour as written in a style sheet -> { r, g, b, a } ready for widget:SetColor, or nil.
function UI.ParseColor(value)
    value = trim(tostring(value))
    local raw = value:match("^raw%((.*)%)$")
    if raw then
        local args = splitArgs(raw)
        local r, g, b, a = tonumber(args[1]), tonumber(args[2]), tonumber(args[3]), tonumber(args[4] or 1)
        if not (r and g and b and a) then return nil end
        return { r, g, b, a }
    end
    local c = srgb(value)
    if not c then return nil end
    return { clamp(c[1]), clamp(c[2]), clamp(c[3]), clamp(c[4]) }
end

local DIRECTIONS = {       -- CSS: 0deg points up, 90deg to the right; "to right" = 90deg
    ["to top"] = 0, ["to right"] = 90, ["to bottom"] = 180, ["to left"] = 270,
    ["to top right"] = 45, ["to bottom right"] = 135, ["to bottom left"] = 225, ["to top left"] = 315,
}

--- "linear-gradient(135deg, #a, #b)" -> { from, to, angle } with colours ready for IMGUI, or nil.
function UI.ParseGradient(value)
    local inside = trim(tostring(value)):match("^linear%-gradient%((.*)%)$")
    if not inside then return nil end
    local args = splitArgs(inside)
    local angle = 180
    local first = (args[1] or ""):lower()
    if DIRECTIONS[first] or first:match("^-?[%d.]+deg$") or tonumber(first) then
        angle = DIRECTIONS[first] or tonumber((first:gsub("deg$", "")))
        table.remove(args, 1)
    end
    if #args ~= 2 then return nil end          -- two stops: what the renderer draws
    local from, to = UI.ParseColor(args[1]), UI.ParseColor(args[2])
    if not (from and to) then return nil end
    return { from, to, angle }
end

-- Words of a value, brackets kept together: "4 4 12 rgba(0, 0, 0, 0.5)" -> 4 words.
local function words(value)
    local out, depth, current = {}, 0, ""
    for i = 1, #value do
        local char = value:sub(i, i)
        if char == "(" then depth = depth + 1 elseif char == ")" then depth = depth - 1 end
        if char:match("%s") and depth == 0 then
            if current ~= "" then out[#out + 1] = current current = "" end
        else
            current = current .. char
        end
    end
    if current ~= "" then out[#out + 1] = current end
    return out
end

-- --- Lengths ------------------------------------------------------------------------------
-- A length is pixels plus a percentage of something only the layout knows: a linear form.
-- vw and vh are percent of the game's picture and become pixels at once.

local viewportUsed = false          -- did the value being read depend on the picture's size

local function viewport(index)
    viewportUsed = true
    return UI.Backend.viewport()[index]
end

local UNITS = {             -- unit -> pixels, percent
    [""] = function(n) return n, 0 end,
    px = function(n) return n, 0 end,
    ["%"] = function(n) return 0, n end,
    vw = function(n) return n * viewport(1) / 100, 0 end,
    vh = function(n) return n * viewport(2) / 100, 0 end,
}

-- min() and max(): pixels compare with pixels and percentages with percentages, nothing else.
local function pick(better)
    return function(args, fail)
        local px, pct = args[1][1], args[1][2]
        for i = 2, #args do
            local otherPx, otherPct = args[i][1], args[i][2]
            if pct == 0 and otherPct == 0 then
                px = better(px, otherPx)
            elseif px == 0 and otherPx == 0 then
                pct = better(pct, otherPct)
            else
                fail("min(), max() and clamp() cannot compare pixels with %")
            end
        end
        return px, pct
    end
end

local FUNCTIONS = { min = pick(math.min), max = pick(math.max) }
function FUNCTIONS.calc(args, fail)
    if #args ~= 1 then fail("calc() takes one expression") end
    return args[1][1], args[1][2]
end
function FUNCTIONS.clamp(args, fail)
    if #args ~= 3 then fail("clamp() takes: lowest, wanted, highest") end
    return FUNCTIONS.max({ args[1], { FUNCTIONS.min({ args[2], args[3] }, fail) } }, fail)
end

-- "calc(50% - 2 * 8px)" -> { { 0, 50 }, "-", { 2, 0 }, "*", { 8, 0 } } inside "calc", "(", ")".
local function lengthTokens(text)
    local out, pos = {}, 1
    while pos <= #text do
        local space = text:match("^%s+", pos)
        local number, unit = text:match("^(%d*%.?%d+)([%a%%]*)", pos)
        local name = text:match("^%a+", pos)
        if space then
            pos = pos + #space
        elseif number then
            local convert = UNITS[unit:lower()]
            if not convert then return nil, "unknown unit \"" .. unit .. "\"" end
            out[#out + 1] = { convert(tonumber(number)) }
            pos = pos + #number + #unit
        elseif name then
            out[#out + 1] = name:lower()
            pos = pos + #name
        else
            local char = text:sub(pos, pos)
            if not ("+-*/(),"):find(char, 1, true) then return nil end
            out[#out + 1] = char
            pos = pos + 1
        end
    end
    return out
end

--- A length as written -> pixels, percent (both numbers; percent is of whatever the property
--- is measured against), or nil, "why not". "12", "12px", "50%", "10vw", "calc(100% - 2 * 8)",
--- "min(40vw, 600)", "clamp(200, 50%, 800)" - pixels and % only meet in sums.
function UI.ParseLength(text)
    local plain = tonumber(text)
    if plain then return plain, 0 end
    local list, problem = lengthTokens(text)
    if not list then return nil, problem or ("not a number: " .. text) end
    local at, sum = 1, nil
    local function fail(why) error(why or ("not a number: " .. text), 0) end
    local function primary()
        local token = list[at]
        at = at + 1
        if type(token) == "table" then return token[1], token[2] end
        if token == "-" then
            local px, pct = primary()
            return -px, -pct
        elseif token == "(" then
            local px, pct = sum()
            if list[at] ~= ")" then fail() end
            at = at + 1
            return px, pct
        elseif FUNCTIONS[token] and list[at] == "(" then
            local args = {}
            repeat
                at = at + 1
                args[#args + 1] = { sum() }
            until list[at] ~= ","
            if list[at] ~= ")" then fail() end
            at = at + 1
            return FUNCTIONS[token](args, fail)
        end
        fail()
    end
    local function product()
        local px, pct = primary()
        while list[at] == "*" or list[at] == "/" do
            local divide = list[at] == "/"
            at = at + 1
            local otherPx, otherPct = primary()
            if divide then
                if otherPct ~= 0 or otherPx == 0 then fail("calc() divides by plain numbers other than 0 only") end
                px, pct = px / otherPx, pct / otherPx
            elseif otherPct == 0 then
                px, pct = px * otherPx, pct * otherPx
            elseif pct == 0 then
                px, pct = px * otherPx, px * otherPct
            else
                fail("calc() cannot multiply two percentages")
            end
        end
        return px, pct
    end
    sum = function()
        local px, pct = product()
        while list[at] == "+" or list[at] == "-" do
            local sign = list[at] == "+" and 1 or -1
            at = at + 1
            local otherPx, otherPct = product()
            px, pct = px + sign * otherPx, pct + sign * otherPct
        end
        return px, pct
    end
    local ok, px, pct = pcall(sum)
    if not ok then return nil, px end
    if at <= #list then return nil, "not a number: " .. text end
    return px, pct
end

-- One length -> pixels, or { pixels, percent } where a percentage means something (`percent`).
local function length(word, percent)
    local px, pct = UI.ParseLength(word)
    if not px then return nil, pct end
    if pct == 0 then return px end
    if not percent then return nil, "% has nothing to be a percentage of here: " .. word end
    return { px, pct }
end

-- One to four lengths in the order of CSS -> { top, right, bottom, left }. `auto` (allowed
-- where `auto` is true) stays a word.
local function sides(value, percent, auto)
    local list = {}
    for i, word in ipairs(words(value)) do
        if auto and word:lower() == "auto" then
            list[i] = "auto"
        else
            local one, problem = length(word, percent)
            if one == nil then return nil, problem end
            list[i] = one
        end
    end
    if #list == 0 or #list > 4 then return nil, "takes one to four values" end
    local top, right = list[1], list[2] or list[1]
    return { top, right, list[3] or top, list[4] or right }
end

-- Numbers and one colour in any order ("solid" and the like are skipped): for border and shadow.
local function numbersAndColor(value)
    local list, color = {}, nil
    for _, word in ipairs(words(value)) do
        local n = tonumber((word:gsub("px$", "")))
        if n then
            list[#list + 1] = n
        elseif word ~= "solid" then
            local c = UI.ParseColor(word)
            if not c or color then return nil end
            color = c
        end
    end
    return list, color
end

-- --- Selectors ----------------------------------------------------------------------------
-- An element here is anything with { tag, id, classes = { name = true }, parent } and, for
-- what looks at them, attrs, children, disabled.

-- The parent as the author sees it: what #if and #for build into does not count.
local function parentOf(element)
    local parent = element.parent
    while parent and parent.fragment do parent = parent.parent end
    return parent
end

--- The children of an element as the author sees them (what #if and #for build into is
--- looked through), with `.index[child]`. Remembered on the element until its `flat` is cleared.
function UI.ChildrenOf(parent)
    local flat = parent.flat
    if not flat then
        flat = { index = {} }
        local function add(holder)
            for _, child in ipairs(holder.children or {}) do
                if child.fragment then
                    add(child)
                else
                    flat[#flat + 1] = child
                    flat.index[child] = #flat
                end
            end
        end
        add(parent)
        parent.flat = flat
    end
    return flat
end

-- Which child of its parent an element is, and of how many.
local function place(element)
    local parent = parentOf(element)
    if not parent then return 1, 1 end
    local flat = UI.ChildrenOf(parent)
    return flat.index[element] or 0, #flat
end

local PSEUDO = {}
--- Adds a pseudo-class that is matched (unlike :hover and :active, which pick a colour slot).
--- def = { test = fn(element, argument) -> boolean; parse = fn(text) -> argument | nil, for
--- :name(text); structural = true when the answer depends on the element's siblings or
--- children, so that those are styled again when a list changes }.
function UI.RegisterPseudo(name, def)
    PSEUDO[name] = def
end

UI.RegisterPseudo("first-child", { structural = true, test = function(element) return (place(element)) == 1 end })
UI.RegisterPseudo("last-child", { structural = true, test = function(element)
    local index, count = place(element)
    return index == count
end })
UI.RegisterPseudo("only-child", { structural = true, test = function(element)
    return select(2, place(element)) == 1
end })
UI.RegisterPseudo("nth-child", {
    structural = true,
    parse = function(text)                  -- odd | even | 3 | 2n | 2n+1 | -n+3  ->  { a, b }
        text = text:lower():gsub("%s", "")
        if text == "odd" then return { 2, 1 } end
        if text == "even" then return { 2, 0 } end
        if tonumber(text) then return { 0, tonumber(text) } end
        local a, b = text:match("^([+-]?%d*)n([+-]?%d*)$")
        if not a then return nil end
        return { tonumber(a) or (a == "-" and -1 or 1), tonumber(b) or 0 }
    end,
    test = function(element, formula)
        local index, a, b = place(element), formula[1], formula[2]
        if a == 0 then return index == b end
        local n = (index - b) / a
        return n >= 0 and n == math.floor(n)
    end,
})
-- As on the web, text counts: <text>Hi</text> is not empty.
UI.RegisterPseudo("empty", { structural = true, test = function(element)
    return #UI.ChildrenOf(element) == 0 and not (element.text and element.text:find("%S"))
end })
UI.RegisterPseudo("disabled", { test = function(element) return element.disabled == true end })
UI.RegisterPseudo("enabled", { test = function(element) return element.disabled ~= true end })

local ATTRIBUTE = {         -- [name<op>value]: fn(what the element has, what is wanted)
    ["="] = function(have, want) return have == want end,
    ["^="] = function(have, want) return want ~= "" and have:sub(1, #want) == want end,
    ["$="] = function(have, want) return want ~= "" and have:sub(-#want) == want end,
    ["*="] = function(have, want) return want ~= "" and have:find(want, 1, true) ~= nil end,
}

local function attributeTest(name, compare, want)
    return function(element)
        local have = element.attrs and element.attrs[name]
        if have == nil then return false end
        return compare == nil or compare(tostring(have), want)
    end
end

local function matchesCompound(element, compound)
    if compound.tag and compound.tag ~= element.tag then return false end
    if compound.id and compound.id ~= element.id then return false end
    for _, class in ipairs(compound.classes) do
        if not element.classes[class] then return false end
    end
    for _, check in ipairs(compound.tests) do
        if not check.test(element, check.argument) then return false end
    end
    return true
end

-- Where the bracket that opens at the start of `text` closes, or nil.
local function closingBracket(text)
    local depth = 0
    for i = 1, #text do
        local char = text:sub(i, i)
        if char == "(" then
            depth = depth + 1
        elseif char == ")" then
            depth = depth - 1
            if depth == 0 then return i end
        end
    end
    return nil
end

-- "button.primary[kind=big]:not(.off):hover" -> { tag, id, classes, tests = { { test, argument } },
-- pseudo (a state), specificity, structural }, or nil.
local function parseCompound(text)
    local compound = { classes = {}, tests = {}, specificity = 0 }
    local rest = text
    local tag = rest:match("^[%a_][%w_%-]*") or rest:match("^%*")
    if tag then
        if tag ~= "*" then compound.tag, compound.specificity = tag:lower(), 1 end
        rest = rest:sub(#tag + 1)
    end
    local function test(fn, argument, weight)
        compound.tests[#compound.tests + 1] = { test = fn, argument = argument }
        compound.specificity = compound.specificity + weight
    end
    while rest ~= "" do
        local kind, name = rest:match("^([#.:])([%w_%-]+)")
        local inside = rest:match("^%[(.-)%]")
        if inside then
            local attribute, operator, value = inside:match("^%s*([%w_%-]+)%s*([%^%$%*]?=)%s*(.-)%s*$")
            attribute = attribute or inside:match("^%s*([%w_%-]+)%s*$")
            if not attribute then return nil end
            test(attributeTest(attribute, ATTRIBUTE[operator], value and value:gsub("^([\"'])(.*)%1$", "%2")), nil, 100)
            rest = rest:sub(#inside + 3)
        elseif not kind then
            return nil
        elseif kind == "#" then
            compound.id, compound.specificity = name, compound.specificity + 10000
            rest = rest:sub(#name + 2)
        elseif kind == "." then
            compound.classes[#compound.classes + 1] = name
            compound.specificity = compound.specificity + 100
            rest = rest:sub(#name + 2)
        else
            rest = rest:sub(#name + 2)
            local argument
            if rest:sub(1, 1) == "(" then
                local close = closingBracket(rest)
                if not close then return nil end
                argument, rest = trim(rest:sub(2, close - 1)), rest:sub(close + 1)
            end
            name = name:lower()
            local def = PSEUDO[name]
            if name == "not" then                  -- weighs what its argument weighs, as in CSS
                local inner = argument and parseCompound(argument)
                if not inner or inner.pseudo then return nil end
                test(function(element) return not matchesCompound(element, inner) end, nil, inner.specificity)
                compound.structural = compound.structural or inner.structural
            elseif def then
                if (def.parse ~= nil) ~= (argument ~= nil) then return nil end
                local parsed = argument and def.parse(argument)
                if argument and parsed == nil then return nil end
                test(def.test, parsed, 100)
                compound.structural = compound.structural or def.structural
            elseif argument then
                return nil
            else
                compound.pseudo = name             -- a state; Resolve says so when there is none such
            end
        end
    end
    return compound
end

-- "a > b.c:hover" -> { parts = { compound, ... }, combinators = { ">" | " " }, pseudo, specificity,
-- structural }, or nil.
local function parseSelector(text)
    local selector = { parts = {}, combinators = {}, specificity = 0 }
    local parts = selector.parts
    local current, depth, combinator = "", 0, nil
    local function finish()
        if current == "" then return true end
        local compound = parseCompound(current)
        if not compound then return false end
        if #parts > 0 then selector.combinators[#parts] = combinator or " " end
        parts[#parts + 1] = compound
        selector.specificity = selector.specificity + compound.specificity
        selector.structural = selector.structural or compound.structural
        current, combinator = "", nil
        return true
    end
    text = trim(text)
    for i = 1, #text do
        local char = text:sub(i, i)
        if char == "(" or char == "[" then depth = depth + 1 elseif char == ")" or char == "]" then depth = depth - 1 end
        if depth == 0 and (char == ">" or char:match("%s")) then
            if not finish() then return nil end
            if char == ">" then
                if combinator or #parts == 0 then return nil end
                combinator = ">"
            end
        else
            current = current .. char
        end
    end
    if not finish() or #parts == 0 or combinator then return nil end
    for i = 1, #parts - 1 do
        if parts[i].pseudo then return nil end   -- a state only makes sense on the styled element
    end
    selector.pseudo = parts[#parts].pseudo
    return selector
end

local function matchesFrom(element, selector, index)
    if not matchesCompound(element, selector.parts[index]) then return false end
    if index == 1 then return true end
    local parent = parentOf(element)
    if selector.combinators[index - 1] == ">" then
        return parent ~= nil and matchesFrom(parent, selector, index - 1)
    end
    while parent do
        if matchesFrom(parent, selector, index - 1) then return true end
        parent = parentOf(parent)
    end
    return false
end

function UI.Matches(element, selector)
    return matchesFrom(element, selector, #selector.parts)
end

--- Parses a selector list ("#a, .b c") for doc:Query and friends.
function UI.ParseSelectors(text)
    local out = {}
    for _, one in ipairs(splitArgs(text)) do
        local selector = parseSelector(one)
        if not selector then return nil end
        out[#out + 1] = selector
    end
    return out
end

-- --- Parsing ------------------------------------------------------------------------------

local function parseDeclarations(body, into, problems, where)
    for chunk in (body .. ";"):gmatch("(.-);") do
        if chunk:find("%S") then
            local name, value = chunk:match("^%s*([%w_%-]+)%s*:%s*(.-)%s*$")
            local apply = chunk:match("^%s*@apply%s+(.-)%s*$")
            if apply then
                into[#into + 1] = { name = "@apply", value = apply }
            elseif name and value ~= "" then
                into[#into + 1] = { name = name, value = value }
            else
                problems[#problems + 1] = where .. ": cannot read \"" .. trim(chunk) .. "\""
            end
        end
    end
    return into
end

--- Parses an element's style="" attribute into declarations.
function UI.ParseInline(text)
    return parseDeclarations(text or "", {}, {}, "style")
end

UI.FontFiles = UI.FontFiles or {}
--- Names a font file (a .ttf in the game's data, so in a mod's pak): then `font-family: name`.
function UI.RegisterFont(name, path)
    UI.FontFiles[name] = path
end

-- "(min-width: 600) and (max-height: 400)" -> { kind, ["min-width"] = 600, ... } or nil.
local function parseCondition(kind, text)
    local condition, any = { kind = kind }, false
    for name, value in text:gmatch("%(%s*([%a%-]+)%s*:%s*(-?[%d.]+)%a*%s*%)") do
        name = name:lower()
        if name ~= "min-width" and name ~= "max-width" and name ~= "min-height" and name ~= "max-height" then return nil end
        condition[name] = tonumber(value)
        any = true
    end
    return any and condition or nil
end

--- Whether a size passes a condition of @media / @container.
function UI.Holds(condition, width, height)
    return width >= (condition["min-width"] or -math.huge) and width <= (condition["max-width"] or math.huge)
        and height >= (condition["min-height"] or -math.huge) and height <= (condition["max-height"] or math.huge)
end

local function newlines(text)
    return select(2, text:gsub("\n", ""))
end

-- Where the block that opens at `open` closes, or nil.
local function closingBrace(text, open)
    local depth, pos = 0, open
    while true do
        local at = text:find("[{}]", pos)
        if not at then return nil end
        depth = depth + (text:sub(at, at) == "{" and 1 or -1)
        if depth == 0 then return at end
        pos = at + 1
    end
end

-- The selectors of a rule written inside another: "&" stands for the parent's selector,
-- without it the rule is for what is inside the parent.
local function nested(parents, head)
    local own = splitArgs(head)
    if not parents then return own end
    local out = {}
    for _, parent in ipairs(parents) do
        local literal = parent:gsub("%%", "%%%%")
        for _, one in ipairs(own) do
            out[#out + 1] = one:find("&", 1, true) and one:gsub("&", literal) or (parent .. " " .. one)
        end
    end
    return out
end

local function addRule(sheet, conditions)
    local rule = { selectors = {}, declarations = {}, order = #sheet.rules + 1, conditions = conditions }
    sheet.rules[#sheet.rules + 1] = rule
    return rule
end

local parseBlock

-- One "head { body }" that starts at `line`. `parents`: the selectors (as text) of the rule it
-- is written in, if any.
local function parseRule(sheet, head, body, line, bodyLine, conditions, parents)
    local where = "line " .. line
    local theme = head:match("^@theme%s+([%w_%-]+)$")
    local query = head:match("^@(%a+)")
    if query == "media" or query == "container" then
        local condition = parseCondition(query, head)
        if not condition then
            sheet.problems[#sheet.problems + 1] = where .. ": cannot read \"" .. head
                .. "\" (min-width, max-width, min-height, max-height in brackets)"
            return
        end
        local inner = {}
        for _, outer in ipairs(conditions or {}) do inner[#inner + 1] = outer end
        inner[#inner + 1] = condition
        sheet.conditions[#sheet.conditions + 1] = condition
        parseBlock(sheet, body, bodyLine, inner, parents)
    elseif head == "@font-face" then         -- @font-face { font-family: Name; src: url(Public/Mod/x.ttf); }
        local family, src
        for _, declaration in ipairs(parseDeclarations(body, {}, sheet.problems, where)) do
            if declaration.name == "font-family" then family = declaration.value:gsub("[\"']", "") end
            if declaration.name == "src" then src = declaration.value:match("url%(%s*[\"']?(.-)[\"']?%s*%)") end
        end
        if family and src then
            UI.RegisterFont(family, src)
        else
            sheet.problems[#sheet.problems + 1] = where .. ": @font-face needs font-family and src: url(...)"
        end
    elseif (theme or head == ":root") and not parents then
        local vars = sheet.root
        if theme then
            if not sheet.themes[theme] then
                sheet.themes[theme] = {}
                sheet.themeOrder[#sheet.themeOrder + 1] = theme
            end
            vars = sheet.themes[theme]
        end
        for _, declaration in ipairs(parseDeclarations(body, {}, sheet.problems, where)) do
            vars[declaration.name] = declaration.value
        end
    else
        local texts, selectors = {}, {}
        for _, one in ipairs(nested(parents, head)) do
            local selector = parseSelector(one)
            if selector then
                texts[#texts + 1], selectors[#selectors + 1] = one, selector
                sheet.structural = sheet.structural or selector.structural
            else
                sheet.problems[#sheet.problems + 1] = where .. ": cannot read the selector \"" .. one .. "\""
            end
        end
        if #selectors == 0 then return end
        local rule = addRule(sheet, conditions)
        rule.selectors = selectors
        parseBlock(sheet, body, bodyLine, conditions, texts, rule)
    end
end

-- What stands at one level of nesting, from `line` on: blocks, and between them the
-- declarations of the rule this is the body of (`rule`; made when first needed for what
-- stands in an @media inside a rule).
parseBlock = function(sheet, text, line, conditions, parents, rule)
    local pos = 1
    while true do
        local open = text:find("{", pos, true)
        local chunk = text:sub(pos, (open or #text + 1) - 1)
        -- a head starts after the last declaration before its "{"
        local cut = open and (chunk:match("^.*;()") or 1) or #chunk + 1
        local loose, head = chunk:sub(1, cut - 1), chunk:sub(cut)
        if loose:find("%S") then
            local where = "line " .. (line + newlines(loose:match("^%s*")))
            if parents then
                if not rule then
                    rule = addRule(sheet, conditions)
                    for i, one in ipairs(parents) do rule.selectors[i] = parseSelector(one) end
                end
                parseDeclarations(loose, rule.declarations, sheet.problems, where)
            else
                sheet.problems[#sheet.problems + 1] = where .. ": \"" .. trim(loose):sub(1, 40) .. "\" is not inside a rule"
            end
        end
        if not open then break end
        local headLine = line + newlines(loose) + newlines(head:match("^%s*"))
        local close = closingBrace(text, open)
        if not close then
            sheet.problems[#sheet.problems + 1] = "line " .. headLine .. ": \"{\" is never closed"
            break
        end
        local body = text:sub(open + 1, close - 1)
        local bodyLine = line + newlines(chunk)
        parseRule(sheet, trim(head), body, headLine, bodyLine, conditions, parents)
        line = bodyLine + newlines(body)
        pos = close + 1
    end
end

--- Parses a style sheet. Never fails: what cannot be read is listed in `problems`.
--- Returns { rules = { { selectors, declarations, order, conditions } }, themes = { name = { ["--x"] = value } },
--- themeOrder = { names }, root = { ["--x"] = value }, conditions = { every @media / @container },
--- structural = true when a selector looks at siblings or children (:first-child ...),
--- problems = { "line N: ..." } }. A rule written inside another is a rule of its own here.
function UI.ParseCss(source, sheet)
    sheet = sheet or { rules = {}, themes = {}, themeOrder = {}, root = {}, problems = {}, conditions = {} }
    local text = source:gsub("\r", ""):gsub("/%*.-%*/", function(comment)
        return (comment:gsub("[^\n]", ""))     -- keep the line count
    end)
    parseBlock(sheet, text, 1, nil, nil)
    sheet.index = nil
    return sheet
end

-- --- The cascade --------------------------------------------------------------------------

-- Every selector of a sheet, filed under what its last compound asks for first - an id, a
-- class, a tag - so that an element only tries the selectors that can match it.
local function indexOf(sheet)
    local index = sheet.index
    if index then return index end
    index = { id = {}, class = {}, tag = {}, rest = {} }
    local function file(group, key, entry)
        local list = group[key]
        if not list then
            list = {}
            group[key] = list
        end
        list[#list + 1] = entry
    end
    for _, rule in ipairs(sheet.rules) do
        for _, selector in ipairs(rule.selectors) do
            local last = selector.parts[#selector.parts]
            local entry = { rule = rule, selector = selector }
            if last.id then file(index.id, last.id, entry)
            elseif last.classes[1] then file(index.class, last.classes[1], entry)
            elseif last.tag then file(index.tag, last.tag, entry)
            else index.rest[#index.rest + 1] = entry end
        end
    end
    sheet.index = index
    return index
end

--- var(--name) and var(--name, fallback) replaced from `vars` (a function: name -> value | nil).
function UI.Substitute(value, vars, depth)
    depth = depth or 0
    if depth > 8 or not value:find("var(", 1, true) then return value end
    local out = value:gsub("var%(%s*(%-%-[%w_%-]+)%s*,?%s*([^()]*)%)", function(name, fallback)
        local found = vars(name)
        if found == nil then found = fallback ~= "" and fallback or "" end
        return found
    end)
    return UI.Substitute(out, vars, depth + 1)
end

-- Do all the @media / @container conditions of a rule hold for this element now?
local function passes(element, conditions)
    for _, condition in ipairs(conditions) do
        local size
        if condition.kind == "container" then
            local box = element.parent
            while box and not (box.isContainer and box.boxSize) do box = box.parent end
            size = box and box.boxSize
        end
        size = size or (element.doc and element.doc.mediaSize)
        if not size or not UI.Holds(condition, size[1], size[2]) then return false end
    end
    return true
end

--- The declarations that apply to an element, weakest first:
--- { { name, value, pseudo } }. `sheets` are applied in order; `inline` last.
--- A rule inside @media / @container applies while the window (element.doc.mediaSize) or the
--- nearest ancestor marked as a container (its boxSize) passes; `anySize` takes them all.
function UI.Cascade(element, sheets, inline, anySize)
    local found = {}
    local function try(entries, sheetIndex)
        for _, entry in ipairs(entries) do
            local rule, selector = entry.rule, entry.selector
            if matchesFrom(element, selector, #selector.parts)
                and (not rule.conditions or anySize or passes(element, rule.conditions)) then
                found[#found + 1] = { rule = rule, pseudo = selector.pseudo, specificity = selector.specificity,
                    order = sheetIndex * 100000 + rule.order }
            end
        end
    end
    for sheetIndex, sheet in ipairs(sheets) do
        if not sheet.scope or sheet.scope == element.component then      -- <style scoped>
            local index = indexOf(sheet)
            try(index.rest, sheetIndex)
            try(index.tag[element.tag] or {}, sheetIndex)
            if element.id and index.id[element.id] then try(index.id[element.id], sheetIndex) end
            for class in pairs(element.classes) do
                if index.class[class] then try(index.class[class], sheetIndex) end
            end
        end
    end
    -- Utility classes (Utilities.lua) stand above the theme's `tag.class` rules and below
    -- anything written for two classes or an id.
    if UI.Utilities and next(element.classes) then
        for i, utility in ipairs(UI.Utilities(element)) do
            if (not utility.conditions or anySize or passes(element, utility.conditions))
                and (not utility.selector or UI.Matches(element, utility.selector)) then
                found[#found + 1] = { rule = { declarations = { utility } }, pseudo = utility.pseudo, specificity = 150,
                    order = 150000 + i }
            end
        end
    end
    table.sort(found, function(a, b)
        if a.specificity ~= b.specificity then return a.specificity < b.specificity end
        return a.order < b.order
    end)
    local out = {}
    for _, match in ipairs(found) do
        for _, declaration in ipairs(match.rule.declarations) do
            if declaration.name == "@apply" then
                for _, applied in ipairs(UI.ApplyUtilities and UI.ApplyUtilities(declaration.value, element) or {}) do
                    out[#out + 1] = { name = applied.name, value = applied.value, pseudo = match.pseudo }
                end
            else
                out[#out + 1] = { name = declaration.name, value = declaration.value, pseudo = match.pseudo }
            end
        end
    end
    for _, declaration in ipairs(inline or {}) do
        out[#out + 1] = { name = declaration.name, value = declaration.value }
    end
    return out
end

-- --- Properties ---------------------------------------------------------------------------
-- What a property means depends on the element: IMGUI has no one "background", it has
-- WindowBg, ChildBg, FrameBg, Button, Header, Tab... The tables below pick the slot.

local KIND = {            -- tag -> the family of IMGUI slots it draws with
    window = "window", child = "child", popup = "popup", tooltip = "popup", menu = "popup",
    menubar = "menubar", button = "button",
    input = "frame", combo = "frame", checkbox = "frame", radio = "frame", slider = "frame", drag = "frame",
    number = "frame", color = "frame", progress = "frame",
    header = "header", selectable = "header", tree = "header", item = "header",
    tab = "tab", tabs = "tab", table = "table", tr = "table", td = "table",
}
UI.Kinds = KIND

local BACKGROUND = {
    window = { "WindowBg" }, child = { "ChildBg" }, popup = { "PopupBg" }, menubar = { "MenuBarBg" },
    button = { "Button", hover = "ButtonHovered", active = "ButtonActive" },
    frame = { "FrameBg", hover = "FrameBgHovered", active = "FrameBgActive" },
    header = { "Header", hover = "HeaderHovered", active = "HeaderActive" },
    tab = { "Tab", hover = "TabHovered", selected = "TabActive", active = "TabActive" },
    table = { "TableRowBg", hover = "TableRowBgAlt" },
}

-- Colour properties that mean the same slot on every element. { base, hover =, active = }
local COLORS = {
    ["color"] = { "Text" },
    ["muted-color"] = { "TextDisabled" },
    ["border-color"] = { "Border" },
    ["accent"] = { { "CheckMark", "SliderGrab" }, active = "SliderGrabActive" },
    ["selection"] = { "TextSelectedBg" },
    ["separator-color"] = { "Separator", hover = "SeparatorHovered", active = "SeparatorActive" },
    ["scrollbar-color"] = { "ScrollbarGrab", hover = "ScrollbarGrabHovered", active = "ScrollbarGrabActive" },
    ["scrollbar-background"] = { "ScrollbarBg" },
    ["title-background"] = { { "TitleBg", "TitleBgCollapsed" }, active = "TitleBgActive" },
    ["link-color"] = { "TextLink" },
    ["grip-color"] = { "ResizeGrip", hover = "ResizeGripHovered", active = "ResizeGripActive" },
    ["button-color"] = { "Button", hover = "ButtonHovered", active = "ButtonActive" },
    ["frame-color"] = { "FrameBg", hover = "FrameBgHovered", active = "FrameBgActive" },
    ["header-color"] = { "Header", hover = "HeaderHovered", active = "HeaderActive" },
    ["tab-color"] = { "Tab", hover = "TabHovered", active = "TabActive", selected = "TabActive" },
    ["popup-background"] = { "PopupBg" },
    ["menubar-background"] = { "MenuBarBg" },
    ["table-header-background"] = { "TableHeaderBg" },
    ["table-border-color"] = { { "TableBorderStrong", "TableBorderLight" } },
}

-- `padding` is IMGUI's own on the elements that have one; on the others it is FramePadding -
-- unless the element is a flex / grid container, where it is the box's padding (Dom.lua decides).
local PADDING = { window = "WindowPadding", child = "WindowPadding", popup = "WindowPadding", table = "CellPadding" }

-- Style variables that depend on the element. kind -> IMGUI name; `default` for the rest.
local VARS_BY_KIND = {
    ["rounding"] = { window = "WindowRounding", child = "ChildRounding", popup = "PopupRounding",
        tab = "TabRounding", default = "FrameRounding" },
    ["border"] = { window = "WindowBorderSize", child = "ChildBorderSize", popup = "PopupBorderSize",
        tab = "TabBorderSize", default = "FrameBorderSize" },
}
VARS_BY_KIND["border-radius"] = VARS_BY_KIND["rounding"]
VARS_BY_KIND["border-width"] = VARS_BY_KIND["border"]

-- Style variables that are the same on every element (they reach everything inside it), two
-- numbers in IMGUI's order: across, then down.
local VARS = {
    ["inner-gap"] = "ItemInnerSpacing", ["indent"] = "IndentSpacing",
    ["opacity"] = "Alpha", ["scrollbar-size"] = "ScrollbarSize", ["scrollbar-rounding"] = "ScrollbarRounding",
    ["grab-size"] = "GrabMinSize", ["grab-rounding"] = "GrabRounding", ["text-align"] = "ButtonTextAlign",
    ["title-align"] = "WindowTitleAlign", ["window-padding"] = "WindowPadding", ["frame-padding"] = "FramePadding",
    ["frame-rounding"] = "FrameRounding", ["frame-border"] = "FrameBorderSize", ["cell-padding"] = "CellPadding",
    ["tab-rounding"] = "TabRounding", ["popup-rounding"] = "PopupRounding", ["child-rounding"] = "ChildRounding",
    ["window-min-size"] = "WindowMinSize", ["separator-text-align"] = "SeparatorTextAlign",
}

local ALIGN = { left = 0, center = 0.5, right = 1, top = 0, middle = 0.5, bottom = 1 }

-- Lengths without % and words of alignment: "8 4", "calc(1vw + 2) 0", "center" -> a list,
-- or nil, "why not".
local function numbers(value)
    local out = {}
    for _, word in ipairs(words(value)) do
        local n, problem = ALIGN[word:lower()], nil
        if not n then n, problem = length(word) end
        if not n then return nil, problem end
        out[#out + 1] = n
    end
    if #out == 0 then return nil, "not a number: " .. value end
    return out
end

-- Text sizes are Script Extender's font units (an element's FontSize). Its six built-in fonts
-- are one font file at the sizes below, so a named size is just a number, and a step is the
-- distance between two of them. In pixels a unit is 2/3 of a pixel in a 1080-line picture and
-- grows with the picture, as the built-in fonts do (docs/facts.md).
UI.Fonts = { "Tiny", "Small", "Medium", "Default", "Large", "Big" }
UI.FontSizes = { Tiny = 24, Small = 28, Medium = 32, Default = 36, Large = 40, Big = 44 }
local FONT_STEP, SMALLEST_FONT = 4, 8
local FONT_BY_NAME = {}
for _, name in ipairs(UI.Fonts) do FONT_BY_NAME[name:lower()] = UI.FontSizes[name] end

--- The base text size in font units: `base` is the overlay's text size, one of UI.Fonts or ""
--- (Script Extender's own, Medium).
function UI.BaseFontSize(base)
    return FONT_BY_NAME[(base or ""):lower()] or UI.FontSizes.Medium
end

--- A text size as written -> font units, { em = n } (a multiple of the parent's size, worked out
--- by the document) or nil. "Large", "+1" / "-2" (steps of 4 from the base), "22", "22px",
--- "1.25rem" (of the base), "1.25em"; `base` as for BaseFontSize.
function UI.FontSizeFor(value, base)
    value = trim(tostring(value)):lower()
    local baseSize = UI.BaseFontSize(base)
    if value == "inherit" or value == "" then return baseSize end
    if FONT_BY_NAME[value] then return FONT_BY_NAME[value] end
    local step = value:match("^[+-]%d+$")
    if step then return math.max(SMALLEST_FONT, baseSize + FONT_STEP * tonumber(step)) end
    local number, unit = value:match("^([%d.]+)(%a*)$")
    number = tonumber(number)
    if not number or number <= 0 then return nil end
    if unit == "" or unit == "px" then return number end
    if unit == "rem" then return number * baseSize end
    if unit == "em" then return { em = number } end
    return nil
end

--- "200 1fr 2fr", "repeat(3, 1fr)", "repeat(auto-fill, minmax(180, 1fr))" ->
--- { { px = 200 }, { fr = 1 }, ... } or { auto = true, min = 180 }; or nil, why.
function UI.ParseTracks(value)
    value = trim(value):lower()
    local count, inner = value:match("^repeat%(%s*([%w%-]+)%s*,%s*(.-)%s*%)$")
    local function track(word)
        local fr = word:match("^([%d.]*)fr$")
        if fr then return { fr = tonumber(fr) or 1 } end
        if word == "auto" then return { fr = 1 } end
        local px = tonumber((word:gsub("px$", "")))
        if px then return { px = px } end
        local low = word:match("^minmax%(%s*([%d.]+)%a*%s*,.-%)$")
        if low then return { fr = 1, min = tonumber(low) } end
        return nil
    end
    if count == "auto-fill" or count == "auto-fit" then
        local one = track(inner)
        if not one then return nil, "cannot read the column: " .. inner end
        return { auto = true, min = one.min or one.px or 120 }
    end
    local out = {}
    if count then
        local one, n = track(inner), tonumber(count)
        if not one or not n then return nil, "cannot read: " .. value end
        for i = 1, math.floor(n) do out[i] = one end
        return out
    end
    for _, word in ipairs(words(value)) do
        local one = track(word)
        if not one then return nil, "cannot read the column: " .. word end
        out[#out + 1] = one
    end
    if #out == 0 then return nil, "no columns" end
    return out
end

-- Layout, as on the web. The kit places the elements itself (Layout.lua) from what these
-- properties leave on the element: each gives { kind = "prop", key = "layout:<name>", value }.
-- LAYOUT[name] = fn(value) -> ops | nil, "why not".
local LAYOUT = {}
local SIDES = { "top", "right", "bottom", "left" }

local function layoutOp(name, value)
    return { kind = "prop", key = "layout:" .. name, value = value }
end

for _, name in ipairs({ "flex-direction", "flex-wrap", "justify-content", "align-items", "align-content",
    "justify-items", "align-self", "justify-self", "position" }) do
    LAYOUT[name] = function(value)           -- a word; "flex-start" is "start"
        return { layoutOp(name, (value:lower():gsub("^flex%-", ""))) }
    end
end

for _, name in ipairs({ "flex-grow", "flex-shrink", "order" }) do
    LAYOUT[name] = function(value)
        local n = tonumber(value)
        if not n then return nil, name .. " takes a number" end
        return { layoutOp(name, n) }
    end
end

-- One length; `auto` is "not set", or stays a word where it means something (a margin).
local function lengthProperty(name, percent, autoValue)
    LAYOUT[name] = function(value)
        if value:lower() == "auto" then return { layoutOp(name, autoValue) } end
        local one, problem = length(value, percent)
        if one == nil then return nil, problem end
        return { layoutOp(name, one) }
    end
end

-- Four sides at once, in the order of CSS.
local function boxProperty(name, prefix, percent, autoValue)
    LAYOUT[name] = function(value)
        local box, problem = sides(value, percent, true)
        if not box then return nil, name .. ": " .. problem end
        local out = {}
        for i, side in ipairs(SIDES) do
            local one = box[i]
            if one == "auto" then one = autoValue end
            out[i] = layoutOp(prefix .. side, one)
        end
        return out
    end
end

lengthProperty("flex-basis", true, false)
lengthProperty("row-gap", false, false)
lengthProperty("column-gap", false, false)
boxProperty("margin", "margin-", true, "auto")
boxProperty("inset", "", true, false)
for _, side in ipairs(SIDES) do
    lengthProperty(side, true, false)                       -- top: 10, left: 50%
    lengthProperty("margin-" .. side, true, "auto")
    lengthProperty("padding-" .. side, false, false)
end

function LAYOUT.flex(value)                     -- flex: grow [shrink] [basis] | none | auto
    local list = words(value:lower())
    local grow, shrink, basis = tonumber(list[1]), tonumber(list[2] or 1), list[3] or "0"
    if list[1] == "none" then grow, shrink, basis = 0, 0, "auto" end
    if list[1] == "auto" then grow, shrink, basis = 1, 1, "auto" end
    if list[2] and not shrink then shrink, basis = 1, list[2] end      -- flex: 1 200
    if not grow then return nil, "flex takes: grow [shrink] [basis]" end
    local base, problem = false, nil
    if basis ~= "auto" then base, problem = length(basis, true) end
    if base == nil then return nil, problem end
    return { layoutOp("flex-grow", grow), layoutOp("flex-shrink", shrink), layoutOp("flex-basis", base) }
end

LAYOUT["aspect-ratio"] = function(value)        -- 16 / 9, 1.5, auto
    if value:lower() == "auto" then return { layoutOp("aspect-ratio", false) } end
    local width, height = value:match("^%s*([%d.]+)%s*/%s*([%d.]+)%s*$")
    local ratio = width and tonumber(width) / tonumber(height) or tonumber(value)
    if not ratio or ratio <= 0 or ratio == math.huge then return nil, "aspect-ratio takes: width / height" end
    return { layoutOp("aspect-ratio", ratio) }
end

LAYOUT["grid-column"] = function(value)
    local n = tonumber(value:lower():match("^span%s+(%d+)$") or value)
    if not n then return nil, "grid-column takes: span N" end
    return { layoutOp("span", math.max(1, math.floor(n))) }
end

LAYOUT["grid-template-columns"] = function(value)
    local tracks, problem = UI.ParseTracks(value)
    if not tracks then return nil, problem end
    return { { kind = "prop", key = "layout:columns", value = value, tracks = tracks } }
end

LAYOUT["container-type"] = function(value)
    local word = value:lower()
    return { layoutOp("container", word ~= "normal" and word ~= "none") }
end

local ON = { ["true"] = true, yes = true, on = true, ["1"] = true }
local PROPS = {
    ["wrap"] = true, ["display"] = true, ["inline"] = true, ["offset"] = true,
    ["background-opacity"] = true,
}
-- Sizes: which side of the picture a window's percentage is of.
local SIZES = { ["width"] = 1, ["min-width"] = 1, ["max-width"] = 1, ["height"] = 2, ["min-height"] = 2, ["max-height"] = 2 }

local CUSTOM = {}
--- Adds a property to the language. def = { parse = fn(value, tag) -> parsed | nil, "why not";
--- apply = fn(el, parsed); remove = fn(el) (optional: without it the window is built anew
--- when the property stops applying) }. Built-in names cannot be replaced.
function UI.RegisterProperty(name, def)
    CUSTOM[name] = def
end

-- What a declaration came to, by tag, property, state and value: a window has a few hundred
-- declarations and most are met again at every restyle. Started anew when it has grown large
-- (values made up at run time, such as a bound style, would fill it).
local resolved, resolvedCount, RESOLVED_MOST = {}, 0, 4000

local function resolve(tag, name, value, pseudo, baseFont)
    local kind = KIND[tag] or "other"
    local state = pseudo == "checked" and "selected" or pseudo
    if state and state ~= "hover" and state ~= "active" and state ~= "selected" then
        return nil, "unknown state :" .. tostring(pseudo)
    end

    local isBackground = name == "background" or name == "background-color" or name == "background-image"
    if isBackground and value:find("url(", 1, true) then
        -- a file of the mod ("Public/MyMod/GUI/logo.png"), a texture's GUID or an atlas icon's name
        if state then return nil, "a picture cannot depend on a state (:" .. state .. ")" end
        local src = value:match("url%(%s*[\"']?(.-)[\"']?%s*%)")
        if not src or src == "" then return nil, "not a picture: " .. value end
        return { { kind = "deco", key = "image", value = src } }
    end
    if isBackground and value:find("gradient(", 1, true) then
        if state then return nil, "a gradient cannot depend on a state (:" .. state .. ")" end
        local gradient = UI.ParseGradient(value)
        if not gradient then return nil, "not a gradient: " .. value .. " (linear-gradient(angle, from, to))" end
        -- the element's own flat fill would cover the gradient: it becomes transparent
        local out = { { kind = "deco", key = "gradient", value = gradient }, { kind = "deco", key = "background", value = false } }
        local slots = BACKGROUND[kind]
        local keys = slots and slots[1]
        for _, key in ipairs(type(keys) == "table" and keys or { keys }) do
            out[#out + 1] = { kind = "color", key = key, value = TRANSPARENT }
        end
        return out
    end
    if name == "background-image" then
        if value:lower() == "none" then
            return { { kind = "deco", key = "gradient", value = false }, { kind = "deco", key = "image", value = false } }
        end
        return nil, "not a gradient: " .. value
    end

    local slots = isBackground and BACKGROUND[kind] or COLORS[name]
    if isBackground and not slots then
        -- IMGUI gives text, groups and rows no fill of their own: it is drawn behind them.
        if state then return nil, "<" .. tag .. "> has no :" .. state .. " background" end
        local color = UI.ParseColor(value)
        if not color then return nil, "not a colour: " .. value end
        return { { kind = "deco", key = "background", value = color }, { kind = "deco", key = "gradient", value = false } }
    end
    if slots then
        local color = UI.ParseColor(value)
        if not color then return nil, "not a colour: " .. value end
        local keys = state and slots[state] or (not state and slots[1])
        if not keys then return nil, name .. " has no :" .. tostring(state) .. " state on <" .. tag .. ">" end
        local out = {}
        for _, key in ipairs(type(keys) == "table" and keys or { keys }) do
            out[#out + 1] = { kind = "color", key = key, value = color }
        end
        if isBackground and not state then out[#out + 1] = { kind = "deco", key = "gradient", value = false } end
        return out
    end
    if state then return nil, "a state (:" .. state .. ") only changes colours, not " .. name end

    if name == "box-shadow" then            -- box-shadow: 4 4 12 #000  (dx dy size colour), or none
        if value:lower() == "none" then return { { kind = "deco", key = "shadow", value = false } } end
        local list, color = numbersAndColor(value)
        if not list or not color then return nil, "box-shadow takes: dx dy size colour" end
        return { { kind = "deco", key = "shadow", value = { color, list[3] or 8, list[1] or 0, list[2] or 0 } } }
    end
    if name == "outline" or ((name == "border") and not tonumber((value:gsub("px$", "")))) then
        -- a frame drawn around the element itself; a plain `border: 1` stays IMGUI's own border
        if value:lower() == "none" then return { { kind = "deco", key = "border", value = false } } end
        local list, color = numbersAndColor(value)
        if not list or not color then return nil, name .. " takes: size colour" end
        return { { kind = "deco", key = "border", value = { color, list[1] or 1 } } }
    end
    if name == "background-mode" or name == "background-repeat" then   -- how a picture fills the element
        local mode = ({ stretch = "Stretch", tile = "Tile", slice = "Slice", ["repeat"] = "Tile", ["no-repeat"] = "Stretch" })[value:lower()]
        if not mode then return nil, name .. " takes stretch, tile or slice" end
        return { { kind = "deco", key = "imageMode", value = mode } }
    end
    if name == "background-slice" then      -- nine slices: the border that keeps its size
        local n = tonumber((value:gsub("px$", "")))
        if not n then return nil, "not a number: " .. value end
        return { { kind = "deco", key = "imageSlice", value = n }, { kind = "deco", key = "imageMode", value = "Slice" } }
    end
    if name == "background-tint" then
        local tint = UI.ParseColor(value)
        if not tint then return nil, "not a colour: " .. value end
        return { { kind = "deco", key = "imageTint", value = tint } }
    end
    if name == "transition" then            -- transition: 0.2 - colours and numbers glide for that long
        local seconds, unit = value:match("([%d.]+)(m?)s?%s*$")
        seconds = tonumber(seconds)
        if value:lower() == "none" then seconds, unit = 0, "" end
        if not seconds then return nil, "transition takes a time: 0.2 or 200ms" end
        return { { kind = "prop", key = "transition", value = unit == "m" and seconds / 1000 or seconds } }
    end
    if name == "font-family" then           -- a font of one's own: @font-face or Weave.RegisterFont
        return { { kind = "prop", key = "font-family", value = (value:gsub("[\"']", "")) } }
    end
    if name == "font-size" or name == "font" then     -- `font` is the same, as the shorthand of CSS
        local size = UI.FontSizeFor(value, baseFont)
        if not size then
            return nil, "no such text size: " .. value .. " (Tiny Small Medium Default Large Big, +1, -1, 22, 1.25em, 1.25rem)"
        end
        return { { kind = "prop", key = "font-size", value = size } }
    end
    if name == "background-padding" then    -- how far the drawn background reaches past the element
        local box, problem = sides(value)
        if not box then return nil, name .. ": " .. problem end
        return { { kind = "deco", key = "padding", value = { box[4], box[1] } } }
    end
    if LAYOUT[name] then return LAYOUT[name](value) end
    if name == "padding" then               -- padding: all | vertical horizontal | top right bottom left
        local box, problem = sides(value)
        if not box then return nil, name .. ": " .. problem end
        if PADDING[kind] then return { { kind = "var", key = PADDING[kind], value = { box[4], box[1] } } } end
        -- an element with no padding of its own: the frames inside it, or - on a flex / grid
        -- container - the box's padding; Dom.lua keeps the one that applies (`box`)
        local out = { { kind = "var", key = "FramePadding", value = { box[4], box[1] }, box = true } }
        for i, side in ipairs(SIDES) do out[#out + 1] = layoutOp("padding-" .. side, box[i]) end
        return out
    end
    if name == "gap" then                   -- gap: both | between rows, between columns
        local list, problem = numbers(value)
        if not list then return nil, problem end
        return { { kind = "var", key = "ItemSpacing", value = { list[2] or list[1], list[1] } } }
    end
    if SIZES[name] then                     -- width: 300 | 50% | calc(100% - 40) | auto
        local word = value:lower()
        local one, problem
        if word == "auto" or word == "none" then
            one = (name == "width" or name == "height") and 0
        else
            one, problem = length(value, true)
            if one == nil then return nil, problem end
        end
        if tag == "window" and type(one) == "table" then     -- a window is measured against the picture
            one = one[1] + one[2] * viewport(SIZES[name]) / 100
        end
        return { { kind = "prop", key = name, value = one } }
    end
    if name == "align" then                 -- the element's own place in the width it is given
        local at = ALIGN[value:lower()]
        if not at then return nil, "align takes left, center or right" end
        return { { kind = "prop", key = "align", value = at } }
    end

    local variable = VARS[name] or (VARS_BY_KIND[name] and (VARS_BY_KIND[name][kind] or VARS_BY_KIND[name].default))
    if variable then
        local list, problem = numbers(value)
        if not list then return nil, problem end
        local out = { { kind = "var", key = variable, value = { list[1], list[2] or list[1] } } }
        if VARS_BY_KIND["rounding"] == VARS_BY_KIND[name] then     -- what is drawn behind follows
            out[2] = { kind = "deco", key = "rounding", value = list[1] }
        end
        return out
    end

    if PROPS[name] then
        if name == "display" then
            local mode = value:lower()
            return { { kind = "prop", key = "visible", value = mode ~= "none" },
                { kind = "prop", key = "layout:display", value = (mode == "flex" or mode == "grid") and mode or false } }
        elseif name == "inline" then
            return { { kind = "prop", key = "inline", value = ON[value:lower()] or false } }
        elseif name == "wrap" then
            local n = ON[value:lower()] and 0 or tonumber(value)
            if not n then return nil, "wrap takes on or a width" end
            return { { kind = "prop", key = "wrap", value = n } }
        else
            local list, problem = numbers(value)
            if not list then return nil, problem end
            return { { kind = "prop", key = name, value = (name == "offset") and { list[1], list[2] or 0 } or list[1] } }
        end
    end

    local custom = CUSTOM[name]
    if custom then
        local parsed, problem = custom.parse(value, tag)
        if parsed == nil then return nil, problem or ("cannot read " .. name .. ": " .. value) end
        return { { kind = "custom", key = name, value = parsed, apply = custom.apply, remove = custom.remove } }
    end

    -- IMGUI's own names work everywhere: `TitleBgActive: #402a10;`, `ScrollbarSize: 14;`
    if name:match("^%u") then
        local color = UI.ParseColor(value)
        if color then return { { kind = "color", key = name, value = color } } end
        local list = numbers(value)
        if list then return { { kind = "var", key = name, value = { list[1], list[2] or list[1] } } } end
        return nil, "neither a colour nor a number: " .. value
    end
    return nil, "unknown property " .. name
end

--- One declaration on one element -> what to do with it:
---   { { kind = "color", key = "Button", value = { r, g, b, a }, id = "color:Button" },
---     { kind = "var", key = "FramePadding", value = { x, y } },
---     { kind = "prop", key = "font-size" | "width" | "layout:margin-top" | ..., value = ... },
---     { kind = "deco", key = "background" | "gradient" | "border" | "shadow" | "rounding" | "padding",
---       value = ... | false } }       drawn behind the element
--- or nil, "why not". A length is a number of pixels, or { pixels, percent } where the layout
--- has something to take the percentage of. The third result is true when the value was
--- worked out from the size of the game's picture (vw, vh, a window's %): it is stale when
--- that changes. The ops are shared by every element the declaration applies to: read only.
function UI.Resolve(tag, name, value, pseudo, baseFont)
    local key = tag .. "|" .. name .. "|" .. (pseudo or "") .. "|" .. (baseFont or "") .. "|" .. value
    local known = resolved[key]
    if known then return known[1], known[2], false end
    viewportUsed = false
    local ops, problem = resolve(tag, name, value, pseudo, baseFont)
    for _, op in ipairs(ops or {}) do op.id = op.kind .. ":" .. op.key end
    -- not remembered: what depends on the picture's size, and properties added from outside
    -- (their parse is theirs, and may be registered anew)
    if not viewportUsed and not CUSTOM[name] then
        if resolvedCount >= RESOLVED_MOST then resolved, resolvedCount = {}, 0 end
        resolved[key], resolvedCount = { ops, problem }, resolvedCount + 1
    end
    return ops, problem, viewportUsed
end
