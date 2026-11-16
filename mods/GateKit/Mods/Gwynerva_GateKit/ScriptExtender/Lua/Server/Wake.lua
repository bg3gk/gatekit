-- GK.Wake (server side): keeping a creature or a place awake and drawn far from the party.
--
--   GK.Wake.One(who)            keeps one creature awake and drawn
--   GK.Wake.Place(at, options)  wakes the space around a point or an entity as a party member there would
--   GK.Wake.Release(handle)     undoes either (nil: everything GK.Wake holds)
--   GK.Wake.State(at)           awake on the server, whole on the client, which rule keeps it
--   GK.Wake.Radii()             the activation manager's distances, read live
--
-- How (game 4.1.1.7631656; research/studies/camera-focus.md): once a tick the server's activation
-- manager (esv::ActivationManager) judges every character against its anchors - the characters of
-- the party's view, and characters asked to be kept up - by two distances, whole under the first
-- and gone beyond the second. A character that is not active has no Active component, does not
-- animate or think, and the client holds a stub of it that is not drawn. The client is sent a
-- creature whole by a shorter distance of its own (about 32 m from a party member), so a creature
-- can be awake and not drawn.
--   One creature: Osi.SetForceUpdate(creature, 1) makes it an anchor of itself; it wakes alone and
--   the client receives it whole.
--   A place: only an anchor of the party's view wakes other characters. A helper - a temporary
--   character the stage spawns there - is given the party member's component with the host's party
--   and user and is appended to both lists of the party's view. No portrait appears. A helper that
--   spawned asleep (beyond the party's reach) is taken off stage before it joins the view: it stays
--   a stub on the client, never drawn and nothing to click, and anchors all the same. A helper
--   that spawned awake (near the party) is left as it is, a plain figure a watcher sees, and the
--   answer says so: off stage a creature the client held whole leaves its figure frozen on screen.
-- What a party member's component on a helper does to the story is not measured: an origin, a
-- player's character or a creature of the level's own cast is refused as a helper.

GK.Wake = GK.Wake or {}
local Wake = GK.Wake

Wake.Kept = Wake.Kept or {}        -- creature -> { row }: kept up by One
Wake.Places = Wake.Places or {}    -- helper -> { at, row, spawned, hidden }: anchors made by Place
Wake.HelperTemplate = "human"      -- a name of GK.Stage.Templates or a root template
Wake.WakeMs = 3000                 -- a guard, in ms of a running game: the manager acts on the next tick
Wake.SleepMs = 3000
Wake.WholeMs = 8000                -- the client's limit of "whole"
Wake.Measured = { wake = 40, sleep = 45 }   -- the distances as measured, said when they cannot be read

local function try(fn, ...)
    local ok, a, b, c = pcall(fn, ...)
    if ok then return a, b, c end
    return nil
end

local function short(id) return type(id) == "string" and id:sub(-36) or id end

local function uuidOf(entity)
    return try(function() return entity.Uuid.EntityUuid end)
end

local function nameOf(id)
    return try(function() return Ext.Entity.Get(id).DisplayName.Name:Get() end)
end

local function awake(id)
    local e = Ext.Entity.Get(id)
    if e and e.Active then return true end
    return false
end

local function position(id)
    local x, y, z = try(Osi.GetPosition, id)
    return x and { x, y, z } or nil
end

local function pointOf(t)
    if type(t) ~= "table" then return nil end
    local x, y, z = t.x or t[1], t.y or t[2], t.z or t[3]
    if type(x) == "number" and type(y) == "number" and type(z) == "number" then return { x, y, z } end
end

local function metres(a, b)
    return math.sqrt((a[1] - b[1]) ^ 2 + (a[2] - b[2]) ^ 2 + (a[3] - b[3]) ^ 2)
end

local function round(n) return n and math.floor(n * 10 + 0.5) / 10 or nil end

--- The activation manager's distances as the game holds them now: { ok, wake, sleep, itemWake,
--- itemSleep, lowItemWake, lowItemSleep, source }. A character is awake within `wake` metres of an
--- anchor and asleep beyond `sleep`; between the two it stays as it was. Read from the manager's
--- own object (needs GateKit's Script Extender build); when it cannot be read, ok = false with the
--- measured `wake` and `sleep` and source = "measured". Observation.
function Wake.Radii()
    local ok, base = pcall(function() return GK.Memory.System("esv::ActivationManager") end)
    if ok and base then
        local at = base - 0x28
        local function read(offset) return try(GK.Memory.Read, at + offset, "float") end
        local out = { ok = true, source = "esv::ActivationManager", wake = read(0xd0), sleep = read(0xd4),
            itemWake = read(0xd8), itemSleep = read(0xdc), lowItemWake = read(0xe0), lowItemSleep = read(0xe4) }
        if type(out.wake) == "number" and type(out.sleep) == "number" and out.wake > 0 and out.sleep >= out.wake then return out end
    end
    return { ok = false, source = "measured", wake = Wake.Measured.wake, sleep = Wake.Measured.sleep,
        error = "the activation manager's distances cannot be read here: " .. tostring(not ok and base or "no esv::ActivationManager")
            .. "; these are the measured ones" }
end

local function partyView()
    return try(function() return Ext.Entity.Get(Osi.GetHostCharacter()).PartyMember.Party.PartyView end)
end

-- The anchors that wake other characters: the characters of the party's view, each
-- { who, at, kind }, kind "place" for a helper of GK.Wake.Place and "party" otherwise.
local function anchors()
    local out = {}
    local view = partyView()
    for _, e in ipairs(view and view.Characters or {}) do
        local id = uuidOf(e)
        local at = id and position(id)
        if at then out[#out + 1] = { who = id, at = at, kind = Wake.Places[id] and "place" or "party" } end
    end
    return out
end

local function nearest(point, except)
    local best, distance
    for _, a in ipairs(anchors()) do
        if a.who ~= except then
            local d = metres(a.at, point)
            if not distance or d < distance then best, distance = a, d end
        end
    end
    return best, distance
end

-- The characters of the level within `radius` of a point, the anchors left out:
-- { { who, at, distance, awake } }, nearest first.
local function around(point, radius)
    local skip = {}
    for _, a in ipairs(anchors()) do skip[a.who] = true end
    local out = {}
    for _, e in ipairs(Ext.Entity.GetAllEntitiesWithComponent("ServerCharacter")) do
        local id = uuidOf(e)
        if id and not skip[id] and e.Level then
            local at = position(id)
            local d = at and metres(at, point)
            if d and d <= radius then
                out[#out + 1] = { who = id, at = at, distance = d, awake = e.Active and true or false }
            end
        end
    end
    table.sort(out, function(a, b) return a.distance < b.distance end)
    return out
end

local function isCharacter(id)
    return type(id) == "string" and try(Osi.IsCharacter, id) == 1
end

local function log(kind, payload)
    if GK.EventLog and GK.EventLog.Append then pcall(GK.EventLog.Append, kind, payload) end
end

local function askClient(message, within)
    if not (GK.Camera and GK.Camera.AskClient) then return { ok = false, error = "no channel to the client (Server/Camera.lua)" } end
    return GK.Camera.AskClient(message, { ok = true, sent = message.op }, within)
end

local function trackRow(key)
    for _, row in ipairs(GK.Stage.Open()) do
        if row.payload and row.payload.key == key then return row end
    end
end

local function spawnRow(id)
    for _, row in ipairs(GK.Stage.Open()) do
        local p = row.payload
        if p and p.kind == "spawn" and short(p.entity) == id then return row end
    end
end

--- Keeps the creature `who` awake and drawn wherever the party is: Osi.SetForceUpdate, which makes
--- it an anchor of itself and of nobody else. Tracked by the stage's ledger (GK.Stage.Clean lets it
--- go); a row `wake_kept`. Observed: { ok, who, name, awake, drawn, was = { awake }, distance,
--- ticks, awakeTicks, clientTicks } once the server holds it active and the client holds it whole
--- (Level and GameObjectVisual); `distance` is to the nearest character of the party's view. A wait
--- that ran out says which - the server's or the client's - and after how many ticks, and the
--- creature is let go again. The force update it had before is not read: Release sets it off.
function Wake.One(who)
    who = short(who)
    if not isCharacter(who) then return { ok = false, error = "no such creature: " .. tostring(who) } end
    local was = { awake = awake(who) }
    local at = position(who)
    local _, distance = nearest(at)
    Osi.SetForceUpdate(who, 1)
    if not Wake.Kept[who] then
        local row = GK.Stage.Track{ kind = "other", key = "wake:one:" .. who, entity = who,
            note = "kept awake (Osi.SetForceUpdate) " .. who,
            undo = function()
                Wake.Kept[who] = nil
                try(Osi.SetForceUpdate, who, 0)
                return true
            end }
        Wake.Kept[who] = { row = row }
    end
    local stage, follow, awakeTicks = "server", nil, nil
    local function fail(answer)
        Wake.Release(who)
        return answer
    end
    return GK.Observe({ ok = true, who = who, waiting = "active on the server, whole on the client" }, function(ticks, final)
        if stage == "server" then
            if awake(who) then
                awakeTicks = ticks
                stage, follow = "client", GK.Follow(askClient({ op = "whole", must = { who }, wait = true, within = Wake.WholeMs }, Wake.WholeMs + 2000))
            elseif ticks >= GK.LimitTicks(Wake.WakeMs) or final then
                return fail({ ok = false, who = who, awake = false, drawn = false, ticks = ticks,
                    error = "not active on the server " .. ticks .. " ticks after Osi.SetForceUpdate" })
            end
            return nil
        end
        local answer = follow()
        if answer == nil then
            if final then return fail({ ok = false, who = who, awake = true, error = "the client did not answer in " .. ticks .. " server ticks" }) end
            return nil
        end
        if not answer.ok then
            return fail({ ok = false, who = who, name = nameOf(who), awake = true, drawn = false, was = was, ticks = ticks,
                awakeTicks = awakeTicks, clientTicks = answer.ticks,
                error = "active on the server after " .. awakeTicks .. " ticks, " .. tostring(answer.error) })
        end
        local out = { ok = true, who = who, name = nameOf(who), awake = true, drawn = true, was = was,
            distance = round(distance), ticks = ticks, awakeTicks = awakeTicks, clientTicks = answer.ticks }
        log("wake_kept", { who = who, how = "one", distance = out.distance, ticks = ticks })
        return out
    end, Wake.WakeMs + Wake.WholeMs + 4000)
end

local function anchor(helper)
    local e = Ext.Entity.Get(helper)
    local host = Ext.Entity.Get(Osi.GetHostCharacter()).PartyMember
    if not e.PartyMember then
        local c = e:CreateComponent("PartyMember")
        c.Party = host.Party
        c.IsPermanent = false
        c.UserId = host.UserId
        c.UserUuid = host.UserUuid
        c.ViewUuid = host.ViewUuid
    end
    local view = host.Party.PartyView
    local first = view.Views[1].Characters
    first[#first + 1] = e
    local all = view.Characters
    all[#all + 1] = e
end

local function unanchor(helper)
    local e = Ext.Entity.Get(helper)
    local view = partyView()
    if view then
        -- the lists are rewritten whole: a removal inside a proxied array is not reliable
        local first, all = {}, {}
        for _, x in ipairs(view.Views[1].Characters) do if uuidOf(x) ~= helper then first[#first + 1] = x end end
        for _, x in ipairs(view.Characters) do if uuidOf(x) ~= helper then all[#all + 1] = x end end
        view.Views[1].Characters = first
        view.Characters = all
    end
    if e and e.PartyMember then e:RemoveComponent("PartyMember") end
    return true
end

-- Why a creature the caller names cannot be the helper, or nil.
local function notAHelper(id)
    local e = Ext.Entity.Get(id)
    if not (e and isCharacter(id)) then return "no such creature: " .. tostring(id) end
    if e.Origin then return id .. " is an origin character" end
    if e.Player or try(Osi.IsPlayer, id) == 1 then return id .. " is a player's character" end
    if e.PartyMember then return id .. " is in a party already" end
    local temporary = false
    for _, flag in ipairs(try(function() return e.ServerCharacter.Flags end) or {}) do
        if tostring(flag) == "Temporary" then temporary = true end
    end
    if not temporary then return id .. " is of the level's own cast (not a temporary character): only a creature spawned for it (GK.Stage.Spawn) may be a helper" end
    return nil
end

-- Lets one place go: the helper out of the party's view, and deleted when Place spawned it.
local function dropPlace(helper)
    local place = Wake.Places[helper]
    if not place then return false end
    Wake.Places[helper] = nil
    unanchor(helper)
    if place.offStage and not place.spawned then try(Osi.SetOnStage, helper, 1) end
    return true
end

--- Wakes the space around `at` - a point {x, y, z} or an entity's uuid - as a party member standing
--- there would: a helper character in the party's view (above). options.helper: a creature the
--- caller spawned, in place of one spawned here (GK.Wake.HelperTemplate at the point; an origin, a
--- player's character and a creature of the level's own cast are refused). Tracked by the stage's
--- ledger; a row `wake_place`. Observed: { ok, helper, at, hidden, seen, radius, count, woke =
--- { { who, name, distance, drawn } }, wokeCount, drawnCount, alreadyAwake, asleep, ticks,
--- wakeTicks, clientTicks } once the creatures within the manager's waking distance of the helper
--- are active and the client holds whole those it is sent: the entity `at` itself when it is a
--- creature, and the others once their number stood still for five client ticks. `hidden` is
--- whether the helper is off stage and never drawn; `seen` says what a watcher could see of it.
--- A wait that ran out says which and after how many ticks, and the place is let go again.
function Wake.Place(at, options)
    options = options or {}
    local point, target = pointOf(at), nil
    if not point then
        target = short(at)
        point = type(target) == "string" and position(target) or nil
        if not point then return { ok = false, error = "give a point {x, y, z} or an entity's uuid: " .. tostring(at) } end
    end
    local radii = Wake.Radii()
    local helper, spawned = nil, false
    if options.helper then
        helper = short(options.helper)
        local why = notAHelper(helper)
        if why then return { ok = false, error = "refused as a helper: " .. why } end
    else
        local ok, made = pcall(GK.Stage.Spawn, { template = options.template or Wake.HelperTemplate, at = point, faction = "neutral" })
        if not ok or not (made and made[1]) then
            return { ok = false, error = "no helper could be spawned at the point: " .. tostring(made) }
        end
        helper, spawned = short(made[1]), true
    end
    local before = {}
    for _, c in ipairs(around(point, radii.wake)) do before[c.who] = c.awake end
    before[helper] = nil
    local place = { at = point, spawned = spawned, hidden = false, offStage = false }
    local stage, since, follow = "born", 0, nil
    local wakeTicks, lastCount, stood = nil, -1, 0
    local found

    local function letGo()
        local row = trackRow("wake:place:" .. helper)
        if row then GK.Stage.UndoTrack(row) else dropPlace(helper) end
        if spawned then
            local born = spawnRow(helper)
            if born then GK.Stage.UndoTrack(born) end
        end
    end
    local function fail(ticks, why, extra)
        letGo()
        local out = { ok = false, helper = helper, at = point, ticks = ticks, error = why }
        for k, v in pairs(extra or {}) do out[k] = v end
        return out
    end

    return GK.Observe({ ok = true, helper = helper, at = point, waiting = "the place awake, whole on the client" }, function(ticks, final)
        if stage == "born" then
            if ticks < 3 then return nil end
            if not Ext.Entity.Get(helper) then return fail(ticks, "the helper " .. helper .. " is gone") end
            -- a newborn is active until the manager has judged it: one beyond every anchor's
            -- sleeping distance is waited for until it sleeps
            local _, reach = nearest(point)
            local beyond = reach == nil or reach > radii.sleep
            if beyond and awake(helper) and ticks < GK.LimitTicks(Wake.WakeMs) and not final then return nil end
            if awake(helper) then
                place.hidden = false
                place.why = beyond and ("it did not fall asleep in " .. ticks .. " ticks") or "it stands within the party's reach, awake"
                stage, since = "anchor", ticks
            else
                try(Osi.SetOnStage, helper, 0)
                place.hidden, place.offStage = true, true
                stage, since = "offstage", ticks
            end
            return nil
        end
        if stage == "offstage" then
            if try(Osi.IsOnStage, helper) == 0 and ticks - since >= 2 then
                stage, since = "anchor", ticks
            elseif ticks - since >= GK.LimitTicks(Wake.WakeMs) or final then
                return fail(ticks, "the helper did not leave the stage in " .. (ticks - since) .. " ticks")
            else
                return nil
            end
        end
        if stage == "anchor" then
            local ok, problem = pcall(anchor, helper)
            if not ok then return fail(ticks, "the helper could not join the party's view: " .. tostring(problem)) end
            Wake.Places[helper] = place
            place.row = GK.Stage.Track{ kind = "other", key = "wake:place:" .. helper, entity = helper,
                note = "a helper in the party's view at " .. string.format("%.1f, %.1f, %.1f", point[1], point[2], point[3]),
                undo = function() dropPlace(helper) return true end }
            stage, since = "waking", ticks
            return nil
        end
        if stage == "waking" then
            found = around(point, radii.wake)
            local count, targetAwake = 0, target == nil or not isCharacter(target)
            for _, c in ipairs(found) do
                if c.awake then count = count + 1 end
                if c.who == target and c.awake then targetAwake = true end
            end
            if count == lastCount then stood = stood + 1 else lastCount, stood = count, 0 end
            if targetAwake and stood >= 3 then
                wakeTicks = ticks - since
                local must, also, kinds = {}, {}, {}
                for _, c in ipairs(found) do
                    if c.awake and c.who ~= helper then
                        if c.who == target then must[#must + 1] = c.who else also[#also + 1] = c.who end
                    end
                end
                stage, follow = "client", GK.Follow(askClient({ op = "whole", must = must, also = also, kinds = kinds, wait = true,
                    within = Wake.WholeMs }, Wake.WholeMs + 2000))
            elseif ticks - since >= GK.LimitTicks(Wake.WakeMs) or final then
                return fail(ticks, (target and not targetAwake) and (target .. " did not wake in " .. (ticks - since) .. " ticks after the helper joined the party's view")
                    or ("the number of creatures awake did not stand still in " .. (ticks - since) .. " ticks"), { awake = count })
            end
            return nil
        end
        local answer = follow()
        if answer == nil then
            if final then return fail(ticks, "the client did not answer in " .. ticks .. " server ticks") end
            return nil
        end
        if not answer.ok then
            return fail(ticks, "awake on the server after " .. wakeTicks .. " ticks, " .. tostring(answer.error),
                { wakeTicks = wakeTicks, clientTicks = answer.ticks })
        end
        local woke, asleep, already, drawn = {}, {}, 0, 0
        for _, c in ipairs(found) do
            if c.who ~= helper then
                if not c.awake then
                    asleep[#asleep + 1] = { who = c.who, name = nameOf(c.who), distance = round(c.distance) }
                elseif before[c.who] then
                    already = already + 1
                else
                    local whole = answer.whole and answer.whole[c.who] == true
                    if whole then drawn = drawn + 1 end
                    woke[#woke + 1] = { who = c.who, name = nameOf(c.who), distance = round(c.distance), drawn = whole }
                end
            end
        end
        local out = { ok = true, helper = helper, at = point, hidden = place.hidden, radius = radii.wake, count = #found,
            woke = woke, wokeCount = #woke, drawnCount = drawn, alreadyAwake = already, asleep = asleep,
            ticks = ticks, wakeTicks = wakeTicks, clientTicks = answer.ticks,
            seen = place.hidden and "nothing: the helper is off stage, a stub on the client, never drawn"
                or ("the helper stands there as a plain figure: " .. tostring(place.why)) }
        log("wake_place", { helper = helper, at = point, hidden = place.hidden, woke = #woke, drawn = drawn,
            alreadyAwake = already, ticks = ticks })
        return out
    end, 3 * Wake.WakeMs + Wake.WholeMs + 6000)
end

--- Lets go what GK.Wake holds: `handle` is a creature kept by GK.Wake.One, a helper of GK.Wake.Place
--- (the `helper` of its answer), or nil for all of them. The undo is done at once (the force update
--- off, the helper out of the party's view and deleted); a row `wake_released`. Observed:
--- { ok, released, slept = { { who, name } }, sleptCount, stayed = { { who, name, why } }, ticks }
--- once every creature it kept awake that no other anchor reaches (farther than the manager's
--- sleeping distance from every character of the party's view, and not kept by One) has lost
--- Active on the server; `stayed` are those another anchor keeps. The client is not waited for.
function Wake.Release(handle)
    local ones, places = {}, {}
    if handle == nil then
        for who in pairs(Wake.Kept) do ones[#ones + 1] = who end
        for helper in pairs(Wake.Places) do places[#places + 1] = helper end
    else
        handle = short(handle)
        if Wake.Kept[handle] then ones[#ones + 1] = handle end
        if Wake.Places[handle] then places[#places + 1] = handle end
        if #ones + #places == 0 then
            return { ok = false, error = "GK.Wake holds nothing by " .. tostring(handle) .. ": give a creature kept by GK.Wake.One or the helper of GK.Wake.Place" }
        end
    end
    local radii = Wake.Radii()
    -- who they keep awake now
    local kept = {}
    for _, who in ipairs(ones) do if awake(who) then kept[who] = true end end
    for _, helper in ipairs(places) do
        local place = Wake.Places[helper]
        for _, c in ipairs(around(place.at, radii.sleep)) do
            if c.awake then kept[c.who] = true end
        end
    end
    -- the undo
    for _, who in ipairs(ones) do
        local row = Wake.Kept[who] and Wake.Kept[who].row
        if row then GK.Stage.UndoTrack(row) end
        Wake.Kept[who] = nil
        try(Osi.SetForceUpdate, who, 0)
    end
    for _, helper in ipairs(places) do
        local place = Wake.Places[helper]
        local row = trackRow("wake:place:" .. helper)
        if row then GK.Stage.UndoTrack(row) else dropPlace(helper) end
        if place.spawned then
            local born = spawnRow(helper)
            if born then GK.Stage.UndoTrack(born) elseif Ext.Entity.Get(helper) then GK.Stage.Remove(helper) end
        end
        kept[helper] = nil
    end
    -- who of them is expected to sleep: nobody else reaches it
    local expect, stayed = {}, {}
    for who in pairs(kept) do
        local at = position(who)
        local a, d = nil, nil
        if at then a, d = nearest(at) end
        if Wake.Kept[who] then
            stayed[#stayed + 1] = { who = who, name = nameOf(who), why = "kept up by GK.Wake.One" }
        elseif a and d <= radii.sleep then
            stayed[#stayed + 1] = { who = who, name = nameOf(who), why = string.format("%.1f m from %s of the party's view", d, a.who) }
        else
            expect[#expect + 1] = who
        end
    end
    local released = {}
    for _, who in ipairs(ones) do released[#released + 1] = who end
    for _, helper in ipairs(places) do released[#released + 1] = helper end
    return GK.Observe({ ok = true, released = released, waiting = "asleep on the server" }, function(ticks, final)
        local still = {}
        for _, who in ipairs(expect) do
            if awake(who) then still[#still + 1] = who end
        end
        if #still > 0 and not final then return nil end
        local slept = {}
        for _, who in ipairs(expect) do
            if not awake(who) then slept[#slept + 1] = { who = who, name = nameOf(who) } end
        end
        local out = { ok = #still == 0, released = released, slept = slept, sleptCount = #slept, stayed = stayed, ticks = ticks }
        if #still > 0 then
            out.stillAwake = still
            out.error = "still active on the server " .. ticks .. " ticks after the release: " .. table.concat(still, ", ")
        end
        log("wake_released", { released = released, slept = #slept, stayed = #stayed, ok = out.ok, ticks = ticks })
        return out
    end, Wake.SleepMs)
end

--- Whether a thing is awake and drawn, and what keeps it so. `at` is an entity's uuid or a point
--- {x, y, z}. Of an entity: { ok, who, name, character, awake, whole, drawn, rule, anchor,
--- anchorKind, distance, radii } - `awake`: it has Active on the server; `whole`: the client holds
--- it with Level and GameObjectVisual; `drawn`: of a creature the same as `whole`, of an item not
--- judged (an item's stub is drawn all the same); `distance` to `anchor`, the nearest character of
--- the party's view (`anchorKind` "party", or "place" for a helper of GK.Wake.Place); `rule`: what
--- keeps it awake - "kept up" (GK.Wake.One), "party" or "place" (awake within the manager's
--- sleeping distance of that anchor), "none" (asleep, or awake by something the toolkit does not
--- read: the story's own force update, a running timeline). Of a point: { ok, at, anchor,
--- anchorKind, distance, rule, characters, awake, whole, radii } with the creatures within the
--- waking distance of the point counted. The client is asked; observed. Observation.
function Wake.State(at)
    local radii = Wake.Radii()
    local point = pointOf(at)
    local out, question
    if point then
        local a, d = nearest(point)
        local found = around(point, radii.wake)
        local list, count = {}, 0
        for _, c in ipairs(found) do
            list[#list + 1] = c.who
            if c.awake then count = count + 1 end
        end
        out = { ok = true, at = point, anchor = a and a.who, anchorKind = a and a.kind, distance = round(d),
            rule = (a and d <= radii.wake) and a.kind or "none", characters = #found, awake = count,
            radii = { wake = radii.wake, sleep = radii.sleep, source = radii.source } }
        question = { op = "whole", also = list }
    else
        local who = short(at)
        local here = type(who) == "string" and position(who) or nil
        if not here then return { ok = false, error = "give an entity's uuid or a point {x, y, z}: " .. tostring(at) } end
        local a, d = nearest(here, who)
        local isAwake = awake(who)
        local rule = "none"
        if Wake.Kept[who] then
            rule = "kept up"
        elseif Wake.Places[who] or (function() for _, x in ipairs(anchors()) do if x.who == who then return true end end end)() then
            rule = Wake.Places[who] and "place" or "party"
        elseif isAwake and a and d <= radii.sleep then
            rule = a.kind
        end
        out = { ok = true, who = who, name = nameOf(who), character = isCharacter(who), awake = isAwake, rule = rule,
            anchor = a and a.who, anchorKind = a and a.kind, distance = round(d),
            radii = { wake = radii.wake, sleep = radii.sleep, source = radii.source } }
        question = { op = "whole", must = { who } }
    end
    local follow = GK.Follow(askClient(question))
    return GK.Observe(out, function(ticks, final)
        local answer = follow()
        if answer == nil then
            if not final then return nil end
            answer = { ok = false, error = "the client did not answer in " .. ticks .. " server ticks" }
        end
        out.ticks = ticks
        if not answer.ok then
            out.ok, out.error = false, "the server's half is read, the client's is not: " .. tostring(answer.error)
            return out
        end
        if out.who then
            out.whole = answer.whole and answer.whole[out.who] == true
            if out.character then out.drawn = out.whole end
        else
            out.whole = answer.count or 0
        end
        return out
    end, 4000)
end

return Wake
