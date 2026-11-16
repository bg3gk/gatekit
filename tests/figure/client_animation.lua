-- A figure made on the client moves when the client itself tells its animation blueprint to play.
-- The game plays a gameplay animation by sending the blueprint the event EoCGE::ASAnimation
-- (three sets of animation short names - start, loop, end - a float and a flag): the client's
-- action state code:ecl::ASAnimation::Update does it through
-- code:ls::AnimationBlueprintSystem::TriggerGameplayEvent<EoCGE::ASAnimation>, and leaves with
-- code:ls::AnimationBlueprintSystem::TriggerGameplayEvent<EoCGE::ASCancelAnimation>.
-- Ext.ClientVisual.PlayAnimation and StopAnimation of GateKit's Script Extender call the same two
-- for any entity that carries AnimationBlueprint. A short name is looked up in the entity's
-- animation sets, so the animation resource is first linked into them under its own id.
-- The rule held here, on a timeline dummy with no server entity: before the event the figure
-- rests and raises nothing; after it the blueprint raises the animation's text keys for the
-- figure, the animation's root motion moves the entity's Transform, and when the animation is
-- over the blueprint raises gameplay events for the figure and the Transform rests again; a loop
-- raises no end by itself and ends when it is stopped. An entity with no animation blueprint is
-- refused. What a person sees: `walks.jpg` in the test's folder, the figure in mid turn.
-- Staging (cheat): the figure, the links, the animations. Reading: observation.
-- Run: gatekit --instance <copy> test tests/figure -k client_animation

test { level = "GustavX:Basic_Level_A",
       about = "A client-only timeline dummy plays an animation resource when its blueprint is sent the ASAnimation event: text keys, root motion, an end; a loop ends when stopped." }

local CIVILIAN = "19dc6cab-aa38-4f09-b9cf-546e1c0ecb8a" -- Humans_Female_Civilian
local WALK = "f427f411-ec56-4a0a-a921-68b591b080e8"      -- HUM_F_Rig_SCENE_LOW_BalTmpl_InWrldAbduction_Nested_OrinVanish: a turn and some steps, nine seconds
local FEAR = "122199f3-ca4d-6b06-bca7-7f276af20883"      -- HUM_F_Rig_DFLT_CINE_Fear_Loop_01

-- The watcher: every tick, the text keys and the gameplay events the animation blueprints raised
-- for the figure (the two singletons of the animation blueprint system), and how far the figure
-- is from where it stood when the watcher was reset.
local WATCH = [[
    GKTestAnim = { keys = 0, events = {}, far = 0, last = 0 }
    local W = GKTestAnim
    local who = GKTestFigure
    local gameplay = Ext.Entity.GetAllEntitiesWithComponent("AnimationGameplayEventsSingleton")[1]
    local textkeys = Ext.Entity.GetAllEntitiesWithComponent("AnimationTextKeyEventsSingleton")[1]
    local function place() local p = who.Transform.Transform.Translate return { p[1], p[2], p[3] } end
    W.from = place()
    function W.reset() W.keys, W.events, W.far, W.from, W.movedAt = 0, {}, 0, place(), nil end
    W.subscription = Ext.Events.Tick:Subscribe(function()
        pcall(function()
            for _, e in ipairs(gameplay.AnimationGameplayEventsSingleton.Events) do
                if e.Entity == who then W.events[#W.events + 1] = tostring(e.Event) end
            end
            for _, e in ipairs(textkeys.AnimationTextKeyEventsSingleton.Events) do
                if e.Entity == who then W.keys = W.keys + 1 end
            end
            local p = place()
            local d = math.sqrt((p[1] - W.from[1]) ^ 2 + (p[3] - W.from[3]) ^ 2)
            if math.abs(d - W.last) > 0.0005 then W.movedAt = Ext.Utils.MonotonicTime() end
            W.last = d
            if d > W.far then W.far = d end
        end)
    end)
    return gameplay ~= nil and textkeys ~= nil ]]

function run(game)
    game.wait_state("Running", "server", 120)
    need(game.client("return Ext.ClientVisual ~= nil and Ext.ClientVisual.PlayAnimation ~= nil and Ext.ClientVisual.StopAnimation ~= nil"),
        "this Script Extender build has no Ext.ClientVisual.PlayAnimation")

    step("a figure two metres from the host, its look loaded", "cheat", function()
        check(game.client(string.format([[
            local host
            for _, e in ipairs(Ext.Entity.GetAllEntitiesWithComponent("ClientControl")) do host = e break end
            local p = host.Transform.Transform.Translate
            GKTestFigure = Ext.ClientVisual.CreateDummy(%q, { p[1] + 2, p[2], p[3] }, { 0, 0, 0, 1 })
            return GKTestFigure ~= nil ]], CIVILIAN)), "the figure was made")
        game.wait("client", "GKTestFigure.Visual ~= nil and GKTestFigure.Visual.Visual ~= nil and GKTestFigure.SceneAttach ~= nil and GKTestFigure.AnimationWaterfall ~= nil", 20)
    end)

    step("the two animations linked into the figure's animation sets under their own ids", "cheat", function()
        local sets = game.client(string.format([[
            local n = 0
            for _, w in ipairs(GKTestFigure.AnimationWaterfall.Waterfall) do
                if tostring(w.Type) == "Visual" and tostring(w.Slot) == "" then
                    local ok = pcall(function()
                        local subset = Ext.Resource.Get(w.Resource, "AnimationSet").AnimationBank.AnimationSubSets[""]
                        for _, id in ipairs({ %q, %q }) do subset.Animation[id] = { ID = id, flags = {} } end
                    end)
                    if ok then n = n + 1 end
                end
            end
            return n ]], WALK, FEAR))
        check(sets > 0, "at least one animation set of the figure took the links", sets)
    end)

    step("left alone it rests and raises nothing", "observation", function()
        check(game.client(WATCH), "both singletons of the animation blueprint system are there")
        game.sleep(1.5)
        local quiet = game.client("return { keys = GKTestAnim.keys, events = #GKTestAnim.events, far = GKTestAnim.far }")
        eq(quiet.keys, 0, "no text key of the figure")
        eq(quiet.events, 0, "no gameplay event of the figure")
        near(quiet.far, 0, 0.001, "the figure did not move")
    end)

    step("told to play, it walks: text keys, root motion, then an end", "observation", function()
        check(game.client(string.format("GKTestAnim.reset() return Ext.ClientVisual.PlayAnimation(GKTestFigure, { %q })", WALK)),
            "the verb sent the event")
        game.wait("client", "GKTestAnim.far > 0.05", 15)
        game.shot("walks.jpg", 0)
        game.wait("client", "#GKTestAnim.events > 0", 20)
        local seen = game.client("return { keys = GKTestAnim.keys, events = GKTestAnim.events, far = GKTestAnim.far }")
        check(seen.keys > 0, "the blueprint raised text keys of the animation for the figure", seen)
        check(seen.far > 0.5, "the root motion moved the entity's Transform", seen)
        game.sleep(1)
        check(game.client("return Ext.Utils.MonotonicTime() - (GKTestAnim.movedAt or 0) > 800"), "after the end the figure rests")
    end)

    step("a loop has no end until it is stopped", "observation", function()
        check(game.client(string.format("GKTestAnim.reset() return Ext.ClientVisual.PlayAnimation(GKTestFigure, {}, { %q })", FEAR)),
            "the verb sent the event with a loop only")
        game.sleep(3)
        eq(game.client("return #GKTestAnim.events"), 0, "no end is raised while it loops")
        check(game.client("return Ext.ClientVisual.StopAnimation(GKTestFigure)"), "the verb sent the cancel event")
        game.wait("client", "#GKTestAnim.events > 0", 5)
    end)

    step("an entity with no animation blueprint is refused", "observation", function()
        check(not game.client(string.format([[
            local camera = Ext.Entity.GetAllEntitiesWithComponent("Camera")[1]
            return Ext.ClientVisual.PlayAnimation(camera, { %q }) ]], WALK)), "a camera plays nothing")
    end)

    step("removed", "cheat", function()
        game.client([[
            if GKTestAnim and GKTestAnim.subscription then Ext.Events.Tick:Unsubscribe(GKTestAnim.subscription) end
            GKTestAnim = nil
            local gone = Ext.ClientVisual.DestroyDummy(GKTestFigure)
            GKTestFigure = nil
            return gone ]])
    end)
end
