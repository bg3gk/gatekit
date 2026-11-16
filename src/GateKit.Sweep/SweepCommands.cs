using System.CommandLine;
using System.Globalization;
using System.Text.Json.Nodes;
using GateKit.GameData;
using GateKit.GameData.Query;
using GateKit.Mcp;
using GateKit.Platform;
using GateKit.Copies;

namespace GateKit.Sweep;

/// <summary>`gatekit sweep plan|run|status|tour` (docs/contracts/cli.md, "sweep").</summary>
public static class SweepCommands
{
    /// <summary>Where a sweep starts when nothing says: the engine's basic level, which loads in a
    /// minute and holds little of its own, so what comes alive is what the steps made.</summary>
    public const string DefaultLevel = "GustavX:Basic_Level_A";

    public static Command CreateCommand()
    {
        var root = new Command("sweep", "plans built from the game's own data - every boost, status type and flag, functor and spell flag the "
            + "stats name - run step after step against a game copy with the capture on, no model in the loop");
        root.Subcommands.Add(PlanCommand());
        root.Subcommands.Add(RunCommand());
        root.Subcommands.Add(StatusCommand());
        root.Subcommands.Add(TourCommand());
        return root;
    }

    /// <summary>`--instance` is a recursive option of the root command (GateKit.Cli owns it).</summary>
    static string? Instance(ParseResult parse)
    {
        try { return parse.GetValue<string?>("--instance"); }
        catch (ArgumentException) { return null; }
    }

    static int Fail(string message)
    {
        Console.Error.WriteLine("ERROR: " + message);
        return 1;
    }

    /// <summary>Where plans and state files go by default: the copies' state folder (never committed:
    /// a plan is read from the game's data).</summary>
    static string StateFolder() => Path.Combine(GateKit.Home.GateKitHome.Current.State.Path!, "sweep");

    /// <summary>The registered component type names (coverage.json), which the boost mapping is read off.</summary>
    public static List<string> ComponentTypes(string coverageRoot)
    {
        var path = Path.Combine(coverageRoot, "coverage.json");
        if (!File.Exists(path)) throw new SweepException($"no {path}: the component types come from the coverage index");
        using var stream = File.OpenRead(path);
        return [.. (JsonNode.Parse(stream)?["components"] as JsonArray ?? []).Select(c => c?["name"]?.GetValue<string>())
            .OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    static Command PlanCommand()
    {
        var kind = new Argument<string>("kind") { Description = string.Join(", ", PlanBuilder.Kinds) + " (statustypes and surfaces read the extender's enums from --instance)" };
        var output = new Option<string?>("--out") { Description = "write the plan here (default <state>/sweep/<kind>.json)" };
        var index = new Option<string>("--db") { Description = "the data index", DefaultValueFactory = _ => DataPaths.IndexDatabase };
        var command = new Command("plan", "build a plan from the data index: one step per thing of the kind, through its cheapest carrier") { kind, output, index };
        command.SetAction(async (parse, cancel) =>
        {
            try
            {
                var coverage = Record.CoverageRoot() ?? throw new SweepException("not a checkout of GateKit: the component types come from research/coverage");
                using var db = ReadOnlyDb.Open(parse.GetValue(index)!);
                var built = db.Scalar("SELECT value FROM meta WHERE key = 'built'") as string;
                var name = parse.GetValue(kind)!;
                var inputs = new PlanInputs
                {
                    Enums = MorePlans.EnumsOf.TryGetValue(name, out var enums) ? await EnumsAsync(Instance(parse), enums, cancel) : new Dictionary<string, IReadOnlyList<string>>(),
                    Leads = name == "leads" ? GateKit.Leads.Lead.Current(coverage) : [],
                };
                var plan = PlanBuilder.Build(name, StatsData.Load(db), ComponentTypes(coverage), built, inputs);
                var path = parse.GetValue(output) ?? Path.Combine(StateFolder(), plan.Kind + ".json");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                File.WriteAllText(path, plan.ToJson());
                Console.Out.WriteLine($"{plan.Kind}: {plan.Steps.Count} steps -> {Path.GetFullPath(path)}");
                foreach (var (key, value) in plan.Counts ?? []) Console.Out.WriteLine($"  {key.Replace('_', ' '),-34} {value}");
                var carriers = plan.Steps.GroupBy(s => s.Carrier.Kind).OrderBy(g => g.Key, StringComparer.Ordinal);
                Console.Out.WriteLine("  carriers                           " + string.Join(", ", carriers.Select(g => $"{g.Key} {g.Count()}")));
                return 0;
            }
            catch (Exception e) when (e is SweepException or DataQueryException or CopyException or GateKit.Leads.LeadsException) { return Fail(e.Message); }
        });
        return command;
    }

    /// <summary>The labels of the extender's enums, as the running copy lists them (Ext.Enums).</summary>
    static async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> EnumsAsync(string? instance, string[] names, CancellationToken cancel)
    {
        if (instance is null) throw new SweepException($"this plan reads the extender's enum {string.Join(", ", names)} from a running copy: give --instance");
        var platform = GamePlatform.Current;
        var copy = new Copies.GameCopy(CopySettings.Resolve(platform, instanceOverride: instance), platform, TextWriter.Null);
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var answer = await copy.Session().LuaAsync("server",
                $"local out = {{}} for k in pairs(Ext.Enums.{name}) do if type(k) == 'string' then out[#out + 1] = k end end table.sort(out) return out",
                TimeSpan.FromSeconds(20), cancel);
            result[name] = [.. (answer as JsonArray ?? []).Select(n => n?.ToString()).OfType<string>()];
        }
        return result;
    }

    static Command RunCommand()
    {
        var planFile = new Argument<string>("plan") { Description = "a plan file (`sweep plan` writes one), or a kind: its plan in <state>/sweep" };
        var from = new Option<int?>("--from") { Description = "start at this step (0-based); default: where the state file stopped" };
        var limit = new Option<int?>("--limit") { Description = "run at most this many steps" };
        var speed = new Option<double>("--speed") { Description = "the game's time scale during the run (GK.Speed.Set); settle times shrink with it", DefaultValueFactory = _ => 1.0 };
        var level = new Option<string>("--level") { Description = "MODULE:LEVEL a copy is started in when none runs, and recipes declare", DefaultValueFactory = _ => DefaultLevel };
        var state = new Option<string?>("--state") { Description = "the state file (default: the plan's, with .state.json)" };
        var run = new Option<string?>("--run") { Description = "the run's id in the record (default GATEKIT_RUN, else sweep-<kind>-<date>)" };
        var drainEvery = new Option<int>("--drain-every") { Description = "drain the capture after this many steps", DefaultValueFactory = _ => 5 };
        var freshEvery = new Option<int>("--fresh-every") { Description = "new dummies after this many steps at the latest", DefaultValueFactory = _ => 10 };
        var resetEvery = new Option<int>("--reset-every") { Description = "return the world to its checkpoint after this many steps (0: never on a schedule)", DefaultValueFactory = _ => 0 };
        var timeout = new Option<double>("--step-timeout") { Description = "seconds the game may spend on one step's calls before the watchdog calls it a hang (settling does not count)", DefaultValueFactory = _ => 45 };
        var budget = new Option<double?>("--budget") { Description = "stop after this many minutes of wall clock" };
        var noRecipes = new Option<bool>("--no-recipes") { Description = "do not write or replay recipes for the types lit" };
        var command = new Command("run", "run a plan against the copy named by --instance: label, step, a few ticks, what came alive; resumable")
        {
            planFile, from, limit, speed, level, state, run, drainEvery, freshEvery, resetEvery, timeout, budget, noRecipes,
        };
        command.SetAction(async (parse, cancel) =>
        {
            try
            {
                var instance = Instance(parse) ?? throw new SweepException("name the game copy: gatekit --instance <copy> sweep run ...");
                var path = parse.GetValue(planFile)!;
                if (!File.Exists(path) && PlanBuilder.Kinds.Contains(path)) path = Path.Combine(StateFolder(), path + ".json");
                if (!File.Exists(path)) throw new SweepException($"no plan {path}; `gatekit sweep plan <kind>` writes one");
                var plan = SweepPlan.Read(path);
                var statePath = parse.GetValue(state) ?? Path.ChangeExtension(path, ".state.json");
                var runId = parse.GetValue(run) ?? (Environment.GetEnvironmentVariable("GATEKIT_RUN") is { Length: > 0 } set ? set
                    : $"sweep-{plan.Kind}-{DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
                var platform = GamePlatform.Current;
                var settings = CopySettings.Resolve(platform, instanceOverride: instance);
                var copy = new Copies.GameCopy(settings, platform, Console.Out);
                var host = new ToolHost(writer => new Copies.GameCopy(settings, platform, writer)) { Instance = settings.Instance };
                var game = new CopyGame(copy, host, parse.GetValue(level)!);
                var options = new SweepOptions
                {
                    From = parse.GetValue(from), Limit = parse.GetValue(limit), Speed = parse.GetValue(speed), Run = runId,
                    DrainEvery = Math.Max(1, parse.GetValue(drainEvery)), FreshEvery = Math.Max(1, parse.GetValue(freshEvery)),
                    ResetEvery = Math.Max(0, parse.GetValue(resetEvery)), StepTimeout = parse.GetValue(timeout),
                    Budget = parse.GetValue(budget) is { } minutes ? TimeSpan.FromMinutes(minutes) : null,
                    Recipes = !parse.GetValue(noRecipes), Instance = instance,
                };
                var result = await new SweepRunner(game, Console.Out).RunAsync(plan, path, statePath, options, cancel);
                Print(result.Summary());
                return 0;
            }
            catch (Exception e) when (e is SweepException or CopyException or PlatformCapabilityException) { return Fail(e.Message); }
        });
        return command;
    }

    static void Print(JsonObject summary)
    {
        foreach (var (key, value) in summary)
        {
            if (value is JsonArray list)
            {
                Console.Out.WriteLine($"{key.Replace('_', ' '),-28} {list.Count}");
                foreach (var item in list) Console.Out.WriteLine("    " + item);
            }
            else Console.Out.WriteLine($"{key.Replace('_', ' '),-28} {value?.ToJsonString() ?? "-"}");
        }
    }

    static Command StatusCommand()
    {
        var file = new Argument<string>("state") { Description = "a state file, or a kind: its state in <state>/sweep" };
        var command = new Command("status", "what a run did so far: steps, steps per minute, types lit, hangs and skips, recipes") { file };
        command.SetAction(parse =>
        {
            var path = parse.GetValue(file)!;
            if (!File.Exists(path) && PlanBuilder.Kinds.Contains(path)) path = Path.Combine(StateFolder(), path + ".state.json");
            if (!File.Exists(path)) return Fail($"no state file {path}");
            try
            {
                var state = System.Text.Json.JsonSerializer.Deserialize<SweepState>(File.ReadAllText(path), SweepPlan.Json)
                    ?? throw new SweepException($"{path} is not a state file");
                Print(state.Summary());
                return 0;
            }
            catch (Exception e) when (e is SweepException or System.Text.Json.JsonException) { return Fail(e.Message); }
        });
        return command;
    }

    static Command TourCommand()
    {
        var name = new Argument<string>("tour") { Description = "a tour of research/coverage/tours (life, actions2, nautiloid, creation, world) or a tour file; several joined by commas play as one" };
        var level = new Option<string>("--level") { Description = "MODULE:LEVEL a copy is started in when none runs", DefaultValueFactory = _ => "GustavX:WLD_Main_A" };
        var speed = new Option<double>("--speed") { Description = "the game's time scale during the tour", DefaultValueFactory = _ => 1.0 };
        var limit = new Option<int?>("--limit") { Description = "only the first N steps" };
        var run = new Option<string?>("--run") { Description = "the run's id in the record (default GATEKIT_RUN, else tour-<name>-<date>)" };
        var verify = new Option<bool>("--verify") { Description = "at the end, every layout against what is alive on both sides (bg3_verify, recorded)" };
        var command = new Command("tour", "play an old scripted tour again with the capture on, draining after every step: verification of the layouts its situations bring alive")
        {
            name, level, speed, limit, run, verify,
        };
        command.SetAction(async (parse, cancel) =>
        {
            try
            {
                var instance = Instance(parse) ?? throw new SweepException("name the game copy: gatekit --instance <copy> sweep tour ...");
                var coverage = Record.CoverageRoot() ?? throw new SweepException("not a checkout of GateKit");
                var paths = parse.GetValue(name)!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(n => SweepTour.Resolve(coverage, n)).ToList();
                var platform = GamePlatform.Current;
                var settings = CopySettings.Resolve(platform, instanceOverride: instance);
                var copy = new Copies.GameCopy(settings, platform, Console.Out);
                var host = new ToolHost(writer => new Copies.GameCopy(settings, platform, writer)) { Instance = settings.Instance };
                var game = new CopyGame(copy, host, parse.GetValue(level)!);
                var runId = parse.GetValue(run) ?? (Environment.GetEnvironmentVariable("GATEKIT_RUN") is { Length: > 0 } set ? set
                    : $"tour-{string.Join("-", paths.Select(Path.GetFileNameWithoutExtension))}-{DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
                var result = await new SweepTour(game, Console.Out).RunAsync(paths, runId, parse.GetValue(speed), parse.GetValue(limit), parse.GetValue(verify), cancel);
                Print(result);
                return 0;
            }
            catch (Exception e) when (e is SweepException or CopyException or PlatformCapabilityException) { return Fail(e.Message); }
        });
        return command;
    }
}
