using GateKit.Stand;

namespace GateKit.Tests;

public sealed class DoctorTests : IDisposable
{
    readonly FakeGame _game = new();

    public void Dispose() => _game.Dispose();

    IReadOnlyList<Doctor.Check> Run(Dictionary<string, string?> env) =>
        Doctor.Run(_game.Platform, environment: name => env.GetValueOrDefault(name), workingDirectory: _game.Temp.Path);

    /// <summary>A machine with everything the stand needs: the fixture's game, plus the stock extender.</summary>
    Dictionary<string, string?> Complete()
    {
        _game.Temp.Write("game/bin/DWrite.dll", "the stock extender's loader");
        return new Dictionary<string, string?>
        {
            ["BG3_GAME"] = _game.Temp["game"],
            ["GATEKIT_PROFILE"] = _game.Temp["profile"],
            ["GATEKIT_BRIDGE"] = _game.Temp["mods"],
            ["GATEKIT_STATE"] = _game.Temp["state"],
            ["GATEKIT_INSTANCES"] = _game.Temp["instances"],
        };
    }

    [Fact]
    public void No_script_extender_at_all_is_a_failure()
    {
        var env = Complete();
        File.Delete(_game.Temp["game/bin/DWrite.dll"]);
        var se = Run(env).Single(c => c.Name == "script extender");
        Assert.Equal(Doctor.Verdict.Bad, se.Verdict);
    }

    [Fact]
    public void A_complete_installation_is_ready()
    {
        var checks = Run(Complete());
        Assert.DoesNotContain(checks, c => c.Verdict == Doctor.Verdict.Bad);
        Assert.Equal(Doctor.Verdict.Good, checks.Single(c => c.Name == "game").Verdict);
        Assert.Equal(Doctor.Verdict.Good, checks.Single(c => c.Name == "bridge mods").Verdict);

        var text = new StringWriter();
        Assert.Equal(0, Doctor.Report(checks, text));
        Assert.Contains("ready", text.ToString());
    }

    [Fact]
    public void The_built_in_packer_is_good_and_the_stock_extender_is_only_a_warning()
    {
        var checks = Run(Complete());
        var packer = checks.Single(c => c.Name == "packer");
        Assert.Equal(Doctor.Verdict.Good, packer.Verdict);
        Assert.Contains("LSLib", packer.Detail, StringComparison.Ordinal);
        Assert.Equal(Doctor.Verdict.Warn, checks.Single(c => c.Name == "script extender").Verdict);
    }

    [Fact]
    public void Asking_for_Divine_without_having_it_is_a_failure()
    {
        var env = Complete();
        env["GATEKIT_PACKER"] = "divine";
        var packer = Run(env).Single(c => c.Name == "packer");
        Assert.Equal(Doctor.Verdict.Bad, packer.Verdict);
        Assert.Contains("GATEKIT_DIVINE", packer.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_game_is_a_failure_that_says_how_to_fix_it()
    {
        var env = Complete();
        env["BG3_GAME"] = Path.Combine(_game.Temp.Path, "nowhere");
        var checks = Run(env);
        var game = checks.Single(c => c.Name == "game");
        Assert.Equal(Doctor.Verdict.Bad, game.Verdict);
        Assert.Contains("BG3_GAME", game.Fix);

        var text = new StringWriter();
        Assert.Equal(1, Doctor.Report(checks, text));
        Assert.Contains("must be fixed", text.ToString());
    }

    [Fact]
    public void The_deck_and_the_firewall_are_reported_and_never_changed()
    {
        var env = Complete();
        _game.Platform.Window = true;                       // this platform can hold windows
        var checks = Run(env);
        Assert.Contains("none", checks.Single(c => c.Name == "deck").Detail, StringComparison.Ordinal);

        // No rule: a warning with the one command to run, and nothing is written anywhere.
        _game.Platform.Firewall = false;
        var firewall = Run(env).Single(c => c.Name == "firewall");
        Assert.Equal(Doctor.Verdict.Warn, firewall.Verdict);
        Assert.Contains("gatekit firewall", firewall.Fix, StringComparison.Ordinal);
        Assert.Contains("elevated", firewall.Fix, StringComparison.Ordinal);
        Assert.Contains("will not change", firewall.Fix, StringComparison.Ordinal);

        _game.Platform.Firewall = true;
        Assert.Equal(Doctor.Verdict.Good, Run(env).Single(c => c.Name == "firewall").Verdict);

        // A deck that is running is named by its process.
        new DeckRecord(_game.Temp["state"]).Announce(Environment.ProcessId, 99);
        Assert.Contains("running", Run(env).Single(c => c.Name == "deck").Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void An_own_extender_build_without_the_dll_is_a_failure()
    {
        var env = Complete();
        env["GATEKIT_SE"] = _game.Temp["se-build"];
        var checks = Run(env);
        var se = checks.Single(c => c.Name == "script extender");
        Assert.Equal(Doctor.Verdict.Bad, se.Verdict);
        Assert.Contains("se install", se.Fix);
    }
}
