-- Weave, documents: a component (Component.lua) with its style sheets (Css.lua) becomes a live
-- window whose elements Lua can find, change and listen to - a browser in miniature.
--
--   local doc = Weave.Mount("settings", { props = { ... } })
--   doc:Open()   doc.state.count = 3   doc:Get("#theme"):On("change", fn)
--
-- Files are looked for as  <Script Extender>/Gwynerva_Weave/ui/<name>.weave  (the user's own
-- copies win) and then under every root added with Weave.AddRoot. They are read again
-- twice a second: a saved <style> restyles the window in place, a saved <template> or
-- <script> builds it anew with its state kept. See docs/weave.md for the whole language.
--
-- Nothing here looks into a widget: what is drawn belongs to the renderer, Weave.Backend
-- (BackendImgui.lua lists what a renderer has to do).

Weave = Weave or {}
local UI = Weave
local Backend = UI.Backend

local USER_DIR = "Gwynerva_Weave/ui/"
local POLL_MS = 400
local now = Ext.Utils.MonotonicTime

local roots = { { prefix = "Mods/Gwynerva_Weave/ScriptExtender/Lua/ui/", context = "data" } }
local documents, documentOrder = {}, {}
local defined = {}            -- file name -> text, for templates and sheets given as strings
local skins = {}              -- hand-built widgets styled by the base sheets
local restyleListeners = {}
local state = { theme = "gate", font = "" }
local base = { files = { "theme.css", "utilities.css", "user.css" }, texts = {}, sheet = UI.ParseCss("") }

--- The kit's version. It grows when something is added; nothing that worked is taken away or
--- changed in meaning once mods depend on it. A mod that needs a newer kit:
---   if (Weave.Version or 0) < 4 then ... end
--- 4: text sizes in font units (font-size, em, rem, text-[n]), dblclick, expand / collapse, :open,
---    speed, step, inner-width; GateKit's Script Extender build on the v34 base required.
UI.Version = 4

local frameListeners = {}     -- fn(doc, time) for every open document, every tick (Layout.lua)
--- Internal: fn(doc, time) on every tick for every open document.
function UI.OnFrame(fn) frameListeners[#frameListeners + 1] = fn end

local Element = {}
Element.__index = Element
local Document = {}
Document.__index = Document

-- --- Sources ------------------------------------------------------------------------------

--- Adds a folder to look for templates and sheets in: `prefix` ends with "/", `context` is
--- "data" (inside the game's Data, so inside a mod's pak) or "user" (the Script Extender
--- folder of the user profile). Later roots are searched first.
function UI.AddRoot(prefix, context)
    table.insert(roots, 1, { prefix = prefix, context = context or "data" })
end

--- Gives a template or a sheet as text instead of a file: Weave.Define("hello.xml", "<window>...").
function UI.Define(file, text)
    defined[file] = text
end

--- The text of a kit file and where it came from, or nil.
function UI.Source(file)
    local ok, text = pcall(Ext.IO.LoadFile, USER_DIR .. file)
    if ok and text and text ~= "" then return text, "user" end
    if defined[file] then return defined[file], "defined" end
    for _, root in ipairs(roots) do
        if root.context == "user" then
            ok, text = pcall(Ext.IO.LoadFile, root.prefix .. file)
        else
            ok, text = pcall(Ext.IO.LoadFile, root.prefix .. file, root.context)
        end
        if ok and text and text ~= "" then return text, root.prefix end
    end
    return nil
end

--- Copies a built-in file into the user's ui folder, where it can be edited while the game
--- runs (the copy wins over the original until it is deleted). Returns the path written.
function UI.Eject(file)
    local text, origin = UI.Source(file)
    if not text then error("no kit file " .. tostring(file)) end
    if origin ~= "user" then Ext.IO.SaveFile(USER_DIR .. file, text) end
    return "Script Extender/" .. USER_DIR .. file
end

-- An error text without the part of the traceback that belongs to Weave itself (everything
-- from the xpcall that ran the user's code downwards).
local function shortTrace(text)
    text = tostring(text)
    local cut = text:find("\n[^\n]*%[C%+%+ Code%]: in function 'xpcall'")
    return cut and text:sub(1, cut - 1) or text
end

local reported = {}
local function report(where, message)
    local line = "[Weave] " .. where .. ": " .. message
    if reported[line] then return end
    reported[line] = true
    if _PW then _PW(line) else print(line) end
end

--- What can differ between set-ups the kit runs on: { files } - pictures from files of a mod,
--- which the DX11 renderer draws and Vulkan does not yet.
function UI.Has()
    return Backend.capabilities()
end

-- Weave needs GateKit's build of Script Extender; on any other it builds nothing and says so
-- once, here, when the kit loads (and Mount says it once more for the first window asked for).
local unsupported = Backend.check()
if unsupported then report("Script Extender", "Weave " .. unsupported .. ": no window is built") end

-- --- Variables and themes -----------------------------------------------------------------

local function themeNames(extra)
    local seen, out = {}, {}
    for _, sheet in ipairs({ base.sheet, extra }) do
        for _, name in ipairs(sheet and sheet.themeOrder or {}) do
            if not seen[name] then seen[name] = true out[#out + 1] = name end
        end
    end
    return out
end

local function variables(sheet)
    local theme = state.theme
    if not base.sheet.themes[theme] and not (sheet and sheet.themes[theme]) then
        theme = base.sheet.themeOrder[1]
    end
    local layers = { sheet and sheet.themes[theme], base.sheet.themes[theme], sheet and sheet.root, base.sheet.root }
    return function(name)
        for i = 1, 4 do
            local layer = layers[i]
            if layer and layer[name] ~= nil then return layer[name] end
        end
        return nil
    end
end

--- The themes the loaded sheets define, in order.
function UI.Themes()
    return themeNames()
end

--- A theme variable's value as written ("--accent" -> "#c8a45c"), variables inside it resolved.
function UI.Var(name)
    local vars = variables()
    local value = vars(name)
    return value and UI.Substitute(value, vars) or nil
end

--- A colour for widget:SetColor from a variable name ("--log-error") or a colour as written.
function UI.Color(value, fallback)
    if tostring(value):sub(1, 2) == "--" then value = UI.Var(value) end
    return (value and UI.ParseColor(UI.Substitute(value, variables()))) or fallback
end

-- --- Styling ------------------------------------------------------------------------------
-- A style is resolved into ops (Weave.Resolve). What an op means to the kit itself is kept
-- here, on the element, for Layout.lua and the rest to read; the renderer gets only what it
-- has to draw.
--   el.layout               what the layout works from: display, margin-top, flex-grow, width ...
--   el.align                its own place in the width it is given, 0 .. 1 (shifted every frame)
--   el.size, el.constraints a window's size and the limits of it
--   el.transition           how long a change of colour or number takes, in seconds
--   el.styleVisible         false under `display: none`

local function sameValue(a, b)
    if type(a) ~= "table" or type(b) ~= "table" then return a == b end
    for i = 1, math.max(#a, #b) do
        if a[i] ~= b[i] then return false end
    end
    return true
end

-- The parent as the author sees it: what #if and #for build into does not count.
local function authorParent(el)
    local parent = el.parent
    while parent and parent.fragment do parent = parent.parent end
    return parent
end

-- The flex / grid container this element is an item of, if its parent's style makes it one.
local function layoutParent(el)
    local parent = authorParent(el)
    return parent and parent.layout and parent.layout.display and parent or nil
end

-- Something the layout works from changed: the element's own and its parent's are looked at again.
local function relayout(el)
    el.layoutVersion = (el.layoutVersion or 0) + 1
    local parent = authorParent(el)
    if parent then parent.layoutVersion = (parent.layoutVersion or 0) + 1 end
end

-- Shown unless the style (display: none), #show or :visible / el:Hide() says otherwise.
local function updateVisible(el)
    if not el.widget or el.tag == "window" then return end
    local visible = el.styleVisible ~= false and el.shown ~= false and el.hidden ~= true
    if el.visibleNow ~= visible then
        el.visibleNow = visible
        Backend.setVisible(el, visible)
        relayout(el)
    end
end

-- The ops of kind "prop" the kit keeps for itself: key -> { apply = fn(el, value, op),
-- remove = fn(el, op) -> false when the window has to be built anew }.
local OWN = {}

OWN.transition = {
    apply = function(el, value) el.transition = value end,
    remove = function(el) el.transition = nil end,
}

OWN.visible = {
    apply = function(el, value)
        el.styleVisible = value
        updateVisible(el)
    end,
    remove = function(el)
        el.styleVisible = nil
        updateVisible(el)
    end,
}

OWN.align = {
    apply = function(el, value)
        el.align = value
        if el.doc then el.doc.aligned[el] = true end
    end,
    remove = function(el)
        el.align = nil
        if el.doc then el.doc.aligned[el] = nil end
        Backend.align(el, nil)
    end,
}

-- A window's size is the document's to set (Document:_resize); any other element's is drawn.
for key, index in pairs({ width = 1, height = 2 }) do
    OWN[key] = {
        apply = function(el, value, op)
            if el.tag ~= "window" then return Backend.applyOp(el, op) end
            el.size = el.size or {}
            el.size[index] = value > 0 and value or nil
            if el.doc and el.doc.placed then el.doc:_resize() end
        end,
        remove = function(el, op)
            if el.tag ~= "window" then return Backend.unapply(el, op) end
            el.size[index] = nil
            el.doc:_resize()
        end,
    }
end

-- Of a window. (On an item of a flex / grid container they are the layout's: see `settle`.)
for _, key in ipairs({ "min-width", "min-height", "max-width", "max-height" }) do
    OWN[key] = {
        apply = function(el, value)
            el.constraints = el.constraints or {}
            el.constraints[key] = value or nil
            el.constraintsDirty = true
        end,
        remove = function(el)
            el.constraints[key] = nil
            el.constraintsDirty = true
        end,
    }
end

-- "layout:<name>": a number, a word or a length for Layout.lua, kept in el.layout.
local LAYOUT = {}
function LAYOUT.apply(el, value, op)
    local name = op.key:sub(8)
    el.layout = el.layout or {}
    el.layout[name] = value or nil
    if name == "columns" then el.layout.tracks = value and op.tracks or nil end
    if name == "container" then el.isContainer = value end
    relayout(el)
    if name == "display" and el.doc then
        el.doc.layouts[el] = value and true or nil
        el.doc.layoutOrder = nil
        el.layoutSettle, el.layoutNext = 4, 0
        -- a container is measured from a marker built with it: it has none if it only became one now
        if value and not el.anchor then el.doc.pendingRebuild = true end
        -- and what it placed stays placed until built anew (the positions, widths and the
        -- marker's room the layout gave are many things to take back; a rebuild does it at once)
        if not value and el.anchor and el.layoutSeen then el.doc.pendingRebuild = true end
    end
end
function LAYOUT.remove(el, op)
    if op.key == "layout:display" and el.anchor and el.layoutSeen then return false end
    LAYOUT.apply(el, false, op)
end

local function handlerOf(op)
    if op.kind ~= "prop" then return nil end
    return OWN[op.key] or (op.key:sub(1, 7) == "layout:" and LAYOUT) or nil
end

-- `old`: what the same key was set to before, when there was something - a transition starts there.
local function applyOp(el, op, old)
    local own = handlerOf(op)
    if own then return own.apply(el, op.value, op) end
    if op.kind == "custom" then return op.apply(el, op.value) end
    Backend.applyOp(el, op, old, old and (el.transition or 0) > 0 and el.transition or nil)
end

-- Takes back what a rule that no longer applies had set. False: the window has to be built anew.
local function unapplyOp(el, op)
    local own = handlerOf(op)
    if own then return own.remove(el, op) ~= false end
    if op.kind == "custom" then
        if not op.remove then return false end
        op.remove(el)
        return true
    end
    return Backend.unapply(el, op)
end

-- The keys of a computed style looked at below, spelled out once.
local PADDING_KEYS, SIZE_KEYS = {}, {}
for i, side in ipairs({ "top", "right", "bottom", "left" }) do PADDING_KEYS[i] = "prop:layout:padding-" .. side end
for i, name in ipairs({ "width", "height", "min-width", "min-height", "max-width", "max-height" }) do
    SIZE_KEYS[i] = { name = name, own = "prop:" .. name, layout = "prop:layout:" .. name, limit = i > 2 }
end

local function describeTag(el)
    return "<" .. el.tag .. (el.id and (" id=\"" .. el.id .. "\"") or "") .. "> "
end

local function marginOf(computed, key)
    local op = computed[key]
    return op and op.value or 0
end

local FONT_KEY = "prop:font-size"

-- The text size an element has from the elements around it: the nearest one with a size of
-- its own (parents are styled before their children), else the overlay's.
local function inheritedFontSize(el)
    local up = el.parent
    while up do
        local op = up.applied and up.applied[FONT_KEY]
        if op and type(op.value) == "number" then return op.value end
        up = up.parent
    end
    return UI.BaseFontSize(state.font)
end

-- What a few properties mean depends on where the element stands, which only the whole
-- computed style and the tree tell:
--   padding      the box's on a flex / grid container, IMGUI's FramePadding elsewhere;
--   sizes        an item's are the layout's to set (so are percentages), anything else's are drawn;
--   margins      an item's are the layout's; in IMGUI's own flow the top and the left one move
--                the element (`offset`) and `auto` on the left pushes it right or centres it (`align`);
--   text size    a window's is the overlay's unless its style gives one; `em` is of the parent's.
local function settle(el, computed, where)
    local display = computed["prop:layout:display"]
    local container = display ~= nil and display.value ~= false
    local parent = layoutParent(el)
    local placed = parent ~= nil and parent.anchor ~= nil       -- by the kit, where it can read geometry

    local frame = computed["var:FramePadding"]
    if container then
        if frame and frame.box then computed["var:FramePadding"] = nil end
    elseif computed[PADDING_KEYS[1]] or computed[PADDING_KEYS[2]] or computed[PADDING_KEYS[3]] or computed[PADDING_KEYS[4]] then
        for _, key in ipairs(PADDING_KEYS) do computed[key] = nil end
    end

    for _, size in ipairs(el.tag ~= "window" and SIZE_KEYS or {}) do
        local op = computed[size.own]
        if op then
            local percent, problem = type(op.value) == "table", nil
            if placed or container then
                computed[size.layout] = { kind = "prop", key = "layout:" .. size.name, value = op.value }
            end
            if placed then
                computed[size.own] = nil
            elseif size.limit then
                computed[size.own] = nil
                if not (container or parent) then
                    problem = "only a window or an element of a flex / grid container has " .. size.name
                end
            elseif percent and size.name == "width" and op.value[1] == 0 and op.value[2] == 100 and not container then
                computed[size.own] = { kind = "prop", key = "width", value = -1 }      -- all the room there is: IMGUI knows that one
            elseif percent then
                computed[size.own] = nil
                if not (container or parent) then problem = size.name .. ": % needs a flex or grid container around the element" end
            end
            if problem then report(where, describeTag(el) .. problem) end
        end
    end

    if not placed then
        local top, left = marginOf(computed, "prop:layout:margin-top"), marginOf(computed, "prop:layout:margin-left")
        if type(top) == "table" or type(left) == "table" then
            if not parent then report(where, describeTag(el) .. "margin: % needs a flex or grid container around the element") end
        elseif left == "auto" and not computed["prop:align"] then
            local centred = marginOf(computed, "prop:layout:margin-right") == "auto"
            computed["prop:align"] = { kind = "prop", key = "align", value = centred and 0.5 or 1 }
        end
        local x, y = type(left) == "number" and left or 0, type(top) == "number" and top or 0
        if (x ~= 0 or y ~= 0) and not computed["prop:offset"] then
            computed["prop:offset"] = { kind = "prop", key = "offset", value = { x, y } }
        end
    end

    -- text size: a window has the overlay's unless its style says otherwise; `em` is a multiple
    -- of the size the element would have without it
    local font = computed[FONT_KEY]
    if not font and el.doc and el.doc.root == el and state.font ~= "" then
        computed[FONT_KEY] = { kind = "prop", key = "font-size", value = UI.BaseFontSize(state.font), id = FONT_KEY }
    elseif font and type(font.value) == "table" then
        computed[FONT_KEY] = { kind = "prop", key = "font-size", value = font.value.em * inheritedFontSize(el), id = FONT_KEY }
    end
end

-- Applies the sheets to one element. Returns true when something that was set could not be
-- taken back in place (a flex / grid container that stops being one, a property added from
-- outside without `remove`): the caller has to build the window anew.
local function styleElement(el, sheets, vars, where)
    if not el.widget then return false end
    local computed = {}
    for _, declaration in ipairs(UI.Cascade(el, sheets, el.inline)) do
        local value = UI.Substitute(declaration.value, vars)
        local ops, problem, viewport = UI.Resolve(el.tag, declaration.name, value, declaration.pseudo, state.font)
        if viewport and el.doc then el.doc.viewportUnits = true end     -- to be worked out again when the picture changes
        if ops then
            for _, op in ipairs(ops) do computed[op.id] = op end
        else
            report(where, describeTag(el) .. declaration.name .. ": " .. tostring(problem))
        end
    end
    settle(el, computed, where)
    local stale = false
    for key, op in pairs(computed) do
        local old = el.applied[key]
        if not old or not sameValue(old.value, op.value) then
            local ok, problem = pcall(applyOp, el, op, old)
            if not ok then
                report(where, "<" .. el.tag .. "> " .. op.key .. ": " .. tostring(problem):gsub("\n.*", ""))
                computed[key] = nil
            end
        end
    end
    for key, old in pairs(el.applied) do
        if not computed[key] then
            local ok, undone = pcall(unapplyOp, el, old)
            if not ok or not undone then stale = true end
        end
    end
    el.applied = computed
    local ok, problem = pcall(Backend.decorate, el, computed)
    if not ok then report(where, "<" .. el.tag .. "> decoration: " .. tostring(problem):gsub("\n.*", "")) end
    if el.constraintsDirty then
        el.constraintsDirty = false
        local c = el.constraints
        local any = c["min-width"] or c["min-height"] or c["max-width"] or c["max-height"]
        ok, problem = pcall(Backend.constrain, el, any and { c["min-width"] or 0, c["min-height"] or 0 } or nil,
            any and { c["max-width"] or 100000, c["max-height"] or 100000 } or nil)
        if not ok then report(where, "size constraints: " .. tostring(problem):gsub("\n.*", "")) end
    end
    return stale
end

-- --- Lists that change ----------------------------------------------------------------------
-- :first-child, :nth-child(odd), :empty ... depend on the element's siblings, so when a list
-- changes its other members may need their style worked out again. That is done only for
-- documents whose sheets (or utility classes) use such a selector, once per change of a list.

local function structural(doc)
    for _, sheet in ipairs(doc.sheets or {}) do
        if sheet.structural then return true end
    end
    return UI.UtilitySheet ~= nil and UI.UtilitySheet.structural == true
end

-- The children of `parent` (maybe what #if / #for build into) are not what they were.
local function childrenChanged(doc, parent)
    while parent.fragment and parent.parent do parent = parent.parent end
    parent.flat = nil                                   -- Css.lua's list of them
    parent.layoutVersion = (parent.layoutVersion or 0) + 1
    if not doc.building and structural(doc) then
        doc.unsettled = doc.unsettled or {}
        doc.unsettled[parent] = true
    end
end

-- Styles again the children of every element whose list changed (and the element: :empty).
local function settleLists(doc)
    local parents = doc.unsettled
    if not parents then return end
    doc.unsettled = nil
    for parent in pairs(parents) do
        local covered, up = false, authorParent(parent)
        while up and not covered do                     -- inside a list that is styled again anyway
            covered, up = parents[up] == true, authorParent(up)
        end
        if not covered and not parent.dead and parent.widget then
            if styleElement(parent, doc.sheets, doc.vars, doc.name) then doc.pendingRebuild = true end
            for _, child in ipairs(UI.ChildrenOf(parent)) do child:_restyle() end
        end
    end
end

-- Styles what was just built into `parent`, from its child number `from` on.
local function styleNew(doc, parent, from)
    local holder = parent
    while holder.fragment and holder.parent do holder = holder.parent end
    if not (doc.unsettled and doc.unsettled[holder]) then      -- else they are styled with their siblings
        for i = from, #parent.children do parent.children[i]:_restyle() end
    end
    settleLists(doc)
end

--- Styles a widget built by hand with the base sheets (theme.css, utilities.css, user.css), as if it were
--- the element <tag id= class=> - and again whenever the theme or those sheets change.
--- The console uses this: its window is Lua, its look is the theme's.
function UI.Skin(widget, tag, id, classes)
    local el = { widget = widget, tag = tag, id = id, classes = {}, applied = {}, attrs = {} }
    for class in (classes or ""):gmatch("%S+") do el.classes[class] = true end
    skins[#skins + 1] = el
    pcall(styleElement, el, { base.sheet }, variables(), "skin " .. tag .. (id and ("#" .. id) or ""))
    return el
end

--- Calls fn() after the theme, the text size or the base sheets changed.
function UI.OnRestyle(fn)
    restyleListeners[#restyleListeners + 1] = fn
end

-- --- Values -------------------------------------------------------------------------------

local function labelText(el)
    return el.attrs.label or el.text or ""
end

local function splitList(text)
    local out = {}
    for part in (text .. "|"):gmatch("(.-)|") do out[#out + 1] = (part:match("^%s*(.-)%s*$")) end
    return out
end

-- --- Elements -----------------------------------------------------------------------------

local function hook(el, event)
    if el.hooked[event] or not el.widget then return end
    if not Backend.hasEvent(event) then error("no such event: " .. tostring(event), 3) end
    el.hooked[event] = true
    local ok = Backend.listen(el, event, function() el:Fire(event, true) end)
    if not ok then report(el.doc.name, "<" .. el.tag .. "> cannot raise \"" .. event .. "\"") end
end

--- Listens to an event on the element: click, rightclick, change, hover, leave, focus, blur,
--- and on a window: close. fn(event) gets { type, target, value, doc, user }.
function Element:On(event, fn, fromTemplate)
    self.handlers[event] = self.handlers[event] or {}
    table.insert(self.handlers[event], fn)
    if not fromTemplate then
        self.userHandlers = self.userHandlers or {}
        self.userHandlers[event] = self.userHandlers[event] or {}
        table.insert(self.userHandlers[event], fn)
    end
    hook(self, event)
    return self
end

--- Raises an event as if the user had caused it (a test clicks a button this way).
function Element:Fire(event, fromUser)
    local doc = self.doc
    local value = self:Value()
    local e = { type = event, target = self, value = value, doc = doc, user = fromUser == true }
    local function call(fn, what)
        local ok, problem = xpcall(fn, debug.traceback, e)
        if not ok then
            reported = {}
            report(doc.name, what .. ": " .. tostring(shortTrace(problem)))
        end
    end
    for _, fn in ipairs(self.handlers[event] or {}) do call(fn, event .. " listener") end
    UI.flush()                 -- what the handlers changed shows at once, not a tick later
    return e
end

--- The element's value: a checkbox's state, an input's text, a combo's chosen option, a
--- slider's number. For the rest: the label.
function Element:Value()
    if Backend.hasValue(self.tag) and self.widget then
        local ok, value = pcall(Backend.getValue, self)
        if ok then return value end
        return nil
    end
    return self.label
end

function Element:SetValue(value)
    if Backend.hasValue(self.tag) and self.widget then Backend.setValue(self, value) else self:SetText(value) end
    return self
end

--- Changes what the element says: a text's text, a button's caption, a window's title.
function Element:SetText(text)
    text = tostring(text == nil and "" or text)
    self.rawLabel = text
    self:_relabel()
    return self
end

function Element:_relabel()
    if not self.widget or not self.rawLabel then return end
    local shown = self.rawLabel
    if shown == self.label then return end
    self.label = shown
    Backend.setLabel(self, shown)
end

--- Replaces a combo's options: { "a", "b" } or { { value = , label = } }.
function Element:SetOptions(list)
    Backend.setOptions(self, list)
    return self
end

function Element:Show(visible)
    self.hidden = visible == false
    updateVisible(self)
    return self
end

function Element:Hide() return self:Show(false) end

function Element:HasClass(name) return self.classes[name] == true end

function Element:_restyle()
    local doc = self.doc
    if self == doc.root then doc.viewportUnits = false end      -- found out again below
    local function visit(el)
        if styleElement(el, doc.sheets, doc.vars, doc.name) then doc.pendingRebuild = true end
        if el.commands then el:_draw() end
        for _, child in ipairs(el.children) do visit(child) end
    end
    visit(self)
end

--- Classes drive the style sheet, as in a browser: el:AddClass("active").
function Element:AddClass(name)
    if not self.classes[name] then self.classes[name] = true self.classesChanged = true self:_restyle() end
    return self
end

function Element:RemoveClass(name)
    if self.classes[name] then self.classes[name] = nil self.classesChanged = true self:_restyle() end
    return self
end

function Element:ToggleClass(name, on)
    if on == nil then on = not self.classes[name] end
    if on then return self:AddClass(name) end
    return self:RemoveClass(name)
end

--- Sets inline style: el:Style("color", "#ff8080") or el:Style("color: red; font: +1").
function Element:Style(name, value)
    local declarations = value ~= nil and { { name = name, value = tostring(value) } } or UI.ParseInline(name)
    self.inline = self.inline or {}
    for _, declaration in ipairs(declarations) do
        local replaced = false
        for _, existing in ipairs(self.inline) do
            if existing.name == declaration.name then existing.value = declaration.value replaced = true end
        end
        if not replaced then self.inline[#self.inline + 1] = declaration end
    end
    self:_restyle()
    return self
end

--- Puts the keyboard cursor into the element (an input) or brings a window to the front.
function Element:Focus()
    Backend.focus(self)
    return self
end

--- Opens a <popup>.
function Element:Popup()
    Backend.openPopup(self)
    return self
end

--- Selects a <tab>.
function Element:Select()
    Backend.selectTab(self)
    return self
end

local EXPANDS = { header = true, tree = true }

--- Opens a <header> or a <tree>; Expand(false) closes it.
function Element:Expand(on)
    if not EXPANDS[self.tag] then error("Weave: Expand is for <header> and <tree>", 2) end
    Backend.expand(self, on ~= false)
    return self
end

function Element:Find(selector)
    return self.doc:Query(selector, self)[1]
end

--- Greys the element out and stops it answering (class "disabled" is added for the look).
function Element:Disable(on)
    on = on ~= false
    self.disabled = on
    Backend.setDisabled(self, on)
    return self:ToggleClass("disabled", on)
end

function Element:Enable() return self:Disable(false) end

-- --- Canvas -------------------------------------------------------------------------------
-- <canvas id="chart" width="480" height="120"/> is a rectangle to draw in. The drawing is a
-- list of commands given once - Script Extender repeats it every frame, Lua does not run per
-- frame. Coordinates count from the canvas's top left corner; colours are written as in a
-- style sheet ("#c8a45c", "var(--accent)", "alpha(var(--text), 0.4)") and follow the theme.
--
--   doc:Get("#chart"):Draw({
--       { "gradient", 0, 0, 480, 120, "var(--bg)", "var(--bg-raised)", 180, 6 },
--       { "polyline", { { 10, 100 }, { 80, 60 }, { 150, 80 } }, "var(--ok)", false, 2 },
--       { "text", 12, 8, "var(--text-muted)", "frames per second" },
--   })

--- Replaces what a <canvas> shows. See the list of commands above and in docs/weave.md.
function Element:Draw(commands)
    if self.tag ~= "canvas" then error("Weave: Draw is for <canvas>", 2) end
    self.commands = commands or {}
    return self:_draw()
end

function Element:_draw()
    if not self.widget then return self end
    local doc, where = self.doc, self.doc.name .. " <canvas" .. (self.id and (" id=\"" .. self.id .. "\"") or "") .. ">"
    local function color(value)
        local c = UI.ParseColor(UI.Substitute(tostring(value), doc.vars))
        if not c then error("not a colour: " .. tostring(value), 0) end
        return c
    end
    Backend.draw(self, self.commands, color, where)
    return self
end

-- --- Building -----------------------------------------------------------------------------

local function uid(doc)
    doc.counter = doc.counter + 1
    return "gk_" .. doc.name .. "_" .. doc.counter
end

-- Attributes that are style: <text width="200" font="+1" wrap="on"> is the same as style="...".
local STYLE_ATTRIBUTES = { "width", "height", "font", "wrap", "inline", "offset" }
-- Tags whose own text is their label; the others keep text for their children only.
local NO_INLINE = { window = true, input = true, dummy = true, image = true, icon = true, canvas = true }

local function buildElement(doc, node, parent)
    local el = setmetatable({
        doc = doc, tag = node.tag, id = node.attrs.id, attrs = {}, text = node.text, parent = parent,
        children = {}, classes = {}, handlers = {}, hooked = {}, applied = {}, line = node.line,
    }, Element)
    for name, value in pairs(node.attrs) do
        if not name:match("^[:@#]") then el.attrs[name] = value end
    end
    for class in (el.attrs.class or ""):gmatch("%S+") do el.classes[class] = true end
    local kept = el.id and doc.keptClasses and doc.keptClasses[el.id]
    if kept then el.classes, el.classesChanged = kept, true end
    el.inline = UI.ParseInline(el.attrs.style)
    if not NO_INLINE[el.tag] or el.tag == "input" then
        for _, name in ipairs(STYLE_ATTRIBUTES) do
            if el.attrs[name] and el.tag ~= "dummy" then
                table.insert(el.inline, 1, { name = name, value = el.attrs[name] })
            end
        end
    end
    if el.id then
        if doc.byId[el.id] then report(doc.name, "line " .. node.line .. ": id \"" .. el.id .. "\" is used twice") end
        doc.byId[el.id] = el
    end
    doc.elements[#doc.elements + 1] = el
    return el
end

local buildChildren

-- Whether any rule - whatever the window's size - makes this element a flex or grid container.
local function wantsLayout(doc, el)
    if not doc.sheets then return false end
    for _, declaration in ipairs(UI.Cascade(el, doc.sheets, el.inline, true)) do
        if declaration.name == "display" and UI.LayoutModes[declaration.value:lower()] then return true end
    end
    return false
end

--- Adds a tag: build(el, parentWidget, id) returns the IMGUI widget for <name>. `el.attrs` are
--- the template's attributes, `el.label` its text.
function UI.RegisterTag(name, build)
    Backend.registerTag(name, build)
end

local function realize(doc, el, parentWidget)
    if not Backend.hasTag(el.tag) then
        report(doc.name, "line " .. el.line .. ": unknown tag <" .. el.tag .. ">")
        return false
    end
    el.rawLabel = el.rawLabel or labelText(el)
    el.label = el.rawLabel
    local ok, widget = pcall(Backend.create, el, parentWidget, uid(doc))
    if not ok or not widget then
        report(doc.name, "line " .. el.line .. ": <" .. el.tag .. "> could not be built: " .. tostring(widget))
        return false
    end
    el.widget = widget
    if wantsLayout(doc, el) then
        -- a marker of no size, first inside: where the contents start and how wide they may be
        local placed, anchor = pcall(Backend.addAnchor, el)
        if placed then el.anchor = anchor end
    end
    local a = el.attrs
    if a.tip then Backend.addTip(el, a.tip) end
    if a.flags then
        for flag in a.flags:gmatch("%S+") do
            if not Backend.setFlag(el, flag) then
                report(doc.name, "line " .. el.line .. ": <" .. el.tag .. "> has no flag " .. flag)
            end
        end
    end
    if a.disabled and a.disabled ~= "false" then
        el.disabled = true
        el.classes.disabled = true
        Backend.setDisabled(el, true)
    end
    -- in a <row> everything after the first stands on the same line; a fragment (what #if and
    -- #for build into) is transparent to that
    local holder = el.parent
    if holder then
        local first = #holder.children == 0
        while holder.fragment and holder.parent do
            first = first and holder.parent.children[1] == holder
            holder = holder.parent
        end
        if holder.tag == "row" and not first and parentWidget then Backend.setInline(el, true) end
    end
    if el.tag == "combo" then
        local labels = splitList(a.options or "")
        local values = a.values and splitList(a.values) or labels
        local list = {}
        for i, label in ipairs(labels) do list[i] = { value = values[i], label = label } end
        if a.options then Backend.setOptions(el, list) else el.values = {} end
        if a.value then Backend.setValue(el, a.value) end
    end
    if a.open and EXPANDS[el.tag] then Backend.expand(el, a.open ~= "false") end
    if a.visible == "false" then el.hidden = true end
    updateVisible(el)
    return true
end

buildChildren = function(doc, node, parentEl, context)
    return UI.Internal.buildNodes(doc, node.children, parentEl, context or parentEl.context)
end

--- Builds more elements inside this one from template text: el:Append('<text class="x">hi</text>').
--- Returns the new elements.
function Element:Append(xml)
    local node, problem = UI.ParseXml("<fragment>" .. xml .. "</fragment>")
    if not node then error("Weave: " .. problem, 2) end
    local first = #self.children + 1
    buildChildren(self.doc, node, self, self.context)
    styleNew(self.doc, self, first)
    return { table.unpack(self.children, first) }
end

local function forget(doc, el)
    for _, child in ipairs(el.children) do forget(doc, child) end
    el.children = {}
    if el.id and doc.byId[el.id] == el then doc.byId[el.id] = nil end
    for i = #doc.elements, 1, -1 do
        if doc.elements[i] == el then table.remove(doc.elements, i) break end
    end
    doc.aligned[el] = nil
    doc.layouts[el] = nil
    doc.layoutOrder = nil
    el.dead = true
end

--- Removes the element and everything inside it.
function Element:Remove()
    local parent = self.parent
    if parent then
        for i = #parent.children, 1, -1 do
            if parent.children[i] == self then table.remove(parent.children, i) end
        end
        childrenChanged(self.doc, parent)
    end
    forget(self.doc, self)
    Backend.destroy(self)
    self.widget = nil
end

--- Removes everything inside the element.
function Element:Clear()
    for i = #self.children, 1, -1 do self.children[i]:Remove() end
    return self
end

-- --- Documents ----------------------------------------------------------------------------

local function pairOf(text, fallback)
    local a, b = tostring(text or ""):match("^%s*(-?[%d.]+)%s*,?%s*(-?[%d.]*)")
    if not a then return fallback end
    return { tonumber(a), tonumber(b) or tonumber(a) }
end

-- Gives the window the size its style asks for, or else its template.
function Document:_resize()
    local a, size = self.root.attrs, self.root.size or {}
    local width, height = size[1] or tonumber(a.width), size[2] or tonumber(a.height)
    if width or height then
        self.requested = { width or 0, height or 0 }
        Backend.setSize(self.window, self.requested)
    end
end

function Document:_place()
    local window, a = self.window, self.root.attrs
    local kept = self.geometry
    if kept and kept.size then
        Backend.setSize(window, kept.size)
        self.requested = kept.size
    else
        self:_resize()
    end
    self.placed = true              -- from here on a style that changes the size resizes the window
    if kept and kept.pos then
        Backend.setPos(window, kept.pos)
    elseif a.x or a.y then
        Backend.setPos(window, { tonumber(a.x) or 0, tonumber(a.y) or 0 }, pairOf(a.pivot, { 0, 0 }))
    elseif a.center then
        local viewport = Backend.viewport()
        Backend.setPos(window, { viewport[1] / 2, viewport[2] / 2 }, { 0.5, 0.5 })
    end
end

-- Size and position as setSize / setPos take them, so a rebuilt window stands where it stood.
function Document:_capture()
    local window = self.window
    if not window then return end
    local ok, size, pos = pcall(function() return Backend.readSize(window), Backend.readPos(window) end)
    if not ok or not size or size[1] <= 0 then return end
    if self.requested and self.requested[1] > 0 and not self.scale then
        self.scale = size[1] / self.requested[1]
    end
    local scale = self.scale or 1
    self.geometry = {
        pos = { math.floor(pos[1] + 0.5), math.floor(pos[2] + 0.5) },
        size = self.requested and { math.floor(size[1] / scale + 0.5), math.floor(size[2] / scale + 0.5) } or nil,
    }
end

function Document:_build()
    local def = self.def
    local node = def.template
    self.counter = 0
    self.byId, self.elements = {}, {}
    self.aligned, self.layouts, self.layoutOrder = {}, {}, nil
    self.placed, self.unsettled = false, nil
    self.defs, self.defOrder = { [def.name] = def }, { def }
    self:_sheets()
    if not self.mediaSize then      -- until the window is drawn: the size it asks for
        local size = self.geometry and self.geometry.size
        self.mediaSize = { size and size[1] or tonumber(node.attrs.width) or 0, size and size[2] or tonumber(node.attrs.height) or 0 }
    end
    local root = buildElement(self, node, nil)
    local a = root.attrs
    root.component = def.name
    root.rawLabel = a.title or a.label or self.name
    root.label = root.rawLabel
    local window = Backend.createWindow(root, "gk_" .. self.name)
    root.widget = window
    for flag in (a.flags or ""):gmatch("%S+") do
        if not Backend.setFlag(root, flag) then report(self.name, "<window> has no flag " .. flag) end
    end
    if wantsLayout(self, root) then root.anchor = Backend.addAnchor(root) end
    self.window, self.root = window, root
    self.overlay = a.overlay ~= "false"
    Backend.listen(root, "close", function()
        self.open = false
        root:Fire("close", true)
    end)
    root.hooked.close = true
    self.building = true
    UI.Internal.mountRoot(self, node, root)
    self.building = false
    self:_sheets()                  -- the components it turned out to use bring their styles
    self.pendingRebuild = false
    root:_restyle()
    self.pendingRebuild = false     -- nothing can be stale on a window just built
    self:_place()
    if self.open then Backend.setOpen(window, true) end
end

-- The sheets that apply to this document: the base ones, then every component's in the order
-- the components were first used.
function Document:_sheets()
    local sheets = { base.sheet }
    for _, def in ipairs(self.defOrder or {}) do sheets[#sheets + 1] = def.sheet end
    self.sheets = sheets
    self.sheet = self.def and self.def.sheet or UI.ParseCss("")
    self.vars = variables(self.sheet)
end

function Document:_load()
    local def, problem = UI.Internal.loadComponent(self.file, true)
    if not def then
        report(self.name, problem)
        return false
    end
    if def.template.tag ~= "window" then
        report(self.name, self.file .. ": the root element of a mounted component must be <window>")
        return false
    end
    self.def = def
    return true
end

function Document:_rebuild()
    self:_capture()
    local focus = self.open
    UI.Internal.unmount(self)
    if self.window then Backend.destroyWindow(self.window) end
    local listeners, drawings = {}, {}
    self.keptClasses = {}         -- what Lua did to an element with an id is not lost with its widget
    for _, el in ipairs(self.elements or {}) do
        if el.id and el.classesChanged then self.keptClasses[el.id] = el.classes end
        if el.id and next(el.handlers) then listeners[el.id] = el.userHandlers end
        if el.id and el.commands and not el.boundDraw then drawings[el.id] = el.commands end
    end
    self:_build()
    -- listeners added from Lua belong to ids, not to widgets: they survive a rebuild
    for id, handlers in pairs(listeners) do
        local el = self.byId[id]
        for event, list in pairs(el and handlers or {}) do
            for _, fn in ipairs(list) do el:On(event, fn) end
        end
    end
    for id, commands in pairs(drawings) do      -- and so do drawings
        local el = self.byId[id]
        if el and el.tag == "canvas" then el:Draw(commands) end
    end
    if focus then Backend.setOpen(self.window, true) end
end

function Document:_restyleAll()
    self:_sheets()
    self.root:_restyle()
end

--- Sets a value of the root component's state (or several: doc:Set({ a = 1, b = 2 })) and
--- brings the window up to date at once. The same as doc.state.key = value, then W.flush().
function Document:Set(key, value)
    if type(key) == "table" then
        for k, v in pairs(key) do self.state[k] = v end
    else
        self.state[key] = value
    end
    UI.flush()
    return self
end

--- A value as the template sees it: state, computed or a property of the root component.
function Document:Value(key)
    return UI.untracked(function() return self.instance.scope.get(key) end)
end

--- Calls a function the root component's <script> defined: doc:Call("save", ...).
function Document:Call(name, ...)
    local fn = self.instance.methods[name]
    if not fn then error("Weave: " .. self.name .. " has no function " .. tostring(name), 2) end
    local a, b, c = fn(...)
    UI.flush()
    return a, b, c
end

--- The element with this id ("#name" or "name"), or the first that matches a selector.
function Document:Get(selector)
    local id = selector:match("^#?([%w_%-]+)$")
    if id and self.byId[id] then return self.byId[id] end
    return self:Query(selector)[1]
end

--- Every element matching a selector list, in document order.
function Document:Query(selector, within)
    local selectors = UI.ParseSelectors(selector)
    if not selectors then error("Weave: cannot read the selector \"" .. tostring(selector) .. "\"", 2) end
    local out = {}
    for _, el in ipairs(self.elements) do
        local inside = within == nil
        local up = el.parent
        while not inside and up do
            if up == within then inside = true end
            up = up.parent
        end
        if inside then
            for _, one in ipairs(selectors) do
                if UI.Matches(el, one) then out[#out + 1] = el break end
            end
        end
    end
    return out
end

--- Listens on every element a selector matches now: doc:On("button.danger", "click", fn).
function Document:On(selector, event, fn)
    for _, el in ipairs(self:Query(selector)) do el:On(event, fn) end
    return self
end

function Document:Open(on)
    on = on ~= false
    if not self.window then return false end
    self.open = on
    if Backend.isOpen(self.window) ~= on then
        Backend.setOpen(self.window, on)
        if on then Backend.focus(self.root) end
    end
    return on
end

function Document:Close() return self:Open(false) end
function Document:Toggle() return self:Open(not self:IsOpen()) end

function Document:IsOpen()
    if not self.window then return false end
    local ok, open = pcall(Backend.isOpen, self.window)
    self.open = ok and open or false
    return self.open
end

--- Reads the file again and builds the window anew (what saving the template does).
function Document:Reload()
    reported = {}
    if self:_load() then self:_rebuild() end
    return self
end

function Document:Destroy()
    UI.Internal.unmount(self)
    if self.window then Backend.destroyWindow(self.window) end
    self.window = nil
    documents[self.name] = nil
    for i = #documentOrder, 1, -1 do
        if documentOrder[i] == self then table.remove(documentOrder, i) end
    end
end

--- Mounts the component <name>.weave as a window (closed; call doc:Open()).
---   opts.props   values for the component's properties
---   opts.env     a table the component's <script> sees after its own names (your mod's functions)
---   opts.file    another file than <name>.weave
--- Mounting a name again replaces the earlier window. Returns the document:
--- doc.state (the root component's reactive state), doc:Get("#id"), doc:Open(), ... - or nil
--- when this Script Extender cannot run Weave (said once in the console).
local refusedOnce = false
function UI.Mount(name, opts)
    if unsupported then
        if not refusedOnce then
            refusedOnce = true
            report(tostring(name), "not mounted: Weave " .. unsupported)
        end
        return nil
    end
    opts = opts or {}
    if documents[name] then documents[name]:Destroy() end
    local doc = setmetatable({ name = name, file = opts.file or (name .. ".weave"), props = opts.props or {},
        env = opts.env, open = false, stateStore = {}, byId = {}, elements = {}, aligned = {}, layouts = {} }, Document)
    if doc:_load() then doc:_build() end     -- if not: the file is watched, the window appears when it can be read
    documents[name] = doc
    documentOrder[#documentOrder + 1] = doc
    return doc
end

--- A loaded document by name.
function UI.Document(name) return documents[name] end

function UI.Documents()
    local out = {}
    for i, doc in ipairs(documentOrder) do out[i] = doc.name end
    return out
end

--- Is any kit window that takes part in the overlay open? (The HUD button does not.)
function UI.AnyOpen()
    for _, doc in ipairs(documentOrder) do
        if doc.overlay and doc:IsOpen() then return true end
    end
    return false
end

--- Closes the kit window opened last. Returns whether one was open.
function UI.CloseTop()
    for i = #documentOrder, 1, -1 do
        local doc = documentOrder[i]
        if doc.overlay and doc:IsOpen() and Backend.closeable(doc.window) then
            doc:Close()
            doc.root:Fire("close")
            return true
        end
    end
    return false
end

-- --- Staying reachable --------------------------------------------------------------------
-- The game window can shrink (a resize, another resolution, windowed after fullscreen) and a
-- saved position can be stale. A window whose title bar is outside the picture cannot be
-- dragged back, so every Weave window is checked regularly while it is open (kit documents
-- with the file poll, the console once a second): it is made no larger than the picture and
-- moved back inside it, whole.

--- Brings an IMGUI window back into the picture if it is out of reach. Returns true when it
--- had to be moved or made smaller.
function UI.KeepOnScreen(window)
    local ok, size, pos, open = pcall(function()
        return Backend.readSize(window), Backend.readPos(window), Backend.isOpen(window)
    end)
    if not ok or not open or not size or size[1] <= 0 then return false end
    local viewport = Backend.viewport()
    if viewport[1] < 320 or viewport[2] < 200 then return false end      -- minimised: nothing to fit into
    local w, h = math.min(size[1], viewport[1]), math.min(size[2], viewport[2])
    local x = math.max(0, math.min(pos[1], viewport[1] - w))
    local y = math.max(0, math.min(pos[2], viewport[2] - h))
    local moved = false
    if w < size[1] - 0.5 or h < size[2] - 0.5 then
        Backend.setSize(window, { math.floor(w), math.floor(h) })
        moved = true
    end
    if math.abs(x - pos[1]) > 0.5 or math.abs(y - pos[2]) > 0.5 then
        Backend.setPos(window, { math.floor(x), math.floor(y) })
        moved = true
    end
    return moved
end

--- Puts every kit window back to the place and size its template gives it.
function UI.ResetWindows()
    for _, doc in ipairs(documentOrder) do
        if doc.window and doc.overlay then
            doc.geometry, doc.scale = nil, nil
            doc:_place()
        end
    end
end

-- --- Theme, text size, base sheets --------------------------------------------------------

local function loadBase()
    local sheet = UI.ParseCss("")
    for _, file in ipairs(base.files) do
        local text = UI.Source(file) or ""
        base.texts[file] = text
        UI.ParseCss(text, sheet)
    end
    for _, problem in ipairs(sheet.problems) do report("theme", problem) end
    base.sheet = sheet
end

local function restyleEverything()
    for _, doc in ipairs(documentOrder) do
        if doc.window then doc:_restyleAll() end
    end
    for i = #skins, 1, -1 do
        local el = skins[i]
        local ok = pcall(styleElement, el, { base.sheet }, variables(), "skin " .. el.tag)
        if not ok then table.remove(skins, i) end      -- its widget is gone
    end
    for _, fn in ipairs(restyleListeners) do pcall(fn) end
end

--- Switches the theme (one of Weave.Themes()). Returns the theme now in use.
function UI.SetTheme(name)
    if base.sheet.themes[name] and state.theme ~= name then
        state.theme = name
        restyleEverything()
    end
    return state.theme
end

function UI.Theme() return state.theme end

--- The overlay's text size: "" (Script Extender's own) or one of Weave.Fonts. Every window
--- has it unless its style gives one; `font: +1`, `1.25rem` in a sheet count from it.
function UI.SetFont(name)
    if state.font ~= name then
        state.font = name
        restyleEverything()
    end
    return state.font
end

function UI.Font() return state.font end

-- --- Hot reload ---------------------------------------------------------------------------

local nextPoll = 0
local function poll()
    local baseChanged = false
    for _, file in ipairs(base.files) do
        if (UI.Source(file) or "") ~= base.texts[file] then baseChanged = true end
    end
    if baseChanged then
        reported = {}
        loadBase()
        restyleEverything()
    end
    for _, doc in ipairs(documentOrder) do
        local change = UI.Internal.changed(doc)       -- nil | "style" | "all"
        if change then reported = {} end
        if change == "all" then
            if doc:_load() then doc:_rebuild() end
        elseif change == "style" then
            if doc.window then doc:_restyleAll() end
        end
        if doc.pendingRebuild and doc.def and doc.window then doc:_rebuild() end
        if doc.window and not doc.scale then doc:_capture() end
        if doc.window and doc.overlay and doc.open then UI.KeepOnScreen(doc.window) end
    end
end

Ext.Events.Tick:Subscribe(function()
    local t = now()
    for _, doc in ipairs(documentOrder) do
        if doc.unsettled and doc.window then settleLists(doc) end      -- elements removed from Lua
        if doc.open and doc.window and doc.root then
            for el in pairs(doc.aligned) do
                if el.widget and el.visibleNow ~= false then pcall(Backend.align, el, el.align) end
            end
            for _, fn in ipairs(frameListeners) do
                local ok, problem = pcall(fn, doc, t)
                if not ok then report(doc.name, tostring(problem)) end
            end
        end
    end
    if t < nextPoll then return end
    nextPoll = t + POLL_MS
    local ok, problem = xpcall(poll, debug.traceback)
    if not ok then report("hot reload", tostring(problem)) end
end)

-- --- For tests and assistants: the window as data -----------------------------------------

-- A copy of a style value: the value itself may be shared by every element it applies to.
local function plain(value)
    if type(value) ~= "table" then return value end
    local out = {}
    for i, inner in ipairs(value) do out[i] = plain(inner) end
    return out
end

local function describe(el)
    local out = { tag = el.tag, id = el.id, label = el.label, line = el.line }
    local classes = {}
    for class in pairs(el.classes) do classes[#classes + 1] = class end
    table.sort(classes)
    if #classes > 0 then out.class = table.concat(classes, " ") end
    if Backend.hasValue(el.tag) then out.value = el:Value() end
    if el.disabled then out.disabled = true end
    if el.commands then out.commands = #el.commands end
    if el.visibleNow == false then out.visible = false end
    local style = {}
    for key, op in pairs(el.applied) do style[key] = plain(op.value) end
    if next(style) then out.style = style end
    if #el.children > 0 then
        out.children = {}
        for i, child in ipairs(el.children) do out.children[i] = describe(child) end
    end
    return out
end

--- A document as plain data: every element with its tag, id, classes, label, value and the
--- style applied to it ("color:Text" = { r, g, b, a } as IMGUI got it).
function UI.Dump(name, selector)
    local doc = documents[name]
    if not doc or not doc.root then return nil end
    local el = selector and doc:Get(selector) or doc.root
    if not el then return nil end
    local out = describe(el)
    if not selector then
        out.open = doc:IsOpen()
        out.theme = state.theme
        out.state = UI.raw(doc.state)
        out.geometry = doc.geometry
    end
    return out
end

--- Raises an event on an element of a loaded document, as the user would:
--- Weave.Fire("overlay", "#hud", "change", false) sets the value first when one is given.
function UI.Fire(name, selector, event, value)
    local doc = documents[name]
    local el = doc and doc:Get(selector)
    if not el then error("Weave: no element " .. tostring(selector) .. " in " .. tostring(name)) end
    if value ~= nil then el:SetValue(value) end
    el:Fire(event or "click", true)
    return el:Value()
end

-- For the kit's other files, not for mods: these may change.
UI.Internal = UI.Internal or {}
-- The variables an element's style is worked out with: its document's, or the base sheets'.
local baseVars
function UI.Internal.varsFor(element)
    if element.doc and element.doc.vars then return element.doc.vars end
    if not baseVars or baseVars.sheet ~= base.sheet or baseVars.theme ~= state.theme then
        baseVars = { sheet = base.sheet, theme = state.theme, fn = variables() }
    end
    return baseVars.fn
end
for name, value in pairs({ report = report, shortTrace = shortTrace, splitList = splitList, buildElement = buildElement, realize = realize,
    Element = Element, Document = Document, updateVisible = updateVisible, childrenChanged = childrenChanged,
    styleNew = styleNew }) do
    UI.Internal[name] = value
end

loadBase()
