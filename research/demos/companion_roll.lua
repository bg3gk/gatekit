-- Demonstration 2: a companion rolls the dialogue check, not the one who speaks.
-- A second party member (a spawned fighter made the host's party follower, with a boost
-- Skill(Persuasion,5) so that her bonus differs from the host's) stands beside the host; the
-- test level's counting-house guard starts a dialogue with a Persuasion check. When the game makes
-- the check's request (the RequestedRoll component, created as the answer is chosen), its roller is
-- swapped for the companion. The game then builds the roll as hers: the window lists her modifiers
-- (Charisma +2, +5: total +7, where the host has +2), the roll events and Osiris RollResult name her,
-- and her total decides the line. A BeforeRoll handler writes her own skill total (her Stats) as the
-- bonus - a guard that has found the game's bonus already hers every time it was run.
-- Run:  ./gatekit --instance <copy> exec server "$(cat research/demos/companion_roll.lua)"
--       then answer 1 and throw - by hand, or:
--       ./gatekit --instance <copy> exec ui "return GK.Answer(1)"
--       ./gatekit --instance <copy> exec ui "return GK.Gui.Resolve{ roll = 'throw' }"   (throw; again: continue)
-- End:  ./gatekit --instance <copy> exec server "$(cat research/demos/companion_roll_end.lua)"
-- Read: exec server "return GKDemo2.report"
if GKDemo2 and GKDemo2.stop then GKDemo2.stop() end
GK.Stage.Begin()
local host = Osi.GetHostCharacter()
local D = { report = { swaps = {}, rolls = {} } }
GKDemo2 = D

-- the companion: a fighter of the game's, a follower of the host's party for the scene
local x, y, z = Osi.GetPosition(host)
D.companion = GK.Stage.Spawn{ template = GK.Fixture.Defaults.enemy, at = { x - 1.5, y, z - 1 } }[1]
Osi.AddPartyFollower(D.companion, host)
GK.Stage.OnClean(function() Osi.RemovePartyFollower(D.companion, host) return true end, "demo: the companion leaves the party")
local boost = "Skill(Persuasion,5)"
GK.Stage.Track{ kind = "other", note = "demo: the companion's silver tongue", op = { op = "boost", who = D.companion, text = boost, cause = "GKDemo" } }
Osi.AddBoosts(D.companion, boost, "GKDemo", D.companion)

local function uuidOf(entity) return entity and entity.Uuid and entity.Uuid.EntityUuid end
local function skillTotal(id, skill)
    local stats = Ext.Entity.Get(id).Stats
    return stats.Skills[Ext.Enums.SkillId[skill].Value + 1]
end

-- (b) the game's own request: its roller becomes the companion before the window opens
D.onRequest = Ext.Entity.OnCreateDeferred("RequestedRoll", function(entity)
    local r = entity.RequestedRoll
    if not (r and r.DialogId ~= 0 and r.EntityUuid == host) then return end
    r.Roller = Ext.Entity.Get(D.companion)
    r.EntityUuid = D.companion
    D.report.swaps[#D.report.swaps + 1] = { skill = tostring(r.Skill), dc = r.DC, hostBonus = r.DiceAdditionalValue }
end)

-- (a) the throw "as the companion": her own skill total replaces the speaker's bonus
D.onBefore = Ext.Events.BeforeRoll:Subscribe(function(e)
    if e.RollType ~= "SkillCheck" or uuidOf(e.Roller) ~= D.companion then return end
    local swap = D.report.swaps[#D.report.swaps]
    local skill = swap and swap.skill or "Persuasion"
    local was = e.Bonus
    e.Bonus = skillTotal(D.companion, skill)
    D.report.rolls[#D.report.rolls + 1] = { skill = skill, bonusWas = was, bonus = e.Bonus, dc = e.DC,
                                            hostWouldHave = skillTotal(host, skill) }
end)
D.onRoll = Ext.Events.Roll:Subscribe(function(e)
    if uuidOf(e.Roller) ~= D.companion then return end
    local row = D.report.rolls[#D.report.rolls]
    if row then row.natural, row.total = e.Natural, e.Total end
end)

-- The game marked the speaker "rolling" (active_roll::InProgressComponent, server and client) when
-- it made the request, and clears the mark of the roller when the roll ends - now the companion.
-- Left on the host, the stale mark stops the host's next dialogue check from opening its window.
function D.unstick()
    local e = Ext.Entity.Get(host)
    local mark = e and e.ServerRollInProgress
    local ok, alive = pcall(function() return mark.Entity:IsAlive() end)
    if mark and not (ok and alive) then
        e:RemoveComponent("ServerRollInProgress")
        if e.RollInProgress then e:RemoveComponent("RollInProgress") end
        D.report.unstuck = (D.report.unstuck or 0) + 1
    end
end
D.onGone = Ext.Entity.OnDestroy("RequestedRoll", function()
    Ext.Timer.WaitFor(500, function() if GKDemo2 == D then D.unstick() end end)
end)

-- the guard and the talk
D.talker = GK.Fixture.Talker{ dialog = "roll" }.entities[1].id
local function talk(n)
    if GKDemo2 ~= D then return end
    if GK.Fixture.Ready("Talker") then D.report.talk = GK.Cheat.Talk(nil, D.talker) return end
    if n > 0 then Ext.Timer.WaitFor(250, function() talk(n - 1) end) end
end
talk(60)

function D.stop()
    if D.onRequest then Ext.Entity.Unsubscribe(D.onRequest) D.onRequest = nil end
    if D.onGone then Ext.Entity.Unsubscribe(D.onGone) D.onGone = nil end
    D.unstick()
    if D.onBefore then Ext.Events.BeforeRoll:Unsubscribe(D.onBefore) D.onBefore = nil end
    if D.onRoll then Ext.Events.Roll:Unsubscribe(D.onRoll) D.onRoll = nil end
end
return { companion = D.companion, talker = D.talker,
         persuasion = { host = skillTotal(host, "Persuasion"), companion = skillTotal(D.companion, "Persuasion") } }
