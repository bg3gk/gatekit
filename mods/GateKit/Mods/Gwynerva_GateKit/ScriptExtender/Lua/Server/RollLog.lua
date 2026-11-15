-- The roll log: one `dice_thrown` row for every roll the game's roll manager throws, whoever asked
-- for it - a check, an attack, a save, damage, a heal, initiative, a die of a stats expression.
-- The rows come from the roll events of GateKit's Script Extender (Ext.Events.RollAgainstDC,
-- RollBoosted, RollDice; developer mode), which carry the return address of the code that asked.
-- That address is looked up among the askers the registry names: a known asker gives the row its
-- `origin`, any other address gives `origin = "unknown"` with the address, never a dropped row.
-- The family `dice` is off until GK.EventLog.On("dice"): a fight with the AI thinking throws many.

GK.RollLog = GK.RollLog or {}
local Log = GK.RollLog

--- Each verb's class, for the fair/cheat/observation split.
Log.Class = { Available = "observation", Origin = "observation", Origins = "observation" }

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

--- Whether this Script Extender build throws the roll events the log is written from.
function Log.Available()
    local ok, event = pcall(function() return Ext.Events.RollBoosted end)
    return ok and event ~= nil
end

local function uuid(handle)
    local ok, id = pcall(function() return handle and Ext.Entity.HandleToUuid(handle) end)
    return ok and id or nil
end

local dice = {}     -- the dice of a roll still open, by the sequence of its outermost call

local function write(e, top, extra)
    if e.Count == 0 and not e.HasResult then dice[top] = nil return end   -- a formula of no dice draws nothing
    local module, rva = tostring(e.Caller):match("^(.-)%+0x(%x+)$")
    rva = rva and tonumber(rva, 16)
    local origin = module and Log.Origin(module, rva)
    local asked = e.Caller
    if origin and origin.entry then
        -- through one of the roll manager's entry functions: the frames above name the asker
        local frames = {}
        for frame in tostring(e.CallerChain or ""):gmatch("0x(%x+)") do frames[#frames + 1] = tonumber(frame, 16) end
        for i = 2, #frames do
            local above = Log.Origin(module, frames[i])
            if not (above and above.entry) then
                origin, asked = above, string.format("%s+0x%X", module, frames[i])
                break
            end
        end
    end
    local payload = {
        who = extra.who, kind = extra.kind, stream = e.HasStream and tostring(e.StreamType) or nil, streamId = e.HasStream and e.StreamId or nil,
        dieType = tostring(e.DieType), count = e.Count, bonus = e.Bonus, dice = dice[top],
        natural = e.HasResult and e.Natural or nil, discarded = e.HasResult and e.Discarded or nil, total = e.Total,
        critical = e.HasResult and tostring(e.Critical) or nil, dc = extra.dc, advantage = extra.advantage, forced = extra.forced,
        origin = origin and origin.id or "unknown", what = origin and origin.what or nil, caller = asked,
    }
    dice[top] = nil
    payload.text = string.format("%s rolled %s%s for %s: %s", payload.who or "the game", tostring(payload.total),
        payload.natural and (" (natural " .. tostring(payload.natural) .. ")") or "", payload.what or "an unknown asker", tostring(asked))
    GK.EventLog.Append("dice_thrown", payload)
end

local function advantage(e)
    if e.Advantage and e.Disadvantage then return "both" end
    return e.Advantage and "advantage" or e.Disadvantage and "disadvantage" or nil
end

local handlers = {
    RollDice = function(e)
        local top = e.AgainstDCSequence ~= 0 and e.AgainstDCSequence or e.BoostedSequence ~= 0 and e.BoostedSequence or e.Sequence
        local list = {}
        for _, d in ipairs(e.Dice or {}) do list[#list + 1] = d end
        dice[top] = list
        if top == e.Sequence then write(e, top, { dc = e.DC ~= -1 and e.DC or nil }) end
    end,
    RollBoosted = function(e)
        if e.AgainstDCSequence ~= 0 then return end
        write(e, e.Sequence, { who = uuid(e.Roller), kind = tostring(e.RollType), dc = e.DC ~= -1 and e.DC or nil, advantage = advantage(e) })
    end,
    RollAgainstDC = function(e)
        write(e, e.Sequence, { who = uuid(e.Roller), kind = tostring(e.RollType), dc = e.DC, advantage = advantage(e),
                               forced = (not e.ViaBoosted and e.HasStream) or nil })
    end,
}

local subscribed = {}

GK.EventLog.Hooks = GK.EventLog.Hooks or {}
GK.EventLog.Hooks.dice = {
    on = function()
        if not Log.Available() then error("this Script Extender build has no roll events: the dice family needs GateKit's build with them, in developer mode") end
        for name, handler in pairs(handlers) do
            subscribed[name] = Ext.Events[name]:Subscribe(function(e) pcall(handler, e) end)
        end
    end,
    off = function()
        for name, id in pairs(subscribed) do pcall(function() Ext.Events[name]:Unsubscribe(id) end) end
        subscribed, dice = {}, {}
    end,
}
