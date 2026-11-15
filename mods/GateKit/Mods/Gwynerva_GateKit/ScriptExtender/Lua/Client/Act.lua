-- GK.Act on the UI side: an attack or a cast made the player's way, as one verb. Fair.
--
--   GK.Act.Cast(spell, target, options)   the hotbar slot of `spell` (GK.Gui.UseSlot), the aim at
--                                         `target` - a uuid or a point {x, y, z} (GK.Gui.Aim) - and
--                                         the click (GK.Gui.Confirm); answers once the game has
--                                         finished the cast as the client shows it
--   GK.Act.Attack(target, options)        the same with the main-hand attack
--                                         (options.spell names another: Projectile_MainHandAttack,
--                                         Target_OffhandAttack ...)
--
-- What the game refuses is answered as the game says it and nothing is done: a disabled slot (no
-- action left, not the character's turn), an aim the targeting does not take (out of reach, no
-- line of sight - the reason the cursor shows; the targeting is then left as a right click does).
--
-- The end is the game's own: the client's cast entity of the caster (eoc::spell_cast::
-- IsCastingComponent's Cast) receives eoc::spell_cast::FinishedEventOneFrameComponent when the
-- cast is over as the player sees it - its animation done, after the server has resolved the hits.
-- Both come from component subscriptions made once at load, which record only while a verb waits
-- and cost nothing while no cast is made. The answer
-- carries the game's result of the cast and, per target, the outcome the client was given with the
-- cast (eoc::spell_cast::RollsComponent: miss, hit, critical). The damage is the server's: the
-- `game_hit` and `game_damage` rows of the event log.
--
-- GK.Act.Waiting holds the state of the last of these verbs, to be read when one does not answer.
--
-- The guard against a hang is the observation's own limit, LIMIT_MS of a running game counted in
-- the client's ticks (GK.LimitTicks): reached without the game's event, it is "the cast never
-- finished".

GK.Act = GK.Act or {}
local Act = GK.Act
local UI = GK.Gui
if not UI then return false end

local LIMIT_MS = 60000          -- a cast: the walk into range and the animation fit in it
local TOGGLE_LIMIT_MS = 10000   -- a weapon drawn or put away: the game answers within a few ticks

local function try(fn, ...)
    local ok, a, b = pcall(fn, ...)
    if ok then return a, b end
end

local function uuidOf(entity)
    return entity and try(function() return entity.Uuid.EntityUuid end) or nil
end

-- An answer of an observed verb is followed to its end by the bridge's own loop (GK.Follow).
local follow = GK.Follow

-- The outcome the client holds for each target of a cast entity, by the one mapping of a hit's
-- flags both sides use (GK.HitOutcome).
local function outcomesOf(cast)
    local out = {}
    local rolls = try(function() return cast.ClientSpellCastRolls or cast.SpellCastRolls end)
    for _, roll in ipairs(rolls and try(function() return rolls.Rolls end) or {}) do
        for _, c in ipairs(try(function() return roll.Casts end) or {}) do
            local flags, names = {}, {}
            for _, f in pairs(try(function() return c.Hit.EffectFlags end) or {}) do flags[tostring(f)] = true; names[#names + 1] = tostring(f) end
            out[#out + 1] = { target = uuidOf(try(function() return roll.Target end)), outcome = GK.HitOutcome(flags), flags = table.concat(names, "|") }
        end
    end
    return out
end

-- The casts being waited for, fed by the game's own component events. On the client the
-- caster's cast entity exists from the start of the targeting (it carries the preview), so a watch
-- begins before the slot is used and learns its caster from the aim. Each watch: casts (caster
-- uuid -> its cast entity), finished (caster uuid -> the finished cast), ended (caster uuid ->
-- its IsCasting went).
--
-- The subscriptions are made once, here, on the main thread, and only record while a watch is
-- open. They are never made or removed from a verb: the verb's answer is looked at in the UI's
-- deferred context, which runs on the render thread, and an Ext.Entity.Unsubscribe from there
-- changes the hook list the main thread may be walking at that moment - the game stopped with
-- Script Extender's assertion "global_ != -1" the one time it was done.
Act.Watches = Act.Watches or {}
local Watches = Act.Watches

local function watchCast(seen)
    seen.casts, seen.finished, seen.ended, seen.byGuid = {}, {}, {}, {}
    Watches[seen] = true
    return function() Watches[seen] = nil end
end

local function guidOf(entity)
    local guid = try(function() return entity.SpellCastState.SpellCastGuid end)
    return guid and tostring(guid) or nil
end

if not Act.CastSubscriptions then
    Act.CastSubscriptions = {
        Ext.Entity.OnCreate("SpellCastIsCasting", function(entity)
            if next(Watches) == nil then return end
            local who = uuidOf(entity)
            local cast = try(function() return entity.SpellCastIsCasting.Cast end)
            if not (who and cast) then return end
            for seen in pairs(Watches) do seen.casts[who] = tostring(cast); seen.ended[who] = nil end
        end),
        Ext.Entity.OnCreate("SpellCastFinishedEvent", function(entity)
            if next(Watches) == nil then return end
            local key, finished = tostring(entity), nil
            -- by the cast's own id (SpellCastGuid, what GK.Gui.Confirm answers): a cast that was
            -- there before the watch opened is matched too
            local guid = guidOf(entity)
            if guid then
                finished = { result = tostring(try(function() return entity.SpellCastFinishedEvent.Result end)), outcomes = outcomesOf(entity) }
                for seen in pairs(Watches) do seen.byGuid[guid] = finished end
            end
            for seen in pairs(Watches) do
                for who, castKey in pairs(seen.casts) do
                    if castKey == key and not seen.finished[who] then
                        finished = finished or { result = tostring(try(function() return entity.SpellCastFinishedEvent.Result end)),
                                                 outcomes = outcomesOf(entity) }
                        seen.finished[who] = finished
                    end
                end
            end
        end),
        Ext.Entity.OnDestroy("SpellCastIsCasting", function(entity)
            if next(Watches) == nil then return end
            local who = uuidOf(entity)
            for seen in pairs(Watches) do
                if who and seen.casts[who] then seen.ended[who] = true end
            end
        end),
    }
end

--- A spell or action from the hotbar at a target, the player's way; answers when the game has
--- finished it: { ok, did, spell, caster, target, result, outcomes = { { target, outcome, flags } },
--- shown = { hit_chance, damage_min, damage_max } (what the cursor showed), ticks }; or
--- { ok = false, did, saw | error } with what the game refused.
function Act.Cast(spell, target, options)
    options = options or {}
    if type(spell) ~= "string" then return { ok = false, did = "nothing", error = "GK.Act.Cast(spell, target): spell is the action's id" } end
    if target == nil then return { ok = false, did = "nothing", error = "GK.Act.Cast(spell, target): a uuid or a point {x, y, z}" } end
    local state = { phase = "use", ticks = 0, seen = {}, spell = spell }
    Act.Waiting = state
    state.unwatch = watchCast(state.seen)
    state.step = follow(UI.UseSlot(spell))
    local result = { ok = true, did = "using " .. spell, spell = spell, target = type(target) == "string" and target or nil }
    local function done(answer)
        if state.unwatch then state.unwatch() end
        state.unwatch = nil
        answer.ticks = state.ticks
        return answer
    end
    -- the targeting left as a right click does, and the answer given once the cancel has answered
    local function leave(answer)
        state.phase, state.leaving = "leave", answer
        state.step = follow(UI.CancelAim())
        return nil
    end
    return GK.Observe(result, function(ticks, final)
        state.ticks = ticks
        if state.phase == "leave" then
            local left = state.step()
            if not left and not final then return nil end
            state.leaving.cancel = left or { ok = false, error = "the cancel never answered in " .. ticks .. " ticks" }
            if left and left.ok == false then
                state.leaving.saw = (state.leaving.saw and (state.leaving.saw .. "; ") or "") .. "leaving the targeting: " .. tostring(left.saw or left.error)
            end
            return done(state.leaving)
        end
        if final and state.phase ~= "cast" then
            pcall(UI.CancelAim)
            return done({ ok = false, did = state.phase, spell = spell, error = "the " .. state.phase .. " step never answered in " .. ticks .. " ticks" })
        end
        if state.phase == "use" then
            local used = state.step()
            if not used then return nil end
            if not used.ok then return done({ ok = false, did = "nothing", spell = spell, saw = used.saw, error = used.error, hint = used.hint }) end
            state.phase = "aim"
            state.step = follow(UI.Aim(target))
            return nil
        end
        if state.phase == "aim" then
            local aimed = state.step()
            if not aimed then return nil end
            if aimed.ok == false or aimed.valid == false then
                return leave({ ok = false, did = "aimed and left the targeting", spell = spell,
                               saw = "the game does not take this aim" .. (aimed.reason and (": " .. aimed.reason) or ""),
                               error = aimed.error, aiming = aimed })
            end
            state.caster = aimed.caster
            state.shown = { hit_chance = aimed.hit_chance, damage_min = aimed.damage_min, damage_max = aimed.damage_max,
                            advantages = aimed.advantages, disadvantages = aimed.disadvantages }
            state.phase = "click"
            state.step = follow(UI.Confirm())
            return nil
        end
        if state.phase == "click" then
            local clicked = state.step()
            if not clicked then return nil end
            if not (clicked.ok and clicked.cast) then
                -- the targeting may still be open, with the aim held: left as a right click does
                return leave({ ok = false, did = "clicked and left the targeting", spell = spell, saw = clicked.saw,
                               error = clicked.error, hint = clicked.hint })
            end
            state.cast = clicked.cast
            state.phase = "cast"
            return nil
        end
        local seen = state.seen
        -- the cast the click started, by its id; else the cast entity the caster began in the watch
        local guid = state.cast and state.cast.guid
        local finished = guid and seen.byGuid[tostring(guid)] or seen.finished[state.caster]
        if finished then
            return done({ ok = true, did = "cast " .. spell, spell = spell, caster = state.caster, target = result.target,
                          cast = state.cast and state.cast.guid,
                          result = finished.result, outcomes = finished.outcomes, shown = state.shown,
                          saw = "the game finished the cast" })
        end
        if seen.ended[state.caster] then
            local ids = {}
            for id in pairs(seen.byGuid) do ids[#ids + 1] = id end
            return done({ ok = false, did = "clicked", spell = spell, caster = state.caster, cast = guid and tostring(guid),
                          finishedCasts = ids, saw = "the cast ended without the game's finished event (cancelled or interrupted)" })
        end
        if final then
            return done({ ok = false, did = state.phase == "cast" and "clicked" or state.phase, spell = spell, caster = state.caster,
                          error = "the cast never finished: no finished event in " .. ticks .. " ticks"
                              .. (seen.casts[state.caster or ""] and "" or " (the caster never began casting)") })
        end
        return nil
    end, LIMIT_MS)
end

--- The weapon attack, the player's way: GK.Act.Cast with the main-hand attack (options.spell for
--- another attack action).
function Act.Attack(target, options)
    options = options or {}
    return Act.Cast(options.spell or "Target_MainHandAttack", target, options)
end

-- ---------------------------------------------------------------------------------------
-- The weapon drawn and put away, the player's way: the key the profile binds to the game's input
-- event ToggleCombatMode ("Sheathe/Unsheathe Weapon"), read from the game's input scheme, pressed
-- in the game's own input system (GK.Key). The server makes esv::unsheath::UserRequestComponent
-- of it and changes the state (the weapon_state row); the client learns it when its
-- eoc::unsheath::StateComponent is replicated - the game's replication event, recorded by a
-- subscription made once here on the main thread, only while a verb waits - and that change is
-- the answer. (The client's ecl::unsheath::VisualStateComponent, what the model shows, has no
-- replication event: Script Extender refuses OnChange for it.)
Act.UnsheathWatches = Act.UnsheathWatches or {}
local UnsheathWatches = Act.UnsheathWatches

if not Act.UnsheathSubscription then
    Act.UnsheathSubscription = Ext.Entity.OnChange("Unsheath", function(entity)
        if next(UnsheathWatches) == nil then return end
        local who = uuidOf(entity)
        local state = who and try(function() return tostring(entity.Unsheath.State) end)
        if not state then return end
        for seen in pairs(UnsheathWatches) do
            local list = seen[who] or {}
            seen[who] = list
            if list[#list] ~= state then list[#list + 1] = state end
        end
    end)
end

-- The key bound to an input event of the game, from the input scheme of the first player:
-- the SDL scan code name and its modifier, or nil and why.
local function boundKey(eventName)
    local manager = Ext.Input.GetInputManager()
    local id
    for _, d in pairs(manager.InputDefinitions) do
        if tostring(d.EventName) == eventName then id = d.EventID; break end
    end
    if not id then return nil, "the game has no input event " .. eventName end
    local bindings = try(function() return manager.InputScheme.InputBindings[1][id] end)
    for _, b in ipairs(bindings and Ext.Types.Serialize(bindings) or {}) do
        local code = GK.ScanCode(tostring(b.InputId))
        if code then return code, b.Modifiers and b.Modifiers[1] and tostring(b.Modifiers[1]) or nil end
    end
    return nil, "no key is bound to " .. eventName .. " in the profile"
end

--- Presses the key the profile binds to one of the game's input events (its EventName in the
--- input manager's definitions: "ToggleWeaponSet", "ToggleCombatMode", ...), in the game's own
--- input system: { ok, event, key, modifier }, or { ok = false, error } when the game has no such
--- event or no key is bound to it. It answers that the key went in, not what the game did with it.
--- The press is the engine's own (InjectKeyPress, down and up in one call): held across two ticks
--- instead, two presses a tick apart (a toggle back at speed 8) merge into one.
function Act.Press(eventName)
    local key, modifier = boundKey(tostring(eventName))
    if not key then return { ok = false, event = eventName, error = modifier } end
    local pressed, problem = pcall(GK.Key, key, modifier)
    if not pressed then return { ok = false, event = eventName, key = key, error = "the key was not pressed: " .. tostring(problem) } end
    return { ok = true, event = eventName, key = key, modifier = modifier }
end

local function controlled() return GK.Controlled()[1] end

local function toggleTo(drawn, verb)
    local entity = controlled()
    if not entity then return { ok = false, did = "nothing", error = "no character of this client is controlled" } end
    local who = uuidOf(entity)
    local from = tostring(try(function() return entity.Unsheath.State end))
    if (from ~= "Sheathed") == drawn then
        return { ok = true, did = "nothing", who = who, from = from, to = from, saw = drawn and "the weapon is already out" or "the weapon is already away" }
    end
    local seen = {}
    UnsheathWatches[seen] = true
    local function unwatch() UnsheathWatches[seen] = nil end
    local press = Act.Press("ToggleCombatMode")
    if not press.ok then unwatch(); return { ok = false, did = "nothing", who = who, error = press.error } end
    local key = press.key
    local result = { ok = true, did = "pressed " .. key .. (press.modifier and ("+" .. press.modifier) or ""), who = who, key = key, from = from }
    return GK.Observe(result, function(ticks, final)
        local list = seen[who] or {}
        local last = list[#list]
        if last and (last ~= "Sheathed") == drawn then
            unwatch()
            return { ok = true, did = result.did, who = who, key = key, from = from, to = last, ticks = ticks,
                     saw = "the game " .. (drawn and "drew" or "put away") .. " the weapon" }
        end
        if final then
            unwatch()
            return { ok = false, did = result.did, who = who, key = key, from = from, states = list,
                     error = "the weapon state never became what " .. verb .. " asks: no change in " .. ticks .. " ticks" }
        end
        return nil
    end, TOGGLE_LIMIT_MS)
end

--- Draws the controlled character's weapon with the player's own toggle (the key bound to
--- ToggleCombatMode); answers when the client's weapon state shows it drawn:
--- { ok, did, who, key, from, to, ticks }; already drawn: did = "nothing".
function Act.Draw() return toggleTo(true, "Draw") end

--- Puts the controlled character's weapon away with the player's own toggle; answers as Draw.
function Act.Sheathe() return toggleTo(false, "Sheathe") end

return true
