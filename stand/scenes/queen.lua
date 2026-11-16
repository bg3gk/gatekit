-- Scene "queen" (the video stand): a party member shoves Lae'zel; the shove throws her far and she
-- is gone; Vlaakith appears beside the shover, says her line and casts Power Word Kill on him.
-- Arm:    ./gatekit --instance <copy> exec server "$(cat stand/scenes/queen.lua)"
-- Disarm: ./gatekit --instance <copy> exec server "$(cat stand/scenes/queen_off.lua)"
-- The driver: make a checkpoint while GKScene.queen.ready is true (armed, out of a fight, Lae'zel
-- alive and within reach of a party member); after the death screen, `reset` to it.
-- State:  GKScene.queen = { armed, ready, readyWhy, fired, done, log = { { tick, ms, text } } }

GKScene = GKScene or {}
local NAME = "queen"
local LAEZEL = "58a69333-40bf-8358-1d17-fff240d7fb12"
local VLAAKITH = "8bdc382b-a31e-4c1c-8987-656ceb333f05"          -- her root template (18 hit points)
local SHOVE = "Target_Shove"                                      -- matched as a prefix
local THROW = "14"                                                -- the shove's distance while it is hers, metres
local LINE = "Ты за это ответишь."
local ARRIVE = "3725b5c2-8274-a2fe-3148-d071c77ffcaa"            -- VFX_Spells_Cast_Intent_Utility_TargetJump_MistyStep_Root_Textkey_02
local ARRIVE_SOUND = "8e5d5b16-e75b-4786-18e1-8ee39bab3ab3"      -- Spell_Impact_Utility_MistyStep_L1to3
local POOF = "f0cf792a-0f74-d17e-ad0d-6052a6131416"              -- VFX_Script_Stub_Poof_01

local old = GKScene[NAME]
if old and old.armed then return { ok = true, already = true, fired = old.fired, ready = old.ready } end

local S = { armed = true, ready = false, readyWhy = "not looked yet", fired = false, done = false, log = {}, made = {} }
GKScene[NAME] = S

local function log(text)
    S.log[#S.log + 1] = { tick = GK.Speed.Clock().ticks, ms = Ext.Utils.MonotonicTime(), text = text }
end
local function try(what, fn, ...)
    local ok, v = pcall(fn, ...)
    if not ok then log("failed: " .. what .. ": " .. tostring(v)) end
    return ok and v or nil
end
local function later(ms, fn)
    Ext.Timer.WaitForRealtime(ms, function() if S.armed then try("a step", fn) end end)
end
local function focus(at, hold) try("focus", GK.Camera.Focus, at, { glide = true, hold = hold }) end
local function bare(id) return id and tostring(id):match("(%x+%-%x+%-%x+%-%x+%-%x+)$") or id end
local function party() return GK.Stage.Party() end
local function isParty(id) for _, m in ipairs(party()) do if m == id then return true end end return false end

-- a stage scope for the spawn's ledger: the stand's when one is open, else the scene's own
if not (GK.Stage.Ledger and GK.Stage.Ledger.baseline) then
    local b = GK.Stage.Begin()
    if b and b.lost then GK.Stage.Begin{ fresh = true } end
    S.ownScope = true
end

GK.Say.Styles.queen =GK.Say.Styles.queen or { color = "#7FFFD4", bold = true, prefix = "• ", suffix = " •", accent = "#E0FFF0", sound = ARRIVE_SOUND }

-- the shove's two Force functors (an enemy's and an ally's), read anew each time (a functor held
-- across ticks expires); their Distance is the game's expression ShoveDistance
local function setThrow(distance)
    local spell = Ext.Stats.Get(SHOVE)
    for _, t in ipairs(spell.SpellSuccess) do
        for _, f in ipairs(t.Functors) do
            if f.TypeId == "Force" then
                f.Distance = distance or "ShoveDistance"   -- the game's own expression
            end
        end
    end
end

-- ready: what the driver waits for before its checkpoint
local function lookReady()
    if not S.armed then return end
    local why
    local alive = Osi.IsDead(LAEZEL) == 0 and isParty(LAEZEL)
    if S.fired then why = "fired"
    elseif not alive then why = "Lae'zel is not an alive party member"
    elseif Osi.IsInCombat(LAEZEL) == 1 then why = "a fight is on"
    else
        local near
        for _, m in ipairs(party()) do
            if m ~= LAEZEL and (Osi.GetDistanceTo(m, LAEZEL) or 99) < 4 then near = m end
        end
        why = near and "ready" or "nobody of the party is within 4 m of Lae'zel"
    end
    S.ready, S.readyWhy = why == "ready", why
    Ext.Timer.WaitForRealtime(500, lookReady)
end
lookReady()

local function queen(shover)
    -- beside the shover, on the side away from where Lae'zel flew
    local x, y, z = Osi.GetPosition(shover)
    local lx, _, lz = table.unpack(S.from or { x + 1, y, z })
    local dx, dz = x - lx, z - lz
    local d = math.sqrt(dx * dx + dz * dz)
    if d < 0.1 then dx, dz, d = 1, 0, 1 end
    local px, pz = x + dx / d * 2.0, z + dz / d * 2.0
    local yaw = math.deg(math.atan(x - px, z - pz))
    try("arrival effect", Osi.PlayEffectAtPosition, ARRIVE, px, y, pz, 1.4)
    try("arrival accent", GK.Say.Accent, { px, y, pz }, { effect = "spectral" })
    local made = try("spawn", GK.Stage.Spawn, { template = VLAAKITH, at = { px, y, pz }, faction = "neutral", rotation = yaw })
    local v = made and made[1]
    if not v then log("Vlaakith could not be made: the scene stops"); return end
    S.vlaakith = v
    S.made[#S.made + 1] = v
    try("teach", GK.Stage.Teach, "Target_PowerWordKill", v)
    log("Vlaakith appears beside " .. shover)
    later(700, function()
        try("turn", Osi.SteerTo, v, shover, 0)
        focus(v, 5)
    end)
    later(1300, function()
        try("line", GK.Say.Line, v, LINE, { style = "queen" })
        log("Vlaakith: " .. LINE)
    end)
    later(4600, function()
        GK.Stage.Cast("Target_PowerWordKill", shover, v)
        log("Vlaakith casts Power Word Kill at " .. shover)
    end)
    local tries = 0
    local function dead()
        tries = tries + 1
        if Osi.IsDead(shover) == 1 then
            -- Lae'zel, off stage since her fall, dies only now: the death screen needs the whole
            -- party dead, and her death earlier called up the game's hints ("Смерть персонажа")
            try("die", Osi.Die, LAEZEL, 0, "NULL_00000000-0000-0000-0000-000000000000", 0, 1, 0)
            S.done = true
            log("the shover is dead: the driver resets to its checkpoint")
        elseif tries < 40 then
            later(250, dead)
        else
            log("the shover did not die within 10 s of the cast")
        end
    end
    later(5200, dead)
end

local function fire(shover)
    if S.fired then return end
    S.fired = true
    S.ready = false
    log("fired: " .. shover .. " shoved Lae'zel")
    -- she flies now; her cry, and the camera follows her landing
    try("cry", GK.Say.Line, LAEZEL, "Предатель!..", { style = "warning" })
    later(700, function() focus(LAEZEL) end)
    later(2400, function()
        local x, y, z = Osi.GetPosition(LAEZEL)
        S.landed = { x, y, z }
        try("poof", Osi.PlayEffectAtPosition, POOF, x, y, z, 1.0)
        GK.Stage.OnStage(LAEZEL, false)
        log("Lae'zel went over the edge: off stage")
    end)
    later(3200, function() focus(shover) end)
    later(4600, function() queen(shover) end)
end

-- the trigger: a shove at Lae'zel by a party member. UsingSpellOnTarget comes before the hit (its
-- force): the throw is lengthened then, and put back when the cast is over (CastedSpell).
local shoving = nil
Ext.Osiris.RegisterListener("UsingSpellOnTarget", 6, "after", function(caster, target, spell)
    if not S.armed or S.fired then return end
    caster = bare(caster)
    if bare(target) == LAEZEL and tostring(spell):sub(1, #SHOVE) == SHOVE and isParty(caster) and caster ~= LAEZEL then
        setThrow(THROW)
        shoving = caster
        local x, y, z = Osi.GetPosition(LAEZEL)
        S.from = { x, y, z }
        log("seen: " .. tostring(spell) .. " at Lae'zel by " .. caster .. "; the throw set to " .. THROW .. " m")
    end
end)
Ext.Osiris.RegisterListener("CastedSpell", 5, "after", function(caster, spell)
    if not shoving or bare(caster) ~= shoving or tostring(spell):sub(1, #SHOVE) ~= SHOVE then return end
    local who = shoving
    shoving = nil
    later(300, function()
        setThrow(nil)
        if not S.armed then return end
        local x, _, z = Osi.GetPosition(LAEZEL)
        local moved = S.from and math.sqrt((x - S.from[1]) ^ 2 + (z - S.from[3]) ^ 2) or 0
        log(string.format("the shove is over: Lae'zel moved %.1f m", moved))
        fire(who)
    end)
end)

log("armed")
return { ok = true, armed = true }
