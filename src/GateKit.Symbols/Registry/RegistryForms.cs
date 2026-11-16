using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GateKit.Json;
using GateKit.Symbols.Decomp;

namespace GateKit.Symbols.Registry;

/// <summary>
/// The rungs, strongest first, and which of them may be given to the decompiler as a real name or
/// type (applied) and which are shown beside the code only (registry/README.md, "Rungs").
/// </summary>
public static class Rungs
{
    public static readonly IReadOnlyList<string> All = ["game", "toolkit", "confirmed", "inferred", "extender", "guess", "unknown"];

    public static readonly IReadOnlySet<string> Applied = new HashSet<string>(StringComparer.Ordinal) { "game", "toolkit", "confirmed" };

    /// <summary>The kinds of evidence that can put an entry on an applied rung.</summary>
    public static readonly IReadOnlySet<string> Confirming = new HashSet<string>(StringComparer.Ordinal) { "watch", "read", "game-name", "toolkit-name", "test" };

    public static bool IsApplied(string? rung) => rung is not null && Applied.Contains(rung);

    /// <summary>The higher of two rungs (the lower index).</summary>
    public static int Rank(string? rung) => rung is null ? All.Count : All.ToList().IndexOf(rung) is var i && i >= 0 ? i : All.Count;

    /// <summary>The kinds of evidence there are; <c>reach</c> says that the code ran in a situation, not what it does.</summary>
    public static readonly IReadOnlyList<string> EvidenceKinds = ["watch", "read", "reach", "pairing", "game-name", "toolkit-name", "extender-name", "test", "text"];

    /// <summary>
    /// The bar (registry/README.md, "Rungs"): the highest rung the evidence of an entry or of a member
    /// carries, and in <paramref name="why"/> what a higher one would need. A store's name puts a
    /// thing on the store's rung when the name is tied to it: for a function (<paramref name="code"/>)
    /// by the store's own match or pair, or by an instrument's evidence of this build's function in the
    /// entry (a read, a watch, a test); a type, a field or a value carries its name itself. A name of
    /// ours is <c>confirmed</c> by a read and a watch together, or by a test. Everything else - a
    /// reading alone, a watch alone, a counter of reach, a pairing, Script Extender's name, a text -
    /// is <c>inferred</c>.
    /// </summary>
    public static string Ceiling(JsonNode? evidence, bool code, out string why)
    {
        var items = (evidence as JsonArray ?? []).OfType<JsonObject>().ToList();
        var kinds = items.Select(e => JsonText.Text(e["kind"]) ?? "").ToHashSet(StringComparer.Ordinal);
        var body = kinds.Contains("read") || kinds.Contains("watch") || kinds.Contains("test");
        bool Tied(string kind) => !code || body || items.Any(e => JsonText.Text(e["kind"]) == kind && JsonText.Text(e["tie"]) is "match" or "pair");
        var has = kinds.Count == 0 ? "it has none" : "it has " + string.Join(", ", kinds.Order(StringComparer.Ordinal));
        why = "";
        if (kinds.Contains("game-name") && Tied("game-name")) return "game";
        if (kinds.Contains("toolkit-name") && Tied("toolkit-name")) return "toolkit";
        if (kinds.Contains("test") || (kinds.Contains("read") && kinds.Contains("watch"))) return "confirmed";
        why = kinds.Contains("game-name") || kinds.Contains("toolkit-name")
            ? "a store's name that nothing ties to this function (no match, no pair, no read, watch or test); " + has
            : "a name of ours is confirmed by a read and a watch together, or by a test; " + has;
        return "inferred";
    }

    /// <summary>The rung an entry or a member stands on by its evidence: the rung asked, lowered to the
    /// <see cref="Ceiling"/> when it is above it; and a name of ours that is <c>confirmed</c> takes the
    /// store's rung once the store's name is among its evidence. Null <paramref name="why"/>: unchanged.</summary>
    public static string? Settle(string? asked, JsonNode? evidence, bool code, out string? why)
    {
        why = null;
        if (asked is null) return null;
        var ceiling = Ceiling(evidence, code, out var short_);
        if (Rank(asked) < Rank(ceiling)) { why = short_.Length > 0 ? short_ : $"its evidence carries {ceiling}"; return ceiling; }
        if (asked == "confirmed" && ceiling is "game" or "toolkit") { why = $"the name is the {(ceiling == "game" ? "game's" : "Toolkit's")}: its evidence says where it is held"; return ceiling; }
        return asked;
    }

    /// <summary>The kind an evidence sentence of an earlier store is carried as: the name's own source where the rung says it, else plain text.</summary>
    public static string EvidenceKindOf(string? rung) => rung switch
    {
        "game" => "game-name",
        "toolkit" => "toolkit-name",
        "extender" => "extender-name",
        _ => "text",
    };
}

/// <summary>
/// The one way between the earlier stores and the registry: a row of <c>functions.jsonl</c>, a row of
/// <c>structs.jsonl</c> and a layout of <c>layouts/**</c> each become an entry and come back from it
/// unchanged, which the import's round trip proves. The readers and writers of those stores
/// (<see cref="FunctionNames"/>, LayoutStore) go through these forms to the registry.
/// </summary>
public static partial class RegistryForms
{
    // --- evidence ---

    static JsonArray Evidence(string rung, string text) =>
        text.Length == 0 ? [] : [new JsonObject { ["kind"] = Rungs.EvidenceKindOf(rung), ["says"] = text }];

    /// <summary>The evidence of an entry or member as one sentence, as the earlier stores wrote it.</summary>
    public static string EvidenceText(JsonNode? evidence) =>
        string.Join(" | ", (evidence as JsonArray ?? []).OfType<JsonObject>().Select(e =>
            JsonText.Text(e["kind"]) == "test" ? "test:" + JsonText.Text(e["test"]) + (JsonText.Text(e["says"]) is { } s ? " - " + s : "") : JsonText.Text(e["says"]) ?? ""));

    // --- variables: the decompiler's names and the storage they stand for ---

    [GeneratedRegex(@"^param_(\d+)$")] private static partial Regex Param();
    [GeneratedRegex(@"^local_([0-9a-fA-F]+)$")] private static partial Regex Local();
    [GeneratedRegex(@"^stack:-0x([0-9A-F]+)$")] private static partial Regex StackBelow();

    static readonly string[] IntegerRegisters = ["rcx", "rdx", "r8", "r9"];

    /// <summary>The parameters of a signature as the decompiler writes it, (type, name) in order; empty when it has none or cannot be read.</summary>
    public static List<(string Type, string Name)> SignatureParameters(string? signature)
    {
        var list = new List<(string, string)>();
        if (signature is null) return list;
        var open = signature.IndexOf('(');
        var close = signature.LastIndexOf(')');
        if (open < 0 || close <= open) return list;
        var inner = signature[(open + 1)..close];
        var depth = 0;
        var start = 0;
        var pieces = new List<string>();
        for (var i = 0; i <= inner.Length; i++)
        {
            if (i == inner.Length || (inner[i] == ',' && depth == 0)) { pieces.Add(inner[start..i].Trim()); start = i + 1; continue; }
            if (inner[i] is '<' or '(') depth++;
            else if (inner[i] is '>' or ')') depth--;
        }
        foreach (var piece in pieces)
        {
            if (piece.Length == 0 || piece == "void" || piece == "...") continue;
            var m = Regex.Match(piece, @"^(.*?)([A-Za-z_][A-Za-z0-9_]*)$");
            if (m.Success) list.Add((m.Groups[1].Value.Trim(), m.Groups[2].Value));
        }
        return list;
    }

    /// <summary>
    /// Where a variable the decompiler named lives: a parameter by its place in the signature
    /// (Windows x64: the first four in rcx, rdx, r8, r9, or xmm0-3 for a float or double; the rest on
    /// the stack from +0x28 at entry), <c>local_N</c> at <c>stack:-0xN</c>; null for a temporary,
    /// whose storage the pseudo-code does not say.
    /// </summary>
    public static string? StorageOf(string decompilerName, string? signature)
    {
        if (Local().Match(decompilerName) is { Success: true } local)
            return "stack:-0x" + local.Groups[1].Value.ToUpperInvariant();
        var parameters = SignatureParameters(signature);
        var at = parameters.FindIndex(p => p.Name == decompilerName);
        if (at < 0) return null;
        if (at >= 4) return string.Create(CultureInfo.InvariantCulture, $"stack:+0x{8 * (at + 1):X}");
        var type = parameters[at].Type.Trim();
        return type is "float" or "double" ? $"reg:xmm{at}" : "reg:" + IntegerRegisters[at];
    }

    /// <summary>The decompiler's name of a storage in a signature; for a stack slot below the frame, <c>local_n</c>; null when the signature has no such place.</summary>
    public static string? DecompilerNameOf(string storage, string? signature)
    {
        if (StackBelow().Match(storage) is { Success: true } below) return "local_" + below.Groups[1].Value.ToLowerInvariant();
        int at;
        if (storage.StartsWith("reg:xmm", StringComparison.Ordinal) && int.TryParse(storage[7..], CultureInfo.InvariantCulture, out var x)) at = x;
        else if (storage.StartsWith("reg:", StringComparison.Ordinal)) at = Array.IndexOf(IntegerRegisters, storage[4..]);
        else if (storage.StartsWith("stack:+0x", StringComparison.Ordinal)) at = (int)(RegistryStore.ParseHex(storage[7..]) / 8) - 1;
        else return null;
        if (at < 0) return null;
        var parameters = SignatureParameters(signature);
        if (at < parameters.Count) return parameters[at].Name;
        return signature is null ? string.Create(CultureInfo.InvariantCulture, $"param_{at + 1}") : null;
    }

    /// <summary>The variables of a function entry by the decompiler's names in a signature: decompiler name -> our name.</summary>
    public static SortedDictionary<string, string> DecompilerVariables(JsonObject entry, string? signature)
    {
        var vars = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var v in Variables(entry))
        {
            var storage = JsonText.Text(v["storage"])!;
            var name = JsonText.Text(v["name"])!;
            var key = storage == "unresolved" ? JsonText.Text(v["decompiler_name"]) : DecompilerNameOf(storage, signature);
            if (key is not null) vars[key] = name;
        }
        return vars;
    }

    static IEnumerable<JsonObject> Variables(JsonObject entry) =>
        (entry["parameters"] as JsonArray ?? []).Concat(entry["locals"] as JsonArray ?? []).OfType<JsonObject>();

    // --- a row of functions.jsonl ---

    /// <summary>A row of the ledger of function names as an entry and its row in a build table.</summary>
    internal static (JsonObject Entry, BuildRow? Place, string? Build) FromFunctionRow(FunctionNames.Row row, Func<string, long, string?> signature)
    {
        var id = RegistryId.Placed(row.Name);
        var entry = new JsonObject { ["id"] = id };
        if (row.Does is { Length: > 0 }) entry["state"] = "described";
        entry["rung"] = row.Source;
        if (row.Does is { Length: > 0 } does) entry["does"] = does;
        var sig = signature(row.Binary, row.Rva);
        var parameters = new JsonArray();
        var locals = new JsonArray();
        foreach (var (old, name) in row.Vars)
        {
            var storage = StorageOf(old, sig);
            var v = new JsonObject { ["storage"] = storage ?? "unresolved", ["name"] = name };
            if (storage is null) v["decompiler_name"] = old;
            (storage is not null && (storage.StartsWith("reg:", StringComparison.Ordinal) || storage.StartsWith("stack:+", StringComparison.Ordinal)) ? parameters : locals).Add(v);
        }
        if (parameters.Count > 0) entry["parameters"] = parameters;
        if (locals.Count > 0) entry["locals"] = locals;
        if (row.Structs.Count > 0)
            entry["ties"] = new JsonArray([.. row.Structs.Select(p => (JsonNode)new JsonObject { ["type"] = p.Value, ["via"] = row.Vars.TryGetValue(p.Key, out var n) ? n : p.Key })]);
        if (row.Fingerprint is { Count: > 0 }) entry["fingerprint"] = row.Fingerprint.DeepClone();
        var evidence = Evidence(row.Source, row.Evidence);
        if (evidence.Count > 0) entry["evidence"] = evidence;
        var place = row.Build is null ? null : new BuildRow(id, row.Rva, row.Size, row.Body);
        return (entry, place, row.Build);
    }

    /// <summary>An entry and its row in a build table as a row of the ledger of function names.</summary>
    internal static FunctionNames.Row ToFunctionRow(JsonObject entry, string binary, string? build, BuildRow place, string? signature)
    {
        var rung = JsonText.Text(entry["rung"]) ?? "unknown";
        var vars = DecompilerVariables(entry, signature);
        var structs = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var tie in (entry["ties"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var via = JsonText.Text(tie["via"]);
            if (via is not null) structs[via] = JsonText.Text(tie["type"])!;
        }
        return new FunctionNames.Row(binary, place.Rva, RegistryId.Unplace(JsonText.Text(entry["id"])!), rung, EvidenceText(entry["evidence"]), JsonText.Text(entry["does"]), [])
        {
            Vars = vars, Structs = structs, Build = build, Fingerprint = entry["fingerprint"]?.DeepClone() as JsonObject, Size = place.Size, Body = place.Body,
        };
    }

    // --- a row of structs.jsonl ---

    /// <summary>A field of the ledger of structure fields as a member of a type entry.</summary>
    internal static JsonObject FromStructField(FunctionNames.Field f)
    {
        var field = new JsonObject { ["offset"] = f.Offset, ["name"] = f.Name };
        if (f.Type is { Length: > 0 }) field["type"] = f.Type;
        field["rung"] = f.Source;
        field["evidence"] = Evidence(f.Source, f.Evidence);
        return field;
    }

    internal static FunctionNames.Field ToStructField(string structure, JsonObject field) => new(structure, field["offset"]!.GetValue<long>(),
        JsonText.Text(field["name"])!, JsonText.Text(field["type"]), JsonText.Text(field["rung"]) ?? "unknown", EvidenceText(field["evidence"]));

    // --- a layout of layouts/** ---

    static readonly string[] LayoutOwn = ["component", "kind", "ext_name", "oneFrame", "size", "summary", "fields"];

    /// <summary>A layout as a type entry: the fields with their rungs and evidence, the size, the
    /// summary as its sentence, the concept, and every other key of the layout kept as it was under
    /// <c>survey</c>, with the batch it was written in.</summary>
    public static JsonObject FromLayout(JsonObject layout, string batch)
    {
        var component = JsonText.Text(layout["component"])!;
        var system = JsonText.Text(layout["kind"]) == "system";
        var entry = new JsonObject { ["id"] = component, ["kind"] = system ? "class" : "struct" };
        if (JsonText.Text(layout["summary"]) is { Length: > 0 } summary) entry["about"] = summary;
        if (layout["size"] is JsonValue size) entry["size"] = size.DeepClone();
        var fields = new JsonArray();
        foreach (var f in (layout["fields"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var field = new JsonObject { ["offset"] = f["offset"]!.DeepClone(), ["name"] = f["name"]!.DeepClone() };
            if (f["type"] is not null) field["type"] = f["type"]!.DeepClone();
            var rung = JsonText.Text(f["source"]);
            if (rung is not null) field["rung"] = rung;
            if (JsonText.Text(f["legacy"]) is { } legacy)
                field["known_as"] = new JsonArray(new JsonObject { ["name"] = legacy, ["source"] = "extender-legacy" });
            if (f["evidence"] is not null) field["evidence"] = Evidence(rung ?? "unknown", JsonText.Text(f["evidence"]) ?? "");
            fields.Add(field);
        }
        entry["fields"] = fields;
        var concept = new JsonObject { ["kind"] = system ? "system" : "component" };
        if (JsonText.Text(layout["ext_name"]) is { } ext) concept["ext_name"] = ext;
        if (layout["oneFrame"] is JsonValue oneFrame) concept["one_frame"] = oneFrame.DeepClone();
        entry["concept"] = concept;
        var survey = new JsonObject { ["batch"] = batch };
        if (layout["summary"] is JsonValue s && JsonText.Text(s) is "") survey["summary"] = "";
        foreach (var (key, value) in layout)
            if (!LayoutOwn.Contains(key)) survey[key] = value?.DeepClone();
        entry["survey"] = survey;
        return entry;
    }

    /// <summary>Whether a type entry came from a layout (it carries the survey).</summary>
    public static bool IsLayout(JsonObject entry) => entry["survey"] is JsonObject;

    /// <summary>The batch a layout type was written in.</summary>
    public static string BatchOf(JsonObject entry) => JsonText.Text(entry["survey"]?["batch"]) ?? "registry";

    /// <summary>A type entry of a layout back as the layout.</summary>
    public static JsonObject ToLayout(JsonObject entry)
    {
        var layout = new JsonObject { ["component"] = JsonText.Text(entry["id"]) };
        var concept = entry["concept"] as JsonObject;
        if (JsonText.Text(concept?["kind"]) == "system") layout["kind"] = "system";
        if (JsonText.Text(concept?["ext_name"]) is { } ext) layout["ext_name"] = ext;
        if (concept?["one_frame"] is JsonValue oneFrame) layout["oneFrame"] = oneFrame.DeepClone();
        if (entry["size"] is JsonValue size) layout["size"] = size.DeepClone();
        var survey = entry["survey"] as JsonObject ?? [];
        if (JsonText.Text(entry["about"]) is { } about) layout["summary"] = about;
        else if (survey["summary"] is JsonValue empty) layout["summary"] = empty.DeepClone();
        var fields = new JsonArray();
        foreach (var field in (entry["fields"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var f = new JsonObject { ["name"] = field["name"]!.DeepClone(), ["offset"] = field["offset"]!.DeepClone() };
            if (field["type"] is not null) f["type"] = field["type"]!.DeepClone();
            if (JsonText.Text(field["rung"]) is { } rung) f["source"] = rung;
            if (field["evidence"] is JsonArray evidence) f["evidence"] = EvidenceText(evidence);
            if ((field["known_as"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(k => JsonText.Text(k["source"]) == "extender-legacy") is { } legacy)
                f["legacy"] = JsonText.Text(legacy["name"]);
            fields.Add(f);
        }
        layout["fields"] = fields;
        foreach (var (key, value) in survey)
            if (key is not ("batch" or "summary")) layout[key] = value?.DeepClone();
        return layout;
    }

    /// <summary>A changed layout put back into its type entry: the layout's own parts replaced, everything
    /// else the entry has (names of other sources, comments, bases) kept.</summary>
    public static JsonObject MergeLayout(JsonObject entry, JsonObject layout)
    {
        var fresh = FromLayout(layout, BatchOf(entry));
        var merged = (JsonObject)entry.DeepClone();
        foreach (var key in new[] { "kind", "about", "size", "fields", "concept", "survey" }) merged.Remove(key);
        // the fields' own extras (a guess, a comment, another source's name) follow the field by its offset and name
        var before = (entry["fields"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        foreach (var field in (fresh["fields"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var was = before.FirstOrDefault(b => b["offset"]?.GetValue<long>() == field["offset"]?.GetValue<long>() && JsonText.Text(b["name"]) == JsonText.Text(field["name"]))
                ?? before.FirstOrDefault(b => b["offset"]?.GetValue<long>() == field["offset"]?.GetValue<long>());
            if (was is null) continue;
            // A layout carries a field's evidence as one sentence; where that sentence and the rung are
            // what the entry already says, the entry's own evidence stays, each piece with its kind.
            if (was["evidence"] is JsonArray kept && JsonText.Text(was["rung"]) == JsonText.Text(field["rung"])
                && EvidenceText(kept) == EvidenceText(field["evidence"]))
                field["evidence"] = kept.DeepClone();
            foreach (var (key, value) in was)
                if (key is not ("offset" or "name" or "type" or "rung" or "evidence") && !field.ContainsKey(key)) field[key] = value?.DeepClone();
        }
        foreach (var (key, value) in fresh.ToList())
        {
            fresh.Remove(key);
            if (key != "id") merged[key] = value;
        }
        return merged;
    }
}
