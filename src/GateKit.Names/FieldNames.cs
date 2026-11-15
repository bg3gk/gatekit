using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GateKit.Symbols.Queries;

namespace GateKit.Names;

/// <summary>A name to write: which field, what to call it, where the name comes from and what backs it.</summary>
/// <param name="Struct">A nested struct of the layout, when the field is inside one.</param>
/// <param name="Type">The field's type, when the naming also settles it.</param>
/// <param name="Legacy">The earlier name to keep as an alias; by default the one being replaced, if it was a real name.</param>
public sealed record NameChange(string Component, long Offset, string Name, string Source, string Evidence,
    string? Struct = null, string? Type = null, string? Legacy = null);

/// <summary>A name that was written (or would be, on a dry run).</summary>
public sealed record NameWritten(string Component, long Offset, string Was, string Name, string Source, string File);

/// <summary>A field still carrying a machine's name, with what the symbol database already knows at its offset.</summary>
public sealed record OwedField(string Component, string File, long Offset, string Name, string Type, List<FieldFact> Facts)
{
    /// <summary>The fact that settles this field's name by itself, when there is exactly one such
    /// name: the game's own wins over the Toolkit's.</summary>
    public FieldFact? Settled
    {
        get
        {
            foreach (var rung in new[] { "game", "toolkit" })
            {
                var names = Facts.Where(f => f.Certain && f.Rung == rung).GroupBy(f => f.Name, StringComparer.Ordinal).ToList();
                if (names.Count == 1) return names[0].First();
                if (names.Count > 1) return null;   // the source disagrees with itself: a reader decides
            }
            return null;
        }
    }
}

/// <summary>An <c>inferred</c> name whose evidence cites Script Extender and no code of the game.</summary>
public sealed record UncitedName(string Component, string File, string? Struct, long Offset, string Name, string Evidence);

/// <summary>A claim about a field that a run recorded with its recipe, and that no layout carries yet.</summary>
public sealed record PendingClaim(string Component, string Offset, string Belief, string Evidence, string ReachedBy, string? Recipe, string State);

/// <summary>
/// Writing names into the record, and finding which ones are ready to be written.
///
/// The rules are the project's, made mechanical. A name carries the rung it came from - <c>game</c>
/// and <c>toolkit</c> are facts somebody can check again, <c>inferred</c> is reasoning from the
/// game's code, <c>extender</c> is Script Extender's name for the field at that offset that nobody
/// has checked against the game's code, <c>unknown</c> says so - and it carries its evidence,
/// whatever the rung. The game's own name
/// wins: a higher rung is never replaced by a lower one unasked, and a real name that gives way to
/// a better one stays as an alias, the way Script Extender's author renames a field.
/// </summary>
public static partial class FieldNames
{
    /// <summary>The ladder, weakest first. <c>extender</c> sits below <c>inferred</c>: the extender's
    /// author named the field, and a name of ours replaces it only with evidence from the code.</summary>
    public static readonly string[] Ladder = ["unknown", "extender", "inferred", "toolkit", "game"];

    [GeneratedRegex(@"^[A-Za-z_]\w*$", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();

    static int Rank(string? source) => Array.IndexOf(Ladder, source ?? "");

    /// <summary>Changes one field of a layout in memory, or says why not. The caller writes the file.</summary>
    public static NameWritten Set(LayoutFile file, NameChange change, bool force = false)
    {
        string where = $"{file.Component}+0x{change.Offset:X}";
        if (Rank(change.Source) < 0)
            throw new NamesException($"{where}: source must be one of {string.Join(", ", Ladder.Reverse())}, not '{change.Source}'");
        if (string.IsNullOrWhiteSpace(change.Evidence))
            throw new NamesException($"{where}: a name is written only with its evidence - what was read, forced or repeated, and where");
        if (!Identifier().IsMatch(change.Name))
            throw new NamesException($"{where}: '{change.Name}' is not a name a field can have");
        if (Tally.IsMachine(change.Name) && change.Source != "unknown")
            throw new NamesException($"{where}: '{change.Name}' is a machine's name; a field that is not understood keeps it with source unknown");

        JsonArray fields;
        if (change.Struct is null) fields = file.Fields;
        else
        {
            var nested = (file.Json["structs"] as JsonArray)?.FirstOrDefault(s => LayoutFile.Text(s?["name"]) == change.Struct)
                ?? throw new NamesException($"{file.Component} has no nested struct {change.Struct}");
            fields = nested["fields"] as JsonArray ?? [];
        }
        var field = fields.FirstOrDefault(f => LayoutFile.Integer(f?["offset"]) == change.Offset) as JsonObject;
        if (field is null)
        {
            var offsets = fields.Select(f => LayoutFile.Integer(f?["offset"])).Where(o => o != null).Select(o => o!.Value).Order().ToList();
            var below = offsets.Where(o => o < change.Offset).Select(o => $"0x{o:X}").LastOrDefault();
            var above = offsets.Where(o => o > change.Offset).Select(o => $"0x{o:X}").FirstOrDefault();
            throw new NamesException($"{where}: no field starts there (the nearest start at {below ?? "-"} and {above ?? "-"}); "
                + "a new field is layout work, not naming");
        }
        string was = LayoutFile.Text(field["name"]) ?? "";
        string? wasSource = LayoutFile.Text(field["source"]);
        if (Tally.IsPadding(was) && change.Type is null)
            throw new NamesException($"{where}: {was} is padding; if it is a field after all, say its type too");
        if (fields.Any(f => !ReferenceEquals(f, field) && LayoutFile.Text(f?["name"]) == change.Name))
            throw new NamesException($"{where}: another field of {change.Struct ?? file.Component} is already called {change.Name}");
        if (!force && Rank(wasSource) > Rank(change.Source) && !Tally.IsMachine(was))
            throw new NamesException($"{where}: {was} is a {wasSource} name ({LayoutFile.Text(field["evidence"])}); "
                + $"a {change.Source} name does not replace it unless forced");

        string? legacy = change.Legacy ?? (was.Length > 0 && was != change.Name && !Tally.IsMachine(was) ? was : null);
        field["name"] = change.Name;
        if (change.Type is not null) field["type"] = change.Type;
        field["source"] = change.Source;
        field["evidence"] = change.Evidence;
        if (legacy is not null) field["legacy"] = legacy;
        return new NameWritten(file.Component, change.Offset, was, change.Name, change.Source, file.Name);
    }

    /// <summary>
    /// Writes a batch of names: every change of one layout in one replacement of its file, and
    /// nothing at all if any change of the batch is refused - a half-applied batch is harder to
    /// reason about than a refused one.
    /// </summary>
    public static List<NameWritten> Apply(string coverageRoot, IReadOnlyList<NameChange> changes, bool force = false, bool dryRun = false)
    {
        var all = LayoutStore.All(coverageRoot);
        var touched = new Dictionary<string, LayoutFile>(StringComparer.Ordinal);
        var written = new List<NameWritten>();
        var refused = new List<string>();
        foreach (var change in changes)
        {
            try
            {
                var file = LayoutStore.Find(all, change.Component);
                touched.TryAdd(file.Path, file);
                written.Add(Set(touched[file.Path], change, force));
            }
            catch (NamesException e)
            {
                refused.Add(e.Message);
            }
        }
        if (refused.Count > 0)
            throw new NamesException(string.Join("\n", refused) + (changes.Count > refused.Count ? "\nnothing was written: the rest of the batch waits for these" : ""));
        if (!dryRun)
            foreach (var file in touched.Values)
                LayoutStore.Replace(file);
        return written;
    }

    /// <summary>Changes read from JSON: one object or an array of them, with the keys
    /// component, offset (a number or "0x18"), name, source, evidence and optionally struct, type, legacy.
    /// A key nobody knows is refused - a tool that ignores what it does not understand teaches its
    /// caller that the question was asked properly.</summary>
    public static List<NameChange> Parse(JsonNode? node)
    {
        var items = node switch { JsonArray a => a.ToList(), JsonObject o => [o], _ => throw new NamesException("names are given as an object or an array of objects") };
        var changes = new List<NameChange>();
        foreach (var item in items)
        {
            if (item is not JsonObject o) throw new NamesException("each name is an object");
            string[] known = ["component", "offset", "name", "source", "evidence", "struct", "type", "legacy"];
            var strange = o.Select(p => p.Key).Where(k => !known.Contains(k)).ToList();
            if (strange.Count > 0)
                throw new NamesException($"unknown key {string.Join(", ", strange)}; a name has {string.Join(", ", known)}");
            string Need(string key) => LayoutFile.Text(o[key]) ?? throw new NamesException($"a name needs \"{key}\"");
            changes.Add(new NameChange(Need("component"), Offset(o["offset"]), Need("name"), Need("source"), Need("evidence"),
                LayoutFile.Text(o["struct"]), LayoutFile.Text(o["type"]), LayoutFile.Text(o["legacy"])));
        }
        return changes;
    }

    /// <summary>24, "24" or "0x18".</summary>
    public static long Offset(JsonNode? node)
    {
        if (LayoutFile.Integer(node) is { } number) return number;
        return LayoutFile.Text(node) is { } text ? Offset(text) : throw new NamesException("a name needs \"offset\"");
    }

    public static long Offset(string text)
    {
        text = text.Trim().TrimStart('+');
        bool hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        try { return hex ? Convert.ToInt64(text[2..], 16) : long.Parse(text, System.Globalization.CultureInfo.InvariantCulture); }
        catch (Exception e) when (e is FormatException or OverflowException) { throw new NamesException($"'{text}' is not an offset (24 or 0x18)"); }
    }

    /// <summary>
    /// The fields that still carry a machine's name, each with what the symbol database knows at its
    /// offset: the game's own member names, the Toolkit's accessors, Script Extender's own layout.
    /// With <paramref name="everyField"/> the named ones too. So a name is chosen among facts
    /// rather than invented, and where a fact settles it, nobody has to choose at all.
    /// </summary>
    public static List<OwedField> Owed(IEnumerable<LayoutFile> files, SymbolQueries symbols, string prefix = "", bool everyField = false)
    {
        var owed = new List<OwedField>();
        foreach (var file in Tally.Counted(files, prefix))
        {
            List<FieldFact>? facts = null;
            foreach (var field in file.Fields)
            {
                string name = LayoutFile.Text(field?["name"]) ?? "";
                if (Tally.IsPadding(name) || (!everyField && !Tally.IsMachine(name))) continue;
                if (LayoutFile.Integer(field?["offset"]) is not { } offset) continue;
                facts ??= symbols.FieldFacts(file.Component);
                owed.Add(new OwedField(file.Component, file.Name, offset, name, LayoutFile.Text(field?["type"]) ?? "?",
                    facts.Where(f => f.Offset == offset).ToList()));
            }
        }
        return owed;
    }

    /// <summary>The names a fact settles by itself, as changes ready to be written: each a fact of
    /// the game or the Toolkit, read at the field's own offset, with the evidence it was read from.</summary>
    public static List<NameChange> Adoptable(IEnumerable<OwedField> owed) =>
        owed.Where(o => o.Settled is { } fact && Identifier().IsMatch(Plain(fact.Name)) && !Tally.IsMachine(Plain(fact.Name)))
            .Select(o => new NameChange(o.Component, o.Offset, Plain(o.Settled!.Name), o.Settled.Rung, o.Settled.Evidence))
            .ToList();

    /// <summary>
    /// The names the record carries at rung <c>unknown</c> that are Script Extender's own: the
    /// extender's author's headers (never the header generated from this record) declare a field of
    /// that name at that offset of that type. Each becomes a change to rung <c>extender</c> - the
    /// name stays, the rung stops understating where it came from. A name that only resembles the
    /// extender's, or sits at another offset, is left as it is.
    /// </summary>
    public static List<NameChange> ExtenderNames(IEnumerable<LayoutFile> files, SymbolQueries symbols, string prefix = "")
    {
        var changes = new List<NameChange>();
        foreach (var file in files.Where(f => !f.IsSystem && f.Component.StartsWith(prefix, StringComparison.Ordinal)))
        {
            List<FieldFact>? facts = null;
            foreach (var field in file.Fields)
            {
                string name = LayoutFile.Text(field?["name"]) ?? "";
                if (LayoutFile.Text(field?["source"]) != "unknown" || name.Length == 0 || Tally.IsMachine(name)) continue;
                if (LayoutFile.Integer(field?["offset"]) is not { } offset) continue;
                facts ??= symbols.FieldFacts(file.Component);
                if (facts.FirstOrDefault(f => f.Rung == "extender" && f.Offset == offset && f.Name == name) is { } fact)
                    changes.Add(new NameChange(file.Component, offset, name, "extender",
                        $"Script Extender declares {file.Component}::{name} at +0x{offset:X} ({fact.Evidence})"));
            }
        }
        return changes;
    }

    [GeneratedRegex(@"\b(SE|Script Extender|BG3Extender|GameDefinitions)\b", RegexOptions.CultureInvariant)]
    private static partial Regex CitesExtender();

    [GeneratedRegex(@"0x[0-9A-F]+|FUN_|\.dll\b|\bbg3(_dx11)?\.exe|macOS|Toolkit|FieldMeta|creator|constructor|destructor|serializ"
        + @"|\b(reads?|writes?|stores?|stored|copies|copied|passes|passed|zeroe[sd]|loads?|calls?|called|returns?|compares?|checks?|tests?)\b"
        + @"|\bfunctions?\b|\bhandlers?\b|\bUpdate\b|\bExecute\b|\blive\b|decomp|probe|forced", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CitesCode();

    /// <summary>
    /// The <c>inferred</c> names whose evidence cites Script Extender and nothing of the game's code
    /// or memory: "SE SpellMetaId.ProgressionSource", "as SE AppearanceMaterialSetting". They may be
    /// right, and they may be <c>extender</c> names in disguise; which, a reader decides - nothing
    /// here demotes them. The test is on words, so it is a list to read, not a verdict.
    /// </summary>
    public static List<UncitedName> InferredWithoutCode(IEnumerable<LayoutFile> files, string prefix = "")
    {
        var found = new List<UncitedName>();
        foreach (var file in files.Where(f => !f.IsSystem && f.Component.StartsWith(prefix, StringComparison.Ordinal)))
        {
            void Walk(JsonArray fields, string? nested)
            {
                foreach (var field in fields)
                {
                    if (LayoutFile.Text(field?["source"]) != "inferred") continue;
                    string evidence = LayoutFile.Text(field?["evidence"]) ?? "";
                    if (!CitesExtender().IsMatch(evidence) || CitesCode().IsMatch(evidence)) continue;
                    found.Add(new UncitedName(file.Component, file.Name, nested, LayoutFile.Integer(field?["offset"]) ?? -1,
                        LayoutFile.Text(field?["name"]) ?? "", evidence));
                }
            }
            Walk(file.Fields, null);
            foreach (var nested in (file.Json["structs"] as JsonArray ?? []).OfType<JsonObject>())
                Walk(nested["fields"] as JsonArray ?? [], LayoutFile.Text(nested["name"]));
        }
        return found;
    }

    /// <summary>The game writes its members as m_Name; the record and Script Extender keep the Name.</summary>
    static string Plain(string name) => name.StartsWith("m_", StringComparison.Ordinal) && name.Length > 2 ? name[2..] : name;

    /// <summary>
    /// What the runs have claimed about fields and the record does not carry yet. A run writes what
    /// it believes of a field with its recipe (<c>findings.jsonl</c>); the belief becomes a name only
    /// here, after a reader has weighed it. A sighting is never offered: one appearance backs no name.
    /// </summary>
    public static List<PendingClaim> Pending(string coverageRoot)
    {
        var path = Path.Combine(coverageRoot, "findings.jsonl");
        var pending = new List<PendingClaim>();
        if (!File.Exists(path)) return pending;
        var layouts = LayoutStore.All(coverageRoot).Where(f => !f.IsSystem).GroupBy(f => f.Component, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonObject? finding;
            try { finding = JsonNode.Parse(line) as JsonObject; }
            catch (JsonException) { continue; }
            if (finding?["fields"] is not JsonObject fields || fields.Count == 0) continue;
            string component = LayoutFile.Text(finding["component"]) ?? "?";
            string evidence = LayoutFile.Text(finding["evidence"]) ?? "?";
            // the findings ledger's enum says "sighting"; rows written before it said "sighting only"
            if (evidence is "sighting" or "sighting only") continue;
            layouts.TryGetValue(component, out var layout);
            foreach (var (offset, belief) in fields)
            {
                string state;
                if (layout is null) state = "no layout for this type yet";
                else
                {
                    long at;
                    try { at = Offset(offset); }
                    catch (NamesException) { at = -1; }
                    var field = layout.Fields.FirstOrDefault(f => LayoutFile.Integer(f?["offset"]) == at);
                    if (field is null) state = "the layout has no field at this offset";
                    else if (Tally.IsMachine(LayoutFile.Text(field["name"]) ?? "")) state = $"still {LayoutFile.Text(field["name"])}";
                    else continue;   // already named
                }
                pending.Add(new PendingClaim(component, offset, belief is JsonValue v && v.TryGetValue<string>(out var s) ? s : belief?.ToJsonString() ?? "",
                    evidence, LayoutFile.Text(finding["reached_by"]) ?? "?", LayoutFile.Text(finding["recipe"]), state));
            }
        }
        return pending;
    }

    public static JsonNode ToJson<T>(T value) => JsonSerializer.SerializeToNode(value, Web)!;

    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
}
