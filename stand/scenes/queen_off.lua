-- Disarms the scene "queen" and takes back what it made: the shove's distance, Vlaakith, and
-- Lae'zel off stage (put back on stage; a death is undone only by the driver's reset).
local S = GKScene and GKScene.queen
pcall(function()
    for _, t in ipairs(Ext.Stats.Get("Target_Shove").SpellSuccess) do
        for _, f in ipairs(t.Functors) do if f.TypeId == "Force" then f.Distance = "ShoveDistance" end end
    end
end)
if not S then return { ok = true, nothing = "never armed in this Lua state; the shove's distance is the game's" } end
S.armed = false
S.ready = false
for _, id in ipairs(S.made or {}) do pcall(GK.Stage.Remove, id) end
if S.fired and Osi.IsOnStage("58a69333-40bf-8358-1d17-fff240d7fb12") == 0 then
    pcall(Osi.SetOnStage, "58a69333-40bf-8358-1d17-fff240d7fb12", 1)
end
S.log[#S.log + 1] = { tick = GK.Speed.Clock().ticks, ms = Ext.Utils.MonotonicTime(), text = "disarmed" }
return { ok = true, removed = #(S.made or {}) }
