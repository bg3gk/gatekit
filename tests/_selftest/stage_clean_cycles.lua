-- Five times: a scene staged (a dummy with a passive, an item given, a status, a surface, the host
-- moved and hurt), then GK.Stage.Clean and GK.Stage.Verify. Our own clean-up must come back clean
-- every time and leave nothing that grows: entity counts, Lua memory, the game's memory and its
-- frame time are read at the start and after the last cycle and printed as a line each.
-- Run: gatekit --instance <copy> test tests -k stage_clean_cycles
-- The long form is a measurement, not the test: `--repeat 20` runs the five cycles twenty times in
-- one game (a hundred cycles), and the line each run prints at its start shows whether server Lua
-- memory grows.

test { level = "GustavX:Basic_Level_A", reset = "fixture" }

local CYCLES = 5

local STAGE = [[
local H = Osi.GetHostCharacter()
local d = GK.Stage.Spawn{ template = "human", faction = "neutral", distance = 5 }[1]
Ext.Timer.WaitFor(200, function() pcall(Osi.AddPassive, d, "MAG_Bhalist_Hat_Passive") end)
Osi.TemplateAddTo("cbb98d74-68e9-4d0f-a4ff-6fa92cfcea13", H, 1, 0)
GK.Stage.Status("BLESS", H, 2)
local x, y, z = Osi.GetPosition(H)
GK.Stage.Surface("SurfaceWater", 1.5, { x - 3, y, z })
Osi.TeleportToPosition(H, x + 2, y, z + 1, "", 0, 0, 0, 0, 1)
Osi.ApplyDamage(H, 2, "Fire", "")
return d
]]

-- the frame time over about two seconds: every tick's delta, on the server
local FRAMES = [[
if GKFrame and GKFrame.sub then Ext.Events.Tick:Unsubscribe(GKFrame.sub) end
GKFrame = { n = 0, sum = 0, max = 0, last = Ext.Utils.MonotonicTime() }
GKFrame.sub = Ext.Events.Tick:Subscribe(function()
    local now = Ext.Utils.MonotonicTime()
    local d = now - GKFrame.last
    GKFrame.last, GKFrame.n, GKFrame.sum = now, GKFrame.n + 1, GKFrame.sum + d
    if d > GKFrame.max then GKFrame.max = d end
    if GKFrame.n >= 60 then Ext.Events.Tick:Unsubscribe(GKFrame.sub); GKFrame.sub = nil end
end)
return true
]]

local COUNTS = [[
collectgarbage("collect")
return { characters = #Ext.Entity.GetAllEntitiesWithComponent("ServerCharacter"),
         items = #Ext.Entity.GetAllEntitiesWithComponent("ServerItem"),
         surfaces = #Ext.Entity.GetAllEntitiesWithComponent("ServerSurface"),
         luaKb = collectgarbage("count"), ledger = #GK.Stage.Open(),
         frameMs = GKFrame and GKFrame.n > 0 and GKFrame.sum / GKFrame.n or nil, frameMaxMs = GKFrame and GKFrame.max }
]]

local function sample(label)
    game.server(FRAMES)
    game.sleep(2.5)
    local c = game.server(COUNTS)
    local p = game.process() or {}
    local client = game.client([[collectgarbage("collect") return collectgarbage("count")]])
    print(string.format("%-9s characters %d  items %d  surfaces %d  server Lua %.0f KB  client Lua %.0f KB  working %.0f MB  private %.0f MB  frame %.1f ms (worst %.0f)",
        label, c.characters, c.items, c.surfaces, c.luaKb, client, p.working_mb or 0, p.private_mb or 0, c.frameMs or 0, c.frameMaxMs or 0))
    return c
end

function run(game)
    game.wait_state("Running", "server", 120)
    game.server([[return GK.Stage.Begin({ fresh = true })]])
    local first = sample("cycle 0")
    local dirty, worst, sum = 0, 0, 0
    for i = 1, CYCLES do
        game.server(STAGE)
        game.sleep(0.6)
        local started = game.now()
        game.server([[return GK.Stage.Clean()]])
        local verdict
        for _ = 1, 16 do
            game.sleep(0.25)
            verdict = game.server([[return GK.Stage.Verify()]])
            if verdict.clean then break end
        end
        local took = game.now() - started
        sum, worst = sum + took, math.max(worst, took)
        if not verdict.clean then
            dirty = dirty + 1
            print("cycle " .. i .. " dirty: " .. join(verdict.differences, "; "))
        end
        if i == CYCLES then
            local now = sample("cycle " .. i)
            check(now.characters == first.characters and now.items == first.items and now.surfaces == first.surfaces,
                "entity counts drifted", first, now)
        end
    end
    print(string.format("dirty cycles: %d of %d; clean and verify %.2f s on average, %.2f s at worst", dirty, CYCLES, sum / CYCLES, worst))
    check(dirty == 0, "a clean that did not verify", dirty)
end
