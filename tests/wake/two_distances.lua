-- Far from the party the game stops a creature twice over, by two distances of its own
-- (research/studies/camera-focus.md, "Who wakes the space"). The server's activation manager keeps
-- a character awake - Active, animating, thinking - within its waking distance of a party member
-- and puts it to sleep beyond its sleeping distance (40 m and 45 m, read live with GK.Wake.Radii).
-- The client is sent a creature whole by a shorter distance of its own (about 32 m; where the game
-- holds it is not found), so a creature just inside the waking distance is awake and not drawn.
-- The control is a human 10 m off, whole on the client: when it has arrived, the others have been
-- judged as well.
-- Staging (cheat): three neutral humans along +x from the host: 10 m, 3 m inside the waking distance,
-- 5 m beyond the sleeping distance. Reading (observation): GK.Wake.Radii, GK.Wake.State.
-- Run: gatekit --instance <copy> test tests -k wake/two_distances --speed working

test { level = "GustavX:Basic_Level_A",
       about = "A creature just inside the manager's waking distance of the party is awake and not drawn; one beyond its sleeping distance is asleep." }

local W = require("_wake")

function run(game)
    game.wait_state("Running", "server", 120)
    local radii = step("the manager's distances", "observation", function() return W.radii(game) end)
    local inside, beyond = radii.wake - 3, radii.sleep + 5
    local cast = step("humans at 10 m, inside the waking distance, beyond the sleeping", "cheat", function()
        return { close = W.human(game, 10), inside = W.human(game, inside), beyond = W.human(game, beyond) }
    end)
    step("the control is whole on the client, the far one judged", "observation", function()
        game.wait("client", string.format("Ext.Entity.Get(%q) ~= nil and Ext.Entity.Get(%q).GameObjectVisual ~= nil", cast.close, cast.close), 10)
        W.asleep(game, cast.beyond)
        local s = W.state(game, cast.close)
        need(s.awake and s.drawn, "the human 10 m off is awake and drawn")
    end)
    step("inside the waking distance: awake, not drawn", "observation", function()
        local s = W.state(game, cast.inside)
        check(math.abs(s.distance - inside) <= 1, "it stands " .. inside .. " m from the host", s.distance)
        eq(s.awake, true, "awake on the server")
        eq(s.rule, "party", "kept awake by the party")
        eq(s.drawn, false, "not whole on the client: not drawn")
    end)
    step("beyond the sleeping distance: asleep", "observation", function()
        local s = W.state(game, cast.beyond)
        check(math.abs(s.distance - beyond) <= 1, "it stands " .. beyond .. " m from the host", s.distance)
        eq(s.awake, false, "asleep on the server")
        eq(s.rule, "none", "nothing keeps it")
        eq(s.drawn, false, "not drawn")
    end)
end
