using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using GateKit.Bridge;
using GateKit.Copies;
using MoonSharp.Interpreter;

namespace GateKit.Testing;

/// <summary>How one test ended.</summary>
/// <param name="NotRun">The test was not run, and says why in <paramref name="Verdict"/> (the
/// running game is in another level than it declares): neither a pass nor a failure.</param>
public sealed record TestResult(string Name, bool Passed, string Verdict, TimeSpan Took, bool NotRun = false)
{
    /// <summary>A precondition (need) was not met: the third outcome, neither a pass nor a failure
    /// of the thing tested. <see cref="Verdict"/> is "UNMET: why".</summary>
    public bool Unmet { get; init; }

    /// <summary>The named steps it ran, with their class, time and outcome.</summary>
    public IReadOnlyList<StepRecord> Steps { get; init; } = [];

    /// <summary>pass, fail, unmet or not run.</summary>
    public string Outcome => NotRun ? "not run" : Unmet ? "unmet" : Passed ? "pass" : "fail";

    /// <summary>What the reset ladder did before it ("fixture 1.4 s"), if anything was asked.</summary>
    public string? Reset { get; init; }

    /// <summary>The ladder's rungs tried before it, with their times (the suite's timing line).</summary>
    public IReadOnlyList<ResetStep> ResetSteps { get; init; } = [];

    /// <summary>What the event log holds that the test did not ask for (<see cref="Testing.Surprises"/>):
    /// reported beside the verdict, never part of it.</summary>
    public IReadOnlyList<Surprise> Surprises { get; init; } = [];

    /// <summary>Seconds the test spent inside each kind of wait of `game` (wait, wait_row, ...).</summary>
    public IReadOnlyDictionary<string, double> Waits { get; init; } = new Dictionary<string, double>();

    /// <summary>Seconds the test spent in the game's own answers to its chunks, outside any wait.</summary>
    public double Answers { get; init; }

    /// <summary>Its cases with their outcomes (empty for a test without cases).</summary>
    public IReadOnlyList<CaseRecord> Cases { get; init; } = [];
}

/// <summary>One case of a test: a variation of its rule, run from a clean scene.</summary>
public sealed record CaseRecord(string Name, string Outcome, TimeSpan Took, string? Error, IReadOnlyList<StepRecord> Steps)
{
    public IReadOnlyList<Surprise> Surprises { get; init; } = [];
    /// <summary>What the reset ladder did before it (its cases after the first).</summary>
    public string? Reset { get; init; }
}

/// <summary>
/// Runs Lua tests against a real game in a copy (docs/contracts/cli.md, "Tests in the game").
///
/// Tests that need the same start (level, mods, save) share one game session, so ten
/// tests cost one start, not ten. A test that wants the world as it was right after the
/// start gets it from a checkpoint and a reset (about a quarter of a minute) instead of a
/// restart. A failure leaves a screenshot and the Script Extender console text in the run's folder.
/// </summary>
public sealed class TestRunner(Copies.GameCopy copy, TextWriter output)
{
    public Copies.GameCopy GameCopy { get; } = copy;
    public TextWriter Out { get; } = output;

    /// <summary>Where a run's folders go: &lt;state&gt;/runs/&lt;stamp&gt;-&lt;what&gt;.</summary>
    public string RunFolder(string what) => Path.Combine(GameCopy.Layout.State, "runs",
        DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + what);

    // --- one test ---

    /// <summary>Runs a test against a game that is up. Throws nothing: the verdict is the answer.</summary>
    public TestResult RunOne(GameTest test, GameSession session, string outFolder, bool say = true,
        Func<Task<(bool Ok, string Line)>>? between = null)
    {
        Directory.CreateDirectory(outFolder);
        var watch = Stopwatch.StartNew();
        var script = GameTest.NewScript(Out, Path.GetDirectoryName(test.Path));
        var steps = new Steps();
        var traces = Trace ? new StepTraces(session, outFolder) : null;
        steps.Mark = (name, cls, outcome, ms, error) =>
        {
            if (outcome is not null) traces?.End(name, steps.Case, outcome);
            MarkStep(session, name, cls, outcome, ms, error, steps.Case);
            if (outcome is null) traces?.Begin(name);
        };
        Bridge.EventStream? events = null;
        string? mark = null;
        GameApi? game = null;
        try
        {
            test.Declare(script);
            events = new Bridge.EventStream(new Bridge.EventLogReader(GameCopy.Layout.Channel));
            mark = events.Mark();
            game = new GameApi(script, GameCopy, session, outFolder, steps) { TestsTree = test.Tree };
            var timed = game;
            steps.Spent = () => timed.Spent;
            mark = game.Mark();
            script.Globals["game"] = game.Table;
            steps.Install(script);
            if (test.Cases.Count > 0)
                return RunCases(test, script, steps, game, events, between, watch, outFolder, say);
            script.Call(script.Globals.Get("run"), DynValue.NewTable(game.Table));
            if (steps.Unmet is { } unmetLate)   // a need() whose error the test caught itself
                return Verdict(new TestResult(test.Name, false, "UNMET: " + Flatten(unmetLate), watch.Elapsed)
                    { Unmet = true, Steps = steps.All, Waits = WaitsOf(game), Answers = game?.AnswerSeconds ?? 0, Surprises = SurprisesOf(test, events, mark, game) }, outFolder, say);
            // rows no registry places may be what a negative check looked for: the run cannot pass over them
            if (events.Unplaced(mark) is { Count: > 0 } unplaced)
                throw new CopyException("the event log holds rows the host could not place: " + string.Join("; ",
                    unplaced.Select(u => $"{u!["kind"]} ({u["rows"]} rows: {u["why"]})")));
            Replays.Note(test.Path, true, watch.Elapsed, Replays.Alive(script), GameCopy, session, Out);
            return Verdict(new TestResult(test.Name, true, "PASS", watch.Elapsed)
                { Steps = steps.All, Waits = WaitsOf(game), Answers = game?.AnswerSeconds ?? 0, Surprises = SurprisesOf(test, events, mark, game) }, outFolder, say);
        }
        catch (Exception problem) when (problem is InterpreterException or CopyException)
        {
            var surprises = SurprisesOf(test, events, mark, game);
            if (steps.Unmet is { } unmet)
                return Verdict(new TestResult(test.Name, false, "UNMET: " + Flatten(unmet), watch.Elapsed)
                    { Unmet = true, Steps = steps.All, Waits = WaitsOf(game), Answers = game?.AnswerSeconds ?? 0, Surprises = surprises }, outFolder, say);
            var message = problem is InterpreterException lua ? lua.DecoratedMessage ?? lua.Message : problem.Message;
            SaveFailure(outFolder);
            Replays.Note(test.Path, false, watch.Elapsed, Replays.Alive(script), GameCopy, session, Out);
            var failed = Verdict(new TestResult(test.Name, false, "FAIL: " + Flatten(message), watch.Elapsed)
                { Steps = steps.All, Waits = WaitsOf(game), Answers = game?.AnswerSeconds ?? 0, Surprises = surprises }, outFolder, say);
            ShowAbout(test, outFolder, say);
            return failed;
        }
    }

    /// <summary>A test with cases: each case from a clean scene (<paramref name="between"/>, the ladder's
    /// rung of the test, before every case after the first), the header's `before` run again before
    /// each, a failing or unmet case not stopping the others. The test fails if a case failed, is
    /// unmet if every case run was unmet, and passes otherwise.</summary>
    TestResult RunCases(GameTest test, Script script, Steps steps, GameApi game, Bridge.EventStream events,
        Func<Task<(bool Ok, string Line)>>? between, Stopwatch watch, string outFolder, bool say)
    {
        steps.CaseNames = new HashSet<string>(test.Cases, StringComparer.Ordinal);
        script.Globals["case"] = DynValue.NewCallback((_, a) =>
            throw new ScriptRuntimeException($"case {a[0].CastToString()}: cases are given at the test's top level, never inside a case or a step"), "case");
        var cases = new List<CaseRecord>();
        var first = true;
        foreach (var name in test.Cases.Where(c => test.OnlyCase is null || c == test.OnlyCase))
        {
            string? reset = null;
            if (!first && between is not null)
            {
                var (ok, line) = between().GetAwaiter().GetResult();
                reset = line;
                if (!ok)
                {
                    cases.Add(new CaseRecord(name, "fail", TimeSpan.Zero, "reset: no rung held: " + line, []) { Reset = line });
                    continue;
                }
            }
            first = false;
            var started = Stopwatch.StartNew();
            var mark = game.Mark();
            var before = steps.All.Count;
            steps.BeginCase(name);
            string outcome = "pass";
            string? error = null;
            try
            {
                if (test.Before is { } staging) script.Call(script.Globals.Get(staging), DynValue.NewTable(game.Table));
                script.Call(GameTest.CaseBody(script, name), DynValue.NewTable(game.Table));
            }
            catch (Exception problem) when (problem is InterpreterException or CopyException)
            {
                error = Flatten(problem is InterpreterException lua ? lua.DecoratedMessage ?? lua.Message : problem.Message);
                outcome = "fail";
            }
            if (steps.EndCase() is { } unmet) { outcome = "unmet"; error = unmet; }
            var record = new CaseRecord(name, outcome, started.Elapsed, error, [.. steps.All.Skip(before)])
            {
                Surprises = SurprisesOf(test, events, mark, game), Reset = reset,
            };
            cases.Add(record);
            if (say) Out.WriteLine(CaseLine(record));
        }
        var failed = cases.Where(c => c.Outcome == "fail").ToList();
        var allUnmet = cases.Count > 0 && cases.All(c => c.Outcome == "unmet");
        var verdict = failed.Count > 0 ? "FAIL: " + string.Join("; ", failed.Select(c => $"case {c.Name}: {c.Error}"))
            : allUnmet ? "UNMET: " + string.Join("; ", cases.Select(c => $"case {c.Name}: {c.Error}"))
            : "PASS";
        if (failed.Count > 0) SaveFailure(outFolder);
        var result = new TestResult(test.Name, failed.Count == 0 && !allUnmet, verdict, watch.Elapsed)
        {
            Unmet = allUnmet && failed.Count == 0, Steps = steps.All, Waits = WaitsOf(game), Answers = game.AnswerSeconds, Cases = cases,
            Surprises = [.. cases.SelectMany(c => c.Surprises)],
        };
        var said = Verdict(result, outFolder, say);
        if (failed.Count > 0) ShowAbout(test, outFolder, say);
        return said;
    }

    static string CaseLine(CaseRecord c) => string.Create(CultureInfo.InvariantCulture,
        $"    case {c.Name}: {c.Outcome} {c.Took.TotalSeconds:0.0} s") + (c.Error is { } e ? "   " + e : "");

    static IReadOnlyDictionary<string, double> WaitsOf(GameApi? game) =>
        game is null ? new Dictionary<string, double>() : new Dictionary<string, double>(game.WaitSeconds, StringComparer.Ordinal);

    /// <summary>What the event log holds since <paramref name="mark"/> that the test did not ask for.
    /// A log that cannot be read gives no report; the verdict does not depend on it.</summary>
    static IReadOnlyList<Surprise> SurprisesOf(GameTest test, Bridge.EventStream? events, string? mark, GameApi? game)
    {
        if (events is null || mark is null) return [];
        try
        {
            // the party as the game has it now, summons included: a log that no longer holds the
            // scope's opening row (a new Lua state, rows dropped) still knows who is of the party
            var log = events.Since(null).Rows;
            try
            {
                // only a server that heartbeats is asked: in the main menu there is none to answer
                if (game?.Session.State(Side.Server, GameSession.Fresh) is not null
                    && game.Session.LuaAsync("server","return GK.Stage.Party(true)", TimeSpan.FromSeconds(5), "observation").GetAwaiter().GetResult() is JsonArray party)
                    log.Add(new JsonObject { ["kind"] = "scope_begin", ["payload"] = new JsonObject { ["party"] = party.DeepClone() } });
            }
            catch (Exception e) when (e is CopyException or BridgeLuaException or BridgeTimeoutException) { }
            return Surprises.Find(log, events.Since(mark).Rows, events.Unplaced(mark),
                game?.Asked ?? new HashSet<string>(StringComparer.Ordinal), test.AllowedSurprises());
        }
        catch (Exception e) when (e is CopyException or IOException or FormatException or ArgumentException or InvalidOperationException) { return []; }
    }

    /// <summary>A step's row in the game's event log (kinds step and step_ended), on the server
    /// when it answers, else on the client; a step's begin waits for the row, its end does not.</summary>
    static void MarkStep(GameSession session, string name, string cls, string? outcome, double ms, string? error, string? inCase = null)
    {
        var side = session.State(Side.Server, GameSession.Fresh) is not null ? Side.Server
            : session.State(Side.Client, GameSession.Fresh) is not null ? Side.Client : (Side?)null;
        if (side is not { } target) return;
        var payload = new JsonObject { ["name"] = name, ["class"] = cls, ["outcome"] = outcome, ["ms"] = ms, ["error"] = error, ["case"] = inCase };
        var text = GateKit.Json.JsonText.Inline(payload);
        var level = 1;
        while (text.Contains("]" + new string('=', level) + "]", StringComparison.Ordinal)) level++;
        var quoted = "[" + new string('=', level) + "[" + text + "]" + new string('=', level) + "]";
        var code = "local GK = Mods.GateKit.GK\nlocal p = Ext.Json.Parse(" + quoted + ")\n"
            + (outcome is null ? "return GK.Step(p.name, p.class, p.case)" : "return GK.StepEnded(p.name, p.class, p.outcome, p.ms, p.error, p.case)");
        if (outcome is null) session.LuaAsync(target.Name(), code, TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        else session.Post(target, code);
    }

    /// <summary>The verdict is written into the test's folder (<c>verdict.txt</c>) the moment it
    /// is known, and said: a suite cut short before its summary - a caller's time limit, a closed
    /// console - still leaves why each test failed, not only a screenshot that it did.</summary>
    TestResult Verdict(TestResult result, string outFolder, bool say = true)
    {
        try
        {
            Directory.CreateDirectory(outFolder);
            File.WriteAllText(Path.Combine(outFolder, "verdict.txt"), Report(result) + "\n", new UTF8Encoding(false));
            if (KeepEvents) CopyEvents(outFolder, DateTime.UtcNow - result.Took - TimeSpan.FromSeconds(1));
        }
        catch (IOException) { }
        if (say) Out.WriteLine(Report(result));
        return result;
    }

    /// <summary>`test --keep-events`: each test's output folder gets events/ - the event log's files
    /// written while it ran (events_&lt;side&gt;_&lt;boot&gt;_&lt;n&gt;.jsonl, both sides) and the registry of
    /// kinds - which `gatekit logic-diff` compares as they are.</summary>
    public bool KeepEvents { get; set; }

    void CopyEvents(string outFolder, DateTime since)
    {
        var channel = GameCopy.Layout.Channel;
        if (!Directory.Exists(channel)) return;
        var target = Path.Combine(outFolder, "events");
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(channel, "events_*.jsonl"))
            if (File.GetLastWriteTimeUtc(file) >= since) File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        foreach (var file in Directory.EnumerateFiles(channel, "events_*_kinds.json"))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
    }

    static string Line(TestResult result) => string.Create(CultureInfo.InvariantCulture,
        $"{(result.NotRun ? "SKIP" : result.Unmet ? "UNMET" : result.Passed ? "PASS" : "FAIL"),-4}  {result.Name,-24} {result.Took.TotalSeconds,5:0.0} s")
        + (result.Reset is { } reset ? $"  [reset: {reset}]" : "")
        + (result.Passed ? "" : "   " + result.Verdict);

    /// <summary>The verdict line and, under it, the test's steps with their class, outcome and time.</summary>
    static string Report(TestResult result) => result.Cases.Count == 0
        ? string.Join("\n", new[] { Line(result) }.Concat(Steps.Lines(result.Steps)).Concat(result.Surprises.Select(s => s.Line())))
        : string.Join("\n", new[] { Line(result) }.Concat(result.Cases.SelectMany(c =>
            new[] { CaseLine(c) }.Concat(Steps.Lines(c.Steps).Select(l => "  " + l)).Concat(c.Surprises.Select(s => "  " + s.Line())))));

    /// <summary>A failure is read with the rule in front of its reader: the test's description
    /// (<see cref="GameTest.About"/>) is said after the verdict and kept beside it in verdict.txt.</summary>
    void ShowAbout(GameTest test, string outFolder, bool say)
    {
        if (test.About.Length == 0) return;
        var lines = test.About.Split('\n').Take(8).Select(l => "      | " + l).ToList();
        if (say) foreach (var line in lines) Out.WriteLine(line);
        try { File.AppendAllText(Path.Combine(outFolder, "verdict.txt"), string.Join("\n", lines) + "\n", new UTF8Encoding(false)); }
        catch (IOException) { }
    }

    static string Flatten(string message) => message.Replace("\r", "").Replace('\n', ' ');

    readonly DateTime _since = DateTime.UtcNow;
    readonly HashSet<string> _crashesSaid = new(StringComparer.Ordinal);

    /// <summary>A screenshot and the console text of the moment it failed; what cannot be had is left
    /// out. A crash dump the copy wrote since the run began (its game died) is named beside them, as
    /// crash.txt, with the function at its address.</summary>
    void SaveFailure(string outFolder)
    {
        try
        {
            var dumps = CrashDumps.Read(CrashDumps.Folder(GameCopy.Layout), _since).Where(d => _crashesSaid.Add(d.Path)).Select(d => CrashDumps.Named(d, CrashDumps.ExtenderPdbs(GameCopy.Layout))).ToList();
            if (dumps.Count > 0)
            {
                Directory.CreateDirectory(outFolder);
                File.WriteAllText(Path.Combine(outFolder, "crash.txt"), string.Join("\n", dumps.Select(d => d.Line())) + "\n", new UTF8Encoding(false));
                foreach (var d in dumps) Out.WriteLine("  the game died: " + d.Line());
            }
        }
        catch (IOException) { }
        try { GameCopy.Shot(Path.Combine(outFolder, "failure.jpg")); }
        catch (Exception e) when (e is CopyException or Platform.PlatformCapabilityException) { }
        try { File.WriteAllText(Path.Combine(outFolder, "console.txt"), string.Join("\n", GameCopy.Logs(false, 200)), new UTF8Encoding(false)); }
        catch (Exception e) when (e is CopyException or Platform.PlatformCapabilityException or IOException) { }
    }

    // --- the suite ---

    /// <summary>Runs every test in a folder, grouped by the start it needs. Returns an exit code.
    ///
    /// Before each test the reset ladder (<see cref="ResetLadder"/>) gives it the world it
    /// needs, cheapest rung first: a clean of what the last one staged in a simple test level, the
    /// level's checkpoint elsewhere, a restart only when nothing else holds. A copy of this home
    /// already running in the level a group declares is used as it is (and left running, cleaned)
    /// instead of starting another. With <paramref name="attach"/> every test runs in the game
    /// that is up, whatever its level, which is neither started nor closed. A copy running in
    /// another level is refused and left alone: a failed start used to end in `down`, which closed
    /// a copy somebody else was playing (N18 of the third run).
    /// <paramref name="cleanAll"/> (`--clean`): the first test in a copy found running is
    /// prepared too. <paramref name="force"/>: this rung at least, whatever a test declares.</summary>
    public async Task<int> RunSuiteAsync(string folder, string? only, bool cleanAll, CancellationToken cancel = default, bool attach = false,
        ResetRung? force = null, int repeat = 1)
    {
        if (Speed is { } asked && asked != "working"
            && !(double.TryParse(asked, NumberStyles.Float, CultureInfo.InvariantCulture, out var scale) && scale > 0))
            throw new CopyException($"--speed is a positive number or `working`, not '{asked}'");
        var found = GameTest.Discover(folder, only, Out);
        GameCopy.CleanStaleTestFiles();
        // --repeat N: the selection N times in a row, in the same session (name#2, name#3 ...)
        var tests = Enumerable.Range(1, Math.Max(1, repeat))
            .SelectMany(n => found.Select(s => n == 1 ? s : s.Again(n))).ToList();
        var running = GameCopy.GamePids().Count > 0;
        if (attach && !running) throw new CopyException("--attach needs a running game (start one with `up`)");
        if (attach) return await RunAttachedAsync(tests, cleanAll, force);
        var groups = new Dictionary<((string?, string, bool, string?, bool), string), List<GameTest>>();
        foreach (var test in tests)
        {
            if (!groups.TryGetValue(test.Group, out var list)) groups[test.Group] = list = [];
            list.Add(test);
        }
        if (running)
        {
            // Only a copy already in the level of a group is used; anything else is refused before a thing is done.
            var here = await LevelHereAsync(GameCopy.Session());
            var fits = groups.Values.FirstOrDefault(g => Fits(g[0], here));
            if (fits is null)
                throw new CopyException($"the game is already running{(here is null ? "" : " in " + here)}, and no test asked for is "
                    + "declared for that level; pass --attach to run them in it as it is, or `down` first. Nothing was stopped");
            // the running copy's group first: the others start a game of their own after it is left
            groups = new[] { fits }.Concat(groups.Values.Where(g => g != fits)).ToDictionary(g => g[0].Group);
        }

        var runFolder = RunFolder("suite");
        var results = new List<TestResult>();
        var suite = Stopwatch.StartNew();
        // The game this run started and keeps up between groups: the start it was given and the
        // extender build it runs. A later group goes into its level or save by the game's own load
        // in that session (MoveAsync); a restart only when that cannot be.
        UpOptions? kept = null;
        string? keptBuild = null;
        try
        {
        foreach (var (_, group) in groups)
        {
            var start = group[0].Start;
            var files = group[0].Files;
            GameSession session;
            var ours = GameCopy.GamePids().Count == 0 || kept is not null;
            var placed = false;
            var moved = false;
            try
            {
                if (ours && kept is not null && GameCopy.GamePids().Count > 0)
                {
                    var into = await MoveAsync(group, kept, keptBuild);
                    if (into is not null) moved = true;
                    else
                    {
                        GameCopy.Down();
                        kept = null;
                    }
                }
                if (moved) session = GameCopy.Session();
                else if (ours)
                {
                    kept = null;
                    // the group's files go in before its start, so the game reads them from its first frame
                    if (files.Count > 0)
                    {
                        if (PlaceFiles(string.Join(", ", group.Select(s => s.Name)), files) is { } refused)
                        {
                            results.AddRange(group.Select(s => Verdict(new TestResult(s.Name, false, $"FAIL ({Flatten(refused)})", TimeSpan.Zero),
                                Path.Combine(runFolder, s.Name))));
                            continue;
                        }
                        placed = true;
                    }
                    try
                    {
                        session = await GameCopy.UpAsync(StartOf(start), cancel);
                        kept = StartOf(start);
                        keptBuild = GameCopy.SeStatus().Sha256;
                    }
                    catch (CopyException problem)
                    {
                        GameCopy.Down();
                        results.AddRange(group.Select(s => new TestResult(s.Name, false, $"FAIL (game did not start: {Flatten(problem.Message)})", TimeSpan.Zero)));
                        continue;
                    }
                }
                else
                {
                    session = GameCopy.Session();
                    if (!Fits(group[0], await LevelHereAsync(session)))
                    {
                        var why = files.Count > 0 ? "a copy is running, and the test's files go in before its game starts" : "a copy runs in another level";
                        results.AddRange(group.Select(s => new TestResult(s.Name, false, "NOT RUN: " + why, TimeSpan.Zero, NotRun: true)));
                        continue;
                    }
                    Out.WriteLine($"in the copy already running ({await LevelHereAsync(session) ?? "its level"}): not started, not closed");
                }
                var keep = false;
                try
                {
                    var run = await RunGroupAsync(group, session, ours ? StartOf(start) : null, untouched: ours, cleanAll, force, runFolder, adopted: moved);
                    results.AddRange(run);
                    // a game with no files of a group, in a level or a save, stays up for the next group
                    keep = ours && !RestartGroups && !placed && Keepable(group) && GameCopy.GamePids().Count > 0;
                }
                finally
                {
                    if (ours && !keep)
                    {
                        GameCopy.Down();
                        kept = null;
                    }
                }
            }
            finally
            {
                // after the group, and after a start that failed: a restart inside the group kept them
                if (placed) RemoveFiles();
            }
        }
        }
        finally
        {
            if (kept is not null) GameCopy.Down();
        }
        var done = results.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var group in groups.Values)
            results.AddRange(group.Where(s => !done.Contains(s.Name))
                .Select(s => new TestResult(s.Name, false, "FAIL (not run: the game closed)", TimeSpan.Zero)));

        return Summarize(results, suite, runFolder);
    }

    /// <summary>Whether a group's game may stay up for the next group, and a running game may be
    /// taken into the group's start: a level or a save, no startup videos, no files, and no test of
    /// the start itself (a test under `startup/` of its tree always gets a process of its own).</summary>
    public static bool Keepable(IReadOnlyList<GameTest> group)
    {
        var start = group[0].Start;
        return (start.Level is not null || start.Save is not null) && !start.NoLevel && !start.StartupVideos
            && group[0].Files.Count == 0 && !group.Any(IsStartTest);
    }

    /// <summary>A test of the game's start: under `startup/` of its tree.</summary>
    public static bool IsStartTest(GameTest test) =>
        Path.GetRelativePath(test.Tree, test.Path).Replace('\\', '/').StartsWith("startup/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Takes the game this run keeps up (started as <paramref name="kept"/>) into the start of
    /// <paramref name="group"/> by the game's own load in the session: the save it declares, or for a
    /// level the save made right after that level's start (<see cref="StartSaves"/>). Null, with
    /// the reason said, when it cannot be: another mod list, another extender build, a level never
    /// started in this copy, a load that failed; the caller then restarts.</summary>
    async Task<GameSession?> MoveAsync(IReadOnlyList<GameTest> group, UpOptions kept, string? keptBuild)
    {
        var start = group[0].Start;
        string? why = null;
        IReadOnlyList<string>? Effective(IReadOnlyList<string>? mods) => GameCopy.Instance is not null ? mods ?? [] : mods;
        var save = start.Save ?? (start.Level is { } level ? new StartSaves(GameCopy.Layout).Find(level, Effective(start.Mods)) : null);
        if (!Keepable(group)) why = "its start is not a level or a save of its own (files, startup videos, the main menu, a test of the start)";
        else if (StartSaves.ModsKey(Effective(start.Mods)) != StartSaves.ModsKey(Effective(kept.Mods))) why = "another mod list";
        else if (GameCopy.SeStatus().Sha256 != keptBuild) why = "another Script Extender build is installed";
        else if (save is null) why = $"no save of {start.Level}'s start in this copy yet";
        if (why is not null)
        {
            Out.WriteLine($"restart for {group[0].Name}: {why}");
            return null;
        }
        var session = GameCopy.Session();
        try
        {
            var took = await session.LoadSaveAsync(save!);
            // what the groups before and the load's own posted chunk left unread, as a new
            // process's start would have cleared it: nobody waits for an answer now
            session.DropUnclaimedAnswers(DateTime.UtcNow);
            var here = await LevelHereAsync(session);
            if (start.Level is { } wanted && !string.Equals(StartSaves.LevelKey(wanted), here, StringComparison.OrdinalIgnoreCase))
                throw new CopyException($"the save {save} loaded into {here ?? "an unknown level"}, not {wanted}");
            session.AdoptCheckpoint(save!, here);
            Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"into {start.Level ?? start.Save} in the running game: {(start.Save is null ? "the save of its start" : "the save")} {save} loaded in {took.TotalSeconds:0.0} s, no restart"));
            return session;
        }
        catch (CopyException e)
        {
            Out.WriteLine($"restart for {group[0].Name}: the load in the running game failed: {Flatten(e.Message)}");
            return null;
        }
    }

    /// <summary>The start <c>up</c> is given for a test's group.</summary>
    public static UpOptions StartOf(TestStart start) =>
        new(start.Level, start.Save, start.Mods, MainMenu: start.MainMenu, StartupVideos: start.StartupVideos, UntilLua: start.UntilLua,
            FirstLaunch: start.FirstLaunch);

    /// <summary>Whether a game in <paramref name="here"/> is where a test's start puts it: the
    /// level it declares (a save, the main menu, a start with the startup videos and a test with
    /// files, which go in before the start, are never taken as met by a running copy).</summary>
    static bool Fits(GameTest test, string? here)
    {
        var start = test.Start;
        if (start.NoLevel || start.Save is not null || start.StartupVideos || test.Files.Count > 0 || here is null) return false;
        if (start.Level is not { } declared) return true;
        return string.Equals(declared[(declared.IndexOf(':', StringComparison.Ordinal) + 1)..], here, StringComparison.OrdinalIgnoreCase);
    }

    static async Task<string?> LevelHereAsync(GameSession session)
    {
        try
        {
            return await session.LuaAsync("server", "return GK.Stage.CurrentLevel()", TimeSpan.FromSeconds(20)) is JsonValue value
                && value.TryGetValue<string>(out var name) && name.Length > 0 ? name : null;
        }
        catch (CopyException) { return null; }
    }

    /// <summary>A group of tests in one session, each after the ladder made its world clean.
    /// <paramref name="restart"/>: how to start the game again (null: never restarted here).</summary>
    async Task<List<TestResult>> RunGroupAsync(List<GameTest> group, GameSession session, UpOptions? restart, bool untouched,
        bool cleanAll, ResetRung? force, string runFolder, Func<GameTest, GameSession, Task<string?>>? skip = null, bool adopted = false)
    {
        var results = new List<TestResult>();
        string? speedBefore = null;
        var actions = new GameResetActions(GameCopy, session, restart, Limits);
        var ladder = new ResetLadder(actions, Out);
        // A copy found running: a baseline it keeps from earlier tests means they may have left
        // something, so the first test is prepared too. A fresh start opens the scope anew.
        // The main menu has no server to open a stage scope: a fresh start there is not asked
        // (GK.Stage.Begin would wait its whole timeout for an answer that never comes).
        var kept = untouched && group.All(t => t.Start.NoLevel) ? null : await ladder.OpenAsync();
        if (!untouched)
        {
            untouched = kept == false && !cleanAll;
            Out.WriteLine(kept switch
            {
                true => "stage scope: the baseline taken earlier in this copy is kept; each test is prepared from it",
                false => "stage scope: the world as it was found is the baseline",
                null => "stage scope: this bridge has no stage ledger (GK.Stage.Begin); tests run as the world is",
            });
        }
        // a group with no level has no level to judge, and no server to ask
        var simple = group.All(t => t.Start.NoLevel) ? null : await SimpleLevel.IsSimpleAsync(session, GameCopy.Settings);
        // Without the ledger nothing can be cleaned: only what a test declares is done (as before the ladder).
        // The rungs are a level's (fixture: clean the scene; venue: reload the level; game: restart). A test that runs
        // elsewhere - the main menu - has no scene to clean and no level to reload: it runs as the
        // place is, and only a restart (declared) applies to it.
        var needs = group.Select(s => s.Start.NoLevel ? (s.Start.Needs == ResetRung.Game ? ResetRung.Game : ResetRung.None)
            : kept is null ? Max(force, s.Start.Needs ?? ResetRung.None)
            : Max(force, ResetLadder.Needed(s.Start.Needs, simple))).ToList();
        // A checkpoint of this level, before anything is staged, for the `venue` rung: a fixture that
        // does not hold then climbs to a reload of a few tens of seconds, not to a restart of a
        // minute and more . Made once, a few
        // seconds, whenever the copy is fresh (a found copy's world is not known clean) and in a level.
        var inLevel = !group[0].Start.NoLevel &&(kept is not null || needs.Any(n => n >= ResetRung.Venue));
        actions.CheckpointAfterRestart = inLevel;
        // A game taken into this start by a save's load has that save as its checkpoint already.
        if (restart is not null && untouched && restart.Save is null && inLevel && !adopted)
        {
            try
            {
                var made = await session.CheckpointAsync();
                // the save of this level's start: a later group of the same level and mods loads it
                // in a running game instead of starting one
                if (restart.Level is { } level) new StartSaves(GameCopy.Layout).Record(level, GameCopy.Instance is not null ? restart.Mods ?? [] : restart.Mods, made);
            }
            catch (CopyException e) { Out.WriteLine("no checkpoint for the venue rung:" + Flatten(e.Message)); }
        }
        Out.WriteLine($"reset ladder: the level is {(simple switch { true => "a simple test level", false => "not a simple test level", _ => "of unknown size" })}"
            + $"; tests need {string.Join(", ", needs.Distinct().Select(n => n.Name()))}");
        if (new Bridge.EventLogReader(GameCopy.Layout.Channel).KindsMismatch() is { } kinds) Out.WriteLine(kinds);
        for (var i = 0; i < group.Count; i++)
            if (await RunPreparedAsync(i) is false) break;
        async Task<bool> RunPreparedAsync(int i)
        {
            var test = group[i];
            // a test that needs drawn frames (its header's `drawn`) in a headless run: a precondition
            // unmet, said with the test's own reason, and nothing prepared for it
            if (test.Drawn is { } drawnWhy && GameCopy.HeadlessAsked())
            {
                results.Add(Verdict(new TestResult(test.Name, false, "UNMET: needs drawn frames (headless run): " + drawnWhy, TimeSpan.Zero)
                    { Unmet = true }, Path.Combine(runFolder, test.Name)));
                return true;
            }
            // a main-menu test that declares no restart runs as the place is: nothing to prepare
            var asItIs = test.Start.NoLevel && needs[i] == ResetRung.None;
            var outcome = asItIs ? new ResetOutcome(ResetRung.None, ResetRung.None, [], []) : await ladder.PrepareAsync(needs[i], untouched);
            untouched = false;
            session = actions.Session;
            var line = outcome.Line();
            Out.WriteLine($"reset before {test.Name}: {line}");
            // the run's speed, or without one the speed the copy had; never above the test's own ceiling
            if (outcome.Ok && !test.Start.NoLevel && (Speed ?? speedBefore) is var wanted && (wanted is not null || test.SpeedCeiling is not null))
            {
                // every test, not only the first: `??=` alone would never look again once a speed was held
                var had = await HoldSpeedAsync(session, actions, wanted, test.SpeedCeiling);
                speedBefore ??= had;
            }
            if (outcome.Steps.Any(s => s.Rung == ResetRung.Fixture) && actions.SideEffects.Count > 0)
                Out.WriteLine("  side effects of the test before, taken back: " + string.Join(", ", actions.SideEffects.Distinct().Take(12)));
            if (outcome.Steps.Any(s => s.Rung == ResetRung.Fixture) && actions.Kept.Count > 0)
                Out.WriteLine("  kept by the game (not dirt): " + string.Join("; ", actions.Kept.Distinct().Take(6)));
            if (!outcome.Ok)
            {
                var remedy = ResetLadder.Remedy(outcome);
                var verdict = "FAIL (reset: no rung held: " + line + ")"
                    + (outcome.Rebased ? "; the world as it is now is the new baseline, so the next test is judged by its own changes" : "")
                    + (remedy is null ? "" : "; to let the ladder climb: " + remedy);
                results.Add(Verdict(new TestResult(test.Name, false, verdict, outcome.Took) { Reset = line },
                    Path.Combine(runFolder, test.Name)));
                return GameCopy.GamePids().Count > 0;
            }
            if (skip is not null && await skip(test, session) is { } elsewhere)
            {
                results.Add(Verdict(new TestResult(test.Name, false, "NOT RUN: " + elsewhere, TimeSpan.Zero, NotRun: true) { Reset = line },
                    Path.Combine(runFolder, test.Name)));
                return true;
            }
            var need = needs[i];
            // between two cases of a test: the same rung as between tests, from the cheapest up
            async Task<(bool, string)> Between()
            {
                var again = await ladder.PrepareAsync(need, false);
                return (again.Ok, again.Line());
            }
            results.Add(RunOne(test, session, Path.Combine(runFolder, test.Name), between: asItIs ? null : Between) with { Reset = line, ResetSteps = outcome.Steps });
            return !(GameCopy.GamePids().Count == 0 && restart is null); // the game died and may not be started here
        }
        // A copy that stays up gets back the speed it had before `--speed`, as its baseline.
        if (speedBefore is { } before && restart is null && GameCopy.GamePids().Count > 0)
        {
            try
            {
                await actions.CleanAsync();
                await HoldSpeedAsync(actions.Session, actions, before);
                Out.WriteLine($"speed: back to {before}");
            }
            catch (CopyException e) { Out.WriteLine("speed: not put back (" + Flatten(e.Message) + ")"); }
        }
        // A copy that stays up is left as it was found: what the last test staged is cleaned.
        if (restart is null && kept is not null && GameCopy.GamePids().Count > 0 && results.Count > 0)
        {
            try
            {
                var (clean, problems) = await actions.CleanAsync();
                Out.WriteLine(clean ? "after the suite: cleaned" : "after the suite: not clean: " + string.Join("; ", problems.Take(4)));
            }
            catch (CopyException e) { Out.WriteLine("after the suite: no clean (" + Flatten(e.Message) + ")"); }
        }
        return results;
    }

    static ResetRung Max(ResetRung? a, ResetRung b) => a is { } x && x > b ? x : b;

    /// <summary>The files of a test, or of a group of tests that share them (their header's `files`),
    /// put into the copy (<see cref="TestFiles"/>); null when they are in place, else why not.</summary>
    string? PlaceFiles(string tests, IReadOnlyList<(string Destination, string Source)> files)
    {
        try
        {
            GameCopy.PlaceTestFiles(tests, files);
            Out.WriteLine($"files of {tests}: {string.Join(", ", files.Select(f => f.Destination))}");
            return null;
        }
        catch (CopyException e) { return e.Message; }
    }

    /// <summary>The files taken out of the copy, whatever the verdicts; what stays is said.</summary>
    void RemoveFiles()
    {
        foreach (var left in new TestFiles(GameCopy.Layout).Remove()) Out.WriteLine("  files: left " + left);
    }

    /// <summary>`test --speed`: the game speed every test of the run starts at - a positive number,
    /// or `working` (GK.Speed.Working). The reset ladder puts the speed back to the baseline's, so
    /// the speed is set and the baseline taken again at it; a level reload or a restart that lost it
    /// is caught before the next test.</summary>
    public string? Speed { get; set; }

    /// <summary>`test --restart-groups`: every group in a new process, the game closed after each.</summary>
    public bool RestartGroups { get; set; }

    /// <summary>`test --trace`: one trace of the entity system a step on both sides (<see cref="StepTraces"/>).</summary>
    public bool Trace { get; set; }

    /// <summary>The reset ladder's waits counted in time (<see cref="ResetLimits"/>).</summary>
    public ResetLimits Limits { get; set; } = ResetLimits.Default;

    /// <summary>Sets the game to <paramref name="speed"/> when it runs at another, and takes the
    /// stage's baseline again at it. Returns the speed the game had, or null when nothing changed.</summary>
    async Task<string?> HoldSpeedAsync(GameSession session, GameResetActions actions, string? speed, double? ceiling = null)
    {
        var want = speed is null ? "(GK.Speed.Get() or 1)" : speed == "working" ? "GK.Speed.Working" : speed;
        var top = ceiling is { } c ? c.ToString(CultureInfo.InvariantCulture) : "nil";
        var had = await session.LuaAsync("server", $$"""
            local want, top, had = {{want}}, {{top}}, GK.Speed.Get() or 1
            if top and want > top then want = top end
            if math.abs(had - want) < 0.001 then return false end
            GK.Speed.Set(want)
            return tostring(had) .. " " .. tostring(want)
            """, TimeSpan.FromSeconds(20), "cheat");
        if (had is not JsonValue value || !value.TryGetValue<string>(out var answer)) return null;
        var parts = answer.Split(' ');
        await actions.BeginAsync(fresh: true);
        Out.WriteLine($"speed: {parts[^1]}{(ceiling is null ? "" : " (the test's ceiling: " + top + ")")}; the game ran at {parts[0]}; the baseline is taken again at it");
        return parts[0];
    }

    async Task<int> RunAttachedAsync(IReadOnlyList<GameTest> tests, bool clean, ResetRung? force)
    {
        var runFolder = RunFolder("suite");
        var suite = Stopwatch.StartNew();
        var session = GameCopy.Session();
        Out.WriteLine("in the running game, as it is (tests that declare another level are not run)");
        var results = await RunGroupAsync([.. tests], session, restart: null, untouched: false, clean, force, runFolder,
            skip: ElsewhereAsync);
        foreach (var test in tests.Where(s => !results.Any(r => r.Name == s.Name)))
            results.Add(new TestResult(test.Name, false, "FAIL (not run: the game closed)", TimeSpan.Zero));
        return Summarize(results, suite, runFolder);
    }

    /// <summary>Why a test with files is not run in a game that is already up.</summary>
    const string FilesNeedAStart = "its files go into the copy before its game starts, and this game is already up; run it without --attach";

    /// <summary>Why an attached test cannot be a fair replay here (null = it can): it has files, or it declares a
    /// level and the running game is in another. A recipe that starts in character creation proves
    /// nothing in Act 1, and its failure there used to be counted as the recipe's. When the game
    /// does not say where it is, the test runs.</summary>
    async Task<string?> ElsewhereAsync(GameTest test, GameSession session)
    {
        if (test.Files.Count > 0) return FilesNeedAStart;
        if (test.Start.MainMenu)
        {
            string? level = null;
            try
            {
                level = await session.LuaAsync("server", "return GK.Stage.CurrentLevel()", TimeSpan.FromSeconds(10)) is JsonValue v
                    && v.TryGetValue<string>(out var n) ? n : null;
            }
            catch (CopyException) { return null; }   // no server answering: the main menu, most likely
            return string.IsNullOrEmpty(level) ? null
                : $"it starts in the main menu and the running game is in {level}; start a copy with `up --main-menu`";
        }
        if (test.Start.Level is not { } declared) return null;
        var wanted = declared[(declared.IndexOf(':', StringComparison.Ordinal) + 1)..];
        string? here;
        try
        {
            here = await session.LuaAsync("server", "return GK.Stage.CurrentLevel()", TimeSpan.FromSeconds(20)) is JsonValue value
                && value.TryGetValue<string>(out var name) ? name : null;
        }
        catch (CopyException) { return null; }
        if (string.IsNullOrEmpty(here) || string.Equals(here, wanted, StringComparison.OrdinalIgnoreCase)) return null;
        return $"it declares {declared} and the running game is in {here}; start a copy in that level for it "
            + "(`up --level`, then `checkpoint`) or run it without --attach";
    }

    int Summarize(List<TestResult> results, Stopwatch suite, string runFolder)
    {
        Out.WriteLine("--");
        foreach (var result in results) Out.WriteLine(Line(result));
        var unmet = results.Count(r => r.Unmet);
        var failed = results.Count(r => !r.Passed && !r.NotRun && !r.Unmet);
        var notRun = results.Count(r => r.NotRun);
        Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{results.Count - failed - notRun - unmet} passed, {failed} failed")
            + (unmet > 0 ? $", {unmet} unmet" : "")
            + (notRun > 0 ? $", {notRun} not run" : "")
            + string.Create(CultureInfo.InvariantCulture, $", {suite.Elapsed.TotalSeconds:0} s in all; output: {runFolder}"));
        Out.WriteLine(Timing(results, suite.Elapsed));
        foreach (var line in Slowness(results)) Out.WriteLine(line);
        var code = ExitCode(results);
        WriteResults(results, suite.Elapsed, runFolder, code);
        return code;
    }

    /// <summary>How many of the slowest steps a run names.</summary>
    public const int SlowestShown = 5;

    /// <summary>The slowest steps of a run, each named `&lt;test id&gt;/&lt;step name&gt;`, slowest first.</summary>
    public static IEnumerable<(string Step, StepRecord Record)> Slowest(IReadOnlyList<TestResult> results, int count = SlowestShown) =>
        results.SelectMany(r => r.Steps.Select(s => (Step: r.Name + "/" + (s.Case is { } c ? c + "/" : "") + s.Name, Record: s)))
            .OrderByDescending(s => s.Record.Took).Take(count);

    /// <summary>Where a run's time went: the slowest steps, and the time inside waits of `game` by
    /// kind apart from the time in the game's own answers.</summary>
    public static IEnumerable<string> Slowness(IReadOnlyList<TestResult> results)
    {
        var slowest = Slowest(results).ToList();
        if (slowest.Count == 0) yield break;
        yield return "slowest steps: " + string.Join("; ", slowest.Select(s => string.Create(CultureInfo.InvariantCulture,
            $"{s.Step} {s.Record.Took.TotalSeconds:0.0} s (waits {s.Record.Waits:0.0} s, answers {s.Record.Answers:0.0} s)")));
        var waits = WaitTotals(results);
        yield return string.Create(CultureInfo.InvariantCulture, $"time in waits {waits.Values.Sum():0.0} s")
            + (waits.Count > 0 ? " (" + string.Join(", ", waits.OrderByDescending(w => w.Value).Select(w => string.Create(CultureInfo.InvariantCulture, $"{w.Key} {w.Value:0.0} s"))) + ")" : "")
            + string.Create(CultureInfo.InvariantCulture, $"; in the game's answers {results.Sum(r => r.Answers):0.0} s");
    }

    static SortedDictionary<string, double> WaitTotals(IReadOnlyList<TestResult> results)
    {
        var totals = new SortedDictionary<string, double>(StringComparer.Ordinal);
        foreach (var (kind, seconds) in results.SelectMany(r => r.Waits)) totals[kind] = totals.GetValueOrDefault(kind) + seconds;
        return totals;
    }

    /// <summary>0 when every test passed; 1 when one failed or was not run; 2 when none failed and
    /// one or more found a precondition unmet (need): the thing tested was not judged.</summary>
    public static int ExitCode(IReadOnlyList<TestResult> results) =>
        results.Any(r => r.NotRun || (!r.Passed && !r.Unmet)) ? 1 : results.Any(r => r.Unmet) ? 2 : 0;

    /// <summary>The results of a run as data (record/test-run, docs/contracts/cli.md, "The tree of tests"):
    /// <c>&lt;run folder&gt;/results.json</c>, printed by <c>test --json</c>.</summary>
    public static JsonObject Results(IReadOnlyList<TestResult> results, TimeSpan took, int exitCode) => new()
    {
        ["schema"] = "record/test-run/1",
        ["exit"] = exitCode,
        ["seconds"] = Math.Round(took.TotalSeconds, 1),
        ["slowest"] = new JsonArray([.. Slowest(results).Select(s => (JsonNode)new JsonObject
        {
            ["step"] = s.Step, ["seconds"] = Math.Round(s.Record.Took.TotalSeconds, 2),
            ["waits"] = Math.Round(s.Record.Waits, 2), ["answers"] = Math.Round(s.Record.Answers, 2),
        })]),
        ["waits"] = Seconds(WaitTotals(results)),
        ["answers"] = Math.Round(results.Sum(r => r.Answers), 2),
        ["tests"] = new JsonArray([.. results.Select(r => (JsonNode)new JsonObject
        {
            ["test"] = r.Name,
            ["outcome"] = r.Outcome,
            ["verdict"] = r.Verdict,
            ["seconds"] = Math.Round(r.Took.TotalSeconds, 1),
            ["waits"] = Seconds(r.Waits),
            ["answers"] = Math.Round(r.Answers, 2),
            // a test with cases keeps its steps under them
            ["steps"] = StepsJson(r.Cases.Count > 0 ? [] : r.Steps),
            ["cases"] = new JsonArray([.. r.Cases.Select(c =>
            {
                var o = new JsonObject
                {
                    ["name"] = c.Name, ["outcome"] = c.Outcome, ["seconds"] = Math.Round(c.Took.TotalSeconds, 1),
                    ["steps"] = StepsJson(c.Steps), ["surprises"] = SurprisesJson(c.Surprises),
                };
                if (c.Error is { } error) o["error"] = error;
                if (c.Reset is { } reset) o["reset"] = reset;
                return (JsonNode)o;
            })]),
            ["surprises"] = SurprisesJson(r.Surprises),
        })]),
    };

    static JsonArray StepsJson(IEnumerable<StepRecord> steps) => new([.. steps.Select(s =>
    {
        var step = new JsonObject
        {
            ["name"] = s.Name, ["class"] = s.Class, ["outcome"] = s.Outcome ?? "fail",
            ["seconds"] = Math.Round(s.Took.TotalSeconds, 1),
            ["waits"] = Math.Round(s.Waits, 2), ["answers"] = Math.Round(s.Answers, 2),
        };
        if (s.Error is { } error) step["error"] = error;
        return (JsonNode)step;
    })]);

    static JsonArray SurprisesJson(IEnumerable<Surprise> surprises) => new([.. surprises.Select(s => (JsonNode)new JsonObject
    {
        ["sort"] = s.Sort, ["what"] = s.What, ["rows"] = s.Rows, ["detail"] = s.Detail,
    })]);

    static JsonObject Seconds(IEnumerable<KeyValuePair<string, double>> waits)
    {
        var o = new JsonObject();
        foreach (var (kind, seconds) in waits.OrderBy(w => w.Key, StringComparer.Ordinal)) o[kind] = Math.Round(seconds, 2);
        return o;
    }

    /// <summary>Set by `test --json`: the results are printed as JSON after the summary.</summary>
    public bool Json { get; set; }

    void WriteResults(IReadOnlyList<TestResult> results, TimeSpan took, string runFolder, int code)
    {
        var data = Results(results, took, code);
        try
        {
            Directory.CreateDirectory(runFolder);
            GateKit.Json.JsonText.Write(Path.Combine(runFolder, "results.json"), data, "record/test-run");
        }
        catch (IOException e) { Out.WriteLine("note: results.json was not written: " + e.Message); }
        if (Json) Out.WriteLine(GateKit.Json.JsonText.Format(data, "record/test-run"));
    }

    /// <summary>The numbers a measurement of the ladder needs, from the results alone:
    /// <c>timing: 30 runs, 9.5 s a run in all (run 3.6 s); fixture held 25 (1.4 s), venue held 4 (23.1 s) ...</c>
    /// A rung that did not hold counts its time too.</summary>
    public static string Timing(IReadOnlyList<TestResult> results, TimeSpan total)
    {
        var ran = results.Where(r => !r.NotRun).ToList();
        if (ran.Count == 0) return "timing: no runs";
        var rungs = ran.SelectMany(r => r.ResetSteps).GroupBy(s => (s.Rung, s.Ok)).OrderBy(g => g.Key.Rung).ThenByDescending(g => g.Key.Ok)
            .Select(g => string.Create(CultureInfo.InvariantCulture,
                $"{g.Key.Rung.Name()} {(g.Key.Ok ? "held" : "failed")} {g.Count()} ({g.Average(s => s.Took.TotalSeconds):0.0} s, {g.Min(s => s.Took.TotalSeconds):0.0}-{g.Max(s => s.Took.TotalSeconds):0.0})"));
        return string.Create(CultureInfo.InvariantCulture,
            $"timing: {ran.Count} runs, {total.TotalSeconds / ran.Count:0.0} s a run in all (run {ran.Average(r => r.Took.TotalSeconds):0.0} s); ")
            + (rungs.Any() ? string.Join(", ", rungs) : "no reset");
    }

    /// <summary>`run test.lua`: one test with a start of its own. Returns an exit code.</summary>
    public async Task<int> RunFileAsync(string path, bool keep, bool attach, UpOptions? overrides = null, CancellationToken cancel = default)
    {
        var test = GameTest.Load(path);
        var outFolder = RunFolder(test.Name);
        Directory.CreateDirectory(outFolder);
        GameCopy.CleanStaleTestFiles();
        keep |= attach;   // a game found running is never closed
        var placed = false;
        try
        {
            if (attach && test.Files.Count > 0) throw new CopyException(FilesNeedAStart);
            // the files go in before the start, so the game reads them from its first frame
            if (test.Files.Count > 0)
            {
                if (PlaceFiles(test.Name, test.Files) is { } refused) throw new CopyException(refused);
                placed = true;
            }
            GameSession session;
            if (attach)
            {
                if (GameCopy.GamePids().Count == 0) throw new CopyException("--attach needs a running game (start one with `up`)");
                session = GameCopy.Session();
            }
            else
            {
                var start = test.Start;
                session = await GameCopy.UpAsync(new UpOptions(overrides?.Level ?? start.Level, overrides?.Save ?? start.Save,
                    overrides?.Mods ?? start.Mods, overrides?.StoryLog ?? false, overrides?.GameArgs,
                    MainMenu: start.MainMenu, StartupVideos: start.StartupVideos, UntilLua: start.UntilLua, FirstLaunch: start.FirstLaunch), cancel);
            }
            var result = RunOne(test, session, outFolder, say: false);
            Out.WriteLine(result.Passed
                ? $"PASS {result.Name}  (output: {outFolder})"
                : result.Unmet ? $"UNMET {result.Name}: {result.Verdict[7..]}"
                : $"FAIL {result.Name}: {result.Verdict[6..]}");
            foreach (var line in Steps.Lines(result.Steps)) Out.WriteLine(line);
            return ExitCode([result]);
        }
        catch (CopyException problem) // the game would not start, or --attach found none
        {
            Out.WriteLine($"FAIL {test.Name}: {problem.Message}");
            return 1;
        }
        finally
        {
            if (!keep) GameCopy.Down();
            if (placed) RemoveFiles();
        }
    }
}
