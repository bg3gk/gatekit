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

return Camera
