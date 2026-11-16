-- GK.Camera.Focus moves the game's camera onto a thing the way the game itself moves it, and the
-- answer comes when the camera has settled; GK.Camera.Return brings it back to the controlled
-- character at the distance it had before the focus (docs/contracts/lua-api.md, "The camera's focus"). The
-- rule held here, over the kinds of thing and the distances that differ: after the focus the
-- camera's target stands within half a metre (x, z) of the thing, the thing is near the middle of
-- the screen (GK.Camera.OnScreen, from the camera's own matrices), and the camera settled within
-- a limit of server ticks; after the return the camera's target is on the host and its distance
-- is the one before the focus (the game itself moves the distance when its targets change, so
-- the saved one is written back). Kinds: a point of the ground, an object (a chest), a creature
-- (a neutral human, never attacked). Distances, to the right of the screen from the host: near
-- (3 m, on screen already), middling (10 m, at the screen's edge) and far (50 m, well outside:
-- the client holds a stub of a creature or an item there, so the verb is called on the server,
-- which sends the server's point; past the game's snap distance of 50 m the camera would cut, so
-- the far cases ask for a glide). Near and middling differ only in the limit, so each is held
-- once; far is held for each kind, because there the answer says whether the thing is drawn: a
-- creature 50 m from the party is asleep and a stub on the client, so the camera shows its place
-- and the answer says `drawn = false` with a warning; the chest's stub is drawn all the same; a
-- point has no `drawn` (research/studies/camera-focus.md; the far creature woken first:
-- test:camera/focus/far).
-- Limits: 45, 60 and 90 server ticks - the camera's own glide takes up to about a second within
-- 18 m and a second and a half for a raised-snap glide of 60 m, at 30 server ticks a second, with
-- room for the answer's way back from the client.
-- Staging (cheat): the camera put back on the host, the thing spawned. Acting (fair): the focus, on
-- the server, and the return. Reading (observation): the answers, the server's position, the screen.
-- Run: gatekit --instance <copy> test tests/camera -k camera/focus/matrix --speed working

test { level = "GustavX:Basic_Level_A",
       about = "GK.Camera.Focus brings the camera onto a point, an object or a creature - near, at the screen's edge or far - near the middle of the screen, settled within a limit; GK.Camera.Return puts it back on the host at its old distance." }

local F = require("_focus")

local KINDS = {
    point = nil,          -- a point of the ground
    object = "chest",
    npc = "human",
}
local RANGES = {
    near = { metres = 3, budget = 45 },
    middling = { metres = 10, budget = 60 },
    far = { metres = 50, budget = 90, glide = true },
}

local function focusCase(kind, range)
    local r = RANGES[range]
    return function(game)
        game.wait_state("Running", "server", 120)
        local host, before
        local thing = step("the camera on the host, the thing " .. r.metres .. " m to the screen's right", "cheat", function()
            F.home(game)
            before = game.client("return GK.Camera.State()")
            local h, p = F.place(game, r.metres)
            host = h
            if not KINDS[kind] then return p end
            return F.uuid(game.server(string.format("return GK.Stage.Spawn{ template = %q, at = { %f, %f, %f }, faction = 'neutral' }[1]",
                KINDS[kind], p[1], p[2], p[3])))
        end)
        local at = F.where(game, thing)
        step("where it stands on the screen before", "observation", function()
            local s = F.screen(game, at)
            if range == "near" then
                need(s.inside and s.u < 0.8, "near: on the screen already, off its middle")
            elseif range == "middling" then
                need(s.inFront and s.u >= 0.9, "middling: at the screen's right edge or just past it")
            else
                need(not s.inside, "far: off the screen")
            end
        end)
        local a = step("focus it", "fair", function()
            local answer = game.server(string.format("return GK.Camera.Focus(%s, { glide = %s })",
                F.literal(thing), tostring(r.glide == true)), 30)
            check(answer.ok, "the camera settled on it", answer)
            return answer
        end)
        step("the camera's target on it, the thing in the middle, in time", "observation", function()
            local off = F.flat(a.at, at)
            local s = F.screen(game, at)
            game.save("settle-" .. kind .. "-" .. range .. ".txt", string.format(
                "%s %s: %d server ticks (%d client ticks, %d ms), %.2f m off, u %.2f v %.2f, how %s, snapped %s\n",
                kind, range, a.serverTicks or -1, a.ticks or -1, a.waitedMs or -1, off, s.u or -1, s.v or -1,
                tostring(a.how), tostring(a.snapped)))
            check(off <= 0.5, "the camera's target within 0.5 m (x, z) of the thing", off, a.at, at)
            check(s.inside and math.abs(s.u - 0.5) <= 0.1 and s.v >= 0.4 and s.v <= 0.7,
                "the thing near the middle of the screen", s)
            check(a.serverTicks <= r.budget, "settled within " .. r.budget .. " server ticks", a.serverTicks)
            check(not a.snapped, "the camera glided, it did not cut", a)
            if kind == "point" then
                eq(a.drawn, nil, "a point has nothing to draw")
            elseif kind == "npc" and range == "far" then
                eq(a.drawn, false, "the far creature is asleep and not drawn, and the answer says so")
                check(type(a.warning) == "string" and a.notDrawn[1] == thing, "the answer names the creature that is not drawn", a)
            else
                eq(a.drawn, true, "the thing is drawn")
            end
        end)
        local back = step("the camera back", "fair", function()
            local answer = game.server(string.format("return GK.Camera.Return({ glide = %s })", tostring(r.glide == true)), 30)
            check(answer.ok, "the camera settled on the host", answer)
            return answer
        end)
        step("on the host, at the distance before the focus", "observation", function()
            local s = game.client("return GK.Camera.State()")
            check(F.flat(s.target, host) <= 0.5, "the camera's target within 0.5 m (x, z) of the host", s.target, host)
            check(math.abs(s.distance - before.distance) <= 0.5, "the camera at its distance before the focus",
                s.distance, before.distance)
            check(back.serverTicks <= r.budget, "back within " .. r.budget .. " server ticks", back.serverTicks)
        end)
    end
end

case("point near", focusCase("point", "near"))
case("object middling", focusCase("object", "middling"))
case("point far", focusCase("point", "far"))
case("object far", focusCase("object", "far"))
case("npc far", focusCase("npc", "far"))
