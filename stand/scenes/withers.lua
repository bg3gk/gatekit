-- Scene "withers" (the video stand): Withers stands by the helm's transponder at the end of the
-- prologue in a dab - an arm thrown out to one side, the other bent across the face, the head in
-- its elbow - and holds it. No animation resource plays the pose: it is the table POSE below,
-- degrees turned about each bone's own axes from the skeleton's rest pose, so another pose is
-- another table.
-- Arm:    ./gatekit --instance <copy> exec client "$(cat stand/scenes/withers.lua)"
-- Disarm: ./gatekit --instance <copy> exec client "$(cat stand/scenes/withers_off.lua)"
-- Client context, a level loaded. Needs GateKit's Script Extender in developer mode
-- (Ext.ClientVisual.CreateDummy, Ext.Debug.ReadMemory and WriteMemory, Ext.Debug.GetObjectAddress).
--
-- How a pose of our own is shown. A figure's body skeleton and head skeleton
-- (entity.Visual.Visual.SkeletonSlots[i].Skeleton) each hold a granny skeleton (bone names,
-- parents, inverse bind matrices), a rest world pose, and a list of mesh bindings; a binding holds
-- the skinning matrices the renderer draws its mesh with (inverse bind * world, one a mesh bone)
-- and which skeleton bone each mesh bone follows. The game rewrites the skinning matrices every
-- frame while the figure is animated; a figure whose AnimationUpdate component is removed is no
-- longer animated, and matrices written then stay. The scene writes
--     skin = inverse bind * rest world * D,   D(bone) = W^-1 * R * W * D(parent)
-- where R is the bone's turn from POSE. The move into the pose is the scene's own: the matrices
-- are blended from those the figure had when it was stopped, over `move` seconds (the game's
-- blueprint cross-fades between its animations by itself, but knows nothing of a written pose).
--
-- Axes, read off the human rig: Shoulder and Elbow y raises the arm sideways, z swings it forward,
-- x twists; Spine and Neck y leans to the figure's left, z bends forward. The figure's left is +x
-- of its model space and it looks along -z.
-- Options, before arming: GKSceneOptions = { withers = { at = {x, y, z}, yaw = degrees, move = 0.4,
-- who = <character root template>, pose = { Bone = { x, y, z } } } }.
-- State: GKScene.withers = { armed, fired, stage, error, at, yaw, wrote, log }.
GKScene = GKScene or {}
local old = GKScene.withers
if old and old.off then pcall(old.off) end

local S = { armed = true, fired = false, stage = "armed", log = {} }
GKScene.withers = S
local O = (GKSceneOptions and GKSceneOptions.withers) or {}
local WITHERS = O.who or "0133f2ad-e121-4590-b5f0-a79413919805" -- S_GLO_JergalAvatar
local TRANSPONDER = { -79.3, 19.1, -389.0 }                      -- the helm marker of TUT_Avernus_C
local POSE = O.pose or {
    Spine1_M = { 0, 6, 4 }, Spine2_M = { 0, 8, 4 },
    Neck_M = { 0, 8, 25 }, Head_M = { 0, 6, 20 },
    Shoulder_L = { 0, 70, 0 },
    Shoulder_R = { 0, 45, 75 }, Elbow_R = { 0, 0, 120 },
}

local RM, WM = Ext.Debug.ReadMemory, Ext.Debug.WriteMemory
local unpack, pack = string.unpack, string.pack
local F12, F16 = "<" .. string.rep("f", 12), "<" .. string.rep("f", 16)
local function now() return Ext.Utils.MonotonicTime() end
local function note(text) S.log[#S.log + 1] = { ms = now(), text = text } end
local function u64(a) return (unpack("<I8", RM(a, 8))) end
local function u32(a) return (unpack("<I4", RM(a, 4))) end

-- 4x4 matrices for row vectors, flat: translation in 13..15
local function mul(a, b)
    local c = {}
    for i = 0, 3 do
        for j = 1, 4 do
            c[i * 4 + j] = a[i * 4 + 1] * b[j] + a[i * 4 + 2] * b[4 + j] + a[i * 4 + 3] * b[8 + j] + a[i * 4 + 4] * b[12 + j]
        end
    end
    return c
end
local function inverse(m)
    local a, b, c, d, e, f, g, h, i = m[1], m[2], m[3], m[5], m[6], m[7], m[9], m[10], m[11]
    local det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g)
    local r = { (e * i - f * h) / det, (c * h - b * i) / det, (b * f - c * e) / det, 0,
                (f * g - d * i) / det, (a * i - c * g) / det, (c * d - a * f) / det, 0,
                (d * h - e * g) / det, (b * g - a * h) / det, (a * e - b * d) / det, 0, 0, 0, 0, 1 }
    r[13] = -(m[13] * r[1] + m[14] * r[5] + m[15] * r[9])
    r[14] = -(m[13] * r[2] + m[14] * r[6] + m[15] * r[10])
    r[15] = -(m[13] * r[3] + m[14] * r[7] + m[15] * r[11])
    return r
end
local function axis(i, degrees)
    local c, s = math.cos(math.rad(degrees)), math.sin(math.rad(degrees))
    if i == 1 then return { 1, 0, 0, 0, 0, c, s, 0, 0, -s, c, 0, 0, 0, 0, 1 } end
    if i == 2 then return { c, 0, -s, 0, 0, 1, 0, 0, s, 0, c, 0, 0, 0, 0, 1 } end
    return { c, s, 0, 0, -s, c, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 }
end
local function turn(d) return mul(mul(axis(1, d[1] or 0), axis(2, d[2] or 0)), axis(3, d[3] or 0)) end

-- A skeleton's tables. Offsets of gr::GrannySkeleton: +0xD8 the granny skeleton (+8 bone count,
-- +0xC bones of 0xA4 bytes: +0 name, +8 parent, +0x50 inverse bind), +0xF8 the rest world pose
-- (+4 its matrices), +0x108 the mesh bindings, +0x114 their count. A binding: +0x10 and +0x18
-- its two skinning poses, +0x20 the bone count, +0xA0 the skeleton bone of each mesh bone.
local function skeleton(sk)
    local at = Ext.Debug.GetObjectAddress(sk)
    local g = u64(at + 0xD8)
    local n = u32(g + 8)
    local raw = RM(u64(g + 0xC), n * 0xA4)
    local rest = RM(u64(u64(at + 0xF8) + 4), n * 64)
    local K = { n = n, name = {}, parent = {}, inv = {}, rest = {}, bindings = {} }
    for i = 0, n - 1 do
        local namePtr, parent = unpack("<I8i4", raw, i * 0xA4 + 1)
        K.name[i] = (unpack("z", RM(namePtr, 64) .. "\0"))
        K.parent[i] = parent
        K.inv[i] = { unpack(F16, raw, i * 0xA4 + 0x50 + 1) }
        K.rest[i] = { unpack(F16, rest, i * 64 + 1) }
    end
    local list, count = u64(at + 0x108), u32(at + 0x114)
    for k = 0, count - 1 do
        local mb = u64(list + k * 8)
        local bones = (unpack("<I2", RM(mb + 0x20, 2)))
        local mapRaw = RM(u64(mb + 0xA0), bones * 4)
        local map = {}
        for j = 0, bones - 1 do map[j] = (unpack("<i4", mapRaw, j * 4 + 1)) end
        K.bindings[#K.bindings + 1] = { p1 = u64(mb + 0x10), p2 = u64(mb + 0x18), bones = bones, map = map }
    end
    return K
end

-- What the pose makes of every mesh bone: the target skinning matrices, and those the figure has.
local function prepare(entity, pose)
    local targets, outer = {}, nil
    for slot, s in ipairs(entity.Visual.Visual.SkeletonSlots) do
        if s.Skeleton then
            local K = skeleton(s.Skeleton)
            local D = {}
            for i = 0, K.n - 1 do
                local name, parent = K.name[i], D[K.parent[i]]
                if outer and outer[name] then
                    D[i] = outer[name] -- a bone the body has too moves as the body's does
                elseif pose[name] then
                    local d = mul(mul(inverse(K.rest[i]), turn(pose[name])), K.rest[i])
                    D[i] = parent and mul(d, parent) or d
                else
                    D[i] = parent
                end
            end
            if slot == 1 then
                outer = {}
                for i, d in pairs(D) do outer[K.name[i]] = d end
            end
            for _, mb in ipairs(K.bindings) do
                local to = {}
                for j = 0, mb.bones - 1 do
                    local i = mb.map[j]
                    local m = mul(K.inv[i], K.rest[i])
                    if D[i] then m = mul(m, D[i]) end
                    to[j] = { m[1], m[5], m[9], m[13], m[2], m[6], m[10], m[14], m[3], m[7], m[11], m[15] }
                end
                targets[#targets + 1] = { mb = mb, to = to, from = RM(mb.p1, mb.bones * 48) }
            end
        end
    end
    return targets
end

local function write(targets, amount, both)
    local wrote = 0
    for _, t in ipairs(targets) do
        local parts = {}
        for j = 0, t.mb.bones - 1 do
            local m = t.to[j]
            if amount < 1 then
                local a = { unpack(F12, t.from, j * 48 + 1) }
                local b = {}
                for x = 1, 12 do b[x] = a[x] + (m[x] - a[x]) * amount end
                m = b
            end
            parts[#parts + 1] = pack(F12, table.unpack(m, 1, 12))
        end
        local bytes = table.concat(parts)
        WM(t.mb.p1, bytes)
        if both then WM(t.mb.p2, bytes) end
        wrote = wrote + 1
    end
    return wrote
end

local function camera()
    for _, e in ipairs(Ext.Entity.GetAllEntitiesWithComponent("Camera")) do
        if e.GameCameraBehavior then return e end
    end
end

local function place()
    local at = O.at
    if not at then
        local level
        for _, e in ipairs(Ext.Entity.GetAllEntitiesWithComponent("ClientControl")) do
            level = tostring(e.Level.LevelName)
            if level ~= "TUT_Avernus_C" then
                local p = e.Transform.Transform.Translate
                at = { p[1] + 2, p[2], p[3] }
            end
            break
        end
        at = at or TRANSPONDER
    end
    local yaw = O.yaw
    if not yaw then
        -- a figure with no turn looks along -z: turn it to the camera
        local cam = camera()
        local c = cam and cam.Transform.Transform.Translate
        yaw = c and math.deg(math.atan(at[1] - c[1], at[3] - c[3])) or 0
    end
    return at, yaw
end

local function tick()
    if not S.armed or S.error or S.stage == "held" then return end
    if S.stage == "armed" then
        if not (Ext.ClientVisual and Ext.ClientVisual.CreateDummy and RM and WM) then
            S.error = "this Script Extender build has no Ext.ClientVisual.CreateDummy or no Ext.Debug.ReadMemory (developer mode)"
            return
        end
        S.at, S.yaw = place()
        local half = math.rad(S.yaw) / 2
        S.figure = Ext.ClientVisual.CreateDummy(WITHERS, S.at, { 0, math.sin(half), 0, math.cos(half) })
        if not S.figure then S.error = "the figure was not made" return end
        S.fired, S.stage = true, "loading"
        note("made")
    elseif S.stage == "loading" then
        local e = S.figure
        local ok = pcall(function() return e.Visual.Visual.SkeletonSlots[1].Skeleton.BoneCount end)
        if not (ok and e.SceneAttach and e.AnimationUpdate) then return end
        S.loadedAt = S.loadedAt or now()
        if now() - S.loadedAt < 1000 then return end -- its first animated frames give the meshes a pose
        e:RemoveComponent("AnimationUpdate")
        S.stage, S.frames = "stopping", 0
        note("its animation is stopped")
    elseif S.stage == "stopping" then
        -- the component leaves at the end of the frame; a write before that is animated over
        S.frames = S.frames + 1
        if S.frames < 3 then return end
        S.targets = prepare(S.figure, POSE)
        S.moveFrom = now()
        S.stage = "moving"
    elseif S.stage == "moving" then
        local part = math.min(1, (now() - S.moveFrom) / ((O.move or 0.4) * 1000))
        local eased = part * part * (3 - 2 * part)
        S.wrote = write(S.targets, eased, part >= 1)
        if part >= 1 then
            S.stage, S.targets = "held", nil
            note("the pose is held")
        end
    end
end

S.subscription = Ext.Events.Tick:Subscribe(function()
    local ok, problem = pcall(tick)
    if not ok then S.error = tostring(problem) end
end)

function S.off()
    S.armed = false
    if S.subscription then pcall(function() Ext.Events.Tick:Unsubscribe(S.subscription) end) S.subscription = nil end
    if S.figure then pcall(Ext.ClientVisual.DestroyDummy, S.figure) end
    S.figure, S.targets, S.stage = nil, nil, "off"
    return true
end

note("armed")
return { armed = true, who = WITHERS }
