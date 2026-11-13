using System.CommandLine;
using GateKit.Platform;
using GateKit.Stand;

namespace GateKit.Scenarios;

/// <summary>What both scenario commands share: the stand, and errors as `ERROR:` lines.</summary>
static class ScenarioHost
{
    /// <summary>The default scenario folder: `scenarios` next to the repository, or in the working folder.</summary>
    public static string DefaultFolder()
    {
        for (var folder = new DirectoryInfo(Directory.GetCurrentDirectory()); folder is not null; folder = folder.Parent)
        {
            var candidate = Path.Combine(folder.FullName, "scenarios");
            if (Directory.Exists(candidate)) return candidate;
        }
        return Path.Combine(Directory.GetCurrentDirectory(), "scenarios");
    }

    /// <summary>`--instance` is a recursive option of the root command (GateKit.Cli owns it).</summary>
    static string? Instance(ParseResult parse)
    {
        var option = parse.RootCommandResult.Command.Options.FirstOrDefault(o => o.Name == "--instance");
        return option is Option<string?> typed ? parse.GetValue(typed) : null;
    }

    public static async Task<int> Run(ParseResult parse, Func<ScenarioRunner, Task<int>> action)
    {
        try
        {
            var platform = GamePlatform.Current;
            var settings = StandSettings.Resolve(platform, instanceOverride: Instance(parse));
            return await action(new ScenarioRunner(new Stand.Stand(settings, platform, Console.Out), Console.Out));
        }
        catch (StandException e)
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

/// <summary>`gatekit test [folder] [-k name] [--clean]` (docs/contracts/cli.md, 2.13).</summary>
public static class TestCommand
{
    public static Command CreateCommand()
    {
        var folder = new Argument<string?>("folder") { Description = "folder with scenario .lua files (default: `scenarios` in the repository)", Arity = ArgumentArity.ZeroOrOne };
        var only = new Option<string?>("-k") { Description = "only scenarios whose name contains this" };
        var clean = new Option<bool>("--clean") { Description = "return the world to its state after the start before every scenario (about 14 s each)" };
        var command = new Command("test", "run every scenario in a folder; scenarios that need the same start share one game") { folder, only, clean };
        command.SetAction((parse, cancel) => ScenarioHost.Run(parse, runner =>
            runner.RunSuiteAsync(parse.GetValue(folder) ?? ScenarioHost.DefaultFolder(), parse.GetValue(only), parse.GetValue(clean), cancel)));
        return command;
    }
}

/// <summary>`gatekit run &lt;scenario.lua&gt; [--keep] [--attach]` (docs/contracts/cli.md, 2.14).</summary>
public static class RunCommand
{
    public static Command CreateCommand()
    {
        var file = new Argument<string>("scenario") { Description = "a scenario .lua file" };
        var keep = new Option<bool>("--keep") { Description = "leave the game running afterwards" };
        var attach = new Option<bool>("--attach") { Description = "use the game that is already up (no start, no stop): for writing scenarios" };
        var level = new Option<string?>("--level") { Description = "start in MODULE:LEVEL instead of what the scenario declares" };
        var save = new Option<string?>("--save") { Description = "load this save instead of what the scenario declares" };
        var mods = new Option<string?>("--mods") { Description = "comma-separated mod folders instead of what the scenario declares" };
        var gameArgs = new Argument<string[]>("game-args") { Description = "extra game arguments, after `--`", Arity = ArgumentArity.ZeroOrMore };
        var command = new Command("run", "start the game, run one scenario, close the game") { file, keep, attach, level, save, mods, gameArgs };
        command.SetAction((parse, cancel) => ScenarioHost.Run(parse, runner =>
        {
            var modList = parse.GetValue(mods) is { } text ? text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : null;
            var overrides = new UpOptions(parse.GetValue(level), parse.GetValue(save), modList, false, parse.GetValue(gameArgs));
            return runner.RunFileAsync(parse.GetValue(file)!, parse.GetValue(keep), parse.GetValue(attach), overrides, cancel);
        }));
        return command;
    }
}
