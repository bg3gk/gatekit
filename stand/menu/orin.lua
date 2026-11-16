-- Under the ground by the main menu Orin kills a townswoman who cowers before her; when the
-- temple's stone door in the distance begins to open she turns to it, looks, and vanishes as she
-- does in the third act when she drops a disguise: a twist of her ring and her teleportation.
-- Client context, in the main menu (there is no server there).
--
-- Both figures are made on the client with Ext.ClientVisual.CreateDummy and move by the game's own
-- animations, sent to their animation blueprints with Ext.ClientVisual.PlayAnimation; the burst is
-- the game's effect, played with Ext.ClientVisual.PlayEffect (README.md says what each calls).
--   Orin        Trailer_Dream_Orin, the root template her actor is shape-shifted into in the
--               dialogs WYR_OrinsImpersonation_*
--   the victim  Humans_Female_Civilian, in HUM_F_Rig_DFLT_CINE_Fear_Loop_01
--   the kill    Orin's HUM_F_Rig_DWS_CMBT_Skill_Orin_DeathbringerAssault_01_Attack; at its first
--               "Cast" text key the victim plays HUM_F_Rig_DFLT_DIE_Physical_Stationary_Front_01
--               and lies as its last frame leaves her: `settle` seconds before the animation's
--               Duration has passed her AnimationUpdate component is removed, which stops her
--               animation blueprint where it is. (HUM_F_Rig_DFLT_DIE_Generic_Deathpose_01 is no
--               loop to lie in: its frames are fourteen different death poses, one a frame, and
--               played as a loop it throws the body about.)
--   the door    the timeline actor 13045dab-... of MM_Accept_Temple, whose visual is
--               DOOR_Bhaal_Stone_Double_Big_A: the timeline lifts it some seconds after the camera
--               has come down. Its Transform is read every tick; "it opens" is its rising by
--               `door_rise` metres above where it stood when the scene began
--   vanishing   HUM_F_Rig_SCENE_WYR_Orin_TwistRing, then VFX_Cinematic_Orin_Teleportation_01 at
--               her feet, and the figure is removed inside the burst
-- The place before the camera is dark: a torch's light of the level is borrowed as in
-- dancer_new.lua (another torch's) and put back by the off file.
--
-- Fires when the main menu is shown and the menu's camera has stood still for `still` seconds.
-- Options, before arming: GKSceneOptions = { menu_orin = { ahead = 5, right = 1.0, apart = 1.1,
-- down = 0.88, still = 0.5, fear = 1.5, settle = 0.1, door_rise = 0.25, door_wait = 40, turn = 0.7, look = 2.0,
-- burst = 2.2, cover = 0.45, light = <uuid of the level's light to borrow, or false>,
-- light_toward = 1.6, light_up = 1.6 } }
-- State: GKScene.menu_orin = { armed, fired, stage, waiting, error, log, door }.
GKScene = GKScene or {}
local old = GKScene.menu_orin
if old and old.off then pcall(old.off) end

local S = { armed = true, fired = false, stage = "armed", log = {}, door = {} }
GKScene.menu_orin = S
local O = (GKSceneOptions and GKSceneOptions.menu_orin) or {}
local LEVEL = "SYS_Menuscreen_Act3_B"
local ORIN = "c46644db-2b4f-4f17-8c22-20c6c164be65"
local VICTIM = "19dc6cab-aa38-4f09-b9cf-546e1c0ecb8a"
local DOOR = "13045dab-efa8-48ff-986d-34b631f5ff69"
-- Another torch's light than dancer_new.lua borrows (a point light of radius 6 with LightMovement),
-- so that the two scenes can stand together.
local LIGHT = "28c8bd46-35d5-4955-9313-8e21917b8977"
if O.light ~= nil then LIGHT = O.light end
local FEAR = "122199f3-ca4d-6b06-bca7-7f276af20883"
local DIE = "5bb1d9e7-140a-3b91-8b9b-7787910cd33d"
local ATTACK = "2f6c18c9-17bc-8013-5602-155c0c2eb692"
local RING = "57eb927b-c716-c3dc-934c-afc55017fec9"
local BURST = "VFX_Cinematic_Orin_Teleportation_01"

local function now() return Ext.Utils.MonotonicTime() end
local function note(text) S.log[#S.log + 1] = { ms = now(), text = text } end
local function try(f) local ok, v = pcall(f) if ok then return v end end
local function stage(name) S.stage, S.since = name, now() note(name) end
local function elapsed() return (now() - S.since) / 1000 end

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

local function levelLight(uuid)
    for _, e in ipairs(Ext.Entity.GetAllEntitiesWithComponent("Light")) do
        if tostring(e.Light.UUID) == uuid then return e end
    end
end

-- The temple's door: the dummy the menu's timeline moves for its scenery actor.
local function findDoor()
    for _, e in ipairs(Ext.Entity.GetAllEntitiesWithComponent("TimelineActorData")) do
        if tostring(e.TimelineActorData.Actor) == DOOR then return e end
    end
end

local function quatY(degrees)
    local half = math.rad(degrees) / 2
    return { 0, math.sin(half), 0, math.cos(half) }
end

-- The yaw that turns a figure standing at `from` to `to`: a figure of these two templates with
-- no turn looks along -z.
local function facing(from, to) return math.deg(math.atan(from[1] - to[1], from[3] - to[3])) end

-- A figure with a playing blueprint follows its entity's Transform, so both are written.
local function put(entity, at, yaw)
    local transform = entity.Transform.Transform
    if at then transform.Translate = at end
    if yaw then transform.RotationQuat = quatY(yaw) end
    entity.Transform.Transform = transform
    local visual = try(function() return entity.Visual.Visual end)
    if visual then
        if at then visual:SetWorldTranslate(at) end
        if yaw then visual:SetWorldRotate(quatY(yaw)) end
    end
end

-- Animation resources become short names of a figure: linked into its plain animation sets, each
-- under its own id.
local function link(entity, resources)
    local sets = 0
    for _, w in ipairs(entity.AnimationWaterfall.Waterfall) do
        if tostring(w.Type) == "Visual" and tostring(w.Slot) == "" then
            local ok = pcall(function()
                local subset = Ext.Resource.Get(w.Resource, "AnimationSet").AnimationBank.AnimationSubSets[""]
                for _, id in ipairs(resources) do subset.Animation[id] = { ID = id, flags = {} } end
            end)
            if ok then sets = sets + 1 end
        end
    end
    return sets
end

-- What the animation blueprints raised this frame for a figure: its text keys, and whether a
-- gameplay event came (an animation that is over raises them).
local function raised(entity)
    local keys, ended = {}, false
    pcall(function()
        for _, e in ipairs(S.textkeys.AnimationTextKeyEventsSingleton.Events) do
            if e.Entity == entity then keys[tostring(e.Event.ID)] = true end
        end
        for _, e in ipairs(S.gameplay.AnimationGameplayEventsSingleton.Events) do
            if e.Entity == entity then ended = true end
        end
    end)
    return keys, ended
end

local function place(cam)
    local view = cam.Camera.Controller.Camera.ViewMatrix
    local p = cam.Transform.Transform.Translate
    local fx, fz = view[3], view[11]
    local length = math.sqrt(fx * fx + fz * fz)
    fx, fz = fx / length, fz / length
    local rx, rz = view[1], view[9]
    local ahead, right, apart = O.ahead or 5, O.right or 1.0, O.apart or 1.1
    local y = p[2] - (O.down or 0.88)
    S.orinAt = { p[1] + fx * ahead + rx * right, y, p[3] + fz * ahead + rz * right }
    S.victimAt = { p[1] + fx * ahead + rx * (right + apart), y, p[3] + fz * ahead + rz * (right + apart) }
    S.orinYaw = facing(S.orinAt, S.victimAt)
    local dx, dz = p[1] - S.orinAt[1], p[3] - S.orinAt[3]
    local reach = math.sqrt(dx * dx + dz * dz)
    local toward = O.light_toward or 1.6
    S.lightSpot = { S.orinAt[1] + dx / reach * toward + rx * apart / 2, y + (O.light_up or 1.6), S.orinAt[3] + dz / reach * toward + rz * apart / 2 }
end

local function moveLight(light, to)
    local transform = light.Transform.Transform
    transform.Translate = to
    light.Transform.Transform = transform
end

local function borrowLight()
    if not LIGHT then return end
    local light = levelLight(LIGHT)
    if not light then note("no light " .. LIGHT .. " in the level: the figures stay in the dark") return end
    if not try(function() return light.LightMovement end) then
        note("the light " .. LIGHT .. " carries no LightMovement and would not follow its Transform")
        return
    end
    local at = light.Transform.Transform.Translate
    S.light, S.lightHome = light, { at[1], at[2], at[3] }
    moveLight(light, S.lightSpot)
    note("a light of the level is put before the figures")
end

local function fire(cam)
    if not (Ext.ClientVisual and Ext.ClientVisual.CreateDummy and Ext.ClientVisual.PlayAnimation and Ext.ClientVisual.PlayEffect) then
        S.error = "this Script Extender build has no Ext.ClientVisual.PlayAnimation or PlayEffect"
        return
    end
    local name = level()
    if name ~= LEVEL then
        S.error = "the menu level is " .. tostring(name) .. ", the scene knows " .. LEVEL
        return
    end
    S.gameplay = Ext.Entity.GetAllEntitiesWithComponent("AnimationGameplayEventsSingleton")[1]
    S.textkeys = Ext.Entity.GetAllEntitiesWithComponent("AnimationTextKeyEventsSingleton")[1]
    if not (S.gameplay and S.textkeys) then S.error = "the animation blueprint system's event singletons are not there" return end
    place(cam)
    S.orin = Ext.ClientVisual.CreateDummy(ORIN, S.orinAt, quatY(S.orinYaw))
    S.victim = Ext.ClientVisual.CreateDummy(VICTIM, S.victimAt, quatY(S.orinYaw + 180))
    if not (S.orin and S.victim) then S.error = "the figures were not made" return end
    S.fired = true
    stage("loading")
end

local function loaded(entity)
    return try(function() return entity.Visual.Visual ~= nil and entity.SceneAttach ~= nil and entity.AnimationWaterfall ~= nil end)
end

local function readDoor()
    local door = S.doorEntity
    return door and try(function() return door.Transform.Transform.Translate[2] end)
end

local function removeOrin(why)
    if S.orin then
        pcall(Ext.ClientVisual.DestroyDummy, S.orin)
        note("Orin is removed: " .. why)
    end
    S.orin = nil
end

local function removeBurst()
    if S.burst then pcall(Ext.ClientVisual.StopEffect, S.burst, "Destroy") end
    S.burst = nil
end

local function clear(why)
    if S.light and S.lightHome then
        pcall(moveLight, S.light, S.lightHome)
        note("the light is back in its place")
    end
    S.light = nil
    removeBurst()
    removeOrin(why)
    if S.victim then
        pcall(Ext.ClientVisual.DestroyDummy, S.victim)
        note("the victim is removed: " .. why)
    end
    S.victim = nil
end

local steps = {}

function steps.loading()
    if not (loaded(S.orin) and loaded(S.victim)) then return end
    if link(S.victim, { FEAR, DIE }) == 0 or link(S.orin, { ATTACK, RING }) == 0 then
        S.error = "no animation set of the figures took the links"
        return
    end
    put(S.orin, S.orinAt, S.orinYaw)
    put(S.victim, S.victimAt, S.orinYaw + 180)
    borrowLight()
    S.doorEntity = findDoor()
    S.door.found = S.doorEntity ~= nil
    S.door.start = readDoor()
    if not S.door.found then note("the level has no door of the timeline actor " .. DOOR .. ": she will turn right after the kill") end
    Ext.ClientVisual.PlayAnimation(S.victim, {}, { FEAR })
    stage("fear")
end

function steps.fear()
    if elapsed() < (O.fear or 1.5) then return end
    Ext.ClientVisual.PlayAnimation(S.orin, { ATTACK })
    stage("attack")
end

-- The victim falls by the death animation alone and is held in its last frame: with her
-- AnimationUpdate component gone nothing samples her blueprint again, so the body rests.
local function fall()
    S.struck = true
    S.fellAt = now()
    S.fallFor = try(function() return Ext.Resource.Get(DIE, "Animation").Duration end) or 2.6
    Ext.ClientVisual.PlayAnimation(S.victim, { DIE })
end

local function settle()
    if not (S.fellAt and S.victim) or S.settled then return end
    if (now() - S.fellAt) / 1000 < S.fallFor - (O.settle or 0.1) then return end
    S.settled = pcall(function() S.victim:RemoveComponent("AnimationUpdate") end)
    note(S.settled and "the body lies still" or "the body could not be stilled")
end

function steps.attack()
    local keys, ended = raised(S.orin)
    if keys.Cast and not S.struck then
        fall()
        note("the first blow lands: the victim falls")
    end
    if ended then
        if not S.struck then
            fall()
            note("the attack ended with no Cast text key seen: the victim falls now")
        end
        stage("killed")
    end
end

-- The door opens when the level says so: its Transform has risen above where it stood.
function steps.killed()
    local y = readDoor()
    S.door.now = y
    local opens = y and S.door.start and y - S.door.start >= (O.door_rise or 0.25)
    if opens then
        S.door.openedBy = "its Transform rose " .. string.format("%.2f", y - S.door.start) .. " m"
    elseif not S.door.found then
        S.door.openedBy = "no door in the level"
    elseif elapsed() > (O.door_wait or 40) then
        S.door.openedBy = "the door did not move in " .. (O.door_wait or 40) .. " s: it stood open when the scene began"
    else
        S.waiting = "the door to open"
        return
    end
    S.waiting = nil
    note("the door: " .. S.door.openedBy)
    local at = try(function() return S.doorEntity.Transform.Transform.Translate end)
    S.turnFrom = S.orinYaw
    S.turnTo = at and facing(S.orinAt, at) or S.orinYaw + 180
    -- the shorter way round
    local delta = (S.turnTo - S.turnFrom + 540) % 360 - 180
    S.turnTo = S.turnFrom + delta
    stage("turning")
end

function steps.turning()
    local part = math.min(1, elapsed() / (O.turn or 0.7))
    put(S.orin, nil, S.turnFrom + (S.turnTo - S.turnFrom) * part)
    if part >= 1 then stage("looking") end
end

function steps.looking()
    if elapsed() < (O.look or 2.0) then return end
    Ext.ClientVisual.PlayAnimation(S.orin, { RING })
    stage("ring")
end

function steps.ring()
    local _, ended = raised(S.orin)
    if elapsed() < (O.burst or 2.2) and not ended then return end
    S.burst = Ext.ClientVisual.PlayEffect(BURST, S.orinAt, nil, 1.0, S.orin)
    if not S.burst then note("the effect " .. BURST .. " was not made: she goes without it") end
    stage("burst")
end

function steps.burst()
    if elapsed() < (O.cover or 0.45) then return end
    removeOrin("she vanished")
    stage("gone")
end

-- The burst plays out; its entity is taken away when its flags say Stopped.
function steps.gone()
    if not S.burst then S.done = true return end
    local stopped = try(function()
        for _, flag in ipairs(S.burst.Effect.Flags) do if tostring(flag) == "Stopped" then return true end end
        return false
    end)
    if stopped ~= false then
        removeBurst()
        note("the burst is over")
        S.done = true
    end
end

local function tick()
    if not S.armed then return end
    local menu = tostring(Ext.Utils.GetGameState()) == "Menu"
    if S.fired then
        if not menu then
            if S.orin or S.victim or S.light then clear("the menu is left") S.done = true end
            return
        end
        if S.error then return end
        settle()
        local step = steps[S.stage]
        if step then step() end
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
    S.stage = "off"
    return true
end

note("armed")
return { armed = true, orin = ORIN, victim = VICTIM, door = DOOR }
