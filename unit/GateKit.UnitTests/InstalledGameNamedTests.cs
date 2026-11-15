using GateKit.Copies;

namespace GateKit.UnitTests;

/// <summary>The rule that no command writes into the installed game unless it is named: without an
/// instance every writing command refuses, in one sentence and before anything is touched, unless
/// `--installed-game` is given (<see cref="CopyLayout.RequireNamed"/>). Only the fakes are used.</summary>
public sealed class InstalledGameNamedTests : IDisposable
{
    readonly FakeGame _game = new();

    public void Dispose() => _game.Dispose();

    Copies.GameCopy Unnamed() => new(_game.Settings(installedGame: false), _game.Platform, _game.Output, new CopyPacker());

    /// <summary>Every writing command of a copy, as the commands call it.</summary>
    public static TheoryData<string> Verbs() =>
        ["build", "up", "down", "exec", "key", "link", "reload", "watch", "checkpoint", "reset"];

    static Task Act(Copies.GameCopy copy, string verb) => verb switch
    {
        "build" => Task.Run(() => copy.Build()),
        "up" => copy.UpAsync(new UpOptions()),
        "down" => Task.Run(() => copy.Down()),
        "exec" => copy.ExecAsync("server", "return 1"),
        "key" => Task.Run(() => copy.Key("escape")),
        "link" => Task.Run(() => copy.Link(".")),
        "reload" => copy.ReloadAsync(),
        "watch" => copy.WatchAsync(CancellationToken.None),
        "checkpoint" => copy.Session().CheckpointAsync(),
        "reset" => copy.Session().ResetAsync(),
        _ => throw new ArgumentException(verb),
    };

    [Theory]
    [MemberData(nameof(Verbs))]
    public async Task A_writing_command_refuses_the_installed_game_nobody_named(string verb)
    {
        var copy = Unnamed();
        var before = File.ReadAllText(copy.Layout.ModSettingsFile);
        var refused = await Assert.ThrowsAsync<CopyException>(() => Act(copy, verb));
        Assert.Equal(CopyLayout.NotNamed(verb), refused.Message);
        Assert.Contains("--installed-game", refused.Message, StringComparison.Ordinal);
        // refused before anything was touched: no backup, no owner record, the mod list as it was
        Assert.False(Directory.Exists(copy.Layout.Backup));
        Assert.False(File.Exists(Path.Combine(copy.Layout.State, "owner.json")));
        Assert.Equal(before, File.ReadAllText(copy.Layout.ModSettingsFile));
    }

    [Theory]
    [MemberData(nameof(Verbs))]
    public void Named_installed_game_or_an_instance_passes_the_check(string verb)
    {
        new Copies.GameCopy(_game.Settings(installedGame: true), _game.Platform, _game.Output).RequireNamed(verb);
        new Copies.GameCopy(_game.Settings("named", installedGame: false), _game.Platform, _game.Output).RequireNamed(verb);
    }

    [Fact]
    public void The_sound_changes_are_held_to_the_same_check()
    {
        foreach (var verb in new[] { "sound set", "sound mute", "sound unmute", "sound unfocused" })
            Assert.Equal(CopyLayout.NotNamed(verb), Assert.Throws<CopyException>(() => Unnamed().RequireNamed(verb)).Message);
    }

    /// <summary>The one door every chunk goes through - a command's, a test's, an MCP tool's, the
    /// deck's - is held to the check itself, so no caller can go round it.</summary>
    [Theory]
    [InlineData("server")]
    [InlineData("client")]
    [InlineData("ui")]
    public async Task A_chunk_of_Lua_is_not_sent_to_the_installed_game_nobody_named(string side)
    {
        var refused = await Assert.ThrowsAsync<CopyException>(() => Unnamed().Session().LuaAsync(side, "return 1"));
        Assert.Equal(CopyLayout.NotNamed("a chunk of Lua for the game"), refused.Message);
    }

    [Fact]
    public void Reading_needs_no_name()
    {
        var copy = Unnamed();
        Assert.NotEmpty(copy.Status());
        Assert.NotEmpty(copy.InstanceList());
        Assert.Empty(copy.Logs(story: true));
    }
}
