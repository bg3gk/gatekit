namespace GateKit.UnitTests;

/// <summary>
/// The `game_hit` row (GK.HitRow, GK.HitAttack, GK.HitSaves in Shared/Bridge.lua) from hits shaped
/// as the game's HitDesc: an attack roll that hit, a saving throw saved against, a miss.
/// </summary>
public sealed class HitRowTests
{
    const string Hits = """
        local function roll(kind, natural, total, critical, extra)
            local c = { Roll = { Roll = { RollType = kind }, Result = { NaturalRoll = natural, Total = total, Critical = critical or "None" } } }
            for k, v in pairs(extra or {}) do c[k] = v end
            return c
        end
        Names = { g = "Goblin", h = "Astarion" }
        local function name(id) return Names[id] end
        Struck = GK.HitRow({ EffectFlags = { "Hit" }, TotalDamageDone = 6, DamageType = "Piercing", SpellId = "Target_MainHandAttack",
            CauseType = "Attack", ConditionRolls = { roll("MeleeWeaponAttack", 20, 25, "Success", { Difficulty = 15 }) } }, "g", "h", name)
        Saved = GK.HitRow({ EffectFlags = { "SavingThrow", "Hit" }, TotalDamageDone = 3, DamageType = "Fire", SpellId = "Projectile_Fireball",
            CauseType = "Spell", ConditionRolls = { roll("SavingThrow", 17, 19, nil, { Ability = "Dexterity", Difficulty = 13 }) } }, "g", "h", name)
        Missed = GK.HitRow({ EffectFlags = { "Miss", "Dodge" }, TotalDamageDone = 0, DamageType = "None", SpellId = "",
            CauseType = "Attack", ConditionRolls = { roll("RangedWeaponAttack", 2, 7, nil, { Difficulty = 15 }) } }, "g", nil, name)
        """;

    static MoonSharp.Interpreter.Script Game()
    {
        var game = BridgeLuaTests.Game();
        game.DoString(Hits);
        return game;
    }

    [Fact]
    public void An_attack_that_hits_carries_its_roll_against_the_armour_class()
    {
        var game = Game();
        Assert.Equal("Astarion - hit - Goblin with Target_MainHandAttack: 6 piercing", game.DoString("return Struck.text").String);
        Assert.Equal("MeleeWeaponAttack", game.DoString("return Struck.attack.kind").String);
        Assert.Equal(15, game.DoString("return Struck.attack.ac").Number);
        Assert.Equal(20, game.DoString("return Struck.attack.natural").Number);
        Assert.Equal("Success", game.DoString("return Struck.attack.critical").String);
        Assert.True(game.DoString("return Struck.saves == nil").Boolean);
    }

    [Fact]
    public void A_saving_throw_is_read_from_the_hit_and_an_ordinary_roll_names_no_critical()
    {
        var game = Game();
        Assert.Equal("Dexterity", game.DoString("return Saved.saves[1].ability").String);
        Assert.Equal(13, game.DoString("return Saved.saves[1].dc").Number);
        Assert.Equal(19, game.DoString("return Saved.saves[1].total").Number);
        Assert.True(game.DoString("return Saved.saves[1].critical == nil").Boolean);
        Assert.True(game.DoString("return Saved.attack == nil").Boolean);
        Assert.Equal("Hit|SavingThrow", game.DoString("return Saved.flags").String);
    }

    [Fact]
    public void A_miss_does_no_damage_and_names_no_spell_and_an_unknown_inflicter()
    {
        var game = Game();
        Assert.Equal("miss", game.DoString("return Missed.outcome").String);
        Assert.Equal("? - miss - Goblin", game.DoString("return Missed.text").String);
        Assert.True(game.DoString("return Missed.spell == nil and Missed.damageType == nil").Boolean);
        Assert.Equal(2, game.DoString("return Missed.attack.natural").Number);
    }
}
