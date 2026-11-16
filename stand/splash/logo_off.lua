-- Takes the stand's logo off at once and disarms the scene.
local S = GKScene and GKScene.splash_logo
if not S then return { armed = false, note = "was not armed" } end
if S.off then S.off() end
return { armed = false, fired = S.fired, left = S.left, log = S.log }
