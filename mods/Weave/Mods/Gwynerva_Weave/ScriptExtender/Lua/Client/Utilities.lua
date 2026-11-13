-- Weave, utility classes: say what an element is like in class="" and write no style sheet.
--
--   <group class="flex items-center gap-2 p-4 rounded-lg bg-panel md:grid md:grid-cols-3">
--   <button class="px-4 py-2 bg-linear-to-r from-accent to-amber-700 hover:bg-accent/80 shadow-lg">
--
-- The engine works the way UnoCSS does: a class is not looked up in a big style sheet, it is
-- taken apart - variants in front (`md:`, `hover:`, `@sm:`), then a rule found by name or by
-- prefix that turns the rest into declarations of Weave's own style language. A class is
-- compiled once and remembered, so the cost of styling an element follows the number of
-- classes on it, not the size of the vocabulary; `p-7`, `w-[300]` and `bg-red-500/40` need no
-- entry anywhere.
--
-- The vocabulary is a preset. "wind4" below follows Tailwind 4's names. More rules, variants
-- or whole presets can be added:
--
--   Weave.RegisterUtility("wind4", "glow", "box-shadow: 0 0 14 alpha(var(--accent), 0.6)")
--   Weave.RegisterUtility("wind4", "tracking-", function(value, ctx) ... end)
--   Weave.RegisterVariant("wind4", "tall", { media = { ["min-height"] = 900 } })
--
-- Values come from the theme, as in Tailwind 4: --spacing (4), --breakpoint-sm ... -2xl,
-- --container-3xs ... -5xl, and colours: `bg-brand` is --color-brand, then --brand, then the
-- built-in palette (red-500, slate-800, ...). Classes the engine does not know are left alone:
-- they may be the author's own.

Weave = Weave or {}
local W = Weave
local I = W.Internal

local presets = {}
local DEFAULT_PRESET = "wind4"

local function preset(name)
    local p = presets[name]
    if not p then
        p = { exact = {}, prefixes = {}, variants = {} }
        presets[name] = p
    end
    return p
end

local generation = 0          -- grows when the vocabulary changes: what was compiled is stale

--- Adds a rule. `matcher`: a whole class name ("flex") or a prefix ending in "-" ("p-").
--- `rule`: declarations as text, or fn(value, ctx) returning declarations as text, a table of
--- parts (see PARTS below) or nil when the value is not for this rule.
--- ctx = { spacing, var(name), color(name), number(value), length(value), size(value) }. A
--- class written with a minus in front ("-mt-2") reaches the rule for "mt-" as the value "-2".
function W.RegisterUtility(presetName, matcher, rule)
    local p = preset(presetName)
    if matcher:sub(-1) == "-" then
        p.prefixes[#p.prefixes + 1] = { prefix = matcher, rule = rule }
        table.sort(p.prefixes, function(a, b) return #a.prefix > #b.prefix end)
    else
        p.exact[matcher] = rule
    end
    generation = generation + 1
end

--- Adds a variant (what stands before the colon): def = { media = { ["min-width"] = 640 } },
--- { container = { ... } }, { pseudo = "hover" } (a state: a colour slot) or
--- { selector = ":first-child" } (what the element has to match, written as in a style sheet);
--- a number may be the name of a theme variable.
function W.RegisterVariant(presetName, name, def)
    preset(presetName).variants[name] = def
    generation = generation + 1
end

-- --- Colours --------------------------------------------------------------------------------

local HUES = {       -- the 500 of each hue; the other shades are worked out from it
    slate = "#64748b", gray = "#6b7280", zinc = "#71717a", neutral = "#737373", stone = "#78716c",
    red = "#ef4444", orange = "#f97316", amber = "#f59e0b", yellow = "#eab308", lime = "#84cc16",
    green = "#22c55e", emerald = "#10b981", teal = "#14b8a6", cyan = "#06b6d4", sky = "#0ea5e9",
    blue = "#3b82f6", indigo = "#6366f1", violet = "#8b5cf6", purple = "#a855f7", fuchsia = "#d946ef",
    pink = "#ec4899", rose = "#f43f5e",
}
local SHADES = {     -- shade -> towards white (positive) or black (negative)
    ["50"] = 0.95, ["100"] = 0.88, ["200"] = 0.75, ["300"] = 0.55, ["400"] = 0.28, ["500"] = 0,
    ["600"] = -0.15, ["700"] = -0.32, ["800"] = -0.48, ["900"] = -0.62, ["950"] = -0.78,
}
local ALIASES = {    -- names that read well in a class, for the theme's own variables
    normal = "--text", base = "--bg", alt = "--bg-alt", strong = "--accent-strong", ok = "--log-result",
    info = "--log-cmd", warn = "--log-warning", error = "--log-error",
}
local PLAIN = { transparent = "transparent", black = "#000000", white = "#ffffff" }

local function arbitrary(value)
    local inside = value:match("^%[(.*)%]$")
    return inside and (inside:gsub("_", " ")) or nil
end

-- "accent", "red-500", "brand/60", "[#ff8800]" -> a colour as the style language writes it.
local function color(lookup, value)
    local name, opacity = value:match("^(.-)/(%d+)$")
    name = name or value
    local css = arbitrary(name) or PLAIN[name]
    if not css then
        if lookup("--color-" .. name) then css = "var(--color-" .. name .. ")"
        elseif ALIASES[name] and lookup(ALIASES[name]) then css = "var(" .. ALIASES[name] .. ")"
        elseif lookup("--" .. name) then css = "var(--" .. name .. ")"
        else
            local hue, shade = name:match("^(%a+)%-(%d+)$")
            local base, toward = HUES[hue or name], SHADES[shade or "500"]
            if not base or not toward then return nil end
            if toward > 0 then css = "mix(" .. base .. ", #ffffff, " .. toward .. ")"
            elseif toward < 0 then css = "mix(" .. base .. ", #000000, " .. -toward .. ")"
            else css = base end
        end
    end
    if opacity then css = "alpha(" .. css .. ", " .. tonumber(opacity) / 100 .. ")" end
    return css
end

-- --- Parts: utilities that only make sense together ----------------------------------------
-- `px-4 py-2` is one padding; `bg-linear-to-r from-a to-b` is one background. Such rules give
-- parts; the parts of one variant group are put together here.

local SHADOWS = {    -- name -> dx dy size
    ["2xs"] = "0 1 1", xs = "0 1 2", sm = "0 1 3", md = "0 3 6", lg = "0 6 14", xl = "0 12 24", ["2xl"] = "0 18 40",
}

local SIDES = { "top", "right", "bottom", "left" }

-- The four sides from the parts `p`, `px` / `py`, `pt pr pb pl` (for the prefix "p"): the
-- narrower class wins, whatever the order in class="". Sides nobody set are nil.
local function sidesOf(parts, prefix)
    local all, x, y = parts[prefix], parts[prefix .. "x"], parts[prefix .. "y"]
    return { parts[prefix .. "t"] or y or all, parts[prefix .. "r"] or x or all,
        parts[prefix .. "b"] or y or all, parts[prefix .. "l"] or x or all }
end

local function assemble(parts, lookup, out)
    local padding = sidesOf(parts, "p")
    if next(padding) then
        -- IMGUI's padding is two numbers, both needed: a side not given is the theme's
        local theme = lookup("--padding")
        local vertical, horizontal = (theme or ""):match("^%s*([%d.]+)%s*([%d.]*)")
        vertical = tonumber(vertical) or 4
        horizontal = tonumber(horizontal) or (theme and vertical) or 8
        out[#out + 1] = { name = "padding", value = (padding[1] or vertical) .. " " .. (padding[2] or horizontal) .. " "
            .. (padding[3] or vertical) .. " " .. (padding[4] or horizontal) }
    end
    local margin, inset = sidesOf(parts, "m"), sidesOf(parts, "i")
    for i, side in ipairs(SIDES) do
        if margin[i] then out[#out + 1] = { name = "margin-" .. side, value = tostring(margin[i]) } end
        if inset[i] then out[#out + 1] = { name = side, value = tostring(inset[i]) } end
    end
    if parts.dir or parts.from or parts.to then
        local from, to = parts.from, parts.to
        if not from then from = "alpha(" .. (to or "#000000") .. ", 0)" end
        if not to then to = "alpha(" .. from .. ", 0)" end
        out[#out + 1] = { name = "background", value = "linear-gradient(" .. (parts.dir or "to right") .. ", " .. from .. ", " .. to .. ")" }
    end
    if parts.shadow or parts.shadowColor then
        out[#out + 1] = { name = "box-shadow", value = (parts.shadow or SHADOWS.md) .. " " .. (parts.shadowColor or "alpha(#000000, 0.5)") }
    end
    if parts.outline or parts.outlineColor then
        out[#out + 1] = { name = "outline", value = (parts.outline or 1) .. " " .. (parts.outlineColor or "var(--border)") }
    end
end

-- --- Compiling one class ---------------------------------------------------------------------

local conditions = {}         -- "media:min-width:640" -> the one condition object for it
W.UtilitySheet = { conditions = {}, rules = {} }      -- for the size-query signature (Layout.lua)

local function condition(kind, name, value)
    local key = kind .. ":" .. name .. ":" .. value
    local found = conditions[key]
    if not found then
        found = { kind = kind, [name] = value }
        conditions[key] = found
        W.UtilitySheet.conditions[#W.UtilitySheet.conditions + 1] = found
    end
    return found
end

local BREAKPOINTS = { sm = 640, md = 768, lg = 1024, xl = 1280, ["2xl"] = 1536 }
local CONTAINERS = { ["3xs"] = 256, ["2xs"] = 288, xs = 320, sm = 384, md = 448, lg = 512, xl = 576, ["2xl"] = 672,
    ["3xl"] = 768, ["4xl"] = 896, ["5xl"] = 1024 }

-- A variant's name -> { conditions = {...} } | { pseudo = } and its weight in the cascade.
local function variant(p, name, lookup)
    local def = p.variants[name]
    if def then
        if def.pseudo then return { pseudo = def.pseudo }, 0 end
        if def.selector then return { selector = def.selector }, 0 end
        local kind = def.media and "media" or "container"
        local out, weight = {}, 0
        for key, value in pairs(def.media or def.container) do
            if type(value) == "string" then value = tonumber(lookup(value)) end
            if not value then return nil end
            out[#out + 1] = condition(kind, key, value)
            weight = math.max(weight, value)
        end
        return { conditions = out }, weight
    end
    local function size(word, sizes, prefix)
        return tonumber(arbitrary(word) or "") or tonumber(lookup(prefix .. word) or "") or sizes[word]
    end
    local container = name:sub(1, 1) == "@"
    local rest = container and name:sub(2) or name
    local kind, sizes, prefix = container and "container" or "media", container and CONTAINERS or BREAKPOINTS,
        container and "--container-" or "--breakpoint-"
    local upTo = rest:match("^max%-(.+)$")
    local from = rest:match("^min%-(.+)$") or rest
    if upTo then
        local n = size(upTo, sizes, prefix)
        if n then return { conditions = { condition(kind, "max-width", n - 1) } }, 100000 - n end
    else
        local n = size(from, sizes, prefix)
        if n then return { conditions = { condition(kind, "min-width", n) } }, n + (container and 20000 or 0) end
    end
    return nil
end

local function splitVariants(class)
    local parts, depth, start = {}, 0, 1
    for i = 1, #class do
        local char = class:sub(i, i)
        if char == "[" then depth = depth + 1
        elseif char == "]" then depth = depth - 1
        elseif char == ":" and depth == 0 then
            parts[#parts + 1] = class:sub(start, i - 1)
            start = i + 1
        end
    end
    parts[#parts + 1] = class:sub(start)
    return parts
end

-- class -> { declarations | parts, conditions, pseudo, selector, weight, group } or false.
local function compile(p, class, lookup, ctx)
    local pieces = splitVariants(class)
    local base = table.remove(pieces)
    -- "-mt-2": the rule for "mt-" gets "-2"
    local sign = base:sub(1, 1) == "-" and "-" or ""
    base = base:sub(#sign + 1)
    local result
    local rule = p.exact[base]
    if rule ~= nil and sign == "" then
        result = type(rule) == "function" and rule("", ctx) or rule
    else
        for _, entry in ipairs(p.prefixes) do
            if base:sub(1, #entry.prefix) == entry.prefix then
                local value = sign .. base:sub(#entry.prefix + 1)
                result = type(entry.rule) == "function" and entry.rule(value, ctx) or nil
                if result ~= nil then break end
            end
        end
    end
    if result == nil or result == false then return false end
    local compiled = { weight = 0, group = table.concat(pieces, ":") }
    local selector = ""
    for _, name in ipairs(pieces) do
        local found, weight = variant(p, name, lookup)
        if not found then return false end
        if found.pseudo then
            compiled.pseudo = found.pseudo
        elseif found.selector then
            selector = selector .. found.selector
        else
            compiled.conditions = compiled.conditions or {}
            for _, one in ipairs(found.conditions) do compiled.conditions[#compiled.conditions + 1] = one end
            compiled.weight = compiled.weight + weight
        end
    end
    if selector ~= "" then               -- first:odd: is one compound, *:first-child:nth-child(odd)
        local parsed = W.ParseSelectors("*" .. selector)
        if not parsed then return false end
        compiled.selector = parsed[1]
        W.UtilitySheet.structural = W.UtilitySheet.structural or parsed[1].structural
    end
    if type(result) == "string" then
        compiled.declarations = W.ParseInline(result)
    else
        compiled.parts = result
    end
    return compiled
end

-- --- For the cascade -------------------------------------------------------------------------

local caches = setmetatable({}, { __mode = "k" })     -- the variables function -> { class -> compiled }

local function contextFor(lookup)
    local spacing = tonumber(lookup("--spacing") or "") or 4
    local ctx = { spacing = spacing, var = lookup }
    ctx.color = function(value) return color(lookup, value) end
    -- "4" -> 16, "px" -> 1, "[300]" -> 300, "0.5" -> 2, "-4" -> -16
    ctx.length = function(value)
        local sign = 1
        if value:sub(1, 1) == "-" then sign, value = -1, value:sub(2) end
        local raw = arbitrary(value)
        local n = raw and tonumber((raw:gsub("px$", ""))) or (value == "px" and 1) or (tonumber(value) and tonumber(value) * spacing)
        return n and sign * n or nil
    end
    -- a size as the style language writes it: what `length` reads, and "1/2" -> 50%,
    -- "full" -> 100%, "screen" -> the picture's (`screen`: "vw" or "vh"), "[calc(100% - 40)]"
    ctx.size = function(value, screen)
        local part, whole = value:match("^(%d+)/(%d+)$")
        if part and tonumber(whole) > 0 then return tonumber(part) / tonumber(whole) * 100 .. "%" end
        if value == "full" then return "100%" end
        if value == "screen" and screen then return "100" .. screen end
        return ctx.length(value) or arbitrary(value)
    end
    ctx.number = function(value) return tonumber(arbitrary(value) or value) end
    return ctx
end

--- The declarations the utility classes of an element give, weakest first:
--- { { name, value, pseudo, conditions, selector, weight } }. Remembered on the element until its classes,
--- the theme or the vocabulary change.
function W.Utilities(element)
    local lookup = I.varsFor(element)
    local key = generation .. "|"
    for class in pairs(element.classes) do key = key .. class .. " " end
    if element.utilityKey == key and element.utilityVars == lookup then return element.utilityList end
    local cache = caches[lookup]
    if not cache or cache.generation ~= generation then
        cache = { generation = generation, classes = {}, ctx = contextFor(lookup) }
        caches[lookup] = cache
    end
    local p = preset(element.doc and element.doc.utilities or DEFAULT_PRESET)
    local out, groups, order = {}, {}, {}
    for class in pairs(element.classes) do
        local compiled = cache.classes[class]
        if compiled == nil then
            local ok, result = pcall(compile, p, class, lookup, cache.ctx)
            compiled = ok and result or false
            cache.classes[class] = compiled
        end
        if compiled then
            if compiled.declarations then
                for _, declaration in ipairs(compiled.declarations) do
                    out[#out + 1] = { name = declaration.name, value = declaration.value, pseudo = compiled.pseudo,
                        conditions = compiled.conditions, selector = compiled.selector, weight = compiled.weight }
                end
            else
                local id = compiled.group .. "|" .. tostring(compiled.pseudo)
                local group = groups[id]
                if not group then
                    group = { parts = {}, pseudo = compiled.pseudo, conditions = compiled.conditions,
                        selector = compiled.selector, weight = compiled.weight }
                    groups[id] = group
                    order[#order + 1] = group
                end
                for name, value in pairs(compiled.parts) do group.parts[name] = value end
            end
        end
    end
    for _, group in ipairs(order) do
        local declarations = {}
        assemble(group.parts, lookup, declarations)
        for _, declaration in ipairs(declarations) do
            declaration.pseudo, declaration.conditions, declaration.weight = group.pseudo, group.conditions, group.weight
            declaration.selector = group.selector
            out[#out + 1] = declaration
        end
    end
    table.sort(out, function(a, b) return a.weight < b.weight end)
    element.utilityKey, element.utilityVars, element.utilityList = key, lookup, out
    return out
end

--- `@apply px-4 py-2 rounded-md;` in a rule: the declarations those classes stand for.
--- Variants make no sense inside a rule and are refused.
function W.ApplyUtilities(text, element)
    local stand = { classes = {}, doc = element.doc }
    for class in text:gmatch("%S+") do
        if class:find(":", 1, true) and not class:find("[", 1, true) then
            I.report(element.doc and element.doc.name or "style", "@apply " .. class .. ": variants cannot be applied inside a rule")
        else
            stand.classes[class] = true
        end
    end
    return W.Utilities(stand)
end

-- --- The preset: Tailwind 4's names ----------------------------------------------------------

local function add(matcher, rule) W.RegisterUtility("wind4", matcher, rule) end

-- layout
add("flex", "display: flex")
add("flex-row", "display: flex; flex-direction: row")
add("flex-col", "display: flex; flex-direction: column")
add("flex-wrap", "flex-wrap: wrap")
add("flex-nowrap", "flex-wrap: nowrap")
add("flex-1", "flex-grow: 1")
add("grow", "flex-grow: 1")
add("grow-", function(value, ctx) local n = ctx.number(value) return n and ("flex-grow: " .. n) end)
add("shrink", "flex-shrink: 1")
add("shrink-", function(value, ctx) local n = ctx.number(value) return n and ("flex-shrink: " .. n) end)
add("basis-", function(value, ctx)
    if value == "auto" then return "flex-basis: auto" end
    local size = ctx.size(value)
    return size and ("flex-basis: " .. size)
end)
add("order-first", "order: -9999")
add("order-last", "order: 9999")
add("order-none", "order: 0")
add("order-", function(value, ctx) local n = ctx.number(value) return n and ("order: " .. n) end)
add("static", "position: static")
add("relative", "position: relative")
add("absolute", "position: absolute")
add("aspect-auto", "aspect-ratio: auto")
add("aspect-square", "aspect-ratio: 1 / 1")
add("aspect-video", "aspect-ratio: 16 / 9")
add("aspect-", function(value)                   -- aspect-[4/3], aspect-4/3
    local ratio = arbitrary(value) or value
    return ratio:match("^[%d.]+%s*/?%s*[%d.]*$") and ("aspect-ratio: " .. ratio)
end)
add("grid", "display: grid")
add("grid-cols-", function(value, _ctx)
    local raw = arbitrary(value)
    if raw then return "grid-template-columns: " .. raw end
    local n = tonumber(value)
    return n and ("grid-template-columns: repeat(" .. n .. ", 1fr)")
end)
add("grid-cols-auto-", function(value, ctx)      -- as many columns of at least this width as fit
    local n = ctx.length(value)
    return n and ("grid-template-columns: repeat(auto-fill, minmax(" .. n .. ", 1fr))")
end)
add("col-span-", function(value)
    if value == "full" then return "grid-column: span 99" end
    return tonumber(value) and ("grid-column: span " .. value)
end)
add("hidden", "display: none")
add("block", "display: block")
add("inline", "inline: true")
add("@container", "container-type: size")
for name, value in pairs({ start = "start", center = "center", ["end"] = "end", between = "space-between",
    around = "space-around", evenly = "space-evenly" }) do
    add("justify-" .. name, "justify-content: " .. value)
    add("content-" .. name, "align-content: " .. value)
end
add("content-stretch", "align-content: stretch")
for _, name in ipairs({ "start", "center", "end", "stretch" }) do
    add("items-" .. name, "align-items: " .. name)
    add("self-" .. name, "align-self: " .. name)
    add("justify-items-" .. name, "justify-items: " .. name)
    add("justify-self-" .. name, "justify-self: " .. name)
end

-- spacing and size
-- Padding, margin and the insets of a positioned element are parts: `p-4 px-2` is one padding.
-- "p" -> p- px- py- pt- pr- pb- pl-; the same for "m" (margins: also `auto`, a percentage and
-- -m-2) and "i" (inset- inset-x- inset-y- top- right- bottom- left-).
local function sideParts(part, classes, wide)
    for suffix, class in pairs(classes) do
        add(class .. "-", function(value, ctx)
            local n = (wide and value == "auto" and "auto") or ctx.length(value) or (wide and ctx.size(value))
            return n and { [part .. suffix] = n }
        end)
    end
end
sideParts("p", { [""] = "p", x = "px", y = "py", t = "pt", r = "pr", b = "pb", l = "pl" })
sideParts("m", { [""] = "m", x = "mx", y = "my", t = "mt", r = "mr", b = "mb", l = "ml" }, true)
sideParts("i", { [""] = "inset", x = "inset-x", y = "inset-y", t = "top", r = "right", b = "bottom", l = "left" }, true)
add("gap-", function(value, ctx) local n = ctx.length(value) return n and ("gap: " .. n) end)
add("gap-x-", function(value, ctx) local n = ctx.length(value) return n and ("column-gap: " .. n) end)
add("gap-y-", function(value, ctx) local n = ctx.length(value) return n and ("row-gap: " .. n) end)
add("indent-", function(value, ctx) local n = ctx.length(value) return n and ("indent: " .. n) end)
-- w-4, w-[300], w-1/2, w-full, w-screen, w-auto
local function sized(property, screen)
    return function(value, ctx)
        if value == "auto" then return property .. ": auto" end
        local size = ctx.size(value, screen)
        return size and (property .. ": " .. size)
    end
end
add("w-", sized("width", "vw"))
add("h-", sized("height", "vh"))
add("size-", function(value, ctx)
    local size = ctx.size(value)
    return size and ("width: " .. size .. "; height: " .. size)
end)
add("min-w-", sized("min-width", "vw"))
add("max-w-", sized("max-width", "vw"))
add("min-h-", sized("min-height", "vh"))
add("max-h-", sized("max-height", "vh"))

-- text: sizes in steps from the overlay's text size, any size in brackets ([22] font units,
-- [1.25em], [1.25rem]); otherwise a colour
for name, step in pairs({ xs = "-2", sm = "-1", base = "inherit", lg = "+1", xl = "+2", ["2xl"] = "+3", ["3xl"] = "+4" }) do
    add("text-" .. name, "font: " .. step)
end
add("text-left", "align: left")
add("text-center", "align: center")
add("text-right", "align: right")
add("text-wrap", "wrap: on")
add("text-", function(value, ctx)
    local inside = arbitrary(value)
    if inside and inside:match("^[%d.]+%a*$") then return "font-size: " .. inside end
    local c = ctx.color(value)
    return c and ("color: " .. c)
end)

-- colours, backgrounds, gradients
add("bg-", function(value, ctx) local c = ctx.color(value) return c and ("background: " .. c) end)
add("bg-none", "background-image: none")
add("bg-cover", "background-mode: stretch")
add("bg-repeat", "background-mode: tile")
add("bg-slice-", function(value, ctx) local n = ctx.number(value) return n and ("background-slice: " .. n) end)
add("transition", "transition: 0.15")
add("transition-none", "transition: none")
add("duration-", function(value, ctx) local n = ctx.number(value) return n and ("transition: " .. n / 1000) end)
add("font-size-", function(value, ctx) local n = ctx.number(value) return n and ("font-size: " .. n) end)
add("bg-pad-", function(value, ctx) local n = ctx.length(value) return n and ("background-padding: " .. n) end)
local DIRECTIONS = { t = "to top", tr = "to top right", r = "to right", br = "to bottom right", b = "to bottom",
    bl = "to bottom left", l = "to left", tl = "to top left" }
local function direction(value)
    local word = value:match("^to%-(%a+)$")
    if word and DIRECTIONS[word] then return { dir = DIRECTIONS[word] } end
    if tonumber(value) then return { dir = value .. "deg" } end
    return nil
end
add("bg-linear-", direction)
add("bg-gradient-", direction)                  -- Tailwind 3's name for the same
add("from-", function(value, ctx) local c = ctx.color(value) return c and { from = c } end)
add("to-", function(value, ctx) local c = ctx.color(value) return c and { to = c } end)
add("accent-", function(value, ctx) local c = ctx.color(value) return c and ("accent: " .. c) end)
add("opacity-", function(value, ctx) local n = ctx.number(value) return n and ("opacity: " .. n / 100) end)

-- borders, corners, shadows
add("border", "border: 1; border-color: var(--border)")
add("border-", function(value, ctx)
    if tonumber(value) then return "border: " .. value end
    local c = ctx.color(value)
    return c and ("border-color: " .. c)
end)
add("outline", function() return { outline = 1 } end)
add("outline-none", "outline: none")
add("outline-", function(value, ctx)
    if tonumber(value) then return { outline = tonumber(value) } end
    local c = ctx.color(value)
    return c and { outlineColor = c }
end)
for name, radius in pairs({ none = 0, xs = 2, sm = 4, md = 6, lg = 8, xl = 12, ["2xl"] = 16, ["3xl"] = 24, full = 999 }) do
    add("rounded-" .. name, "rounding: " .. radius)
end
add("rounded", "rounding: 4")
add("rounded-", function(value, _ctx) local n = tonumber(arbitrary(value) or "") return n and ("rounding: " .. n) end)
add("shadow", function() return { shadow = SHADOWS.md } end)
add("shadow-none", "box-shadow: none")
add("shadow-", function(value, ctx)
    if SHADOWS[value] then return { shadow = SHADOWS[value] } end
    local c = ctx.color(value)
    return c and { shadowColor = c }
end)

-- variants: states, then what the element has to be (sizes are found by name: sm: md: lg: xl:
-- 2xl: max-md: @sm: min-[500]:)
W.RegisterVariant("wind4", "hover", { pseudo = "hover" })
W.RegisterVariant("wind4", "active", { pseudo = "active" })
W.RegisterVariant("wind4", "selected", { pseudo = "selected" })
for name, selector in pairs({ first = ":first-child", last = ":last-child", only = ":only-child", odd = ":nth-child(odd)",
    even = ":nth-child(even)", empty = ":empty", disabled = ":disabled", enabled = ":enabled" }) do
    W.RegisterVariant("wind4", name, { selector = selector })
end
