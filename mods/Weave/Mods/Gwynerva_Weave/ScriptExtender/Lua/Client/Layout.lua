-- Weave, part 4: layout and size queries - flex, grid, the box model, @media, @container.
--
-- IMGUI lays elements out top to bottom and knows nothing else. Here the kit does it the way a
-- browser does: a container with `display: flex` or `display: grid` has its children measured
-- (where Script Extender drew them, how large) and placed by the kit at computed positions.
--
-- The arithmetic is pure functions over numbers (they are the reference for a renderer that
-- does its own layout); `place` at the end measures, calls them and moves the elements.
--
--   box    { width, height, gapX, gapY, intrinsic }: the room for the items. `height` is nil
--          when the container is as tall as its contents; `intrinsic` says the same of its
--          width (then `width` is -1 and there is no room to share).
--   item   { w, h, style, stretch, stretchY, el } on the way in: its size with its margins,
--          its style (el.layout), whether the kit may set its width / height. `prepare` adds
--          the margins (ml mr mt mb, mx = ml + mr, my; autoL autoR autoT autoB for `auto`), the
--          limits (minW maxW minH maxH), ratio, basis, fixedW / fixedH (a size its style gives).
--          A layout sets x, y (from the corner of the box, margins included) and, where it
--          decides the width, calls Weave.LayoutWidth(item, width) (margins included), which
--          keeps it within min / max and lets the height follow an aspect-ratio.
--
-- More layouts can be added without touching the kit:
--
--   Weave.RegisterLayout("masonry", function(box, items, style) ... end)
--
-- Margins, min / max, `order`, `position: absolute` and percentages are dealt with around the
-- layout function, so every layout has them.
--
-- Containers are worked out parents first, and nothing a container is told depends on where
-- its own children were drawn: a container inside another takes the place its parent gave it
-- and reports the size its own layout came to. So the result is the same whatever it was
-- before, and nothing can chase its own tail.
--
-- Cost: one container costs two property reads per frame while its window is open; children
-- are measured again only when the container moved or changed, right after a change, and
-- four times a second otherwise.

Weave = Weave or {}
local UI = Weave
local I = UI.Internal
local Backend = UI.Backend
local floor, max, min = math.floor, math.max, math.min

local layouts = {}
local EMPTY = {}
local QUERY_MS, REMEASURE_MS, SETTLE_PASSES = 100, 250, 4
local SMALLEST = 16          -- what flex leaves of an item without a min-width of its own

UI.LayoutModes = {}

--- Adds a layout mode for `display: <name>`. fn(box, items, style): see the top of this file.
function UI.RegisterLayout(name, fn)
    layouts[name] = fn
    UI.LayoutModes[name] = true
end

-- Elements the kit never places; those whose width it may set, and whose height (the others
-- keep their own).
local SKIP = { tooltip = true, popup = true, menubar = true }
UI.Stretchable = { button = true, child = true, progress = true, table = true, input = true, combo = true,
    slider = true, drag = true, number = true, selectable = true }
UI.StretchableY = { button = true, child = true, progress = true, table = true, selectable = true }

-- --- Boxes -----------------------------------------------------------------------------------

-- The two directions, as the names of an item's and a box's fields: layouts are written once.
local ROW = { pos = "x", size = "w", set = "width", margin = "mx", min = "minW", max = "maxW", fixed = "fixedW",
    stretch = "stretch", before = "autoL", after = "autoR", room = "width", gap = "gapX" }
local COLUMN = { pos = "y", size = "h", set = "height", margin = "my", min = "minH", max = "maxH", fixed = "fixedH",
    stretch = "stretchY", before = "autoT", after = "autoB", room = "height", gap = "gapY" }

-- A length (pixels, or { pixels, percent }) against what it is a percentage of; nil when that
-- is not known, or for a word such as "auto".
local function resolve(value, reference)
    if type(value) == "number" then return value end
    if type(value) ~= "table" or not reference then return nil end
    return value[1] + value[2] * reference / 100
end

-- A size with margins, kept within the item's min and max along an axis.
local function limit(item, axis, outer, least)
    local inner, low, high = outer - item[axis.margin], item[axis.min] or least or 0, item[axis.max]
    if high and inner > high then inner = high end
    if inner < low then inner = low end
    return inner + item[axis.margin]
end

-- Gives an item its size along an axis (margins included).
local function setSize(item, axis, outer)
    outer = limit(item, axis, outer)
    item[axis.size], item[axis.set] = outer, outer
    return outer
end

-- Gives an item its width (margins included); the height follows an aspect-ratio.
local function setWidth(item, outer)
    outer = setSize(item, ROW, outer)
    if item.ratio and not item.fixedH then setSize(item, COLUMN, (outer - item.mx) / item.ratio + item.my) end
    return outer
end
--- For a layout registered with RegisterLayout: gives an item its width, margins included.
UI.LayoutWidth = setWidth

-- Where a line of `free` leftover room starts and how much extra goes between its n items.
local function justify(mode, free, n)
    if free <= 0 then return 0, 0 end
    if mode == "center" then return free / 2, 0 end
    if mode == "end" or mode == "right" then return free, 0 end
    if mode == "space-between" then return 0, n > 1 and free / (n - 1) or 0 end
    if mode == "space-around" then return free / (2 * n), free / n end
    if mode == "space-evenly" then return free / (n + 1), free / (n + 1) end
    return 0, 0
end

-- An item of `size` in `room` across the direction of flow.
local function across(mode, room, size)
    if mode == "center" then return (room - size) / 2 end
    if mode == "end" or mode == "right" or mode == "bottom" then return room - size end
    return 0
end

-- Where an item stands in `room` along an axis: `auto` margins take what is left over first
-- (both: centred; the one before it: pushed to the end), then `mode` decides.
local function seat(item, axis, room, mode)
    local before, after = item[axis.before], item[axis.after]
    if not (before or after) then return across(mode, room, item[axis.size]) end
    local space = max(0, room - item[axis.size])
    return before and (after and space / 2 or space) or 0
end

local MARGINS = { "margin-top", "margin-right", "margin-bottom", "margin-left" }

-- What each item's style comes to in this box: margins, limits, sizes given by the style
-- (percentages are of the box's width, heights of its height when that is known), the order.
-- Returns the items that are laid out, in order, and those taken out of the flow.
local function prepare(box, items, style)
    local wide, tall = not box.intrinsic and box.width or nil, box.height
    local column = style["flex-direction"] == "column"
    local flow, floating, ordered = {}, {}, false
    for index, item in ipairs(items) do
        local s, m = item.style, {}
        for i, name in ipairs(MARGINS) do m[i] = resolve(s[name], wide) or 0 end
        item.mt, item.mr, item.mb, item.ml = m[1], m[2], m[3], m[4]
        item.autoT, item.autoR, item.autoB, item.autoL = s["margin-top"] == "auto", s["margin-right"] == "auto",
            s["margin-bottom"] == "auto", s["margin-left"] == "auto"
        item.mx, item.my = item.ml + item.mr, item.mt + item.mb
        item.minW, item.maxW = resolve(s["min-width"], wide), resolve(s["max-width"], wide)
        item.minH, item.maxH = resolve(s["min-height"], tall), resolve(s["max-height"], tall)
        item.ratio, item.basis, item.index = s["aspect-ratio"], resolve(s["flex-basis"], column and tall or wide), index
        item.w, item.h = item.w + item.mx, item.h + item.my
        local width, height = resolve(s.width, wide), resolve(s.height, tall)
        if width and width <= 0 then width = nil end            -- 0 and -1 are IMGUI's "as it comes"
        if height and height <= 0 then height = nil end
        item.fixedW, item.fixedH = width ~= nil, height ~= nil
        if height then setSize(item, COLUMN, height + item.my) end
        if width then
            setWidth(item, width + item.mx)
        elseif height and item.ratio then
            item.fixedW = true
            setSize(item, ROW, height * item.ratio + item.mx)
        elseif item.ratio then                                  -- as wide as it is: only the height follows
            setSize(item, COLUMN, (item.w - item.mx) / item.ratio + item.my)
        end
        if s.position == "absolute" then
            floating[#floating + 1] = item
        else
            flow[#flow + 1] = item
            ordered = ordered or s.order ~= nil
        end
    end
    if ordered then
        table.sort(flow, function(a, b)
            local first, second = a.style.order or 0, b.style.order or 0
            if first ~= second then return first < second end
            return a.index < b.index
        end)
    end
    return flow, floating
end

-- An item taken out of the flow (position: absolute) stands against the container's box,
-- `width` x `height`, by its insets; without them where the contents start (`left`, `top`).
local function float(item, width, height, left, top)
    local s = item.style
    local l, r = resolve(s.left, width), resolve(s.right, width)
    local t, b = resolve(s.top, height), resolve(s.bottom, height)
    if l and r and item.stretch and not item.fixedW then setWidth(item, width - l - r) end
    if t and b and item.stretchY and not item.fixedH then setSize(item, COLUMN, height - t - b) end
    item.x = l or (r and width - r - item.w) or left
    item.y = t or (b and height - b - item.h) or top
end

-- position: relative moves an item from where the layout put it.
local function nudge(item, width, height)
    local s = item.style
    if s.position ~= "relative" then return end
    local l, r = resolve(s.left, width), resolve(s.right, width)
    local t, b = resolve(s.top, height), resolve(s.bottom, height)
    item.x = item.x + (l or (r and -r) or 0)
    item.y = item.y + (t or (b and -b) or 0)
end

-- --- Flex ------------------------------------------------------------------------------------

-- Gives the items of a line their sizes along the flow (`target`) so that they take `room`,
-- growing by flex-grow or shrinking by flex-shrink - CSS's "resolving flexible lengths". An
-- item has a `base` it starts from and a `fit`: the base within its min and max. Whether the
-- line grows or shrinks is decided by the fits; an item that a limit holds keeps that size and
-- the others share again: when the limits gave out more than they took, those held at a
-- minimum, otherwise those held at a maximum.
local function share(line, room, axis)
    local fits = 0
    for _, item in ipairs(line) do fits = fits + item.fit end
    local growing, active, free = fits < room, {}, room
    for _, item in ipairs(line) do
        item.factor = growing and item.grow or item.shrink * (item.base - item[axis.margin])
        local held = (growing and item.base > item.fit) or (not growing and item.base < item.fit)
        if item.factor > 0 and not held then
            active[#active + 1] = item
            free = free - item.base
        else
            item.target = item.fit
            free = free - item.fit
        end
    end
    while #active > 0 do
        local total, excess = 0, 0
        for _, item in ipairs(active) do total = total + item.factor end
        for _, item in ipairs(active) do
            item.wanted = item.base + free * item.factor / total
            item.target = limit(item, axis, item.wanted, SMALLEST)
            excess = excess + item.target - item.wanted
        end
        if math.abs(excess) < 0.01 then return end              -- everyone has what the limits allow
        local rest = {}
        for _, item in ipairs(active) do
            local over = item.target - item.wanted
            if (excess > 0 and over > 0.01) or (excess < 0 and over < -0.01) then
                free = free - (item.target - item.base)         -- it keeps its limit
            else
                rest[#rest + 1] = item
            end
        end
        active = rest
    end
end

UI.RegisterLayout("flex", function(box, items, style)
    local column = style["flex-direction"] == "column"
    local main, cross = column and COLUMN or ROW, column and ROW or COLUMN
    -- the room along the flow and across it; nil where the container is as large as its contents
    local room, breadth = box[main.room], box[cross.room]
    if box.intrinsic then
        if column then breadth = nil else room = nil end
    end
    local gap, lineGap = box[main.gap], box[cross.gap]

    -- what each item starts from: its flex-basis, the size its style gives it, or the size it
    -- has; one that grows with neither starts from nothing (its size now is one the kit gave it)
    for _, item in ipairs(items) do
        local flexible, declared = item[main.stretch], item.basis ~= nil or item[main.fixed]
        item.grow = room and flexible and item.style["flex-grow"] or 0
        item.shrink = flexible and declared and (item.style["flex-shrink"] or 1) or 0
        if item.basis then item.base = item.basis + item[main.margin]
        elseif item.grow > 0 and not declared then item.base = item[main.margin]
        else item.base = item[main.size] end
        item.fit = limit(item, main, item.base)
    end

    -- lines
    local wrap = room ~= nil and style["flex-wrap"] == "wrap"
    local lines, line = {}, nil
    for _, item in ipairs(items) do
        local needed = line and (line.used + gap + item.fit) or item.fit
        if not line or (wrap and needed > room + 0.5) then
            line = { used = item.fit }
            lines[#lines + 1] = line
        else
            line.used = needed
        end
        line[#line + 1] = item
    end

    -- sizes along the flow, then how thick each line is
    local thickness = 0
    for _, row in ipairs(lines) do
        if room then share(row, room - gap * (#row - 1), main) end
        row.cross = 0
        for _, item in ipairs(row) do
            local target = item.target or item.fit
            if item.grow > 0 then target = limit(item, main, target, SMALLEST) end
            if math.abs(target - item[main.size]) > 0.01 then
                if column then setSize(item, COLUMN, target) else setWidth(item, target) end
            end
            row.cross = max(row.cross, item[cross.size])
        end
        thickness = thickness + row.cross
    end

    -- the lines across the flow: one line takes all the room there is, several share what is
    -- left by align-content
    local at, between = 0, 0
    if breadth and #lines == 1 then
        lines[1].cross = breadth
    elseif breadth then
        local free = breadth - thickness - lineGap * (#lines - 1)
        local mode = style["align-content"]
        if mode == "stretch" then
            for _, row in ipairs(lines) do row.cross = row.cross + max(0, free) / #lines end
        else
            at, between = justify(mode, free, #lines)
        end
    end

    for _, row in ipairs(lines) do
        -- across: stretch, as on the web unless told otherwise - but a width only, and only to
        -- a width the container has (the kit cannot know what size an element would have had
        -- by itself once it has set one) - then the item's place in the line
        for _, item in ipairs(row) do
            local mode = item.style["align-self"] or style["align-items"] or "stretch"
            if mode == "stretch" and column and breadth and item.stretch and not item.fixedW
                and not (item.autoL or item.autoR) then
                setWidth(item, row.cross)
            end
            item[cross.pos] = at + seat(item, cross, row.cross, mode)
        end
        -- along: `auto` margins take the room that is left, else justify-content
        local used, autos = gap * (#row - 1), 0
        for _, item in ipairs(row) do
            used = used + item[main.size]
            autos = autos + (item[main.before] and 1 or 0) + (item[main.after] and 1 or 0)
        end
        local free = room and room - used or 0
        local pos, extra, each = 0, 0, 0
        if autos > 0 then each = max(0, free) / autos else pos, extra = justify(style["justify-content"], free, #row) end
        for _, item in ipairs(row) do
            if item[main.before] then pos = pos + each end
            item[main.pos] = pos
            pos = pos + item[main.size] + gap + extra + (item[main.after] and each or 0)
        end
        at = at + row.cross + lineGap + between
    end
end)

-- --- Grid ------------------------------------------------------------------------------------

UI.RegisterLayout("grid", function(box, items, style)
    local tracks, widths = style.tracks or { { fr = 1 } }, {}
    if box.intrinsic then        -- no width to divide: every share is as wide as the widest item
        local widest = 0
        for _, item in ipairs(items) do widest = max(widest, item.w) end
        for i, track in ipairs(tracks.auto and { {} } or tracks) do widths[i] = track.px or widest end
    elseif tracks.auto then      -- as many columns of at least `min` as fit
        local n = max(1, floor((box.width + box.gapX) / (tracks.min + box.gapX)))
        for i = 1, n do widths[i] = (box.width - box.gapX * (n - 1)) / n end
    else
        local fixed, shares = 0, 0
        for _, track in ipairs(tracks) do
            fixed, shares = fixed + (track.px or 0), shares + (track.fr or 0)
        end
        local free = max(0, box.width - fixed - box.gapX * (#tracks - 1))
        for i, track in ipairs(tracks) do widths[i] = track.px or free * track.fr / shares end
    end
    local n = #widths
    local starts, x = {}, 0
    for i = 1, n do
        starts[i] = x
        x = x + widths[i] + box.gapX
    end
    local column, y, height, row = 1, 0, 0, {}
    local function flush()
        for _, item in ipairs(row) do
            item.y = y + seat(item, COLUMN, height, item.style["align-self"] or style["align-items"])
        end
        if #row > 0 then y = y + height + box.gapY end
        column, height, row = 1, 0, {}
    end
    for _, item in ipairs(items) do
        local span = min(item.style.span or 1, n)
        if column + span - 1 > n then flush() end
        local last = column + span - 1
        local cell = starts[last] + widths[last] - starts[column]
        local mode = item.style["justify-self"] or style["justify-items"] or "stretch"
        if mode == "stretch" and item.stretch and not item.fixedW and not (item.autoL or item.autoR) then
            setWidth(item, cell)
        end
        item.x = starts[column] + seat(item, ROW, cell, mode)
        row[#row + 1] = item
        height = max(height, item.h)
        column = last + 1
        if column > n then flush() end
    end
    flush()
end)

-- --- Measuring and placing ------------------------------------------------------------------

-- Positions are given to IMGUI in the coordinates of the window the element is drawn in.
local function ownerOf(el)
    while el.parent and el.tag ~= "window" and el.tag ~= "child" and el.tag ~= "popup" do el = el.parent end
    return el
end

-- The room between items: the container's own row-gap / column-gap, else `gap`, which is
-- IMGUI's ItemSpacing and reaches everything inside: the nearest one set applies.
local function gapOf(el)
    local x, y = el.layout["column-gap"], el.layout["row-gap"]
    while el and not (x and y) do
        local op = el.applied["var:ItemSpacing"]
        if op then x, y = x or op.value[1], y or op.value[2] end
        el = el.parent
    end
    return x or 8, y or 4
end

-- A container drawn in its parent's window: it has no size of its own, so the parent's layout
-- tells it its place and width and it reports the size its own layout came to. (A window or a
-- scrolling box that is a container is a widget like any other to the layout around it.)
local function isNested(el)
    return el.anchor ~= nil and el.layout ~= nil and el.layout.display ~= nil and ownerOf(el) ~= el
end

-- The container a nested one reports its size to.
local function containerOf(el)
    local parent = el.parent
    while parent and parent.fragment do parent = parent.parent end
    return parent
end

local function place(el, t)
    local rect, avail = Backend.anchorRect(el), Backend.anchorAvail(el)
    if avail[1] <= 0 then return end                       -- not drawn: a tab that is not selected
    local owner, style = ownerOf(el), el.layout
    -- placed by a parent's layout and drawn in the same window as it: the parent's word counts
    local given = owner ~= el and el.placed or nil
    -- the box, padding included: what the parent gave, what a window or a scrolling box has,
    -- else the element's own size or the room there is
    local width, height
    if given then
        width, height = el.givenWidth or -1, el.givenHeight
    elseif owner == el then
        width, height = avail[1], avail[2]
    else
        local own = { mx = 0, my = 0, minW = resolve(style["min-width"], avail[1]), maxW = resolve(style["max-width"], avail[1]),
            minH = resolve(style["min-height"]), maxH = resolve(style["max-height"]) }
        local wanted, tall = resolve(style.width, avail[1]), resolve(style.height)
        width = limit(own, ROW, wanted and wanted > 0 and wanted or avail[1])
        height = (tall and tall > 0 or own.minH) and limit(own, COLUMN, tall or 0) or nil
    end
    local seen = el.layoutSeen
    local same = seen and seen[4] == el.layoutVersion and seen[5] == #el.children and seen[3] == width and seen[6] == height
        and ((given and seen[1] == given[1] and seen[2] == given[2])
            or (not given and seen[1] == rect[1] and seen[2] == rect[2]))
    if same and el.layoutSettle <= 0 and t < el.layoutNext then return end
    el.layoutSeen = { given and given[1] or rect[1], given and given[2] or rect[2], width, el.layoutVersion, #el.children, height }
    el.layoutNext = t + REMEASURE_MS
    if el.layoutSettle > 0 then el.layoutSettle = el.layoutSettle - 1 end

    local layout = layouts[style.display]
    if not layout then return end
    local left, top
    if given then
        left, top = given[1], given[2]
    else
        local origin, scroll = Backend.rect(owner), Backend.scroll(owner)
        left, top = rect[1] - origin[1] + scroll[1], rect[2] - origin[2] + scroll[2]
    end
    local padTop, padRight = style["padding-top"] or 0, style["padding-right"] or 0
    local padBottom, padLeft = style["padding-bottom"] or 0, style["padding-left"] or 0
    local gapX, gapY = gapOf(el)
    local items = {}
    local function collect(parent)
        for _, child in ipairs(parent.children) do
            if child.fragment then           -- what #if and #for build into: its contents count
                -- the group they are built into starts where IMGUI's own flow has come to; it
                -- is kept at the corner of the contents, inside the box whatever is in it
                local x, y = floor(left + padLeft + 0.5), floor(top + padTop + 0.5)
                if not child.placed or child.placed[1] ~= x or child.placed[2] ~= y then
                    child.placed = { x, y }
                    Backend.place(child, x, y)
                end
                if child.visibleNow ~= false then collect(child) end
            elseif child.widget and child.visibleNow ~= false and not SKIP[child.tag] then
                local item = { el = child, style = child.layout or EMPTY, stretch = UI.Stretchable[child.tag] or false,
                    stretchY = UI.StretchableY[child.tag] or false }
                local content = isNested(child) and child.contentSize
                if content then              -- a container inside: the size its own layout came to
                    item.w, item.h, item.stretch, item.stretchY = content[1], content[2], true, true
                else
                    local r = Backend.rect(child)
                    item.w, item.h = r[3] - r[1], r[4] - r[2]
                end
                items[#items + 1] = item
            end
        end
    end
    collect(el)
    local intrinsic = width < 0
    local box = { width = intrinsic and -1 or max(0, width - padLeft - padRight),
        height = height and max(0, height - padTop - padBottom), gapX = gapX, gapY = gapY, intrinsic = intrinsic }
    local flow, floating = prepare(box, items, style)
    layout(box, flow, style)

    local right, bottom = 0, 0
    for _, item in ipairs(flow) do
        right, bottom = max(right, (item.x or 0) + item.w), max(bottom, (item.y or 0) + item.h)
    end
    -- the size the container comes to, padding included: the width it has, if it has one
    -- (what does not fit sticks out, it does not widen the box)
    local outerWidth = (intrinsic and right or box.width) + padLeft + padRight
    local outerHeight = height or (bottom + padTop + padBottom)
    for _, item in ipairs(flow) do
        item.x, item.y = (item.x or 0) + padLeft, (item.y or 0) + padTop
        nudge(item, outerWidth, outerHeight)
    end
    for _, item in ipairs(floating) do float(item, outerWidth, outerHeight, padLeft, padTop) end

    local changed = false
    for _, item in ipairs(items) do
        local child = item.el
        local x, y = floor(left + item.x + item.ml + 0.5), floor(top + item.y + item.mt + 0.5)
        local placed = child.placed
        if not placed or placed[1] ~= x or placed[2] ~= y then
            child.placed = { x, y }
            Backend.place(child, x, y)
            changed = true
        end
        local w = item.width and floor(item.width - item.mx + 0.5)
        local h = item.height and floor(item.height - item.my + 0.5)
        if isNested(child) then
            if child.givenWidth ~= w or child.givenHeight ~= h then child.givenWidth, child.givenHeight, changed = w, h, true end
        else
            if w and item.stretch and child.placedWidth ~= w then
                child.placedWidth, changed = w, true
                Backend.setWidth(child, w)
            end
            if h and item.stretchY and child.placedHeight ~= h then
                child.placedHeight, changed = h, true
                Backend.setHeight(child, h)
            end
        end
    end
    -- padding and a height of its own are not something IMGUI gives a group: the marker at its
    -- start takes the whole box's room, so the group - and what is drawn behind it - is that large
    if owner ~= el then
        local strut = (padTop + padRight + padBottom + padLeft > 0 or height ~= nil) and { outerWidth, outerHeight } or { 0, 0 }
        local had = el.strut or { 0, 0 }
        if had[1] ~= strut[1] or had[2] ~= strut[2] then
            el.strut = strut
            Backend.sizeAnchor(el, strut[1], strut[2])
        end
    end
    local content = el.contentSize
    if not content or content[1] ~= outerWidth or content[2] ~= outerHeight then
        el.contentSize = { outerWidth, outerHeight }
        local parent = containerOf(el)
        if parent then parent.layoutSettle = SETTLE_PASSES end           -- its size is news to the parent
    end
    -- what was just set shows in the numbers a frame or two later: look again a few times
    if changed then el.layoutSettle = SETTLE_PASSES end
end

-- --- Size queries ---------------------------------------------------------------------------

-- Which @media / @container conditions hold now, as a string: the style is applied again only
-- when this changes - when a breakpoint is crossed, not on every pixel of a resize.
local function querySignature(doc, boxes)
    local bits = {}
    for _, sheet in ipairs(boxes.sheets) do
        for _, condition in ipairs(sheet.conditions) do
            if condition.kind == "media" then
                bits[#bits + 1] = UI.Holds(condition, doc.mediaSize[1], doc.mediaSize[2]) and "1" or "0"
            else
                for _, box in ipairs(boxes) do
                    bits[#bits + 1] = UI.Holds(condition, box.boxSize[1], box.boxSize[2]) and "1" or "0"
                end
            end
        end
    end
    return table.concat(bits)
end

local function queries(doc)
    local conditional, sheets = false, {}
    for i, sheet in ipairs(doc.sheets) do sheets[i] = sheet end
    sheets[#sheets + 1] = UI.UtilitySheet            -- sm: md: @lg: on utility classes
    for _, sheet in ipairs(sheets) do
        if #sheet.conditions > 0 then conditional = true end
    end
    if not conditional then return end
    local scale = doc.scale or 1
    local size = Backend.readSize(doc.window)
    if size[1] > 0 then doc.mediaSize = { size[1] / scale, size[2] / scale } end
    local boxes = { sheets = sheets }
    for _, el in ipairs(doc.elements) do
        if el.isContainer and el.widget then
            local r = Backend.rect(el)
            if r[3] > r[1] then el.boxSize = { (r[3] - r[1]) / scale, (r[4] - r[2]) / scale } end
            if el.boxSize then boxes[#boxes + 1] = el end
        end
    end
    local signature = querySignature(doc, boxes)
    if signature ~= doc.querySignature then
        doc.querySignature = signature
        doc.root:_restyle()
    end
end

-- vw, vh and a window's percentages are of the game's picture: when its size changes, the
-- documents that used one are styled again (the others are not even asked).
local function viewportChanged(doc)
    if not doc.viewportUnits then return false end
    local size, seen = Backend.viewport(), doc.viewportSeen
    doc.viewportSeen = { size[1], size[2] }
    return seen ~= nil and (seen[1] ~= size[1] or seen[2] ~= size[2])
end

-- --- Every frame, for every open document ---------------------------------------------------

UI.OnFrame(function(doc, t)
    if t >= (doc.queryNext or 0) then
        doc.queryNext = t + QUERY_MS
        if viewportChanged(doc) then doc.root:_restyle() end
        queries(doc)
    end
    local order = doc.layoutOrder
    if not order then                   -- parents before their children: the order they were built in
        order = {}
        for _, el in ipairs(doc.elements) do
            if doc.layouts[el] then order[#order + 1] = el end
        end
        doc.layoutOrder = order
    end
    for _, el in ipairs(order) do
        if el.widget and el.anchor and el.layout and el.layout.display then
            local ok, problem = pcall(place, el, t)
            if not ok then I.report(doc.name, "layout of <" .. el.tag .. ">: " .. tostring(problem):gsub("\n.*", "")) end
        end
    end
end)
