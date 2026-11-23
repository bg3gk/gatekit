-- When the pathfinder does not reach the target of `go` or `use`, the bridge says why, from the
-- game's own answers (GK.See.Path, GK.Act.Alternatives) and the passages the agent knows of
-- (GK.See.Passages): on another floor (how far above or below) or on the same height with the way
-- going round, and the nearest known ladder, door or portal, with the call that uses it. In blind
-- mode a target never in a member's sight is refused before any walk.
-- Venue: the Nautiloid (TUT_Avernus_C). The helm's transmitter (bcbba417) stands on the helm's
-- deck, the height of the start room, which no walk joins to it; the lower deck under the helm is
-- 15 m below it. Staging (cheat): the session's mode; the host put on the lower deck (GK.Teleport).
-- Acting (ordered): ai_step `use`. Reading (observation): the answer.
-- Run: gatekit --instance <copy> test tests -k ai/no_path --speed working

test { level = "GustavX:TUT_Avernus_C",
       about = "A walk the pathfinder refuses says why - another floor or the way round - and names the nearest known way." }

local TRANSMITTER = "bcbba417"
local LOWER_DECK = { -39.0, 2.7, -372.0 }

-- The step's own answer: the bridge follows the observation the chunk returns.
local function useTransmitter(game)
    return game.server(string.format([[return Mods.GateKitAI.AI.Step({ ["do"] = "use", target = GK.See.Resolve(%q) })]], TRANSMITTER), 120)
end

local function answer(game, got)
    print("why not: " .. tostring(got.why_not))
    return got
end

local function start(game)
    game.wait_state("Running", "server", 120)
    step("the opening dialogue gone", "observation", function()
        game.wait("server", "not Mods.GateKitAI.AI.Util.inDialogue or not Mods.GateKitAI.AI.Util.inDialogue()", 60)
    end)
end

case("the same height, the way round", function(game)
    start(game)
    local mark = step("open mode; use the transmitter from the start room", "cheat", function()
        check(game.server("return Mods.GateKitAI.AI.Session.Start({ mode = 'open', plan = 'off' })").ok, "open mode")
        return useTransmitter(game)
    end)
    step("refused with the reason and a known way", "observation", function()
        local p = answer(game, mark)
        eq(p.ok, false, "the use is refused")
        check(contains(p.why_not, "no path:"), "a reason for no path", p.why_not)
        check(contains(p.why_not, "on your height"), "the same height, the way goes round", p.why_not)
        check(contains(p.why_not, "the nearest way you know"), "the nearest known way is named", p.why_not)
    end)
end)

case("another floor", function(game)
    start(game)
    local mark = step("open mode, the host on the lower deck; use the transmitter", "cheat", function()
        check(game.server("return Mods.GateKitAI.AI.Session.Start({ mode = 'open', plan = 'off' })").ok, "open mode")
        game.server(string.format("GK.Teleport(%f, %f, %f) return true", LOWER_DECK[1], LOWER_DECK[2], LOWER_DECK[3]))
        game.sleep(2)
        return useTransmitter(game)
    end)
    step("refused: above, on another floor, with the ladder up", "observation", function()
        local p = answer(game, mark)
        eq(p.ok, false, "the use is refused")
        check(contains(p.why_not, "above you, on another floor"), "it says the target is above, on another floor", p.why_not)
        check(contains(p.why_not, "(ladder): up"), "the nearest way is a ladder going up", p.why_not)
    end)
end)

case("blind, never seen", function(game)
    start(game)
    local mark = step("blind mode; use the transmitter from the start room", "cheat", function()
        game.server("GK.See.Forget() return true")
        check(game.server("return Mods.GateKitAI.AI.Session.Start({ mode = 'blind' })").ok, "blind mode")
        return useTransmitter(game)
    end)
    step("refused as not knowable, nothing walked", "observation", function()
        local p = answer(game, mark)
        eq(p.ok, false, "the use is refused")
        check(contains(p.why_not, "not knowable to a player"), "a target out of every member's sight is refused", p.why_not)
    end)
end)
