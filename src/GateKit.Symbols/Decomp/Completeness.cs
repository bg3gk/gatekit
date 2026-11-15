using System.Globalization;
using System.Text.Json.Nodes;
using GateKit.Json;
using GateKit.Symbols.Registry;

namespace GateKit.Symbols.Decomp;

/// <summary>How far the reading of one kind of thing has got: how many are known to exist, how many
/// the registry describes, the two states of the unknown, and the registry's count by rung.</summary>
internal sealed class KindTally(string kind)
{
    public string Kind { get; } = kind;
    /// <summary>Known to exist from the databases (null when no database lists them), the registry's own included.</summary>
    public long? Known;
    public long InRegistry, Described, LookedAt, Unlooked;
    public SortedDictionary<string, long> ByRung { get; } = new(Comparer<string>.Create((a, b) => Rungs.Rank(a).CompareTo(Rungs.Rank(b)) is var c && c != 0 ? c : string.CompareOrdinal(a, b)));
    public string? Note;

    /// <summary>Known and never looked at: not in the registry at all, or in it as never looked at.</summary>
    public long? NeverLookedAt => Known is long k ? Math.Max(0, k - InRegistry) + Unlooked : null;

    public void Count(string? state, string? rung, bool described)
    {
        InRegistry++;
        if (described) Described++;
        else if (state == "looked_at") LookedAt++;
        else if (state == "unlooked") Unlooked++;
        var r = rung ?? "no rung";
        ByRung[r] = ByRung.GetValueOrDefault(r) + 1;
    }

    public JsonObject ToJson()
    {
        var o = new JsonObject { ["known"] = Known, ["in_registry"] = InRegistry, ["described"] = Described, ["looked_at"] = LookedAt, ["never_looked_at"] = NeverLookedAt };
        var rungs = new JsonObject();
        foreach (var (rung, n) in ByRung) rungs[rung] = n;
        o["by_rung"] = rungs;
        if (Note is not null) o["note"] = Note;
        return o;
    }

    static string N(long? v) => v is long n ? n.ToString("N0", CultureInfo.InvariantCulture) : "not counted";

    public string Line()
    {
        var known = Known is long k ? $"{N(k)} known: " : "";
        var share = Known is long all && all > 0 ? string.Create(CultureInfo.InvariantCulture, $" ({100.0 * Described / all:F2} %)") : "";
        return $"  {Kind,-13}{known}{N(InRegistry)} in the registry, {N(Described)} described{share}, {N(LookedAt)} looked at and not understood, "
               + (NeverLookedAt is null ? "never looked at not counted" : $"{N(NeverLookedAt)} never looked at") + (ByRung.Count > 0 ? "; by rung " + string.Join(", ", ByRung.Select(p => $"{p.Key} {N(p.Value)}")) : "")
               + (Note is null ? "" : $"  ({Note})");
    }
}

/// <summary>
/// Completeness as `decomp tally` counts it: for functions (per binary, through the build's table),
/// types, fields, globals and enumerations, what the registry describes against what the databases
/// know exists, with "looked at, not understood" apart from "never looked at", and by rung.
/// </summary>
internal static class Completeness
{
    static readonly System.Text.RegularExpressions.Regex Placeholder = new(@"^(?:field\d*_|field_|_Pad|_Base|_\d|unk|padding)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    static bool Described(JsonObject entry, string sentence) =>
        JsonText.Text(entry["state"]) == "described" || (JsonText.Text(entry["state"]) is null && JsonText.Text(entry[sentence]) is { Length: > 0 });

    /// <summary>The functions of one binary: <paramref name="known"/> of them in its database; the
    /// registry's are those of its table (<paramref name="placed"/> ids).</summary>
    public static KindTally Functions(IReadOnlyList<RegistryEntry> entries, ISet<string> placed, long? known)
    {
        var t = new KindTally("functions") { Known = known };
        foreach (var e in entries.Where(e => e.Kind == EntryKind.Function && placed.Contains(e.Id)))
            t.Count(JsonText.Text(e.Json["state"]), JsonText.Text(e.Json["rung"]), Described(e.Json, "does"));
        if (known is null) t.Note = "no pseudo-code database of the binary";
        return t;
    }

    /// <summary>Types, fields, globals and enumerations; <paramref name="knownTypes"/> and
    /// <paramref name="knownEnums"/> are the names the databases declare (null when there is none).</summary>
    public static List<KindTally> Others(IReadOnlyList<RegistryEntry> entries, ISet<string>? knownTypes, ISet<string>? knownEnums)
    {
        var types = new KindTally("types");
        var fields = new KindTally("fields") { Note = "known: the fields the registry lists; described: named, not a placeholder, on a rung above unknown" };
        var globals = new KindTally("globals") { Note = "known not counted: no database lists the game's globals" };
        var enums = new KindTally("enumerations");
        var typeIds = new HashSet<string>(StringComparer.Ordinal);
        var enumIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            var json = e.Json;
            switch (e.Kind)
            {
                case EntryKind.Global:
                    globals.Count(JsonText.Text(json["state"]), JsonText.Text(json["rung"]), Described(json, "about"));
                    break;
                case EntryKind.Type:
                    var isEnum = JsonText.Text(json["kind"]) is "enum" or "bitfield";
                    var fieldList = (json["fields"] as JsonArray ?? []).OfType<JsonObject>().ToList();
                    // a type is described when it says what it is, or names a field that is not a placeholder
                    var named = fieldList.Any(f => JsonText.Text(f["name"]) is { } n && !Placeholder.IsMatch(n));
                    (isEnum ? enums : types).Count(JsonText.Text(json["state"]), JsonText.Text(json["rung"]),
                        Described(json, "about") || named || (isEnum && (json["values"] as JsonArray)?.Count > 0));
                    (isEnum ? enumIds : typeIds).Add(e.Id);
                    foreach (var f in fieldList)
                    {
                        var rung = JsonText.Text(f["rung"]) ?? JsonText.Text(json["rung"]);
                        var name = JsonText.Text(f["name"]) ?? "";
                        fields.Count(JsonText.Text(f["state"]), rung, !Placeholder.IsMatch(name) && rung is not (null or "unknown"));
                    }
                    break;
            }
        }
        fields.Known = fields.InRegistry;
        types.Known = knownTypes is null ? null : knownTypes.Union(typeIds, StringComparer.Ordinal).LongCount();
        enums.Known = knownEnums is null ? null : knownEnums.Union(enumIds, StringComparer.Ordinal).LongCount();
        if (knownTypes is null) types.Note = "known not counted: no symbol database";
        if (knownEnums is null) enums.Note = "known not counted: no symbol database";
        return [types, fields, globals, enums];
    }

    /// <summary>The type and enumeration names the symbol database declares (Script Extender's
    /// headers) and the components of the coverage ledger, or null for a missing source.</summary>
    public static (ISet<string>? Types, ISet<string>? Enums) Known(DecompSession s, string? coverage)
    {
        HashSet<string>? types = null, enums = null;
        if (s.Symbols is { } symbols)
        {
            try
            {
                types = [.. symbols.Query("SELECT coalesce(engine_class, name) FROM se_types").Select(r => Py.Str(r[0])).Where(n => n.Length > 0)];
                enums = [.. symbols.Query("SELECT CASE WHEN namespace IS NULL OR namespace = '' THEN name ELSE namespace || '::' || name END FROM enums").Select(r => Py.Str(r[0])).Where(n => n.Length > 0)];
            }
            catch (Microsoft.Data.Sqlite.SqliteException) { types = null; enums = null; }
        }
        if (coverage is not null && types is not null)
            foreach (var t in RecordTypes.LedgerComponents(coverage)) types.Add(t);
        return (types, enums);
    }
}
