-- GK.See: the situation as a player could know it - who and what is around, the fight, the party,
-- who watches whom (stealth), where one can walk. Server side; reads only, changes nothing.
--
-- Honest by default: every list is filtered by what the party perceives (the engine's own sight
-- test, Osi.CanSee, or a clear line of sight within a member's sight range - the camera shows what
-- the character's eyes do not face). Things a player cannot know are left out: characters off
-- stage, invisible or sneaking unseen, undiscovered traps, items in other inventories, story
-- triggers. `{ all = true }` in any options table turns the filter off (debugging, tests); every
-- row then says `perceived = false` where the party would not know it.
--
--   GK.See.Around(options)       characters and objects near the party: { at, characters, objects }
--   GK.See.Entity(id, options)   one character or object, the way the Examine window shows it
--   GK.See.Combat(options)       the fight: round, whose turn, the turn order with positions, health,
--                                armour, statuses, resources; for a party member on turn also what
--                                it can do (GK.See.Actions)
--   GK.See.Party()               members: level, class, experience, health, resources, spell slots,
--                                statuses, equipment, gold, level-up available
--   GK.See.Actions(who, target)  what a character can do now: spells and actions with cost, range,
--                                whether the resources are there, and whether `target` (uuid or
--                                {x,y,z}) is in range and in sight
--   GK.See.Path(who, to)         walking: { reachable, length, movement, withinMovement }
--   GK.See.Watchers(options)     who could notice the party: each NPC's sight range, field of view,
--                                facing, and which members it sees now
--   GK.See.Exposure(point, who)  which NPCs would see a character standing at `point`
--   GK.See.Status()              one line of everything: level, mode, whose turn, party health
--
-- Options shared by the readers: radius (metres, default 25), who (the viewpoint; default: the
-- party), all (no perception filter), limit (rows per list).
--
-- Conventions: positions are { x, y, z } with y up; `d` is the distance in metres from the
-- viewpoint; `dir` is the compass direction from it, with north = +z and east = +x (the game's map
-- has the same orientation: see docs/agent-play.md); `rel` is "party", "ally", "neutral" or
-- "hostile".

GK.See = GK.See or {}
local See = GK.See

-- ---------------------------------------------------------------------------------------
-- Small helpers

local function try(fn, ...)
    local ok, a, b, c = pcall(fn, ...)
    if ok then return a, b, c end
    return nil
end

local function has(entity, component)
    local ok, value = pcall(function() return entity[component] end)
    return ok and value ~= nil
end

local function uuidOf(entity)
    return entity and entity.Uuid and entity.Uuid.EntityUuid or nil
end

local function entityOf(id)
    if type(id) == "string" then return try(Ext.Entity.Get, id) end
    return id
end

local function nameOf(id)
    local entity = entityOf(id)
    local name = entity and try(function() return entity.DisplayName.Name:Get() end)
    if name and name ~= "" then return name end
    local handle = type(id) == "string" and try(Osi.GetDisplayName, id)
    return handle and try(Ext.Loca.GetTranslatedString, handle) or nil
end
See.Name = nameOf

local function posOf(id)
    local entity = entityOf(id)
    local p = entity and try(function() return entity.Transform.Transform.Translate end)
    if p then return { p[1], p[2], p[3] } end
    if type(id) == "string" then
        local x, y, z = try(Osi.GetPosition, id)
        if x then return { x, y, z } end
    end
    return nil
end
See.Position = posOf

-- Rounded so that JSON shows 8.7, not 8.700000000000001: divide, never multiply by 0.1.
local function round(v, step)
    local per = 1 / (step or 0.1)
    return math.floor(v * per + 0.5) / per
end

local function rpos(p) return p and { round(p[1]), round(p[2]), round(p[3]) } or nil end

local function dist(a, b)
    local dx, dy, dz = a[1] - b[1], a[2] - b[2], a[3] - b[3]
    return math.sqrt(dx * dx + dy * dy + dz * dz)
end

local function flat(a, b)
    local dx, dz = a[1] - b[1], a[3] - b[3]
    return math.sqrt(dx * dx + dz * dz)
end

local COMPASS = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" }
local function compass(from, to)
    local dx, dz = to[1] - from[1], to[3] - from[3]
    if dx * dx + dz * dz < 0.25 then return "here" end
    local angle = math.deg(math.atan(dx, dz)) % 360   -- 0 = +z (north), 90 = +x (east)
    return COMPASS[math.floor((angle + 22.5) / 45) % 8 + 1]
end
See.Compass = compass

--- The direction a character faces, in degrees (0 = north = +z, 90 = east = +x), from the
--- rotation of its transform. Checked live: the forward vector is (-sin yaw, 0, -cos yaw) where
--- yaw is the rotation about y, i.e. the game's TargetRotation.
local function facingOf(entity)
    local q = try(function() return entity.Transform.Transform.RotationQuat end)
    if not q then return nil end
    local yaw = 2 * math.atan(q[2], q[4])
    local fx, fz = -math.sin(yaw), -math.cos(yaw)
    return math.deg(math.atan(fx, fz)) % 360, { fx, 0, fz }
end

local function bool(v) return v == 1 or v == true end

local function host() return Osi.GetHostCharacter() end

--- The uuid of a character or item whose uuid starts with `prefix` (8 characters or more), on
--- the host's level; nil when none or more than one. For short ids typed by an agent.
function See.Resolve(prefix)
    prefix = tostring(prefix or ""):lower()
    if #prefix < 6 then return nil end
    local found
    for _, kind in ipairs({ "ServerCharacter", "ServerItem" }) do
        for _, e in ipairs(Ext.Entity.GetAllEntitiesWithComponent(kind)) do
            local id = uuidOf(e)
            if id and id:sub(1, #prefix) == prefix then
                if found and found ~= id then return nil end
                found = id
            end
        end
    end
    return found
end

--- The party's uuids, the host first.
function See.Members()
    local out, seen = { host() }, {}
    seen[out[1]] = true
    for _, entity in ipairs(Ext.Entity.GetAllEntitiesWithComponent("PartyMember")) do
        local id = uuidOf(entity)
        if id and not seen[id] and try(Osi.IsOnStage, id) ~= 0 then
            seen[id] = true
            out[#out + 1] = id
        end
    end
    return out
end

-- A party member: PartyMember, and on stage - the stand keeps an off-stage player dummy
-- ("Player") that also carries PartyMember (seen 2026-11-13 by the blind run's game log).
local function isMember(id)
    local entity = entityOf(id)
    if not (entity ~= nil and has(entity, "PartyMember")) then return false end
    local uuid = type(id) == "string" and id or uuidOf(entity)
    return uuid == nil or try(Osi.IsOnStage, uuid) ~= 0
end

-- ---------------------------------------------------------------------------------------
-- Perception

See.EyeHeight = 1.6          -- metres above the feet a character looks from
See.FixtureRange = 30        -- doors and ladders are perceived up to here (with a clear line)
See.CameraRange = 16         -- what the default camera shows around the leader (bg3-agent-harness, measured)

-- Static and dynamic geometry; terrain, scenery, items; not characters and not what one can see
-- through (glass, grates, foliage flagged CanSeeThrough).
local RAY_TYPE = 1 | 4
local RAY_INCLUDE = 0x10 | 0x20 | 0x400 | 0x800 | 0x100000 | 0x1000000
local RAY_EXCLUDE = 0x200 | 0x4000 | 0x20000 | 0x800000

--- Whether nothing solid stands between two points. The target's own body is allowed for (a ray
--- that ends within `slack` metres of the target counts as clear).
--- `target` (an entity) may be given: a ray that hits the target's own body counts as clear
--- (a big object - a tank, a door - is hit on its surface, far from its centre).
function See.Clear(from, to, slack, target)
    local hit = try(Ext.Level.RaycastClosest, from, to, RAY_TYPE, RAY_INCLUDE, RAY_EXCLUDE, 0)
    if not hit then return true end
    local at = hit.Position
    if not at then return true end
    if dist(at, to) <= (slack or 0.6) then return true end
    if target then
        local hitEntity = try(function() return hit.Shape.PhysicsObject.Entity end)
        if hitEntity and target and hitEntity == target then return true end
    end
    return false
end

local function eyeOf(id)
    local p = posOf(id)
    return p and { p[1], p[2] + See.EyeHeight, p[3] } or nil
end

local function centreOf(id, isCharacter)
    local p = posOf(id)
    if not p then return nil end
    return { p[1], p[2] + (isCharacter and 1.0 or 0.4), p[3] }
end

local function sightOf(entity)
    local s = try(function() return entity.Sight end)
    return s and s.Sight or 18, s and s.FOV or 90, s and s.DarkvisionRange or 0
end

--- Whether character `viewer` perceives `target`: the engine's own sight test (field of view,
--- range, light, stealth), or - for objects and for characters that do not hide - a clear line
--- of sight within the viewer's sight range (the player's camera looks around freely).
function See.Perceives(viewer, target, isCharacter)
    if bool(try(Osi.CanSee, viewer, target)) then return true, "sight" end
    local ve, te = entityOf(viewer), entityOf(target)
    if not (ve and te) then return false end
    if isCharacter then
        if bool(try(Osi.IsInvisible, target)) then return false end
        local dark = try(function() return te.Darkness end)
        if dark and (dark.ActuallySneaking or dark.CharacterSneaking) then return false end
    end
    local eye, centre = eyeOf(viewer), centreOf(target, isCharacter)
    if not (eye and centre) then return false end
    local range = sightOf(ve)
    -- big fixtures (doors, ladders) show on screen from further than the eyes' sight range: the
    -- camera looks down on a wider area (bg3-agent-harness used a similar rule for large objects)
    if not isCharacter and (has(te, "Door") or has(te, "IsDoor") or has(te, "Ladder")) then
        range = math.max(range, See.FixtureRange)
    end
    if dist(eye, centre) > range then return false end
    if See.Clear(eye, centre, 0.6, te) then return true, "line" end
    return false
end

--- Whether any party member perceives `target` (GK.See.PartyPerceives(uuid)).
local function partyPerceives(target, isCharacter, members)
    if isCharacter == nil then
        local e = entityOf(target)
        isCharacter = e ~= nil and has(e, "ServerCharacter")
    end
    members = members or See.Members()
    for _, m in ipairs(members) do
        if m == target then return true, "self" end
        if bool(try(Osi.IsDead, m)) == false then
            local yes, how = See.Perceives(m, target, isCharacter)
            if yes then return true, how end
        end
    end
    return false
end
See.PartyPerceives = partyPerceives
See.IsMember = isMember

-- ---------------------------------------------------------------------------------------
-- Characters

--- Relation of a character to the party: "party", "ally", "neutral" or "hostile".
function See.Relation(id, to)
    to = to or host()
    if isMember(id) then return "party" end
    if bool(try(Osi.IsEnemy, id, to)) then return "hostile" end
    if bool(try(Osi.IsAlly, id, to)) then return "ally" end
    return "neutral"
end

-- Statuses a player sees: those the game shows over the portrait or in the Examine window.
local hiddenStatusCache = {}
local function statusShown(id)
    local cached = hiddenStatusCache[id]
    if cached ~= nil then return cached end
    local stat = try(Ext.Stats.Get, id)
    local shown = false
    if stat then
        local flags = try(function() return stat.StatusPropertyFlags end) or {}
        local hidden = false
        for _, f in ipairs(flags) do
            if f == "DisableOverhead" and false then hidden = true end
            if f == "DisablePortraitIndicator" then hidden = true end
        end
        local display = try(function() return stat.DisplayName end)
        shown = not hidden and display ~= nil and display ~= ""
    end
    hiddenStatusCache[id] = shown
    return shown
end

--- A character's statuses: { { id, name, turns } }. `all` includes the technical ones.
function See.Statuses(id, all)
    local entity = entityOf(id)
    local out = {}
    local container = entity and try(function() return entity.StatusContainer.Statuses end)
    if not container then return out end
    for handle, statusId in pairs(container) do
        if all or statusShown(statusId) then
            local status = try(Ext.Entity.Get, handle)
            local row = { id = statusId }
            local stat = try(Ext.Stats.Get, statusId)
            local display = stat and try(function() return stat.DisplayName end)
            if display and display ~= "" then row.name = try(Ext.Loca.GetTranslatedString, display) end
            local turns = status and try(function() return status.ServerStatus.Lifetime end)
            if turns and turns > 0 then row.turns = round(turns / 6, 1) end
            local desc = stat and try(function() return stat.Description end)
            if desc and desc ~= "" then
                local text = try(Ext.Loca.GetTranslatedString, desc)
                if text and text ~= "" then row.description = text:gsub("<[^>]+>", "") end
            end
            out[#out + 1] = row
        end
    end
    table.sort(out, function(a, b) return a.id < b.id end)
    return out
end

local ABILITIES = { "Strength", "Dexterity", "Constitution", "Intelligence", "Wisdom", "Charisma" }

--- Hit points, armour class, abilities, resistances: what the Examine window shows.
local function vitals(entity, out)
    local health = try(function() return entity.Health end)
    if health then
        out.hp, out.maxHp = health.Hp, health.MaxHp
        if health.TemporaryHp and health.TemporaryHp > 0 then out.tempHp = health.TemporaryHp end
    end
    out.ac = try(function() return entity.Resistances.AC end)
    local stats = try(function() return entity.Stats end)
    if stats then
        local abilities = try(function() return stats.Abilities end)
        if abilities then
            out.abilities = {}
            for i, name in ipairs(ABILITIES) do out.abilities[name:sub(1, 3):upper()] = abilities[i + 1] end
        end
    end
    return out
end

local DAMAGE = { "None", "Slashing", "Piercing", "Bludgeoning", "Acid", "Thunder", "Necrotic", "Fire", "Lightning",
                 "Cold", "Psychic", "Poison", "Radiant", "Force" }

--- Resistances as the Examine window lists them: { Resistant = { "Fire" }, Immune = { ... },
--- Vulnerable = { ... } } (nil when there are none).
local function resistancesOf(entity)
    local res = try(function() return Ext.Types.Serialize(entity.Resistances).Resistances end)
    if not res then return nil end
    local out = {}
    for i, flags in ipairs(res) do
        local damage = DAMAGE[i] or tostring(i)
        -- flags come as a list of names, or as "ResistanceBoostFlags(ResistantToMagical, ...)"
        local text = type(flags) == "table" and table.concat(flags, ",") or tostring(flags)
        for kind in text:gmatch("(%a+)To%a*") do
            out[kind] = out[kind] or {}
            table.insert(out[kind], damage)
        end
    end
    return next(out) and out or nil
end

--- The passives the Examine window shows (not those flagged IsHidden): { { id, name } }.
local function passivesOf(entity)
    local out = {}
    for _, p in ipairs(try(function() return entity.PassiveContainer.Passives end) or {}) do
        local id = try(function() return p.Passive.PassiveId end)
        local stat = id and try(Ext.Stats.Get, id)
        if stat then
            local hidden = false
            for _, f in ipairs(try(function() return stat.Properties end) or {}) do
                if f == "IsHidden" then hidden = true end
            end
            local name = try(function() return Ext.Loca.GetTranslatedString(stat.DisplayName) end)
            if not hidden and name and name ~= "" then out[#out + 1] = { id = id, name = name } end
        end
    end
    return out
end
See.Passives = function(id) return passivesOf(entityOf(id)) end

local function canTalk(id)
    if bool(try(Osi.IsDialogueBlocked, id)) then return false end
    local dialog = try(Osi.SpeakerGetDialog, id, 0)
    if dialog then return true end
    return bool(try(Osi.GetHasOsirisDialog, id))
end

--- One character row.
local function characterRow(entity, id, from, options)
    local p = posOf(entity)
    local row = {
        id = id, name = nameOf(entity), rel = See.Relation(id),
        d = p and from and round(dist(p, from)) or nil, dir = p and from and compass(from, p) or nil,
        pos = rpos(p),
    }
    vitals(entity, row)
    row.ac = nil; row.abilities = nil   -- the full sheet is GK.See.Entity's
    if bool(try(Osi.IsDead, id)) then row.dead = true end
    if not row.dead and row.hp == 0 then row.downed = true end
    if bool(try(Osi.IsInCombat, id)) then row.inCombat = true end
    local statuses = See.Statuses(id, options.all and options.technical)
    if #statuses > 0 then
        local names = {}
        for _, s in ipairs(statuses) do names[#names + 1] = s.name or s.id end
        row.statuses = names
    end
    if row.rel ~= "party" and not row.dead and row.rel ~= "hostile" then
        if canTalk(id) then row.talk = true end
        if has(entity, "Trader") or has(entity, "CanTrade") then row.trade = true end
    end
    if row.dead and bool(try(function() return entity.CanBeLooted ~= nil end)) then row.loot = true end
    return row
end

-- ---------------------------------------------------------------------------------------
-- Objects

-- What a use action is called in the rows ("Door", "Ladder", "Book" ...): the engine class name
-- without its namespace and suffix.
local function useKinds(entity)
    local actions = try(function() return entity.UseAction.UseActions end)
    local kinds = {}
    if actions then
        for _, a in ipairs(actions) do
            local name = tostring(Ext.Types.GetObjectType(a) or ""):gsub("^.*::", ""):gsub("ActionData$", ""):gsub("Action$", "")
            kinds[#kinds + 1] = name
        end
    end
    return kinds
end

local KIND_OF_USE = {
    Door = "door", Ladder = "ladder", Book = "book", Consume = "consumable", Teleport = "portal",
    Lockpick = "tool", Disarm = "tool", Learn = "scroll", Recipe = "recipe", Throw = "item",
    OpenClose = "door", Insert = "socket", StoryUse = "usable", StoryUseInInventory = "usable",
    SitOrLie = "seat", Lying = "bed", Sit = "seat", Combine = "item", Equip = "gear",
    SpellBook = "book", Unlock = "key", CreateSurface = "usable", CreatePuddle = "usable",
    ShowStoryElementUI = "usable", Destroy = "usable", Summon = "usable", Constrain = "usable",
}

--- What an item is to a player: door, container, ladder, portal, usable (anything that is used
--- where it stands: levers, buttons, pods), gear (equipment), consumable, book, key, valuable, item; nil = scenery.
local function classify(entity, id)
    if has(entity, "Door") or has(entity, "IsDoor") then return "door" end
    local uses = useKinds(entity)
    for _, u in ipairs(uses) do
        if u == "Door" then return "door" end
    end
    for _, u in ipairs(uses) do
        if u == "Ladder" then return "ladder" end
        if u == "Teleport" then return "portal" end
    end
    if bool(try(Osi.IsContainer, id)) and not bool(try(Osi.GetCanPickUp, id)) then return "container" end
    if bool(try(Osi.IsContainer, id)) then return "container" end
    local pickable = bool(try(Osi.GetCanPickUp, id))
    if pickable then
        for _, u in ipairs(uses) do
            local k = KIND_OF_USE[u]
            if k and k ~= "usable" and k ~= "item" then return k end
        end
        if has(entity, "Equipable") then return "gear" end
        if bool(try(Osi.IsGold, id)) then return "gold" end
        return "item"
    end
    if #uses > 0 and bool(try(Osi.GetCanInteract, id)) then return "usable" end
    return nil
end
See.Classify = classify

local function itemRow(entity, id, kind, from)
    local p = posOf(entity)
    local row = {
        id = id, name = nameOf(entity), kind = kind,
        d = p and from and round(dist(p, from)) or nil, dir = p and from and compass(from, p) or nil,
        pos = rpos(p),
    }
    if p and from and math.abs(p[2] - from[2]) >= 1.5 then row.dy = round(p[2] - from[2]) end
    if kind == "door" or kind == "container" then
        if bool(try(Osi.IsLocked, id)) then row.locked = true end
        if kind == "door" then row.open = bool(try(Osi.IsOpened, id)) end
    end
    if bool(try(Osi.IsTrap, id)) and bool(try(Osi.IsTrapDiscovered, id)) then
        row.trap = bool(try(Osi.IsTrapArmed, id)) and "armed" or "disarmed"
    end
    local owner = try(function() return entity.ServerItem.Owner end)
    if owner and uuidOf(owner) and not isMember(uuidOf(owner)) then row.owned = true end
    local value = try(Osi.ItemGetGoldValue, id)
    if value and value > 1 and kind ~= "door" then row.value = value end
    return row
end

-- ---------------------------------------------------------------------------------------
-- Scope: how much the reader may know

--- Readers take `scope`:
---   "seen"   (default) what the party perceives right now - a player looking at the screen;
---   "known"  that, plus what the party saw earlier on this level and does not see now, with
---            its last known place (`remembered = true`, `ago` seconds) - a player's memory;
---   "all"    everything on the level within the radius, perceived or not (`perceived = false`
---            on what the party would not know): omniscient, for tests and debugging only.
--- `all = true` is the same as scope "all".
See.Scopes = { seen = "what the party perceives now", known = "seen now or remembered from earlier",
               all = "omniscient: everything, for tests and debugging" }
See.Memory = See.Memory or {}   -- [level] = { [uuid] = { row, t, character } }

function See.Scope(options)
    options = options or {}
    if options.all or options.scope == "all" then
        options.all, options.scope = true, "all"
    else
        options.scope = options.scope or "seen"
        if not See.Scopes[options.scope] then error("scope: seen, known or all") end
    end
    return options
end

--- Whether the party saw an object earlier (it is in the memory of the current level).
function See.Remembers(id)
    for _, memory in pairs(See.Memory) do
        if memory[id] then return true end
    end
    return false
end

--- Forgets what the party saw (all levels, or one).
function See.Forget(level)
    if level then See.Memory[level] = nil else See.Memory = {} end
    return true
end

-- ---------------------------------------------------------------------------------------
-- Around

local function viewpoint(options)
    local members = options.who and { options.who } or See.Members()
    local at = posOf(options.who or host())
    return members, at
end

local function onLevel(entity, level)
    local l = try(function() return entity.Level.LevelName end)
    return l == nil or l == level
end

--- Characters and objects near the party.
---   options: radius (25), who, all, limit (40 per list), kinds = { "door", "container", ... }
---            (object kinds to keep), characters = false / objects = false (skip a list),
---            scenery = true (also items that do nothing)
--- Returns { at = {x,y,z}, level, characters = {...}, objects = {...} }, nearest first.
---   character rows: { id, name, rel, d, dir, pos, hp, maxHp, statuses, dead, downed, inCombat,
---                     talk, trade, loot }
---   object rows:    { id, name, kind, d, dir, dy, pos, locked, open, trap, owned, value }
function See.Around(options)
    options = See.Scope(options)
    local radius = options.radius or 25
    local members, at = viewpoint(options)
    local level = try(function() return Ext.Entity.Get(host()).Level.LevelName end)
    local wanted
    if options.kinds then
        wanted = {}
        for _, k in ipairs(options.kinds) do wanted[k] = true end
    end
    local out = { at = rpos(at), level = level, from = nameOf(options.who or host()), characters = {}, objects = {} }
    if not at then return out end

    -- kinds = { ... } filters objects; characters are kept only when "character" is among them
    if wanted and not wanted.character and not wanted.characters then options.characters = false end
    if options.characters ~= false then
        for _, entity in ipairs(Ext.Entity.GetAllEntitiesWithComponent("ServerCharacter")) do
            local id = uuidOf(entity)
            local p = id and posOf(entity)
            if p and flat(p, at) <= radius and onLevel(entity, level) and id ~= (options.who or host()) then
                local onStage = bool(try(Osi.IsOnStage, id))
                local hidden = not onStage or bool(try(Osi.IsInvisibleByScript, id))
                local perceived = not hidden and partyPerceives(id, true, members)
                if perceived or isMember(id) or options.all then
                    local row = characterRow(entity, id, at, options)
                    if options.all and not perceived and not isMember(id) then row.perceived = false end
                    if options.all and not onStage then row.offstage = true end
                    out.characters[#out.characters + 1] = row
                end
            end
        end
    end

    if options.objects ~= false then
        for _, entity in ipairs(Ext.Entity.GetAllEntitiesWithComponent("ServerItem")) do
            local id = uuidOf(entity)
            local p = id and posOf(entity)
            if p and flat(p, at) <= radius and not has(entity, "InventoryMember") and onLevel(entity, level) then
                local kind = classify(entity, id)
                if kind == nil and options.scenery then kind = "scenery" end
                if kind and (not wanted or wanted[kind]) then
                    local onStage = bool(try(Osi.IsOnStage, id))
                    local hidden = not onStage or bool(try(Osi.IsInvisible, id))
                        or (bool(try(Osi.IsTrap, id)) and not bool(try(Osi.IsTrapDiscovered, id)))
                    local perceived = not hidden and partyPerceives(id, false, members)
                    if perceived or options.all then
                        local row = itemRow(entity, id, kind, at)
                        if options.all and not perceived then row.perceived = false end
                        out.objects[#out.objects + 1] = row
                    end
                end
            end
        end
    end

    -- Memory: what the party perceives now is remembered; with scope "known" what it saw before
    -- (and does not perceive now) comes back with its last known place and how long ago.
    local now = Ext.Utils.MonotonicTime() / 1000
    local memory = See.Memory[level or "?"] or {}
    See.Memory[level or "?"] = memory
    local listed = {}
    for _, list in ipairs({ out.characters, out.objects }) do
        for _, row in ipairs(list) do
            listed[row.id] = true
            if row.perceived ~= false and row.rel ~= "party" then
                memory[row.id] = { row = row, t = now, character = list == out.characters }
            end
        end
    end
    if options.scope == "known" then
        for id, m in pairs(memory) do
            if not listed[id] and m.row.pos and flat(m.row.pos, at) <= radius * 2 and not isMember(id)
                and (not wanted or (m.character and wanted.character) or (not m.character and wanted[m.row.kind])) then
                local row = {}
                for k, v in pairs(m.row) do row[k] = v end
                row.remembered = true
                row.ago = math.floor(now - m.t)
                row.d = round(dist(row.pos, at))
                row.dir = compass(at, row.pos)
                -- what is known about it now: gone (removed), taken (in somebody's inventory now:
                -- the party took it, so it is not remembered as lying there)
                local still = Ext.Entity.Get(id)
                if not still then row.gone = true end
                if not (still and has(still, "InventoryMember")) then
                    table.insert(m.character and out.characters or out.objects, row)
                end
            end
        end
    end

    local function nearest(a, b) return (a.d or 1e9) < (b.d or 1e9) end
    table.sort(out.characters, nearest)
    table.sort(out.objects, nearest)
    local limit = options.limit or 40
    for _, list in ipairs({ out.characters, out.objects }) do
        for i = #list, limit + 1, -1 do list[i] = nil end
    end
    return out
end

-- ---------------------------------------------------------------------------------------
-- One entity in full

--- One character or object, as the Examine window shows it (and a little more for the party:
--- resources, spell slots, experience). Refused for something the party does not perceive,
--- unless options.all.
function See.Entity(id, options)
    options = options or {}
    local entity = entityOf(id)
    if not entity then return { error = "no such object" } end
    local isCharacter = has(entity, "ServerCharacter")
    local members, at = viewpoint(options)
    -- items the party carries can always be examined (open the inventory, hover)
    local owner = not isCharacter and try(Osi.GetInventoryOwner, id)
    owner = type(owner) == "string" and (owner:match("(%x+%-%x+%-%x+%-%x+%-%x+)$") or owner) or nil
    local carried = owner and isMember(owner)
    if not options.all and not isMember(id) and not carried and not partyPerceives(id, isCharacter, members) then
        return { error = "not perceived by the party" }
    end
    local out
    if isCharacter then
        out = characterRow(entity, id, at, options)
        vitals(entity, out)
        out.level = try(function() return entity.EocLevel.Level end)
        out.resist = resistancesOf(entity)
        out.passives = passivesOf(entity)
        local bonuses = See.Bonuses and See.Bonuses(id) or {}
        out.initiative = bonuses.initiative
        out.proficiency = bonuses.proficiency
        out.sight = try(function() return entity.Sight.Sight end)
        out.darkvision = try(function() return entity.Sight.DarkvisionRange end)
        local cp = try(function() return entity.CombatParticipant.CombatHandle end)
        if cp then out.initiativeRoll = try(function() return entity.CombatParticipant.InitiativeRoll end) end
        if isMember(id) then out.skills = bonuses.skills end
        out.size = try(function() return tostring(entity.ObjectSize.Size) end)
        out.statuses = See.Statuses(id, options.all and options.technical)
        out.race = try(function() return Ext.StaticData.Get(entity.Race.Race, "Race").DisplayName:Get() end)
        local classes = try(function() return entity.Classes.Classes end)
        if classes then
            out.classes = {}
            for _, c in ipairs(classes) do
                local def = try(Ext.StaticData.Get, c.ClassUUID, "ClassDescription")
                out.classes[#out.classes + 1] = { name = def and try(function() return def.DisplayName:Get() end) or def and def.Name,
                                                  level = c.Level }
            end
        end
        local conc = try(function() return entity.Concentration.SpellId.Prototype end)
        if conc and conc ~= "" then out.concentration = conc end
        local _, facing = facingOf(entity)
        out.facing = facing and round(facingOf(entity), 1) or nil
        if isMember(id) or options.all then out.resources = See.Resources(id) end
        local weapon = try(Osi.GetEquippedWeapon, id)
        if weapon then out.weapon = nameOf(weapon) end
        -- both weapon sets (switching between them is free in a fight)
        local melee = try(Osi.GetEquippedItem, id, "Melee Main Weapon")
        local offhand = try(Osi.GetEquippedItem, id, "Melee Offhand Weapon")
        local ranged = try(Osi.GetEquippedItem, id, "Ranged Main Weapon")
        out.weapons = { melee = melee and nameOf(melee) or "none", offhand = offhand and nameOf(offhand) or nil,
                        ranged = ranged and nameOf(ranged) or "none" }
    else
        out = itemRow(entity, id, classify(entity, id) or "scenery", at)
        out.uses = useKinds(entity)
        -- the tooltip's text: the description of the item's template (or its root template)
        local function translated(ts)
            local h = ts and try(function() return ts.Handle.Handle end)
            if h and h ~= "" and not h:find("Unknown") then return try(Ext.Loca.GetTranslatedString, h) end
        end
        local t = try(function() return entity.ServerItem.Template end)
        local d = t and translated(try(function() return t.Description end))
        if not d and t then
            local root = try(Ext.Template.GetRootTemplate, t.TemplateName)
            d = root and translated(try(function() return root.Description end))
        end
        if d and d ~= "" then out.description = d end
        -- what "use" does, in plain words
        local verbs = { Door = "open / close", OpenClose = "open / close", Ladder = "climb", Book = "read", Consume = "drink or eat",
                        Teleport = "travel", Lockpick = "pick a lock with it", Disarm = "disarm a trap with it",
                        Learn = "learn (a scroll)", Recipe = "learn a recipe", StoryUse = "interact (the story decides)",
                        SitOrLie = "sit or lie", Sit = "sit", Lying = "lie down", Insert = "put something in",
                        Combine = "combine", Equip = "equip", Unlock = "unlock with it", Throw = "throw" }
        local does = {}
        for _, u in ipairs(out.uses) do does[#does + 1] = verbs[u] or u end
        if out.kind == "container" then does[#does + 1] = "open and look inside (loot window)" end
        if #does > 0 then out.use = table.concat(does, "; ") end
        out.canPickUp = bool(try(Osi.GetCanPickUp, id))
        out.weight = try(function() return entity.Data.Weight end)
    end
    return out
end

-- ---------------------------------------------------------------------------------------
-- Resources and actions

--- A character's action resources: { action, bonus, reaction, movement, slots = { [level] = n },
--- other = { name = n } }; each value is { left, max }.
function See.Resources(id)
    local entity = entityOf(id)
    local res = entity and try(function() return Ext.Types.Serialize(entity.ActionResources).Resources end)
    local out = { slots = {}, other = {} }
    if not res then return out end
    for key, list in pairs(res) do
        local def = try(Ext.StaticData.Get, key, "ActionResource")
        local name = def and def.Name or key
        for _, v in ipairs(list) do
            local pair = { round(v.Amount, 0.1), round(v.MaxAmount, 0.1) }
            if name == "ActionPoint" then out.action = pair
            elseif name == "BonusActionPoint" then out.bonus = pair
            elseif name == "ReactionActionPoint" then out.reaction = pair
            elseif name == "Movement" then out.movement = pair
            elseif name == "SpellSlot" or name == "WarlockSpellSlot" then
                out.slots[v.ResourceId or (#out.slots + 1)] = pair
            else
                out.other[name] = pair
            end
        end
    end
    if next(out.slots) == nil then out.slots = nil end
    if next(out.other) == nil then out.other = nil end
    return out
end

-- Costs from a spell's UseCosts string: "ActionPoint:1;SpellSlotsGroup:1:1:2" ...
local function costsOf(stat)
    local text = try(function() return stat.UseCosts end) or ""
    local out = {}
    for part in tostring(text):gmatch("[^;]+") do
        local name, rest = part:match("^%s*([%w_]+):?(.*)$")
        if name then
            local n = tonumber(rest:match("^([%d%.]+)")) or 1
            if name == "ActionPoint" then out.action = n
            elseif name == "BonusActionPoint" then out.bonus = n
            elseif name == "ReactionActionPoint" then out.reaction = n
            elseif name == "Movement" then out.movement = n
            elseif name == "SpellSlotsGroup" or name == "SpellSlot" then
                local _, _, level = rest:match("^(%d+):(%d+):(%d+)")
                out.slot = tonumber(level) or tonumber(rest:match("(%d+)$")) or 1
            else
                out[name] = n
            end
        end
    end
    return out
end

local function rangeOf(stat, id)
    local raw = try(function() return stat.TargetRadius end) or try(function() return stat.Range end)
    local v = tonumber(raw)
    if v then return v end
    if raw == "MeleeMainWeaponRange" or raw == "MeleeOffHandWeaponRange" then return 1.5 end
    if raw == "RangedMainWeaponRange" or raw == "RangedOffHandWeaponRange" then
        local weapon = try(Osi.GetEquippedItem, id, "Ranged Main Weapon")
        local wstat = weapon and try(function() return Ext.Stats.Get(Ext.Entity.Get(weapon).Data.StatsId) end)
        local r = wstat and tonumber(try(function() return wstat["Long Range"] or wstat.WeaponRange end))
        return r and r / 100 or 18
    end
    return raw
end

local function flagsOf(stat, field)
    local list = try(function() return stat[field] end) or {}
    local set = {}
    for _, f in ipairs(list) do set[f] = true end
    return set
end

--- What a character can do now: { id, name, level, type, costs, range, area, concentration,
--- ready, why, target = { inRange, inSight, d } }. Spells come from its spell book (the hotbar
--- shows the same ones). `target` (uuid or {x,y,z}) adds per-row whether it can be reached.
--- options: all (also spells that are not ready), filter (part of the id or name).
function See.Actions(who, target, options)
    options = options or {}
    who = who or host()
    local entity = entityOf(who)
    local book = entity and try(function() return Ext.Types.Serialize(entity.SpellBook).Spells end)
    if not book then return {} end
    local res = See.Resources(who)
    local cooldowns = {}
    for _, c in ipairs(try(function() return Ext.Types.Serialize(entity.SpellBookCooldowns).Cooldowns end) or {}) do
        local sid = c.SpellId and c.SpellId.Prototype
        if sid then cooldowns[sid] = tostring(c.CooldownType or "") end
    end
    local inCombat = bool(try(Osi.IsInCombat, who))
    local tpos = type(target) == "table" and target or (target and posOf(target))
    local mypos = posOf(who)
    local out, seen = {}, {}
    for _, spell in ipairs(book) do
        local sid = spell.Id and spell.Id.Prototype
        if sid and not seen[sid] then
            seen[sid] = true
            local stat = try(Ext.Stats.Get, sid)
            if stat then
                local row = { id = sid, name = try(function() return Ext.Loca.GetTranslatedString(stat.DisplayName) end),
                              level = try(function() return stat.Level end), type = try(function() return stat.SpellType end) }
                if row.name == "" then row.name = nil end
                row.costs = costsOf(stat)
                row.range = rangeOf(stat, who)
                local area = tonumber(try(function() return stat.AreaRadius end))
                if area and area > 0 then row.area = area end
                local sflags = flagsOf(stat, "SpellFlags")
                if sflags.IsConcentration then row.concentration = true end
                -- the conditions the game checks (its tooltip names them): the agent can read them
                -- before trying; GK.Act reports the game's refusal when one is not met
                local req = try(function() return stat.RequirementConditions end)
                local tc = try(function() return stat.TargetConditions end)
                if req and req ~= "" then row.requires = req end
                if options.conditions and tc and tc ~= "" then row.targetConditions = tc end
                local why
                local c = row.costs
                local function short(kind, pair) return c[kind] and pair and pair[1] < c[kind] end
                if inCombat then
                    if short("action", res.action) then why = "no action left"
                    elseif short("bonus", res.bonus) then why = "no bonus action left"
                    elseif short("reaction", res.reaction) then why = "no reaction left" end
                end
                if not why and c.slot then
                    local ok = false
                    for level, pair in pairs(res.slots or {}) do
                        if level >= c.slot and pair[1] >= 1 then ok = true end
                    end
                    if not ok then why = "no spell slot of level " .. c.slot .. " or higher" end
                end
                if not why and cooldowns[sid] then
                    local kind = cooldowns[sid]
                    local recharge = kind:find("ShortRest") and "recharges on a short rest"
                        or kind:find("Rest") and "recharges on a long rest"
                        or kind:find("Turn") and "recharges next turn" or "recharging"
                    why = "used: " .. recharge
                end
                row.ready = why == nil
                row.why = why
                if tpos and mypos then
                    local d = dist(mypos, tpos)
                    local t = { d = round(d) }
                    if type(row.range) == "number" then t.inRange = d <= row.range + 0.5 end
                    if type(target) == "string" then
                        t.inSight = bool(try(Osi.HasLineOfSight, who, target))
                    else
                        t.inSight = See.Clear(eyeOf(who), { tpos[1], tpos[2] + 0.5, tpos[3] })
                    end
                    -- the game's own conditions for this spell and target (GK.Conditions):
                    -- false = the game would refuse, with the part that fails
                    if type(target) == "string" and GK.Conditions then
                        local check = GK.Conditions.Check(who, sid, target)
                        if check.ok == false then t.blocked = check.failed end
                    end
                    if type(target) == "string" and has(entityOf(target) or {}, "ServerCharacter") then
                        local odds = See.Odds(who, sid, target)
                        if odds.kind ~= "none" then
                            t.chance = odds.chance
                            t.expected = odds.expected
                            t.damage = odds.damage and odds.damage.dice
                        end
                    end
                    row.target = t
                end
                local f = options.filter
                if (options.all or row.ready) and (not f or sid:find(f, 1, true) or (row.name or ""):find(f, 1, true)) then
                    out[#out + 1] = row
                end
            end
        end
    end
    table.sort(out, function(a, b)
        if (a.level or 0) ~= (b.level or 0) then return (a.level or 0) < (b.level or 0) end
        return a.id < b.id
    end)
    return out
end

-- ---------------------------------------------------------------------------------------
-- Odds: what a roll is likely to give, before rolling

See.Skills = { "Deception", "Intimidation", "Performance", "Persuasion", "Acrobatics", "SleightOfHand", "Stealth",
               "Arcana", "History", "Investigation", "Nature", "Religion", "Athletics", "AnimalHandling", "Insight",
               "Medicine", "Perception", "Survival" }
local ABBR = { "STR", "DEX", "CON", "INT", "WIS", "CHA" }
local ABILITY_OF = { Strength = "STR", Dexterity = "DEX", Constitution = "CON", Intelligence = "INT", Wisdom = "WIS",
                     Charisma = "CHA" }
See.AbilityOf = ABILITY_OF

--- Chance that d20 + bonus reaches dc (advantage = 1, disadvantage = -1); a natural 1 and 20
--- always miss and hit.
function See.Chance(bonus, dc, advantage)
    local p = (21 - (dc - bonus)) / 20
    p = math.max(0.05, math.min(0.95, p))
    if advantage == 1 then p = 1 - (1 - p) ^ 2 elseif advantage == -1 then p = p * p end
    return round(p, 0.01)
end

--- A character's check bonuses: { abilities = { STR = mod ... }, skills = { Stealth = total ... },
--- proficiency, initiative, spellAbility, spellDC, spellAttack }. Skill totals are the engine's
--- own (ability modifier + proficiency + expertise), from the Stats component.
function See.Bonuses(id)
    local entity = entityOf(id)
    local stats = entity and try(function() return Ext.Types.Serialize(entity.Stats) end)
    if not stats then return {} end
    local out = { abilities = {}, skills = {}, proficiency = stats.ProficiencyBonus, initiative = stats.InitiativeBonus }
    for i, abbr in ipairs(ABBR) do out.abilities[abbr] = (stats.AbilityModifiers or {})[i + 1] end
    for i, name in ipairs(See.Skills) do out.skills[name] = (stats.Skills or {})[i] end
    local castAbility = ABILITY_OF[stats.SpellCastingAbility or ""]
    if castAbility then
        out.spellAbility = castAbility
        out.spellAttack = (out.abilities[castAbility] or 0) + (out.proficiency or 0)
        out.spellDC = 8 + out.spellAttack
    end
    return out
end

-- Names the game shows on check tags and roll windows -> the key in See.Bonuses ("Stealth",
-- "DEX"). Language-independent: the English names, plus every translation handle the game uses
-- for a skill or ability name (taken from the game's English localisation by the offline index,
-- 2026-11-13), translated at first use into whatever language the game runs in.
See.CheckHandles = {
    Acrobatics = { "h65e11968gf9b6g4c47g9efcg03fb60fb923c_4", "hf18eb9a0g33a1g481fg9646g5ddeaa7e2156" },
    AnimalHandling = { "h65e11968gf9b6g4c47g9efcg03fb60fb923c_13", "hab871a76g3541g4045ga1c1gbb9b5d13ee36" },
    Arcana = { "h22b7e6b6g8c55g49c6gba43gd16c8e399fea", "h65e11968gf9b6g4c47g9efcg03fb60fb923c_7" },
    Athletics = { "h06f7e4d9g1d7eg42aagb745g2e08ebeb9de2", "h65e11968gf9b6g4c47g9efcg03fb60fb923c_12" },
    CHA = { "h0122e500g1839g48daga1d5gf7bf51e74043", "h6658a3ccg94d7g459ag95e2g2acf2d4e021f", "h6c83537fg6358g41e0g8a18g32cc8316ced2_6", "habf9df38g5018g44c4gbb6fga77177a64218", "hec30ce0dgb76bg45cagaf40gad771ff7902b_6" },
    CON = { "h6c83537fg6358g41e0g8a18g32cc8316ced2_3", "hcd19f46ag85bcg41f2gb8fbg1dc69843d250", "hec30ce0dgb76bg45cagaf40gad771ff7902b_3" },
    DEX = { "h6c83537fg6358g41e0g8a18g32cc8316ced2_2", "hc30ef867gedecg4c22ga387g648ea8224248", "hd22eaf13g9cc3g41d1g932fga2d3e40c23ed", "hec30ce0dgb76bg45cagaf40gad771ff7902b_2" },
    Deception = { "h65e11968gf9b6g4c47g9efcg03fb60fb923c_0", "hcaeb4d5fg8b57g4520g912fg4fae8267b844" },
    History = { "h65e11968gf9b6g4c47g9efcg03fb60fb923c_8", "hab026295g69b7g4013g9b14g807992eb1a22" },
    INT = { "h33d41553g12cag401eg8c71g640d3d654054", "h6c83537fg6358g41e0g8a18g32cc8316ced2_4", "hec30ce0dgb76bg45cagaf40gad771ff7902b_4" },
    Insight = { "h64f8e22agf0eeg4a0bga9abgb0178dd5e670", "h65e11968gf9b6g4c47g9efcg03fb60fb923c_14" },
    Intimidation = { "h65e11968gf9b6g4c47g9efcg03fb60fb923c_1", "h6efd5166g9073g439fgb814gcd9186cb8e61" },
    Investigation = { "h65e11968gf9b6g4c47g9efcg03fb60fb923c_9", "hfd00683fg1522g4f04g91c4g9bef52cda3d0" },
    Medicine = { "h23dfa632g875dg4081ga2abg950b7f7adc13", "h65e11968gf9b6g4c47g9efcg03fb60fb923c_15" },
    Nature = { "h4bd4988bgc309g4e04g8127g3e3d1cef4082", "h63548a6ag0bc9g4e87g8d62gd6e7b8771333", "h65e11968gf9b6g4c47g9efcg03fb60fb923c_10" },
    Perception = { "h153612e7g6e83g48fbg855ag7088e7eea89e", "h65e11968gf9b6g4c47g9efcg03fb60fb923c_16" },
    Performance = { "h319cdae5ge254g4ea3gb8c8gaa333351d74c", "h57b57678g7336g443bgbaa5g6b63d0bd2cec", "h65e11968gf9b6g4c47g9efcg03fb60fb923c_2", "h7907ceb0g2af1g4ad4g82aeg21d65a3b0332", "ha2cf03c5g78cdg4afaga215g84a57ad94c14", "hc632dc67g7806g436fg9a73gb80b4d6f5e7c" },
    Persuasion = { "h257372d3g6f98g4450g813bg190e19aecce4", "h65e11968gf9b6g4c47g9efcg03fb60fb923c_3" },
    Religion = { "h65e11968gf9b6g4c47g9efcg03fb60fb923c_11", "h9eab2802gb592g4c23g87b9g165135e307a1" },
    STR = { "h5f4b4474gec8fg4135g80abg4585450c6900", "h6c83537fg6358g41e0g8a18g32cc8316ced2_1", "hc8c67074g3c19g44d1g8b7bg9e5a8d06d87f", "hec30ce0dgb76bg45cagaf40gad771ff7902b_1" },
    SleightOfHand = { "hab6103cdg3d5dg4a85ga0b4gbd3f45024a7a", "hba2e587ag00b7g4f75ga38bg8754bcaa7b6b" },
    Stealth = { "h49c7514bg504dg4445g915cgb9562d8585ac", "h65e11968gf9b6g4c47g9efcg03fb60fb923c_6" },
    Survival = { "h65e11968gf9b6g4c47g9efcg03fb60fb923c_17", "ha479d52eg60c5g4f9eg88ddg43e201dae0d3" },
    WIS = { "h6c83537fg6358g41e0g8a18g32cc8316ced2_5", "h89caaa39g8d68g420fg9697g2bc52f81aa74", "hec30ce0dgb76bg45cagaf40gad771ff7902b_5", "hfb735304g4b35g49e2gbabeg05b183cb6de1" },
}
local ENGLISH = { ["Sleight of Hand"] = "SleightOfHand", ["Animal Handling"] = "AnimalHandling", Strength = "STR",
                  Dexterity = "DEX", Constitution = "CON", Intelligence = "INT", Wisdom = "WIS", Charisma = "CHA" }
local CHECK_NAMES
local function checkKey(name)
    if not name then return nil end
    if ABILITY_OF[name] then return ABILITY_OF[name] end
    for _, s in ipairs(See.Skills) do if s == name then return s end end
    if not CHECK_NAMES then
        CHECK_NAMES = {}
        for english, key in pairs(ENGLISH) do CHECK_NAMES[english:lower()] = key end
        for _, skill in ipairs(See.Skills) do CHECK_NAMES[skill:lower()] = skill end
        for key, list in pairs(See.CheckHandles) do
            for _, handle in ipairs(list) do
                local text = try(Ext.Loca.GetTranslatedString, handle)
                if text and text ~= "" then CHECK_NAMES[text:lower()] = key end
            end
        end
    end
    return CHECK_NAMES[name:lower()]
end
See.CheckKey = checkKey

--- For every party member: the bonus for a check ("Stealth", "DEX", or the game's own name such
--- as "Sleight of Hand" in any language the game runs in), and with a DC the chance to pass - best first.
function See.WhoBest(check, dc)
    local key = checkKey(check) or check
    local out = {}
    for _, m in ipairs(See.Members()) do
        local b = See.Bonuses(m)
        local bonus = (b.skills or {})[key] or (b.abilities or {})[key]
        if bonus then
            out[#out + 1] = { id = m, name = nameOf(m), bonus = bonus, chance = dc and See.Chance(bonus, dc) or nil }
        end
    end
    table.sort(out, function(a, b) return a.bonus > b.bonus end)
    return out
end

--- Odds of the checks in a dialogue's answers (tags as the dialogue shows them, e.g.
--- { "Dexterity" }, localised as the game shows them): for each tag the speaker's bonus and the best party member's.
function See.CheckOdds(tags, speaker, dc)
    speaker = speaker or host()
    local out = {}
    for _, tag in ipairs(tags or {}) do
        local key = checkKey(tag)
        if key then
            local b = See.Bonuses(speaker)
            local mine = (b.skills or {})[key] or (b.abilities or {})[key]
            local best = See.WhoBest(key, dc)[1]
            local isAbility = key:match("^%u%u%u$") ~= nil
            local m = See.RollMods(speaker, isAbility and "ability" or "skill", key)
            out[#out + 1] = { check = tag, key = key, bonus = mine, advantage = m.advantage ~= 0 and m.advantage or nil,
                              extra = #m.dice > 0 and table.concat(m.dice, "+") or nil,
                              chance = dc and mine and See.Chance(mine + (m.average or 0), dc, m.advantage) or nil,
                              best = best and best.id ~= speaker and { name = best.name, bonus = best.bonus } or nil }
        end
    end
    return out
end

-- Boosts that change a roll, read from the BoostsContainer the engine keeps on every character
-- (statuses, passives, items and spells all end up there): RollBonus (Bless and Guidance add a
-- die) and Advantage / Disadvantage of a kind.
-- BoostsContainer.Boosts is a list of { Type = "RollBonus", Boosts = { boost entities } }
-- (checked live 2026-11-13; not a map keyed by type).
local function boostEntities(entity, kind)
    local out = {}
    local list = try(function() return entity.BoostsContainer.Boosts end)
    if not list then return out end
    for _, entry in pairs(list) do
        local t = try(function() return entry.Type end)
        if tostring(t) == kind then
            for _, b in pairs(try(function() return entry.Boosts end) or {}) do out[#out + 1] = b end
        end
    end
    return out
end

local function amountText(v)
    if v == nil then return nil end
    if type(v) == "string" then return v end
    local code = try(function() return v.Code end)
    if code then return code end
    return tostring(v)
end

-- Which RollBonus roll types count for a roll: attack rolls by kind, saving throws, checks.
local ATTACK_ROLLS = { Attack = true, MeleeWeaponAttack = true, RangedWeaponAttack = true, MeleeSpellAttack = true,
                       RangedSpellAttack = true, MeleeUnarmedAttack = true, RangedUnarmedAttack = true,
                       MeleeOffHandWeaponAttack = true, RangedOffHandWeaponAttack = true }

--- What changes a roll of `who`: { advantage = 1 | 0 | -1, dice = { "1d4" }, average, sources = {...} }.
--- kind: "attack" (attackType: "MeleeWeaponAttack", "RangedSpellAttack" ...), "save" (ability
--- "DEX"...), "skill" (skill name), "ability" (ability). `target`: for an attack, the target's
--- boosts that give its attackers advantage or disadvantage (AttackTarget). Advantage and
--- disadvantage cancel out, as in the rules.
function See.RollMods(who, kind, what, target)
    local e = entityOf(who)
    local out = { advantage = 0, dice = {}, average = 0, sources = {} }
    if not e then return out end
    local adv, dis = false, false
    local abilityName
    for name, abbr in pairs(ABILITY_OF) do if abbr == what then abilityName = name end end
    for _, b in ipairs(boostEntities(e, "RollBonus")) do
        local r = try(function() return b.RollBonusBoost end)
        if r then
            local rt = tostring(r.RollType)
            local counts = (kind == "attack" and (ATTACK_ROLLS[rt] and (rt == "Attack" or rt == what)))
                or (kind == "save" and rt == "SavingThrow" and (tostring(r.Ability) == abilityName or tostring(r.Ability) == "None"))
                or (kind == "skill" and (rt == "SkillCheck" or rt == "Skill") and (tostring(r.Skill) == what or tostring(r.Skill) == "Invalid"))
                or ((kind == "ability" or kind == "skill") and rt == "RawAbility")
            if counts then
                local amount = amountText(r.Amount)
                if amount then
                    out.dice[#out.dice + 1] = amount
                    local dice, plus = 0, 0
                    for n, sides in amount:gmatch("(%d*)d(%d+)") do dice = dice + (tonumber(n) or 1) * (tonumber(sides) + 1) / 2 end
                    if dice == 0 then plus = tonumber(amount) or 0 end
                    out.average = out.average + dice + plus
                    out.sources[#out.sources + 1] = "bonus " .. amount .. " (" .. rt .. ")"
                end
            end
        end
    end
    local function scan(list, side)
        for _, b in ipairs(list) do
            local a = try(function() return b.AdvantageBoost end)
            if a then
                local at, typ = tostring(a.AdvantageType), tostring(a.Type)
                local counts
                if side == "self" then
                    counts = (kind == "attack" and at == "AttackRoll")
                        or (kind == "save" and (at == "AllSavingThrows" or (at == "SavingThrow" and tostring(a.Ability) == abilityName)))
                        or (kind == "skill" and (at == "AllSkills" or (at == "Skill" and tostring(a.Skill) == what)))
                        or ((kind == "ability" or kind == "skill") and (at == "AllAbilities" or (at == "Ability" and tostring(a.Ability) == abilityName)))
                else
                    counts = kind == "attack" and at == "AttackTarget"
                end
                if counts then
                    if typ == "Disadvantage" then dis = true else adv = true end
                    out.sources[#out.sources + 1] = typ:lower() .. " (" .. at .. (side == "target" and ", from the target" or "") .. ")"
                end
            end
        end
    end
    scan(boostEntities(e, "Advantage"), "self")
    scan(boostEntities(e, "Disadvantage"), "self")
    local te = target and entityOf(target)
    if te then
        scan(boostEntities(te, "Advantage"), "target")
        scan(boostEntities(te, "Disadvantage"), "target")
    end
    if adv and not dis then out.advantage = 1 elseif dis and not adv then out.advantage = -1 end
    return out
end

-- Dice text -> average: "1d10" -> 5.5, "2d6+3" -> 10
local function diceAverage(text)
    local total, found = 0, false
    for n, sides in tostring(text):gmatch("(%d*)d(%d+)") do
        total = total + (tonumber(n) or 1) * (tonumber(sides) + 1) / 2
        found = true
    end
    local plus = tostring(text):match("[%+%-]%s*%d+%s*$")
    if plus then total = total + tonumber((plus:gsub("%s", ""))) end
    return found and total or nil
end

local function weaponOf(id, ranged)
    local item = try(Osi.GetEquippedItem, id, ranged and "Ranged Main Weapon" or "Melee Main Weapon")
    local e = item and entityOf(item)
    local statsId = e and try(function() return e.Data.StatsId end)
    return statsId and try(Ext.Stats.Get, statsId), item
end

local CANTRIP_DICE = { D4Cantrip = "1d4", D6Cantrip = "1d6", D8Cantrip = "1d8", D10Cantrip = "1d10", D12Cantrip = "1d12" }

--- The odds of a spell or action against a target: { kind = "attack" | "save" | "none",
--- bonus, ac (attacks), dc, save, saveBonus (save spells), chance (that it lands / that the
--- target fails the save), damage = { dice, average, type }, expected = average x chance }.
--- Counts what the character sheet shows; dice added by statuses (Bless, Bane, Guidance) and
--- advantage from position are not modelled - options.advantage = 1 / -1 applies one.
function See.Odds(who, spell, target, options)
    options = options or {}
    who = who or host()
    local stat = try(Ext.Stats.Get, spell)
    if not stat then return { error = "no such spell" } end
    local roll = try(function() return stat.SpellRoll.Default end) or ""
    local mine = See.Bonuses(who)
    local te = target and type(target) == "string" and entityOf(target)
    local out = { spell = spell }
    local ranged = roll:find("Ranged") ~= nil
    local weaponStat = weaponOf(who, ranged)
    if roll:find("Attack%(") then
        out.kind = "attack"
        if roll:find("Spell") then
            out.bonus = mine.spellAttack
        else
            local finesse = false
            local props = weaponStat and try(function() return weaponStat["Weapon Properties"] end) or {}
            for _, p in ipairs(props) do if p == "Finesse" then finesse = true end end
            local str, dex = (mine.abilities or {}).STR or 0, (mine.abilities or {}).DEX or 0
            local mod = ranged and dex or (finesse and math.max(str, dex) or str)
            out.bonus = mod + (mine.proficiency or 0)
            out.abilityMod = mod
        end
        out.ac = te and try(function() return te.Resistances.AC end)
        local attackType = roll:match("AttackType%.(%a+)")
        local m = See.RollMods(who, "attack", attackType, target)
        local advantage = options.advantage or m.advantage
        if #m.sources > 0 then out.mods = m.sources end
        if advantage ~= 0 then out.advantage = advantage end
        -- high ground: a ranged attack from 3 m (10 ft) or more above the target +2, from below -2
        local height = 0
        local sp, tp = posOf(who), te and posOf(target)
        if ranged and sp and tp then
            local dy = sp[2] - tp[2]
            if dy >= 3 then height = 2 elseif dy <= -3 then height = -2 end
            if height ~= 0 then
                out.mods = out.mods or {}
                table.insert(out.mods, (height > 0 and "+2 high ground" or "-2 low ground"))
            end
        end
        if out.bonus and out.ac then out.chance = See.Chance(out.bonus + height + (m.average or 0), out.ac, advantage) end
    else
        local save = roll:match("SavingThrow%(Ability%.(%a+)")
        if save then
            out.kind = "save"
            out.save = ABILITY_OF[save] or save
            out.dc = mine.spellDC
            local tb = te and See.Bonuses(target)
            out.saveBonus = tb and (tb.abilities or {})[out.save]
            if out.dc and out.saveBonus then
                local m = te and See.RollMods(target, "save", out.save) or { advantage = 0, average = 0, sources = {} }
                if #m.sources > 0 then out.targetMods = m.sources end
                out.chance = round(1 - See.Chance(out.saveBonus + (m.average or 0), out.dc, options.advantage or m.advantage), 0.01)
            end
        else
            out.kind = "none"
        end
    end
    local list = try(function() return stat.TooltipDamageList end) or ""
    local dice, kind = tostring(list):match("DealDamage%(([^,]+),%s*(%a+)%)")
    if dice then
        local cantrip = dice:match("LevelMapValue%((%w+)%)")
        if cantrip then dice = CANTRIP_DICE[cantrip] or dice end
        if dice:find("Weapon") then
            local wdice = weaponStat and try(function() return weaponStat.Damage end)
            local mod = out.abilityMod or 0
            dice = wdice and (wdice .. (mod >= 0 and "+" or "") .. mod) or dice
            kind = weaponStat and try(function() return weaponStat["Damage Type"] end) or kind
        end
        out.damage = { dice = dice, average = diceAverage(dice), type = kind }
        if out.damage.average then
            out.expected = round(out.damage.average * (out.chance or 1), 0.1)
        end
    end
    return out
end

-- ---------------------------------------------------------------------------------------
-- Walking

--- Whether a character can walk to a point (or an object), and how far that is along the path:
--- { reachable, length, straight, movement = metres left this turn (in a fight), withinMovement }.
function See.Path(who, to)
    who = who or host()
    local goal = type(to) == "table" and to or posOf(to)
    local from = posOf(who)
    if not (goal and from) then return { error = "no position" } end
    local out = { straight = round(dist(from, goal)) }
    local entity = entityOf(who)
    local path = try(Ext.Level.BeginPathfindingImmediate, entity, goal)
    if not path then
        out.error = "pathfinding refused"
        return out
    end
    local found = try(Ext.Level.FindPath, path)
    out.reachable = found == true
    if found then
        local nodes = try(function() return path.Nodes end)
        local length, last = 0, from
        local count = 0
        if nodes then
            for _, node in ipairs(nodes) do
                local p = node.Position
                if p then
                    length = length + dist(last, p)
                    last = p
                    count = count + 1
                end
            end
        end
        out.length = round(length)
        out.nodes = count
        if count > 0 then out.ends = rpos(last) end
    end
    try(Ext.Level.ReleasePath, path)
    local res = See.Resources(who)
    if res.movement and bool(try(Osi.IsInCombat, who)) then
        out.movement = res.movement[1]
        if out.length then out.withinMovement = out.length <= res.movement[1] + 0.1 end
        if GK.Act and GK.Act.Threats then
            local threats = GK.Act.Threats(who, out.ends or goal)
            if #threats > 0 then out.threatenedBy = threats end
            local provokes = GK.Act.Provokes(who, out.ends or goal)
            if #provokes > 0 then out.provokes = provokes end
        end
    end
    return out
end

-- ---------------------------------------------------------------------------------------
-- Ways: where one can go from here

--- How far a character can walk in each of the eight compass directions: for each, the farthest
--- point (sampled every `step` metres up to `range`) the pathfinder reaches by a path not much
--- longer than the straight line, with the floor height there - an exit, a corridor, a ledge.
--- Only ground the party can see (a clear line from a member's eyes within the camera's range,
--- See.CameraRange, or the member's sight) counts: what lies behind walls is not known.
--- options: range (24), step (3), who, all (ignore what is seen)
--- Returns { at, ways = { N = { reach, pos, path, dy, seen }, NE = ... } }.
function See.Ways(options)
    options = options or {}
    local who = options.who or host()
    local p = posOf(who)
    if not p then return { error = "no position" } end
    local range, step = options.range or 24, options.step or 3
    local members = See.Members()
    local function seen(q)
        if options.all then return true end
        for _, m in ipairs(members) do
            local e = eyeOf(m)
            local mp = posOf(m)
            if e and mp and dist(mp, q) <= math.max(See.CameraRange, (sightOf(entityOf(m)))) then
                -- from the eyes to chest height over the spot, or from above the head (the
                -- camera looks down on the party) to the floor itself
                if See.Clear(e, { q[1], q[2] + 1.2, q[3] }, 1.0) then return true end
                if See.Clear({ e[1], e[2] + 3.5, e[3] }, { q[1], q[2] + 0.3, q[3] }, 1.0) then return true end
            end
        end
        return false
    end
    local out = { at = rpos(p), from = nameOf(who), ways = {} }
    -- a budget: a failed path search is the expensive kind (it explores everything reachable),
    -- so the whole survey stops after options.budget seconds (default 1.5) and says so
    local deadline = Ext.Utils.MonotonicTime() + (options.budget or 1.5) * 1000
    for k, name in ipairs(COMPASS) do
        if Ext.Utils.MonotonicTime() > deadline then out.partial = true break end
        local angle = math.rad((k - 1) * 45)
        local dx, dz = math.sin(angle), math.cos(angle)
        local best
        local misses = 0
        for r = step, range, step do
            if Ext.Utils.MonotonicTime() > deadline then out.partial = true break end
            local x, z = p[1] + dx * r, p[3] + dz * r
            local found = false
            for _, y in ipairs(try(Ext.Level.GetHeightsAt, x, z) or {}) do
                if math.abs(y - p[2]) < 6 then
                    local q = { x, y, z }
                    local path = See.Path(who, q)
                    if path.reachable and path.ends and flat(path.ends, q) < 1.5 and path.length <= r * 1.6 + 3 then
                        local isSeen = seen(q)
                        if isSeen or options.all then
                            best = { reach = r, pos = rpos(q), path = path.length, dy = round(y - p[2]), seen = isSeen or nil }
                            found = true
                            break
                        end
                    end
                end
            end
            if found then misses = 0 else misses = misses + 1 end
            if misses >= 2 then break end
        end
        out.ways[name] = best or { reach = 0 }
    end
    return out
end

-- ---------------------------------------------------------------------------------------
-- Stealth: who watches

--- Whether a character standing at `point` would be in `npc`'s sight: inside its sight range and
--- field of view, with a clear line from its eyes. (The engine's own test needs an object, so
--- for a point the same rules are applied here.)
local function wouldSee(npc, npcEntity, point)
    local range, fov = sightOf(npcEntity)
    local eye = eyeOf(npc)
    if not eye then return false end
    local target = { point[1], point[2] + 1.0, point[3] }
    local d = dist(eye, target)
    if d > range then return false, "far" end
    local _, forward = facingOf(npcEntity)
    if forward then
        local dx, dz = target[1] - eye[1], target[3] - eye[3]
        local len = math.sqrt(dx * dx + dz * dz)
        if len > 0.5 then
            local cos = (dx * forward[1] + dz * forward[3]) / len
            if cos < math.cos(math.rad(fov / 2)) then return false, "behind" end
        end
    end
    if not See.Clear(eye, target) then return false, "blocked" end
    return true
end

--- The characters that could notice the party: every living non-party character within the
--- radius that the party perceives (all = every one), with its sight range, field of view,
--- facing, whether it is hostile, and which members it sees right now (the engine's own test,
--- which counts light, stealth and invisibility).
--- options: radius (30), all, hostileOnly
function See.Watchers(options)
    options = options or {}
    local members, at = viewpoint(options)
    local radius = options.radius or 30
    local out = {}
    if not at then return out end
    for _, entity in ipairs(Ext.Entity.GetAllEntitiesWithComponent("ServerCharacter")) do
        local id = uuidOf(entity)
        local p = id and posOf(entity)
        if p and not isMember(id) and flat(p, at) <= radius and bool(try(Osi.IsOnStage, id))
            and not bool(try(Osi.IsDead, id)) then
            local rel = See.Relation(id)
            if not options.hostileOnly or rel == "hostile" then
                local perceived = partyPerceives(id, true, members)
                if perceived or options.all then
                    local range, fov, dark = sightOf(entity)
                    local facing = facingOf(entity)
                    local row = { id = id, name = nameOf(entity), rel = rel, d = round(flat(p, at)), dir = compass(at, p),
                                  pos = rpos(p), sight = range, fov = fov, darkvision = dark,
                                  facing = facing and round(facing, 1) or nil, sees = {} }
                    if not perceived then row.perceived = false end
                    for _, m in ipairs(members) do
                        if bool(try(Osi.CanSee, id, m)) then row.sees[#row.sees + 1] = nameOf(m) or m end
                    end
                    out[#out + 1] = row
                end
            end
        end
    end
    table.sort(out, function(a, b) return a.d < b.d end)
    -- How visible each member is: sneaking, and the light where it stands (the game's own
    -- obscurity: Clear, Light(ly obscured), Heavy(ily obscured)).
    local party = {}
    for _, m in ipairs(members) do
        local e = entityOf(m)
        local dark = e and try(function() return e.Darkness end)
        party[#party + 1] = { id = m, name = nameOf(m),
                              sneaking = dark and (dark.CharacterSneaking or dark.ActuallySneaking) or false,
                              obscurity = dark and tostring(dark.Obscurity) or nil }
    end
    return { watchers = out, party = party }
end

--- Which characters would see someone standing at `point` ({x,y,z} or an object's uuid):
--- { { id, name, rel, d } } - by sight range, field of view and line of sight (light and
--- stealth rolls are the engine's and are not counted here). options: radius (30), all.
function See.Exposure(point, options)
    options = options or {}
    local p = type(point) == "table" and point or posOf(point)
    if not p then return { error = "no position" } end
    local members = See.Members()
    local out = {}
    for _, entity in ipairs(Ext.Entity.GetAllEntitiesWithComponent("ServerCharacter")) do
        local id = uuidOf(entity)
        local q = id and posOf(entity)
        if q and not isMember(id) and flat(p, q) <= (options.radius or 30) and bool(try(Osi.IsOnStage, id))
            and not bool(try(Osi.IsDead, id)) then
            if options.all or partyPerceives(id, true, members) then
                local yes = wouldSee(id, entity, p)
                if yes then
                    out[#out + 1] = { id = id, name = nameOf(entity), rel = See.Relation(id), d = round(dist(p, q)) }
                end
            end
        end
    end
    table.sort(out, function(a, b) return a.d < b.d end)
    return out
end

-- ---------------------------------------------------------------------------------------
-- Combat

--- The fight the party is in, or nil:
---   { round, active = { id, name, rel }, order = { { id, name, rel, initiative, acted, hp, maxHp,
---     ac, pos, d (from the active character), statuses, concentration, resources (party) } },
---     actions = GK.See.Actions of the active member (party member only) }
--- Combatants the party does not perceive are listed as { hidden = true } (the turn-order bar
--- shows a portrait for them too) unless options.all.
function See.Combat(options)
    options = options or {}
    local members = See.Members()
    local fight
    for _, m in ipairs(members) do
        fight = GK.Stage and GK.Stage.Combat(m)
        if fight then break end
    end
    if not fight then return nil end
    local activePos = fight.active and posOf(fight.active)
    local out = { round = fight.round, order = {} }
    for _, c in ipairs(fight.order) do
        local id = c.uuid
        local entity = id and entityOf(id)
        -- the turn order also holds objects the level scripts put there (initiative -20); they
        -- take no turns a player sees
        if entity and has(entity, "ServerCharacter") then
            local member = isMember(id)
            local perceived = member or partyPerceives(id, true, members)
            if perceived or options.all then
                local p = posOf(entity)
                local acted = try(function() return entity.TurnBased.ActedThisRoundInCombat end)
                local row = { id = id, name = c.name, rel = See.Relation(id), initiative = c.initiative,
                              acted = acted or nil, pos = rpos(p),
                              d = (p and activePos) and round(dist(p, activePos)) or nil }
                vitals(entity, row)
                row.abilities = nil
                if c.dead then row.dead = true end
                local statuses = See.Statuses(id)
                if #statuses > 0 then
                    row.statuses = {}
                    for _, s in ipairs(statuses) do row.statuses[#row.statuses + 1] = s.name or s.id end
                end
                local conc = try(function() return entity.Concentration.SpellId.Prototype end)
                if conc and conc ~= "" then row.concentration = conc end
                if member or options.all then row.resources = See.Resources(id) end
                if id == fight.active then row.active = true end
                if not perceived then row.perceived = false end
                out.order[#out.order + 1] = row
            else
                out.order[#out.order + 1] = { hidden = true, initiative = c.initiative }
            end
        end
    end
    -- Whose turn it is: every combatant whose turn is open (party members next to each other in
    -- the initiative order share one turn - each acts and ends its own; the group's turn ends when
    -- all have ended).
    out.turn = {}
    for _, row in ipairs(out.order) do
        local e = row.id and entityOf(row.id)
        local tb = e and try(function() return e.TurnBased end)
        if tb and tb.IsActiveCombatTurn and not tb.RequestedEndTurn
            and not (GK.Act and GK.Act.HasEnded and GK.Act.HasEnded(row.id)) then
            row.active = true
            out.turn[#out.turn + 1] = { id = row.id, name = row.name, rel = row.rel }
        elseif row.active then
            row.active = nil
        end
    end
    local timers = See.Timers()
    if #timers > 0 then out.timers = timers end
    -- a copy: the JSON writer prints a table met twice in one answer as "*RECURSION*"
    local first = out.turn[1]
    out.active = first and { id = first.id, name = first.name, rel = first.rel }
        or (fight.active and { id = fight.active, name = nameOf(fight.active), rel = See.Relation(fight.active) })
    out.mine = {}
    for _, t in ipairs(out.turn) do
        if isMember(t.id) then
            local actions = See.Actions(t.id)
            local movement = (See.Resources(t.id).movement or {})[1]
            local blocked = {}
            for _, a in ipairs(See.Actions(t.id, nil, { all = true })) do
                if not a.ready and a.why and (a.why:find("used") or a.why:find("slot")) then
                    blocked[#blocked + 1] = (a.name or a.id) .. ": " .. a.why
                end
            end
            out.mine[#out.mine + 1] = { id = t.id, name = t.name, movement = movement, actions = actions,
                                        targets = See.Targets(t.id, actions, movement), flee = See.Flee(t.id),
                                        notReady = #blocked > 0 and blocked or nil }
        end
    end
    return out
end

--- Whose turn is open, cheaply (no odds, no conditions): { inFight, round, turn = { { id, name, rel } } }.
--- For waiting loops; GK.See.Combat is the full picture.
function See.Turn()
    local members = See.Members()
    local fight
    for _, m in ipairs(members) do
        fight = GK.Stage and GK.Stage.Combat(m)
        if fight then break end
    end
    if not fight then return { inFight = false } end
    local out = { inFight = true, round = fight.round, turn = {} }
    for _, c in ipairs(fight.order) do
        local e = c.uuid and entityOf(c.uuid)
        local tb = e and try(function() return e.TurnBased end)
        if tb and tb.IsActiveCombatTurn and not tb.RequestedEndTurn and has(e, "ServerCharacter")
            and not (GK.Act and GK.Act.HasEnded and GK.Act.HasEnded(c.uuid)) then
            out.turn[#out.turn + 1] = { id = c.uuid, name = c.name, rel = See.Relation(c.uuid) }
        end
    end
    return out
end

--- For a party member in a fight: every hostile the party perceives with the best things to do
--- to it - { id, name, d, ac, hp, options = { { id, name, chance, expected, damage, move } } } -
--- best expected damage first. `move` is how far the character must walk first (0 = in range
--- now); options it cannot reach with the movement left are dropped.
function See.Targets(who, actions, movement)
    actions = actions or See.Actions(who)
    local mp = posOf(who)
    local members = See.Members()
    local out = {}
    if not mp then return out end
    for _, entity in ipairs(Ext.Entity.GetAllEntitiesWithComponent("ServerCharacter")) do
        local id = uuidOf(entity)
        local p = id and posOf(entity)
        if p and not isMember(id) and dist(p, mp) <= 40 and bool(try(Osi.IsInCombat, id))
            and not bool(try(Osi.IsDead, id)) and See.Relation(id) == "hostile"
            and partyPerceives(id, true, members) then
            local d = dist(p, mp)
            local row = { id = id, name = nameOf(entity), d = round(d), ac = try(function() return entity.Resistances.AC end),
                          hp = try(function() return entity.Health.Hp end), options = {} }
            for _, a in ipairs(actions) do
                if a.ready and type(a.range) == "number" and a.range > 0 and (a.type == "Target" or a.type == "Projectile"
                    or a.type == "Zone" or a.type == "Cone" or a.type == "Throw") then
                    local move = math.max(0, d - a.range - 0.6)
                    local allowed = not GK.Conditions or GK.Conditions.Check(who, a.id, id).ok ~= false
                    if allowed and move <= (movement or 0) + 0.01 then
                        local odds = See.Odds(who, a.id, id)
                        if odds.kind ~= "none" and odds.expected then
                            row.options[#row.options + 1] = { id = a.id, name = a.name, chance = odds.chance,
                                                              expected = odds.expected, damage = odds.damage and odds.damage.dice,
                                                              move = round(move) }
                        end
                    end
                end
            end
            table.sort(row.options, function(x, y) return (x.expected or 0) > (y.expected or 0) end)
            for i = #row.options, 4, -1 do row.options[i] = nil end
            out[#out + 1] = row
        end
    end
    table.sort(out, function(x, y) return x.d < y.d end)
    return out
end

--- What the party carries: per member { id, name, items = { { id, name, kind, count, equipped,
--- value, weight, use } } } - the inventory window. Containers carried (a bag) are listed with
--- their own contents under `inside`.
function See.Inventory(who)
    local out = {}
    local members = who and { who } or See.Members()
    local function list(owner, depth)
        local rows = {}
        local e = entityOf(owner)
        for _, inv in ipairs(try(function() return e.InventoryOwner.Inventories end) or {}) do
            for _, slot in pairs(try(function() return inv.InventoryContainer.Items end) or {}) do
                local ie = try(function() return slot.Item end)
                local id = ie and uuidOf(ie)
                if id then
                    local row = { id = id, name = nameOf(ie), kind = classify(ie, id) or "item",
                                  count = try(function() return ie.Stack.Amount end) }
                    local value = try(Osi.ItemGetGoldValue, id)
                    if value and value > 0 then row.value = value end
                    if try(function() return ie.Equipable end) and bool(try(Osi.IsEquipped, id)) then row.equipped = true end
                    if depth < 2 and bool(try(Osi.IsContainer, id)) then row.inside = list(id, depth + 1) end
                    rows[#rows + 1] = row
                end
            end
        end
        table.sort(rows, function(a, b) return (a.name or "") < (b.name or "") end)
        return rows
    end
    for _, m in ipairs(members) do
        out[#out + 1] = { id = m, name = nameOf(m), gold = try(Osi.GetGold, m), items = list(m, 0) }
    end
    return out
end

--- Whether a character can flee the fight now (the hot bar's Flee button): { can, why = { reasons },
--- enemyAt = metres to the nearest enemy, need = metres the game wants }. From the engine's
--- FleeCapability component: Flags name what blocks it (EnemyTooClose, Condition - a status,
--- Region - the place forbids it, as the Nautiloid does).
function See.Flee(who)
    who = who or host()
    local e = entityOf(who)
    local f = e and try(function() return Ext.Types.Serialize(e.FleeCapability) end)
    if not f then return { can = false, why = { "unknown" } } end
    local why = {}
    for _, flag in ipairs(f.Flags or {}) do why[#why + 1] = tostring(flag) end
    return { can = #why == 0, why = #why > 0 and why or nil, enemyAt = round(f.FleeDistance or 0),
             need = round(f.CurrentFleeDistance or 0) }
end

-- ---------------------------------------------------------------------------------------
-- Party

--- Every party member: { id, name, level, classes, xp, nextXp, levelUp, hp, maxHp, ac, pos,
--- statuses, resources, concentration, weapon, inCombat, dead }, and the party's gold.
function See.Party()
    local out = { members = {} }
    local gold = 0
    for _, id in ipairs(See.Members()) do
        local entity = entityOf(id)
        if entity then
            local row = See.Entity(id, { all = true })
            row.dir, row.d, row.rel, row.talk, row.trade = nil, nil, nil, nil, nil
            local xp = try(function() return entity.Experience end)
            if xp then
                row.xp = xp.TotalExperience
                row.nextXp = xp.NextLevelExperience
            end
            local avail = try(function() return entity.AvailableLevel.Level end)
            if avail and row.level and avail > row.level then row.levelUp = avail end
            row.controlled = bool(try(Osi.IsControlled, id)) or nil
            out.members[#out.members + 1] = row
            gold = gold + (try(Osi.GetGold, id) or 0)
        end
    end
    out.gold = gold
    return out
end

-- ---------------------------------------------------------------------------------------
-- Status line

--- The story's turn counters on screen (the countdown at the right edge: the prologue's helm,
--- the Iron Throne, the ending, ...): { { name, text, turnsLeft, seconds, duration } }. They are
--- eoc::story::TimerComponent entities (StoryTimer), started by Osiris TurnBasedTimerLaunch;
--- Duration and Elapsed are in seconds of game time, a turn being 6 (found 2026-11-13: the helm
--- of the Nautiloid starts "TUT_Helm_Timer", 90 s = 15 turns).
function See.Timers()
    local out = {}
    for _, e in ipairs(Ext.Entity.GetAllEntitiesWithComponent("StoryTimer")) do
        local t = try(function() return Ext.Types.Serialize(e.StoryTimer) end)
        if t then
            local key = t.field_4
            -- the key names a TranslatedString (handle) in the game's key table
            local text = key and try(function()
                local ts = Ext.Loca.GetTranslatedStringKey(key)
                local handle = type(ts) == "string" and ts or (ts and ts.Handle and (ts.Handle.Handle or ts.Handle))
                return handle and Ext.Loca.GetTranslatedString(handle)
            end)
            local left = (t.Duration or 0) - (t.Elapsed or 0)
            out[#out + 1] = { name = t.Name, key = key, text = text ~= "" and text or nil, duration = t.Duration,
                              seconds = round(left, 1), turnsLeft = math.ceil(left / 6) }
        end
    end
    return out
end

--- Where a character is, in words a person reads: the level, its height, and the nearest named
--- thing the party perceives (a door, a ladder, a container, a usable fixture or a character),
--- e.g. "TUT_Avernus_C, by Sphincter Door (door, 3 m NE)". The zone name the HUD shows comes from the UI
--- side (GK.Places.Zone) and is put first by stand/situation.py when there is one.
function See.Place(who)
    who = who or host()
    local p = posOf(who)
    local level = try(function() return Ext.Entity.Get(who).Level.LevelName end) or "?"
    if not p then return { level = level } end
    local best, bestD
    local around = See.Around({ who = who, radius = 15, limit = 12 })
    for _, row in ipairs(around.objects or {}) do
        if row.name and row.kind ~= "item" and row.kind ~= "gold" and (not bestD or row.d < bestD) then
            best, bestD = row.name .. " (" .. row.kind .. ", " .. row.d .. " m " .. (row.dir or "") .. ")", row.d
        end
    end
    if not best then
        for _, row in ipairs(around.characters or {}) do
            if row.rel ~= "party" and row.name and (not bestD or row.d < bestD) then
                best, bestD = row.name .. " (" .. row.d .. " m " .. (row.dir or "") .. ")", row.d
            end
        end
    end
    return { level = level, height = round(p[2]), pos = rpos(p), near = best,
             text = level .. (best and (", by " .. best) or "") }
end

--- The situation in one table: { level, mode = "exploration"|"combat"|"dialogue"|"turn-based",
--- gameState, active (whose turn), party = { { name, hp, maxHp, dead } }, combat round }.
function See.Status(options)
    options = options or {}
    local h = host()
    local out = { level = try(function() return Ext.Entity.Get(h).Level.LevelName end),
                  gameState = tostring(Ext.Utils.GetGameState()), party = {} }
    local inCombat, inDialog, ftb = false, false, false
    for _, m in ipairs(See.Members()) do
        local e = entityOf(m)
        if bool(try(Osi.IsInCombat, m)) then inCombat = true end
        if bool(try(Osi.IsInDialog, m)) then inDialog = true end
        if bool(try(Osi.IsInForceTurnBasedMode, m)) then ftb = true end
        local health = e and try(function() return e.Health end)
        out.party[#out.party + 1] = { id = m, name = nameOf(m), hp = health and health.Hp, maxHp = health and health.MaxHp,
                                      dead = bool(try(Osi.IsDead, m)) or nil }
    end
    out.mode = inDialog and "dialogue" or inCombat and "combat" or ftb and "turn-based" or "exploration"
    -- footing: a member standing where the pathfinder cannot start (on a table, a corpse, a ledge);
    -- path searches cost, so only when asked (options.footing) and once per place (GK.Act.Stuck caches)
    if options.footing and GK.Act and GK.Act.Stuck then
        for _, row in ipairs(out.party) do
            if not row.dead and try(GK.Act.Stuck, row.id) then
                row.stuck = "cannot walk from here (standing on something?): try a jump down or a short step"
            end
        end
    end
    local timers = See.Timers()
    if #timers > 0 then out.timers = timers end
    if inCombat then
        local fight = GK.Stage and GK.Stage.Combat(h)
        if fight then
            out.round = fight.round
            if fight.active then
                out.active = { id = fight.active, name = nameOf(fight.active), rel = See.Relation(fight.active) }
            end
        end
    end
    return out
end

return See
