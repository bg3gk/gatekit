-- Server side of the GateKit API: read characters, change the world.
-- Everything takes and returns plain values (uuids, numbers, tables), so the results travel
-- as JSON. `who` is a character uuid; nil means the host's character.

local SLOTS = {
    "Helmet", "Breast", "Cloak", "Gloves", "Boots", "Amulet", "Ring", "Ring2",
    "Melee Main Weapon", "Melee Offhand Weapon", "Ranged Main Weapon", "Ranged Offhand Weapon",
    "Underwear", "VanityBody", "VanityBoots", "MusicalInstrument",
}

local function try(fn, ...)
    local ok, a, b, c = pcall(fn, ...)
    if ok then return a, b, c end
    return nil
end

local function host(who)
    return who or Osi.GetHostCharacter()
end

local function displayName(id)
    local entity = Ext.Entity.Get(id)
    local name = entity and entity.DisplayName and entity.DisplayName.Name
    return name and try(function() return name:Get() end) or nil
end

--- One item as { uuid, template, name }.
function GK.Item(id)
    if not id then return nil end
    return { uuid = id, template = try(Osi.GetTemplate, id), name = displayName(id) }
end

--- A character: identity, place, health, flags and what it wears.
function GK.Character(who)
    local id = host(who)
    local entity = Ext.Entity.Get(id)
    if not entity then return nil end
    local x, y, z = try(Osi.GetPosition, id)
    local equipment = {}
    for _, slot in ipairs(SLOTS) do
        local item = try(Osi.GetEquippedItem, id, slot)
        if item then equipment[slot] = GK.Item(item) end
    end
    return {
        uuid = entity.Uuid and entity.Uuid.EntityUuid or id,
        name = displayName(id),
        template = try(Osi.GetTemplate, id),
        level = try(Osi.GetRegion, id),
        position = x and { x, y, z } or nil,
        hp = entity.Health and entity.Health.Hp,
        maxHp = entity.Health and entity.Health.MaxHp,
        inParty = entity.PartyMember ~= nil,
        inCombat = try(Osi.IsInCombat, id) == 1,
        unsheathed = try(Osi.IsWeaponUnsheathed, id) == 1,
        equipment = equipment,
    }
end

--- Every party member, as GK.Character.
function GK.Party()
    local out = {}
    for _, entity in ipairs(Ext.Entity.GetAllEntitiesWithComponent("PartyMember")) do
        local id = entity.Uuid and entity.Uuid.EntityUuid
        if id then out[#out + 1] = GK.Character(id) end
    end
    return out
end

--- Whether a character is in a dialogue: true, false, or nil and why when it cannot be read.
--- Osi.IsInDialog does not exist in this game (N19 of the third run): called through a pcall it
--- failed every time and read as "not in a dialogue", so four checks never fired. The server
--- character's own InDialog flag is what GK.Act reads, and it was right live; the Osiris query
--- is asked as well only where the game has it.
function GK.InDialog(who)
    local entity = type(who) == "string" and try(Ext.Entity.Get, who) or who
    local flag = entity and try(function() return entity.ServerCharacter.InDialog end)
    if type(flag) == "boolean" then return flag end
    local query = try(function() return Osi.IsInDialog end)
    if query ~= nil then
        local answer = try(query, who)
        if answer ~= nil then return answer == 1 end
    end
    return nil, "cannot tell whether " .. tostring(who) .. " is in a dialogue (no ServerCharacter.InDialog, no Osi.IsInDialog)"
end

--- A position in either form callers write it: { x, y, z } or { x = .., y = .., z = .. }
--- (X/Y/Z too). Answers { x, y, z }, or nil and why for anything else (wish 37: Stage.Cast took
--- only the first form and failed on the second with a raw "Number expected").
function GK.Pos(p)
    local why = "a position is { x, y, z } or { x = .., y = .., z = .. }, got "
        .. (type(p) == "table" and "a table without three numbers" or tostring(p))
    if type(p) ~= "table" then return nil, why end
    local x, y, z = p[1] or p.x or p.X, p[2] or p.y or p.Y, p[3] or p.z or p.Z
    if type(x) ~= "number" or type(y) ~= "number" or type(z) ~= "number" then return nil, why end
    return { x, y, z }
end

--- Puts `count` items of a root template into a character's inventory.
--- The items exist a moment later; GK.Find(template, who) returns them. A template the game does
--- not have is an error (wish 29: an invented uuid answered true).
function GK.Give(template, count, who)
    -- Written as Give(who, template[, count]) - the order a runner reached for (N30 of the third
    -- run) - it is understood: a character first and a text second can only mean that.
    if type(count) == "string" and type(template) == "string" and try(Osi.IsCharacter, template) == 1 then
        template, count, who = count, who, template
        if type(count) ~= "number" then count = nil end
    end
    if count ~= nil and type(count) ~= "number" then
        error("GK.Give(template, count, who): count is a number, got " .. tostring(count) .. "; nothing was given", 2)
    end
    local id = host(who)
    -- the bridge's own short names (GK.Stage.Templates: thieves_tools, longsword ...) are taken too
    local short = GK.Stage and GK.Stage.Templates and GK.Stage.Templates[template]
    if short then template = short end
    local uuid = type(template) == "string" and template:match("(%x+%-%x+%-%x+%-%x+%-%x+)$")
    local root = uuid and try(Ext.Template.GetRootTemplate, uuid)
    if not root then
        error("no root template " .. tostring(template) .. ": GK.Give(template, count, who) takes a root template uuid, "
            .. "Name_uuid or a short name of GK.Stage.Templates; "
            .. "an item's is Ext.Stats.Get(<its stats name>).RootTemplate. Nothing was given", 2)
    end
    count = count or 1
    local held = try(GK.Find, uuid, id)
    Osi.TemplateAddTo(uuid, id, count, 0)
    return { template = uuid, name = try(function() return root.Name end), count = count, to = id,
             before = held and #held or nil,
             note = "queued: the items exist a moment later - GK.Find(template, who) returns them" }
end

--- Items of a root template in a character's inventory (uuids).
function GK.Find(template, who)
    local id = host(who)
    local wanted = template:match("(%x+%-%x+%-%x+%-%x+%-%x+)$") or template
    local out = {}
    local entity = Ext.Entity.Get(id)
    for _, inventory in ipairs(entity.InventoryOwner.Inventories) do
        for _, slot in pairs(inventory.InventoryContainer.Items) do
            local item = slot.Item
            local uuid = item.Uuid and item.Uuid.EntityUuid
            local itemTemplate = uuid and try(Osi.GetTemplate, uuid) or ""
            if uuid and itemTemplate:find(wanted, 1, true) then out[#out + 1] = uuid end
        end
    end
    return out
end

--- Equips an item (uuid) or the first item of a root template found in the inventory.
function GK.Equip(item, who)
    local id = host(who)
    if not Ext.Entity.Get(item) or try(Osi.GetTemplate, item) == nil then
        local found = GK.Find(item, id)
        if #found == 0 then error("no item of template " .. tostring(item) .. " in the inventory") end
        item = found[1]
    end
    Osi.Equip(id, item)
    return item
end

function GK.Unequip(item, who)
    Osi.Unequip(host(who), item)
    return true
end

--- Draws (true) or sheathes (false) the character's weapons at once.
function GK.Unsheath(drawn, who)
    Osi.SetWeaponUnsheathed(host(who), drawn and 1 or 0, 1)
    return true
end

--- Moves a character to a position, or next to another object when `x` is a uuid.
function GK.Teleport(x, y, z, who)
    local id = host(who)
    if type(x) == "string" then
        Osi.TeleportTo(id, x, "", 0, 0, 0, 0, 1)
    else
        Osi.TeleportToPosition(id, x, y, z, "", 0, 0, 0, 0, 1)
    end
    return true
end

--- Creates a character or item from a root template at a position (default: beside the host).
--- Returns the new object's uuid.
function GK.Spawn(template, x, y, z)
    if not x then
        x, y, z = Osi.GetPosition(Osi.GetHostCharacter())
        x = x + 1.5
    end
    return Osi.CreateAt(template, x, y, z, 0, 1, "")
end

--- The dialogues the story has registered: { { speaker, dialog } }, optionally only those whose
--- speaker or dialogue name contains `filter`.
function GK.Dialogs(filter)
    local out = {}
    for _, row in ipairs(Osi.DB_Dialogs:Get(nil, nil)) do
        local speaker, dialog = tostring(row[1]), tostring(row[2])
        if not filter or speaker:find(filter, 1, true) or dialog:find(filter, 1, true) then
            out[#out + 1] = { speaker = speaker, dialog = dialog }
        end
    end
    return out
end

--- Starts a dialogue between `speaker` (an object or character) and a player character.
--- Moves the player next to the speaker first: the story refuses a dialogue from afar.
--- Returns whether the story accepted it; GK.Dialog() (UI side) shows it a moment later.
function GK.StartDialog(dialog, speaker, who)
    -- D26 of the day run: a character's uuid given as the dialog failed deep inside, "String
    -- expected for argument 3, got nil" from TeleportTo. The arguments are said by name.
    if type(dialog) ~= "string" or dialog == "" then
        error("GK.StartDialog(dialog, speaker, who): dialog is a dialog resource name or uuid (GK.Dialogs lists them), got " .. tostring(dialog), 2)
    end
    if try(Osi.IsCharacter, dialog) == 1 then
        error("GK.StartDialog(dialog, speaker, who): the first argument is a character (" .. dialog .. "), not a dialog; "
            .. "the speaker comes second - GK.Dialogs(filter) names the dialogs", 2)
    end
    if type(speaker) ~= "string" or try(Osi.IsCharacter, speaker) ~= 1 and try(Osi.IsItem, speaker) ~= 1 then
        error("GK.StartDialog(dialog, speaker, who): speaker is the uuid of a character or an item, got " .. tostring(speaker), 2)
    end
    local id = host(who)
    Osi.TeleportTo(id, speaker, "", 0, 0, 0, 0, 1)
    return Osi.QRY_StartDialog(dialog, speaker, id) == true
end

--- Removes an object created with GK.Spawn.
function GK.Remove(id)
    Osi.RequestDelete(id)
    return true
end

--- Records an Osiris event in the event log whenever it fires: a row of kind `osiris`, payload
--- { event, args }. Once per event name and Lua state; a second call is a no-op.
GK.Watched = GK.Watched or {}
function GK.Watch(name, arity)
    if GK.Watched[name] then return true end
    Ext.Osiris.RegisterListener(name, arity, "after", function(...)
        GK.EventLog.Append("osiris", { event = name, args = { ... } })
    end)
    GK.Watched[name] = arity
    return true
end

-- Events most tests care about. A wrong name or arity only costs that one listener.
for name, arity in pairs({
    DialogStarted = 2, DialogEnded = 2, Equipped = 2, Unequipped = 2, TemplateAddedTo = 4,
    CombatStarted = 1, CombatEnded = 1, LevelGameplayStarted = 2,
    TeleportedToCamp = 1, Died = 1,
    -- Not EnteredLevel: it fires for every object of a level and floods the log.
}) do
    pcall(GK.Watch, name, arity)
end
