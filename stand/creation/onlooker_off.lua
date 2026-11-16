-- Disarms the scene "onlooker" and removes the figure and the furniture it placed.
local S = GKScene and GKScene.onlooker
if not S then return { armed = false, note = "was not armed" } end
S.armed = false
if S.id and Ext.Entity.Get(S.id) then
    pcall(Osi.StopAnimation, S.id, 1)
    pcall(GK.Stage.Remove, S.id)
end
for _, id in pairs(S.made or {}) do
    if id ~= S.id and Ext.Entity.Get(id) then pcall(GK.Stage.Remove, id) end
end
S.id, S.made = nil, {}
return { armed = false, fired = S.fired, log = S.log }
