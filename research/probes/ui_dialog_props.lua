-- UI context: top-level property names and kinds of the dialogue view model and its data context.
local function typeName(object)
    local ok, name = pcall(function() return tostring(object.Type) end)
    return ok and name or "?"
end
local found = {}
local function walk(element, depth)
    if depth > 60 then return end
    local ok, context = pcall(function() return element.DataContext end)
    if ok and context then
        local name = typeName(context)
        if (name == "gui::VMDialogue" or name == "gui::DCDialogue") and not found[name] then found[name] = context end
    end
    local n = 0
    pcall(function() n = element.VisualChildrenCount end)
    for i = 1, n do
        local okc, child = pcall(function() return element:VisualChild(i) end)
        if okc and child then walk(child, depth + 1) end
    end
end
walk(Ext.UI.GetRoot(), 0)
local out = {}
for name, object in pairs(found) do
    local props = {}
    for key, value in pairs(object:GetAllProperties()) do
        local kind = type(value)
        if kind == "userdata" then
            local t = typeName(value)
            local okn, n = pcall(function() return #value end)
            props[#props + 1] = tostring(key) .. "=<" .. t .. ">" .. ((okn and type(n) == "number") and ("#" .. n) or "")
        elseif kind == "string" then
            props[#props + 1] = tostring(key) .. "='" .. (#value > 40 and (#value .. " bytes") or value) .. "'"
        else
            props[#props + 1] = tostring(key) .. "=" .. tostring(value)
        end
    end
    table.sort(props)
    out[name] = table.concat(props, " | ")
end
return out
