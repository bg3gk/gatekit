using GateKit.GameData.Query;

namespace GateKit.Sweep;

/// <summary>One stats entry with its fields as the game sees them: its own over its parent's
/// (<c>using</c>), all the way up.</summary>
public sealed record StatEntry(string Name, string Type, string? Parent, string Mod, IReadOnlyDictionary<string, string> Fields)
{
    public string? Field(string name) => Fields.TryGetValue(name, out var value) && value.Length > 0 ? value : null;

    /// <summary>Not a test, debug or cheat entry by its name: what a player of the shipping game can meet.</summary>
    public bool Shipping => !Development(Name);

    internal static bool Development(string name)
    {
        foreach (var word in new[] { "Test", "TEST", "Debug", "DEBUG", "Cheat", "CHEAT", "_DEV", "Dev_", "DEV_", "Dummy", "Template" })
            if (name.Contains(word, StringComparison.Ordinal)) return true;
        return false;
    }
}

/// <summary>The game's stats from the data index (tables <c>stats</c> and <c>stat_fields</c>),
/// inheritance resolved. A later mod's entry of the same name wins, as in the game.</summary>
public sealed class StatsData
{
    public IReadOnlyList<StatEntry> Entries { get; }
    readonly Dictionary<string, StatEntry> _byName;

    public StatsData(IEnumerable<StatEntry> entries)
    {
        _byName = new Dictionary<string, StatEntry>(StringComparer.Ordinal);
        foreach (var entry in entries) _byName[entry.Name] = entry;
        Entries = [.. Resolve(_byName).OrderBy(e => e.Name, StringComparer.Ordinal)];
        _byName = Entries.ToDictionary(e => e.Name, StringComparer.Ordinal);
    }

    public StatEntry? Get(string name) => _byName.GetValueOrDefault(name);

    /// <summary>Every entry with its own fields over its parents'.</summary>
    static IEnumerable<StatEntry> Resolve(Dictionary<string, StatEntry> raw)
    {
        var done = new Dictionary<string, StatEntry>(StringComparer.Ordinal);
        StatEntry Of(StatEntry entry, int depth)
        {
            if (done.TryGetValue(entry.Name, out var resolved)) return resolved;
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            if (depth < 32 && entry.Parent is { Length: > 0 } parent && parent != entry.Name && raw.TryGetValue(parent, out var up))
                foreach (var (key, value) in Of(up, depth + 1).Fields) fields[key] = value;
            foreach (var (key, value) in entry.Fields) fields[key] = value;
            resolved = entry with { Fields = fields };
            done[entry.Name] = resolved;
            return resolved;
        }
        foreach (var entry in raw.Values) yield return Of(entry, 0);
    }

    /// <summary>Loads every stats entry of the index. Mod order is the index's row order (the game's load order).</summary>
    public static StatsData Load(ReadOnlyDb db)
    {
        var fields = new Dictionary<long, Dictionary<string, string>>();
        foreach (var row in db.Rows("SELECT stat_id, field, value FROM stat_fields"))
        {
            var id = Convert.ToInt64(row["stat_id"], System.Globalization.CultureInfo.InvariantCulture);
            if (!fields.TryGetValue(id, out var map)) fields[id] = map = new Dictionary<string, string>(StringComparer.Ordinal);
            map[(string)row["field"]!] = row["value"] as string ?? "";
        }
        var entries = new List<StatEntry>();
        foreach (var row in db.Rows("SELECT id, name, type, parent, mod FROM stats ORDER BY id"))
        {
            var id = Convert.ToInt64(row["id"], System.Globalization.CultureInfo.InvariantCulture);
            entries.Add(new StatEntry((string)row["name"]!, row["type"] as string ?? "", row["parent"] as string, row["mod"] as string ?? "",
                fields.TryGetValue(id, out var map) ? map : new Dictionary<string, string>()));
        }
        return new StatsData(entries);
    }
}
