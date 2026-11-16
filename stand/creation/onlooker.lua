-- Scene "onlooker" (the video stand): Raphael stands beside the character being created, turned to
-- it, and says a line. Server context, in character creation (the level SYS_CC_I).
--
-- The character on the screen is a client-only dummy (ClientCCDummy; the client entity named by
-- ClientCCDummyDefinition.Dummy of the definition that carries CCCharacterDefinition); the server
-- does not hold it, so the place is given in the level's own coordinates: the dummy stands at
-- DUMMY and the creation camera looks at it from CAMERA. Other places:
-- GKSceneOptions = { onlooker = { at = {x,y,z}, yaw = degrees, line = "...", template = uuid, look = uuid } }
-- Arm:    ./gatekit --instance <copy> exec server "$(cat stand/creation/onlooker.lua)"
-- Disarm: ./gatekit --instance <copy> exec server "$(cat stand/creation/onlooker_off.lua)"
-- State:  GKScene.onlooker = { armed, fired, log, id, at, yaw }
GKScene = GKScene or {}
local old = GKScene.onlooker
if old and old.armed then return { ok = true, already = true, id = old.id } end

local O = (GKSceneOptions and GKSceneOptions.onlooker) or {}
local LEVEL = "SYS_CC_I"
local DUMMY = { 9.99, 0.89, 111.14 }
local CAMERA = { 7.43, 1.94, 113.54 }
local S = { armed = true, fired = false, log = {} }
GKScene.onlooker = S

local function log(text) S.log[#S.log + 1] = { ms = Ext.Utils.MonotonicTime(), text = text } end
local function try(what, fn, ...)
    local ok, v = pcall(fn, ...)
    if not ok then log("failed: " .. what .. ": " .. tostring(v)) end
    return ok and v or nil
end

local level = Osi.GetRegion(Osi.GetHostCharacter())
if level ~= LEVEL then
    S.armed, S.error = false, "the level is " .. tostring(level) .. ", character creation is " .. LEVEL
    return { ok = false, error = S.error }
end

-- Beside the dummy as the camera sees it: `side` metres to the picture's right, `behind` metres
-- further from the camera.
local fx, fz = DUMMY[1] - CAMERA[1], DUMMY[3] - CAMERA[3]
local length = math.sqrt(fx * fx + fz * fz)
fx, fz = fx / length, fz / length
local rx, rz = fz, -fx
local side, behind = O.side or 0.75, O.behind or 0.9
S.at = O.at or { DUMMY[1] + rx * side + fx * behind, DUMMY[2], DUMMY[3] + rz * side + fz * behind }
-- The yaw asked for. It does not hold: after the transform into the look GK.Stage.Yaw reads 0 and
-- the figure shows its back to the camera, whatever is given here or to Osi.ToTransform.
S.yaw = O.yaw or math.deg(math.atan(DUMMY[1] - S.at[1], DUMMY[3] - S.at[3]))

if not (GK.Stage.Ledger and GK.Stage.Ledger.baseline) then
    local b = GK.Stage.Begin()
    if b and b.lost then GK.Stage.Begin{ fresh = true } end
    S.ownScope = true
end

-- Raphael: his root makes a bare human, his look is on the placed object S_GLO_Monitor.
local made = try("spawn", GK.Stage.Spawn, { template = O.template or "acd7060a-43bd-4dc9-b050-9d9b2cf7b7df",
    at = S.at, faction = "neutral", rotation = S.yaw })
S.id = made and made[1]
if not S.id then S.armed = false return { ok = false, error = "the spawn failed", log = S.log } end
log("placed at " .. string.format("%.2f %.2f %.2f", S.at[1], S.at[2], S.at[3]) .. ", yaw " .. string.format("%.0f", S.yaw))

GK.Say.Styles.onlooker = { color = "#FF4040", bold = true, italic = true, prefix = "♦ ", suffix = " ♦", accent = "#FFD040" }
Ext.Timer.WaitForRealtime(300, function()
    if not S.armed then return end
    try("look", GK.Stage.Transform, O.look or "f65becd6-5cd7-4c88-b85e-6dd06b60f7b8", S.id)
end)
Ext.Timer.WaitForRealtime(1500, function()
    if not S.armed then return end
    local line = O.line or "Take your time. I am only watching."
    try("line", GK.Say.Line, S.id, line, { style = "onlooker" })
    S.fired = true
    log("said: " .. line)
end)

return { ok = true, id = S.id, at = S.at, yaw = S.yaw }
