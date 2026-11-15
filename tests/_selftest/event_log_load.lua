-- What the event log costs in a level of the story, nothing staged: the tutorial ship running at
-- speed 4 for 20 s, first with the always-on families only, then with every family on. Prints
-- rows per second by family and milliseconds per tick on both sides (GK.EventLog.Costs). All of
-- it is observation; the speed is a cheat (GK.Speed).
-- Run: gatekit --instance <copy> test tests -k event_log_load

test { level = "GustavX:TUT_Avernus_C", mods = {},
           about = "Rows per second by family and the log's cost per tick in a story level, always-on families against every family." }

local COSTS = "return GK.EventLog.Costs()"
local SECONDS = 20

local function measure(label, everything)
    if everything then
        game.server("GK.EventLog.On({ 'osiris', 'status', 'entity', 'surface', 'component' }, { seconds = 120, components = { 'Health' } }) return true")
    end
    local s0, c0, t0 = game.server(COSTS), game.client(COSTS), game.now()
    game.sleep(SECONDS)
    local s1, c1, seconds = game.server(COSTS), game.client(COSTS), game.now() - t0
    if everything then game.server("GK.EventLog.Off() return true") end
    local parts = {}
    for _, pair in ipairs({ { "server", s0, s1 }, { "client", c0, c1 } }) do
        local side, a, b = pair[1], pair[2], pair[3]
        local families = {}
        for name, n in pairs(b.families or {}) do
            local before = (a.families or {})[name]
            local rows = n.rows - (before and before.rows or 0)
            if rows > 0 then families[#families + 1] = ("%s %.1f/s"):format(name, rows / seconds) end
        end
        table.sort(families)
        local ticks = b.ticks - a.ticks
        local ms = (b.ms + b.appendMs) - (a.ms + a.appendMs)
        parts[#parts + 1] = ("%s: %.3f ms/tick over %d ticks; %s"):format(side, ticks > 0 and ms / ticks or 0, ticks,
            #families > 0 and table.concat(families, ", ") or "no rows")
        check(b.first <= b.seq + 1, "the store keeps its bound", b)
    end
    print(("%s (%.0f s at speed 4): %s"):format(label, seconds, table.concat(parts, " | ")))
end

function run(game)
    game.wait_state("Running", "server", 120)
    game.server("GK.Speed.Set(4) return true")
    measure("always-on families", false)
    measure("every family on", true)
    game.server("GK.Speed.Set(1) return true")
end
