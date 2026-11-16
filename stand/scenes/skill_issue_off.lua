-- Disarms the scene "skill_issue" and takes back what it made: the rig on Zhalk's save, the turd
-- polymorph, his immortality, the dung, the spell taught to Shadowheart.
local ZHALK = "ed103005-fd71-457d-ae6c-39654bbd8f2e"
local SHADOWHEART = "3ed74f06-3c60-42dc-83f6-f034cb47c679"
local S = GKScene and GKScene.skill_issue
if not S then return { ok = true, nothing = "never armed in this Lua state" } end
S.armed = false
S.awaiting = nil
local done = {}
if S.rig then done.unrig = GK.Rolls.Unrig(S.rig) end
pcall(function() Ext.Stats.Get("GK_Scene_TurdPolymorph").SpellSuccess[1].Functors[1].StatusId = "POLYMORPH_SHEEP" end)
if Osi.HasActiveStatus(ZHALK, "GK_SCENE_TURD") == 1 then Osi.RemoveStatus(ZHALK, "GK_SCENE_TURD"); done.turd = "removed" end
if S.immortal then pcall(Osi.SetImmortal, ZHALK, 0); S.immortal = false end
pcall(Osi.RemoveSpell, SHADOWHEART, "GK_Scene_TurdPolymorph", 0)
for _, item in ipairs(S.made or {}) do pcall(Osi.RequestDelete, item) end
done.items = #(S.made or {})
S.log[#S.log + 1] = { tick = GK.Speed.Clock().ticks, ms = Ext.Utils.MonotonicTime(), text = "disarmed" }
return { ok = true, done = done }
