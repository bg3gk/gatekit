-- What blind mode lists in `near` is what a member's sight holds, by the game's own sight state
-- (`ServerViewshedParticipant.CanSee` on the one seen, which Osi.CanSee answers from): within the
-- member's sight range, a clear line, not in magical darkness, in every direction - the camera
-- shows what is behind the character, so its facing does not count. What the party is merely
-- shown (on the level, on stage, no Invisibility) is wider: a creature behind a wall or on another
-- floor is shown and is not in sight, and blind `near` leaves it out.
-- Venue: TestLevel_Tom from its start point (-20.75, 0, 47.25): the corridor's south wall, a floor
-- 4.9 m above and one 2.3 m below to the west (heights by Ext.Level.GetHeightsAt). Staging (cheat):
-- a neutral goblin per case; for the case behind the character the host turned away from it
-- (Osi.SteerTo); for the dark, the Darkness spell cast on the goblin by a second staged goblin
-- (Osi.UseSpell), the host outside it. A bare `DarknessCloud` surface (Osi.CreateSurfaceAtPosition)
-- is drawn and takes nobody out of sight; the spell's darkness outlives the removal of the
-- surface layer and stays until the level is loaded again, so this case is the last. Reading
-- (observation): the sight state, GK.See.PartyPerceives and the bridge's blind `now`.
-- Run: gatekit --instance <copy> test tests -k ai/sight --speed working

test { level = "GustavX:TestLevel_Tom", before = "stage",
       about = "Blind `near` lists a creature in plain view or behind the character, and not one behind a wall, on another floor or in darkness." }

local START = { -20.75, 0.0, 47.25 }

local function uuid(id) return id and string.match(id, "(%x+%-%x+%-%x+%-%x+%-%x+)$") or id end

function stage(game)
    game.wait_state("Running", "server", 120)
    step("the host at the level's start, a blind session", "cheat", function()
        local at = game.server("local x, y, z = Osi.GetPosition(Osi.GetHostCharacter()) return { x, y, z }")
        need(math.abs(at[1] - START[1]) < 0.5 and math.abs(at[3] - START[3]) < 0.5, "the host stands at the level's start point")
        check(game.server("return Mods.GateKitAI.AI.Session.Start({ mode = 'blind' })").ok, "the session is blind")
    end)
end

-- A neutral goblin standing on the floor nearest to `at`.
local function goblin(game, at)
    local g = uuid(game.server(string.format([[
        local best
        for _, h in ipairs(Ext.Level.GetHeightsAt(%f, %f)) do
            if math.abs(h - %f) < 1 then best = h end
        end
        if not best then return nil end
        return GK.Stage.Spawn{ template = "goblin", at = { %f, best, %f }, faction = "neutral", exact = true }[1]
    ]], at[1], at[3], at[2], at[1], at[3])))
    need(g ~= nil, "a floor at the goblin's place")
    game.wait("client", string.format("Ext.Entity.Get(%q) ~= nil", g), 10)
    return g
end

-- What the game and the bridge say of the goblin: the members whose sight holds it, whether the
-- party is shown it, whether blind `near` lists it by its handle, and the line.
local READ = [[
    local g, h = %q, Osi.GetHostCharacter()
    local now = Mods.GateKitAI.AI.Now({ full = true, peek = true, detail = "full" })
    local mark, line = "#" .. g:sub(1, 6)
    for _, l in ipairs(now.near or {}) do if l:find(mark, 1, true) then line = l end end
    local p, hp = GK.See.Position(g), GK.See.Position(h)
    return { sight = GK.See.InSight(g), canSee = Osi.CanSee(h, g), shown = GK.See.PartyPerceives(g), line = line,
             d = math.sqrt((p[1] - hp[1]) ^ 2 + (p[2] - hp[2]) ^ 2 + (p[3] - hp[3]) ^ 2), dy = p[2] - hp[2] }
]]

local function read(game, g)
    -- the sight system updates on its own ticks: read once it has settled
    game.wait("server", string.format("Ext.Entity.Get(%q).ServerViewshedParticipant ~= nil", g), 10)
    game.sleep(1)
    local r = game.server(string.format(READ, g))
    print(string.format("goblin %.1f m away, %.1f m up: in sight of %d, CanSee %s, shown %s, near: %s",
        r.d, r.dy, #r.sight, tostring(r.canSee), tostring(r.shown), tostring(r.line)))
    return r
end

local function seen(game, at, what)
    local g = step("a goblin " .. what, "cheat", function() return goblin(game, at) end)
    step("blind near lists it", "observation", function()
        local r = read(game, g)
        check(#r.sight > 0 and r.canSee == 1, "the host's sight holds it", r)
        check(r.line ~= nil, "blind near lists it", r)
    end)
end

local function unseen(game, at, what, floor)
    local g = step("a goblin " .. what, "cheat", function() return goblin(game, at) end)
    step("shown, out of sight, not in blind near", "observation", function()
        local r = read(game, g)
        if floor then need(math.abs(r.dy) >= 2, "it stands on another floor", r) end
        check(r.shown, "the party is shown it (no Invisibility, on stage): the rule blind mode used to list", r)
        eq(#r.sight, 0, "no member's sight holds it")
        eq(r.canSee, 0, "Osi.CanSee agrees")
        eq(r.line, nil, "blind near leaves it out")
    end)
end

case("plain view", function(game) seen(game, { START[1] + 4, 0, START[3] - 2 }, "4.5 m away in the open") end)

case("behind a wall", function(game) unseen(game, { -16.8, 0, 42.2 }, "behind the corridor's south wall") end)

case("another floor", function(game)
    unseen(game, { -29.5, 4.92, 44.0 }, "on the floor above", true)
    unseen(game, { -32.0, -2.28, 47.0 }, "on the floor below", true)
end)

case("behind the character", function(game)
    local at = { START[1] + 6, 0, START[3] }
    local g = step("a goblin 6 m east, the host turned west", "cheat", function()
        local g = goblin(game, at)
        game.server(string.format("Osi.SteerTo(Osi.GetHostCharacter(), %q, 1) return true", game.server("return GK.See.Resolve('b29b9d82')")))
        return g
    end)
    step("behind the host and still in sight", "observation", function()
        game.sleep(1)
        local cos = game.server(string.format([[
            local h = Osi.GetHostCharacter()
            local q = Ext.Entity.Get(h).Transform.Transform.RotationQuat
            local yaw = 2 * math.atan(q[2], q[4])
            local fx, fz = -math.sin(yaw), -math.cos(yaw)
            local p, hp = GK.See.Position(%q), GK.See.Position(h)
            local dx, dz = p[1] - hp[1], p[3] - hp[3]
            return (dx * fx + dz * fz) / math.sqrt(dx * dx + dz * dz)
        ]], g))
        print(string.format("the goblin is %.0f degrees off the host's facing", math.deg(math.acos(cos))))
        need(cos < -0.5, "the goblin stands behind the host (more than 120 degrees off its facing)")
        local r = read(game, g)
        check(#r.sight > 0, "the host's sight holds it: a member's sight has no field of view", r)
        check(r.line ~= nil, "blind near lists it", r)
    end)
end)

case("in the dark", function(game)
    local at = { START[1] + 9, 0, START[3] }
    local g = step("a goblin 9 m east in the corridor", "cheat", function() return goblin(game, at) end)
    step("in sight before the dark", "observation", function()
        local r = read(game, g)
        need(#r.sight > 0 and r.line ~= nil, "it is in sight and in blind near before the dark", r)
    end)
    step("magical darkness on it", "cheat", function()
        local caster = goblin(game, { START[1] + 8, 0, START[3] + 1.2 })
        game.server(string.format("Osi.UseSpell(%q, 'Target_Darkness', %q) return true", caster, g))
        game.wait("server", string.format("#GK.See.InSight(%q) == 0", g), 10)
        need(game.server("return Osi.HasActiveStatus(Osi.GetHostCharacter(), 'BLINDED_DARKNESS')") == 0, "the host stands outside the darkness")
    end)
    step("out of sight, not in blind near", "observation", function()
        local r = read(game, g)
        check(r.shown, "the party is still shown it", r)
        eq(#r.sight, 0, "no member's sight holds it")
        eq(r.line, nil, "blind near leaves it out")
    end)
end)
