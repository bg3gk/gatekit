-- Client side. Lists every live timeline with the kinds of track it carries, and the full
-- content of the tracks that decide whether weapons and bodies are shown.
local wanted = { TLShowWeapon = true, TLShowVisual = true, TLShapeShift = true }
local out = {}
for id, timeline in pairs(Ext.System.ClientTimeline.Timelines) do
    local entry = { id = tostring(id), kinds = {}, tracks = {} }
    local ok, err = pcall(function()
        entry.phase = timeline.PhaseIndex
        entry.time = timeline.TimePlayed
        entry.duration = timeline.Duration
        entry.gameplay = timeline.IsGameplayTimeline
        local components = timeline.Components
        entry.total = #components
        for i = 1, #components do
            local component = components[i]
            local kind = tostring(Ext.Types.GetObjectType(component))
            entry.kinds[kind] = (entry.kinds[kind] or 0) + 1
            for name in pairs(wanted) do
                if kind:find(name, 1, true) then
                    entry.tracks[#entry.tracks + 1] = { kind = kind, data = Ext.Json.Parse(Ext.Json.Stringify(component, {
                        StringifyInternalTypes = true, IterateUserdata = true, AvoidRecursion = true })) }
                end
            end
        end
    end)
    if not ok then entry.error = tostring(err) end
    out[#out + 1] = entry
end
return out
