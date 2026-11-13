-- Client side: properties of the first DataContext of a given type in the UI tree.
--   exec client "WANT='gui::DCDialogue' ; <this file>"  - set the global WANT first
local want = WANT or "gui::DCDialogue"
local found

local function typeName(object)
    local ok, name = pcall(function() return tostring(object.Type) end)
    return ok and name or "?"
end

local function walk(element, depth)
    if found or depth > 60 then return end
    local ok, context = pcall(function() return element.DataContext end)
    if ok and context and typeName(context) == want then found = context return end
    local n = 0
    pcall(function() n = element.VisualChildrenCount end)
    for i = 1, n do
        local okc, child = pcall(function() return element:VisualChild(i) end)
        if okc and child then walk(child, depth + 1) end
    end
end
walk(Ext.UI.GetRoot(), 0)
if not found then return "not found: " .. want end

local function describe(object, depth)
    local out = {}
    local ok, props = pcall(function() return object:GetAllProperties() end)
    if not ok then return "ERR " .. tostring(props) end
    for name, value in pairs(props) do
        local kind = type(value)
        if kind == "userdata" then
            local t = typeName(value)
            local entry = "<" .. t .. ">"
            local okn, n = pcall(function() return #value end)
            if okn and type(n) == "number" then
                entry = entry .. " len=" .. n
                if depth < 2 and n > 0 then
                    local oke, first = pcall(function() return value[1] end)
                    if oke and first then out[tostring(name) .. "[1]"] = describe(first, depth + 1) end
                end
            end
            out[tostring(name)] = entry
        else
            local text = tostring(value)
            if #text > 90 then text = text:sub(1, 90) .. "..." end
            out[tostring(name)] = text
        end
    end
    return out
end
return describe(found, 0)
