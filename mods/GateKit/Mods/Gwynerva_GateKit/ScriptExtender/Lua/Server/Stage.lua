-- GK.Stage: putting a situation together from code - who stands where, wearing what, on whose
-- side; fights turn by turn; statuses, damage, death; rests and the camp; another level.
-- Server side. Everything takes and returns plain values (uuids, numbers, tables), like the
-- rest of the GateKit API; `who` is a character uuid, nil means the host's character.
--
-- Whatever a scene creates is remembered, so GK.Stage.Reset() takes the game back to where it
-- was and scenes can be staged one after another in the same game.
--
--   local goblins = GK.Stage.Spawn{ template = "goblin", count = 3, faction = "enemy" }
--   GK.Stage.Fight(goblins)                      -- the fight starts
--   GK.Stage.Combat()                            -- whose turn, the order, the round
--   GK.Stage.EndTurn()                           -- the active one ends its turn
--   GK.Stage.Reset()
--
-- This file can also be loaded into a running game as it is (gatekit exec server @Stage.lua).

local GK = GK or Mods.GateKit.GK

local Stage = GK.Stage or {}
GK.Stage = Stage

-- What the current scene has created or changed (kept across reloads of this file).
Stage.Scene = Stage.Scene or { created = {}, factions = {}, statuses = {} }

-- A character created by Osi.CreateAt and asked to be deleted (Osi.RequestDeleteTemporary) in
-- the same server tick closes the game a moment later, with no error and no dump; a tick later
-- the same deletion is fine. The stage and the fixtures note the tick each object was created in
-- (Stage.Born) and put off a deletion of one created in this tick to the next (Stage.Later).
Stage.Born = Stage.Born or {}
Stage.Later = Stage.Later or {}

local function tickNow() return GK.EventLog and GK.EventLog.Ticks or 0 end

--- Notes that the stage or a fixture created `id` in this tick.
function Stage.NoteBorn(id)
    if type(id) == "string" then Stage.Born[id:sub(-36)] = tickNow() end
end

--- Whether `id` was created in this tick; then `fn(id)` runs on a later tick instead, and true.
function Stage.Unborn(id, fn)
    local born = type(id) == "string" and Stage.Born[id:sub(-36)]
    if not born or born < tickNow() then return false end
    Stage.Later[#Stage.Later + 1] = { tick = tickNow(), fn = function() fn(id) end }
    return true
end

--- Runs `fn` once `n` server ticks have passed (at least one), from the stage's queue on the tick.
--- The staging's own waits between two calls of the game are counted so, in ticks, the same at
--- any game speed. GAP: each is a wait where the game's event of the first call's end is not found
--- yet (research/open-items.md).
function Stage.AfterTicks(n, fn)
    Stage.Later[#Stage.Later + 1] = { tick = tickNow() + math.max(1, n) - 1, fn = fn }
end
local function afterMs(ms, fn) Stage.AfterTicks(GK.LimitTicks(ms), fn) end
-- ms of a running game, counted in the server's ticks (for the staging's own limits)
local function gameMs() return tickNow() * GK.TickMs end
Stage.AfterMs = afterMs

if not Stage.LaterSubscribed then
    Stage.LaterSubscribed = true
    Ext.Events.Tick:Subscribe(function()
        if #Stage.Later == 0 then return end
        local due, keep = {}, {}
        for _, later in ipairs(Stage.Later) do
            if later.tick < tickNow() then due[#due + 1] = later.fn else keep[#keep + 1] = later end
        end
        Stage.Later = keep
        for _, fn in ipairs(due) do pcall(fn) end
    end)
end

local function try(fn, ...)
    local ok, a, b, c = pcall(fn, ...)
    if ok then return a, b, c end
    return nil
end

-- The stage ledger (GK.Stage.Begin / Clean / Verify, at the end of this file) is rows of the event
-- log: every change a GK.Stage function makes to something that existed before is a scope_track
-- row, written once per key with how to undo it - the first note of a key wins, so the undo puts
-- back what was there before the scope - and its undo is a scope_undo row. Stage.Tracks holds the
-- track rows not undone yet (the rows themselves, any scope; GK.Fixture's among them); the undo of a
-- row is a function here (Stage.Ledger.undo, by seq) or, for a fixture's row, data in its payload.
Stage.Ledger = Stage.Ledger or { undo = {}, keys = {}, spots = {} }
Stage.Tracks = Stage.Tracks or {}

local function note(key, what, undo, kind, entity, extra)
    local ledger = Stage.Ledger
    if ledger.keys[key] then return end
    local payload = { key = key, what = what, kind = kind or "other", entity = entity }
    for k, v in pairs(extra or {}) do payload[k] = v end
    local row = GK.EventLog.Append("scope_track", payload)
    ledger.keys[key] = row.seq
    ledger.undo[row.seq] = undo
    Stage.Tracks[#Stage.Tracks + 1] = row
    return row
end

--- The track rows not undone yet, oldest first (a view: the rows of the event log). filter: nil
--- (all), "scope" (the open stage scope's), "fixture" (GK.Fixture's).
function Stage.Open(filter)
    local out = {}
    for _, row in ipairs(Stage.Tracks) do
        if filter == nil or (filter == "scope" and row.scope == GK.EventLog.Scope) or (filter == "fixture" and row.payload.fixture) then
            out[#out + 1] = row
        end
    end
    return out
end

--- Undoes one track row once, whoever asks first (GK.Stage.Clean, GK.Fixture.Clear): its function, or
--- its payload's data undo (`op`, GK.Fixture.Undo). Writes a scope_undo row; answers ok, why.
function Stage.UndoTrack(row)
    local index
    for i, r in ipairs(Stage.Tracks) do if r == row then index = i break end end
    if not index then return true end
    table.remove(Stage.Tracks, index)
    local fn = Stage.Ledger.undo[row.seq]
    Stage.Ledger.undo[row.seq] = nil
    local ok, done, why
    if fn then
        ok, done, why = pcall(fn)
    elseif row.payload.op and GK.Fixture and GK.Fixture.RunOp then
        ok, done, why = pcall(GK.Fixture.RunOp, row.payload.op)
    else
        ok, done = true, true
    end
    local fine = ok and done ~= false
    local because = not ok and tostring(done) or (done == false and why) or nil
    GK.EventLog.Append("scope_undo", { track = row.seq, ok = fine, why = because })
    return fine, because, ok and type(why) == "string" and why or nil
end

--- The ledger's public hook, for staging code outside this file (GK.Fixture and a test's own):
---   GK.Stage.Track{ kind = "spawn"|"status"|"item"|"surface"|"other", entity = uuid?, undo = fn?, note = "..." }
--- GK.Stage.Clean() calls `undo` under pcall (newest first; it returns true, or false and why),
--- lists a failure under notUndone, and Verify counts a tracked spawn or item that is still there.
--- A spawn or item without `undo` is removed (GK.Stage.Remove). spec.fixture and spec.op are a
--- fixture's: its name, and its undo as data (written into the row, so it outlives a reload).
--- Answers the scope_track row.
function Stage.Track(spec)
    if type(spec) ~= "table" then error("GK.Stage.Track{ kind, entity, undo, note }: a table, got " .. tostring(spec), 2) end
    local ledger = Stage.Ledger
    ledger.serial = (ledger.serial or 0) + 1
    local kind, entity, undo = spec.kind or "other", spec.entity, spec.undo
    if undo == nil and spec.op == nil and entity and (kind == "spawn" or kind == "item") then
        undo = function()
            if Ext.Entity.Get(entity) then Stage.Remove(entity) end
            return true
        end
    end
    if type(undo) ~= "function" and spec.op == nil and not entity then
        error("GK.Stage.Track: `undo` (a function), `op` (a fixture's undo) or `entity` (a spawn or item) is needed", 2)
    end
    local row = note(spec.key or ("track:" .. ledger.serial), spec.note or (kind .. " " .. tostring(entity or "")),
        undo, kind, entity, { fixture = spec.fixture, op = spec.op, note = spec.note, carried = spec.carried })
    -- a boost the ledger added, by "who|cause|text": Verify tells it from one added behind its back
    local op = spec.op
    if row and type(op) == "table" and op.op == "boost" and type(op.text) == "string" then
        ledger.boosts = ledger.boosts or {}
        for part in op.text:gmatch("[^;]+") do ledger.boosts[tostring(op.who) .. "|" .. (op.cause or "GKFixture") .. "|" .. part] = row.seq end
    end
    return row
end

-- A position the stage used (a spawn, a surface): Clean clears surfaces around it.
local function spot(at)
    if type(at) == "table" and at[1] then
        local spots = Stage.Ledger.spots
        if #spots < 64 then spots[#spots + 1] = { at[1], at[2], at[3] } end
    end
end

-- Where an object stood, and in whose bag it was, before the stage moved it.
local function noteMoved(id)
    if type(id) ~= "string" then return end
    local x, y, z = try(Osi.GetPosition, id)
    if not x then return end
    local owner = try(Osi.GetInventoryOwner, id)
    note("moved:" .. id, "moved " .. id, function()
        if Ext.Entity.Get(id) == nil then return false, "gone: " .. id end
        if owner then
            if try(Osi.GetInventoryOwner, id) ~= owner then try(Osi.ToInventory, id, owner, 1, 0, 0) end
        else
            try(Osi.TeleportToPosition, id, x, y, z, "", 0, 0, 0, 0, 1)
        end
        return true
    end)
end

local function host(who)
    return who or Osi.GetHostCharacter()
end

local function uuidOf(entity)
    return entity and entity.Uuid and entity.Uuid.EntityUuid or nil
end

local function displayName(id)
    local entity = Ext.Entity.Get(id)
    local name = entity and entity.DisplayName and entity.DisplayName.Name
    return name and try(function() return name:Get() end) or nil
end

-- Short names for things a scene usually needs. Root templates of the base game; a name that
-- is not here is taken as a root template (name_uuid or uuid) as it is.
Stage.Templates = {
    -- enemies
    goblin = "2db928f2-f1c5-4b5a-8751-168f9f292249",          -- Goblins_Male_Melee
    goblin_ranger = "bdf828ab-4335-4626-b159-312b67397011",   -- Goblins_Male_Ranger
    goblin_caster = "01fa8d64-f63e-4bb8-9ee4-cba84dad3781",   -- Goblins_Male_Caster
    goblin_boss = "b0f372f0-f74f-4c00-84e6-34ba09117578",     -- Goblins_Male_Boss
    kobold = "dfdcfbaa-ed9b-48ba-8302-927c5f5af12d",          -- Kobolds_Melee
    gnoll = "9955e7bc-fe8d-4545-923e-680e071347fb",           -- BASE_Gnolls
    zombie = "a42a8493-b701-4d71-bdc8-0857e718c247",          -- BASE_Undead_Zombie
    skeleton = "531d498b-2ad0-4ebc-b272-e445faf3e38c",        -- BASE_Undead_Skeleton
    -- bystanders
    human = "26287685-17a9-443d-ab87-9d00f9fa7b3d",           -- BASE_Humans
    elf = "0bbd332f-747c-406c-b397-d2f607a49900",             -- BASE_Elves
    dwarf = "29e00087-abef-48e5-a427-b71594257f58",           -- BASE_Dwarves
    tiefling = "67432cf5-52a4-4ca9-bba6-d6103869cc44",        -- BASE_Tieflings
    -- weapons
    longsword = "f344c6b2-64bc-4c6d-a66d-5d3966191210",
    greatsword = "dfc51f62-6a05-4bd4-ae92-d1a8a2e3d5a7",
    shortbow = "d28a5c6d-b569-40b7-abd5-4b6f325c98ce",
    longbow = "27943fda-2491-4e67-a740-559d02dc08fa",
    dagger = "9ddcb3b2-14fb-48d7-9047-cd2ee34de51c",
    quarterstaff = "44f1ec46-4976-41ac-9697-313083bbc3c1",
    mace = "01a26e77-c4bc-4237-ae71-59c634723a95",
    crossbow = "58f57bdc-1d89-4b60-aedc-11f36e270205",
    shield = "84912ccc-b15c-4d6e-b69e-154f3fa2ebbe",
    torch = "50c43f27-a12e-412c-88f0-56e15eba692a",
    -- armour
    leather = "02ae5d88-8044-43df-8363-02a2900776db",
    chainmail = "b973e3cb-c18d-4514-ba01-ebcabbf26968",
    halfplate = "34a71679-0ab5-4fd4-bba8-d6ad341ee490",
    plate = "95b4e2c4-759a-4d33-a2d1-4e0ee6ed4f29",
    helmet = "67a87879-533e-4f9b-8274-f5bd37748ace",
    -- consumables and props
    potion = "d47006e9-8a51-453d-b200-9e0d42e9bbab",          -- healing potion
    scroll_fireball = "79d2bb95-53fc-4e41-a004-5e1b83db8de7",
    scroll_revivify = "c1c3e4fb-d68c-4e10-afdc-d4550238d50e",
    chest = "5f089bb9-1816-43b8-8e44-f2d58804ea23",           -- CONT_GEN_Chest_Wood_A
    thieves_tools = "08851ac0-3bfa-44f3-80c6-6ab0536f0e10",
}

-- Factions by what they are to the player. SetFaction wants the uuid alone.
Stage.Factions = {
    enemy = "64321d50-d516-b1b2-cfac-2eb773de1ff6",    -- Evil NPC: hostile to the party
    neutral = "cfb709b3-220f-9682-bcfb-6f0d8837462e",  -- Neutral NPC
    ally = "b37bfd4c-baed-08f4-9866-290f8bb39e62",     -- Good
}

local function template(name)
    return Stage.Templates[name] or name
end

local function faction(name)
    return Stage.Factions[name] or name
end

--- The position of an object as { x, y, z }.
function Stage.Position(who)
    local x, y, z = Osi.GetPosition(host(who))
    return x and { x, y, z } or nil
end

--- The facing of an object about the vertical axis, in degrees (0..360), read from its transform's
--- rotation quaternion; nil when it has none.
function Stage.Yaw(who)
    local q = try(function() return Ext.Entity.Get(host(who)).Transform.Transform.RotationQuat end)
    if not q then return nil end
    local x, y, z, w = q[1], q[2], q[3], q[4]
    local yaw = math.deg(math.atan(2 * (w * y + x * z), 1 - 2 * (y * y + x * x)))
    return (yaw + 360) % 360
end

-- Where the n-th of `count` objects goes: on a circle of `radius` around a point, so that a
-- group does not pile up on one tile.
local function ring(center, n, count, radius)
    local angle = (n - 1) / math.max(count, 1) * 2 * math.pi
    return center[1] + radius * math.cos(angle), center[2], center[3] + radius * math.sin(angle)
end

--- Creates characters or items. `spec` is a template name, or a table:
---   template   short name from GK.Stage.Templates, or a root template
---   count      how many (default 1)
---   at         { x, y, z }; or `near` = uuid (default: the host), with `distance` (default 3)
---   faction    "enemy" | "neutral" | "ally" | a faction uuid (characters only)
---   equipment  list of template names to put on (weapons, armour)
---   items      list of template names to put into the inventory
---   level      character level to set (Osi.SetLevel)
---   rotation   its facing: the yaw in degrees, or { pitch, yaw, roll } (Osi.ToTransform); read
---              back with GK.Stage.Yaw
--- Returns the list of new uuids. Equipment is given at once and put on a moment later,
--- when the items exist: GK.Stage.Ready(uuids) tells when.
function Stage.Spawn(spec)
    if type(spec) == "string" then spec = { template = spec } end
    local count = spec.count or 1
    local center = spec.at and assert(GK.Pos(spec.at)) or Stage.Position(spec.near)
    local distance = spec.at and (count > 1 and 1.5 or 0) or (spec.distance or 3)
    spot(center)
    local made = {}
    for n = 1, count do
        local x, y, z = ring(center, n, count, distance)
        -- A point of the ring is at the centre's height, and on uneven ground that is inside the
        -- hill or in the air: a goblin three metres away came out under the map, dead (wish c of
        -- the day run). The game's own nearest valid position is used (within 4 m); a point it
        -- cannot place is said rather than created there.
        if not spec.exact then
            local ok, vx, vy, vz = pcall(Osi.FindValidPosition, x, y, z, 4.0, host(), 1)
            if ok and vx and vy and vz then
                x, y, z = vx, vy, vz
            elseif ok then
                error(string.format("no valid position for %s near %.1f, %.1f, %.1f (Osi.FindValidPosition found none within 4 m); "
                    .. "give `at` elsewhere, or exact = true to create it there anyway", tostring(spec.template), x, y, z), 2)
            end
        end
        -- "temporary" (the fifth argument): only such characters can be deleted again
        local id = Osi.CreateAt(template(spec.template), x, y, z, 1, 1, "")
        if id == nil then error(Stage.WhyNotCreated(spec.template, x, y, z), 2) end
        Stage.NoteBorn(id)
        made[#made + 1] = id
        Stage.Scene.created[#Stage.Scene.created + 1] = id
        Stage.Track{ kind = "spawn", entity = id, note = "spawned " .. tostring(spec.template) .. " " .. id }
        if spec.rotation ~= nil then
            -- the game's own transform call: pitch, yaw, roll in degrees (a number is the yaw)
            local r = type(spec.rotation) == "table" and spec.rotation or { 0, spec.rotation, 0 }
            Osi.ToTransform(id, x, y, z, tonumber(r[1] or r.pitch) or 0, tonumber(r[2] or r.yaw) or 0, tonumber(r[3] or r.roll) or 0)
        end
        if spec.faction then Osi.SetFaction(id, faction(spec.faction)) end
        if spec.level then try(Osi.SetLevel, id, spec.level) end
        for _, item in ipairs(spec.items or {}) do Osi.TemplateAddTo(template(item), id, 1, 0) end
        for _, item in ipairs(spec.equipment or {}) do Osi.TemplateAddTo(template(item), id, 1, 0) end
    end
    if spec.equipment and #spec.equipment > 0 then
        -- TemplateAddTo creates the items on a later tick; put them on once they are there.
        afterMs(700, function()
            for _, id in ipairs(made) do
                for _, item in ipairs(spec.equipment) do
                    local found = try(GK.Find, template(item), id)
                    if found and found[1] then Osi.Equip(id, found[1], 1, 0, 0) end
                end
            end
        end)
    end
    return made
end

--- Why Osi.CreateAt gave nothing for a template (it says nothing itself; wish 21, N10 of the
--- third run: "could not create goblin"). The short names of Stage.Templates came from another
--- build and may not exist in this one; a root template read off a live entity always does.
function Stage.WhyNotCreated(name, x, y, z)
    local resolved = template(name)
    local uuid = type(resolved) == "string" and resolved:match("(%x+%-%x+%-%x+%-%x+%-%x+)$")
    local root = uuid and try(Ext.Template.GetRootTemplate, uuid)
    local short = Stage.Templates[name] ~= nil and (" (GK.Stage.Templates." .. tostring(name) .. " = " .. tostring(resolved) .. ")") or ""
    if not root then
        return "could not create " .. tostring(name) .. short .. ": the game has no root template "
            .. tostring(uuid or resolved) .. ". Osi.GetTemplate(<a live character of the kind>) gives one that exists"
    end
    return string.format("could not create %s%s: the template exists (%s) but the game created nothing at %.1f, %.1f, %.1f "
        .. "- a place off the walkable ground is the usual reason; try `at` or `near` elsewhere",
        tostring(name), short, tostring(try(function() return root.Name end) or uuid), x or 0, y or 0, z or 0)
end

--- Whether every one of these objects exists as an entity yet (creation takes a tick or two).
function Stage.Ready(ids)
    for _, id in ipairs(ids) do
        if Ext.Entity.Get(id) == nil then return false end
    end
    return true
end

--- Gives a character things and puts them on: GK.Stage.Outfit({ "longsword", "shield" }, who).
function Stage.Outfit(equipment, who)
    local id = host(who)
    for _, item in ipairs(equipment) do Osi.TemplateAddTo(template(item), id, 1, 0) end
    afterMs(700, function()
        for _, item in ipairs(equipment) do
            local found = try(GK.Find, template(item), id)
            if found and found[1] then Osi.Equip(id, found[1], 1, 0, 0) end
        end
    end)
    return true
end

--- Changes whose side a character is on; GK.Stage.Reset() puts the old faction back.
function Stage.Faction(name, who)
    local id = host(who)
    if Stage.Scene.factions[id] == nil then
        local current = Osi.GetFaction(id)
        Stage.Scene.factions[id] = current and current:match("(%x+%-%x+%-%x+%-%x+%-%x+)$") or false
    end
    Osi.SetFaction(id, faction(name))
    return Osi.GetFaction(id)
end

--- Marks the tutorials as seen for the host's user, so that no tutorial window (which pauses
--- the game: "Combat", "Turn-based mode" ...) interrupts a staged scene.
function Stage.Quiet()
    local user = Osi.GetReservedUserID(Osi.GetHostCharacter())
    if user == nil then return false end  -- no user for a moment (a level or a save is loading)
    Osi.CompleteTutorialForUser(user)
    return Osi.WasTutorialCompletedForUser(user) == 1
end

--- What state the game is in, for a scene that seems stuck:
---   { gameState, level, hostDead, inCombat, inDialog }
--- gameState "Paused" with nothing else explaining it usually means a modal window on the
--- client (game over, a tutorial); GK.Stage.Blocking() on the UI side names it.
function Stage.State()
    local id = Osi.GetHostCharacter()
    return {
        gameState = tostring(Ext.Utils.GetGameState()),
        level = Stage.CurrentLevel(),
        hostDead = Osi.IsDead(id) == 1,
        inCombat = Osi.IsInCombat(id) == 1,
        inDialog = GK.InDialog(id),  -- nil: it cannot be read (GK.InDialog says why)
    }
end

-- ---------------------------------------------------------------------------------------
-- Fights

--- The party's characters (uuids).
--- The party's characters (uuids). Summoned creatures carry PartyMember too, and a runner's
--- lockpick went to a summoned dryad: they are left out unless `withSummons` is true.
function Stage.Party(withSummons)
    local out = {}
    for _, entity in ipairs(Ext.Entity.GetAllEntitiesWithComponent("PartyMember")) do
        local id = uuidOf(entity)
        if id and (withSummons or try(Osi.IsSummon, id) ~= 1) then out[#out + 1] = id end
    end
    return out
end

--- Makes the party unkillable (or mortal again). A scene that kills the last party member
--- ends in the "game over" screen, which pauses the server until a save is loaded - so
--- GK.Stage.Fight protects the party first; GK.Stage.Reset() lifts the protection.
function Stage.Protect(on)
    for _, id in ipairs(Stage.Party(true)) do Osi.SetImmortal(id, on == false and 0 or 1) end
    Stage.Scene.protected = on ~= false
    return true
end

--- Starts a fight between `enemies` (uuids) and a character (default: the host).
--- Enemies that are not hostile yet are moved to the enemy faction first. The party is made
--- unkillable unless `mortal` is true.
function Stage.Fight(enemies, who, mortal)
    if type(enemies) == "string" then enemies = { enemies } end
    if type(enemies) ~= "table" or #enemies == 0 then
        error("GK.Stage.Fight needs `enemies`: a uuid or a list of uuids (GK.Stage.Spawn gives them), got " .. tostring(enemies), 2)
    end
    local id = host(who)
    -- The game does not start a fight with a dialogue open and says nothing (N8 of the third
    -- run: the level's intro narrator); GK.Act refuses the same way.
    local talking, unread = GK.InDialog(id)
    if talking then
        error("in a dialogue: the game starts no fight while one is open - answer it first "
            .. "(GK.Answer(n) on the UI side, or gatekit unblock); nothing was done", 2)
    end
    if not mortal then Stage.Protect(true) end
    for _, enemy in ipairs(enemies) do
        if Osi.IsEnemy(enemy, id) ~= 1 then Stage.Faction("enemy", enemy) end
    end
    -- The game ignores EnterCombat for a character it has not finished setting up (seen with
    -- enemies spawned a moment ago, most of all right after a save was loaded), and says
    -- nothing. So the call is repeated every half second until every enemy is in the fight,
    -- for at most fifteen seconds.
    local attempts = 0
    local function engage()
        attempts = attempts + 1
        local waiting = false
        for _, enemy in ipairs(enemies) do
            if Ext.Entity.Get(enemy) ~= nil and Osi.IsDead(enemy) ~= 1 and Osi.IsInCombat(enemy) ~= 1 then
                Osi.EnterCombat(enemy, id)
                waiting = true
            end
        end
        if waiting and attempts < 30 then afterMs(500, engage) end
    end
    engage()
    return { enemies = #enemies, inCombat = Osi.IsInCombat(id) == 1, dialogueNotChecked = unread,
             note = "EnterCombat is repeated for up to 15 s until every enemy is in; GK.Stage.State().inCombat says when it has" }
end

local function combatOf(id)
    local entity = Ext.Entity.Get(id)
    local participant = entity and entity.CombatParticipant
    local combat = participant and participant.CombatHandle
    if combat and combat.TurnOrder then return combat end
    return nil
end

--- The fight a character is in (default: the host), or nil:
---   { guid, round, active = uuid of whose turn it is,
---     order = { { uuid, name, initiative, team, player, hp, maxHp, acted, dead } ... } }
function Stage.Combat(who)
    local id = host(who)
    local combat = combatOf(id)
    if combat == nil then return nil end
    local order = combat.TurnOrder
    local out = { guid = Osi.CombatGetGuidFor(id), round = order.Round, order = {} }
    for _, group in ipairs(order.CurrentGroups) do
        for _, member in ipairs(group.Members) do
            local entity = member.Entity
            local uuid = uuidOf(entity)
            -- Whose turn it is: the game marks it on the participant (Osi.CombatGetActiveEntity
            -- gave nothing in a fight started from code).
            if entity.TurnBased and entity.TurnBased.IsActiveCombatTurn then out.active = uuid end
            out.order[#out.order + 1] = {
                uuid = uuid, name = uuid and displayName(uuid), initiative = member.Initiative,
                team = tostring(group.Team), player = group.IsPlayer,
                hp = entity.Health and entity.Health.Hp, maxHp = entity.Health and entity.Health.MaxHp,
                acted = entity.TurnBased and entity.TurnBased.HadTurnInCombat or false,
                dead = uuid and Osi.IsDead(uuid) == 1 or false,
            }
        end
    end
    return out
end

--- Ends the turn of a character; default: whoever's turn it is in the host's fight.
function Stage.EndTurn(who)
    local id = who
    if id == nil then
        local combat = Stage.Combat()
        id = combat and combat.active or Osi.GetHostCharacter()
    end
    Osi.EndTurn(id)
    return id
end

--- A digest of the logic's state of some creatures at one moment, for a differential check
--- (`gatekit logic-diff`: the same scenario run twice must leave the same digests): for each id,
--- in the order given, { who, at = position rounded to 0.1 m, hp, maxHp, dead, statuses = sorted
--- status ids, equipped = sorted templates of what it wears, combat = { initiative, acted } }.
--- With `moment` (a name of the scenario's own), the digest is also written to the event log as
--- a logic_digest row. W3 round 4 of the start-zone night.
function Stage.Digest(ids, moment)
    if type(ids) == "string" then ids = { ids } end
    local out = {}
    for _, id in ipairs(ids or {}) do
        local entity = Ext.Entity.Get(id)
        local row = { who = id }
        if entity then
            local x, y, z = try(Osi.GetPosition, id)
            local function r(v) return v and math.floor(v * 10 + 0.5) / 10 or nil end
            row.at = x and { r(x), r(y), r(z) } or nil
            row.hp = try(function() return entity.Health.Hp end)
            row.maxHp = try(function() return entity.Health.MaxHp end)
            row.dead = try(Osi.IsDead, id) == 1
            local statuses = {}
            for _, statusId in pairs(try(function() return entity.StatusContainer.Statuses end) or {}) do
                statuses[#statuses + 1] = tostring(statusId)
            end
            table.sort(statuses)
            row.statuses = statuses
            local equipped = {}
            for _, slot in ipairs({ "Helmet", "Breast", "Cloak", "Gloves", "Boots", "Amulet", "Ring", "Ring2",
                "Melee Main Weapon", "Melee Offhand Weapon", "Ranged Main Weapon", "Ranged Offhand Weapon" }) do
                local item = try(Osi.GetEquippedItem, id, slot)
                if item then equipped[#equipped + 1] = slot .. "=" .. tostring(try(Osi.GetTemplate, item)) end
            end
            table.sort(equipped)
            row.equipped = equipped
            local turn = try(function() return entity.TurnBased end)
            if turn then row.combat = { acted = try(function() return turn.HadTurnInCombat end) or false } end
        else
            row.gone = true
        end
        out[#out + 1] = row
    end
    if moment then GK.EventLog.Append("logic_digest", { moment = moment, who = out }) end
    return out
end

--- Ends the fight a character is in (default: the host's) without anybody dying.
--- Asks the game to end the fight `who` (default the host) is in. N54 of the third run: it answered
--- true and the fight went on - Osi.EndCombat is a request the game may not carry out (enemies
--- still hostile and in sight start it again). So the answer says it was asked, and whether the
--- character is still in a fight a moment later is for GK.See / GK.Character to tell.
function Stage.EndCombat(who)
    local id = host(who)
    local guid = Osi.CombatGetGuidFor(id)
    if guid == nil then return false end
    Osi.EndCombat(guid)
    return { asked = true, combat = guid,
             note = "asked, not done: read GK.Character(who).inCombat after a moment - hostile enemies in sight start a fight again" }
end

--- Turn-based mode outside a fight, on or off, for a player character.
function Stage.TurnBased(on, who)
    local id = host(who)
    local was = try(Osi.IsInForceTurnBasedMode, id)
    if was ~= nil then
        note("turnbased:" .. id, "turn-based mode of " .. id, function()
            if Ext.Entity.Get(id) then try(Osi.ForceTurnBasedMode, id, was) end
            return true
        end)
    end
    Osi.ForceTurnBasedMode(id, on and 1 or 0)
    return true
end

-- ---------------------------------------------------------------------------------------
-- Actions

--- A character casts a spell on a target (uuid) or at a position ({ x, y, z } or { x =, y =, z = }).
--- The spell is a stats name ("Target_MainHandAttack", "Projectile_FireBolt", "Shout_Dash").
--- The character must know the spell; GK.Stage.Teach(spell, who) adds it.
-- A character's uuid (the game says so), for telling arguments in the other order apart.
local function isCharacter(x) return type(x) == "string" and try(Osi.IsCharacter, x) == 1 end
-- The kind of a stats entry ("SpellData", "StatusData" ...), or nil when the game has none.
local function statKind(name)
    local stat = type(name) == "string" and try(Ext.Stats.Get, name)
    return stat and try(function() return stat.ModifierList end) or nil
end

--- Any character casts `spell` on `target` (a uuid or a position), no rules checked: GK.Stage.Cast(spell,
--- target, who), the caster LAST - unlike GK.Cheat.Cast(who, spell, target), which puts the caster first and
--- acts for party members only. The order of GK.Act is understood here too (a caster is told apart by
--- being a character). Use it for a spawned or enemy creature; GK.Cheat.Cast for the party (scripted),
--- GK.Gui.UseSlot + Aim + Confirm for the party as a player.
function Stage.Cast(spell, target, who)
    -- N35 of the third run: GK.Act.* take who first. Cast(who, spell, target) - a character, then
    -- a spell - can only mean that, and is understood.
    if isCharacter(spell) and statKind(target) == "SpellData" then spell, target, who = target, who, spell end
    local id = host(who)
    if type(spell) ~= "string" then
        error("GK.Stage.Cast(spell, target, who): spell is a stats name such as Projectile_FireBolt, got " .. tostring(spell), 2)
    end
    local kind = statKind(spell)
    if kind == nil then error("GK.Stage.Cast: no spell " .. spell .. " in the game's stats; nothing was cast", 2) end
    if kind ~= "SpellData" then error("GK.Stage.Cast: " .. spell .. " is " .. kind .. ", not a spell; nothing was cast", 2) end
    -- A spell taught a moment ago is not in the book yet, and a cast then did nothing while the
    -- call said true (a runner's wish of the day run): the cast waits until the book has it.
    local known = try(Osi.HasSpell, id, spell) == 1
    local taught = Stage.Taught and Stage.Taught[id .. "|" .. spell]
    if not known and taught and gameMs() - taught < 5000 then
        local tries = 0
        local function again()
            tries = tries + 1
            if try(Osi.HasSpell, id, spell) == 1 or tries >= 12 then
                Stage.Taught[id .. "|" .. spell] = nil   -- cast now, whatever the book says
                pcall(Stage.Cast, spell, target, id)
            else
                afterMs(250, again)
            end
        end
        afterMs(250, again)
        return { asked = true, deferred = "taught a moment ago and not in the spell book yet: cast as soon as it is (within 3 s)" }
    end
    -- N39: the engine's own complaint ("String expected for argument 2, got table") is turned into
    -- what it means: this spell does not take that kind of target.
    local ok, problem
    if type(target) == "table" then
        local at, why = GK.Pos(target)
        if not at then error("GK.Stage.Cast(spell, target, who): " .. why, 2) end
        ok, problem = pcall(Osi.UseSpellAtPosition, id, spell, at[1], at[2], at[3], 1)
        if not ok then
            error("GK.Stage.Cast: the game will not cast " .. spell .. " at a position (" .. tostring(problem)
                .. "): it takes a character or an item - pass its uuid as the target; nothing was cast", 2)
        end
    elseif target == nil or type(target) == "string" then
        ok, problem = pcall(Osi.UseSpell, id, spell, target or id)
        if not ok then
            error("GK.Stage.Cast: the game will not cast " .. spell .. " on " .. tostring(target or id) .. " ("
                .. tostring(problem) .. "); a spell cast at the ground takes { x, y, z }; nothing was cast", 2)
        end
    else
        error("GK.Stage.Cast(spell, target, who): target is a uuid or a position { x, y, z }, got " .. tostring(target), 2)
    end
    -- N57/N58 of the third run: an enemy "cast" answered true and nothing happened. Osiris takes the
    -- request and may not act on it; the answer says what is known, not that it was cast.
    return { asked = true, known = known,
             note = (known and "" or "the caster does not have it in its spell book (Osi.HasSpell 0): the game may cast nothing - GK.Stage.Teach first; ")
                 .. "asked, not done: the cast shows in the event log as a game_cast row (GK.GameLog) or in its effect" }
end

--- Adds a spell to a character. A name that is not a spell in the game's stats is an error
--- (wish 36: a spell that does not exist answered true); `new` says whether it was not known
--- before (wish 34).
function Stage.Teach(spell, who)
    local id = host(who)
    local stat = type(spell) == "string" and try(Ext.Stats.Get, spell)
    if not stat then
        error("no spell " .. tostring(spell) .. " in the game's stats (a stats name such as Projectile_FireBolt); "
            .. "nothing was taught", 2)
    end
    local kind = try(function() return stat.ModifierList end)
    if kind and kind ~= "SpellData" then error(spell .. " is " .. kind .. ", not a spell; nothing was taught", 2) end
    local knew = try(Osi.HasSpell, id, spell) == 1
    if not knew then
        note("spell:" .. id .. "|" .. spell, "taught " .. spell .. " to " .. id, function()
            if Ext.Entity.Get(id) then try(Osi.RemoveSpell, id, spell, 0) end
            return true
        end)
    end
    Osi.AddSpell(id, spell, 0, 1)
    Stage.Taught = Stage.Taught or {}
    Stage.Taught[id .. "|" .. spell] = gameMs()
    return { spell = spell, who = id, new = not knew }
end

--- Uses an item (uuid, or a template name of something in the inventory): drinks, reads, opens.
function Stage.Use(item, who)
    local id = host(who)
    if Ext.Entity.Get(item) == nil or try(Osi.GetTemplate, item) == nil then
        local found = GK.Find(template(item), id)
        if #found == 0 then
            Osi.TemplateAddTo(template(item), id, 1, 0)
            error("no such item yet; it was given just now - call again in a moment")
        end
        item = found[1]
    end
    Osi.Use(id, item, "")
    return item
end

--- Applies a status ("BURNING", "PRONE", "SLEEPING" ...) for a number of turns (-1: forever).
--- Written who-first - Status(who, status[, turns]), the order of GK.Act.* (N35 of the third run,
--- where it answered true and did nothing) - it is understood; a character in the status's place
--- that cannot be read either way is refused with the signature.
local function statusArgs(name, status, who, turns)
    if type(who) == "number" and turns == nil then who, turns = nil, who end
    if isCharacter(status) and type(who) == "string" and not isCharacter(who) then status, who = who, status end
    if type(turns) == "string" and type(who) == "number" then who, turns = turns, who end
    if type(status) ~= "string" or isCharacter(status) then
        error("GK.Stage." .. name .. "(status, who, turns): status is a status name such as BURNING, who a character "
            .. "uuid (default the host); got " .. tostring(status) .. ", " .. tostring(who) .. "; nothing was done", 3)
    end
    if who ~= nil and not isCharacter(who) and try(Osi.IsItem, who) ~= 1 then
        error("GK.Stage." .. name .. "(status, who, turns): who is a character or item uuid, got " .. tostring(who)
            .. "; nothing was done", 3)
    end
    return status, who, turns
end

function Stage.Status(status, who, turns)
    status, who, turns = statusArgs("Status", status, who, turns)
    local id = host(who)
    -- a status the object already had is not taken off by a clean: it was there before
    local had = try(Osi.HasActiveStatus, id, status) == 1
    Osi.ApplyStatus(id, status, (turns or -1) < 0 and -1.0 or turns * 6.0, 1, id)
    if not had then
        Stage.Track{ kind = "status", entity = id, note = status .. " on " .. id, key = "status:" .. id .. "|" .. status,
                     undo = function() if Ext.Entity.Get(id) then try(Osi.RemoveStatus, id, status) end return true end }
    end
    return true
end

function Stage.Unstatus(status, who)
    status, who = statusArgs("Unstatus", status, who)
    local id = host(who)
    if try(Osi.HasActiveStatus, id, status) == 1 then
        note("unstatus:" .. id .. "|" .. status, "took " .. status .. " off " .. id, function()
            if Ext.Entity.Get(id) == nil then return false, "gone: " .. id end
            try(Osi.ApplyStatus, id, status, -1.0, 1, id)
            return true, "put " .. status .. " back on " .. id .. " for good (its remaining time is not known)"
        end)
    end
    Osi.RemoveStatus(id, status)
    return true
end

--- The statuses on a character: { { id, turns } }.
function Stage.Statuses(who)
    local entity = Ext.Entity.Get(host(who))
    local out = {}
    local container = entity and entity.StatusContainer
    if container then
        for _, id in pairs(container.Statuses) do out[#out + 1] = tostring(id) end
    end
    table.sort(out)
    return out
end

--- Deals damage of a type ("Slashing", "Fire" ...; default "Bludgeoning"): Damage(amount, who, kind).
--- Written the other way round - Damage(who, amount), as wish 22 did - it is understood as well;
--- anything else is refused with the signature instead of the engine's "String expected".
--- Answers what the game made of it, from its Health: { who, asked, kind, hpBefore, hpAfter,
--- dealt, row, hit, note? } - observed (GK.Observe): `row` is the seq of the game's own game_hit
--- row of the hit and `hit` the damage it says.
-- Whether downing `id` would leave no party member standing: the game then shows its game-over
-- box, which pauses the server and stayed after healing - only a reset or a load took it away
-- (N34 of the third run). nil when `id` is not a party member, or another one stands, or the
-- party is protected (GK.Stage.Protect).
local function lastStanding(id)
    local isMember, others = false, 0
    for _, m in ipairs(Stage.Party()) do
        if m == id then isMember = true
        elseif try(Osi.IsDead, m) ~= 1 then others = others + 1 end
    end
    if not isMember or others > 0 or Stage.Scene.protected then return nil end
    return "the last party member standing: downing it brings the game-over box, which pauses the game and "
        .. "stays after healing (a reset to the checkpoint - bg3_reset - or a load takes it away). GK.Stage.Protect() first, "
        .. "or pass true as the last argument to do it anyway; nothing was done"
end

-- The hit points and life of an object before the stage changed them.
local function hpOf(id)
    return try(function() return Ext.Entity.Get(id).Health.Hp end)
end

-- The hit points a clean asked the game for, by object: Osi.SetHitpoints takes effect a tick
-- later, so what was asked is what the rest of the same clean goes by.
Stage.HpAsked = Stage.HpAsked or {}

-- Asks the game for `hp` hit points on `id`, unless it holds them or a clean asked for them already;
-- answers the line that says it, or nil when nothing was asked.
local function putHp(id, hp)
    local now = hpOf(id)
    if not hp or hp <= 0 or now == hp or Stage.HpAsked[id] == hp then return nil end
    try(Osi.SetHitpoints, id, hp, "Guaranteed")
    Stage.HpAsked[id] = hp
    return string.format("hit points of %s set back to %s (were %s)", id, tostring(hp), tostring(now))
end

local function noteHp(id)
    if type(id) ~= "string" then return end
    local hp = hpOf(id)
    local dead = try(Osi.IsDead, id) == 1
    note("hp:" .. id, "hit points of " .. id, function()
        if Ext.Entity.Get(id) == nil then return false, "gone: " .. id end
        local raised = not dead and try(Osi.IsDead, id) == 1
        if raised then try(Osi.Resurrect, id, "", 0) end
        local line = putHp(id, hp)
        return true, (raised and ("raised " .. id .. (line and "; " or "")) or "") .. (line or (raised and "" or ("hit points of " .. id .. " were as before")))
    end)
end

-- What a change of hit points came to, read from the game's Health component: who, and the hit
-- points before and after.
local function hpAnswer(id, before, extra)
    local after = hpOf(id)
    local answer = { who = id, hpBefore = before, hpAfter = after }
    for k, v in pairs(extra or {}) do answer[k] = v end
    return answer, after
end

local function bareId(id)
    return type(id) == "string" and (id:match("(%x+%-%x+%-%x+%-%x+%-%x+)$") or id) or id
end

--- Refused when it could down the last party member standing, unless `anyway` is true.
function Stage.Damage(amount, who, kind, anyway)
    if type(amount) == "string" and type(who) == "number" then amount, who = who, amount end
    if type(amount) ~= "number" then
        error("GK.Stage.Damage(amount, who, kind): amount is a number of hit points, who a character uuid "
            .. "(default the host), kind a damage type (default Bludgeoning); got " .. tostring(amount), 2)
    end
    local id = host(who)
    local hp = hpOf(id)
    if hp == nil then error("GK.Stage.Damage: " .. tostring(id) .. " has no hit points (no Health component); nothing was done", 2) end
    if not anyway and amount >= hp then
        local why = lastStanding(id)
        if why then error("GK.Stage.Damage: " .. tostring(id) .. " is " .. why, 2) end
    end
    noteHp(id)
    kind = kind or "Bludgeoning"
    local since = GK.EventLog.Seq
    Osi.ApplyDamage(id, amount, kind, "")
    -- the game applies it at once: its Health says what it came to; its own game_hit row of the hit
    -- follows (the hit's one-frame event), and the answer waits for it
    local answer, after = hpAnswer(id, hp, { asked = amount, kind = kind })
    answer.dealt = after and hp - after or nil
    if answer.dealt == 0 then
        answer.note = "the game took no hit points: a resistance, an immunity, temporary hit points or immortality absorbed it"
    end
    local target = bareId(id)
    return GK.Observe(answer, function(_, final)
        for _, row in ipairs(GK.EventLog.Since(since, { kinds = { game_hit = true } }).rows) do
            if bareId(row.payload.who) == target then
                answer.row, answer.hit = row.seq, row.payload.damage
                answer.hpAfter = hpOf(id) or answer.hpAfter
                answer.dealt = answer.hpAfter and hp - answer.hpAfter or answer.dealt
                return answer
            end
        end
        if final then
            answer.note = (answer.note and answer.note .. "; " or "") .. "the game wrote no game_hit row of it"
            return answer
        end
    end)
end

--- Sets hit points (default: to the maximum). Answers { who, asked, hpBefore, hpAfter, note? },
--- observed (GK.Observe): the game sets them a tick later, and `hpAfter` is what its Health then holds.
function Stage.Heal(who, hp)
    -- Heal(hp, who), the order of Damage(amount, who), is understood (N35)
    if type(who) == "number" and (hp == nil or type(hp) == "string") then who, hp = hp, who end
    local id = host(who)
    local entity = Ext.Entity.Get(id)
    local before = hpOf(id)
    if before == nil then error("GK.Stage.Heal: " .. tostring(id) .. " has no hit points (no Health component); nothing was done", 2) end
    noteHp(id)
    local asked = hp or entity.Health.MaxHp or 1
    Osi.SetHitpoints(id, asked, "Guaranteed")
    return GK.Observe({ who = id, asked = asked, hpBefore = before }, function(_, final)
        local now = hpOf(id)
        if now == asked or final then
            local answer = hpAnswer(id, before, { asked = asked })
            if now ~= asked then answer.note = "the game holds " .. tostring(now) .. " hit points, not the " .. tostring(asked) .. " asked" end
            return answer
        end
    end)
end

function Stage.Kill(who, anyway)
    if not anyway then
        local why = lastStanding(host(who))
        if why then error("GK.Stage.Kill: " .. tostring(host(who)) .. " is " .. why, 2) end
    end
    noteHp(host(who))
    Osi.Die(host(who), 0, "NULL_00000000-0000-0000-0000-000000000000", 0, 1, 0)
    return true
end

function Stage.Resurrect(who)
    noteHp(host(who))
    Osi.Resurrect(host(who), "", 0)
    return true
end

function Stage.Immortal(on, who)
    local id = host(who)
    local was = try(Osi.IsImmortal, id)
    if was ~= nil then
        note("immortal:" .. id, "immortality of " .. id, function()
            if Ext.Entity.Get(id) then try(Osi.SetImmortal, id, was) end
            return true
        end)
    end
    Osi.SetImmortal(id, on and 1 or 0)
    return true
end

--- Turns a character into the shape of another template; GK.Stage.Untransform puts it back.
function Stage.Transform(shape, who)
    local id = host(who)
    note("transform:" .. id, "transformed " .. id, function()
        if Ext.Entity.Get(id) then try(Osi.RemoveTransforms, id) end
        return true
    end)
    Osi.Transform(id, template(shape), "296bcfb3-9dab-4a93-8ab1-f1c53c6674c9")
    return true
end

function Stage.Untransform(who)
    Osi.RemoveTransforms(host(who))
    return true
end

--- A surface on the ground: GK.Stage.Surface("SurfaceFire", 3) around the host.
function Stage.Surface(kind, radius, at, turns)
    if at then
        local why
        at, why = GK.Pos(at)
        if not at then error("GK.Stage.Surface(kind, radius, at, turns): " .. why, 2) end
    end
    at = at or Stage.Position()
    spot(at)
    local x, y, z, r = at[1], at[2], at[3], (radius or 3) + 1
    Stage.Track{ kind = "surface", note = tostring(kind) .. " surface", undo = function()
        for _, layer in ipairs({ "Ground", "Cloud" }) do try(Osi.RemoveSurfaceLayerAtPosition, x, y, z, layer, r) end
        return true
    end }
    Osi.CreateSurfaceAtPosition(at[1], at[2], at[3], kind, radius or 3, (turns or 2) * 6.0)
    return true
end

--- Locks (or, with `locked` false, unlocks) an item without a key. Answers { asked, locked } -
--- `locked` as Osi.IsLocked reads it in the same tick - and, when the two differ, a note: the game
--- applies a lock on a later tick (N29 of the third run: the same-tick reading said unlocked, one
--- tick later IsLocked read 1), so read Osi.IsLocked again in a moment before deciding.
function Stage.Lock(item, locked)
    local want = locked ~= false
    if Ext.Entity.Get(item) == nil then error("no entity " .. tostring(item) .. "; nothing was done", 2) end
    Stage.NoteLock(item)
    -- "NOKEY": with an empty key the game left a real chest unlocked (N15 of the third run,
    -- CONT_MF_Chest_A_000 in WLD_Main_A); with "NOKEY" it locked it.
    if want then Osi.Lock(item, "NOKEY") else Osi.Unlock(item, Osi.GetHostCharacter()) end
    local now = try(Osi.IsLocked, item) == 1
    if now == want then return { asked = want, locked = now } end
    return { asked = want, locked = now,
             note = "not yet: the game applies it on a later tick - read Osi.IsLocked again in a moment" }
end

-- ---------------------------------------------------------------------------------------
-- Rests, the camp, other levels

-- A short rest is a player command: GK.Command("ShortRest") on the UI side.

--- Asks for a long rest as the player would (the camp, the night, the morning).
function Stage.LongRest()
    note("longrest", "a long rest", function() return false, "a long rest (the night, the camp's story) cannot be taken back" end)
    Osi.RequestLongRest(Osi.GetHostCharacter(), 1)
    return true
end

--- Restores the party as a long rest does, at once and where it stands.
function Stage.Restore(who)
    Osi.RestoreParty(host(who))
    return true
end

function Stage.Camp(go)
    note("camp", "the party sent to camp", function() return false, "the party was sent to camp: a level change, not taken back" end)
    if go == false then
        Osi.PROC_Camp_ForcePlayersToCamp()  -- placeholder: leaving is done by the UI command
    else
        Osi.PROC_Camp_ForcePlayersToCamp()
    end
    return true
end

--- Moves the party to another level of the loaded module ("WLD_Main_A", "SCL_Main_A" ...).
function Stage.Level(name)
    note("level", "the party moved to " .. tostring(name), function() return false, "the party was moved to another level (" .. tostring(name) .. ")" end)
    Osi.TeleportPartiesToLevelWithMovie(name, "", "")
    return true
end

--- The level the host is in.
function Stage.CurrentLevel()
    local entity = Ext.Entity.Get(Osi.GetHostCharacter())
    return entity and entity.Level and entity.Level.LevelName or try(Osi.GetRegion, Osi.GetHostCharacter())
end

-- ---------------------------------------------------------------------------------------
-- Scouting: what the level itself offers for a scene

local function has(entity, component)
    local ok, value = pcall(function() return entity[component] end)
    return ok and value ~= nil
end

local function distanceTo(id, from)
    return try(Osi.GetDistanceTo, from, id)
end

--- What is on the host's level, taken from the game's own entities:
---   GK.Stage.Scout()                      a tally: how many characters, enemies, doors ...
---   GK.Stage.Scout{ kind = "enemies" }    the list; kinds: "characters", "enemies" (alive,
---                                         hostile to the host), "offstage" (waiting for the
---                                         story to bring them in), "dead", "containers",
---                                         "doors", "locked", "traders"
---   options: radius (metres from the host), limit (default 60), name (substring of the
---   template or display name)
--- Rows: { uuid, name, template, distance, x, y, z, hp, onStage, enemy, dead, locked }.
function Stage.Scout(options)
    options = options or {}
    local id = Osi.GetHostCharacter()
    local level = Stage.CurrentLevel()
    local tally = { characters = 0, enemies = 0, offstage = 0, dead = 0, containers = 0, doors = 0, locked = 0,
                    traders = 0 }
    local rows = {}
    local function consider(entity, isCharacter)
        if not (entity.Level and entity.Level.LevelName == level) then return end
        local uuid = uuidOf(entity)
        if uuid == nil or uuid == id then return end
        local kinds = {}
        local row = { uuid = uuid }
        if isCharacter then
            kinds.characters = true
            row.dead = Osi.IsDead(uuid) == 1
            row.onStage = Osi.IsOnStage(uuid) == 1
            row.enemy = Osi.IsEnemy(uuid, id) == 1
            if row.dead then kinds.dead = true
            elseif not row.onStage then kinds.offstage = true
            elseif row.enemy then kinds.enemies = true end
            -- Trader alone: CanTrade is carried by characters who do not trade, and "traders" listed
            -- every character (N5 of the third run); GetAllEntitiesWithComponent("Trader") is the 19 real ones.
            if has(entity, "Trader") then kinds.traders = true end
            row.hp = entity.Health and entity.Health.Hp
            row.template = try(function() return entity.ServerCharacter.Template.Name end)
        else
            if Osi.IsContainer(uuid) == 1 then kinds.containers = true end
            if has(entity, "Door") then kinds.doors = true end
            if Osi.IsLocked(uuid) == 1 then kinds.locked = true; row.locked = true end
            if next(kinds) == nil then return end
            row.template = try(function() return entity.ServerItem.Template.Name end)
        end
        for kind in pairs(kinds) do tally[kind] = tally[kind] + 1 end
        if options.kind and kinds[options.kind] then
            row.name = displayName(uuid)
            if options.name and not ((row.template or ""):find(options.name, 1, true)
                or (row.name or ""):find(options.name, 1, true)) then return end
            row.distance = distanceTo(uuid, id)
            if options.radius and (row.distance or 1e9) > options.radius then return end
            local x, y, z = Osi.GetPosition(uuid)
            row.x, row.y, row.z = x, y, z
            rows[#rows + 1] = row
        end
    end
    for _, entity in ipairs(Ext.Entity.GetAllEntitiesWithComponent("ServerCharacter")) do consider(entity, true) end
    for _, entity in ipairs(Ext.Entity.GetAllEntitiesWithComponent("ServerItem")) do consider(entity, false) end
    if not options.kind then
        tally.level = level
        return tally
    end
    table.sort(rows, function(a, b) return (a.distance or 1e9) < (b.distance or 1e9) end)
    local out = {}
    for i = 1, math.min(#rows, options.limit or 60) do out[i] = rows[i] end
    return out
end

-- The level's trigger zones by kind: the Script Extender component that marks each.
Stage.TriggerKinds = {
    event = "ServerEventTrigger",              -- the story is told when somebody walks in or out
    scene = "ServerTimelineSceneTrigger",      -- where a cutscene or dialogue is staged
    exploration = "ServerExplorationTrigger",  -- gives experience for discovering a place
    teleport = "ServerTeleportTrigger",        -- waypoints and transitions
    portal = "ServerPortalTrigger",
    region = "ServerRegionTrigger",            -- named regions of the map
    area = "ServerEocAreaTrigger",             -- general-purpose areas the story registers
    point = "ServerEocPointTrigger",           -- named points (spawn and move targets)
    start = "ServerStartTrigger",
    camp = "ServerCampRegionTrigger",
    crime = "ServerCrimeAreaTrigger",
    atmosphere = "ServerAtmosphereTrigger",
    music = "ServerMusicVolumeTrigger",
    sound = "ServerSoundVolumeTrigger",
    lighting = "ServerLightingTrigger",
    room = "ServerRoomTrigger",
    floor = "ServerFloorTrigger",
    chasm = "ServerChasmRegionTrigger",
}

--- The trigger zones of the host's level.
---   GK.Stage.Triggers()                         how many of each kind (GK.Stage.TriggerKinds)
---   GK.Stage.Triggers("event", { radius = 60 }) the list, nearest first; options: radius,
---                                               limit (default 60), name (part of the template name)
--- Rows: { uuid, name, distance, x, y, z, enterEvent, leaveEvent }. GK.Stage.Visit(uuid) puts
--- the host inside one, which is how the story's own events and scenes are set off.
function Stage.Triggers(kind, options)
    options = options or {}
    local level = Stage.CurrentLevel()
    if kind == nil then
        local tally = { level = level }
        for name, component in pairs(Stage.TriggerKinds) do
            local count = 0
            for _, entity in ipairs(try(Ext.Entity.GetAllEntitiesWithComponent, component) or {}) do
                if entity[component].Level == level then count = count + 1 end
            end
            tally[name] = count
        end
        return tally
    end
    local component = Stage.TriggerKinds[kind] or kind
    local id = Osi.GetHostCharacter()
    local rows = {}
    for _, entity in ipairs(Ext.Entity.GetAllEntitiesWithComponent(component)) do
        local trigger = entity[component]
        local uuid = uuidOf(entity)
        if uuid and trigger.Level == level then
            local row = { uuid = uuid, name = try(function() return trigger.Template.Name end) }
            if not options.name or (row.name or ""):find(options.name, 1, true) then
                row.enterEvent = try(function() return trigger.EnterEvent end)
                row.leaveEvent = try(function() return trigger.LeaveEvent end)
                local x, y, z = Osi.GetPosition(uuid)
                row.x, row.y, row.z = x, y, z
                row.distance = distanceTo(uuid, id)
                if not options.radius or (row.distance or 1e9) <= options.radius then rows[#rows + 1] = row end
            end
        end
    end
    table.sort(rows, function(a, b) return (a.distance or 1e9) < (b.distance or 1e9) end)
    local out = {}
    for i = 1, math.min(#rows, options.limit or 60) do out[i] = rows[i] end
    return out
end

--- The items of the host's level that do something when used, by what they do: the kind is
--- the type of the item's first use action ("Door", "OpenClose", "Book", "Ladder", "Consume",
--- "Teleport", "Combine", "Lockpick", "SpawnCharacter" ...).
---   GK.Stage.Usables()                          how many of each kind
---   GK.Stage.Usables("Ladder", { limit = 5 })   the list, nearest first: { uuid, name, template, distance }
--- GK.Stage.UseItem(uuid) has the host walk up to nothing: it is put next to the item and uses it.
--- Only items that carry use actions are looked at, and at most `options.scan` of them
--- (default 6000): a big level has tens of thousands of items.
function Stage.Usables(kind, options)
    options = options or {}
    local level = Stage.CurrentLevel()
    local id = Osi.GetHostCharacter()
    local tally, rows, scanned = {}, {}, 0
    for _, entity in ipairs(Ext.Entity.GetAllEntitiesWithComponent("UseAction")) do
        scanned = scanned + 1
        if scanned > (options.scan or 6000) then break end
        if entity.Level and entity.Level.LevelName == level then
            local actions = entity.UseAction.UseActions
            local first = actions and actions[1]
            if first then
                local name = tostring(Ext.Types.GetObjectType(first)):gsub("^.*::", ""):gsub("ActionData$", ""):gsub("Action$", "")
                tally[name] = (tally[name] or 0) + 1
                if kind == name then
                    local uuid = uuidOf(entity)
                    if uuid then
                        rows[#rows + 1] = { uuid = uuid, template = try(function() return entity.ServerItem.Template.Name end),
                                            distance = distanceTo(uuid, id) }
                    end
                end
            end
        end
    end
    if kind == nil then return tally end
    table.sort(rows, function(a, b) return (a.distance or 1e9) < (b.distance or 1e9) end)
    local out = {}
    for i = 1, math.min(#rows, options.limit or 20) do
        out[i] = rows[i]
        out[i].name = displayName(rows[i].uuid)
    end
    return out
end

--- A character (default: the host) is put next to an item and uses it: opens the door or the
--- chest, reads the book, climbs the ladder, drinks, pulls the lever.
function Stage.UseItem(item, who)
    local id = host(who)
    noteMoved(id)
    noteMoved(item)
    Osi.TeleportTo(id, item, "", 0, 0, 0, 0, 1)
    Osi.Use(id, item, "")
    return true
end

--- Other things done to an item, by name: "pickup", "drop", "throw" (at `target`, or a few
--- metres away), "attack", "equip", "unequip", "lock", "unlock", "destroy", "move" (two metres aside).
function Stage.ItemAction(action, item, target, who)
    local id = host(who)
    noteMoved(item)
    if action == "attack" then noteMoved(id) end
    if action == "lock" or action == "unlock" then Stage.NoteLock(item) end
    if action == "destroy" then
        note("destroyed:" .. tostring(item), "destroyed " .. tostring(item),
            function()
                if not Stage.ExistedBefore(item) then return true end   -- made by the stage: it goes anyway
                return false, "destroyed " .. tostring(item) .. ": a destroyed object is not brought back"
            end)
    end
    if action == "pickup" then
        Osi.ToInventory(item, id, 1, 1, 0)
    elseif action == "drop" then
        Osi.Drop(item)
    elseif action == "throw" then
        if target then
            Osi.UseSpell(id, "Throw_Throw", target, item)
        else
            -- throwing needs somebody to throw at (the spell call takes a target, not a point with
            -- an item): without one the item is tossed a few metres aside
            local x, y, z = Osi.GetPosition(id)
            Osi.ItemMoveToPosition(item, x + 5, y, z, 8.0, 4.0, "", 0)
        end
    elseif action == "attack" then
        Osi.TeleportTo(id, item, "", 0, 0, 0, 0, 1)
        Osi.UseSpell(id, "Target_MainHandAttack", item)
    elseif action == "equip" then
        -- Osi.Equip ignores an item that is not worn or wielded: said rather than answered true
        if try(function() return Ext.Entity.Get(item).Equipable end) == nil then
            error("equip: " .. tostring(item) .. " is not worn or wielded (no Equipable component); Osi.Equip ignores it", 2)
        end
        Osi.ToInventory(item, id, 1, 0, 0)
        afterMs(500, function() try(Osi.Equip, id, item, 1, 0, 0) end)
    elseif action == "unequip" then
        Osi.Unequip(id, item)
    elseif action == "lock" then
        Osi.Lock(item, "NOKEY")  -- as GK.Stage.Lock: an empty key locks nothing
    elseif action == "unlock" then
        Osi.Unlock(item, id)
    elseif action == "destroy" then
        Osi.Die(item, 0, id, 0, 1, 0)
    elseif action == "move" then
        local x, y, z = Osi.GetPosition(item)
        Osi.ItemMoveToPosition(item, x + 2, y, z, 5.0, 2.0, "", 0)
    else
        error("no such item action: " .. tostring(action))
    end
    -- D6 of the day run: "equip" answered true and equipped nothing. The answer is what the game
    -- shows: a table (truthy, as `true` was) whose ok says whether the effect was seen.
    local checks = {
        pickup = function() return try(Osi.GetInventoryOwner, item) == id, "in the inventory" end,
        drop = function() return try(Osi.GetInventoryOwner, item) == nil, "on the ground" end,
        equip = function() return try(Osi.IsEquipped, item) == 1, "equipped" end,
        unequip = function() return try(Osi.IsEquipped, item) ~= 1, "not equipped" end,
        lock = function() return try(Osi.IsLocked, item) == 1, "locked" end,
        unlock = function() return try(Osi.IsLocked, item) ~= 1, "unlocked" end,
        destroy = function() return try(Osi.IsDead, item) == 1 or Ext.Entity.Get(item) == nil, "destroyed" end,
    }
    local check = checks[action]
    if not check then return { ok = true, did = action, note = "the effect of " .. action .. " is not checked here" } end
    return GK.Observe({ ok = true, did = action }, function(_, final)
        local done, what = check()
        if done then return { ok = true, did = action, saw = what } end
        if final then return { ok = false, did = action, saw = "nothing changed: not " .. what } end
    end, action == "equip" and 3000 or 2000)
end

--- A short fight that plays itself: the host fights `enemies`, and every turn that starts is
--- ended at once by the game itself (no waiting from outside), until `turns` turns have
--- passed (default 6) - then the fight is ended. GK.Stage.SkirmishState() tells how far it is:
--- { running, turns, started } - poll it or wait for `running == false`.
function Stage.Skirmish(enemies, turns, who)
    if (type(enemies) ~= "table" or #enemies == 0) and type(enemies) ~= "string" then
        error("GK.Stage.Skirmish needs `enemies`: a uuid or a list of uuids (GK.Stage.Spawn gives them), got " .. tostring(enemies), 2)
    end
    local info = { running = true, turns = 0, limit = turns or 6, started = gameMs(), who = who }
    Stage.SkirmishInfo = info
    Stage.Fight(enemies, who)
    -- The fight is watched, not listened to: the Osiris event TurnStarted did not reach a
    -- listener registered after the story was loaded in 12 fights of 31 (tour of 2026-11-13),
    -- and such a fight hung until the time limit. Ten times a second: whose turn is it? A turn
    -- not seen before is counted and ended; one that stays is ended again every half second.
    local function watch()
        if Stage.SkirmishInfo ~= info or not info.running then return end
        local now = gameMs()
        local ok, combat = pcall(Stage.Combat, info.who)
        if ok and combat and combat.active then
            local turn = combat.active .. ":" .. tostring(combat.round)
            if turn ~= info.turn then
                info.turn, info.turns, info.ended = turn, info.turns + 1, 0
                if info.turns >= info.limit then
                    info.running = false
                    try(Stage.EndCombat, info.who)
                    return
                end
            end
            if now - (info.ended or 0) >= 500 then
                info.ended = now
                try(Osi.EndTurn, combat.active)
            end
        elseif info.turns > 0 then
            info.running = false  -- the fight is over by itself (everybody on one side is down)
            return
        elseif now - info.started > 12000 then
            info.running = false  -- a fight that never starts must not hang the caller
            info.neverStarted = true
            return
        end
        afterMs(100, watch)
    end
    watch()
    return true
end

function Stage.SkirmishState()
    return Stage.SkirmishInfo or { running = false, turns = 0 }
end

--- Kills every living enemy of the host within `radius` metres (default 30; with `inFight`
--- only those in the host's fight). Returns how many. For getting past a fight that is in
--- the way of what a scene is after.
-- Lock picking and trap disarming as the player asks for them: one element appended to the
-- engine system's own request queue (what the server's network handler does for the player's
-- "Pick lock" / "Disarm"; bg3.exe+0x306B930). The engine then walks the character there, asks the
-- story for permission, rolls and animates. Osiris has no call for either. Needs the GateKit build
-- of Script Extender (the author's LockpickingSystem sat 8 bytes low; DisarmSystem is ours).
-- Checked 2026-11-13: a locked reliquary on the Nautiloid picked with Astarion's thieves' tools.

-- A tool of the character: the first item whose template name contains `pattern`.
--- The first item in this character's inventory whose template name contains one of `tags`.
local function toolOf(id, ...)
    local tags = { ... }
    local entity = Ext.Entity.Get(id)
    local ok, items = pcall(function() return entity.InventoryOwner.PrimaryInventory.InventoryContainer.Items end)
    if not ok or not items then return nil end
    for _, tag in ipairs(tags) do
        for _, slot in pairs(items) do
            local item = slot.Item
            local name = ""
            pcall(function() name = item.ServerItem.Template.Name end)
            if name:find(tag, 1, true) then return item end
        end
    end
end

--- Picks a lock: GK.Stage.PickLock(item, who, tool). The tool defaults to the character's thieves'
--- tools (the game's client checks that the character owns one; the server does not).
function Stage.PickLock(item, who, tool)
    local id = host(who)
    local toolEntity = tool and Ext.Entity.Get(tool) or toolOf(id, "ThievesTools")
    if not toolEntity then return false, "no thieves' tools" end
    local system = Ext.System.ServerLockpicking
    local queue = system.PickLockRequests
    queue[#queue + 1] = { Player = Ext.Entity.Get(id), Target = Ext.Entity.Get(item), Tool = toolEntity }
    -- Queued is not picked (N16 of the third run: true, and the lock stayed): the game rolls, the
    -- client's dice window waits for its clicks (GK.RollNext), and only a success unlocks.
    return { queued = true, picked = false, locked = try(Osi.IsLocked, item) == 1,
             note = "the request is queued: the roll is the client's (GK.RollNext); read Osi.IsLocked afterwards - "
                 .. "GK.Act.Lockpick does all of it and says how it ended" }
end

--- Disarms a trap: GK.Stage.Disarm(trap, who, tool). The game takes the request only for an armed,
--- discovered trap that can be disarmed (the same checks are made here). Not yet seen live.
function Stage.Disarm(trap, who, tool)
    local id = host(who)
    local trapEntity = Ext.Entity.Get(trap)
    if not trapEntity then return false, "no such trap" end
    -- TOOL_GEN_Disam_Toolkit_A: the game's own spelling, typo and all, is what finds the kit.
    local toolEntity = tool and Ext.Entity.Get(tool) or toolOf(id, "Disam_Toolkit", "Disarm_Toolkit", "TrapDisarm")
    if not toolEntity then return false, "no trap disarm toolkit" end
    local system = Ext.System.ServerTrapDisarm
    if not system then return false, "this Script Extender build has no DisarmSystem" end
    local queue = system.DisarmRequests
    queue[#queue + 1] = { Character = Ext.Entity.Get(id), Tool = toolEntity, Trap = trapEntity, Force = false }
    -- as PickLock: queued, not disarmed
    return { queued = true, disarmed = false,
             note = "the request is queued: the roll is the client's (GK.RollNext); GK.Cheat.Disarm does all of it and says how it ended" }
end

function Stage.Slay(radius, inFight)
    local id = Osi.GetHostCharacter()
    local killed = 0
    for _, row in ipairs(Stage.Scout{ kind = "enemies", radius = radius or 30, limit = 500 }) do
        if not inFight or Osi.IsInCombat(row.uuid) == 1 then
            Osi.Die(row.uuid, 0, id, 0, 1, 0)
            killed = killed + 1
        end
    end
    return killed
end

--- Keeps the party alive whatever happens (or stops doing so): every party member is made
--- unkillable, and should one die anyway (a script lifted the protection, a fall, an instant
--- kill) it is raised at once with full hit points - before the "game over" window. Stays on
--- through GK.Stage.Reset(); survives until the Lua state is reset.
function Stage.Guard(on)
    local was = Stage.Guarding == true
    note("guard", "the party guard", function() Stage.Guarding = was; return true end)
    Stage.Guarding = on ~= false
    if Stage.Guarding and not Stage.GuardInstalled then
        Stage.GuardInstalled = true
        local function rescue(id)
            if not Stage.Guarding then return end
            for _, member in ipairs(Stage.Party(true)) do
                if member:sub(-36) == tostring(id):sub(-36) then
                    Osi.Resurrect(member, "", 0)
                    Osi.SetImmortal(member, 1)
                    afterMs(300, function() try(Stage.Heal, member) end)
                end
            end
        end
        Ext.Osiris.RegisterListener("Dying", 1, "before", rescue)
        Ext.Osiris.RegisterListener("Died", 1, "after", rescue)
        local function renew()
            if Stage.Guarding then
                for _, member in ipairs(Stage.Party(true)) do try(Osi.SetImmortal, member, 1) end
            end
            afterMs(2000, renew)
        end
        renew()
    end
    return Stage.Guarding
end

--- Puts a character (default: the host) next to an object, at once - no walking.
--- `party` = true brings the whole party.
---
--- The destination comes first and the traveller second, as in GK.Teleport, where the character
--- is also the last and optional argument: Visit(chest) sends the host to the chest. Written the
--- other way round - Visit(host, chest) - it used to teleport the chest to the host without a
--- word, which reads as a success and is not one, so the wrong order is now refused by name.
--- To move an object to somebody on purpose, which is a different act, there is Stage.Bring.
function Stage.Visit(target, who, party)
    if who ~= nil and Osi.IsCharacter(who) ~= 1 then
        return nil, "Visit(target, who): the traveller goes second and has to be a character; "
            .. "to move an object to somebody use Stage.Bring(what, to)"
    end
    local movers = party and Stage.Party() or { host(who) }
    for _, id in ipairs(movers) do
        noteMoved(id)
        Osi.TeleportTo(id, target, "", 0, 0, 0, 0, 1)
    end
    return Stage.Position(movers[1])
end

--- Moves an object to a character (default: the host), at once: the other half of Visit, for
--- staging a situation around somebody rather than sending somebody to it. Returns where it landed.
function Stage.Bring(what, to)
    noteMoved(what)
    Osi.TeleportTo(what, host(to), "", 0, 0, 0, 0, 1)
    return Stage.Position(what)
end

--- Brings a character the story keeps off stage into the level (and takes it off again).
function Stage.OnStage(id, on)
    local was = try(Osi.IsOnStage, id)
    if was ~= nil then
        note("onstage:" .. id, "on stage: " .. id, function()
            if Ext.Entity.Get(id) then try(Osi.SetOnStage, id, was) end
            return true
        end)
    end
    Osi.SetOnStage(id, on == false and 0 or 1)
    return Osi.IsOnStage(id) == 1
end

--- The dialogues the story has registered for a speaker (a uuid or part of a name):
--- { { speaker, dialog } }. GK.Stage.Talk starts one.
function Stage.Dialogs(speaker)
    local key = speaker and (speaker:match("(%x+%-%x+%-%x+%-%x+%-%x+)$") or speaker)
    return GK.Dialogs(key)
end

--- Starts a dialogue with a speaker: the one named, or the first the story registered for it.
--- The host is put next to the speaker. Returns the dialogue started, or nil.
function Stage.Talk(speaker, dialog, who)
    if dialog == nil then
        local known = Stage.Dialogs(speaker)
        if #known == 0 then return nil end
        dialog, speaker = known[1].dialog, known[1].speaker
    end
    if GK.StartDialog(dialog, speaker, who) then return dialog end
    return nil
end

--- Takes away what holds a scene up on the server side and says what it did:
--- a dead party member is raised, hit points restored, forced turn-based mode left,
--- tutorials marked as seen. (Windows, dialogues and cutscenes are the client's.)
function Stage.Unblock()
    local did = {}
    for _, id in ipairs(Stage.Party()) do
        if Osi.IsDead(id) == 1 then
            Osi.Resurrect(id, "", 0)
            did[#did + 1] = "raised " .. id
        end
    end
    local id = Osi.GetHostCharacter()
    if try(Osi.IsInForceTurnBasedMode, id) == 1 then
        Osi.ForceTurnBasedMode(id, 0)
        did[#did + 1] = "left turn-based mode"
    end
    if not Stage.Quiet() then did[#did + 1] = "tutorials still not marked" end
    -- What holds the scene and is not the server's to take away is named, not passed over
    -- (N8: an empty answer while the intro dialogue was open).
    local talking, unread = GK.InDialog(id)
    if unread then did[#did + 1] = "NOT CHECKED: " .. unread end
    if talking then
        did[#did + 1] = "NOT DONE: the host is in a dialogue - the client's to answer (GK.Answer(n) on the UI side, or gatekit unblock)"
    end
    return did
end

-- ---------------------------------------------------------------------------------------
-- Taking the scene down

--- Removes an object from the game. Osi.RequestDelete only takes items; a character is taken
--- out of its fight and off the stage first (which works for any character) and then deleted
--- if it was created as temporary, as GK.Stage.Spawn does. An object created in this same tick is
--- removed on the next one (GK.Stage.Unborn).
function Stage.Remove(id)
    if Stage.Unborn(id, Stage.Remove) then return true end
    if Osi.IsCharacter(id) == 1 then
        try(Osi.LeaveCombat, id)
        try(Osi.SetOnStage, id, 0)
        try(Osi.RequestDeleteTemporary, id)
    else
        Osi.RequestDelete(id)
    end
    return true
end

--- Ends the host's fight, removes what the scene created, takes its statuses off and puts
--- changed factions back. Returns how many objects were removed.
function Stage.Reset()
    local scene = Stage.Scene
    try(Stage.EndCombat)
    if scene.protected then try(Stage.Protect, false) end
    for _, pair in ipairs(scene.statuses) do try(Osi.RemoveStatus, pair[1], pair[2]) end
    for id, old in pairs(scene.factions) do
        if old and Ext.Entity.Get(id) then try(Osi.SetFaction, id, old) end
    end
    local removed = 0
    for _, id in ipairs(scene.created) do
        if Ext.Entity.Get(id) then
            Stage.Remove(id)
            removed = removed + 1
        end
    end
    Stage.Scene = { created = {}, factions = {}, statuses = {} }
    return removed
end

-- ---------------------------------------------------------------------------------------
-- The stage scope: a baseline of the world, a clean back to it, and a check that it is clean
--
--   GK.Stage.Begin()     the world as it is now is the baseline (kept if one is there already)
--   ...                  a test stages its scene: GK.Stage.*, or the game's Osiris calls directly
--   GK.Stage.Clean()     everything since the baseline taken back: { did, removed, notUndone }
--   GK.Stage.Verify()    { clean, differences, pending }: compared with the baseline
--
-- What GK.Stage did is undone from the ledger (the first value each change replaced). What was
-- done behind its back is found by comparing with the baseline: objects that were not there
-- (spawned characters and their summons, items given, loot dropped, thrown things) are removed;
-- the party's place, hit points, life, statuses, passives, spells, tags, equipment, faction,
-- immortality and resources are put back; objects near the stage that moved, died or were hurt
-- are put back; new surfaces are cleared; combat is ended; the game speed and the guard are set
-- back. What cannot be put back is said in `notUndone`, and Verify says the world is not clean
-- - the caller then reloads the level (the host's reset ladder, docs/contracts/lua-api.md).

-- The equipment slots, from the game's own enumeration (Ext.Enums.ItemSlot), each by the name
-- Osi.GetEquippedItem knows: the weapon slots are spelt out there (it answers nothing for
-- "MeleeMainHand" and the like), the others are the enumeration's names.
local OSI_SLOT = { MeleeMainHand = "Melee Main Weapon", MeleeOffHand = "Melee Offhand Weapon",
                   RangedMainHand = "Ranged Main Weapon", RangedOffHand = "Ranged Offhand Weapon" }
local function partySlots()
    local out = {}
    for name in pairs(Ext.Enums.ItemSlot or {}) do
        if type(name) == "string" and name ~= "Sentinel" then out[#out + 1] = OSI_SLOT[name] or name end
    end
    table.sort(out)
    return out
end
local PARTY_SLOTS = partySlots()

--- The equipment slots the stage reads and puts back, by the names Osi.GetEquippedItem knows.
function Stage.EquipmentSlots()
    local out = {}
    for i, s in ipairs(PARTY_SLOTS) do out[i] = s end
    return out
end

--- Whether an object was there when the baseline was taken (true when there is no baseline).
function Stage.ExistedBefore(id)
    local base = Stage.Ledger.baseline
    if not base or type(id) ~= "string" then return true end
    return base.entities[id:sub(-36)] ~= nil
end

--- Notes the lock of an item, so a clean puts it back.
function Stage.NoteLock(item)
    local was = try(Osi.IsLocked, item)
    if was == nil then return end
    note("lock:" .. item, "the lock of " .. item, function()
        if Ext.Entity.Get(item) == nil then return true end
        if was == 1 and try(Osi.IsLocked, item) ~= 1 then try(Osi.Lock, item, "NOKEY")
        elseif was ~= 1 and try(Osi.IsLocked, item) == 1 then try(Osi.Unlock, item, Osi.GetHostCharacter()) end
        return true
    end)
end

--- Sets a story flag and notes what it was: Flag(flag, on, who) - who nil is the global flag.
function Stage.Flag(flag, on, who)
    local get = who and function() return Osi.GetFlag(flag, who) end or function() return Osi.GetFlag(flag, "NULL_00000000-0000-0000-0000-000000000000") end
    local was = try(get)
    note("flag:" .. flag .. "|" .. tostring(who), "flag " .. flag, function()
        if was == nil then return false, "flag " .. flag .. " could not be read before it was set" end
        if was == 1 then
            if who then Osi.SetFlag(flag, who) else Osi.SetFlag(flag, "NULL_00000000-0000-0000-0000-000000000000") end
        else
            if who then Osi.ClearFlag(flag, who) else Osi.ClearFlag(flag, "NULL_00000000-0000-0000-0000-000000000000") end
        end
        return true
    end)
    if on == false then
        if who then Osi.ClearFlag(flag, who) else Osi.ClearFlag(flag, "NULL_00000000-0000-0000-0000-000000000000") end
    else
        if who then Osi.SetFlag(flag, who) else Osi.SetFlag(flag, "NULL_00000000-0000-0000-0000-000000000000") end
    end
    return try(get)
end

--- Adds an Osiris fact (DB_Name(a, b ...)) and notes it: a clean deletes it again.
function Stage.Fact(db, ...)
    local args = { ... }
    local dbase = Osi[db]
    if dbase == nil then error("GK.Stage.Fact: no Osiris database " .. tostring(db), 2) end
    local had = try(function() return #dbase:Get(table.unpack(args)) > 0 end)
    if not had then
        note("fact:" .. db .. ":" .. table.concat((function() local t = {} for i, a in ipairs(args) do t[i] = tostring(a) end return t end)(), ","),
            "fact " .. db, function() dbase:Delete(table.unpack(args)) return true end)
    end
    dbase(table.unpack(args))
    return true
end

--- Something a test did that a clean should take back, as a function: OnClean(fn, what).
--- fn returns true (done), or false and why it could not be.
function Stage.OnClean(fn, what)
    local ledger = Stage.Ledger
    ledger.serial = (ledger.serial or 0) + 1
    note("custom:" .. ledger.serial, what or "a test's own undo", fn)
    return true
end

local function uuidSet(component)
    local set, n = {}, 0
    for _, entity in ipairs(Ext.Entity.GetAllEntitiesWithComponent(component)) do
        local id = uuidOf(entity)
        if id then set[id] = true; n = n + 1 end
    end
    return set, n
end

local function sortedKeys(set)
    local out = {}
    for key in pairs(set) do out[#out + 1] = key end
    table.sort(out)
    return out
end

--- The boosts of an object added through Osiris (Osi.AddBoosts: the boost's cause type is Story),
--- as { { text, cause } }: `text` as the game holds it ("Initiative(3)"), `cause` the name given.
function Stage.StoryBoosts(id)
    local out = {}
    local entity = try(Ext.Entity.Get, id)
    local groups = entity and try(function() return entity.BoostsContainer.Boosts end)
    for _, group in ipairs(groups or {}) do
        for _, boost in ipairs(group.Boosts or {}) do
            local info = try(function() return boost.BoostInfo end)
            if info and try(function() return tostring(info.Cause.Type) end) == "Story" then
                -- the game keeps a boost as its name and its arguments: Initiative and "3"
                local name, args = tostring(info.Params.Boost), tostring(info.Params.Params or "")
                out[#out + 1] = { text = args ~= "" and (name .. "(" .. args .. ")") or name, cause = tostring(info.Cause.Cause) }
            end
        end
    end
    return out
end

-- The Story boosts of an object as a multiset "cause|text" -> count.
local function storySet(id)
    local set = {}
    for _, b in ipairs(Stage.StoryBoosts(id)) do
        local key = b.cause .. "|" .. b.text
        set[key] = (set[key] or 0) + 1
    end
    return set
end

-- Components the engine keeps or removes by itself (Server/GameKeeps.json): read once.
local KEEPS = "Mods/Gwynerva_GateKit/ScriptExtender/Lua/Server/GameKeeps.json"
Stage.Keeps = nil

--- The data of GameKeeps.json: { kept = { name = { lua, empty?, default?, why } }, passing = { name = { why } } }.
function Stage.GameKeeps()
    if Stage.Keeps == nil then
        local text = try(Ext.IO.LoadFile, KEEPS, "data")
        local data = text and try(Ext.Json.Parse, text)
        Stage.Keeps = type(data) == "table" and data or { kept = {}, passing = {}, unread = KEEPS }
    end
    return Stage.Keeps
end

-- Whether the engine removes a component by itself a moment later: a one-frame component, or one
-- GameKeeps.json lists as passing.
local function passing(name)
    return name:find("OneFrameComponent$") ~= nil or Stage.GameKeeps().passing[name] ~= nil
end

-- Whether a component GameKeeps.json lists as kept holds its default on `entity`: true; false and
-- what it holds instead; nil for a component the list does not have.
local function keptDefault(entity, name)
    local entry = Stage.GameKeeps().kept[name]
    if not entry then return nil end
    local component = entity and try(function() return entity[entry.lua] end)
    if component == nil then return false, "it cannot be read as " .. entry.lua end
    -- a component whose value is the game's own record of its last request, whatever it holds
    if entry.any then return true end
    if entry.empty then
        -- a list or a set of the engine: counted by walking it, which both allow
        local n = try(function()
            local count = 0
            for _ in pairs(component[entry.empty]) do count = count + 1 end
            return count
        end)
        if n == 0 then return true end
        return false, entry.empty .. " holds " .. tostring(n)
    end
    for field, value in pairs(entry.default or {}) do
        local now = try(function() return component[field] end)
        if now ~= value then return false, field .. " = " .. tostring(now) .. ", its default " .. tostring(value) end
    end
    return true
end

-- The kind of recharge of an action resource from the game's data ("Default" is each turn; "Rest",
-- "ShortRest", "FullRest", "ExhaustedRest", "Combat", "Never").
local function replenishOf(guid)
    local def = try(Ext.StaticData.Get, guid, "ActionResource")
    local kind = def and try(function() return tostring(def.ReplenishType) end)
    return def and try(function() return def.Name end) or tostring(guid), kind and kind:match("%((%w+)%)") or kind
end

-- A resource the game itself fills again when a turn starts or a fight ends.
local function refilledByTheGame(kind) return kind == "Default" or kind == "Combat" end

-- Whether the game itself is about to fill a member's turn resources again: it is in a fight (the
-- game fills them when the fight ends) or holds the game's recharge timer of a resource spent out
-- of combat.
local function refillComing(m)
    return m.combat or m.passing["esv::action_resource::CooldownTrackingComponent"] == true
end

-- Whether the system that owns the drawn weapon is still at work on a member (a fight, or one of
-- its own passing components: an animation that keeps the weapon out, a sheathing that plays).
local function unsheathBusy(m)
    if m.combat then return true end
    for name in pairs(m.passing) do if name:find("^esv::unsheath::") and not name:find("OneFrameComponent$") then return true end end
    return false
end

-- The spells of a spell book as a multiset by prototype and source: "<prototype>|<source type>#<n>",
-- the n-th of that pair. A weapon's attack and the same attack learned are two entries, and a
-- second grant of one is seen.
local function spellsOf(entity)
    local out, seen = {}, {}
    try(function()
        for _, s in ipairs(entity.SpellBook.Spells) do
            local pair = tostring(s.Id.Prototype) .. "|" .. tostring(s.Id.SourceType)
            seen[pair] = (seen[pair] or 0) + 1
            out[pair .. "#" .. seen[pair]] = true
        end
    end)
    return out
end

-- Spells the equipment brings and takes away by itself (their source is the item or the weapon):
-- Clean puts the equipment back and leaves these to it.
local EQUIPMENT_SOURCES = { EquippedItem = true, WeaponAttack = true, UnarmedAttack = true }
local function spellEntry(key)
    local prototype, source = key:match("^(.-)|(.-)#%d+$")
    return prototype or key, source
end

-- What a party member is now: everything a clean puts back.
local function member(id)
    local entity = Ext.Entity.Get(id)
    local m = { pos = Stage.Position(id), dead = try(Osi.IsDead, id) == 1, faction = try(Osi.GetFaction, id),
                immortal = try(Osi.IsImmortal, id), statuses = {}, passives = {}, spells = {}, tags = {}, equipped = {},
                drawn = try(Osi.IsWeaponUnsheathed, id), combat = try(Osi.IsInCombat, id) == 1 }
    m.hp = try(function() return entity.Health.Hp end)
    for _, status in ipairs(Stage.Statuses(id)) do m.statuses[status] = true end
    try(function() for _, p in ipairs(entity.PassiveContainer.Passives) do m.passives[p.Passive.PassiveId] = true end end)
    m.spells = spellsOf(entity)
    try(function() for _, t in ipairs(entity.Tag.Tags) do m.tags[tostring(t)] = true end end)
    for _, slot in ipairs(PARTY_SLOTS) do
        local item = try(Osi.GetEquippedItem, id, slot)
        if item then m.equipped[slot] = item end
    end
    m.boosts = try(function()
        local n = 0
        for _, b in ipairs(entity.BoostsContainer.Boosts) do n = n + #b.Boosts end
        return n
    end)
    m.story = storySet(id)
    -- every action resource by "<guid>#<level index>": { amount, name, kind } (kind: its recharge)
    m.resources = {}
    try(function()
        for guid, list in pairs(entity.ActionResources.Resources) do
            local name, kind = replenishOf(tostring(guid))
            for i, e in ipairs(list) do
                m.resources[tostring(guid) .. "#" .. i] = { amount = e.Amount, max = e.MaxAmount, name = name .. (#list > 1 and ("#" .. i) or ""), kind = kind }
            end
        end
    end)
    m.cooldowns = {}
    try(function()
        for _, c in ipairs(entity.SpellBookCooldowns.Cooldowns) do
            m.cooldowns[try(function() return c.SpellId.Prototype end) or tostring(c)] = true
        end
    end)
    m.concentration = try(function() return entity.Concentration.SpellId.Prototype end) or ""
    -- experience: a kill gives it, and enough of it adds eoc::exp::CanLevelUpComponent
    m.experience = try(function()
        return { total = entity.Experience.TotalExperience, current = entity.Experience.CurrentLevelExperience,
                 available = try(function() return entity.AvailableLevel.Level end) }
    end)
    -- every component the member holds: a story state set by a raw Osiris call (a component the
    -- clean has no undo for) keeps a type alive, and `alive` of the next test would then fail. A
    -- passing one (the engine removes it by itself) is apart: `busy` says the game is still at work.
    m.components, m.passing = {}, {}
    try(function()
        for _, name in ipairs(entity:GetAllComponentNames(true)) do
            if passing(name) then m.passing[name] = true else m.components[name] = true end
        end
    end)
    m.busy = next(m.passing) ~= nil
    return m
end

-- The listed passing components a member holds (the one-frame ones are left out of the line).
local function passingLine(m)
    local out = {}
    for name in pairs(m.passing) do if not name:find("OneFrameComponent$") then out[#out + 1] = name end end
    table.sort(out)
    return table.concat(out, ", ")
end

local function near(level, center, radius)
    local out = {}
    for _, component in ipairs({ "ServerCharacter", "ServerItem" }) do
        for _, entity in ipairs(Ext.Entity.GetAllEntitiesWithComponent(component)) do
            local id = uuidOf(entity)
            if id and entity.Level and entity.Level.LevelName == level then
                local x, y, z = try(Osi.GetPosition, id)
                if x and center and (x - center[1]) ^ 2 + (z - center[3]) ^ 2 <= radius * radius then
                    out[id] = { x, y, z, owner = try(Osi.GetInventoryOwner, id), dead = try(Osi.IsDead, id) == 1,
                                hp = try(function() return entity.Health.Hp end), character = component == "ServerCharacter" }
                end
            end
        end
    end
    return out
end

-- The game's relation component (one, on the server): its tables of plain values, by field, as
-- copies. An attack on a neutral creature adds entries of value 0 (hostile) to one of them, and
-- every creature of that faction is then the party's enemy, a fight's end and the attacked
-- creature's deletion notwithstanding; deleting the entries makes them neutral again (read live).
-- The game's own names of these fields are not known (field_40 ...).
local function relationComponent()
    local holder = try(function() return Ext.Entity.GetAllEntitiesWithComponent("Relation")[1] end)
    return holder and try(function() return holder.Relation end)
end

local function relations()
    local r = relationComponent()
    if not r then return nil end
    local out = {}
    for field, map in pairs(r) do
        local copy, plain = {}, true
        local ok = pcall(function()
            for k, v in pairs(map) do
                local kind = type(v)
                if kind == "number" or kind == "boolean" or kind == "string" then copy[k] = v else plain = false break end
            end
        end)
        if ok and plain then out[tostring(field)] = copy end
    end
    return out
end

-- How the relation tables differ from `was`: { field = { added, changed, lost } } of the fields that differ.
local function relationChanges(was, now)
    local out = {}
    if not (was and now) then return out end
    for field, before in pairs(was) do
        local after = now[field] or {}
        local d = { added = {}, changed = {}, lost = {} }
        for k, v in pairs(after) do
            if before[k] == nil then d.added[#d.added + 1] = k elseif before[k] ~= v then d.changed[#d.changed + 1] = k end
        end
        for k in pairs(before) do if after[k] == nil then d.lost[#d.lost + 1] = k end end
        if #d.added + #d.changed + #d.lost > 0 then out[field] = d end
    end
    return out
end

--- Puts the game's relation tables back as `was` (a baseline's): entries added are deleted,
--- changed and lost ones written back. Answers what it did, a line per table.
function Stage.RestoreRelations(was)
    local r = relationComponent()
    local did = {}
    if not (r and was) then return did end
    for field, d in pairs(relationChanges(was, relations())) do
        local ok = pcall(function()
            local map = r[field]
            for _, k in ipairs(d.added) do map[k] = nil end
            for _, k in ipairs(d.changed) do map[k] = was[field][k] end
            for _, k in ipairs(d.lost) do map[k] = was[field][k] end
        end)
        did[#did + 1] = string.format("relations %s: %d added taken away, %d changed and %d lost written back%s",
            field, #d.added, #d.changed, #d.lost, ok and "" or " (the write failed)")
    end
    return did
end

-- The world in a few numbers and sets: what a clean returns to and Verify compares.
local function snapshot(radius)
    local s = { level = Stage.CurrentLevel(), party = {}, time = Ext.Utils.MonotonicTime() }
    local chars, nChars = uuidSet("ServerCharacter")
    local items, nItems = uuidSet("ServerItem")
    s.entities = {}
    for id in pairs(chars) do s.entities[id] = "character" end
    for id in pairs(items) do s.entities[id] = "item" end
    s.counts = { characters = nChars, items = nItems, surfaces = #Ext.Entity.GetAllEntitiesWithComponent("ServerSurface") }
    for _, id in ipairs(Stage.Party(true)) do
        local m = member(id)
        -- a baseline taken while the game is still at work holds where the game is going, not
        -- where it is: a turn resource it is filling holds its maximum; a weapon its unsheath
        -- system is moving is not part of the baseline
        if refillComing(m) then
            for _, r in pairs(m.resources) do if refilledByTheGame(r.kind) and r.max then r.amount = r.max end end
        end
        if unsheathBusy(m) then m.drawn = nil end
        s.party[id] = m
    end
    local hostId = Osi.GetHostCharacter()
    s.center = Stage.Position(hostId)
    s.radius = radius or 30
    s.near = near(s.level, s.center, s.radius)
    s.combat = try(Osi.IsInCombat, hostId) == 1
    s.relations = relations()
    s.speed = try(function() return GK.Speed.State() end)
    s.guarding = Stage.Guarding == true
    s.protected = Stage.Scene.protected == true
    s.attention = Stage.AttentionNow()
    s.outOfLevel = Stage.OutOfLevel()
    s.globals = {}
    for key in pairs(_ENV) do s.globals[key] = true end
    return s
end

-- A test's globals in this Lua state (SweepDummy = <uuid> ...) outlive it: a later test that reads
-- one finds an object the clean removed, and waits for it forever (seen 2026-11-14: a recipe that
-- failed before its own clean-up left SweepTarget, and every recipe after it waited on Ready).
-- Globals that were not there at the baseline and hold plain values are forgotten; tables and
-- functions (a watch, a helper) are left.
local function forgetGlobals(base)
    local forgotten = {}
    for key, value in pairs(_ENV) do
        local kind = type(value)
        if not base.globals[key] and (kind == "string" or kind == "number" or kind == "boolean") then forgotten[#forgotten + 1] = key end
    end
    for _, key in ipairs(forgotten) do _ENV[key] = nil end
    table.sort(forgotten)
    return forgotten
end

--- What lives outside the level and so outlives a level load (measured 2026-11-14: the engine's
--- global switches do; the list is docs/contracts/lua-api.md, "What survives a level load"): the game speed as GK.Speed holds
--- it and every plain value of Ext.Utils.GetGlobalSwitches(). { speed, switches }.
function Stage.OutOfLevel()
    local switches = {}
    try(function()
        for key, value in pairs(Ext.Utils.GetGlobalSwitches()) do
            local kind = type(value)
            if kind == "number" or kind == "boolean" or kind == "string" then switches[tostring(key)] = value end
        end
    end)
    local speed = try(function() local s = GK.Speed.State() return { enabled = s.enabled, engine = s.engine } end)
    return { speed = speed, switches = switches }
end

--- Puts what lives outside the level back as `saved` (a GK.Stage.OutOfLevel() of before), and
--- says what it changed. Used by Clean, and by the host after a level load (the Lua state, and
--- with it the baseline, does not outlive the load; the host carries `saved` across it).
function Stage.RestoreOutOfLevel(saved)
    local did = {}
    if type(saved) ~= "table" then return did end
    local speed = saved.speed
    if speed and GK.Speed then
        local now = try(GK.Speed.State)
        if now and (now.enabled ~= speed.enabled or now.engine ~= speed.engine) then
            if speed.enabled then
                try(GK.Speed.Enable)
            else
                try(GK.Speed.Disable)                     -- back at 1, following nothing
                if speed.engine and speed.engine ~= 1 then try(GK.Speed.Set, speed.engine) end
            end
            did[#did + 1] = "game speed set back to " .. tostring(speed.engine)
        end
    end
    if type(saved.switches) == "table" then
        local switches = Ext.Utils.GetGlobalSwitches()
        local changed = {}
        for key, value in pairs(saved.switches) do
            -- the game's DeltaTimeScale is the speed's own, set back above
            if key ~= "DeltaTimeScale" then
                local now = try(function() return switches[key] end)
                if now ~= nil and now ~= value then
                    if try(function() switches[key] = value return true end) then changed[#changed + 1] = key end
                end
            end
        end
        table.sort(changed)
        if #changed > 0 then did[#did + 1] = "global switches set back: " .. table.concat(changed, ", ", 1, math.min(#changed, 8)) end
    end
    return did
end

--- What a test changed of the out-of-level state since the baseline, with the baseline's values:
--- what the host keeps across a level load and sets again after it. Only what changed: the load
--- itself sets many switches, and setting every one back to its value from before froze the
--- client (seen 2026-11-14). nil without a baseline.
function Stage.BaselineOutOfLevel()
    local base = Stage.Ledger.baseline
    local saved = base and base.outOfLevel
    if not saved then return nil end
    local now = Stage.OutOfLevel()
    local out = { switches = {} }
    if saved.speed and now.speed and (saved.speed.engine ~= now.speed.engine or saved.speed.enabled ~= now.speed.enabled) then
        out.speed = saved.speed
    end
    for key, value in pairs(saved.switches or {}) do
        if key ~= "DeltaTimeScale" and now.switches[key] ~= nil and now.switches[key] ~= value then out.switches[key] = value end
    end
    return out
end

--- The attention record now (GK.Attention): { seq, waiting = { key = what } }, or nil without it.
function Stage.AttentionNow()
    local A = GK.Attention
    if not (A and A.Mark) then return nil end
    local waiting = {}
    for key, item in pairs(A.Waiting or {}) do waiting[key] = tostring(item.what) end
    return { seq = A.Mark(), waiting = waiting }
end

-- What the attention record holds since `seq` that the baseline did not: the side effects of a
-- test nobody staged on purpose (a fight that began, a dialogue, a death ...), as short lines.
local function sideEffects(base)
    local A = GK.Attention
    if not (A and A.Since and base) then return {}, {} end
    local since = A.Since(base.seq)
    local lines = {}
    for _, e in ipairs(since.events or {}) do
        if #lines < 30 then
            lines[#lines + 1] = tostring(e.what) .. ((e.who or e.window or e.dialog) and (" " .. tostring(e.who or e.window or e.dialog)) or "")
        end
    end
    local waiting = {}
    for key, item in pairs(A.Waiting or {}) do
        if not base.waiting[key] then waiting[#waiting + 1] = key .. ": " .. tostring(item.what) end
    end
    table.sort(waiting)
    return lines, waiting
end
Stage.SideEffects = sideEffects

--- Opens the stage scope: the world as it is now is the baseline Clean returns to. A baseline
--- already taken in this level is kept (`kept` = true) unless options.fresh; a baseline of another
--- level is replaced. options.radius: how far around the host objects are watched (default 30 m).
--- Returns { kept, level, counts, party, ms }.
function Stage.Begin(options)
    options = options or {}
    -- a baseline that cannot see the equipment would take every weapon for none
    if #PARTY_SLOTS == 0 then error("GK.Stage.Begin: the game's equipment slots (Ext.Enums.ItemSlot) cannot be read", 2) end
    -- a scope lost with an earlier Lua state: the world now is not taken for a baseline (it may hold
    -- what that scope staged); only a fresh Begin - after a level load - takes one
    if Stage.Lost and not options.fresh then
        return { kept = true, lost = Stage.Lost, level = Stage.CurrentLevel(), ms = 0 }
    end
    local t0 = Ext.Utils.MonotonicTime()
    local ledger = Stage.Ledger
    local kept = ledger.baseline ~= nil and not options.fresh and ledger.baseline.level == Stage.CurrentLevel()
    local L = GK.EventLog
    local abandoned = 0
    local strays = {}
    if not kept then
        -- what a scope staged and no Clean removed (this scope's, or a previous Lua state's: Stage.Strays)
        -- is never part of a baseline: it is carried into the new scope, so its Clean removes it
        -- and Verify names it until then
        for id, seq in pairs(Stage.Strays or {}) do strays[id] = seq end
        for _, row in ipairs(Stage.Tracks) do
            local p = row.payload
            if not p.fixture and (p.kind == "spawn" or p.kind == "item") and type(p.entity) == "string" then strays[p.entity:sub(-36)] = row.seq end
        end
        for id in pairs(strays) do if Ext.Entity.Get(id) == nil then strays[id] = nil end end
        Stage.Strays = {}
        -- a scope an earlier Lua state lost is no longer this one's matter: the world now is the baseline
        Stage.Lost = nil
        -- the scope before ends: its families go off, and what it tracked is no longer this
        -- scope's to undo (a fixture's rows stay open for GK.Fixture.Clear)
        if L.Scope then L.ScopeEnded(L.Scope) end
        local undo, tracks = {}, {}
        for _, row in ipairs(Stage.Tracks) do
            if row.payload.fixture then
                tracks[#tracks + 1] = row
                undo[row.seq] = ledger.undo[row.seq]
            else
                abandoned = abandoned + 1
            end
        end
        Stage.Tracks = tracks
        Stage.Ledger = { undo = undo, keys = {}, spots = {}, baseline = snapshot(options.radius) }
        Stage.Scene = { created = {}, factions = {}, statuses = {} }
        ledger = Stage.Ledger
    end
    local party, near = 0, 0
    for _ in pairs(ledger.baseline.party) do party = party + 1 end
    for _ in pairs(ledger.baseline.near) do near = near + 1 end
    local answer = { kept = kept, level = ledger.baseline.level, counts = ledger.baseline.counts, party = party, near = near }
    -- a new scope is the seq of its scope_begin row; every row written in it carries it, that one too
    if not kept then L.Scope = L.Seq + 1 end
    local carried = {}
    for id in pairs(strays) do
        ledger.baseline.entities[id] = nil
        ledger.baseline.near[id] = nil
        carried[#carried + 1] = id
    end
    -- what a stray carries goes with it
    for id in pairs(strays) do
        try(function()
            for _, inventory in ipairs(Ext.Entity.Get(id).InventoryOwner.Inventories) do
                for _, slot in pairs(inventory.InventoryContainer.Items) do
                    local item = uuidOf(slot.Item)
                    if item then ledger.baseline.entities[item] = nil; ledger.baseline.near[item] = nil end
                end
            end
        end)
    end
    table.sort(carried)
    L.Append("scope_begin", { kept = kept, level = answer.level, counts = answer.counts, party = party, near = near,
                              abandoned = abandoned > 0 and abandoned or nil, strays = #carried > 0 and carried or nil })
    answer.scope = L.Scope
    for _, id in ipairs(carried) do
        Stage.Track{ kind = try(Osi.IsCharacter, id) == 1 and "spawn" or "item", entity = id, key = "stray:" .. id,
                     carried = strays[id], note = "left by a scope that was not cleaned: " .. id }
    end
    if #carried > 0 then answer.strays = carried end
    -- detailed families for this scope: on until it ends (its Clean or the next Begin)
    if options.families then L.On(options.families, { seconds = options.seconds or 600, components = options.components }) end
    answer.ms = Ext.Utils.MonotonicTime() - t0
    return answer
end

--- What the level is, for the host to tell a simple test level from a level of the story: the
--- characters and items in it that are not the party. { level, characters, items, party }.
function Stage.Census()
    local level = Stage.CurrentLevel()
    local party = {}
    for _, id in ipairs(Stage.Party(true)) do party[id] = true end
    local out = { level = level, characters = 0, items = 0, party = 0 }
    for id in pairs(party) do out.party = out.party + 1 end
    for _, component in ipairs({ "ServerCharacter", "ServerItem" }) do
        for _, entity in ipairs(Ext.Entity.GetAllEntitiesWithComponent(component)) do
            local id = uuidOf(entity)
            if id and not party[id] and entity.Level and entity.Level.LevelName == level then
                if component == "ServerCharacter" then out.characters = out.characters + 1 else out.items = out.items + 1 end
            end
        end
    end
    return out
end

local function far(a, b, slack)
    if not a or not b then return a ~= b end
    return (a[1] - b[1]) ^ 2 + (a[3] - b[3]) ^ 2 > slack * slack or math.abs(a[2] - b[2]) > 2
end

local function diffSets(before, now)
    local added, lost = {}, {}
    if before == nil or now == nil then return added, lost end   -- a baseline taken before this was read
    for key in pairs(now) do if not before[key] then added[#added + 1] = key end end
    for key in pairs(before) do if not now[key] then lost[#lost + 1] = key end end
    table.sort(added)
    table.sort(lost)
    return added, lost
end

-- Weapons the game is putting away by itself, at the end of a fight or of an attack's animation:
-- id -> { tick, redraw, handle }. Settled on the game's own event of the change,
-- esv::unsheath::StateChangedEventOneFrameComponent to Sheathed (the weapon_state row), and drawn
-- again a tick after it when the baseline had it drawn. WEAPON_GUARD ticks without the event and
-- Verify says that it never came.
Stage.Redraw = Stage.Redraw or {}
local WEAPON_GUARD = 900

local function forgetSheathing(id)
    Stage.Redraw[id] = nil
end

local function awaitSheathed(id, redraw)
    local awaited = Stage.Redraw[id]
    if type(awaited) == "table" then awaited.redraw = awaited.redraw or redraw; return end
    Stage.Redraw[id] = { tick = tickNow(), redraw = redraw }
end

--- The game changed a creature's weapon state (the `weapon_state` row, written by GK.Act's hook of
--- the game's own event): a weapon the stage waits to see put away is settled, and drawn again a
--- tick later when the baseline had it drawn. Observation of the row; the redraw is the stage's.
function Stage.WeaponStateChanged(id, to)
    local awaited = id and Stage.Redraw[id]
    if type(awaited) ~= "table" or to ~= "Sheathed" then return end
    Stage.Later[#Stage.Later + 1] = { tick = tickNow(), fn = function()
        if Stage.Redraw[id] ~= awaited then return end
        forgetSheathing(id)
        if awaited.redraw and Ext.Entity.Get(id) then try(Osi.SetWeaponUnsheathed, id, 1, 1) end
    end }
end

--- The one writer of action resources (the stage's restore, GK.Stage.Refill, the party's
--- inspiration): the amount of resource `guid`'s `index`-th level on an entity (and its MaxAmount
--- when `max` is given), replicated to the clients. Answers whether it was written.
function Stage.WriteResource(entity, guid, index, amount, max)
    local ok = pcall(function()
        local row = entity.ActionResources.Resources[guid][index or 1]
        row.Amount = amount
        if max then row.MaxAmount = max end
    end)
    if ok then pcall(function() entity:Replicate("ActionResources") end) end
    return ok
end

local function setAmount(entity, key, amount)
    local guid, index = key:match("^(.*)#(%d+)$")
    return Stage.WriteResource(entity, guid, tonumber(index), amount)
end

-- The action resource definitions by name (ActionPoint, BonusActionPoint, Movement,
-- ReactionActionPoint, SpellSlot, InspirationPoint ...), read from the game's static data once.
local resourceIds
function Stage.ResourceId(name)
    if not resourceIds then
        resourceIds = {}
        for _, guid in ipairs(try(Ext.StaticData.GetAll, "ActionResource") or {}) do
            local def = try(Ext.StaticData.Get, guid, "ActionResource")
            local n = def and try(function() return def.Name end)
            if n then resourceIds[tostring(n)] = tostring(guid) end
        end
    end
    return resourceIds[name]
end

local REFILL = { action = "ActionPoint", bonus = "BonusActionPoint", movement = "Movement", reaction = "ReactionActionPoint",
                 slots = "SpellSlot" }

--- Gives a creature its turn's resources back inside a turn (a cheat): spec = { who, action,
--- bonus, movement, reaction, slots }, each true (to its maximum) or a number (that amount; for
--- `slots`, every level); none named: action, bonus, movement and reaction to their maximum. Every
--- change is a row of the stage ledger with the earlier amount as data, so Clean puts it back.
--- Answers once the server reads the new amounts and a `stage_refill` row says what changed:
--- { ok, who, changed = { { name, level, from, to } }, ticks }.
function Stage.Refill(spec)
    spec = spec or {}
    local who = spec.who or Osi.GetHostCharacter()
    local id = type(who) == "string" and (who:match("(%x+%-%x+%-%x+%-%x+%-%x+)$") or who) or who
    local entity = Ext.Entity.Get(id)
    if not entity then error("GK.Stage.Refill: no creature " .. tostring(who), 2) end
    local wanted = {}
    local any = false
    for key in pairs(REFILL) do if spec[key] ~= nil then any = true end end
    for key, name in pairs(REFILL) do
        local value = spec[key]
        if value == nil and not any and key ~= "slots" then value = true end
        if value ~= nil and value ~= false then wanted[#wanted + 1] = { name = name, value = value } end
    end
    local changed, writes = {}, {}
    for _, w in ipairs(wanted) do
        local guid = Stage.ResourceId(w.name)
        local levels = guid and try(function() return entity.ActionResources.Resources[guid] end)
        for index, row in ipairs(levels or {}) do
            local from, top = row.Amount, row.MaxAmount
            local to = w.value == true and top or tonumber(w.value)
            if to and to ~= from then
                -- `fixture`: the row is carried into a new Lua state (GK.Fixture) and undone there
                Stage.Track{ kind = "other", fixture = "Stage", key = "resource:" .. id .. "|" .. guid .. "#" .. index,
                             op = { op = "resource", who = id, resource = guid, index = index, amount = from },
                             note = string.format("%s of %s %s -> %s", w.name, id, tostring(from), tostring(to)) }
                if Stage.WriteResource(entity, guid, index, to) then
                    changed[#changed + 1] = { name = w.name, level = #levels > 1 and index or nil, from = from, to = to }
                    writes[#writes + 1] = { guid = guid, index = index, to = to }
                end
            end
        end
    end
    local asked = tickNow()
    -- GAP: the game raises no event for a written amount; the server's component is read on the
    -- next tick (the write is in force once it reads back), and the row says it
    return GK.Observe({ ok = true, who = id, changed = changed }, function(_, final)
        if tickNow() <= asked and not final then return nil end
        for _, w in ipairs(writes) do
            local now = try(function() return entity.ActionResources.Resources[w.guid][w.index].Amount end)
            if now ~= w.to then
                if final then return { ok = false, who = id, changed = changed, error = "the amounts did not read back" } end
                return nil
            end
        end
        GK.EventLog.Append("stage_refill", { who = id, changed = changed, ticks = tickNow() - asked })
        return { ok = true, who = id, changed = changed, ticks = tickNow() - asked }
    end, 3000)
end

--- Puts back what an action changes on a party member, from its baseline `was` (`now`: as it is):
--- cooldowns, every action resource, the drawn or sheathed weapon. What the game gives back by
--- itself is left to it and said: a turn resource while a refill is coming, the weapon while a
--- fight or an unsheath animation is under way. Answers what it did and left, a line each.
function Stage.RestoreMember(id, was, now)
    local did = {}
    local entity = Ext.Entity.Get(id)
    if not entity or type(was.resources) ~= "table" then return did end
    -- spells cooling down that were not: the game's own restore takes every cooldown off (and
    -- fills resources and hit points, which are set back to the baseline after it)
    local cooling = diffSets(was.cooldowns, now.cooldowns)
    if #cooling > 0 then
        try(Osi.RestoreParty, id)
        did[#did + 1] = "cooldowns of " .. id .. " taken off (RestoreParty): " .. table.concat(cooling, ", ", 1, math.min(#cooling, 5))
        now = member(id)
    end
    local left, set = {}, {}
    for key, base in pairs(was.resources) do
        local cur = now.resources[key]
        if cur and cur.amount ~= base.amount then
            if refilledByTheGame(base.kind) and cur.amount < base.amount and refillComing(now) then
                left[#left + 1] = base.name
            elseif setAmount(entity, key, base.amount) then
                set[#set + 1] = base.name .. " " .. tostring(cur.amount) .. " -> " .. tostring(base.amount) .. " (" .. tostring(base.kind) .. ")"
            end
        end
    end
    table.sort(left)
    table.sort(set)
    if #left > 0 then did[#did + 1] = "left to the game, which fills them again (each turn, and when a fight ends): " .. table.concat(left, ", ") .. " of " .. id end
    if #set > 0 then did[#did + 1] = "action resources of " .. id .. " set back: " .. table.concat(set, ", ") end
    local xp, xpNow = was.experience, now.experience
    if xp and xpNow and xp.total ~= xpNow.total then
        local ok = pcall(function()
            entity.Experience.TotalExperience = xp.total
            entity.Experience.CurrentLevelExperience = xp.current
            entity:Replicate("Experience")
            if xp.available then entity.AvailableLevel.Level = xp.available; entity:Replicate("AvailableLevel") end
            -- the level-up the gain allowed goes with it, unless the baseline had one
            if not was.components["eoc::exp::CanLevelUpComponent"] and entity.CanLevelUp then entity:RemoveComponent("CanLevelUp") end
        end)
        did[#did + 1] = "experience of " .. id .. " set back: " .. tostring(xpNow.total) .. " -> " .. tostring(xp.total) .. (ok and "" or " (the write failed)")
    end
    if was.drawn ~= nil and now.drawn == 1 and unsheathBusy(now) then
        -- the game puts them away when its fight or animation ends: awaited on its event, drawn again after it if they were
        awaitSheathed(id, was.drawn == 1)
        did[#did + 1] = "the weapons of " .. id .. (was.drawn == 1 and " are drawn again once the game has put them away" or " left to the game, which puts them away")
            .. " (the end of its fight or animation: its weapon_state row)"
    elseif was.drawn ~= nil and now.drawn ~= nil and now.drawn ~= was.drawn then
        try(Osi.SetWeaponUnsheathed, id, was.drawn, 1)
        forgetSheathing(id)
        did[#did + 1] = "the weapons of " .. id .. (was.drawn == 1 and " drawn again" or " put away") .. " (Osi.SetWeaponUnsheathed)"
    end
    return did
end

--- Takes everything back to the baseline (GK.Stage.Begin). Returns { did = {...}, removed =
--- { characters, items, surfaces }, notUndone = {...}, ms }: notUndone lists what is still not
--- as it was, and a caller that finds it non-empty reloads the level. Without a baseline it does
--- what GK.Stage.Reset does and says that nothing else could be compared.
function Stage.Clean()
    local t0 = Ext.Utils.MonotonicTime()
    local ledger = Stage.Ledger
    local base = ledger.baseline
    local did, notUndone = {}, {}
    local removed = { characters = 0, items = 0, surfaces = 0 }
    Stage.HpAsked = {}
    -- every tracked change taken back is said: by the line its undo gave, else by what it was
    local function said(ok, why, what)
        if ok == false then notUndone[#notUndone + 1] = why or what
        elseif type(why) == "string" then did[#did + 1] = why
        else did[#did + 1] = "undone: " .. tostring(what) end
    end

    -- 0. what the attention record saw happen since the baseline: the side effects
    local effects = base and sideEffects(base.attention) or {}
    -- a dialogue a test started (or a spawned NPC began) is asked to stop for every party member
    for _, id in ipairs(Stage.Party(true)) do
        if GK.InDialog and try(GK.InDialog, id) then
            try(Osi.DialogRequestStop, id)
            did[#did + 1] = "asked the dialogue of " .. id .. " to stop"
        end
    end
    -- 1. the fight ends first: nothing acts while the rest is taken away
    for _, id in ipairs(Stage.Party(true)) do
        local guid = try(Osi.CombatGetGuidFor, id)
        if guid then try(Osi.EndCombat, guid); did[#did + 1] = "ended combat " .. tostring(guid) end
    end
    -- 2. what was tracked in this scope (GK.Stage itself, GK.Fixture, a test's own), newest first;
    --    what was tracked as a spawn or an item: Verify looks that each one is gone
    local L = GK.EventLog
    local mine = Stage.Open("scope")
    local tracked = {}
    for i = #mine, 1, -1 do
        local row = mine[i]
        local p = row.payload
        local fine, because, line = Stage.UndoTrack(row)
        said(fine, because and (tostring(p.what) .. ": " .. tostring(because)) or line, p.what)
        if (p.kind == "spawn" or p.kind == "item") and type(p.entity) == "string" then tracked[#tracked + 1] = p.entity end
    end
    local resetRemoved = Stage.Reset()
    if not base then removed.characters = resetRemoved end
    -- the end of a clean: its row, and the families the scope switched on go off
    local function finish(result)
        result.ms = Ext.Utils.MonotonicTime() - t0
        L.Append("scope_clean", { did = #result.did, removed = result.removed, notUndone = result.notUndone,
                                  sideEffects = result.sideEffects, ms = result.ms })
        if L.Scope then L.ScopeEnded(L.Scope) end
        return result
    end
    if not base then
        notUndone[#notUndone + 1] = "no baseline (GK.Stage.Begin was not called): only what GK.Stage itself made was taken back"
        Stage.Ledger = { undo = ledger.undo, keys = {}, spots = {}, tracked = tracked }
        return finish({ did = did, removed = removed, notUndone = notUndone })
    end

    -- 3. objects that were not there: removed (a character created as temporary is deleted; one
    --    the game will not delete is taken off the stage and said)
    local partyNow = {}
    for _, id in ipairs(Stage.Party(true)) do partyNow[id] = true end
    for _, component in ipairs({ "ServerCharacter", "ServerItem" }) do
        for _, entity in ipairs(Ext.Entity.GetAllEntitiesWithComponent(component)) do
            local id = uuidOf(entity)
            if id and not base.entities[id] and not base.party[id] then
                if component == "ServerCharacter" then
                    if partyNow[id] then try(Osi.RemoveFromParty, id) end
                    Stage.Remove(id)
                    removed.characters = removed.characters + 1
                else
                    if not Stage.Unborn(id, function(i) try(Osi.RequestDelete, i) end) then try(Osi.RequestDelete, id) end
                    removed.items = removed.items + 1
                end
            end
        end
    end
    -- 4. the party as it was
    for id, was in pairs(base.party) do
        if Ext.Entity.Get(id) == nil then
            notUndone[#notUndone + 1] = "party member " .. id .. " is gone"
        else
            local now = member(id)
            if was.dead == false and now.dead then try(Osi.Resurrect, id, "", 0); did[#did + 1] = "raised " .. id end
            local added, lost = diffSets(was.statuses, now.statuses)
            for _, status in ipairs(added) do try(Osi.RemoveStatus, id, status) end
            for _, status in ipairs(lost) do
                notUndone[#notUndone + 1] = "status " .. status .. " of " .. id .. " ended and is not put back (its time is not known)"
            end
            added, lost = diffSets(was.passives, now.passives)
            for _, passive in ipairs(added) do try(Osi.RemovePassive, id, passive) end
            for _, passive in ipairs(lost) do try(Osi.AddPassive, id, passive) end
            if #added + #lost > 0 then did[#did + 1] = "passives of " .. id .. ": -" .. #added .. " +" .. #lost end
            -- the equipment first: an item put back grants its own spells (a weapon's attack), and
            -- the spells are compared after it, the equipment's own left to it
            for slot, item in pairs(was.equipped) do
                if now.equipped[slot] ~= item then
                    if Ext.Entity.Get(item) then
                        if try(Osi.GetInventoryOwner, item) ~= id then try(Osi.ToInventory, item, id, 1, 0, 0) end
                        try(Osi.Equip, id, item, 1, 0, 0)
                        did[#did + 1] = "equipped " .. slot .. " of " .. id .. " again"
                    else
                        notUndone[#notUndone + 1] = "the " .. slot .. " item of " .. id .. " is gone"
                    end
                end
            end
            added, lost = diffSets(was.spells, spellsOf(Ext.Entity.Get(id)))
            for _, key in ipairs(added) do
                local spell, source = spellEntry(key)
                if not EQUIPMENT_SOURCES[source or ""] then try(Osi.RemoveSpell, id, spell, 0) end
            end
            for _, key in ipairs(lost) do
                local spell, source = spellEntry(key)
                if not EQUIPMENT_SOURCES[source or ""] then try(Osi.AddSpell, id, spell, 0, 1) end
            end
            added, lost = diffSets(was.tags, now.tags)
            for _, tag in ipairs(added) do try(Osi.ClearTag, id, tag) end
            for _, tag in ipairs(lost) do try(Osi.SetTag, id, tag) end
            if was.faction and now.faction ~= was.faction then
                try(Osi.SetFaction, id, was.faction:match("(%x+%-%x+%-%x+%-%x+%-%x+)$") or was.faction)
            end
            if was.immortal ~= nil and now.immortal ~= was.immortal then try(Osi.SetImmortal, id, was.immortal) end
            for _, line in ipairs(Stage.RestoreMember(id, was, now)) do did[#did + 1] = line end
            if far(was.pos, now.pos, 0.5) and was.pos then
                try(Osi.TeleportToPosition, id, was.pos[1], was.pos[2], was.pos[3], "", 0, 0, 0, 0, 1)
                did[#did + 1] = "moved " .. id .. " back"
            end
            local line = putHp(id, was.hp)
            if line then did[#did + 1] = line end
        end
    end
    for id in pairs(partyNow) do
        if not base.party[id] and base.entities[id] then
            try(Osi.RemoveFromParty, id)
            did[#did + 1] = "took " .. id .. " out of the party"
        end
    end
    -- 5. objects near the stage that moved, died or were hurt
    for id, was in pairs(base.near) do
        if not base.party[id] then
            if Ext.Entity.Get(id) == nil then
                notUndone[#notUndone + 1] = "object " .. id .. " near the stage is gone"
            else
                if was.character then
                    if not was.dead and try(Osi.IsDead, id) == 1 then try(Osi.Resurrect, id, "", 0); did[#did + 1] = "raised " .. id end
                    local line = putHp(id, was.hp)
                    if line then did[#did + 1] = line end
                elseif try(Osi.IsDead, id) == 1 and not was.dead then
                    notUndone[#notUndone + 1] = "item " .. id .. " near the stage was destroyed"
                end
                local owner = try(Osi.GetInventoryOwner, id)
                if owner ~= was.owner then
                    if was.owner then try(Osi.ToInventory, id, was.owner, 1, 0, 0)
                    else
                        try(Osi.Drop, id)
                        try(Osi.TeleportToPosition, id, was[1], was[2], was[3], "", 0, 0, 0, 0, 1)
                    end
                    did[#did + 1] = "put " .. id .. " back where it was"
                elseif not was.owner then
                    local x, y, z = try(Osi.GetPosition, id)
                    if x and far({ x, y, z }, was, 0.5) then
                        try(Osi.TeleportToPosition, id, was[1], was[2], was[3], "", 0, 0, 0, 0, 1)
                        did[#did + 1] = "moved " .. id .. " back"
                    end
                end
            end
        end
    end
    -- 6. surfaces that were not there: cleared around the party and the spots the stage used
    local surfaces = #Ext.Entity.GetAllEntitiesWithComponent("ServerSurface")
    if surfaces > base.counts.surfaces then
        local places = {}
        for id in pairs(base.party) do places[#places + 1] = Stage.Position(id) end
        for _, at in ipairs(ledger.spots) do places[#places + 1] = at end
        if base.center then places[#places + 1] = base.center end
        for _, at in ipairs(places) do
            if at then
                for _, layer in ipairs({ "Ground", "Cloud" }) do
                    try(Osi.RemoveSurfaceLayerAtPosition, at[1], at[2], at[3], layer, 25.0)
                end
            end
        end
        removed.surfaces = surfaces - base.counts.surfaces
    end
    -- 7. the game's relations (a hostility an attack declared), the game's pace and the guard
    for _, line in ipairs(Stage.RestoreRelations(base.relations)) do did[#did + 1] = line end
    for _, line in ipairs(Stage.RestoreOutOfLevel(base.outOfLevel or { speed = base.speed })) do did[#did + 1] = line end
    Stage.Guarding = base.guarding
    Stage.Taught = nil
    local forgotten = base.globals and forgetGlobals(base) or {}
    if #forgotten > 0 then did[#did + 1] = "forgot globals: " .. table.concat(forgotten, ", ", 1, math.min(#forgotten, 8)) end
    ledger.keys, ledger.spots, ledger.tracked = {}, {}, tracked
    -- the next clean reads only what happens after this one (what this clean causes - a fight
    -- ending - is left out: Verify reads what is still waiting, not old events)
    if base.attention and GK.Attention and GK.Attention.Mark then base.attention.seq = GK.Attention.Mark() end
    return finish({ did = did, removed = removed, notUndone = notUndone, sideEffects = effects })
end

-- Of the components a member gained, those the game keeps (GameKeeps.json) and that hold their
-- default go to `kept` as lines; one that holds something else is a difference that says what. The
-- rest are answered.
local function judgeKept(id, entity, added, kept, differ)
    local out = {}
    for _, name in ipairs(added) do
        local fine, holds = keptDefault(entity, name)
        if fine then
            kept[#kept + 1] = name .. " on " .. id .. ": " .. Stage.GameKeeps().kept[name].why
        elseif fine == false then
            differ("component " .. name .. " of " .. id .. " is one the game keeps, but not at its default: " .. tostring(holds))
        else
            out[#out + 1] = name
        end
    end
    return out
end

-- The Osiris boosts of `id` that are not as they were, each named: added through the ledger (a
-- track row with a boost undo - in force, or undone and not gone yet) or outside it. `differ`
-- takes the line and whether it is only a moment's matter. Answers whether it named any.
local function storyDifferences(id, was, now, differ)
    if was == nil or now == nil then return false end
    local open = {}
    for _, row in ipairs(Stage.Open()) do
        local op = row.payload and row.payload.op
        if op and op.op == "boost" and type(op.text) == "string" then
            for part in op.text:gmatch("[^;]+") do open[op.who .. "|" .. (op.cause or "GKFixture") .. "|" .. part] = true end
        end
    end
    local tracked = Stage.Ledger.boosts or {}
    local named = false
    for key, n in pairs(now) do
        if n - (was[key] or 0) > 0 then
            named = true
            local cause, text = key:match("^(.-)|(.*)$")
            local full = id .. "|" .. key
            if open[full] then
                differ("boost " .. text .. " on " .. id .. " (cause " .. cause .. ") is tracked and still in force: Clean takes it off")
            elseif tracked[full] then
                differ("boost " .. text .. " on " .. id .. " (cause " .. cause .. ") is undone and not gone yet", true)
            else
                differ("boost " .. text .. " on " .. id .. " (cause " .. cause .. ") was added outside the ledger: "
                    .. "not tracked, and Clean does not take it off")
            end
        end
    end
    for key, n in pairs(was) do
        if n - (now[key] or 0) > 0 then
            named = true
            differ("boost " .. key:match("|(.*)$") .. " of " .. id .. " was taken off")
        end
    end
    return named
end

--- The server's preview casts (eoc::spell_cast::StateComponent with the option IsPreview: what a
--- client's targeting holds while it aims) by caster uuid: { uuid = { spell ... } }.
function Stage.PreviewCasts()
    local out = {}
    for _, entity in ipairs(try(Ext.Entity.GetAllEntitiesWithComponent, "SpellCastState") or {}) do
        local state = try(function() return entity.SpellCastState end)
        local caster = state and try(function() return state.Caster.Uuid.EntityUuid end)
        local preview = false
        for _, flag in pairs(state and try(function() return state.CastOptions end) or {}) do
            if tostring(flag) == "IsPreview" then preview = true end
        end
        if caster and preview then
            out[caster] = out[caster] or {}
            table.insert(out[caster], tostring(try(function() return state.SpellId.OriginatorPrototype end)))
        end
    end
    return out
end

--- Compares the world with the baseline: { clean, differences = {...}, pending, kept, busy, ms }.
--- `pending` is true when the only differences are a moment's matter: objects the game has been
--- asked to delete and has not yet (a deletion takes a tick or two), boosts undone that have not
--- left yet (a few ticks), what the game is still carrying out by itself (its passing components,
--- a turn resource it is filling, a weapon it is putting away): ask again in a moment. `kept`:
--- components the game made and keeps, at their default (GameKeeps.json) - not dirt, each with
--- why; `busy`: the passing components each member holds now.
function Stage.Verify()
    local t0 = Ext.Utils.MonotonicTime()
    local base = Stage.Ledger.baseline
    if Stage.Lost then
        local lost = Stage.Lost.undos
        return { clean = false, pending = false, lost = Stage.Lost, differences = { "the stage scope was lost with the Lua state before this one (a bridge reload): its baseline and "
            .. #lost .. " undo(s) that were not data" .. (#lost > 0 and (": " .. table.concat(lost, "; ", 1, math.min(#lost, 5))) or "")
            .. "; the world is not known clean until a level load" } }
    end
    if not base then return { clean = false, differences = { "no baseline: GK.Stage.Begin was not called" }, pending = false } end
    local differences, deleting, transient = {}, 0, 0
    local kept, busy = {}, {}
    local function differ(text, moment)
        if #differences < 40 then differences[#differences + 1] = text end
        if moment then transient = transient + 1 end
    end
    if Stage.CurrentLevel() ~= base.level then differ("level " .. tostring(Stage.CurrentLevel()) .. ", not " .. tostring(base.level)) end
    local extra = {}
    for _, component in ipairs({ "ServerCharacter", "ServerItem" }) do
        for _, entity in ipairs(Ext.Entity.GetAllEntitiesWithComponent(component)) do
            local id = uuidOf(entity)
            if id and not base.entities[id] and not base.party[id] then extra[#extra + 1] = id end
        end
    end
    -- tracked spawns and items (GK.Stage.Track) still there; open ones count too
    local seen = {}
    for _, id in ipairs(extra) do seen[id] = true end
    local lists = { Stage.Ledger.tracked or {} }
    local open = {}
    for _, row in ipairs(Stage.Open("scope")) do
        local p = row.payload
        if (p.kind == "spawn" or p.kind == "item") and type(p.entity) == "string" then open[#open + 1] = p.entity end
    end
    lists[2] = open
    for _, list in ipairs(lists) do
        for _, id in ipairs(list) do
            if not seen[id] and Ext.Entity.Get(id) ~= nil then seen[id] = true; extra[#extra + 1] = id end
        end
    end
    if #extra > 0 then
        deleting = #extra
        differ(#extra .. " object(s) that were not there: " .. table.concat(extra, ", ", 1, math.min(#extra, 5)), true)
        -- a staged creature still there: boosts added to it through Osiris, by name
        for _, id in ipairs(extra) do storyDifferences(id, {}, storySet(id), differ) end
    end
    local missing = 0
    for id in pairs(base.entities) do if Ext.Entity.Get(id) == nil then missing = missing + 1 end end
    if missing > 0 then differ(missing .. " object(s) of the baseline are gone") end
    local previews = Stage.PreviewCasts()
    local surfaces = #Ext.Entity.GetAllEntitiesWithComponent("ServerSurface")
    if surfaces > base.counts.surfaces then differ((surfaces - base.counts.surfaces) .. " surface(s) more than before") end
    for id, was in pairs(base.party) do
        if Ext.Entity.Get(id) == nil then differ("party member " .. id .. " is gone")
        else
            local now = member(id)
            local entity = Ext.Entity.Get(id)
            local function sets(name)
                local added, lost = diffSets(was[name], now[name])
                if name == "components" then added = judgeKept(id, entity, added, kept, differ) end
                -- a spell of the equipment follows the item put back a tick later: a moment's matter
                local function moment(list)
                    if name ~= "spells" then return false end
                    for _, key in ipairs(list) do
                        local _, source = spellEntry(key)
                        if not EQUIPMENT_SOURCES[source or ""] then return false end
                    end
                    return true
                end
                if #added > 0 then differ(name .. " of " .. id .. " added: " .. table.concat(added, ", ", 1, math.min(#added, 5)), moment(added)) end
                if #lost > 0 then differ(name .. " of " .. id .. " lost: " .. table.concat(lost, ", ", 1, math.min(#lost, 5)), moment(lost)) end
            end
            sets("statuses"); sets("passives"); sets("spells"); sets("tags"); sets("components"); sets("cooldowns")
            -- a preview cast between tests is an aim left behind: no targeting is open while the
            -- stage is verified, and the game does not end it by itself
            for _, spell in ipairs(previews[id] or {}) do
                differ(id .. " holds a preview cast of " .. spell .. " on the server with no aim open (an aim left without "
                    .. "GK.Gui.CancelAim, or a client task that dropped it)")
            end
            local still = passingLine(now)
            if still ~= "" then
                differ("the game is still at work on " .. id .. " (it removes these by itself): " .. still, true)
                busy[#busy + 1] = id .. ": " .. still
            end
            if type(was.resources) == "table" then
                for key, base in pairs(was.resources) do
                    local cur = now.resources[key]
                    if not cur then differ("action resource " .. base.name .. " of " .. id .. " is gone")
                    elseif cur.amount ~= base.amount then
                        local refill = refilledByTheGame(base.kind) and cur.amount < base.amount and refillComing(now)
                        differ("action resource " .. base.name .. " of " .. id .. ": " .. tostring(cur.amount) .. ", was " .. tostring(base.amount)
                            .. (refill and " (the game fills it again: a turn resource, its refill under way)" or ""), refill)
                    end
                end
                for key, cur in pairs(now.resources) do
                    if not was.resources[key] then differ("action resource " .. cur.name .. " of " .. id .. " is new") end
                end
            end
            if was.drawn ~= nil and now.drawn ~= was.drawn then
                local awaited = type(Stage.Redraw[id]) == "table" and Stage.Redraw[id] or nil
                local wait = awaited ~= nil and tickNow() - awaited.tick <= WEAPON_GUARD
                differ("the weapons of " .. id .. " are " .. (now.drawn == 1 and "drawn" or "put away") .. ", were " .. (was.drawn == 1 and "drawn" or "put away")
                    .. (wait and (awaited.redraw and " (drawn again once the game has put them away: its weapon_state row)" or " (the game is putting them away: its weapon_state row)")
                        or awaited and (" (the game's weapon_state row to Sheathed never came in " .. WEAPON_GUARD .. " ticks)") or ""), wait)
            end
            if was.experience and now.experience and was.experience.total ~= now.experience.total then
                differ("experience of " .. id .. ": " .. tostring(now.experience.total) .. ", was " .. tostring(was.experience.total))
            end
            if (was.concentration or "") ~= now.concentration then
                differ(id .. " concentrates on " .. (now.concentration ~= "" and now.concentration or "nothing") .. ", was on "
                    .. ((was.concentration or "") ~= "" and was.concentration or "nothing"))
            end
            if was.combat ~= nil and was.combat ~= now.combat and id ~= Osi.GetHostCharacter() then differ(id .. (now.combat and " is in a fight" or " is out of its fight")) end
            for slot, item in pairs(was.equipped) do
                if now.equipped[slot] ~= item then differ(slot .. " of " .. id .. " is not what it was") end
            end
            for slot in pairs(now.equipped) do
                if not was.equipped[slot] then differ(slot .. " of " .. id .. " holds something new") end
            end
            if now.dead ~= was.dead then differ(id .. (now.dead and " is dead" or " lives")) end
            if was.hp ~= now.hp then differ("hit points of " .. id .. ": " .. tostring(now.hp) .. ", were " .. tostring(was.hp)) end
            if was.faction ~= now.faction then differ("faction of " .. id .. ": " .. tostring(now.faction)) end
            if was.immortal ~= now.immortal then differ("immortality of " .. id .. ": " .. tostring(now.immortal)) end
            local named = storyDifferences(id, was.story, now.story, differ)
            if not named and was.boosts ~= now.boosts then differ("boosts of " .. id .. ": " .. tostring(now.boosts) .. ", were " .. tostring(was.boosts)) end
            if far(was.pos, now.pos, 1.0) then differ(id .. " is not where it was") end
        end
    end
    for _, id in ipairs(Stage.Party(true)) do
        if not base.party[id] and base.entities[id] then differ(id .. " joined the party") end
    end
    for id, was in pairs(base.near) do
        if not base.party[id] and Ext.Entity.Get(id) ~= nil then
            local x, y, z = try(Osi.GetPosition, id)
            local owner = try(Osi.GetInventoryOwner, id)
            if owner ~= was.owner then differ(id .. " near the stage changed hands")
            elseif not was.owner and x and far({ x, y, z }, was, 1.0) then differ(id .. " near the stage moved") end
            if (try(Osi.IsDead, id) == 1) ~= was.dead then differ(id .. " near the stage " .. (was.dead and "lives" or "is dead")) end
        end
    end
    for field, d in pairs(relationChanges(base.relations, relations())) do
        differ(string.format("the game's relations (%s): %d entries added, %d changed, %d lost - a hostility an attack declared, "
            .. "or a relation set by the story; Clean puts them back", field, #d.added, #d.changed, #d.lost))
    end
    local hostId = Osi.GetHostCharacter()
    if (try(Osi.IsInCombat, hostId) == 1) ~= base.combat then differ("the host " .. (base.combat and "is out of combat" or "is in combat")) end
    local speed = try(function() return GK.Speed.State() end)
    if base.speed and speed and (speed.engine ~= base.speed.engine or speed.enabled ~= base.speed.enabled) then
        differ("game speed " .. tostring(speed.engine) .. (speed.enabled and " (following)" or "") .. ", was " .. tostring(base.speed.engine))
    end
    if (Stage.Guarding == true) ~= base.guarding then differ("the party guard is " .. (Stage.Guarding and "on" or "off")) end
    if base.outOfLevel and base.outOfLevel.switches then
        local now = Stage.OutOfLevel().switches
        for key, value in pairs(base.outOfLevel.switches) do
            if key ~= "DeltaTimeScale" and now[key] ~= nil and now[key] ~= value then
                differ("global switch " .. key .. " = " .. tostring(now[key]) .. ", was " .. tostring(value))
            end
        end
    end
    -- what holds the game for the player and did not at the baseline (GK.Attention)
    if base.attention then
        local _, waiting = sideEffects(base.attention)
        for _, item in ipairs(waiting) do differ("attention: " .. item .. " is waiting") end
    end
    -- objects asked to go are not gone yet, boosts undone have not left: only a moment, if nothing else differs
    local pending = transient > 0 and #differences == transient and missing == 0
    -- the game's clock with the answer: a caller asking again while `pending` bounds its wait in
    -- game time, which holds at any speed. GAP: the caller reads Verify again rather than waiting
    -- for a row. The weapon is settled on its own event (awaitSheathed: the weapon_state row);
    -- the destruction of each passing component (Ext.Entity.OnDestroy) is the event for the rest.
    local clock = try(function() return GK.Speed.Clock() end) or {}
    local result = { clean = #differences == 0, differences = differences, pending = pending, kept = kept, busy = busy,
                     ticks = clock.ticks, time = clock.time, ms = Ext.Utils.MonotonicTime() - t0 }
    GK.EventLog.Append("scope_verify", { clean = result.clean, differences = #differences, pending = pending, kept = #kept, ms = result.ms })
    return result
end

-- What the previous Lua state of this side staged and did not undo (a spawn or an item it tracked,
-- not a fixture's: GK.Fixture carries its own): read once from its rows, carried into the next Begin.
if not Stage.StraysRead then
    Stage.StraysRead = true
    Stage.Strays = Stage.Strays or {}
    local previous = GK.EventLog.Previous and try(GK.EventLog.Previous) or nil
    if previous then
        local open, undone, inherited = {}, {}, nil
        for _, row in ipairs(previous) do
            local p = row.payload or {}
            if row.kind == "scope_track" and not p.fixture and (p.kind == "spawn" or p.kind == "item") and type(p.entity) == "string" then
                open[#open + 1] = row
            elseif row.kind == "scope_undo" and p.track then undone[p.track] = true
            -- what that state had itself inherited, until a Begin of its own took it into a scope
            elseif row.kind == "scope_strays" then inherited = p.entities
            elseif row.kind == "scope_begin" and not p.kept then inherited = nil end
        end
        for _, id in ipairs(inherited or {}) do Stage.Strays[id] = 0 end
        for _, row in ipairs(open) do
            if not undone[row.seq] then Stage.Strays[row.payload.entity:sub(-36)] = row.seq end
        end
    end
    -- said in this state's own log, so that a state after this one inherits them too when no
    -- Begin here takes them into a scope
    local ids = {}
    for id in pairs(Stage.Strays) do ids[#ids + 1] = id end
    table.sort(ids)
    if #ids > 0 then try(function() GK.EventLog.Append("scope_strays", { entities = ids }) end) end
end

-- A stage scope the previous Lua state left open (begun, not cleaned) cannot be carried whole: its
-- baseline and the undos that were functions went with that state. What is data is carried (the
-- fixtures' rows and the stage's resource writes: GK.Fixture; the spawns and items: Stage.Strays above);
-- the rest is declared lost here, with a scope_lost row, and GK.Stage.Verify says the world is not
-- known clean until the next Begin - so that the reset ladder climbs to a level load.
if not Stage.LostRead then
    Stage.LostRead = true
    local previous = GK.EventLog.Previous and try(GK.EventLog.Previous) or nil
    if previous then
        local open, undone, scope, scopeOpen = {}, {}, nil, false
        for _, row in ipairs(previous) do
            local p = row.payload or {}
            if row.kind == "scope_begin" then scope, scopeOpen, open = row.scope or p.scope or row.seq, true, {}
            elseif row.kind == "scope_clean" then scopeOpen, open = false, {}
            elseif row.kind == "scope_track" and not p.op and not (p.kind == "spawn" or p.kind == "item") then open[#open + 1] = row
            elseif row.kind == "scope_undo" and p.track then undone[p.track] = true end
        end
        if scopeOpen then
            local lost = {}
            for _, row in ipairs(open) do
                if not undone[row.seq] then lost[#lost + 1] = tostring(row.payload.what or row.payload.key) end
            end
            Stage.Lost = { scope = scope, undos = lost }
            try(function() GK.EventLog.Append("scope_lost", { scope = scope, undos = lost }) end)
        end
    end
end

return true
