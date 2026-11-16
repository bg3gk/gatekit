-- Demonstration 1: five goblins and Magic Missile.
-- Five goblins stand in a ring north of the host (+z); the camera is turned to look north, so the
-- "top" goblin is the far one. The host casts Magic Missile, every dart at the top goblin. A
-- handler on the extender's damage events takes each dart's damage off the top goblin, and the
-- first dart deals 1000 force to each of the other four through GK.Stage.Damage.
-- Run:  ./gatekit --instance <copy> exec client "return GK.Camera.Look(0, { now = true })"
--       ./gatekit --instance <copy> exec server "$(cat research/demos/missile_redirect.lua)"
-- End:  ./gatekit --instance <copy> exec server "$(cat research/demos/missile_redirect_end.lua)"
-- Read what happened: exec server "return GKDemo1.report"
if GKDemo1 and GKDemo1.stop then GKDemo1.stop() end
GK.Stage.Begin()
local host = Osi.GetHostCharacter()
local D = { goblins = {}, report = { hpBefore = {}, hpAfter = {}, darts = {}, dealt = {} }, subs = {} }
GKDemo1 = D

-- the host knows the spell and has slots; the host acts first
GK.Rolls.Pin{ kind = "Initiative", n = 100 }
GK.Stage.Teach("Projectile_MagicMissile", host)
local slots = "ActionResource(SpellSlot,4,1);ActionResource(SpellSlot,4,2)"
GK.Stage.Track{ kind = "other", note = "demo: spell slots of the host", op = { op = "boost", who = host, text = slots, cause = "GKDemo" } }
Osi.AddBoosts(host, slots, "GKDemo", host)

-- the ring: centre 7 m north of the host, radius 3 m, the first goblin at the top (north)
local x, y, z = Osi.GetPosition(host)
local cx, cz, r = x, z + 7, 3
for n = 0, 4 do
    local a = math.rad(72 * n)
    local gx, gz = cx + r * math.sin(a), cz + r * math.cos(a)
    local id = GK.Stage.Spawn{ template = "goblin", at = { gx, y, gz }, faction = "enemy" }[1]
    D.goblins[#D.goblins + 1] = id
end
D.top = D.goblins[1]

local function uuidOf(handle)
    local e = handle and Ext.Entity.Get(handle)
    return e and e.Uuid and e.Uuid.EntityUuid
end
local function hp(id) local e = Ext.Entity.Get(id) return e and e.Health and e.Health.Hp end

-- DealDamage names the target (a copy: writing it changes nothing); BeforeDealDamage, raised
-- inside it as the game throws the damage event, holds the computed hit, whose amounts a
-- handler may write. So: remember the target, then zero the hit when it is the top goblin.
local current
D.subs.deal = Ext.Events.DealDamage:Subscribe(function(e) current = uuidOf(e.Target) end)
D.subs.before = Ext.Events.BeforeDealDamage:Subscribe(function(e)
    if current ~= D.top then return end
    local dart = { rolled = e.Hit.TotalDamageDone }
    e.Hit.TotalDamageDone, e.Attack.TotalDamageDone = 0, 0
    for _, p in ipairs(e.Hit.DamageList) do p.Amount = 0 end
    for _, p in ipairs(e.Attack.DamageList) do p.Amount = 0 end
    D.report.darts[#D.report.darts + 1] = dart
    if #D.report.darts == 1 then
        -- the others pay: a tick later, outside the game's own damage code
        Ext.OnNextTick(function()
            for i = 2, #D.goblins do
                local ok, answer = pcall(GK.Stage.Damage, 1000, D.goblins[i], "Force", true)
                D.report.dealt[#D.report.dealt + 1] = { who = D.goblins[i], ok = ok, error = not ok and tostring(answer) or nil }
            end
        end)
    end
end)

-- a small clock: wait for a condition, then act
local function when(cond, fn, tries)
    tries = tries or 120
    if cond() then return fn() end
    if tries <= 0 then D.report.stuck = true return end
    Ext.Timer.WaitFor(250, function() if GKDemo1 == D then when(cond, fn, tries - 1) end end)
end

when(function() for _, g in ipairs(D.goblins) do if (hp(g) or 0) <= 0 then return false end end return true end, function()
    for _, g in ipairs(D.goblins) do D.report.hpBefore[g] = hp(g) end
    GK.Stage.Fight(D.goblins)
    when(function() local c = GK.Stage.Combat() return c and c.active == host end, function()
        Ext.Timer.WaitFor(1500, function()
            D.report.cast = GK.Cheat.Cast(host, "Projectile_MagicMissile", D.top)
            Ext.Timer.WaitFor(1000, function()
                when(function() local e = Ext.Entity.Get(host) return e.SpellCastIsCasting == nil end, function()
                    Ext.Timer.WaitFor(1500, function()
                        for _, g in ipairs(D.goblins) do D.report.hpAfter[g] = { hp = hp(g), dead = Osi.IsDead(g) == 1 } end
                        D.report.done = true
                    end)
                end)
            end)
        end)
    end)
end)

function D.stop()
    for name, sub in pairs(D.subs) do
        if name == "deal" then Ext.Events.DealDamage:Unsubscribe(sub) else Ext.Events.BeforeDealDamage:Unsubscribe(sub) end
    end
    D.subs = {}
end
return { goblins = D.goblins, top = D.top }
