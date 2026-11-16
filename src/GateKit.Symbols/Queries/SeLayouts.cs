using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GateKit.Symbols.Queries;

/// <summary>
/// Layout files, for review, of the types Script Extender declares that the record has no layout
/// for (`gatekit symbols se-layouts`, docs/contracts/cli.md, "symbols se-layouts"). They are written from the
/// extender's own declarations in the symbol database (se_types, se_fields): the offsets and types
/// are what its headers compute, the names are its author's.
///
/// <b>The rung is <c>extender</c> for every field with a real name</b> (the
/// extender's own <c>field_24</c> stays <c>unknown</c>, which is what it says): the name
/// is Script Extender's for the field at that offset, and nobody has checked it against the
/// game's code. It is the name the Lua reads (what the extender answers to, and what the in-tick
/// comparison and bg3_verify compare), while the rung tells the truth about what is known of the
/// game's own name. The status is <c>declared</c>: in the extender's build, not proven against
/// memory. Types whose only declaration is the header generated from this record (Coverage.h) are
/// not written: that would hand the record's own names back as if the extender had them.
/// </summary>
internal static partial class SeLayouts
{
    /// <summary>A name the machine made up (field_24, _Pad5, unk_3): the same test as the names record's tally.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^(field_|_Pad|unknown|unk_|pad\d|dummy)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex MachineName();

    static readonly JsonSerializerOptions Stored = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>C++ spellings the extender's headers use, in the spelling the layouts use.</summary>
    static readonly Dictionary<string, string> Spellings = new(StringComparer.Ordinal)
    {
        ["int"] = "int32_t", ["unsigned int"] = "uint32_t", ["int32"] = "int32_t", ["uint32"] = "uint32_t",
        ["int16"] = "int16_t", ["uint16"] = "uint16_t", ["int8"] = "int8_t", ["uint8"] = "uint8_t",
        ["int64"] = "int64_t", ["uint64"] = "uint64_t", ["short"] = "int16_t", ["unsigned short"] = "uint16_t",
        ["char"] = "int8_t", ["unsigned char"] = "uint8_t", ["__int64"] = "int64_t",
    };

    /// <summary>Every C++ spelling of a type in the layouts' spelling, inside template arguments too:
    /// <c>HashMap&lt;EntityHandle, int&gt;</c> becomes <c>HashMap&lt;EntityHandle, int32_t&gt;</c>.</summary>
    static string Spell(string type) =>
        Spellings.TryGetValue(type, out var whole) ? whole
            : System.Text.RegularExpressions.Regex.Replace(type, @"(?<![\w:])(?:unsigned (?:int|short|char)|__int64|u?int(?:8|16|32|64)|int|short|char)(?![\w:])",
                m => Spellings.GetValueOrDefault(m.Value, m.Value));

    public const string NamesNote = "Every name here is Script Extender's author's (his headers), not the game's own: they are kept "
        + "because they are what the extender's Lua answers to and what bg3_verify compares, and every field carries the rung "
        + "`extender` - the extender's name for the field at that offset, which nobody has checked against the game's code. "
        + "A name is raised from `extender` only with evidence from the code, by `gatekit names set`.";

    /// <summary>The engine names that already have a layout: a type of the registry beside the coverage
    /// record (one id, one file, whatever made it), or a layout file under its <c>layouts</c>.</summary>
    internal static HashSet<string> Existing(string coverage)
    {
        var have = new HashSet<string>(StringComparer.Ordinal);
        var store = Registry.RegistryStore.Of(coverage);
        if (Directory.Exists(Path.Combine(store.Root, Registry.RegistryStore.Folder(Registry.EntryKind.Type))))
            foreach (var entry in store.All().Where(e => e.Kind == Registry.EntryKind.Type)) have.Add(entry.Id);
        var layouts = Path.Combine(coverage, "layouts");
        if (!Directory.Exists(layouts)) return have;
        foreach (var path in Directory.EnumerateFiles(layouts, "*.json", SearchOption.AllDirectories))
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(path))?["component"] is JsonValue v && v.TryGetValue<string>(out var name)) have.Add(name);
            }
            catch (JsonException) { }
        }
        return have;
    }

    /// <summary>Writes a layout file for each type coverage.json calls mapped that has none; returns (written, without a declaration).</summary>
    public static (int Written, int Missing, int Skipped) Write(SymbolContext c, string coverage, string output, string? prefix)
    {
        var index = Path.Combine(coverage, "coverage.json");
        if (!File.Exists(index)) throw new SymbolsException($"no coverage index: {index}");
        var types = new Dictionary<string, (bool OneFrame, long Size)>(StringComparer.Ordinal);
        foreach (var row in JsonNode.Parse(File.ReadAllText(index))!["components"]!.AsArray())
        {
            if (row is not JsonObject component || component["name"]?.GetValue<string>() is not { } name) continue;
            if (component["mapped"] is not JsonValue m || !(m.TryGetValue<bool>(out var mapped) ? mapped : m.ToString() != "0")) continue;
            if (prefix is not null && !name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var oneFrame = component["oneFrame"] is JsonValue o && o.TryGetValue<bool>(out var of) && of;
            var size = component["totalSize"] is JsonValue s && s.TryGetValue<long>(out var sz) ? sz : -1;
            types.TryAdd(name, (oneFrame, size));
        }
        var have = Existing(coverage);
        var enums = c.Db.Query("SELECT name, namespace FROM enums").Select(r => PyText.Str(r[0])).ToHashSet(StringComparer.Ordinal);
        int written = 0, missing = 0, skipped = 0;
        Directory.CreateDirectory(output);
        foreach (var (name, (oneFrame, engineSize)) in types.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (have.Contains(name)) { skipped++; continue; }
            var t = c.Db.One("SELECT id, ext_name, kind, file, line, size FROM se_types WHERE engine_class = ? "
                + "AND kind <> 'struct' AND kind <> 'system' AND file NOT LIKE '%Coverage.h' ORDER BY id LIMIT 1", name);
            if (t is null) { missing++; continue; }
            var where = $"{PyText.Str(t[3]).Replace('\\', '/')}:{PyText.Str(t[4])}";
            var kind = PyText.Str(t[2]);
            var seSize = t[5] is null ? (long?)null : PyText.L(t[5]);
            var fields = new JsonArray();
            var open = new JsonArray();
            var unplaced = new List<string>();
            var hidden = new List<string>();
            // An offset is only written where it fits: inside the type and after the field before it.
            // The database takes an offset from a field's old name (field_24) where it cannot compute
            // one, and such a name can be stale: LootingState got +0x24 in a 16-byte type, and after
            // a SpellId (0x38) the members took offsets from names that assumed 0x28. From the first
            // field that does not fit, no offset is trusted (the names agent's two hand fixes).
            var typeSize = engineSize >= 0 ? engineSize : seSize ?? 0;
            long previousEnd = 0;
            var doubtful = new List<string>();
            foreach (var f in c.Db.Query("SELECT name, type, legacy, hidden, offset, size, checked FROM se_fields WHERE type_id = ? ORDER BY idx", t[0]).ToList())
            {
                var field = PyText.Str(f[0]);
                if (f[4] is not null && doubtful.Count == 0)
                {
                    var at = PyText.L(f[4]);
                    var width = f[5] is null ? 0 : PyText.L(f[5]);
                    if ((typeSize > 0 && at + Math.Max(width, 1) > typeSize) || at < previousEnd)
                        doubtful.Add($"{field} at +0x{at:X}" + (PyText.L(f[6] ?? 0L) == 2 ? " (taken from its old name)" : ""));
                    else previousEnd = at + width;
                }
                else if (doubtful.Count > 0 && !PyText.Truthy(f[3])) { doubtful.Add(field); continue; }
                if (doubtful.Count > 0 && !PyText.Truthy(f[3])) continue;
                if (PyText.Truthy(f[3])) { hidden.Add(field); continue; }
                if (f[4] is null) { unplaced.Add(field); continue; }
                var type = PyText.Str(f[1]).Trim();
                type = Spell(type);
                var offset = PyText.L(f[4]);
                var check = f[6] is null ? "" : PyText.L(f[6]) switch
                {
                    1 => "; the computed offset equals the one in its earlier name",
                    2 => "; the offset is taken from its earlier name",
                    _ => "",
                };
                var entry = new JsonObject
                {
                    ["name"] = field,
                    ["offset"] = offset,
                    ["type"] = type,
                    // the extender's field_24 is no name of anybody's: it keeps the rung that says so
                    ["source"] = MachineName().IsMatch(field) ? "unknown" : "extender",
                    ["evidence"] = $"Script Extender declares {name}::{field} at +0x{offset:X} ({where}): its author's name and type; "
                        + $"the offset is computed from the declared types{check}. Not checked against the game's code.",
                };
                if (PyText.Truthy(f[2])) entry["legacy"] = PyText.Str(f[2]);
                if (enums.Contains(type)) entry["evidence"] += $" {type} is an enumeration of the extender's.";
                fields.Add(entry);
            }
            if (unplaced.Count > 0) open.Add($"no computed offset (a type of unknown size comes before): {string.Join(", ", unplaced)}");
            if (doubtful.Count > 0) open.Add($"offsets that do not fit the type (outside it, or inside the field before), left out from the first: {string.Join(", ", doubtful)}");
            if (hidden.Count > 0) open.Add($"not exposed to Lua by the extender, left out: {string.Join(", ", hidden)}");
            var size = engineSize >= 0 ? engineSize : seSize ?? 0;
            if (seSize is { } declared && engineSize >= 0 && declared != engineSize)
                open.Add($"the extender's declaration is {declared} bytes, the game registers {engineSize}: the layout may be out of date");
            var file = new JsonObject
            {
                ["component"] = name,
                ["ext_name"] = PyText.Str(t[1]),
                ["size"] = size,
                ["oneFrame"] = oneFrame || kind.StartsWith("oneframe", StringComparison.Ordinal),
            };
            if (fields.Count == 0 && (kind.EndsWith("tag", StringComparison.Ordinal) || size <= 1)) file["tag"] = true;
            file["fields"] = fields;
            file["status"] = "declared";
            file["from"] = $"Script Extender's declarations ({where}, kind {kind}), by gatekit symbols se-layouts on "
                + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            file["names"] = NamesNote;
            if (open.Count > 0) file["open_questions"] = open;
            var path = Path.Combine(output, name.Replace("::", ".", StringComparison.Ordinal) + ".json");
            File.WriteAllText(path, file.ToJsonString(Stored).ReplaceLineEndings("\n") + "\n", new UTF8Encoding(false));
            written++;
        }
        return (written, missing, skipped);
    }
}
