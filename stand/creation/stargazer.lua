-- Scene "stargazer" (the video stand): far behind the character being created a tiefling looks
-- through a telescope, the Emerald Grove's stargazer (S_GLO_Stargazer: the root template
-- Tieflings_Female_Nadira, the item TOOL_GEN_Telescope_A, the behaviour timeline
-- BHVR_WRLD_DEN_TelescopeStargazing with both as its speakers). Server context, in character
-- creation (the level SYS_CC_I).
--
-- A character can be created only on the dummy's own platform, so she is created there and moved.
-- GKSceneOptions = { stargazer = { back = metres behind the dummy, side = metres to the picture's
--     right, up = metres above the dummy's feet, step = metres from the telescope to her,
--     turn = degrees added to the facing, timeline = true to start the behaviour timeline as well
--     (the server accepts it; nothing of it is seen on the creation screen) } }
-- Arm:    ./gatekit --instance <copy> exec server "$(cat stand/creation/stargazer.lua)"
-- Disarm: ./gatekit --instance <copy> exec server "$(cat stand/creation/stargazer_off.lua)"
-- State:  GKScene.stargazer = { armed, fired, log, id, telescope, at, dialog = { success, instance } }
GKScene = GKScene or {}
local old = GKScene.stargazer
if old and old.armed then return { ok = true, already = true, id = old.id } end

local O = (GKSceneOptions and GKSceneOptions.stargazer) or {}
local LEVEL = "SYS_CC_I"
local DUMMY = { 9.99, 0.89, 111.14 }
local CAMERA = { 7.43, 1.94, 113.54 }
local NADIRA = "8d6c664f-8743-4bd5-b184-585217b09eb3"     -- Tieflings_Female_Nadira
local TELESCOPE = "7dc63d8d-c4b7-49a1-906d-9dff56e805f8"  -- TOOL_GEN_Telescope_A
local LOOKING = "BHVR_WRLD_DEN_TelescopeStargazing_3dd299d8-4312-0a35-ac7d-221052a552e4"
local HER = { "9d894c26-9c26-7a96-240b-01570b181a60",   -- HUM_F_Rig_WRLD_DFLT_CUST_StargazingTelescope_01_Start
              "350603a8-399b-83a1-16c0-c7203278dce1",   -- ..._Loop
              "848ef5a1-aa33-312c-297b-49ea3ac5daff" }  -- ..._End
local ITS = { "d2f8f0a1-4c7a-08b1-b6a8-d001f1329a01",   -- TOOL_GEN_Telescope_A_Rig_WRLD_DFLT_CUST_StargazingTelescope_01_Start
              "77055ad5-e95e-d0ae-6497-660d5db67338",   -- ..._Loop
              "a9bbb5f5-9b8d-346e-7479-4a07abbf21ff" }  -- ..._End
local NOBODY ="NULL_00000000-0000-0000-0000-000000000000"  -- the bare null guid makes the query fail
local S = { armed = true, fired = false, log = {} }
GKScene.stargazer = S

local function log(text) S.log[#S.log + 1] = { ms = Ext.Utils.MonotonicTime(), text = text } end
local function try(what, fn, ...)
    local ok, a, b = pcall(fn, ...)
    if not ok then log("failed: " .. what .. ": " .. tostring(a)) return nil end
    return a, b
end
local function later(ms, fn)
    Ext.Timer.WaitForRealtime(ms, function() if S.armed then try("a step", fn) end end)
end

local level = Osi.GetRegion(Osi.GetHostCharacter())
if level ~= LEVEL then
    S.armed, S.error = false, "the level is " .. tostring(level) .. ", character creation is " .. LEVEL
    return { ok = false, error = S.error }
end

local fx, fz = DUMMY[1] - CAMERA[1], DUMMY[3] - CAMERA[3]
local length = math.sqrt(fx * fx + fz * fz)
fx, fz = fx / length, fz / length
local rx, rz = fz, -fx
local back, side, up = O.back or 5.5, O.side or -1.1, O.up or 0
local function place(b, s) return { DUMMY[1] + fx * b + rx * s, DUMMY[2] + up, DUMMY[3] + fz * b + rz * s } end
S.at = place(back, side)
-- She stands a step to the picture's left of the telescope and looks along the picture, to the right.
local stand = place(back, side - (O.step or 0.55))
local facing = math.deg(math.atan(rx, rz)) + (O.turn or 0)

if not (GK.Stage.Ledger and GK.Stage.Ledger.baseline) then
    local b = GK.Stage.Begin()
    if b and b.lost then GK.Stage.Begin{ fresh = true } end
    S.ownScope = true
end
local made = try("spawn the telescope", GK.Stage.Spawn, { template = TELESCOPE, at = S.at, exact = true, rotation = facing })
S.telescope = made and made[1]
made = try("spawn the stargazer", GK.Stage.Spawn, { template = NADIRA, at = { DUMMY[1] + fx * 1.6 + rx * 0.6, DUMMY[2], DUMMY[3] + fz * 1.6 + rz * 0.6 }, faction = "neutral" })
S.id = made and made[1]
if not (S.id and S.telescope) then
    S.armed = false
    return { ok = false, error = "the spawn failed", log = S.log }
end
later(300, function()
    Osi.TeleportToPosition(S.id, stand[1], stand[2], stand[3], "", 0, 0, 0, 0, 0)
    Osi.SteerTo(S.id, S.telescope, 1)
end)
later(1500, function()
    local x, y, z = Osi.GetPosition(S.id)
    log(string.format("stands at %.2f %.2f %.2f", x, y, z))
    -- The looking pose has no short name of its own: the animations are named by their resources,
    -- the ones the timeline's animation track plays on her and on the telescope.
    try("her animation", Osi.PlayLoopingAnimation, S.id, HER[1], HER[2], HER[3], "", "", "", "")
    try("the telescope's animation", Osi.PlayLoopingAnimation, S.telescope, ITS[1], ITS[2], ITS[3], "", "", "", "")
    if O.timeline then
        local success, instance = try("the timeline", Osi.StartBehaviorDialog_Internal, LOOKING, 0, S.id, S.telescope,
            NOBODY, NOBODY, NOBODY, NOBODY, NOBODY, NOBODY, NOBODY)
        S.dialog = { success = success, instance = instance }
        log("the timeline: success " .. tostring(success) .. ", instance " .. tostring(instance))
    end
    log("looks through the telescope")
    S.fired = true
end)
return { ok = true, id = S.id, telescope = S.telescope, at = S.at }
