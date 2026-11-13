-- GK.Act: what a player can do, and nothing more. Server side.
--
-- Every command is something the player could do with the mouse and the hotbar, under the same
-- rules: the character walks (no teleport), a target must be perceived by the party (GK.See), a
-- fight allows a character to act only on its own turn, attacks and spells roll their dice and
-- spend their action, bonus action, movement and spell slot. Anything that bypasses the rules
-- (teleport, kill, heal, add items or gold, unlock without a key, start a dialogue from afar) is
-- not here: it is GK.Cheat (Server/Cheat.lua), for setting up tests only.
--
-- Commands start a job and return at once: { job = n, ... } or { ok = false, error = "..." } when
-- a player could not do it (out of range, not your turn, no path, no action left - the reason a
-- player would see). GK.Act.Job(n) tells how it went: { status = "running" | "done" | "failed",
-- result, error, seconds }. The Python side (stand/situation.py) waits for you.
--
--   GK.Act.Move(who, to, options)        walk to a point {x,y,z} or next to an object (uuid); in a
--                                        fight within the movement left (options.partial = walk
--                                        as far as it goes); options.speed "Run" | "Walk" | "Sprint"
--   GK.Act.Cast(who, spell, target, options)   a spell or action from the character's spell book
--                                        at a uuid or {x,y,z}; options.upcast = slot level,
--                                        options.target2 (a second target), options.walk = walk
--                                        into range first (out of combat)
--   GK.Act.Attack(who, target)           the main-hand weapon attack (melee or ranged by weapon)
--   GK.Act.Jump(who, point) / Shove(who, target) / Hide(who) / Dash(who) / Disengage(who) /
--   GK.Act.Throw(who, item, target)      the common actions, as casts
--   GK.Act.Use(who, item)                walk up and use: open/close a door or chest, pull a
--                                        lever, climb a ladder, read, drink
--   GK.Act.PickUp(who, item)             walk up and take it
--   GK.Act.Loot(who, corpse)             walk up and open its loot window
--   GK.Act.Take(who, container, item)    take one item (or all, item = nil) from an open container
--   GK.Act.Talk(who, npc)                walk up and ask to talk, as clicking on the NPC does
--   GK.Act.EndTurn(who)                  end the character's turn
--   GK.Act.TurnBased(on, who)            the turn-based mode button (also outside a fight)
--   GK.Act.Sneak(on, who)                hide / stop hiding
--   GK.Act.Equip(who, item) / Unequip(who, item)
--   GK.Act.Stop(who)                     stops what the character is doing (walk, cast queue)
--   GK.Act.Job(n) / GK.Act.Jobs()        a job's state / the recent ones
--
-- Dialogue answers, dice rolls, reactions, rests and level-ups are the UI's: GK.Dialog/GK.Answer,
-- GK.Blocking/GK.RollNext, GK.Gui (client side; stand/situation.py wraps them).
--
-- How the rules are kept (bg3-agent-harness found these the hard way, re-checked live here):
--   * Osiris walks (CharacterMoveTo) teleport when there is no path; so a walk goes to a point the
--     pathfinder proved reachable, with CharacterMoveToPosition, and a jump of more than 3 m in a
--     tick marks the job `teleported`.
--   * Osiris casts carry IgnoreSpellRolls/IgnoreCastChecks/IgnoreTargetChecks; the cast's state is
--     caught when it is created and those flags are taken off, so the attack rolls against AC and
--     the target rolls its save.
--   * Whatever an Osiris walk, cast or use did not charge (movement, action, bonus action, slot) is
--     charged after it, as the game charges a player.

GK.Act = GK.Act or {}
local Act = GK.Act
local See = GK.See

local function try(fn, ...)
    local ok, a, b, c = pcall(fn, ...)
    if ok then return a, b, c end
    return nil
end

local function bool(v) return v == 1 or v == true end
local function now() return Ext.Utils.MonotonicTime() / 1000 end

local function entityOf(id) return type(id) == "string" and try(Ext.Entity.Get, id) or id end
local function posOf(id) return See.Position(id) end
local function dist(a, b)
    local dx, dy, dz = a[1] - b[1], a[2] - b[2], a[3] - b[3]
    return math.sqrt(dx * dx + dy * dy + dz * dz)
end
local function flat(a, b)
    local dx, dz = a[1] - b[1], a[3] - b[3]
    return math.sqrt(dx * dx + dz * dz)
end

local function fail(message, extra)
    local out = { ok = false, error = message }
    for k, v in pairs(extra or {}) do out[k] = v end
    return out
end

-- ---------------------------------------------------------------------------------------
-- Jobs

Act.JobList = Act.JobList or {}
Act.NextJob = Act.NextJob or 1
Act.Reach = 1.6        -- metres from an object's centre a character interacts from (use, pick up)
Act.TalkReach = 2.5

local function newJob(kind, who, data)
    local n = Act.NextJob
    Act.NextJob = n + 1
    local job = { n = n, kind = kind, who = who, status = "running", started = now(), data = data or {} }
    Act.JobList[n] = job
    -- keep the last 50
    if Act.JobList[n - 50] then Act.JobList[n - 50] = nil end
    -- whoever watches sees what the job aims at (GK.Focus: the game's own rings and beams)
    if GK.Focus and GK.Focus.JobStarted then try(GK.Focus.JobStarted, job) end
    return job
end

local function finish(job, status, result, err)
    if job.status ~= "running" then return end
    job.status, job.result, job.error = status, result, err
    job.seconds = math.floor((now() - job.started) * 10 + 0.5) / 10
    if job.onFinish then try(job.onFinish, job) end
    if GK.Focus and GK.Focus.JobFinished then try(GK.Focus.JobFinished, job) end
    GK.Note("Act" .. job.kind, { job = job.n, status = status, error = err })
end

local function view(job)
    if not job then return nil end
    return { job = job.n, kind = job.kind, who = job.who, status = job.status, result = job.result, error = job.error,
             seconds = job.seconds or math.floor((now() - job.started) * 10 + 0.5) / 10, step = job.step,
             -- how much of those seconds was spent waiting on the engine rather than on us
             waited = job.held and math.floor(job.held * 10 + 0.5) / 10 or nil }
end

function Act.Job(n) return view(Act.JobList[n]) or fail("no such job") end

function Act.Jobs()
    local out = {}
    for n = Act.NextJob - 1, math.max(1, Act.NextJob - 10), -1 do
        if Act.JobList[n] then out[#out + 1] = view(Act.JobList[n]) end
    end
    return out
end

-- A job made of steps: each step is function(job) -> nil (wait), true (next step), or false, msg (fail).
-- The ticker calls the current step every 100 ms until it moves on.
local function stepJob(job)
    local step = job.steps and job.steps[job.stepIndex]
    if not step then return end
    local ok, a, b = pcall(step, job)
    if not ok then return finish(job, "failed", nil, "error: " .. tostring(a)) end
    if a == true then
        job.stepIndex = job.stepIndex + 1
        job.stepStarted = now()
        if job.stepIndex > #job.steps then finish(job, "done", job.result) end
    elseif a == false then
        finish(job, "failed", job.result, b)
    end
end

local function tick()
    for _, job in pairs(Act.JobList) do
        if job.status == "running" then
            -- `held` is the time the engine was plainly still working (a dice window waiting for
            -- an answer, say). It does not count against the deadline: a job that is being waited
            -- on has not timed out, and saying it has would be a lie about the world.
            if job.timeout and now() - job.started - (job.held or 0) > job.timeout then
                if job.onTimeout then try(job.onTimeout, job) end
                finish(job, "failed", job.result, job.timeoutMessage or "timed out")
            elseif job.steps then
                stepJob(job)
            end
        end
    end
end

-- late-bound: a reloaded Act.lua takes over the running ticker
Act.Tick = tick

local function startTicker()
    if Act.Ticker then return end
    Act.Ticker = Ext.Timer.WaitFor(100, function() Act.Tick() end, 100)
end

-- ---------------------------------------------------------------------------------------
-- Who may act

local function inCombat(who) return bool(try(Osi.IsInCombat, who)) end

local function inDialog(who)
    local e = entityOf(who)
    return bool(try(function() return e.ServerCharacter.InDialog end))
end

--- Whether a character ended its turn this round through GK.Act.EndTurn (the engine does not show
--- it while the rest of a party turn group still acts).
function Act.HasEnded(who)
    local ended = Act.Ended and Act.Ended[who]
    if not ended then return false end
    local e = entityOf(who)
    local round = try(function() return e.CombatParticipant.CombatHandle.TurnOrder.Round end)
    local tb = e and try(function() return e.TurnBased end)
    if round ~= ended.round or not (tb and tb.IsActiveCombatTurn) then
        Act.Ended[who] = nil
        return false
    end
    return true
end

local function hisTurn(who)
    local e = entityOf(who)
    local tb = e and try(function() return e.TurnBased end)
    return tb and tb.IsActiveCombatTurn and not tb.RequestedEndTurn and not Act.HasEnded(who)
end

--- Why `who` cannot act now (nil = it can): not a party member, dead, in a dialogue, or - in a
--- fight - not its turn.
function Act.CannotAct(who, anyTime)
    if not who then return "nobody" end
    local e = entityOf(who)
    if not e then return "no such character" end
    if not See.IsMember(who) then return "not a party member" end
    if bool(try(Osi.IsDead, who)) then return "dead" end
    local hp = try(function() return e.Health.Hp end)
    if hp == 0 then return "downed" end
    if inDialog(who) then return "in a dialogue" end
    -- a player commands the character it has selected (a click on the portrait first: Act.Select)
    if Act.SelectedOnly and not bool(try(Osi.IsControlled, who)) then return "not selected (select it first)" end
    if not anyTime and inCombat(who) and not hisTurn(who) then return "not its turn" end
    return nil
end

--- Commands go to the selected character only (as for a player), unless this is turned off.
Act.SelectedOnly = Act.SelectedOnly ~= false

--- The party member the player controls now.
function Act.Selected()
    for _, m in ipairs(See.Members()) do
        if bool(try(Osi.IsControlled, m)) then return m end
    end
    return Osi.GetHostCharacter()
end

local function resolveWho(who) return who or Act.Selected() end

local function needPerceived(target)
    if type(target) ~= "string" then return nil end
    if See.IsMember(target) then return nil end
    if not try(Ext.Entity.Get, target) then return "no such object" end
    if See.PartyPerceives(target) then return nil end
    -- seen earlier on this level: a player clicks where it was (GK.See scope "known")
    if See.Remembers(target) then return nil end
    return "the party does not see it (and has not seen it)"
end

-- ---------------------------------------------------------------------------------------
-- Resources: what the game charges a player

local RESOURCE_IDS = {}
local function resourceId(name)
    if RESOURCE_IDS[name] then return RESOURCE_IDS[name] end
    for _, guid in ipairs(Ext.StaticData.GetAll("ActionResource")) do
        local def = Ext.StaticData.Get(guid, "ActionResource")
        if def and def.Name then RESOURCE_IDS[def.Name] = guid end
    end
    return RESOURCE_IDS[name]
end

--- Takes `amount` of a resource (by name: "Movement", "ActionPoint", "SpellSlot" with `level`).
--- Returns how much was taken.
function Act.Charge(who, name, amount, level)
    local e = entityOf(who)
    local guid = resourceId(name)
    local list = e and guid and try(function() return e.ActionResources.Resources[guid] end)
    if not list then return 0 end
    local taken = 0
    for _, r in ipairs(list) do
        if level == nil or r.ResourceId == level or r.Level == level then
            local t = math.min(r.Amount, amount - taken)
            if t > 0 then
                r.Amount = r.Amount - t
                taken = taken + t
            end
        end
        if taken >= amount then break end
    end
    if taken > 0 then e:Replicate("ActionResources") end
    return taken
end

local function snapshot(who)
    local r = See.Resources(who)
    local out = { action = r.action and r.action[1], bonus = r.bonus and r.bonus[1], reaction = r.reaction and r.reaction[1],
                  movement = r.movement and r.movement[1], slots = {} }
    for level, pair in pairs(r.slots or {}) do out.slots[level] = pair[1] end
    return out
end

-- ---------------------------------------------------------------------------------------
-- Walking

--- A point next to an object that the character can walk to: rings of 1.0, 1.5 and 2.2 metres
--- around it, the side facing the walker first, each point proven by the pathfinder.
function Act.ApproachPoint(who, target, within, loose)
    local tp, mp = posOf(target), posOf(who)
    if not (tp and mp) then return nil end
    within = within or Act.Reach
    if flat(tp, mp) <= within + 0.6 and math.abs(tp[2] - mp[2]) < 2 then return mp, 0 end
    -- Candidates: where a path towards the object itself ends (a click on the object: the
    -- pathfinder stops as close as the ground allows), and rings of points around it. The best is
    -- the shortest walk that ends on the walker's own floor: a corpse on a table must not be
    -- approached by climbing onto the table (seen 2026-11-13: the character ended on the table,
    -- on an island the pathfinder could not leave - a jump got it down).
    local candidates = {}
    local function consider(pos, length)
        if not pos then return end
        local climb = math.max(0, math.abs(pos[2] - mp[2]) - 0.5)
        candidates[#candidates + 1] = { pos = pos, length = length or 0, score = (length or 0) + 8 * climb }
    end
    local direct = See.Path(who, tp)
    if direct.ends and flat(direct.ends, tp) <= within + 0.9 and math.abs(direct.ends[2] - tp[2]) < 2.5 then
        consider(direct.ends, direct.length)
    end
    local base = math.atan(mp[1] - tp[1], mp[3] - tp[3])
    -- a door stands in a wall: its rings go wider; `loose` (a curious click on anything) goes
    -- wider still and lets the game decide whether it is within reach
    local e = entityOf(target)
    local wide = e and (try(function() return e.IsDoor end) ~= nil or try(function() return e.Door end) ~= nil)
    local radii = wide and { 1.2, 1.8, 2.5 } or { 1.0, 1.5, 2.2 }
    if loose then radii = { 1.0, 1.5, 2.2, 3.0, 4.0 } end
    for _, r in ipairs(radii) do
        local found = 0
        for k = 0, 7 do
            local a = base + (k % 2 == 0 and 1 or -1) * math.ceil(k / 2) * math.pi / 4
            local x, z = tp[1] + math.sin(a) * r, tp[3] + math.cos(a) * r
            local heights = try(Ext.Level.GetHeightsAt, x, z) or {}
            for _, y in ipairs(heights) do
                if math.abs(y - tp[2]) < 3 then
                    local p = See.Path(who, { x, y, z })
                    if p.reachable and p.ends and flat(p.ends, { x, y, z }) < 1.3 and flat(p.ends, tp) <= math.max(within + 1.2, r + 0.8) then
                        consider(p.ends, p.length)
                        found = found + 1
                    end
                end
            end
            if found >= 3 then break end
        end
        if #candidates >= 3 then break end
    end
    if #candidates == 0 then return nil end
    table.sort(candidates, function(a, b) return a.score < b.score end)
    return candidates[1].pos, candidates[1].length
end

-- A walk step: starts CharacterMoveToPosition, ends on arrival (the Osiris event), cancel or
-- stillness; watches for teleports; charges the movement the engine did not.
local function walkSteps(job, who, goal, length, options)
    options = options or {}
    local fighting = inCombat(who)
    local before = fighting and snapshot(who) or nil
    local event = "GK_Act_" .. job.n
    job.event = event
    return function(j)
        if not j.walking then
            j.walking = true
            j.walkStart = now()
            j.last = posOf(who)
            j.walked = 0
            j.stillSince = nil
            Osi.CharacterMoveToPosition(who, goal[1], goal[2], goal[3], options.speed or "Run", event, j.n)
            return nil
        end
        local p = posOf(who)
        if p and j.last then
            local step = dist(p, j.last)
            if step > 3.0 then j.teleported = true end
            j.walked = j.walked + step
            if step < 0.02 then j.stillSince = j.stillSince or now() else j.stillSince = nil end
            j.last = p
        end
        local arrived = j.arrived or (p and dist(p, goal) < 0.6 and j.stillSince and now() - j.stillSince > 0.2)
        local stuck = j.stillSince and now() - j.stillSince > 2.5 and now() - j.walkStart > 1.0
        if j.cancelled or arrived or stuck then
            if fighting and before and before.movement then
                local after = snapshot(who).movement or before.movement
                local charged = before.movement - after
                local owed = math.min(j.walked, length or j.walked) - charged
                if owed > 0.2 then
                    j.chargedByAct = (j.chargedByAct or 0) + Act.Charge(who, "Movement", owed)
                end
            end
            j.result = { pos = p and { math.floor(p[1] * 10 + 0.5) / 10, math.floor(p[2] * 10 + 0.5) / 10, math.floor(p[3] * 10 + 0.5) / 10 }, walked = math.floor(j.walked * 10 + 0.5) / 10, teleported = j.teleported,
                         movementLeft = fighting and snapshot(who).movement or nil, chargedByAct = j.chargedByAct }
            if j.teleported then return false, "the game moved the character by itself (teleport)" end
            if arrived then return true end
            if j.cancelled then return false, "the walk was cancelled" end
            if p and dist(p, goal) < 1.5 then return true end
            return false, "stopped on the way (" .. string.format("%.1f", dist(p, goal)) .. " m short)"
        end
        return nil
    end
end

-- ---------------------------------------------------------------------------------------
-- When walking does not get there

--- For a goal the character cannot walk to: the nearest point it can walk to (sampled along the
--- line back from the goal and around it), how far the goal is from there, and the ways the
--- character has to cross the rest - jumps and teleports from its spell book, ready now, with
--- their range. The agent decides: walk to `nearest`, then `jump` / `cast <teleport>` to the goal.
function Act.Alternatives(who, goal)
    who = resolveWho(who)
    local from = posOf(who)
    if not (from and goal) then return {} end
    local out = { goal = goal }
    local best
    local dx, dz = from[1] - goal[1], from[3] - goal[3]
    local len = math.sqrt(dx * dx + dz * dz)
    local tries = 0
    local deadline = Ext.Utils.MonotonicTime() + 1500   -- failed searches are slow: at most 1.5 s
    for r = 1, math.min(len, 20), 1 do
        if Ext.Utils.MonotonicTime() > deadline then out.partial = true break end
        for _, a in ipairs({ 0, 0.5, -0.5, 1.0, -1.0 }) do
            local ux, uz = dx / len, dz / len
            local ca, sa = math.cos(a), math.sin(a)
            local x = goal[1] + (ux * ca - uz * sa) * r
            local z = goal[3] + (ux * sa + uz * ca) * r
            for _, y in ipairs(try(Ext.Level.GetHeightsAt, x, z) or {}) do
                if math.abs(y - goal[2]) < 8 and tries < 24 and Ext.Utils.MonotonicTime() <= deadline then
                    tries = tries + 1
                    local p = See.Path(who, { x, y, z })
                    if p.reachable and p.ends and flat(p.ends, { x, y, z }) < 1.5 then
                        best = { pos = p.ends, walk = p.length, gap = math.floor(dist(p.ends, goal) * 10 + 0.5) / 10,
                                 dy = math.floor((goal[2] - p.ends[2]) * 10 + 0.5) / 10 }
                        break
                    end
                end
            end
            if best then break end
        end
        if best then break end
    end
    out.nearest = best
    local ways = {}
    for _, row in ipairs(See.Actions(who, nil, { all = true })) do
        local stat = try(Ext.Stats.Get, row.id)
        local kind = stat and try(function() return stat.SpellType end)
        local isJump = row.id:find("Jump") ~= nil
        if isJump or kind == "Teleportation" then
            ways[#ways + 1] = { id = row.id, name = row.name, range = row.range, ready = row.ready, why = row.why,
                                kind = isJump and "jump" or "teleport" }
        end
    end
    out.ways = ways
    return out
end

-- ---------------------------------------------------------------------------------------
-- Getting unstuck

local function walkableTile(x, y, z)
    local heights = try(Ext.Level.GetHeightsAt, x, z) or {}
    local best
    for _, h in ipairs(heights) do
        if math.abs(h - y) < 1.5 and (not best or math.abs(h - y) < math.abs(best - y)) then best = h end
    end
    if not best then return nil end
    local tile = try(Ext.Level.GetTileDebugInfo, { x, best, z })
    for _, f in ipairs(tile and try(function() return tile.Flags end) or {}) do
        if tostring(f):find("WalkBlock") then return nil end
    end
    return best
end

--- Whether the pathfinder can start from where the character stands: a character standing on a
--- blocked tile (seen: pushed against a table while looting) finds no path to anywhere.
Act.StuckCache = Act.StuckCache or {}
function Act.Stuck(who)
    local p = posOf(who)
    if not p then return false end
    -- one answer per place: the same spot gives the same answer, and failed searches are slow
    local key = string.format("%s|%.0f|%.0f|%.0f", who, p[1] * 2, p[2] * 2, p[3] * 2)
    local cached = Act.StuckCache[key]
    if cached ~= nil then return cached end
    local answer = Act.StuckNow(who, p)
    Act.StuckCache = { [key] = answer }   -- remember only the latest place
    return answer
end

function Act.StuckNow(who, p)
    for k = 0, 3 do
        local a = k * math.pi / 2
        local x, z = p[1] + math.sin(a) * 1.2, p[3] + math.cos(a) * 1.2
        local y = walkableTile(x, p[2], z)
        if y then
            local path = See.Path(who, { x, y, z })
            if path.reachable then return false end
        end
    end
    return true
end

--- A short step (at most 3 m) onto the nearest free tile, without a proven path - what a player's
--- click next to the character does. Used only when the character is stuck (Act.Stuck).
function Act.Unstick(who)
    who = resolveWho(who)
    local p = posOf(who)
    if not p then return fail("no position") end
    for _, r in ipairs({ 0.8, 1.4, 2.0, 2.8 }) do
        for k = 0, 11 do
            local a = k * math.pi / 6
            local x, z = p[1] + math.sin(a) * r, p[3] + math.cos(a) * r
            local y = walkableTile(x, p[2], z)
            if y then
                local job = newJob("Move", who, { to = { x, y, z }, unstick = true })
                job.steps = { walkSteps(job, who, { x, y, z }, r) }
                job.stepIndex = 1
                job.timeout = 8
                startTicker()
                return { job = job.n, to = { x, y, z } }
            end
        end
    end
    return fail("no free tile within 3 m")
end

--- Where a walk can go, checked as a player's click would be: a point is walked to if the
--- pathfinder reaches it; an object is approached. In a fight the path must fit the movement
--- left, or with options.partial the walk stops where the movement ends.
local function planWalk(who, to, options)
    options = options or {}
    local goal, length
    if type(to) == "string" then
        local why = needPerceived(to)
        if why then return nil, why end
        goal, length = Act.ApproachPoint(who, to, options.within)
        if not goal then return nil, "no path to it" end
        if length == 0 then return goal, 0, true end
    elseif type(to) == "table" and #to == 3 then
        goal = to
        local p = See.Path(who, goal)
        if not p.reachable or not p.ends or flat(p.ends, goal) > 3.0 then
            return nil, "no path there" .. (p.ends and " (the path ends " .. string.format("%.1f", flat(p.ends, goal)) .. " m away)" or "")
        end
        -- a click on the ground lands on the nearest walkable spot: walk to where the path ends
        goal = p.ends
        length = p.length
    else
        return nil, "a point {x,y,z} or an object's uuid"
    end
    if inCombat(who) then
        local left = (See.Resources(who).movement or { 0 })[1]
        if length > left + 0.3 then
            if not options.partial then
                return nil, string.format("too far: %.1f m, %.1f m of movement left (add `partial` to walk the %.1f m you can)", length, left, left)
            end
            -- cut the path where the movement ends
            local entity = entityOf(who)
            local path = Ext.Level.BeginPathfindingImmediate(entity, goal)
            local cut
            if path and Ext.Level.FindPath(path) then
                local last, walked = posOf(who), 0
                for _, node in ipairs(path.Nodes) do
                    local q = node.Position
                    local seg = dist(last, q)
                    if walked + seg >= left - 0.1 then
                        local f = (left - 0.1 - walked) / seg
                        cut = { last[1] + (q[1] - last[1]) * f, last[2] + (q[2] - last[2]) * f, last[3] + (q[3] - last[3]) * f }
                        break
                    end
                    walked = walked + seg
                    last = q
                end
            end
            if path then Ext.Level.ReleasePath(path) end
            if not cut then return nil, "no movement left" end
            goal, length = cut, left - 0.1
        end
    end
    return goal, length
end

--- Who would get an attack of opportunity if `who` walked from where it stands to `goal`:
--- hostile characters the party perceives that stand within melee reach now and would not at the
--- goal, that are not incapacitated and have their reaction (as far as it can be read), unless
--- `who` has disengaged. Returns { names }, empty when the walk is safe.
Act.MeleeReach = 2.1   -- 1.5 m weapon reach measured edge to edge (two character radii)
function Act.Provokes(who, goal)
    local out = {}
    if not inCombat(who) then return out end
    local e = entityOf(who)
    local statuses = try(function() return e.StatusContainer.Statuses end) or {}
    for _, id in pairs(statuses) do
        if tostring(id):find("DISENGAGE") then return out end
    end
    local from = posOf(who)
    if not (from and goal) then return out end
    for _, c in ipairs(Ext.Entity.GetAllEntitiesWithComponent("ServerCharacter")) do
        local id = try(function() return c.Uuid.EntityUuid end)
        local p = id and posOf(c)
        if p and dist(p, from) <= Act.MeleeReach + 0.3 and dist(p, goal) > Act.MeleeReach
            and See.Relation(id) == "hostile" and not bool(try(Osi.IsDead, id)) and bool(try(Osi.IsInCombat, id))
            and See.PartyPerceives(id) then
            local reaction = (See.Resources(id).reaction or { 1 })[1]
            if reaction > 0 then out[#out + 1] = See.Name(id) or id end
        end
    end
    return out
end

--- Who would threaten a character standing at `goal`: perceived hostiles holding a melee weapon
--- (or fighting unarmed) within melee reach of that spot. Standing there means the Threatened
--- condition: ranged attacks and spells at disadvantage, and leaving later provokes. The player
--- sees this on the path preview; here it is a warning, not a refusal.
function Act.Threats(who, goal)
    local out = {}
    if not (goal and inCombat(who)) then return out end
    for _, c in ipairs(Ext.Entity.GetAllEntitiesWithComponent("ServerCharacter")) do
        local id = try(function() return c.Uuid.EntityUuid end)
        local p = id and posOf(c)
        if p and dist(p, goal) <= Act.MeleeReach + 0.3 and See.Relation(id) == "hostile"
            and not bool(try(Osi.IsDead, id)) and (try(function() return c.Health.Hp end) or 1) > 0
            and See.PartyPerceives(id) then
            local ranged = try(Osi.GetEquippedItem, id, "Ranged Main Weapon")
            local melee = try(Osi.GetEquippedItem, id, "Melee Main Weapon")
            local wielding = try(function() return tostring(c.WeaponSet.WeaponSet) end)
            -- a creature with a bow drawn does not threaten; one with a melee weapon or no weapon does
            if melee or not ranged or wielding == "Melee" then out[#out + 1] = See.Name(id) or id end
        end
    end
    return out
end

local function threatNote(who, goal)
    local threats = Act.Threats(who, goal)
    if #threats == 0 then return nil end
    return "you will be Threatened there by " .. table.concat(threats, ", ")
        .. " (ranged attacks and spells at disadvantage; leaving provokes an attack of opportunity)"
end
Act.ThreatNote = threatNote

function Act.Move(who, to, options)
    options = options or {}
    who = resolveWho(who)
    local why = Act.CannotAct(who)
    if why then return fail(why) end
    local goal, length, already = planWalk(who, to, options)
    if not goal then
        -- not walkable: say how close walking gets and what could cross the rest; with
        -- options.partial walk to that nearest point (a click towards an unreachable spot)
        local target = type(to) == "table" and to or posOf(to)
        local alt = target and Act.Alternatives(who, target) or nil
        if options.partial and alt and alt.nearest and alt.nearest.pos then
            local g2, l2 = planWalk(who, alt.nearest.pos, { partial = true })
            if g2 then
                goal, length = g2, l2
                options.note = string.format("the goal is not walkable; walking to the nearest point (%.1f m from it)", alt.nearest.gap or 0)
            end
        end
        if not goal then
            -- standing where no path starts (on a table, a corpse) looks like "no path" to anywhere:
            -- say so, the remedy is a jump or a short step (checked only here: path searches cost)
            local extra = alt and { alternatives = alt } or {}
            if Act.Stuck(who) then
                extra.stuck = "the character stands where no walk can start (on an object?): jump down or step aside"
            end
            return fail(length, extra)
        end
    end
    -- a player sees the warning on the path preview: here a walk that provokes is refused unless
    -- asked for (options.provoke); Disengage first makes it safe
    local provokes = Act.Provokes(who, goal)
    if #provokes > 0 and not options.provoke then
        return fail("leaving the reach of " .. table.concat(provokes, ", ") .. " provokes an attack of opportunity:"
            .. " Disengage first (bonus or action), or move with provoke = true", { provokes = provokes })
    end
    local job = newJob("Move", who, { to = to, goal = goal })
    if already then
        finish(job, "done", { pos = posOf(who), walked = 0 })
        return view(job)
    end
    job.steps = { walkSteps(job, who, goal, length, options) }
    job.stepIndex = 1
    job.timeout = 8 + (length or 10) / 1.5
    job.result = { length = length }
    job.warning = threatNote(who, goal)
    if options.note then job.warning = options.note .. (job.warning and ("; " .. job.warning) or "") end
    job.onFinish = function(j)
        if j.warning and type(j.result) == "table" then j.result.warning = j.warning end
    end
    startTicker()
    return { job = job.n, length = math.floor((length or 0) * 10 + 0.5) / 10, goal = goal, warning = job.warning }
end

-- ---------------------------------------------------------------------------------------
-- Casting

-- Casts this module ordered: caster uuid -> { spell, t }. The SpellCastState created for them has
-- its Ignore* options taken off.
Act.Ordered = Act.Ordered or {}
local STRIP = { IgnoreSpellRolls = true, IgnoreCastChecks = true, IgnoreTargetChecks = true }

local function stripCastOptions(entity)
    local state = try(function() return entity.SpellCastState end)
    if not state then return end
    local caster = try(function() return state.Caster.Uuid.EntityUuid end)
    local order = caster and Act.Ordered[caster]
    if not order or now() - order.t > 10 then return end
    local options = try(function() return state.CastOptions end)
    if type(options) ~= "table" and type(options) ~= "userdata" then return end
    local keep, stripped, preview = {}, {}, false
    for _, flag in pairs(options) do
        if flag == "IsPreview" then preview = true end
        if STRIP[flag] then stripped[#stripped + 1] = flag else keep[#keep + 1] = flag end
    end
    order.seen = keep; order.preview = preview
    if preview or #stripped == 0 then return end
    state.CastOptions = keep
    order.stripped = stripped
end

if not Act.CastHook then
    Act.CastHook = Ext.Entity.OnCreate("SpellCastState", function(entity) pcall(stripCastOptions, entity) end)
end

local function spellRow(who, spell)
    for _, row in ipairs(See.Actions(who, nil, { all = true })) do
        if row.id == spell then return row end
    end
    return nil
end

--- The stats name of `spell` cast with a slot of `level`: the variant "<spell>_<level>" whose
--- RootSpellID is the spell; nil when the spell has no such variant.
function Act.Upcast(spell, level)
    local name = spell .. "_" .. tostring(level)
    local stat = try(Ext.Stats.Get, name)
    if stat and (try(function() return stat.RootSpellID end) == spell) then return name end
    return nil
end

local MELEE_SLACK = 0.6   -- the two characters' radii: a 1.5 m reach is measured edge to edge

function Act.Cast(who, spell, target, options)
    options = options or {}
    who = resolveWho(who)
    local why = Act.CannotAct(who)
    if why then return fail(why) end
    local row = spellRow(who, spell)
    if not row then return fail("the character does not have " .. tostring(spell)) end
    if not row.ready then return fail(row.why or "not ready") end
    local castName = spell
    if options.upcast and options.upcast ~= row.level then
        if not row.costs.slot then return fail(spell .. " does not use a spell slot") end
        if options.upcast < (row.level or 0) then return fail("cannot cast below its own level") end
        local slots = See.Resources(who).slots or {}
        if not (slots[options.upcast] and slots[options.upcast][1] >= 1) then
            return fail("no spell slot of level " .. options.upcast .. " left")
        end
        castName = Act.Upcast(spell, options.upcast)
        if not castName then return fail(spell .. " has no level " .. options.upcast .. " version") end
    end
    target = target or who
    local tpos
    if type(target) == "string" then
        local bad = needPerceived(target)
        if bad then return fail(bad) end
        tpos = posOf(target)
    elseif type(target) == "table" then
        tpos = target
    end
    if not tpos then return fail("no target") end
    local mp = posOf(who)
    local d = target == who and 0 or dist(mp, tpos)
    local range = type(row.range) == "number" and row.range or nil
    local fighting = inCombat(who)
    local walkFirst
    if range and d > range + MELEE_SLACK then
        if options.walk then
            walkFirst = true
        else
            return fail(string.format("out of range: %.1f m, range %.1f m", d, range), { distance = d, range = range })
        end
    end
    if type(target) == "string" and target ~= who and not walkFirst then
        local los = bool(try(Osi.HasLineOfSight, who, target))
        if not los and not See.Clear({ mp[1], mp[2] + See.EyeHeight, mp[3] }, { tpos[1], tpos[2] + 1, tpos[3] }, 1.2) then
            return fail("no line of sight")
        end
    end

    -- the game's conditions, as far as GK.Conditions can tell: refuse what the game would refuse
    if GK.Conditions and type(target) == "string" then
        local check = GK.Conditions.Check(who, castName, target)
        if check.ok == false then
            local refusal = "the game would refuse it: " .. tostring(check.failed)
            if castName:find("SneakAttack") then
                refusal = refusal .. " (Sneak Attack needs advantage, or an ally of yours within 1.5 m of the target)"
            end
            return fail(refusal, { conditions = check.text })
        end
    end
    local job = newJob("Cast", who, { spell = castName, target = target })
    job.steps = {}
    if walkFirst then
        local goal, length = planWalk(who, target, { within = math.max(1.0, range - 0.5) })
        if not goal then finish(job, "failed", nil, length) return view(job) end
        local provokes = (length or 0) > 0 and Act.Provokes(who, goal) or {}
        if #provokes > 0 and not options.provoke then
            finish(job, "failed", { provokes = provokes }, "walking into range leaves the reach of "
                .. table.concat(provokes, ", ") .. " (an attack of opportunity): Disengage first, or provoke = true")
            return view(job)
        end
        if length > 0 then job.steps[#job.steps + 1] = walkSteps(job, who, goal, length) end
    end
    local before
    job.steps[#job.steps + 1] = function(j)
        before = snapshot(who)
        Act.Ordered[who] = { spell = castName, t = now() }
        j.castFor = castName
        if type(target) == "table" then
            -- A spell cast at a point that also has something to act with - Throw, which needs
            -- the thing being thrown - used to lose it here: only the position form was called,
            -- the game refused the cast for want of a second target, and what came back was the
            -- spell's own target condition ("target not Self()"), which reads as a different
            -- fault entirely. The form that carries the item is tried first; Osiris refuses a
            -- call it does not have rather than running it wrong, so the plain form is the
            -- fallback and the job says which was used.
            if options.target2 then
                j.castWithItem = pcall(Osi.UseSpellAtPosition, who, castName,
                    target[1], target[2], target[3], options.target2, 1)
                if not j.castWithItem then
                    Osi.UseSpellAtPosition(who, castName, target[1], target[2], target[3], 1)
                end
            else
                Osi.UseSpellAtPosition(who, castName, target[1], target[2], target[3], 1)
            end
        else
            -- The three-argument form: the five-argument one (with a second target and
            -- WithoutMove) was accepted and did nothing here (2026-11-13). Range is checked above,
            -- so the cast's own walk never comes into play.
            if options.target2 then
                Osi.UseSpell(who, castName, target, options.target2)
            else
                Osi.UseSpell(who, castName, target)
            end
        end
        return true
    end
    job.steps[#job.steps + 1] = function(j)
        if j.castFailed then
            -- say what the game checks for this spell (its tooltip does): a player would read it
            local stat = try(Ext.Stats.Get, castName)
            local req = stat and try(function() return stat.RequirementConditions end) or ""
            local tc = stat and try(function() return stat.TargetConditions end) or ""
            local refusal = "the game refused the cast"
            -- Said before the spell's own conditions, because those are about the target and
            -- this is about the call: reading them instead is what sent a run looking in the
            -- wrong place for an afternoon.
            if j.castWithItem == false then
                refusal = refusal .. " (and this build of Osiris has no cast-at-a-point that carries an "
                    .. "item, so the thing being thrown could not be named)"
            end
            if req ~= "" or tc ~= "" then
                refusal = refusal .. " - its conditions: " .. (req ~= "" and ("requires " .. req) or "")
                    .. (req ~= "" and tc ~= "" and "; " or "") .. (tc ~= "" and ("target " .. tc) or "")
            end
            if castName:find("SneakAttack") then
                refusal = refusal .. " (Sneak Attack needs advantage on the attack, or an ally of yours within 1.5 m of the target)"
            end
            return false, refusal
        end
        if not j.casted then
            if now() - (j.stepStarted or j.started) > (fighting and 4 or 8) and not j.castStarted then
                return false, "the cast did not start"
            end
            return nil
        end
        -- charge what the game did not (Osiris casts may skip costs)
        local after = snapshot(who)
        local charged = {}
        local costs = row.costs or {}
        -- only while the fight goes on: a blow that ends the fight is followed by the resources
        -- coming back, and charging then would take from the next fight
        if fighting and inCombat(who) then
            for _, k in ipairs({ "action", "bonus", "reaction" }) do
                local need = costs[k]
                if need and before[k] and after[k] and before[k] - after[k] < need then
                    local name = k == "action" and "ActionPoint" or k == "bonus" and "BonusActionPoint" or "ReactionActionPoint"
                    charged[k] = Act.Charge(who, name, need - (before[k] - after[k]))
                end
            end
        end
        if costs.slot then
            local level = options.upcast or costs.slot
            local spent = false
            for l, v in pairs(before.slots) do
                if (after.slots[l] or v) < v then spent = true end
            end
            if not spent then
                charged.slot = Act.Charge(who, "SpellSlot", 1, level)
                if charged.slot == 0 then charged.slot = Act.Charge(who, "WarlockSpellSlot", 1, level) end
            end
        end
        local order = Act.Ordered[who]
        j.result = { spell = castName, rolled = order and order.stripped ~= nil or nil,
                     chargedByAct = next(charged) and charged or nil }
        Act.Ordered[who] = nil
        return true
    end
    job.stepIndex = 1
    job.stepStarted = now()
    job.timeout = 30 + (walkFirst and 30 or 0)
    Act.Casting = Act.Casting or {}
    Act.Casting[who] = job
    startTicker()
    return { job = job.n, spell = castName, distance = math.floor(d * 10 + 0.5) / 10, range = range }
end

--- The main-hand attack: melee when the target is within reach (or with a melee weapon only),
--- ranged otherwise.
--- The weapon attack. options.mode = "melee" | "ranged"; without it: melee when the target is in
--- reach or the walk to it fits the movement left (a melee weapon usually hits harder), the
--- ranged weapon otherwise.
function Act.Attack(who, target, options)
    options = options or {}
    who = resolveWho(who)
    local ranged = try(Osi.GetEquippedItem, who, "Ranged Main Weapon")
    local tp, mp = posOf(target), posOf(who)
    local mode = options.mode
    if not mode then
        if not ranged or (tp and mp and dist(tp, mp) <= 1.5 + MELEE_SLACK) then
            mode = "melee"
        else
            mode = "ranged"
            if options.walk ~= false and type(target) == "string" then
                local goal, length = Act.ApproachPoint(who, target, 1.0)
                local left = inCombat(who) and (See.Resources(who).movement or { 0 })[1] or 1e9
                if goal and length and length <= left + 0.3 and #Act.Provokes(who, goal) == 0 then mode = "melee" end
            end
        end
    end
    if mode == "ranged" and not ranged then return fail("no ranged weapon equipped") end
    return Act.Cast(who, mode == "melee" and "Target_MainHandAttack" or "Projectile_MainHandAttack", target, options)
end

function Act.Jump(who, point) return Act.Cast(who, "Projectile_Jump", point) end
function Act.Shove(who, target) return Act.Cast(who, "Target_Shove", target) end
function Act.Hide(who) return Act.Cast(who, "Shout_Hide", resolveWho(who)) end
function Act.Dash(who) return Act.Cast(who, "Shout_Dash", resolveWho(who)) end
function Act.Disengage(who) return Act.Cast(who, "Shout_Disengage", resolveWho(who)) end
function Act.Help(who, target) return Act.Cast(who, "Target_Help", target) end
function Act.Throw(who, item, target) return Act.Cast(who, "Throw_Throw", target, { target2 = item }) end

-- ---------------------------------------------------------------------------------------
-- Objects

local function approachThen(kind, who, target, within, action, done, timeout, loose)
    who = resolveWho(who)
    local why = Act.CannotAct(who)
    if why then return fail(why) end
    local bad = needPerceived(target)
    if bad then return fail(bad) end
    local goal, length = Act.ApproachPoint(who, target, within, loose)
    if not goal then return fail("cannot get to it") end
    if inCombat(who) then
        local left = (See.Resources(who).movement or { 0 })[1]
        if length > left + 0.3 then return fail(string.format("too far: %.1f m, %.1f m of movement left", length, left)) end
        local provokes = length > 0 and Act.Provokes(who, goal) or {}
        if #provokes > 0 then
            return fail("walking there leaves the reach of " .. table.concat(provokes, ", ")
                .. " (an attack of opportunity): Disengage first, or move with provoke = true and then act",
                { provokes = provokes })
        end
    end
    -- a ladder: the climb is started only by a character that walks up to it and stands still;
    -- one already at its foot (or still settling) is ignored by the game (seen 2026-11-13 by the
    -- blind run; bg3-agent-harness saw the same) - so step back and walk in again
    local e = entityOf(target)
    local isLadder = kind == "Use" and e and try(function() return e.Ladder end) ~= nil
    if isLadder and length < 1.0 then
        local tp, mp = posOf(target), posOf(who)
        if tp and mp then
            local dx, dz = mp[1] - tp[1], mp[3] - tp[3]
            local len = math.max(0.1, math.sqrt(dx * dx + dz * dz))
            local back = { tp[1] + dx / len * 3.0, mp[2], tp[3] + dz / len * 3.0 }
            local p = See.Path(who, back)
            if p.reachable and p.ends then back = p.ends end
            local job0 = { back = back }
            goal, length = back, (p.length or 2)
            isLadder = job0
        end
    end
    local job = newJob(kind, who, { target = target })
    job.steps = {}
    if length > 0 then job.steps[1] = walkSteps(job, who, goal, length) end
    if type(isLadder) == "table" then
        -- walk back towards the ladder's foot, then wait until standing still
        local foot, footLen = Act.ApproachPoint(who, target, within, loose)
        job.steps[#job.steps + 1] = function(j)
            if not j.towardFoot then
                j.towardFoot = true
                local again, l2 = Act.ApproachPoint(who, target, within, loose)
                j.footStep = walkSteps(j, who, again or foot, l2 or footLen or 2)
                j.arrived, j.cancelled, j.stillSince = nil, nil, nil
                j.walking = false
            end
            local r, err = j.footStep(j)
            return r, err
        end
    end
    if e and try(function() return e.Ladder end) ~= nil then
        job.steps[#job.steps + 1] = function(j)
            j.stillSince = j.stillSince or now()
            return now() - j.stillSince > 0.5 or nil
        end
    end
    job.steps[#job.steps + 1] = function(j)
        j.result = nil
        local ok, err = action(j)
        if ok == false then return false, err end
        return true
    end
    job.steps[#job.steps + 1] = function(j)
        local status, err = done(j)
        if status == nil and now() - (j.stepStarted or j.started) > (timeout or 6) then return false, "nothing happened" end
        return status, err
    end
    job.stepIndex = 1
    job.stepStarted = now()
    job.timeout = 40
    startTicker()
    return { job = job.n, walk = math.floor((length or 0) * 10 + 0.5) / 10 }
end

local function carriedBy(item)
    local owner = try(Osi.GetInventoryOwner, item)
    owner = type(owner) == "string" and (owner:match("(%x+%-%x+%-%x+%-%x+%-%x+)$") or owner) or nil
    return owner and See.IsMember(owner) and owner or nil
end
Act.CarriedBy = carriedBy

--- Using an item the party carries (a potion, a scroll, a note): no walking - the character
--- whose inventory holds it uses it, as a click in the inventory does.
function Act.UseCarried(who, item)
    who = resolveWho(who)
    local why = Act.CannotAct(who)
    if why then return fail(why) end
    local owner = carriedBy(item)
    if not owner then return fail("not carried by the party") end
    local job = newJob("Use", who, { target = item })
    job.steps = {
        function(j)
            Osi.Use(owner == who and who or owner, item, "GK_Act_" .. j.n)
            return true
        end,
        function(j)
            if inDialog(j.who) then
                j.result = { success = true, effect = "a dialogue started" }
                return true
            end
            if j.useFinished ~= nil then
                j.result = { success = j.useFinished, effect = j.useFinished and "used (see heard / events)" or "it did not work" }
                return j.useFinished or false, (not j.useFinished) and "it did not work" or nil
            end
            if now() - (j.stepStarted or j.started) > 5 then
                j.result = { effect = "nothing visible happened (see heard / events)" }
                return true
            end
            return nil
        end,
    }
    job.stepIndex = 1
    job.stepStarted = now()
    job.timeout = 15
    startTicker()
    return { job = job.n, carried = true }
end

function Act.Use(who, item)
    if carriedBy(item) then return Act.UseCarried(who, item) end
    local before = { opened = bool(try(Osi.IsOpened, item)) }
    return approachThen("Use", who, item, Act.Reach, function(j)
        j.startPos = posOf(j.who)
        local le = entityOf(item)
        if le and try(function() return le.Ladder end) ~= nil then
            -- the climb does not start while chained companions crowd the ladder's foot: unchain
            -- them for the climb (the player's chain button), chain them again afterwards
            j.unchained = {}
            for _, m in ipairs(See.Members()) do
                if m ~= j.who and not bool(try(Osi.IsDead, m)) then
                    if try(Osi.DetachFromPartyGroup, m) ~= false then
                        try(Osi.PurgeOsirisQueue, m)
                        j.unchained[#j.unchained + 1] = m
                    end
                end
            end
            j.onFinish = function(job)
                for _, m in ipairs(job.unchained or {}) do try(Osi.AttachToPartyGroup, m, job.who) end
            end
        end
        Osi.Use(j.who, item, "GK_Act_" .. j.n)
    end, function(j)
        if inDialog(j.who) then
            j.result = { success = true, dialog = true }
            return true
        end
        -- a ladder: the climb takes seconds, and the Osiris task behind it climbs again and again
        -- if left alone; so wait until the character stands still at another height, then clear
        -- its queue (clearing it earlier breaks the climb off - seen 2026-11-13)
        local le = entityOf(item)
        if le and try(function() return le.Ladder end) ~= nil then
            local p = posOf(j.who)
            local dy = (p and j.startPos) and (p[2] - j.startPos[2]) or 0
            j.lastP = j.lastP or p
            local moved = p and j.lastP and dist(p, j.lastP) > 0.05
            j.lastP = p
            if moved then j.stillAt = nil else j.stillAt = j.stillAt or now() end
            -- the task climbs back at once from the other end: stop it the moment the character
            -- got there (most of the ladder's height), then let it come to rest
            if math.abs(dy) >= 1.5 and not j.stopped then
                j.stopped = true
                try(Osi.PurgeOsirisQueue, j.who)
            end
            if j.stopped and j.stillAt and now() - j.stillAt > 0.3 then
                j.result = { success = true, effect = string.format("climbed %s %.1f m (now at height %.1f)",
                             dy > 0 and "up" or "down", math.abs(dy), p[2]) }
                return true
            end
            if now() - (j.stepStarted or j.started) > 9 then
                try(Osi.PurgeOsirisQueue, j.who)
                j.result = { success = false, effect = "did not climb (the game did not start the climb)" }
                return false, "did not climb"
            end
            return nil
        end
        if j.useFinished ~= nil then
            -- the door's or lid's state changes with the animation: look again a moment later
            j.settleAt = j.settleAt or now() + 0.8
            if now() < j.settleAt then return nil end
            local opened, locked = bool(try(Osi.IsOpened, item)), bool(try(Osi.IsLocked, item))
            local kind = See.Classify(entityOf(item), item)
            local effect
            if kind == "door" then effect = opened and "the door is open now" or "the door is closed now"
            elseif kind == "container" then effect = opened and "it is open: see popup (loot window)" or "it did not open"
            elseif kind == "ladder" then
                -- judged by where the character is now, not by the game's "use finished"
                local p = posOf(j.who)
                local dy = (p and j.startPos) and (p[2] - j.startPos[2]) or 0
                if math.abs(dy) >= 1.0 then
                    effect = string.format("climbed %s %.1f m (now at height %.1f)", dy > 0 and "up" or "down", math.abs(dy), p[2])
                elseif not j.retried then
                    -- a climb asked for while the character still settles is dropped: once more
                    j.retried = true
                    j.useFinished = nil
                    j.settleAt = nil
                    try(Osi.PurgeOsirisQueue, j.who)
                    Osi.Use(j.who, item, "GK_Act_" .. j.n)
                    return nil
                else
                    effect = "did not climb (the game did not start the climb)"
                    j.useFinished = false
                end
            else effect = j.useFinished and "used (see heard / events for what happened)" or "nothing happened" end
            if locked and not opened then effect = "locked" end
            j.result = { success = j.useFinished, effect = effect, opened = opened, locked = locked }
            try(Osi.PurgeOsirisQueue, j.who)
            if j.useFinished then return true end
            return false, locked and "locked" or "it did not work"
        end
        if bool(try(Osi.IsOpened, item)) ~= before.opened then
            j.result = { success = true, opened = bool(try(Osi.IsOpened, item)) }
            return true
        end
        return nil
    end, 12, true)
end

function Act.PickUp(who, item)
    return approachThen("PickUp", who, item, Act.Reach, function(j)
        if not bool(try(Osi.GetCanPickUp, item)) then return false, "cannot be picked up" end
        Osi.Pickup(j.who, item, "GK_Act_" .. j.n, 0)
    end, function(j)
        if j.pickupFailed then return false, "could not pick it up" end
        local owner = try(Osi.GetInventoryOwner, item)
        owner = type(owner) == "string" and (owner:match("(%x+%-%x+%-%x+%-%x+%-%x+)$") or owner) or owner
        if owner and See.IsMember(owner) then
            j.result = { owner = owner, name = See.Name(item) }
            return true
        end
        return nil
    end)
end

function Act.Loot(who, corpse)
    -- a container (a chest, a backpack) is looted by opening it: the same as Use
    local e = entityOf(corpse)
    if e and try(function() return e.ServerItem end) ~= nil then return Act.Use(who, corpse) end
    return approachThen("Loot", who, corpse, Act.Reach, function(j)
        if not bool(try(Osi.IsDead, corpse)) then return false, "it is not dead" end
        Osi.OpenCharacterLootUI(j.who, corpse)
    end, function(j)
        j.result = { opened = true }
        return true
    end)
end

--- The items inside a container or a corpse the party has open or stands at (within reach):
--- { { id, name, count } }. A player sees them in the loot window.
function Act.Contents(who, container)
    who = resolveWho(who)
    local cp, mp = posOf(container), posOf(who)
    if not (cp and mp) then return fail("no such container") end
    if dist(cp, mp) > Act.Reach + 1.0 then return fail("too far to look inside") end
    local e = entityOf(container)
    local out = {}
    local inventories = try(function() return e.InventoryOwner.Inventories end) or {}
    for _, inv in ipairs(inventories) do
        for _, slot in pairs(try(function() return inv.InventoryContainer.Items end) or {}) do
            local id = try(function() return slot.Item.Uuid.EntityUuid end)
            if id then
                out[#out + 1] = { id = id, name = See.Name(id), count = try(function() return slot.Item.Stack.Amount end) }
            end
        end
    end
    return out
end

--- Takes one item (or everything, item = nil) out of a container or corpse the character stands at.
function Act.Take(who, container, item)
    who = resolveWho(who)
    local why = Act.CannotAct(who, true)
    if why then return fail(why) end
    local listed = Act.Contents(who, container)
    if listed.ok == false then return listed end
    local taken = {}
    for _, row in ipairs(listed) do
        if item == nil or row.id == item then
            Osi.ToInventory(row.id, who, row.count or 1, 1, 0)
            taken[#taken + 1] = row.name or row.id
        end
    end
    if #taken == 0 then return fail("not in there") end
    return { ok = true, taken = taken }
end

-- ---------------------------------------------------------------------------------------
-- Talking

function Act.Talk(who, npc)
    return approachThen("Talk", who, npc, Act.TalkReach, function(j)
        if See.Relation(npc) == "hostile" then return false, "hostile" end
        if bool(try(Osi.IsDead, npc)) then return false, "dead" end
        j.dialogBefore = inDialog(j.who)
        -- the event the game raises when a player clicks on a character: the story picks and
        -- starts the dialogue it wants
        Osi.DialogStartRequested(npc, j.who)
    end, function(j)
        if j.dialogFailed then return false, "the character does not want to talk" end
        if inDialog(j.who) or inDialog(npc) then
            j.result = { dialog = true }
            return true
        end
        return nil
    end, 6)
end

-- ---------------------------------------------------------------------------------------
-- Turns and modes

--- Makes a party member the one the player controls - a click on its portrait.
function Act.Select(who)
    who = resolveWho(who)
    if not See.IsMember(who) then return fail("not a party member") end
    Osi.MakePlayerActive(who)
    return { ok = true, controlled = bool(try(Osi.IsControlled, who)) }
end

--- Ends the selected character's turn. The player's way is the UI's End Turn button: the bridge's
--- client runs it (GK.Command("EndTurn") on the UI side; stand/situation.py does that after this
--- check). Osi.EndTurn was dropped by the game for members of a party turn group (2026-11-13), so
--- this server call is only the fallback for a character alone in its turn.
function Act.EndTurn(who, viaUi)
    who = resolveWho(who)
    if not inCombat(who) then return fail("not in a fight") end
    if not hisTurn(who) then return fail("not its turn") end
    if not bool(try(Osi.IsControlled, who)) then return fail("not selected (select it first)") end
    if viaUi then return { ok = true, ui = "EndTurn" } end
    Osi.EndTurn(who)
    -- in a party turn group the engine marks the end only when the whole group has ended:
    -- remember it here, so the reader does not offer this character's turn again
    Act.Ended = Act.Ended or {}
    local e = entityOf(who)
    local combat = try(function() return e.CombatParticipant.CombatHandle end)
    Act.Ended[who] = { round = combat and try(function() return combat.TurnOrder.Round end), t = now() }
    return { ok = true }
end

--- Fleeing the fight: the hot bar's Flee button for the selected character, when the game allows
--- it (GK.See.Flee: no enemy within the distance the game wants, no blocking status, a place that
--- allows it). The check is here; the Python side presses the button (GK.Command("Flee") on the UI).
function Act.Flee(who)
    who = resolveWho(who)
    if not inCombat(who) then return fail("not in a fight") end
    if not bool(try(Osi.IsControlled, who)) then return fail("not selected (select it first)") end
    local f = See.Flee(who)
    if not f.can then
        local reasons = { EnemyTooClose = string.format("an enemy is %.1f m away, the game wants %.0f m", f.enemyAt or 0, f.need or 0),
                          Condition = "a condition prevents it", Region = "this place does not allow fleeing" }
        local text = {}
        for _, w in ipairs(f.why or {}) do text[#text + 1] = reasons[w] or w end
        return fail("cannot flee: " .. table.concat(text, "; "), { flee = f })
    end
    return { ok = true, ui = "Flee" }
end

--- The turn-based mode button: a fight-like turn order outside a fight, for careful moves.
function Act.TurnBased(on, who)
    who = resolveWho(who)
    if inCombat(who) then return fail("in a fight already") end
    Osi.ForceTurnBasedMode(who, on == false and 0 or 1)
    return { ok = true }
end

function Act.Sneak(on, who)
    who = resolveWho(who)
    if on == false then
        -- the hotbar's sneak toggle: leaving stealth is free
        Osi.RemoveStatus(who, "SNEAKING", "")
        return { ok = true }
    end
    return Act.Hide(who)
end

function Act.Equip(who, item)
    who = resolveWho(who)
    local why = Act.CannotAct(who)
    if why then return fail(why) end
    local owner = try(Osi.GetInventoryOwner, item)
    if owner ~= who then return fail("not in its inventory") end
    if inCombat(who) and not bool(try(Osi.IsWeapon, item)) then return fail("armour cannot be changed in a fight") end
    Osi.Equip(who, item)
    return { ok = true }
end

function Act.Unequip(who, item)
    who = resolveWho(who)
    local why = Act.CannotAct(who)
    if why then return fail(why) end
    Osi.Unequip(who, item)
    return { ok = true }
end

-- ---------------------------------------------------------------------------------------
-- Locks and traps: the player's own lock picking and disarming

--- Whether this Script Extender build can queue the engine's lock picking and disarming
--- requests safely: the GateKit fork from 1cf92c82 on (it also has the DisarmSystem binding;
--- an older build had the request layout 8 bytes off - writing it there would corrupt memory).
function Act.CanPickLocks()
    return GK.Stage and GK.Stage.PickLock ~= nil and try(function() return Ext.System.ServerTrapDisarm end) ~= nil
end

--- The first item in `who`'s inventory whose root template contains one of `tags`, in that order.
local function toolFor(who, ...)
    local tags = { ... }
    local e = entityOf(who)
    for _, tag in ipairs(tags) do
        for _, inv in ipairs(try(function() return e.InventoryOwner.Inventories end) or {}) do
            for _, slot in pairs(try(function() return inv.InventoryContainer.Items end) or {}) do
                local id = try(function() return slot.Item.Uuid.EntityUuid end)
                local name = id and try(Osi.GetTemplate, id) or ""
                if name:find(tag, 1, true) then return id end
            end
        end
    end
    return nil
end

--- Whether the engine still has an attempt of this kind running on this character: while one of
--- these components is on it, the game is working - usually waiting for the player to answer the
--- dice window - and nothing has failed yet.
---
--- This exists because the lock picking job used to call a failure at 25 seconds while the roll
--- window was still open and unanswered, and the lock then opened. An API that reports a failure
--- that did not happen is worse than one that waits, so the clock only starts once the engine has
--- let the attempt go.
local function stillAttempting(who, components)
    local e = entityOf(who)
    if not e then return false end
    for _, name in ipairs(components) do
        if try(function() return e[name] end) ~= nil then return true end
    end
    return false
end

--- One step of an attempt the engine runs on its own.
---   who         the character making it
---   done()      true once it has plainly worked
---   o.effect    what to record as the result then
---   o.busy      the components the engine keeps on the character while it is still at it
---   o.patience  seconds to allow after the engine has let go, before calling it a failure
---   o.failure   what to say then
local function attemptStep(who, done, o)
    return function(j)
        if done() then
            j.step = nil
            j.result = { effect = o.effect }
            return true
        end
        if stillAttempting(who, o.busy) then
            -- The engine is still at it. Hold the clock and say what is being waited for, so that
            -- an unanswered dice window reads as a question and not as a failure.
            local t = now()
            j.step = "the engine is still at it (if a dice window is open, answer it: GK.RollNext on the client)"
            j.held = (j.held or 0) + (j.heldSince and t - j.heldSince or 0)
            j.heldSince = t
            j.stepStarted = t
            j.patience = nil
            return nil
        end
        -- The engine has let go. Give the world a moment to catch up, then judge.
        j.step, j.heldSince = nil, nil
        j.patience = j.patience or now()
        if now() - j.patience > o.patience then return false, o.failure end
        return nil
    end
end

--- Picks a lock as the context menu's Lockpick does: the engine walks the character there, asks
--- the story, opens the dice window (Sleight of Hand; `roll` throws it) and unlocks on success.
--- Needs Thieves' Tools in the character's inventory (one breaks on a failure).
function Act.Lockpick(who, item)
    who = resolveWho(who)
    local why = Act.CannotAct(who)
    if why then return fail(why) end
    local bad = needPerceived(item)
    if bad then return fail(bad) end
    if not bool(try(Osi.IsLocked, item)) then return fail("it is not locked") end
    if not Act.CanPickLocks() then return fail("this Script Extender build cannot start lock picking (restart with the current GateKit build)") end
    local tool = toolFor(who, "ThievesTools", "Thieves")
    if not tool then return fail("no thieves' tools in its inventory") end
    local ok, err = GK.Stage.PickLock(item, who, tool)
    if not ok then return fail(err or "the game did not take the request") end
    local job = newJob("Lockpick", who, { target = item })
    job.steps = { attemptStep(who, function() return not bool(try(Osi.IsLocked, item)) end, {
        effect = "unlocked",
        busy = { "LockpickingState", "LockPicking" },
        patience = 6,
        failure = "still locked (a failed roll breaks a set of tools; see events)",
    }) }
    job.stepIndex = 1
    job.stepStarted = now()
    job.timeout = 40
    startTicker()
    return { job = job.n, tool = See.Name(tool) }
end

--- Disarms a discovered trap with a Trap Disarm Toolkit, as the context menu's Disarm does.
function Act.Disarm(who, trap)
    who = resolveWho(who)
    local why = Act.CannotAct(who)
    if why then return fail(why) end
    local bad = needPerceived(trap)
    if bad then return fail(bad) end
    if not (bool(try(Osi.IsTrap, trap)) and bool(try(Osi.IsTrapDiscovered, trap))) then return fail("no discovered trap there") end
    if not bool(try(Osi.IsTrapArmed, trap)) then return fail("the trap is not armed") end
    if not Act.CanPickLocks() then return fail("this Script Extender build cannot start disarming (restart with the current GateKit build)") end
    -- The game calls the kit TOOL_GEN_Disam_Toolkit_A - Larian's own typo, and the game's
    -- spelling is the one that finds the item. The others are tried in case it is ever fixed.
    local tool = toolFor(who, "Disam_Toolkit", "Disarm_Toolkit", "TrapDisarm")
    if not tool then return fail("no trap disarm toolkit in its inventory") end
    local ok, err = GK.Stage.Disarm(trap, who, tool)
    if not ok then return fail(err or "the game did not take the request") end
    local job = newJob("Disarm", who, { target = trap })
    job.steps = { attemptStep(who, function() return not bool(try(Osi.IsTrapArmed, trap)) end, {
        effect = "disarmed",
        busy = { "DisarmingState" },
        patience = 6,
        failure = "still armed (see events)",
    }) }
    job.stepIndex = 1
    job.stepStarted = now()
    job.timeout = 40
    startTicker()
    return { job = job.n, tool = See.Name(tool) }
end

-- ---------------------------------------------------------------------------------------
-- Weapons drawn and put away

--- Draws or sheathes weapons and waits for the game to agree, rather than saying it is done.
---
--- GK.Unsheath asked Osiris and returned at once. The engine does not always apply it there and
--- then - a character standing still can stay as it was until it next moves - so the call looked
--- like it had worked and the world said otherwise. A whole namespace of the engine
--- (esv::unsheath) went unmapped behind that, because the run could not tell what it had done.
---
--- So this is a job: it asks, then watches `IsWeaponUnsheathed` until it says what was asked for.
--- If the game has not come round within the patience, it fails and says that plainly - which is
--- a fact about the engine worth having, not a thing to paper over.
---
--- The player's own way is the hotbar's weapon toggle, and the bridge cannot press it:
--- `LSToggleButton.IsChecked` is a Nullable<Bool>, which Script Extender does not read today.
--- Until the fork can, this is the honest way to reach the same state.
local function unsheathJob(who, drawn, kind)
    who = resolveWho(who)
    local why = Act.CannotAct(who)
    if why then return fail(why) end
    if bool(try(Osi.IsWeaponUnsheathed, who)) == drawn then
        return { effect = drawn and "already drawn" or "already sheathed" }
    end
    Osi.SetWeaponUnsheathed(who, drawn and 1 or 0, 1)
    local job = newJob(kind, who, { drawn = drawn })
    job.steps = { function(j)
        if bool(try(Osi.IsWeaponUnsheathed, who)) == drawn then
            j.result = { effect = drawn and "drawn" or "sheathed" }
            return true
        end
        if now() - (j.stepStarted or j.started) > 4 then
            return false, (drawn and "the weapons are still away" or "the weapons are still out")
                .. ": the engine took the order and has not applied it, which it does when the "
                .. "character next acts or moves"
        end
        return nil
    end }
    job.stepIndex = 1
    job.stepStarted = now()
    job.timeout = 8
    startTicker()
    return { job = job.n }
end

--- Takes the character's weapons out, as the hotbar's weapon toggle does. Waits for the game.
function Act.Draw(who) return unsheathJob(who, true, "Draw") end

--- Puts the character's weapons away. Waits for the game, and says so if it does not come round.
function Act.Sheathe(who) return unsheathJob(who, false, "Sheathe") end

--- Immersive mode (docs/agent-immersive.md): a line the acting character says, shown in the game
--- as a notification, and a plain reason; both kept in GK.Act.Narration. Changes nothing in the world.
Act.Narration = Act.Narration or {}
function Act.Say(text, why, who)
    who = resolveWho(who)
    if not text or text == "" then return fail("nothing to say") end
    local name = See.Name(who) or "?"
    try(Osi.ShowNotification, who, name .. ": " .. text)
    local row = { t = math.floor(now() * 10) / 10, who = who, name = name, text = text, why = why }
    Act.Narration[#Act.Narration + 1] = row
    if #Act.Narration > 500 then table.remove(Act.Narration, 1) end
    return { ok = true, said = name .. ": " .. text }
end

function Act.Stop(who)
    who = resolveWho(who)
    try(Osi.PurgeOsirisQueue, who)
    for _, job in pairs(Act.JobList) do
        if job.who == who and job.status == "running" then finish(job, "failed", job.result, "stopped") end
    end
    return { ok = true }
end

-- ---------------------------------------------------------------------------------------
-- Game events the jobs wait for

local function bare(id)
    return type(id) == "string" and (id:match("(%x+%-%x+%-%x+%-%x+%-%x+)$") or id) or id
end

local function jobFor(who, kind)
    who = bare(who)
    local found
    for _, job in pairs(Act.JobList) do
        if job.status == "running" and job.who == who and (not kind or job.kind == kind) then
            if not found or job.n > found.n then found = job end
        end
    end
    return found
end

-- Handlers are looked up in Act.On at the moment an event fires, so reloading this file replaces
-- them; the Osiris listeners themselves are registered once per Lua state.
Act.On = {
    EntityEvent = function(_object, event)
        local n = tonumber(tostring(event):match("^GK_Act_(%d+)$"))
        local job = n and Act.JobList[n]
        if job then job.arrived = true end
    end,
    CharacterMoveToCancelled = function(_character, moveId)
        local job = Act.JobList[moveId]
        if job and job.walking then job.cancelled = true end
    end,
    CastSpell = function(caster)
        local job = jobFor(caster, "Cast")
        if job then job.castStarted = true end
    end,
    CastedSpell = function(caster)
        local job = jobFor(caster, "Cast")
        if job then job.casted = true end
    end,
    CastSpellFailed = function(caster)
        local job = jobFor(caster, "Cast")
        if job then job.castFailed = true end
    end,
    UseFinished = function(character, _item, success)
        local job = jobFor(character, "Use")
        if job and job.useFinished == nil then job.useFinished = success == 1 end
    end,
    PickupFailed = function(character)
        local job = jobFor(character, "PickUp")
        if job then job.pickupFailed = true end
    end,
    DialogRequestFailed = function()
        for _, job in pairs(Act.JobList) do
            if job.status == "running" and job.kind == "Talk" then job.dialogFailed = true end
        end
    end,
}

local ARITY = { EntityEvent = 2, CharacterMoveToCancelled = 2, CastSpell = 5, CastedSpell = 5, CastSpellFailed = 5,
                UseFinished = 3, PickupFailed = 2, DialogRequestFailed = 2 }
Act.Registered = Act.Registered or {}
for name, arity in pairs(ARITY) do
    if not Act.Registered[name] then
        Act.Registered[name] = pcall(Ext.Osiris.RegisterListener, name, arity, "after", function(...)
            local handler = GK.Act.On[name]
            if handler then pcall(handler, ...) end
        end)
    end
end

return Act
