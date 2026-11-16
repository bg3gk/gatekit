-- Scene "onlooker" (the video stand): behind the back of the character being created Raphael sits
-- at a small table and plays lanceboard, as he does with Mol in Last Light (the story goal
-- Act2_HAV_MolsDeal and his behaviour script: a seat taken with Osi.Use, then the looping animation
-- CUST_PlayLanceBoard_01). Server context, in character creation (the level SYS_CC_I).
--
-- The character on the screen is a client-only dummy (ClientCCDummy; the client entity named by
-- ClientCCDummyDefinition.Dummy of the definition that carries CCCharacterDefinition); the server
-- does not hold it, so the place is given in the level's own coordinates: the dummy stands at
-- DUMMY and the creation camera looks at it from CAMERA. A character can be created only on the
-- dummy's own platform (up to about 1.6 m behind it); an item anywhere. Raphael is created on the
-- platform and takes the seat from there.
-- GKSceneOptions = { onlooker = { back = metres behind the dummy to the table, side = metres to the
--     picture's right, seat = metres from the table to the stool, play = false for sitting only } }
-- Arm:    ./gatekit --instance <copy> exec server "$(cat stand/creation/onlooker.lua)"
-- Disarm: ./gatekit --instance <copy> exec server "$(cat stand/creation/onlooker_off.lua)"
-- State:  GKScene.onlooker = { armed, fired, seated, log, id, made = { table, board, stool } }
GKScene = GKScene or {}
local old = GKScene.onlooker
if old and old.armed then return { ok = true, already = true, id = old.id } end

local O = (GKSceneOptions and GKSceneOptions.onlooker) or {}
local LEVEL = "SYS_CC_I"
local DUMMY = { 9.99, 0.89, 111.14 }
local CAMERA = { 7.43, 1.94, 113.54 }
local RAPHAEL, LOOK = "acd7060a-43bd-4dc9-b050-9d9b2cf7b7df", "f65becd6-5cd7-4c88-b85e-6dd06b60f7b8"
local TABLE = "dab06534-5571-4251-a959-6c26d9d0da2d"   -- INTERACT_FUR_GEN_Tavern_Table_Round_Small_A
local BOARD = "be905812-03f2-4803-b4d1-65300d4d8d14"   -- PUZ_GEN_Lanceboard_Board_A_Posed_B
local STOOL = "8830c715-7e93-4910-b9a5-c042552e9769"   -- FUR_GEN_Tavern_Stool_Large_A
local TABLE_TOP = 0.95
local PLAY = { "fcc00260-4b6c-45cc-ba51-638c8ee21959",  -- CUST_PlayLanceBoard_01_Start
               "7811e4d3-7fb6-4217-aec2-4710c3fe6552",  -- CUST_PlayLanceBoard_01_Loop
               "c17e8e76-947f-4b93-82ec-32a5f6932693" } -- CUST_PlayLanceBoard_01_End
local S = { armed = true, fired = false, seated = false, log = {}, made = {} }
GKScene.onlooker = S

local function log(text) S.log[#S.log + 1] = { ms = Ext.Utils.MonotonicTime(), text = text } end
local function try(what, fn, ...)
    local ok, v = pcall(fn, ...)
    if not ok then log("failed: " .. what .. ": " .. tostring(v)) end
    return ok and v or nil
end
local function later(ms, fn)
    Ext.Timer.WaitForRealtime(ms, function() if S.armed then try("a step", fn) end end)
end

local level = Osi.GetRegion(Osi.GetHostCharacter())
if level ~= LEVEL then
    S.armed, S.error = false, "the level is " .. tostring(level) .. ", character creation is " .. LEVEL
    return { ok = false, error = S.error }
end

-- Places as the camera sees them: `back` metres further from the camera than the dummy, `side`
-- metres to the picture's right.
local fx, fz = DUMMY[1] - CAMERA[1], DUMMY[3] - CAMERA[3]
local length = math.sqrt(fx * fx + fz * fz)
fx, fz = fx / length, fz / length
local rx, rz = fz, -fx
local function place(back, side, up)
    return { DUMMY[1] + fx * back + rx * side, DUMMY[2] + (up or 0), DUMMY[3] + fz * back + rz * side }
end
local back, side, seat = O.back or 2.2, O.side or 1.0, O.seat or 0.9
-- The stool stands beyond the table. A sitter faces against the stool's own facing, so the stool
-- is turned away from the camera and Raphael faces the table and the camera.
local toCamera = math.deg(math.atan(fx, fz))

if not (GK.Stage.Ledger and GK.Stage.Ledger.baseline) then
    local b = GK.Stage.Begin()
    if b and b.lost then GK.Stage.Begin{ fresh = true } end
    S.ownScope = true
end
local function make(key, spec)
    local made = try("spawn " .. key, GK.Stage.Spawn, spec)
    S.made[key] = made and made[1]
    return S.made[key]
end
make("table", { template = TABLE, at = place(back, side), exact = true })
make("board", { template = BOARD, at = place(back, side, TABLE_TOP), exact = true })
local stool = make("stool", { template = STOOL, at = place(back + seat, side), exact = true, rotation = O.turn or toCamera })
S.id = make("raphael", { template = O.template or RAPHAEL, at = place(1.6, 0.6), faction = "neutral" })
if not (S.id and stool) then
    S.armed = false
    return { ok = false, error = "the spawn failed", log = S.log }
end
log("the table " .. back .. " m behind the dummy, " .. side .. " m to the right")

-- Raphael's root makes a bare human; his look is on the placed object S_GLO_Monitor.
later(300, function() GK.Stage.Transform(O.look or LOOK, S.id) end)
later(1200, function() Osi.Use(S.id, stool, 1, 1, "GK_Stand_Onlooker_Sat") end)
later(4500, function()
    local x, _, z = Osi.GetPosition(S.id)
    local sx, _, sz = Osi.GetPosition(stool)
    S.seated = math.abs(x - sx) < 0.3 and math.abs(z - sz) < 0.3
    log(S.seated and "seated" or "not on the stool")
    if S.seated and O.play ~= false then
        Osi.PlayLoopingAnimation(S.id, PLAY[1], PLAY[2], PLAY[3], "", "", "", "")
        log("plays lanceboard")
    end
    S.fired = true
end)

return { ok = true, id = S.id, made = S.made }
