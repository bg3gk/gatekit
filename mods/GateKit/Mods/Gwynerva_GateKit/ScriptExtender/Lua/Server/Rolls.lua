-- GK.Rolls: a creature's rolls decided in advance, the dice mode, and the rolls the game finishes.
-- Server side.
--
--   GK.Rolls.Pin{ who, kind, n, once, karmic }   the natural die of who's own rolls of `kind` is n (a cheat)
--   GK.Rolls.Unpin{ who, kind }          the pin taken off
--   GK.Rolls.Pins()                      what is pinned now, read from the creatures' boosts
--   GK.Rolls.Kinds()                     the roll kinds: the game's StatsRollType, and Initiative
--   GK.Rolls.Karmic() / SetKarmic(on)    Karmic Dice read, and switched for the length of a scope
--   GK.Rolls.Inspiration() / SetInspiration(n)   the party's inspiration points, read and set for a scope
--   GK.Rolls.Streams() / SetStreams(list)   the server's roll streams (base seed, each stream's seed and
--                                        count), read, and written for a scope
--
-- A pin is the game's own pair of boosts MinimumRollResult(kind,n);MaximumRollResult(kind,n) on
-- the roller, added by Osi.AddBoosts with the cause GKPin; Initiative has no roll kind and takes
-- the boost Initiative(n), a bonus added to the initiative roll. Every pin is a track row of the
-- stage ledger with its undo as data (GK.Fixture's `boost` undo), so GK.Stage.Clean takes it off.
-- docs/contracts/lua-api.md, "GK.Rolls".

GK.Rolls = GK.Rolls or {}
local Rolls = GK.Rolls

--- Each verb's class, for the fair/cheat/observation split.
Rolls.Class = {
    Pin = "cheat", Unpin = "cheat", Pins = "observation", Kinds = "observation",
    Karmic = "observation", SetKarmic = "cheat", Finished = "observation",
    Inspiration = "observation", SetInspiration = "cheat", Streams = "observation", SetStreams = "cheat",
    Next = "observation", SetNext = "cheat",
    WriteInspiration = "cheat", WriteKarmic = "cheat", WriteStreams = "cheat",
}

local CAUSE = "GKPin"
local FIXTURE = "Rolls"
local LIMIT_TICKS = 90       -- server ticks an answer waits to see a boost arrive or leave (about 3 s of a
                             -- running game); counted in ticks, so that a stall of the game is not a failure
local WITHIN = 30000         -- ms: only a guard against a game that stopped ticking
local LEAVE_TICKS = 300      -- ticks a removal is watched before it is called stuck
local FINISHED_TICKS = 5     -- ticks a finished roll waits for its RollResult event (it comes the next tick;
                             -- an older one left unjoined would be joined to the next roll of its roller)

Rolls.Seen = Rolls.Seen or {}         -- uuid -> true: every creature pinned in this Lua state
Rolls.Leaving = Rolls.Leaving or {}   -- removals asked, until the boost has left the container
Rolls.Recent = Rolls.Recent or {}     -- roller uuid -> the last finished roll not yet joined to a RollResult
Rolls.Asked = Rolls.Asked or {}       -- ledger seq -> true: the pin rows whose boost this Lua state asked for
Rolls.Gone = Rolls.Gone or {}         -- roll uuid -> what a destroyed finished request held, until its finished event

local function try(fn, ...)
    local ok, a, b = pcall(fn, ...)
    if ok then return a, b end
    return nil
end

local function bare(id)
    return type(id) == "string" and (id:match("(%x+%-%x+%-%x+%-%x+%-%x+)$") or id) or id
end

local function uuidOf(handle)
    local entity = handle and try(Ext.Entity.Get, handle)
    return entity and try(function() return entity.Uuid.EntityUuid end) or nil
end

local function ticks() return GK.EventLog.Ticks end

local writeKarmic   -- Karmic Dice written for the scope (below, beside SetKarmic)

-- ---------------------------------------------------------------------------------------
-- Kinds

--- The roll kinds a pin takes: every value of the game's StatsRollType enumeration except None
--- and the end marker Sentinel, sorted, then Initiative (which the enumeration does not have).
function Rolls.Kinds()
    local out = {}
    for name in pairs(Ext.Enums.StatsRollType) do
        if type(name) == "string" and name ~= "None" and name ~= "Sentinel" then out[#out + 1] = name end
    end
    table.sort(out)
    out[#out + 1] = "Initiative"
    return out
end

local function kindOf(kind, verb)
    for _, name in ipairs(Rolls.Kinds()) do
        if name == kind then return name end
    end
    error("GK.Rolls." .. verb .. ": no roll kind " .. tostring(kind) .. "; the kinds are " .. table.concat(Rolls.Kinds(), ", "), 3)
end

-- The kinds the roll system finishes as rolls of their own (checks, saving throws), so a
-- finished-roll event names them; attack and damage dice are part of a hit and pass through no
-- event the bridge reads, and initiative is not a roll kind at all.
local function finishes(kind)
    return kind ~= "Initiative" and not kind:find("Attack$") and not kind:find("Damage$")
end
Rolls.Finishes = finishes

-- ---------------------------------------------------------------------------------------
-- The boosts on a creature

local function storyBoosts(who) return GK.Stage.StoryBoosts(who) end

local function textOf(kind, n)
    if kind == "Initiative" then return ("Initiative(%d)"):format(n) end
    return ("MinimumRollResult(%s,%d);MaximumRollResult(%s,%d)"):format(kind, n, kind, n)
end

-- Whether every part of a boost text is on the creature with the pin's cause.
local function inForce(who, text)
    local have = {}
    for _, b in ipairs(storyBoosts(who)) do
        if b.cause == CAUSE then have[b.text] = (have[b.text] or 0) + 1 end
    end
    for part in text:gmatch("[^;]+") do
        if not have[part] then return false end
    end
    return true
end

local function anyLeft(who, text)
    local have = {}
    for _, b in ipairs(storyBoosts(who)) do
        if b.cause == CAUSE then have[b.text] = true end
    end
    for part in text:gmatch("[^;]+") do
        if have[part] then return true end
    end
    return false
end

-- The open ledger rows of pins, oldest first; filtered by who and kind when given.
local function pinRows(who, kind)
    local out = {}
    for _, row in ipairs(GK.Stage.Open("fixture")) do
        local p = row.payload
        local op = p and p.op
        if p.fixture == FIXTURE and op and op.op == "boost" and op.roll and (who == nil or op.who == who) and (kind == nil or op.roll == kind) then out[#out + 1] = row end
    end
    return out
end

-- ---------------------------------------------------------------------------------------
-- Pin and Unpin

-- A removal asked: watched every tick until the boost has left, then a roll_unpin row. The game
-- carries a removal out 3-4 ticks after it is asked and then takes every boost of its text and
-- cause; when the boost was not on to begin with (a row an earlier Lua state left open), nothing
-- shows the removal done, so it counts as pending for REMOVAL_TICKS - a boost of the same text
-- added before then would be taken with it.
local REMOVAL_TICKS = 8
local function leave(op, row, why, rolled)
    local absent = not anyLeft(op.who, op.text)
    Rolls.Leaving[#Rolls.Leaving + 1] = { who = op.who, kind = op.roll, n = op.n, text = op.text, track = row.seq, why = why,
                                          asked = ticks(), rolled = rolled, notBefore = absent and ticks() + REMOVAL_TICKS or nil }
end

local function leavingOf(who, text)
    for _, l in ipairs(Rolls.Leaving) do if l.who == who and l.text == text then return true end end
    return false
end

local function undo(row, why, rolled)
    local op = row.payload.op
    local ok, because = GK.Stage.UndoTrack(row)
    if ok then leave(op, row, why, rolled) end
    return ok, because
end

local function needScope(verb)
    if not (GK.Stage.Ledger and GK.Stage.Ledger.baseline) then
        error("GK.Rolls." .. verb .. ": no stage scope is open - call GK.Stage.Begin() first, so that GK.Stage.Clean "
            .. "takes the pin off again; nothing was pinned", 3)
    end
end

local function integer(value)
    return type(value) == "number" and value == math.floor(value) and value or nil
end

--- Pins the natural die of `who`'s own rolls of `kind` at `n`. who: a creature's uuid (default
--- the host); kind: one of GK.Rolls.Kinds(); n: the die (1..20; a damage kind: every die of the
--- roll, at least 1); for Initiative, n is a bonus added to the initiative roll (any integer;
--- +-100 decides the order). once: the pin goes after the holder's next finished roll of the
--- kind (checks and saving throws only). A pin of the same who and kind is replaced (of the same
--- n: kept). Karmic Dice forces successes above a pin, so a pin of any kind but Initiative switches
--- it off for the scope (`karmic = true` keeps it on). Refused outside a stage scope. The answer is
--- observed: it comes once the boost is on the creature and the roll manager no longer weights.
function Rolls.Pin(spec)
    if type(spec) ~= "table" then
        error("GK.Rolls.Pin{ who, kind, n, once, karmic }: a table, got " .. tostring(spec), 2)
    end
    local who = bare(spec.who or Osi.GetHostCharacter())
    local entity = type(who) == "string" and try(Ext.Entity.Get, who)
    if not (entity and try(function() return entity.BoostsContainer end)) then
        error("GK.Rolls.Pin: " .. tostring(who) .. " is no creature that holds boosts; nothing was pinned", 2)
    end
    local kind = kindOf(spec.kind, "Pin")
    local n = integer(spec.n)
    if not n then error("GK.Rolls.Pin: n is a whole number, got " .. tostring(spec.n) .. "; nothing was pinned", 2) end
    if kind ~= "Initiative" and n < 1 then error("GK.Rolls.Pin: a die is at least 1, got " .. n .. "; nothing was pinned", 2) end
    if kind ~= "Initiative" and not kind:find("Damage$") and n > 20 then
        error("GK.Rolls.Pin: a d20 is at most 20, got " .. n .. "; nothing was pinned", 2)
    end
    if spec.once and not finishes(kind) then
        error("GK.Rolls.Pin: once is for the kinds the roll system finishes as rolls of their own (checks, saving throws); "
            .. kind .. " is not one - pin it and Unpin after the roll; nothing was pinned", 2)
    end
    needScope("Pin")
    -- Karmic Dice forces successes above a pin (a dialogue check pinned at 1 came out 17): off for
    -- the scope, unless the caller keeps it on to study it
    local karmic
    if kind ~= "Initiative" and spec.karmic ~= true and Ext.Utils.GetGlobalSwitches().WeightedRolls == true then
        writeKarmic(false)
        karmic = "switched off for the scope"
    end
    local text = textOf(kind, n)
    -- A removal of a boost takes every boost of the same text and cause that is on the creature
    -- when the game carries it out, 3-4 ticks later: a pin of the same text is kept rather than
    -- taken off and added again, and a new boost waits until a leaving one of its text is gone.
    -- Only a row of this scope is kept: GK.Stage.Clean undoes the rows of its own scope, and a
    -- fixture's row of an earlier scope stays open - also one carried from a previous Lua state (GK.Fixture
    -- carries the open rows of the log, which outlives a restart of the game), whose boost a fresh
    -- level never had. Such a row is undone like a replaced one and the pin made anew in this scope.
    local kept
    for _, row in ipairs(pinRows(who, kind)) do
        if row.payload.op.text == text and not kept and row.scope == GK.EventLog.Scope then kept = row
        else undo(row, row.scope == GK.EventLog.Scope and "replaced" or "an earlier scope's") end
    end
    local once = spec.once and true or nil
    -- a kept row is a pin while its boost is on the creature or asked for by this Lua state
    local row, added = kept, kept ~= nil and (Rolls.Asked[kept.seq] or anyLeft(who, text))
    if kept then
        kept.payload.op.once = once
    else
        if not leavingOf(who, text) then Osi.AddBoosts(who, text, CAUSE, who) added = true end
        row = GK.Stage.Track{ kind = "other", entity = who, fixture = FIXTURE, key = "rolls:" .. (GK.EventLog.Seq + 1),
            op = { op = "boost", who = who, text = text, cause = CAUSE, roll = kind, n = n, once = once },
            note = ("pin %s of %s at %d%s"):format(kind, who, n, once and " once" or "") }
    end
    if added then Rolls.Asked[row.seq] = true end
    Rolls.Seen[who] = true
    GK.EventLog.Append("roll_pin", { who = who, kind = kind, n = n, once = once, boost = text, track = row.seq, kept = kept and true or nil })
    local asked = ticks()
    local answer = { ok = true, who = who, kind = kind, n = n, once = once, boost = text, track = row.seq,
                     inForce = false, karmic = karmic }
    return GK.Observe(answer, function(_, final)
        if not added and not anyLeft(who, text) and not leavingOf(who, text) then
            Osi.AddBoosts(who, text, CAUSE, who)
            added = true
            Rolls.Asked[row.seq] = true
        end
        if added and inForce(who, text) and (not karmic or Rolls.Karmic().mode == "Random") then
            answer.inForce, answer.ticks = true, ticks() - asked
            return answer
        end
        if final or ticks() - asked > LIMIT_TICKS then
            return { ok = false, who = who, kind = kind, boost = text, track = row.seq, karmic = karmic,
                     error = inForce(who, text) and ("Karmic Dice was switched off and the roll manager still weights after " .. (ticks() - asked) .. " ticks")
                         or ("the boost was asked for and is not on " .. who .. " after " .. (ticks() - asked) .. " ticks") }
        end
    end, WITHIN)
end

--- Takes the pins of `who` (default the host) and `kind` (default: every kind) off. The answer
--- is observed: it comes once the boosts have left the creature. { ok, removed }.
function Rolls.Unpin(spec)
    spec = spec or {}
    local who = bare(spec.who or Osi.GetHostCharacter())
    local kind = spec.kind and kindOf(spec.kind, "Unpin") or nil
    local removed, texts = 0, {}
    for _, row in ipairs(pinRows(who, kind)) do
        if undo(row, "unpin") then
            removed = removed + 1
            texts[#texts + 1] = row.payload.op.text
        end
    end
    local asked = ticks()
    local answer = { ok = true, who = who, kind = kind, removed = removed }
    if removed == 0 then return answer end
    return GK.Observe(answer, function(_, final)
        for _, text in ipairs(texts) do
            if anyLeft(who, text) then
                if final or ticks() - asked > LIMIT_TICKS then answer.ok, answer.error = false, "the boost is still on " .. who .. " after " .. (ticks() - asked) .. " ticks" return answer end
                return nil
            end
        end
        answer.ticks = ticks() - asked
        return answer
    end, WITHIN)
end

--- What is pinned now, read from the boosts of every creature pinned in this Lua state or named
--- by an open ledger row: { { who, kind, n, once, boost, track, tracked } }. `tracked` false: the
--- ledger row is undone and the boost has not left yet.
function Rolls.Pins()
    local whos, rows = {}, {}
    for who in pairs(Rolls.Seen) do whos[who] = true end
    for _, row in ipairs(pinRows()) do
        local op = row.payload.op
        whos[op.who] = true
        rows[op.who .. "|" .. op.text] = row
    end
    local out = {}
    for who in pairs(whos) do
        local seen = {}
        for _, b in ipairs(storyBoosts(who)) do
            if b.cause == CAUSE then
                local kind, n = b.text:match("^MinimumRollResult%((%w+),(%-?%d+)%)$")
                if not kind then n = b.text:match("^Initiative%((%-?%d+)%)$"); kind = n and "Initiative" end
                if kind and not seen[kind .. n] then
                    seen[kind .. n] = true
                    local text = textOf(kind, tonumber(n))
                    local row = rows[who .. "|" .. text]
                    out[#out + 1] = { who = who, kind = kind, n = tonumber(n), boost = text, once = row and row.payload.op.once or nil,
                                      track = row and row.seq or nil, tracked = row ~= nil }
                end
            end
        end
    end
    table.sort(out, function(a, b) return a.who .. a.kind < b.who .. b.kind end)
    return out
end

-- Every tick: the removals asked whose boost has left become roll_unpin rows.
local function watchLeaving()
    if #Rolls.Leaving == 0 then return end
    local keep = {}
    for _, l in ipairs(Rolls.Leaving) do
        local gone = not anyLeft(l.who, l.text) and (l.notBefore == nil or ticks() >= l.notBefore)
        local stuck = ticks() - l.asked > LEAVE_TICKS
        if gone or stuck then
            GK.EventLog.Append("roll_unpin", { who = l.who, kind = l.kind, n = l.n, why = l.why, track = l.track, asked = l.asked,
                                               rolled = l.rolled, ticks = ticks() - l.asked, stuck = stuck and not gone or nil })
        else
            keep[#keep + 1] = l
        end
    end
    Rolls.Leaving = keep
end

-- ---------------------------------------------------------------------------------------
-- The rolls the game finishes

local function translated(value)
    if value == nil then return nil end
    local text = try(function() return value:Get() end)
    if (not text or text == "") then
        local handle = try(function() return value.Handle.Handle end)
        text = handle and try(Ext.Loca.GetTranslatedString, handle)
    end
    return text ~= "" and text or nil
end

-- The modifiers the roll's metadata lists, as { { name, value } }; nil when the roll is gone.
local function modifiersOf(rolled)
    local md = try(function() return rolled.Metadata end)
    if not md then return nil end
    local out = {}
    local function add(name, value)
        if type(value) == "number" and value ~= 0 then out[#out + 1] = { name = name, value = value } end
    end
    try(function() for skill, v in pairs(md.SkillBonuses) do add("skill " .. tostring(skill), v) end end)
    try(function() for ability, v in pairs(md.AbilityBoosts) do add("ability " .. tostring(ability), v) end end)
    add("proficiency", try(function() return md.ProficiencyBonus end))
    add("roll bonus", try(function() return md.RollBonus end))
    add("high ground", try(function() return md.HighGroundBonus end))
    add("low ground", try(function() return -md.LowGroundPenalty end))
    for _, list in ipairs({ "ResolvedRollBonuses", "FixedRollBonuses" }) do
        try(function()
            for _, b in ipairs(md[list]) do
                local value = b.ResolvedRollBonus or b.RollBonus or b.Bonus
                local name = translated(b.Description) or translated(b.SourceName) or list
                if value then out[#out + 1] = { name = name, value = value } end
            end
        end)
    end
    return out
end

local function kindName(number)
    local name = try(function() return tostring(Ext.Enums.StatsRollType[number]) end)
    return name or tostring(number)
end

local request, fill   -- what a roll's request holds, and the finished roll filled from it (below)

-- One finished roll as plain fields (the game's esv::roll::FinishedEvent, and the roll's own
-- request: alive, or as it was when destroyed).
local function finished(ev)
    local roller, subject = uuidOf(ev.Roller), uuidOf(ev.Subject)
    local r = { who = roller, subject = subject ~= roller and subject or nil, kind = kindName(ev.RollType),
                ability = tostring(ev.Ability), skill = tostring(ev.Skill), dc = ev.DC, natural = ev.NaturalRoll,
                bonus = ev.DiceAdditionalValue, context = ev.RollContext, canceled = ev.Canceled or nil, tick = ticks(),
                inspired = ev.ConsumedInspirationPoint or nil }
    -- the enumerations' own words for "none": None, Invalid, and their end marker Sentinel
    local none = { None = true, Invalid = true, Sentinel = true }
    if none[r.ability] then r.ability = nil end
    if none[r.skill] then r.skill = nil end
    r.uuid = try(function() return tostring(ev.RollUuid) end)
    -- the roll's request: alive for a passive roll; an active roll's is destroyed before its
    -- finished event, and what it held then was kept by its uuid (onGone below)
    local rolled = try(function() return Ext.Entity.Get(ev.Roll).RequestedRoll end)
    local held = rolled and request(rolled) or (r.uuid and Rolls.Gone[r.uuid])
    if r.uuid then Rolls.Gone[r.uuid] = nil end
    fill(r, held, ev)
    return r
end

-- What a request holds, as plain values. Advantage and disadvantage: the finished event's own
-- flags stay false for a passive roll with an Advantage boost, seen in the game;
-- the request says it (RequestedRoll.Roll). The game draws a second d20 for every roll
-- (Result.DiscardedDiceTotal, set on a plain roll too) and keeps the higher with advantage, the
-- lower with disadvantage, either with both or neither: `discarded` is the other die.
request = function(c)
    local critical = try(function() return tostring(c.Result.Critical) end)
    return { total = try(function() return c.Result.Total end), critical = critical ~= "None" and critical or nil,
             modifiers = modifiersOf(c), discarded = try(function() return c.Result.DiscardedDiceTotal end),
             advantage = try(function() return c.Roll.Advantage end), disadvantage = try(function() return c.Roll.Disadvantage end),
             tick = ticks() }
end

fill = function(r, held, ev)
    held = held or {}
    local adv, dis = held.advantage, held.disadvantage
    if adv == nil and ev then adv = ev.Advantage end
    if dis == nil and ev then dis = ev.Disadvantage end
    r.advantage = (adv and dis and "both") or (adv and "advantage") or (dis and "disadvantage") or nil
    r.total = held.total or r.total
    r.critical = held.critical or r.critical
    r.modifiers = held.modifiers or r.modifiers
    r.discarded = held.discarded or r.discarded
    if r.total == nil and type(r.natural) == "number" and type(r.bonus) == "number" then r.total = r.natural + r.bonus end
end

-- A request destroyed: what it held, by its uuid, for its finished event (an active roll's comes
-- after); entries older than GONE_TICKS are dropped.
local GONE_TICKS = 30
local function onGone(c)
    local uuid = try(function() return tostring(c.RollUuid) end)
    if not uuid or try(function() return c.Finished end) ~= true then return end
    Rolls.Gone[uuid] = request(c)
    for id, held in pairs(Rolls.Gone) do
        if ticks() - held.tick > GONE_TICKS then Rolls.Gone[id] = nil end
    end
end

local function onFinished(c)
    for _, ev in ipairs(c.Events or {}) do
        local ok, r = pcall(finished, ev)
        if ok and r.who then
            -- a pin of this roller and kind in force now: its n, and a once-pin goes
            -- (a row whose boost is not on - one an earlier Lua state left open - pinned nothing)
            for _, row in ipairs(pinRows(r.who, r.kind)) do
                local op = row.payload.op
                if inForce(r.who, op.text) then
                    r.pinned = op.n
                    if op.once then undo(row, "once", r.tick) end
                end
            end
            local queue = Rolls.Recent[r.who] or {}
            queue[#queue + 1] = r
            Rolls.Recent[r.who] = queue
        end
    end
end

--- The oldest finished roll of `who` that no RollResult event has taken yet (GK.GameLog joins it
--- to its game_roll row; the event comes a tick after the roll), or nil; `take` removes it. A roll
--- older than FINISHED_TICKS ticks is dropped.
function Rolls.Finished(who, take)
    who = bare(who)
    local queue = who and Rolls.Recent[who]
    if not queue then return nil end
    while queue[1] and ticks() - queue[1].tick > FINISHED_TICKS do table.remove(queue, 1) end
    local r = queue[1]
    if r and take then table.remove(queue, 1) end
    if #queue == 0 then Rolls.Recent[who] = nil end
    return r
end

if not Rolls.Subscribed then
    Rolls.Subscribed = true
    pcall(function()
        Ext.Entity.OnCreateDeferred("ServerRollFinishedEvent", function(_, _, c) pcall(onFinished, c) end)
    end)
    -- read only, inside the engine's destruction: the request's values are copied, nothing is called
    pcall(function()
        Ext.Entity.OnDestroy("RequestedRoll", function(_, _, c) pcall(onGone, c) end)
    end)
    Ext.Events.Tick:Subscribe(function() pcall(watchLeaving) end)
end

-- ---------------------------------------------------------------------------------------
-- Karmic Dice

local function manager() return try(function() return Ext.System.ServerRollSave.RollManager end) end

--- Karmic Dice now: { on, option, mode, saved }. `option` is the game's own setting
--- (GlobalSwitches.WeightedRolls), `mode` the roll manager's RollModeType that follows it
--- (WeightedRandom when on, Random when off), `saved` the roll streams component's copy, which
--- does not follow the option.
function Rolls.Karmic()
    local switches = Ext.Utils.GetGlobalSwitches()
    local option = try(function() return switches.WeightedRolls end)
    local m = manager()
    local streams = try(function() return Ext.Entity.GetAllEntitiesWithComponent("ServerRollStreams")[1].ServerRollStreams end)
    local mode = m and try(function() return tostring(m.RollModeType) end)
    return { on = mode == "WeightedRandom", option = option, mode = mode,
             saved = streams and try(function() return tostring(streams.RollModeType) end) }
end

--- Switches Karmic Dice on or off for the length of the stage scope: the game's option
--- (GlobalSwitches.WeightedRolls) is written and GK.Stage.Clean writes the old value back. Refused
--- outside a scope. The answer is observed: it comes once the roll manager follows.
-- Writes the option, and the undo that writes the old value back on Clean.
--- Writes Karmic Dice (GlobalSwitches.WeightedRolls) and nothing else: the undo of SetKarmic.
function Rolls.WriteKarmic(on) Ext.Utils.GetGlobalSwitches().WeightedRolls = on end

-- The undo of a write of the rolls, as data in its ledger row (GK.Fixture's `op`): it outlives a reload
-- of the bridge. The first write of a key in a scope keeps the value from before the scope.
local function trackUndo(key, op, note)
    GK.Stage.Track{ kind = "other", fixture = FIXTURE, key = "rolls:" .. key, op = op, note = note }
end

writeKarmic = function(on)
    local switches = Ext.Utils.GetGlobalSwitches()
    local was = switches.WeightedRolls
    if was ~= on then
        trackUndo("karmic", { op = "karmic", on = was }, "Karmic Dice set back to " .. tostring(was))
        switches.WeightedRolls = on
    end
    return was
end

-- ---------------------------------------------------------------------------------------
-- The roll streams: where the dice come from

local function streamsOf(holder)
    local out = {}
    for _, s in ipairs(try(function() return holder.Streams end) or {}) do
        local st = try(function() return s.State end)
        local combat = try(function() return tostring(s.CombatGuid) end)
        out[#out + 1] = { type = try(function() return tostring(s.StreamType) end),
            seed = st and try(function() return st.Seed end), rolls = st and try(function() return st.NumRolls end),
            frame = st and try(function() return st.Frame end),
            failDebt = st and try(function() return st.FailDebt end), successDebt = st and try(function() return st.SuccessDebt end),
            player = try(function() local p = tostring(s.PlayerUuid) return p ~= "" and p or nil end),
            combat = combat ~= "00000000-0000-0000-0000-000000000000" and combat or nil,
            cast = try(function() local c = tostring(s.RandomCastUuid) return c ~= "" and c or nil end) }
    end
    return out
end

--- The server's roll streams now: { base, frame, mode, streams = { { type, seed, rolls, frame,
--- failDebt, successDebt, player, combat, cast } }, saved = the same of the roll streams component }.
--- Read from the roll manager (Ext.System.ServerRollSave.RollManager); `rolls` counts the dice
--- drawn from the stream (two for every d20 roll). A load sets `base` to the clock in seconds and
--- makes the streams anew.
function Rolls.Streams()
    local m = manager()
    local c = try(function() return Ext.Entity.GetAllEntitiesWithComponent("ServerRollStreams")[1].ServerRollStreams end)
    local function read(holder)
        if not holder then return nil end
        return { base = try(function() return holder.BaseSeed end), frame = try(function() return holder.LastFrame end),
                 mode = try(function() return tostring(holder.RollModeType) end), streams = streamsOf(holder) }
    end
    local out = read(m) or {}
    out.saved = read(c)
    return out
end

local function streamsComponent()
    return try(function() return Ext.Entity.GetAllEntitiesWithComponent("ServerRollStreams")[1].ServerRollStreams end)
end

-- The streams of the component that `spec` names: its type, and its player and combat when given.
local function matching(spec)
    local out = {}
    local c = streamsComponent()
    for i, s in ipairs(c and try(function() return c.Streams end) or {}) do
        local mine = streamsOf({ Streams = { s } })[1]
        if mine.type == spec.type and (spec.player == nil or mine.player == spec.player)
            and (spec.combat == nil or mine.combat == spec.combat) then
            out[#out + 1] = { index = i, stream = s, read = mine }
        end
    end
    return out
end

--- Writes the seed and the count of roll streams for the length of the stage scope: `list` is
--- { { type, player, combat, seed, rolls } } - the shape GK.Rolls.Streams().streams has, so a
--- reading taken earlier puts the streams back where they were - or a whole Streams() answer.
--- Each entry names one stream of the roll streams component (ServerRollStreams; the game draws
--- from it - the roll manager follows it, a write to the manager is not honoured) by its type,
--- and its player and combat when given; `seed` and `rolls` left out stay. Clean writes the old
--- seed and count back, or reports the stream gone. Refused outside a scope.
--- { ok, written = { { type, player, combat, seed, rolls, was = { seed, rolls } } }, missing = { spec } }.
function Rolls.SetStreams(list)
    if type(list) == "table" and list.streams then list = list.streams end
    if type(list) ~= "table" then error("GK.Rolls.SetStreams{ { type, player, combat, seed, rolls } }: a list, got " .. tostring(list), 2) end
    needScope("SetStreams")
    local written, missing = {}, {}
    for _, spec in ipairs(list) do
        if type(spec) ~= "table" or type(spec.type) ~= "string" then
            error("GK.Rolls.SetStreams: each entry names a stream by its type (" .. tostring(spec) .. ")", 2)
        end
        for _, field in ipairs({ "seed", "rolls" }) do
            if spec[field] ~= nil and not (integer(spec[field]) and spec[field] >= 0) then
                error("GK.Rolls.SetStreams: " .. field .. " is a whole number from 0, got " .. tostring(spec[field]), 2)
            end
        end
        local found = matching(spec)
        if #found > 1 then
            error(("GK.Rolls.SetStreams: %d streams of type %s - name the player or the combat"):format(#found, spec.type), 2)
        end
        local f = found[1]
        if not f then
            missing[#missing + 1] = { type = spec.type, player = spec.player, combat = spec.combat }
        else
            local key = { type = spec.type, player = f.read.player, combat = f.read.combat }
            local was = { seed = f.read.seed, rolls = f.read.rolls }
            trackUndo("stream:" .. key.type .. "|" .. tostring(key.player) .. "|" .. tostring(key.combat),
                { op = "streams", list = { { type = key.type, player = key.player, combat = key.combat, seed = was.seed, rolls = was.rolls } } },
                ("roll stream %s%s set back to seed %s, %s dice"):format(key.type, key.player and (" of " .. key.player) or "",
                    tostring(was.seed), tostring(was.rolls)))
            if spec.seed ~= nil then f.stream.State.Seed = spec.seed end
            if spec.rolls ~= nil then f.stream.State.NumRolls = spec.rolls end
            local now = matching(key)[1].read
            written[#written + 1] = { type = key.type, player = key.player, combat = key.combat, seed = now.seed, rolls = now.rolls, was = was }
        end
    end
    return { ok = #missing == 0, written = written, missing = #missing > 0 and missing or nil }
end

--- Writes the seed and count of the streams named (the shape SetStreams takes) and nothing else:
--- the undo of SetStreams. A stream that is gone (a load makes them anew) is an error.
function Rolls.WriteStreams(list)
    for _, spec in ipairs(list or {}) do
        local back = matching(spec)[1]
        if not back then error("the stream " .. tostring(spec.type) .. " is gone (a load makes the streams anew)") end
        if spec.seed ~= nil then back.stream.State.Seed = spec.seed end
        if spec.rolls ~= nil then back.stream.State.NumRolls = spec.rolls end
    end
end

-- The outputs 0..upto of the game's generator for a stream: the standard 64-bit Mersenne Twister
-- seeded with the stream's seed (code:eoc::roll::stream::RollManager::InternalRollDice).
local function outputs(seed, upto)
    local mt = { [0] = seed }
    for i = 1, 311 do local p = mt[i - 1]; mt[i] = (p ~ (p >> 62)) * 6364136223846793005 + i end
    local at, out = 312, {}
    for k = 0, upto do
        if at >= 312 then
            for i = 0, 311 do
                local x = (mt[i] & ~0x7FFFFFFF) | (mt[(i + 1) % 312] & 0x7FFFFFFF)
                local v = mt[(i + 156) % 312] ~ (x >> 1)
                if x & 1 ~= 0 then v = v ~ 0xB5026F5AA96619E9 end
                mt[i] = v
            end
            at = 0
        end
        local x = mt[at]; at = at + 1
        x = x ~ ((x >> 29) & 0x5555555555555555)
        x = x ~ ((x << 17) & 0x71D67FFFEDA60000)
        x = x ~ ((x << 37) & 0xFFF7EEE000000000)
        out[k] = x ~ (x >> 43)
    end
    return out
end

-- One die of `faces` from an output, the output taken as unsigned.
local function face(x, faces) return 1 + (((x >> 1) % faces) * 2 + (x & 1)) % faces end

-- The output a call's die number k (from 0) is drawn at, the stream's count being `count` when the
-- call begins: the generator is seeded once for the call and each die discards the count again.
local function drawnAt(count, k) return (k + 1) * count + (k * (k + 3)) // 2 end

local function oneStream(spec, verb)
    if type(spec) ~= "table" or type(spec.type) ~= "string" then
        error("GK.Rolls." .. verb .. "{ type, player, combat, ... }: the stream's type, got " .. tostring(spec and spec.type), 3)
    end
    local found = matching(spec)
    if #found > 1 then error(("GK.Rolls.%s: %d streams of type %s - name the player or the combat"):format(verb, #found, spec.type), 3) end
    return found[1] and found[1].read
end

--- The dice the stream `spec` names gives to its next roll, computed from its seed and count and
--- not drawn: { ok, type, player, combat, seed, rolls, dice = { ... } }. `spec.dice` is how many
--- dice the roll draws (default 2: the game draws a pair for every d20 roll and keeps the first
--- unless advantage or disadvantage chooses), `spec.faces` their faces (default 20), `spec.rolls`
--- a count to compute from instead of the stream's own. A roll forced by Karmic Dice, a die
--- thrown again by a reroll boost and a die replaced by a guaranteed-outcome boost are not these.
function Rolls.Next(spec)
    local s = oneStream(spec, "Next")
    if not s then return { ok = false, error = "no stream of type " .. spec.type .. " (the game makes a stream at its first roll)" } end
    local dice, faces, count = spec.dice or 2, spec.faces or 20, spec.rolls or s.rolls
    if not (integer(dice) and dice >= 1 and dice <= 64) then error("GK.Rolls.Next: dice is a whole number from 1 to 64, got " .. tostring(spec.dice), 2) end
    if not (integer(faces) and faces >= 2) then error("GK.Rolls.Next: faces is a whole number from 2, got " .. tostring(spec.faces), 2) end
    if not (integer(count) and count >= 0) then error("GK.Rolls.Next: rolls is a whole number from 0, got " .. tostring(spec.rolls), 2) end
    local out = outputs(s.seed, drawnAt(count, dice - 1))
    local list = {}
    for k = 0, dice - 1 do list[#list + 1] = face(out[drawnAt(count, k)], faces) end
    return { ok = true, type = s.type, player = s.player, combat = s.combat, seed = s.seed, rolls = count, dice = list }
end

local SEARCH = 4000   -- counts looked through for a wanted die; one in `faces` fits (one in its square for both dice)

--- Chooses the natural die of the next d20 roll drawn from the stream `spec` names, for the
--- length of the stage scope: moves the stream's count forward to the nearest one at which the
--- first die of the pair is `spec.die` and, with `spec.both`, the second too (so that advantage
--- and disadvantage keep it as well). One roll is chosen: the roll after it draws what the
--- stream gives at the count it is left at. `spec.faces` (default 20) and `spec.dice` (default 2)
--- describe another roll, every die of which is then `spec.die` with `both`. Not chosen: a roll
--- Karmic Dice forces, a die a boost throws again or bounds, a die replaced outside the streams.
--- Refused outside a scope. { ok, type, player, combat, seed, rolls, was, dice }.
function Rolls.SetNext(spec)
    local s = oneStream(spec, "SetNext")
    needScope("SetNext")
    if not s then return { ok = false, error = "no stream of type " .. spec.type .. " (the game makes a stream at its first roll)" } end
    local dice, faces = spec.dice or 2, spec.faces or 20
    if not (integer(faces) and faces >= 2) then error("GK.Rolls.SetNext: faces is a whole number from 2, got " .. tostring(spec.faces), 2) end
    if not (integer(dice) and dice >= 1 and dice <= 4) then error("GK.Rolls.SetNext: dice is a whole number from 1 to 4, got " .. tostring(spec.dice), 2) end
    if not (integer(spec.die) and spec.die >= 1 and spec.die <= faces) then
        error(("GK.Rolls.SetNext: die is a whole number from 1 to %d, got %s"):format(faces, tostring(spec.die)), 2)
    end
    local last = s.rolls + SEARCH
    local out = outputs(s.seed, drawnAt(last, dice - 1))
    for count = s.rolls, last do
        local fits = face(out[count], faces) == spec.die
        if fits and spec.both then
            for k = 1, dice - 1 do fits = fits and face(out[drawnAt(count, k)], faces) == spec.die end
        end
        if fits then
            local set = Rolls.SetStreams{ { type = s.type, player = s.player, combat = s.combat, rolls = count } }
            local list = {}
            for k = 0, dice - 1 do list[#list + 1] = face(out[drawnAt(count, k)], faces) end
            return { ok = set.ok, type = s.type, player = s.player, combat = s.combat, seed = s.seed, rolls = count, was = s.rolls, dice = list }
        end
    end
    return { ok = false, error = ("no count within %d of %d gives the die %d"):format(SEARCH, s.rolls, spec.die), rolls = s.rolls }
end

function Rolls.SetKarmic(on)
    if type(on) ~= "boolean" then error("GK.Rolls.SetKarmic(on): true or false, got " .. tostring(on), 2) end
    needScope("SetKarmic")
    local was = writeKarmic(on)
    local want = on and "WeightedRandom" or "Random"
    local asked = ticks()
    return GK.Observe({ ok = true, on = on, was = was }, function(_, final)
        local now = Rolls.Karmic()
        if now.mode == want then now.ok, now.was, now.ticks = true, was, ticks() - asked return now end
        if final or ticks() - asked > LIMIT_TICKS then now.ok, now.was = false, was; now.error = "the roll manager's mode is " .. tostring(now.mode) .. ", not " .. want return now end
    end, WITHIN)
end

-- ---------------------------------------------------------------------------------------
-- Inspiration: the points a failed roll in the roll window is thrown again for

-- The resource's definition, by its name in the game's static data (GK.Stage.ResourceId).
local function inspirationId() return GK.Stage.ResourceId("InspirationPoint") end

-- Every action resource row of InspirationPoint, with the entity that holds it.
local function inspirationRows()
    local out = {}
    local id = inspirationId()
    if not id then return out end
    for _, e in ipairs(Ext.Entity.GetAllEntitiesWithComponent("ActionResources")) do
        local list = try(function() return e.ActionResources.Resources[id] end)
        for _, r in ipairs(list or {}) do out[#out + 1] = { entity = e, row = r } end
    end
    return out
end

-- The most points the party can hold: the resource definition's MaxValue. The resource row's own
-- MaxAmount is not it: it grows as points are given.
local function inspirationCap()
    local id = inspirationId()
    return id and try(function() return math.floor(Ext.StaticData.Get(id, "ActionResource").MaxValue + 0.5) end)
end

--- The party's inspiration now: { points, max, holders = { { uuid?, points } } } (max: the
--- resource definition's; holders: every entity that keeps the resource, with its amount).
function Rolls.Inspiration()
    local rows = inspirationRows()
    local first = rows[1] and rows[1].row
    local holders = {}
    for _, r in ipairs(rows) do
        -- the holder is the party's own entity (PartyComposition, PartyView), not a character: one
        -- pool for the party, its ceiling the resource's MaxValue (seen: six points given, four held)
        holders[#holders + 1] = { uuid = try(function() return r.entity.Uuid.EntityUuid end), entity = tostring(r.entity),
                                  party = try(function() return r.entity.PartyComposition ~= nil end) or false,
                                  points = math.floor(r.row.Amount + 0.5) }
    end
    return { points = first and math.floor(first.Amount + 0.5) or nil, max = inspirationCap(), holders = holders }
end

--- Writes the party's inspiration on every entity that keeps it (the one writer of resources,
--- GK.Stage.WriteResource): the undo of SetInspiration.
function Rolls.WriteInspiration(n, top)
    local id = inspirationId()
    for _, r in ipairs(inspirationRows()) do GK.Stage.WriteResource(r.entity, id, 1, n, top) end
end
local writeInspiration = Rolls.WriteInspiration

-- Writes n points over rows that exist, with the undo as data that writes the old ones back on Clean.
local function setInspiration(value, was, top)
    if was ~= value then
        trackUndo("inspiration", { op = "inspiration", points = was, max = top }, "inspiration set back to " .. was)
        writeInspiration(value, value > top and value or nil)
    end
    return { ok = true, points = Rolls.Inspiration().points, was = was }
end

--- Sets the party's inspiration to n (0..max, max read from the resource's definition; above it
--- only with options.past) for the length of the scope: the resource's amount (and its MaxAmount,
--- when lower) is written and GK.Stage.Clean writes both back. A party that has never had a point
--- has no row of the resource (a fresh level): one point is given through the story
--- (Osi.GiveInspirationPoints), which makes the row, and the points are then written as above with 0
--- to go back to; the row itself stays, as the game keeps it. Refused outside a scope. The answer
--- is observed when a point had to be given. { ok, points, was, made }.
function Rolls.SetInspiration(n, options)
    options = options or {}
    local value = integer(n)
    if not value or value < 0 then error("GK.Rolls.SetInspiration(n): a whole number from 0, got " .. tostring(n), 2) end
    needScope("SetInspiration")
    local cap = inspirationCap() or 0
    if value > cap and not options.past then
        return { ok = false, points = Rolls.Inspiration().points, max = cap, error = "at most " .. cap .. " (the game's ceiling; options.past to go beyond it)" }
    end
    local rows = inspirationRows()
    if #rows > 0 then
        return setInspiration(value, math.floor(rows[1].row.Amount + 0.5), rows[1].row.MaxAmount)
    end
    Osi.GiveInspirationPoints(Osi.GetHostCharacter(), 1, "", "")
    local asked = ticks()
    return GK.Observe({ ok = true, made = true }, function(_, final)
        local now = inspirationRows()
        if #now > 0 then
            -- the given point is on the row now: written to n whatever n is, and back to 0 on Clean
            trackUndo("inspiration", { op = "inspiration", points = 0, max = 0 }, "inspiration set back to 0")
            writeInspiration(value, value)
            return { ok = true, points = Rolls.Inspiration().points, was = 0, made = true, ticks = ticks() - asked }
        end
        if final or ticks() - asked > LIMIT_TICKS then return { ok = false, error = "a point was given and no entity holds the InspirationPoint resource after " .. (ticks() - asked) .. " ticks" } end
    end, WITHIN)
end

return Rolls
