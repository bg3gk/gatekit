-- Readers for the named windows of GK.Gui (Client/Windows.lua): what the player sees in a window,
-- as a small JSON-safe summary. GK.Gui.Read(name) calls GK.Gui.Windows[name].read(data, ui), where
-- `data` is the widget's DataContext (a Noesis view model) and `ui` is GK.Gui.
--
-- Where the data comes from: the bindings of the window's XAML (Mods/MainUI/GUI/Pages/<Page>.xaml;
-- the character panel's pages live in Public/Game/GUI/Library/CharacterSheetTemplates_k.xaml).
-- Each reader names the bindings it follows. Rules: every Noesis access is guarded, lists are
-- capped (CAP), strings are translated (handles and ResStr_ ids), numbers are finite and rounded,
-- a table is either a list or a record - never both. Found 2026-11-13, game 4.1.1.7631656.

local UI = GK.Gui
if not UI or not UI.Windows then return false end
local W = UI.Windows

local CAP = 40
local UNKNOWN = "ls::TranslatedStringRepository::s_HandleUnknown"

-- Guarded access ----------------------------------------------------------------------------

local function get(object, key)
    if object == nil then return nil end
    local ok, value = pcall(function() return object[key] end)
    if ok then return value end
    return nil
end

-- object.A.B.C, nil at the first missing step
local function path(object, dotted)
    for part in dotted:gmatch("[^%.]+") do
        object = get(object, part)
        if object == nil then return nil end
    end
    return object
end

local function count(collection)
    if collection == nil then return 0 end
    local ok, n = pcall(function() return #collection end)
    return ok and type(n) == "number" and n or 0
end

local function at(collection, i)
    local ok, value = pcall(function() return collection[i] end)
    return ok and value or nil
end

local function typeOf(object)
    local ok, name = pcall(function() return tostring(object.Type) end)
    return ok and name or "?"
end

-- A view-model property that `object.X` cannot reach: `object.Type` is the Noesis type name
-- ("ls.MapMarker"), which hides a property of the view model called Type (MapMarker.Type = "Player").
local function property(object, key)
    if object == nil then return nil end
    local ok, value = pcall(function() return object:GetProperty(key) end)
    if ok then return value end
    return nil
end

-- Translated text: loca handles ("h...g...") and resource ids ("ResStr_123") become text; the
-- game's "unknown handle" marker and empty strings become nil; a VMContextTransString gives its Text.
local function text(value)
    if value == nil then return nil end
    if type(value) == "userdata" then
        value = get(value, "Text")
        if type(value) ~= "string" then return nil end
    end
    if type(value) ~= "string" then return nil end
    if value == "" or value == UNKNOWN then return nil end
    if value:match("^h%x+g%x+g%x+g%x+g%x+$") or value:match("^ResStr_%d+$") then
        local ok, translated = pcall(Ext.Loca.GetTranslatedString, value)
        if ok and type(translated) == "string" and translated ~= "" then return translated end
        return nil
    end
    return value
end

-- A finite number, rounded to 2 decimals (whole numbers stay whole); nil otherwise.
local function num(value)
    if type(value) ~= "number" then return nil end
    if value ~= value or value == math.huge or value == -math.huge then return nil end
    if value == math.floor(value) then return math.floor(value) end
    return math.floor(value * 100 + 0.5) / 100
end

-- An enum or plain value as a string or number for JSON.
local function plain(value)
    local kind = type(value)
    if kind == "string" then return text(value) or (value ~= UNKNOWN and value ~= "" and value or nil) end
    if kind == "number" then return num(value) end
    if kind == "boolean" then return value end
    if value == nil then return nil end
    return nil
end

-- Maps a collection: fn(item, i) returns a value or nil (skipped). Returns the list (at most
-- `cap` entries) and the number of non-nil results over the whole collection.
local function each(collection, fn, cap)
    cap = cap or CAP
    local out, total = {}, 0
    for i = 1, count(collection) do
        local item = at(collection, i)
        if item ~= nil then
            local ok, value = pcall(fn, item, i)
            if ok and value ~= nil then
                total = total + 1
                if #out < cap then out[#out + 1] = value end
            end
        end
    end
    return out, total
end

-- A stat view model (VMStat / VMRangeStat): its Value, or { value, max }.
local function stat(object)
    return num(get(object, "Value"))
end

local function range(object)
    local value, max = num(get(object, "Value")), num(get(object, "Max") or get(object, "MaxValue"))
    if value == nil and max == nil then return nil end
    return { value = value, max = max }
end

-- A character's name: the view model's Name (a ResStr_ id for origins), else the entity's.
local function characterName(character)
    local name = text(get(character, "Name"))
    if name then return name end
    local uuid = get(character, "EntityUUID")
    if type(uuid) == "string" then
        local ok, translated = pcall(function()
            return Ext.Loca.GetTranslatedString(Ext.Entity.Get(uuid).DisplayName.NameKey.Handle.Handle)
        end)
        if ok and type(translated) == "string" and translated ~= "" then return translated end
    end
    return nil
end

local function itemName(item)
    return text(get(item, "Name")) or text(get(item, "NameAlchemy"))
end

-- An item of an inventory slot (VMItem): { name, count, ... } - only what the slot shows.
local function itemSummary(item)
    if item == nil then return nil end
    local out = { name = itemName(item) or "?" }
    local n = num(get(item, "Count"))
    if n and n ~= 1 then out.count = n end
    local equipped = get(item, "Equipped")
    if type(equipped) == "string" and equipped ~= "NotEquipped" then out.equipped = equipped end
    if get(item, "IsStolen") == true then out.stolen = true end
    if get(item, "IsNew") == true then out.new = true end
    if get(item, "IsContainer") == true then
        local inside = 0
        for _, inventory in ipairs(each(get(item, "Inventories"), function(x) return x end)) do
            inside = inside + count(get(inventory, "Slots"))
        end
        out.contains = inside
    end
    return out
end

-- An inventory (VMInventory): its Slots, each { Index, Object = VMItem }.
local function inventoryItems(inventory)
    return each(get(inventory, "Slots"), function(slot) return itemSummary(get(slot, "Object")) end)
end

-- Readers are wrapped: whatever happens inside, a table comes back.
local function reader(fn)
    return function(data, ui)
        local ok, result = pcall(fn, data, ui)
        if ok and type(result) == "table" then return result end
        return { error = ok and "the reader returned nothing" or tostring(result) }
    end
end

local function selectedCharacter(data)
    return path(data, "CurrentPlayer.SelectedCharacter")
end

-- A named element of an open widget (for state that lives in the XAML, not in the view model:
-- a ListBox's SelectedItem).
local function element(ui, window, name)
    local widget = ui.Widget(window)
    if not widget then return nil end
    local ok, found = pcall(function() return widget:Find(name) end)
    return ok and found or nil
end

-- journal: JournalQuests.xaml (DCJournalQuests) -----------------------------------------------
-- Left tree: CurrentPlayer.JournalItemContainers -> QuestCategoryContainer {QuestCategory.Description,
-- QuestViews} -> QuestView {Item (ls.Quest: Title, IsDisabled, IsMainQuest, Objectives),
-- SubQuestViews, HasPlayerSeenLastUpdate}. Visibility: QuestsFilter "Archived" shows only items
-- with MainItem.IsDisabled; "Current" with HideCompletedQuestsInCurrentList hides Item.IsDisabled.
-- Right side: CurrentPlayer.SelectedJournalItemView.Item {Title, Objectives (QuestObjective:
-- Description, IsCompleted, Steps -> QuestStep.Description)} or, for an ls.JournalItem, Steps.

local function objectivesOf(item)
    return each(get(item, "Objectives"), function(objective)
        local description = text(get(objective, "Description"))
        if not description then return nil end
        return { text = description, done = get(objective, "IsCompleted") == true }
    end)
end

local function questState(view, item)
    if get(item, "IsDisabled") == true then return "closed" end
    if get(view, "QuestIsInProgress") == false then return "inactive" end
    return "active"
end

local function questSummary(view, filter, hideClosed)
    local item = get(view, "Item")
    if item == nil then return nil end
    local disabled = get(item, "IsDisabled") == true
    if filter == "Archived" and get(path(view, "MainItem"), "IsDisabled") == false then return nil end
    if filter ~= "Archived" and hideClosed and disabled then return nil end
    local out = { title = text(get(item, "Title")) or "?", state = questState(view, item) }
    if get(item, "IsMainQuest") == true then out.main = true end
    if get(view, "HasPlayerSeenLastUpdate") == false then out.updated = true end
    if get(view, "IsSelected") == true then out.selected = true end
    local current = {}
    for _, objective in ipairs((objectivesOf(item))) do
        if not objective.done then current[#current + 1] = objective.text end
    end
    if #current > 0 then out.objectives = current end
    local subs = each(get(view, "SubQuestViews"), function(sub) return questSummary(sub, filter, hideClosed) end, 10)
    if #subs > 0 then out.subquests = subs end
    return out
end

W.journal.read = reader(function(data)
    local player = get(data, "CurrentPlayer")
    local filter = get(player, "QuestsFilter")
    local hideClosed = get(player, "HideCompletedQuestsInCurrentList") == true
    local out = { filter = plain(filter), hideCompleted = hideClosed }
    local total = 0
    out.categories = each(get(player, "JournalItemContainers"), function(container)
        local views = get(container, "QuestViews")
        local title = text(path(container, "QuestCategory.Description")) or text(get(container, "Title")) or "?"
        local quests, n = each(views, function(view) return questSummary(view, filter, hideClosed) end)
        total = total + n
        if n == 0 then return nil end
        return { category = title, quests = quests }
    end)
    out.quests = total
    local selected = path(player, "SelectedJournalItemView")
    local item = get(selected, "Item")
    if item then
        local s = { title = text(get(item, "Title")) or "?", state = questState(selected, item) }
        if typeOf(item) == "ls.Quest" then
            s.objectives = each(get(item, "Objectives"), function(objective)
                local description = text(get(objective, "Description"))
                local steps = each(get(objective, "Steps"), function(step) return text(get(step, "Description")) end, 10)
                if not description and #steps == 0 then return nil end
                return { text = description or "", done = get(objective, "IsCompleted") == true, log = steps }
            end, 15)
        else
            s.log = each(get(item, "Steps"), function(step) return text(get(step, "Description")) end, 15)
        end
        s.onMap = path(selected, "Quest.HasMapMarkers") == true
        out.selected = s
    end
    return out
end)

-- map: JournalMap.xaml (VMWaypoint) ---------------------------------------------------------
-- Zone name: CurrentPlayer.SelectedCharacter.PlayerCharacterProperties.CurrentSubregionName.
-- Waypoint list: CurrentPlayer.Waypoints -> { RegionName (a loca key), WaypointList -> VMWaypoint.Name }.
-- The map itself: CurrentPlayer.WorldMap (ls.WorldMap: Markers -> ls.MapMarker {Type, Name,
-- QuestViews}, CurrentBuilding.UuidStr, Floor). The level is not in the window: it is read from the
-- selected character's entity (Level.LevelName) for context.

local function locaKey(key)
    local translated = text(key)
    if translated and translated ~= key then return translated end
    local ok, handle = pcall(Ext.Loca.GetTranslatedStringKey, key)
    if ok and type(handle) == "string" then return text(handle) or key end
    return translated or key
end

W.map.read = reader(function(data)
    local player = get(data, "CurrentPlayer")
    local character = get(player, "SelectedCharacter")
    local out = {}
    local uuid = get(character, "EntityUUID")
    if type(uuid) == "string" then
        local ok, level = pcall(function() return Ext.Entity.Get(uuid).Level.LevelName end)
        if ok and type(level) == "string" then out.level = level end
    end
    out.zone = text(path(character, "PlayerCharacterProperties.CurrentSubregionName"))
    local map = get(player, "WorldMap")
    out.building = plain(path(map, "CurrentBuilding.UuidStr"))
    out.floor = num(get(map, "Floor"))
    local waypoints = 0
    out.regions = each(get(player, "Waypoints"), function(region)
        local names, n = each(get(region, "WaypointList"), function(waypoint) return text(get(waypoint, "Name")) or "?" end)
        waypoints = waypoints + n
        return { region = locaKey(get(region, "RegionName")) or "", waypoints = names }
    end)
    out.waypoints = waypoints
    local byType = {}
    local markers, total = each(get(map, "Markers"), function(marker)
        local kind = plain(property(marker, "Type")) or "?"
        byType[tostring(kind)] = (byType[tostring(kind)] or 0) + 1
        if kind == "Player" then return nil end
        return { type = kind, name = text(get(marker, "Name")) }
    end)
    out.markers = { count = count(get(map, "Markers")), byType = byType, named = markers, namedCount = total }
    local selectedMarker = get(player, "SelectedMapMarker")
    if selectedMarker then out.selectedMarker = text(get(selectedMarker, "Name")) end
    out.travelError = plain(get(player, "TravelError"))
    return out
end)

-- inventory: CharacterPanel.xaml + CharacterSheetTemplates_k.xaml (the inventory page) ---------
-- Character: CurrentPlayer.SelectedCharacter {Name, Inventory (VMInventory: Gold, Slots -> Object
-- = VMItem {Name, Count, Equipped, IsStolen, IsNew}), EncumbranceStats {CurrentWeight,
-- WeightMaximum, State}, Equipment.<Slot>.Item (Helmet ... Underwear, the slot names of the XAML)}.
-- Party view: CurrentPlayer.PartyInventory.Slots (bound as DataContext.CurrentPlayer.PartyInventory.Slots)
-- and CurrentPlayer.PartyGold; PlayerCharacterProperties.UsePartyInventory says which one is shown.

local EQUIPMENT_SLOTS = {
    "Helmet", "Cloak", "Breast", "Gloves", "Boots", "Amulet", "Ring", "Ring2",
    "MeleeMainHand", "MeleeOffHand", "RangedMainHand", "RangedOffHand",
    "LightSource", "MusicalInstrument", "VanityBody", "VanityBoots", "Underwear",
}

local function equipmentOf(character)
    local equipment = get(character, "Equipment")
    local out = {}
    for _, slot in ipairs(EQUIPMENT_SLOTS) do
        local item = path(equipment, slot .. ".Item")
        if item then out[slot] = itemName(item) or "?" end
    end
    return out
end

W.inventory.read = reader(function(data)
    local player = get(data, "CurrentPlayer")
    local character = get(player, "SelectedCharacter")
    local inventory = get(character, "Inventory")
    local out = { character = characterName(character) }
    out.gold = num(get(inventory, "Gold"))
    out.partyGold = num(get(player, "PartyGold"))
    local encumbrance = get(character, "EncumbranceStats")
    out.weight = num(get(encumbrance, "CurrentWeight"))
    out.maxWeight = num(get(encumbrance, "WeightMaximum"))
    out.encumbrance = plain(get(encumbrance, "State"))
    local items, total = inventoryItems(inventory)
    out.items, out.itemCount = items, total
    out.equipment = equipmentOf(character)
    out.partyView = path(character, "PlayerCharacterProperties.UsePartyInventory") == true
    local party, partyTotal = inventoryItems(get(player, "PartyInventory"))
    out.party = { items = party, itemCount = partyTotal }
    out.sort = text(path(character, "PlayerCharacterProperties.CurrentInventorySortOption.Name"))
    return out
end)

-- character: CharacterSheetTemplates_k.xaml (the character sheet) -----------------------------
-- SelectedCharacter.Stats (gui::VMCharacterStats): Race.DisplayName, Background, God, ClassList ->
-- VMClass {ClassDisplayName, SubclassDisplayName, Level.Value}, Level {Value, XPForNextLevel,
-- LevelUpAvailable}, Experience, Health, TemporaryHealth, ArmorClass, Initiative, MovementResource,
-- ProficiencyBonus, DarkvisionRange, Abilities -> VMAbility {Ability, Value, Modifier,
-- HasSavingThrowProficiency, SavingThrow.AdditionalValue, Skills -> VMSkill {Skill, Value,
-- IsProficient, IsExpert}}, Resistances -> VMResistance {DamageType, NonMagical, Magical, Full},
-- Passives (features), the attack block (MeleeAttackBonus, MeleeAttackDamageRoll ...).
-- SelectedCharacter.StatusEffects -> VMStatus {Name, StatusId, Duration}; Tags -> VMTag.Name;
-- PlayerCharacterProperties.ApprovalRatings -> {Character.Name, Rating}.

local function abilitiesOf(stats)
    local abilities, saves, skills = {}, {}, {}
    each(get(stats, "Abilities"), function(ability)
        local id = plain(get(ability, "Ability")) or plain(get(ability, "IDString"))
        if not id or id == "None" then return nil end
        abilities[id] = { score = num(get(ability, "Value")), mod = num(get(ability, "Modifier")) }
        if get(ability, "IsPrimary") == true then abilities[id].primary = true end
        saves[id] = { bonus = num(path(ability, "SavingThrow.AdditionalValue")),
                      proficient = get(ability, "HasSavingThrowProficiency") == true }
        each(get(ability, "Skills"), function(skill)
            local name = plain(get(skill, "Skill")) or plain(get(skill, "IDString"))
            if not name then return nil end
            local entry = { bonus = num(get(skill, "Value")) }
            if get(skill, "IsProficient") == true then entry.proficient = true end
            if get(skill, "IsExpert") == true then entry.expert = true end
            skills[name] = entry
            return true
        end)
        return true
    end)
    return abilities, saves, skills
end

local function attack(stats, prefix)
    local bonus = num(path(stats, prefix .. "AttackBonus.Value"))
    local roll = get(stats, prefix .. "AttackDamageRoll")
    if type(roll) ~= "string" or roll == "-" then return nil end
    return { bonus = bonus, damage = roll, type = text(get(stats, prefix .. "AttackDamageTypeString")) }
end

W.character.read = reader(function(data)
    local character = selectedCharacter(data)
    local stats = get(character, "Stats")
    local out = { name = characterName(character) }
    out.race = text(path(stats, "Race.DisplayName"))
    out.background = text(get(stats, "Background"))
    out.deity = text(get(stats, "God"))
    out.classes = each(get(stats, "ClassList"), function(class)
        return { class = text(get(class, "ClassDisplayName")) or plain(get(class, "IDString")) or "?",
                 subclass = text(get(class, "SubclassDisplayName")), level = num(path(class, "Level.Value")) }
    end, 12)
    local level = get(stats, "Level")
    out.level = num(get(level, "Value"))
    out.xp = stat(get(stats, "Experience"))
    out.xpNext = num(get(level, "XPForNextLevel"))
    if get(level, "LevelUpAvailable") == true then out.levelUp = true end
    out.hp = range(get(stats, "Health"))
    local temporary = stat(get(stats, "TemporaryHealth"))
    if temporary and temporary > 0 then out.tempHp = temporary end
    out.ac = stat(get(stats, "ArmorClass"))
    out.initiative = stat(get(stats, "Initiative"))
    out.movement = range(get(stats, "MovementResource"))
    out.proficiency = stat(get(stats, "ProficiencyBonus"))
    out.darkvision = stat(get(stats, "DarkvisionRange"))
    out.alive = get(stats, "IsAlive")
    out.abilities, out.saves, out.skills = abilitiesOf(stats)
    out.attacks = { melee = attack(stats, "Melee"), offhandMelee = attack(stats, "OffHandMelee"),
                    ranged = attack(stats, "Ranged"), offhandRanged = attack(stats, "OffHandRanged") }
    out.resistances = each(get(stats, "Resistances"), function(resistance)
        local nonMagical, magical, full = plain(get(resistance, "NonMagical")), plain(get(resistance, "Magical")), plain(get(resistance, "Full"))
        if (nonMagical or "None") == "None" and (magical or "None") == "None" and (full or "None") == "None" then return nil end
        return { type = plain(get(resistance, "DamageType")) or "?", nonMagical = nonMagical, magical = magical, full = full }
    end)
    out.conditions = each(get(character, "StatusEffects"), function(status)
        return { name = text(get(status, "Name")) or plain(get(status, "StatusId")) or "?", id = plain(get(status, "StatusId")),
                 turns = num(get(status, "Duration")) }
    end)
    out.features = each(get(stats, "Passives"), function(passive) return text(get(passive, "Name")) end)
    out.tags = each(get(character, "Tags"), function(tag) return text(get(tag, "Name")) end, 15)
    out.armorProficiency = each(get(stats, "ProtectionProficiencies"), function(p) return plain(get(p, "ProficiencyType")) end, 10)
    out.weaponProficiency = { simple = count(get(stats, "SimpleWeaponProficiencies")),
                              martial = count(get(stats, "MartialWeaponProficiencies")) }
    local approvals = each(path(character, "PlayerCharacterProperties.ApprovalRatings"), function(rating)
        return { name = characterName(get(rating, "Character")) or "?", rating = num(get(rating, "Rating")) }
    end, 12)
    if #approvals > 0 then out.approval = approvals end
    return out
end)

-- spellbook: CharacterSheetTemplates_k.xaml (the spellbook page) -----------------------------
-- SelectedCharacter.PlayerCharacterProperties.SpellBooks -> VMSpellBook {SpellBookType, Class
-- (VMClass: ClassDisplayName, SpellCastAbility.Ability, SpellDifficultyCheck.Value,
-- SpellAttackBonus.Value, MustPrepareSpells), PreparedSpellsStat {Value, Max}, ActionGroups ->
-- VMActionGroup {GroupType (Name, GroupType), GroupResources -> VMActionResource {Level, Value,
-- MaxValue} = spell slots, Actions -> VMCharacterAction {Name, NeedsActivation, Activated}}}.
-- Spell slots overall: SelectedCharacter.Stats.SpellSlots.

local function resourceSummary(resource)
    return { name = text(get(resource, "Name")), level = num(get(resource, "Level")),
             value = num(get(resource, "Value")), max = num(get(resource, "MaxValue")) }
end

W.spellbook.read = reader(function(data)
    local character = selectedCharacter(data)
    local properties = get(character, "PlayerCharacterProperties")
    local out = { character = characterName(character) }
    out.books = each(get(properties, "SpellBooks"), function(book)
        local class = get(book, "Class")
        local entry = { type = plain(get(book, "SpellBookType")) }
        if class then
            entry.class = text(get(class, "ClassDisplayName"))
            local ability = plain(path(class, "SpellCastAbility.Ability"))
            if ability and ability ~= "None" then entry.castingAbility = ability end
            local dc = num(path(class, "SpellDifficultyCheck.Value"))
            if dc and dc > 0 then entry.saveDC = dc end
            local bonus = num(path(class, "SpellAttackBonus.Value"))
            if bonus and dc and dc > 0 then entry.attackBonus = bonus end
            if get(class, "MustPrepareSpells") == true then entry.mustPrepare = true end
        end
        local prepared = get(book, "PreparedSpellsStat")
        if (num(get(prepared, "Max")) or -1) >= 0 then entry.prepared = range(prepared) end
        entry.groups = each(get(book, "ActionGroups"), function(group)
            local unprepared = {}
            local spells = each(get(group, "Actions"), function(action)
                local name = text(get(action, "Name")) or plain(get(action, "PrototypeID"))
                if name and get(action, "NeedsActivation") == true and get(action, "Activated") ~= true then
                    unprepared[#unprepared + 1] = name
                end
                return name
            end, 25)
            if #spells == 0 then return nil end
            local g = { name = text(path(group, "GroupType.Name")), kind = plain(path(group, "GroupType.GroupType")), spells = spells }
            local slots = each(get(group, "GroupResources"), resourceSummary, 6)
            if #slots > 0 then g.slots = slots end
            if #unprepared > 0 then g.unprepared = unprepared end
            return g
        end, 20)
        if #entry.groups == 0 and not class then return nil end
        return entry
    end, 8)
    out.slots = each(path(character, "Stats.SpellSlots"), resourceSummary, 12)
    out.canLearnSpells = path(character, "Stats.CanLearnSpells") == true
    return out
end)

-- alchemy: CharacterSheetTemplates_k.xaml (the alchemy page; DCAlchemy is the CharacterPanel's data) --
-- Data.PotionRecipes / ElixirRecipes / GrenadeRecipes / CoatingRecipes / Extract*Recipes ->
-- VMRecipesCollection {TotalCraftableRecipes, Recipes -> VMRecipe {Result.Item.NameAlchemy,
-- CanBeCrafted, IsNew}}; ExtractAir ... ExtractWater .Item.Count; SelectedRecipe {Result,
-- BaseExtract}; HideAvailableRecipes, ItemsToCraft, TotalAvailableExtractions.

local RECIPE_GROUPS = {
    { "potions", "PotionRecipes" }, { "elixirs", "ElixirRecipes" }, { "grenades", "GrenadeRecipes" },
    { "coatings", "CoatingRecipes" }, { "extractAir", "ExtractAirRecipes" }, { "extractEarth", "ExtractEarthRecipes" },
    { "extractFey", "ExtractFeyRecipes" }, { "extractFire", "ExtractFireRecipes" },
    { "extractShadow", "ExtractShadowRecipes" }, { "extractWater", "ExtractWaterRecipes" },
}

W.alchemy.read = reader(function(data)
    local out = { hideUnavailable = get(data, "HideAvailableRecipes") == true,
                  itemsToCraft = num(get(data, "ItemsToCraft")), extractions = num(get(data, "TotalAvailableExtractions")) }
    local groups = {}
    for _, g in ipairs(RECIPE_GROUPS) do
        local collection = path(data, "Data." .. g[2])
        if collection then
            local recipes, total = each(get(collection, "Recipes"), function(recipe)
                local entry = { name = itemName(path(recipe, "Result.Item")) or "?" }
                if get(recipe, "CanBeCrafted") == true then entry.craftable = true end
                if get(recipe, "IsNew") == true then entry.new = true end
                return entry
            end, 20)
            if total > 0 then
                groups[g[1]] = { recipes = recipes, count = total, craftable = num(get(collection, "TotalCraftableRecipes")) }
            end
        end
    end
    out.recipes = groups
    local extracts = {}
    for _, kind in ipairs({ "Air", "Earth", "Fey", "Fire", "Shadow", "Water" }) do
        local n = num(path(data, "Extract" .. kind .. ".Item.Count"))
        if n then extracts[kind] = n end
    end
    out.extracts = extracts
    local selected = get(data, "SelectedRecipe")
    if selected then
        out.selected = { result = itemName(path(selected, "Result.Item")), base = itemName(path(selected, "BaseExtract.Item")),
                         craftable = get(selected, "CanBeCrafted") == true }
    end
    return out
end)

-- reactions: CharacterSheetTemplates_k.xaml (the reactions page) ------------------------------
-- SelectedCharacter.PlayerCharacterProperties.Interrupts -> VMInterrupt {Name, Enabled, Ask,
-- Available, CanUse, Costs -> VMActionResource.Name}.

W.reactions.read = reader(function(data)
    local character = selectedCharacter(data)
    local out = { character = characterName(character) }
    out.reactions = each(path(character, "PlayerCharacterProperties.Interrupts"), function(interrupt)
        local entry = { name = text(get(interrupt, "Name")) or plain(get(interrupt, "PrototypeID")) or "?",
                        enabled = get(interrupt, "Enabled") == true, ask = get(interrupt, "Ask") == true }
        if get(interrupt, "Available") == false then entry.available = false end
        if get(interrupt, "CanUse") == false then entry.canUse = false end
        local costs = each(get(interrupt, "Costs"), function(cost) return text(get(cost, "Name")) end, 4)
        if #costs > 0 then entry.cost = costs end
        return entry
    end)
    return out
end)

-- party: PartyPanel.xaml --------------------------------------------------------------------
-- Columns: Data.PartyCharacters -> Character {Name, Stats.Level, Stats.Health, Inventory.Gold,
-- CharacterType, PlayerCharacterProperties.IsSelectedCharacter, Equipment}; merged inventory:
-- DataContext.CurrentPlayer.PartyInventory (InventoriesMergerBehavior); groups (chains):
-- CurrentPlayer.PartyGroups -> VMCharacterGroup.Characters.

W.party.read = reader(function(data)
    local player = get(data, "CurrentPlayer")
    local out = {}
    out.members = each(path(data, "Data.PartyCharacters"), function(character)
        local entry = { name = characterName(character) or "?", level = num(path(character, "Stats.Level.Value")),
                        hp = range(path(character, "Stats.Health")), gold = num(path(character, "Inventory.Gold")),
                        kind = plain(get(character, "CharacterType")), items = count(path(character, "Inventory.Slots")),
                        player = num(get(character, "OwnerPlayerId")) }
        if path(character, "PlayerCharacterProperties.IsSelectedCharacter") == true then entry.selected = true end
        return entry
    end, 12)
    out.groups = each(get(player, "PartyGroups"), function(group)
        return each(get(group, "Characters"), function(character) return characterName(character) or "?" end, 12)
    end, 12)
    out.gold = num(get(player, "PartyGold"))
    out.supplies = num(get(player, "PartySupplies"))
    local items, total = inventoryItems(get(player, "PartyInventory"))
    out.inventory = { items = items, itemCount = total }
    return out
end)

-- illithid: TadpolePowersTree.xaml ------------------------------------------------------------
-- CurrentPlayer.SelectedCharacter.PlayerCharacterProperties {TadpolePowers -> VMTadpolePower
-- {Power.Name, State}, TadpoledState, TadpoleTreeState}; CurrentPlayer.PartyTadpoleCount;
-- IsInPreparationStage.

W.illithid.read = reader(function(data)
    local character = selectedCharacter(data)
    local properties = get(character, "PlayerCharacterProperties")
    local out = { character = characterName(character), tadpoled = plain(get(properties, "TadpoledState")),
                  tree = plain(get(properties, "TadpoleTreeState")), tadpoles = num(path(data, "CurrentPlayer.PartyTadpoleCount")),
                  preparing = get(data, "IsInPreparationStage") == true }
    local states = {}
    out.powers = each(get(properties, "TadpolePowers"), function(power)
        local state = plain(get(power, "State")) or "?"
        states[state] = (states[state] or 0) + 1
        return { name = text(path(power, "Power.Name")) or plain(path(power, "Power.PassiveName")) or "?", state = state }
    end)
    out.states = states
    return out
end)

-- inspiration: JournalInspiration.xaml (DCJournalInspiration) -----------------------------------
-- List: Data.BackgroundGoals -> VMBackground {Title, Description, BackgroundOwners -> VMGoalOwner.Name,
-- GoalCategories -> JournalItemContainer.ContainerChildren -> VMBackgroundGoal {Title, Description,
-- GoalOwners}}. The selection is the ListBox "BackgroundsGoalsList".SelectedItem (XAML state).
-- Inspiration points: SelectedCharacter.Stats.InspirationPointsResource.

W.inspiration.read = reader(function(data, ui)
    local out = {}
    out.backgrounds = each(path(data, "Data.BackgroundGoals"), function(background)
        local goals = {}
        each(get(background, "GoalCategories"), function(category)
            each(get(category, "ContainerChildren"), function(goal)
                if #goals < 20 then
                    goals[#goals + 1] = { title = text(get(goal, "Title")) or "?",
                                          by = each(get(goal, "GoalOwners"), function(o) return text(get(o, "Name")) end, 6) }
                end
                return true
            end)
            return true
        end)
        return { title = text(get(background, "Title")) or "?",
                 owners = each(get(background, "BackgroundOwners"), function(o) return text(get(o, "Name")) end, 6),
                 goals = goals }
    end, 20)
    out.points = range(path(selectedCharacter(data), "Stats.InspirationPointsResource"))
    local list = element(ui, "inspiration", "BackgroundsGoalsList")
    local selected = list and get(list, "SelectedItem")
    if selected then out.selected = text(get(selected, "Title")) end
    return out
end)

-- dialogues: JournalDialogues.xaml (DCJournalDialogues) -----------------------------------------
-- GroupingTab ("ByDate" / "ByMap") picks DialoguesGroupedByDate or DialoguesGroupedByMap: a flat list
-- of JournalDialogueDayGroup {DayOfMonth, MonthName}, JournalDialogueMapGroup {Map} and
-- JournalDialogue {TimeInDay, Participants -> DialogueParticipant.Name}. Right side: SelectedDialogue
-- {DialogFileName, Participants, DialogueLines -> JournalDialogueLine {SpeakerName, Text,
-- RollSkill, RollAbility, RollIsSuccess}}.

local function participants(dialogue)
    return each(get(dialogue, "Participants"), function(p)
        if path(p, "IsNarrator") == true then return nil end
        return text(get(p, "Name"))
    end, 8)
end

W.dialogues.read = reader(function(data)
    local tab = plain(get(data, "GroupingTab")) or "ByDate"
    local out = { grouping = tab }
    local list = get(data, tab == "ByMap" and "DialoguesGroupedByMap" or "DialoguesGroupedByDate")
    local dialogues = 0
    out.entries = each(list, function(entry)
        local kind = typeOf(entry)
        if kind:find("DayGroup", 1, true) then
            return { day = tostring(plain(get(entry, "DayOfMonth")) or "?") .. " " .. tostring(text(get(entry, "MonthName")) or plain(get(entry, "MonthName")) or "") }
        elseif kind:find("MapGroup", 1, true) then
            return { map = text(get(entry, "Map")) or "?" }
        end
        dialogues = dialogues + 1
        return { time = plain(get(entry, "TimeInDay")), with = participants(entry) }
    end)
    out.dialogues = dialogues
    local selected = get(data, "SelectedDialogue")
    if selected then
        local lines, total = each(get(selected, "DialogueLines"), function(line)
            local speaker = text(get(line, "SpeakerName")) or text(path(line, "Speaker.Name"))
            local said = text(get(line, "Text")) or ""
            local roll = plain(get(line, "RollSkill")) or plain(get(line, "RollAbility"))
            if roll and roll ~= "None" and roll ~= "Sentinel" then
                said = said .. " [" .. tostring(roll) .. (get(line, "RollIsSuccess") == true and " success]" or " fail]")
            end
            return speaker and (speaker .. ": " .. said) or said
        end)
        out.selected = { file = plain(get(selected, "DialogFileName")), with = participants(selected), lines = lines, lineCount = total }
    end
    return out
end)

-- tutorials: JournalTutorials.xaml (DCJournalTutorial) ------------------------------------------
-- TutorialsContainers -> TutorialContainer {Section (SectionValue is the title), HasShownTutorials,
-- Tutorials -> TutorialView {Title, HasBeenShown, IsNewTutorial, IsSelected}}. Unless
-- CurrentPlayer.ShowAllTutorials, tutorials not yet shown and empty sections are collapsed.
-- InitialSelection is the tutorial selected on open.

W.tutorials.read = reader(function(data)
    local showAll = path(data, "CurrentPlayer.ShowAllTutorials") == true
    local out = { showAll = showAll }
    local shown, selected = 0, nil
    out.sections = each(get(data, "TutorialsContainers"), function(container)
        local fresh = 0
        local titles = each(get(container, "Tutorials"), function(tutorial)
            if get(tutorial, "IsSelected") == true then selected = text(get(tutorial, "Title")) end
            if not showAll and get(tutorial, "HasBeenShown") ~= true then return nil end
            if get(tutorial, "IsNewTutorial") == true then fresh = fresh + 1 end
            return text(get(tutorial, "Title")) or "?"
        end, 25)
        if #titles == 0 then return nil end
        shown = shown + #titles
        local entry = { section = plain(get(container, "Section")) or "?", tutorials = titles }
        if fresh > 0 then entry.new = fresh end
        return entry
    end, 20)
    out.shown = shown
    out.selected = selected or text(path(data, "InitialSelection.Title"))
    return out
end)

return true
