-- An effect can be played by the client alone. The game makes an effect entity from an effect
-- resource and starts it with code:ls::EffectsManager::PlayEffect (the effect's name, the level,
-- the scene, a place, a rotation, a scale); what happens to it afterwards is ordered through the
-- effects manager's queue (stop, force stop, destroy). Ext.ClientVisual.PlayEffect of GateKit's
-- Script Extender calls the first, Ext.ClientVisual.StopEffect queues the orders.
-- The rule held here: the entity answered carries Effect, a Transform at the place asked for and
-- the Level of the entity it was played for; within a second its flags say Playing; an effect that
-- does not loop says Stopped by itself; a force stop says Stopped at once; a destroy takes the
-- Effect component away. The key is the effect's name: its resource id finds nothing.
-- What a person sees: `effect.jpg` in the test's folder, Orin's teleportation two metres from the host.
-- Staging (cheat): the effects and their orders. Reading: observation.
-- Run: gatekit --instance <copy> test tests/figure -k client_effect

test { level = "GustavX:Basic_Level_A",
       about = "An effect entity made on the client with the effects manager's own PlayEffect plays, ends by itself when it does not loop, and obeys the manager's stop and destroy orders." }

local EFFECT = "VFX_Cinematic_Orin_Teleportation_01" -- five seconds, does not loop
local EFFECT_ID = "09adce39-4b68-653a-576b-a94324521252"

local function flags(game)
    return game.client([[
        local fx = GKTestEffect and GKTestEffect.Effect
        if not fx then return { gone = true } end
        local out = {}
        for _, flag in ipairs(fx.Flags) do out[tostring(flag)] = true end
        return out ]])
end

local function play(game)
    return game.client(string.format([[
        local host
        for _, e in ipairs(Ext.Entity.GetAllEntitiesWithComponent("ClientControl")) do host = e break end
        local p = host.Transform.Transform.Translate
        GKTestAt = { p[1] + 2, p[2], p[3] }
        GKTestEffect = Ext.ClientVisual.PlayEffect(%q, GKTestAt, nil, 1.0, host)
        return GKTestEffect ~= nil ]], EFFECT))
end

function run(game)
    game.wait_state("Running", "server", 120)
    need(game.client("return Ext.ClientVisual ~= nil and Ext.ClientVisual.PlayEffect ~= nil and Ext.ClientVisual.StopEffect ~= nil"),
        "this Script Extender build has no Ext.ClientVisual.PlayEffect")
    need(game.client(string.format("return tostring(Ext.Resource.Get(%q, 'Effect').EffectName)", EFFECT_ID)) == EFFECT,
        "the game's data has no effect " .. EFFECT)

    step("an effect two metres from the host", "cheat", function()
        check(play(game), "the verb answered an entity")
    end)

    step("it stands where asked, in the host's level, and plays", "observation", function()
        local seen = game.client([[
            local e = GKTestEffect
            local host
            for _, h in ipairs(Ext.Entity.GetAllEntitiesWithComponent("ClientControl")) do host = h break end
            return { at = e.Transform.Transform.Translate, wanted = GKTestAt, level = e.Level and e.Level.LevelName,
                     hostLevel = host.Level.LevelName, visual = e.Visual ~= nil } ]])
        for i = 1, 3 do near(seen.at[i], seen.wanted[i], 0.01, "the effect's Transform is the place asked for") end
        eq(seen.level, seen.hostLevel, "it carries the level of the entity it was played for")
        game.wait("client", "(function() for _, f in ipairs(GKTestEffect.Effect.Flags) do if tostring(f) == 'Playing' then return true end end end)()", 5)
        game.shot("effect.jpg", 0)
    end)

    step("it ends by itself", "observation", function()
        game.wait("client", "(function() for _, f in ipairs(GKTestEffect.Effect.Flags) do if tostring(f) == 'Stopped' then return true end end end)()", 30)
        check(not flags(game).Playing, "a stopped effect no longer says Playing")
    end)

    step("a destroy order takes the Effect away", "observation", function()
        check(game.client("return Ext.ClientVisual.StopEffect(GKTestEffect, 'Destroy')"), "the order was queued")
        game.wait("client", "GKTestEffect.Effect == nil", 5)
        check(not game.client("return Ext.ClientVisual.StopEffect(GKTestEffect, 'Destroy')"), "a second order finds no effect")
    end)

    step("a force stop ends a playing effect at once", "observation", function()
        check(play(game), "a second effect was made")
        game.wait("client", "(function() for _, f in ipairs(GKTestEffect.Effect.Flags) do if tostring(f) == 'Playing' then return true end end end)()", 5)
        check(game.client("return Ext.ClientVisual.StopEffect(GKTestEffect, 'ForceStop')"), "the order was queued")
        game.wait("client", "(function() for _, f in ipairs(GKTestEffect.Effect.Flags) do if tostring(f) == 'Stopped' then return true end end end)()", 3)
        game.client("Ext.ClientVisual.StopEffect(GKTestEffect, 'Destroy') return true")
    end)

    step("the key is the effect's name", "observation", function()
        check(not game.client(string.format("return Ext.ClientVisual.PlayEffect(%q, GKTestAt) ~= nil", EFFECT_ID)),
            "the resource id of the same effect makes nothing")
        check(not game.client("return Ext.ClientVisual.PlayEffect('GK_no_such_effect', GKTestAt) ~= nil"), "an unknown name makes nothing")
        game.client("GKTestEffect, GKTestAt = nil, nil return true")
    end)
end
