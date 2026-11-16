-- A figure can stand in a level with no server entity behind it. The game builds such figures
-- itself: an actor of a timeline that names a template and has no entity of the level gets a
-- timeline dummy, made on the client by code:ecl::TLPreviewDummyHelpers::CreateDummy,
-- code:ecl::TLPreviewDummyHelpers::SetupDummy, the template overload of PrePrepareDummy,
-- code:ecl::TLPreviewDummyHelpers::LoadDummy and, when the visual is loaded,
-- code:ecl::TLPreviewDummyHelpers::AttachDummyToScene. Ext.ClientVisual.CreateDummy of GateKit's
-- Script Extender calls the same five for a character root template.
-- The rule held here: the entity is answered at once and carries TLPreviewDummy with the
-- template's id; within seconds it has a loaded visual at the place asked for and a scene
-- attachment; it is neither a character of the client nor known to the server by its uuid; after
-- Ext.ClientVisual.DestroyDummy the uuid names nothing. What a person sees: `figure.jpg` in the
-- test's folder, Vlaakith two metres from the host.
-- Staging (cheat): the figure and its removal. Reading: observation.
-- Run: gatekit --instance <copy> test tests/figure -k client_dummy

test { level = "GustavX:Basic_Level_A",
       about = "A timeline dummy made on the client from a character root template is drawn in the level and has no server entity; destroying it leaves nothing." }

local VLAAKITH = "8bdc382b-a31e-4c1c-8987-656ceb333f05"

function run(game)
    game.wait_state("Running", "server", 120)
    need(game.client("return Ext.ClientVisual ~= nil and Ext.ClientVisual.CreateDummy ~= nil"),
        "this Script Extender build has no Ext.ClientVisual.CreateDummy")

    local made = step("a figure two metres from the host", "cheat", function()
        return game.client(string.format([[
            local host
            for _, e in ipairs(Ext.Entity.GetAllEntitiesWithComponent("ClientControl")) do host = e break end
            local p = host.Transform.Transform.Translate
            local at = { p[1] + 2, p[2], p[3] }
            GKTestFigure = Ext.ClientVisual.CreateDummy(%q, at, { 0, 0, 0, 1 })
            return { made = GKTestFigure ~= nil, at = at } ]], VLAAKITH))
    end)
    check(made.made, "the verb answered an entity", made)

    step("its look is loaded and put into the scene", "observation", function()
        game.wait("client", "GKTestFigure.Visual ~= nil and GKTestFigure.Visual.Visual ~= nil and GKTestFigure.SceneAttach ~= nil", 20)
        local seen = game.client([[
            local e = GKTestFigure
            return { template = e.TLPreviewDummy.OriginalCharacterTemplate, uuid = e.Uuid.EntityUuid,
                     at = e.Visual.Visual.WorldTransform.Translate, level = e.Level.LevelName,
                     character = e.ClientCharacter ~= nil or e.IsCharacter ~= nil } ]])
        eq(seen.template, VLAAKITH, "the dummy names its template")
        eq(seen.level, "Basic_Level_A", "it stands in the client's level")
        for i = 1, 3 do near(seen.at[i], made.at[i], 0.01, "the visual stands where it was asked") end
        check(not seen.character, "it is no character of the client")
        made.uuid = seen.uuid
        game.shot("figure.jpg", 0)
    end)

    step("the server knows nothing of it", "observation", function()
        local server = game.server(string.format("return { entity = Ext.Entity.Get(%q) ~= nil, exists = Osi.Exists(%q) }", made.uuid, made.uuid))
        check(not server.entity and server.exists == 0, "no server entity under the dummy's uuid", server)
    end)

    step("removed", "cheat", function()
        check(game.client("return Ext.ClientVisual.DestroyDummy(GKTestFigure)"), "the verb removed a timeline dummy")
    end)

    step("nothing is left", "observation", function()
        game.wait("client", string.format("Ext.Entity.Get(%q) == nil", made.uuid), 10)
        check(not game.client("local gone = Ext.ClientVisual.DestroyDummy(GKTestFigure) GKTestFigure = nil return gone"),
            "a second removal finds no dummy")
    end)
end
