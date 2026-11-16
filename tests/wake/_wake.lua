-- Shared by the tests of tests/wake: the manager's distances, humans staged along +x from the host,
-- and the state of a creature.

local M = {}

function M.uuid(id) return id and string.match(id, "(%x+%-%x+%-%x+%-%x+%-%x+)$") or id end

-- The activation manager's live distances; unmet where they cannot be read.
function M.radii(game)
    local r = game.server("return GK.Wake.Radii()")
    need(r.ok, "the activation manager's distances can be read (GK.Wake.Radii)")
    return r
end

-- A neutral human `metres` along +x from the host, `aside` metres along +z: its uuid.
function M.human(game, metres, aside)
    return M.uuid(game.server(string.format([[
        local x, y, z = Osi.GetPosition(Osi.GetHostCharacter())
        return GK.Stage.Spawn{ template = "human", at = { x + %f, y, z + %f }, faction = "neutral" }[1] ]], metres, aside or 0)))
end

-- Waits until the manager has judged a newborn far from the party: it has lost Active.
function M.asleep(game, who)
    game.wait("server", string.format("Ext.Entity.Get(%q) ~= nil and not Ext.Entity.Get(%q).Active", who, who), 10)
end

function M.state(game, who)
    return game.server(string.format("return GK.Wake.State(%q)", who))
end

return M
