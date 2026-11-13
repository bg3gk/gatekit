-- GK.GameLog: what happened, as a player would have noticed it - the combat log and the event
-- toasts in one feed: damage, healing, statuses gained and lost (curses, poison, blessings),
-- downs and deaths, spells cast, fights and turns, items and gold gained or lost, journal
-- updates, dice results, companions' approval, level-ups. Server side.
--
--   GK.GameLog.Since(n, options)  { last, entries = { { n, t, kind, text, ... } } } after entry n;
--                                 options.all = also what the party did not perceive
--   GK.GameLog.Kinds              the kinds: damage, heal, status, unstatus, down, death, revive,
--                                 cast, fight, turn, item, lost, gold, quest, roll, approval, level
--
-- Fair: an entry is kept when a party member is involved, or the party perceives the one it
-- happened to (GK.See.PartyPerceives at that moment). Statuses are those a player sees (shown on
-- portraits or in Examine). Texts are English; names are the game's own (its language).

GK.GameLog = GK.GameLog or {}
local Log = GK.GameLog
Log.Entries = Log.Entries or {}
Log.Next = Log.Next or 1
Log.Keep = 500
Log.Hp = Log.Hp or {}   -- last hit points seen per entity, to tell healing from damage

local function try(fn, ...)
    local ok, a, b = pcall(fn, ...)
    if ok then return a, b end
    return nil
end

local function bare(id)
    return type(id) == "string" and (id:match("(%x+%-%x+%-%x+%-%x+%-%x+)$") or id) or id
end

local function name(id)
    id = bare(id)
    return id and (GK.See.Name(id) or id:sub(1, 8)) or "?"
end

local function involvesParty(...)
    for _, id in ipairs({ ... }) do
        if id and GK.See.IsMember(bare(id)) then return true end
    end
    return false
end

local function perceived(...)
    if involvesParty(...) then return true end
    for _, id in ipairs({ ... }) do
        id = bare(id)
        if id and id ~= "" and id ~= "00000000-0000-0000-0000-000000000000" and try(GK.See.PartyPerceives, id) then
            return true
        end
    end
    return false
end

local function add(kind, text, data, ...)
    local fair = perceived(...)
    local n = Log.Next
    Log.Next = n + 1
    local entry = { n = n, t = math.floor(Ext.Utils.MonotonicTime() / 100) / 10, kind = kind, text = text, fair = fair }
    for k, v in pairs(data or {}) do entry[k] = v end
    Log.Entries[n] = entry
    Log.Entries[n - Log.Keep] = nil
end

local function statusName(id)
    local stat = try(Ext.Stats.Get, id)
    local display = stat and try(function() return stat.DisplayName end)
    local text = display and display ~= "" and try(Ext.Loca.GetTranslatedString, display)
    return text ~= "" and text or nil
end

local function statusShown(id)
    local stat = try(Ext.Stats.Get, id)
    if not stat then return false end
    for _, f in ipairs(try(function() return stat.StatusPropertyFlags end) or {}) do
        if f == "DisablePortraitIndicator" then return false end
    end
    return statusName(id) ~= nil
end

local function hpOf(id)
    local e = try(Ext.Entity.Get, bare(id))
    return e and try(function() return e.Health.Hp end), e and try(function() return e.Health.MaxHp end)
end

local function spellName(id)
    local stat = try(Ext.Stats.Get, id)
    local display = stat and try(function() return stat.DisplayName end)
    local text = display and try(Ext.Loca.GetTranslatedString, display)
    return text ~= "" and text or id
end

-- Quest titles and step texts as the journal shows them, from the game's quest prototypes
-- (Mods/GustavDev/Story/Journal/quest_prototypes.lsx: QuestID + QuestTitle handle, and per
-- QuestStep its ID + Description handle). Read once, at the first quest event.
Log.QuestText = Log.QuestText or nil
local function questTexts()
    if Log.QuestText then return Log.QuestText end
    local out = { titles = {}, steps = {} }
    Log.QuestText = out
    local text
    for _, path in ipairs({ "Mods/GustavDev/Story/Journal/quest_prototypes.lsx", "Mods/Gustav/Story/Journal/quest_prototypes.lsx" }) do
        text = try(Ext.IO.LoadFile, path, "data")
        if text then break end
    end
    if not text then return out end
    local pos = 1
    while true do
        local s1, e1, quest = text:find('id="QuestID" type="FixedString" value="([^"]*)"', pos)
        if not s1 then break end
        local nextQuest = text:find('id="QuestID"', e1 + 1) or #text
        local title = text:sub(e1, math.min(e1 + 800, nextQuest)):match('id="QuestTitle" type="TranslatedString" handle="([^"]+)"')
        if title then out.titles[quest] = title end
        -- the steps of this quest lie after its attributes, before the next quest
        local block = text:sub(e1, nextQuest)
        for step in block:gmatch('<node id="QuestStep">(.-)</node>') do
            local desc = step:match('id="Description" type="TranslatedString" handle="([^"]+)"')
            local id = step:match('id="ID" type="FixedString" value="([^"]*)"')
            if id and desc then out.steps[quest .. "/" .. id] = desc end
        end
        pos = e1 + 1
    end
    return out
end

local function questTitle(quest)
    local h = questTexts().titles[quest]
    local t = h and try(Ext.Loca.GetTranslatedString, h)
    return (t and t ~= "") and t or tostring(quest)
end

local function stepText(quest, step)
    local h = questTexts().steps[tostring(quest) .. "/" .. tostring(step)]
    local t = h and try(Ext.Loca.GetTranslatedString, h)
    return (t and t ~= "") and t or nil
end
Log.QuestTitle = questTitle

Log.On = {
    AttackedBy = function(defender, owner, attacker, damageType, amount, cause)
        if (amount or 0) <= 0 then return end
        local hp, max = hpOf(defender)
        Log.Hp[bare(defender)] = hp
        add("damage", string.format("%s takes %d %s damage from %s%s", name(defender), amount, tostring(damageType):lower(),
            name(attacker ~= "" and attacker or owner), hp and string.format(" (%d/%d hp)", hp, max or 0) or ""),
            { who = bare(defender), by = bare(attacker), amount = amount, damage = damageType, cause = cause, hp = hp },
            defender, attacker, owner)
    end,
    HitpointsChanged = function(entity)
        local id = bare(entity)
        local hp, max = hpOf(id)
        local before = Log.Hp[id]
        Log.Hp[id] = hp
        if before and hp and hp > before then
            add("heal", string.format("%s regains %d hp (%d/%d)", name(id), hp - before, hp, max or 0),
                { who = id, amount = hp - before, hp = hp }, id)
        end
    end,
    StatusApplied = function(object, status, causee)
        if not statusShown(status) then return end
        local by = causee and causee ~= "" and causee ~= "00000000-0000-0000-0000-000000000000" and name(causee) or nil
        add("status", string.format("%s is now %s%s", name(object), statusName(status), by and (" (from " .. by .. ")") or ""),
            { who = bare(object), status = status, statusName = statusName(status), by = by and bare(causee) or nil }, object)
    end,
    StatusRemoved = function(object, status)
        if not statusShown(status) then return end
        add("unstatus", string.format("%s is no longer %s", name(object), statusName(status)),
            { who = bare(object), status = status, statusName = statusName(status) }, object)
    end,
    DownedChanged = function(character, downed)
        if downed == 1 then
            add("down", name(character) .. " is down (dying)", { who = bare(character) }, character)
        else
            add("revive", name(character) .. " is back on their feet", { who = bare(character) }, character)
        end
    end,
    Died = function(character)
        add("death", name(character) .. " dies", { who = bare(character) }, character)
    end,
    Resurrected = function(character)
        add("revive", name(character) .. " is raised from the dead", { who = bare(character) }, character)
    end,
    CastedSpell = function(caster, spell)
        add("cast", string.format("%s uses %s", name(caster), spellName(spell)), { who = bare(caster), spell = spell }, caster)
    end,
    CombatStarted = function()
        add("fight", "a fight begins", {}, Osi.GetHostCharacter())
    end,
    CombatEnded = function()
        add("fight", "the fight is over", {}, Osi.GetHostCharacter())
    end,
    TurnStarted = function(object)
        if try(Osi.IsCharacter, object) ~= 1 then return end   -- level objects in the turn order
        add("turn", name(object) .. "'s turn", { who = bare(object) }, object)
    end,
    AddedTo = function(object, holder, addType)
        if not GK.See.IsMember(bare(holder)) then return end
        add("item", string.format("%s receives %s", name(holder), name(object)), { who = bare(holder), item = bare(object), how = addType }, holder)
    end,
    RemovedFrom = function(object, holder)
        if not GK.See.IsMember(bare(holder)) then return end
        add("lost", string.format("%s no longer has %s", name(holder), name(object)), { who = bare(holder), item = bare(object) }, holder)
    end,
    GoldChanged = function(holder, amount)
        if not GK.See.IsMember(bare(holder)) or amount == 0 then return end
        add("gold", string.format("%s %s %d gold", name(holder), amount > 0 and "gains" or "loses", math.abs(amount)),
            { who = bare(holder), amount = amount }, holder)
    end,
    QuestAccepted = function(character, quest)
        Log.SeenQuests = Log.SeenQuests or {}
        Log.SeenQuests[quest] = true
        add("quest", "new quest in the journal: " .. questTitle(quest), { quest = quest, title = questTitle(quest) }, character)
    end,
    QuestUpdateUnlocked = function(character, quest, state)
        local step = stepText(quest, state)
        Log.SeenQuests = Log.SeenQuests or {}
        local first = not Log.SeenQuests[quest]
        Log.SeenQuests[quest] = true
        add("quest", (first and "new quest in the journal: " or "journal updated: ") .. questTitle(quest) .. (step and (" - " .. step) or ""),
            { quest = quest, state = state, title = questTitle(quest), text = step }, character)
    end,
    -- many quests begin with an update of a sub-quest, not with QuestAccepted (seen: the captive in
    -- the pod was never "accepted"); announce the first update of each quest as a new quest
    SubQuestUpdateUnlocked = function(character, quest, state)
        local step = stepText(quest, state)
        Log.SeenQuests = Log.SeenQuests or {}
        local first = not Log.SeenQuests[quest]
        Log.SeenQuests[quest] = true
        add("quest", (first and "new quest in the journal: " or "journal updated: ") .. questTitle(quest) .. (step and (" - " .. step) or ""),
            { quest = quest, state = state, title = questTitle(quest), text = step }, character)
    end,
    QuestClosed = function(quest)
        add("quest", "quest closed: " .. questTitle(quest), { quest = quest, title = questTitle(quest) }, Osi.GetHostCharacter())
    end,
    RollResult = function(_event, roller, _subject, resultType, isActive, criticality)
        -- a passive roll's subject is not named: the game does not show what was noticed
        local verdict = resultType == 1 and "succeeds" or "fails"
        -- criticality arrives as a number (0 = none) or a name; only a real critical is worth saying.
        -- The event carries no dice: an active roll's numbers are in `roll` (the roll window).
        local crit = tostring(criticality)
        local critText = (crit == "1" or crit:find("Success")) and " (natural 20)"
            or (crit == "2" or crit:find("Fail")) and " (natural 1)" or ""
        add("roll", string.format("%s's %s check %s%s", name(roller), isActive == 1 and "active" or "passive", verdict, critText),
            { who = bare(roller), success = resultType == 1, active = isActive == 1 }, roller)
    end,
    ApprovalRatingChanged = function(owner, rated, value)
        if not GK.See.IsMember(bare(owner)) then return end
        add("approval", string.format("%s's approval of %s is now %d", name(owner), name(rated), value),
            { who = bare(owner), of = bare(rated), value = value }, owner)
    end,
    LeveledUp = function(character)
        add("level", name(character) .. " reached a new level", { who = bare(character) }, character)
    end,
    Unlocked = function(item, character)
        add("item", string.format("%s unlocks %s", name(character), name(item)), { who = bare(character), item = bare(item) }, character, item)
    end,
}

local ARITY = { AttackedBy = 7, HitpointsChanged = 2, StatusApplied = 4, StatusRemoved = 4, DownedChanged = 2, Died = 1,
                Resurrected = 1, CastedSpell = 5, CombatStarted = 1, CombatEnded = 1, TurnStarted = 1, AddedTo = 3,
                RemovedFrom = 2, GoldChanged = 2, QuestAccepted = 2, QuestUpdateUnlocked = 3, SubQuestUpdateUnlocked = 3, QuestClosed = 1,
                RollResult = 6, ApprovalRatingChanged = 3, LeveledUp = 1, Unlocked = 3 }
Log.Registered = Log.Registered or {}
for event, arity in pairs(ARITY) do
    if not Log.Registered[event] then
        Log.Registered[event] = pcall(Ext.Osiris.RegisterListener, event, arity, "after", function(...)
            local handler = GK.GameLog.On[event]
            if handler then pcall(handler, ...) end
        end)
    end
end
Log.Kinds = { "damage", "heal", "status", "unstatus", "down", "death", "revive", "cast", "fight", "turn", "item", "lost",
              "gold", "quest", "roll", "approval", "level" }

function Log.Since(n, options)
    options = options or {}
    n = n or 0
    local out = {}
    for i = math.max(n + 1, Log.Next - Log.Keep), Log.Next - 1 do
        local e = Log.Entries[i]
        if e and (e.fair or options.all) and (not options.kinds or options.kinds[e.kind]) then out[#out + 1] = e end
    end
    return { last = Log.Next - 1, entries = out }
end

return Log
