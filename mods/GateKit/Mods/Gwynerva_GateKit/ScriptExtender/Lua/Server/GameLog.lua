-- GK.GameLog: what happened, as a player would have noticed it - the combat log and the event
-- toasts in one feed: damage, healing, statuses gained and lost (curses, poison, blessings),
-- downs and deaths, spells cast, fights and turns, items and gold gained or lost, journal
-- updates, dice results, companions' approval, level-ups. Server side. The entries are rows of
-- the event log, family `game` (kinds game_<kind>); this file writes them and reads them back.
--
--   GK.GameLog.Since(n, options)  { last, entries = { { n, t, kind, text, ... } } } after entry n
--                                 (n is the row's seq); options.all = also what the party did not
--                                 perceive; options.kinds = { kind = true }
--   GK.GameLog.Kinds              the kinds: damage, heal, status, unstatus, down, death, revive,
--                                 cast, fight, turn, item, lost, gold, quest, roll, approval, level
--
-- Fair: an entry is a `player` row when a party member is involved, or the party perceives the
-- one it happened to (GK.See.PartyPerceives at that moment); else the row is `omniscient`.
-- Statuses are those a player sees (shown on portraits or in Examine). Texts are English; names
-- are the game's own (its language).

GK.GameLog = GK.GameLog or {}
local Log = GK.GameLog
Log.Hp = Log.Hp or {}   -- last hit points seen per entity, to tell healing from damage
Log.Saved = Log.Saved or {}   -- the entries a loaded save carried (GK.SaveState), before this boot's rows

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
    local payload = { text = text }
    for k, v in pairs(data or {}) do payload[k] = v end
    GK.EventLog.Append("game_" .. kind, payload, { knows = not fair and "omniscient" or nil })
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

local ZERO = "00000000-0000-0000-0000-000000000000"

local function ticks() return GK.EventLog and GK.EventLog.Ticks or 0 end

local function uuidOf(entity)
    return entity and try(function() return entity.Uuid.EntityUuid end) or nil
end

local function guidText(g)
    local s = g ~= nil and tostring(g) or nil
    return s and s ~= "" and s ~= ZERO and s or nil
end

local function named(s) return type(s) == "string" and s ~= "" and s or nil end

-- A status's own duration where the game gives it: the status's LifeTime on its holder's status
-- manager (esv::Status), in seconds - the same seconds as Osi.ApplyStatus's duration; a turn is 6
-- of them. Nil for a status with no end (LifeTime -1) and for one no longer held.
local function statusTurns(object, status)
    local e = try(Ext.Entity.Get, bare(object))
    local manager = e and (try(function() return e.ServerCharacter.StatusManager end) or try(function() return e.ServerItem.StatusManager end))
    for _, st in ipairs(manager and try(function() return manager.Statuses end) or {}) do
        if try(function() return st.StatusId end) == status then
            local life = try(function() return st.LifeTime end)
            if type(life) == "number" and life > 0 then return life / 6 end
            return nil
        end
    end
    return nil
end

-- What a hit's own data (HitDesc.Damage, eoc's StatsDamage, read through Ext.Types.Serialize)
-- says of its damage: per damage type what was rolled and what was dealt, the resistances that
-- applied, the multiplier, the rolls and the modifiers the game lists, and what they leave
-- unexplained. The fields are those of the game; one it does not fill is left out.
--   types      { [type] = { rolled = TotalDamagePerType, dealt = FinalDamagePerType } }
--   resisted   { { type, change, flags } }: Damage.Resistances, `change` the amount the
--              resistance took (negative) or added, `flags` the game's ResistanceBoostFlags
--   multiplier Damage.Multiplier when it is not 1
--   rolls      { { type, dice, flat, natural, total, critical, from } }: the weapon's rolls
--              (Damage.DamageRolls, from = "weapon") and the dice of the damage's stats
--              expression (SE's Damage.ConditionRoll: its StatExpression, RollParams and
--              DamageTypeParams; from = the expression). `natural` is the sum of the dice as the
--              hit keeps it (the hit keeps no die by itself: the roll log's dice_thrown has them)
--   modifiers  { { type, value, name, die } }: Damage.Modifiers and Modifiers2 (boosts:
--              DamageBonus, CharacterWeaponDamage, ...), `die` when the modifier was thrown
--   unlisted   { [type] = n }: rolled minus what the listed rolls and modifiers add up to,
--              only where that is not zero - the terms do not add up
local RESISTANCE_FLAGS = { { 1, "BelowDamageThreshold" }, { 2, "ResistantToMagical" }, { 4, "ImmuneToMagical" },
                           { 8, "VulnerableToMagical" }, { 16, "ResistantToNonMagical" }, { 32, "ImmuneToNonMagical" },
                           { 64, "VulnerableToNonMagical" } }

local function flagNames(n, table_)
    if type(n) ~= "number" then return nil end
    local out = {}
    for _, f in ipairs(table_) do
        if math.floor(n / f[1]) % 2 == 1 then out[#out + 1] = f[2] end
    end
    return #out > 0 and table.concat(out, "|") or nil
end

local function diceOf(def)
    if type(def) ~= "table" then return nil end
    local n = def.AmountOfDices
    local faces = tostring(def.DiceValue or ""):match("^D(%d+)$")
    if type(n) == "number" and n > 0 and faces then return n .. "d" .. faces end
    return nil
end

local function textOf(name)
    local handle = type(name) == "table" and type(name.Handle) == "table" and name.Handle.Handle or nil
    if type(handle) ~= "string" or handle:find("HandleUnknown") then return nil end
    local t = try(Ext.Loca.GetTranslatedString, handle)
    return t ~= "" and t or nil
end

local function rollTerm(sr, damageType, from)
    if type(sr) ~= "table" then return nil end
    local roll, result = sr.Roll or {}, sr.Result or {}
    local def = roll.Roll or {}
    local critical = result.Critical ~= nil and tostring(result.Critical) or nil
    return { type = damageType, dice = diceOf(def), flat = def.DiceAdditionalValue, negative = def.DiceNegative or nil,
             natural = result.NaturalRoll, total = result.Total, critical = critical ~= "None" and critical or nil, from = from }
end

local function modifierTerm(m)
    if type(m) ~= "table" then return nil end
    local die
    local source = m.Source
    if type(source) == "table" then
        die = diceOf(source)
        for _, sr in ipairs(source.RollParams or {}) do
            die = die or diceOf(type(sr.Roll) == "table" and sr.Roll.Roll or nil)
        end
    end
    return { type = m.DamageType, value = m.Value, name = textOf(m.SourceName) or named(m.SourceId), die = die }
end

local function hitDamage(hit)
    local d = try(Ext.Types.Serialize, hit.Damage)
    if type(d) ~= "table" then return nil end
    local out = {}
    local types, anyType = {}, false
    for t, n in pairs(d.TotalDamagePerType or {}) do types[tostring(t)] = { rolled = n }; anyType = true end
    for t, n in pairs(d.FinalDamagePerType or {}) do
        types[tostring(t)] = types[tostring(t)] or {}
        types[tostring(t)].dealt = n
        anyType = true
    end
    if anyType then out.types = types end
    local resisted = {}
    for _, r in ipairs(d.Resistances or {}) do
        local meta = type(r) == "table" and r.Meta or {}
        resisted[#resisted + 1] = { type = meta.DamageType, change = meta.Value, flags = flagNames(r.Type, RESISTANCE_FLAGS) }
    end
    if #resisted > 0 then out.resisted = resisted end
    if type(d.Multiplier) == "number" and d.Multiplier ~= 1 then out.multiplier = d.Multiplier end
    local rolls = {}
    for t, list in pairs(d.DamageRolls or {}) do
        for _, sr in ipairs(list) do rolls[#rolls + 1] = rollTerm(sr, tostring(t), "weapon") end
    end
    local expression = d.ConditionRoll
    if type(expression) == "table" then
        local typesOf = expression.DamageTypeParams or {}
        for i, sr in ipairs(expression.RollParams or {}) do
            rolls[#rolls + 1] = rollTerm(sr, typesOf[i] and tostring(typesOf[i]) or try(function() return tostring(hit.DamageType) end),
                named(expression.StatExpression) or "expression")
        end
    end
    if #rolls > 0 then out.rolls = rolls end
    local modifiers = {}
    for _, field in ipairs({ "Modifiers", "Modifiers2" }) do
        for _, m in ipairs(d[field] or {}) do modifiers[#modifiers + 1] = modifierTerm(m) end
    end
    if #modifiers > 0 then out.modifiers = modifiers end
    local unlisted, anyUnlisted = {}, false
    for t, v in pairs(types) do
        if type(v.rolled) == "number" then
            local listed = 0
            for _, r in ipairs(rolls) do if r.type == t and type(r.total) == "number" then listed = listed + r.total end end
            for _, m in ipairs(modifiers) do if tostring(m.type) == t and type(m.value) == "number" then listed = listed + m.value end end
            if v.rolled - listed ~= 0 then unlisted[t] = v.rolled - listed; anyUnlisted = true end
        end
    end
    if anyUnlisted then out.unlisted = unlisted end
    return out
end

-- The amount of each damage type the hit dealt, in the game's order (HitDesc.DamageList):
-- "7 piercing, 3 cold, 2 fire"; nil when it lists none.
local function dealtText(hit)
    local parts = {}
    for _, p in ipairs(try(function() return hit.DamageList end) or {}) do
        local amount, kind = try(function() return p.Amount end), try(function() return tostring(p.DamageType) end)
        if type(amount) == "number" and amount > 0 and kind then parts[#parts + 1] = string.format("%d %s", amount, kind:lower()) end
    end
    return #parts > 0 and table.concat(parts, ", ") or nil
end

-- The game_hit row (Server/Act.lua, GK.HitRow in Shared/Bridge.lua) gains the hit's cast and
-- what hitDamage reads; its text names each damage type with its own amount, and what a
-- resistance took. Wrapped here, so that Bridge.lua's own row stays as it is for its readers.
local function hitRowMore(payload, hit)
    payload.cast = guidText(try(function() return hit.SpellCastGuid end))
    local damage = hitDamage(hit)
    for k, v in pairs(damage or {}) do payload[k] = v end
    local dealt = dealtText(hit)
    if dealt and type(payload.text) == "string" then
        local head = payload.text:match("^(.-): %d+ %S+$") or payload.text
        local resisted = {}
        for t, v in pairs(damage and damage.types or {}) do
            if type(v.rolled) == "number" and type(v.dealt) == "number" and v.rolled ~= v.dealt then
                resisted[#resisted + 1] = string.format("%s %d -> %d", t:lower(), v.rolled, v.dealt)
            end
        end
        table.sort(resisted)
        payload.text = head .. ": " .. dealt .. (#resisted > 0 and (" (" .. table.concat(resisted, ", ") .. ")") or "")
    end
end

if GK.HitRow and GK.HitRow ~= Log.HitRowWrapped then
    local base = GK.HitRow
    Log.HitRowWrapped = function(hit, target, by, nameOf)
        local payload = base(hit, target, by, nameOf)
        if type(payload) == "table" then pcall(hitRowMore, payload, hit) end
        return payload
    end
    GK.HitRow = Log.HitRowWrapped
end

-- A heal, as the game's functor that made it says: the functor's context after it ran
-- (Ext.Events.AfterExecuteFunctor) holds the hit of the heal - its inflicter, spell, status and
-- cast - and its AttackDesc.TotalHealDone, the amount before the target's maximum capped it.
-- Kept by target until HitpointsChanged of that target writes the game_heal row (the same tick).
Log.Healing = Log.Healing or {}
local function healDone(e)
    local result = e.Hit
    if not result then return end
    local healed = try(function() return result.Attack.TotalHealDone end)
    if type(healed) ~= "number" or healed <= 0 then return end
    local target = uuidOf(try(function() return e.Params.Target end))
    if not target then return end
    local h = result.Hit
    Log.Healing[target] = { tick = ticks(), healed = healed,
                            by = uuidOf(try(function() return h.Inflicter end)) or uuidOf(try(function() return h.InflicterOwner end)),
                            spell = named(try(function() return h.SpellId end)), status = named(try(function() return h.StatusId end)),
                            cast = guidText(try(function() return h.SpellCastGuid end)) }
end
if not Log.HealSubscribed then
    Log.HealSubscribed = pcall(function()
        Ext.Events.AfterExecuteFunctor:Subscribe(function(e) pcall(healDone, e) end)
    end)
end

-- Who killed whom, as Osiris KilledBy(victim, owner, attacker, storyAction) says, some ticks
-- before Died of the same victim; kept by victim until its game_death row.
Log.Killed = Log.Killed or {}

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
            local data = { who = id, amount = hp - before, hp = hp }
            -- the heal's functor ran in this tick or just before (Log.Healing)
            local j = Log.Healing[id]
            Log.Healing[id] = nil
            if j and ticks() - j.tick <= 3 then
                data.by, data.spell, data.status, data.cast, data.healed = j.by, j.spell, j.status, j.cast, j.healed
            end
            local from = data.by and string.format(" from %s%s", name(data.by), (data.spell or data.status) and ("'s " .. tostring(data.spell or data.status)) or "") or ""
            local capped = data.healed and data.healed > data.amount and string.format(", %d before the cap", data.healed) or ""
            add("heal", string.format("%s regains %d hp (%d/%d)%s%s", name(id), hp - before, hp, max or 0, from, capped), data, id, data.by)
        end
    end,
    StatusApplied = function(object, status, causee)
        if not statusShown(status) then return end
        local by = causee and causee ~= "" and causee ~= "00000000-0000-0000-0000-000000000000" and name(causee) or nil
        local turns = try(statusTurns, object, status)
        add("status", string.format("%s is now %s%s%s", name(object), statusName(status), by and (" (from " .. by .. ")") or "",
            turns and string.format(" for %g turns", turns) or ""),
            { who = bare(object), status = status, statusName = statusName(status), by = by and bare(causee) or nil, turns = turns }, object)
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
    KilledBy = function(victim, owner, attacker)
        local by = bare(attacker)
        if not by or by == "" or by == ZERO then by = bare(owner) end
        if by and by ~= "" and by ~= ZERO then Log.Killed[bare(victim)] = { tick = ticks(), by = by } end
    end,
    Died = function(character)
        local id = bare(character)
        local k = Log.Killed[id]
        Log.Killed[id] = nil
        local by = k and ticks() - k.tick <= 300 and k.by or nil
        add("death", name(character) .. " dies" .. (by and (" (killed by " .. name(by) .. ")") or ""), { who = id, by = by }, character)
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
        -- criticality arrives as a number (0 = none) or a name; only a real critical is worth saying
        local crit = tostring(criticality)
        local critical = (crit == "1" or crit:find("Success")) and "success" or (crit == "2" or crit:find("Fail")) and "failure" or nil
        local critText = critical == "success" and " (natural 20)" or critical == "failure" and " (natural 1)" or ""
        -- what was rolled: the roll the game finished a tick before (GK.Rolls.Finished)
        local r = GK.Rolls and GK.Rolls.Finished and GK.Rolls.Finished(roller, true) or {}
        local data = { who = bare(roller), success = resultType == 1, active = isActive == 1, critical = critical,
                       kind = r.kind, ability = r.ability, skill = r.skill, dc = r.dc, advantage = r.advantage,
                       modifiers = r.modifiers, bonus = r.bonus, natural = r.natural, discarded = r.discarded, total = r.total, pinned = r.pinned,
                       inspired = r.inspired }
        local what = r.skill or r.ability or "check"
        local numbers = r.natural and string.format(" (%s%s%s)", tostring(r.natural),
            r.bonus and r.bonus ~= 0 and string.format(" %+d = %s", r.bonus, tostring(r.total)) or "",
            r.dc and r.dc > 0 and (" vs " .. r.dc) or "") or ""
        add("roll", string.format("%s's %s %s %s%s%s", name(roller), isActive == 1 and "active" or "passive", what, verdict, numbers, critText),
            data, roller)
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

local ARITY = { AttackedBy = 7, HitpointsChanged = 2, StatusApplied = 4, StatusRemoved = 4, DownedChanged = 2, Died = 1, KilledBy = 4,
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

--- A game row as an entry: { n = seq, t (seconds), kind, text, fair, ...payload }.
local function entry(row)
    local e = { n = row.seq, t = math.floor(row.t / 100) / 10, kind = row.kind:sub(6), fair = row.knows == "player" }
    for k, v in pairs(row.payload or {}) do e[k] = v end
    return e
end
Log.Entry = entry

function Log.Since(n, options)
    options = options or {}
    n = n or 0
    local out = {}
    if n == 0 then
        for _, e in ipairs(Log.Saved) do
            if (e.fair or options.all) and (not options.kinds or options.kinds[e.kind]) then out[#out + 1] = e end
        end
    end
    local got = GK.EventLog.Since(n, { family = "game" })
    for _, row in ipairs(got.rows) do
        local e = entry(row)
        if (e.fair or options.all) and (not options.kinds or options.kinds[e.kind]) then out[#out + 1] = e end
    end
    return { last = GK.EventLog.Seq, entries = out, dropped = got.dropped }
end

return Log
