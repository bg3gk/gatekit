using MoonSharp.Interpreter;

namespace GateKit.UnitTests;

/// <summary>
/// GK.Stage.Damage and GK.Stage.Heal answer what the game made of them, and GK.Stage.Clean says
/// every hit point it put back: against RollsTests' stand-in world, with creatures that have hit
/// points, and a game that writes the hit's own game_hit row a tick after it applies the damage.
/// </summary>
public sealed class StageHitPointsTests
{
    const string Host = "11111111-1111-1111-1111-111111111111";

    const string HitPoints = """
        local function bare(id) return id:match("(%x+%-%x+%-%x+%-%x+%-%x+)$") or id end
        for id, c in pairs(World.creatures) do c.Health = { Hp = 10, MaxHp = 12 } end
        Osi.ApplyDamage = function(id, amount, kind)
            local c = World.creatures[bare(id)]
            local dealt = math.min(amount, c.Health.Hp)
            if c.resists then dealt = 0 end
            c.Health.Hp = c.Health.Hp - dealt
            World.after(1, function() GK.EventLog.Append("game_hit", { who = bare(id), damage = dealt, damageType = kind }) end)
        end
        -- as the game: the hit points are set a tick later
        Osi.SetHitpoints = function(id, hp) World.after(1, function() World.creatures[bare(id)].Health.Hp = hp end) end
        """;

    static Script Game()
    {
        var game = RollsTests.Game();
        game.DoString(HitPoints);
        return game;
    }

    static Table Run(Script game, string code) => game.DoString(code).Table;

    [Fact]
    public void Damage_answers_the_hit_points_before_and_after_and_the_games_own_row_of_the_hit()
    {
        var game = Game();
        var answer = Run(game, $"return Settle(GK.Stage.Damage(3, '{Host}', 'Fire', true))");
        Assert.Equal(10, answer.Get("hpBefore").Number);
        Assert.Equal(7, answer.Get("hpAfter").Number);
        Assert.Equal(3, answer.Get("dealt").Number);
        Assert.Equal("Fire", answer.Get("kind").String);
        Assert.Equal(3, answer.Get("hit").Number);
        var row = (int)answer.Get("row").Number;
        Assert.Equal("game_hit", game.DoString($"return GK.EventLog.Rows[{row}].kind").String);
        Assert.True(answer.Get("note").IsNil());
    }

    [Fact]
    public void Damage_the_game_absorbs_says_so()
    {
        var game = Game();
        game.DoString($"World.creatures['{Host}'].resists = true");
        var answer = Run(game, $"return Settle(GK.Stage.Damage(4, '{Host}', nil, true))");
        Assert.Equal(0, answer.Get("dealt").Number);
        Assert.Contains("took no hit points", answer.Get("note").String, StringComparison.Ordinal);
    }

    [Fact]
    public void Heal_answers_the_hit_points_before_and_after()
    {
        var game = Game();
        game.DoString($"World.creatures['{Host}'].Health.Hp = 4");
        var answer = Run(game, $"return Settle(GK.Stage.Heal('{Host}'))");
        Assert.Equal((4, 12, 12), (answer.Get("hpBefore").Number, answer.Get("asked").Number, answer.Get("hpAfter").Number));
        Assert.True(answer.Get("note").IsNil());
    }

    [Fact]
    public void Clean_says_the_hit_points_it_put_back_once()
    {
        // the stage ledger's undo of a damage put the hit points back and said nothing: `did` came back empty
        var game = Game();
        game.DoString("GK.EventLog.Scope = GK.EventLog.Seq + 1");
        game.DoString($"Settle(GK.Stage.Damage(5, '{Host}', nil, true))");
        var clean = Run(game, "return GK.Stage.Clean()");
        game.DoString("World.tick()");
        Assert.Equal(10, game.DoString($"return World.creatures['{Host}'].Health.Hp").Number);
        var did = clean.Get("did").Table.Values.Select(v => v.String).ToList();
        Assert.Equal([$"hit points of {Host} set back to 10 (were 5)"], did.Where(l => l.Contains("hit points", StringComparison.Ordinal)));
    }
}
