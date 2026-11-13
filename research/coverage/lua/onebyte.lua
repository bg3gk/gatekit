-- For every component type registered with at most one byte: how many live entities carry it
-- and which byte values they hold. A tag (an empty C++ struct) and a one-byte data component
-- look the same in the registry; the values help to tell them apart.
local wanted = {}
for _, entry in ipairs(Ext.Debug.GetComponentTypes()) do
    if entry.TotalSize ~= nil and entry.TotalSize <= 1 then wanted[entry.Name] = true end
end
local out = {}
for _, entity in ipairs(Ext.Entity.GetAllEntities()) do
    for _, name in pairs(entity:GetAllComponentNames()) do
        if wanted[name] then
            local row = out[name]
            if row == nil then row = { Count = 0, Values = {} }; out[name] = row end
            row.Count = row.Count + 1
            if row.Count <= 200 then
                local at = Ext.Debug.GetComponentAddress(entity, name)
                local byte = at and Ext.Debug.ReadMemory(at.Address, 1)
                local key = byte and tostring(byte:byte()) or "unreadable"
                row.Values[key] = (row.Values[key] or 0) + 1
            end
        end
    end
end
return out
