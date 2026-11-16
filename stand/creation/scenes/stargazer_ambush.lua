-- Scene "stargazer_ambush" (the video stand): behind the character being created the Emerald
-- Grove's stargazer looks through her telescope; a bugbear appears behind her, the fight begins
-- with her caught off guard, and he kills her - as in the grove (the story goal Act1_DEN_Misc,
-- "Bugbear Assassin", and his behaviour script DEN_BugbearNinja: a real fight between the two with
-- no party member in it). Server context, in character creation (the level SYS_CC_I).
--
-- The looking: her animation set has no link to the telescope animations (in the game only the
-- behaviour timeline BHVR_WRLD_DEN_TelescopeStargazing plays them, and the stargazer's own script
-- leaves that timeline commented out). The scene adds the links - a short name of its own to the
-- animation resource, in every plain set of her animation waterfall - and then the game's
-- Osi.PlayLoopingAnimation plays them as any other.
-- The fight is the game's own: the two are made temporary enemies (the party is neutral to both
-- and is not taken in), she carries SURPRISED for one turn and one hit point, his attack die is
-- pinned. The scene only waits for what the game says: both in the fight, her death. Her body
-- falls into the lavender and is not seen from the creation camera; he stays standing over it.
--
-- A character can be created only on the dummy's own platform: both are created there and
-- teleported away in the same tick, she to the telescope and he under the ground below his
-- place, from where he comes up behind her at the moment of the ambush.
-- GKSceneOptions = { stargazer_ambush = { back = metres behind the dummy, side = metres to the
--     picture's right, up = metres above the dummy's feet, step = metres from the telescope to her,
--     turn = degrees added to the facing, behind = metres from her to the bugbear along the
--     picture (to the left, behind her back), deep = metres he stands further from the camera
--     (the creation window's left panel covers what is further left than about 1.5 m of her),
--     after = seconds from her looking to the ambush (left out: the driver calls go()),
--     beat = seconds he stands behind her before the fight (default 1.5),
--     crit = false for a plain hit (his die pinned at 19): a critical hit, the default, shakes
--     and blurs the whole creation picture for a moment, as the game does on any critical } }
-- Arm:    ./gatekit --instance <copy> exec server "$(cat stand/creation/scenes/stargazer_ambush.lua)"
-- Ambush: ./gatekit --instance <copy> exec server "return GKScene.stargazer_ambush.go()"
-- Disarm: ./gatekit --instance <copy> exec server "$(cat stand/creation/scenes/stargazer_ambush_off.lua)"
-- State:  GKScene.stargazer_ambush = { armed, fired, stage, log, id, telescope, bugbear, at,
--     looking, fight = { joined, round, surprised }, killed, by }; stage is "arming", "looking",
--     "appeared", "fight", "killed" or "failed".
GKScene = GKScene or {}
local old = GKScene.stargazer_ambush
if old and old.armed then return { ok = true, already = true, id = old.id, stage = old.stage } end

local O = (GKSceneOptions and GKSceneOptions.stargazer_ambush) or {}
local LEVEL = "SYS_CC_I"
local DUMMY = { 9.99, 0.89, 111.14 }
local CAMERA = { 7.43, 1.94, 113.54 }
local NADIRA = "8d6c664f-8743-4bd5-b184-585217b09eb3"     -- Tieflings_Female_Nadira
local TELESCOPE = "7dc63d8d-c4b7-49a1-906d-9dff56e805f8"  -- TOOL_GEN_Telescope_A
local BUGBEAR = "3065382b-a148-4294-b1e9-1a34875db3ba"    -- Bugbears_Male, the assassin's root template
local NEUTRAL = "cfb709b3-220f-9682-bcfb-6f0d8837462e"
local HIDE = 6    -- metres under his place where the bugbear waits
-- HUM_F_Rig_WRLD_DFLT_CUST_StargazingTelescope_01_Start, _Loop, _End: the animation resources.
-- Each is linked under its own uuid as the short name.
local LOOK = { "9d894c26-9c26-7a96-240b-01570b181a60", "350603a8-399b-83a1-16c0-c7203278dce1", "848ef5a1-aa33-312c-297b-49ea3ac5daff" }
local S = { armed = true, fired = false, stage = "arming", log = {}, fight = {} }
GKScene.stargazer_ambush = S

local function log(text) S.log[#S.log + 1] = { ms = Ext.Utils.MonotonicTime(), text = text } end
local function try(what, fn, ...)
    local ok, a, b = pcall(fn, ...)
    if not ok then log("failed: " .. what .. ": " .. tostring(a)) return nil end
    return a, b
end
local function later(ms, fn)
    Ext.Timer.WaitForRealtime(ms, function() if S.armed then try("a step", fn) end end)
end
-- Runs `fn` once `condition()` holds, asked ten times a second; after `limit` seconds `otherwise`.
local function when(what, condition, fn, limit, otherwise)
    local started = Ext.Utils.MonotonicTime()
    local function ask()
        if not S.armed then return end
        local ok, holds = pcall(condition)
        if ok and holds then try(what, fn) return end
        if Ext.Utils.MonotonicTime() - started > (limit or 20) * 1000 then
            log("never came: " .. what)
            if otherwise then try(what .. " (given up)", otherwise) end
            return
        end
        Ext.Timer.WaitForRealtime(100, ask)
    end
    ask()
end
local function fail(why)
    S.stage, S.error = "failed", why
    log("failed: " .. why)
end

local level = Osi.GetRegion(Osi.GetHostCharacter())
if level ~= LEVEL then
    S.armed, S.error = false, "the level is " .. tostring(level) .. ", character creation is " .. LEVEL
    return { ok = false, error = S.error }
end

-- Places as the camera sees them: `back` metres further from the camera than the dummy, `side`
-- metres to the picture's right.
local fx, fz = DUMMY[1] - CAMERA[1], DUMMY[3] - CAMERA[3]
local length = math.sqrt(fx * fx + fz * fz)
fx, fz = fx / length, fz / length
local rx, rz = fz, -fx
local back, side, up = O.back or 5.5, O.side or -1.1, O.up or 0
local function place(b, s) return { DUMMY[1] + fx * b + rx * s, DUMMY[2] + up, DUMMY[3] + fz * b + rz * s } end
S.at = place(back, side)
-- She stands a step to the picture's left of the telescope and looks along it, to the right; the
-- bugbear comes up behind her back, further to the left.
local step = O.step or 0.55
local stand = place(back, side - step)
local ambush = place(back + (O.deep or 0.8), side - step - (O.behind or 0.8))
local facing = math.deg(math.atan(rx, rz)) + (O.turn or 0)

-- Links animation resources into every plain animation set of a creature's waterfall, each under
-- its own uuid as the short name: how many sets took them.
local function link(who, resources)
    local entity = Ext.Entity.Get(who)
    local sets = 0
    for _, w in ipairs(entity.AnimationWaterfall.Waterfall) do
        if tostring(w.Type) == "Visual" and tostring(w.Slot) == "" then
            local ok = pcall(function()
                local subset = Ext.Resource.Get(w.Resource, "AnimationSet").AnimationBank.AnimationSubSets[""]
                for _, resource in ipairs(resources) do subset.Animation[resource] = { ID = resource, flags = {} } end
            end)
            if ok then sets = sets + 1 end
        end
    end
    return sets
end

if not (GK.Stage.Ledger and GK.Stage.Ledger.baseline) then
    local b = GK.Stage.Begin()
    if b and b.lost then GK.Stage.Begin{ fresh = true } end
    S.ownScope = true
end
local made = try("spawn the telescope", GK.Stage.Spawn, { template = TELESCOPE, at = S.at, exact = true, rotation = facing })
S.telescope = made and made[1]
made = try("spawn the stargazer", GK.Stage.Spawn, { template = NADIRA, at = { DUMMY[1] + fx * 1.6 + rx * 0.6, DUMMY[2], DUMMY[3] + fz * 1.6 + rz * 0.6 }, faction = NEUTRAL })
S.id = made and made[1]
made = try("spawn the bugbear", GK.Stage.Spawn, { template = BUGBEAR, at = { DUMMY[1] + fx * 1.2 - rx * 0.6, DUMMY[2], DUMMY[3] + fz * 1.2 - rz * 0.6 }, faction = NEUTRAL })
S.bugbear = made and made[1]
if not (S.id and S.telescope and S.bugbear) then
    S.armed = false
    return { ok = false, error = "the spawn failed", log = S.log }
end
-- A teleport in the tick of the creation is obeyed, to a point off the ground too: neither is
-- ever drawn on the platform. She goes to her place, he waits under the ground below his.
Osi.TeleportToPosition(S.id, stand[1], stand[2], stand[3], "", 0, 0, 0, 0, 0)
Osi.TeleportToPosition(S.bugbear, ambush[1], ambush[2] - HIDE, ambush[3], "", 0, 0, 0, 0, 0)

-- The ambush: he comes on stage behind her, and after a beat the game's own fight.
function S.go()
    if not S.armed then return { ok = false, error = "not armed" } end
    if S.stage ~= "looking" then return { ok = false, error = "the scene is at " .. tostring(S.stage) .. ", the ambush starts from looking" } end
    S.stage = "appeared"
    Osi.TeleportToPosition(S.bugbear, ambush[1], ambush[2], ambush[3], "", 0, 0, 0, 0, 0)
    Osi.SteerTo(S.bugbear, S.id, 1)
    log("the bugbear appears behind her")
    try("her hit points", Osi.SetHitpoints, S.id, 1, "")
    S.pin = try("his attack die", GK.Rolls.Pin, { who = S.bugbear, kind = "Attack", n = O.crit == false and 19 or 20 })
    later((O.beat or 1.5) * 1000, function()
        -- Caught off guard: the status the story's own PROC_MakeSurprised gives, one turn.
        Osi.ApplyStatus(S.id, "SURPRISED", 6.0, 1, S.bugbear)
        Osi.SetRelationTemporaryHostile(S.bugbear, S.id)
        log("made enemies; she is surprised")
        when("both in the fight", function() return Osi.IsInCombat(S.id) == 1 and Osi.IsInCombat(S.bugbear) == 1 end, function()
            S.stage = "fight"
            S.fight.joined = true
            S.fight.surprised = Osi.HasActiveStatus(S.id, "SURPRISED") == 1
            log("the fight began" .. (S.fight.surprised and ", she is surprised" or ", she is not surprised"))
            when("her death", function() return Osi.IsDead(S.id) == 1 end, function()
                S.stage, S.killed, S.fired = "killed", true, true
                log("she is dead")
            end, 60, function() fail("she did not die within 60 s of the fight's start") end)
        end, 15, function() fail("no fight began within 15 s: in the fight - she " .. tostring(Osi.IsInCombat(S.id)) .. ", he " .. tostring(Osi.IsInCombat(S.bugbear))) end)
    end)
    return { ok = true, stage = S.stage }
end

later(300, function() Osi.SteerTo(S.id, S.telescope, 1) end)
later(1500, function()
    local x, y, z = Osi.GetPosition(S.id)
    log(string.format("stands at %.2f %.2f %.2f", x, y, z))
    S.linked = try("link the looking animations", link, S.id, LOOK)
    try("her animation", Osi.PlayLoopingAnimation, S.id, LOOK[1], LOOK[2], LOOK[3], "", "", "", "")
    S.looking = (S.linked or 0) > 0
    S.stage = "looking"
    log("looks through the telescope (the animations linked into " .. tostring(S.linked) .. " sets)")
    if O.after then later(O.after * 1000, function() if S.stage == "looking" then S.go() end end) end
end)
return { ok = true, id = S.id, telescope = S.telescope, bugbear = S.bugbear, at = S.at }
