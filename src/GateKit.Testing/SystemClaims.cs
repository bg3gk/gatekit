using System.CommandLine;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using GateKit.Copies;
using MoonSharp.Interpreter;

namespace GateKit.Testing;

/// <summary>
/// Claims about systems that the game checks (research/coverage/systems.md;
/// docs/contracts/cli.md, "Claims the game checks"). A claim is an item of a description's <c>does</c>; it becomes
/// checkable by carrying
/// <c>"check": {"when": "&lt;recipe in tests/_coverage&gt;", "expect": [{"appears"|"gone"|"equals"|"changes": {...}}]}</c>
/// or says <c>"check": "not checkable live: &lt;why&gt;"</c>. <c>systems check</c> replays the recipe on a
/// running copy with the expectations around it (the prelude's <c>expect</c>) and keeps the
/// verdict beside the claim: <c>"verdict": {"passed", "failed", "last", "cases": [...]}</c>.
/// </summary>
public static class SystemClaims
{
    public const string NotCheckable = "not checkable live";
    static readonly string[] Verbs = ["appears", "gone", "equals", "changes"];
    const int KeptCases = 20;

    static readonly JsonSerializerOptions Stored = new()
    {
        WriteIndented = true,
        IndentSize = 1,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The description file of a system: <c>&lt;folder&gt;/&lt;ns with :: as __&gt;/&lt;Name&gt;.json</c>.</summary>
    public static string FileOf(string folder, string system)
    {
        var cut = system.LastIndexOf("::", StringComparison.Ordinal);
        var ns = cut >= 0 ? system[..cut] : "_";
        var name = cut >= 0 ? system[(cut + 2)..] : system;
        return Path.Combine(folder, ns.Replace("::", "__", StringComparison.Ordinal), name + ".json");
    }

    /// <summary>Why a claim's check cannot be run (null: it can).</summary>
    public static string? Problem(JsonNode? check)
    {
        if (check is null) return "no check";
        if (check is JsonValue text && text.TryGetValue<string>(out var said))
            return said.StartsWith(NotCheckable, StringComparison.Ordinal) ? said : $"a check is an object or \"{NotCheckable}: why\", not \"{said}\"";
        if (check is not JsonObject spec) return "a check is an object";
        if (spec["when"] is not JsonValue w || !w.TryGetValue<string>(out var when) || when.Length == 0) return "`when` names the recipe (a test in tests/_coverage)";
        if (spec["expect"] is not JsonArray expects || expects.Count == 0) return "`expect` is a non-empty list";
        foreach (var item in expects)
        {
            if (item is not JsonObject one || one.Count != 1 || !Verbs.Contains(one.First().Key))
                return "each expectation is one of {\"appears\": ...}, {\"gone\": ...}, {\"equals\": ...}, {\"changes\": ...}";
            if (one.First().Value is not JsonObject args || args["component"] is null) return $"`{one.First().Key}` needs a component";
            if (one.First().Key is "equals" or "changes" && args["field"] is null) return $"`{one.First().Key}` needs a field";
            if (one.First().Key == "equals" && !args.ContainsKey("value")) return "`equals` needs a value";
        }
        return null;
    }

    /// <summary>Claims written, with a check, checked, passed, failed, not checkable live.</summary>
    public static JsonObject Tally(string folder, string? prefix = null)
    {
        int systems = 0, claims = 0, withCheck = 0, notCheckable = 0, checkedOnce = 0, passed = 0, failed = 0, malformed = 0;
        if (Directory.Exists(folder))
        {
            foreach (var path in Directory.EnumerateFiles(folder, "*.json", SearchOption.AllDirectories))
            {
                JsonObject? file;
                try { file = JsonNode.Parse(File.ReadAllText(path))?.AsObject(); }
                catch (Exception e) when (e is JsonException or InvalidOperationException) { continue; }
                if (file?["system"] is not JsonValue s || !s.TryGetValue<string>(out var system)) continue;
                if (prefix is not null && !system.StartsWith(prefix, StringComparison.Ordinal)) continue;
                systems++;
                foreach (var item in file["does"] as JsonArray ?? [])
                {
                    if (item is not JsonObject claim) continue;
                    claims++;
                    var check = claim["check"];
                    if (check is null) continue;
                    var problem = Problem(check);
                    if (problem is null) withCheck++;
                    else if (problem.StartsWith(NotCheckable, StringComparison.Ordinal)) { notCheckable++; continue; }
                    else { malformed++; continue; }
                    if (claim["verdict"]?["last"] is JsonValue last && last.TryGetValue<string>(out var verdict))
                    {
                        checkedOnce++;
                        if (verdict == "passed") passed++; else failed++;
                    }
                }
            }
        }
        return new JsonObject
        {
            ["systems"] = systems,
            ["claims"] = claims,
            ["with_a_check"] = withCheck,
            ["not_checkable_live"] = notCheckable,
            ["check_malformed"] = malformed,
            ["checked"] = checkedOnce,
            ["passed"] = passed,
            ["failed"] = failed,
        };
    }

    /// <summary>Runs every checkable claim of <paramref name="system"/> in the running game and writes
    /// each verdict beside its claim. Returns an exit code: 0 when every claim run passed.</summary>
    public static async Task<int> CheckAsync(TestRunner runner, string system, string folder, string tests, bool clean,
        int? only = null)
    {
        var copy = runner.GameCopy;
        var output = runner.Out;
        if (copy.GamePids().Count == 0) throw new CopyException("`systems check` needs a running game (start one with `up`)");
        var path = FileOf(folder, system);
        if (!File.Exists(path)) throw new CopyException($"no description of {system}: {path}");
        var file = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new CopyException($"{path} is not a JSON object");
        var does = file["does"] as JsonArray ?? [];
        var session = copy.Session();
        var game = await GameVersion(session);
        var build = typeof(SystemClaims).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        var runs = 0;
        var failures = 0;
        // --clean: the reset ladder before each claim (a clean of what the last recipe staged in a
        // simple test level, the level's checkpoint elsewhere); the copy is never restarted here.
        var actions = new GameResetActions(copy, session, restart: null, runner.Limits);
        var ladder = new ResetLadder(actions, output);
        bool? simple = null;
        var untouched = false;
        if (clean)
        {
            untouched = await ladder.OpenAsync() == false;
            simple = await SimpleLevel.IsSimpleAsync(session, copy.Settings);
        }
        for (var i = 0; i < does.Count; i++)
        {
            if (only is { } wanted && wanted != i + 1) continue;
            if (does[i] is not JsonObject claim) continue;
            var label = $"{system} claim {i + 1}";
            if (Problem(claim["check"]) is { } problem)
            {
                if (claim["check"] is not null) output.WriteLine($"SKIP  {label}: {problem}");
                continue;
            }
            var check = claim["check"]!.AsObject();
            var when = check["when"]!.GetValue<string>();
            var recipe = Path.Combine(tests, when.EndsWith(".lua", StringComparison.Ordinal) ? when : when + ".lua");
            if (clean)
            {
                ResetRung? declared = null;
                try { declared = File.Exists(recipe) ? GameTest.Load(recipe).Start.Needs : null; }
                catch (CopyException) { }
                var outcome = await ladder.PrepareAsync(ResetLadder.Needed(declared, simple), untouched);
                untouched = false;
                session = actions.Session;
                output.WriteLine($"reset before {label}: {outcome.Line()}");
                if (!outcome.Ok) { output.WriteLine($"FAIL  {label}: no reset held: {outcome.Line()}"); failures++; continue; }
            }
            var verdict = Run(runner, session, recipe, check["expect"]!.AsArray());
            runs++;
            var ok = verdict["ok"]!.GetValue<bool>();
            if (!ok) failures++;
            var record = claim["verdict"] as JsonObject ?? new JsonObject { ["passed"] = 0, ["failed"] = 0 };
            record["passed"] = (record["passed"]?.GetValue<int>() ?? 0) + (ok ? 1 : 0);
            record["failed"] = (record["failed"]?.GetValue<int>() ?? 0) + (ok ? 0 : 1);
            record["last"] = ok ? "passed" : "failed";
            var cases = record["cases"] as JsonArray ?? [];
            cases.Add(new JsonObject
            {
                ["date"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                ["instance"] = copy.Instance?.Name,
                ["game"] = game,
                ["gatekit"] = build,
                ["ok"] = ok,
                ["recipe"] = verdict["recipe"]?.DeepClone(),
                ["expect"] = verdict["expect"]?.DeepClone(),
            });
            while (cases.Count > KeptCases) cases.RemoveAt(0);
            record["cases"] = cases;
            claim["verdict"] = record;
            output.WriteLine($"{(ok ? "PASS" : "FAIL")}  {label} ({when}): "
                + string.Join("; ", (verdict["expect"] as JsonArray ?? []).Select(v => $"{v?["verb"]} {((v?["ok"]?.GetValue<bool>() ?? false) ? "ok" : "no")}: {v?["detail"]}")));
            File.WriteAllText(path, file.ToJsonString(Stored).ReplaceLineEndings("\n") + "\n", new UTF8Encoding(false));
        }
        output.WriteLine($"{runs - failures} passed, {failures} failed, of {does.Count} claims; written to {path}");
        return failures > 0 ? 1 : 0;
    }

    static async Task<string?> GameVersion(GameSession session)
    {
        try
        {
            return await session.LuaAsync("server", "return Ext.Utils.GameVersion()", TimeSpan.FromSeconds(10)) is JsonValue v
                && v.TryGetValue<string>(out var text) ? text : null;
        }
        catch (CopyException) { return null; }
    }

    /// <summary>One claim: the recipe's steps between expect.begin and expect.finish. Never throws:
    /// a recipe that fails is a verdict too.</summary>
    static JsonObject Run(TestRunner runner, GameSession session, string recipe, JsonArray expects)
    {
        if (!File.Exists(recipe))
            return new JsonObject { ["ok"] = false, ["recipe"] = "no such recipe: " + recipe, ["expect"] = new JsonArray() };
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var test = GameTest.Load(recipe);
            var script = GameTest.NewScript(runner.Out, Path.GetDirectoryName(test.Path));
            test.Declare(script);
            var api = new GameApi(script, runner.GameCopy, session,
                runner.RunFolder("claim-" + test.Name));
            Directory.CreateDirectory(api.OutFolder);
            script.Globals["game"] = api.Table;
            script.Globals["EXPECTS"] = LuaValues.ToLua(script, expects);
            var result = script.DoString("""
                expect.begin(EXPECTS)
                local ok, problem = pcall(run, game)
                local verdicts = expect.finish(EXPECTS)
                return { recipe = ok and "ok" or ("failed: " .. tostring(problem)), expect = verdicts }
                """, null, "claim");
            var answer = LuaValues.ToJson(result) as JsonObject ?? [];
            var list = answer["expect"] as JsonArray ?? [];
            var ok = list.Count == expects.Count && list.All(v => v?["ok"]?.GetValue<bool>() == true);
            Replays.Note(test.Path, ok, watch.Elapsed, Replays.Alive(script), runner.GameCopy, session, runner.Out, via: "systems check");
            return new JsonObject { ["ok"] = ok, ["recipe"] = answer["recipe"]?.DeepClone(), ["expect"] = list.DeepClone() };
        }
        catch (Exception e) when (e is InterpreterException or CopyException)
        {
            var message = e is InterpreterException lua ? lua.DecoratedMessage ?? lua.Message : e.Message;
            return new JsonObject { ["ok"] = false, ["recipe"] = "failed: " + message, ["expect"] = new JsonArray() };
        }
    }
}

/// <summary>`gatekit systems tally` and `gatekit --instance X systems check SYSTEM` (cli.md, "Claims the game checks").</summary>
public static class SystemsCommand
{
    /// <summary>The checkout the command is run in (a worktree's own files), else the one the program
    /// was built in: `systems tally` in a worktree used to count the main checkout's files.</summary>
    static string? Repository()
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "GateKit.slnx"))) return dir.FullName;
        return CopySettings.RepositoryRoot();
    }

    static string DefaultSystems() => Repository() is { } repo
        ? Path.Combine(repo, "research", "coverage", "systems") : Path.Combine("research", "coverage", "systems");

    static string DefaultRecipes() => Repository() is { } repo
        ? Path.Combine(repo, "tests", "_coverage") : Path.Combine("tests", "_coverage");

    public static Command CreateCommand()
    {
        var command = new Command("systems", "claims about systems that the game checks (research/coverage/systems.md)");

        var folder = new Option<string?>("--systems") { Description = "the descriptions folder (default research/coverage/systems of the repository)" };
        var ns = new Option<string?>("--ns") { Description = "only systems whose name starts with this" };
        var json = new Option<bool>("--json") { Description = "the counts as JSON" };
        var tally = new Command("tally", "claims written, with a check, checked, passed, failed, not checkable live") { folder, ns, json };
        tally.SetAction(parse =>
        {
            var counts = SystemClaims.Tally(parse.GetValue(folder) ?? DefaultSystems(), parse.GetValue(ns));
            if (parse.GetValue(json)) Console.Out.WriteLine(counts.ToJsonString());
            else Console.Out.WriteLine(string.Join("\n", counts.Select(p => $"{p.Key.Replace('_', ' '),-20} {p.Value}")));
            return 0;
        });
        command.Subcommands.Add(tally);

        var system = new Argument<string>("system") { Description = "the system, e.g. esv::inventory::StackSystem" };
        var systems = new Option<string?>("--systems") { Description = "the descriptions folder (default research/coverage/systems)" };
        var recipes = new Option<string?>("--recipes") { Description = "where `when` recipes are (default tests/_coverage)" };
        var clean = new Option<bool>("--clean") { Description = "the reset ladder before each claim: a clean in a simple test level, the checkpoint elsewhere" };
        var claim = new Option<int?>("--claim") { Description = "only this claim (1 = the first item of `does`)" };
        var check = new Command("check", "replay each checkable claim of a system in the running game and write its verdict beside it")
            { system, systems, recipes, clean, claim };
        check.SetAction((parse, cancel) => TestHost.Run(parse, runner => SystemClaims.CheckAsync(runner, parse.GetValue(system)!,
            parse.GetValue(systems) ?? DefaultSystems(), parse.GetValue(recipes) ?? DefaultRecipes(), parse.GetValue(clean), parse.GetValue(claim))));
        command.Subcommands.Add(check);
        return command;
    }
}
