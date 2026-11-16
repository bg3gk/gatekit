-- The fight log: the rows of a fight the game log does not write - what a creature is about to do
-- (its cast begun, ~20 ticks before any die of it is thrown), the reactions and interrupts used,
-- who joined and left a fight and why, the end of a turn and the start of a round, concentration
-- begun and ended, an item used. Family `fight`, off until GK.EventLog.On("fight").
--
-- Each row is written from what the game itself says, and a field the game does not give is left
-- out:
--   fight_options   the AI's candidate actions for a creature (Ext.System.ServerAi.Ai[subject].Actions)
--   fight_choice    the candidate the AI takes (the list's NextAction stepping on)
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
-- fight_options, fight_choice: what a creature weighed, and what it chose
--
-- The game's AI keeps, per creature it is deciding for, the list of its candidate actions
-- (Ext.System.ServerAi.Ai[subject].Actions, CombatAiAction), read once the AI system has updated.
-- A finished calculation with a list new to this turn is written as `fight_options`, some ten to
-- twenty ticks before the cast's state; the list's NextAction stepping on is the AI taking one, in
-- the tick the cast's state is created: `fight_choice`. After the cast the list is one action of
-- no spell, the end of the turn (written too: a choice with no spell).

local function resourceLabel(id)
    return try(function() return tostring(Ext.StaticData.Get(id, "ActionResource").Name) end) or tostring(id)
end

local function costsOf(list)
    local out, any = {}, false
    for _, c in ipairs(try(function() return list end) or {}) do
        local id = try(function() return c.Resources[1] end)
        local amount = try(function() return c.Amount end)
        if id and type(amount) == "number" then
            local label = resourceLabel(id)
            local level = try(function() return c.SubResourceId end) or 0
            if type(level) == "number" and level > 0 then label = label .. "/" .. level end
            out[label] = (out[label] or 0) + amount
            any = true
        end
    end
    return any and out or nil
end

local AMOUNTS = { damage = "DamageAmount", heal = "HealAmount", control = "ControlAmount", boost = "BoostAmount",
                  position = "PositionScore", dot = "DoTAmount" }

local function amountsOf(score)
    local per = try(function() return score.Score.DamageAmounts end)
    if not per then return nil end
    local out, any = {}, false
    for who, a in pairs(per) do
        local one, some = {}, false
        for field, key in pairs(AMOUNTS) do
            local v = try(function() return a[key] end)
            if type(v) == "number" and v ~= 0 then one[field] = v; some = true end
        end
        local id = uuidOf(who)
        if id and some then out[id] = one; any = true end
    end
    return any and out or nil
end

local function far(a, b)
    if not a or not b then return false end
    local dx, dy, dz = a[1] - b[1], a[2] - b[2], a[3] - b[3]
    return dx * dx + dy * dy + dz * dz > 0.01
end

local function sane(v)
    -- the game fills an unused position with the largest float
    return v and math.abs(v[1]) < 1e30 and v or nil
end

-- the game's own chance of a candidate's roll, asked of the game with the creature as it is now
-- (the weapon set in hand counts): a local call, not the wrapped verb GK.Rolls.Chance
local function chanceOf(subject, spell, targetEntity)
    if not spell or spell == "" or not targetEntity then return nil end
    if not try(function() return targetEntity.ServerCharacter end) then return nil end
    local ok, chance = pcall(Ext.Stats.GetSpellRollChance, subject, spell, targetEntity)
    return ok and type(chance) == "number" and chance or nil
end

local function optionOf(subject, here, a)
    local spell = try(function() return tostring(a.Spell.Prototype) end)
    if spell == "" then spell = nil end
    local targetEntity = try(function() return a.Target end)
    local o = {
        guid = try(function() return tostring(a.ActionGuid) end),
        type = try(function() return tostring(a.Type) end),
        spell = spell,
        target = uuidOf(targetEntity),
        score = try(function() return a.FinalScore end),
        action = try(function() return a.ActionFinalScore end),
        position = try(function() return a.PositionFinalScore end),
        movement = try(function() return a.MovementFinalScore end),
        multiplier = try(function() return a.ScoreMultiplier end),
        range = { min = try(function() return a.MinRange end), preferred = try(function() return a.PreferredRange end),
                  max = try(function() return a.MaxRange end) },
        costs = costsOf(try(function() return a.ActionCosts end)),
        amounts = amountsOf(try(function() return a.Score end)),
    }
    if not o.target then o.at = sane(vec(try(function() return a.TargetPos end))) end
    local castAt = sane(vec(try(function() return a.CastPosition end)))
    if far(castAt, here) then o.from = castAt end
    local flags = {}
    for _, f in pairs(try(function() return a.Flags end) or {}) do flags[#flags + 1] = tostring(f) end
    if #flags > 0 then table.sort(flags) o.flags = flags end
    o.chance = chanceOf(subject, spell, o.target and targetEntity or nil)
    return o
end

-- per creature: the list last written (its options, best first), the content and the ids it was
-- written for, each id's rank, the NextAction it had then. The AI recalculates during a turn (a
-- weapon drawn): the same candidates with the same scores come back under new ids, and are not
-- written again - only their ids are taken over.
local thinking = {}

local function keyOf(a)
    return string.format("%s|%s|%s", tostring(try(function() return a.Spell.Prototype end)),
        tostring(uuidOf(try(function() return a.Target end))), tostring(try(function() return a.FinalScore end)))
end

local function idOf(a) return try(function() return tostring(a.ActionGuid) end) end

local function ranks(state, list)
    state.rankOf = {}
    for _, a in ipairs(list) do
        local id, rank = idOf(a), state.rankByKey[keyOf(a)]
        if id and rank then state.rankOf[id] = rank end
    end
end

local function writeOptions(subject, who, list)
    local here = vec(try(function() return subject.Transform.Transform.Translate end))
    local options, keys = {}, {}
    for i, a in ipairs(list) do
        options[i] = optionOf(subject, here, a)
        keys[options[i]] = keyOf(a)
    end
    table.sort(options, function(x, y) return (x.score or -math.huge) > (y.score or -math.huge) end)
    local rankByKey = {}
    for i, o in ipairs(options) do
        local k = keys[o]
        if rankByKey[k] == nil then rankByKey[k] = i end
    end
    local best = options[1]
    append("fight_options", { text = string.format("%s weighs %d option%s, the best %s%s (%.2f)", name(who), #options,
                                  #options == 1 and "" or "s", tostring(best and best.spell or "the end of its turn"),
                                  best and best.target and (" on " .. name(best.target)) or "", best and best.score or 0),
                              who = who, options = options })
    return options, rankByKey
end

local function writeChoice(who, state, a)
    local guid = idOf(a)
    local rank = guid and state.rankOf[guid]
    local chosen = rank and state.options[rank] or {}
    append("fight_choice", { text = string.format("%s chooses %s%s%s", name(who), tostring(chosen.spell or "to end its turn"),
                                 chosen.target and (" on " .. name(chosen.target)) or "",
                                 rank and string.format(" (%d of %d)", rank, #state.options) or ""),
                             who = who, spell = chosen.spell, target = chosen.target, at = chosen.at, score = chosen.score,
                             rank = rank, options = #state.options, guid = guid })
end

local function aiUpdated()
    local all = Ext.System.ServerAi.Ai
    local present = {}
    for subject, data in pairs(all) do
        local who = uuidOf(subject)
        if who then
            present[who] = true
            local list = {}
            for i, a in pairs(data.Actions or {}) do list[i] = a end
            local state = thinking[who]
            if #list == 0 then
                thinking[who] = nil
            elseif data.CalculationFinished and (type(data.ActionCount) ~= "number" or data.ActionCount == #list) then
                -- (a list whose ActionCount is behind its length is still being scored)
                local ids = {}
                for i, a in ipairs(list) do ids[i] = tostring(idOf(a)) end
                ids = table.concat(ids, "|")
                local nextAction = data.NextAction
                if not state or state.ids ~= ids then
                    local content = {}
                    for i, a in ipairs(list) do content[i] = keyOf(a) end
                    content = table.concat(content, "\n")
                    if state and state.content == content then
                        state.ids = ids
                        ranks(state, list)
                        if type(nextAction) == "number" and type(state.next) == "number" and nextAction < state.next then state.next = nextAction end
                    else
                        local options, rankByKey = writeOptions(subject, who, list)
                        state = { ids = ids, content = content, next = nextAction, options = options, rankByKey = rankByKey }
                        ranks(state, list)
                        thinking[who] = state
                    end
                end
                if type(nextAction) == "number" and type(state.next) == "number" and nextAction > state.next then
                    state.next = nextAction
                    local a = list[nextAction]
                    if a then writeChoice(who, state, a) end
                end
            end
        end
    end
    for who in pairs(thinking) do
        if not present[who] then thinking[who] = nil end
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
        thinking = {}
        local good, a = pcall(Ext.Entity.OnSystemPostUpdate, "ServerAi", function() pcall(aiUpdated) end)
        if good and a then handles[#handles + 1] = a end
    end,
    off = function()
        for _, h in ipairs(handles) do pcall(Ext.Entity.Unsubscribe, h) end
        handles = {}
        if ticker then pcall(function() Ext.Events.Tick:Unsubscribe(ticker) end) end
        ticker = nil
        pending, casts, previews, thinking = {}, {}, {}, {}
        forget()
    end,
}

return F
