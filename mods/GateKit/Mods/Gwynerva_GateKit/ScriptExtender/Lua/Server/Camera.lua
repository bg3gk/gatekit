-- GK.Camera (server side): the camera's focus asked from the server.
--
-- The camera is the client's (Client/Camera.lua does the move and observes it). The server knows
-- where every object is; the client does not: a creature far from the camera is a stub there whose
-- position is stale. So the server's GK.Camera.Focus sends the client the server's own point of
-- each uuid, on GK_Camera, and the client focuses the entity when it holds it whole and the point
-- when it holds a stub. The client's observed answer comes back on the same channel; the server's
-- answer is that answer, with the server ticks it waited.

local Camera = GK.Camera or {}
GK.Camera = Camera

Camera.Answers = Camera.Answers or {}
Camera.NextId = Camera.NextId or 0
Camera.AnswerMs = 8000         -- a guard, in ms of a running game: the client settles well before

Camera.Channel = Camera.Channel or Ext.Net.CreateChannel(ModuleUUID, "GK_Camera", function(msg)
    if type(msg) == "table" and msg.id then GK.Camera.Answers[msg.id] = msg.answer or { ok = false } end
end)

local function pointOf(t)
    if type(t) ~= "table" then return nil end
    local x, y, z = t.x or t[1], t.y or t[2], t.z or t[3]
    if type(x) == "number" and type(y) == "number" and type(z) == "number" then return { x, y, z } end
end

local function ask(message, sent)
    Camera.NextId = Camera.NextId + 1
    local id = Camera.NextId
    message.id = id
    local ok, problem = pcall(function() Camera.Channel:Broadcast(message) end)
    if not ok then return { ok = false, error = "could not reach the client: " .. tostring(problem) } end
    sent.id = id
    return GK.Observe(sent, function(ticks, final)
        local answer = Camera.Answers[id]
        if answer then
            Camera.Answers[id] = nil
            answer.serverTicks = ticks
            return answer
        end
        if final then return { ok = false, error = "the client did not answer in " .. ticks .. " server ticks" } end
    end, Camera.AnswerMs)
end

--- GK.Camera.Focus from the server: `at` (an entity's uuid, a point {x, y, z}, or a list of two)
--- is sent to the client with the server's point of every uuid, so that a far creature (a stub on
--- the client) is focused where the server has it. Options as the client's. Observed: the client's
--- answer once the camera settled, with `serverTicks`.
function Camera.Focus(at, options)
    local list = at
    if type(at) ~= "table" or pointOf(at) then list = { at } end
    local points = {}
    for _, item in ipairs(list) do
        if type(item) == "string" then
            local ok, x, y, z = pcall(Osi.GetPosition, item)
            if not (ok and x) then return { ok = false, error = "no such entity: " .. item } end
            points[item] = { x, y, z }
        elseif not pointOf(item) then
            return { ok = false, error = "not a uuid or a point: " .. tostring(item) }
        end
    end
    return ask({ op = "focus", at = at, points = points, options = options }, { ok = true, sent = "focus" })
end

--- GK.Camera.Return from the server: the client's restore to the controlled character. Observed
--- as the client's.
function Camera.Return(options)
    return ask({ op = "return", options = options }, { ok = true, sent = "return" })
end

return Camera
