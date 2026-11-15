using System.Text.Json.Nodes;

namespace GateKit.Mcp;

/// <summary>
/// What GateKit knows about each of the engine's component types, joined from the three files
/// that hold it: <c>coverage.json</c> (what Script Extender covers, a row per side),
/// <c>seen/index.json</c> (where a type was actually observed alive) and the layout files.
/// It reads the committed record and needs no game.
///
/// The groups matter more than the counts, because they say what kind of work a type is:
/// <list type="bullet">
/// <item><b>unseen</b> - mapped from a reading of the code, never observed alive. Nothing has
///   tested the layout. This is the frontier a run should be aimed at.</item>
/// <item><b>unverified</b> - observed alive, but the fields have never been checked against the
///   raw memory of the same instance.</item>
/// <item><b>waiting</b> - a layout hypothesis not confirmed.</item>
/// <item><b>alive</b> - observed with no layout at all: work for a hypothesis.</item>
/// <item><b>dead</b> - neither mapped nor ever seen.</item>
/// </list>
/// A type counts once whatever side it lives on.
/// </summary>
public sealed class Ledger
{
    public sealed record Entry(
        bool Mapped, bool Verified, bool Replicated, bool OneFrame, int Size,
        List<string> Sides, JsonObject? Seen, string? Layout, string? LayoutFile);

    readonly Dictionary<string, Entry> _types;

    Ledger(Dictionary<string, Entry> types) => _types = types;

    public static string Namespace(string name)
    {
        var parts = name.Split("::");
        return parts.Length >= 2 ? parts[0] + "::" + parts[1] : parts[0];
    }

    public static Ledger Load(string root)
    {
        var types = new Dictionary<string, Entry>(StringComparer.Ordinal);
        var coveragePath = Path.Combine(root, "coverage.json");
        if (!File.Exists(coveragePath))
            throw new Copies.CopyException($"the coverage index is missing: {coveragePath}");

        foreach (var row in JsonNode.Parse(File.ReadAllText(coveragePath))!["components"]!.AsArray())
        {
            if (row is not JsonObject component) continue;
            var name = component["name"]!.GetValue<string>();
            var mapped = Truthy(component["mapped"]);
            var verified = Truthy(component["verified"]);
            var replicated = Truthy(component["replicated"]);
            if (types.TryGetValue(name, out var had))
            {
                had.Sides.Add(component["side"]?.GetValue<string>() ?? "?");
                types[name] = had with
                {
                    Mapped = had.Mapped || mapped,
                    Verified = had.Verified || verified,
                    Replicated = had.Replicated || replicated,
                };
            }
            else
            {
                types[name] = new Entry(mapped, verified, replicated,
                    Truthy(component["oneFrame"]), (int)(LayoutCheck.Integer(component["totalSize"]) ?? 0),
                    [component["side"]?.GetValue<string>() ?? "?"], null, null, null);
            }
        }

        var seenPath = Path.Combine(root, "seen", "index.json");
        if (File.Exists(seenPath))
        {
            foreach (var (name, sides) in JsonNode.Parse(File.ReadAllText(seenPath))!.AsObject())
            {
                if (types.TryGetValue(name, out var entry)) types[name] = entry with { Seen = sides?.AsObject() };
            }
        }

        // the layouts: the registry's types made from them, else the layout files; one that cannot be
        // read is left out, so a half-written file does not blind the ledger
        if (GateKit.Names.LayoutStore.Registry(root) is not null || Directory.Exists(Path.Combine(root, "layouts")))
        {
            foreach (var file in GateKit.Names.LayoutStore.All(root))
            {
                if (file.Json["component"]?.GetValue<string>() is not { } name) continue;
                if (!types.TryGetValue(name, out var entry)) continue;
                types[name] = entry with
                {
                    Layout = file.Json["status"]?.GetValue<string>() ?? "hypothesis",
                    LayoutFile = file.Batch + "/" + GateKit.Names.LayoutStore.LayoutName(name),
                };
            }
        }

        return new Ledger(types);
    }

    static bool Truthy(JsonNode? value) => value switch
    {
        null => false,
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<double>(out var d) => d != 0,
        JsonValue v when v.TryGetValue<string>(out var s) => s.Length > 0,
        _ => true,   // an object, such as coverage.json's `verified` record, means yes
    };

    /// <summary>Whether <paramref name="name"/> is one of the engine's component types.</summary>
    public bool Knows(string name) => _types.ContainsKey(name);

    /// <summary>Up to <paramref name="max"/> types a mistyped or shortened name probably meant: those
    /// under it as a namespace, or whose own name holds one of the words of <paramref name="hints"/>
    /// (a recipe's name, say <c>camp_EndTheDayResultEventOneFrame</c>).</summary>
    public IReadOnlyList<string> Near(string name, IEnumerable<string> hints, int max = 6)
    {
        var words = hints.SelectMany(h => h.Split(['_', '-', ' ', ':'], StringSplitOptions.RemoveEmptyEntries))
            .Where(w => w.Length >= 5).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string Tail(string type) => type[(type.LastIndexOf("::", StringComparison.Ordinal) + 2)..];
        var byWord = _types.Keys.Where(type => words.Any(w => Tail(type).Contains(w, StringComparison.OrdinalIgnoreCase)));
        var under = _types.Keys.Where(type => type.StartsWith(name.TrimEnd(':') + "::", StringComparison.Ordinal));
        return [.. byWord.Concat(under).Distinct(StringComparer.Ordinal)
            .OrderBy(type => type.StartsWith(name, StringComparison.Ordinal) ? 0 : 1).ThenBy(type => type, StringComparer.Ordinal)
            .Take(max)];
    }

    public JsonObject Tally()
    {
        var byGroup = new JsonObject();
        foreach (var group in new[] { "unseen", "unverified", "waiting", "alive", "dead" })
            byGroup[group] = Group(group, "").Count;
        return new JsonObject
        {
            ["types"] = _types.Count,
            ["mapped"] = _types.Count(p => p.Value.Mapped),
            ["seen_alive"] = _types.Count(p => p.Value.Seen is { Count: > 0 }),
            ["verified_live"] = _types.Count(p => p.Value.Verified),
            ["groups"] = byGroup,
            ["what_each_group_means"] = new JsonObject
            {
                ["unseen"] = "mapped from the code, never observed alive: the frontier. Find a situation that makes one appear.",
                ["unverified"] = "observed alive, fields never checked against raw memory (bg3_probe_fields).",
                ["waiting"] = "a layout hypothesis not confirmed yet.",
                ["alive"] = "observed with no layout at all: work for a hypothesis (bg3_probe).",
                ["dead"] = "neither mapped nor ever seen.",
            },
        };
    }

    public IReadOnlyList<string> Group(string group, string prefix)
    {
        bool Wanted(KeyValuePair<string, Entry> pair)
        {
            var (mapped, seen, layout) = (pair.Value.Mapped, pair.Value.Seen is { Count: > 0 }, pair.Value.Layout);
            return group switch
            {
                "unseen" => mapped && !seen,
                "unverified" => mapped && seen && !pair.Value.Verified,
                "waiting" => !mapped && layout is not null,
                "alive" => !mapped && layout is null && seen,
                "dead" => !mapped && layout is null && !seen,
                _ => throw new Copies.CopyException($"unknown group {group}"),
            };
        }

        return [.. _types.Where(Wanted).Select(p => p.Key)
            .Where(name => name.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)];
    }

    public JsonObject Describe(string name)
    {
        var entry = _types[name];
        var row = new JsonObject
        {
            ["name"] = name,
            ["size"] = entry.Size,
            ["sides"] = new JsonArray([.. entry.Sides.Distinct().Select(s => (JsonNode)s!)]),
        };
        if (entry.OneFrame) row["one_frame"] = "lives for a single frame: the standing capture is the only way to catch it";
        if (entry.Replicated) row["replicated"] = true;
        if (entry.LayoutFile is not null) row["layout"] = $"{entry.LayoutFile} ({entry.Layout})";
        if (entry.Seen is { } seen)
        {
            var where = new JsonObject();
            foreach (var (side, info) in seen)
            {
                var labels = info?["labels"]?.AsArray().Take(3).Select(l => l?.GetValue<string>() ?? "") ?? [];
                where[side] = $"at most {info?["max"]} at once; first under: {string.Join(", ", labels)}";
            }
            row["seen"] = where;
        }
        return row;
    }
}
