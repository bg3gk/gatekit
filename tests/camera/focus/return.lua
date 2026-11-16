-- GK.Camera.Return brings the camera back to the controlled character by the game's own restore,
-- at the distance it had before the first focus (docs/contracts/lua-api.md, section 22): outside a
-- fight the camera behaviour's targets and the distance saved by the focus; the game itself moves
-- the distance a little when its targets change (10 m -> 11.5 m measured), so the saved one is
-- written back. Stands on test:camera/focus/matrix (the focus itself).
-- Staging (cheat): the camera on the host. Acting (fair): a focus on a point 10 m off, the return.
-- Reading (observation): the answer, the host's position, the camera's distance.
-- Run: gatekit --instance <copy> test tests -k camera/focus/return --speed working

test { level = "GustavX:Basic_Level_A",
       about = "After a focus, GK.Camera.Return puts the camera back on the host at its old distance." }

local F = require("_focus")

function run(game)
    game.wait_state("Running", "server", 120)
    local before = step("the camera on the host", "cheat", function()
        F.home(game)
        return game.client("return GK.Camera.State()")
    end)
    local host, p = F.place(game, 10)
    step("a focus on a point 10 m off", "fair", function()
        local a = game.server(string.format("return GK.Camera.Focus(%s)", F.literal(p)), 30)
        check(a.ok, "the camera settled on the point", a)
    end)
    step("the camera is away from the host", "observation", function()
        local s = game.client("return GK.Camera.State()")
        need(F.flat(s.target, host) > 5, "the focus moved the camera off the host")
    end)
    local back = step("back to the host", "fair", function()
        local a = game.server("return GK.Camera.Return()", 30)
        check(a.ok, "the camera settled on the host", a)
        return a
    end)
    step("on the host, at the old distance", "observation", function()
        local s = game.client("return GK.Camera.State()")
        check(F.flat(s.target, host) <= 0.5, "the camera's target within 0.5 m (x, z) of the host", s.target, host)
        check(math.abs(s.distance - before.distance) <= 0.5, "the camera at its distance before the focus",
            s.distance, before.distance)
        check(back.serverTicks <= 60, "settled within 60 server ticks", back.serverTicks)
    end)
end
