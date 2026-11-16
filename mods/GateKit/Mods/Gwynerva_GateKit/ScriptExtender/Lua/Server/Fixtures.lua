-- GK.Fixture: small scenes staged in an empty level (GustavX:Basic_Level_A loads in about 27 s) instead
-- of loading an act: a fight, a talker, a trader, loot, a lock and a trap, built from the game's own
-- data next to the party. Server side. Staging is a cheat (it creates, teaches and arranges); what
-- an agent then does in the scene is fair through GK.Gui and GK.Act, or scripted through GK.Cheat.
--
--   GK.Fixture.Fight{ enemies = 2, provoke = true, caster = true }   a fight that really starts
--   GK.Fixture.Talker{ dialog = "greeting" | "roll" | <resource uuid> }   GK.Cheat.Talk starts it
--   GK.Fixture.Trader{ treasure = "...", gold = 150 }                 GK.Cheat.Trade opens the window
--   GK.Fixture.Loot{}        a chest with goods and a corpse to loot
--   GK.Fixture.Lock{}        a locked chest;   GK.Fixture.Trap{}   an armed mine, already spotted
--   GK.Fixture.Down{}        a party member at 0 hit points (see its rule: alone, it is game over)
--   GK.Fixture.Box{ text }   a message box (the story's own OpenMessageBox)
--   GK.Fixture.Clear()       takes away everything the fixtures made or changed, newest first
--   GK.Fixture.Made()        what is staged now
--
-- Each fixture answers { fixture, entities = { { id, role, name, at } }, howto, rule, cannot }.
-- Everything staged is written down once, as a scope_track row of the event log (GK.Stage.Track):
-- GK.Fixture.Clear undoes the fixture rows, GK.Stage.Clean / Verify see them as the scope's.

GK.Fixture = GK.Fixture or {}
local Fixture = GK.Fixture

local function try(fn, ...)
    local ok, a, b, c = pcall(fn, ...)
    if ok then return a, b, c end
    return nil
end

local function bare(id)
    return type(id) == "string" and (id:match("(%x+%-%x+%-%x+%-%x+%-%x+)$") or id) or id
end

local function host() return bare(Osi.GetHostCharacter()) end
local function pos(id)
    local x, y, z = Osi.GetPosition(id)
    return x and { x, y, z } or nil
end
local function round(p) return p and { math.floor(p[1] * 10 + 0.5) / 10, math.floor(p[2] * 10 + 0.5) / 10, math.floor(p[3] * 10 + 0.5) / 10 } end
local function nameOf(id)
    local handle = try(Osi.GetDisplayName, id)
    return handle and try(Osi.ResolveTranslatedString, handle) or nil
end

-- ---------------------------------------------------------------------------------------
-- Defaults: what the fixtures create, chosen from the game's data (the offline index, `gatekit data`).
-- Each entry says the query that chose it; any of them can be replaced (GK.Fixture.Defaults.x = ...).

Fixture.Defaults = Fixture.Defaults or {
    -- Dummies are dressed and armed by their own templates. Chosen from the index: character root
    -- templates with an Equipment set and a visual of their own, female where the choice is free:
    --   gatekit data sql "select t.name, t.uuid, eq.value, vis.value from templates t join objects o
    --     on o.key = t.uuid join attrs eq on eq.object_id = o.id and eq.attr_id = (select id from names
    --     where name = 'Equipment') left join attrs vis on vis.object_id = o.id and vis.attr_id =
    --     (select id from names where name = 'CharacterVisualResourceID') where t.type = 'character'
    --     and t.name like '%Female%' and eq.value <> ''"
    -- A melee enemy: Humans_Female_Guard_Melee (EQP_Longsword_ScaleMail_Shield: a longsword threatens).
    enemy = "07323556-2afa-432a-872c-546277124106",
    -- A caster of the party's level: Goblins_Female_Caster (EQP_Goblin_Staff, stats Goblin_Caster).
    -- (Humans_Female_FlamingFist_Caster_B, 43 hp, downed the test party's 10 hp Astarion with one
    -- Fire Bolt: too strong for a first-level party.)
    caster = "9e9000e1-c4cf-42cf-8f5c-60e4cda2bca5",
    -- The talker of the counting-house check: Humans_Female_CountingHouseGuard_Melee - the dialog's
    -- own speaker group is the counting house's guards (EQP_LOW_CountingHouseGuard_Melee).
    guard = "9e054949-8251-438e-bbf5-7371815b5bea",
    -- A townswoman for a greeting: Humans_Female_Civilian (clothes come with its visual 9bc5bfe0).
    person = "19dc6cab-aa38-4f09-b9cf-546e1c0ecb8a",
    -- A trader: Dwarves_Female_Civilian (visual 2fe71dd9); no template of the game is a bare trader -
    -- traders are placed characters - so a dressed civilian is given the trade.
    trader = "1ad176b6-d4df-49b9-b487-c0751c39e55e",
    -- Factions (gatekit data find "Evil NPC" / "Neutral NPC"): hostile to the party, and neutral.
    hostile = "64321d50-d516-b1b2-cfac-2eb773de1ff6",
    neutral = "cfb709b3-220f-9682-bcfb-6f0d8837462e",
    -- A melee weapon for a party member that holds none (Stats: WPN_Dagger; any melee weapon does).
    melee = "9ddcb3b2-14fb-48d7-9047-cd2ee34de51c",
    -- Dialogs, by their DIALOG RESOURCE id (what Osiris and DB_Dialogs take: timelines.dialog_resource,
    -- not dialogs.uuid). Chosen with a recursive query over dialog_nodes / dialog_links / dialog_flags:
    -- two speakers, a root with no flag checks, "Generic NPC Dialog".
    dialogs = {
        -- a greeting with three answers and no checks and no flags set (6 nodes)
        greeting = { resource = "f4918acb-48dc-4dca-341f-6815dc29e325", name = "TUT_LowerDeck_LaezelStairs", speaker = "person" },
        -- the root's first answer is a Persuasion check (ActiveRoll, DC 15 here) - the roll window
        roll = { resource = "d709bf80-b240-53e5-5c57-81b707c1f771", name = "LOW_NGB_CountingHouse_UseForbiddenItem", answer = 1, speaker = "guard" },
    },
    -- A trader's table that includes trader gold (awk over Stats/Generated/TreasureTable.txt: tables
    -- that pull a Gold_Trader_* subtable); the weaponsmith's has weapons, armour and gold.
    tradeTreasure = "DEN_Weaponsmith_Trade",
    partyGold = 150,
    -- A wooden chest (CONT_GEN_Chest_Wood_A) and a table to fill it with (the same trader table:
    -- goods of every kind, so loot shows several rows).
    chest = "5f089bb9-1816-43b8-8e44-f2d58804ea23",
    chestTreasure = "DEN_Weaponsmith_Trade",
    -- A pressure mine (REF_PUZ_Trap_Mine_A): templates whose IsTrap attribute is True
    --   gatekit data sql "select t.name, t.uuid from templates t join objects o on o.key=t.uuid
    --     join attrs a on a.object_id=o.id and a.attr_id=(select id from names where name='IsTrap')
    --     where a.value='True' and t.type='item'"
    trap = "b66736fc-36db-4c79-8b9d-4098787fd35f",
    -- The lock's difficulty: a DifficultyClass resource, the kind placed locks carry in
    -- LockDifficultyClassID (gatekit data sql "select a.value, count(*) from attrs a where a.attr_id =
    -- (select id from names where name = 'LockDifficultyClassID') group by a.value order by 2 desc");
    -- Act3_Easy, DC 10. A lock without one is never taken up for lockpicking (seen live: no dice window).
    lockDC = "5028066b-6ea0-4a6a-9e3e-53bee62559a7",
    -- The tools a member needs (given when it has none): thieves' tools, a trap disarm toolkit.
    thievesTools = "08851ac0-3bfa-44f3-80c6-6ab0536f0e10",   -- TOOL_GEN_ThievesTools_A_Closed_A
    disarmTools = "22c74b5e-bef2-41b1-b9ed-f4acc766d4ee",    -- TOOL_GEN_Disam_Toolkit_A
    -- Reactions for `caster = true`: the spell that grants the interrupt, the slot level it needs.
    reactions = {
        { spell = "Shout_Shield_Wizard", interrupt = "Interrupt_Shield_Wizard", slot = 1 },
        { spell = "Shout_HellishRebuke", interrupt = "Interrupt_HellishRebuke", slot = 1 },
        { spell = "Target_Counterspell", interrupt = "Interrupt_Counterspell", slot = 3 },
    },
    -- What the caster casts at the party member: an attack-roll spell (Shield can answer the hit,
    -- Counterspell the cast, Hellish Rebuke the damage).
    casterSpell = "Projectile_FireBolt",
    -- Metres from the party member: inside sight (a hostile that sees the party starts the fight).
    distance = 6,
}
local D = Fixture.Defaults

-- ---------------------------------------------------------------------------------------
-- The record of what was staged

-- What a fixture stages is a scope_track row of the event log (GK.Stage.Track with `fixture` and `op`),
-- and its undo is data in that row, not a closure: { op = "delete" | "prefs" | "weaponset" |
-- "spell" | "boost" | "gold" | "dialogs" | "status" | "turn", ... }. The log's files outlive the
-- Lua state: when it starts again, the fixture rows the previous one left open are carried into the
-- new log, so a reload of the bridge (or of Lua) between staging and GK.Fixture.Clear() loses nothing.

local function delete(id)
    -- created in this very tick: deleted on the next (GK.Stage.Unborn says why)
    if GK.Stage and GK.Stage.Unborn and GK.Stage.Unborn(id, delete) then return end
    if try(Osi.IsCharacter, id) == 1 then
        try(Osi.LeaveCombat, id)
        try(Osi.SetOnStage, id, 0)
        try(Osi.RequestDeleteTemporary, id)
    else
        try(Osi.RequestDelete, id)
    end
end

local UNDO = {
    delete = function(u) delete(u.id) end,
    prefs = function(u)
        local e = Ext.Entity.Get(u.who)
        local prefs = e and try(function() return e.InterruptPreferences.Preferences end)
        if prefs then
            prefs[u.interrupt] = u.bits ~= nil and u.bits or nil
            try(function() e:Replicate("InterruptPreferences") end)
        end
    end,
    weaponset = function(u)
        local e = Ext.Entity.Get(u.who)
        if e then try(function() e.WeaponSet.WeaponSet = u.set; e:Replicate("WeaponSet") end) end
        try(Osi.SetWeaponUnsheathed, u.who, u.drawn or 0, 1)
    end,
    spell = function(u) try(Osi.RemoveSpell, u.who, u.spell, 0) end,
    boost = function(u) try(Osi.RemoveBoosts, u.who, u.text, 0, u.cause or "GKFixture", u.who) end,
    gold = function(u) try(Osi.AddGold, u.who, -u.amount) end,
    dialogs = function(u) try(Osi.PROC_RemoveAllDialogEntriesForSpeaker, u.id) end,
    status = function(u) try(Osi.RemoveStatus, u.id, u.status, "") end,
    immortal = function(u) try(Osi.SetImmortal, u.id, 0) end,
    turn = function(u) GK.Fixture.OnTurn[u.id] = nil end,
    -- an action resource back to its earlier amount (GK.Stage.Refill, the stage's resources)
    resource = function(u)
        local e = Ext.Entity.Get(u.who)
        if not (e and GK.Stage.WriteResource(e, u.resource, u.index, u.amount, u.max)) then error("the resource of " .. tostring(u.who) .. " could not be written") end
    end,
    -- the party's inspiration back to its earlier points (GK.Rolls.SetInspiration)
    inspiration = function(u) GK.Rolls.WriteInspiration(u.points, u.max) end,
    -- Karmic Dice and the roll streams back to what they were (GK.Rolls.SetKarmic, SetStreams)
    karmic = function(u) GK.Rolls.WriteKarmic(u.on) end,
    streams = function(u) GK.Rolls.WriteStreams(u.list) end,
    rig = function(u) GK.Rolls.WriteRig(u.id) end,
    tradegen = function(u) GK.Fixture.OnTradeGenerated[u.id] = nil end,
}
Fixture.Undo = UNDO

Fixture.Scenes = Fixture.Scenes or {}

--- Runs a fixture's undo given as data (a row's payload.op): true, or false and why.
function Fixture.RunOp(op)
    local fn = UNDO[op and op.op]
    if not fn then return false, "no undo for " .. tostring(op and op.op) end
    local ok, problem = pcall(fn, op)
    if not ok then return false, tostring(problem) end
    return true
end

--- Writes one staged thing down: a scope_track row with the fixture's name and its undo as data.
--- GK.Fixture.Clear and the stage scope's Clean both may undo it; the undo runs once, whoever is first.
local function track(fixture, kind, entity, op, note)
    return GK.Stage.Track({ kind = kind, entity = entity, fixture = fixture, op = op, note = tostring(note),
                            key = "fixture:" .. GK.EventLog.Seq + 1 })
end
Fixture.Track = track

-- The fixture rows a previous Lua state of this side left open (tracked, not undone): carried into
-- this log once, as rows of their own that name the row they came from.
if not Fixture.Carried then
    Fixture.Carried = true
    local previous = GK.EventLog.Previous and GK.EventLog.Previous() or nil
    if previous then
        local open, undone = {}, {}
        for _, row in ipairs(previous) do
            local p = row.payload or {}
            if row.kind == "scope_track" and p.fixture and p.op then open[#open + 1] = row
            elseif row.kind == "scope_undo" and p.track then undone[p.track] = true end
        end
        for _, row in ipairs(open) do
            if not undone[row.seq] then
                local p = row.payload
                pcall(GK.Stage.Track, { kind = p.kind, entity = p.entity, fixture = p.fixture, op = p.op, note = p.note,
                                        carried = p.carried or row.seq, key = "fixture:" .. GK.EventLog.Seq + 1 })
            end
        end
    end
end

-- Where the n-th thing of a fixture goes: around the party member, on walkable ground.
local function spot(who, distance, angle)
    local p = pos(who)
    local x, z = p[1] + distance * math.cos(angle), p[3] + distance * math.sin(angle)
    local ok, vx, vy, vz = pcall(Osi.FindValidPosition, x, p[2], z, 4.0, who, 1)
    if ok and vx then return vx, vy, vz end
    return x, p[2], z
end

local function spawn(fixture, template, role, x, y, z)
    local id = Osi.CreateAt(template, x, y, z, 1, 1, "")
    if not id then error(("GK.Fixture.%s: the game created nothing from template %s at %.1f, %.1f, %.1f"):format(fixture, template, x, y, z), 3) end
    id = bare(id)
    if GK.Stage and GK.Stage.NoteBorn then GK.Stage.NoteBorn(id) end
    track(fixture, try(Osi.IsCharacter, id) == 1 and "spawn" or "item", id, { op = "delete", id = id }, role)
    return id
end

local function row(id, role)
    return { id = id, role = role, name = nameOf(id), at = round(pos(id)) }
end

-- Things the game does to a new object only once it exists as an entity with an inventory (a
-- treasure generated in the tick of CreateAt is lost: seen live, the trader stayed empty).
Fixture.Pending = Fixture.Pending or {}
local function whenReady(fixture, id, fn)
    Fixture.Pending[fixture] = (Fixture.Pending[fixture] or 0) + 1
    local tries = 0
    local function step()
        tries = tries + 1
        local e = Ext.Entity.Get(id)
        if (e and try(function() return e.InventoryOwner end)) or tries > 50 then
            Fixture.Pending[fixture] = math.max(0, (Fixture.Pending[fixture] or 1) - 1)
            if e then pcall(fn) end
            return
        end
        GK.Stage.AfterMs(100, step)
    end
    GK.Stage.AfterMs(100, step)
end

-- The items inside a container or character, each written down to be deleted on Clear: goods a
-- fixture generated are the fixture's even after the party took them.
local function trackContents(fixture, holder, note)
    local e = Ext.Entity.Get(holder)
    for _, inv in ipairs(try(function() return e.InventoryOwner.Inventories end) or {}) do
        for _, slot in pairs(try(function() return inv.InventoryContainer.Items end) or {}) do
            local id = try(function() return slot.Item.Uuid.EntityUuid end)
            if id then track(fixture, "item", id, { op = "delete", id = id }, note) end
        end
    end
end

-- A character a fixture made counts as ready once the party perceives it: GK.Act acts only on what
-- the party sees, and a character created a tick ago is not seen yet (GK.Cheat.Talk refused it).
local function whenSeen(fixture, id)
    Fixture.Pending[fixture] = (Fixture.Pending[fixture] or 0) + 1
    local tries = 0
    local function step()
        tries = tries + 1
        local seen = GK.See and GK.See.PartyPerceives and try(GK.See.PartyPerceives, id)
        if seen or tries > 100 then
            Fixture.Pending[fixture] = math.max(0, (Fixture.Pending[fixture] or 1) - 1)
            return
        end
        GK.Stage.AfterMs(100, step)
    end
    GK.Stage.AfterMs(100, step)
end

--- Whether a fixture has finished what it does a moment after it answered (treasure, locks, traps).
function Fixture.Ready(fixture)
    if fixture then return (Fixture.Pending[fixture] or 0) == 0 end
    for _, n in pairs(Fixture.Pending) do if n > 0 then return false end end
    return true
end

local function scene(fixture, out)
    out.fixture = fixture
    Fixture.Scenes[fixture] = out
    return out
end

-- Staging needs a quiet screen: tutorials that pause the game (combat, rolls, trade) are marked as
-- seen for the host's user - the game's own switch, which nothing has to undo.
local function quiet()
    local user = try(Osi.GetReservedUserID, host())
    if user then try(Osi.CompleteTutorialForUser, user) end
end

-- ---------------------------------------------------------------------------------------
-- Interrupt preferences ("Ask" in the reactions panel) and the weapon a member holds

local ASK, ENABLED = 1, 2

local function prefsOf(id)
    local e = Ext.Entity.Get(id)
    return e, e and try(function() return e.InterruptPreferences.Preferences end)
end

--- Sets "ask me" on a reaction of a party member (what the reactions panel's Ask does), keeping
--- the old value to put back.
local function askFor(fixture, who, interrupt)
    local e, prefs = prefsOf(who)
    if not prefs then return false, "no InterruptPreferences on " .. tostring(who) end
    local old = try(function() return prefs[interrupt] end)
    local oldBits = 0
    if old ~= nil then
        -- a bitfield reads as "InterruptInteractionTypes(Enabled,Ask)"
        local text = tostring(old)
        if text:find("Ask", 1, true) then oldBits = oldBits + ASK end
        if text:find("Enabled", 1, true) then oldBits = oldBits + ENABLED end
    end
    if oldBits == ASK + ENABLED then return true end
    prefs[interrupt] = ASK + ENABLED
    try(function() e:Replicate("InterruptPreferences") end)
    track(fixture, "other", who, { op = "prefs", who = who, interrupt = interrupt, bits = old ~= nil and oldBits or nil }, interrupt .. " set to ask")
    return true
end

local function knowsInterrupt(who, interrupt)
    local e = Ext.Entity.Get(who)
    for _, i in ipairs(try(function() return e.InterruptContainer.Interrupts end) or {}) do
        if try(function() return i.InterruptData.Interrupt end) == interrupt then return true end
    end
    return false
end

--- What stops `who` from threatening the squares around it (an opportunity attack needs a melee
--- weapon in hand), or nil. Read from the game's own state: the weapon set drawn and the threat
--- range the combat system gives a character.
function Fixture.WhyNoThreat(who)
    who = who or host()
    local e = Ext.Entity.Get(who)
    if not e then return "no such character" end
    if try(Osi.HasMeleeWeaponEquipped, who, "Any") ~= 1 then return "holds no melee weapon (a bow threatens nobody)" end
    local set = tostring(try(function() return e.WeaponSet.WeaponSet end))
    if set ~= "Melee" then return "its ranged weapons are in hand (weapon set " .. set .. ")" end
    if try(Osi.IsWeaponUnsheathed, who) ~= 1 then return "its weapon is sheathed" end
    if try(function() return e.ThreatRange end) == nil then return "it has no threat range yet (the combat system gives one when a melee weapon is equipped)" end
    if try(Osi.HasActiveStatus, who, "REACTION_USED") == 1 then return "its reaction is spent this round" end
    return nil
end

--- Puts a melee weapon in a member's hand and draws it: the weapon set switched to melee, and the
--- equipped melee weapon equipped again so the combat system gives the member a threat range
--- (in Basic_Level_A the starting gear of the test party gives none until it is re-equipped).
local function armForMelee(fixture, who)
    local e = Ext.Entity.Get(who)
    local weapon = bare(try(Osi.GetEquippedItem, who, "Melee Main Weapon"))
    if not weapon then
        weapon = bare(Osi.CreateAtObject(D.melee, who, 0, 0, "", 0))
        if weapon then
            track(fixture, "item", weapon, { op = "delete", id = weapon }, "a melee weapon for " .. tostring(who))
        end
    end
    if not weapon then return false, "no melee weapon could be found or made" end
    local oldSet = tostring(try(function() return e.WeaponSet.WeaponSet end))
    local oldDrawn = try(Osi.IsWeaponUnsheathed, who)
    if oldSet ~= "Melee" then
        try(function() e.WeaponSet.WeaponSet = "Melee"; e:Replicate("WeaponSet") end)
        track(fixture, "other", who, { op = "weaponset", who = who, set = oldSet, drawn = oldDrawn }, "weapon set " .. oldSet .. " -> Melee")
    end
    try(Osi.Unequip, who, weapon)
    GK.Stage.AfterMs(300, function()
        try(Osi.Equip, who, weapon, 1, 0, 0)
        GK.Stage.AfterMs(300, function() try(Osi.SetWeaponUnsheathed, who, 1, 1) end)
    end)
    return true
end

-- Teaches a spell (and takes it away on Clear); adds slots with a boost of the fixture's own cause.
local function teach(fixture, who, spell)
    if try(Osi.HasSpell, who, spell) == 1 then return end
    Osi.AddSpell(who, spell, 0, 1)
    track(fixture, "other", who, { op = "spell", who = who, spell = spell }, "taught " .. spell)
end

local function boost(fixture, who, text)
    Osi.AddBoosts(who, text, "GKFixture", who)
    track(fixture, "status", who, { op = "boost", who = who, text = text, cause = "GKFixture" }, "boost " .. text)
end

-- ---------------------------------------------------------------------------------------
-- Turn hooks: what a fixture wants done when a combatant's turn starts (the enemy that flees, the
-- caster that casts). Looked up when the event fires, so a reloaded file replaces them.

Fixture.OnTurn = Fixture.OnTurn or {}

if not Fixture.TurnHooked then
    Fixture.TurnHooked = pcall(Ext.Osiris.RegisterListener, "TurnStarted", 1, "after", function(character)
        local id = bare(character)
        local hook = GK.Fixture.OnTurn[id]
        if hook then
            GK.Fixture.OnTurn[id] = nil
            local ok, problem = pcall(hook, id)
            if not ok then Ext.Utils.PrintWarning("[GateKit] GK.Fixture turn hook: " .. tostring(problem)) end
        end
    end)
end

Fixture.OnTradeGenerated = Fixture.OnTradeGenerated or {}
-- The opening is seen on the engine's side (a trade asked for by script raises no RequestTrade
-- event): the trader gets CharacterTrading while its window is open.
if not Fixture.TradeHooked then
    Fixture.TradeHooked = pcall(Ext.Entity.OnCreate, "CharacterTrading", function(entity)
        local id = try(function() return entity.Uuid.EntityUuid end)
        local fn = id and GK.Fixture.OnTradeGenerated[id]
        if fn then pcall(fn) end
    end)
end

Fixture.OnDowned = Fixture.OnDowned or {}
if not Fixture.DownHooked then
    Fixture.DownHooked = pcall(Ext.Osiris.RegisterListener, "DownedChanged", 2, "after", function(character, downed)
        local fn = downed == 1 and GK.Fixture.OnDowned[bare(character)]
        if fn then pcall(fn) end
    end)
end

local function onTurn(fixture, id, fn, note)
    Fixture.OnTurn[id] = fn
    track(fixture, "other", id, { op = "turn", id = id }, note)
end

-- ---------------------------------------------------------------------------------------
-- FIGHT

--- A fight that starts by the game's own rule.
---   enemies   how many (default 1)
---   provoke   the first enemy starts in a party member's reach and flees on its turn: the member
---             gets an opportunity attack, with "ask" on, so the reaction prompt opens
---   caster    the first enemy is a caster and casts at the member on its turn; the member knows
---             Shield, Hellish Rebuke and Counterspell (slots given), all set to "ask"
---   who       the party member (default the host); distance (default 6 m)
function Fixture.Fight(o)
    o = o or {}
    local who = o.who or host()
    local n = math.max(1, o.enemies or 1)
    if GK.InDialog(who) then
        error("GK.Fixture.Fight: " .. tostring(who) .. " is in a dialogue - the game starts no fight while one is open", 2)
    end
    quiet()
    local out = { entities = {}, problems = {} }
    -- the member cannot die (its hit points stop at 1): a party of one at 0 is game over
    if not o.mortal and try(Osi.IsImmortal, who) ~= 1 then
        Osi.SetImmortal(who, 1)
        track("Fight", "status", who, { op = "immortal", id = who }, "the member cannot die")
    end
    if o.provoke then
        local ok, why = armForMelee("Fight", who)
        if not ok then error("GK.Fixture.Fight{provoke}: " .. why, 2) end
        askFor("Fight", who, "Interrupt_AttackOfOpportunity")
        if not knowsInterrupt(who, "Interrupt_AttackOfOpportunity") then
            out.problems[#out.problems + 1] = "the member has no Interrupt_AttackOfOpportunity"
        end
    end
    if o.caster then
        local slots = {}
        for _, r in ipairs(D.reactions) do
            teach("Fight", who, r.spell)
            slots[r.slot] = true
        end
        for level in pairs(slots) do boost("Fight", who, ("ActionResource(SpellSlot,2,%d)"):format(level)) end
        GK.Stage.AfterMs(200, function()
            for _, r in ipairs(D.reactions) do askFor("Fight", who, r.interrupt) end
        end)
    end
    local enemies = {}
    for i = 1, n do
        local template = (o.caster and i == 1) and (o.casterTemplate or D.caster) or (o.template or D.enemy)
        local close = o.provoke and i == 1
        local x, y, z = spot(who, close and 1.3 or (o.distance or D.distance), (i - 1) * 2 * math.pi / n)
        local id = spawn("Fight", template, close and "the enemy that will flee" or (o.caster and i == 1 and "the caster" or "enemy"), x, y, z)
        Osi.SetFaction(id, o.faction or D.hostile)
        enemies[#enemies + 1] = id
    end
    -- the game starts the fight itself when a hostile sees the party; EnterCombat is asked as well
    -- (an incapacitated or blind enemy joins nothing either way - see `rule`)
    GK.Stage.AfterMs(300, function()
        for _, id in ipairs(enemies) do
            if try(Osi.IsInCombat, id) ~= 1 then try(Osi.EnterCombat, id, who) end
        end
    end)
    if o.provoke then
        local fleer = enemies[1]
        -- the flight is ordered once the member threatens the enemy (checked, not assumed)
        local tries = 0
        local function arm()
            tries = tries + 1
            local why = Fixture.WhyNoThreat(who)
            local threatened = false
            local fe = Ext.Entity.Get(fleer)
            for _, h in ipairs(try(function() return fe.CombatIsThreatened.ThreatenedBy end) or {}) do
                if try(function() return h.Uuid.EntityUuid end) == who then threatened = true end
            end
            -- and the enemy is in the member's own fight, in its turn order (an enemy that joined a
            -- moment ago at GK.Speed 4 was not, and its turn never came)
            local guid = try(Osi.CombatGetGuidFor, who)
            local together = guid ~= nil and try(Osi.CombatGetGuidFor, fleer) == guid
            if together and GK.Stage and GK.Stage.Combat then
                together = false
                for _, r in ipairs((GK.Stage and GK.Stage.Combat and try(GK.Stage.Combat, who) or {}).order or {}) do
                    if r.uuid == fleer then together = true end
                end
            end
            if why == nil and threatened and together then
                -- COMMAND_FLEE (a FEAR status): on its turn the creature runs from the source,
                -- leaving the reach without Disengage - which provokes. A move ordered by script
                -- (CharacterMoveToPosition) does not provoke.
                Osi.ApplyStatus(fleer, "COMMAND_FLEE", 12, 1, who)
                track("Fight", "status", fleer, { op = "status", id = fleer, status = "COMMAND_FLEE" }, "COMMAND_FLEE")
                Fixture.Scenes.Fight.provokeReady = true
                return
            end
            if tries >= 40 then
                Fixture.Scenes.Fight.provokeReady = false
                Fixture.Scenes.Fight.provokeProblem = why or (not threatened and "the enemy is not inside the member's reach (no Threatened from it)")
                    or "the enemy is not in the member's fight and its turn order"
                return
            end
            GK.Stage.AfterMs(250, arm)
        end
        GK.Stage.AfterMs(800, arm)
    end
    if o.caster then
        local caster, spell = enemies[1], o.spell or D.casterSpell
        teach("Fight", caster, spell)
        onTurn("Fight", caster, function(id)
            -- on its own turn, so the cast is the caster's action and the member may react
            GK.Stage.AfterMs(300, function() try(Osi.UseSpell, id, spell, who) end)
        end, "casts " .. spell .. " at the member on its turn")
    end
    for _, id in ipairs(enemies) do out.entities[#out.entities + 1] = row(id, "enemy") end
    out.member = who
    out.rule = "A hostile creature that sees the party starts a fight; initiative orders the turns. "
        .. "An opportunity attack: a creature leaving the reach of a hostile without Disengage provokes a reaction; "
        .. "the reactor must wield a melee weapon (a bow threatens nobody) and have its reaction left. "
        .. "Shield answers an attack that hits, Counterspell a spell being cast, Hellish Rebuke damage taken - each costs the reaction and a slot."
    out.howto = (o.provoke and "End the member's turn (GK.Act.EndTurn): on its turn the fleeing enemy leaves the reach and the reaction prompt opens (GK.Gui.Resolve{reaction='react'|'decline'}). "
            or "")
        .. (o.caster and "End the member's turn: the caster casts at it and a reaction prompt (Shield / Counterspell / Hellish Rebuke) opens. " or "")
        .. "Fight with GK.Gui.UseSlot + Aim + Confirm (the scripted GK.Cheat.Attack / Cast skip rolls and costs); GK.Stage.Combat() shows the order. GK.Fixture.Clear() removes the enemies and ends the fight."
    out.cannot = "no AI grid beyond the platform: enemies that flee far stop at its edge; no story reactions (no crime, no allies called)"
    return scene("Fight", out)
end

--- Whether the provoke of the last Fight is armed: true, false (with why), or nil (still checking).
function Fixture.ProvokeReady()
    local s = Fixture.Scenes.Fight
    if not s then return nil, "no Fight staged" end
    return s.provokeReady, s.provokeProblem
end

-- ---------------------------------------------------------------------------------------
-- TALKER

--- An NPC that owns a real dialogue, the way the story gives one: a DB_Dialogs fact (what the
--- level's story does for its characters), so a request to talk - GK.Cheat.Talk - starts it.
---   dialog   "greeting" | "roll" | a dialog resource uuid (default "greeting")
function Fixture.Talker(o)
    o = o or {}
    local who = o.who or host()
    quiet()
    local choice = D.dialogs[o.dialog or "greeting"] or { resource = o.dialog, name = o.dialog }
    if try(Osi.DialogSpeakerHasLines, choice.resource, 1) == nil then
        error("GK.Fixture.Talker: the game has no dialog resource " .. tostring(choice.resource)
            .. " (a dialog is named by its resource id: gatekit data sql \"select t.dialog_resource from dialogs d join timelines t on t.dialog_id = d.id where d.name like '%NAME'\")", 2)
    end
    local x, y, z = spot(who, o.distance or 2.5, math.pi / 2)
    local npc = spawn("Talker", o.template or D[choice.speaker or "person"] or D.person, "talker", x, y, z)
    whenSeen("Talker", npc)
    Osi.SetFaction(npc, D.neutral)
    Osi.DB_Dialogs(npc, choice.resource)
    track("Talker", "other", npc, { op = "dialogs", id = npc }, "DB_Dialogs " .. tostring(choice.name))
    local out = { entities = { row(npc, "talker") }, dialog = choice.name, resource = choice.resource }
    out.rule = "Talking is the player's click on a character: the story picks the dialogue it owns (DB_Dialogs) and starts it when both speakers are free (not in combat, not in another dialogue)."
        .. (choice.answer and (" Answer " .. choice.answer .. " is a skill check: the roll window opens, d20 + modifier against the DC.") or "")
    out.howto = "when GK.Fixture.Ready('Talker') (the party sees it), GK.Cheat.Talk(nil, \"" .. npc .. "\"); then GK.Dialog() / GK.Answer(n) on the UI side"
        .. (choice.answer and ("; GK.Answer(" .. choice.answer .. ") opens the roll, GK.Gui.Resolve{roll='throw'} rolls it") or "")
    out.cannot = "no story state: a dialogue whose root lines check flags of an act falls through to nothing; voiced lines play, the camera stays the platform's"
    return scene("Talker", out)
end

-- ---------------------------------------------------------------------------------------
-- TRADER

--- An NPC that trades: SetCanTrade and a trade treasure of the game's (Fixture.Defaults.tradeTreasure),
--- generated into its inventory now, and gold for the party so both sides can pay.
function Fixture.Trader(o)
    o = o or {}
    local who = o.who or host()
    quiet()
    local x, y, z = spot(who, o.distance or 2.5, -math.pi / 2)
    local npc = spawn("Trader", o.template or D.trader, "trader", x, y, z)
    whenSeen("Trader", npc)
    Osi.SetFaction(npc, D.neutral)
    Osi.SetCanTrade(npc, 1)
    Osi.SetCustomTradeTreasure(npc, o.treasure or D.tradeTreasure)
    -- the trade window opened once shows the trader's own inventory: the table is generated into
    -- it here (a story trader gets it on TradeGenerationStarted, which an NPC of a bare template lacks)
    -- The trade window's first opening clears the trader's goods and makes new ones from its trade
    -- treasure, which for an NPC of a bare template makes nothing (seen live: 45 items before the
    -- first opening, 0 after). So the table is generated into it once that first opening has
    -- begun (CharacterTrading on the trader, a moment later) - the goods show in the open window and stay.
    local treasure, level = o.treasure or D.tradeTreasure, o.level or 2
    Fixture.OnTradeGenerated[npc] = function()
        Fixture.OnTradeGenerated[npc] = nil
        GK.Stage.AfterMs(300, function()
            Osi.GenerateTreasure(npc, treasure, level, who)
            GK.Stage.AfterMs(500, function() trackContents("Trader", npc, "a trader's good") end)
        end)
    end
    track("Trader", "other", npc, { op = "tradegen", id = npc }, "goods generated after the game's own generation")
    local gold = o.gold or D.partyGold
    if gold and gold > 0 then
        Osi.AddGold(who, gold)
        track("Trader", "other", who, { op = "gold", who = who, amount = gold }, gold .. " gold for the party")
    end
    local out = { entities = { row(npc, "trader") }, treasure = o.treasure or D.tradeTreasure }
    out.rule = "A trader sells what its treasure table holds and buys for the gold it has; prices follow the trader's attitude and the party's Charisma."
    out.howto = "GK.Cheat.Trade(nil, \"" .. npc .. "\") opens the trade window; GK.Gui.Trade moves items and gold, GK.Gui.Blocking() reads both sides"
    out.cannot = "no barter dialogue (the template has no trade node); the trade opens in barter mode straight away"
    return scene("Trader", out)
end

-- ---------------------------------------------------------------------------------------
-- LOOT, LOCK, TRAP

-- A tool a member needs, given (and taken back on Clear) when it carries none of the kind.
local function giveTool(fixture, who, template, tag)
    local e = Ext.Entity.Get(who)
    for _, inv in ipairs(try(function() return e.InventoryOwner.Inventories end) or {}) do
        for _, slot in pairs(try(function() return inv.InventoryContainer.Items end) or {}) do
            local id = try(function() return slot.Item.Uuid.EntityUuid end)
            if id and (try(Osi.GetTemplate, id) or ""):find(tag, 1, true) then return end
        end
    end
    local id = bare(Osi.CreateAtObject(template, who, 0, 0, "", 0))
    if id then
        GK.Stage.AfterMs(200, function() try(Osi.ToInventory, id, who, 1, 0, 0) end)
        track(fixture, "item", id, { op = "delete", id = id }, "a tool for the member")
    end
end

local function chest(fixture, who, angle, treasure)
    local x, y, z = spot(who, 2.2, angle)
    local id = spawn(fixture, D.chest, "chest", x, y, z)
    if treasure then
        whenReady(fixture, id, function()
            Osi.GenerateTreasure(id, treasure, 2, who)
            Fixture.Pending[fixture] = (Fixture.Pending[fixture] or 0) + 1
            GK.Stage.AfterMs(500, function()
                trackContents(fixture, id, "an item of the chest")
                Fixture.Pending[fixture] = math.max(0, Fixture.Pending[fixture] - 1)
            end)
        end)
    end
    return id
end

--- A chest with goods and a lootable corpse (an enemy that died here).
function Fixture.Loot(o)
    o = o or {}
    local who = o.who or host()
    quiet()
    local box = chest("Loot", who, math.pi, o.treasure or D.chestTreasure)
    local x, y, z = spot(who, 3.0, math.pi * 0.75)
    local body = spawn("Loot", o.template or D.enemy, "corpse", x, y, z)
    -- a death the game carries out (Osi.Die) leaves a corpse with its own gear to loot
    whenReady("Loot", body, function()
        trackContents("Loot", body, "the corpse's gear")
        Osi.Die(body, 0, "NULL_00000000-0000-0000-0000-000000000000", 0, 0)
    end)
    local out = { entities = { row(box, "chest"), row(body, "corpse") } }
    out.rule = "Any container or corpse can be looted by a character standing next to it; taking from a container nobody owns is no crime."
    out.howto = "GK.Cheat.Use(nil, \"" .. box .. "\") opens the chest, GK.Cheat.Loot(nil, \"" .. body .. "\") the corpse; GK.Gui.Resolve{loot='takeall'} takes everything"
    out.cannot = "nothing is owned here: the crime of stealing needs an owner and witnesses of a level"
    return scene("Loot", out)
end

--- A locked chest (goods inside). Lockpicking: Sleight of Hand with thieves' tools against the lock's DC.
function Fixture.Lock(o)
    o = o or {}
    local who = o.who or host()
    quiet()
    local box = chest("Lock", who, math.pi * 1.25, o.treasure or D.chestTreasure)
    whenReady("Lock", box, function()
        Osi.Lock(box, "NOKEY")
        local e = Ext.Entity.Get(box)
        try(function() e.Lock.LockDifficultyClassID = o.dc or D.lockDC; e:Replicate("Lock") end)
    end)
    giveTool("Lock", who, D.thievesTools, "ThievesTools")
    local out = { entities = { row(box, "locked chest") } }
    out.rule = "A lock opens with its key or a lockpicking check: Dexterity (Sleight of Hand) with thieves' tools against the lock's DC; a failure keeps the tools."
    out.howto = "GK.Act.Lockpick(nil, \"" .. box .. "\") (the member needs thieves' tools); the roll window opens"
    out.cannot = "no door: a door needs the level's walls and navigation to mean anything; a chest has the same lock"
    return scene("Lock", out)
end

--- An armed mine, already spotted (a found trap: its outline shows), on the walkable ground.
function Fixture.Trap(o)
    o = o or {}
    local who = o.who or host()
    quiet()
    local x, y, z = spot(who, o.distance or 3.0, math.pi * 1.5)
    local mine = spawn("Trap", o.template or D.trap, "trap", x, y, z)
    GK.Stage.AfterMs(200, function()
        try(Osi.SetTrapArmed, mine, 1)
        if o.hidden ~= true then try(Osi.SetTrapDiscovered, mine, 1) end
    end)
    giveTool("Trap", who, D.disarmTools, "Disam_Toolkit")
    local out = { entities = { row(mine, "trap") } }
    out.rule = "A trap is found with a Wisdom (Perception) check and disarmed with Dexterity (Sleight of Hand) and thieves' tools; stepping on an armed one sets it off."
    out.howto = "GK.Cheat.Disarm(nil, \"" .. mine .. "\") (thieves' tools); or walk onto it to set it off"
    out.cannot = "no hidden detection test by default (hidden = true leaves it unspotted: the party's passive Perception then decides)"
    return scene("Trap", out)
end

-- ---------------------------------------------------------------------------------------
-- DOWN and BOX

--- A party member brought to 0 hit points by damage it takes. A character at 0 hit points is
--- downed and makes death saving throws; when every party member is down the game is over. In
--- Basic_Level_A the party is one character, so the member is raised again (healed to `hp`) a
--- moment after it went down - before the game's own "party defeated" check.
function Fixture.Down(o)
    o = o or {}
    local who = o.who or host()
    local hp = try(Osi.GetHitpoints, who) or 0
    if hp <= 0 then error("GK.Fixture.Down: " .. tostring(who) .. " is already at 0 hit points", 2) end
    local alone = true
    for _, e in ipairs(Ext.Entity.GetAllEntitiesWithComponent("PartyMember")) do
        local id = try(function() return e.Uuid.EntityUuid end)
        if id and id ~= who and try(Osi.IsSummon, id) ~= 1 and try(Osi.IsDead, id) ~= 1 and (try(Osi.GetHitpoints, id) or 0) > 0 then alone = false end
    end
    try(Osi.SetImmortal, who, 0)
    Osi.ApplyDamage(who, hp + 50, "Force", "NULL_00000000-0000-0000-0000-000000000000")
    local raise = o.raise
    if raise == nil then raise = alone end
    if raise then
        -- raised in the tick the game reports it down (DownedChanged), before its own "party
        -- defeated" check: a timer was too late at GK.Speed 4 (game over)
        Fixture.OnDowned[who] = function()
            Fixture.OnDowned[who] = nil
            try(Osi.RemoveStatus, who, "DOWNED", "")
            try(Osi.SetHitpoints, who, o.hp or math.max(1, hp))
        end
    end
    return scene("Down", { entities = { row(who, "downed member") }, alone = alone, raised = raise,
        rule = "At 0 hit points a character is downed: death saving throws on its turns, any healing gets it up; the party is defeated when nobody stands.",
        howto = alone and "the member stood alone: it was raised again at once, so the game is not over" or "help it up (GK.Gui.UseSlot(\"Target_Help\"), Aim, Confirm) or heal it",
        cannot = "a party of one (the test level's) cannot stay down: that is game over" })
end

--- A message box, the story's own (Osi.OpenMessageBox); its OK closes it.
function Fixture.Box(o)
    o = type(o) == "string" and { text = o } or (o or {})
    Osi.OpenMessageBox(o.who or host(), o.text or "GateKit")
    return scene("Box", { entities = {}, rule = "A story message waits for the player's OK.",
        howto = "GK.Gui.Resolve{box='affirmative'} presses OK", cannot = "" })
end

-- ---------------------------------------------------------------------------------------
-- Taking it all away

--- Everything the fixtures made or changed is undone, newest first: spawned characters and items
--- deleted (a fight with only them in it ends), statuses, spells, boosts, gold, interrupt settings
--- and weapon sets put back, dialogue facts removed. Returns { undone, failed }.
function Fixture.Clear()
    local undone, failed = 0, {}
    local rows = GK.Stage.Open("fixture")
    for i = #rows, 1, -1 do
        local ok, problem = GK.Stage.UndoTrack(rows[i])
        if ok then undone = undone + 1 else failed[#failed + 1] = tostring(rows[i].payload.note) .. ": " .. tostring(problem) end
    end
    Fixture.Scenes = {}
    Fixture.OnTurn = {}
    Fixture.OnTradeGenerated = {}
    Fixture.OnDowned = {}
    Fixture.Pending = {}
    return { undone = undone, failed = failed }
end

--- What is staged now (the fixture rows not undone): { { fixture, kind, entity, note, exists, seq } }.
function Fixture.Made()
    local out = {}
    for _, row in ipairs(GK.Stage.Open("fixture")) do
        local p = row.payload
        out[#out + 1] = { fixture = p.fixture, kind = p.kind, entity = p.entity, note = p.note, seq = row.seq,
                          exists = p.entity and Ext.Entity.Get(p.entity) ~= nil or nil }
    end
    return out
end

return Fixture
