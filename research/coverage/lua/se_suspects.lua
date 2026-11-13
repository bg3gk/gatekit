-- Live facts about types Script Extender may declare wrongly (research/coverage/leads.md).
-- For each one-byte type: how many entities carry it on this side and which byte values they
-- hold. Run on the server (a tag's byte is always 0 there) and on the client. Read-only.
local names = ...
local out = {}
for _, name in ipairs(names) do
    local values, count = {}, 0
    for _, entity in ipairs(Ext.Debug.GetComponentHolders(name, 400)) do
        local at = Ext.Debug.GetComponentAddress(entity, name)
        local byte = at and Ext.Debug.ReadMemory(at.Address, 1)
        local key = byte and tostring(byte:byte()) or "unreadable"
        values[key] = (values[key] or 0) + 1
        count = count + 1
    end
    out[name] = { Holders = count, Values = values }
end
return out
