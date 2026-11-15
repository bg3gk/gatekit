using System.Diagnostics;
using System.Text.Json.Nodes;

namespace GateKit.Mcp;

/// <summary>
/// `gatekit mcp sequence FILE`: a list of tool calls run by one server - one attention cursor, one
/// connection to the game - with calls that may run side by side: a `bg3_wait` started in the
/// background, a trigger a moment later, and the wait awaited, with the time from the trigger to
/// the wait's end. What a live proof of an event-ended wait needs, without a client of its own.
///
/// <code>
/// { "steps": [
///   { "id": "w", "tool": "bg3_wait", "arguments": { "timeout": 30 }, "background": true },
///   { "sleep": 0.5 },
///   { "id": "t", "tool": "bg3_lua", "arguments": { "side": "server", "code": "return GK.Act.EndTurn(nil)" } },
///   { "await": "w", "since": "t", "until": ["reaction"] } ] }
/// </code>
///
/// A step is a call (`tool`, `arguments`; `background` starts it and goes on), a `sleep` (seconds),
/// or an `await` of a background call: `since` names the step its delay is counted from; `until`
/// lists attention items to wait for - a wait that ended on something else is started again (with
/// the same arguments) until each was named or the wait timed out. `expect` on an await or a call:
/// text that must appear in its answer, else the sequence fails. Each step prints one JSON line.
/// </summary>
public static class McpSequence
{
    public sealed record Answer(IReadOnlyList<string> Texts, bool IsError);

    public static async Task<int> RunAsync(JsonObject plan, Func<string, JsonObject, CancellationToken, Task<Answer>> call,
        TextWriter output, CancellationToken cancel = default)
    {
        var steps = plan["steps"] as JsonArray ?? throw new ArgumentException("a sequence is { \"steps\": [ ... ] }");
        var clock = Stopwatch.StartNew();
        var started = new Dictionary<string, double>();
        var running = new Dictionary<string, (Task<Answer> Task, string Tool, JsonObject Arguments, double At)>();
        var failed = false;
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i] is not JsonObject step) throw new ArgumentException($"step {i + 1} is not an object");
            var id = step["id"]?.GetValue<string>() ?? $"#{i + 1}";
            var line = new JsonObject { ["step"] = i + 1, ["id"] = id };
            if (step["sleep"] is { } sleep)
            {
                await Task.Delay(TimeSpan.FromSeconds(sleep.GetValue<double>()), cancel);
                continue;
            }
            if (step["await"]?.GetValue<string>() is { } awaited)
            {
                if (!running.Remove(awaited, out var job)) throw new ArgumentException($"step {i + 1}: nothing named {awaited} runs in the background");
                line["id"] = awaited;
                if (step["since"] is { } named) line["since"] = named.DeepClone();
                var wanted = (step["until"] as JsonArray)?.Select(n => n!.GetValue<string>()).ToList() ?? [];
                var seen = new List<string>();
                var rounds = new JsonArray();
                var from = step["since"]?.GetValue<string>() is { } since && started.TryGetValue(since, out var t0) ? t0 : job.At;
                Answer answer;
                while (true)
                {
                    answer = await job.Task;
                    var at = clock.Elapsed.TotalMilliseconds;
                    var round = new JsonObject { ["ms"] = Math.Round(at - from) };
                    var items = Items(answer);
                    round["attention"] = items;
                    rounds.Add(round);
                    foreach (var item in items) if (item?["what"]?.GetValue<string>() is { } what) seen.Add(what);
                    var ended = Ended(answer);
                    if (wanted.All(seen.Contains) || ended != "attention") break;
                    job = (call(job.Tool, (JsonObject)job.Arguments.DeepClone(), cancel), job.Tool, job.Arguments, at);
                }
                line["rounds"] = rounds;
                var missing = wanted.Where(w => !seen.Contains(w)).ToList();
                if (missing.Count > 0)
                {
                    line["missing"] = new JsonArray(missing.Select(m => (JsonNode)m).ToArray());
                    failed = true;
                }
                failed |= Check(step, answer, line);
                output.WriteLine(line.ToJsonString());
                continue;
            }
            var tool = step["tool"]?.GetValue<string>() ?? throw new ArgumentException($"step {i + 1} has no tool, sleep or await");
            var arguments = step["arguments"] as JsonObject ?? [];
            started[id] = clock.Elapsed.TotalMilliseconds;
            var task = call(tool, (JsonObject)arguments.DeepClone(), cancel);
            if (step["background"]?.GetValue<bool>() == true)
            {
                running[id] = (task, tool, arguments, started[id]);
                continue;
            }
            var result = await task;
            line["tool"] = tool;
            line["ms"] = Math.Round(clock.Elapsed.TotalMilliseconds - started[id]);
            line["answer"] = result.Texts.Count > 0 ? Cut(result.Texts[0]) : null;
            var notes = Notes(result);
            if (notes.Count > 0) line["attention"] = notes;
            if (result.IsError) { line["error"] = true; failed = true; }
            failed |= Check(step, result, line);
            output.WriteLine(line.ToJsonString());
        }
        foreach (var (name, job) in running)
        {
            await job.Task;
            output.WriteLine(new JsonObject { ["id"] = name, ["note"] = "a background call nobody awaited ended" }.ToJsonString());
        }
        return failed ? 1 : 0;
    }

    static bool Check(JsonObject step, Answer answer, JsonObject line)
    {
        if (step["expect"]?.GetValue<string>() is not { } expect) return false;
        if (answer.Texts.Any(t => t.Contains(expect, StringComparison.Ordinal))) return false;
        line["expected"] = expect;
        return true;
    }

    static string Cut(string text) => text.Length > 400 ? text[..400] + "..." : text;

    static JsonObject? Body(Answer answer)
    {
        if (answer.Texts.Count == 0) return null;
        try { return JsonNode.Parse(answer.Texts[0]) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    static string? Ended(Answer answer) => Body(answer)?["endedBy"]?.GetValue<string>();

    // The items a bg3_wait ended on, kept short: what, side, since_ms, since_ticks.
    static JsonArray Items(Answer answer)
    {
        var items = new JsonArray();
        foreach (var item in Body(answer)?["attention"] as JsonArray ?? [])
        {
            var o = new JsonObject();
            foreach (var key in new[] { "what", "side", "since_ms", "since_ticks" })
                if (item?[key] is { } v) o[key] = v.DeepClone();
            items.Add(o);
        }
        return items;
    }

    // The attention block another tool's answer carries (its second text block), kept short.
    static JsonArray Notes(Answer answer)
    {
        var notes = new JsonArray();
        foreach (var text in answer.Texts.Skip(1))
        {
            if (!text.StartsWith("attention", StringComparison.Ordinal)) continue;
            var at = text.IndexOf('[', StringComparison.Ordinal);
            var json = at >= 0 ? text[at..] : "[]";
            try
            {
                foreach (var item in JsonNode.Parse(json) as JsonArray ?? [])
                {
                    var o = new JsonObject();
                    foreach (var key in new[] { "what", "waiting", "side" })
                        if (item?[key] is { } v) o[key] = v.DeepClone();
                    notes.Add(o);
                }
            }
            catch (System.Text.Json.JsonException) { notes.Add(text.Length > 200 ? text[..200] : text); }
        }
        return notes;
    }

    /// <summary>Reads a plan from a file, with ${NAME} replaced by `--set NAME=VALUE` values.</summary>
    public static JsonObject Load(string path, IEnumerable<string> sets)
    {
        var text = File.ReadAllText(path);
        foreach (var set in sets)
        {
            var eq = set.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0) throw new ArgumentException($"--set wants NAME=VALUE, got {set}");
            text = text.Replace("${" + set[..eq] + "}", set[(eq + 1)..], StringComparison.Ordinal);
        }
        return JsonNode.Parse(text) as JsonObject ?? throw new ArgumentException(path + " is not one JSON object");
    }
}
