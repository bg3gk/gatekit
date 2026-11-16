-- Shared by the tests of tests/camera/focus: where things stand on the screen, and the camera put
-- back on the host before a case.

local M = {}

function M.uuid(id) return id and string.match(id, "(%x+%-%x+%-%x+%-%x+%-%x+)$") or id end

function M.flat(a, b) return math.sqrt((a[1] - b[1]) ^ 2 + (a[3] - b[3]) ^ 2) end

-- The camera on the host, at its own distance (GK.Camera.Return; a glide, the camera may be far).
function M.home(game)
    return game.client("return GK.Camera.Return({ glide = true })", 30)
end

-- The direction to the right of the screen on the ground, from the camera's own heading: a point
-- that way goes from the middle of the screen to its right edge as it goes away from the host.
function M.right(game)
    local looking = game.client("return GK.Camera.State().looking")
    local a = math.rad((looking + 90) % 360)
    return math.sin(a), math.cos(a)
end

-- The host's position and a point `metres` to the right of the screen from it.
function M.place(game, metres)
    local h = game.server("return { Osi.GetPosition(Osi.GetHostCharacter()) }")
    local dx, dz = M.right(game)
    return h, { h[1] + dx * metres, h[2], h[3] + dz * metres }
end

-- Where a point is on the screen now.
function M.screen(game, p)
    return game.client(string.format("return GK.Camera.OnScreen({ %f, %f, %f })", p[1], p[2], p[3]))
end

-- The server's position of an object, or the point itself.
function M.where(game, thing)
    if type(thing) == "table" then return thing end
    return game.server(string.format("return { Osi.GetPosition(%q) }", thing))
end

function M.literal(thing)
    if type(thing) == "table" then return string.format("{ %f, %f, %f }", thing[1], thing[2], thing[3]) end
    return string.format("%q", thing)
end

return M
