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
Log.Class = { Available = "observation", Origin = "observation", Origins = "observation", Causes = "observation" }

-- The askers: the functions that call the roll manager, by their place in each executable of the
-- build the registry holds (registry/builds/<build>/<binary>). `what` is the roll asked for.
local ASKERS = {
    ["bg3_dx11.exe"] = {
        { 0x106CA40, 2954, "unplaced::RollAttackAgainstArmourClass", "weapon attack" },
        { 0x106DBA0, 2748, "unplaced::RollSpellAttackAgainstArmourClass", "spell attack" },
        { 0x106EC20, 2600, "unplaced::RollUnarmedAttackAgainstArmourClass", "unarmed attack" },
        { 0x10700C0, 2281, "unplaced::RollSavingThrow", "saving throw" },
        { 0x1075BA0, 1950, "unplaced::RollSkillCheck", "skill check" },
        { 0x10764A0, 1410, "eoc::stats::AbilityCheck", "ability check" },
        { 0x1070DE0, 4281, "unplaced::RollDamageDice", "damage" },
        { 0x1072B10, 1299, "unplaced::RollDodgeAttack", "dodge roll" },
        { 0xEF8580, 764, "eoc::StatusPrototype::GetHealAmount", "heal" },
        { 0x1187D40, 446, "unplaced::RollExpressionAgainstDifficulty", "roll of a stats expression" },
        { 0x1185A70, 540, "unplaced::RollExpressionDice", "dice of a stats expression" },
        { 0x11934E0, 534, "unplaced::RollDamageExpressionDice", "dice of a damage expression" },
        { 0x1193940, 145, "unplaced::RollExpressionPlainDice", "dice of a stats expression" },
        { 0x32019A0, 71, "esv::combat::SystemHelper::RollDie", "initiative" },
        { 0x339C2C0, 822, "unplaced::RollSpellRecharge", "spell recharge" },
        { 0x2E936C0, 895, "unplaced::PickRandomCast", "random cast pick" },
        { 0x2E92110, 1258, "unplaced::RollRandomCast", "random cast check" },
        { 0x10F08A0, 559, "unplaced::ScriptRoll", "script roll" },
        { 0x41C4D00, 2248, "unplaced::EstimateSpellDamage", "damage estimate" },
        -- the roll manager's own entry functions: the asker is a frame further up
        { 0x105B5E0, 182, "eoc::roll::stream::RollManager::RollDice", "plain dice", true },
        { 0x105B6A0, 112, "eoc::roll::stream::RollManager::RollDiceAgainstDC", "dice against a difficulty", true },
        { 0x105BA00, 257, "eoc::roll::stream::RollManager::RollDiceWithBoosts", "dice with boosts", true },
    },
}

--- The asker an address belongs to: { id, what, entry }, or nil for an address no named asker
--- holds. `entry` marks one of the roll manager's own entry functions, whose caller is the asker.
function Log.Origin(module, rva)
    for _, a in ipairs(ASKERS[module] or {}) do
        if rva >= a[1] and rva < a[1] + a[2] then return { id = a[3], what = a[4], entry = a[5] } end
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
    local ok, event = pcall(function() return Ext.Events.Roll end)
    return ok and event ~= nil
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
local CAUSES = {
    ["bg3_dx11.exe"] = {
        { 0x2FFC4E0, 618, "unplaced::EvaluateStatusCondition", "status" },
        { 0xFCDD50, 1795, "unplaced::EvaluateSpellRollCondition", "spell" },
        { 0x3295250, 793, "unplaced::ThrowActiveRoll", "dialogue throw" },
        { 0x3297ED0, 842, "esv::RollSystem::ProcessCreateEvents", "request" },
        { 0x36EBCB0, 3411, "unplaced::DealDamageFunctorAtPosition", "damage at a position" },
        { 0x32019A0, 71, "esv::combat::SystemHelper::RollDie", "initiative" },
    },
}
-- a status's condition asked as the status is applied, or on a later event of it (a tick, a turn)
local STATUS_ENTER = { ["bg3_dx11.exe"] = { 0x2FF9FC0, 639 } }

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
            if rva >= c[1] and rva < c[1] + c[2] then
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
    payload.text = string.format("%s rolled %s (natural %s) for %s", payload.who or "the game", tostring(payload.total),
        tostring(payload.natural), payload.what or ("an unknown asker " .. tostring(asked)))
    GK.EventLog.Append("dice_thrown", payload)
end

local subscribed

GK.EventLog.Hooks = GK.EventLog.Hooks or {}
GK.EventLog.Hooks.dice = {
    on = function()
        if not Log.Available() then error("this Script Extender build has no roll event (Ext.Events.Roll): the dice family needs a build that has it") end
        subscribed = Ext.Events.Roll:Subscribe(function(e) pcall(write, e) end)
    end,
    off = function()
        if subscribed then pcall(function() Ext.Events.Roll:Unsubscribe(subscribed) end) end
        subscribed = nil
    end,
}
