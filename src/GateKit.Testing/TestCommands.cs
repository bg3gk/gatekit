using System.CommandLine;
using GateKit.Platform;
using GateKit.Copies;

namespace GateKit.Testing;

/// <summary>What both test commands share: the copy, and errors as `ERROR:` lines.</summary>
static class TestHost
{
    /// <summary>The default test folder: `tests` next to the repository, or in the working folder.</summary>
    public static string DefaultFolder()
    {
        for (var folder = new DirectoryInfo(Directory.GetCurrentDirectory()); folder is not null; folder = folder.Parent)
        {
            var candidate = Path.Combine(folder.FullName, "tests");
            if (Directory.Exists(candidate)) return candidate;
        }
        return Path.Combine(Directory.GetCurrentDirectory(), "tests");
    }

    /// <summary>`--instance` is a recursive option of the root command (GateKit.Cli owns it).</summary>
    static string? Instance(ParseResult parse)
    {
        var option = parse.RootCommandResult.Command.Options.FirstOrDefault(o => o.Name == "--instance");
        return option is Option<string?> typed ? parse.GetValue(typed) : null;
    }

    /// <summary>`--installed-game` is the root's too.</summary>
    static bool InstalledGame(ParseResult parse) =>
        parse.RootCommandResult.Command.Options.FirstOrDefault(o => o.Name == CopySettings.InstalledGameOption) is Option<bool> typed && parse.GetValue(typed);

    public static async Task<int> Run(ParseResult parse, Func<TestRunner, Task<int>> action)
    {
        try
        {
            var platform = GamePlatform.Current;
            var settings = CopySettings.Resolve(platform, instanceOverride: Instance(parse), installedGame: InstalledGame(parse));
            return await action(new TestRunner(new Copies.GameCopy(settings, platform, Console.Out), Console.Out));
        }
        catch (CopyException e)
        {
            Console.Error.WriteLine("ERROR: " + e.Message);
            return 1;
        }
        catch (PlatformCapabilityException e)
        {
            Console.Error.WriteLine("ERROR: " + e.Message);
            return 1;
        }
    }
}

/// <summary>`gatekit test [folder] [-k name] [--clean]` (docs/contracts/cli.md, 3.1).</summary>
public static class TestCommand
{
    public static Command CreateCommand()
    {
        var folder = new Argument<string?>("folder") { Description = "folder of tests (.lua), read with its subfolders; a test's id is its path under it (default: `tests` in the repository)", Arity = ArgumentArity.ZeroOrOne };
        var only = new Option<string?>("-k") { Description = "only the test whose id (path under the folder, without .lua) is exactly this; text that is no whole id: every test whose id contains it" };
        var json = new Option<bool>("--json") { Description = "print the results as JSON (record/test-run) after the summary; they are written to <run folder>/results.json either way" };
        var clean = new Option<bool>("--clean") { Description = "prepare the first test too in a copy found running (the reset ladder prepares every later one anyway)" };
        var reset = new Option<string?>("--reset") { Description = "at least this rung of the reset ladder before every test: fixture (our own clean-up in the scene, verified), venue (reload the level), game (restart the game, the last resort) or none; default: what each declares, else fixture in a simple test level and venue elsewhere, one rung up only when a rung does not hold" };
        var repeat = new Option<int>("--repeat") { Description = "run the selection this many times in a row in the same game (name#2 ...): with the timing line, a measurement of the ladder", DefaultValueFactory = _ => 1 };
        var speed = new Option<string?>("--speed") { Description = "the game speed every test starts at: a positive number, or `working` (GK.Speed.Working); the stage's baseline is taken again at it, and a copy left running gets its own speed back" };
        var attach = new Option<bool>("--attach") { Description = "run in the game that is already up (not started, not closed), whatever level the tests declare" };
        var trace = new Option<bool>("--trace") { Description = "trace the entity system through every step on both sides (GK.Trace): one trace a step, written as trace/<nn>-<step>.json in the test's output folder and as rows of the event log's trace family" };
        var command = new Command("test", "run every test in a folder and its subfolders; tests that need the same start share one game (a copy already in their level is used), each after the reset ladder; exit 0 all passed, 1 a failure, 2 a precondition unmet and nothing failed") { folder, only, clean, attach, reset, repeat, speed, trace, json };
        command.SetAction((parse, cancel) => TestHost.Run(parse, runner =>
        {
            runner.Json = parse.GetValue(json);
            runner.Speed = parse.GetValue(speed);
            runner.Trace = parse.GetValue(trace);
            return runner.RunSuiteAsync(parse.GetValue(folder) ?? TestHost.DefaultFolder(), parse.GetValue(only), parse.GetValue(clean), cancel, parse.GetValue(attach),
                parse.GetValue(reset) is { } level ? ResetRungs.Parse(level) : null, parse.GetValue(repeat));
        }));
        return command;
    }
}

/// <summary>`gatekit run &lt;test.lua&gt; [--keep] [--attach]` (docs/contracts/cli.md, 3.4).</summary>
public static class RunCommand
{
    public static Command CreateCommand()
    {
        var file = new Argument<string>("test") { Description = "a test .lua file" };
        var keep = new Option<bool>("--keep") { Description = "leave the game running afterwards" };
        var attach = new Option<bool>("--attach") { Description = "use the game that is already up (no start, no stop): for writing tests" };
        var level = new Option<string?>("--level") { Description = "start in MODULE:LEVEL instead of what the test declares" };
        var save = new Option<string?>("--save") { Description = "load this save instead of what the test declares" };
        var mods = new Option<string?>("--mods") { Description = "comma-separated mod folders instead of what the test declares" };
        var gameArgs = new Argument<string[]>("game-args") { Description = "extra game arguments, after `--`", Arity = ArgumentArity.ZeroOrMore };
        var command = new Command("run", "start the game, run one test, close the game") { file, keep, attach, level, save, mods, gameArgs };
        command.SetAction((parse, cancel) => TestHost.Run(parse, runner =>
        {
            var modList = parse.GetValue(mods) is { } text ? text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : null;
            var overrides = new UpOptions(parse.GetValue(level), parse.GetValue(save), modList, false, parse.GetValue(gameArgs));
            return runner.RunFileAsync(parse.GetValue(file)!, parse.GetValue(keep), parse.GetValue(attach), overrides, cancel);
        }));
        return command;
    }
}
