-- Shared by the tests of tests/fight/npc: a goblin and a human staged neutral side by side along +x
-- from the host, the two made hostile to each other, and the rows of their fight.

local M = {}

function M.uuid(id) return id and string.match(id, "(%x+%-%x+%-%x+%-%x+%-%x+)$") or id end

-- A neutral goblin `metres` along +x from the host and a neutral human 3 m aside of it, the attack
-- die of each pinned (`dice = { goblin = n, human = n }`): { goblin, human, host }.
function M.pair(game, metres, dice)
    local made = game.server(string.format([[
        local x, y, z = Osi.GetPosition(Osi.GetHostCharacter())
        local goblin = GK.Stage.Spawn{ template = "goblin", at = { x + %f, y, z }, faction = "neutral" }[1]
        local human = GK.Stage.Spawn{ template = "human", at = { x + %f, y, z + 3 }, faction = "neutral" }[1]
        return { goblin = goblin, human = human, host = Osi.GetHostCharacter() } ]], metres, metres))
    local cast = { goblin = M.uuid(made.goblin), human = M.uuid(made.human), host = M.uuid(made.host) }
    game.wait("server", string.format("Ext.Entity.Get(%q) ~= nil and Ext.Entity.Get(%q) ~= nil", cast.goblin, cast.human), 10)
    for name, n in pairs(dice or {}) do M.die(game, cast[name], n) end
    return cast
end

-- The natural die of every attack roll of `who`.
function M.die(game, who, n)
    local pinned = game.server(string.format("return GK.Rolls.Pin{ who = %q, kind = 'Attack', n = %d }", who, n))
    check(pinned.ok and pinned.inForce, "the attack die is pinned", pinned)
end

-- The goblin to the faction hostile to the party, the human to the party's allies: the two are
-- enemies of each other, and only the goblin of the host.
function M.hostile(game, cast)
    game.server(string.format("GK.Stage.Faction('enemy', %q) GK.Stage.Faction('ally', %q) return true", cast.goblin, cast.human))
    game.wait("server", string.format("Osi.IsEnemy(%q, %q) == 1", cast.goblin, cast.human), 10)
end

-- The fight both joined after `since`: its id. Fails when they joined two fights.
function M.joined(game, cast, since, seconds)
    local g = row(since, "fight_joined", function(p) return M.uuid(p.who) == cast.goblin end, seconds or 30).payload
    local h = row(since, "fight_joined", function(p) return M.uuid(p.who) == cast.human end, seconds or 30).payload
    check(g.combat ~= nil and g.combat == h.combat, "one fight, named in both rows", g, h)
    return g.combat
end

-- Fails when a row after `since` says that `who` joined a fight, or the game says it is in one.
function M.never_joined(game, who, since, what)
    for _, r in ipairs(game.events(since, { "fight_joined" }).rows or {}) do
        check(M.uuid(r.payload.who) ~= who, what .. " joined a fight", r.payload)
    end
    eq(game.server(string.format("return Osi.IsInCombat(%q)", who)), 0, what .. " is in no fight")
end

return M
