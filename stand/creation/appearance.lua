-- Scene "appearance" (the video stand): the character being created, or the guardian, takes a saved
-- look. Client context, in character creation (the level SYS_CC_I), after the race, the subrace and
-- the body type are chosen in the creation window itself (README, "The saved character").
--
-- The look is given as the saved character's own components, by their own field names:
-- GKSceneOptions = { appearance = {
--     who = "character" | "guardian",                       -- "character" when absent
--     appearance = <CharacterCreationAppearance: Visuals, Elements, AdditionalChoices, EyeColor,
--                   SecondEyeColor, HairColor, SkinColor>,
--     stats = { Name = "...", PointBuyAbilities = {...} },  -- of CharacterCreationStats; the character only
--     voice = <Voice.Voice> } }
-- Run:   ./gatekit --instance <copy> exec client "$(cat stand/creation/appearance.lua)"
-- State: GKScene.appearance = { ok, who, log, before, after }
--
-- The scene writes the client's side only. The server takes the definition from the client's own
-- message, which the window's next change sends whole (README, "The saved character").
GKScene = GKScene or {}
local A = GKSceneOptions and GKSceneOptions.appearance
if not (A and A.appearance) then return { ok = false, error = "GKSceneOptions.appearance.appearance is not set" } end
local guardian = A.who == "guardian"
local definition = Ext.Entity.GetAllEntitiesWithComponent(guardian and "CCCompanionDefinition" or "CCCharacterDefinition")[1]
if not definition then return { ok = false, error = "no creation definition: not in character creation" } end

local S = { ok = true, who = guardian and "guardian" or "character", log = {} }
GKScene.appearance = S
local function set(what, fn)
    local ok, err = pcall(fn)
    if not ok then S.ok = false S.log[#S.log + 1] = what .. ": " .. tostring(err) end
end
local function look(visual)
    return { visuals = #visual.Visuals, elements = #visual.Elements, skin = visual.SkinColor, hair = visual.HairColor, eyes = visual.EyeColor }
end
local a = A.appearance
local function write(visual)
    visual.Visuals = a.Visuals
    visual.Elements = a.Elements
    if a.AdditionalChoices then visual.AdditionalChoices = a.AdditionalChoices end
    visual.EyeColor = a.EyeColor
    visual.SecondEyeColor = a.SecondEyeColor or a.EyeColor
    visual.HairColor = a.HairColor
    visual.SkinColor = a.SkinColor
end

-- The client keeps a definition in three places: what it edits and sends (the character:
-- ClientCCBaseDefinitionState.Definition; the guardian: ClientCCCompanionDefinition), what the dummy
-- is drawn from (ClientCCDummyDefinition) and the looks remembered by root template
-- (ClientCharacterCreationAppearanceCache).
local pending, template
if guardian then
    pending = definition.ClientCCCompanionDefinition
    template = pending.RootTemplate
else
    pending = definition.ClientCCBaseDefinitionState.Definition
    template = pending.Definition.RootTemplate
end
local dummy = definition.ClientCCDummyDefinition
S.before = look(dummy.Visual)
set("pending definition", function() write(pending.Visual) end)
set("dummy definition", function() write(dummy.Visual) end)
set("remembered look", function()
    local kept = definition.ClientCharacterCreationAppearanceCache.Appearances[template]
    if kept then write(kept) end
end)
if guardian then
    if A.voice then set("voice", function() pending.Voice = A.voice end) end
else
    if A.stats and A.stats.Name then set("name", function() pending.Name = A.stats.Name end) end
    if A.stats and A.stats.PointBuyAbilities then set("abilities", function() pending.Abilities = A.stats.PointBuyAbilities end) end
    if A.voice then set("voice", function() pending.Definition.Voice = A.voice end) end
end
-- The dummy's picture is rebuilt when ChangeId2 of its definition moves.
set("redraw", function() dummy.ChangeId2 = dummy.ChangeId2 + 1 end)
S.after = look(dummy.Visual)
return S
