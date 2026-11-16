-- A creature asked to be kept up (Osi.SetForceUpdate) is an anchor of itself and of nobody else
-- (research/studies/camera-focus.md, "The anchors"): GK.Wake.One wakes that creature alone, the
-- client receives it whole and draws it, and its neighbour three metres off sleeps on;
-- GK.Wake.Release lets it sleep again (docs/contracts/lua-api.md, "GK.Wake").
-- Stands on test:wake/two_distances (a creature beyond the sleeping distance is asleep).
-- Staging (cheat): two neutral humans 5 m beyond the manager's sleeping distance, 3 m apart; the
-- wake and its release. Reading (observation): the answers, GK.Wake.State.
-- Run: gatekit --instance <copy> test tests -k wake/one --speed working

test { level = "GustavX:Basic_Level_A",
       about = "GK.Wake.One makes one sleeping creature awake and drawn and leaves its neighbour asleep; the release lets it sleep again." }

local W = require("_wake")

function run(game)
    game.wait_state("Running", "server", 120)
    local radii = step("the manager's distances", "observation", function() return W.radii(game) end)
    local cast = step("two humans beyond the sleeping distance", "cheat", function()
        return { one = W.human(game, radii.sleep + 5), other = W.human(game, radii.sleep + 5, 3) }
    end)
    step("both asleep", "observation", function()
        W.asleep(game, cast.one)
        W.asleep(game, cast.other)
        local s = W.state(game, cast.one)
        need(not s.awake and not s.drawn, "the human is asleep and not drawn before the wake")
    end)
    local woke = step("keep the one awake", "cheat", function()
        local a = game.server(string.format("return GK.Wake.One(%q)", cast.one), 30)
        check(a.ok, "awake on the server and whole on the client", a)
        return a
    end)
    step("awake and drawn, alone", "observation", function()
        game.save("one.txt", string.format("one: awake after %d server ticks, whole after %d client ticks more\n",
            woke.awakeTicks or -1, woke.clientTicks or -1))
        local s = W.state(game, cast.one)
        check(s.awake and s.drawn, "awake on the server, drawn on the client", s)
        eq(s.rule, "kept up", "kept up by the wake")
        local other = W.state(game, cast.other)
        eq(other.awake, false, "the neighbour 3 m off is still asleep")
        eq(other.drawn, false, "and not drawn")
    end)
    local released = step("let it go", "cheat", function()
        local a = game.server(string.format("return GK.Wake.Release(%q)", cast.one), 30)
        check(a.ok, "the release finished", a)
        return a
    end)
    step("asleep again", "observation", function()
        eq(released.sleptCount, 1, "the one slept again")
        game.wait("client", string.format("Ext.Entity.Get(%q) == nil or not Ext.Entity.Get(%q).Level", cast.one, cast.one), 10)
        local s = W.state(game, cast.one)
        eq(s.awake, false, "asleep on the server")
        eq(s.drawn, false, "a stub on the client again")
        eq(s.rule, "none", "nothing keeps it")
    end)
end
