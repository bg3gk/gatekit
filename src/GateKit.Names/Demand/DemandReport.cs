using System.Text.Json.Nodes;

namespace GateKit.Names.Demand;

/// <summary>
/// The demand file joined with the record: what the mods use most, and how far GateKit's record of
/// each of those is from right. Read-only; the counts are taken over the component rows.
/// </summary>
public sealed class DemandReport
{
    /// <summary>One demanded field and what the record says of it.</summary>
    public sealed record FieldState(string Type, int TypeMods, string Field, int Mods, string State, string? Rung, string? Current);

    public sealed record TypeRow(string Type, string? Ext, int Mods, JsonObject Uses, bool Registered, bool Mapped,
        bool Seen, bool Verified, bool OneFrame, string? Layout, int LayoutFields, int EvidenceFields, int OpenDemandedFields,
        int Systems, int Creators);

    /// <summary>A whole number however the row holds it (parsed from a file, or built in memory as an int).</summary>
    public static long? Num(JsonNode? node) => node is not JsonValue v ? null
        : v.TryGetValue<long>(out var l) ? l : v.TryGetValue<int>(out var i) ? i : v.TryGetValue<double>(out var d) ? (long)d : null;

    public List<TypeRow> Types { get; } = [];
    public List<FieldState> Fields { get; } = [];
    public List<JsonObject> Osiris { get; } = [];
    public List<JsonObject> Api { get; } = [];
    public List<JsonObject> Events { get; } = [];
    public List<JsonObject> Functors { get; } = [];
    public bool HasSystems { get; private set; }
    /// <summary>How many mods the demand file was built from.</summary>
    public int Mods { get; private set; }

    /// <summary>A field's state, given its layout: <c>evidence</c> (inferred, toolkit or game and not a
    /// machine's name), <c>machine</c> (a machine's name whatever the rung), <c>extender</c> or
    /// <c>unknown</c> (the rung), <c>not in the layout</c> when no field of the type or of its nested
    /// structs carries the name (or a legacy name), <c>no layout</c> when the type has none.</summary>
    public static (string State, string? Rung, string? Current) Judge(LayoutFile? layout, string field)
    {
        if (layout is null) return ("no layout", null, null);
        // a field of the type first, then one of its nested structs (a mod names Resources[].Amount as Amount)
        var nested = (layout.Json["structs"] as JsonArray ?? []).OfType<JsonObject>().SelectMany(s => s["fields"] as JsonArray ?? []);
        foreach (var node in layout.Fields.Concat(nested))
        {
            var name = LayoutFile.Text(node?["name"]) ?? "";
            var legacy = LayoutFile.Text(node?["legacy"]);
            if (!name.Equals(field, StringComparison.OrdinalIgnoreCase) && !(legacy?.Equals(field, StringComparison.OrdinalIgnoreCase) ?? false)) continue;
            var rung = LayoutFile.Text(node?["source"]) ?? "unknown";
            if (Tally.IsMachine(name)) return ("machine", rung, name);
            if (Tally.HasEvidence(rung)) return ("evidence", rung, name);
            return (rung == "extender" ? "extender" : "unknown", rung, name);
        }
        return ("not in the layout", null, null);
    }

    public static bool Open(string state) => state is not "evidence";

    public static DemandReport Build(IReadOnlyList<JsonObject> rows, RecordState record)
    {
        var report = new DemandReport { HasSystems = record.HasSystems };
        foreach (var row in rows)
        {
            switch (LayoutFile.Text(row["kind"]))
            {
                case "source": report.Mods = (int)(Num(row["count"]) ?? 0); break;
                case "osiris": report.Osiris.Add(row); break;
                case "se_api": report.Api.Add(row); break;
                case "se_event": report.Events.Add(row); break;
                case "functor": report.Functors.Add(row); break;
                case "component":
                {
                    var type = LayoutFile.Text(row["type"]) ?? "?";
                    int mods = (int)(Num(row["mods"]) ?? 0);
                    var state = record.Types.GetValueOrDefault(type);
                    var layout = state?.Layout;
                    int open = 0;
                    foreach (var field in (row["fields"] as JsonArray ?? []).OfType<JsonObject>())
                    {
                        var name = LayoutFile.Text(field["name"]) ?? "";
                        var (judged, rung, current) = Judge(layout, name);
                        if (Open(judged)) open++;
                        if (mods > 0) report.Fields.Add(new FieldState(type, mods, name, (int)(Num(field["mods"]) ?? 0), judged, rung, current));
                    }
                    int evidence = layout?.Fields.Count(f => !Tally.IsMachine(LayoutFile.Text(f?["name"]) ?? "") && Tally.HasEvidence(LayoutFile.Text(f?["source"]) ?? "")) ?? 0;
                    var entry = new TypeRow(type, LayoutFile.Text(row["ext_name"]), mods,
                        row["uses"] as JsonObject ?? [], state is not null && (state.Mapped || state.Sides.Count > 0), state?.Mapped ?? false,
                        state?.Seen ?? false, state?.Verified ?? false, state?.OneFrame ?? false,
                        layout is null ? null : $"{Path.GetFileName(Path.GetDirectoryName(layout.Path))}/{layout.Name} ({layout.Status ?? "hypothesis"})",
                        layout?.Fields.Count ?? 0, evidence, open, state?.Systems.Count ?? 0, state?.Creators.Count ?? 0);
                    if (mods > 0) report.Types.Add(entry);
                    break;
                }
            }
        }
        return report;
    }

    public IEnumerable<TypeRow> NeverSeen => Types.Where(t => !t.Seen);
    public IEnumerable<TypeRow> Unverified => Types.Where(t => t.Seen && !t.Verified);
    public IEnumerable<FieldState> OpenFields => Fields.Where(f => Open(f.State));
    public IEnumerable<TypeRow> NoSystem => Types.Where(t => t.Systems == 0);
    public IEnumerable<TypeRow> NoCreator => Types.Where(t => t.Creators == 0);
    /// <summary>Types whose layout carries a field without an evidence-backed name.</summary>
    public IEnumerable<TypeRow> LayoutOpen => Types.Where(t => t.Layout is not null && t.EvidenceFields < t.LayoutFields);

    static string Uses(JsonObject uses) =>
        $"r{uses["read"]} w{uses["write"]} c{uses["create"]} s{uses["subscribe"]}";

    static string Yes(bool b) => b ? "yes" : "-";

    public JsonObject Headline() => new()
    {
        ["mods"] = Mods,
        ["types_used"] = Types.Count,
        ["never_seen"] = NeverSeen.Count(),
        ["never_seen_not_registered"] = NeverSeen.Count(t => !t.Registered),
        ["seen_unverified"] = Unverified.Count(),
        ["without_layout"] = Types.Count(t => t.Layout is null),
        ["with_open_layout_fields"] = LayoutOpen.Count(),
        ["with_open_demanded_fields"] = Types.Count(t => t.OpenDemandedFields > 0),
        ["demanded_fields"] = Fields.Count,
        ["demanded_fields_open"] = OpenFields.Count(),
        ["no_described_system"] = HasSystems ? NoSystem.Count() : null,
        ["no_described_creator"] = HasSystems ? NoCreator.Count() : null,
        ["osiris_names"] = Osiris.Count,
        ["se_api_names"] = Api.Count,
        ["se_events"] = Events.Count,
        ["functors"] = Functors.Count,
    };

    static JsonObject ToJson(TypeRow t) => new()
    {
        ["type"] = t.Type, ["ext_name"] = t.Ext, ["mods"] = t.Mods, ["uses"] = t.Uses.DeepClone(),
        ["registered"] = t.Registered, ["mapped"] = t.Mapped, ["seen"] = t.Seen, ["verified"] = t.Verified, ["one_frame"] = t.OneFrame,
        ["layout"] = t.Layout, ["layout_fields"] = t.LayoutFields, ["evidence_fields"] = t.EvidenceFields,
        ["open_demanded_fields"] = t.OpenDemandedFields, ["systems"] = t.Systems, ["creators"] = t.Creators,
    };

    static JsonObject ToJson(FieldState f) => new()
    {
        ["type"] = f.Type, ["type_mods"] = f.TypeMods, ["field"] = f.Field, ["mods"] = f.Mods, ["state"] = f.State, ["rung"] = f.Rung, ["current"] = f.Current,
    };

    public JsonObject ToJson(int top) => new()
    {
        ["headline"] = Headline(),
        ["top"] = new JsonArray([.. Types.Take(top).Select(t => (JsonNode)ToJson(t))]),
        ["never_seen"] = new JsonArray([.. NeverSeen.Select(t => (JsonNode)ToJson(t))]),
        ["unverified"] = new JsonArray([.. Unverified.Select(t => (JsonNode)ToJson(t))]),
        ["open_fields"] = new JsonArray([.. OrderedFields().Select(f => (JsonNode)ToJson(f))]),
        ["no_described_system"] = new JsonArray([.. NoSystem.Select(t => (JsonNode)t.Type)]),
        ["no_described_creator"] = new JsonArray([.. NoCreator.Select(t => (JsonNode)t.Type)]),
        ["osiris"] = new JsonArray([.. Osiris.Take(top * 2).Select(o => (JsonNode)OsirisJudged(o))]),
        ["osiris_gaps"] = new JsonArray([.. OsirisGaps().Select(o => (JsonNode)o)]),
    };

    /// <summary>The Osiris names (any rank) the index lacks, or holds with another arity than a report gives.</summary>
    public IEnumerable<JsonObject> OsirisGaps() =>
        Osiris.Select(OsirisJudged).Where(o => (string?)o["verdict"] is { } v && v != "index holds it" && v != "no index to ask");

    IEnumerable<FieldState> OrderedFields() =>
        OpenFields.OrderByDescending(f => f.Mods).ThenByDescending(f => f.TypeMods).ThenBy(f => f.Type, StringComparer.Ordinal).ThenBy(f => f.Field, StringComparer.Ordinal);

    /// <summary>The overloads an index row holds for a name: (arity, out parameters), fewest first. An
    /// index row of the one-overload shape, {kind, arity, out}, reads as that one.</summary>
    public static List<(long Arity, long Out)> Overloads(JsonNode? index) =>
        [.. (index?["overloads"] as JsonArray ?? (index is JsonObject { } one && one.ContainsKey("arity") ? [one.DeepClone()] : []))
            .OfType<JsonObject>().Select(o => (Num(o["arity"]) ?? -1, Num(o["out"]) ?? 0)).OrderBy(o => o.Item1)];

    /// <summary>An Osiris row with the verdict on its overloads: a call is matched to the overload of
    /// its arity, as Osiris matches it; every arity a report gives must be one of them, and one that
    /// is not is named beside those the index holds.</summary>
    public static JsonObject OsirisJudged(JsonObject row)
    {
        var copy = (JsonObject)row.DeepClone();
        copy.Remove("mod_slugs");
        var arities = (row["arities"] as JsonArray ?? []).Select(a => Num(a) ?? -1).ToList();
        var lua = (row["lua_arguments"] as JsonArray ?? []).Select(a => Num(a) ?? -1).ToList();
        string verdict;
        if (!row.ContainsKey("index")) verdict = "no index to ask";
        else if (row["index"] is not JsonObject index) verdict = LayoutFile.Truthy(row["database"]) ? "a story database, not in the header" : "not in the index";
        else
        {
            var overloads = Overloads(index);
            // Name/N counts every parameter; a quoted Osi.Name(...) passes only the in parameters
            var missing = arities.Where(a => !overloads.Any(o => o.Arity == a))
                .Concat(lua.Where(a => !overloads.Any(o => o.Arity - o.Out == a)))
                .Distinct().Order().ToList();
            verdict = missing.Count == 0 ? "index holds it"
                : $"index holds /{string.Join(", /", overloads.Select(o => o.Arity))}; no overload takes {string.Join(" or ", missing)} arguments";
        }
        copy["verdict"] = verdict;
        return copy;
    }

    public void Print(TextWriter output, int top)
    {
        var h = Headline();
        output.WriteLine($"{Mods} mods; {Types.Count} engine types they use");
        output.WriteLine($"  never seen alive        : {h["never_seen"]} ({h["never_seen_not_registered"]} of them not registered in coverage.json)");
        output.WriteLine($"  seen, not verified live : {h["seen_unverified"]}");
        output.WriteLine($"  no layout               : {h["without_layout"]}");
        output.WriteLine($"  layout with a field not named with evidence: {h["with_open_layout_fields"]}");
        output.WriteLine($"  a field the mods name without an evidence-backed name: {h["with_open_demanded_fields"]} types, {h["demanded_fields_open"]} of {h["demanded_fields"]} fields");
        if (HasSystems) output.WriteLine($"  no described system     : {h["no_described_system"]}; no described creator: {h["no_described_creator"]}");
        output.WriteLine();
        output.WriteLine($"top {top} by mods        mods  uses           seen ver  named/fields  open  systems");
        foreach (var t in Types.Take(top))
            output.WriteLine($"  {t.Type,-48} {t.Mods,3}  {Uses(t.Uses),-13}  {Yes(t.Seen),-4} {Yes(t.Verified),-4} {t.EvidenceFields,4}/{t.LayoutFields,-4}    {t.OpenDemandedFields,3}  {t.Systems}/{t.Creators}");
        output.WriteLine();
        output.WriteLine("(a) demanded types never seen alive:");
        foreach (var t in NeverSeen) output.WriteLine($"  {t.Mods,3}  {t.Type}{(t.Registered ? "" : "  (not registered)")}{(t.OneFrame ? "  (one frame)" : "")}");
        output.WriteLine();
        output.WriteLine("(b) demanded types seen, not verified live:");
        foreach (var t in Unverified) output.WriteLine($"  {t.Mods,3}  {t.Type}");
        output.WriteLine();
        output.WriteLine("(c) demanded fields with no evidence-backed name (mods using the field, then the type):");
        foreach (var f in OrderedFields())
            output.WriteLine($"  {f.Mods,3}/{f.TypeMods,-3} {f.Type}.{f.Field}  {f.State}{(f.Current is not null && !f.Current.Equals(f.Field, StringComparison.Ordinal) ? $" (now {f.Current})" : "")}");
        output.WriteLine();
        if (HasSystems)
        {
            output.WriteLine("(d) demanded types no described system names (reads, writes, uses):");
            foreach (var t in NoSystem) output.WriteLine($"  {t.Mods,3}  {t.Type}");
            output.WriteLine("    named by a system, but no described system writes or adds it:");
            foreach (var t in NoCreator.Where(t => t.Systems > 0)) output.WriteLine($"  {t.Mods,3}  {t.Type}");
        }
        else output.WriteLine("(d) no systems record (systems/**/*.json): nothing to say about creators and users");
        output.WriteLine();
        output.WriteLine("(e) Osiris functions and events by mods (calling/listening), and the index:");
        foreach (var o in Osiris.Take(top * 2).Select(OsirisJudged))
            output.WriteLine($"  {o["mods"],3}  {o["name"],-36} {o["called_by"]}/{o["listened_by"]}  {(o["index"] is JsonObject i ? "/" + string.Join(",", Overloads(i).Select(x => x.Arity)) : "-"),-10} {o["verdict"]}");
        output.WriteLine("    every name whose arity the index does not hold, or that it lacks:");
        foreach (var o in OsirisGaps())
            output.WriteLine($"  {o["mods"],3}  {o["name"],-36} {o["verdict"]}");
        output.WriteLine();
        output.WriteLine("Script Extender API by mods:");
        foreach (var a in Api.Take(top)) output.WriteLine($"  {a["mods"],3}  {a["name"]}{(a["in_se"] is JsonValue v && !v.GetValue<bool>() ? "  (not in the tree)" : "")}");
        output.WriteLine("events by mods: " + string.Join(", ", Events.Take(top).Select(e => $"{e["name"]} {e["mods"]}")));
        output.WriteLine("functors by mods: " + string.Join(", ", Functors.Take(top).Select(e => $"{e["name"]} {e["mods"]}")));
    }

    /// <summary>The fields still owed a name in the types the mods use: machine names and rungs
    /// unknown or extender, ordered by the mods naming the field, then the mods using the type.</summary>
    public static List<JsonObject> Worst(IReadOnlyList<JsonObject> rows, RecordState record, int top)
    {
        var list = new List<(JsonObject Row, int FieldMods, int TypeMods)>();
        foreach (var row in rows.Where(r => LayoutFile.Text(r["kind"]) == "component"))
        {
            int typeMods = (int)(Num(row["mods"]) ?? 0);
            if (typeMods == 0) continue;
            var type = LayoutFile.Text(row["type"]) ?? "";
            if (record.Types.GetValueOrDefault(type)?.Layout is not { } layout) continue;
            var demanded = (row["fields"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(f => (Name: LayoutFile.Text(f["name"]) ?? "", Mods: (int)(Num(f["mods"]) ?? 0))).ToList();
            foreach (var node in layout.Fields)
            {
                var name = LayoutFile.Text(node?["name"]) ?? "";
                var legacy = LayoutFile.Text(node?["legacy"]);
                var rung = LayoutFile.Text(node?["source"]) ?? "unknown";
                if (Tally.IsPadding(name)) continue;
                bool machine = Tally.IsMachine(name);
                if (!machine && rung is not ("unknown" or "extender")) continue;
                int fieldMods = demanded.Where(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                                                    || (legacy?.Equals(d.Name, StringComparison.OrdinalIgnoreCase) ?? false)).Sum(d => d.Mods);
                list.Add((new JsonObject
                {
                    ["type"] = type, ["field"] = name, ["offset"] = Num(node?["offset"]), ["rung"] = rung,
                    ["machine"] = machine, ["field_mods"] = fieldMods, ["type_mods"] = typeMods,
                }, fieldMods, typeMods));
            }
        }
        return [.. list.OrderByDescending(x => x.FieldMods).ThenByDescending(x => x.TypeMods)
            .ThenBy(x => (string)x.Row["type"]!, StringComparer.Ordinal).ThenBy(x => Num(x.Row["offset"]) ?? 0)
            .Take(top).Select(x => x.Row)];
    }
}
