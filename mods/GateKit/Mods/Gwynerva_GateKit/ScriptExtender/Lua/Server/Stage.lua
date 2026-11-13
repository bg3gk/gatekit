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
-- This file can also be loaded into a running game as it is (gatekit.py exec server @Stage.lua).

local GK = GK or Mods.GateKit.GK

local Stage = GK.Stage or {}
GK.Stage = Stage

-- What the current scene has created or changed (kept across reloads of this file).
Stage.Scene = Stage.Scene or { created = {}, factions = {}, statuses = {} }

local function try(fn, ...)
    local ok, a, b, c = pcall(fn, ...)
    if ok then return a, b, c end
    return nil
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
--- Returns the list of new uuids. Equipment is given at once and put on a moment later,
--- when the items exist: GK.Stage.Ready(uuids) tells when.
function Stage.Spawn(spec)
    if type(spec) == "string" then spec = { template = spec } end
    local count = spec.count or 1
    local center = spec.at or Stage.Position(spec.near)
    local distance = spec.at and (count > 1 and 1.5 or 0) or (spec.distance or 3)
    local made = {}
    for n = 1, count do
        local x, y, z = ring(center, n, count, distance)
        -- "temporary" (the fifth argument): only such characters can be deleted again
        local id = Osi.CreateAt(template(spec.template), x, y, z, 1, 1, "")
        if id == nil then error("could not create " .. tostring(spec.template)) end
        made[#made + 1] = id
        Stage.Scene.created[#Stage.Scene.created + 1] = id
        if spec.faction then Osi.SetFaction(id, faction(spec.faction)) end
        if spec.level then try(Osi.SetLevel, id, spec.level) end
        for _, item in ipairs(spec.items or {}) do Osi.TemplateAddTo(template(item), id, 1, 0) end
        for _, item in ipairs(spec.equipment or {}) do Osi.TemplateAddTo(template(item), id, 1, 0) end
    end
    if spec.equipment and #spec.equipment > 0 then
        -- TemplateAddTo creates the items on a later tick; put them on once they are there.
        Ext.Timer.WaitFor(700, function()
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
    Ext.Timer.WaitFor(700, function()
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
        inDialog = try(Osi.IsInDialog, id) == 1,
    }
end

-- ---------------------------------------------------------------------------------------
-- Fights

--- The party's characters (uuids).
function Stage.Party()
    local out = {}
    for _, entity in ipairs(Ext.Entity.GetAllEntitiesWithComponent("PartyMember")) do
        out[#out + 1] = uuidOf(entity)
    end
    return out
end

--- Makes the party unkillable (or mortal again). A scene that kills the last party member
--- ends in the "game over" screen, which pauses the server until a save is loaded - so
--- GK.Stage.Fight protects the party first; GK.Stage.Reset() lifts the protection.
function Stage.Protect(on)
    for _, id in ipairs(Stage.Party()) do Osi.SetImmortal(id, on == false and 0 or 1) end
    Stage.Scene.protected = on ~= false
    return true
end

--- Starts a fight between `enemies` (uuids) and a character (default: the host).
--- Enemies that are not hostile yet are moved to the enemy faction first. The party is made
--- unkillable unless `mortal` is true.
function Stage.Fight(enemies, who, mortal)
    local id = host(who)
    if type(enemies) == "string" then enemies = { enemies } end
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
        if waiting and attempts < 30 then Ext.Timer.WaitFor(500, engage) end
    end
    engage()
    return true
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

--- Ends the fight a character is in (default: the host's) without anybody dying.
function Stage.EndCombat(who)
    local guid = Osi.CombatGetGuidFor(host(who))
    if guid == nil then return false end
    Osi.EndCombat(guid)
    return true
end

--- Turn-based mode outside a fight, on or off, for a player character.
function Stage.TurnBased(on, who)
    Osi.ForceTurnBasedMode(host(who), on and 1 or 0)
    return true
end

-- ---------------------------------------------------------------------------------------
-- Actions

--- A character casts a spell on a target (uuid) or at a position ({ x, y, z }).
--- The spell is a stats name ("Target_MainHandAttack", "Projectile_FireBolt", "Shout_Dash").
--- The character must know the spell; GK.Stage.Teach(spell, who) adds it.
function Stage.Cast(spell, target, who)
    local id = host(who)
    if type(target) == "table" then
        Osi.UseSpellAtPosition(id, spell, target[1], target[2], target[3], 1)
    else
        Osi.UseSpell(id, spell, target or id)
    end
    return true
end

function Stage.Teach(spell, who)
    Osi.AddSpell(host(who), spell, 0, 1)
    return true
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
function Stage.Status(status, who, turns)
    local id = host(who)
    Osi.ApplyStatus(id, status, (turns or -1) < 0 and -1.0 or turns * 6.0, 1, id)
    Stage.Scene.statuses[#Stage.Scene.statuses + 1] = { id, status }
    return true
end

function Stage.Unstatus(status, who)
    Osi.RemoveStatus(host(who), status)
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

--- Deals damage of a type ("Slashing", "Fire" ...; default "Bludgeoning").
function Stage.Damage(amount, who, kind)
    local id = host(who)
    Osi.ApplyDamage(id, amount, kind or "Bludgeoning", "")
    return true
end

--- Sets hit points (default: to the maximum).
function Stage.Heal(who, hp)
    local id = host(who)
    local entity = Ext.Entity.Get(id)
    Osi.SetHitpoints(id, hp or (entity.Health and entity.Health.MaxHp) or 1, "Guaranteed")
    return true
end

function Stage.Kill(who)
    Osi.Die(host(who), 0, "NULL_00000000-0000-0000-0000-000000000000", 0, 1, 0)
    return true
end

function Stage.Resurrect(who)
    Osi.Resurrect(host(who), "", 0)
    return true
end

function Stage.Immortal(on, who)
    Osi.SetImmortal(host(who), on and 1 or 0)
    return true
end

--- Turns a character into the shape of another template; GK.Stage.Untransform puts it back.
function Stage.Transform(shape, who)
    Osi.Transform(host(who), template(shape), "296bcfb3-9dab-4a93-8ab1-f1c53c6674c9")
    return true
end

function Stage.Untransform(who)
    Osi.RemoveTransforms(host(who))
    return true
end

--- A surface on the ground: GK.Stage.Surface("SurfaceFire", 3) around the host.
function Stage.Surface(kind, radius, at, turns)
    at = at or Stage.Position()
    Osi.CreateSurfaceAtPosition(at[1], at[2], at[3], kind, radius or 3, (turns or 2) * 6.0)
    return true
end

function Stage.Lock(item, locked)
    if locked == false then Osi.Unlock(item, Osi.GetHostCharacter()) else Osi.Lock(item, "") end
    return Osi.IsLocked(item) == 1
end

-- ---------------------------------------------------------------------------------------
-- Rests, the camp, other levels

-- A short rest is a player command: GK.Command("ShortRest") on the UI side.

--- Asks for a long rest as the player would (the camp, the night, the morning).
function Stage.LongRest()
    Osi.RequestLongRest(Osi.GetHostCharacter(), 1)
    return true
end

--- Restores the party as a long rest does, at once and where it stands.
function Stage.Restore(who)
    Osi.RestoreParty(host(who))
    return true
end

function Stage.Camp(go)
    if go == false then
        Osi.PROC_Camp_ForcePlayersToCamp()  -- placeholder: leaving is done by the UI command
    else
        Osi.PROC_Camp_ForcePlayersToCamp()
    end
    return true
end

--- Moves the party to another level of the loaded module ("WLD_Main_A", "SCL_Main_A" ...).
function Stage.Level(name)
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
            if has(entity, "CanTrade") or has(entity, "Trader") then kinds.traders = true end
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
    Osi.TeleportTo(id, item, "", 0, 0, 0, 0, 1)
    Osi.Use(id, item, "")
    return true
end

--- Other things done to an item, by name: "pickup", "drop", "throw" (at `target`, or a few
--- metres away), "attack", "equip", "unequip", "lock", "unlock", "destroy", "move" (two metres aside).
function Stage.ItemAction(action, item, target, who)
    local id = host(who)
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
        Osi.ToInventory(item, id, 1, 0, 0)
        Ext.Timer.WaitFor(500, function() try(Osi.Equip, id, item, 1, 0, 0) end)
    elseif action == "unequip" then
        Osi.Unequip(id, item)
    elseif action == "lock" then
        Osi.Lock(item, "")
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
    return true
end

--- A short fight that plays itself: the host fights `enemies`, and every turn that starts is
--- ended at once by the game itself (no waiting from outside), until `turns` turns have
--- passed (default 6) - then the fight is ended. GK.Stage.SkirmishState() tells how far it is:
--- { running, turns, started } - poll it or wait for `running == false`.
function Stage.Skirmish(enemies, turns, who)
    local info = { running = true, turns = 0, limit = turns or 6, started = Ext.Utils.MonotonicTime(), who = who }
    Stage.SkirmishInfo = info
    Stage.Fight(enemies, who)
    -- The fight is watched, not listened to: the Osiris event TurnStarted did not reach a
    -- listener registered after the story was loaded in 12 fights of 31 (tour of 2026-11-13),
    -- and such a fight hung until the time limit. Ten times a second: whose turn is it? A turn
    -- not seen before is counted and ended; one that stays is ended again every half second.
    local function watch()
        if Stage.SkirmishInfo ~= info or not info.running then return end
        local now = Ext.Utils.MonotonicTime()
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
        Ext.Timer.WaitFor(100, watch)
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
    return true
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
    return true
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
    Stage.Guarding = on ~= false
    if Stage.Guarding and not Stage.GuardInstalled then
        Stage.GuardInstalled = true
        local function rescue(id)
            if not Stage.Guarding then return end
            for _, member in ipairs(Stage.Party()) do
                if member:sub(-36) == tostring(id):sub(-36) then
                    Osi.Resurrect(member, "", 0)
                    Osi.SetImmortal(member, 1)
                    Ext.Timer.WaitFor(300, function() try(Stage.Heal, member) end)
                end
            end
        end
        Ext.Osiris.RegisterListener("Dying", 1, "before", rescue)
        Ext.Osiris.RegisterListener("Died", 1, "after", rescue)
        local function renew()
            if Stage.Guarding then
                for _, member in ipairs(Stage.Party()) do try(Osi.SetImmortal, member, 1) end
            end
            Ext.Timer.WaitFor(2000, renew)
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
    for _, id in ipairs(movers) do Osi.TeleportTo(id, target, "", 0, 0, 0, 0, 1) end
    return Stage.Position(movers[1])
end

--- Moves an object to a character (default: the host), at once: the other half of Visit, for
--- staging a situation around somebody rather than sending somebody to it. Returns where it landed.
function Stage.Bring(what, to)
    Osi.TeleportTo(what, host(to), "", 0, 0, 0, 0, 1)
    return Stage.Position(what)
end

--- Brings a character the story keeps off stage into the level (and takes it off again).
function Stage.OnStage(id, on)
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
--- tutorials marked as seen. (Windows, dialogues and cutscenes are the client's:
--- the stand's `unblock` does both sides.)
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
    return did
end

-- ---------------------------------------------------------------------------------------
-- Taking the scene down

--- Removes an object from the game. Osi.RequestDelete only takes items; a character is taken
--- out of its fight and off the stage first (which works for any character) and then deleted
--- if it was created as temporary, as GK.Stage.Spawn does.
function Stage.Remove(id)
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

-- The events a staged scene produces, into the shared event log (GK.Events).
if not Stage.Watching then
    Stage.Watching = true
    for name, arity in pairs({
        CombatRoundStarted = 2, TurnStarted = 1, TurnEnded = 1, EnteredCombat = 2, LeftCombat = 2,
        Dying = 1, Resurrected = 1, StatusApplied = 4, StatusRemoved = 4, CastedSpell = 5,
        LongRestStarted = 0, LongRestFinished = 0, ShortRested = 1, EnteredForceTurnBased = 1,
        LeftForceTurnBased = 1, ShapeshiftChanged = 4, AttackedBy = 10,
    }) do
        pcall(GK.Watch, name, arity)
    end
end

return true
