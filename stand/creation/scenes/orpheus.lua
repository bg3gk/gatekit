-- Scene "orpheus" (the video stand): far behind the creation scene, on a floating rock, Orpheus
-- kneels chained inside a faceted sphere - as he is kept in the Astral Prism (the level
-- INT_Main_A with the art level CRE_AstralPlane_E_Art; the story goal Act2b_INT_EmperorRevealed
-- puts on him the status INT_ORPHEUS_INFERNALBINDSANIM, whose loop is CUST_OrpheusChained_01, and
-- the beams INT_INFERNALCHAINS_LEFT and _RIGHT from two crystals). Server context, in character
-- creation (the level SYS_CC_I).
--
-- What it is made of, all the game's own: the character root template CINE_GTY_Orpheus (his look;
-- the placed S_GLO_Orpheus is a local template of another level and cannot be created here), the
-- sphere BLD_Astral_Globe_Of_Domination_Small_A, the rock NAT_Rock_Granite_Platform_C_Interactive,
-- two crystals Dynamic_NAT_Rock_Granite_Boulder_Big_360_H_Crystal (the template of the story's
-- S_END_CrystalShakle_001 and _002) as the sources of the two chain beams.
--
-- Far is the point: beyond the client's distance a creature is not sent whole and is not drawn,
-- and beyond the activation manager's sleeping distance of the party it sleeps. Items are drawn
-- at any distance. With `wake` (the default) the scene keeps him awake with GK.Wake.One - no
-- camera moves - and the client then holds and draws him; `wake = false` shows the contrary.
-- A character can be created only on the dummy's platform: he is created there and teleported to
-- the rock in the same tick, so he is never drawn on the platform. show() makes everything and
-- hide() removes it.
--
-- The page of the creation window is read only in the UI context (the list `flowPage`, 2 = the
-- guardian: stand/creation/page.lua), so the driver shows and hides the scene:
-- GKSceneOptions = { orpheus = { back = metres behind the dummy (default 42: 53 m from the party,
--     beyond both distances), side = metres to the picture's right (default -4.5), up = metres
--     above the dummy's feet (default 9) - the defaults put it in the open sky between the statue
--     and the character; the character's own figure, the right-hand cliff and the window's panels
--     cover most other directions - spread = metres from him to each crystal (default 4),
--     shown = false to arm it hidden (then the driver calls show()), wake = false,
--     globe = a template for the sphere, small = false for the large sphere } }
-- Woken, he is drawn at half opacity (the client's out-of-sight fade, as any creature no party
-- member sees); stand/creation/scenes/solid.lua, run in the client context after show(), makes
-- him solid.
-- Arm:    ./gatekit --instance <copy> exec server "$(cat stand/creation/scenes/orpheus.lua)"
-- Show:   ./gatekit --instance <copy> exec server "return GKScene.orpheus.show()"   (hide() the same way)
-- State:  ./gatekit --instance <copy> exec server "return GK.Wake.State(GKScene.orpheus.id)"
-- Disarm: ./gatekit --instance <copy> exec server "$(cat stand/creation/scenes/orpheus_off.lua)"
-- State:  GKScene.orpheus = { armed, fired, shown, log, id, at, distance (to the nearest party
--     member), made = { rock, globe, left, right }, awake, chained }
GKScene = GKScene or {}
local old = GKScene.orpheus
if old and old.armed then return { ok = true, already = true, id = old.id, shown = old.shown } end

local O = (GKSceneOptions and GKSceneOptions.orpheus) or {}
local LEVEL = "SYS_CC_I"
local DUMMY = { 9.99, 0.89, 111.14 }
local CAMERA = { 7.43, 1.94, 113.54 }
local ORPHEUS = "f03d791c-59ab-4d05-bd7c-e85150345a0e"     -- CINE_GTY_Orpheus
local GLOBE = "bedf6aa6-3250-4646-b97e-b2093e171cb9"       -- BLD_Astral_Globe_Of_Domination_A
local GLOBE_SMALL = "2bfe2ba3-2070-4c68-b54a-027c6dd2f74b" -- BLD_Astral_Globe_Of_Domination_Small_A
local ROCK = "2b2af539-e5ab-400c-96f0-285e465f21e3"        -- NAT_Rock_Granite_Platform_C_Interactive
local CRYSTAL = "c3a879cf-3f55-4580-af66-d97d1c029cac"     -- Dynamic_NAT_Rock_Granite_Boulder_Big_360_H_Crystal
local NEUTRAL = "cfb709b3-220f-9682-bcfb-6f0d8837462e"
local BOUND = "INT_ORPHEUS_INFERNALBINDSANIM"
local S = { armed = true, fired = false, shown = false, log = {}, made = {} }
GKScene.orpheus = S

local function log(text) S.log[#S.log + 1] = { ms = Ext.Utils.MonotonicTime(), text = text } end
local function try(what, fn, ...)
    local ok, a, b = pcall(fn, ...)
    if not ok then log("failed: " .. what .. ": " .. tostring(a)) return nil end
    return a, b
end
local function when(what, condition, fn, limit)
    local started = Ext.Utils.MonotonicTime()
    local function ask()
        if not S.armed then return end
        local ok, holds = pcall(condition)
        if ok and holds then try(what, fn) return end
        if Ext.Utils.MonotonicTime() - started > (limit or 20) * 1000 then log("never came: " .. what) return end
        Ext.Timer.WaitForRealtime(100, ask)
    end
    ask()
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
local back, side, up = O.back or 42, O.side or -4.5, O.up or 9
local function place(b, s, u) return { DUMMY[1] + fx * b + rx * s, DUMMY[2] + up + (u or 0), DUMMY[3] + fz * b + rz * s } end
S.at = place(back, side)
local toCamera = math.deg(math.atan(-fx, -fz))
local places = {
    rock = place(back, side, O.rockDrop or -1.0),
    globe = place(back, side),
    left = place(back, side - (O.spread or 4)),
    right = place(back, side + (O.spread or 4)),
}

if not (GK.Stage.Ledger and GK.Stage.Ledger.baseline) then
    local b = GK.Stage.Begin()
    if b and b.lost then GK.Stage.Begin{ fresh = true } end
    S.ownScope = true
end
local templates = {
    rock = O.rock or ROCK,
    globe = O.globe or (O.small == false and GLOBE or GLOBE_SMALL),
    left = CRYSTAL,
    right = CRYSTAL,
}

local function distance()
    local nearest
    for _, member in ipairs(GK.Stage.Party(true)) do
        local x, y, z = Osi.GetPosition(member)
        if x then
            local d = math.sqrt((x - S.at[1]) ^ 2 + (y - S.at[2]) ^ 2 + (z - S.at[3]) ^ 2)
            if not nearest or d < nearest then nearest = d end
        end
    end
    return nearest
end

function S.show()
    if not S.armed then return { ok = false, error = "not armed" } end
    if S.shown then return { ok = true, shown = true, already = true, distance = S.distance } end
    -- Everything is made where it is shown: the client keeps the first place of a far thing (its
    -- stub is not moved when the server moves it), so nothing is made elsewhere and brought here.
    for key, at in pairs(places) do
        local made = try("spawn " .. key, GK.Stage.Spawn, { template = templates[key], at = at, exact = true, rotation = toCamera })
        S.made[key] = made and made[1]
    end
    local made = try("spawn Orpheus", GK.Stage.Spawn, { template = ORPHEUS, at = { DUMMY[1] + fx * 1.4, DUMMY[2], DUMMY[3] + fz * 1.4 }, faction = NEUTRAL })
    S.id = made and made[1]
    if not S.id then return { ok = false, error = "Orpheus was not created", log = S.log } end
    Osi.TeleportToPosition(S.id, S.at[1], S.at[2], S.at[3], "", 0, 0, 0, 0, 0)
    S.shown, S.distance = true, distance()
    if O.wake ~= false then
        S.wake = try("keep him awake", GK.Wake.One, S.id)
    end
    -- The chained kneel and the beams are statuses: they are taken by a creature that is awake.
    when("Orpheus awake", function() return Ext.Entity.Get(S.id).Active ~= nil end, function()
        S.awake = true
        Osi.ApplyStatus(S.id, BOUND, -1.0, 1, S.id)
        if S.made.left then Osi.ApplyStatus(S.id, "INT_INFERNALCHAINS_LEFT", -1.0, 1, S.made.left) end
        if S.made.right then Osi.ApplyStatus(S.id, "INT_INFERNALCHAINS_RIGHT", -1.0, 1, S.made.right) end
        when("the chained kneel", function() return Osi.HasActiveStatus(S.id, BOUND) == 1 end, function()
            S.chained, S.fired = true, true
            log("kneels chained")
        end, 10)
    end, 10)
    log(string.format("shown %.0f m from the party%s", S.distance or -1, O.wake ~= false and ", kept awake" or ", left asleep"))
    return { ok = true, shown = true, distance = S.distance, at = S.at }
end

function S.hide()
    if not S.armed then return { ok = false, error = "not armed" } end
    if S.wake and S.id then try("let him sleep", GK.Wake.Release, S.id) end
    if S.id and Ext.Entity.Get(S.id) then try("remove Orpheus", GK.Stage.Remove, S.id) end
    for key, id in pairs(S.made) do
        if Ext.Entity.Get(id) then try("remove " .. key, GK.Stage.Remove, id) end
    end
    S.id, S.made, S.wake = nil, {}, nil
    S.shown, S.awake, S.chained = false, false, false
    log("hidden")
    return { ok = true, shown = false }
end

if O.shown ~= false then S.show() end
return { ok = true, id = S.id, made = S.made, at = S.at, shown = S.shown, distance = S.distance }
