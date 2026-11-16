-- Disarms the scene "stargazer_ambush" and removes the stargazer, the telescope and the bugbear;
-- his pinned die goes with him. The animation links stay in the sets until the game restarts.
local S = GKScene and GKScene.stargazer_ambush
if not S then return { armed = false, note = "was not armed" } end
S.armed = false
if S.bugbear and Ext.Entity.Get(S.bugbear) then pcall(GK.Rolls.Unpin, { who = S.bugbear }) end
for _, id in ipairs({ S.id, S.bugbear, S.telescope }) do
    if id and Ext.Entity.Get(id) then pcall(GK.Stage.Remove, id) end
end
S.id, S.bugbear, S.telescope = nil, nil, nil
return { armed = false, fired = S.fired, stage = S.stage, log = S.log }
