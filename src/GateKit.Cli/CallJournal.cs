using System.CommandLine;
using System.Globalization;
using System.Text.Json.Nodes;
using GateKit.Json;

namespace GateKit.Cli;

/// <summary>
/// One line a call of the program in <c>&lt;state&gt;/calls/journal.jsonl</c> (schema state/calls):
/// when, the command's words (never the values of its arguments or options), how long the process
/// took from its start, how much of that went before the command began, the exit code, and whether
/// it ran from the launcher's publish or from a build. It answers how often the tool is called and
/// what its start costs; `gatekit calls` sums it up. GATEKIT_CALLS=0 turns it off; a journal that
/// cannot be written is left alone and the call goes on.
/// </summary>
static class CallJournal
{
    public const string Variable = "GATEKIT_CALLS";
    public const string Kind = "state/calls/1";

    static string Path() => System.IO.Path.Combine(GateKit.Home.GateKitHome.Current.State.Require(), "calls", "journal.jsonl");

    /// <summary>Milliseconds since this process started.</summary>
    public static double SinceStart()
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        return (DateTime.Now - self.StartTime).TotalMilliseconds;
    }

    public static void Write(ParseResult parsed, double ready, int exit)
    {
        if (Environment.GetEnvironmentVariable(Variable) == "0") return;
        try
        {
            var words = new List<string>();
            for (var at = parsed.CommandResult; at is not null; at = at.Parent as System.CommandLine.Parsing.CommandResult)
                if (at.Command is not RootCommand) words.Insert(0, at.Command.Name);
            var parts = AppContext.BaseDirectory.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            var line = JsonText.Inline(new JsonObject
            {
                ["at"] = DateTime.UtcNow.ToString(SchemaSet.TimeFormat, CultureInfo.InvariantCulture),
                ["command"] = string.Join(' ', words),
                ["ms"] = Math.Round(SinceStart()),
                ["start_ms"] = Math.Round(ready),
                ["exit"] = exit,
                ["from"] = parts.Contains("artifacts") ? "publish" : parts.Contains("bin") ? "build" : "other",
            }) + "\n";
            Append(Path(), System.Text.Encoding.UTF8.GetBytes(line));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or GateKit.Home.HomeException) { }
    }

    /// <summary>One line appended with the file held by this writer alone, so that two calls ending
    /// at once each add a whole line. The row's shape is fixed here and checked by the schema when
    /// the journal is read; a ledger's lock and a check of every row on write would cost each call
    /// more than the rest of its journaling.</summary>
    static void Append(string path, byte[] line)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
                file.Seek(0, SeekOrigin.End);
                file.Write(line);
                return;
            }
            catch (IOException) when (attempt < 20) { Thread.Sleep(10); }
        }
    }

    public static Command CreateCommand()
    {
        var since = new Option<string?>("--since") { Description = "only the calls that ended at or after this time (2026-11-16T14:00, UTC)" };
        var top = new Option<int>("--top") { Description = "how many commands to list (default 15)", DefaultValueFactory = _ => 15 };
        var command = new Command("calls", "how often the program was called and what its start cost, from the journal every call writes (GATEKIT_CALLS=0 writes none)") { since, top };
        command.SetAction(p =>
        {
            var path = Path();
            var rows = JsonText.ReadLinesIfExists(path, Kind, _ => { }) ?? [];
            if (p.GetValue(since) is { } from)
                rows = [.. rows.Where(r => string.CompareOrdinal(JsonText.Text(r["at"]), from) >= 0)];
            Console.Out.Write(Summary(rows, p.GetValue(top), path));
            return 0;
        });
        return command;
    }

    /// <summary>The calls counted, their times by percentile, the time before the commands began, and the commands by count.</summary>
    internal static string Summary(IReadOnlyList<JsonObject> rows, int top, string path)
    {
        var b = new System.Text.StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        b.Append(inv, $"{rows.Count} calls in {path}\n");
        if (rows.Count == 0) return b.ToString();
        double Number(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;
        var ms = rows.Select(r => Number(r["ms"])).Order().ToList();
        var start = rows.Select(r => Number(r["start_ms"])).ToList();
        double At(double q) => ms[Math.Min(ms.Count - 1, (int)Math.Floor(q * ms.Count))];
        b.Append(inv, $"from {JsonText.Text(rows[0]["at"])} to {JsonText.Text(rows[^1]["at"])}\n");
        b.Append(inv, $"duration ms: median {At(0.5):0}, p90 {At(0.9):0}, p99 {At(0.99):0}, max {ms[^1]:0}; in all {ms.Sum() / 1000:0.0} s\n");
        b.Append(inv, $"before the command began: median {start.Order().ElementAt(start.Count / 2):0} ms; in all {start.Sum() / 1000:0.0} s ({start.Sum() / Math.Max(1, ms.Sum()):P1} of the time)\n");
        foreach (var g in rows.GroupBy(r => JsonText.Text(r["from"]) ?? "?").OrderBy(g => g.Key, StringComparer.Ordinal))
            b.Append(inv, $"from {g.Key}: {g.Count()} calls, median start {g.Select(r => Number(r["start_ms"])).Order().ElementAt(g.Count() / 2):0} ms\n");
        b.Append(inv, $"failed (exit not 0): {rows.Count(r => Number(r["exit"]) != 0)}\n");
        foreach (var g in rows.GroupBy(r => JsonText.Text(r["command"]) is { Length: > 0 } c ? c : "(none)").OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Take(top))
        {
            var each = g.Select(r => Number(r["ms"])).Order().ToList();
            b.Append(inv, $"  {g.Count(),6}  {g.Key}  median {each[each.Count / 2]:0} ms, in all {each.Sum() / 1000:0.0} s\n");
        }
        return b.ToString();
    }
}
