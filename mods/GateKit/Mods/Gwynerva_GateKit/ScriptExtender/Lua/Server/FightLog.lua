-- The fight log: the rows of a fight the game log does not write - what a creature is about to do
-- (its cast begun, ~20 ticks before any die of it is thrown), the reactions and interrupts used,
-- who joined and left a fight and why, the end of a turn and the start of a round, concentration
-- begun and ended, an item used. Family `fight`, off until GK.EventLog.On("fight").
--
-- Each row is written from what the game itself says, and a field the game does not give is left
-- out:
--   fight_action    the creation of a cast's state (eoc::spell_cast::StateComponent, SpellCastState)
--   fight_reaction  Osiris ReactionInterruptUsed; the trigger from esv::move::LeaveAttackRangeEvent
--   fight_joined    esv::combat::JoinEvent (CombatantJoinEvent): the fight and the initiative
--   fight_left      Osiris LeftCombat; why from Osiris KilledBy, FleeFromCombat and CombatEnded
--   turn_ended      Osiris TurnEnded; the round from esv::combat::TurnEndedEvent
--   round_started   Osiris CombatRoundStarted
--   concentration   esv::concentration::ConcentrationChanged; DamageCheck says a hit asked a save
--   item_used       Osiris TemplateUseFinished (the user, the template, the item, whether it worked)
--   resource_spent  the caster's ActionResources as its cast's state is created and destroyed
--
-- The Osiris listeners are registered once (they cannot be removed) and cost a table look while
-- the family is off; the component subscriptions exist only while it is on.

GK.FightLog = GK.FightLog or {}
local F = GK.FightLog
local L = GK.EventLog

local function try(fn, ...)
    local ok, a = pcall(fn, ...)
    if ok then return a end
    return nil
end

local function bare(id)
    return type(id) == "string" and (id:match("(%x+%-%x+%-%x+%-%x+%-%x+)$") or id) or id
end

local function uuidOf(entity)
    return entity and try(function() return entity.Uuid.EntityUuid end) or nil
end

local function name(id)
    id = bare(id)
    return id and (try(GK.See.Name, id) or id:sub(1, 8)) or "?"
end

local function vec(v)
    if v == nil then return nil end
    local x, y, z = try(function() return v[1] end), try(function() return v[2] end), try(function() return v[3] end)
    if type(x) ~= "number" or type(y) ~= "number" or type(z) ~= "number" then return nil end
    return { x, y, z }
end

local function ticks() return L.Ticks or 0 end

local function on() return L.Switched and L.Switched.fight ~= nil end

-- What the family remembers between the game's events, to join them: per creature the last
-- leave of its reach, the last cast it began, the last kill and flight, the last damage check of
-- its concentration, the round of its last turn's end; the fights that have begun.
local seen = { left = {}, cast = {}, killed = {}, fled = {}, check = {}, round = {}, started = {} }
F.Seen = seen

local function forget()
    for k in pairs(seen) do seen[k] = {} end
    F.Seen = seen
end

local function recent(entry, within)
    return entry and ticks() - entry.tick <= within and entry or nil
end

local function append(kind, payload)
    pcall(L.Append, kind, payload)
end

-- ---------------------------------------------------------------------------------------
-- fight_action: a creature begins a cast

local function flagsOf(options)
    local out = {}
    for _, f in pairs(try(function() return options end) or {}) do out[tostring(f)] = true end
    return out
end

local function concentrationSpell(spell)
    local stat = try(Ext.Stats.Get, spell)
    for _, f in pairs(stat and try(function() return stat.SpellFlags end) or {}) do
        if tostring(f) == "IsConcentration" then return true end
    end
    return false
end

-- What a creature holds of each action resource: { ["ActionPoint"] = 1, ["SpellSlot/1"] = 2 ... }
-- (a resource of a level is named with it), from its ActionResources and the resource's own name.
local function resourcesOf(entity)
    local resources = entity and try(function() return entity.ActionResources.Resources end)
    if not resources then return nil end
    local out = {}
    for id, list in pairs(resources) do
        local label = try(function() return Ext.StaticData.Get(id, "ActionResource").Name end) or tostring(id)
        for _, r in ipairs(list) do
            local level = try(function() return r.Level end) or 0
            local amount = try(function() return r.Amount end)
            if type(amount) == "number" then out[level > 0 and (tostring(label) .. "/" .. level) or tostring(label)] = amount end
        end
    end
    return out
end

-- the casts begun, by their entity, with what their caster held as they began; the casts still
-- aimed (previews), by their entity
local casts, previews = {}, {}

-- resource_spent: a cast's state is destroyed as the cast ends; what its caster holds then,
-- against what it held as the cast began, is what the cast spent (a walk into the cast is
-- charged in part: seen 0.5 of movement for a walk of 2 m)
local function castEnded(entity)
    local key = tostring(entity)
    local c = casts[key]
    casts[key], previews[key] = nil, nil
    if not c then return end
    local after = resourcesOf(c.caster)
    if not after then return end
    local spent, any = {}, false
    for label, was in pairs(c.before) do
        local now = after[label]
        if type(now) == "number" and now < was then spent[label] = was - now; any = true end
    end
    if not any then return end
    local parts = {}
    for label, n in pairs(spent) do parts[#parts + 1] = string.format("%s %g", label, n) end
    table.sort(parts)
    append("resource_spent", { text = string.format("%s's %s spent %s", name(c.who), tostring(c.spell), table.concat(parts, ", ")),
                               who = c.who, spell = c.spell, cast = c.cast, spent = spent })
end

local function cast(entity)
    local state = entity.SpellCastState
    if not state then return end
    local options = flagsOf(state.CastOptions)
    -- a player's cast is made as a preview while it is aimed, and becomes the cast when it is
    -- confirmed (the game clears IsPreview on the same state): its row waits for that
    if options.IsPreview then previews[tostring(entity)] = entity return end
    local who = uuidOf(state.Caster)
    local spell = try(function() return tostring(state.SpellId.Prototype) end)
    local targets = {}
    for _, t in ipairs(try(function() return state.Targets end) or {}) do
        local id = uuidOf(try(function() return t.Target end))
        local at = vec(try(function() return t.Position end))
        if id then targets[#targets + 1] = id elseif at then targets[#targets + 1] = at end
    end
    local from, to = vec(try(function() return state.CasterStartPosition end)), vec(try(function() return state.CastPosition end))
    if from and to then
        local dx, dy, dz = to[1] - from[1], to[2] - from[2], to[3] - from[3]
        if dx * dx + dy * dy + dz * dz < 0.01 then from, to = nil, nil end
    else
        from, to = nil, nil
    end
    local guid = try(function() return tostring(state.SpellCastGuid) end)
    local reaction = (options.IsInterrupt or options.IsReaction) and true or nil
    if who then seen.cast[who] = { tick = ticks(), cast = guid, spell = spell, reaction = reaction } end
    local before = resourcesOf(state.Caster)
    if before then casts[tostring(entity)] = { who = who, caster = state.Caster, cast = guid, spell = spell, before = before } end
    local first = targets[1]
    local text = string.format("%s begins %s%s%s%s", name(who), tostring(spell),
        type(first) == "string" and (" on " .. name(first)) or (type(first) == "table" and " at a position" or ""),
        #targets > 1 and string.format(" (%d targets)", #targets) or "",
        to and " after a move" or "")
    append("fight_action", { text = text, who = who, spell = spell, targets = #targets > 0 and targets or nil,
                             from = from, to = to, cast = guid, reaction = reaction })
end

-- each tick: a preview that became a cast is written; one that is gone was never cast
local function confirmPreviews()
    for key, entity in pairs(previews) do
        local state = try(function() return entity.SpellCastState end)
        if not state then
            previews[key] = nil
        elseif not flagsOf(try(function() return state.CastOptions end)).IsPreview then
            previews[key] = nil
            pcall(cast, entity)
        end
    end
end

-- ---------------------------------------------------------------------------------------
-- fight_reaction: a reaction or an interrupt used

local function leftReach(entity)
    local e = entity.ServerMoveLeaveAttackRangeEvent
    if not e then return end
    local observer, mover = uuidOf(e.Source), uuidOf(e.Target)
    if observer then seen.left[observer] = { tick = ticks(), mover = mover, where = vec(try(function() return e.LeavePosition end)) } end
end

local function reactionUsed(object, interrupt, auto)
    local who = bare(object)
    local payload = { who = who, interrupt = tostring(interrupt), auto = auto == 1 }
    local left = recent(seen.left[who], 30)
    if left then payload.trigger = { mover = left.mover, where = left.where } end
    local c = recent(seen.cast[who], 30)
    if c and c.reaction then payload.cast = c.cast; payload.spell = c.spell end
    payload.text = string.format("%s uses %s%s%s", name(who), tostring(interrupt), auto == 1 and " (by itself)" or "",
        left and (" as " .. name(left.mover) .. " leaves its reach") or "")
    append("fight_reaction", payload)
end

-- ---------------------------------------------------------------------------------------
-- fight_joined, fight_left

local function combatGuid(combat)
    return combat and try(function() return tostring(combat.CombatState.MyGuid) end) or nil
end

local function joined(entity)
    local e = entity.CombatantJoinEvent
    if not e then return end
    local who = uuidOf(entity)
    local fight = try(function() return e.Combat end)
    local combat = combatGuid(fight)
    local initiative = try(function() return e.Initiative end)
    local payload = { who = who, combat = combat, initiative = initiative, round = fight and try(function() return fight.TurnOrder.Round end) }
    if combat and seen.started[combat] then payload.underway = true end
    payload.text = string.format("%s joins the fight%s%s", name(who), initiative and string.format(" with initiative %d", initiative) or "",
        payload.underway and " under way" or "")
    append("fight_joined", payload)
end

local function writeLeft(payload)
    payload.text = string.format("%s leaves the fight%s%s", name(payload.who), payload.why and (": " .. payload.why) or "",
        payload.by and (" by " .. name(payload.by)) or "")
    append("fight_left", payload)
end

-- A creature that leaves neither killed nor fled may be leaving because the fight ends: the game
-- says so a tick later (Osiris CombatEnded, after every LeftCombat of the fight). Its row waits
-- for that tick, at most PENDING ticks, and is then written with or without the reason.
local PENDING = 3
local pending = {}

local function combatEnded(combat)
    combat = bare(combat)
    local keep = {}
    for _, p in ipairs(pending) do
        if p.payload.combat == combat then
            p.payload.why = "the fight ended"
            writeLeft(p.payload)
        else
            keep[#keep + 1] = p
        end
    end
    pending = keep
    seen.started[combat] = nil
end

local function flushPending()
    if #pending == 0 then return end
    local keep = {}
    for _, p in ipairs(pending) do
        if ticks() - p.tick > PENDING then writeLeft(p.payload) else keep[#keep + 1] = p end
    end
    pending = keep
end

local function killedBy(defender, owner, attacker)
    seen.killed[bare(defender)] = { tick = ticks(), by = bare(attacker), owner = bare(owner) }
end

local function fled(participant, combat)
    seen.fled[bare(participant)] = { tick = ticks(), combat = bare(combat) }
end

local function leftCombat(object, combat)
    local who = bare(object)
    local payload = { who = who, combat = bare(combat) }
    local killed, flight = recent(seen.killed[who], 10), recent(seen.fled[who], 300)
    seen.killed[who], seen.fled[who] = nil, nil
    if killed then
        payload.why, payload.by = "killed", killed.by ~= "" and killed.by or killed.owner
    elseif flight then
        payload.why = "fled"
    else
        pending[#pending + 1] = { tick = ticks(), payload = payload }
        return
    end
    writeLeft(payload)
end

-- ---------------------------------------------------------------------------------------
-- turn_ended, round_started

local function turnEndedEvent(entity)
    local e = entity.TurnEndedEvent
    local who = uuidOf(entity)
    local round = e and try(function() return e.TurnEnded[1].Round end)
    if who and type(round) == "number" then seen.round[who] = { tick = ticks(), round = round } end
end

local function turnEnded(object)
    if try(Osi.IsCharacter, object) ~= 1 then return end
    local who = bare(object)
    local payload = { who = who }
    local r = recent(seen.round[who], 5)
    if r then payload.round = r.round end
    local combat = try(Osi.CombatGetGuidFor, object)
    if type(combat) == "string" and combat ~= "" then payload.combat = bare(combat) end
    payload.text = string.format("%s's turn ends%s", name(who), r and string.format(" (round %d)", r.round) or "")
    append("turn_ended", payload)
end

local function roundStarted(combat, n)
    append("round_started", { text = string.format("round %s begins", tostring(n)), combat = bare(combat), round = n })
end

local function combatStarted(combat)
    seen.started[bare(combat)] = true
end

-- ---------------------------------------------------------------------------------------
-- concentration

local function damageCheck(entity)
    local who = uuidOf(entity)
    local e = entity.ConcentrationDamageCheck
    if who and e then seen.check[who] = { tick = ticks(), hits = try(function() return #e.Hits end) } end
end

local function concentrationChanged(entity)
    local e = entity.ConcentrationChanged
    if not e then return end
    local who = uuidOf(entity)
    local ended = try(function() return tostring(e.Ended.Prototype) end)
    local started = try(function() return tostring(e.Started.Prototype) end)
    if ended and ended ~= "" then
        local payload = { who = who, state = "ended", spell = ended, interrupted = try(function() return e.Interrupted end) }
        local check = who and seen.check[who]
        local c = who and recent(seen.cast[who], 60)
        if payload.interrupted and check then
            payload.why, payload.hits = "damage", check.hits
        elseif not payload.interrupted and c and c.spell and concentrationSpell(c.spell) then
            payload.why, payload.cast = "a new spell", c.cast
        end
        if who then seen.check[who] = nil end
        payload.text = string.format("%s stops concentrating on %s%s", name(who), ended, payload.why and (": " .. payload.why) or "")
        append("concentration", payload)
    end
    if started and started ~= "" then
        local payload = { who = who, state = "started", spell = started }
        local targets = {}
        for _, t in ipairs(try(function() return entity.Concentration.Targets end) or {}) do
            local id = uuidOf(try(function() return t.Target end))
            if id then targets[#targets + 1] = id end
        end
        if #targets > 0 then payload.targets = targets end
        local c = who and recent(seen.cast[who], 60)
        if c and c.spell == started then payload.cast = c.cast end
        if who then seen.check[who] = nil end
        payload.text = string.format("%s concentrates on %s", name(who), started)
        append("concentration", payload)
    end
end

-- ---------------------------------------------------------------------------------------
-- item_used

local function templateUseFinished(character, template, item, ok)
    local who, it = bare(character), bare(item)
    append("item_used", { text = string.format("%s uses %s%s", name(who), name(it), ok == 1 and "" or " (it did not work)"),
                          who = who, item = it, template = bare(template), ok = ok == 1 })
end

-- ---------------------------------------------------------------------------------------
-- The Osiris events: listened to once, written only while the family is on.

local OSIRIS = {
    ReactionInterruptUsed = { 3, reactionUsed },
    LeftCombat = { 2, leftCombat },
    KilledBy = { 4, killedBy },
    FleeFromCombat = { 2, fled },
    TurnEnded = { 1, turnEnded },
    CombatRoundStarted = { 2, roundStarted },
    CombatStarted = { 1, combatStarted },
    CombatEnded = { 1, combatEnded },
    TemplateUseFinished = { 4, templateUseFinished },
}
F.Registered = F.Registered or {}
for event, spec in pairs(OSIRIS) do
    if not F.Registered[event] then
        F.Registered[event] = pcall(Ext.Osiris.RegisterListener, event, spec[1], "after", function(...)
            if not on() then return end
            pcall(spec[2], ...)
        end)
    end
end

-- The components: subscribed while the family is on.
local COMPONENTS = {
    SpellCastState = cast,
    ServerMoveLeaveAttackRangeEvent = leftReach,
    CombatantJoinEvent = joined,
    TurnEndedEvent = turnEndedEvent,
    ConcentrationDamageCheck = damageCheck,
    ConcentrationChanged = concentrationChanged,
}

local handles, ticker = {}, nil

L.Hooks = L.Hooks or {}
L.Hooks.fight = {
    on = function()
        forget()
        pending, casts, previews = {}, {}, {}
        for component, fn in pairs(COMPONENTS) do
            local ok, h = pcall(Ext.Entity.OnCreate, component, function(entity) pcall(fn, entity) end)
            if ok and h then handles[#handles + 1] = h end
        end
        local ok, h = pcall(Ext.Entity.OnDestroy, "SpellCastState", function(entity) pcall(castEnded, entity) end)
        if ok and h then handles[#handles + 1] = h end
        local fine, t = pcall(function() return Ext.Events.Tick:Subscribe(function() pcall(flushPending) pcall(confirmPreviews) end) end)
        if fine then ticker = t end
    end,
    off = function()
        for _, h in ipairs(handles) do pcall(Ext.Entity.Unsubscribe, h) end
        handles = {}
        if ticker then pcall(function() Ext.Events.Tick:Unsubscribe(ticker) end) end
        ticker = nil
        pending, casts, previews = {}, {}, {}
        forget()
    end,
}

return F
