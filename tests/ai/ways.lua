-- `now.ways` lists the passages out of where the agent stands (GK.See.Passages: ladders, doors,
-- portals, then the mechanisms worked in place), each with its handle and where it leads. In
-- blind mode only those a member's sight holds now or held once on this level: a door on the
-- floor above is not listed from below; once the host has stood by it, it is listed from below as
-- "seen before". In open mode the same door is listed at once.
-- Venue: TestLevel_Tom from its start point (-20.75, 0, 47.25): the passage 5.8 m west
-- (b29b9d82) in sight, the door 8.9 m away on the floor 5 m above (45f988f5) out of it. Staging
-- (cheat): the host put by the upper door and back (GK.Teleport). Reading (observation): the
-- bridge's `now` and the sight state.
-- Run: gatekit --instance <copy> test tests -k ai/ways --speed working

test { level = "GustavX:TestLevel_Tom",
       about = "Blind `now.ways` lists the passages in sight or seen before, and not one on another floor never seen; open mode lists it." }

local START = { -20.75, 0.0, 47.25 }
local BY_UPPER = { -29.5, 4.92, 44.0 }

local WAYS = [[
    local s = Mods.GateKitAI.AI.Session.Start({ mode = %q })
    local now = Mods.GateKitAI.AI.Now({ full = true, peek = true, detail = "full" })
    local function line(prefix)
        local id = GK.See.Resolve(prefix)
        for _, l in ipairs(now.ways or {}) do if l:find("#" .. id:sub(1, 6), 1, true) then return l end end
        return nil
    end
    return { ok = s.ok, ways = now.ways or {}, near = now.near or {}, lower = line("b29b9d82"), upper = line("45f988f5"),
             upperSight = #GK.See.InSight(GK.See.Resolve("45f988f5")) }
]]

local function ways(game, mode)
    local r = game.server(string.format(WAYS, mode))
    print(mode .. " ways: " .. table.concat(r.ways, " | "))
    return r
end

function run(game)
    game.wait_state("Running", "server", 120)
    step("the host at the level's start, nothing remembered", "cheat", function()
        local at = game.server("local x, y, z = Osi.GetPosition(Osi.GetHostCharacter()) return { x, y, z }")
        need(math.abs(at[1] - START[1]) < 0.5 and math.abs(at[3] - START[3]) < 0.5, "the host stands at the level's start point")
        game.server("GK.See.Forget() return true")
    end)
    local blind = step("blind ways from the start", "cheat", function() return ways(game, "blind") end)
    step("the passage in sight is listed, the door above is not", "observation", function()
        check(blind.lower ~= nil, "the passage in sight is in ways", blind.ways)
        check(contains(blind.lower, "door"), "with its kind", blind.lower)
        eq(blind.upperSight, 0, "no member's sight holds the door above")
        eq(blind.upper, nil, "the door above is not in blind ways")
    end)
    local open = step("open ways from the start", "cheat", function() return ways(game, "open") end)
    step("open mode lists the door above, with its height", "observation", function()
        check(open.upper ~= nil, "the door above is in open ways", open.ways)
        check(contains(open.upper, "above"), "it says the door is above", open.upper)
    end)
    step("the host by the door above, then back", "cheat", function()
        game.server(string.format("GK.Teleport(%f, %f, %f) return true", BY_UPPER[1], BY_UPPER[2], BY_UPPER[3]))
        game.sleep(1)
        local up = ways(game, "blind")
        check(up.upper ~= nil, "standing by it, blind ways lists the door", up.ways)
        game.server(string.format("GK.Teleport(%f, %f, %f) return true", START[1], START[2], START[3]))
        game.sleep(1)
    end)
    local back = step("blind ways from the start again", "cheat", function() return ways(game, "blind") end)
    step("the door above is listed as seen before", "observation", function()
        eq(back.upperSight, 0, "the door above is still out of sight")
        check(back.upper ~= nil and contains(back.upper, "seen before"), "blind ways lists it as seen before", back.ways)
    end)
end
