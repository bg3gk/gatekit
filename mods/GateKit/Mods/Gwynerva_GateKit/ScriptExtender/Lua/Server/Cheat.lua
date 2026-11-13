-- GK.Cheat: everything that bends the game's rules, in one place, under one name - for setting up
-- tests, never for an agent that plays. The honest half is GK.See (what the party perceives) and
-- GK.Act (what a player can do); stand/situation.py and the bg3_agent_* MCP tools use only those.
--
-- The functions themselves live where they always did (GK.Stage, GK.Teleport, GK.Give ...); this
-- table only names them, so a reader can tell at a glance which calls are cheats, and so a run can
-- check its own log: GK.Cheat.Used() lists the cheats called since the last GK.Cheat.Reset().
--
--   GK.Cheat.List()        { name = "what it does" } for every cheat
--   GK.Cheat.<Name>(...)   calls it (and records the call)
--   GK.Cheat.Used()        the cheats called so far: { { name, t } }
--
-- See also GK.See's scope "all" (omniscient reading): not a change to the world, but knowledge a
-- player does not have; it is refused in agent runs (GK_AGENT_STRICT=1 in stand/situation.py).

GK.Cheat = GK.Cheat or {}
local Cheat = GK.Cheat
Cheat.Log = Cheat.Log or {}

local S = function(name) return function(...) return GK.Stage[name](...) end end

-- name -> { function, description }
local CATALOG = {
    Teleport = { function(...) return GK.Teleport(...) end, "moves a character at once, no walking" },
    Visit = { S("Visit"), "puts a character next to an object at once" },
    Bring = { S("Bring"), "moves an object to a character at once" },
    UseFrom = { S("UseItem"), "teleports next to an item and uses it" },
    Spawn = { S("Spawn"), "creates characters or items" },
    Give = { function(...) return GK.Give(...) end, "adds items to an inventory" },
    Kill = { S("Kill"), "kills a character" },
    Slay = { S("Slay"), "kills every enemy around" },
    Damage = { S("Damage"), "deals damage without an attack" },
    Heal = { S("Heal"), "restores hit points" },
    Restore = { S("Restore"), "restores the party at once" },
    Resurrect = { S("Resurrect"), "raises a dead character" },
    Immortal = { S("Immortal"), "makes a character unkillable" },
    Protect = { S("Protect"), "makes the party unkillable" },
    Guard = { S("Guard"), "keeps the party alive whatever happens" },
    Status = { S("Status"), "applies a status" },
    Unstatus = { S("Unstatus"), "removes a status" },
    Teach = { S("Teach"), "adds a spell" },
    Lock = { S("Lock"), "locks or unlocks without a key" },
    Faction = { S("Faction"), "changes a character's side" },
    Fight = { S("Fight"), "starts a fight with chosen enemies" },
    EndCombat = { S("EndCombat"), "ends a fight without a winner" },
    EndTurnOf = { S("EndTurn"), "ends anybody's turn, enemies' too" },
    Cast = { S("Cast"), "casts without range, roll or cost checks" },
    Talk = { S("Talk"), "starts a chosen dialogue from anywhere" },
    StartDialog = { function(...) return GK.StartDialog(...) end, "starts a chosen dialogue from anywhere" },
    OnStage = { S("OnStage"), "brings in a character the story keeps away" },
    Level = { S("Level"), "moves the party to another level" },
    Camp = { S("Camp"), "sends the party to camp from anywhere" },
    LongRest = { S("LongRest"), "a long rest asked for by the story" },
    Unblock = { S("Unblock"), "raises the dead, leaves forced modes" },
    Experience = { function(amount, who) return Osi.AddExplorationExperience(who or Osi.GetHostCharacter(), amount) end,
                   "adds experience" },
}

for name, entry in pairs(CATALOG) do
    Cheat[name] = function(...)
        Cheat.Log[#Cheat.Log + 1] = { name = name, t = Ext.Utils.MonotonicTime() / 1000 }
        return entry[1](...)
    end
end

function Cheat.List()
    local out = {}
    for name, entry in pairs(CATALOG) do out[name] = entry[2] end
    return out
end

function Cheat.Used() return Cheat.Log end

function Cheat.Reset()
    Cheat.Log = {}
    return true
end

return Cheat
