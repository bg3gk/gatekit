-- Only an anchor that is a character of the party's view wakes other characters
-- (research/studies/camera-focus.md, "One creature, or a whole place"): GK.Wake.Place puts a helper
-- there - spawned asleep, taken off stage so that it is never drawn, given the party member's
-- component and appended to the party's view - and every creature within the manager's waking
-- distance of it wakes and is drawn, as around a party member; GK.Wake.Release takes the helper
-- away and they sleep again (docs/contracts/lua-api.md, section 23).
-- Stands on test:wake/two_distances (a creature beyond the sleeping distance is asleep).
-- Staging (cheat): two neutral humans 5 m beyond the manager's sleeping distance, 3 m apart; the
-- place woken around the first, and its release. Reading (observation): the answers, GK.Wake.State.
-- Run: gatekit --instance <copy> test tests -k wake/place --speed working

test { level = "GustavX:Basic_Level_A",
       about = "GK.Wake.Place wakes and draws a sleeping creature and its neighbour through a helper nobody sees; the release lets both sleep again." }

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
        local s = W.state(game, cast.other)
        need(not s.awake and not s.drawn, "the neighbour is asleep and not drawn before the wake")
    end)
    local woke = step("wake the place around the one", "cheat", function()
        local a = game.server(string.format("return GK.Wake.Place(%q)", cast.one), 30)
        check(a.ok, "the place is awake and whole on the client", a)
        return a
    end)
    step("both awake and drawn, the helper unseen", "observation", function()
        game.save("place.txt", string.format("place: %d server ticks in all, awake %d ticks after the helper joined, whole after %d client ticks more\n",
            woke.ticks or -1, woke.wakeTicks or -1, woke.clientTicks or -1))
        eq(woke.wokeCount, 2, "it woke the two")
        eq(woke.drawnCount, 2, "the client held both whole")
        eq(woke.hidden, true, "the helper is off stage, never drawn")
        local helper = game.client(string.format("local e = Ext.Entity.Get(%q) return e ~= nil and e.Level ~= nil", woke.helper))
        eq(helper, false, "the client holds no more than a stub of the helper")
        local s = W.state(game, cast.other)
        check(s.awake and s.drawn, "the neighbour is awake and drawn", s)
        eq(s.rule, "place", "kept awake by the place's helper")
    end)
    local released = step("let the place go", "cheat", function()
        local a = game.server(string.format("return GK.Wake.Release(%q)", woke.helper), 30)
        check(a.ok, "the release finished", a)
        return a
    end)
    step("both asleep again, the helper gone", "observation", function()
        eq(released.sleptCount, 2, "the two slept again")
        eq(W.state(game, cast.one).awake, false, "the one is asleep")
        eq(W.state(game, cast.other).awake, false, "the neighbour is asleep")
        game.wait("server", string.format("Ext.Entity.Get(%q) == nil", woke.helper), 10)
        local party = game.server("return #GK.Stage.Party()")
        eq(party, 1, "the party is the host alone")
    end)
end
