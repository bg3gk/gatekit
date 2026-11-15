using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GateKit.Symbols.Decomp;

/// <summary>One field of a structure as the disassembler is told about it.</summary>
/// <param name="Offset">Offset in the structure.</param>
/// <param name="Size">Bytes it takes, never past the next field or the end of the structure.</param>
/// <param name="Kind">int, uint, float, bool, ptr or bytes: what the script builds the data type from.</param>
/// <param name="Name">The field's name in the record.</param>
/// <param name="Type">The type as the record writes it (a comment for the reader).</param>
internal sealed record RecordField(long Offset, int Size, string Kind, string Name, string Type);

/// <summary>A structure of the record: a component and the fields known of it.</summary>
internal sealed record RecordType(string Name, long Size, List<RecordField> Fields)
{
    /// <summary>The lines of the types file for this structure: name, size, then per field the
    /// offset (hex), size, kind, name and the record's type, tab-separated.</summary>
    public string Lines()
    {
        var b = new StringBuilder();
        foreach (var f in Fields)
            b.Append(Name).Append('\t').Append(Size.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(f.Offset.ToString("X", CultureInfo.InvariantCulture)).Append('\t').Append(f.Size.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(f.Kind).Append('\t').Append(f.Name).Append('\t').Append(f.Type).Append('\n');
        return b.ToString();
    }
}

/// <summary>The structures of the coverage record - the registry's types, or the layout files before
/// the registry held them - as a disassembler can take them: what `refresh` hands to ApplyTypes.java.</summary>
internal static partial class RecordTypes
{
    [GeneratedRegex(@"^(?:u?int8_t|char|unsigned char)\[(\d+)\]$")]
    private static partial Regex ByteArray();

    [GeneratedRegex(@"[^A-Za-z0-9_]")]
    private static partial Regex NotIdentifier();

    /// <summary>(kind, size) of the types whose shape is the same in every build; null for the
    /// rest, which are written as plain bytes up to the next field.</summary>
    internal static (string Kind, int Size)? Known(string type)
    {
        type = type.Trim();
        if (type.EndsWith('*'))
            return ("ptr", 8);
        var bytes = ByteArray().Match(type);
        if (bytes.Success && int.TryParse(bytes.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > 0)
            return ("bytes", n);
        return type switch
        {
            "bool" => ("bool", 1),
            "int8_t" or "char" => ("int", 1),
            "uint8_t" or "unsigned char" => ("uint", 1),
            "int16_t" => ("int", 2),
            "uint16_t" => ("uint", 2),
            "int32_t" or "int" => ("int", 4),
            "uint32_t" => ("uint", 4),
            "int64_t" => ("int", 8),
            "uint64_t" => ("uint", 8),
            "float" => ("float", 4),
            "double" => ("float", 8),
            "glm::vec2" => ("float", 8),
            "glm::vec3" => ("float", 12),
            "glm::vec4" or "glm::quat" => ("float", 16),
            // an index into the engine's string table, an entity's handle: integers the pseudo-code compares as such
            "FixedString" => ("uint", 4),
            "EntityHandle" => ("uint", 8),
            "Guid" => ("bytes", 16),
            _ => null,
        };
    }

    /// <summary>Whether a folder is a coverage record: it has the registry beside it (types in it), or the layout files.</summary>
    static bool IsCoverage(string folder) =>
        Directory.Exists(Path.Combine(Registry.RegistryStore.Of(folder).Root, "types")) || Directory.Exists(Path.Combine(folder, "layouts"));

    /// <summary>The coverage record: the one given, else GATEKIT_COVERAGE, else research/coverage of
    /// the repository the command runs in or from. Null when there is none.</summary>
    public static string? FindCoverage(string? given)
    {
        var set = given ?? Environment.GetEnvironmentVariable("GATEKIT_COVERAGE");
        if (!string.IsNullOrWhiteSpace(set))
            return IsCoverage(set) ? Path.GetFullPath(set) : throw new SymbolsException($"{set} is no coverage record: it has neither a registry beside it nor a layouts folder");
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "research", "coverage");
                if (Directory.Exists(candidate) && IsCoverage(candidate))
                    return candidate;
            }
        return null;
    }

    /// <summary>Every structure of the record that has at least one field, by name, whatever the
    /// rungs of its fields: the registry's types made from layouts when the registry beside the
    /// record holds types, else the layout files (a component written in two keeps the first in path
    /// order; a file that is not a layout is skipped).</summary>
    public static SortedDictionary<string, RecordType> Read(string coverage)
    {
        var types = new SortedDictionary<string, RecordType>(StringComparer.Ordinal);
        var store = Registry.RegistryStore.Of(coverage);
        if (Directory.Exists(Path.Combine(store.Root, "types")))
        {
            foreach (var entry in store.All().Where(e => e.Kind == Registry.EntryKind.Type && Registry.RegistryForms.IsLayout(e.Json)))
                if (FromJson(Registry.RegistryForms.ToLayout(entry.Json)) is { } type)
                    types.TryAdd(type.Name, type);
            return types;
        }
        var folder = Path.Combine(coverage, "layouts");
        foreach (var path in Directory.EnumerateFiles(folder, "*.json", SearchOption.AllDirectories)
                     .OrderBy(p => Path.GetRelativePath(folder, p).Replace('\\', '/'), StringComparer.Ordinal))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
                if (Parse(doc.RootElement) is { } type)
                    types.TryAdd(type.Name, type);
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                // not a layout this reader understands: the names command reports those
            }
        }
        return types;
    }

    /// <summary>The name of the one line that stands for a structure nothing is known of but its size.</summary>
    public const string SizeOnly = "_";

    /// <summary>The structures of <see cref="Read"/>, and for every component of the ledger
    /// (coverage.json, `components[].name` and `inlineSize`) that has no layout with a field, a
    /// structure of that size with nothing in it: a pointer to it still says which component the
    /// decompiler is looking at, and its bytes read `p->field_0x10`. In the types file such a
    /// structure is one line with a field of size 0 named <see cref="SizeOnly"/>.</summary>
    public static SortedDictionary<string, RecordType> ReadWithSizes(string coverage) => WithSizes(Read(coverage), coverage);

    /// <summary>The names of the components the coverage ledger lists with a size.</summary>
    public static IEnumerable<string> LedgerComponents(string coverage) => WithSizes(new SortedDictionary<string, RecordType>(StringComparer.Ordinal), coverage).Keys;

    /// <summary>A layout (or a layout-like object: component, size, fields of offset, name, type) as a structure, or null.</summary>
    internal static RecordType? FromJson(System.Text.Json.Nodes.JsonObject layout)
    {
        if (GateKit.Json.JsonText.Text(layout["component"]) is not { Length: > 0 } name || name.Contains('\t', StringComparison.Ordinal)) return null;
        static long? Integer(System.Text.Json.Nodes.JsonNode? node) =>
            node is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<long>(out var n) ? n : node is System.Text.Json.Nodes.JsonValue d && d.TryGetValue<double>(out var x) && x == Math.Floor(x) ? (long)x : null;
        long size = Integer(layout["size"]) is long given && given > 0 ? given : 0;
        var fields = (layout["fields"] as System.Text.Json.Nodes.JsonArray ?? []).OfType<System.Text.Json.Nodes.JsonObject>()
            .Select(f => (Offset: Integer(f["offset"]), Name: GateKit.Json.JsonText.Text(f["name"]), Type: GateKit.Json.JsonText.Text(f["type"]) ?? ""))
            .Where(f => f.Offset is >= 0 && f.Name is not null)
            .Select(f => (f.Offset!.Value, f.Name!, f.Type));
        return Build(name, size, fields);
    }

    /// <summary>
    /// The structures `decomp refresh` gives the project: those of the registry's applied set
    /// (<see cref="Registry.Applied"/>: a type on an applied rung or with applied fields, those fields
    /// only), and a structure of its size alone for every other component of the ledger.
    /// </summary>
    public static SortedDictionary<string, RecordType> FromApplied(IEnumerable<System.Text.Json.Nodes.JsonObject> applied, string? coverage)
    {
        var types = new SortedDictionary<string, RecordType>(StringComparer.Ordinal);
        foreach (var line in applied.Where(l => GateKit.Json.JsonText.Text(l["kind"]) == "type"))
        {
            var layout = new System.Text.Json.Nodes.JsonObject { ["component"] = GateKit.Json.JsonText.Text(line["id"]) };
            if (line["size"] is not null) layout["size"] = line["size"]!.DeepClone();
            layout["fields"] = line["fields"]?.DeepClone() ?? new System.Text.Json.Nodes.JsonArray();
            if (FromJson(layout) is { } type) types.TryAdd(type.Name, type);
        }
        return coverage is null ? types : WithSizes(types, coverage);
    }

    static SortedDictionary<string, RecordType> WithSizes(SortedDictionary<string, RecordType> types, string coverage)
    {
        var ledger = Path.Combine(coverage, "coverage.json");
        if (!File.Exists(ledger))
            return types;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(ledger));
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("components", out var list) || list.ValueKind != JsonValueKind.Array)
                return types;
            foreach (var c in list.EnumerateArray())
            {
                if (c.ValueKind != JsonValueKind.Object || !c.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String
                    || !c.TryGetProperty("inlineSize", out var s) || s.ValueKind != JsonValueKind.Number || !s.TryGetInt64(out long size) || size <= 0 || size > 0x100000)
                    continue;
                string name = n.GetString()!;
                if (name.Length == 0 || name.Contains('\t', StringComparison.Ordinal) || types.ContainsKey(name))
                    continue;
                types[name] = new RecordType(name, size, [new RecordField(0, 0, "bytes", SizeOnly, "size only")]);
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            // a ledger this reader does not understand: the layouts stand alone
        }
        return types;
    }

    /// <summary>A layout as a structure, or null when it names no component or no field.</summary>
    internal static RecordType? Parse(JsonElement layout)
    {
        if (layout.ValueKind != JsonValueKind.Object || !layout.TryGetProperty("component", out var c) || c.ValueKind != JsonValueKind.String
            || !layout.TryGetProperty("fields", out var list) || list.ValueKind != JsonValueKind.Array)
            return null;
        string name = c.GetString()!;
        if (name.Length == 0 || name.Contains('\t', StringComparison.Ordinal))
            return null;
        long size = layout.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out long given) && given > 0 ? given : 0;
        var read = new List<(long, string, string)>();
        foreach (var f in list.EnumerateArray())
        {
            if (f.ValueKind != JsonValueKind.Object || !f.TryGetProperty("offset", out var o) || o.ValueKind != JsonValueKind.Number
                || !o.TryGetInt64(out long offset) || offset < 0 || !f.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String)
                continue;
            read.Add((offset, n.GetString()!, f.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : ""));
        }
        return Build(name, size, read);
    }

    /// <summary>A structure of its fields (offset, name, the record's type): each field as big as
    /// its type when it is one of <see cref="Known"/> and fits, else bytes up to the next; null with no field.</summary>
    static RecordType? Build(string name, long size, IEnumerable<(long Offset, string Name, string Type)> given)
    {
        var raw = new SortedDictionary<long, (string Name, string Type)>();
        foreach (var (offset, fieldName, fieldType) in given)
        {
            string field = NotIdentifier().Replace(fieldName, "_");
            if (field.Length == 0)
                continue;
            raw.TryAdd(offset, (field, fieldType.Replace('\t', ' ').Replace('\n', ' ')));
        }
        if (raw.Count == 0)
            return null;
        var offsets = raw.Keys.ToList();
        var fields = new List<RecordField>(raw.Count);
        for (int i = 0; i < offsets.Count; i++)
        {
            long offset = offsets[i];
            var (field, type) = raw[offset];
            // room up to the next field, or to the end of the structure when its size is known
            long room = i + 1 < offsets.Count ? offsets[i + 1] - offset : size > offset ? size - offset : long.MaxValue;
            var known = Known(type);
            if (known is { } k && k.Size <= room)
                fields.Add(new RecordField(offset, k.Size, k.Kind, field, type));
            else
                // an unknown shape, or one the record gives less room than it needs: bytes, and the name still stands
                fields.Add(new RecordField(offset, (int)Math.Min(room == long.MaxValue ? 1 : room, 0x10000), "bytes", field, type));
        }
        var last = fields[^1];
        return new RecordType(name, Math.Max(size, last.Offset + last.Size), fields);
    }

    /// <summary>The types file of a set of structures (the whole record, or the ones that changed).</summary>
    public static string Render(IEnumerable<RecordType> types) => string.Concat(types.Select(t => t.Lines()));

    /// <summary>A types file split back into the lines of each structure, for comparing two passes.</summary>
    public static Dictionary<string, string> Split(string text)
    {
        var blocks = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            int tab = line.IndexOf('\t');
            if (tab <= 0)
                continue;
            var name = line[..tab];
            if (!blocks.TryGetValue(name, out var b))
                blocks[name] = b = new StringBuilder();
            b.Append(line).Append('\n');
        }
        return blocks.ToDictionary(p => p.Key, p => p.Value.ToString(), StringComparer.Ordinal);
    }
}
