-- The main menu's dancer: one of the menu's own four adventurers leaves the stairs and dances to
-- the right of the menu. Client context, in the main menu (there is no server there).
--
-- The menu is the level SYS_Menuscreen_Act3_B drawn under the UI; its figures are timeline
-- dummies (TLPreviewDummy) of the timeline MM_Accept_Temple, whose TLTransform track writes each
-- one's Transform every frame. A dummy whose ClientTimelineActorControl.Timeline is the null guid
-- is no longer written by the timeline and keeps its animation; its picture is then moved with
-- the visual's own SetWorldTranslate / SetWorldRotate (writing Transform alone moves nothing).
--
-- Fires when the main menu is shown and the menu's camera (the Camera entity that carries the
-- SoundListener) has stood still for `still` seconds: the descent from the splash screen is over.
-- Options, before arming: GKSceneOptions = { menu_dancer = { who = <template uuid>, ahead = 5,
-- right = 2, down = 0.88, facing = 0, sway = 40, period = 1.1, hop = 0.07, still = 0.5 } }
GKScene = GKScene or {}
local old = GKScene.menu_dancer
if old and old.off then pcall(old.off) end

local S = { armed = true, fired = false, log = {} }
GKScene.menu_dancer = S
local O = (GKSceneOptions and GKSceneOptions.menu_dancer) or {}
local NULL = "00000000-0000-0000-0000-000000000000"
local LEVEL = "SYS_Menuscreen_Act3_B"
-- The four adventurers of MM_Accept_Temple, by the level's own template of each.
local ADVENTURERS = {
    "4f0c9e6f-5b24-4b45-b8ad-ab0b1a0e6570", "7f1c14c1-50b2-43c8-93b3-1fcb2c85f435",
    "af5cb07c-331c-4a23-8a33-7917cb5c068f", "0ec5c60c-6dbe-46b4-8f14-f0b62b4bf7be",
}
local WHO = O.who or ADVENTURERS[1]

local function now() return Ext.Utils.MonotonicTime() end
local function note(text) S.log[#S.log + 1] = { ms = now(), text = text } end
local function try(f) local ok, v = pcall(f) if ok then return v end end

local function menuCamera()
    for _, e in ipairs(Ext.Entity.GetAllEntitiesWithComponent("Camera")) do
        if try(function() return e.SoundListener end) then return e end
    end
end

local function dummy()
    for _, e in ipairs(Ext.Entity.GetAllEntitiesWithComponent("TLPreviewDummy")) do
        if try(function() return e.TLPreviewDummy.OriginalCharacterTemplate end) == WHO then return e end
    end
end

local function quatY(degrees)
    local half = math.rad(degrees) / 2
    return { 0, math.sin(half), 0, math.cos(half) }
end

-- Where the dancer stands: `ahead` metres along the camera's look on the ground plane, `right`
-- metres to the picture's right, `down` metres under the camera (the cave floor before it).
local function place(cam)
    local view = cam.Camera.Controller.Camera.ViewMatrix
    local p = cam.Transform.Transform.Translate
    local fx, fz = view[3], view[11]
    local length = math.sqrt(fx * fx + fz * fz)
    fx, fz = fx / length, fz / length
    local rx, rz = view[1], view[9]
    local ahead, right = O.ahead or 5, O.right or 2
    S.spot = { p[1] + fx * ahead + rx * right, p[2] - (O.down or 0.88), p[3] + fz * ahead + rz * right }
    -- turned to the camera: the model's front is its +z
    S.yaw = math.deg(math.atan(p[1] - S.spot[1], p[3] - S.spot[3])) + (O.facing or 0)
end

local function fire(cam)
    local e = dummy()
    if not e then S.error = "no adventurer " .. WHO .. " among the menu's dummies" return end
    if try(function() return e.Level.LevelName end) ~= LEVEL then
        S.error = "the menu level is " .. tostring(try(function() return e.Level.LevelName end)) .. ", the scene knows " .. LEVEL
        return
    end
    S.entity = e
    S.timeline = e.ClientTimelineActorControl.Timeline
    local v = e.Visual.Visual.WorldTransform
    S.home = { pos = { v.Translate[1], v.Translate[2], v.Translate[3] },
               rot = { v.RotationQuat[1], v.RotationQuat[2], v.RotationQuat[3], v.RotationQuat[4] } }
    place(cam)
    e.ClientTimelineActorControl.Timeline = NULL
    S.fired, S.startedAt = true, now()
    note("the adventurer left the stairs and dances at " .. string.format("%.1f %.1f %.1f", S.spot[1], S.spot[2], S.spot[3]))
end

local function dance()
    local e = S.entity
    local visual = e and try(function() return e.Visual.Visual end)
    if not visual then return end
    local t = (now() - S.startedAt) / 1000
    local period = O.period or 1.1
    local phase = 2 * math.pi * t / period
    visual:SetWorldTranslate({ S.spot[1], S.spot[2] + math.abs(math.sin(phase)) * (O.hop or 0.07), S.spot[3] })
    visual:SetWorldRotate(quatY(S.yaw + (O.sway or 40) * math.sin(phase / 2)))
end

local function tick()
    if not S.armed then return end
    if S.fired then dance() return end
    if S.error then return end
    if tostring(Ext.Utils.GetGameState()) ~= "Menu" or GK.Gui.Stuck() ~= "mainmenu" then S.waiting = "the main menu" return end
    local cam = menuCamera()
    if not cam then S.waiting = "the menu's camera" return end
    local p = cam.Transform.Transform.Translate
    local last = S.camAt
    if not last or math.abs(last[1] - p[1]) + math.abs(last[2] - p[2]) + math.abs(last[3] - p[3]) > 0.001 then
        S.camAt, S.camSince, S.waiting = { p[1], p[2], p[3] }, now(), "the camera to stand still"
        return
    end
    if now() - S.camSince < (O.still or 0.5) * 1000 then return end
    S.waiting = nil
    fire(cam)
end

S.subscription = Ext.Events.Tick:Subscribe(function()
    local ok, problem = pcall(tick)
    if not ok then S.error = tostring(problem) end
end)

function S.off()
    S.armed = false
    if S.subscription then pcall(function() Ext.Events.Tick:Unsubscribe(S.subscription) end) S.subscription = nil end
    local e = S.entity
    if e and S.timeline then
        -- given back to its timeline, which writes its place again from the next frame
        pcall(function() e.ClientTimelineActorControl.Timeline = S.timeline end)
        pcall(function()
            e.Visual.Visual:SetWorldTranslate(S.home.pos)
            e.Visual.Visual:SetWorldRotate(S.home.rot)
        end)
        note("the adventurer is given back to the timeline")
    end
    S.entity = nil
    return true
end

note("armed")
return { armed = true, who = WHO }
