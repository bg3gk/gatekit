-- The entries of bg3.wiki's "List of bugged actions" that the game's data alone shows are still
-- in the data: each case reads the fields of one stat and asserts the formula as it is, beside
-- the tooltip's own formula where the stat has one. Found in build 4.1.1.7631656; when this fails,
-- the game changed (research/studies/rolls.md, section 23). Nothing is staged: the stats are read
-- in the running game (Ext.Stats.Get), so what is asserted is the data the game loaded.
-- Not here: Gossamer Tomb (its status lives in the Honour mode's own stats, not loaded in the
-- test level); Sand Toss's Strength (test:quirks/attack/weapon_set_in_hand, it needs a roll).
-- Reading: observation.
-- Run: gatekit --instance <copy> test tests -k quirks/spells/wiki_list

test { level = "GustavX:Basic_Level_A",
           about = "The wiki's bugged actions that the data shows are still bugged in the data." }

-- every functor of a functor field as "<TypeId> <context> <damage> | <conditions>", read in the game
local READ = [[
    local s = Ext.Stats.Get(%q)
    if not s then return nil end
    local function ctx(f) local t = {} for _, v in pairs(f.PropertyContext or {}) do t[#t + 1] = tostring(v) end table.sort(t) return table.concat(t, ",") end
    local out = { DamageType = tostring(s.DamageType), DescriptionParams = tostring(s.DescriptionParams), TooltipDamageList = tostring(s.TooltipDamageList), fields = {} }
    for _, field in ipairs({ "SpellProperties", "SpellSuccess", "SpellFail" }) do
        local list = {}
        for _, g in ipairs(s[field] or {}) do
            for _, f in ipairs(g.Functors or {}) do
                local ok, dmg = pcall(function() return f.Damage.Code end)
                local okd, dtype = pcall(function() return f.DamageType end)
                list[#list + 1] = string.format("%%s %%s %%s %%s | %%s", tostring(f.TypeId), ctx(f), ok and tostring(dmg) or "-", okd and tostring(dtype) or "-", tostring(f.StatsConditions or ""))
            end
        end
        out.fields[field] = list
    end
    return out
]]

local function read(game, name)
    local s = game.server(string.format(READ, name))
    need(s ~= nil, "the game has the stat " .. name)
    return s
end

-- the functors of a field that deal damage, as "<damage> <type>"
local function damage(list)
    local out = {}
    for _, line in ipairs(list or {}) do
        local dmg, dtype = string.match(line, "^DealDamage %S+ (%S+) (%S+)")
        if dmg then out[#out + 1] = dmg .. " " .. dtype end
    end
    return out
end

-- Firewine Explosion: "On save, targets take (4d6)/2 Fire damage instead of (2d6)/2 Fire damage,
-- which is effectively full damage."
case("firewine_explosion", function(game)
    step("the save's damage", "observation", function()
        local s = read(game, "Projectile_CRE_BreweryKobolds_FirewineBelly_Explosion")
        eq(join(damage(s.fields.SpellSuccess), "; "), "2d6 Fire", "a failed save: 2d6")
        eq(join(damage(s.fields.SpellFail), "; "), "(4d6)/2 Fire", "a made save: half of 4d6, not half of 2d6")
    end)
end)

-- Cinderous Swipe: "The version of this ability granted in wild shape form deals only 1d6 Fire
-- extra damage, not 2d6 Fire like the tooltip says."
case("cinderous_swipe_wildshape", function(game)
    step("the wild shape's fire damage", "observation", function()
        local s = read(game, "Zone_RangedSwipe_Wildshape_Myrmidon_Fire")
        check(contains(s.DescriptionParams, "2d6,Fire"), "the tooltip says 2d6 fire", s.DescriptionParams)
        local fire = {}
        for _, d in ipairs(damage(s.fields.SpellSuccess)) do if string.match(d, "Fire$") then fire[#fire + 1] = d end end
        eq(join(fire, "; "), "1d6 Fire; (1d6)/2 Fire", "the hit deals 1d6 fire (half with Evasion)")
        local myrmidon = read(game, "Zone_RangedSwipe_Myrmidon_Fire")
        check(has(damage(myrmidon.fields.SpellSuccess), "2d6 Fire"), "the myrmidon's own Cinderous Swipe deals the 2d6", myrmidon.fields.SpellSuccess)
    end)
end)

-- Gargantuan Cleave: "The tooltip shows this action applying full weapon damage when in fact it
-- deals half the weapon damage."
case("gargantuan_cleave", function(game)
    step("the weapon's damage halved", "observation", function()
        local s = read(game, "Zone_UNI_SuperheavyWeapon_Spell")
        check(contains(s.TooltipDamageList, "MainMeleeWeapon+1d6"), "the tooltip lists the whole weapon's damage", s.TooltipDamageList)
        check(has(damage(s.fields.SpellSuccess), "Placeholder0/2 None"), "the hit deals the weapon's damage over 2", s.fields.SpellSuccess)
    end)
end)

-- Divine Strike: Fire (Melee): "This attack is not correctly marked with a fire DamageType flag,
-- which means it does not trigger certain effects which check for fire damage."
case("divine_strike_fire_melee", function(game)
    step("fire dealt, no damage type", "observation", function()
        local s = read(game, "Target_DivineStrike_Melee_Nature_Fire")
        check(has(damage(s.fields.SpellSuccess), "1d8 Fire"), "it deals 1d8 fire", s.fields.SpellSuccess)
        eq(s.DamageType, "None", "the spell's DamageType")
        local ranged = read(game, "Projectile_DivineStrike_Ranged_Nature_Fire")
        eq(ranged.DamageType, "Fire", "the ranged Divine Strike: Fire is marked fire")
    end)
end)
