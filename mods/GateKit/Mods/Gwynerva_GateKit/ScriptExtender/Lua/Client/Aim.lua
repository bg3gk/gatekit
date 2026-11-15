-- GK.Gui.Aim: aiming at the world the way the pointer does, with the game window in the background.
--
-- How the game aims (found 2026-11-14, game 4.1.1.7631656, EoCClient.dll symbols):
--   * ecl::PickingHelper::Update (the "PickingHelper" system) clears the pick and runs DoPick,
--     which casts a ray from ls::Mouse::GetPos - the engine's own mouse position, fed by the
--     operating system's pointer. Injected SDL mouse motion never reaches it, so a window in the
--     background always picks screen point (0, 0) (D16 of the 2026-11-14 day run).
--     PickingHelper::SetTargetOverride is not read by DoPick or by the spell targeting.
--   * The targeting of a hotbar action is a client task (ecl::CharacterTask_Spell, the input
--     controller's PreviewTask); every frame after the picking it takes the picked character, item
--     or ground point as its target (CharacterTask_Spell::UpdatePreview -> PickSpellTarget), and the
--     game draws its own preview from that: the trajectory, the area, the outline of every entity
--     the area covers (ecl::spell_preview::HighlightsComponent on the spell cast's preview entity),
--     the hit chance and the reason a target is refused.
--   * A left click (input event 4, ecl::CharacterTask_Spell::HandleSpellInput) confirms what the
--     task holds; press and release must fall in different frames, and the pick must not be
--     marked as being over the UI (HasPickedInUI, set from where the real pointer last was).
-- So the aim is written where the pointer's pick lands - into the picking helper's result, right
-- after the game's own PickingHelper system ran (Ext.Entity.OnSystemPostUpdate) - and everything
-- after that is the game's: the same task, preview, checks and cast request as a real click.
-- Nothing is cast, moved or decided here that a pointer over the same spot would not.
--
--   GK.Gui.Aim(target, options)    target: an entity uuid (or handle) or a point {x, y, z}; answers
--                                  with GK.Gui.Aiming() once the game's targeting holds the aim
--   GK.Gui.Aiming()                the targeting now: mode, spell, shape, aim, valid, reason, affected,
--                                  and what the cursor shows: hit chance, (dis)advantages, damage
--   GK.Gui.Confirm()               the left click; answers whether the cast started (its targets
--                                  written by the server), or what was seen instead
--   GK.Gui.CancelAim(options)      gives the pointer back and leaves the targeting (keep = true:
--                                  only gives the pointer back)
--   GK.Cheat.AimBest(options)      tries the aims a player would try and keeps the best one, over
--                                  every character within 30 m, perceived or not
--
-- UI side (Ext.UI.Defer): the bridge runs every GK.Gui. chunk there.

local UI = GK.Gui
if not UI then return false end

UI.AimState = UI.AimState or {}
local S = UI.AimState

local function try(fn, ...)
    local ok, a, b = pcall(fn, ...)
    if ok then return a, b end
end

local function now() return Ext.Utils.MonotonicTime() end
local ticksNow = GK.Ticks

-- Limits in the client's ticks (GK.LimitTicks): guards against a hang, never the ordinary end.
local CLOSED_TICKS = GK.LimitTicks(1000)   -- the targeting closed and no cast came: a click taken for something else
-- Aim's settle: picking updates before a new aim's verdict is read. Ten are too few at speed 1
-- (a throw aimed over an obstacle read the straight shot's verdict before the arc's); twenty are
-- 600 ms at thirty updates a second.
local SETTLE = 20
local CLEAR_TICKS = 10                    -- CancelAim: a second look at the server's preview casts this many ticks later

local function vec(p) return p and { p[1], p[2], p[3] } or nil end

local function dist(a, b)
    if not (a and b) then return math.huge end
    local dx, dy, dz = a[1] - b[1], a[2] - b[2], a[3] - b[3]
    return math.sqrt(dx * dx + dy * dy + dz * dz)
end

local function uuidOf(entity)
    return entity and try(function() return entity.Uuid.EntityUuid end) or nil
end

local function same(a, b)
    if a == nil or b == nil then return false end
    local ua, ub = uuidOf(a), uuidOf(b)
    if ua and ub then return ua == ub end
    return tostring(a) == tostring(b)
end

local function nameOf(entity)
    local handle = try(function() return entity.DisplayName.NameKey.Handle.Handle end)
    local text = handle and try(Ext.Loca.GetTranslatedString, handle)
    if text and text ~= "" then return text end
    return try(function() return entity.DisplayName.Name end)
end

local function positionOf(entity)
    return vec(try(function() return entity.Transform.Transform.Translate end))
end

-- The character this client controls whose targeting is shown, and its input controller: of the
-- characters kept from the game's ClientControl events (GK.Controlled), the one with a task.
local function controlled()
    local first
    local kept = GK.Controlled()
    S.whyNone = #kept == 0 and "no ClientControl kept" or nil
    for _, e in ipairs(kept) do
        local okRead, ic = pcall(function() return e.ClientCharacter.InputController end)
        if not okRead then S.whyNone = "reading the input controller of " .. tostring(e) .. ": " .. tostring(ic); ic = nil end
        if okRead and not ic then S.whyNone = tostring(e) .. " has no input controller" end
        if ic then
            if try(function() return ic.PreviewTask end) or try(function() return ic.RunningTask end) then return e, ic end
            first = first or { e, ic }
        end
    end
    if first then return first[1], first[2] end
end

local function taskOf(ic)
    return try(function() return ic.PreviewTask end), try(function() return ic.RunningTask end)
end

local function spellOf(task)
    return task and try(function() return task.Spell.OriginatorPrototype end) or nil
end

local function helper()
    return try(Ext.UI.GetPickingHelper, 1)
end

-- The pick ------------------------------------------------------------------------------------

-- What the pointer would be over: written after the game's own picking, every frame while aiming.
local function writePick()
    if not S.active then return end
    local h = helper()
    if not h then return end
    local entity = S.entity
    local at = S.position
    if entity then
        at = positionOf(entity) or at
        if not at then return end
    end
    local inner = h.Inner
    local ground = vec(at)
    -- +0x15C of the helper (Inner.field_154_Flags): bit 0 HasHitWalkable, bit 1 HasPickedInUI
    -- (ecl::PickingHelper::HasHitWalkable / HasPickedInUI). A pointer over the game's own UI - the
    -- last place the real mouse crossed the window, the turn order bar - marks the pick "in UI"
    -- and the targeting ignores the click; the aimed pick is on the ground, not on the UI.
    local flags = inner.field_154_Flags or 0
    inner.field_154_Flags = (flags - flags % 4) + 1
    inner.Position = ground
    inner.WorldPosition = ground
    inner.SceneryPosition = ground
    inner.PlaceablePosition = ground
    for k = 1, 2 do
        local pick = inner.Inner[k]
        pick.Character = S.kind == "character" and entity or nil
        pick.DeadCharacter = S.kind == "corpse" and entity or nil
        pick.Item = S.kind == "item" and entity or nil
        pick.GameObject = entity
        if entity then
            local above = { at[1], at[2] + 1, at[3] }
            pick.CharacterPosition = above
            pick.DeadCharacterPosition = above
            pick.ItemPosition = vec(at)
        end
    end
    S.frames = (S.frames or 0) + 1
end
local function picked()
    S.fired = (S.fired or 0) + 1
    writePick()
end
UI.AimWrite = picked

-- The subscription to the game's picking is made on the main thread and never from an
-- Ext.UI.Defer callback (the render thread): one made or dropped there has crashed a copy (Script
-- Extender's "global_ != -1" assertion). One made as the bridge loads at game start, before a
-- level, never fires. So it is made on the game's own event for a level running (GameStateChanged
-- to Running), the one before it dropped first, and at load when a level already runs. Until its
-- first update is seen the bridge's tick watches it (the poller below) and makes it again once if
-- no update came in a second; once it has fired the poller leaves the tick. While nothing is aimed
-- the callback counts and returns.
local TRIES = 3

local function subscribe()
    if S.subscription then pcall(Ext.Entity.Unsubscribe, S.subscription) end
    S.subscription, S.followConfirmed = nil, false
    local ok, id = pcall(Ext.Entity.OnSystemPostUpdate, "PickingHelper", function() GK.Gui.AimWrite() end)
    if not ok then S.subscribeError = tostring(id) return end
    S.subscription, S.subscribeError = id, nil
    S.firedAtSubscribe, S.subscribedAt = S.fired or 0, ticksNow()
    S.tries = (S.tries or 0) + 1
end

local function follow()
    if S.followConfirmed then
        GK.Attention.Pollers.aimFollow = nil
        return
    end
    if not S.subscription then return end
    if (S.fired or 0) > (S.firedAtSubscribe or 0) then
        S.followConfirmed = true
    elseif ticksNow() - (S.subscribedAt or 0) > GK.LimitTicks(1000) and (S.tries or 0) < TRIES then
        subscribe()
    end
end

local function levelRuns()
    S.tries = 0
    subscribe()
    if GK.Attention and GK.Attention.Pollers then GK.Attention.Pollers.aimFollow = follow end
end

if not S.stateSubscribed then
    S.stateSubscribed = true
    Ext.Events.GameStateChanged:Subscribe(function(e)
        if tostring(e.ToState) == "Running" then pcall(levelRuns) end
    end)
    if tostring(Ext.Utils.GetGameState()) == "Running" and #GK.Controlled() > 0 then levelRuns() end
end

local function hook()
    if S.followConfirmed then return true end
    return false, "the game's picking is not followed yet (it is a moment after a level runs)"
        .. (S.subscribeError and (": " .. S.subscribeError) or "")
end

local function release()
    S.active = false
    S.entity, S.position, S.kind, S.uuid = nil, nil, nil, nil
    -- the real pointer's pick takes over: the view model follows whatever lies under it
    S.followed = nil
end

-- What the cursor's view model follows: the aim (an entity uuid or a rounded point) and the action.
local function followKey(aimed, spell)
    local at = aimed.uuid or (aimed.position and string.format("%.1f,%.1f,%.1f", aimed.position[1], aimed.position[2], aimed.position[3]))
    return tostring(spell) .. "@" .. tostring(at)
end

-- Relations: what the party knows of a character, asked of the server (GK.See.Relation) --------

UI.Relations = UI.Relations or {}
local relationTickets = {}
local nextTicket = 1
-- The channels are made as the file loads, on the main thread, like the picking's subscription.
local askChannel
do
    local ok, made = pcall(Ext.Net.CreateChannel, ModuleUUID, "GK_Ask")
    if ok then askChannel = made end
    pcall(Ext.Net.CreateChannel, ModuleUUID, "GK_Answer", function(msg)
        local cb = relationTickets[msg.ticket]
        relationTickets[msg.ticket] = nil
        if cb then pcall(cb, msg) end
    end)
end

local function askServer(code, callback)
    if not askChannel then return false end
    local ticket = nextTicket
    nextTicket = nextTicket + 1
    relationTickets[ticket] = callback
    return pcall(function() askChannel:SendToServer({ ticket = ticket, code = code }) end)
end

-- Asks for the relations of these uuids to `caster` (kept for 5 s of a running game, in ticks).
-- Returns true when all are known.
local function relations(uuids, caster)
    local missing, t, waiting = {}, ticksNow(), false
    for _, u in ipairs(uuids) do
        local known = UI.Relations[u]
        if known and known.pending and t - known.at < GK.LimitTicks(2000) then
            waiting = true
        elseif not known or known.pending or t - known.at > GK.LimitTicks(5000) then
            missing[#missing + 1] = u
        end
    end
    if #missing == 0 then return not waiting end
    local list = {}
    for _, u in ipairs(missing) do list[#list + 1] = string.format("%q", u) end
    local code = string.format([[local out = {} local caster = %q
for _, u in ipairs({ %s }) do
  local r = GK.See and GK.See.Relation and GK.See.Relation(u, caster) or "?"
  if u == caster then r = "self" elseif r == "hostile" then r = "enemy" end
  out[u] = r
end
return out]], tostring(caster), table.concat(list, ", "))
    local pending = {}
    for _, u in ipairs(missing) do
        if not (UI.Relations[u] and UI.Relations[u].pending) then UI.Relations[u] = { pending = true, at = t } end
        pending[#pending + 1] = u
    end
    askServer(code, function(msg)
        if msg.ok and type(msg.value) == "table" then
            for u, r in pairs(msg.value) do UI.Relations[u] = { relation = r, at = ticksNow() } end
        end
    end)
    return false
end

-- Reading the game's targeting ------------------------------------------------------------------

-- The game's numbers for the shape of a spell, from its stats (what the tooltip and the preview use).
local SHAPE_FIELDS = { "SpellType", "TargetRadius", "AreaRadius", "ExplodeRadius", "Range", "Angle", "Base", "Shape",
                       "Width", "Height", "AmountOfTargets", "MaxDistance", "TargetConditions" }

local function shapeOf(spell)
    local stat = spell and try(Ext.Stats.Get, spell)
    if not stat then return nil end
    local out = {}
    for _, field in ipairs(SHAPE_FIELDS) do
        local v = try(function() return stat[field] end)
        if v ~= nil and v ~= "" and v ~= 0 and v ~= -1 then out[field] = type(v) == "userdata" and tostring(v) or v end
    end
    local kind = tostring(out.SpellType or "")
    local radius = tonumber(out.ExplodeRadius) or tonumber(out.AreaRadius)
    if kind == "Zone" then
        out.kind = tostring(out.Shape or ""):lower() == "cone" and "cone" or "line"
    elseif kind == "Shout" then
        out.kind = radius and "radius (around the caster)" or "self"
    elseif radius and radius > 0 then
        out.kind = "radius"
    elseif kind == "Rush" or kind == "Wall" then
        out.kind = "line"
    elseif kind == "Teleportation" or kind == "ProjectileStrike" or kind == "Throw" then
        out.kind = "point"
    else
        out.kind = (tonumber(out.AmountOfTargets) or 1) > 1 and "several targets" or "single"
    end
    return out
end

-- The preview entity of the spell being aimed (its highlights and targeting components).
local function previewOf(caster)
    local found
    for _, e in ipairs(try(Ext.Entity.GetAllEntitiesWithComponent, "ClientSpellPreviewHighlights") or {}) do
        local owner = try(function() return e.SpellCastState.Caster end)
        if owner and caster and same(owner, caster) then return e end
        found = found or e
    end
    return found
end

local function highlighted(preview)
    local out = {}
    local set = preview and try(function() return preview.ClientSpellPreviewHighlights.Entities end)
    if not set then return out end
    for k, v in pairs(set) do
        local e = type(k) == "userdata" and k or v
        if type(e) == "userdata" then out[#out + 1] = e end
    end
    return out
end

-- TextContext of a task line (ecl::TextLine +0x10): the hit chance, and the warning of an aim the
-- task refuses ("not enough movement", "path blocked").
local TEXT_HIT_CHANCE = 0x80
local TEXT_WARNING = 0x04

-- The hotbar's view of the task: whether the game would take a click, and what it says.
local function activeTask()
    local data = UI.Data and UI.Data("HotBar")
    return data and try(function() return data.CurrentPlayer.UIData.ActiveTask end)
end

-- What the cursor text shows about the target under the aim, from the player's view model
-- (gui::VMUIData, filled by the engine's gui::ViewModelProvider::OnUpdateOverheadTarget on every
-- frame a spell is aimed - the aim written into the picking counts): the hit chance with its
-- advantages and disadvantages (HitChanceDesc) and the damage range after resistances.
local function locaOf(handle)
    local text = handle and try(Ext.Loca.GetTranslatedString, tostring(handle))
    if text and text ~= "" then return text end
    return handle and tostring(handle) or nil
end

local function cursorPreview()
    local data = UI.Data and UI.Data("HotBar")
    local ui = data and try(function() return data.CurrentPlayer.UIData end)
    if not ui then return nil end
    local out = {}
    local desc = try(function() return ui.HitChanceDesc end)
    if desc and try(function() return desc.ShowDescription end) then
        out.hit_chance = try(function() return desc.TotalHitChance end)
        -- the chance of one roll, before advantage and disadvantage
        out.base_hit_chance = try(function() return desc.BaseHitChance end)
        for _, side in ipairs({ "Advantages", "Disadvantages" }) do
            local list, names = try(function() return desc[side] end), {}
            for i = 1, (list and try(function() return #list end) or 0) do
                names[#names + 1] = locaOf(try(function() return list[i].Description end))
            end
            out[side:lower()] = names
        end
    end
    local range = try(function() return ui.DamagePreviewWithResistances end)
    local rolls = try(function() return ui.ObservableDamagesPreview end)
    local types = {}
    for i = 1, (rolls and try(function() return #rolls end) or 0) do
        local t = try(function() return tostring(rolls[i].DamageType) end)
        if t then types[#types + 1] = t end
    end
    if #types > 0 then
        out.damage_types = types
        out.damage_min = range and try(function() return range.Min end) or nil
        out.damage_max = range and try(function() return range.Max end) or nil
    end
    return out
end

--- The targeting now: { ok, mode, spell, shape, aim = { entity, name, position }, aimedByUs,
--- valid, reason, lines = { { text, context, template } }, hit_chance, advantages, disadvantages,
--- damage_min, damage_max, damage_types, affected = { { uuid, name, relation, hit_chance } },
--- relationsPending }. hit_chance and the rest are absent when the game shows none.
--- ok = false when no targeting is active.
function UI.Aiming()
    local caster, ic = controlled()
    if not ic then return { ok = false, error = "no character of this client is controlled (main menu, loading?)" } end
    local preview, running = taskOf(ic)
    local task = preview or running
    if not task then return { ok = false, mode = nil, error = "no targeting is active: use a hotbar action first (GK.Gui.UseSlot)" } end
    local out = { ok = true, mode = tostring(try(function() return task.TaskType end)), caster = uuidOf(caster),
                  running = running ~= nil and preview == nil or nil }
    local spell = spellOf(task)
    out.spell = spell
    out.shape = shapeOf(spell)
    local target = try(function() return task.Target end)
    local entity = target and try(function() return target.Target end)
    local position = target and vec(try(function() return target.Position end))
    out.aim = { entity = uuidOf(entity), name = entity and nameOf(entity) or nil, position = position }
    out.aimedByUs = S.active or false
    out.state = try(function() return task.SpellTargetingState end)
    -- valid and why not: the hotbar's task view (what the cursor text shows) and the task's own
    -- text lines (a template such as "[1]%" with its argument, and the context saying what it is)
    local info = activeTask()
    if info then out.valid = try(function() return info.CanExecute end) end
    local contexts = {}
    local vmLines = info and try(function() return info.Info end)
    for i = 1, math.min(try(function() return #vmLines end) or 0, 8) do
        contexts[i] = try(function() return tostring(vmLines[i].TextContext) end)
    end
    local lines, said = {}, {}
    local texts = try(function() return task.TaskInfo.Texts end)
    local preview = cursorPreview()
    local shown = false
    for i = 1, math.min(try(function() return #texts end) or 0, 8) do
        local line = texts[i]
        local template = try(function() return line:Get() end) or ""
        -- the line's own TextContext byte (ecl::TextLine +0x10) decides; the view model's line,
        -- which follows the task some frames later, only names the others
        local own = try(function() return line.field_10 end)
        local context = own == TEXT_HIT_CHANCE and "HitChance" or own == TEXT_WARNING and "Warning" or contexts[i]
        local row = { text = template, context = context }
        if context == "HitChance" then
            -- the line is a template ("[1]%") whose argument lives in the game's argument-string
            -- store, not in the line; the same number is the view model's HitChanceDesc
            shown = true
            if preview and preview.hit_chance then
                row.template = template
                row.text = (template:gsub("%[1%]", tostring(preview.hit_chance)))
            else
                row.template = template
                out.hitChanceNote = "the game shows a hit chance here but the hotbar's view model gave no HitChanceDesc"
            end
        elseif template ~= "" then
            said[#said + 1] = template
        end
        if row.text:find("%[%d+%]") and not row.template then
            row.template = template
            row.note = "an argument of this line is not readable: it lives in the game's argument-string store"
        end
        lines[#lines + 1] = row
    end
    if #lines > 0 then out.lines = lines end
    if #said > 0 then out.reason = table.concat(said, "; ") end
    -- the hit chance only when the task itself shows one (the view model keeps its last target's)
    if shown and preview and preview.hit_chance then
        out.hit_chance = preview.hit_chance
        out.base_hit_chance = preview.base_hit_chance
        out.advantages = preview.advantages
        out.disadvantages = preview.disadvantages
    end
    if preview and preview.damage_types and entity then
        out.damage_min, out.damage_max, out.damage_types = preview.damage_min, preview.damage_max, preview.damage_types
    end
    if info and try(function() return info.AoOWarning end) then out.warning = "provokes an attack of opportunity" end
    -- the game's own highlight: the entities its preview marks as inside the area
    local previewEntity = spell and previewOf(caster) or nil
    if spell and not previewEntity then
        out.affectedNote = "the game has no spell preview entity now (ecl::spell_preview::HighlightsComponent): nothing is listed rather than guessed"
    end
    local affected, uuids = {}, {}
    for _, e in ipairs(highlighted(previewEntity)) do
        local u = uuidOf(e)
        -- the game shows a chance for the creature under the aim only
        affected[#affected + 1] = { uuid = u, name = nameOf(e),
                                    hit_chance = (u and u == out.aim.entity) and out.hit_chance or nil }
        if u then uuids[#uuids + 1] = u end
    end
    if out.caster and #uuids > 0 then
        local known = relations(uuids, out.caster)
        for _, row in ipairs(affected) do
            local r = row.uuid and UI.Relations[row.uuid]
            row.relation = r and r.relation or nil
        end
        if not known then out.relationsPending = true end
    end
    out.affected = affected
    return out
end

-- Aiming ------------------------------------------------------------------------------------------

local function resolve(target)
    if type(target) == "table" then
        local x, y, z = target[1] or target.x, target[2] or target.y, target[3] or target.z
        if x and y and z then return { position = { x, y, z } } end
        return nil, "a point is {x, y, z}"
    end
    local entity = target and try(Ext.Entity.Get, target)
    if not entity then return nil, "no entity " .. tostring(target) .. " on this client (a uuid, or a point {x, y, z})" end
    local kind = "item"
    if try(function() return entity.ClientCharacter end) or try(function() return entity.IsCharacter end) then
        kind = "character"
        local dead = try(function() return entity.Health.Hp end)
        if dead == 0 then kind = "corpse" end
    end
    local at = positionOf(entity)
    if not at then return nil, "no position for " .. tostring(target) end
    return { entity = entity, kind = kind, position = at, uuid = uuidOf(entity) }
end

-- Whether the game's targeting holds the aim we write.
local function holds(aimed)
    local _, ic = controlled()
    local task = ic and taskOf(ic)
    local target = task and try(function() return task.Target end)
    if not target then return false end
    if aimed.entity then
        local t = try(function() return target.Target end)
        return t ~= nil and same(t, aimed.entity)
    end
    return dist(vec(try(function() return target.Position end)), aimed.position) < 1.0
end

--- Points the targeting at an entity (uuid) or a point {x, y, z}, as the pointer over it would.
--- Valid while a targeting mode is active (a hotbar action, throw, jump, an item's use, a move).
--- Answers once the game holds the aim (or says it did not, within options.within ms, default
--- 1500) with what GK.Gui.Aiming() reads then.
local aimNow

function UI.Aim(target, options)
    options = options or {}
    local aimed, why = resolve(target)
    if not aimed then return { ok = false, error = why } end
    local _, ic = controlled()
    if ic then return aimNow(aimed, options, ic) end
    if #GK.Controlled() == 0 then return { ok = false, error = "no character of this client is controlled", why = S.whyNone, did = "nothing" } end
    -- a character is controlled and its input controller could not be read from this context (the
    -- render thread, while the main thread rearranged the character's components at a fight's
    -- start): the aim is begun on the next tick it can be read. GAP: the entity is read from the UI's
    -- context; a read kept from the main thread expires with its tick (Script Extender's lifetimes),
    -- so the cure is the verbs' entity reads moved to the main thread.
    local step, unread = nil, S.whyNone
    return GK.Observe({ ok = true, did = "aiming" }, function(_, final)
        if not step then
            local _, again = controlled()
            if not again then
                if final then return { ok = false, error = "no character of this client is controlled", why = unread, did = "nothing" } end
                return nil
            end
            step = GK.Follow(aimNow(aimed, options, again))
        end
        local answer = step()
        if answer == nil and final then return { ok = false, did = "aimed", error = "the aim never answered" } end
        return answer
    end, (options.within or 1500) * 2)
end

aimNow = function(aimed, options, ic)
    if not taskOf(ic) then
        return { ok = false, error = "no targeting is active: use a hotbar action first (GK.Gui.UseSlot(spell))", did = "nothing" }
    end
    local hooked, problem = hook()
    if not hooked then return { ok = false, error = "the picking cannot be followed: " .. tostring(problem) } end
    -- the same aim as the one the view model already follows, held without a break: it reads
    -- that aim now, and there is nothing to wait for
    local key = followKey(aimed, spellOf((taskOf(ic))))
    local already = S.active and S.followed == key
    S.active = true
    S.entity, S.position, S.kind, S.uuid = aimed.entity, aimed.position, aimed.kind, aimed.uuid
    S.since = ticksNow()
    S.frames = 0
    -- calm mode switches the picking off; the aim needs it on
    local h = helper()
    if h and not try(function() return h.Activated end) then try(function() h.Activated = true end) end
    local result = { ok = true, aiming = aimed.uuid or aimed.position, did = "aimed" }
    return GK.Observe(result, function(ticks, final)
        if S.frames < 2 or not holds(aimed) then
            if not final then return nil end
            S.followed = nil
            local seen = UI.Aiming()
            return { ok = false, did = "aimed", saw = "the game's targeting did not take the aim (it holds "
                         .. tostring(seen.aim and (seen.aim.name or seen.aim.entity) or "nothing") .. ")",
                     hint = "is the target in this level and on this client? the targeting may have closed",
                     aiming = seen }
        end
        local seen = UI.Aiming()
        -- a read of the controlled character's input controller that failed (seen from the UI's
        -- context while the main thread changed it) is looked at again on the next tick
        if seen.ok == false and not final then return nil end
        if seen.relationsPending and not final then return nil end
        -- a refusal whose reason the task has not written yet (seen at speed 8: valid false, no
        -- line) is not the cursor's whole verdict
        if seen.valid == false and not seen.reason and not final then return nil end
        -- The view model follows the hovered target some frames behind the task's own lines, and
        -- whether a target out of reach can be walked to is judged later still (seen: Fire Bolt at
        -- a goblin moved 0.15 m beyond its reach read valid at 200 ms, when the view model had
        -- moved on, and refused "not enough movement" after the settle). Past an obstacle the
        -- task's first verdict is the straight line's ("path blocked") and the walk to a place
        -- with a clear line is judged three or four picking updates later. A new aim waits the settle:
        -- options.settle updates of the game's PickingHelper system (default SETTLE; S.frames counts
        -- those, not the frames drawn). GAP: a count of the game's own updates for want of an event
        -- of the verdict. Leads: CurrentPlayer.CurrentTarget, the hovered target's view model the
        -- damage-preview mod binds (not on the hotbar's data context); the task's
        -- MoveInSpellRangeCache.
        if seen.mode == "Spell" and not final and not already and S.frames < (options.settle or SETTLE) then
            return nil
        end
        if seen.mode == "Spell" and not seen.hitChanceNote then S.followed = key end
        seen.previewWaitedTicks = ticks
        seen.did = "aimed"
        return seen
    end, options.within or 1500)
end

-- The casts of `spell` by `caster` that hold their targets, by SpellCastGuid: { guid = { targets } }.
-- A cast entity (eoc::spell_cast::StateComponent, shared from the server) exists while the action
-- is aimed, with no targets; the server writes the targets into it when it takes the cast request
-- (the client sees them in the frame the targeting closes, with spell_cast::MovementComponent).
local function startedCasts(caster, spell)
    local out = {}
    for _, e in ipairs(try(Ext.Entity.GetAllEntitiesWithComponent, "SpellCastState") or {}) do
        local s = try(function() return e.SpellCastState end)
        local who = s and try(function() return s.Caster end)
        local id = s and try(function() return s.SpellId.OriginatorPrototype end)
        local n = s and try(function() return #s.Targets end) or 0
        if who and same(who, caster) and id == spell and n > 0 then
            local targets = {}
            for i = 1, n do
                local t = s.Targets[i]
                targets[i] = { entity = uuidOf(try(function() return t.Target end)), position = vec(try(function() return t.Position end)) }
            end
            out[tostring(try(function() return s.SpellCastGuid end))] = targets
        end
    end
    return out
end

-- What the first click of a two-click action picked (Throw: the object). The task holds the
-- hovered object in its result (ecl::CharacterTask_Spell Result.Target) before any click; the
-- click that picks it adds NeedsPreviewToActivate to the task's Flags and the task stays open for
-- the second click, and drops the object from the task's own Target. The flag alone is not the
-- sign: the click of an ordinary one-click attack on a creature adds it too (seen: Target_MainHandAttack
-- on a goblin, 100 ms after the press, with the goblin in Result.Target), but the task's Target
-- still holds the goblin then, until the cast arrives.
local function pickedOf(task)
    local flags = task and try(function() return tostring(task.Flags) end)
    if not (flags and flags:find("NeedsPreviewToActivate", 1, true)) then return nil end
    local picked = uuidOf(try(function() return task.Result.Target.Target end))
    local still = uuidOf(try(function() return task.Target.Target end))
    if picked and still == picked then return nil end
    return picked
end
--- What the first click of a two-click action picked in a targeting task (its uuid), or nil: the
--- rule Confirm answers `picked` by. Observation.
function UI.FirstClickPicked(task) return pickedOf(task) end

--- The left click on the aim. Refused when the game itself would not take it (the reason the
--- cursor shows). Answers with what happened: ok when the cast of the action started (the
--- server wrote its targets into the cast), with `cast = { spell, guid, targets }`; ok with
--- `picked = uuid` when the click was the first of a two-click action (Throw: the object to
--- throw) and the targeting waits for the second; ok = false with what was seen otherwise - the
--- targeting still open (the click was not taken) or closed with no cast of it.
function UI.Confirm(options)
    options = options or {}
    local before = UI.Aiming()
    if not before.ok then return { ok = false, did = "nothing", error = before.error } end
    if not S.active then
        -- a click without an aim lands wherever the real pointer last was: never given
        return { ok = false, did = "nothing", error = "nothing is aimed: GK.Gui.Aim(target) first", aiming = before }
    end
    if before.valid == false then
        return { ok = false, did = "nothing", saw = "the game does not take this aim" .. (before.reason and (": " .. before.reason) or ""),
                 hint = "aim elsewhere (GK.Gui.Aim) or leave it (GK.Gui.CancelAim)", aiming = before }
    end
    local spell = before.spell
    local caster = controlled()
    local earlier = startedCasts(caster, spell)
    local _, icBefore = controlled()
    local pickedBefore = pickedOf(icBefore and taskOf(icBefore))
    local pressed, problem = pcall(Ext.Input.InjectMouseButton, 1, true)
    if not pressed then return { ok = false, did = "nothing", error = "the click could not be given: " .. tostring(problem) } end
    local looks, held = 0, true
    local closedAt
    local result = { ok = true, did = "clicked", spell = spell, aim = before.aim, affected = before.affected }
    return GK.Observe(result, function(ticks, final)
        -- the release at the second look (the observation is looked at once a tick): a press and
        -- a release in one frame are not a click to the game
        looks = looks + 1
        if held then
            if looks < 2 and not final then return nil end
            pcall(Ext.Input.InjectMouseButton, 1, false)
            held = false
        end
        for guid, targets in pairs(startedCasts(caster, spell)) do
            if not earlier[guid] then
                release()
                -- out of range and out of a fight the caster walks into range first: the cast has
                -- started all the same (its targets are written), the blow comes after the walk
                result.cast = { spell = spell, guid = guid, targets = targets }
                result.saw = "the cast of " .. tostring(spell) .. " started"
                return result
            end
        end
        local _, ic = controlled()
        local preview = ic and taskOf(ic)
        local stillAiming = preview and spellOf(preview) == spell and spell ~= nil
        if not stillAiming then
            -- the targets arrive from the server around the frame the targeting closes; none
            -- within a second after it closed (a guard, not the ordinary end) is a click the
            -- game took for something else
            closedAt = closedAt or ticks
            if ticks - closedAt < CLOSED_TICKS and not final then return nil end
            release()
            return { ok = false, did = "clicked", spell = spell, aim = before.aim,
                     saw = "the targeting of " .. tostring(spell) .. " closed and no cast of it started",
                     hint = "read the event log (game_cast) and GK.Gui.Aiming()" }
        end
        local picked = pickedOf(preview)
        if picked and picked ~= pickedBefore and (before.aim.entity == nil or picked == before.aim.entity) then
            -- the aim stays held until the next Aim moves it
            result.picked = picked
            result.saw = "the first click of " .. tostring(spell) .. " picked " .. tostring(before.aim.name or picked)
                .. ": the targeting waits for the second (GK.Gui.Aim where it goes, then GK.Gui.Confirm)"
            return result
        end
        if not final then return nil end
        return { ok = false, did = "clicked", saw = "the targeting of " .. tostring(spell) .. " is still open: the game did not take the click",
                 hint = "read GK.Gui.Aiming() (valid, reason); the aim is still held: GK.Gui.Aim again, or GK.Gui.CancelAim",
                 aiming = UI.Aiming() }
    end, options.within or 2500)
end

--- Gives the pointer back; unless options.keep, also leaves the targeting as a right click does
--- (the hotbar's own ActionCancelCommand).
-- The caster's preview casts on the server (GK.Stage.PreviewCasts), asked over the bridge's own
-- channel: calls back with a list of spells.
local function serverPreviews(caster, callback)
    return askServer(string.format("return (GK.Stage.PreviewCasts()[%q] or {})", tostring(caster)), function(msg)
        callback(msg.ok and type(msg.value) == "table" and msg.value or nil)
    end)
end

--- Gives the pointer back; unless options.keep, also leaves the targeting as a right click does
--- (the hotbar's own ActionCancelCommand). The cancel runs whenever the caster holds a task or the
--- server holds a preview cast of it - a targeting the client turned into a walk keeps the
--- server's preview otherwise - and the answer comes when the targeting of the action left is
--- closed and the server holds no preview cast of the caster: { ok, did, saw, left? } (`left`: the
--- server's preview casts still there at the limit).
function UI.CancelAim(options)
    options = options or {}
    release()
    if options.keep then return { ok = true, did = "the pointer aims again; the targeting stays" } end
    local caster, ic = controlled()
    local task = ic and taskOf(ic)
    local leaving = spellOf(task)
    local who = uuidOf(caster)
    local asked = { pending = true }
    if who then serverPreviews(who, function(list) asked.pending, asked.list = false, list end) end
    local command, ran
    local function cancel()
        local data = UI.Data and UI.Data("HotBar")
        for _, name in ipairs({ "ActionCancelCommand", "CancelTaskCommand" }) do
            command = data and try(function() return data[name] end)
            if command and pcall(function() command:Execute(nil) end) then ran = true; return end
        end
    end
    if task then cancel() end
    if task and not command then return { ok = false, did = "nothing", error = "the hotbar has no cancel command" } end
    local looks = 0
    return GK.Observe({ ok = true, did = ran and "cancelled" or "asked the server" }, function(_, final)
        looks = looks + 1
        local _, again = controlled()
        local preview = again and taskOf(again)
        -- the pointer over a creature makes the game's own idle targeting (the main-hand attack
        -- on a hostile) at once: what counts is that the targeting of the action left is gone
        local closed = leaving == nil or not preview or spellOf(preview) ~= leaving
        if asked.pending and not final then return nil end
        local held = asked.list and #asked.list > 0
        if held and not ran then
            -- the server holds a preview the client's task no longer names: the cancel runs, and
            -- the server is asked again
            cancel()
            asked.pending = true
            serverPreviews(who, function(list) asked.pending, asked.list = false, list end)
            return nil
        end
        if held and asked.checked ~= looks and not final then
            asked.pending, asked.checked = true, looks
            serverPreviews(who, function(list) asked.pending, asked.list = false, list end)
            return nil
        end
        if closed and not held and who and not final then
            -- the server's preview of a targeting the client turned into a walk can come a moment
            -- after the ask: none is believed until a second answer, CLEAR_TICKS later, agrees
            asked.clearAt = asked.clearAt or GK.Ticks()
            if GK.Ticks() - asked.clearAt < CLEAR_TICKS or not asked.second then
                if not asked.pending and GK.Ticks() - asked.clearAt >= CLEAR_TICKS then
                    asked.pending, asked.second = true, true
                    serverPreviews(who, function(list) asked.pending, asked.list = false, list end)
                end
                return nil
            end
        end
        if closed and not held then
            return { ok = true, did = ran and "cancelled" or "nothing to leave", already = not ran or nil,
                     saw = leaving and ("the targeting of " .. tostring(leaving) .. " closed") or "no targeting and no preview cast" }
        end
        if final then
            return { ok = false, did = ran and "ran the hotbar's cancel" or "nothing", left = asked.list,
                     saw = not closed and "the targeting is still open" or ("the server still holds a preview cast of " .. table.concat(asked.list or {}, ", ")) }
        end
    end, 2000)
end

-- The best aim ----------------------------------------------------------------------------------

local function centroid(points)
    local x, y, z = 0, 0, 0
    for _, p in ipairs(points) do x, y, z = x + p[1], y + p[2], z + p[3] end
    local n = #points
    return { x / n, y / n, z / n }
end

local AVOID = { party = { self = true, party = true }, allies = { self = true, party = true, ally = true } }

--- Tries the aims a player would try - each enemy, the middle of every pair, the middle of all -
--- for the action in hand, reads the game's own `affected` for each, and leaves the aim on the
--- best: { ok, best, ranking = { { aim, enemies, avoided, affected, hit_chance } } }. options:
--- want = "most enemies" (the default) or "best chance" (single targets by the hit chance the game
--- shows; aims with none rank last), avoid = "party" | "allies" | nil, candidates = n (default
--- 12), targets = { uuid or {x,y,z} ... } (instead of the enemies around). No click: Confirm is
--- the caller's.
--- A cheat (GK.Cheat.AimBest): its candidates are every character within 30 m, perceived or not.
function GK.Cheat.AimBest(options)
    options = options or {}
    local now0 = UI.Aiming()
    if not now0.ok then return { ok = false, error = now0.error } end
    local caster = controlled()
    local origin = positionOf(caster)
    local limit = options.candidates or 12
    local reach = 30
    local shape = now0.shape or {}
    local candidates, enemies = {}, {}
    if options.targets then
        for _, t in ipairs(options.targets) do candidates[#candidates + 1] = t end
    else
        -- the characters a player sees around: their relation decides who counts
        local around = {}
        for _, e in ipairs(try(Ext.Entity.GetAllEntitiesWithComponent, "ClientCharacter") or {}) do
            local p = positionOf(e)
            local u = uuidOf(e)
            local hp = try(function() return e.Health.Hp end)
            if p and u and dist(p, origin) < reach and hp ~= 0 then around[#around + 1] = { uuid = u, position = p } end
        end
        local uuids = {}
        for _, c in ipairs(around) do uuids[#uuids + 1] = c.uuid end
        relations(uuids, uuidOf(caster))
        S.bestAround = around
    end
    local state = { phase = options.targets and "try" or "relations", index = 0, ranking = {}, startedAt = ticksNow() }
    local function addCandidates()
        for _, c in ipairs(S.bestAround or {}) do
            local r = UI.Relations[c.uuid]
            if r and r.relation == "enemy" then enemies[#enemies + 1] = c end
        end
        for _, e in ipairs(enemies) do candidates[#candidates + 1] = e.uuid end
        for i = 1, #enemies do
            for j = i + 1, #enemies do
                candidates[#candidates + 1] = centroid({ enemies[i].position, enemies[j].position })
            end
        end
        if #enemies > 2 then
            local all = {}
            for _, e in ipairs(enemies) do all[#all + 1] = e.position end
            candidates[#candidates + 1] = centroid(all)
        end
        while #candidates > limit do table.remove(candidates) end
    end
    local function score(seen)
        local hit, avoided = 0, 0
        local avoid = AVOID[options.avoid or ""] or {}
        for _, a in ipairs(seen.affected or {}) do
            if a.relation == "enemy" then hit = hit + 1 end
            if avoid[a.relation or ""] then avoided = avoided + 1 end
        end
        local value = seen.valid == false and -1000 or hit * 10 - avoided * 1000
        if options.want == "best chance" then
            -- single targets ranked by the game's own hit chance; an aim with none shown ranks last
            value = (seen.valid == false or not seen.hit_chance) and -1000 or seen.hit_chance - avoided * 1000
        end
        return value, hit, avoided
    end
    local result = { ok = true, did = "trying aims" }
    return GK.Observe(result, function(_, final)
        if state.phase == "relations" then
            local pending = false
            for _, c in ipairs(S.bestAround or {}) do
                local r = UI.Relations[c.uuid]
                if not r or r.pending then pending = true end
            end
            if pending and ticksNow() - state.startedAt < GK.LimitTicks(1500) then return nil end
            addCandidates()
            state.phase = "try"
        end
        if state.phase == "try" then
            if state.current then
                local aimed = state.current
                local waited = ticksNow() - state.currentAt
                if (S.frames or 0) < 3 or (not holds(aimed) and waited < GK.LimitTicks(600)) then return nil end
                local seen = UI.Aiming()
                if seen.relationsPending and waited < GK.LimitTicks(1200) then return nil end
                local value, hit, avoided = score(seen)
                local names = {}
                for _, a in ipairs(seen.affected or {}) do names[#names + 1] = (a.name or a.uuid) .. "(" .. tostring(a.relation) .. ")" end
                state.ranking[#state.ranking + 1] = { aim = state.target, value = value, enemies = hit, avoided = avoided,
                                                      valid = seen.valid, reason = seen.reason, affected = names,
                                                      hit_chance = seen.hit_chance, held = holds(aimed) }
                state.current = nil
            end
            state.index = state.index + 1
            local nextTarget = candidates[state.index]
            if nextTarget then
                local aimed = resolve(nextTarget)
                if aimed then
                    S.active, S.entity, S.position, S.kind, S.uuid = true, aimed.entity, aimed.position, aimed.kind, aimed.uuid
                    S.frames, S.since, S.followed = 0, ticksNow(), nil
                    hook()
                    state.current, state.currentAt, state.target = aimed, ticksNow(), nextTarget
                end
                return nil
            end
            state.phase = "settle"
        end
        if state.phase == "settle" then
            table.sort(state.ranking, function(a, b) return a.value > b.value end)
            local best = state.ranking[1]
            if not best then
                return { ok = false, did = "tried nothing", saw = "no candidate: no enemy around (relations of " .. #(S.bestAround or {}) .. " characters read)" }
            end
            if best.value < 0 and options.avoid then
                release()
                return { ok = false, did = "tried " .. #state.ranking .. " aims", saw = "every aim hits what is to be avoided or is refused by the game",
                         ranking = state.ranking }
            end
            local aimed = resolve(best.aim)
            S.active, S.entity, S.position, S.kind, S.uuid = true, aimed.entity, aimed.position, aimed.kind, aimed.uuid
            S.frames, S.since, S.followed = 0, ticksNow(), nil
            state.phase = "hold"
            -- a copy: one table in two places of an answer reads as a recursion to the JSON writer
            local function copy(t)
                if type(t) ~= "table" then return t end
                local out = {}
                for k, v in pairs(t) do out[k] = copy(v) end
                return out
            end
            state.best = copy(best)
            return nil
        end
        if state.phase == "hold" then
            if (S.frames or 0) < 3 and not final then return nil end
            return { ok = true, did = "tried " .. #state.ranking .. " aims; the aim is on the best", best = state.best,
                     ranking = state.ranking, aiming = UI.Aiming() }
        end
        if final then return { ok = false, did = "ran out of time", ranking = state.ranking } end
    end, options.within or 15000)
end

-- The aim is given back when nothing aims any more (the targeting closed some other way).
if GK.Attention and GK.Attention.Pollers then
    GK.Attention.Pollers.aimRelease = function()
        if not S.active then return end
        local t = ticksNow()
        if t - (S.since or 0) < GK.LimitTicks(1000) or t - (S.checkedAt or 0) < GK.LimitTicks(500) then return end
        S.checkedAt = t
        local _, ic = controlled()
        if ic and taskOf(ic) then S.lostAt = nil return end
        -- given back only when no targeting has been seen for a while (it can blink between tasks)
        S.lostAt = S.lostAt or t
        if t - S.lostAt > GK.LimitTicks(1500) then S.lostAt = nil release() end
    end
end

return true
