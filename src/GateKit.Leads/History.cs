using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GateKit.Leads;

/// <summary>One try at a lead: when, in which run and copy, with what, and how it went.</summary>
public sealed record Attempt(string Time, string Run, string Instance, string Instrument, string Outcome, string Note, string Source)
{
    public JsonObject ToJson() => new()
    {
        ["time"] = Time, ["run"] = Run, ["instance"] = Instance, ["instrument"] = Instrument,
        ["outcome"] = Outcome, ["note"] = Note, ["source"] = Source,
    };

    public static Attempt From(JsonObject o)
    {
        static string S(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
        return new(S(o["time"]), S(o["run"]), S(o["instance"]), S(o["instrument"]), S(o["outcome"]), S(o["note"]),
            S(o["source"]) is { Length: > 0 } src ? src : "report");
    }
}

/// <summary>
/// The tries the runs made before leads existed, read from what they wrote: research/coverage/runs.jsonl
/// (one row per stretch) and the run records run-*.md (a bullet per stretch). Read by rules, sentence
/// by sentence: a sentence that names a type - or a namespace, which stands for each never-seen type
/// in it - and says it did not appear is a try; the words of the same sentence say with what (Osi. is
/// osiris, a window or a click is gui, a cheat or GK.Stage is stage, else play) and whether it was
/// blocked (could not even be tried) or empty (tried, nothing). A sentence about what to do next is not
/// a try. The same try in a run row and in a record within 40 minutes is counted once.
/// </summary>
public static partial class History
{
    [GeneratedRegex(@"\b(?:esv|ecl|eoc|ls|gui|navcloud|mon)(?:::[A-Za-z_]\w*)+(?:::\{[\w,\s]+\})?", RegexOptions.CultureInvariant)]
    private static partial Regex Mention();

    [GeneratedRegex(@"(?<=[.;!?])\s+(?=[A-Z(*`])|\n\s*\n")]
    private static partial Regex SentenceBreak();

    [GeneratedRegex(@"\bnothing\b|\bno\b|\bnot\b|\bnever\b|blocked|set aside|without|did nothing|unfinished|0 of \d", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Negative();

    [GeneratedRegex(@"\bnext\b|\bshould\b|\bplan\b|\bwill\b|\bwants?\b|\bhint|\bstretch \d+:|\bleads?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Future();

    [GeneratedRegex(@"blocked|could not|cannot|can't|unfinished|no (?:level-\d )?slot|refused|out of reach|not reachable|no camp level", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Blocked();

    [GeneratedRegex(@"^- (?<time>\d{1,2}:\d{2}) (?<text>.*runner.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex RecordBullet();

    [GeneratedRegex(@"runner `(?<i>[\w-]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex RunnerName();

    [GeneratedRegex(@"run-(?<d>\d{4}-\d{2}-\d{2})", RegexOptions.CultureInvariant)]
    private static partial Regex RecordDate();

    /// <summary>The instrument a sentence says it used.</summary>
    public static string InstrumentOf(string sentence) =>
        sentence.Contains("Osi.", StringComparison.Ordinal) ? "osiris"
        : Regex.IsMatch(sentence, @"GK\.Gui|click|button|hotbar|window|panel|widget", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ? "gui"
        : Regex.IsMatch(sentence, @"cheat|GK\.Stage|GK\.Cheat|taught", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ? "stage"
        : Regex.IsMatch(sentence, @"two copies|multiplayer|second copy", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ? "two_copies"
        : "play";

    /// <summary>The never-seen types a mention stands for: itself (with or without the Component
    /// suffix), each of a brace list, or every never-seen type of a namespace.</summary>
    public static IEnumerable<string> Expand(string mention, IReadOnlyCollection<string> unseen)
    {
        var names = new List<string>();
        var brace = mention.IndexOf("::{", StringComparison.Ordinal);
        if (brace >= 0 && mention.EndsWith('}'))
        {
            var head = mention[..brace];
            names.AddRange(mention[(brace + 3)..^1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(p => head + "::" + p));
        }
        else names.Add(mention.TrimEnd(':'));
        foreach (var name in names)
        {
            if (unseen.Contains(name)) { yield return name; continue; }
            if (unseen.Contains(name + "Component")) { yield return name + "Component"; continue; }
            var prefix = name + "::";
            foreach (var type in unseen)
                if (type.StartsWith(prefix, StringComparison.Ordinal) && type.IndexOf("::", prefix.Length, StringComparison.Ordinal) < 0)
                    yield return type;
        }
    }

    /// <summary>The tries a piece of prose records for the never-seen types.</summary>
    public static IEnumerable<(string Type, Attempt Attempt)> Read(string text, string time, string run, string instance, string source, IReadOnlyCollection<string> unseen)
    {
        foreach (var raw in SentenceBreak().Split(text))
        {
            var sentence = raw.Trim();
            if (sentence.Length == 0 || !Negative().IsMatch(sentence) || Future().IsMatch(sentence)) continue;
            var types = Mention().Matches(sentence).SelectMany(m => Expand(m.Value, unseen)).Distinct(StringComparer.Ordinal).ToList();
            if (types.Count == 0) continue;
            var outcome = Blocked().IsMatch(sentence) ? "blocked" : "empty";
            var note = sentence.Length > 220 ? sentence[..217] + "..." : sentence;
            foreach (var type in types)
                yield return (type, new Attempt(time, run, instance, InstrumentOf(sentence), outcome, note, source));
        }
    }

    /// <summary>Every try the coverage record's prose holds, by type, deduplicated.</summary>
    public static Dictionary<string, List<Attempt>> Import(string coverageRoot, IReadOnlyCollection<string> unseen)
    {
        var found = new List<(string Type, Attempt Attempt)>();
        var runs = Path.Combine(coverageRoot, "runs.jsonl");
        if (File.Exists(runs))
            foreach (var line in File.ReadLines(runs))
            {
                JsonObject? row;
                try { row = JsonNode.Parse(line) as JsonObject; }
                catch (System.Text.Json.JsonException) { continue; }
                if (row is null) continue;
                string S(string key) => row[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : row[key]?.ToJsonString() ?? "";
                var text = string.Join("\n\n", S("found"), S("open"));
                var time = S("ended").Replace(' ', 'T');
                found.AddRange(Read(text, time, S("run"), S("half"), "runs.jsonl", unseen));
            }
        foreach (var file in Directory.Exists(coverageRoot)
                     ? Directory.EnumerateFiles(coverageRoot, "run-*.md").Order(StringComparer.Ordinal).ToList() : [])
        {
            var date = RecordDate().Match(Path.GetFileName(file)) is { Success: true } d ? d.Groups["d"].Value : "";
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var m = RecordBullet().Match(lines[i]);
                if (!m.Success) continue;
                var text = m.Groups["text"].Value;
                for (int k = i + 1; k < lines.Length && lines[k].StartsWith("  ", StringComparison.Ordinal); k++)
                    text += " " + lines[k].Trim();
                var instance = RunnerName().Match(text) is { Success: true } r ? r.Groups["i"].Value : "unknown";
                var time = $"{date}T{m.Groups["time"].Value.PadLeft(5, '0')}";
                found.AddRange(Read(text, time, Path.GetFileName(file) + " " + m.Groups["time"].Value, instance, Path.GetFileName(file), unseen));
            }
        }
        var byType = new Dictionary<string, List<Attempt>>(StringComparer.Ordinal);
        foreach (var (type, attempt) in found.OrderBy(f => f.Attempt.Time, StringComparer.Ordinal).ThenBy(f => f.Attempt.Source, StringComparer.Ordinal))
        {
            if (!byType.TryGetValue(type, out var list)) byType[type] = list = [];
            if (list.Any(a => a.Instrument == attempt.Instrument && Near(a.Time, attempt.Time))) continue;
            list.Add(attempt with { Source = "import " + attempt.Source });
        }
        return byType;
    }

    static bool Near(string a, string b) =>
        DateTime.TryParse(a, CultureInfo.InvariantCulture, DateTimeStyles.None, out var x)
        && DateTime.TryParse(b, CultureInfo.InvariantCulture, DateTimeStyles.None, out var y)
        && Math.Abs((x - y).TotalMinutes) <= 40;
}
