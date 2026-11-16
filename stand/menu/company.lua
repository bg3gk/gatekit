-- The splash screen's company: the Emperor in his mind flayer form and several intellect devourers
-- stand beside the statue on the "press any key" screen and look toward the city.
-- Client context, armed while the splash screen stands (`gatekit --instance <copy> up --splash`).
--
-- Each is a figure of the scene's own, made with Ext.ClientVisual.CreateDummy from a character
-- root template (README.md, "A figure of the scene's own"), and plays an idle animation of its own
-- kind as a loop (Ext.ClientVisual.PlayAnimation; the resource is linked into the figure's plain
-- animation sets under its own id):
--   the Emperor   Mindflayer_Emperor, MFL_M_Rig_DFLT_IDLE_Random_Peace_02
--   a devourer    Intellect_Devourer, INTDEV_Rig_DFLT_IDLE_Random_Peace_01
-- A place is given from the splash's camera as statue.lua gives its own: `ahead` metres along its
-- look on the ground plane, `right` metres to the picture's right, `down` metres under it. The
-- statue stands 4.7 m ahead and 2.2 m to the right; the company stands on the grass to both sides
-- of it, clear of the text in the picture's middle.
-- The city is in the picture, straight ahead of the camera: `face = "city"` turns each along the
-- camera's look, its back to the watcher; `face = "statue"` turns each to the statue.
-- The splash screen is in daylight and no light is borrowed: dancer_new.lua and orin.lua keep
-- their two torch lights.
-- The figures are removed the moment the splash screen is passed.
-- Options, before arming: GKSceneOptions = { menu_company = { face = "city", turn = 0,
-- members = { { who = <root template>, loop = <animation resource>, ahead, right, down, turn, scale } } } }
-- State: GKScene.menu_company = { armed, fired, waiting, error, done, members, log }.
GKScene = GKScene or {}
local old = GKScene.menu_company
if old and old.off then pcall(old.off) end

local S = { armed = true, fired = false, log = {}, members = {} }
GKScene.menu_company = S
local O = (GKSceneOptions and GKSceneOptions.menu_company) or {}
local LEVEL = "SYS_Menuscreen_Act3_B"
local EMPEROR = "6efb2704-a025-49e0-ba9f-2b4f62dd2195"
local DEVOURER = "5620024c-5d1b-477d-ace8-43e2275f6158"
local EMPEROR_IDLE = "01c90bcf-bf1f-1e05-a0c9-946b8be32383"
local DEVOURER_IDLE = "31ea1a18-02da-d830-7429-e60d308d1684"
local STATUE = { ahead = 4.7, right = 2.2 }
local MEMBERS = O.members or {
    { who = EMPEROR, loop = EMPEROR_IDLE, ahead = 5.4, right = 0.9, down = 2.75 },
    { who = DEVOURER, loop = DEVOURER_IDLE, ahead = 5.0, right = 1.75, down = 2.6, turn = -10 },
    { who = DEVOURER, loop = DEVOURER_IDLE, ahead = 4.1, right = 1.0, down = 2.45, turn = 12 },
    { who = DEVOURER, loop = DEVOURER_IDLE, ahead = 4.3, right = 3.3, down = 2.3, turn = -15 },
}

local function now() return Ext.Utils.MonotonicTime() end
local function note(text) S.log[#S.log + 1] = { ms = now(), text = text } end
local function try(f) local ok, v = pcall(f) if ok then return v end end

local function menuCamera()
    for _, e in ipairs(Ext.Entity.GetAllEntitiesWithComponent("Camera")) do
        if try(function() return e.SoundListener end) then return e end
    end
end

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

-- A figure with a playing blueprint follows its entity's Transform, so both are written.
local function put(entity, at, yaw, scale)
    local transform = entity.Transform.Transform
    transform.Translate = at
    transform.RotationQuat = quatY(yaw)
    if scale then transform.Scale = { scale, scale, scale } end
    entity.Transform.Transform = transform
    local visual = try(function() return entity.Visual.Visual end)
    if visual then
        visual:SetWorldTranslate(at)
        visual:SetWorldRotate(quatY(yaw))
        if scale then visual:SetWorldScale({ scale, scale, scale }) end
    end
end

local function link(entity, resource)
    local sets = 0
    for _, w in ipairs(entity.AnimationWaterfall.Waterfall) do
        if tostring(w.Type) == "Visual" and tostring(w.Slot) == "" then
            local ok = pcall(function()
                Ext.Resource.Get(w.Resource, "AnimationSet").AnimationBank.AnimationSubSets[""].Animation[resource] = { ID = resource, flags = {} }
            end)
            if ok then sets = sets + 1 end
        end
    end
    return sets
end

local function loaded(entity)
    return try(function() return entity.Visual.Visual ~= nil and entity.SceneAttach ~= nil and entity.AnimationWaterfall ~= nil end)
end

local function fire(cam)
    if not (Ext.ClientVisual and Ext.ClientVisual.CreateDummy and Ext.ClientVisual.PlayAnimation) then
        S.error = "this Script Extender build has no Ext.ClientVisual.CreateDummy or PlayAnimation"
        return
    end
    local name = level()
    if name ~= LEVEL then
        S.error = "the menu level is " .. tostring(name) .. ", the scene knows " .. LEVEL
        return
    end
    local view = cam.Camera.Controller.Camera.ViewMatrix
    local p = cam.Transform.Transform.Translate
    local fx, fz = view[3], view[11]
    local length = math.sqrt(fx * fx + fz * fz)
    fx, fz = fx / length, fz / length
    local rx, rz = view[1], view[9]
    local statue = { p[1] + fx * STATUE.ahead + rx * STATUE.right, 0, p[3] + fz * STATUE.ahead + rz * STATUE.right }
    for i, m in ipairs(MEMBERS) do
        local at = { p[1] + fx * m.ahead + rx * m.right, p[2] - m.down, p[3] + fz * m.ahead + rz * m.right }
        -- a figure with no turn looks along -z
        local yaw
        if (O.face or "city") == "statue" then
            yaw = math.deg(math.atan(at[1] - statue[1], at[3] - statue[3]))
        else
            yaw = math.deg(math.atan(-fx, -fz))
        end
        yaw = yaw + (O.turn or 0) + (m.turn or 0)
        local entity = Ext.ClientVisual.CreateDummy(m.who, at, quatY(yaw))
        S.members[i] = { entity = entity, at = at, yaw = yaw, loop = m.loop, scale = m.scale, who = m.who }
        if not entity then note("no figure was made from the template " .. tostring(m.who)) end
    end
    S.fired = true
    note("the company is made: " .. #S.members .. " figures, facing the " .. (O.face or "city"))
end

-- Each figure begins its idle once it is loaded.
local function animate()
    local waiting = 0
    for _, m in ipairs(S.members) do
        if m.entity and not m.playing then
            if loaded(m.entity) then
                put(m.entity, m.at, m.yaw, m.scale)
                if m.loop and link(m.entity, m.loop) > 0 then
                    Ext.ClientVisual.PlayAnimation(m.entity, {}, { m.loop })
                    m.playing = "loop"
                else
                    m.playing = "the blueprint's own idle"
                end
            else
                waiting = waiting + 1
            end
        end
    end
    S.waiting = waiting > 0 and (waiting .. " figures to load") or nil
end

local function remove(why)
    local removed = 0
    for _, m in ipairs(S.members) do
        if m.entity then
            pcall(Ext.ClientVisual.DestroyDummy, m.entity)
            m.entity = nil
            removed = removed + 1
        end
    end
    if removed > 0 then note(removed .. " figures are removed: " .. why) end
end

local function tick()
    if not S.armed then return end
    local splash = GK.Gui.Stuck() == "splash"
    if S.fired then
        if splash then animate() elseif not S.done then remove("the splash screen is passed") S.done = true end
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
return { armed = true, members = #MEMBERS, face = O.face or "city" }
