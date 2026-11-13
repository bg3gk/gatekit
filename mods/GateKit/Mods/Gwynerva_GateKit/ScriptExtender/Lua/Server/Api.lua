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

--- Puts `count` items of a root template into a character's inventory.
--- The items exist a moment later; GK.Find(template, who) returns them.
function GK.Give(template, count, who)
    Osi.TemplateAddTo(template, host(who), count or 1, 0)
    return true
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
    local id = host(who)
    Osi.TeleportTo(id, speaker, "", 0, 0, 0, 0, 1)
    return Osi.QRY_StartDialog(dialog, speaker, id) == true
end

--- Removes an object created with GK.Spawn.
function GK.Remove(id)
    Osi.RequestDelete(id)
    return true
end

--- Records an Osiris event in the event log (GK.Events) whenever it fires.
function GK.Watch(name, arity)
    Ext.Osiris.RegisterListener(name, arity, "after", function(...)
        GK.Note(name, { ... })
    end)
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
