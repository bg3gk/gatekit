using System.Text.Json.Nodes;
using GateKit.Json;
using Microsoft.Data.Sqlite;

namespace GateKit.Symbols.Registry;

/// <summary>A name a store holds for a thing, and what ties it to the thing.</summary>
/// <param name="Kind">The kind of evidence it is: <c>toolkit-name</c> or <c>game-name</c>.</param>
/// <param name="Store">The store: a Toolkit binary (EoCShared.dll), <c>bg3_macos_x64</c>, or <c>type-names</c> (the type names the executable keeps).</param>
/// <param name="Name">The name as the store writes it, without a signature.</param>
/// <param name="Tie"><c>match</c> (the symbol database's match of the export to the function), <c>pair</c> (a stored pair of the two builds) or <c>name</c> (the name alone).</param>
/// <param name="How">What made the tie, as the store records it (strings, calls, a seed), or null.</param>
public sealed record StoreName(string Kind, string Store, string Name, string Tie, string? How = null);

/// <summary>The symbol stores, asked for the names they hold for one thing of the registry.</summary>
public interface INameStores
{
    /// <summary>Every name the stores hold for the thing: under the name given, and - for a function
    /// with places - whatever the stores' own ties (a match, a pair) put at those addresses.</summary>
    IReadOnlyList<StoreName> Of(EntryKind kind, string id, IReadOnlyList<(string Binary, long Rva)> places);

    /// <summary>Whether an address of a binary lies in a function the stores know.</summary>
    bool InCode(string binary, long rva) => false;
}

/// <summary>
/// Where a name comes from, found by the tool and written as evidence (registry/README.md, "The
/// source of a name"): the Toolkit's exports and their matches to the executables in the symbol
/// database, the macOS build's names and the stored pairs in the pseudo-code database, the type
/// names the executable keeps.
/// </summary>
public static class NameSources
{
    /// <summary>The name of an id as a store would hold it: no piece, no overload's parameter list.</summary>
    public static string? Bare(string id)
    {
        var parts = RegistryId.Parse(id);
        if (parts.Piece is not null || parts.Member is not null || RegistryId.IsUnnamed(parts.Entity)) return null;
        var entity = parts.Entity;
        if (entity.EndsWith(')') && !parts.Scope[^1].StartsWith("operator", StringComparison.Ordinal))
        {
            var depth = 0;
            for (var i = entity.Length - 1; i >= 0; i--)
            {
                if (entity[i] == ')') depth++;
                else if (entity[i] == '(' && --depth == 0) return entity[..i];
            }
        }
        return entity;
    }

    /// <summary>Whose the name of an entry is, read from its evidence: the game's, the Toolkit's, or ours.</summary>
    public static string Whose(JsonObject entry)
    {
        var kinds = (entry["evidence"] as JsonArray ?? []).OfType<JsonObject>().Select(e => JsonText.Text(e["kind"])).ToList();
        return kinds.Contains("game-name") ? "the game's" : kinds.Contains("toolkit-name") ? "the Toolkit's" : "ours";
    }

    static string Sentence(StoreName found, bool code)
    {
        var holds = found.Store == "type-names" ? "The executable keeps this type's name as text"
            : found.Kind == "game-name" ? "The macOS build's symbols hold this name"
            : $"{found.Store} exports this name";
        if (!code) return holds;
        return holds + "; " + found.Tie switch
        {
            "match" => "the symbol database matches the export to this function" + (found.How is null ? "" : $" by {found.How}"),
            "pair" => "a stored pair of the two builds joins that function to this one" + (found.How is null ? "" : $" ({found.How})"),
            _ => "nothing but the name ties it to this function",
        };
    }

    static int TieRank(string tie) => tie is "match" or "pair" ? 0 : 1;

    /// <summary>
    /// Writes what the stores hold into an entry: one item of evidence a kind for the id's own name
    /// (the tool's item, marked by its <c>store</c>, is replaced when the stores say otherwise; an item
    /// of that kind written by hand is left to speak alone), and under <c>known_as</c> a different name
    /// a store ties to the function. Each new finding is a line of the journal.
    /// </summary>
    public static bool Apply(JsonObject entry, EntryKind kind, IReadOnlyList<StoreName> found, Journal journal, DateTime when)
    {
        var id = JsonText.Text(entry["id"])!;
        var bare = Bare(id);
        var code = kind == EntryKind.Function;
        var changed = false;
        var evidence = entry["evidence"] as JsonArray;
        foreach (var group in found.Where(f => f.Name == bare).GroupBy(f => f.Kind).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var best = group.OrderBy(f => TieRank(f.Tie)).ThenBy(f => f.Store == "type-names" ? 0 : 1).ThenBy(f => f.Store, StringComparer.Ordinal).First();
            var item = new JsonObject { ["kind"] = best.Kind, ["store"] = best.Store };
            if (code) item["tie"] = best.Tie;
            item["says"] = Sentence(best, code);
            var same = (evidence ?? []).OfType<JsonObject>().Where(e => JsonText.Text(e["kind"]) == best.Kind).ToList();
            var own = same.FirstOrDefault(e => e["store"] is not null);
            if (own is null && same.Count > 0) continue;                       // said by hand
            if (own is not null && JsonNode.DeepEquals(own, item)) continue;
            if (evidence is null) entry["evidence"] = evidence = [];
            if (own is not null) evidence[evidence.IndexOf(own)] = item;
            else evidence.Add(item);
            changed = true;
            journal.Add(new JournalLine(when, "matched", id) { Store = best.Store, Tie = code ? best.Tie : null });
        }
        if (!code || found.Any(f => f.Name == bare && f.Tie != "name")) return changed;
        var others = found.Where(f => f.Name != bare && f.Tie != "name").GroupBy(f => f.Name, StringComparer.Ordinal).ToList();
        if (others.Count != 1) return changed;                                 // none, or folded code under several names
        var other = others[0].OrderBy(f => f.Kind, StringComparer.Ordinal).First();
        var known = entry["known_as"] as JsonArray;
        if ((known ?? []).OfType<JsonObject>().Any(k => JsonText.Text(k["name"]) == other.Name)) return changed;
        if (known is null) entry["known_as"] = known = [];
        known.Add(new JsonObject { ["name"] = other.Name, ["source"] = other.Kind == "game-name" ? "macos" : "toolkit" });
        journal.Add(new JournalLine(when, "differs", id) { Store = other.Store, Name = other.Name, Tie = other.Tie });
        return true;
    }
}

/// <summary>The stores as the home's databases hold them; a database that is not there answers nothing.</summary>
public sealed class DatabaseNameStores(SqliteConnection? symbols, SqliteConnection? decomp) : INameStores
{
    const string Macos = "bg3_macos_x64";

    readonly Dictionary<string, long?> binaries = new(StringComparer.OrdinalIgnoreCase), dlls = new(StringComparer.OrdinalIgnoreCase);

    long? Binary(string name)
    {
        if (!binaries.TryGetValue(name, out var id))
            binaries[name] = id = Sql.Long(decomp!.Scalar("SELECT id FROM binaries WHERE name = ? COLLATE NOCASE", name));
        return id;
    }

    public bool InCode(string binary, long rva)
    {
        if (symbols is null) return false;
        try
        {
            if (!dlls.TryGetValue(binary, out var dll)) dlls[binary] = dll = Sql.Long(symbols.Scalar("SELECT id FROM dlls WHERE name = ? COLLATE NOCASE", binary));
            return dll is not null && symbols.One("SELECT rva, size FROM functions WHERE dll_id = ? AND rva <= ? ORDER BY rva DESC LIMIT 1", dll, rva) is { } near
                && rva < Sql.Long(near[0])!.Value + (Sql.Long(near[1]) ?? 0);
        }
        catch (SqliteException) { return false; }
    }

    public IReadOnlyList<StoreName> Of(EntryKind kind, string id, IReadOnlyList<(string Binary, long Rva)> places)
    {
        var found = new List<StoreName>();
        var bare = NameSources.Bare(id);
        try
        {
            if (kind == EntryKind.Type) Types(bare, found);
            else if (kind == EntryKind.Function) Functions(bare, places, found);
        }
        catch (SqliteException) { }                                             // an older database without a table: what it has not, it does not say
        return found.Distinct().ToList();
    }

    void Types(string? bare, List<StoreName> found)
    {
        if (bare is null || symbols is null) return;
        if (symbols.Scalar("SELECT 1 FROM type_names WHERE type IN (?, ?) LIMIT 1", "class " + bare, "struct " + bare) is not null)
            found.Add(new StoreName("game-name", "type-names", bare, "name"));
        var cut = bare.LastIndexOf("::", StringComparison.Ordinal);
        foreach (var row in symbols.Query("SELECT DISTINCT d.name FROM symbols s JOIN dlls d ON d.id = s.dll_id WHERE s.class = ? AND s.namespace = ? AND d.origin = 'toolkit' ORDER BY d.name LIMIT 1",
                     cut < 0 ? bare : bare[(cut + 2)..], cut < 0 ? "" : bare[..cut]))
            found.Add(new StoreName("toolkit-name", (string)row[0]!, bare, "name"));
    }

    void Functions(string? bare, IReadOnlyList<(string Binary, long Rva)> places, List<StoreName> found)
    {
        if (symbols is not null)
        {
            foreach (var (binary, rva) in places)
            {
                if (!dlls.TryGetValue(binary, out var game)) dlls[binary] = game = Sql.Long(symbols.Scalar("SELECT id FROM dlls WHERE name = ? COLLATE NOCASE", binary));
                if (game is null) continue;
                foreach (var row in symbols.Query(
                             "SELECT DISTINCT s.full_name, d.name, m.method, m.relation FROM matches m JOIN symbols s ON s.dll_id = m.toolkit_dll AND s.rva = m.toolkit_rva"
                             + " JOIN dlls d ON d.id = m.toolkit_dll WHERE m.game_dll = ? AND m.game_rva = ? AND s.kind IN ('method', 'function')", game, rva))
                {
                    // a function of no namespace is `unplaced::<Name>` in the registry
                    var (name, relation) = (RegistryId.IsValid((string)row[0]!) ? RegistryId.Placed((string)row[0]!) : (string)row[0]!, row[3] as string);
                    // a match of inlined or partial code names a neighbour: it ties the id's own name, never proposes another
                    if (name == bare ? relation != "inlined" || row[2] as string == "vtable" : relation is null or "same")
                        found.Add(new StoreName("toolkit-name", (string)row[1]!, name, "match", row[2] as string));
                }
            }
            // a name under `unplaced::` says nothing of its scope: only an address can tie it to a store's name
            if (bare is not null && !bare.StartsWith(RegistryId.Unplaced + "::", StringComparison.Ordinal))
                foreach (var row in symbols.Query("SELECT DISTINCT d.name FROM symbols s JOIN dlls d ON d.id = s.dll_id WHERE s.method = ? AND s.full_name = ? AND s.kind IN ('method', 'function') AND d.origin = 'toolkit' ORDER BY d.name",
                             bare[(bare.LastIndexOf("::", StringComparison.Ordinal) + 2)..], bare))
                    if (!found.Any(f => f.Kind == "toolkit-name" && f.Name == bare))
                        found.Add(new StoreName("toolkit-name", (string)row[0]!, bare, "name"));
        }
        if (decomp is null || Binary(Macos) is not { } mac) return;
        foreach (var (binary, rva) in places)
        {
            if (Binary(binary) is not { } here) continue;
            foreach (var row in decomp.Query(
                         "SELECT f.name, p.state FROM function_pairs p JOIN functions f ON f.binary_id = ? AND f.rva = p.source_rva"
                         + " WHERE p.source_binary = ? AND p.target_binary = ? AND p.target_rva = ? AND p.state IN ('seed', 'accepted')"
                         + " UNION SELECT f.name, p.state FROM function_pairs p JOIN functions f ON f.binary_id = ? AND f.rva = p.target_rva"
                         + " WHERE p.target_binary = ? AND p.source_binary = ? AND p.source_rva = ? AND p.state IN ('seed', 'accepted')", mac, mac, here, rva, mac, mac, here, rva))
                if (row[0] is string name && !name.StartsWith("FUN_", StringComparison.Ordinal) && !name.StartsWith("thunk_", StringComparison.Ordinal) && RegistryId.IsValid(name))
                    found.Add(new StoreName("game-name", Macos, RegistryId.Placed(name), "pair", row[1] as string == "seed" ? "a seed" : "an accepted pair"));
        }
        if (bare is null || bare.StartsWith(RegistryId.Unplaced + "::", StringComparison.Ordinal) || found.Any(f => f.Kind == "game-name" && f.Name == bare)) return;
        if (decomp.Scalar("SELECT 1 FROM functions INDEXED BY functions_name WHERE name = ? AND binary_id = ? LIMIT 1", bare, mac) is not null)
            found.Add(new StoreName("game-name", Macos, bare, "name"));
    }
}
