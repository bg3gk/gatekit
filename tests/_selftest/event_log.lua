-- The one event log, read the way a caller reads it: one stream for both sides (game.events),
-- one attention view (game.attention, game.wait_attention). Proved at speed 1 and at 4:
--   - every side's seq grows by one, the stream is in the order of the game's clock, every row
--     carries its side and fits the registry (the host leaves out any that does not);
--   - a stage scope's rows: scope_begin, scope_track, scope_undo, scope_clean, scope_verify, all
--     with the scope's id, and the rows of what happened inside it carry it too;
--   - attention is a view of the rows: a dialogue (a client window) and a fight (server state)
--     wait while their begin row is the last of their key, and stop waiting at their end row;
--   - the detailed families are off unless switched on, and go off when the scope ends.
-- Then what the log costs: rows per second by family and milliseconds per tick on both sides,
-- with the always-on families only and with every family on, in a fight at speed 4.
-- How the scene is made: staged (GK.Stage scope, GK.Fixture talker and fight: cheats); the talk and
-- the end of turn are the player's own actions (GK.Act, fair); everything read is observation.
-- Run: gatekit --instance <copy> test tests -k event_log

test { level = "GustavX:Basic_Level_A",
           about = "The event log is one stream, its scope rows and its attention view hold at speed 1 and under acceleration." }

local function rows_of(answer) return answer.rows or {} end

--- Every side's seq grows by one, and the stream is in the order of t.
local function check_stream(rows, label)
    local last, lastT = {}, -1
    for _, row in ipairs(rows) do
        check(row.side == "server" or row.side == "client", label .. ": a row without its side", row)
        if last[row.side] then
            check(row.seq == last[row.side] + 1, label .. ": seq of " .. row.side .. " does not grow by one", last[row.side], row)
        end
        last[row.side] = row.seq
        check(row.t >= lastT, label .. ": the stream is out of the game clock's order", lastT, row)
        lastT = row.t
        check(row.knows == "player" or row.knows == "omniscient", label .. ": a row without who could know it", row)
    end
end

local function kinds(rows, filter)
    local out = {}
    for _, row in ipairs(rows) do
        if not filter or filter(row) then out[#out + 1] = row.kind end
    end
    return out
end

local function has(list, value)
    for _, v in ipairs(list) do if v == value then return true end end
    return false
end

local function waiting(what)
    for _, item in ipairs(game.attention()) do
        if item.waiting and item.what == what then return item end
    end
    return nil
end

local function resolve_all()
    for _ = 1, 30 do
        if not game.ui("return GK.Gui.Blocking().window") then return end
        game.ui("return GK.Gui.Resolve({ roll = 'throw', dialogue = 'last' })")
        game.sleep(0.6)
    end
    check(false, "a window stayed open", game.ui("return GK.Gui.Blocking()"))
end

local function proof(speed)
    game.server(("GK.Speed.Set(%d) return true"):format(speed))
    local mark = game.mark()
    local begun = game.server("return GK.Stage.Begin({ fresh = true })")
    check(begun.scope, "Begin names its scope", begun)

    -- a client window: the dialogue of a staged talker, started by the player's own click
    game.server("GK.FixtureT = GK.Fixture.Talker{ dialog = 'greeting' } return true")
    game.wait("server", "GK.Fixture.Ready('Talker')", 15)
    local talk = game.mark()
    game.server("return GK.Cheat.Talk(nil, GK.FixtureT.entities[1].id)")
    local opened = game.wait_attention({ "dialogue", "dialogue_choice" }, 30, talk)
    check(opened.side == "client", "the dialogue window is the client's", opened)
    check(waiting("dialogue"), "the dialogue waits while its begin row is the last of its key", game.attention())
    resolve_all()
    game.wait_attention({ "dialogue_ended" }, 30, talk)
    check(not waiting("dialogue"), "the dialogue stops waiting at its end row", game.attention())

    -- server state: a fight of a staged hostile; it ends when the fixture is taken away
    local fight = game.mark()
    game.server("GK.FixtureF = GK.Fixture.Fight{} return true")
    local started = game.wait_attention({ "combat_started" }, 30, fight)
    check(started.side == "server", "the fight is the server's", started)
    check(waiting("combat_started"), "the fight waits", game.attention())
    game.server("return GK.Fixture.Clear()")
    game.wait_attention({ "combat_ended" }, 30, fight)
    check(not waiting("combat_started"), "the fight stops waiting at combat_ended", game.attention())

    -- the scope: clean and verify, then its rows
    game.sleep(0.5)
    game.server("return GK.Stage.Clean()")
    -- (what Verify finds is the stage scope's business, not the log's: a fight leaves the weapon
    -- drawn, which Clean does not put back; the log is asked only to hold the verify's row)
    local verdict
    for _ = 1, 16 do
        game.sleep(0.25)
        verdict = game.server("return GK.Stage.Verify()")
        if verdict.clean then break end
    end
    if not verdict.clean then print("verify: " .. join(verdict.differences, "; ")) end
    game.sleep(0.3)   -- the log's files are written in the tick
    local answer = game.events(mark)
    check(not answer.dropped, "nothing was dropped since the mark", answer.dropped)
    local rows = rows_of(answer)
    check_stream(rows, "speed " .. speed)
    local scoped = kinds(rows, function(r) return r.scope == begun.scope end)
    for _, kind in ipairs({ "scope_begin", "scope_track", "scope_undo", "scope_clean", "scope_verify", "combat_started", "game_fight" }) do
        check(has(scoped, kind), "the scope's rows hold " .. kind, scoped)
    end
    local client = kinds(rows, function(r) return r.side == "client" end)
    check(has(client, "dialogue") and has(client, "dialogue_ended"), "the client's rows are in the same stream", client)
    -- the detailed families stayed off: nobody switched them on
    for _, kind in ipairs({ "status_applied", "entity_created", "surface_created", "osiris" }) do
        check(not has(kinds(rows), kind), kind .. " was written without being switched on", kind)
    end
    local counts = 0
    for _ in ipairs(rows) do counts = counts + 1 end
    print(("speed %d: %d rows in the stream since the mark, %d in the scope %d"):format(speed, counts, #scoped, begun.scope))
end

-- A family switched on for a scope is off once the scope ends.
local function families_follow_the_scope()
    game.server("GK.Stage.Begin({ fresh = true, families = { 'status', 'entity' } }) return true")
    local on = game.server("return GK.EventLog.SwitchedOn()")
    check(has(on, "status") and has(on, "entity"), "Begin switched the families on", on)
    local mark = game.mark()
    game.server("local h = Osi.GetHostCharacter() Osi.ApplyStatus(h, 'BLESS', 6, 1, h) return true")
    game.sleep(0.6)
    check(has(kinds(rows_of(game.events(mark, { "status_applied" }))), "status_applied"), "a status was written while its family was on")
    game.server("return GK.Stage.Clean()")
    on = game.server("return GK.EventLog.SwitchedOn()")
    check(not has(on, "status") and not has(on, "entity"), "the scope's Clean switched them off", on)
    local after = game.mark()
    game.server("local h = Osi.GetHostCharacter() Osi.ApplyStatus(h, 'BLESS', 6, 1, h) return true")
    game.sleep(0.6)
    check(#rows_of(game.events(after, { "status_applied" })) == 0, "no status row once the family is off")
    game.server("local h = Osi.GetHostCharacter() Osi.RemoveStatus(h, 'BLESS') return true")
end

-- What the log costs, on both sides: rows per second by family and ms per tick.
local COSTS = "return GK.EventLog.Costs()"

local function measure(label, everything)
    game.server("GK.Speed.Set(4) return true")
    if everything then
        local on = "GK.EventLog.On({ 'osiris', 'status', 'entity', 'surface', 'component' }, { seconds = 120, components = { 'Health' } })"
        game.server(on)
    end
    local s0, c0, t0 = game.server(COSTS), game.client(COSTS), game.now()
    game.server("GK.FixtureF = GK.Fixture.Fight{ enemies = 2, caster = true } return true")
    for _ = 1, 8 do
        game.sleep(1.5)
        pcall(game.server, "return GK.Act.EndTurn(nil)")
    end
    game.server("return GK.Fixture.Clear()")
    game.sleep(1.0)
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
    end
    print(("%s (%.0f s at speed 4): %s"):format(label, seconds, table.concat(parts, " | ")))
    game.server("GK.Speed.Set(1) return true")
end

function run(game)
    game.wait_state("Running", "server", 120)
    proof(1)
    proof(4)
    game.server("GK.Speed.Set(1) return true")
    families_follow_the_scope()
    measure("always-on families", false)
    measure("every family on", true)
end
