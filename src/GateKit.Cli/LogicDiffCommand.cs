using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GateKit.Cli;

/// <summary>`logic-diff`: a differential check of the game's logic. Two records of the same scenario -
/// the bridge's event log (events_&lt;side&gt;_&lt;boot&gt;_&lt;n&gt;.jsonl, copied out of the channel after
/// each run) of a game run as the game does it and of one run with cuts (no frames drawn, fast
/// loads...) - are read, each row made comparable (its clock, sequence and scope dropped, the ids a
/// run makes anew - spawned creatures, casts, fights - numbered by first appearance), and compared:
/// rows per kind, then the two orders of rows, the first places they part named. The same record
/// twice is the proof; a difference is the place to look.</summary>
public static partial class LogicDiffCommand
{
    /// <summary>Payload fields that are of the run, not of the logic: ticks, sequence numbers,
    /// return addresses.</summary>
    public static readonly string[] DefaultIgnore = ["opened", "track", "asked", "rolled", "ticks", "caller", "streamId", "seen", "blackTicks"];

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidPattern();

    /// <summary>The rows of one record: every events_&lt;side&gt;_&lt;boot&gt;_*.jsonl of the folder for the
    /// boot asked (default: the latest boot, the run's), in seq order.</summary>
    public static List<JsonObject> Read(string path, string side, int? boot)
    {
        var files = File.Exists(path) ? [path] : Directory.GetFiles(path, $"events_{side}_*_*.jsonl").ToList();
        var byBoot = new Dictionary<int, List<JsonObject>>();
        foreach (var file in files)
        {
            var parts = Path.GetFileNameWithoutExtension(file).Split('_');
            var b = parts.Length >= 4 && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
            var list = byBoot.TryGetValue(b, out var l) ? l : byBoot[b] = [];
            foreach (var line in File.ReadLines(file))
            {
                if (line.Length == 0) continue;
                try { if (JsonNode.Parse(line) is JsonObject o) list.Add(o); }
                catch (System.Text.Json.JsonException) { }
            }
        }
        if (byBoot.Count == 0) return [];
        var chosen = boot is { } wanted && byBoot.TryGetValue(wanted, out var w) ? w : byBoot.OrderByDescending(p => p.Key).First().Value;
        return [.. chosen.OrderBy(o => o["seq"]?.GetValue<double>() ?? 0)];
    }

    /// <summary>The scenario's own stretch of a record: from the last row of kind `from` through the
    /// first row of kind `to` after it (to the end when there is none). A record holds the load
    /// before the scenario, the tests run before it in the same Lua state, and the clean-up after
    /// it; their rows are of the run's moment, not of the scenario. Null `from`: the whole record.</summary>
    public static List<JsonObject> Window(List<JsonObject> rows, string? from, string? to)
    {
        if (string.IsNullOrEmpty(from)) return rows;
        var start = rows.FindLastIndex(r => r["kind"]?.ToString() == from);
        if (start < 0) return [];
        var end = string.IsNullOrEmpty(to) ? -1 : rows.FindIndex(start + 1, r => r["kind"]?.ToString() == to);
        return end < 0 ? rows[start..] : rows[start..(end + 1)];
    }

    /// <summary>One row as comparable text: kind and payload, the ignored fields out, every GUID
    /// replaced by its number of first appearance in this record, numbers rounded to `digits`.</summary>
    public static List<(string Kind, string Text)> Normalize(IEnumerable<JsonObject> rows, IReadOnlyCollection<string> kinds,
        IReadOnlyCollection<string> families, IReadOnlyDictionary<string, string> familyOf, IReadOnlyCollection<string> ignore, int digits,
        bool sortBursts = false, IReadOnlyCollection<string>? anchors = null, bool maskIds = false)
    {
        var all = rows.ToList();
        // the ids the anchor rows name, in their order, whatever families are compared
        var anchorIds = new List<string>();
        if (anchors is { Count: > 0 })
            foreach (var row in all.Where(r => anchors.Contains(r["kind"]?.ToString() ?? "")))
                foreach (Match m in GuidPattern().Matches(row["payload"]?.ToJsonString() ?? ""))
                    if (!anchorIds.Contains(m.Value, StringComparer.OrdinalIgnoreCase)) anchorIds.Add(m.Value);
        var raw = new List<(string Kind, string Text, double Tick)>();
        foreach (var row in all)
        {
            var kind = row["kind"]?.ToString() ?? "";
            if (kinds.Count > 0 && !kinds.Contains(kind)) continue;
            if (families.Count > 0 && !(familyOf.TryGetValue(kind, out var f) && families.Contains(f))) continue;
            var text = new StringBuilder(kind).Append(' ');
            Write(row["payload"], text, ignore, digits);
            var line = maskIds ? GuidPattern().Replace(text.ToString(), "#") : text.ToString();
            raw.Add((kind, line, row["tick"] is JsonValue v && v.TryGetValue<double>(out var t) ? t : -1));
        }
        // rows of one kind written in one tick are one burst: the order inside it is a container's
        // iteration (the statuses a level load applies), not the logic's
        if (sortBursts)
            for (var i = 0; i < raw.Count;)
            {
                var j = i + 1;
                while (j < raw.Count && raw[j].Kind == raw[i].Kind && raw[j].Tick == raw[i].Tick) j++;
                // sorted by the text with its ids masked: an id made anew in each run must not decide the order
                if (j - i > 1) raw.Sort(i, j - i, Comparer<(string Kind, string Text, double Tick)>.Create((x, y) =>
                    string.CompareOrdinal(GuidPattern().Replace(x.Text, "#"), GuidPattern().Replace(y.Text, "#"))));
                i = j;
            }
        var ids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // ids named first in rows of the anchor kinds (a scenario's own rows that name what it made, in
        // the scenario's order) take the first numbers: an id's number then does not hang on which of
        // two equal rows a burst wrote first
        foreach (var id in anchorIds) ids[id] = ids.Count + 1;
        var out_ = new List<(string, string)>();
        foreach (var (kind, text, _) in raw)
            out_.Add((kind, GuidPattern().Replace(text, m =>
            {
                if (!ids.TryGetValue(m.Value, out var n)) ids[m.Value] = n = ids.Count + 1;
                return "#" + n.ToString(CultureInfo.InvariantCulture);
            })));
        return out_;
    }

    static void Write(JsonNode? node, StringBuilder sb, IReadOnlyCollection<string> ignore, int digits)
    {
        switch (node)
        {
            case null: sb.Append("null"); break;
            case JsonObject o:
                sb.Append('{');
                // keys that are ids made anew in each run must not decide the order: masked, then by value
                foreach (var (k, v) in o.OrderBy(p => GuidPattern().Replace(p.Key, "#"), StringComparer.Ordinal).ThenBy(p => GuidPattern().Replace(p.Value?.ToJsonString() ?? "", "#"), StringComparer.Ordinal))
                {
                    if (ignore.Contains(k)) continue;
                    sb.Append(k).Append(':');
                    Write(v, sb, ignore, digits);
                    sb.Append(',');
                }
                sb.Append('}');
                break;
            case JsonArray a:
                sb.Append('[');
                foreach (var v in a) { Write(v, sb, ignore, digits); sb.Append(','); }
                sb.Append(']');
                break;
            case JsonValue v when v.TryGetValue<double>(out var d):
                sb.Append(Math.Round(d, digits).ToString(CultureInfo.InvariantCulture));
                break;
            default: sb.Append(node.ToJsonString()); break;
        }
    }

    /// <summary>The report: rows per kind in each, the rows in the same order, the first places
    /// the orders part (a longest common subsequence of the two).</summary>
    public static IEnumerable<string> Compare(List<(string Kind, string Text)> a, List<(string Kind, string Text)> b, int show)
    {
        yield return $"A: {a.Count} rows, B: {b.Count} rows";
        var kinds = a.Select(r => r.Kind).Concat(b.Select(r => r.Kind)).Distinct().Order(StringComparer.Ordinal);
        var differ = 0;
        foreach (var k in kinds)
        {
            int na = a.Count(r => r.Kind == k), nb = b.Count(r => r.Kind == k);
            if (na != nb) differ++;
            yield return $"  {(na == nb ? " " : "*")} {k,-28} {na,6} {nb,6}";
        }
        // the longest common subsequence, then the rows outside it, in order
        int n = a.Count, m = b.Count;
        var len = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                len[i, j] = a[i].Text == b[j].Text ? len[i + 1, j + 1] + 1 : Math.Max(len[i + 1, j], len[i, j + 1]);
        var common = len[0, 0];
        yield return $"same rows in the same order: {common} of {Math.Max(n, m)}; only in A {n - common}, only in B {m - common}";
        if (common == n && common == m) { yield return "THE SAME: both records hold the same rows in the same order"; yield break; }
        var shown = 0;
        for (int i = 0, j = 0; (i < n || j < m) && shown < show;)
        {
            if (i < n && j < m && a[i].Text == b[j].Text) { i++; j++; continue; }
            if (j >= m || (i < n && len[i + 1, j] >= len[i, j + 1])) { yield return $"  A#{i + 1,-5} {Cut(a[i].Text)}"; i++; }
            else { yield return $"  B#{j + 1,-5} {Cut(b[j].Text)}"; j++; }
            shown++;
        }
    }

    static string Cut(string s) => s.Length <= 300 ? s : s[..300] + "...";

    public static Command Create()
    {
        var first = new Argument<string>("a") { Description = "a folder holding a run's events_<side>_<boot>_<n>.jsonl (copied from the channel), or one such file" };
        var second = new Argument<string>("b") { Description = "the same of the other run" };
        var side = new Option<string>("--side") { Description = "server or client", DefaultValueFactory = _ => "server" };
        var boot = new Option<int?>("--boot") { Description = "the boot of the Lua state in both (default: each folder's latest boot)" };
        var family = new Option<string[]>("--family") { Description = "only the kinds of these families (the registry's, events_<side>_kinds.json beside the rows); default: every kind", AllowMultipleArgumentsPerToken = true };
        var kind = new Option<string[]>("--kind") { Description = "only these kinds", AllowMultipleArgumentsPerToken = true };
        var ignore = new Option<string[]>("--ignore") { Description = "payload fields left out besides the run's own (ticks, sequence numbers, return addresses)", AllowMultipleArgumentsPerToken = true };
        var digits = new Option<int>("--digits") { Description = "numbers rounded to this many decimals", DefaultValueFactory = _ => 2 };
        var show = new Option<int>("--show") { Description = "how many differing rows to print", DefaultValueFactory = _ => 25 };
        var keepOrder = new Option<bool>("--keep-order") { Description = "compare rows of one kind written in one tick in the order written (by default such a burst is sorted: its order is a container's iteration, not the logic's)" };
        var rigged = new Option<bool>("--rigged") { Description = "a scenario whose throws are rigged: the dice drawn before the rig decided (dice, discarded, rigged, was) are left out, the kept natural stays" };
        var anchor = new Option<string[]>("--anchor") { Description = "kinds whose rows name what the scenario made, in its own order (roll_pin, a spawn row): the ids they name are numbered first, in that order, so two equal rows of a burst cannot swap two ids", AllowMultipleArgumentsPerToken = true };
        var unordered = new Option<bool>("--unordered") { Description = "compare the two records as bags of rows, every row's place left out: for a stretch whose work the game spreads over its ticks as time allows (a level's setup: the statuses and items of each entity as it loads), where the same rows come in another order in every run, native or not" };
        var maskIds = new Option<bool>("--mask-ids") { Description = "every id made anew in a run written as # instead of numbered by first appearance: for a large record (a level's setup) whose bursts of equal rows name different new ids, so a burst's order cannot renumber the rest; the names around the ids (templates) still tell rows apart" };
        var window = new Option<string?>("--window") { Description = "FROM:TO - only the scenario's stretch of each record: from its first row of kind FROM through the first row of kind TO after it (family_on:family_off is a scenario that switched its families on and off)" };
        var command = new Command("logic-diff", "a differential check of the game's logic: two event-log records of the same scenario (the game as it is, and with cuts) compared row by row, run-made ids numbered by first appearance") { first, second, side, boot, family, kind, ignore, digits, show, keepOrder, rigged, anchor, window, maskIds, unordered };
        command.SetAction(parse =>
        {
            var s = parse.GetValue(side)!;
            var familyOf = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var folder in new[] { parse.GetValue(first)!, parse.GetValue(second)! })
            {
                var kindsFile = Directory.Exists(folder) ? Path.Combine(folder, $"events_{s}_kinds.json") : "";
                if (!File.Exists(kindsFile)) continue;
                if (JsonNode.Parse(File.ReadAllText(kindsFile))?["kinds"] is JsonObject ks)
                    foreach (var (k, v) in ks) if (v?["family"]?.ToString() is { } f) familyOf[k] = f;
            }
            var ign = DefaultIgnore.Concat(parse.GetValue(ignore) ?? []).Concat(parse.GetValue(rigged) ? ["dice", "discarded", "rigged", "was"] : []).ToHashSet(StringComparer.Ordinal);
            var kinds = (parse.GetValue(kind) ?? []).ToHashSet(StringComparer.Ordinal);
            var families = (parse.GetValue(family) ?? []).ToHashSet(StringComparer.Ordinal);
            var span = parse.GetValue(window) is { } w ? w.Split(':', 2) : null;
            string? from = span?[0], to = span is { Length: 2 } ? span[1] : null;
            var a = Normalize(Window(Read(parse.GetValue(first)!, s, parse.GetValue(boot)), from, to), kinds, families, familyOf, ign, parse.GetValue(digits), !parse.GetValue(keepOrder), (parse.GetValue(anchor) ?? []).ToHashSet(StringComparer.Ordinal), parse.GetValue(maskIds));
            var b = Normalize(Window(Read(parse.GetValue(second)!, s, parse.GetValue(boot)), from, to), kinds, families, familyOf, ign, parse.GetValue(digits), !parse.GetValue(keepOrder), (parse.GetValue(anchor) ?? []).ToHashSet(StringComparer.Ordinal), parse.GetValue(maskIds));
            if (parse.GetValue(unordered))
            {
                a = [.. a.OrderBy(r => r.Text, StringComparer.Ordinal)];
                b = [.. b.OrderBy(r => r.Text, StringComparer.Ordinal)];
            }
            var lines = Compare(a, b, parse.GetValue(show)).ToList();
            foreach (var line in lines) Console.Out.WriteLine(line);
            return lines.Any(l => l.StartsWith("THE SAME", StringComparison.Ordinal)) ? 0 : 1;
        });
        return command;
    }
}
