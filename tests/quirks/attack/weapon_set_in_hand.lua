-- A melee attack's chance is worked out with the weapon set in hand: asked while the bow is in
-- hand, the game counts the main-hand attack without the dagger (Strength, unarmed) where the
-- attack itself, once made, is thrown with the dagger's Dexterity; and Dirty Trick: Sand Toss,
-- cast with the ranged set in hand, throws its attack with Strength. bg3.wiki, "List of bugged
-- actions", "Dirty Trick: Sand Toss": with a ranged weapon active the attack roll uses the
-- Strength modifier; research/studies/rolls.md, section 22, for the chance. A melee weapon attack
-- uses the melee weapon's ability (bg3.wiki, "Attack rolls"; a finesse weapon the higher of
-- Strength and Dexterity). Found in build 4.1.1.7631656; when this fails, the game changed.
-- The numbers are not constants: the wanted bonus is worked from the host's own abilities and
-- proficiency, the chance from the difficulty and bonus of the roll the game then throws.
-- Staging (cheat): the host first, a hostile goblin beside it kept alive, the fight, the ranged
-- set put in hand, the casts. Reading: observation.
-- Run: gatekit --instance <copy> test tests -k quirks/attack/weapon_set_in_hand

test { level = "GustavX:Basic_Level_A", before = "stage", surprises = { "combat_started" },
           about = "A melee attack's chance is worked out with the weapon set in hand." }

local function uuid(id) return id and string.match(id, "(%x+%-%x+%-%x+%-%x+%-%x+)$") or id end

local host, goblin, abilities

local function mod(score) return math.floor((score - 10) / 2) end
-- the chance of one d20 against a difficulty with a bonus: a 1 always misses, a 20 always hits
local function chance(dc, bonus) return math.max(1, math.min(19, 21 - (dc - bonus))) / 20 end

function stage(game)
    game.wait_state("Running", "server", 120)
    host = uuid(game.server("return Osi.GetHostCharacter()"))
    step("the game answers a chance", "observation", function()
        need(game.server("return GK.Verbs.Lacks('Rolls.Chance') == nil"), "this Script Extender build asks the game for a chance")
        check(game.server("return GK.RollLog.Available()"), "this Script Extender build throws the roll event")
        game.server("return GK.EventLog.On('dice', { seconds = 600 })")
        abilities = game.server(string.format([[
            local e = Ext.Entity.Get(%q)
            return { str = e.Stats.Abilities[2], dex = e.Stats.Abilities[3], prof = e.Stats.ProficiencyBonus }
        ]], host))
        need(mod(abilities.dex) > mod(abilities.str), "the host's Dexterity is above its Strength, so that the two can be told apart")
    end)
    local staged = game.mark()
    goblin = uuid(step("a goblin beside the host, the fight, the bow in hand", "cheat", function()
        local g = game.server([[
            local h = Osi.GetHostCharacter()
            GK.Rolls.Pin{ kind = "Initiative", n = 100 }
            local x, y, z = Osi.GetPosition(h)
            local g = GK.Stage.Spawn{ template = "goblin", at = { x + 1.5, y, z }, faction = "enemy" }[1]
            GK.Stage.Immortal(true, g)
            return g
        ]])
        game.wait("client", string.format("Ext.Entity.Get(%q) ~= nil", g), 10)
        game.server(string.format("return GK.Stage.Fight({ %q })", g))
        game.server(string.format("Ext.Entity.Get(%q).WeaponSet.WeaponSet = 'Ranged' return true", host))
        return g
    end))
    step("the scene is as staged", "observation", function()
        local turn = row(staged, "game_turn", function(p) return p.who ~= nil end, 60)
        need(uuid(turn.payload.who) == host, "the host has the first turn")
        eq(game.server(string.format("return tostring(Ext.Entity.Get(%q).WeaponSet.WeaponSet)", host)), "Ranged", "the bow in hand")
    end)
end

local function attack_row(game, mark, spell)
    return row(mark, "dice_thrown", function(p) return uuid(p.who) == host and p.spell == spell and string.match(p.kind or "", "Attack$") end, 30).payload
end

case("melee_asked_with_the_bow", function(game)
    local asked = step("the game's chance, with each set", "observation", function()
        local function ask(set)
            local a = game.server(string.format("return GK.Rolls.Chance{ who = %q, spell = 'Target_MainHandAttack', target = %q, set = %q }", host, goblin, set))
            check(a.ok, "the game answers", a)
            return a.chance
        end
        return { bow = ask("Ranged"), dagger = ask("Melee") }
    end)
    local mark = game.mark()
    step("the attack, made", "cheat", function()
        local c = game.server(string.format("return GK.Cheat.Cast(%q, 'Target_MainHandAttack', %q)", host, goblin))
        check(c and c.job, "the cast is asked", c)
    end)
    step("the bow's number is the attack without the dagger", "observation", function()
        local p = attack_row(game, mark, "Target_MainHandAttack")
        eq(p.bonus, mod(abilities.dex) + abilities.prof, "the attack thrown: the dagger's Dexterity and proficiency")
        check(math.abs(asked.dagger - chance(p.dc, p.bonus)) < 0.0001, "with the melee set the chance is the dice's", asked, p.dc, p.bonus)
        local unarmed = p.bonus - mod(abilities.dex) + mod(abilities.str)
        check(math.abs(asked.bow - chance(p.dc, unarmed)) < 0.0001, "with the bow in hand the chance counts Strength in place of Dexterity", asked, p.dc, unarmed)
        print(("asked with the bow %.4f, with the dagger %.4f; thrown against %d with +%d"):format(asked.bow, asked.dagger, p.dc, p.bonus))
    end)
end)

case("sand_toss", function(game)
    step("Sand Toss known", "cheat", function()
        game.server(string.format("GK.Stage.Teach('Target_DirtyTrick_Blind', %q) return true", host))
        game.wait("server", string.format("Osi.HasSpell(%q, 'Target_DirtyTrick_Blind') == 1", host), 10)
    end)
    local mark = game.mark()
    step("Sand Toss with the bow in hand", "cheat", function()
        local c = game.server(string.format("return GK.Cheat.Cast(%q, 'Target_DirtyTrick_Blind', %q)", host, goblin))
        check(c and c.job, "the cast is asked", c)
    end)
    step("its attack is thrown with Strength", "observation", function()
        local p = attack_row(game, mark, "Target_DirtyTrick_Blind")
        eq(p.kind, "MeleeWeaponAttack", "a melee weapon attack")
        eq(p.bonus, mod(abilities.str) + abilities.prof, "Strength and proficiency, not the dagger's Dexterity")
        print(("Sand Toss with the bow in hand: +%d (Strength %d, Dexterity %d, proficiency %d)"):format(p.bonus, abilities.str, abilities.dex, abilities.prof))
    end)
end)
