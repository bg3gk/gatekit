-- GK.Speed: game speed by situation - enemy turns fast, your own turn and dialogues at the game's
-- pace, exploration as you like. Server side (the host decides; in single player server and
-- client share one process and one clock setting).
--
-- The engine has its own time scale: ls::GlobalSwitches +0x40, a float the server loop, the
-- client loop and the UI multiply their frame delta by (bg3.exe 4.1.1.7631656: +0x5E5D85,
-- +0x3F7A2CA, +0x121129E). Script Extender exposes it as GlobalSwitches.DeltaTimeScale in the
-- GateKit build and as AnimationBlueprintSpeedScale_M in the stock one - the same field, so this
-- file works with any Script Extender, no native code needed. Measured: at 4 a two-turn status
-- ends after 4.7 s instead of 18.2 s; a six-turn fight of ended turns takes 1.3 s instead of 4.5 s.
-- Sound is mixed in real time and does not follow: voiced dialogue at a high scale gets cut, so
-- dialogues default to 1.
--
--   GK.Speed.Enable(profile)   starts following the situation; profile (optional) overrides
--                              the scales of GK.Speed.Profile: { exploration, ownTurn, allyTurn,
--                              enemyTurn, dialogue, combat }
--   GK.Speed.Disable()         back to the game's pace (1) and stops following
--   GK.Speed.Set(scale)        a fixed scale (stops following); GK.Speed.Get() the current one;
--                              Set("working") is GK.Speed.Working, the project's working speed
--   GK.Speed.Clock()           the game's own clock: { ticks, time, delta, real, scale }
--   GK.Speed.Boost(scale, seconds)  a temporary scale over whatever the situation says
--   GK.Speed.Rule(name, priority, test, scale)  a situation of your own: test(state) -> true
--                              when it applies; the highest priority that applies wins
--   GK.Speed.State()           { situation, scale, enabled, active = character whose turn it is }
--
-- Limits: a load or a Lua reset turns it off (the game's pace again) - Enable it after a load;
-- the scale is the host's; in multiplayer the other players' clients keep their own
-- (their animations would run at 1 while the server runs faster). The switch itself has no cap:
-- a direct write is honoured as written (measured exactly up to 32), so any positive scale is
-- accepted; at 16 and above the game's own rules are known to break (BREAKS), and the answer
-- says so. The scale is not saved with the game; after a load it is 1 until the next situation
-- change.

GK.Speed = GK.Speed or {}
local Speed = GK.Speed

--- Each verb's class: the time scale is set by script (cheat); reading it is observation.
Speed.Class = {
    Enable = "cheat", Disable = "cheat", Set = "cheat", Boost = "cheat", Rule = "cheat",
    Get = "observation", State = "observation", Clock = "observation",
}

Speed.Profile = Speed.Profile or {
    exploration = 1,   -- walking around
    dialogue = 1,      -- voiced lines are real time: faster would cut them
    ownTurn = 1,       -- a party member's turn: the player thinks
    allyTurn = 2,      -- an allied NPC's turn
    enemyTurn = 3,     -- an enemy's turn: watching
    combat = 1,        -- in combat, between turns
}

--- The project's working speed: the scale tests run at besides 1 (GK.Speed.Set("working")).
--- Up to 12 every verdict of the speed ramp equalled speed 1.
Speed.Working = 8

-- From this scale on the game is known to break: the party-defeated check fires on a downed
-- member being raised (16), a turn starts before the previous hit lands (24).
local BREAKS = 16

local state = Speed.Status or { enabled = false, situation = "exploration", scale = 1, dialogs = {}, rules = {} }
Speed.Status = state

-- A scale the switch takes: a positive finite number, or "working". Anything else is refused.
local function scaleOf(value, verb)
    if value == "working" then return Speed.Working end
    local n = tonumber(value)
    if not n or n ~= n or n <= 0 or n == math.huge then
        error("GK.Speed." .. verb .. ": a scale is a positive number or \"working\", got " .. tostring(value), 3)
    end
    return n
end

local function clamp(value)
    value = tonumber(value) or 1
    if value ~= value or value <= 0 or value == math.huge then return 1 end
    return value
end

local function warning(scale)
    if scale >= BREAKS then
        return ("at %s the game is known to break: from 16 a downed member being raised fires the party-defeated check, "
            .. "from 24 a turn starts before the previous hit lands; verdicts are only known to hold up to 12"):format(tostring(scale))
    end
end

-- The engine's switch, under either name.
local function write(scale)
    local switches = Ext.Utils.GetGlobalSwitches()
    local ok = pcall(function() switches.DeltaTimeScale = scale end)
    if not ok then ok = pcall(function() switches.AnimationBlueprintSpeedScale_M = scale end) end
    return ok
end

local function read()
    local switches = Ext.Utils.GetGlobalSwitches()
    local ok, value = pcall(function() return switches.DeltaTimeScale end)
    if not ok or value == nil then ok, value = pcall(function() return switches.AnimationBlueprintSpeedScale_M end) end
    return ok and value or nil
end

function Speed.Get() return read() end

local function apply(scale, situation)
    scale = clamp(scale)
    state.situation = situation or state.situation
    if state.scale ~= scale or read() ~= scale then
        state.scale = scale
        write(scale)
    end
end

local function isParty(character)
    if character == nil then return false end
    local ok, member = pcall(Osi.IsPartyMember, character, 1)
    return ok and member == 1
end

local function partyInCombat()
    local ok, players = pcall(function() return Osi.DB_Players:Get(nil) end)
    if not ok or not players then return false end
    for _, row in ipairs(players) do
        local inCombat = pcall(Osi.IsInCombat, row[1]) and Osi.IsInCombat(row[1]) == 1
        if inCombat then return true end
    end
    return false
end

-- A party member in a dialogue: the events may miss one (a dialogue started by a script with
-- no player listed as involved), the query does not.
local function partyInDialog()
    local ok, players = pcall(function() return Osi.DB_Players:Get(nil) end)
    if not ok or not players then return false end
    for _, row in ipairs(players) do
        if GK.InDialog(row[1]) then return true end
    end
    return false
end

local function enemyOfParty(character)
    local ok, players = pcall(function() return Osi.DB_Players:Get(nil) end)
    if not ok or not players or not players[1] then return false end
    local okEnemy, enemy = pcall(Osi.IsEnemy, character, players[1][1])
    return okEnemy and enemy == 1
end

-- The situation now, from what the events told: the highest-priority rule that applies.
local function situation()
    local s = { active = state.active, dialogs = 0 }
    for _ in pairs(state.dialogs) do s.dialogs = s.dialogs + 1 end
    if s.dialogs == 0 and partyInDialog() then s.dialogs = 1 end
    s.inCombat = partyInCombat()
    -- rules of the caller first (by priority), then the built-in order
    local best
    for _, rule in pairs(state.rules) do
        local ok, applies = pcall(rule.test, s)
        if ok and applies and (not best or rule.priority > best.priority) then best = rule end
    end
    if best then return best.name, best.scale end
    if s.dialogs > 0 then return "dialogue" end
    if s.inCombat then
        if state.active == nil then return "combat" end
        if isParty(state.active) then return "ownTurn" end
        if enemyOfParty(state.active) then return "enemyTurn" end
        return "allyTurn"
    end
    return "exploration"
end

local function update()
    if not state.enabled then return end
    if state.boostUntil and GK.Ticks() < state.boostUntil then
        apply(state.boost, "boost")
        return
    end
    state.boostUntil = nil
    local name, scale = situation()
    apply(scale or Speed.Profile[name] or 1, name)
end
Speed.Update = update

-- Events: whose turn it is, which dialogues with a player run. A look every fifteen ticks from
-- the bridge's tick, while the scale follows the situation, catches what no event announces
-- (combat ended by a script, a load). GAP: a read on the tick for want of those events.
local function listen()
    if state.listening then return end
    state.listening = true
    Ext.Osiris.RegisterListener("TurnStarted", 1, "after", function(character)
        state.active = character
        update()
    end)
    Ext.Osiris.RegisterListener("TurnEnded", 1, "after", function(character)
        if state.active == character then state.active = nil end
        update()
    end)
    Ext.Osiris.RegisterListener("CombatEnded", 1, "after", function()
        state.active = nil
        update()
    end)
    Ext.Osiris.RegisterListener("DialogStarted", 2, "after", function(dialog, instance)
        local ok, players = pcall(Osi.DialogGetNumberOfInvolvedPlayers, instance)
        if not ok or (players or 0) > 0 then state.dialogs[instance] = dialog end
        update()
    end)
    Ext.Osiris.RegisterListener("DialogEnded", 2, "after", function(_, instance)
        state.dialogs[instance] = nil
        update()
    end)
    Ext.Events.GameStateChanged:Subscribe(function(e)
        -- loading, saving, the main menu: the game's pace, and forget what was running
        if tostring(e.ToState) ~= "Running" then
            state.dialogs, state.active = {}, nil
            if state.enabled then apply(1, "paused") end
        end
    end)
end

local LOOK_TICKS = 15
local function follow()
    if not (GK.Attention and GK.Attention.Pollers) then return end
    local due = GK.Ticks() + LOOK_TICKS
    GK.Attention.Pollers.speed = function()
        if not state.enabled then GK.Attention.Pollers.speed = nil return end
        if GK.Ticks() < due then return end
        due = GK.Ticks() + LOOK_TICKS
        update()
    end
end

function Speed.Enable(profile)
    for key, value in pairs(profile or {}) do Speed.Profile[key] = value end
    state.enabled = true
    listen()
    follow()
    update()
    return Speed.State()
end

function Speed.Disable()
    state.enabled = false
    apply(1, "off")
    return Speed.State()
end

function Speed.Set(scale)
    scale = scaleOf(scale, "Set")
    state.enabled = false
    apply(scale, "fixed")
    local answer = Speed.State()
    answer.warning = warning(scale)
    return answer
end

function Speed.Boost(scale, seconds)
    state.boost = scaleOf(scale, "Boost")
    -- its length is of a running game, in the server's ticks
    state.boostUntil = GK.Ticks() + GK.LimitTicks((tonumber(seconds) or 5) * 1000)
    if not state.enabled then
        -- a boost works while not following, too: it ends back at 1
        state.enabled = true
        state.disableAfterBoost = true
        listen()
    end
    follow()
    update()
    return Speed.State()
end

function Speed.Rule(name, priority, test, scale)
    if test == nil then
        state.rules[name] = nil
    else
        state.rules[name] = { name = name, priority = priority or 0, test = test, scale = scale }
    end
    update()
    return true
end

function Speed.State()
    local dialogs = 0
    for _ in pairs(state.dialogs) do dialogs = dialogs + 1 end
    return { enabled = state.enabled, situation = state.situation, scale = state.scale, engine = read(),
             active = state.active, dialogs = dialogs, profile = Speed.Profile }
end

-- The game's own clock as the last server tick gave it (Ext.Events.Tick's Time).
Speed.Time = Speed.Time or {}
if not Speed.ClockSubscribed then
    Speed.ClockSubscribed = true
    Ext.Events.Tick:Subscribe(function(e)
        local t = Speed.Time
        pcall(function()
            t.ticks, t.time, t.delta = e.Time.Ticks, e.Time.Time, e.Time.DeltaTime
            t.real = Ext.Utils.MonotonicTime()
        end)
    end)
end

--- The game's clock: { ticks = the server's own tick count, time = the game time in seconds,
--- delta = the game seconds of the last tick (the scale makes a tick longer, not more frequent),
--- real = Ext.Utils.MonotonicTime() of that tick in ms, scale }. Two readings give ticks, game
--- time and real time between them. Observation.
function Speed.Clock()
    local t = Speed.Time
    return { ticks = t.ticks, time = t.time, delta = t.delta, real = t.real, scale = read() }
end

-- The engine's scale outlives a load and a Lua reset, the state of this file does not: a game
-- loaded at 3 would stay at 3 with nothing to bring it back (seen 2026-11-13). So when this file
-- loads and nothing follows the situation, the game's own pace is put back.
if not state.enabled then write(1) end

return Speed
