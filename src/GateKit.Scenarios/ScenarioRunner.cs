using System.Diagnostics;
using System.Globalization;
using System.Text;
using GateKit.Stand;
using MoonSharp.Interpreter;

namespace GateKit.Scenarios;

/// <summary>How one scenario ended.</summary>
public sealed record ScenarioResult(string Name, bool Passed, string Verdict, TimeSpan Took);

/// <summary>
/// Runs Lua scenarios against a real game through the stand (a port of the Python
/// <c>run_suite</c> / <c>run_scenario</c>, docs/contracts/cli.md 2.13-2.14).
///
/// Scenarios that need the same start (level, mods, save) share one game session, so ten
/// scenarios cost one start, not ten. A scenario that wants the world as it was right after the
/// start gets it from a checkpoint and a reset (about a quarter of a minute) instead of a
/// restart. A failure leaves a screenshot and the Script Extender console text in the run's folder.
/// </summary>
public sealed class ScenarioRunner(Stand.Stand stand, TextWriter output)
{
    public Stand.Stand Stand { get; } = stand;
    public TextWriter Out { get; } = output;

    /// <summary>Where a run's folders go: &lt;state&gt;/runs/&lt;stamp&gt;-&lt;what&gt;.</summary>
    public string RunFolder(string what) => Path.Combine(Stand.Layout.State, "runs",
        DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + what);

    // --- one scenario ---

    /// <summary>Runs a scenario against a game that is up. Throws nothing: the verdict is the answer.</summary>
    public ScenarioResult RunOne(Scenario scenario, GameSession session, string outFolder)
    {
        Directory.CreateDirectory(outFolder);
        var watch = Stopwatch.StartNew();
        var script = Scenario.NewScript(Out, Path.GetDirectoryName(scenario.Path));
        try
        {
            scenario.Declare(script);
            var game = new GameApi(script, Stand, session, outFolder);
            script.Globals["game"] = game.Table;
            script.Call(script.Globals.Get("run"), DynValue.NewTable(game.Table));
            return new ScenarioResult(scenario.Name, true, "PASS", watch.Elapsed);
        }
        catch (Exception problem) when (problem is InterpreterException or StandException)
        {
            var message = problem is InterpreterException lua ? lua.DecoratedMessage ?? lua.Message : problem.Message;
            SaveFailure(outFolder);
            return new ScenarioResult(scenario.Name, false, "FAIL: " + Flatten(message), watch.Elapsed);
        }
    }

    static string Flatten(string message) => message.Replace("\r", "").Replace('\n', ' ');

    /// <summary>A screenshot and the console text of the moment it failed; what cannot be had is left out.</summary>
    void SaveFailure(string outFolder)
    {
        try { Stand.Shot(Path.Combine(outFolder, "failure.jpg")); }
        catch (Exception e) when (e is StandException or Platform.PlatformCapabilityException) { }
        try { File.WriteAllText(Path.Combine(outFolder, "console.txt"), string.Join("\n", Stand.Logs(false, 200)), new UTF8Encoding(false)); }
        catch (Exception e) when (e is StandException or Platform.PlatformCapabilityException or IOException) { }
    }

    // --- the suite ---

    /// <summary>Runs every scenario in a folder, grouped by the start it needs. Returns an exit code.</summary>
    public async Task<int> RunSuiteAsync(string folder, string? only, bool cleanAll, CancellationToken cancel = default)
    {
        var scenarios = Scenario.Discover(folder, only);
        var groups = new Dictionary<(string?, string, bool, string?), List<Scenario>>();
        foreach (var scenario in scenarios)
        {
            if (!groups.TryGetValue(scenario.Start.Key, out var list)) groups[scenario.Start.Key] = list = [];
            list.Add(scenario);
        }

        var runFolder = RunFolder("suite");
        var results = new List<ScenarioResult>();
        var suite = Stopwatch.StartNew();
        foreach (var (_, group) in groups)
        {
            var start = group[0].Start;
            GameSession session;
            try
            {
                session = await Stand.UpAsync(new UpOptions(start.Level, start.Save, start.Mods), cancel);
            }
            catch (StandException problem)
            {
                Stand.Down();
                results.AddRange(group.Select(s => new ScenarioResult(s.Name, false, $"FAIL (game did not start: {Flatten(problem.Message)})", TimeSpan.Zero)));
                continue;
            }
            try
            {
                var wantsClean = group.Select(s => cleanAll || s.Start.Clean).ToList();
                var dirty = false;
                if (wantsClean.Any(w => w) && start.Save is null) await session.CheckpointAsync();
                for (var i = 0; i < group.Count; i++)
                {
                    if (wantsClean[i] && dirty && session.CheckpointName is not null)
                    {
                        try
                        {
                            var took = await session.ResetAsync();
                            Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"world reset before {group[i].Name}: {took.TotalSeconds:0.0} s"));
                        }
                        catch (StandException problem)
                        {
                            results.Add(new ScenarioResult(group[i].Name, false, $"FAIL (reset: {Flatten(problem.Message)})", TimeSpan.Zero));
                            break;
                        }
                    }
                    dirty = true;
                    results.Add(RunOne(group[i], session, Path.Combine(runFolder, group[i].Name)));
                    if (Stand.GamePids().Count == 0) break; // the game died; the rest of this group cannot run
                }
            }
            finally
            {
                Stand.Down();
            }
        }
        var done = results.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var group in groups.Values)
            results.AddRange(group.Where(s => !done.Contains(s.Name))
                .Select(s => new ScenarioResult(s.Name, false, "FAIL (not run: the game closed)", TimeSpan.Zero)));

        var failed = 0;
        foreach (var result in results)
        {
            Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{(result.Passed ? "PASS" : "FAIL"),-4}  {result.Name,-24} {result.Took.TotalSeconds,5:0.0} s") +
                (result.Passed ? "" : "   " + result.Verdict));
            if (!result.Passed) failed++;
        }
        Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{results.Count - failed} passed, {failed} failed, {suite.Elapsed.TotalSeconds:0} s in all; output: {runFolder}"));
        return failed > 0 ? 1 : 0;
    }

    /// <summary>`run scenario.lua`: one scenario with a start of its own. Returns an exit code.</summary>
    public async Task<int> RunFileAsync(string path, bool keep, bool attach, UpOptions? overrides = null, CancellationToken cancel = default)
    {
        var scenario = Scenario.Load(path);
        var outFolder = RunFolder(scenario.Name);
        Directory.CreateDirectory(outFolder);
        try
        {
            GameSession session;
            if (attach)
            {
                if (Stand.GamePids().Count == 0) throw new StandException("--attach needs a running game (start one with `up`)");
                session = Stand.Session();
                keep = true;
            }
            else
            {
                var start = scenario.Start;
                session = await Stand.UpAsync(new UpOptions(overrides?.Level ?? start.Level, overrides?.Save ?? start.Save,
                    overrides?.Mods ?? start.Mods, overrides?.StoryLog ?? false, overrides?.GameArgs), cancel);
            }
            var result = RunOne(scenario, session, outFolder);
            Out.WriteLine(result.Passed
                ? $"PASS {result.Name}  (output: {outFolder})"
                : $"FAIL {result.Name}: {result.Verdict[6..]}");
            return result.Passed ? 0 : 1;
        }
        catch (StandException problem) // the game would not start, or --attach found none
        {
            Out.WriteLine($"FAIL {scenario.Name}: {problem.Message}");
            return 1;
        }
        finally
        {
            if (!keep) Stand.Down();
        }
    }
}
