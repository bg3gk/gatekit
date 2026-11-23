using System.CommandLine;
using GateKit.Copies;
using GateKit.Platform;
using GateKit.Testing;

namespace GateKit.Cli;

/// <summary>`logic-check`: the differential check of the logic in one step. The tests of a folder that
/// declare a `logic_diff` header are run twice - the game as it is, then headless - each keeping its
/// event log (`test --keep-events`), and each test's two records are compared by its own rules
/// (<see cref="LogicDiffCommand.RulesOf"/>). Either run can be given instead of made (`--native`,
/// `--headless`): a run folder of an earlier `test --keep-events`.</summary>
public static class LogicCheckCommand
{
    public static Command Create()
    {
        var folder = new Argument<string?>("folder") { Description = "the scenarios: a folder of tests with a logic_diff header (default: tests/headless)", Arity = ArgumentArity.ZeroOrOne };
        var only = new Option<string?>("-k") { Description = "only the tests whose id is or contains this" };
        var native = new Option<string?>("--native") { Description = "the run of the game as it is: a run folder of `test --keep-events` (not made again)" };
        var headless = new Option<string?>("--headless") { Description = "the headless run: a run folder of `test --headless --keep-events` (not made again)" };
        var show = new Option<int>("--show") { Description = "how many differing rows to print per test", DefaultValueFactory = _ => 25 };
        var command = new Command("logic-check", "run the differential scenarios (tests with a logic_diff header) as the game is and headless, and compare each pair of records by the test's rules: THE SAME, or the rows that differ; exit 0 when every pair is the same") { folder, only, native, headless, show };
        command.SetAction(async (parse, cancel) =>
        {
            try
            {
                var where = parse.GetValue(folder) ?? Path.Combine(TestDefaultFolder(), "headless");
                var tests = GameTest.Discover(where, parse.GetValue(only), Console.Out).Where(t => t.LogicDiff is not null).ToList();
                if (tests.Count == 0) throw new CopyException("no test with a logic_diff header in the selection");
                var nativeRun = parse.GetValue(native) ?? await RunAsync(parse, tests, headlessRun: false, cancel);
                var headlessRun = parse.GetValue(headless) ?? await RunAsync(parse, tests, headlessRun: true, cancel);
                Console.Out.WriteLine($"native:   {nativeRun}");
                Console.Out.WriteLine($"headless: {headlessRun}");
                var same = 0;
                foreach (var test in tests)
                {
                    var nativeTest = Path.Combine(nativeRun, test.Name);
                    var headlessTest = Path.Combine(headlessRun, test.Name);
                    if (!Passed(nativeTest) || !Passed(headlessTest))
                    {
                        Console.Out.WriteLine($"UNVERIFIED {test.Name}: both recorded verdicts must be PASS");
                        continue;
                    }
                    var a = Path.Combine(nativeTest, "events");
                    var b = Path.Combine(headlessTest, "events");
                    if (!Directory.Exists(a) || !Directory.Exists(b))
                    {
                        Console.Out.WriteLine($"MISSING  {test.Name}: no record in {(Directory.Exists(a) ? b : a)}");
                        continue;
                    }
                    var (ok, lines) = LogicDiffCommand.Check(a, b, LogicDiffCommand.RulesOf(test.LogicDiff!), parse.GetValue(show));
                    if (ok) same++;
                    var counts = lines.FirstOrDefault(l => l.StartsWith("A: ", StringComparison.Ordinal) && l.EndsWith(" rows", StringComparison.Ordinal)) ?? "";
                    Console.Out.WriteLine($"{(ok ? "SAME    " : "DIFFERS ")} {test.Name}   {counts}");
                    foreach (var line in lines.Where(l => l.Contains("set aside", StringComparison.Ordinal))) Console.Out.WriteLine("    " + line);
                    if (!ok) foreach (var line in lines.Where(l => !l.Contains("set aside", StringComparison.Ordinal))) Console.Out.WriteLine("    " + line);
                }
                Console.Out.WriteLine($"{same} of {tests.Count} scenario(s) the same native and headless");
                return same == tests.Count ? 0 : 1;
            }
            catch (Exception e) when (e is CopyException or IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine("ERROR: " + e.Message);
                return 1;
            }
        });
        return command;
    }

    static bool Passed(string testFolder)
    {
        var verdict = Path.Combine(testFolder, "verdict.txt");
        return File.Exists(verdict)
            && File.ReadLines(verdict).FirstOrDefault()?.StartsWith("PASS ", StringComparison.Ordinal) == true;
    }

    static string TestDefaultFolder()
    {
        for (var at = new DirectoryInfo(Directory.GetCurrentDirectory()); at is not null; at = at.Parent)
            if (Directory.Exists(Path.Combine(at.FullName, "tests"))) return Path.Combine(at.FullName, "tests");
        return Path.Combine(Directory.GetCurrentDirectory(), "tests");
    }

    /// <summary>One run of the scenarios with their event logs kept, the game as it is or headless (every
    /// game the run starts). The copy must be stopped before the run starts.</summary>
    static async Task<string> RunAsync(ParseResult parse, IReadOnlyList<GameTest> tests, bool headlessRun, CancellationToken cancel)
    {
        var platform = GamePlatform.Current;
        var settings = CopySettings.Resolve(platform, instanceOverride: parse.GetValue(CopyCommands.InstanceOption),
            installedGame: parse.GetValue(CopyCommands.InstalledGameOption));
        if (settings.Instance is null)
            throw new CopyException("logic-check needs a named copy for a live run; use --instance NAME");
        var copy = new GameCopy(settings, platform, Console.Out);
        if (copy.GamePids().Count > 0)
            throw new CopyException("logic-check needs a stopped copy for a live run; use down first");

        const string headlessKey = "GATEKIT_HEADLESS";
        const string fastLoadKey = "GATEKIT_FAST_LOAD";
        var previousHeadless = Environment.GetEnvironmentVariable(headlessKey);
        var previousFastLoad = Environment.GetEnvironmentVariable(fastLoadKey);
        try
        {
            Environment.SetEnvironmentVariable(headlessKey, headlessRun ? "1" : null);
            Environment.SetEnvironmentVariable(fastLoadKey, null);
            var runner = new TestRunner(copy, Console.Out) { KeepEvents = true };
            var mode = headlessRun ? "headless" : "native";
            Console.Out.WriteLine($"--- the scenarios {mode} ---");
            var result = await runner.RunSuiteAsync(tests, cleanAll: false, cancel, requireHeadless: headlessRun);
            var runFolder = runner.LastRunFolder ?? throw new CopyException("the scenarios did not run");
            if (result != 0) throw new CopyException($"the {mode} suite failed; see {runFolder}");
            return runFolder;
        }
        finally
        {
            Environment.SetEnvironmentVariable(headlessKey, previousHeadless);
            Environment.SetEnvironmentVariable(fastLoadKey, previousFastLoad);
            if (copy.GamePids().Count > 0) copy.Down();
        }
    }
}
