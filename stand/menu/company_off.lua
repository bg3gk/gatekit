-- Disarms the splash screen's company and removes its figures.
local S = GKScene and GKScene.menu_company
if not S then return { armed = false, note = "was not armed" } end
if S.off then S.off() end
return { armed = false, fired = S.fired, log = S.log }
