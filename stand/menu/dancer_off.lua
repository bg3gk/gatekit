-- Disarms the main menu's dancer and gives the adventurer back to the menu's timeline.
local S = GKScene and GKScene.menu_dancer
if not S then return { armed = false, note = "was not armed" } end
if S.off then S.off() end
return { armed = false, fired = S.fired, log = S.log }
