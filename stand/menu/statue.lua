-- Someone behind the statue on the splash screen: a new figure stands half hidden behind the
-- statue to the picture's right and leans out from time to time.
-- Client context, armed while the splash screen stands (`gatekit --instance <copy> up --splash`).
--
-- The figure is not one of the menu's own: it is made on the client with
-- Ext.ClientVisual.CreateDummy, which builds a timeline dummy from a character root template with
-- the game's own functions (README.md says what that needs). No timeline owns it, so the scene
-- moves it with the visual's SetWorldTranslate / SetWorldRotate, and removes it the moment the
-- splash screen is passed.
-- Options, before arming: GKSceneOptions = { menu_statue = { who = <root template uuid>,
-- ahead = 5.7, right = 2.62, down = 2.7, facing = 0, lean = 0.4, period = 6 } }
GKScene = GKScene or {}
local old = GKScene.menu_statue
if old and old.off then pcall(old.off) end

local S = { armed = true, fired = false, log = {} }
GKScene.menu_statue = S
local O = (GKSceneOptions and GKSceneOptions.menu_statue) or {}
local LEVEL = "SYS_Menuscreen_Act3_B"
local WHO = O.who or "8bdc382b-a31e-4c1c-8987-656ceb333f05" -- Vlaakith, a root template with its own look

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

local function quatY(degrees)
    local half = math.rad(degrees) / 2
    return { 0, math.sin(half), 0, math.cos(half) }
end

-- The spot is given from the camera: `ahead` metres along its look on the ground plane, `right`
-- metres to the picture's right, `down` metres under it: behind the statue, which stands 4.7 m
-- ahead and 2.2 m to the right. `S.side` is the picture's right on the ground plane: the lean goes
-- against it, out from behind the statue.
local function place(cam)
    local view = cam.Camera.Controller.Camera.ViewMatrix
    local p = cam.Transform.Transform.Translate
    local fx, fz = view[3], view[11]
    local length = math.sqrt(fx * fx + fz * fz)
    fx, fz = fx / length, fz / length
    local rx, rz = view[1], view[9]
    local ahead, right = O.ahead or 5.7, O.right or 2.62
    S.side = { rx, rz }
    S.spot = { p[1] + fx * ahead + rx * right, p[2] - (O.down or 2.7), p[3] + fz * ahead + rz * right }
    S.yaw = math.deg(math.atan(p[1] - S.spot[1], p[3] - S.spot[3])) + (O.facing or 0)
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
    S.fired, S.startedAt = true, now()
    note("a figure hides behind the statue at " .. string.format("%.1f %.1f %.1f", S.spot[1], S.spot[2], S.spot[3]))
end

local function remove(why)
    if S.entity then
        pcall(Ext.ClientVisual.DestroyDummy, S.entity)
        note("the figure is removed: " .. why)
    end
    S.entity = nil
end

local function peek()
    local visual = S.entity and try(function() return S.entity.Visual.Visual end)
    if not visual then return end
    local t = (now() - S.startedAt) / 1000
    -- out and back once per period, hidden for the rest of it
    local out = math.max(0, math.sin(2 * math.pi * t / (O.period or 6))) * (O.lean or 0.4)
    visual:SetWorldTranslate({ S.spot[1] - S.side[1] * out, S.spot[2], S.spot[3] - S.side[2] * out })
    visual:SetWorldRotate(quatY(S.yaw))
end

local function tick()
    if not S.armed then return end
    local splash = GK.Gui.Stuck() == "splash"
    if S.fired then
        if splash then peek() elseif S.entity then remove("the splash screen is passed") S.done = true end
        return
    end
    if S.error then return end
    if not splash then S.waiting = "the splash screen" return end
    local cam = menuCamera()
    if not cam then S.waiting = "the menu's camera" return end
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
    remove("disarmed")
    return true
end

note("armed")
return { armed = true, who = WHO }
