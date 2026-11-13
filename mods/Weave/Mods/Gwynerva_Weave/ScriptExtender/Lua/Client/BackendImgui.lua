-- Weave, the renderer: everything that touches what is actually drawn. This one draws with
-- Script Extender's IMGUI widgets. Dom.lua, Component.lua and Layout.lua know elements, styles
-- and layout; they keep `el.widget`, `el.anchor` and `doc.window` as handles they never look
-- into and call the functions below. Another renderer is another file that fills the same table.
--
-- An element `el` is Dom.lua's: { tag, attrs, label, widget, parent, doc, ... }. A renderer may
-- keep fields of its own on it; this one keeps suffix, size, values, pendingValue, offset,
-- alignShift, alignWait, decorated, constrained. What a style means to the kit itself - the
-- layout's numbers, visibility, alignment, a window's size and its limits, transitions, custom
-- properties - is kept by Dom.lua and never comes here as an op.
--
-- This renderer needs GateKit's build of Script Extender on the v34 base (docs/se-fork.md):
-- drawing behind elements, geometry read back, styles that can be taken back, a text size per
-- element. Without it the kit builds nothing (check below).
--
-- Weave.Backend - the contract:
--
-- Capabilities
--   name                          "imgui"
--   check()                       -> nil when this renderer can draw here, or one sentence: why not
--   capabilities()                -> { files }: what differs between set-ups that can draw at all;
--                                 files = pictures from files of a mod (not under Vulkan yet)
--
-- Windows (`window` is what createWindow returned)
--   createWindow(el, id)          -> window, closed, titled el.label; reads el.attrs.closeable / scaling
--   destroyWindow(window)         removes it with everything inside; never fails
--   isOpen(window)                -> boolean
--   setOpen(window, on)           shows or hides it
--   closeable(window)             -> boolean: does it have a close button
--   setSize(window, { w, h })     sizes it now
--   setPos(window, { x, y }[, { px, py }])   moves it now; the pivot is which point of it goes there (0..1)
--   readSize(window)              -> { w, h } as drawn last ({ 0, 0 } before the first frame)
--   readPos(window)               -> { x, y } as drawn last
--   viewport()                    -> { w, h } of the game's picture
--
-- Elements
--   registerTag(name, build)      adds a tag: build(el, parent, id) -> widget
--   hasTag(tag)                   -> boolean: is <tag> an element this renderer builds
--   create(el, parent, id)        -> widget for el inside the parent's widget, or nil; may raise;
--                                 `id` is unique in the document; may clear el.rawLabel (no text)
--   destroy(el)                   removes el's widget with everything inside; never fails
--   addAnchor(el)                 -> a marker of no size inside el, to be built before its children:
--                                 where the contents start and how wide they may be (el.anchor)
--   setLabel(el, text)            what the element says: a text's text, a button's caption, a title,
--                                 the line over a progress bar
--   setFlag(el, flag)             -> boolean: switches on a renderer flag by name (flags="NoResize")
--   setDisabled(el, on)           greys it out and stops it answering; never fails
--   setInline(el, on)             on the same line as the element before it
--   setVisible(el, on)            shows or hides it; never fails
--   addTip(el, text)              -> tip: a line of text in the element's tooltip
--   setTip(tip, text)             changes that line
--   focus(el)                     the keyboard cursor into it; a window comes to the front
--   openPopup(el)                 opens a <popup>
--   selectTab(el)                 selects a <tab>
--   expand(el, on)                opens or closes a <header> or a <tree>
--
-- Values and events
--   hasValue(tag)                 -> boolean: does <tag> hold a value (checkbox, input, combo ...)
--   getValue(el)                  -> the value; may raise
--   setValue(el, value)           sets it; raises no event
--   setOptions(el, list)          a combo's options: { "a", ... } or { { value = , label = }, ... }
--   hasEvent(event)               -> boolean: is there such an event (click, dblclick, change, close,
--                                 expand ...)
--   listen(el, event, fn)         -> boolean: fn() is called when the user causes the event; one fn
--                                 per event, false when the element cannot raise it
--
-- Style (an op is what Weave.Resolve gives, { kind, key, value }; the ones that come here:
-- "color" (key: a colour slot), "var" (key: a style variable, value: { x, y }), "deco" (drawn
-- behind, see decorate) and "prop" with the keys width, height (pixels), inline, wrap, offset
-- ({ x, y }), font-family, font-size (in Script Extender's font units: the six built-in sizes are
-- 24 28 32 36 40 44; drawn at 2/3 of that in pixels in a 1080-line picture), background-opacity)
--   applyOp(el, op[, old, glide]) applies one; may raise. `old` is the op it replaces; with
--                                 `glide` (seconds) the change from it is a transition
--   unapply(el, op)               -> boolean: takes it back; false when the window has to be built
--                                 anew (this renderer takes back every op it is given)
--   decorate(el, computed)        sets what is drawn behind el from the "deco:*" ops of its computed style
--   constrain(el, lowest, highest)   the least and the most a window may measure, { w, h } each;
--                                 without them the window is free again
--   align(el, fraction)           called every frame for an element with a place of its own in the
--                                 width it is given (0 left .. 1 right): shifts it there; without a
--                                 fraction puts it back
--   setWidth(el, width)           the width a layout gives an element
--   setHeight(el, height)         and the height (the elements that can take one)
--
-- Geometry (numbers are as drawn last, in the picture's coordinates)
--   rect(el)                      -> { x1, y1, x2, y2 } of the element
--   scroll(el)                    -> { x, y }: how far el's contents are scrolled
--   anchorRect(el)                -> { x1, y1, x2, y2 } of el's anchor: [1], [2] is where the contents start
--   anchorAvail(el)               -> { w, h }: the room there was at the anchor; w <= 0 when not drawn
--   place(el, x, y)               puts the element at a position inside the window it is drawn in
--   sizeAnchor(el, width, height) makes el's anchor take that much room, so that el is at least
--                                 that large (a container's padding and height); 0, 0 takes it back
--
-- Canvas
--   draw(el, commands, color, where)   replaces what a <canvas> shows; color(value) -> { r, g, b, a }
--                                 or raises; a bad command is skipped and reported under `where`

Weave = Weave or {}
local UI = Weave
UI.Backend = {}
local Backend = UI.Backend
Backend.name = "imgui"

local now = Ext.Utils.MonotonicTime

-- Dom.lua's helpers, looked up when called: this file is loaded before it.
local function report(where, message) return UI.Internal.report(where, message) end
local function splitList(text) return UI.Internal.splitList(text) end

-- --- What this Script Extender can do ----------------------------------------------------

-- One member for each addition of GateKit's build the renderer stands on: drawing behind
-- elements, geometry read back, styles taken back, a text size per element. Reading a member
-- the build does not have raises an error.
local REQUIRED_MEMBERS = { "SetBackground", "LastRect", "ClearStyle", "FontSize" }
local verdict             -- false: can draw; a string: why not

local function lackingMembers()
    local lacking = {}
    local probe = Ext.IMGUI.NewWindow("###gk_probe")
    probe.Open = false
    for _, member in ipairs(REQUIRED_MEMBERS) do
        if not pcall(function() return probe[member] end) then lacking[#lacking + 1] = member end
    end
    pcall(function() probe:Destroy() end)
    return lacking
end

function Backend.check()
    if verdict == nil then
        local ok, version = pcall(Ext.Utils.Version)
        version = ok and tonumber(version) or 0
        local lacking = version >= 34 and lackingMembers() or {}
        if version < 34 then
            verdict = "needs GateKit's build of Script Extender (v34 base, docs/se-fork.md); this one is v" .. version
        elseif #lacking > 0 then
            verdict = "needs GateKit's build of Script Extender (docs/se-fork.md); this v" .. version
                .. " has no " .. table.concat(lacking, ", ")
        else
            verdict = false
        end
    end
    return verdict or nil
end

-- Pictures from files are decoded by the DX11 renderer only; under Vulkan they draw nothing yet.
local capabilities
function Backend.capabilities()
    if not capabilities then
        local ok, info = pcall(Ext.IMGUI.GetBackendInfo)
        capabilities = { files = ok and type(info) == "table" and info.Renderer == "DX11" }
    end
    return { files = capabilities.files }
end

-- A path with a folder in it is a file of a mod; anything else is a texture's GUID or an icon.
local function usable(src, where)
    if not src:find("[/\\]") or Backend.capabilities().files then return true end
    report(where, "pictures from files need the DX11 renderer; under Vulkan " .. src .. " is left out")
    return false
end

-- --- Windows ------------------------------------------------------------------------------

function Backend.createWindow(el, id)
    -- what follows ### is IMGUI's identifier: the title can change, the window stays the same
    el.suffix = "###" .. id
    local window = Ext.IMGUI.NewWindow(el.label .. el.suffix)
    window.Open = false
    window.Closeable = el.attrs.closeable ~= "false"
    if el.attrs.scaling then window.Scaling = el.attrs.scaling end
    return window
end

function Backend.destroyWindow(window)
    pcall(function() window:Destroy() end)
end

function Backend.isOpen(window) return window.Open end
function Backend.setOpen(window, on) window.Open = on end
function Backend.closeable(window) return window.Closeable end
function Backend.setSize(window, size) window:SetSize(size, "Always") end

function Backend.setPos(window, pos, pivot)
    if pivot then window:SetPos(pos, "Always", pivot) else window:SetPos(pos, "Always") end
end

function Backend.readSize(window) return window.LastSize end
function Backend.readPos(window) return window.LastPosition end
function Backend.viewport() return Ext.IMGUI.GetViewportSize() end

-- --- Styling ------------------------------------------------------------------------------

local SIZED = { button = true, child = true, progress = true, selectable = true, table = true }

function Backend.setVisible(el, visible)
    pcall(function() el.widget.Visible = visible end)
end

-- min-width and friends of a window, kept by Script Extender until changed.
function Backend.constrain(el, lowest, highest)
    if not lowest then
        if el.constrained then el.constrained = false el.widget:SetSizeConstraints() end
        return
    end
    el.constrained = true
    el.widget:SetSizeConstraints(lowest, highest)
end

local DECORATIONS = {}      -- what is drawn behind an element -> its key in a computed style
for _, name in ipairs({ "background", "gradient", "rounding", "padding", "border", "shadow", "image", "imageMode",
    "imageSlice", "imageTint" }) do
    DECORATIONS[name] = "deco:" .. name
end

-- Everything drawn behind the element, set anew from the computed style.
function Backend.decorate(el, computed)
    local want = {}
    for name, key in pairs(DECORATIONS) do
        local op = computed[key]
        if op and op.value then want[name] = op.value end
    end
    local any = want.background or want.gradient or want.border or want.shadow or want.image
    if not any and not el.decorated then return end
    local w = el.widget
    w:ClearDecoration()
    el.decorated = any and true or false
    if not any then return end
    if want.gradient then
        w:SetBackgroundGradient(want.gradient[1], want.gradient[2], want.gradient[3])
    elseif want.background then
        w:SetBackground(want.background)
    end
    if want.image and usable(want.image, el.doc and el.doc.name or "<" .. el.tag .. ">") then
        w:SetBackgroundImage(want.image, want.imageMode or "Stretch", want.imageTint or { 1, 1, 1, 1 }, want.imageSlice or 0)
    end
    if want.rounding then w:SetBackgroundRounding(want.rounding) end
    if want.padding then w:SetBackgroundPadding(want.padding[1], want.padding[2]) end
    if want.border then w:SetBorder(want.border[1], want.border[2]) end
    if want.shadow then w:SetShadow(want.shadow[1], want.shadow[2], want.shadow[3], want.shadow[4]) end
end

-- font-family: a font of one's own, loaded once per family. One font file is one font at every
-- size on this Script Extender, so the size it is loaded at only matters where no font-size
-- says otherwise: Medium's, Script Extender's own text size. The name is prefixed so that a
-- family cannot replace one of the six built-in fonts.
local LOADED_SIZE = 32
local loadedFonts = {}      -- family .. "|" .. path -> the name it was loaded under, or false

local function setFamily(el, family)
    if not family then
        el.widget.Font = ""
        return
    end
    local path = UI.FontFiles[family]
    if not path then error("no font \"" .. family .. "\" (@font-face or Weave.RegisterFont)") end
    local key = family .. "|" .. path
    if loadedFonts[key] == nil then
        local name = "Weave/" .. family
        loadedFonts[key] = Ext.IMGUI.LoadFont(name, path, LOADED_SIZE) and name or false
    end
    if not loadedFonts[key] then error("the font file cannot be loaded: " .. path) end
    el.widget.Font = loadedFonts[key]
end

-- A width or a height in pixels, or nil for the widget's own: each kind of widget takes it its
-- own way.
local function resize(el, index, value)
    local w = el.widget
    if SIZED[el.tag] then
        el.size = el.size or {}
        el.size[index] = value
        w.Size = (el.size[1] or el.size[2]) and { el.size[1] or 0, el.size[2] or 0 } or nil
    elseif el.tag == "input" and (index == 2 or el.attrs.multiline) then
        el.size = el.size or {}
        el.size[index] = value
        w.SizeHint = (el.size[1] or el.size[2]) and { el.size[1] or -1, el.size[2] or 0 } or nil
    elseif el.tag == "dummy" then
        w[index == 1 and "Width" or "Height"] = value or tonumber(el.attrs[index == 1 and "width" or "height"]) or 0
    elseif index == 1 then
        w.ItemWidth = value
    end
end

-- Where IMGUI's own flow puts the element, moved by the style's offset and by the shift that
-- aligns it. Nothing at all when both are 0: an offset, even of 0, would keep the layout from
-- placing the element (an offset wins over a position).
local function shift(el)
    local base, extra = el.offset or { 0, 0 }, el.alignShift or 0
    local x, y = base[1] + extra, base[2]
    el.widget.PositionOffset = (x ~= 0 or y ~= 0) and { x, y } or nil
end

local PROPERTIES = {        -- op.key -> { set = fn(el, value), clear = fn(el) } for the ops of kind "prop"
    width = { set = function(el, value) resize(el, 1, value) end, clear = function(el) resize(el, 1, nil) end },
    height = { set = function(el, value) resize(el, 2, value) end, clear = function(el) resize(el, 2, nil) end },
    inline = { set = function(el, value) el.widget.SameLine = value end, clear = function(el) el.widget.SameLine = false end },
    wrap = { set = function(el, value) el.widget.TextWrapPos = value end, clear = function(el) el.widget.TextWrapPos = nil end },
    offset = {
        set = function(el, value) el.offset = value shift(el) end,
        clear = function(el) el.offset = nil shift(el) end,
    },
    ["font-family"] = { set = setFamily, clear = function(el) setFamily(el, nil) end },
    ["font-size"] = {
        set = function(el, value) el.widget.FontSize = value end,
        clear = function(el) el.widget.FontSize = nil end,
    },
    ["background-opacity"] = {
        set = function(el, value) el.widget:SetBgAlpha(value) end,
        clear = function(el) el.widget:SetBgAlpha(nil) end,
    },
}

-- `old`: what the same key was set to before, when there was something; with `glide` (seconds)
-- the change from it is a transition.
function Backend.applyOp(el, op, old, glide)
    local w = el.widget
    glide = old and glide
    if op.kind == "color" then
        w:SetColor(op.key, op.value)
        if glide then w:AnimateColor(op.key, old.value, op.value, glide) end
    elseif op.kind == "var" then
        w:SetStyle(op.key, op.value[1], op.value[2])
        if glide and op.value[1] == op.value[2] and old.value[1] == old.value[2] then
            w:AnimateStyle(op.key, old.value[1], op.value[1], glide)      -- one number: opacity, rounding
        end
        -- a child window draws its border only when asked to
        if op.key == "ChildBorderSize" and el.tag == "child" then w.Border = op.value[1] > 0 end
    elseif op.kind == "prop" and PROPERTIES[op.key] then
        PROPERTIES[op.key].set(el, op.value)
    end
end

function Backend.setWidth(el, width) resize(el, 1, width) end
function Backend.setHeight(el, height) resize(el, 2, height) end

-- align: IMGUI lays things out left to right and knows no "right" or "center". Script
-- Extender reports where an element was drawn and how much width it had, so the kit shifts the
-- element by the room left over - on every tick while the window is open, since the room
-- changes as the window is resized. After a shift the next few ticks are skipped: the numbers
-- read then may still be from a frame drawn before it. Without a fraction the element goes
-- back to where IMGUI puts it.
function Backend.align(el, fraction)
    local w = el.widget
    if not fraction then
        el.alignShift = nil
        shift(el)
        return
    end
    if (el.alignWait or 0) > 0 then el.alignWait = el.alignWait - 1 return end
    local rect, avail = w.LastRect, w.LastAvail
    local width = rect[3] - rect[1]
    if width <= 0 or avail[1] <= 0 then return end
    local had = el.alignShift or 0
    local want = math.max(0, math.floor((avail[1] + had - width) * fraction + 0.5))
    if want ~= had then
        el.alignShift, el.alignWait = want, 3
        shift(el)
    end
end

-- Takes back what a rule that no longer applies had set.
function Backend.unapply(el, op)
    local w = el.widget
    if op.kind == "deco" then
        return true             -- decorate() sets everything drawn behind anew
    elseif op.kind == "color" then
        w:ClearColor(op.key)
    elseif op.kind == "var" then
        w:ClearStyle(op.key)
        if op.key == "ChildBorderSize" and el.tag == "child" then w.Border = el.attrs.border ~= nil end
    elseif op.kind == "prop" and PROPERTIES[op.key] then
        PROPERTIES[op.key].clear(el)
    else
        return false
    end
    return true
end

-- --- Values -------------------------------------------------------------------------------

local function number4()
    return {
        get = function(el) return el.widget.Value[1] end,
        set = function(el, v)
            v = tonumber(v) or 0
            if el.attrs.int then v = math.floor(v + 0.5) end
            el.widget.Value = { v, 0, 0, 0 }
        end,
    }
end

local VALUE = {
    checkbox = {
        get = function(el) return el.widget.Checked end,
        set = function(el, v) el.widget.Checked = v and true or false end,
    },
    radio = {
        get = function(el) return el.widget.Active end,
        set = function(el, v) el.widget.Active = v and true or false end,
    },
    selectable = {
        get = function(el) return el.widget.Selected end,
        set = function(el, v) el.widget.Selected = v and true or false end,
    },
    input = {
        get = function(el) return el.widget.Text end,
        set = function(el, v) el.widget.Text = tostring(v == nil and "" or v) end,
    },
    combo = {
        get = function(el) return el.values[el.widget.SelectedIndex + 1] end,
        set = function(el, v)
            el.pendingValue = v
            for i, value in ipairs(el.values) do
                if value == v then el.widget.SelectedIndex = i - 1 return end
            end
        end,
    },
    progress = {
        get = function(el) return el.widget.Value end,
        set = function(el, v) el.widget.Value = tonumber(v) or 0 end,
    },
    color = {
        get = function(el) return el.widget.Color end,
        set = function(el, v) el.widget.Color = v end,
    },
    slider = number4(), drag = number4(), number = number4(),
}
VALUE.picker = VALUE.color

function Backend.hasValue(tag) return VALUE[tag] ~= nil end
function Backend.getValue(el) return VALUE[el.tag].get(el) end
function Backend.setValue(el, value) VALUE[el.tag].set(el, value) end

function Backend.setOptions(el, list)
    local labels, values = {}, {}
    for i, option in ipairs(list or {}) do
        if type(option) == "table" then
            values[i] = option.value
            labels[i] = tostring(option.label or option.value)
        else
            values[i] = option
            labels[i] = tostring(option)
        end
    end
    el.values = values
    el.widget.Options = labels
    if el.pendingValue ~= nil then VALUE.combo.set(el, el.pendingValue) end
end

-- --- Events -------------------------------------------------------------------------------

local EVENTS = {
    click = "OnClick", dblclick = "OnDoubleClick", rightclick = "OnRightClick", change = "OnChange",
    hover = "OnHoverEnter", leave = "OnHoverLeave", focus = "OnActivate", blur = "OnDeactivate",
    close = "OnClose", expand = "OnExpand", collapse = "OnCollapse",
}

function Backend.hasEvent(event) return EVENTS[event] ~= nil end

function Backend.listen(el, event, fn)
    local property = EVENTS[event]
    return (pcall(function() el.widget[property] = fn end))
end

-- --- Elements -----------------------------------------------------------------------------

function Backend.setLabel(el, text)
    -- a progress bar's text is the line drawn over it (what overlay="" sets when it is built)
    if el.tag == "progress" then el.widget.Overlay = text return end
    el.widget.Label = text .. (el.suffix or "")
end

function Backend.setFlag(el, flag)
    return (pcall(function() el.widget[flag] = true end))
end

function Backend.setDisabled(el, on)
    pcall(function() el.widget.Disabled = on end)
end

function Backend.setInline(el, on)
    el.widget.SameLine = on
end

function Backend.addTip(el, text)
    return el.widget:Tooltip():AddText(text)
end

function Backend.setTip(tip, text)
    tip.Label = text
end

function Backend.focus(el)
    if el.tag == "window" then el.widget:SetFocus() else el.widget:Activate() end
end

function Backend.openPopup(el)
    el.widget:Open()
end

function Backend.selectTab(el)
    local widget = el.widget
    widget.SetSelected = true         -- "select now"; left on it would pin the tab
    local at = now() + 250
    local handler
    handler = Ext.Events.Tick:Subscribe(function()
        if now() < at then return end
        Ext.Events.Tick:Unsubscribe(handler)
        pcall(function() widget.SetSelected = false end)
    end)
end

function Backend.expand(el, on)
    el.widget:SetOpen(on and true or false, "Always")
end

function Backend.addAnchor(el)
    return el.widget:AddDummy(0, 0)
end

function Backend.destroy(el)
    -- a tooltip is part of the element it belongs to and goes with it
    if el.tag ~= "tooltip" then pcall(function() el.widget:Destroy() end) end
end

-- --- Geometry -----------------------------------------------------------------------------

function Backend.rect(el) return el.widget.LastRect end
function Backend.scroll(el) return el.widget.LastScroll end
function Backend.anchorRect(el) return el.anchor.LastRect end
function Backend.anchorAvail(el) return el.anchor.LastAvail end

-- Positions are given to IMGUI in the coordinates of the window the element is drawn in.
function Backend.place(el, x, y)
    el.widget.AbsolutePosition = { x, y }
end

function Backend.sizeAnchor(el, width, height)
    el.anchor.Width, el.anchor.Height = width, height
end

-- --- Canvas -------------------------------------------------------------------------------
-- The commands are described in Dom.lua (Element:Draw) and docs/weave.md.

local function point(c, i) return { c[i], c[i + 1] } end

local DRAW = {       -- command -> function(canvas, command, colour)
    line = function(w, c, color) w:AddLine(point(c, 2), point(c, 4), color(c[6]), c[7] or 1) end,
    rect = function(w, c, color) w:AddRect(point(c, 2), point(c, 4), color(c[6]), c[7] or 0, c[8] or 1) end,
    fill = function(w, c, color) w:AddRectFilled(point(c, 2), point(c, 4), color(c[6]), c[7] or 0) end,
    gradient = function(w, c, color)
        w:AddRectGradient(point(c, 2), point(c, 4), color(c[6]), color(c[7]), c[8] or 180, c[9] or 0)
    end,
    circle = function(w, c, color) w:AddCircle(point(c, 2), c[4], color(c[5]), c[6] or 1) end,
    disc = function(w, c, color) w:AddCircleFilled(point(c, 2), c[4], color(c[5])) end,
    triangle = function(w, c, color)
        if c[9] == "fill" then return w:AddTriangleFilled(point(c, 2), point(c, 4), point(c, 6), color(c[8])) end
        w:AddTriangle(point(c, 2), point(c, 4), point(c, 6), color(c[8]), c[9] or 1)
    end,
    bezier = function(w, c, color)
        w:AddBezier(point(c, 2), point(c, 4), point(c, 6), point(c, 8), color(c[10]), c[11] or 1)
    end,
    polyline = function(w, c, color) w:AddPolyline(c[2], color(c[3]), c[4] or false, c[5] or 1) end,
    text = function(w, c, color) w:AddText(point(c, 2), color(c[4]), tostring(c[5])) end,
    image = function(w, c, color, where)    -- { "image", src, x1, y1, x2, y2[, tint] }
        if not usable(tostring(c[2]), where) then return end
        w:AddImage(c[2], point(c, 3), point(c, 5), c[7] and color(c[7]) or { 1, 1, 1, 1 })
    end,
}

function Backend.draw(el, commands, color, where)
    el.widget:Clear()
    for i, command in ipairs(commands) do
        local draw = DRAW[command[1]]
        local ok, problem = pcall(draw or error, el.widget, command, color, where)
        if not draw then
            report(where, "command " .. i .. ": no such command: " .. tostring(command[1]))
        elseif not ok then
            report(where, "command " .. i .. " (" .. command[1] .. "): " .. tostring(problem):gsub("\n.*", ""))
        end
    end
end

-- --- Building -----------------------------------------------------------------------------

-- tag -> function(el, parentWidget, id) -> widget. `el.suffix` is what follows the visible
-- label to keep IMGUI's identifier unique and stable.
local BUILD = {}

local function leaf(method, ...)
    local extra = { ... }
    return function(el, parent, id)
        local args = {}
        for i, name in ipairs(extra) do args[i] = el.attrs[name] end
        local widget = parent[method](parent, el.label, table.unpack(args, 1, #extra))
        widget.IDContext = id
        return widget
    end
end

local function container(method)
    return function(el, parent, id)
        el.suffix = "###" .. id
        return parent[method](parent, el.label .. el.suffix)
    end
end

local function anonymous(method)
    return function(el, parent, id)
        el.rawLabel = nil
        return parent[method](parent, id)
    end
end

local function numeric(float, integer)
    return function(el, parent, id)
        local a = el.attrs
        local method = a.int and integer or float
        local value, low, high = tonumber(a.value) or 0, tonumber(a.min) or 0, tonumber(a.max) or (a.int and 100 or 1)
        local widget = parent[method](parent, el.label, value, low, high)
        widget.IDContext = id
        -- how much a pixel of dragging changes the value (<drag> only)
        if tonumber(a.speed) and el.tag == "drag" then widget.Speed = tonumber(a.speed) end
        return widget
    end
end

BUILD.text = leaf("AddText")
BUILD.bullet = leaf("AddBulletText")
BUILD.link = leaf("AddTextLink")
BUILD.heading = leaf("AddSeparatorText")
BUILD.button = leaf("AddButton")
BUILD.selectable = leaf("AddSelectable")
BUILD.combo = leaf("AddCombo")
BUILD.slider = numeric("AddSlider", "AddSliderInt")
BUILD.drag = numeric("AddDrag", "AddDragInt")
BUILD.group = anonymous("AddGroup")
BUILD.row = anonymous("AddGroup")
BUILD.tabs = anonymous("AddTabBar")
BUILD.child = anonymous("AddChildWindow")
BUILD.popup = anonymous("AddPopup")
BUILD.tab = container("AddTabItem")
BUILD.header = container("AddCollapsingHeader")
BUILD.tree = container("AddTree")
BUILD.menu = container("AddMenu")

function BUILD.separator(el, parent) el.rawLabel = nil return parent:AddSeparator() end
function BUILD.spacing(el, parent) el.rawLabel = nil return parent:AddSpacing() end
function BUILD.newline(el, parent) el.rawLabel = nil return parent:AddNewLine() end
function BUILD.dummy(el, parent)
    el.rawLabel = nil
    return parent:AddDummy(tonumber(el.attrs.width) or 0, tonumber(el.attrs.height) or 0)
end
function BUILD.checkbox(el, parent, id)
    local widget = parent:AddCheckbox(el.label, el.attrs.checked == "true")
    widget.IDContext = id
    return widget
end
function BUILD.radio(el, parent, id)
    local widget = parent:AddRadioButton(el.label, el.attrs.checked == "true")
    widget.IDContext = id
    return widget
end
function BUILD.input(el, parent, id)
    local a = el.attrs
    local widget = parent:AddInputText(el.label, a.value or "")
    widget.IDContext = id
    if a.hint then widget.Hint = a.hint end
    if a.multiline then widget.Multiline = true end
    if a.readonly then widget.ReadOnly = true end
    if a.enter then widget.EnterReturnsTrue = true end      -- "change" is raised by Enter only
    if a.password then widget.Password = true end
    return widget
end
function BUILD.number(el, parent, id)
    local a = el.attrs
    local widget
    if a.int then widget = parent:AddInputInt(el.label, tonumber(a.value) or 0)
    else widget = parent:AddInputScalar(el.label, tonumber(a.value) or 0) end
    widget.IDContext = id
    -- with a step the field has - and + buttons that change it by that much
    if tonumber(a.step) then widget.Step = a.int and math.floor(tonumber(a.step)) or tonumber(a.step) end
    return widget
end
function BUILD.color(el, parent, id)
    local widget = parent:AddColorEdit(el.label)
    widget.IDContext = id
    return widget
end
function BUILD.picker(el, parent, id)
    local widget = parent:AddColorPicker(el.label)
    widget.IDContext = id
    return widget
end
function BUILD.progress(el, parent)
    el.rawLabel = nil
    local widget = parent:AddProgressBar()
    widget.Value = tonumber(el.attrs.value) or 0
    if el.attrs.overlay then widget.Overlay = el.attrs.overlay end
    return widget
end
function BUILD.image(el, parent)
    el.rawLabel = nil
    local a = el.attrs
    local width, height = tonumber(a.width), tonumber(a.height)
    usable(a.src or "", el.doc.name)
    if not (width and height) then         -- its own size, or the given side in proportion
        local size = Ext.IMGUI.GetImageSize(a.src)
        if size and size[1] > 0 and size[2] > 0 then
            if width then height = width * size[2] / size[1]
            elseif height then width = height * size[1] / size[2]
            else width, height = size[1], size[2] end
        end
    end
    return parent:AddImage(a.src, { width or 32, height or width or 32 })
end
function BUILD.icon(el, parent)
    el.rawLabel = nil
    local size = tonumber(el.attrs.size) or 32
    return parent:AddIcon(el.attrs.src, { size, size })
end
function BUILD.canvas(el, parent)
    el.rawLabel = nil
    return parent:AddCanvas(tonumber(el.attrs.width) or 100, tonumber(el.attrs.height) or 100)
end
-- What #if and #for build into: IMGUI can only append, so each gets a place of its own.
function BUILD.fragment(el, parent, id)
    el.rawLabel, el.fragment = nil, true
    return parent:AddGroup(id)
end
function BUILD.tooltip(el)
    el.rawLabel = nil
    return el.parent.widget:Tooltip()
end
function BUILD.menubar(el)
    el.rawLabel = nil
    return el.doc.window:AddMainMenu()
end
function BUILD.item(el, parent)
    return parent:AddItem(el.label, el.attrs.shortcut)
end
BUILD["table"] = function(el, parent, id)
    el.rawLabel = nil
    local headers = el.attrs.headers and splitList(el.attrs.headers) or nil
    local widget = parent:AddTable(id, tonumber(el.attrs.columns) or (headers and #headers) or 1)
    if headers then
        for _, name in ipairs(headers) do widget:AddColumn(name) end
        widget.ShowHeader = true
    end
    -- the width of the contents when the table scrolls sideways (flags="ScrollX")
    if tonumber(el.attrs["inner-width"]) then widget.InnerWidth = tonumber(el.attrs["inner-width"]) end
    return widget
end
function BUILD.tr(el, parent) el.rawLabel = nil return parent:AddRow() end
function BUILD.td(el, parent) el.rawLabel = nil return parent:AddCell() end

function Backend.registerTag(name, build)
    BUILD[name] = build
end

function Backend.hasTag(tag) return BUILD[tag] ~= nil end

function Backend.create(el, parent, id)
    local widget = BUILD[el.tag](el, parent, id)
    if widget and el.attrs.border and el.tag == "child" then widget.Border = true end
    return widget
end
