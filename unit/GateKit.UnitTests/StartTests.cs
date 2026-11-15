using GateKit.Copies;

namespace GateKit.UnitTests;

/// <summary>One start of a copy at a time, and a start without the bridge said as what happened.</summary>
public sealed class StartTests : IAsyncLifetime
{
    readonly FakeGame _game = new();
    readonly List<FakeBridge> _bridges = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var bridge in _bridges) await bridge.DisposeAsync();
        _game.Dispose();
    }

    const string Loaded = "Initializing client with target context Game\nLUA VM reset\n";

    [Fact]
    public void A_start_without_the_bridge_is_read_from_the_process_and_the_extenders_log()
    {
        var waited = TimeSpan.FromSeconds(80);
        Assert.Equal(StartVerdict.Exited, StartCheck.Read(null, running: false, waited, "Gwynerva_GateKit", "logs").Verdict);
        Assert.Equal(StartVerdict.NoExtender, StartCheck.Read(null, running: true, waited, "Gwynerva_GateKit", "logs").Verdict);
        Assert.Equal(StartVerdict.Loading, StartCheck.Read("Extender runtime log written\n", true, waited, "Gwynerva_GateKit", "logs").Verdict);
        var off = StartCheck.Read(Loaded + "ecl::ScriptExtender::" + StartCheck.ModuleLoaded + "\n", true, waited, "Gwynerva_GateKit", "logs");
        Assert.Equal(StartVerdict.ModsOff, off.Verdict);
        Assert.True(off.Restart);
        var silent = StartCheck.Read(Loaded + "Loading bootstrap script: Mods/Gwynerva_GateKit/ScriptExtender/Lua/BootstrapClient.lua\nboom\n",
            true, waited, "Gwynerva_GateKit", "logs");
        Assert.Equal(StartVerdict.Silent, silent.Verdict);
        Assert.False(silent.Restart);
        Assert.Contains("\"boom\"", silent.Text, StringComparison.Ordinal);
        // the bridge's own folder decides: another mod's Lua is not the bridge's
        Assert.Equal(StartVerdict.ModsOff, StartCheck.Read(Loaded + "Loading bootstrap script: Mods/Gwynerva_Weave/ScriptExtender/Lua/BootstrapClient.lua\n"
            + StartCheck.ModuleLoaded, true, waited, "Gwynerva_GateKit", "logs").Verdict);
    }

    [Fact]
    public async Task A_second_up_or_build_of_one_copy_is_refused_at_once_and_changes_nothing()
    {
        var copy = _game.GameCopy("busy");
        var packer = (CopyPacker)copy.Packer;
        Directory.CreateDirectory(copy.Layout.State);
        using (GateKit.Home.SharedFile.Lock(copy.StartLock))
        {
            var up = await Assert.ThrowsAsync<CopyException>(() => copy.UpAsync(new UpOptions(Level: "GustavX:TUT_Avernus_C")));
            Assert.StartsWith("another `up` or `build` of instance busy is under way", up.Message, StringComparison.Ordinal);
            Assert.Contains("this `up` changed nothing", up.Message, StringComparison.Ordinal);
            var build = Assert.Throws<CopyException>(() => copy.Build());
            Assert.Contains("this `build` changed nothing", build.Message, StringComparison.Ordinal);
        }
        Assert.Empty(_game.Platform.Started);
        Assert.Equal(0, packer.Packed);
        Assert.False(Directory.Exists(copy.Layout.Mods));
        // let go of, the copy starts as ever, and its lock goes with the start
        _game.Platform.OnStart = _ => Answer(copy);
        await copy.UpAsync(new UpOptions(Level: "GustavX:TUT_Avernus_C"));
        Assert.False(File.Exists(copy.StartLock + ".lock"));
        copy.Down();
    }

    [Fact]
    public void A_build_leaves_each_pak_whole_and_nothing_beside_it()
    {
        var copy = _game.GameCopy("packs");
        var installed = copy.Build();
        Assert.Equal(2, installed.Count);
        foreach (var pak in installed) Assert.True(new FileInfo(pak).Length > 0);
        var folders = installed.Select(Path.GetDirectoryName).Distinct().ToList();
        Assert.All(folders, f => Assert.DoesNotContain(Directory.EnumerateFiles(f!), n => n.EndsWith(".tmp", StringComparison.Ordinal) || n.Contains(".packing", StringComparison.Ordinal)));
        var cache = Path.Combine(copy.Settings.BuildCache!, "packs");
        Assert.Equal(["Gwynerva_GateKit.pak", "Gwynerva_Weave.pak"], Directory.EnumerateFiles(cache).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_start_with_the_mods_off_is_seen_in_the_log_and_started_again_once()
    {
        var copy = _game.GameCopy("off");
        _game.Platform.OnStart = launch =>
        {
            if (_game.Platform.Started.Count == 1)
                File.WriteAllText(Path.Combine(copy.Layout.SeLogs, Copies.GameCopy.RuntimeLogPrefix + " 1.log"), Loaded + StartCheck.ModuleLoaded + "\n");
            else Answer(copy);
        };
        await copy.UpAsync(new UpOptions(Level: "GustavX:TUT_Avernus_C"));
        Assert.Equal(2, _game.Platform.Started.Count);
        Assert.Contains("the game loaded its module with the bridge's mod off", _game.Output.ToString(), StringComparison.Ordinal);
        Assert.Contains("restarting", _game.Output.ToString(), StringComparison.Ordinal);
        copy.Down();
    }

    [Fact]
    public async Task A_game_that_exits_before_the_bridge_reports_is_said_and_not_started_again()
    {
        var copy = _game.GameCopy("gone");
        _game.Platform.OnStart = _ => _game.Platform.ExitAfterLooks = 1;
        var error = await Assert.ThrowsAsync<CopyException>(() => copy.UpAsync(new UpOptions(Level: "GustavX:TUT_Avernus_C")));
        Assert.StartsWith("the bridge did not report: the game process ended", error.Message, StringComparison.Ordinal);
        Assert.Single(_game.Platform.Started);
    }

    void Answer(Copies.GameCopy copy)
    {
        _bridges.Add(new FakeBridge(copy.Layout.Channel, "client", _ => null) { GameState = "Running" });
        _bridges.Add(new FakeBridge(copy.Layout.Channel, "server", _ => null));
    }
}
