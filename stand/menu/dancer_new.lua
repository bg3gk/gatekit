-- The main menu's dancer as a figure of its own: a new figure is made to the right of the menu,
-- lit, and dances there; the menu's own adventurers stay on the stairs.
-- Client context, in the main menu (there is no server there).
--
-- The figure is made on the client with Ext.ClientVisual.CreateDummy, which builds a timeline
-- dummy from a character root template with the game's own functions (README.md says what that
-- needs). No timeline owns it: the scene moves it with the visual's SetWorldTranslate /
-- SetWorldRotate. The place before the camera is dark, so one of the level's own lights is
-- borrowed: a light that carries LightMovement is put where its Transform says every frame, so
-- writing its Transform moves it; it is put between the figure and the camera and goes back to
-- its place when the scene is disarmed.
--
-- Fires when the main menu is shown and the menu's camera (the Camera entity that carries the
-- SoundListener) has stood still for `still` seconds: the descent from the splash screen is over.
-- Options, before arming: GKSceneOptions = { menu_dancer_new = { who = <root template uuid>,
-- ahead = 5, right = 2, down = 0.88, facing = 0, sway = 40, period = 1.1, hop = 0.07, still = 0.5,
-- light = <uuid of the level's light to borrow, or false>, light_toward = 1.6, light_up = 1.6 } }
GKScene = GKScene or {}
local old = GKScene.menu_dancer_new
if old and old.off then pcall(old.off) end

local S = { armed = true, fired = false, log = {} }
GKScene.menu_dancer_new = S
local O = (GKSceneOptions and GKSceneOptions.menu_dancer_new) or {}
local LEVEL = "SYS_Menuscreen_Act3_B"
local WHO = O.who or "8bdc382b-a31e-4c1c-8987-656ceb333f05" -- Vlaakith, a root template with its own look
-- A torch's light of the level (a point light of radius 6 with LightMovement), far from the picture's middle.
local LIGHT = "213b0683-f7d3-4d40-8259-f6c0e046e503"
if O.light ~= nil then LIGHT = O.light end

local function now() return Ext.Utils.MonotonicTime() end
local function note(text) S.log[#S.log + 1] = { ms = now(), text = text } end
local function try(f) local ok, v = pcall(f) if ok then return v end end

local function menuCamera()
    for _, e in ipairs(Ext.Entity.GetAllEntitiesWithComponent("Camera")) do
        if try(function() return e.SoundListener end) then return e end
    end
end

-- The level the client is in, read from any of the menu's own timeline dummies.
local function level()
    for _, e in ipairs(Ext.Entity.GetAllEntitiesWithComponent("TLPreviewDummy")) do
        local name = try(function() return e.Level.LevelName end)
        if name then return name end
    end
end

local function levelLight(uuid)
    for _, e in ipairs(Ext.Entity.GetAllEntitiesWithComponent("Light")) do
        if tostring(e.Light.UUID) == uuid then return e end
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
    -- the light stands between the figure and the camera, at the height of its chest
    local dx, dz = p[1] - S.spot[1], p[3] - S.spot[3]
    local reach = math.sqrt(dx * dx + dz * dz)
    local toward = O.light_toward or 1.6
    S.lightSpot = { S.spot[1] + dx / reach * toward, S.spot[2] + (O.light_up or 1.6), S.spot[3] + dz / reach * toward }
end

local function moveLight(light, to)
    local transform = light.Transform.Transform
    transform.Translate = to
    light.Transform.Transform = transform
end

local function borrowLight()
    if not LIGHT then return end
    local light = levelLight(LIGHT)
    if not light then note("no light " .. LIGHT .. " in the level: the figure stays in the dark") return end
    if not try(function() return light.LightMovement end) then
        note("the light " .. LIGHT .. " carries no LightMovement and would not follow its Transform")
        return
    end
    local at = light.Transform.Transform.Translate
    S.light, S.lightHome = light, { at[1], at[2], at[3] }
    moveLight(light, S.lightSpot)
    note("a light of the level is put before the figure")
end

local function fire(cam)
    if not (Ext.ClientVisual and Ext.ClientVisual.CreateDummy) then
        S.error = "this Script Extender build has no Ext.ClientVisual.CreateDummy"
        return
    end
    local name = level()
    if name ~= LEVEL then
        S.error = "the menu level is " .. tostring(name) .. ", the scene knows " .. LEVEL
        return
    end
    place(cam)
    S.entity = Ext.ClientVisual.CreateDummy(WHO, S.spot, quatY(S.yaw))
    if not S.entity then S.error = "no figure was made from the template " .. WHO return end
    borrowLight()
    S.fired, S.startedAt = true, now()
    note("a new figure dances at " .. string.format("%.1f %.1f %.1f", S.spot[1], S.spot[2], S.spot[3]))
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

local function clear(why)
    if S.light and S.lightHome then
        pcall(moveLight, S.light, S.lightHome)
        note("the light is back in its place")
    end
    S.light = nil
    if S.entity then
        pcall(Ext.ClientVisual.DestroyDummy, S.entity)
        note("the figure is removed: " .. why)
    end
    S.entity = nil
end

local function tick()
    if not S.armed then return end
    local menu = tostring(Ext.Utils.GetGameState()) == "Menu"
    if S.fired then
        if menu then dance() elseif S.entity then clear("the menu is left") S.done = true end
        return
    end
    if S.error then return end
    if not menu or GK.Gui.Stuck() ~= "mainmenu" then S.waiting = "the main menu" return end
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
    clear("disarmed")
    return true
end

note("armed")
return { armed = true, who = WHO }
