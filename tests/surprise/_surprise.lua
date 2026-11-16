-- Shared by the tests of tests/surprise: the scene and the readings.
-- The scene: two hostile humans side by side, 4 m apart, both turned away from the host and
-- unkillable, `metres` along +x from it; the host hidden (the status SNEAKING), invisible (the
-- status INVISIBLE) or neither. Turned away, the two see a hidden or an invisible host not at
-- all; one that is neither they notice at once and the fight begins by sight.

local M = {}

function M.uuid(id) return id and string.match(id, "(%x+%-%x+%-%x+%-%x+%-%x+)$") or id end

-- Waits for the running game and switches on the family whose rows say who joined a fight.
function M.watch(game)
    game.wait_state("Running", "server", 120)
    step("the fight log on", "observation", function()
        game.server("return GK.EventLog.On('fight', { seconds = 600 })")
    end)
end

-- Stages the scene: { host, target, ally }. `how` is "hidden", "invisible" or "plain".
function M.scene(game, how, metres)
    local status = how == "hidden" and "SNEAKING" or how == "invisible" and "INVISIBLE" or nil
    local made = game.server(string.format([[
        local host = Osi.GetHostCharacter()
        local status = %s
        if status then GK.Stage.Status(status, host) end
        local x, y, z = Osi.GetPosition(host)
        local target = GK.Stage.Spawn{ template = "human", at = { x + %f, y, z }, faction = "enemy", rotation = 90 }[1]
        local ally = GK.Stage.Spawn{ template = "human", at = { x + %f, y, z + 4 }, faction = "enemy", rotation = 90 }[1]
        GK.Stage.Immortal(true, target)
        GK.Stage.Immortal(true, ally)
        return { host = host, target = target, ally = ally } ]],
        status and string.format("%q", status) or "nil", metres, metres))
    local cast = { host = M.uuid(made.host), target = M.uuid(made.target), ally = M.uuid(made.ally) }
    game.wait("server", string.format("Ext.Entity.Get(%q) ~= nil and Ext.Entity.Get(%q) ~= nil", cast.target, cast.ally), 10)
    return cast
end

-- The scene stands as staged: nobody fights yet and the target does not see the host.
function M.quiet(game, cast)
    local s = game.server(string.format(
        "return { host = Osi.IsInCombat(%q), target = Osi.IsInCombat(%q), sees = Osi.CanSee(%q, %q), enemy = Osi.IsEnemy(%q, %q) }",
        cast.host, cast.target, cast.target, cast.host, cast.target, cast.host))
    need(s.enemy == 1, "the target is hostile to the host")
    need(s.host == 0 and s.target == 0, "nobody fights before the opener")
    need(s.sees == 0, "the target does not see the host")
end

-- The host's weapon attack on `who`, ordered by script: "ranged" or "melee".
function M.attack(game, cast, who, mode)
    local a = game.server(string.format("return GK.Cheat.Attack(%q, %q, { mode = %q })", cast.host, who, mode))
    check(a and a.job, "the attack was ordered", a)
end

-- The tick `who` joined a fight after `since`.
function M.joined(since, who, seconds)
    return row(since, "fight_joined", function(p) return M.uuid(p.who) == who end, seconds or 30)
end

-- The row that says `who` became SURPRISED after `since`: the game's own, with no giver.
function M.surprised(since, who, seconds)
    local r = row(since, "game_status", function(p) return p.status == "SURPRISED" and M.uuid(p.who) == who end, seconds or 30)
    eq(r.payload.turns, 1, "the status's turns")
    check(r.payload.by == nil or r.payload.by == "", "the status has no giver", r.payload)
    return r
end

-- Fails when a row after `since` says that `who` became SURPRISED. Asked once the first turn of
-- the fight has begun: the game gives the status within a few ticks of the joining, a second of
-- game time before any turn.
function M.never_surprised(game, since, who, what)
    row(since, "game_turn", function(p) return p.who ~= nil end, 60)
    for _, r in ipairs(game.events(since, { "game_status" }).rows or {}) do
        check(not (r.payload.status == "SURPRISED" and M.uuid(r.payload.who) == who), what .. " became surprised", r.payload)
    end
    eq(game.server(string.format("return Osi.HasActiveStatus(%q, 'SURPRISED')", who)), 0, what .. " holds the status")
end

return M
