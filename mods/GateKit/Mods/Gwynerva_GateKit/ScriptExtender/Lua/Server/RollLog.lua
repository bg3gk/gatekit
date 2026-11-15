-- The roll log: one `dice_thrown` row for every roll the game's roll manager throws, whoever asked
-- for it - a check, an attack, a save, damage, a heal, initiative, a die of a stats expression.
-- The rows come from the roll event of the Script Extender (Ext.Events.Roll: one event for a roll,
-- whichever of the roll manager's functions carried it out); in developer mode the event carries
-- the return address of the code that asked.

-- That address is looked up among the askers the registry names: a known asker gives the row its
-- `origin`, any other address gives `origin = "unknown"` with the address, never a dropped row.
-- The family `dice` is off until GK.EventLog.On("dice"): a fight with the AI thinking throws many.

GK.RollLog = GK.RollLog or {}
local Log = GK.RollLog

--- Each verb's class, for the fair/cheat/observation split.
Log.Class = { Available = "observation", Origin = "observation", Origins = "observation", Causes = "observation", Forget = "observation" }

-- The askers (the functions that call the roll manager; `what` is the roll asked for) and the
-- causes are named in Server/RollOrigins.json by registry id; their places in each executable
-- are written there from the registry's build tables by `gatekit registry places`, never by hand.
local ORIGINS = "Mods/Gwynerva_GateKit/ScriptExtender/Lua/Server/RollOrigins.json"
local ASKERS, CAUSES, STATUS_ENTER = {}, {}, {}
do
    local ok, data = pcall(function() return Ext.Json.Parse(Ext.IO.LoadFile(ORIGINS, "data")) end)
    data = ok and type(data) == "table" and data or {}
    -- the newest build the file holds: the registry keeps one table per build of the game
    local build
    for name in pairs(data.places or {}) do if not build or name > build then build = name end end
    for module, places in pairs(build and data.places[build] or {}) do
        ASKERS[module], CAUSES[module] = {}, {}
        for _, a in ipairs(data.askers or {}) do
            local at = places[a.id]
            if at then table.insert(ASKERS[module], { at[1], at[2], a.id, a.what, a.entry }) end
        end
        for _, c in ipairs(data.causes or {}) do
            local at = places[c.id]
            if at then table.insert(CAUSES[module], { at[1], at[2], c.id, c.cause }) end
        end
        local enter = data.statusApplied and places[data.statusApplied.id]
        if enter then STATUS_ENTER[module] = { enter[1], enter[2] } end
    end
    Log.Build = build
end

--- The asker an address belongs to: { id, what, entry }, or nil for an address no named asker
--- holds. `entry` marks one of the roll manager's own entry functions, whose caller is the asker.
local forgotten = {}

--- Leaves one named asker or cause out of the log's tables until the family `dice` is turned off
--- (the scope's end does it): its rolls are then written as any roll asked from a place the
--- registry does not name yet. Changes nothing in the game. Without an id: nothing is left out.
function Log.Forget(id)
    if id == nil then forgotten = {} return { ok = true, forgotten = {} } end
    forgotten[id] = true
    local out = {}
    for name in pairs(forgotten) do out[#out + 1] = name end
    table.sort(out)
    return { ok = true, forgotten = out }
end

function Log.Origin(module, rva)
    for _, a in ipairs(ASKERS[module] or {}) do
        if rva >= a[1] and rva < a[1] + a[2] and not forgotten[a[3]] then return { id = a[3], what = a[4], entry = a[5] } end
    end
    return nil
end

--- The askers known for each executable: { [module] = { { id, what, rva, size } } }.
function Log.Origins()
    local out = {}
    for module, list in pairs(ASKERS) do
        out[module] = {}
        for _, a in ipairs(list) do out[module][#out[module] + 1] = { id = a[3], what = a[4], rva = a[1], size = a[2] } end
    end
    return out
end

--- Whether this Script Extender build throws the roll event the log is written from.
function Log.Available()
    return GK.Verbs.Has("Events.Roll")
end

local function uuid(handle)
    local ok, id = pcall(function() return handle and Ext.Entity.HandleToUuid(handle) end)
    return ok and id or nil
end

local function advantage(e)
    if e.Advantage and e.Disadvantage then return "both" end
    return e.Advantage and "advantage" or e.Disadvantage and "disadvantage" or nil
end

-- The asker among the game's frames above the roll: the first frame a named asker holds that is
-- not one of the roll manager's own entry functions. The frames are there in developer mode.
local function asker(e)
    local module = tostring(e.Caller or ""):match("^(.-)%+0x%x+$")
    if not module then return nil, nil end
    local first
    for frame in tostring(e.CallerChain or ""):gmatch("0x(%x+)") do
        local rva = tonumber(frame, 16)
        local at = string.format("%s+0x%X", module, rva)
        first = first or at
        local origin = Log.Origin(module, rva)
        if origin and not origin.entry then return origin, at end
        if not origin then return nil, at end
    end
    return nil, first
end

-- What caused the roll: the frame above the asker that belongs to the system which decided a roll
-- was needed. A cause holds whatever kind of roll it asks for - a status's condition may ask a save
-- or a check, a spell's roll condition an attack or a save - so it says why, where the asker says
-- what. The places are the registry's, for the build it holds.

--- The causes known for each executable: { [module] = { { id, cause, rva, size } } }.
function Log.Causes()
    local out = {}
    for module, list in pairs(CAUSES) do
        out[module] = {}
        for _, c in ipairs(list) do out[module][#out[module] + 1] = { id = c[3], cause = c[4], rva = c[1], size = c[2] } end
    end
    return out
end

-- A roll the roll system makes from a request names its cause in the request, not on the stack:
-- the request's context (a concentration check, a dialogue's check, a script's) and who it is about.
local function requestOf(e)
    local ok, found = pcall(function()
        for _, entity in ipairs(Ext.Entity.GetAllEntitiesWithComponent("RequestedRoll")) do
            local r = entity.RequestedRoll
            if r.Roller == e.Roller and not r.Finished then
                return { context = tostring(r.RollContext), fromScript = r.FromScript or nil, subject = uuid(r.Subject),
                         dialog = (function() local d = tostring(r.DialogId) return d ~= "" and d:sub(1, 1) ~= "0" and d or nil end)() }
            end
        end
    end)
    return ok and found or nil
end

local function causeOf(e)
    local module = tostring(e.Caller or ""):match("^(.-)%+0x%x+$")
    local list = module and CAUSES[module]
    if not list then return nil end
    local frames = {}
    for frame in tostring(e.CallerChain or ""):gmatch("0x(%x+)") do frames[#frames + 1] = tonumber(frame, 16) end
    for _, rva in ipairs(frames) do
        for _, c in ipairs(list) do
            if rva >= c[1] and rva < c[1] + c[2] and not forgotten[c[3]] then
                local cause = { id = c[3], cause = c[4] }
                if c[4] == "status" then
                    local enter = STATUS_ENTER[module]
                    cause.cause = "status event"
                    for _, above in ipairs(frames) do
                        if enter and above >= enter[1] and above < enter[1] + enter[2] then cause.cause = "status applied" end
                    end
                elseif c[4] == "request" then
                    cause.request = requestOf(e)
                end
                return cause
            end
        end
    end
    return nil
end

local function write(e)
    if e.DiceCount == 0 and #e.Dice == 0 then return end   -- a formula of no dice draws nothing
    local origin, asked = asker(e)
    local cause = causeOf(e)
    local dice = {}
    for _, die in ipairs(e.Dice) do dice[#dice + 1] = die end
    local payload = {
        who = uuid(e.Roller), kind = tostring(e.RollType), stream = e.HasStream and tostring(e.Stream) or nil,
        streamId = e.HasStream and e.StreamId or nil, dieType = tostring(e.DiceType), count = e.DiceCount, bonus = e.Bonus,
        dice = dice, natural = e.Natural, discarded = e.Discarded, total = e.Total, critical = tostring(e.Critical),
        dc = e.DC, advantage = advantage(e), forced = e.Forced or nil,
        origin = origin and origin.id or "unknown", what = origin and origin.what or nil, caller = asked,
        cause = cause and cause.cause or "unknown", causeId = cause and cause.id or nil, request = cause and cause.request or nil,
    }
    -- What the roll is for, as the extender reads it from the condition or the effect the roll is
    -- thrown inside (a build without these fields leaves them out): the two parties, the spell,
    -- the status. Where no frame named the cause, the effect does: a status's or a spell's effect.
    pcall(function()
        payload.source, payload.target = uuid(e.Source), uuid(e.Target)
        local spell, status = tostring(e.Spell or ""), tostring(e.Status or "")
        payload.spell = spell ~= "" and spell or nil
        payload.status = status ~= "" and status or nil
        if payload.cause == "unknown" then
            payload.cause = payload.status and "status effect" or payload.spell and "spell effect" or "unknown"
        end
    end)
    -- a rig decided this roll a moment ago (the log hears the event last): the row says what the
    -- game goes on with, and what was thrown before the rig
    local rig = GK.Rolls and GK.Rolls.JustRigged
    if rig then
        GK.Rolls.JustRigged = nil
        payload.rigged = { id = rig.id, natural = rig.was.natural, total = rig.was.total }
        payload.total = rig.total
        -- the extender sets the critical by the game's rule once every listener has returned: a
        -- natural 1 fails, a natural 20 succeeds (a lowered threshold is not known here)
        if payload.dieType == "D20" then
            payload.critical = payload.natural == 1 and "Fail" or payload.natural == 20 and "Success" or "None"
        end
    end
    payload.text = string.format("%s rolled %s (natural %s) for %s", payload.who or "the game", tostring(payload.total),
        tostring(payload.natural), payload.what or ("an unknown asker " .. tostring(asked)))
    GK.EventLog.Append("dice_thrown", payload)
end

-- A roll changed after the throw - an interrupt set it, adjusted it or rerolled it - is a row of
-- its own: the row of the throw says what was thrown, this one what the game goes on with. From
-- the extender's RollAdjusted event, on a build that has it.
local function adjusted(e)
    local payload = {
        roll = tostring(e.RollUuid), dc = e.DC, natural = e.Natural, total = e.Total, critical = tostring(e.Critical),
        was = { natural = e.OldNatural, total = e.OldTotal, critical = tostring(e.OldCritical) },
        outcomeChanged = e.OutcomeChanged or nil,
    }
    payload.text = string.format("a roll of %s (natural %s) became %s (natural %s) after the throw", tostring(e.OldTotal),
        tostring(e.OldNatural), tostring(e.Total), tostring(e.Natural))
    GK.EventLog.Append("dice_adjusted", payload)
end

local subscribedAdjusted

local subscribed

GK.EventLog.Hooks = GK.EventLog.Hooks or {}
GK.EventLog.Hooks.dice = {
    on = function()
        if not Log.Available() then error("this Script Extender build has no roll event (Ext.Events.Roll): the dice family needs a build that has it") end
        if GK.Rolls then GK.Rolls.JustRigged = nil end
        -- the lowest priority: heard after every other listener, so the row is what they left
        subscribed = Ext.Events.Roll:Subscribe(function(e) pcall(write, e) end, { Priority = 1 })
        local ok, event = GK.Verbs.Has("Events.RollAdjusted"), Ext.Events.RollAdjusted
        if ok and event then subscribedAdjusted = event:Subscribe(function(e) pcall(adjusted, e) end, { Priority = 1 }) end
    end,
    off = function()
        if subscribed then pcall(function() Ext.Events.Roll:Unsubscribe(subscribed) end) end
        subscribed = nil
        if subscribedAdjusted then pcall(function() Ext.Events.RollAdjusted:Unsubscribe(subscribedAdjusted) end) end
        subscribedAdjusted = nil
        forgotten = {}
    end,
}
