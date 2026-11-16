-- GK.Camera: the player's camera from code - turn it to look where the party goes, so a person
-- watching an agent play sees what lies ahead. Client side.
--
--   GK.Camera.State()            { rotation, looking (degrees, 0 = north = +z, 90 = east = +x),
--                                  pitch, distance, target = {x,y,z} }
--   GK.Camera.Look(degrees)      turns the camera to look that way (the game eases into it)
--   GK.Camera.Face(to)           turns it to look from the character it follows towards `to`:
--                                a point {x,y,z} or an object's uuid
--
-- How (found 2026-11-13, game 4.1.1.7631656): the game camera is the entity with
-- GameCameraBehavior. Its Direction is the unit vector from the followed target to the camera
-- (pitch 40.7 degrees by default); Direction and DirectionDestination are recomputed from
-- RotationY every frame, so RotationY is the field to set. Measured: the camera looks towards
-- RotationY - 45 degrees (0 = +z).

GK.Camera = GK.Camera or {}
local Camera = GK.Camera
Camera.Offset = 45

local function behavior()
    local list = Ext.Entity.GetAllEntitiesWithComponent("GameCameraBehavior")
    local e = list and list[1]
    return e and e.GameCameraBehavior or nil
end

local function angleOf(dx, dz)
    return math.deg(math.atan(dx, dz)) % 360
end

function Camera.State()
    local c = behavior()
    if not c then return { error = "no game camera" } end
    local d = c.Direction
    return {
        rotation = c.RotationY,
        looking = d and math.floor(angleOf(-d[1], -d[3]) + 0.5) % 360 or nil,
        pitch = c.PitchDegrees, distance = c.Distance,
        target = c.TargetCurrent and { c.TargetCurrent[1], c.TargetCurrent[2], c.TargetCurrent[3] } or nil,
        -- added for GK.Camera.Focus: whether the camera glides now, its mode, and the focus held
        moving = c.MovingToTarget, mode = c.CameraMode,
        focus = Camera.Current and Camera.Current.what or nil,
    }
end

-- Turning: set at once, RotationY makes the game swing the camera's direction vector straight to
-- the new one - for a half turn that passes over the character's head, which is hard to watch.
-- So the turn is played here: the shorter way round, a few degrees per frame, easing in and out,
-- at most Camera.Speed degrees a second; small corrections (under Camera.MinTurn) are skipped.
Camera.Speed = 180        -- degrees a second
Camera.MaxSeconds = 1    -- a half turn takes a second
Camera.MinTurn = 25

local function shortest(from, to)
    return (to - from + 540) % 360 - 180
end

function Camera.Look(degrees, options)
    options = options or {}
    local c = behavior()
    if not c then return { error = "no game camera" } end
    local target = (degrees + Camera.Offset) % 360
    local start = c.RotationY or 0
    local delta = shortest(start, target)
    if math.abs(delta) < (options.min or Camera.MinTurn) then return { ok = true, skipped = "small turn" } end
    if options.now then
        c.RotationY = target
        return { ok = true, rotation = target }
    end
    Camera.Turn = (Camera.Turn or 0) + 1
    local turn = Camera.Turn
    local duration = math.min(Camera.MaxSeconds, math.max(0.4, math.abs(delta) / Camera.Speed))
    local began = Ext.Utils.MonotonicTime()
    local function step()
        if Camera.Turn ~= turn then return end   -- a newer turn took over
        local cam = behavior()
        if not cam then return end
        local t = math.min(1, (Ext.Utils.MonotonicTime() - began) / 1000 / duration)
        local eased = t * t * (3 - 2 * t)          -- smoothstep: slow, faster, slow
        cam.RotationY = (start + delta * eased) % 360
        if t < 1 then Ext.Timer.WaitFor(16, step) end
    end
    step()
    return { ok = true, from = start, to = target, seconds = duration }
end

function Camera.Face(to)
    local c = behavior()
    if not c then return { error = "no game camera" } end
    local p = to
    if type(to) == "string" then
        local e = Ext.Entity.Get(to)
        local t = e and e.Transform and e.Transform.Transform.Translate
        p = t and { t[1], t[2], t[3] } or nil
    end
    local from = c.TargetCurrent
    if not (p and from) then return { error = "no position" } end
    local dx, dz = p[1] - from[1], p[3] - from[3]
    if dx * dx + dz * dz < 1 then return { ok = true, note = "already there" } end
    return Camera.Look(angleOf(dx, dz))
end

-- ---------------------------------------------------------------------------------------------
-- The camera's focus: the camera moved to a thing the way the game itself moves it.
--
--   GK.Camera.Focus(at, options)   at: an entity's uuid, a point {x,y,z}, or a list of two of them
--   GK.Camera.Return(options)      back to the controlled character, by the game's own restore
--   GK.Camera.OnScreen(at)         where a point or an entity is on the screen: { u, v, inFront }
--
-- How (game 4.1.1.7631656; research in the night notes K1): the camera entity (GameCameraBehavior)
-- also holds CameraCombatTarget, which the game writes on a ranged attack (TargetMode 4, the
-- targets, no zoom) and on the restore after it (TargetMode 1, the actor, zoom); it is read only
-- when marked changed (MarkChanged), and outside a fight (CameraMode 8) not at all. On the player's
-- own turn (CameraMode 9) a TargetMode 4 written there did not move the camera; the restore did.
-- What moves the camera in and out of a fight is the behaviour's own Targets - read only when
-- their COUNT changes, so one entity is written twice ({e, e}) - and, for a point,
-- TargetDestination with the Targets emptied. The return in a fight is the game's restore. Beyond the camera switches' TargetSnapDistance (50 m)
-- the camera cuts instead of gliding.
--
-- A creature far from the camera (30-45 m and beyond) is a stub on the client: about 17 components
-- instead of 120, not drawn, and its Transform stays where it was when it became a stub (measured:
-- moved by the server, the client kept the old point). Such an entity is refused here; the server's
-- GK.Camera.Focus (Server/Camera.lua) sends the server's own point instead, over GK_Camera.

Camera.SnapDistance = 50          -- the game's TargetSnapDistance, read back from the switches
Camera.GlideSnap = 200            -- the snap distance while a glide is asked for
Camera.SettleMs = 4000            -- the limit of an answer: a guard, in ms of a running game
Camera.Near = 0.5                 -- metres (x, z) between the camera's target and the thing
local INF = 3.4028234663852886e38

Camera.Generation = Camera.Generation or 0

local function cameraEntity()
    local list = Ext.Entity.GetAllEntitiesWithComponent("GameCameraBehavior")
    return list and list[1] or nil
end

local function controlled()
    local list = Ext.Entity.GetAllEntitiesWithComponent("ClientControl")
    return list and list[1] or nil
end

local function vec(v) return v and { v[1], v[2], v[3] } or nil end

local function pointOf(t)
    if type(t) ~= "table" then return nil end
    local x, y, z = t.x or t[1], t.y or t[2], t.z or t[3]
    if type(x) == "number" and type(y) == "number" and type(z) == "number" then return { x, y, z } end
end

-- An entity the client holds whole (not a far creature's stub): one with a level presence.
local function whole(e)
    return e ~= nil and pcall(function() return e.Level end) and e.Level ~= nil
end

local function positionOf(e)
    local ok, t = pcall(function() return e.Transform.Transform.Translate end)
    return ok and t and { t[1], t[2], t[3] } or nil
end

local function flat(a, b) return math.sqrt((a[1] - b[1]) ^ 2 + (a[3] - b[3]) ^ 2) end

local function inFight(who)
    local ok, handle = pcall(function() return who.CombatParticipant.CombatHandle end)
    return ok and handle ~= nil
end

-- World to screen: clip = P * V * p, the camera's own matrices (column-major, as glm keeps them).
local function project(p)
    local cam = cameraEntity()
    local ctl = cam and cam.Camera and cam.Camera.Controller
    local V, P = ctl and ctl.Camera.ViewMatrix, ctl and ctl.Camera.ProjectionMatrix
    if not (V and P) then return nil end
    local function mul(M, x, y, z, w)
        return M[1] * x + M[5] * y + M[9] * z + M[13] * w, M[2] * x + M[6] * y + M[10] * z + M[14] * w,
            M[3] * x + M[7] * y + M[11] * z + M[15] * w, M[4] * x + M[8] * y + M[12] * z + M[16] * w
    end
    local vx, vy, vz, vw = mul(V, p[1], p[2], p[3], 1)
    local cx, cy, _, cw = mul(P, vx, vy, vz, vw)
    if cw <= 0 then return { inFront = false } end
    local u, v = (cx / cw + 1) / 2, (1 - cy / cw) / 2
    return { u = u, v = v, inFront = true, inside = u >= 0 and u <= 1 and v >= 0 and v <= 1 }
end

--- Where a point {x,y,z} or an entity (its feet) is on the screen: { u, v, inFront, inside }, u and v
--- from 0 to 1 from the top left corner of the view, from the camera's view and projection matrices.
--- A far creature's stub is refused (its client position is stale).
function Camera.OnScreen(at)
    local p = pointOf(at)
    if not p and type(at) == "string" then
        local e = Ext.Entity.Get(at)
        if not e then return { error = "no such entity on the client: " .. at } end
        if not whole(e) then
            return { error = "a far creature's stub: its client position is stale; give the server's point (Osi.GetPosition) instead" }
        end
        p = positionOf(e)
    end
    if not p then return { error = "give an entity's uuid or a point {x, y, z}" } end
    return project(p) or { error = "no game camera" }
end

-- The snap distance: raised for a glide, always put back.
local function switches()
    local ok, list = pcall(function() return Ext.Utils.GetGlobalSwitches().CameraSwitches end)
    return ok and list or nil
end

local function restoreSnap()
    local saved = Camera.SnapSaved
    if not saved then return end
    Camera.SnapSaved = nil
    local list = switches()
    for i, value in pairs(saved) do pcall(function() list[i].TargetSnapDistance = value end) end
end

local function raiseSnap()
    restoreSnap()
    local list = switches()
    if not list then return false end
    local saved = {}
    for i = 1, #list do
        saved[i] = list[i].TargetSnapDistance
        Camera.SnapDistance = saved[1]
        pcall(function() list[i].TargetSnapDistance = Camera.GlideSnap end)
    end
    Camera.SnapSaved = saved
    -- a guard: the answer puts it back when it comes; an answer nobody follows does not
    local generation = Camera.Generation
    Ext.Timer.WaitFor(Camera.SettleMs + 2000, function()
        if Camera.Generation == generation then restoreSnap() end
    end)
    return true
end

-- Resolves what to focus: { { entity, uuid, point } } with a point for each, or nil and why.
-- `given` are the server's points by uuid (a stub is focused there).
local function resolve(at, given)
    local list = at
    if type(at) ~= "table" or pointOf(at) then list = { at } end
    if #list == 0 or #list > 2 then return nil, "focus one thing or two: a uuid or a point {x, y, z}" end
    local out = {}
    for _, item in ipairs(list) do
        local p = pointOf(item)
        if p then
            out[#out + 1] = { point = p }
        elseif type(item) == "string" and item ~= "" then
            local e = Ext.Entity.Get(item)
            local served = given and given[item]
            if whole(e) then
                out[#out + 1] = { entity = e, uuid = item, point = positionOf(e) }
            elseif served then
                out[#out + 1] = { uuid = item, point = served, stub = e ~= nil }
            elseif e then
                return nil, "a far creature's stub (" .. item .. "): the client's position of it is stale; "
                    .. "focus it with GK.Camera.Focus on the server, which sends the server's point"
            else
                return nil, "no such entity: " .. item
            end
        else
            return nil, "not a uuid or a point: " .. tostring(item)
        end
    end
    return out
end

local function centre(targets)
    if #targets == 1 then return targets[1].point end
    local a, b = targets[1].point, targets[2].point
    return { (a[1] + b[1]) / 2, (a[2] + b[2]) / 2, (a[3] + b[3]) / 2 }
end

-- Writes the move. Answers how ("targets", "destination").
-- In a fight too: the combat target (TargetMode 4, one target or two, or a Position; marked
-- changed) was written on the player's own turn (CameraMode 9) and the game copied it into
-- CameraTarget but did not move the camera, in five variants tried; the behaviour's targets and
-- destination move it in a fight as outside. The combat target serves the return (TargetMode 1).
local function move(cam, targets)
    local c = cam.GameCameraBehavior
    local entities = {}
    for _, t in ipairs(targets) do
        if t.entity then entities[#entities + 1] = t.entity end
    end
    local allEntities = #entities == #targets
    if allEntities then
        -- the behaviour reads its Targets when their count changes: repeat them to a new count
        local list, n = {}, #c.Targets
        local copies = (#entities * 2 == n) and 3 or 2
        for _, e in ipairs(entities) do
            for _ = 1, copies do list[#list + 1] = e end
        end
        c.Targets = list
        return "targets"
    end
    c.Targets = {}
    c.TargetDestination = centre(targets)
    return "destination"
end

local function answerOf(cam, targets, how, extra)
    local c = cam.GameCameraBehavior
    local at = vec(c.TargetCurrent)
    local want = centre(targets)
    local out = { at = at, distance = c.DistanceCurrent, how = how }
    for k, v in pairs(extra or {}) do out[k] = v end
    out.off = at and want and math.floor(flat(at, want) * 100 + 0.5) / 100 or nil
    local screens = {}
    for i, t in ipairs(targets) do
        local s = project(t.point)
        screens[i] = s and s.u and { u = s.u, v = s.v, inside = s.inside } or s
    end
    out.onScreen = #targets == 1 and screens[1] or screens
    return out
end

local returnNow   -- below: a focus's hold ends with it

-- The observation every move shares: settled when the game says the camera is no longer moving
-- and its target stood still for three frames; cancelled when the player takes the camera.
local function observe(result, cam, targets, how, options, done)
    local c = cam.GameCameraBehavior
    local last, still, snapped, maxStep = vec(c.TargetCurrent), 0, false, 0
    local inControl = c.PlayerInControl
    local generation = Camera.Generation
    return GK.Observe(result, function(ticks, final)
        local function finish(answer)
            restoreSnap()
            if done then done(answer) end
            return answer
        end
        if Camera.Generation ~= generation then
            return finish({ ok = false, error = "a newer camera move took over", ticks = ticks })
        end
        local cc = cam.GameCameraBehavior
        if cc.PlayerInControl and not inControl then
            return finish(answerOf(cam, targets, how, { ok = false, ticks = ticks,
                error = "cancelled by the player's input: the camera's PlayerInControl became true" }))
        end
        local now = vec(cc.TargetCurrent)
        local step = (now and last) and flat(now, last) or 0
        if step > maxStep then maxStep = step end
        if step > 8 then snapped = true end
        last = now
        local zooming = math.abs((cc.DistanceCurrent or 0) - (cc.DistanceDestination or 0)) > 0.05
        if step < 0.01 and not cc.MovingToTarget and not zooming and ticks >= 2 then still = still + 1 else still = 0 end
        if still >= 3 then
            local answer = answerOf(cam, targets, how, { ticks = ticks, snapped = snapped })
            if #targets == 1 then
                answer.ok = answer.off ~= nil and answer.off <= Camera.Near
                if not answer.ok then
                    answer.error = maxStep < 0.01
                        and "the camera did not move: the game holds it (a dialogue's or a scene's camera)"
                        or "the camera settled " .. tostring(answer.off) .. " m from the thing"
                end
            else
                answer.ok = true
                for _, s in ipairs(answer.onScreen) do
                    if not (s and s.inside) then
                        answer.ok, answer.error = false, "the camera settled with a target off the screen"
                    end
                end
            end
            if answer.ok and options and options.hold and options.hold > 0 then
                Ext.Timer.WaitFor(math.floor(options.hold * 1000), function()
                    if Camera.Generation == generation then returnNow() end
                end)
            end
            return finish(answer)
        end
        if final then
            return finish(answerOf(cam, targets, how, { ok = false, ticks = ticks, snapped = snapped,
                error = "the camera did not settle in " .. GK.LimitTicks(Camera.SettleMs) .. " ticks" }))
        end
    end, Camera.SettleMs)
end

-- The focus itself; `given` are the server's points by uuid, `done(answer)` is told the answer.
local function focus(at, options, given, done)
    options = options or {}
    local cam = cameraEntity()
    if not cam then return { ok = false, error = "no game camera" } end
    local targets, why = resolve(at, given)
    if not targets then return { ok = false, error = why } end
    local c = cam.GameCameraBehavior
    local who = controlled()
    local fight = who ~= nil and inFight(who)
    if not Camera.Current then
        Camera.Saved = { distance = c.DistanceDestination or c.Distance, fight = fight }
    end
    Camera.Generation = Camera.Generation + 1
    restoreSnap()
    local want = centre(targets)
    local far = c.TargetCurrent and flat(c.TargetCurrent, want) or 0
    local glide = false
    if options.glide and far >= (Camera.SnapDistance or 50) - 1 then glide = raiseSnap() end
    local ok, how = pcall(move, cam, targets)
    if not ok then
        restoreSnap()
        return { ok = false, error = "the camera refused the write: " .. tostring(how) }
    end
    local what = {}
    for i, t in ipairs(targets) do what[i] = t.uuid or t.point end
    Camera.Current = { what = what, how = how, fight = fight }
    return observe({ ok = true, how = how, from = vec(c.TargetCurrent), to = want, far = far, glide = glide },
        cam, targets, how, options, function(answer)
            answer.fight = fight
            if done then done(answer) end
        end)
end

--- Moves the game's camera to `at` - an entity's uuid, a point {x, y, z}, or a list of two (framed
--- together, as on a ranged attack) - the way the game moves it: through the camera behaviour's
--- targets (an entity) or its destination (a point), in a fight and outside.
--- options.glide: glide past the game's snap distance (50 m) instead of cutting; options.hold:
--- seconds before the camera returns by itself (default: it stays until GK.Camera.Return, a new
--- focus or the game's own next move). Observed: { ok, at, distance, ticks, how, snapped, fight,
--- onScreen = { u, v }, off } once the camera settled; ok = false with the reason otherwise.
--- Client side; a far creature's stub is refused (GK.Camera.Focus on the server sends its point).
function Camera.Focus(at, options)
    return focus(at, options)
end

-- Back to the controlled character, by the game's own restore.
returnNow = function(done, options)
    local cam = cameraEntity()
    if not cam then return { ok = false, error = "no game camera" } end
    local who = controlled()
    if not who then return { ok = false, error = "no controlled character" } end
    local c = cam.GameCameraBehavior
    Camera.Generation = Camera.Generation + 1
    restoreSnap()
    local home = positionOf(who)
    local glide = false
    if options and options.glide and home and c.TargetCurrent
        and flat(c.TargetCurrent, home) >= (Camera.SnapDistance or 50) - 1 then
        glide = raiseSnap()
    end
    local fight = inFight(who)
    local saved = Camera.Saved
    local ok, problem = pcall(function()
        if fight then
            local t = cam.CameraCombatTarget.Target
            t.TargetMode = 1
            t.Targets = { who }
            -- a Position left set overrides the Targets: the return silently fails without this
            t.Position = { INF, INF, INF }
            t.ZoomToDistance = true
            cam:MarkChanged("CameraCombatTarget")
        else
            c.Targets = (#c.Targets == 1) and { who, who } or { who }
            if saved and saved.distance then c.DistanceDestination = saved.distance end
        end
    end)
    if not ok then return { ok = false, error = "the camera refused the write: " .. tostring(problem) } end
    Camera.Current, Camera.Saved = nil, nil
    local u = who.Uuid and who.Uuid.EntityUuid
    local targets = { { entity = who, uuid = u, point = positionOf(who) } }
    return observe({ ok = true, how = fight and "combat target" or "targets", to = targets[1].point, glide = glide },
        cam, targets, fight and "combat target" or "targets", nil, function(answer)
            if saved and saved.distance then
                answer.restoredDistance = saved.distance
                if answer.ok and not fight and math.abs((answer.distance or 0) - saved.distance) > 0.5 then
                    answer.ok, answer.error = false, "back on the character, at another distance"
                end
            end
            if done then done(answer) end
        end)
end

--- Brings the camera back to the controlled character by the game's own restore (in a fight the
--- combat target's TargetMode 1 with the Position reset; outside, the behaviour's targets and the
--- distance saved by the first focus). options.glide as GK.Camera.Focus's. Observed like
--- GK.Camera.Focus, with `restoredDistance`. Client side.
function Camera.Return(options)
    return returnNow(nil, options)
end

-- The server's half (Server/Camera.lua) sends { op, id, at, points, options } on GK_Camera; the
-- answer goes back on the same channel once the observation ends. While such an answer is pending
-- one tick subscription follows it; it does nothing once nothing is pending.
Camera.Pending = Camera.Pending or {}
local function followTick()
    for id, follow in pairs(Camera.Pending) do
        local ok, answer = pcall(follow)
        if not ok then answer = { ok = false, error = tostring(answer) } end
        if answer ~= nil then
            Camera.Pending[id] = nil
            pcall(function() Camera.Channel:SendToServer({ id = id, answer = answer }) end)
        end
    end
end

local function fromServer(msg)
    if type(msg) ~= "table" or not msg.id then return end
    local result
    if msg.op == "focus" then
        result = focus(msg.at, msg.options, msg.points)
    elseif msg.op == "return" then
        result = returnNow(nil, msg.options)
    else
        result = { ok = false, error = "unknown camera request " .. tostring(msg.op) }
    end
    Camera.Pending[msg.id] = GK.Follow(result)
    if not Camera.TickSubscription then
        Camera.TickSubscription = Ext.Events.Tick:Subscribe(function() GK.Camera.FollowTick() end)
    end
end
Camera.FollowTick = followTick

do
    local ok, made = pcall(Ext.Net.CreateChannel, ModuleUUID, "GK_Camera", function(msg) fromServer(msg) end)
    if ok then Camera.Channel = made end
end

return Camera
