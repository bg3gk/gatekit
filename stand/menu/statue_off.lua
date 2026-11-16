-- Disarms the figure behind the splash screen's statue and removes it.
local S = GKScene and GKScene.menu_statue
if not S then return { armed = false, note = "was not armed" } end
if S.off then S.off() end
return { armed = false, fired = S.fired, log = S.log }
