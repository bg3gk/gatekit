using System.Text.Json.Nodes;
using GateKit.Json;
using GateKit.Symbols.Decomp;

namespace GateKit.Symbols.Registry;

/// <summary>
/// `registry import`: the earlier stores carried into the registry - the rows of
/// <c>functions.jsonl</c> into <c>functions/</c> and the build tables, the rows of
/// <c>structs.jsonl</c> and the layouts of <c>layouts/**</c> into <c>types/</c>. It can be run
/// again: an entry that is already what the store says is left alone, one that differs is refused
/// unless <c>--replace</c>. After writing, every entry is read back and turned into the store's
/// form again, and the two are compared: the counts and that comparison are the proof that nothing
/// was lost.
/// </summary>
public static class RegistryImport
{
    public sealed record Counts(int Read, int Written, int Unchanged, int Refused, int RoundTrip)
    {
        public override string ToString() => $"{Read} read, {Written} written, {Unchanged} unchanged, {Refused} refused; round trip {RoundTrip} of {Read} equal";
    }

    static bool Same(JsonNode? a, JsonNode? b) => JsonNode.DeepEquals(Normal(a), Normal(b));

    /// <summary>A copy with the members of every object sorted, so that the order of keys does not count.</summary>
    static JsonNode? Normal(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, Normal(p.Value)))),
        JsonArray a => new JsonArray([.. a.Select(Normal)]),
        null => null,
        _ => node.DeepClone(),
    };

    /// <summary>Writes an entry unless the registry already holds exactly it; true when written, false when unchanged.</summary>
    static bool Put(RegistryStore store, EntryKind kind, JsonObject entry, bool replace, bool dry)
    {
        var id = JsonText.Text(entry["id"])!;
        var existing = store.Get(id);
        if (existing is not null)
        {
            var was = (JsonObject)existing.Json.DeepClone();
            was.Remove("schema");
            if (Same(was, entry)) return false;
            if (!replace) throw new RegistryException($"{id}: {store.Relative(existing.Path)} differs from what the store says; --replace writes the store's");
        }
        if (!dry) store.Write(kind, (JsonObject)entry.DeepClone(), create: false);
        return true;
    }

    /// <summary>The rows of functions.jsonl into function entries and build tables.</summary>
    internal static Counts Functions(RegistryStore store, IReadOnlyList<FunctionNames.Row> rows, Func<string, long, string?> signature,
        Func<string, string?> sha256, bool replace, bool dry, TextWriter output)
    {
        int written = 0, unchanged = 0, refused = 0;
        var places = new Dictionary<(string Build, string Binary), List<BuildRow>>();
        foreach (var row in rows)
        {
            var (entry, place, build) = RegistryForms.FromFunctionRow(row, signature);
            try
            {
                if (Put(store, EntryKind.Function, entry, replace, dry)) written++; else unchanged++;
                foreach (var dropped in row.Legacy)
                    output.WriteLine($"  {row.Name}: the earlier name {dropped} is our own and is not kept (no source but ours gives it)");
                foreach (var v in (entry["parameters"] as JsonArray ?? []).Concat(entry["locals"] as JsonArray ?? []).OfType<JsonObject>()
                             .Where(v => JsonText.Text(v["storage"]) == "unresolved"))
                    output.WriteLine($"  {JsonText.Text(entry["id"])}: {JsonText.Text(v["name"])} is known only by the decompiler's temporary {JsonText.Text(v["decompiler_name"])}; its storage is to be read");
                if (place is not null && build is not null)
                {
                    if (!places.TryGetValue((build, row.Binary), out var list)) places[(build, row.Binary)] = list = [];
                    list.Add(place);
                }
            }
            catch (RegistryException e)
            {
                refused++;
                output.WriteLine("  refused: " + e.Message);
            }
        }
        if (!dry)
            foreach (var ((build, binary), list) in places)
            {
                var table = store.Table(build, binary);
                table.RemoveAll(r => list.Any(l => l.Id == r.Id));
                store.WriteTable(build, binary, [.. table, .. list]);
                store.RecordBinary(build, binary, sha256(binary));
            }
        store.Refresh();
        var back = dry ? 0 : rows.Count(row =>
        {
            var id = RegistryId.Placed(row.Name);
            var entry = store.Get(id);
            var place = row.Build is null ? null : store.Table(row.Build, row.Binary).FirstOrDefault(r => r.Id == id);
            if (entry is null || (row.Build is not null && place is null)) return false;
            var again = RegistryForms.ToFunctionRow(entry.Json, row.Binary, row.Build, place ?? new BuildRow(id, row.Rva, null, null), signature(row.Binary, row.Rva));
            return FunctionNames.Line(again) == FunctionNames.Line(row with { Legacy = [] });
        });
        return new Counts(rows.Count, written, unchanged, refused, back);
    }

    /// <summary>The rows of structs.jsonl into type entries, one a structure.</summary>
    internal static Counts Structs(RegistryStore store, IReadOnlyList<FunctionNames.Field> fields, bool replace, bool dry, TextWriter output)
    {
        int written = 0, unchanged = 0, refused = 0;
        foreach (var group in fields.GroupBy(f => f.Struct).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var existing = store.Get(group.Key);
            if (existing is not null && RegistryForms.IsLayout(existing.Json))
            {
                refused += group.Count();
                output.WriteLine($"  refused: {group.Key} is a layout's type; its fields are named with `gatekit names set`");
                continue;
            }
            var entry = new JsonObject
            {
                ["id"] = group.Key, ["kind"] = "struct",
                ["fields"] = new JsonArray([.. group.OrderBy(f => f.Offset).Select(f => (JsonNode)RegistryForms.FromStructField(f))]),
            };
            try
            {
                if (Put(store, EntryKind.Type, entry, replace, dry)) written += group.Count(); else unchanged += group.Count();
            }
            catch (RegistryException e)
            {
                refused += group.Count();
                output.WriteLine("  refused: " + e.Message);
            }
        }
        store.Refresh();
        var back = dry ? 0 : fields.Count(f =>
            store.Get(f.Struct) is { } entry && (entry.Json["fields"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(x => RegistryForms.ToStructField(f.Struct, x)).Contains(f));
        return new Counts(fields.Count, written, unchanged, refused, back);
    }

    /// <summary>The layouts under <c>layouts/</c> into type entries.</summary>
    public static Counts Layouts(RegistryStore store, string coverage, bool replace, bool dry, TextWriter output)
    {
        var folder = Path.Combine(coverage, "layouts");
        var files = Directory.EnumerateFiles(folder, "*.json", SearchOption.AllDirectories)
            .OrderBy(p => Path.GetRelativePath(folder, p).Replace('\\', '/'), StringComparer.Ordinal).ToList();
        int written = 0, unchanged = 0, refused = 0;
        var read = new List<(JsonObject Layout, string Batch)>();
        foreach (var path in files)
        {
            var layout = JsonText.Read(path, "record/layout/1").AsObject();
            var batch = Path.GetRelativePath(folder, Path.GetDirectoryName(path)!).Replace('\\', '/');
            read.Add((layout, batch));
            try
            {
                if (Put(store, EntryKind.Type, RegistryForms.FromLayout(layout, batch), replace, dry)) written++; else unchanged++;
            }
            catch (RegistryException e)
            {
                refused++;
                output.WriteLine($"  refused: {Path.GetRelativePath(coverage, path)}: {e.Message}");
            }
        }
        store.Refresh();
        var back = dry ? 0 : read.Count(r => store.Get(JsonText.Text(r.Layout["component"])!) is { } entry
            && Same(RegistryForms.ToLayout(entry.Json), r.Layout) && RegistryForms.BatchOf(entry.Json) == r.Batch);
        return new Counts(files.Count, written, unchanged, refused, back);
    }
}
