-- Weave, components: one file holds what a piece of interface is, does and looks like.
--
--   <template>
--     <window title="Counter" width="320" height="160">
--       <text>{{ count }} clicks, doubled: {{ double }}</text>
--       <button @click="count++">Click</button>
--       <text #if="count > 5" class="text-warn">That is a lot.</text>
--       <text #for="name, i in names" :key="name">{{ i }}. {{ name }}</text>
--       <input #model="note"/>
--     </window>
--   </template>
--   <script>
--     count = 0                       -- a value: reactive state
--     names = { "Astarion", "Karlach" }
--     note = ""
--     function reset() count = 0 end  -- a function: the template can call it
--     computed("double", function() return count * 2 end)
--     watch("count", function(new, old) print(old, "->", new) end)
--   </script>
--   <style scoped>
--     button { rounding: 6; }
--   </style>
--
-- Any block may live in a file of its own: <script src="counter.lua"/>, <style src="counter.css"/>.
-- A tag that is not an element is looked for as a component: <stat-card title="HP" :value="hp"/>
-- is stat-card.weave beside it (or Weave.RegisterComponent). What stands inside the tag goes
-- where the component's template says <slot/>.
--
-- Directives: #if / #else-if / #else, #for="item, i in list" with :key, #show, #model;
-- :attribute="expression" binds, @event="statement or function" listens, {{ expression }} is
-- text. Expressions are Lua with a little sugar (Expr.lua).

Weave = Weave or {}
local W = Weave
W.Internal = W.Internal or {}
local I = W.Internal
local Backend = W.Backend

local STORAGE_DIR = "Gwynerva_Weave/storage/"

-- --- Files ----------------------------------------------------------------------------------

local defs = {}               -- file -> definition

-- The first <tag ...> ... </tag> at the top level. `last`: up to the last closing tag (a
-- template has <template #if> inside). Returns attributes, body, the line the body starts on.
local function block(text, tag, last)
    local open, openEnd, attrs = text:find("<" .. tag .. "([^>]*)>")
    if not open then return nil end
    local line = select(2, text:sub(1, openEnd):gsub("\n", "")) + 1
    local parsed = {}
    for name, value in attrs:gmatch('([%w%-]+)%s*=%s*"([^"]*)"') do parsed[name] = value end
    for name in attrs:gsub('[%w%-]+%s*=%s*"[^"]*"', ""):gmatch("[%w%-]+") do parsed[name] = "true" end
    if attrs:match("/%s*$") then return parsed, "", line end
    local close, from = nil, openEnd + 1
    while true do
        local found = text:find("</" .. tag .. ">", from, true)
        if not found then break end
        close = found
        if not last then break end
        from = found + 1
    end
    if not close then return nil, "<" .. tag .. "> is never closed" end
    return parsed, text:sub(openEnd + 1, close - 1), line
end

local function parseComponent(file, text)
    local name = file:match("([^/\\]+)%.weave$") or file
    local def = { name = name, file = file, text = text, sources = {} }
    text = text:gsub("\r\n", "\n")          -- an editor's line ends are not a change of the template
    local function body(tag, last)
        local attrs, content, line = block(text, tag, last)
        if not attrs then return nil, content end
        if attrs.src then
            content = W.Source(attrs.src)
            if not content then return nil, "no file " .. attrs.src end
            def.sources[attrs.src] = content
            content = content:gsub("\r\n", "\n")
            line = 1
        end
        return attrs, content, line
    end
    local attrs, content, line = body("template", true)
    if not attrs then return nil, file .. ": " .. (content or "no <template>") end
    local node, problem = W.ParseXml(string.rep("\n", line - 1) .. content)
    if not node then return nil, file .. ": " .. problem end
    def.template = node
    attrs, content, line = body("script")
    if attrs then
        def.scriptText = string.rep("\n", line - 1) .. content
        def.scriptBody = content       -- compared on hot reload: where it stands in the file does not matter
    elseif content then
        return nil, file .. ": " .. content
    end
    attrs, content, line = body("style")
    def.styleText = attrs and (string.rep("\n", line - 1) .. content) or ""
    def.sheet = W.ParseCss(def.styleText)
    if attrs and attrs.scoped then def.sheet.scope = name end
    for _, complaint in ipairs(def.sheet.problems) do I.report(file, "<style> " .. complaint) end
    return def
end

--- Gives a component as text: Weave.RegisterComponent("stat-card", "<template>...").
function W.RegisterComponent(name, text)
    W.Define(name .. ".weave", text)
    defs[name .. ".weave"] = nil
end

-- The definition of a component file, read again when `fresh`. Returns def or nil, why.
function I.loadComponent(file, fresh)
    local def = defs[file]
    if def and not fresh then return def end
    local text = W.Source(file)
    if not text then return nil, "no component " .. file end
    local parsed, problem = parseComponent(file, text)
    if not parsed then
        defs[file] = nil
        return nil, problem
    end
    defs[file] = parsed
    return parsed
end

-- What changed in the files a document is made of since it was built: nil, "style" or "all".
function I.changed(doc)
    if not doc.def then                 -- never built: is the file readable now?
        local text = W.Source(doc.file)
        if text and text ~= doc.failedText then
            doc.failedText = text
            return "all"
        end
        return nil
    end
    local change
    for _, def in ipairs(doc.defOrder or {}) do
        local text = W.Source(def.file)
        local different = text ~= nil and text ~= def.text
        for src, old in pairs(def.sources) do
            if (W.Source(src) or "") ~= old then different = true end
        end
        if different then
            local fresh = parseComponent(def.file, text or def.text)
            if not fresh then              -- broken: said once, the window stays as it was
                if def.brokenText ~= text then
                    def.brokenText = text
                    local _, problem = parseComponent(def.file, text)
                    I.report(doc.name, tostring(problem))
                end
            elseif fresh.scriptBody == def.scriptBody and W.SameTree(fresh.template, def.template) then
                -- only the style differs: keep the definition (and everything built from it)
                def.text, def.sources, def.styleText = fresh.text, fresh.sources, fresh.styleText
                def.sheet = fresh.sheet
                change = change or "style"
            else
                defs[def.file] = fresh
                change = "all"
            end
        end
    end
    return change
end

--- Whether two parsed templates are the same (tags, attributes, text, children).
function W.SameTree(a, b)
    if a.tag ~= b.tag or a.text ~= b.text or #a.children ~= #b.children then return false end
    for name, value in pairs(a.attrs) do
        if b.attrs[name] ~= value then return false end
    end
    for name in pairs(b.attrs) do
        if a.attrs[name] == nil then return false end
    end
    for i, child in ipairs(a.children) do
        if not W.SameTree(child, b.children[i]) then return false end
    end
    return true
end

-- --- Blocks: what is built together is taken down together ---------------------------------

local function newBlock(parent)
    local blk = { effects = {}, blocks = {}, els = {}, instances = {} }
    if parent then parent.blocks[#parent.blocks + 1] = blk end
    return blk
end

local function teardown(blk, keepWidgets)
    for _, effect in ipairs(blk.effects) do W.stop(effect) end
    for _, child in ipairs(blk.blocks) do teardown(child, keepWidgets) end
    for _, instance in ipairs(blk.instances) do
        for _, e in ipairs(instance.effects) do W.stop(e) end
        for _, fn in ipairs(instance.unmounted) do pcall(fn) end
    end
    if not keepWidgets then
        for _, el in ipairs(blk.els) do
            if not el.dead then el:Remove() end
        end
    end
    blk.effects, blk.blocks, blk.els, blk.instances = {}, {}, {}, {}
end

local function effect(context, name, fn)
    local e = W.effect(fn, { name = name })
    context.block.effects[#context.block.effects + 1] = e
    return e
end

-- Evaluates in a scope; a failure is reported once with where it stands, and gives nil.
local function evaluate(doc, expression, scope, where)
    local ok, value = W.eval(expression, scope)
    if ok then return value end
    I.report(doc.name, where .. " \"" .. expression.text .. "\": " .. tostring(value))
    return nil
end

local function compile(doc, text, where)
    local expression, problem = W.compile(text)
    if not expression then I.report(doc.name, where .. " \"" .. text .. "\": " .. tostring(problem)) end
    return expression
end

-- --- Bindings -------------------------------------------------------------------------------

local function classList(value)
    local out = {}
    if type(value) == "string" then
        for name in value:gmatch("%S+") do out[name] = true end
    elseif type(value) == "table" then
        for key, on in pairs(value) do
            if type(key) == "number" then
                for name in tostring(on):gmatch("%S+") do out[name] = true end
            elseif on then
                out[key] = true
            end
        end
    end
    return out
end

-- :name="expression" -> what to do with the value. Others can be added: W.Bindings.tip = fn(el, value).
W.Bindings = {
    class = function(el, value)
        local classes = {}
        for name in pairs(el.staticClasses) do classes[name] = true end
        for name in pairs(classList(value)) do classes[name] = true end
        el.classes = classes
        if not el.doc.building then el:_restyle() end
    end,
    style = function(el, value)
        local bound = {}
        if type(value) == "table" then
            for name, v in pairs(value) do bound[#bound + 1] = { name = (name:gsub("_", "-")), value = tostring(v) } end
        elseif value then
            bound = W.ParseInline(tostring(value))
        end
        local inline = {}
        for _, declaration in ipairs(el.staticInline) do inline[#inline + 1] = declaration end
        for _, declaration in ipairs(bound) do inline[#inline + 1] = declaration end
        el.inline = inline
        if not el.doc.building then el:_restyle() end
    end,
    disabled = function(el, value) el:Disable(value and true or false) end,
    options = function(el, value) Backend.setOptions(el, value) end,
    value = function(el, value) el:SetValue(value) end,
    checked = function(el, value) el:SetValue(value) end,
    label = function(el, value) el:SetText(value) end,
    text = function(el, value) el:SetText(value) end,
    title = function(el, value) el:SetText(value) end,
    visible = function(el, value) el:Show(value and true or false) end,
    open = function(el, value) el:Expand(value and true or false) end,      -- <header>, <tree>
    draw = function(el, value)
        el.boundDraw = true
        el:Draw(value)
    end,
    tip = function(el, value)
        if not el.tipText then el.tipText = Backend.addTip(el, tostring(value or "")) end
        Backend.setTip(el.tipText, tostring(value or ""))
    end,
}
for _, name in ipairs({ "width", "height", "font", "wrap" }) do
    W.Bindings[name] = function(el, value) el:Style(name, value) end
end

-- A deep read, so an effect that hands a table on (options, a drawing) runs again when
-- anything inside it changes.
local function touch(value, depth)
    if type(value) ~= "table" or (depth or 0) > 6 then return value end
    for _, inner in pairs(value) do touch(inner, (depth or 0) + 1) end
    return value
end

local function bindAttribute(doc, el, name, text, context)
    local expression = compile(doc, text, "<" .. el.tag .. "> :" .. name)
    local apply = W.Bindings[name]
    if not expression then return end
    effect(context, ":" .. name, function()
        local value = touch(evaluate(doc, expression, context.scope, "<" .. el.tag .. "> :" .. name))
        if el.dead or not el.widget then return end
        W.untracked(function()
            if apply then
                local ok, problem = pcall(apply, el, W.raw(value))
                if not ok then I.report(doc.name, "<" .. el.tag .. "> :" .. name .. ": " .. tostring(problem)) end
            elseif el.attrs[name] ~= value then
                el.attrs[name] = value                 -- [name=value] in a style sheet may look at it
                if not doc.building then el:_restyle() end
            end
        end)
    end)
end

-- "a {{ x }} b" -> { "a ", expression, " b" } or nil when there is nothing to work out.
local function textParts(doc, text, where)
    if not text or not text:find("{{", 1, true) then return nil end
    local parts, pos = {}, 1
    while true do
        local open, close = text:find("{{", pos, true), nil
        if open then close = text:find("}}", open, true) end
        if not open or not close then
            if pos <= #text then parts[#parts + 1] = text:sub(pos) end
            break
        end
        if open > pos then parts[#parts + 1] = text:sub(pos, open - 1) end
        parts[#parts + 1] = compile(doc, text:sub(open + 2, close - 1):match("^%s*(.-)%s*$"), where) or ""
        pos = close + 2
    end
    return parts
end

local function bindText(doc, el, parts, context)
    effect(context, "{{ }}", function()
        local out = {}
        for i, part in ipairs(parts) do
            if type(part) == "table" then
                local value = evaluate(doc, part, context.scope, "<" .. el.tag .. "> {{ }}")
                out[i] = value == nil and "" or tostring(value)
            else
                out[i] = part
            end
        end
        if not el.dead and el.widget then el:SetText(table.concat(out)) end
    end)
end

local function bindEvent(doc, el, event, text, context)
    if not Backend.hasEvent(event) then
        I.report(doc.name, "line " .. el.line .. ": no such event: @" .. event)
        return
    end
    local expression = compile(doc, text, "<" .. el.tag .. "> @" .. event)
    if not expression then return end
    el:On(event, function(e)
        local scope = W.scope({ event = e, value = e.value }, context.scope)
        local result = evaluate(doc, expression, scope, "<" .. el.tag .. "> @" .. event)
        if type(result) == "function" then result(e) end     -- @click="save" names a function
    end, true)
end

local function bindModel(doc, el, text, context)
    local where = "<" .. el.tag .. "> #model"
    local read = compile(doc, text, where)
    local write = compile(doc, text .. " = __value", where)
    if not read or not write then return end
    effect(context, "#model", function()
        local value = evaluate(doc, read, context.scope, where)
        if not el.dead and el.widget and value ~= nil then W.untracked(function() pcall(el.SetValue, el, value) end) end
    end)
    el:On("change", function(e)
        evaluate(doc, write, W.scope({ __value = e.value }, context.scope), where)
    end, true)
end

-- --- Building -------------------------------------------------------------------------------

local buildNodes, mountComponent

local DIRECTIVES = { ["#if"] = true, ["#else-if"] = true, ["#else"] = true, ["#for"] = true, ["#show"] = true,
    ["#model"] = true }

-- A copy of a template node without the attributes a wrapper has already used.
local function without(node, ...)
    local copy = { tag = node.tag, attrs = {}, children = node.children, text = node.text, line = node.line, order = node.order }
    for name, value in pairs(node.attrs) do copy.attrs[name] = value end
    for _, name in ipairs({ ... }) do copy.attrs[name] = nil end
    return copy
end

local function fragment(doc, parentEl, context, line)
    local el = I.buildElement(doc, { tag = "fragment", attrs = {}, children = {}, line = line }, parentEl)
    el.context, el.component = context, context.instance.def.name
    if not I.realize(doc, el, parentEl.widget) then return nil end
    parentEl.children[#parentEl.children + 1] = el
    I.childrenChanged(doc, parentEl)
    if context.block.parentEl == parentEl then context.block.els[#context.block.els + 1] = el end
    return el
end

local function restyleNew(doc, parentEl, from)
    if not doc.building then I.styleNew(doc, parentEl, from) end
end

local function buildOne(doc, node, parentEl, context)
    local a = node.attrs
    if not Backend.hasTag(node.tag) then
        local def = I.loadComponent(node.tag .. ".weave")
        if def then return mountComponent(doc, def, node, parentEl, context) end
    end
    local el = I.buildElement(doc, node, parentEl)
    el.context, el.component = context, context.instance.def.name
    el.staticClasses, el.staticInline = {}, el.inline
    for name in pairs(el.classes) do el.staticClasses[name] = true end
    local where = "<" .. node.tag .. ">"
    local parts = textParts(doc, a.label or a.title or node.text, where .. " {{ }}")
    if parts then el.rawLabel = "" end
    if not I.realize(doc, el, parentEl.widget) then return nil end
    parentEl.children[#parentEl.children + 1] = el
    I.childrenChanged(doc, parentEl)
    if context.block.parentEl == parentEl then context.block.els[#context.block.els + 1] = el end
    if parts then bindText(doc, el, parts, context) end
    for _, name in ipairs(node.order or {}) do
        local value = a[name]
        local kind, rest = name:sub(1, 1), name:sub(2)
        if kind == ":" and rest ~= "key" then
            bindAttribute(doc, el, rest, value, context)
        elseif kind == "@" then
            bindEvent(doc, el, rest, value, context)
        elseif name == "#model" then
            bindModel(doc, el, value, context)
        elseif name == "#show" then
            local expression = compile(doc, value, where .. " #show")
            if expression then
                effect(context, "#show", function()
                    el.shown = evaluate(doc, expression, context.scope, where .. " #show") and true or false
                    if not el.dead then I.updateVisible(el) end
                end)
            end
        elseif kind == "#" and not DIRECTIVES[name] then
            I.report(doc.name, "line " .. node.line .. ": no such directive: " .. name)
        end
    end
    buildNodes(doc, node.children, el, context)
    return el
end

-- #if / #else-if / #else: one fragment, one branch inside it at a time.
local function buildIf(doc, chain, parentEl, context)
    local holder = fragment(doc, parentEl, context, chain[1].node.line)
    if not holder then return end
    for _, branch in ipairs(chain) do
        if branch.text then branch.expression = compile(doc, branch.text, "#if") end
    end
    local shown, blk = nil, nil
    effect(context, "#if", function()
        local pick
        for index, branch in ipairs(chain) do
            if not branch.text or (branch.expression and evaluate(doc, branch.expression, context.scope, "#if")) then
                pick = index
                break
            end
        end
        if pick == shown then return end
        shown = pick
        W.untracked(function()
            if blk then teardown(blk) end
            blk = nil
            if not pick or holder.dead then return end
            blk = newBlock(context.block)
            blk.parentEl = holder
            local from = #holder.children + 1
            buildNodes(doc, { chain[pick].node }, holder, { scope = context.scope, instance = context.instance, block = blk })
            restyleNew(doc, holder, from)
        end)
    end)
end

-- #for: one fragment; each item is a block with a scope of its own. Blocks are kept by key.
-- IMGUI can only append, so when the order changes in the middle the list is built anew.
local function buildFor(doc, node, parentEl, context)
    local spec, problem = W.parseFor(node.attrs["#for"])
    if not spec then
        I.report(doc.name, "line " .. node.line .. ": " .. tostring(problem))
        return
    end
    local keyExpression = node.attrs[":key"] and compile(doc, node.attrs[":key"], "#for :key")
    local holder = fragment(doc, parentEl, context, node.line)
    if not holder then return end
    local template = without(node, "#for", ":key")
    local rows = {}                         -- { key, vars, block } in order
    effect(context, "#for", function()
        local list = evaluate(doc, spec.source, context.scope, "#for")
        local items = {}
        if type(list) == "number" then
            for i = 1, list do items[i] = i end
        elseif type(list) == "table" then
            for i, item in ipairs(list) do items[i] = item end
        end
        local keys = {}
        for i, item in ipairs(items) do
            local key = i
            if keyExpression then
                local vars = { [spec.item] = item }
                if spec.index then vars[spec.index] = i end
                key = evaluate(doc, keyExpression, W.scope(vars, context.scope), "#for :key")
                if key == nil then key = i end
            end
            keys[i] = key
        end
        W.untracked(function()
            if holder.dead then return end
            -- Can the rows that stay keep their order, with the new ones only at the end?
            local wanted, kept, appendOnly = {}, {}, true
            for i, key in ipairs(keys) do wanted[key] = i end
            for _, row in ipairs(rows) do
                if wanted[row.key] then kept[#kept + 1] = row end
            end
            for i, row in ipairs(kept) do
                if keys[i] ~= row.key then appendOnly = false break end
            end
            if not appendOnly then
                for _, row in ipairs(rows) do teardown(row.block) end
                rows = {}
            else
                for _, row in ipairs(rows) do
                    if not wanted[row.key] then teardown(row.block) end
                end
                rows = kept
            end
            local from = #holder.children + 1
            for i, item in ipairs(items) do
                local row = rows[i]
                if row then                 -- kept: its item and index may have changed
                    row.vars[spec.item] = item
                    if spec.index then row.vars[spec.index] = i end
                else
                    local raw = { [spec.item] = item }
                    if spec.index then raw[spec.index] = i end
                    row = { key = keys[i], vars = W.reactive(raw), block = newBlock(context.block) }
                    row.block.parentEl = holder
                    rows[i] = row
                    buildNodes(doc, { template }, holder,
                        { scope = W.scope(row.vars, context.scope), instance = context.instance, block = row.block })
                end
            end
            restyleNew(doc, holder, from)
        end)
    end)
end

buildNodes = function(doc, nodes, parentEl, context)
    local i = 1
    while i <= #nodes do
        local node = nodes[i]
        local a = node.attrs
        if a["#for"] then
            buildFor(doc, node, parentEl, context)
            i = i + 1
        elseif a["#if"] then
            local chain = { { text = a["#if"], node = without(node, "#if") } }
            i = i + 1
            while nodes[i] and (nodes[i].attrs["#else-if"] or nodes[i].attrs["#else"]) do
                local other = nodes[i]
                chain[#chain + 1] = { text = other.attrs["#else-if"], node = without(other, "#else-if", "#else") }
                i = i + 1
            end
            buildIf(doc, chain, parentEl, context)
        elseif a["#else"] or a["#else-if"] then
            I.report(doc.name, "line " .. node.line .. ": #else without #if before it")
            i = i + 1
        elseif node.tag == "slot" then
            local instance = context.instance
            if instance.slotNodes and #instance.slotNodes > 0 then
                buildNodes(doc, instance.slotNodes, parentEl,
                    { scope = instance.slotContext.scope, instance = instance.slotContext.instance, block = context.block })
            else
                buildNodes(doc, node.children, parentEl, context)      -- what <slot> holds is the default
            end
            i = i + 1
        elseif node.tag == "template" then
            buildNodes(doc, node.children, parentEl, context)
            i = i + 1
        else
            buildOne(doc, node, parentEl, context)
            i = i + 1
        end
    end
end
I.buildNodes = buildNodes

-- --- Instances ------------------------------------------------------------------------------

local function translate(key)
    local ok, text = pcall(Ext.Loca.GetTranslatedString, key)
    if ok and text and text ~= "" then return text end
    return key
end
W.Globals.t = translate

-- A table kept in a file: `settings = useStorage("settings", { volume = 5 })`.
local function storage(doc, key, defaults)
    local file = STORAGE_DIR .. doc.name .. "." .. key .. ".json"
    local data = defaults or {}
    local ok, text = pcall(Ext.IO.LoadFile, file)
    if ok and text and text ~= "" then
        local parsed, value = pcall(Ext.Json.Parse, text)
        if parsed and type(value) == "table" then
            for k, v in pairs(value) do data[k] = v end
        end
    end
    local store = W.reactive(data)
    local first = true
    local saver = W.effect(function()
        touch(store)
        if first then first = false return end
        pcall(Ext.IO.SaveFile, file, Ext.Json.Stringify(W.raw(store)))
    end, { name = "useStorage " .. key })
    return store, saver
end

local function newInstance(doc, def, path, parentInstance)
    local rawState = doc.stateStore[path]
    local reloading = rawState ~= nil
    rawState = rawState or {}
    doc.stateStore[path] = rawState
    local instance = { def = def, doc = doc, path = path, parent = parentInstance, state = W.reactive(rawState),
        props = W.reactive({}), computed = {}, methods = {}, mounted = {}, unmounted = {}, listeners = {},
        effects = {} }
    instance.scope = W.scope({}, nil, {
        get = function(name)
            local fn = instance.methods[name]
            if fn ~= nil then return fn end
            local getter = instance.computed[name]
            if getter then return getter() end
            local value = instance.state[name]
            if value ~= nil then return value end
            value = instance.props[name]
            if value ~= nil then return value end
            value = instance.helpers[name]         -- emit, t, el ... are for templates too
            if value ~= nil then return value end
            return doc.env and doc.env[name]       -- and what the mod that mounted the window gave it
        end,
        has = function(name) return rawState[name] ~= nil or instance.computed[name] ~= nil end,
        set = function(name, value) instance.state[name] = value end,
    })
    local helpers = {
        state = instance.state, props = instance.props, doc = doc,
        computed = function(name, fn)
            local getter, e = W.computed(fn, def.name .. " computed " .. tostring(name))
            instance.computed[name] = getter
            instance.effects[#instance.effects + 1] = e
        end,
        watch = function(source, callback)
            local read = source
            if type(source) == "string" then read = function() return instance.scope.get(source) end end
            instance.effects[#instance.effects + 1] = W.watch(read, callback, def.name .. " watch")
        end,
        effect = function(fn)
            instance.effects[#instance.effects + 1] = W.effect(fn, { name = def.name .. " effect" })
        end,
        emit = function(event, ...)
            local listener = instance.listeners[event]
            if listener then return listener(...) end
        end,
        onMounted = function(fn) instance.mounted[#instance.mounted + 1] = fn end,
        onUnmounted = function(fn) instance.unmounted[#instance.unmounted + 1] = fn end,
        useStorage = function(key, defaults)
            local store, saver = storage(doc, key, defaults)
            instance.effects[#instance.effects + 1] = saver
            return store
        end,
        el = function(selector) return doc:Get(selector) end,
        t = translate, reactive = W.reactive, raw = W.raw,
    }
    instance.helpers = helpers
    instance.run = function()
        if not def.scriptText then return end
        local outer = doc.env or {}
        local env = setmetatable({}, {
            __index = function(_, name)
                local value = instance.scope.get(name)
                if value ~= nil then return value end
                value = outer[name]
                if value ~= nil then return value end
                return _G[name]
            end,
            __newindex = function(_, name, value)
                if type(value) == "function" then
                    instance.methods[name] = value
                elseif not (reloading and instance.running and rawState[name] ~= nil) then
                    instance.state[name] = value       -- on a hot reload what is there is kept
                end
            end,
        })
        local chunk, problem = load(def.scriptText, env)
        if not chunk then
            I.report(doc.name, "<script> " .. tostring(problem))
            return
        end
        instance.running = true
        local ok, failure = xpcall(chunk, debug.traceback)
        instance.running = false
        if not ok then I.report(doc.name, "<script> " .. tostring(I.shortTrace(failure))) end
    end
    return instance
end

local function noteDefinition(doc, def)
    if doc.defs[def.name] then return end
    doc.defs[def.name] = def
    doc.defOrder[#doc.defOrder + 1] = def
    if not doc.building then doc:_restyleAll() end
end

mountComponent = function(doc, def, node, parentEl, context)
    if def.template.tag == "window" then
        I.report(doc.name, "line " .. node.line .. ": <" .. node.tag .. "> is a window; mount it with Weave.Mount")
        return nil
    end
    noteDefinition(doc, def)
    local a = node.attrs
    local count = (context.block.count or 0) + 1
    context.block.count = count
    local path = context.instance.path .. "/" .. def.name .. ":" .. tostring(a.id or count)
    local instance = newInstance(doc, def, path, context.instance)
    local blk = newBlock(context.block)
    blk.parentEl = parentEl
    blk.instances[1] = instance
    instance.slotNodes, instance.slotContext = node.children, context
    for _, name in ipairs(node.order or {}) do
        local value = a[name]
        local kind, rest = name:sub(1, 1), name:sub(2)
        if kind == ":" and rest ~= "key" then
            local expression = compile(doc, value, "<" .. node.tag .. "> :" .. rest)
            if expression then
                blk.effects[#blk.effects + 1] = W.effect(function()
                    instance.props[rest] = evaluate(doc, expression, context.scope, "<" .. node.tag .. "> :" .. rest)
                end, { name = ":" .. rest })
            end
        elseif kind == "@" then
            local expression = compile(doc, value, "<" .. node.tag .. "> @" .. rest)
            instance.listeners[rest] = expression and function(...)
                local result = evaluate(doc, expression, W.scope({ value = (...), args = { ... } }, context.scope),
                    "<" .. node.tag .. "> @" .. rest)
                if type(result) == "function" then return result(...) end
                return result
            end
        elseif kind ~= "#" and name ~= "class" and name ~= "id" and name ~= "style" then
            instance.props[name] = value
        end
    end
    instance.run()
    for _, e in ipairs(instance.effects) do blk.effects[#blk.effects + 1] = e end
    local from = #parentEl.children + 1
    buildNodes(doc, { def.template }, parentEl, { scope = instance.scope, instance = instance, block = blk })
    -- class, id and style on the component's tag go to the first element it built
    local first = parentEl.children[from]
    if first then
        if a.id and not first.id then first.id, doc.byId[a.id] = a.id, first end
        for name in (a.class or ""):gmatch("%S+") do
            first.classes[name] = true
            if first.staticClasses then first.staticClasses[name] = true end
        end
        if a.style then
            for _, declaration in ipairs(W.ParseInline(a.style)) do first.inline[#first.inline + 1] = declaration end
        end
        if a["#show"] then
            local expression = compile(doc, a["#show"], "<" .. node.tag .. "> #show")
            if expression then
                blk.effects[#blk.effects + 1] = W.effect(function()
                    first.shown = evaluate(doc, expression, context.scope, "#show") and true or false
                    if not first.dead then I.updateVisible(first) end
                end, { name = "#show" })
            end
        end
    end
    for _, fn in ipairs(instance.mounted) do
        local ok, problem = pcall(fn)
        if not ok then I.report(doc.name, def.name .. " onMounted: " .. tostring(problem)) end
    end
    return first
end

-- The root component of a document: its template's root is the window Dom.lua has made.
function I.mountRoot(doc, node, rootEl)
    local def = doc.def
    local instance = newInstance(doc, def, def.name, nil)
    for name, value in pairs(doc.props or {}) do instance.props[name] = value end
    local blk = newBlock(nil)
    blk.parentEl = rootEl
    blk.instances[1] = instance
    doc.instance, doc.state, doc.block = instance, instance.state, blk
    instance.run()
    for _, e in ipairs(instance.effects) do blk.effects[#blk.effects + 1] = e end
    local context = { scope = instance.scope, instance = instance, block = blk }
    rootEl.context, rootEl.staticClasses, rootEl.staticInline = context, {}, rootEl.inline
    local parts = textParts(doc, node.attrs.title or node.attrs.label, "<window> title")
    if parts then bindText(doc, rootEl, parts, context) end
    for _, name in ipairs(node.order or {}) do
        local kind, rest = name:sub(1, 1), name:sub(2)
        if kind == ":" then bindAttribute(doc, rootEl, rest, node.attrs[name], context)
        elseif kind == "@" then bindEvent(doc, rootEl, rest, node.attrs[name], context) end
    end
    buildNodes(doc, node.children, rootEl, context)
    for _, fn in ipairs(instance.mounted) do
        local ok, problem = pcall(fn)
        if not ok then I.report(doc.name, def.name .. " onMounted: " .. tostring(problem)) end
    end
end

-- Stops everything a document's components run (its widgets go with the window).
function I.unmount(doc)
    if doc.block then teardown(doc.block, true) end
    doc.block = nil
end
