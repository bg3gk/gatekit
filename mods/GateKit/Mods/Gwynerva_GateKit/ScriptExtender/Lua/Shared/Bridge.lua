-- The game side of the GateKit bridge (docs/contracts/bridge-protocol.md is the contract).
--
-- The host (the `gatekit` command, the MCP server, tests; "the host" below) writes a
-- command file into "Script Extender/Gwynerva_GateKit/";
-- this file polls it, runs the Lua inside, and writes the answer next to it. Files are the
-- channel because Script Extender offers no socket and typing into its console window is fragile.
--
--   queue_<side>.jsonl     one command per line: {"seq", "id", "code", "ui", "class"}   appended by the host
--   res_<side>_<id>.json   {"id", "ok", "result" | "error", "log"}           written here, removed by the caller
--   state_<side>.json      heartbeat, once a second; "done" = last seq run   written here
--
-- <side> is "client" or "server": each has its own Lua state and its own files.
-- The queue lets several processes talk to one side at once: every command gets a line of its
-- own and every answer a file of its own, so nothing is overwritten before it is read. The
-- host appends under a lock with ever-growing `seq` numbers and drops lines this side has
-- reported as done; this side runs, in order, every line whose seq is above the last it ran.
-- (Lua here can neither list a folder nor delete a file, so the host does all the cleaning.)
--
-- Former channel, still served for a host that does not know the queue:
--   cmd_<side>.json {"id", "code", "ui"} -> res_<side>.json

-- GateKit's API table. Other code reaches it as Mods.GateKit.GK.
GK = GK or {}

local DIR = "Gwynerva_GateKit/"
local POLL_MS = 150
local BEAT_MS = 1000

local JSON_OPTIONS = {
    Beautify = false,
    StringifyInternalTypes = true,
    IterateUserdata = true,
    AvoidRecursion = true,
}

local function toJson(value)
    local ok, text = pcall(Ext.Json.Stringify, value, JSON_OPTIONS)
    if ok then return text end
    return nil
end

-- Turns whatever a command returned into something the answer file can hold.
local function plain(value)
    if value == nil then return nil end
    local kind = type(value)
    if kind == "string" or kind == "number" or kind == "boolean" then return value end
    local text = toJson(value)
    if text then
        local ok, parsed = pcall(Ext.Json.Parse, text)
        if ok then return parsed end
    end
    return tostring(value)
end

local function gameState()
    local ok, state = pcall(function() return Ext.Utils.GetGameState() end)
    if ok and state ~= nil then return tostring(state) end
    return nil
end

--- Reads the listed components of an entity into a plain table (missing ones are skipped).
function GK.Dump(entity, components)
    local out = {}
    if not entity then return out end
    for _, name in ipairs(components) do
        local ok, value = pcall(function() return entity[name] end)
        if ok and value ~= nil then out[name] = plain(value) end
    end
    return out
end

--- Runs a Lua chunk and returns { ok, result | error, log }. `GK.Log(...)` inside the chunk
--- adds lines to `log`. Used by the file channel, the in-game console and the server relay.
--- `class` (a step's: fair, cheat, observation) holds the verbs the chunk calls to it (GK.Verbs).
function GK.Run(code, class)
    local logs = {}
    GK.Log = function(...)
        local parts = {}
        for i = 1, select("#", ...) do parts[i] = tostring((select(i, ...))) end
        logs[#logs + 1] = table.concat(parts, " ")
    end

    local answer = { log = logs }
    local chunk, syntaxError = load(code)
    if not chunk then
        answer.ok = false
        answer.error = "syntax: " .. tostring(syntaxError)
        return answer
    end
    local function traced()
        return xpcall(chunk, function(err)
            local trace = debug and debug.traceback and debug.traceback(tostring(err), 2)
            return trace or tostring(err)
        end)
    end
    local ok, value
    if class ~= nil and GK.Verbs and GK.Verbs.Within then
        local ran, fine, result, refused = nil, nil, nil, nil
        ran, fine, refused = GK.Verbs.Within(class, function()
            local a, b = traced()
            result = b
            return a
        end)
        if not ran then ok, value = false, tostring(fine)
        elseif refused then ok, value = false, refused
        else ok, value = fine, result end
    else
        ok, value = traced()
    end
    answer.ok = ok
    if ok then
        local observe, within
        value, observe, within = GK.Unobserved(value)
        answer.result = plain(value)
        -- the answer waits for what the call observes (GK.Observe); the file channel honours it,
        -- other callers (the console) get the answer as it stands
        answer.observe, answer.within = observe, within
    else
        answer.error = tostring(value)
    end
    return answer
end

-- Answers that are what was OBSERVED, not what was attempted --------------------------------
--
-- An acting verb (GK.Gui.Click, GK.Gui.Aim, GK.Act.Select ...) does its part at once and hands
-- back GK.Observe(result, check, within): `check(ticks, final)` is called once a tick of this side
-- until it returns the final table (or nil to keep waiting); `ticks` counts this side's ticks since
-- the observation began. `within` is a limit in milliseconds of a game that runs, counted in this
-- side's ticks at the nominal rate (GK.LimitTicks): when that many ticks have passed it is called
-- one last time with final = true and must answer then. A game that stands still runs no tick, so
-- it eats nothing of the limit. A verb called from other Lua sees `result` as it stands; the file
-- channel waits for the observation before it writes the answer, so the host gets what happened.
-- Several observations in one answer are not merged: the outermost wins. A verb that waits on
-- another verb's observation follows it with GK.Follow, the same loop the file channel runs.

local OBSERVE = "__gk_observe"
local WITHIN = "__gk_within"

-- The nominal length of a tick: the server runs thirty a second at most, whatever the game speed.
-- A limit in ms becomes ticks at this rate.
local NOMINAL_TICK_MS = 1000 / 30
GK.TickMs = NOMINAL_TICK_MS

-- The clock every limit of the bridge is counted in. On the server it is the game's own tick
-- (GK.EventLog.Ticks). A client ticks once a frame, at whatever rate its renderer draws (seen at
-- 144 a second with the window in the background), so a client's frames are counted by their time
-- at the nominal rate, each frame at most FRAME_CAP_MS: a game that stands still runs no frame and
-- eats nothing, a frame that hangs (a load, a hitch) eats at most that. The event log's rows keep
-- each side's own count (the client's frames).
local FRAME_CAP_MS = 100
GK.Clock = GK.Clock or { ticks = 0 }
local clock = GK.Clock

--- This side's clock in ticks: the clock every limit of the bridge is counted in.
function GK.Ticks()
    if GK.Side == "client" then return math.floor(clock.ticks) end
    return GK.EventLog and GK.EventLog.Ticks or 0
end

-- One frame of the client: its time added to the clock (called once a tick by the bridge's tick).
local function advanceClock()
    local now = Ext.Utils.MonotonicTime()
    if clock.last then clock.ticks = clock.ticks + math.min(math.max(0, now - clock.last), FRAME_CAP_MS) / NOMINAL_TICK_MS end
    clock.last = now
end

--- A limit in ms of a running game as this side's ticks (at least one).
function GK.LimitTicks(ms)
    return math.max(1, math.ceil((tonumber(ms) or 1500) / NOMINAL_TICK_MS))
end

--- Marks `result` (a table) as observed later: see above. Returns `result` itself.
function GK.Observe(result, check, within)
    if type(result) ~= "table" or type(check) ~= "function" then return result end
    result[OBSERVE] = check
    result[WITHIN] = within or 1500
    return result
end

--- Takes the observation off a value: value (a copy without it), check, within.
function GK.Unobserved(value)
    if type(value) ~= "table" or type(rawget(value, OBSERVE)) ~= "function" then return value end
    local check, within = rawget(value, OBSERVE), rawget(value, WITHIN)
    local copy = {}
    for k, v in pairs(value) do
        if k ~= OBSERVE and k ~= WITHIN then copy[k] = v end
    end
    return copy, check, within
end

--- An observation that is only waiting: answers once `ready()` returns a value (that value, a
--- table) or, at the limit of `within` ms of a running game, `timeout()`.
function GK.Later(ready, within, timeout)
    return GK.Observe({ ok = true, waiting = true }, function(_, final)
        local value = ready()
        if value ~= nil then return value end
        if final then return timeout and timeout() or { ok = false, error = "nothing happened in " .. GK.LimitTicks(within) .. " ticks" } end
    end, within)
end

--- Follows a value that may be observed (GK.Observe) to its end, one look a call: answers a
--- function that returns the final answer, or nil while it waits. Each call is one look; a caller
--- calls it once a tick. The limit is the observation's own `within`, in this side's ticks; the
--- final answer carries `waitedTicks` (and `waitedMs`, the real time, for reading only). A check
--- that has nothing to say at its limit is answered "the event never came". A value that is not
--- observed is answered as it is, at the first call. The file channel runs this same loop.
function GK.Follow(value)
    local plainValue, check, within = GK.Unobserved(value)
    if not check then return function() return plainValue end end
    local limit = GK.LimitTicks(within)
    local started, startedMs = GK.Ticks(), Ext.Utils.MonotonicTime()
    return function()
        local waited = GK.Ticks() - started
        local final = waited >= limit
        local ok, answer = pcall(check, waited, final)
        if not ok then
            answer = { ok = false, error = "the observation failed: " .. tostring(answer), attempted = plainValue }
        elseif answer == nil then
            if not final then return nil end
            answer = { ok = false, error = "the event never came: nothing was seen in " .. limit .. " ticks", attempted = plainValue }
        end
        if type(answer) == "table" then
            answer = GK.Unobserved(answer)
            answer.waitedTicks = waited
            answer.waitedMs = math.floor(Ext.Utils.MonotonicTime() - startedMs)
        end
        return answer
    end
end

--- The outcome of a hit from its effect flags (a set: { Miss = true, ... }), one mapping for the
--- server's `game_hit` row and the client's cast: "critical miss", "miss", "critical", "hit" or "none".
function GK.HitOutcome(flags)
    if flags.Miss then return flags.Critical and "critical miss" or "miss" end
    if flags.Critical then return "critical" end
    if flags.Hit then return "hit" end
    return "none"
end

local function try(fn)
    local ok, value = pcall(fn)
    if ok then return value end
end

-- One roll of a hit's ConditionRolls as { natural, total, critical }.
local function rollOf(c)
    local result = try(function() return c.Roll.Result end)
    local critical = result and try(function() return tostring(result.Critical) end)
    return result and try(function() return result.NaturalRoll end), result and try(function() return result.Total end),
        critical ~= "None" and critical or nil
end

--- The saving throws a hit asked of its target, from HitDesc.ConditionRolls (the target's roll is
--- resolved inside the hit, not by the roll system: no esv::roll finished event names it):
--- { { ability, dc, natural, total, critical } }, or nil when it asked for none. The verdict is the
--- hit's: the flag SavingThrow on a hit the target saved against.
function GK.HitSaves(hit)
    local out = {}
    for _, c in ipairs(try(function() return hit.ConditionRolls end) or {}) do
        if try(function() return tostring(c.Roll.Roll.RollType) end) == "SavingThrow" then
            local natural, total, critical = rollOf(c)
            out[#out + 1] = { ability = try(function() return tostring(c.Ability) end), dc = try(function() return c.Difficulty end),
                              natural = natural, total = total, critical = critical }
        end
    end
    return #out > 0 and out or nil
end

--- The attack roll of a hit, from the same ConditionRolls: the attack roll function
--- (bg3_dx11.exe+0x106CA40, named RollAttackAgainstArmourClass) appends it with the target's armour
--- class as its Difficulty. { kind (MeleeWeaponAttack, RangedSpellAttack, ...), ac, natural, total,
--- critical }, or nil for a hit with no attack roll. `natural` is the die after the roller's
--- minimum and maximum roll boosts (a pin) bounded it, not the die the stream drew.
function GK.HitAttack(hit)
    for _, c in ipairs(try(function() return hit.ConditionRolls end) or {}) do
        local kind = try(function() return tostring(c.Roll.Roll.RollType) end)
        if kind and kind:find("Attack") then
            local natural, total, critical = rollOf(c)
            return { kind = kind, ac = try(function() return c.Difficulty end), natural = natural, total = total, critical = critical }
        end
    end
    return nil
end

--- The payload of a `game_hit` row from the game's hit (HitDesc), its target and its inflicter
--- (uuids or nil) and a function that names a uuid.
function GK.HitRow(hit, target, by, name)
    local flags, names = {}, {}
    for _, f in pairs(try(function() return hit.EffectFlags end) or {}) do flags[tostring(f)] = true; names[#names + 1] = tostring(f) end
    table.sort(names)
    local outcome = GK.HitOutcome(flags)
    local damage = try(function() return hit.TotalDamageDone end) or 0
    local spellId = try(function() return hit.SpellId end)
    local spell = spellId ~= nil and spellId ~= "" and spellId or nil
    local damageType = try(function() return tostring(hit.DamageType) end)
    local text = string.format("%s %s %s%s%s", by and name(by) or "?", outcome == "none" and "affects" or ("- " .. outcome .. " -"),
        target and name(target) or "?", spell and (" with " .. spell) or "",
        damage > 0 and string.format(": %d %s", damage, tostring(damageType):lower()) or "")
    return { text = text, who = target, by = by, spell = spell, outcome = outcome, damage = damage,
             damageType = damage > 0 and damageType or nil, cause = try(function() return tostring(hit.CauseType) end),
             flags = table.concat(names, "|"), saves = GK.HitSaves(hit), attack = GK.HitAttack(hit) }
end

--- An answer as text for people: log lines, then the result or the error.
function GK.Format(answer)
    local lines = {}
    for _, line in ipairs(answer.log or {}) do lines[#lines + 1] = line end
    if not answer.ok then
        lines[#lines + 1] = "ERROR: " .. tostring(answer.error)
    elseif answer.result ~= nil then
        local value = answer.result
        if type(value) == "table" then
            local ok, text = pcall(Ext.Json.Stringify, value, { Beautify = true })
            value = ok and text or tostring(value)
        end
        lines[#lines + 1] = tostring(value)
    else
        lines[#lines + 1] = "(no result)"
    end
    return table.concat(lines, "\n")
end

--- Allows or forbids the game's autosaves.
function GK.AutoSave(allowed)
    return pcall(function() Ext.Utils.GetGlobalSwitches().CanAutoSave = allowed and true or false end)
end

function GK.Start(side)
    GK.Side = side
    local queueFile = DIR .. "queue_" .. side .. ".jsonl"
    local cmdFile = DIR .. "cmd_" .. side .. ".json"
    local resFile = DIR .. "res_" .. side .. ".json"
    local stateFile = DIR .. "state_" .. side .. ".json"

    local function readCommand()
        local text = Ext.IO.LoadFile(cmdFile)
        if not text or text == "" then return nil end
        local ok, cmd = pcall(Ext.Json.Parse, text)
        if ok and type(cmd) == "table" and cmd.id ~= nil then return cmd end
        return nil
    end

    -- The queue's complete lines in order. Reading stops at the first line that does not parse:
    -- the host may be writing it right now, and nothing after it may run before it.
    local function readQueue()
        local text = Ext.IO.LoadFile(queueFile)
        local out = {}
        if not text or text == "" then return out end
        for line in text:gmatch("[^\n]+") do
            local ok, cmd = pcall(Ext.Json.Parse, line)
            if not ok or type(cmd) ~= "table" or type(cmd.seq) ~= "number" or cmd.id == nil then break end
            out[#out + 1] = cmd
        end
        return out
    end

    -- Test runs must not leave autosaves behind: the game mirrors every save to Steam's cloud
    -- folder and brings it back on the next start. (`GK.AutoSave(true)` turns them on again.)
    GK.AutoSave(false)

    -- Commands left over from before this Lua state started must not run again.
    local stale = readCommand()
    local seen = stale and stale.id or nil
    local done = 0
    for _, cmd in ipairs(readQueue()) do
        if cmd.seq > done then done = cmd.seq end
    end

    local state = { side = side, boot = Ext.Utils.MonotonicTime(), gameState = gameState(), queue = 1, done = done }
    Ext.Events.GameStateChanged:Subscribe(function(e)
        state.gameState = tostring(e.ToState)
    end)

    local function write(answer, file)
        answer.side = side
        answer.observe, answer.within = nil, nil
        local text = toJson(answer)
        if not text then
            text = toJson({ id = answer.id, side = side, ok = false, error = "the answer could not be written as JSON" })
        end
        Ext.IO.SaveFile(file, text)
    end

    -- Answers waiting for an observation: { answer, file, step (GK.Follow), ui }.
    local waiting = {}

    local function run(cmd, file, ui)
        local answer = GK.Run(cmd.code, cmd.class)
        answer.id = cmd.id
        if answer.ok and answer.observe then
            local observed = GK.Observe(type(answer.result) == "table" and answer.result or { value = answer.result },
                                        answer.observe, answer.within)
            waiting[#waiting + 1] = { answer = answer, file = file, step = GK.Follow(observed), ui = ui }
            return
        end
        write(answer, file)
    end

    -- One look at a waiting answer, in the context its command ran in: GK.Follow's step, whose
    -- limit is counted in this side's ticks.
    local function observe(entry)
        local value = entry.step()
        if value == nil then return false end
        entry.answer.result = plain(value)
        write(entry.answer, entry.file)
        return true
    end

    -- A client chunk that touches the game's UI (GK.Gui, GK.Act - the hotbar's verbs on the client -,
    -- GK.Cheat, Ext.UI) is run in the UI's context too, whichever side it was sent to: run from the
    -- tick, a command such as the main menu's New Game never answered.
    local function touchesUi(code)
        -- GK.Cheat on the client is the UI's cheats (Run, Set, Step, Recover ...)
        return type(code) == "string" and (code:find("GK.Gui.", 1, true) or code:find("GK.Act.", 1, true)
            or code:find("GK.Cheat.", 1, true) or code:find("Ext.UI.", 1, true)) ~= nil
    end

    local function dispatch(cmd, file)
        if (cmd.ui or (side == "client" and touchesUi(cmd.code))) and Ext.UI and Ext.UI.Defer then
            -- The game's UI (Noesis) may only be touched from its own deferred context.
            Ext.UI.Defer(function() run(cmd, file, true) end)
        else
            run(cmd, file, false)
        end
    end

    local function observeAll()
        local i = 1
        while i <= #waiting do
            local entry = waiting[i]
            if entry.busy then
                i = i + 1
            elseif entry.ui and Ext.UI and Ext.UI.Defer then
                entry.busy = true
                Ext.UI.Defer(function()
                    entry.busy = false
                    if not entry.done and observe(entry) then entry.done = true end
                end)
                i = i + 1
            elseif observe(entry) then
                table.remove(waiting, i)
            else
                i = i + 1
            end
        end
        for j = #waiting, 1, -1 do
            if waiting[j].done then table.remove(waiting, j) end
        end
    end

    local nextPoll, nextBeat = 0, 0
    Ext.Events.Tick:Subscribe(function()
        local now = Ext.Utils.MonotonicTime()
        if side == "client" then advanceClock() end
        if #waiting > 0 then observeAll() end
        if GK.Attention then GK.Attention.Tick(now) end
        if GK.EventLog then GK.EventLog.Tick() end
        if now >= nextBeat then
            nextBeat = now + BEAT_MS
            state.time = now
            -- the side's progress: the host counts its own timeout in these ticks
            state.tick = GK.Ticks()
            state.gameState = gameState() or state.gameState
            Ext.IO.SaveFile(stateFile, toJson(state) or "{}")
        end
        if now < nextPoll then return end
        nextPoll = now + POLL_MS

        for _, cmd in ipairs(readQueue()) do
            if cmd.seq > done then
                done = cmd.seq
                state.done = done
                dispatch(cmd, DIR .. "res_" .. side .. "_" .. tostring(cmd.id) .. ".json")
            end
        end

        local cmd = readCommand()
        if cmd and cmd.id ~= seen then
            seen = cmd.id
            dispatch(cmd, resFile)
        end
    end)
end
