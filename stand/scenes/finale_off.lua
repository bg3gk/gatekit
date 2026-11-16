-- Disarms the scene "finale" and takes back what it made: the black screen, the brain and its
-- glow, the barrel if it is still whole, the party's statuses, and the party's right to join a
-- fight. The ended fight and the brain's blast are undone only by the driver's reset; running
-- credits are closed by their own page (GK.Cheat.Run("Credits", "CreditsEnded") on the UI side).
local S = GKScene and GKScene.finale
if not S then return { ok = true, nothing = "never armed in this Lua state" } end
local stage = S.stage
if S.off then S.off() end
S.log[#S.log + 1] = { tick = GK.Speed.Clock().ticks, ms = Ext.Utils.MonotonicTime(), text = "disarmed" }
return { ok = true, was = stage, fired = S.fired, done = S.done }
