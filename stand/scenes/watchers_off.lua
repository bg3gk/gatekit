-- Disarms the scene "watchers" and removes Raphael and Mizora if they are still there.
local S = GKScene and GKScene.watchers
if not S then return { ok = true, nothing = "never armed in this Lua state" } end
S.armed = false
S.notice = nil
local removed = 0
for _, w in pairs(S.who or {}) do
    if w.id and not w.vanished and Ext.Entity.Get(w.id) then
        pcall(GK.Stage.Remove, w.id)
        w.vanished = true
        removed = removed + 1
    end
end
S.log[#S.log + 1] = { tick = GK.Speed.Clock().ticks, ms = Ext.Utils.MonotonicTime(), text = "disarmed" }
return { ok = true, removed = removed }
