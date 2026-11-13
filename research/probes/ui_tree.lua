-- Client side: walk the Noesis visual tree and report the distinct DataContext types found,
-- with the path of the first element carrying each. Pass a word to list one type's properties:
--   exec client "@probes/ui_tree.lua"            (overview)
local root = Ext.UI.GetRoot()
local seen, order, count = {}, {}, 0

local function typeName(object)
    local ok, name = pcall(function() return tostring(object.Type) end)
    return ok and name or "?"
end

local function walk(element, path, depth)
    count = count + 1
    if count > 60000 or depth > 60 then return end
    local ok, context = pcall(function() return element.DataContext end)
    if ok and context then
        local name = typeName(context)
        if not seen[name] then
            seen[name] = path
            order[#order + 1] = name
        end
    end
    local n = 0
    pcall(function() n = element.VisualChildrenCount end)
    for i = 1, n do
        local okc, child = pcall(function() return element:VisualChild(i) end)
        if okc and child then walk(child, path .. "/" .. typeName(child), depth + 1) end
    end
end

walk(root, typeName(root), 0)
local out = { elements = count, contexts = {} }
for _, name in ipairs(order) do
    local path = seen[name]
    if #path > 150 then path = "..." .. path:sub(-150) end
    out.contexts[#out.contexts + 1] = name .. "  @ " .. path
end
return out
