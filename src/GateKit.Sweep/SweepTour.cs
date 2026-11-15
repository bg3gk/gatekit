using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using GateKit.Bridge;
using GateKit.Copies;

namespace GateKit.Sweep;

/// <summary>
/// The scripted tours of 12-13 November (research/coverage/tours/*.json), played again with the
/// capture on: many short situations one after another in one running game, a drain after each,
/// so that every layout whose type comes alive is compared with live bytes in the tick of capture.
///
/// A tour is a list of steps <c>{ label, server, ui, client, wait, fixed, until, expect, then,
/// reset }</c>: the Lua of each side runs in that order (<c>{host}</c> is the host's uuid), then
/// the tour waits until the game has finished what the act started (nothing on screen waits for
/// the player and the host is not casting; <c>fixed</c> waits exactly <c>wait</c>; <c>until</c>
/// is a server expression that ends the wait), <c>then</c> are further acts of the same step and
/// <c>reset</c> returns the world to the checkpoint afterwards. Whatever blocks the game is
/// removed after each step (GK.Stage.Unblock).
/// </summary>
public sealed class SweepTour(ISweepGame game, TextWriter output)
{
    const string Busy = "(function() local id = Osi.GetHostCharacter(); local e = Ext.Entity.Get(id); "
        + "return e ~= nil and e.SpellCastIsCasting ~= nil end)()";

    /// <summary>The tour file of a name (research/coverage/tours/&lt;name&gt;.json), or the path itself.</summary>
    public static string Resolve(string coverageRoot, string nameOrPath)
    {
        if (File.Exists(nameOrPath)) return nameOrPath;
        var path = Path.Combine(coverageRoot, "tours", nameOrPath + ".json");
        if (File.Exists(path)) return path;
        var known = Directory.Exists(Path.Combine(coverageRoot, "tours"))
            ? string.Join(", ", Directory.EnumerateFiles(Path.Combine(coverageRoot, "tours"), "*.json").Select(Path.GetFileNameWithoutExtension).Order())
            : "none";
        throw new SweepException($"no tour `{nameOrPath}`; the tours are: {known}");
    }

    /// <summary>Several tours played as one, in the order given (`life,world`): the steps of each
    /// labelled with its own name.</summary>
    public static JsonArray Steps(IReadOnlyList<string> paths)
    {
        var all = new JsonArray();
        foreach (var path in paths)
        {
            var steps = JsonNode.Parse(File.ReadAllText(path)) as JsonArray ?? throw new SweepException($"{path} is not a list of tour steps");
            var name = Path.GetFileNameWithoutExtension(path);
            var n = 0;
            foreach (var node in steps)
            {
                if (node is not JsonObject step) continue;
                var copy = (JsonObject)step.DeepClone();
                copy["label"] = paths.Count > 1 ? $"{name}-{step["label"]?.GetValue<string>() ?? n.ToString(CultureInfo.InvariantCulture)}" : copy["label"]?.DeepClone();
                all.Add(copy);
                n++;
            }
        }
        return all;
    }

    public Task<JsonObject> RunAsync(string path, string run, double speed, int? limit, CancellationToken cancel) =>
        RunAsync([path], run, speed, limit, verify: false, cancel);

    public async Task<JsonObject> RunAsync(IReadOnlyList<string> paths, string run, double speed, int? limit, bool verify, CancellationToken cancel)
    {
        // joined with a dash: a plus sign reaches the capture's Lua label as \u002B and breaks it
        var name = string.Join("-", paths.Select(Path.GetFileNameWithoutExtension));
        var steps = Steps(paths);
        var disagreements = new SortedSet<string>(StringComparer.Ordinal);
        var clock = Stopwatch.StartNew();
        await game.EnsureUpAsync(cancel);
        var before = SweepRunner.VerifiedNames(game.CoverageRoot);
        var hasReset = steps.Any(s => s?["reset"]?.GetValue<bool>() == true);
        if (hasReset)
        {
            try { await game.CheckpointAsync(cancel); }
            catch (CopyException e) { output.WriteLine("no checkpoint; steps that reset will not: " + e.Message.Split('\n')[0]); hasReset = false; }
        }
        await ArmAsync(name, run, speed, cancel);
        var host = (await game.LuaAsync("server", "return Osi.GetHostCharacter()", TimeSpan.FromSeconds(20), cancel))?.GetValue<string>() ?? "";
        var done = 0;
        var failed = new JsonArray();
        var fresh = new JsonArray();
        foreach (var node in steps.Take(limit ?? int.MaxValue))
        {
            if (node is not JsonObject step) continue;
            var label = $"tour:{name}:{step["label"]?.GetValue<string>() ?? done.ToString(CultureInfo.InvariantCulture)}";
            var watch = Stopwatch.StartNew();
            string? problem = null;
            try
            {
                foreach (var side in new[] { "server", "client" })
                    await game.LuaAsync(side, $"return GK.Seen.Mark({PlanBuilder.Lua(label)})", TimeSpan.FromSeconds(15), cancel);
                await ActAsync(step, host, speed, cancel);
                foreach (var more in step["then"] as JsonArray ?? [])
                    if (more is JsonObject then) await ActAsync(then, host, speed, cancel);
            }
            catch (CopyException e)
            {
                // "Lua error on server:" and the error itself are two lines; the second is the one that says why
                var text = e.Message.ReplaceLineEndings(" ").Trim();
                problem = text.Length > 300 ? text[..300] + "..." : text;
            }
            try { await game.LuaAsync("server", "return GK.Stage.Unblock()", TimeSpan.FromSeconds(20), cancel); }
            catch (CopyException) { }
            JsonNode? lit = null;
            try
            {
                lit = await game.ToolAsync("bg3_seen_new", [], cancel);
                var drained = await game.ToolAsync("bg3_seen_drain", [], cancel);
                foreach (var d in drained?["disagreements"] as JsonArray ?? [])
                    if (d?.ToString() is { Length: > 0 } text) disagreements.Add(text);
            }
            catch (CopyException e) { problem ??= e.Message.Split('\n')[0]; }
            foreach (var row in lit?["new_to_the_record"] as JsonArray ?? []) fresh.Add($"{row?["Name"]} ({row?["side"]}) by {label}");
            done++;
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{label}: {(problem is null ? "ok" : "FAILED " + problem)} {watch.Elapsed.TotalSeconds:0.0} s"));
            if (problem is not null) failed.Add($"{label}: {problem}");
            if (!game.Alive())
            {
                output.WriteLine("the game closed; restarting the copy");
                await game.RestartAsync(cancel);
                await ArmAsync(name, run, speed, cancel);
                host = (await game.LuaAsync("server", "return Osi.GetHostCharacter()", TimeSpan.FromSeconds(20), cancel))?.GetValue<string>() ?? "";
            }
            else if (hasReset && step["reset"]?.GetValue<bool>() == true)
            {
                try
                {
                    await game.ResetWorldAsync(cancel);
                    await ArmAsync(name, run, speed, cancel);
                }
                catch (CopyException e) { output.WriteLine("reset failed: " + e.Message.Split('\n')[0]); }
            }
        }
        try { await game.LuaAsync("server", "return GK.Speed.Set(1)", TimeSpan.FromSeconds(20), cancel); }
        catch (CopyException) { }
        var checkedLayouts = new JsonObject();
        if (verify)
        {
            // Everything alive at the end of the tour, every layout, both sides (bg3_verify with
            // record): the long-lived types a level holds are verified here, not in a drain.
            foreach (var side in new[] { "server", "client" })
            {
                try
                {
                    var answer = await game.ToolAsync("bg3_verify", new JsonObject { ["side"] = side, ["record"] = true, ["detail"] = 200 }, cancel);
                    checkedLayouts[side] = new JsonObject
                    {
                        ["agreed"] = answer?["agreed"]?.DeepClone(),
                        ["disagreed"] = answer?["disagreed"]?.DeepClone(),
                        ["recorded_as_verified"] = answer?["recorded_as_verified"]?.DeepClone(),
                    };
                    foreach (var d in answer?["disagreements"] as JsonArray ?? [])
                        if (d?.ToString() is { Length: > 0 } text) disagreements.Add($"{side}: {text}");
                }
                catch (CopyException e) { checkedLayouts[side] = "failed: " + e.Message.Split('\n')[0]; }
            }
        }
        var after = SweepRunner.VerifiedNames(game.CoverageRoot);
        var verified = after.Except(before).Order(StringComparer.Ordinal).ToList();
        return new JsonObject
        {
            ["tour"] = name,
            ["steps"] = done,
            ["failed"] = failed,
            ["minutes"] = Math.Round(clock.Elapsed.TotalMinutes, 1),
            ["verified_live_before"] = before.Count,
            ["verified_live_after"] = after.Count,
            ["newly_verified"] = new JsonArray([.. verified.Select(v => (JsonNode)v!)]),
            ["never_seen_types_lit"] = fresh,
            ["verify"] = verify ? checkedLayouts : null,
            ["disagreements"] = new JsonArray([.. disagreements.Select(d => (JsonNode)d)]),
        };
    }

    async Task ArmAsync(string name, string run, double speed, CancellationToken cancel)
    {
        await game.ToolAsync("bg3_seen_start", new JsonObject
        {
            ["label"] = $"tour:{name}:start",
            ["role"] = "tour",
            ["run"] = run,
            ["stretch"] = name,
        }, cancel);
        if (Math.Abs(speed - 1.0) > 1e-9)
            await game.LuaAsync("server", $"return GK.Speed.Set({PlanBuilder.Invariant(speed)})", TimeSpan.FromSeconds(20), cancel);
    }

    async Task ActAsync(JsonObject step, string host, double speed, CancellationToken cancel)
    {
        var timeout = TimeSpan.FromSeconds(step["timeout"]?.GetValue<double>() ?? 30);
        foreach (var side in new[] { "server", "ui", "client" })
            if (step[side]?.GetValue<string>() is { Length: > 0 } code)
                await game.LuaAsync(side, code.Replace("{host}", host, StringComparison.Ordinal), timeout, cancel);
        var wait = (step["wait"]?.GetValue<double>() ?? 2) / Math.Max(1, speed);
        if (step["until"]?.GetValue<string>() is { Length: > 0 } until)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed.TotalSeconds < Math.Max(wait * 4, 20))
            {
                try
                {
                    if ((await game.LuaAsync("server", "return " + until, TimeSpan.FromSeconds(10), cancel))?.GetValue<bool>() == true) break;
                }
                catch (Exception e) when (e is CopyException or InvalidOperationException or FormatException) { }
                await Task.Delay(300, cancel);
            }
            await SettleAsync(8, 0.3, cancel);
        }
        else if (step["fixed"]?.GetValue<bool>() == true) await Task.Delay(TimeSpan.FromSeconds(wait), cancel);
        else await SettleAsync(Math.Max(wait * 4, 20), Math.Min(wait, 1.0), cancel);
    }

    /// <summary>Until nothing on screen waits for the player and the host is not casting, seen twice
    /// in a row; at least <paramref name="shortest"/> seconds, at most <paramref name="longest"/>.</summary>
    async Task SettleAsync(double longest, double shortest, CancellationToken cancel)
    {
        var watch = Stopwatch.StartNew();
        await Task.Delay(TimeSpan.FromSeconds(shortest), cancel);
        var calm = 0;
        while (watch.Elapsed.TotalSeconds < longest && calm < 2)
        {
            bool busy;
            try
            {
                var info = await game.LuaAsync("ui", "return GK.AutoplayInfo()", TimeSpan.FromSeconds(10), cancel);
                busy = info.Field("busy")?.GetValue<bool>() == true
                    || (await game.LuaAsync("server", "return " + Busy, TimeSpan.FromSeconds(10), cancel))?.GetValue<bool>() == true;
            }
            catch (Exception e) when (e is CopyException or InvalidOperationException or FormatException) { busy = true; }
            calm = busy ? 0 : calm + 1;
            if (calm < 2) await Task.Delay(400, cancel);
        }
    }
}
