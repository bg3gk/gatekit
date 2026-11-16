-- The bones of a figure are named things with a live pose, and a pose can be written and held.
-- A figure's body skeleton (entity.Visual.Visual.SkeletonSlots[1].Skeleton) holds a granny
-- skeleton with the bones' names and parents, a live world pose the animation writes every frame
-- (one 4x4 matrix a bone, metres from the entity's origin) and mesh bindings whose skinning
-- matrices the renderer draws with. The offsets are in the reader below.
-- The rules held here, on a timeline dummy with no server entity:
--   - the hand bone Wrist_R rises by more than half a metre while a waving animation plays and is
--     back where it was when the animation is over;
--   - with the figure's AnimationUpdate component removed nothing rewrites its skinning matrices:
--     matrices written then are the same bytes three seconds later, where an animated figure's
--     change within a second.
-- What a person sees: `waves.jpg` and `held.jpg` (the whole figure drawn 0.6 m up) in the test's folder.
-- Staging (cheat): the figure, the link, the animation, the written matrices. Reading: observation.
-- Run: gatekit --instance <copy> test tests/figure -k client_bones

test { level = "GustavX:Basic_Level_A",
       about = "A named bone's live position follows a played animation and returns; skinning matrices written into a figure whose AnimationUpdate is removed stay." }

local CIVILIAN = "19dc6cab-aa38-4f09-b9cf-546e1c0ecb8a" -- Humans_Female_Civilian
local WAVE = "8d632e1b-5d4f-1def-7b2f-a06dc28e7f6e"     -- HUM_F_Rig_DFLT_CUST_Waving_01

-- gr::GrannySkeleton: +0xD8 the granny skeleton (+8 bone count, +0xC bones of 0xA4 bytes: +0 name),
-- +0xE0 the live world pose (+4 its 4x4 matrices, translation in the last row), +0x108 the mesh
-- bindings, +0x114 their count; a binding: +0x10 a skinning pose (3x4 floats a bone, translation
-- the fourth of each row), +0x20 its bone count.
local READER = [[
    GKBones = {}
    local RM = Ext.Debug.ReadMemory
    local function u64(a) return (string.unpack("<I8", RM(a, 8))) end
    local function u32(a) return (string.unpack("<I4", RM(a, 4))) end
    local function at() return Ext.Debug.GetObjectAddress(GKTestFigure.Visual.Visual.SkeletonSlots[1].Skeleton) end
    function GKBones.names()
        local g = u64(at() + 0xD8)
        local n, bones = u32(g + 8), u64(g + 0xC)
        local out = {}
        for i = 0, n - 1 do out[(string.unpack("z", RM(u64(bones + i * 0xA4), 64) .. "\0"))] = i end
        return out, n
    end
    function GKBones.height(bone)
        local m = u64(u64(at() + 0xE0) + 4) + bone * 64
        return (string.unpack("<f", RM(m + 0x34, 4)))
    end
    function GKBones.binding()
        local mb = u64(u64(at() + 0x108))
        return u64(mb + 0x10), (string.unpack("<I2", RM(mb + 0x20, 2)))
    end
    -- the camera on the figure: a figure out of the picture is not animated
    GK.Camera.Focus(GKTestAt, {})
    local names, n = GKBones.names()
    GKBones.wrist = names.Wrist_R
    return { bones = n, wrist = names.Wrist_R, root = names.Dummy_Root } ]]

function run(game)
    game.wait_state("Running", "server", 120)
    need(game.client("return Ext.ClientVisual ~= nil and Ext.ClientVisual.PlayAnimation ~= nil and Ext.Debug.ReadMemory ~= nil and Ext.Debug.GetObjectAddress ~= nil and Ext.Debug.IsDeveloperMode()"),
        "this Script Extender build has no Ext.ClientVisual.PlayAnimation or no Ext.Debug.ReadMemory in developer mode")

    step("a figure two metres from the host, its bones read by name", "cheat", function()
        check(game.client(string.format([[
            local host
            for _, e in ipairs(Ext.Entity.GetAllEntitiesWithComponent("ClientControl")) do host = e break end
            local p = host.Transform.Transform.Translate
            GKTestAt = { p[1] + 2, p[2], p[3] }
            GKTestFigure = Ext.ClientVisual.CreateDummy(%q, GKTestAt, { 0, 0, 0, 1 })
            return GKTestFigure ~= nil ]], CIVILIAN)), "the figure was made")
        game.wait("client", "GKTestFigure.Visual ~= nil and GKTestFigure.Visual.Visual ~= nil and GKTestFigure.SceneAttach ~= nil and GKTestFigure.AnimationWaterfall ~= nil", 20)
        local read = game.client(READER)
        check(read.bones > 50, "the skeleton lists its bones", read)
        eq(read.root, 0, "the first bone is Dummy_Root")
        check(read.wrist ~= nil, "a bone is named Wrist_R", read)
        game.client(string.format([[
            for _, w in ipairs(GKTestFigure.AnimationWaterfall.Waterfall) do
                if tostring(w.Type) == "Visual" and tostring(w.Slot) == "" then
                    pcall(function()
                        Ext.Resource.Get(w.Resource, "AnimationSet").AnimationBank.AnimationSubSets[""].Animation[%q] = { ID = %q, flags = {} }
                    end)
                end
            end
            return true ]], WAVE, WAVE))
        game.sleep(3)
    end)

    step("the wrist rises with the wave and comes back", "observation", function()
        local rest = game.client("return GKBones.height(GKBones.wrist)")
        check(rest > 0.5 and rest < 1.3, "at rest the hand hangs by the hip", rest)
        game.client(string.format([[
            GKBones.top = 0
            GKBones.watch = Ext.Events.Tick:Subscribe(function()
                pcall(function() local y = GKBones.height(GKBones.wrist) if y > GKBones.top then GKBones.top = y end end)
            end)
            return Ext.ClientVisual.PlayAnimation(GKTestFigure, { %q }) ]], WAVE))
        game.wait("client", "GKBones.top > " .. (rest + 0.5), 10)
        game.shot("waves.jpg", 0)
        game.sleep(9)
        local after = game.client("Ext.Events.Tick:Unsubscribe(GKBones.watch) return { now = GKBones.height(GKBones.wrist), top = GKBones.top }")
        check(after.top - rest > 0.5, "the hand rose by more than half a metre", after)
        near(after.now, rest, 0.08, "and is back where it hung")
    end)

    step("an animated figure's skinning matrices change by themselves", "observation", function()
        game.client("local p, n = GKBones.binding() GKBones.before = Ext.Debug.ReadMemory(p, n * 48) return true")
        game.sleep(1)
        check(game.client("local p, n = GKBones.binding() return Ext.Debug.ReadMemory(p, n * 48) ~= GKBones.before"),
            "the idle rewrote them within a second")
    end)

    step("with AnimationUpdate removed, written matrices stay", "cheat", function()
        game.client("GKTestFigure:RemoveComponent('AnimationUpdate') return true")
        game.sleep(0.5)
        -- every mesh bone of every binding 0.6 m up: the figure is drawn that much higher
        check(game.client([[
            local RM = Ext.Debug.ReadMemory
            local at = Ext.Debug.GetObjectAddress(GKTestFigure.Visual.Visual.SkeletonSlots[1].Skeleton)
            local list = (string.unpack("<I8", RM(at + 0x108, 8)))
            local count = (string.unpack("<I4", RM(at + 0x114, 4)))
            GKBones.wrote = {}
            for k = 0, count - 1 do
                local mb = (string.unpack("<I8", RM(list + k * 8, 8)))
                local bones = (string.unpack("<I2", RM(mb + 0x20, 2)))
                for _, off in ipairs({ 0x10, 0x18 }) do
                    local p = (string.unpack("<I8", RM(mb + off, 8)))
                    local raw, parts = RM(p, bones * 48), {}
                    for j = 0, bones - 1 do
                        local m = { string.unpack("<ffffffffffff", raw, j * 48 + 1) }
                        m[8] = m[8] + 0.6
                        parts[#parts + 1] = string.pack("<ffffffffffff", table.unpack(m, 1, 12))
                    end
                    local bytes = table.concat(parts)
                    Ext.Debug.WriteMemory(p, bytes)
                    GKBones.wrote[#GKBones.wrote + 1] = { p, bytes }
                end
            end
            return #GKBones.wrote > 0 ]]), "the matrices were written")
        game.sleep(3)
        game.shot("held.jpg", 0)
        local held = game.client([[
            local same = 0
            for _, w in ipairs(GKBones.wrote) do if Ext.Debug.ReadMemory(w[1], #w[2]) == w[2] then same = same + 1 end end
            return { same = same, of = #GKBones.wrote } ]])
        eq(held.same, held.of, "three seconds later every buffer holds the bytes written")
    end)

    step("removed", "cheat", function()
        game.client([[
            GKBones = nil
            local gone = Ext.ClientVisual.DestroyDummy(GKTestFigure)
            GKTestFigure = nil
            return gone ]])
    end)
end
