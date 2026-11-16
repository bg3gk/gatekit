-- Disarms the scene "withers" and removes its figure.
local S = GKScene and GKScene.withers
if not S then return { ok = true, nothing = "never armed in this Lua state" } end
if S.off then S.off() end
return { ok = true, stage = S.stage }
