-- Past the camera's snap distance (the switches' TargetSnapDistance, 50 m) the game does not glide
-- (research/studies/camera-focus.md, "The long way"): onto a point it cuts, and onto a creature the
-- client holds whole it fades to black, cuts, holds the black while the place gets ready and fades
-- in. A creature that far from the party is asleep and a stub on the client, so GK.Camera.Focus
-- with options.wake wakes it first - "one": the creature alone (GK.Wake.One), "place": the space
-- around it (GK.Wake.Place) - and then the fade comes and the creature is drawn when it lifts
-- (docs/contracts/lua-api.md, section 22). The camera does not go farther from its character than
-- the switches' MaxDistFromChar, so the focus raises that leash; GK.Camera.Return fades back,
-- lets the creature sleep again and puts the leash back.
-- Stands on test:camera/focus/matrix (the focus), test:wake/one and test:wake/place (the waking).
-- Limit: 20 s a case - two fades of the game's own, each holding the black for over a second.
-- Staging (cheat): the camera on the host, a neutral human 60 m to the screen's right; the focus
-- with a wake is a cheat. Acting (fair): the focus on a point, the return. Reading (observation):
-- the answers, GK.Wake.State, the switches.
-- Run: gatekit --instance <copy> test tests -k camera/focus/far --speed working

test { level = "GustavX:Basic_Level_A",
       about = "Past the snap distance the camera cuts to a point and fades to a creature woken first; the return fades back, and the creature sleeps and the leash is as before." }

local F = require("_focus")

local METRES = 60

local function leash(game)
    return game.client("return Ext.Utils.GetGlobalSwitches().CameraSwitches[1].MaxDistFromChar")
end

case("point", function(game)
    game.wait_state("Running", "server", 120)
    local p = step("the camera on the host, a point " .. METRES .. " m to the screen's right", "cheat", function()
        F.home(game)
        local _, point = F.place(game, METRES)
        return point
    end)
    local a = step("focus the point", "fair", function()
        local answer = game.server(string.format("return GK.Camera.Focus(%s)", F.literal(p)), 30)
        check(answer.ok, "the camera settled on the point", answer)
        return answer
    end)
    step("a cut, no fade, nothing to draw", "observation", function()
        eq(a.far, true, "the move is past the snap distance")
        eq(a.snapped, true, "the camera cut")
        eq(a.faded, false, "no fade onto a point")
        eq(a.drawn, nil, "a point has nothing to draw")
        check(F.flat(a.at, p) <= 0.5, "the camera's target within 0.5 m (x, z) of the point", a.at)
    end)
    step("the camera back", "fair", function()
        local back = game.server("return GK.Camera.Return()", 30)
        check(back.ok, "the camera settled on the host", back)
    end)
end)

local function woken(how)
    return function(game)
        game.wait_state("Running", "server", 120)
        local before = leash(game)
        local who = step("the camera on the host, a human " .. METRES .. " m to the screen's right", "cheat", function()
            F.home(game)
            local _, p = F.place(game, METRES)
            return F.uuid(game.server(string.format("return GK.Stage.Spawn{ template = 'human', at = { %f, %f, %f }, faction = 'neutral' }[1]",
                p[1], p[2], p[3])))
        end)
        step("asleep there", "observation", function()
            game.wait("server", string.format("Ext.Entity.Get(%q) ~= nil and not Ext.Entity.Get(%q).Active", who, who), 10)
            local s = game.server(string.format("return GK.Wake.State(%q)", who))
            need(s.ok and not s.awake and not s.drawn, "the human is asleep and not drawn before the focus")
        end)
        local a = step("focus it, waking " .. how, "cheat", function()
            local answer = game.server(string.format("return GK.Camera.Focus(%q, { wake = %q })", who, how), 60)
            check(answer.ok, "the camera settled on it", answer)
            return answer
        end)
        step("faded, and the creature drawn there", "observation", function()
            game.save("far-" .. how .. ".txt", string.format(
                "%s: wake %d server ticks, the focus %d client ticks, black %d, fade %d, server ticks in all %d\n",
                how, a.wakeTicks or -1, a.ticks or -1, a.blackTicks or -1, a.fadeTicks or -1, a.serverTicks or -1))
            eq(a.far, true, "the move is past the snap distance")
            eq(a.faded, true, "the game's fade came")
            check((a.blackTicks or 0) >= 1, "the black held", a.blackTicks)
            eq(a.drawn, true, "the creature is drawn when the fade has lifted")
            eq(a.leashRaised, true, "the leash was raised for the move")
            check(F.flat(a.at, F.where(game, who)) <= 0.5, "the camera's target within 0.5 m (x, z) of the creature", a.at)
            local s = game.server(string.format("return GK.Wake.State(%q)", who))
            check(s.awake and s.drawn, "awake on the server, whole on the client", s)
            eq(s.rule, how == "one" and "kept up" or "place", "what keeps it awake")
        end)
        local back = step("the camera back", "fair", function()
            local answer = game.server("return GK.Camera.Return()", 60)
            check(answer.ok, "the camera settled on the host, the wake released", answer)
            return answer
        end)
        step("faded back, the creature asleep, the leash as before", "observation", function()
            eq(back.faded, true, "the game's fade came on the way back")
            check(back.released and back.released.ok and back.released.sleptCount >= 1, "the creature slept again", back.released)
            local s = game.server(string.format("return GK.Wake.State(%q)", who))
            eq(s.awake, false, "asleep on the server")
            eq(s.rule, "none", "nothing keeps it")
            eq(leash(game), before, "the switches' MaxDistFromChar as before")
            local host = game.server("return { Osi.GetPosition(Osi.GetHostCharacter()) }")
            check(F.flat(back.at, host) <= 0.5, "the camera's target within 0.5 m (x, z) of the host", back.at)
        end)
    end
end

case("creature woken alone", woken("one"))
case("creature with its place woken", woken("place"))
