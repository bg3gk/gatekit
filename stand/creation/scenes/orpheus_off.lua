-- Disarms the scene "orpheus": lets him sleep and removes him, the sphere, the rock and the crystals.
local S = GKScene and GKScene.orpheus
if not S then return { armed = false, note = "was not armed" } end
S.armed = false
if S.wake and S.id then pcall(GK.Wake.Release, S.id) end
if S.id and Ext.Entity.Get(S.id) then pcall(GK.Stage.Remove, S.id) end
for _, id in pairs(S.made or {}) do
    if id and Ext.Entity.Get(id) then pcall(GK.Stage.Remove, id) end
end
S.id, S.made, S.wake = nil, {}, nil
return { armed = false, fired = S.fired, log = S.log }
