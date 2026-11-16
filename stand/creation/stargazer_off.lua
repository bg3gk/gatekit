-- Disarms the scene "stargazer" and removes the figure and the telescope it placed.
local S = GKScene and GKScene.stargazer
if not S then return { armed = false, note = "was not armed" } end
S.armed = false
if S.dialog and S.dialog.instance then pcall(Osi.DialogRequestBehaviorGracefulStop, S.dialog.instance, 1) end
for _, id in ipairs({ S.id, S.telescope }) do
    if id and Ext.Entity.Get(id) then pcall(GK.Stage.Remove, id) end
end
S.id, S.telescope = nil, nil
return { armed = false, fired = S.fired, log = S.log }
