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
-- "Nothing is raised" is an absence: it is read over a count of the client's frames the watcher
-- itself counts (120 for the figure at rest, 300 for the loop), not over a pause.
-- The links are written into animation set resources the figure shares with every figure of its
-- kind in the process; the last step takes them out again, and a run that fails before it leaves
-- two unused entries in those sets until the game restarts (the client has no ledger to undo it).
-- Staging (cheat): the figure, the links, the animations played and stopped, the removal.
-- Reading: observation.
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
    GKTestAnim = { keys = 0, events = {}, far = 0, last = 0, frames = 0, still = 0 }
    local W = GKTestAnim
    local who = GKTestFigure
    local gameplay = Ext.Entity.GetAllEntitiesWithComponent("AnimationGameplayEventsSingleton")[1]
    local textkeys = Ext.Entity.GetAllEntitiesWithComponent("AnimationTextKeyEventsSingleton")[1]
    local function place() local p = who.Transform.Transform.Translate return { p[1], p[2], p[3] } end
    W.from = place()
    -- frames: the client's frames watched since the reset; still: the frames since the figure last moved
    function W.reset() W.keys, W.events, W.far, W.from, W.frames, W.still = 0, {}, 0, place(), 0, 0 end
    W.subscription = Ext.Events.Tick:Subscribe(function()
        W.frames = W.frames + 1
        pcall(function()
            for _, e in ipairs(gameplay.AnimationGameplayEventsSingleton.Events) do
                if e.Entity == who then W.events[#W.events + 1] = tostring(e.Event) end
            end
            for _, e in ipairs(textkeys.AnimationTextKeyEventsSingleton.Events) do
                if e.Entity == who then W.keys = W.keys + 1 end
            end
            local p = place()
            local d = math.sqrt((p[1] - W.from[1]) ^ 2 + (p[3] - W.from[3]) ^ 2)
            if math.abs(d - W.last) > 0.0005 then W.still = 0 else W.still = W.still + 1 end
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
        -- the sets written are remembered, so that the last step takes the links out of the same ones
        local sets = game.client(string.format([[
            GKTestLinked = {}
            local sets = 0
            for _, w in ipairs(GKTestFigure.AnimationWaterfall.Waterfall) do
                if tostring(w.Type) == "Visual" and tostring(w.Slot) == "" then
                    local ok = pcall(function()
                        local subset = Ext.Resource.Get(w.Resource, "AnimationSet").AnimationBank.AnimationSubSets[""]
                        for _, id in ipairs({ %q, %q }) do
                            if subset.Animation[id] == nil then
                                subset.Animation[id] = { ID = id, flags = {} }
                                GKTestLinked[#GKTestLinked + 1] = { w.Resource, id }
                            end
                        end
                    end)
                    if ok then sets = sets + 1 end
                end
            end
            return sets ]], WALK, FEAR))
        check(sets > 0, "at least one animation set of the figure took the links", sets)
    end)

    step("the figure watched", "cheat", function()
        check(game.client(WATCH), "both singletons of the animation blueprint system are there")
    end)
    step("left alone it rests and raises nothing", "observation", function()
        game.wait("client", "GKTestAnim.frames >= 120", 30)
        local quiet = game.client("return { keys = GKTestAnim.keys, events = #GKTestAnim.events, far = GKTestAnim.far }")
        eq(quiet.keys, 0, "no text key of the figure")
        eq(quiet.events, 0, "no gameplay event of the figure")
        near(quiet.far, 0, 0.001, "the figure did not move")
    end)

    step("told to play the walk", "cheat", function()
        check(game.client(string.format("GKTestAnim.reset() return Ext.ClientVisual.PlayAnimation(GKTestFigure, { %q })", WALK)),
            "the verb sent the event")
    end)
    step("it walks: text keys, root motion, then an end", "observation", function()
        game.wait("client", "GKTestAnim.far > 0.05", 15)
        game.shot("walks.jpg", 0)
        game.wait("client", "#GKTestAnim.events > 0", 20)
        local seen = game.client("return { keys = GKTestAnim.keys, events = GKTestAnim.events, far = GKTestAnim.far }")
        check(seen.keys > 0, "the blueprint raised text keys of the animation for the figure", seen)
        check(seen.far > 0.5, "the root motion moved the entity's Transform", seen)
        game.wait("client", "GKTestAnim.still >= 20", 10)
    end)

    step("told to play a loop only", "cheat", function()
        check(game.client(string.format("GKTestAnim.reset() return Ext.ClientVisual.PlayAnimation(GKTestFigure, {}, { %q })", FEAR)),
            "the verb sent the event with a loop only")
    end)
    step("the loop raises no end by itself", "observation", function()
        game.wait("client", "GKTestAnim.frames >= 300", 60)
        eq(game.client("return #GKTestAnim.events"), 0, "no end is raised while it loops")
    end)
    step("the loop stopped", "cheat", function()
        check(game.client("return Ext.ClientVisual.StopAnimation(GKTestFigure)"), "the verb sent the cancel event")
    end)
    step("stopped, it ends", "observation", function()
        game.wait("client", "#GKTestAnim.events > 0", 5)
    end)

    step("an entity with no animation blueprint is refused", "cheat", function()
        check(not game.client(string.format([[
            local camera = Ext.Entity.GetAllEntitiesWithComponent("Camera")[1]
            return Ext.ClientVisual.PlayAnimation(camera, { %q }) ]], WALK)), "a camera plays nothing")
    end)

    step("removed: the watcher, the figure, the links", "cheat", function()
        local left = game.client([[
            if GKTestAnim and GKTestAnim.subscription then Ext.Events.Tick:Unsubscribe(GKTestAnim.subscription) end
            GKTestAnim = nil
            Ext.ClientVisual.DestroyDummy(GKTestFigure)
            GKTestFigure = nil
            local left = 0
            for _, link in ipairs(GKTestLinked or {}) do
                local resource, id = link[1], link[2]
                local subset = Ext.Resource.Get(resource, "AnimationSet").AnimationBank.AnimationSubSets[""]
                pcall(function() subset.Animation[id] = nil end)
                if subset.Animation[id] ~= nil then left = left + 1 end
            end
            GKTestLinked = nil
            return left ]])
        eq(left, 0, "links this run made that are still in the shared animation sets")
    end)
end
