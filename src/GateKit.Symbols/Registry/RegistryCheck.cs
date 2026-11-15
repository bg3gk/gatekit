using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GateKit.Json;

namespace GateKit.Symbols.Registry;

/// <summary>What `registry check` found: refusals fail it; owed work and notes are listed.</summary>
public sealed class CheckReport
{
    public List<(string Rule, string Where, string What)> Refusals { get; } = [];
    public List<(string Rule, string Where, string What)> Owed { get; } = [];
    public List<(string Rule, string Where, string What)> Notes { get; } = [];

    public void Refuse(string rule, string where, string what) => Refusals.Add((rule, where, what));
    public void Owe(string rule, string where, string what) => Owed.Add((rule, where, what));
    public void Note(string rule, string where, string what) => Notes.Add((rule, where, what));
}

/// <summary>
/// `registry check`: every entry valid and at the path of its id, ids unique also by case, every
/// build table well formed, every citation of code and tests in the repository resolved, and the
/// refusals of registry/README.md: a sentence that names an agent, a session, a date, a time, a
/// machine's path or a model, or quotes the game's code; an empty sentence; a name on an applied
/// rung without evidence that can confirm it; a guess without what it rests on.
/// </summary>
public static partial class RegistryCheck
{
    /// <summary>The keys of an entry that hold our own sentences.</summary>
    static readonly HashSet<string> SentenceKeys = ["does", "about", "says", "rests_on", "would_confirm", "ask", "tried", "answer", "text"];

    [GeneratedRegex(@"\b20\d\d-[01]\d-[0-3]\d\b")] private static partial Regex Date();
    [GeneratedRegex(@"(?<![\d.:+x])\b[0-2]?\d:[0-5]\d(:[0-5]\d)?\b(?![\d:])")] private static partial Regex Clock();
    [GeneratedRegex(@"(?i)\b(sub-?agents?|coordinator|overseer|worktree|conversation|the brief)\b")] private static partial Regex Work();
    [GeneratedRegex(@"(?i)\b(claude|opus|sonnet|haiku|fable|gpt-?\d|chatgpt|gemini|codex)\b")] private static partial Regex Model();
    [GeneratedRegex(@"(?i)([a-z]:[\\/]|\\\\[a-z]|/home/|/users/|%localappdata%|%appdata%|\bappdata\b)")] private static partial Regex MachinePath();
    [GeneratedRegex(@"\b(param_\d+|local_[0-9a-fA-F]+|[a-z]{1,3}Var\d+|[a-z]{0,3}Stack_[0-9a-fA-F]+|in_[A-Z]{2,3}|extraout_[A-Za-z0-9_]+|LAB_[0-9a-fA-F]+|undefined[1248]?)\b")] private static partial Regex DecompilerName();
    [GeneratedRegex(@"\*\s*\(\s*[A-Za-z_][A-Za-z0-9_ ]*\*+\s*\)\s*\(")] private static partial Regex PointerArithmetic();
    [GeneratedRegex(@"\b(FUN|DAT|PTR|thunk_FUN)_[0-9a-fA-F]{6,}\b|\b[A-Za-z0-9_]+\.(exe|dll)\+0x[0-9A-Fa-f]+\b|(?<![\w.])\+0x[0-9A-F]{5,}\b")] private static partial Regex Address();

    public static CheckReport Run(RegistryStore store)
    {
        var report = new CheckReport();
        var entries = new List<RegistryEntry>();
        foreach (var kind in Enum.GetValues<EntryKind>())
        {
            var folder = Path.Combine(store.Root, RegistryStore.Folder(kind));
            if (!Directory.Exists(folder)) continue;
            foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                var rel = store.Relative(path);
                if (!path.EndsWith(".json", StringComparison.Ordinal)) { report.Refuse("file", rel, "not an entry: an entry's file ends in .json"); continue; }
                JsonObject json;
                try { json = JsonText.Read(path, RegistryStore.Schema(kind)).AsObject(); }
                catch (JsonProblemException e)
                {
                    foreach (var p in e.Problems) report.Refuse("schema", rel, p.ToString());
                    continue;
                }
                var entry = new RegistryEntry(kind, path, json);
                entries.Add(entry);
                try
                {
                    var expected = store.PathOf(kind, entry.Id);
                    if (!string.Equals(Path.GetFullPath(expected), Path.GetFullPath(path), StringComparison.Ordinal))
                        report.Refuse("path", rel, $"its id {entry.Id} belongs at {store.Relative(expected)}");
                }
                catch (RegistryException e) { report.Refuse("id", rel, e.Message); }
            }
        }

        // one id, one file - across the kinds, and by case
        foreach (var group in entries.GroupBy(e => RegistryId.CaseKey(e.Id)).Where(g => g.Count() > 1))
            report.Refuse("unique", string.Join(", ", group.Select(e => store.Relative(e.Path))),
                group.Select(e => e.Id).Distinct().Count() == 1 ? $"{group.First().Id} has more than one file" : "these ids differ only by case: one file system would keep one of them");
        var ids = entries.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var known = entries.ToDictionary(e => e.Id, StringComparer.Ordinal);

        CheckTables(store, ids, report);
        foreach (var entry in entries) CheckEntry(store, entry, report);
        CheckCitations(store, known, report);
        return report;
    }

    static void CheckTables(RegistryStore store, HashSet<string> ids, CheckReport report)
    {
        if (!Directory.Exists(store.BuildsFolder)) return;
        foreach (var buildFolder in Directory.EnumerateDirectories(store.BuildsFolder).Order(StringComparer.Ordinal))
        {
            var build = Path.GetFileName(buildFolder);
            if (!File.Exists(store.InfoPath(build))) report.Refuse("build", store.Relative(buildFolder), "no build.json: which build and which bytes these addresses are of");
            else
                try { store.Info(build); }
                catch (JsonProblemException e) { foreach (var p in e.Problems) report.Refuse("schema", store.Relative(store.InfoPath(build)), p.ToString()); }
            foreach (var binaryFolder in Directory.EnumerateDirectories(buildFolder).Order(StringComparer.Ordinal))
            {
                var binary = Path.GetFileName(binaryFolder);
                var seen = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var shard in store.Shards(build, binary))
                {
                    var rel = store.Relative(shard);
                    List<JsonObject> rows;
                    try { rows = JsonText.ReadLines(shard, RegistryStore.BuildSchema); }
                    catch (JsonProblemException e)
                    {
                        foreach (var p in e.Problems) report.Refuse("table", rel, p.ToString());
                        continue;
                    }
                    string? last = null;
                    foreach (var row in rows.Select(BuildRow.FromJson))
                    {
                        if (!ids.Contains(row.Id)) report.Refuse("table", rel, $"{row.Id} has no entry");
                        if (seen.TryGetValue(row.Id, out var other)) report.Refuse("table", rel, $"{row.Id} twice in the table of {binary} (also in {other}); `gatekit registry mend`");
                        seen[row.Id] = rel;
                        try
                        {
                            if (store.ShardPath(build, binary, row.Id) != shard) report.Refuse("table", rel, $"{row.Id} belongs in {store.Relative(store.ShardPath(build, binary, row.Id))}");
                        }
                        catch (RegistryException e) { report.Refuse("id", rel, e.Message); }
                        if (last is not null && string.CompareOrdinal(last, row.Id) > 0) report.Refuse("table", rel, $"not sorted at {row.Id}; `gatekit registry mend`");
                        last = row.Id;
                    }
                }
            }
        }
    }

    static void CheckEntry(RegistryStore store, RegistryEntry entry, CheckReport report)
    {
        var rel = store.Relative(entry.Path);
        var json = entry.Json;
        CheckSentences(json, rel, report);
        if (json["survey"] is JsonObject survey)
            foreach (var (key, value) in survey)
                if (value is JsonValue && JsonText.Text(value) is { } text && (Date().IsMatch(text) || MachinePath().IsMatch(text)))
                    report.Note("survey", rel + "#/survey/" + key, "the survey's carried text names a date or a machine's path; it is to become our sentences");
        CheckRest(json, rel, report);
    }

    /// <summary>Our sentences: no provenance, no code, never empty. An address where an id belongs is a note.</summary>
    /// <summary>
    /// Our sentences: no provenance, no code, never empty; an address where an id belongs is a note.
    /// In a sentence of evidence provenance and code are owed work, not refusals: the earlier stores
    /// carried such sentences over, every writer of the registry refuses a new one
    /// (<see cref="Refusals"/>), and the old ones are rewritten by a person, never by a program.
    /// </summary>
    public static void CheckSentences(JsonObject json, string rel, CheckReport report)
    {
        foreach (var (pointer, text, carried) in Sentences(json, "", false))
        {
            var at = rel + "#" + pointer;
            Action<string, string, string> flag = carried ? report.Owe : report.Refuse;
            if (text.Trim().Length == 0) report.Refuse("sentence", at, "an empty sentence says nothing a reader can use");
            foreach (var (regex, what) in new[] { (Date(), "a date"), (Clock(), "a time of day"), (Work(), "the work (a session, a conversation)"), (Model(), "a model"), (MachinePath(), "a machine's path") })
                if (regex.Match(text) is { Success: true } m) flag("provenance", at, $"names {what}: \"{m.Value}\"");
            if (DecompilerName().Match(text) is { Success: true } name) flag("code", at, $"quotes the decompiler's name {name.Value}: name the thing by its storage or its id");
            if (PointerArithmetic().Match(text) is { Success: true } arithmetic) flag("code", at, $"quotes the game's code: \"{arithmetic.Value}\"");
            foreach (Match address in Address().Matches(text))
                report.Note("address", at, $"an address where an id belongs: {address.Value} (code:<id>)");
        }
    }

    static void CheckRest(JsonObject json, string rel, CheckReport report)
    {
        // the state of the description
        if (JsonText.Text(json["state"]) == "described" && JsonText.Text(json["does"]) is null && JsonText.Text(json["about"]) is null)
            report.Refuse("sentence", rel, "described, and no sentence says what it is (does or about)");

        // rungs: applied needs evidence that can confirm, a guess its reason
        CheckRung(json, rel, "", report);
        foreach (var (list, label) in new[] { ("fields", "field"), ("parameters", "parameter"), ("locals", "local"), ("values", "value") })
            foreach (var (member, i) in (json[list] as JsonArray ?? []).OfType<JsonObject>().Select((m, i) => (m, i)))
                CheckRung(member, rel, $"/{list}/{i}", report);

        // what is below the line says how it could be reached from the game
        var rung = JsonText.Text(json["rung"]);
        if (!Rungs.IsApplied(rung) && JsonText.Text(json["state"]) != "unlooked"
            && !(json["questions"] as JsonArray ?? []).OfType<JsonObject>().Any(q => JsonText.Text(q["about"]) == "reach"))
            report.Owe("questions", rel, "unconfirmed, and no question says how the game reaches it (`registry note --ask`)");

        // variables and ties
        var variables = (json["parameters"] as JsonArray ?? []).Concat(json["locals"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        foreach (var v in variables)
            if (JsonText.Text(v["storage"]) == "unresolved")
            {
                if (JsonText.Text(v["decompiler_name"]) is null) report.Refuse("variable", rel, $"{JsonText.Text(v["name"])}: unresolved storage and no decompiler's name to find it by");
                else report.Note("variable", rel, $"{JsonText.Text(v["name"])} is known only by the decompiler's temporary {JsonText.Text(v["decompiler_name"])}: read its storage");
            }
            else if (JsonText.Text(v["decompiler_name"]) is not null)
                report.Refuse("variable", rel, $"{JsonText.Text(v["name"])}: a variable with its storage is not kept by the decompiler's name");
        var names = variables.Select(v => JsonText.Text(v["name"])).ToHashSet(StringComparer.Ordinal);
        foreach (var tie in (json["ties"] as JsonArray ?? []).OfType<JsonObject>())
            if (JsonText.Text(tie["via"]) is { } via && !names.Contains(via))
                report.Refuse("tie", rel, $"ties {JsonText.Text(tie["type"])} through {via}, which is no parameter or local of it");
    }

    static void CheckRung(JsonObject json, string rel, string pointer, CheckReport report)
    {
        var rung = JsonText.Text(json["rung"]);
        var at = rel + (pointer.Length > 0 ? "#" + pointer : "");
        var label = JsonText.Text(json["name"]) is { } n ? $"{n}: " : "";
        if (Rungs.IsApplied(rung))
        {
            var kinds = (json["evidence"] as JsonArray ?? []).OfType<JsonObject>().Select(e => JsonText.Text(e["kind"])).ToList();
            if (!kinds.Any(k => k is not null && Rungs.Confirming.Contains(k)))
                report.Refuse("rung", at, $"{label}on the applied rung {rung} with no evidence that can confirm it ({string.Join(", ", Rungs.Confirming)}); "
                    + (kinds.Count == 0 ? "it has none" : "it has " + string.Join(", ", kinds)));
            if (json["guess"] is not null) report.Refuse("rung", at, $"{label}a guess is not on an applied rung");
        }
        if (rung == "guess" && json["guess"] is null) report.Refuse("rung", at, $"{label}a guess says what it rests on and what would confirm it");
    }

    /// <summary>Every sentence of an entry, with its pointer and whether it is carried text (an evidence
    /// item of kind text); the survey is carried data and is not among them.</summary>
    static IEnumerable<(string Pointer, string Text, bool Carried)> Sentences(JsonNode? node, string pointer, bool carried)
    {
        switch (node)
        {
            case JsonObject o:
                var isCarried = carried || pointer.Contains("/evidence/", StringComparison.Ordinal);
                foreach (var (key, value) in o)
                {
                    if (key == "survey") continue;
                    var here = JsonPointer.Append(pointer, key);
                    if (SentenceKeys.Contains(key) && JsonText.Text(value) is { } text) yield return (here, text, isCarried);
                    else foreach (var inner in Sentences(value, here, carried)) yield return inner;
                }
                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                    foreach (var inner in Sentences(a[i], JsonPointer.Append(pointer, i), carried)) yield return inner;
                break;
        }
    }

    /// <summary>What a writer of the registry refuses in an entry it is about to write: every sentence held to the rule, carried text too.
    /// A sentence the entry already had (<paramref name="before"/>) is not held against the write.</summary>
    public static List<string> Refusals(JsonObject entry, JsonObject? before = null)
    {
        var report = new CheckReport();
        CheckSentences(entry, "", report);
        var old = before is null ? [] : Sentences(before, "", false).Select(s => s.Text).ToHashSet(StringComparer.Ordinal);
        var texts = Sentences(entry, "", false).ToDictionary(s => "#" + s.Pointer, s => s.Text);
        return [.. report.Refusals.Concat(report.Owed).Where(r => !(texts.TryGetValue(r.Where, out var t) && old.Contains(t))).Select(r => $"{r.Where} {r.What}")];
    }

    static void CheckCitations(RegistryStore store, Dictionary<string, RegistryEntry> known, CheckReport report)
    {
        foreach (var c in RegistryRefs.All(store))
        {
            if (c.Kind == "code")
            {
                if (!RegistryId.IsValid(c.Address)) { report.Refuse("cite-code", c.Where, $"{c.Address} is not a registry id"); continue; }
                var parts = RegistryId.Parse(c.Address);
                if (!known.TryGetValue(parts.Entity, out var entry)) { report.Refuse("cite-code", c.Where, $"{c.Address}: no entry {parts.Entity}"); continue; }
                if (parts.Member is { } member && !HasMember(entry.Json, member))
                    report.Refuse("cite-code", c.Where, $"{c.Address}: {parts.Entity} has no member {member}");
            }
            else
            {
                var (found, rest) = RegistryRefs.ResolveTest(store.Repository, c.Address);
                if (!found) report.Refuse("cite-test", c.Where, $"{c.Address}: no test of that path under tests/");
                else if (rest is not null) report.Note("cite-test", c.Where, $"{c.Address}: the case and step {rest} are not checked yet");
            }
        }
    }

    static bool HasMember(JsonObject entry, string member)
    {
        var first = member.Split('.')[0];
        return new[] { "fields", "parameters", "locals", "values" }.Any(list =>
            (entry[list] as JsonArray ?? []).OfType<JsonObject>().Any(m => JsonText.Text(m["name"]) == first));
    }
}
