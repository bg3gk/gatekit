using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GateKit.Json;
using GateKit.Symbols.Registry;

namespace GateKit.Symbols.Queries;

internal static partial class SymbolText
{
    /// <summary>
    /// `symbols meta-layouts`: how much of the layouts verified in the game the engine's own metadata
    /// gives by itself. For every type of the registry made from a layout whose survey status is
    /// `confirmed` (its offsets read back in a running game), the layout derived from the metadata
    /// alone - the facts <see cref="FieldFacts"/> reads (ls::FieldMeta strings of the executable with
    /// the macOS build's names, the Toolkit's accessors matched to the game) that settle a field by
    /// themselves - is held against the registry's fields: whether the metadata places a field at its
    /// offset, whether it names it (against the names on an applied rung, and apart from them against
    /// Script Extender's), whether the sizes of the two types agree where both are known, and where
    /// the metadata places a field the layout does not have.
    /// </summary>
    public static void MetaLayouts(SymbolContext c, string? ns, int top)
    {
        var store = RegistryStore.Find(null) ?? throw new SymbolsException("no registry found (GATEKIT_REGISTRY, or a checkout's registry/)");
        // the classes the metadata places anything in: FieldMeta strings and the Toolkit's accessors, read once
        var placed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in c.Db.Query("SELECT DISTINCT text FROM strings WHERE text LIKE '%FieldMeta<%'").ToList())
            foreach (Match m in FieldMeta().Matches(PyText.Str(r[0]))) placed.Add(m.Groups[2].Value);
        foreach (var r in c.Db.Query("SELECT DISTINCT class FROM accessors").ToList()) placed.Add(PyText.Str(r[0]));

        int types = 0, withMeta = 0, fields = 0, offsetRight = 0, named = 0, nameRight = 0, nameWrong = 0, extNamed = 0, extRight = 0,
            sized = 0, sizeRight = 0, extra = 0, verifiedNames = 0, verifiedNamedByMeta = 0, macTypes = 0, macFields = 0, macOnLayout = 0;
        var macFile = c.Env.MacosSymbols == null ? null : Path.Combine(c.Env.MacosSymbols, "fieldmeta.json");
        var perType = new List<(string Id, int Fields, int Offsets, int Names, int Sizes, int SizeRight)>();
        var wrong = new List<string>();
        foreach (var entry in store.All().Where(e => e.Kind == EntryKind.Type).OrderBy(e => e.Id, StringComparer.Ordinal))
        {
            var json = entry.Json;
            if (JsonText.Text(json["survey"]?["status"]) != "confirmed" || JsonText.Text(json["concept"]?["kind"]) == "system") continue;
            if (ns is not null && !entry.Id.StartsWith(ns, StringComparison.Ordinal)) continue;
            var layout = (json["fields"] as JsonArray ?? []).OfType<JsonObject>()
                .Where(f => f["offset"] is JsonValue && JsonText.Text(f["name"]) is { } n && !n.StartsWith("_Pad", StringComparison.OrdinalIgnoreCase))
                .Select(f => (Offset: f["offset"]!.GetValue<long>(), Name: JsonText.Text(f["name"])!, Type: JsonText.Text(f["type"]), Rung: JsonText.Text(f["rung"])))
                .ToList();
            if (layout.Count == 0) continue;
            types++;
            fields += layout.Count;
            var macNames = MacFields(macFile, entry.Id);
            if (macNames.Count > 0) { macTypes++; macFields += macNames.Count; macOnLayout += macNames.Count(m => layout.Any(f => Plain(f.Name) == Plain(m.Field))); }
            var applied = layout.Where(f => Rungs.IsApplied(f.Rung)).ToList();
            verifiedNames += applied.Count;
            if (!placed.Contains(entry.Id)) { perType.Add((entry.Id, layout.Count, 0, 0, 0, 0)); continue; }
            var facts = FieldFacts(c.Db, c.Env, entry.Id, null).Where(f => f.Rung is "game" or "toolkit" && f.Offset is not null).ToList();
            if (facts.Count == 0) { perType.Add((entry.Id, layout.Count, 0, 0, 0, 0)); continue; }
            withMeta++;
            var settled = facts.Where(f => f.Certain).GroupBy(f => f.Offset!.Value).ToDictionary(g => g.Key, g => g.First());
            var at = facts.Select(f => f.Offset!.Value).ToHashSet();
            int offsets = 0, names = 0, sizes = 0, sizesRight = 0;
            foreach (var field in layout)
            {
                if (at.Contains(field.Offset)) { offsetRight++; offsets++; }
                var fact = settled.GetValueOrDefault(field.Offset) ?? facts.FirstOrDefault(f => f.Offset == field.Offset);
                if (fact is not null && SizeOf(fact.Type) is { } metaSize && SizeOf(field.Type) is { } layoutSize)
                {
                    sized++; sizes++;
                    if (metaSize == layoutSize) { sizeRight++; sizesRight++; }
                    else wrong.Add($"{entry.Id} +0x{field.Offset:X}: size {metaSize} from {fact.Type}, the layout's {field.Type} is {layoutSize}");
                }
                if (settled.GetValueOrDefault(field.Offset) is not { } named1) continue;
                named++; names++;
                var same = Plain(named1.Name) == Plain(field.Name);
                if (Rungs.IsApplied(field.Rung))
                {
                    verifiedNamedByMeta++;
                    if (same) nameRight++;
                    else { nameWrong++; wrong.Add($"{entry.Id} +0x{field.Offset:X}: the metadata names {named1.Name}, the layout's {field.Rung} name is {field.Name}"); }
                }
                else if (field.Rung == "extender") { extNamed++; if (same) extRight++; }
            }
            foreach (var (offset, fact) in settled)
                if (!layout.Any(f => f.Offset == offset)) { extra++; wrong.Add($"{entry.Id} +0x{offset:X}: the metadata places {fact.Name} where the layout starts no field"); }
            perType.Add((entry.Id, layout.Count, offsets, names, sizes, sizesRight));
        }

        static string Share(int part, int whole) => whole == 0 ? "-" : (100.0 * part / whole).ToString("0.0", CultureInfo.InvariantCulture) + " %";
        c.WriteLine($"layouts confirmed in the game: {types} types, {fields} fields (padding apart); the metadata places something in {withMeta} of the types");
        c.WriteLine($"offset given by the metadata: {offsetRight} of {fields} fields ({Share(offsetRight, fields)})");
        c.WriteLine($"name settled by the metadata alone: {named} of {fields} fields ({Share(named, fields)})");
        c.WriteLine($"  against names on an applied rung: {verifiedNamedByMeta} of {verifiedNames} named by it ({Share(verifiedNamedByMeta, verifiedNames)}), {nameRight} the same, {nameWrong} different (the game's m_ written or not)");
        c.WriteLine($"  against Script Extender's names (rung extender): {extRight} of {extNamed} the same");
        c.WriteLine($"size of the type, both known: {sizeRight} of {sized} the same ({Share(sizeRight, sized)})");
        c.WriteLine($"offset, name and size all from the metadata and right: {perType.Sum(t => Math.Min(t.Names, t.SizeRight))} at most (per type the lesser of named and sized right)");
        c.WriteLine($"fields the metadata places where the layout starts none: {extra}");
        c.WriteLine($"names without an offset (the macOS build's fieldmeta.json): {macTypes} of the types, {macFields} names, {macOnLayout} of them the name of a field of the layout");
        c.WriteLine($"the types with most fields the metadata places (fields, offsets, names, sizes compared, sizes right):");
        foreach (var t in perType.Where(t => t.Offsets > 0).OrderByDescending(t => t.Offsets).ThenBy(t => t.Id, StringComparer.Ordinal).Take(top))
            c.WriteLine($"  {t.Id}  {t.Fields} {t.Offsets} {t.Names} {t.Sizes} {t.SizeRight}");
        c.WriteLine($"where the metadata and the layout differ ({wrong.Count}; the first {Math.Min(top, wrong.Count)}):");
        foreach (var line in wrong.Take(top)) c.WriteLine("  " + line);
    }

    /// <summary>A name without the game's member prefix, for comparing the macOS build's names with the layouts'.</summary>
    static string Plain(string name) => (name.StartsWith("m_", StringComparison.Ordinal) ? name[2..] : name).ToLowerInvariant();

    /// <summary>The size of a type where its spelling says it (the primitives and the engine's few fixed
    /// ones, as the game's Windows build lays them out); null when it does not.</summary>
    static int? SizeOf(string? type)
    {
        if (type is null) return null;
        var t = Regex.Replace(type, @"\bconst\b|\s+", " ").Trim();
        if (t.EndsWith('*')) return 8;
        var head = t.Split('<')[0].Trim();
        return head switch
        {
            "bool" or "char" or "signed char" or "unsigned char" or "int8_t" or "uint8_t" or "int8" or "uint8" => 1,
            "short" or "unsigned short" or "int16_t" or "uint16_t" or "int16" or "uint16" => 2,
            "int" or "unsigned int" or "int32_t" or "uint32_t" or "int32" or "uint32" or "float" or "FixedString" or "ls::FixedString" => 4,
            "long long" or "unsigned long long" or "int64_t" or "uint64_t" or "int64" or "uint64" or "double" or "EntityHandle" or "ls::EntityHandle" or "ecs::EntityRef" => 8,
            "ls::ID" when t.Contains("EntityHandleTraits", StringComparison.Ordinal) => 8,
            "Guid" or "ls::Guid" or "Version" => 16,
            "Array" or "ls::DynamicArray" or "ls::Array" or "DynamicArray" => 16,
            "glm::vec3" or "vec3" or "ls::Vector3" => 12,
            "glm::vec2" or "vec2" => 8,
            "glm::quat" or "quat" or "glm::vec4" or "vec4" => 16,
            _ => null,
        };
    }
}
