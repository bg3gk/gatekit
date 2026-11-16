-- GK.Fight: readings of a fight a creature is in, for any creature at any moment - the player's
-- character, or anyone outside its turn (the game's AI weighs its options only on the turn of a
-- creature it controls: the family `fight` writes those as `fight_options`).
--
-- GK.Fight.Options is GateKit's own reading of a creature's spell book against a target, not the
-- game's "can cast": the game's own check (esv::SpellHelpers::CanCast) is not placed in this build
-- of the game. Nothing is cast and nothing is changed.

GK.Fight = GK.Fight or {}
local Fight = GK.Fight

--- Each verb's class, for the fair/cheat/observation split.
Fight.Class = { Options = "observation" }

local function try(fn, ...)
    local ok, a = pcall(fn, ...)
    if ok then return a end
    return nil
end

local function bare(id)
    return type(id) == "string" and (id:match("(%x+%-%x+%-%x+%-%x+%-%x+)$") or id) or id
end

-- What a creature holds of each action resource, by the resource's name, a resource of a level
-- named with it: { ["ActionPoint"] = 1, ["SpellSlot/1"] = 2 }.
local function held(entity)
    local out = {}
    for id, list in pairs(try(function() return entity.ActionResources.Resources end) or {}) do
        local label = try(function() return tostring(Ext.StaticData.Get(id, "ActionResource").Name) end) or tostring(id)
        for _, r in ipairs(list) do
            local level = try(function() return r.Level end) or 0
            local amount = try(function() return r.Amount end)
            if type(amount) == "number" then out[level > 0 and (label .. "/" .. level) or label] = amount end
        end
    end
    return out
end

-- The spell's UseCosts ("ActionPoint:1;SpellSlotsGroup:1:1:1") against what the creature holds:
-- the costs { [name] = amount } and whether every one is held. A spell slot is asked as the group
-- (SpellSlotsGroup:<count>:<amount>:<level>): any slot resource of that level held counts.
local function costs(spell, have)
    local text = tostring(try(function() return Ext.Stats.Get(spell).UseCosts end) or "")
    local out, affordable, any = {}, true, false
    for part in text:gmatch("[^;]+") do
        local fields = {}
        for f in part:gmatch("[^:]+") do fields[#fields + 1] = f:match("^%s*(.-)%s*$") end
        local resource = fields[1]
        if resource and resource ~= "" then
            any = true
            if resource == "SpellSlotsGroup" then
                local amount, level = tonumber(fields[3]) or 1, tonumber(fields[4]) or 1
                local label = "SpellSlot/" .. level
                out[label] = amount
                local slots = (have["SpellSlot/" .. level] or 0) + (have["WarlockSpellSlot/" .. level] or 0)
                if slots < amount then affordable = false end
            else
                local amount = tonumber(fields[#fields]) or 1
                local label = resource
                if #fields >= 3 and tonumber(fields[3]) then label = resource .. "/" .. fields[3] amount = tonumber(fields[2]) or 1 end
                out[label] = amount
                if (have[label] or 0) < amount then affordable = false end
            end
        end
    end
    return any and out or nil, affordable
end

-- The reach of a spell in metres: a number of its TargetRadius, else the range of the weapon its
-- radius names (MeleeMainWeaponRange, RangedMainWeaponRange: the equipped weapon's WeaponRange,
-- in centimetres in its stats).
local WEAPON_SLOT = { MeleeMainWeaponRange = "Melee Main Weapon", RangedMainWeaponRange = "Ranged Main Weapon",
                      MeleeOffHandWeaponRange = "Melee Offhand Weapon", RangedOffHandWeaponRange = "Ranged Offhand Weapon" }

local function reach(spell, who)
    local radius = tostring(try(function() return Ext.Stats.Get(spell).TargetRadius end) or "")
    local n = tonumber(radius)
    if n then return n, radius end
    local slot = WEAPON_SLOT[radius]
    if slot then
        local item = try(Osi.GetEquippedItem, who, slot)
        local stats = item and try(function() return Ext.Entity.Get(item).Data.StatsId end)
        local range = stats and try(function() return Ext.Stats.Get(stats).WeaponRange end)
        if type(range) == "number" then return range / 100, radius end
    end
    return nil, radius ~= "" and radius or nil
end

--- The spells `who` could use on `target` now, by GateKit's own reading - not the game's "can
--- cast", which is not placed in this build: each spell of its spell book with what it costs
--- (its UseCosts against the creature's action resources: `affordable`), its reach against the
--- distance now (`inRange`; Osi.GetDistanceTo, the reach from the spell's TargetRadius or the
--- equipped weapon's range), and the game's chance of its roll (Ext.Stats.GetSpellRollChance, as
--- the creature stands now: the weapon set in hand counts; for a spell with a roll). Cooldowns,
--- conditions, the line of sight and the spell's own requirements are not read.
--- { ok, who, target, distance, options = { { spell, affordable, inRange, range, distance, costs, chance } } },
--- or { ok = false, error }.
function Fight.Options(who, target)
    who, target = bare(who or Osi.GetHostCharacter()), bare(target)
    local caster, other = try(Ext.Entity.Get, who), target and try(Ext.Entity.Get, target)
    if not caster or not other then return { ok = false, error = "the creature or the target is no entity of the game" } end
    local spells = try(function() return caster.SpellBook.Spells end)
    if not spells then return { ok = false, error = "the creature has no spell book" } end
    local distance = try(Osi.GetDistanceTo, who, target)
    local have = held(caster)
    local options, seen = {}, {}
    for _, s in pairs(spells) do
        local spell = try(function() return tostring(s.Id.Prototype) end)
        if spell and spell ~= "" and not seen[spell] then
            seen[spell] = true
            local o = { spell = spell, distance = distance }
            o.costs, o.affordable = costs(spell, have)
            local range, radius = reach(spell, who)
            o.range, o.radius = range, radius
            if type(range) == "number" and type(distance) == "number" then o.inRange = distance <= range end
            local roll = try(function() return Ext.Stats.Get(spell).SpellRoll end)
            local rolled = type(roll) == "table" and tostring(roll.Default or "") or tostring(roll or "")
            if rolled ~= "" then
                local ok, chance = pcall(Ext.Stats.GetSpellRollChance, caster, spell, other)
                if ok and type(chance) == "number" then o.chance = chance end
            end
            options[#options + 1] = o
        end
    end
    table.sort(options, function(a, b) return a.spell < b.spell end)
    return { ok = true, who = who, target = target, distance = distance, options = options }
end

return Fight
