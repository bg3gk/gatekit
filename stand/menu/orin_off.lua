-- Disarms Orin's scene of the main menu: removes the figures and the burst that are left and
-- puts the borrowed light back.
local S = GKScene and GKScene.menu_orin
if not S then return { armed = false, note = "was not armed" } end
if S.off then S.off() end
return { armed = false, fired = S.fired, door = S.door, log = S.log }
