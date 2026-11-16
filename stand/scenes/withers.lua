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
-- where R is the bone's turn from POSE. The pose is written whole at once (`move` = 0, the
-- default): the target matrices come from the rest pose alone, so the figure needs no animated
-- frame first and may stand out of every view. With `move` > 0 the matrices are blended from
-- those the figure had when it was stopped, over `move` seconds (the game's blueprint cross-fades
-- between its animations by itself, but knows nothing of a written pose).
-- He waits: armed at the prologue's start (and again after every load), far from the party, he
-- stands posed, lit and turned to the helm's door long before anyone comes in.
--
-- Axes, read off the human rig: Shoulder and Elbow y raises the arm sideways, z swings it forward,
-- x twists; Spine and Neck y leans to the figure's left, z bends forward. The figure's left is +x
-- of its model space and it looks along -z.
-- Options, before arming: GKSceneOptions = { withers = { at = {x, y, z}, yaw = degrees (default: to
-- the helm's door in the helm, to the camera elsewhere), move = 0,
-- who = <character root template>, pose = { Bone = { x, y, z } }, light = <uuid of the level's
-- light to borrow, or false>, light_toward = 1.6, light_up = 1.8 } }.
-- The figure is on the client only: no blast, hit or shove of the game reaches it.
-- State: GKScene.withers = { armed, fired, stage, error, at, yaw, wrote, log }.
GKScene = GKScene or {}
local old = GKScene.withers
if old and old.off then pcall(old.off) end

local S = { armed = true, fired = false, stage = "armed", log = {} }
GKScene.withers = S
local O = (GKSceneOptions and GKSceneOptions.withers) or {}
local WITHERS = O.who or "0133f2ad-e121-4590-b5f0-a79413919805" -- S_GLO_JergalAvatar
-- At the helm's transponder of TUT_Avernus_C (-82.4, 18.1, -389.0): 2 m to the north-east of it, the
-- transponder behind his right hand as he faces the door, 1.2 m from where a character stands to
-- use it (1.7 m east of it). Straight north of it the transponder's overhang hides him from the
-- game's camera. The height is read from the floor by a ray where one answers.
local PLACE = { -81.0, 18.45, -387.5 }
-- The helm's door (the sphincter the party comes through, 59 m east of him).
local HELM_DOOR = { -21.9, 18.2, -389.0 }
-- The helm is dark. A point light of the level that carries LightMovement follows its Transform
-- (stand/menu/README.md, "Light"): one from a deck far under the helm is borrowed and put back.
local LIGHT = "8e89e599-51da-4f07-883a-0ee43a14c027"
if O.light ~= nil then LIGHT = O.light end
-- The bones that lean over his idle (turned in their frame of each frame); every other bone of the
-- pose, and what hangs under it, is held as posed on the living body (breathing, below).
local LEAN = O.lean or { Spine1_M = true, Spine2_M = true }
-- The spine leans a little to his right, toward the transponder.
local POSE = O.pose or {
    Spine1_M = { 0, -4, 3 }, Spine2_M = { 0, -5, 3 },
    Neck_M = { 0, 8, 25 }, Head_M = { 0, 6, 20 },
    Shoulder_L = { 0, 70, 0 },
    -- the bent arm crosses in front of the face, the hand about 0.1 m ahead of it
    Shoulder_R = { 0, 45, 75 }, Elbow_R = { 0, 0, 95 },
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

-- Breathing: the pose laid over his idle every frame. After the client's Effect system (the
-- skinning matrices of the frame are made then and drawn after it) each skinning buffer that holds
-- a bone the pose moves, or a bone under one, is rewritten when the game made it anew this frame:
-- S' = S * D, D(bone) = W^-1 * R * W * D(parent) with W the bone's LIVE world matrix, so the posed
-- bones hang on the living skeleton. A buffer the game did not make anew (he is out of every view
-- and not animated) still holds the bytes written last and is left. A binding has two skinning
-- buffers and the game fills one of them a frame, in turn: both are watched.
local function layerSetup(entity, pose)
    local L = { slots = {}, frames = 0, ms = 0, wrote = 0, skipped = 0 }
    local outerMoved
    for slot, s in ipairs(entity.Visual.Visual.SkeletonSlots) do
        if s.Skeleton then
            local K = skeleton(s.Skeleton)
            local moved, posed = {}, {}
            for i = 0, K.n - 1 do
                local name = K.name[i]
                if (outerMoved and outerMoved[name]) or (not outerMoved and pose[name]) or moved[K.parent[i]] then moved[i] = true end
                if not outerMoved and pose[name] then posed[#posed + 1] = i end
            end
            if slot == 1 then
                outerMoved = {}
                for i in pairs(moved) do outerMoved[K.name[i]] = true end
                -- the held parts: each bone under a posed bone that is not a leaning one is held as
                -- the pose made from the rest pose, carried by its anchor (the parent of the held
                -- part's first bone): C = rest * D(rest) * rest(anchor)^-1, then C * the anchor's
                -- world of the frame
                local Drest, top, C, anchor = {}, {}, {}, {}
                for i = 0, K.n - 1 do
                    local p = K.parent[i]
                    local name = K.name[i]
                    if pose[name] and not LEAN[name] then
                        local d = mul(mul(inverse(K.rest[i]), turn(pose[name])), K.rest[i])
                        Drest[i] = Drest[p] and mul(d, Drest[p]) or d
                        top[i] = top[p] or i
                    elseif top[p] then
                        Drest[i], top[i] = Drest[p], top[p]
                    end
                    if top[i] then
                        anchor[i] = K.parent[top[i]]
                        C[i] = mul(mul(K.rest[i], Drest[i]), inverse(K.rest[anchor[i]]))
                    end
                end
                -- a held bone's skinning matrix is inverse bind * C * the anchor's world: IC is the
                -- first two, made once
                local IC = {}
                for i, c in pairs(C) do IC[i] = mul(K.inv[i], c) end
                L.C, L.IC, L.anchor = C, IC, anchor
            elseif L.C then
                -- the bones the head's skeleton shares with the body need the body's change itself
                L.shared = L.shared or {}
                for i = 0, K.n - 1 do L.shared[K.name[i]] = true end
            end
            -- each binding holds two skinning buffers, which the game fills in turn
            local buffers = {}
            for _, mb in ipairs(K.bindings) do
                for j = 0, mb.bones - 1 do
                    if moved[mb.map[j]] then
                        buffers[#buffers + 1] = { mb = mb, at = mb.p1, probe = j, mark = RM(mb.p1, mb.bones * 48) }
                        buffers[#buffers + 1] = { mb = mb, at = mb.p2, probe = j, mark = RM(mb.p2, mb.bones * 48) }
                        break
                    end
                end
            end
            L.slots[#L.slots + 1] = { sk = s.Skeleton, K = K, buffers = buffers, body = slot == 1 }
        end
    end
    return L
end

local function layerFrame(L, pose)
    local t0 = now()
    local outer
    for _, slot in ipairs(L.slots) do
        local K = slot.K
        local D = {}
        if slot.body then
            local at = Ext.Debug.GetObjectAddress(slot.sk)
            local raw = RM(u64(u64(at + 0xE0) + 4), K.n * 64)
            local Wc = {}
            local function W(i)
                local w = Wc[i]
                if not w then w = { unpack(F16, raw, i * 64 + 1) } w[17] = nil Wc[i] = w end
                return w
            end
            local first = W(0)
            if math.abs(first[1]) + math.abs(first[6]) + math.abs(first[11]) < 0.1 then return false end -- no live pose yet
            local H, finals = {}, {}
            slot.H = H
            for i = 0, K.n - 1 do
                local parent = D[K.parent[i]]
                local name = K.name[i]
                if L.C[i] then
                    -- held: where the pose puts it on its anchor as the anchor stands this frame
                    local a = L.anchor[i]
                    local final = finals[a]
                    if not final then final = D[a] and mul(W(a), D[a]) or W(a) finals[a] = final end
                    H[i] = final
                    if L.shared and L.shared[name] then D[i] = mul(inverse(W(i)), mul(L.C[i], final)) end
                elseif pose[name] then
                    -- leaning: turned in its own frame of this frame, so the idle carries it
                    local d = mul(mul(inverse(W(i)), turn(pose[name])), W(i))
                    D[i] = parent and mul(d, parent) or d
                else
                    D[i] = parent
                end
            end
            outer = {}
            for i, d in pairs(D) do outer[K.name[i]] = d end
            L.D = D
        else
            for i = 0, K.n - 1 do D[i] = (outer and outer[K.name[i]]) or D[K.parent[i]] end
        end
        for _, buffer in ipairs(slot.buffers) do
            local mb = buffer.mb
            -- the matrix of its first moved bone tells whether the game filled it this frame: the
            -- game's own differs from the one written there
            local o = buffer.probe * 48
            if RM(buffer.at + o, 48) == buffer.mark:sub(o + 1, o + 48) then
                L.skipped = L.skipped + 1
            else
                local raw = RM(buffer.at, mb.bones * 48)
                local parts = {}
                local H = slot.H
                for j = 0, mb.bones - 1 do
                    local i = mb.map[j]
                    local d = D[i]
                    if H and H[i] then
                        local m = mul(L.IC[i], H[i])
                        parts[#parts + 1] = pack(F12, m[1], m[5], m[9], m[13], m[2], m[6], m[10], m[14], m[3], m[7], m[11], m[15])
                    elseif d then
                        local a = { unpack(F12, raw, j * 48 + 1) }
                        local m = mul({ a[1], a[5], a[9], 0, a[2], a[6], a[10], 0, a[3], a[7], a[11], 0, a[4], a[8], a[12], 1 }, d)
                        parts[#parts + 1] = pack(F12, m[1], m[5], m[9], m[13], m[2], m[6], m[10], m[14], m[3], m[7], m[11], m[15])
                    else
                        parts[#parts + 1] = raw:sub(j * 48 + 1, j * 48 + 48)
                    end
                end
                local bytes = table.concat(parts)
                WM(buffer.at, bytes)
                buffer.mark = bytes
                L.wrote = L.wrote + 1
            end
        end
    end
    L.frames = L.frames + 1
    L.ms = L.ms + (now() - t0)
    return true
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
        if not at then
            at = { PLACE[1], PLACE[2], PLACE[3] }
            local ok, hit = pcall(Ext.Level.RaycastClosest, { at[1], at[2] + 2, at[3] }, { at[1], at[2] - 4, at[3] }, 1, 0xFFFFFFFF, 0, -1)
            if ok and hit and hit.Position then at[2] = hit.Position[2] end
            S.inHelm = true
        end
    end
    local yaw = O.yaw
    if not yaw then
        -- a figure with no turn looks along -z: in the helm turn it to the helm's door, whence the
        -- party first sees him; elsewhere to the camera
        local c = S.inHelm and HELM_DOOR
        if not c then
            local cam = camera()
            c = cam and cam.Transform.Transform.Translate
        end
        yaw = c and math.deg(math.atan(at[1] - c[1], at[3] - c[3])) or 0
    end
    return at, yaw
end

local function moveLight(light, to)
    local transform = light.Transform.Transform
    transform.Translate = to
    light.Transform.Transform = transform
end

-- The light stands between the figure and the camera, at the height of its chest.
local function borrowLight()
    if not LIGHT then return end
    local light
    for _, e in ipairs(Ext.Entity.GetAllEntitiesWithComponent("Light")) do
        if tostring(e.Light.UUID) == LIGHT and e.LightMovement then light = e break end
    end
    if not light then note("no light " .. LIGHT .. " with LightMovement in the level: the figure stays in the dark") return end
    -- before his face: the way he is turned (a figure turned by `yaw` looks along -sin, -cos)
    local dx, dz = -math.sin(math.rad(S.yaw)), -math.cos(math.rad(S.yaw))
    local toward = O.light_toward or 1.6
    local at = light.Transform.Transform.Translate
    S.light, S.lightHome = light, { at[1], at[2], at[3] }
    moveLight(light, { S.at[1] + dx * toward, S.at[2] + (O.light_up or 1.8), S.at[3] + dz * toward })
    -- brighter than the deck's lamp it was: its Intensity and Radius, put back by the off file
    S.lightWas = { Intensity = light.Light.Intensity, Radius = light.Light.Radius }
    light.Light.Intensity = O.light_intensity or 20000
    light.Light.Radius = O.light_radius or 5
    note("a light of the level is put before the figure")
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
        if O.breathing ~= false then
            -- the dab from the rest pose first: what is drawn should he be seen before he is
            -- animated; then the layer over his idle from the first frame the game animates him
            borrowLight()
            S.wrote = write(prepare(e, POSE), 1, true)
            S.layer = layerSetup(e, POSE) -- marks what the dab from the rest pose left in each buffer
            S.layerSub = Ext.Entity.OnSystemPostUpdate("Effect", function()
                if not S.armed or not S.layer then return end
                local okFrame, problem = pcall(layerFrame, S.layer, POSE)
                if not okFrame then S.layer.error = tostring(problem) end
            end)
            S.stage = "held"
            note("the pose is held over his idle (breathing)")
            return
        end
        S.loadedAt = S.loadedAt or now()
        if now() - S.loadedAt < 1000 then return end -- its first animated frames give the meshes a pose
        e:RemoveComponent("AnimationUpdate")
        borrowLight()
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
        local move = O.move or 0
        local part = move > 0 and math.min(1, (now() - S.moveFrom) / (move * 1000)) or 1
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
    if S.layerSub then pcall(Ext.Entity.Unsubscribe, S.layerSub) S.layerSub = nil end
    S.layer = nil
    if S.light and S.lightHome then pcall(moveLight, S.light, S.lightHome) end
    if S.light and S.lightWas then
        pcall(function() S.light.Light.Intensity, S.light.Light.Radius = S.lightWas.Intensity, S.lightWas.Radius end)
    end
    S.light = nil
    if S.figure then pcall(Ext.ClientVisual.DestroyDummy, S.figure) end
    S.figure, S.targets, S.stage = nil, nil, "off"
    return true
end

note("armed")
return { armed = true, who = WITHERS }
