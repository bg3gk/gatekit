-- Disarms the main menu's new dancer: removes the figure and puts the borrowed light back.
local S = GKScene and GKScene.menu_dancer_new
if not S then return { armed = false, note = "was not armed" } end
if S.off then S.off() end
return { armed = false, fired = S.fired, log = S.log }
