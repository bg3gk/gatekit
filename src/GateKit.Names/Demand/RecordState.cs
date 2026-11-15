using System.Text.Json.Nodes;

namespace GateKit.Names.Demand;

/// <summary>
/// What the committed record says about each component type, as far as the demand report needs it:
/// mapped and verified (<c>coverage.json</c>, a type counts once whatever side it is on), seen alive
/// (<c>seen/index.json</c>), its layout and fields, and which described systems read, write or add
/// it (<c>systems/**/*.json</c>). The groups follow the ledger (<c>bg3_coverage_todo</c>): unseen is
/// mapped and never seen; unverified is mapped, seen, and its fields never checked against memory.
/// </summary>
public sealed class RecordState
{
    public sealed class TypeState
    {
        public bool Mapped, Verified, OneFrame;
        public bool Seen;
        public SortedSet<string> Sides { get; } = new(StringComparer.Ordinal);
        public LayoutFile? Layout;
        /// <summary>Described systems naming the type (reads, writes, uses).</summary>
        public SortedSet<string> Systems { get; } = new(StringComparer.Ordinal);
        /// <summary>Described systems that write or add it.</summary>
        public SortedSet<string> Creators { get; } = new(StringComparer.Ordinal);
    }

    public Dictionary<string, TypeState> Types { get; } = new(StringComparer.Ordinal);
    public List<LayoutFile> Layouts { get; private set; } = [];
    public bool HasCoverage { get; private set; }
    public bool HasSystems { get; private set; }

    public TypeState State(string type)
    {
        if (!Types.TryGetValue(type, out var state)) Types[type] = state = new TypeState();
        return state;
    }

    static bool Truthy(JsonNode? node) => LayoutFile.Truthy(node);

    public static RecordState Load(string coverageRoot, Action<string>? note = null)
    {
        var record = new RecordState();
        var coverage = Path.Combine(coverageRoot, "coverage.json");
        if (File.Exists(coverage) && JsonNode.Parse(File.ReadAllText(coverage))?["components"] is JsonArray rows)
        {
            record.HasCoverage = true;
            foreach (var row in rows.OfType<JsonObject>())
            {
                if (LayoutFile.Text(row["name"]) is not { } name) continue;
                var state = record.State(name);
                state.Mapped |= Truthy(row["mapped"]);
                state.Verified |= Truthy(row["verified"]);
                state.OneFrame |= Truthy(row["oneFrame"]);
                if (LayoutFile.Text(row["side"]) is { } side) state.Sides.Add(side);
            }
        }
        else note?.Invoke($"no {coverage}: nothing is known about what was mapped or verified");

        var seen = Path.Combine(coverageRoot, "seen", "index.json");
        if (File.Exists(seen) && JsonNode.Parse(File.ReadAllText(seen)) is JsonObject index)
            foreach (var (name, sides) in index)
                if (sides is JsonObject { Count: > 0 }) record.State(name).Seen = true;

        record.Layouts = LayoutStore.All(coverageRoot, path => note?.Invoke($"note: {path} cannot be read as a layout"));
        foreach (var layout in record.Layouts.Where(l => !l.IsSystem))
        {
            var state = record.State(layout.Component);
            // one type may have a layout in more than one batch: the one in the build wins, then the later
            if (state.Layout is not { InBuild: true } || layout.InBuild) state.Layout = layout;
        }

        var systems = Path.Combine(coverageRoot, "systems");
        if (Directory.Exists(systems))
        {
            foreach (var path in Directory.EnumerateFiles(systems, "*.json", SearchOption.AllDirectories))
            {
                JsonObject? system;
                try { system = JsonNode.Parse(File.ReadAllText(path)) as JsonObject; }
                catch (System.Text.Json.JsonException) { continue; }
                if (system is null || LayoutFile.Text(system["system"]) is not { } name) continue;
                record.HasSystems = true;
                var symbols = system["symbols"] as JsonObject;
                foreach (var t in Names(symbols?["reads"])) record.State(t).Systems.Add(name);
                foreach (var t in Names(symbols?["writes"])) { record.State(t).Systems.Add(name); record.State(t).Creators.Add(name); }
                foreach (var use in (system["uses"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    if (LayoutFile.Text(use["type"]) is not { } t) continue;
                    record.State(t).Systems.Add(name);
                    if (LayoutFile.Text(use["kind"]) == "add") record.State(t).Creators.Add(name);
                }
            }
        }
        return record;
    }

    static IEnumerable<string> Names(JsonNode? node) =>
        (node as JsonArray ?? []).Select(LayoutFile.Text).OfType<string>();

    /// <summary>Every name the record knows as a component type.</summary>
    public IEnumerable<string> KnownTypes => Types.Keys;
}
