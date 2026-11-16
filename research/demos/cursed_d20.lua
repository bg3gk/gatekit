-- Demonstration 4: the cursed d20.
-- The host and two goblins trade attacks (the host shoots on each of its turns; all are kept
-- alive by the stage). A curse hangs on the dice: every third attack roll of the host comes up a
-- natural 1, every third attack roll of a goblin a natural 20 - and a notification says so as
-- the die lands. Nothing but the extender's Roll event does it: the handler writes `Natural`
-- after the throw, and the game moves the total and the critical with it by its own rule, so the
-- hit, the damage (doubled dice on a 20) and the combat log follow the cursed die.
-- Run:  ./gatekit --instance <copy> exec client "return GK.Camera.Look(0, { now = true })"
--       ./gatekit --instance <copy> exec server "$(cat research/demos/cursed_d20.lua)"
-- End:  ./gatekit --instance <copy> exec server "$(cat research/demos/cursed_d20_end.lua)"
-- Read: exec server "return GKDemo4.report"
if GKDemo4 and GKDemo4.stop then GKDemo4.stop() end
GK.Stage.Begin()
local host = Osi.GetHostCharacter()
local D = { goblins = {}, count = { host = 0, goblins = 0 }, report = { rolls = {} } }
GKDemo4 = D

local function name(id) return Osi.ResolveTranslatedString(Osi.GetDisplayName(id)) or id end
local function uuidOf(handle) local e = handle and Ext.Entity.Get(handle) return e and e.Uuid and e.Uuid.EntityUuid end

-- the ledger keeps the hit points of the host's FIRST heal as what Clean puts back: heal it now,
-- at full health, so that the heals of the fight do not make Clean restore a low number
GK.Stage.Heal(host)
GK.Rolls.Pin{ kind = "Initiative", n = 100 }
local x, y, z = Osi.GetPosition(host)
for n = 1, 2 do
    local g = GK.Stage.Spawn{ template = "goblin", at = { x + (n == 1 and -2 or 2), y, z + 7 }, faction = "enemy" }[1]
    GK.Stage.Immortal(true, g)
    D.goblins[#D.goblins + 1] = g
end

-- the curse: the Roll event, after the throw
D.onRoll = Ext.Events.Roll:Subscribe(function(e)
    if not tostring(e.RollType):find("Attack") then return end
    local who = uuidOf(e.Roller)
    if not who then return end
    local side = who == host and "host" or "goblins"
    D.count[side] = D.count[side] + 1
    local row = { who = who, kind = tostring(e.RollType), n = D.count[side], thrown = e.Natural, totalWas = e.Total, dc = e.DC }
    if D.count[side] % 3 == 0 then
        local die = side == "host" and 1 or 20
        e.Natural = die
        row.cursed, row.natural, row.total, row.critical = die, e.Natural, e.Total, tostring(e.Critical)
        Osi.ShowNotification(host, string.format("The curse! %s's attack no. %d: the d20 showed %d - it is now a natural %d",
            name(who), D.count[side], row.thrown, die))
    end
    table.insert(D.report.rolls, row)
end)

-- the clock: on the host's turn, shoot at a goblin, then end the turn
local hostRound, shots = nil, 0
local function clock()
    if GKDemo4 ~= D then return end
    -- immortality stops death, not the fall: at 0 hit points the host lies down and cannot act,
    -- and cursed 20s fall often - so the host is healed whenever it runs low
    local e = Ext.Entity.Get(host)
    if e.Health.Hp <= 4 and not D.healing then
        D.healing = true
        GK.Stage.Heal(host)
        Ext.Timer.WaitFor(500, function() D.healing = false end)
    end
    local c = GK.Stage.Combat()
    if c and c.active == host and c.round ~= hostRound then
        hostRound = c.round
        local round = c.round
        local function shoot(try)
            if GKDemo4 ~= D then return end
            shots = shots + 1
            local cast = GK.Cheat.Cast(host, "Projectile_MainHandAttack", D.goblins[(shots % 2) + 1])
            Ext.Timer.WaitFor(1000, function()
                -- the game refuses a cast now and then at the start of a turn: try again
                local job = cast.job and GK.Act.Job(cast.job)
                D.report.lastCast = job or cast
                if (not job or job.status == "failed") and try < 4 then return shoot(try + 1) end
                Ext.Timer.WaitFor(3500, function()
                    local now = GK.Stage.Combat()
                    if GKDemo4 == D and now and now.active == host and now.round == round then GK.Stage.EndTurn(host) end
                end)
            end)
        end
        Ext.Timer.WaitFor(2000, function() shoot(1) end)
    end
    Ext.Timer.WaitFor(200, clock)
end
Ext.Timer.WaitFor(1500, function() GK.Stage.Fight(D.goblins) clock() end)

function D.stop()
    if D.onRoll then Ext.Events.Roll:Unsubscribe(D.onRoll) D.onRoll = nil end
end
return { goblins = D.goblins }
