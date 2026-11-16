-- Demonstration 3: the foretold die.
-- Two goblins fight the host (kept alive by the stage). Before a goblin's attack throws its die,
-- a notification on screen says what it is about to do and the chance the game itself gives it
-- to succeed; when the die is thrown, a second one says what the die did - and a running tally
-- sets the hits foretold (the sum of the chances) beside the hits the dice gave.
-- Tools: the event log's family `fight` (its `fight_action` row is written from the game's
-- SpellCastState some twenty ticks before the first die), GK.Rolls.Chance (the game's own chance,
-- Ext.Stats.GetSpellRollChance), the extender's Roll event (the die as thrown).
-- Run:  ./gatekit --instance <copy> exec client "return GK.Camera.Look(0, { now = true })"
--       ./gatekit --instance <copy> exec server "$(cat research/demos/foretold_die.lua)"
-- End:  ./gatekit --instance <copy> exec server "$(cat research/demos/foretold_die_end.lua)"
-- Read: exec server "return GKDemo3.report"
if GKDemo3 and GKDemo3.stop then GKDemo3.stop() end
GK.Stage.Begin()
local host = Osi.GetHostCharacter()
local D = { goblins = {}, waiting = {}, report = { foretold = {}, expected = 0, hits = 0, thrown = 0 } }
GKDemo3 = D

local function name(id) return Osi.ResolveTranslatedString(Osi.GetDisplayName(id)) or id end
local function spellName(spell)
    local ok, text = pcall(function() return Ext.Loca.GetTranslatedString(Ext.Stats.Get(spell).DisplayName) end)
    return ok and text and text ~= "" and text or spell
end
local function uuidOf(handle) local e = handle and Ext.Entity.Get(handle) return e and e.Uuid and e.Uuid.EntityUuid end
local function say(text) Osi.ShowNotification(host, text) D.report.said = D.report.said or {} table.insert(D.report.said, text) end

GK.Stage.Heal(host)   -- the ledger keeps the first heal's hit points for Clean: full health
GK.Rolls.Pin{ kind = "Initiative", n = 100 }
local x, y, z = Osi.GetPosition(host)
for n = 1, 2 do
    local g = GK.Stage.Spawn{ template = "goblin", at = { x + (n == 1 and -2 or 2), y, z + 6 }, faction = "enemy" }[1]
    GK.Stage.Immortal(true, g)
    D.goblins[#D.goblins + 1] = g
end
GK.EventLog.On("fight", { seconds = 600 })
D.seq = GK.EventLog.Since(0, { limit = 1 }).seq

-- the foretelling: each new fight_action of a goblin, with the game's chance
local function foretell()
    for _, row in ipairs(GK.EventLog.Since(D.seq, { kinds = { fight_action = true } }).rows) do
        D.seq = math.max(D.seq, row.seq)
        local p = row.payload
        local target = p.targets and p.targets[1]
        if p.who and p.who ~= host and type(target) == "string" then
            local c = GK.Rolls.Chance{ who = p.who, spell = p.spell, target = target }
            if c.ok and c.chance then
                local f = { who = p.who, spell = p.spell, target = target, chance = c.chance }
                D.waiting[p.who] = f
                table.insert(D.report.foretold, f)
                say(string.format("Foretold: %s - %s at %s - %d%% to hit", name(p.who), spellName(p.spell), name(target), math.floor(c.chance * 100 + 0.5)))
            end
        end
    end
end

-- the dice: the attack roll of a creature that was foretold
D.onRoll = Ext.Events.Roll:Subscribe(function(e)
    local who = uuidOf(e.Roller)
    local f = who and D.waiting[who]
    if not f or not tostring(e.RollType):find("Attack") then return end
    D.waiting[who] = nil
    local hit = e.Critical == "Success" or (e.Natural ~= 1 and e.DC ~= nil and e.Total >= e.DC)
    f.natural, f.total, f.dc, f.hit = e.Natural, e.Total, e.DC, hit
    D.report.thrown, D.report.expected = D.report.thrown + 1, D.report.expected + f.chance
    if hit then D.report.hits = D.report.hits + 1 end
    say(string.format("The die: %d (total %d vs %s) - %s. Hits foretold %.1f, dealt %d of %d",
        e.Natural, e.Total, tostring(e.DC), hit and "HIT" or "miss", D.report.expected, D.report.hits, D.report.thrown))
end)

-- the clock: read the log, end the host's turns, keep the fight's family on
local tick, hostRound = 0, nil
local function clock()
    if GKDemo3 ~= D then return end
    tick = tick + 1
    pcall(foretell)
    if tick % 100 == 0 then GK.EventLog.On("fight", { seconds = 600 }) end
    -- immortality stops death, not the fall: keep the host on its feet
    if tick % 5 == 0 and Ext.Entity.Get(host).Health.Hp <= 4 then GK.Stage.Heal(host) end
    local c = GK.Stage.Combat()
    if c and c.active == host and c.round ~= hostRound then
        hostRound = c.round
        Ext.Timer.WaitFor(2000, function() if GKDemo3 == D then GK.Stage.EndTurn(host) end end)
    end
    Ext.Timer.WaitFor(100, clock)
end
Ext.Timer.WaitFor(1500, function() GK.Stage.Fight(D.goblins) clock() end)

function D.stop()
    if D.onRoll then Ext.Events.Roll:Unsubscribe(D.onRoll) D.onRoll = nil end
    GK.EventLog.Off("fight")
end
return { goblins = D.goblins }
