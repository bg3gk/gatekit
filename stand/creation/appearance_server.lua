-- The server's half of the scene "appearance", for the guardian: the same look written straight into
-- the server's CCCompanionDefinition and replicated. A cheat: the server does not check what it is
-- given. The character does not need it (README, "The saved character").
-- Server context. GKSceneOptions.appearance as for appearance.lua, set in the server's context too.
-- Run: ./gatekit --instance <copy> exec server "$(cat stand/creation/appearance_server.lua)"
local A = GKSceneOptions and GKSceneOptions.appearance
if not (A and A.appearance) then return { ok = false, error = "GKSceneOptions.appearance.appearance is not set" } end
if A.who ~= "guardian" then return { ok = false, error = "for the guardian only: the character's definition goes by the client's own message" } end
local definition = Ext.Entity.GetAllEntitiesWithComponent("CCCompanionDefinition")[1]
if not definition then return { ok = false, error = "no guardian definition: not in character creation" } end
local a, visual = A.appearance, definition.CCCompanionDefinition.Visual
local ok, err = pcall(function()
    visual.Visuals = a.Visuals
    visual.Elements = a.Elements
    if a.AdditionalChoices then visual.AdditionalChoices = a.AdditionalChoices end
    visual.EyeColor = a.EyeColor
    visual.SecondEyeColor = a.SecondEyeColor or a.EyeColor
    visual.HairColor = a.HairColor
    visual.SkinColor = a.SkinColor
    if A.voice then definition.CCCompanionDefinition.Voice = A.voice end
    definition:Replicate("CCCompanionDefinition")
end)
return { ok = ok, error = (not ok) and tostring(err) or nil, visuals = #visual.Visuals, skin = visual.SkinColor }
