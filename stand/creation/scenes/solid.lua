-- Makes a creature the party does not see solid again. A creature no party member has in sight is
-- drawn at half opacity: the client's out-of-sight fade (the components
-- ClientCharacterOutOfSightFadeView0 / View1 with TargetValue 0.5, and FadingView0 / View1 with
-- the opacity now). A creature kept awake far from the party (GK.Wake.One, GK.Wake.Place) is such
-- a one. Written with TargetValue 1 and Active false, the game fades the figure in itself and
-- takes the components off.
-- Client context. GKSceneOptions = { solid = "<uuid>" } for one creature; left out: every
-- character the client holds faded.
-- Run: ./gatekit --instance <copy> exec client "GKSceneOptions = { solid = '<uuid>' } $(cat stand/creation/scenes/solid.lua)"
-- Answers { ok, solid = { uuids written }, none = true when nothing was faded }.
local one = GKSceneOptions and GKSceneOptions.solid
local VIEWS = { "ClientCharacterOutOfSightFadeView0", "ClientCharacterOutOfSightFadeView1" }
local function unfade(entity)
    local written = false
    for _, name in ipairs(VIEWS) do
        local view = entity[name]
        if view then
            view.TargetValue = 1.0
            view.Active = false
            written = true
        end
    end
    return written
end
local done = {}
if type(one) == "string" then
    local entity = Ext.Entity.Get(one)
    if not entity then return { ok = false, error = "the client holds no entity " .. one } end
    if unfade(entity) then done[1] = one end
else
    for _, entity in ipairs(Ext.Entity.GetAllEntitiesWithComponent(VIEWS[1])) do
        if unfade(entity) then done[#done + 1] = entity.Uuid and entity.Uuid.EntityUuid or "?" end
    end
end
return { ok = true, solid = done, none = #done == 0 }
