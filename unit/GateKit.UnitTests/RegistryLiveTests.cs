using System.Diagnostics;
using GateKit.Symbols.Registry;

namespace GateKit.UnitTests;

/// <summary>The registry's live layer between processes: two gatekit processes noting at once, each
/// seeing at its next read what the other wrote. Run with a home of the test's own.</summary>
public sealed class RegistryLiveTests : IDisposable
{
    readonly string home = GateKit.Testing.Scratch.NewPath("registry-live");

    public RegistryLiveTests() => Directory.CreateDirectory(home);

    public void Dispose()
    {
        if (Directory.Exists(home)) Directory.Delete(home, true);
    }

    Process Start(string who, string id)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "gatekit.dll");
        var info = new ProcessStartInfo("dotnet") { WorkingDirectory = home, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { dll, "registry", "note", id, "--rung", "inferred", "--does", "Rolls the dice of one request.", "--who", who, "--task", "concurrency" })
            info.ArgumentList.Add(a);
        info.Environment["GATEKIT_HOME"] = home;
        info.Environment["GATEKIT_CONFIG"] = "";
        info.Environment.Remove("GATEKIT_STATE");
        info.Environment.Remove("GATEKIT_REGISTRY");
        return Process.Start(info)!;
    }

    [Fact]
    public void Two_processes_noting_at_once_lose_nothing_and_each_reads_the_other()
    {
        var started = new List<Process>();
        for (var round = 0; round < 4; round++)
        {
            started.Add(Start("first-" + round, "esv::RollSystem::RollDice"));
            started.Add(Start("second-" + round, "esv::RollSystem::RollDice"));
        }
        foreach (var p in started)
        {
            Assert.True(p.WaitForExit(120_000), "a gatekit process did not finish");
            Assert.True(p.ExitCode == 0, p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd());
        }
        var live = new LiveLayer(Path.Combine(home, "state", "registry-live"));
        var who = live.Of("esv::RollSystem::RollDice").Select(c => c.Who).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(8, who.Count);
        Assert.Equal(8, who.Distinct().Count());
    }
}
