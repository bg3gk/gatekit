-- Disarms the scene "onlooker" and removes the figure it placed.
local S = GKScene and GKScene.onlooker
if not S then return { armed = false, note = "was not armed" } end
S.armed = false
if S.id and Ext.Entity.Get(S.id) then pcall(GK.Stage.Remove, S.id) end
S.id = nil
return { armed = false, fired = S.fired, log = S.log }
