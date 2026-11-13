-- How many live entities carry each component type on this side, mapped or not.
-- Returns { entities = N, counts = { [componentName] = count } }.
local counts, entities = {}, 0
for _, entity in ipairs(Ext.Entity.GetAllEntities()) do
    entities = entities + 1
    for _, name in pairs(entity:GetAllComponentNames(false)) do
        counts[name] = (counts[name] or 0) + 1
    end
end
return { entities = entities, counts = counts }
