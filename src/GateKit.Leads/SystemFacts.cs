using System.Text.Json.Nodes;

namespace GateKit.Leads;

/// <summary>
/// The committed facts of each system (research/coverage/systems/*/*.json, the <c>symbols.uses</c>
/// written by <c>gatekit symbols describe-systems</c>): which component operations its per-frame
/// update performs, to depth 2. The adds are creators; the gets and hases are what the update reads
/// before it acts - the guard a lead names.
/// </summary>
public sealed class SystemFacts
{
    readonly Dictionary<string, List<Creator>> adds = new(StringComparer.Ordinal);
    readonly Dictionary<string, SortedSet<string>> reads = new(StringComparer.Ordinal);

    public static SystemFacts Load(string coverageRoot)
    {
        var facts = new SystemFacts();
        var folder = Path.Combine(coverageRoot, "systems");
        if (!Directory.Exists(folder)) return facts;
        foreach (var file in Directory.EnumerateFiles(folder, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            JsonObject? doc;
            try { doc = JsonNode.Parse(File.ReadAllText(file)) as JsonObject; }
            catch (System.Text.Json.JsonException) { continue; }
            if (doc?["system"]?.GetValue<string>() is not { } system) continue;
            if (doc["symbols"]?["uses"] is not JsonArray uses) continue;
            foreach (var use in uses.OfType<JsonObject>())
            {
                var type = use["type"]?.GetValue<string>();
                var kind = use["kind"]?.GetValue<string>();
                if (type is null || kind is null) continue;
                if (kind == "add")
                {
                    var function = use["function"]?.GetValue<string>() ?? "?";
                    var binary = function.Split('+')[0];
                    if (!facts.adds.TryGetValue(type, out var list)) facts.adds[type] = list = [];
                    list.Add(new Creator(system, function, binary, use["site"]?.GetValue<string>(),
                        use["depth"]?.GetValue<int>(), use["confidence"]?.GetValue<string>() ?? "unknown"));
                }
                else if (kind is "get" or "has")
                {
                    if (!facts.reads.TryGetValue(system, out var set)) facts.reads[system] = set = new(StringComparer.Ordinal);
                    set.Add(type);
                }
            }
        }
        return facts;
    }

    /// <summary>The functions a system's update reaches that add the type.</summary>
    public IReadOnlyList<Creator> Adds(string type) => adds.TryGetValue(type, out var list) ? list : [];

    /// <summary>The components a system's update gets or asks about.</summary>
    public IReadOnlyList<string> Reads(string system) => reads.TryGetValue(system, out var set) ? [.. set] : [];
}
