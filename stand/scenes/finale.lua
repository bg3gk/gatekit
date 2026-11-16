-- Scene "finale" (the video stand): nobody uses the transponder. On the driver's call the owner's
-- character leaves the fight, the Netherbrain appears beside the transponder, the character throws
-- a Runepowder Barrel at it, the brain dies in the blast, the screen fades to black and the game's
-- credits roll. After that the run is over: GKScene.finale.done is true and the driver does nothing.
-- Arm:    ./gatekit --instance <copy> exec server "$(cat stand/scenes/finale.lua)"
-- Go:     ./gatekit --instance <copy> exec server "return GKScene.finale.go()"
-- Disarm: ./gatekit --instance <copy> exec server "$(cat stand/scenes/finale_off.lua)"
-- Server context, the prologue's level. Arming does nothing in the game; `go` starts the beats, and
-- each waits for the game where the game says something:
--   leaving   Osi.SetCanJoinCombat(member, 0) and Osi.EndCombat(the fight), as the story takes a
--             creature out of a fight; waits for Osi.IsInCombat to answer 0. The game's own Flee is
--             refused in the helm ("this place does not allow fleeing").
--   coming    only when the character stands farther than `reach` from the brain's place: it walks
--             (GK.Cheat.Move) until it is within reach
--   arriving  the brain: the root template Elder_Brain_Crowned_Highres, which the placed object
--             S_GLO_Netherbrain of the last level is made from, with that object's name, turned to
--             the character, scaled by `scale`, its hit points set to `hp` (one barrel deals 50 to
--             120, halved by a saved throw; the brain has 275); the burst of its last confrontation
--             and the crown's glow as its arrival; the camera's focus on it for `behold` seconds
--   arming    the barrel (UNI_UND_Runepowder_Barrel) comes into the character's pack and its hit
--             points are set to 1: a thrown barrel takes 1 to 4 and breaks only at 0, and a barrel
--             explodes when it breaks. The party cannot be hurt for the blast (the status
--             INVULNERABLE_NOT_SHOWN, the story's own PROC_SetInvulnerable) and the character is as
--             strong as a cloud giant (the elixir's status): a barrel weighs more than it can throw
--   throwing  GK.Cheat.Throw(character, barrel, brain): the game's own Throw action; waits for the
--             brain's death
--   blast     `after` seconds of the explosion on the screen
--   fading    Osi.ScreenFadeTo, the story's fade; waits for the Osiris event ScreenFadeDone
--   credits   PROC_StartMovie("Credits"), the line the story rolls the credits with at the end of
--             the game. The credits page closes when it has rolled out or is closed (its command
--             CreditsEnded) and by itself only raises the Osiris event CreditsEnded; the story
--             answers that event with GameEnd, which takes the session to the main menu. The scene
--             answers it the same way, PROC_GameEndWithMovie(""), unless `menu` is false (then the
--             game stays in the level under the black screen)
-- Options, before arming: GKSceneOptions = { finale = { at = {x, y, z}, scale = 2, hp = 20,
-- companions = "leave" | "fight", reach = 14, behold = 3.5, after = 2.5, fade = 2.0,
-- wait = 20, black = 0, menu = true } }. `black`: seconds the black screen is held before the
-- credits, for the picture stand/scenes/finale_credits.lua shows on it (its seconds and fades).
-- State: GKScene.finale = { armed, fired, stage, waiting, error, done, creditsEnded, brain, barrel,
-- death, log = { { tick, ms, text } } }.

GKScene = GKScene or {}
local NAME = "finale"
local old = GKScene[NAME]
if old and old.off then pcall(old.off) end

local O = (GKSceneOptions and GKSceneOptions.finale) or {}
local BRAIN = "28e25c38-ed31-41a2-b084-8f15560e7a36"             -- Elder_Brain_Crowned_Highres
local BRAIN_NAME = "h1074521bg6c4cg4a05g902ag2bb378602221"       -- the display name of S_GLO_Netherbrain
local BARREL = "79dfb371-cd87-48f3-b542-26db2d39cfe2"            -- UNI_UND_Runepowder_Barrel
local BURST = "3de76076-0033-74a0-40a0-cddfb73895ec"             -- VFX_Cinematic_Netherbrain_Confrontation_PsionicBurst_01
local GLOW = "9a05ba66-4730-3d1d-f175-32a9d34dfb7a"              -- VFX_Cinematic_Elderbrain_Crown_Glow_01
local UNHURT = "INVULNERABLE_NOT_SHOWN"
local STRONG = "POTION_OF_STRENGTH_CLOUD_GIANT"
local FADE = "GK_Scene_Finale"
local AT = O.at or { -74.0, 18.4, -389.0 }                        -- on the deck, 8 m from the transponder

local S = { armed = true, fired = false, stage = "armed", done = false, log = {}, statuses = {}, barred = {} }
GKScene[NAME] = S

local function now() return Ext.Utils.MonotonicTime() end
local function log(text)
    S.log[#S.log + 1] = { tick = GK.Speed.Clock().ticks, ms = now(), text = text }
end
local function try(what, fn, ...)
    local ok, v = pcall(fn, ...)
    if not ok then log("failed: " .. what .. ": " .. tostring(v)) end
    return ok and v or nil
end
local function stage(name) S.stage, S.since, S.waiting = name, now(), nil log(name) end
local function elapsed() return (now() - S.since) / 1000 end
local function fail(text) S.error = text log("stopped: " .. text) end
local function bare(id) return id and tostring(id):match("(%x+%-%x+%-%x+%-%x+%-%x+)$") or id end
local function distance(a, b)
    local ax, _, az = Osi.GetPosition(a)
    local bx, bz = b[1], b[3]
    if type(b) == "string" then bx, _, bz = Osi.GetPosition(b) end
    return math.sqrt((ax - bx) ^ 2 + (az - bz) ^ 2)
end

-- a stage scope for the spawn's and the statuses' ledger: the stand's when one is open
if not (GK.Stage.Ledger and GK.Stage.Ledger.baseline) then
    local b = GK.Stage.Begin()
    if b and b.lost then GK.Stage.Begin{ fresh = true } end
    S.ownScope = true
end

local function status(name, who)
    try("status " .. name, GK.Stage.Status, name, who, -1)
    S.statuses[#S.statuses + 1] = { name, who }
end

local steps = {}

function steps.leaving()
    if Osi.IsInCombat(S.who) == 1 then
        S.waiting = "the character to be out of the fight"
        if elapsed() > (O.wait or 20) then fail("the character is still in a fight " .. (O.wait or 20) .. " s after Osi.EndCombat") end
        return
    end
    log("the character is out of the fight")
    if distance(S.who, AT) > (O.reach or 14) then
        local x, y, z = Osi.GetPosition(S.who)
        local d = distance(S.who, AT)
        local keep = (O.reach or 14) - 3
        local to = { AT[1] + (x - AT[1]) / d * keep, y, AT[3] + (z - AT[3]) / d * keep }
        S.walk = try("walk", GK.Cheat.Move, S.who, to, { partial = true })
        stage("coming")
    else
        stage("arriving")
    end
end

function steps.coming()
    if distance(S.who, AT) <= (O.reach or 14) then stage("arriving") return end
    S.waiting = "the character to come within reach of the brain's place"
    if elapsed() > (O.wait or 20) * 2 then fail("the character did not come within " .. (O.reach or 14) .. " m of the brain's place") end
end

function steps.arriving()
    if not S.brain then
        local x, _, z = Osi.GetPosition(S.who)
        local yaw = math.deg(math.atan(x - AT[1], z - AT[3]))
        try("burst", Osi.PlayEffectAtPosition, BURST, AT[1], AT[2], AT[3], 1.0)
        local made = try("spawn", GK.Stage.Spawn, { template = BRAIN, at = AT, faction = "neutral", rotation = yaw })
        S.brain = made and made[1]
        if not S.brain then fail("the brain could not be made") return end
        try("focus", GK.Camera.Focus, AT, { glide = true, hold = (O.behold or 3.5) + 1 })
        return
    end
    local e = Ext.Entity.Get(S.brain)
    if not (e and e.Health and e.GameObjectVisual) then S.waiting = "the brain to be there" return end
    if not S.dressed then
        S.dressed = true
        try("scale", function()
            e.GameObjectVisual.Scale = O.scale or 2.0
            e:Replicate("GameObjectVisual")
        end)
        try("name", function()
            e.DisplayName.NameKey.Handle.Handle = BRAIN_NAME
            e:Replicate("DisplayName")
        end)
        -- its hit points are set without a hit: no number floats over it
        try("hit points", Osi.SetHitpoints, S.brain, O.hp or 20, "")
        S.glow = try("glow", Osi.PlayLoopEffect, S.brain, GLOW, "", 1.0)
        try("turn", Osi.SteerTo, S.who, S.brain, 0)
        log("the brain stands beside the transponder: " .. S.brain)
    end
    if elapsed() < (O.behold or 3.5) then return end
    for _, member in ipairs(GK.Stage.Party()) do status(UNHURT, member) end
    status(STRONG, S.who)
    try("barrel", Osi.TemplateAddTo, BARREL, S.who, 1, 0)
    stage("arming")
end

function steps.arming()
    if not S.barrel then
        local found = try("find", GK.Find, BARREL, S.who)
        S.barrel = found and found[1]
        if not S.barrel then
            S.waiting = "the barrel to be in the pack"
            if elapsed() > (O.wait or 20) then fail("no barrel came into the pack") end
            return
        end
        try("barrel's hit points", Osi.SetHitpoints, S.barrel, 1, "")
        return
    end
    -- the set hit points are there a tick later, and the statuses' boosts a few ticks later
    if Osi.GetHitpoints(S.barrel) ~= 1 or Osi.HasActiveStatus(S.who, STRONG) ~= 1 or Osi.HasActiveStatus(S.who, UNHURT) ~= 1 then
        S.waiting = "the barrel's one hit point and the character's statuses"
        if elapsed() > (O.wait or 20) then fail("the barrel or the character was not made ready") end
        return
    end
    if elapsed() < 0.5 then return end
    try("focus", GK.Camera.Focus, { S.who, S.brain }, { glide = true, hold = 8 })
    local answer = try("throw", GK.Cheat.Throw, S.who, S.barrel, S.brain)
    S.throw = answer
    if not (answer and answer.job) then
        fail("the throw was refused: " .. tostring(answer and answer.error or "no answer"))
        return
    end
    log("the barrel is thrown at the brain")
    stage("throwing")
end

function steps.throwing()
    if Osi.IsDead(S.brain) == 1 then
        log("the brain is dead")
        if S.glow then try("glow off", Osi.StopLoopEffect, S.glow) S.glow = nil end
        stage("blast")
        return
    end
    local job = try("job", GK.Act.Job, S.throw.job)
    if job and job.status == "failed" and not S.throwFailed then
        S.throwFailed = true
        fail("the game refused the throw: " .. tostring(job.error))
        return
    end
    S.waiting = "the brain's death"
    if elapsed() > (O.wait or 20) then
        fail("the brain did not die within " .. (O.wait or 20) .. " s of the throw (its hit points: " .. tostring(Osi.GetHitpoints(S.brain)) .. ")")
    end
end

function steps.blast()
    -- the game's own row of the death comes some ticks after the death: who killed it
    if not S.death then
        for _, row in ipairs(GK.EventLog.Since(S.seq, { kinds = { game_death = true }, limit = 5000 }).rows) do
            if bare(row.payload and row.payload.who) == S.brain then
                S.death = { by = row.payload.by, text = row.payload.text, seq = row.seq }
                log("the game's row: " .. tostring(row.payload.text))
            end
        end
    end
    if elapsed() < (O.after or 2.5) then return end
    try("fade", Osi.ScreenFadeTo, S.who, O.fade or 2.0, 0, FADE)
    S.faded = false
    stage("fading")
end

function steps.fading()
    if not S.faded then
        S.waiting = "the screen to be black (ScreenFadeDone)"
        if elapsed() < (O.fade or 2.0) + 5 then return end
        log("no ScreenFadeDone came: the credits start all the same")
        S.faded = true
    end
    -- the black is held for the picture finale_credits.lua shows on it
    S.blackAt = S.blackAt or now()
    if (now() - S.blackAt) / 1000 < (O.black or 0) then S.waiting = "the black screen to be held" return end
    -- the story's own line; it plays a movie once for a profile, so a second run in one game
    -- makes the call of that procedure directly
    local played = try("fact", function() return #Osi.DB_MoviePlayed:Get(nil, "Credits") > 0 end)
    if played then
        try("credits", Osi.MoviePlay, S.who, "Credits", 1)
        log("the credits: Osi.MoviePlay (the story's fact says they were played before in this game)")
    else
        try("credits", Osi.PROC_StartMovie, "Credits")
        log("the credits: PROC_StartMovie(\"Credits\")")
    end
    S.done = true
    stage("credits")
end

function steps.credits() end

local function tick()
    if not (S.armed and S.fired) or S.error then return end
    local step = steps[S.stage]
    if step then step() end
end

S.subscription = Ext.Events.Tick:Subscribe(function()
    local ok, problem = pcall(tick)
    if not ok and not S.error then fail(tostring(problem)) end
end)

Ext.Osiris.RegisterListener("ScreenFadeDone", 2, "after", function(_, id)
    if S.armed and id == FADE then S.faded = true end
end)
Ext.Osiris.RegisterListener("CreditsEnded", 0, "after", function()
    if S.armed and S.stage == "credits" then
        S.creditsEnded = true
        if O.menu == false then
            log("the credits ended; the screen stays black")
        else
            -- as the story ends a game whose credits are over (the ending at Moonrise answers
            -- CreditsEnded so): the Osiris call GameEnd, which takes the session to the main menu
            log("the credits ended: PROC_GameEndWithMovie(\"\"), the story's GameEnd")
            try("game end", Osi.PROC_GameEndWithMovie, "")
        end
    end
end)

--- The driver calls this on the owner's command.
function S.go()
    if S.fired then return { ok = false, error = "already going", stage = S.stage } end
    if not S.armed then return { ok = false, error = "disarmed" } end
    S.who = Osi.GetHostCharacter()
    S.fired = true
    S.seq = GK.EventLog.Since(1e12).seq
    local members = (O.companions == "fight") and { S.who } or GK.Stage.Party()
    for _, member in ipairs(members) do
        if Osi.CanJoinCombat(member) == 1 then
            Osi.SetCanJoinCombat(member, 0)
            S.barred[#S.barred + 1] = member
        end
    end
    local fight = try("fight", Osi.CombatGetGuidFor, S.who)
    if fight then Osi.EndCombat(fight) end
    log("go: " .. #S.barred .. " of the party can join no fight" .. (fight and ("; the fight " .. tostring(fight) .. " is ended") or "; no fight was on"))
    stage("leaving")
    return { ok = true, who = S.who, fight = fight, barred = S.barred }
end

function S.off()
    S.armed = false
    if S.subscription then pcall(function() Ext.Events.Tick:Unsubscribe(S.subscription) end) S.subscription = nil end
    if S.who then pcall(Osi.ClearScreenFade, S.who, 1.0, FADE, 0) end
    if S.glow then pcall(Osi.StopLoopEffect, S.glow) S.glow = nil end
    for _, pair in ipairs(S.statuses) do pcall(GK.Stage.Unstatus, pair[1], pair[2]) end
    S.statuses = {}
    for _, member in ipairs(S.barred) do pcall(Osi.SetCanJoinCombat, member, 1) end
    S.barred = {}
    if S.brain and Ext.Entity.Get(S.brain) then pcall(GK.Stage.Remove, S.brain) end
    if S.barrel and Ext.Entity.Get(S.barrel) then pcall(Osi.RequestDelete, S.barrel) end
    S.stage = "off"
    return true
end

log("armed")
return { ok = true, armed = true, at = AT, ownScope = S.ownScope == true }
