using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using GateKit.Mcp;
using GateKit.Testing;
using GateKit.Copies;

namespace GateKit.Sweep;

/// <summary>
/// What a sweep leaves for every never-seen type it lit: a recipe in tests/_coverage (the
/// step's own Lua on a fresh dummy, watched from before, failing unless the types come alive), a
/// replay of it from a clean world, and a findings row saying honestly what backs it - a replay
/// that passed is a finding reached by cheat, a replay that failed leaves a lead.
/// </summary>
public sealed class SweepRecipes(ISweepGame game, TextWriter output)
{
    /// <summary>The recipe's file name (without folder): sweep_&lt;kind&gt;_&lt;step id&gt;.lua.</summary>
    public static string FileName(string kind, string stepId) => "sweep_" + Safe(kind + "_" + stepId) + ".lua";

    /// <summary>The test of one step that lit <paramref name="types"/>; <paramref name="direct"/>
    /// uses the step's AddBoosts fallback, which is what lit them.</summary>
    public static string RecipeText(SweepPlan plan, SweepStep step, IReadOnlyList<(string Side, string Component)> types, bool direct, string level)
    {
        var how = direct && step.Fallback is { } fallback ? fallback.How : step.How;
        var undo = direct && step.Fallback is { } back ? back.Undo : step.Undo;
        var carrier = direct ? $"AddBoosts {step.Fallback?.Carrier.Name}" : $"{step.Carrier.Kind} {step.Carrier.Name}";
        var needTarget = System.Text.RegularExpressions.Regex.IsMatch(how, @"\bT\b");
        var spawn = new StringBuilder("SweepDummy = GK.Stage.Spawn{ template = \"human\", faction = \"neutral\", distance = 5 }[1]\n");
        if (needTarget) spawn.Append("SweepTarget = GK.Stage.Spawn{ template = \"human\", faction = \"neutral\", distance = 8 }[1]\n");
        spawn.Append("return { SweepDummy, SweepTarget }");
        var steps = new List<RecipeTest.Step>
        {
            new("server", spawn.ToString(), null, null, null, null, "A fresh dummy (a temporary neutral human) for the step to work on."),
            new("server", null, null, "GK.Stage.Ready({ SweepDummy, SweepTarget })", 20, null, null),
            new("server", SweepRunner.Locals + how, null, null, 20, Math.Max(1.5, step.Settle + 0.5),
                $"The sweep's step {step.Id}: {carrier} (a cheat: the cause is staged, no player acts)."),
        };
        var cleanup = new List<RecipeTest.Step>
        {
            new("server", SweepRunner.Locals + (undo ?? "") + "\nfor _, id in ipairs({ D, T }) do pcall(GK.Stage.Remove, id) end\n"
                + "SweepDummy, SweepTarget = nil, nil\nreturn true", null, null, null, null, "Leave nothing behind for the next replay."),
        };
        var names = string.Join(", ", types.Select(t => t.Component));
        var header = new StringBuilder()
            .Append("-- ").Append(names).Append(": made to appear by a sweep step, no model in the loop.\n")
            .Append("-- Claim: ").Append(step.What).Append(" makes ").Append(types.Count == 1 ? "it" : "them").Append(" appear on a fresh dummy.\n")
            .Append("-- Reached by cheat (").Append(carrier).Append("), in ").Append(level).Append("; plan `").Append(plan.Kind)
            .Append("`, step ").Append(step.Id).Append(".\n")
            .Append("-- Written ").Append(DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .Append(" by `gatekit sweep run`. Replay: gatekit --instance <copy> test tests/_coverage -k ")
            .Append(FileName(plan.Kind, step.Id)[..^4]).Append("\n\n");
        return RecipeTest.Write(header.ToString(), level, types, steps, 10, mayExist: false, cleanup);
    }

    /// <summary>Writes a recipe for every step that lit a type, returns the world to its checkpoint,
    /// replays each recipe once and appends the findings rows.</summary>
    public async Task WriteAndConfirmAsync(SweepPlan plan, SweepState state, CancellationToken cancel)
    {
        var repository = Path.GetDirectoryName(Path.GetDirectoryName(game.CoverageRoot))!;
        var folder = Path.Combine(repository, "tests", "_coverage");
        Directory.CreateDirectory(folder);
        var written = new List<(SweepStep Step, string Path, List<LitType> Types)>();
        foreach (var group in state.Lit.GroupBy(l => (l.Step, l.By)))
        {
            if (state.Recipes.Any(r => r.Step == group.Key.Step && group.All(l => r.Types.Contains(l.Type)))) continue;
            var step = plan.Steps.FirstOrDefault(s => s.Id == group.Key.Step);
            if (step is null) continue;
            var types = group.Select(l => (l.Side, l.Type)).Distinct().ToList();
            var name = FileName(plan.Kind, step.Id + (group.Key.By == "direct" ? "_direct" : ""));
            var path = Path.Combine(folder, name);
            File.WriteAllText(path, RecipeText(plan, step, types, group.Key.By == "direct", game.Level).ReplaceLineEndings("\n"), new UTF8Encoding(false));
            written.Add((step, path, group.ToList()));
            output.WriteLine($"recipe written: tests/_coverage/{name} ({string.Join(", ", types.Select(t => t.Type))})");
        }
        if (written.Count == 0) return;

        var clean = false;
        if (game.GameCopy is { } copy && game.Session is { } session)
        {
            try
            {
                await game.ResetWorldAsync(cancel);
                state.Resets++;
                clean = true;
                output.WriteLine("world returned to the checkpoint for the replays");
            }
            catch (CopyException e) { output.WriteLine("no clean world for the replays (" + e.Message.Split('\n')[0] + "); they run in the world as it is"); }
            var runner = new TestRunner(copy, output);
            var runs = runner.RunFolder("sweep-recipes");
            foreach (var (step, path, types) in written)
            {
                var result = runner.RunOne(GateKit.Testing.GameTest.Load(path), session, Path.Combine(runs, Path.GetFileNameWithoutExtension(path)));
                Finish(plan, step, path, types, result.Passed, result.Verdict, clean, state);
            }
        }
        else
        {
            foreach (var (step, path, types) in written) Finish(plan, step, path, types, false, "not replayed (no game session)", clean, state);
        }
    }

    void Finish(SweepPlan plan, SweepStep step, string path, List<LitType> types, bool passed, string verdict, bool clean, SweepState state)
    {
        var recipe = "tests/_coverage/" + Path.GetFileName(path);
        state.Recipes.Add(new RecipeOutcome(step.Id, recipe, [.. types.Select(t => t.Type)], passed, verdict));
        var rows = new List<JsonObject>();
        foreach (var lit in types)
        {
            var row = new JsonObject
            {
                ["date"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                ["component"] = lit.Type,
                ["recipe"] = recipe,
                ["claim"] = $"{step.What} makes {lit.Type} appear on {lit.Side} on a fresh dummy"
                    + (lit.By == "direct" ? " (through the step's direct AddBoosts, after the carrier lit none of the expected types)" : "") + ".",
                ["evidence"] = passed ? "replayed" : "sighting",
                ["reached_by"] = "cheat",
                ["level"] = game.Level,
                ["fields"] = null,
                ["open"] = "Fields not examined: the sweep only makes the type appear. Verification against raw memory happens in the drain when a layout exists.",
                ["note"] = $"gatekit sweep, plan {plan.Kind}, run {state.Run}; replay {(passed ? "passed" : "did not pass")} "
                    + (clean ? "from the checkpoint" : "in the world as it was") + (passed ? "" : $": {verdict}"),
                ["kind"] = passed ? "finding" : "lead",
            };
            rows.Add(row);
        }
        Record.Append(game.CoverageRoot, "findings", rows);
        output.WriteLine($"{(passed ? "CONFIRMED" : "not confirmed")}: {recipe}" + (passed ? "" : $"  {verdict}"));
    }

    static string Safe(string text)
    {
        var builder = new StringBuilder();
        var underscore = false;
        foreach (var c in text)
        {
            if (char.IsAsciiLetterOrDigit(c)) { builder.Append(c); underscore = false; }
            else if (!underscore) { builder.Append('_'); underscore = true; }
        }
        return builder.ToString().Trim('_');
    }
}
