using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GateKit.Bridge;

namespace GateKit.Copies;

/// <summary>
/// The start's timeline of a running copy as Script Extender's fork records it
/// (`Ext.StartupVideos.Timeline()`, the fork's Docs/API.md "The timeline's rows"): every row, or
/// those of some kinds, read after the fact in one chunk on the client, and printed one line a row
/// (`gatekit timeline`, docs/contracts/cli.md).
/// </summary>
public static partial class StartTimeline
{
    /// <summary>The fields every row has, printed as columns.</summary>
    static readonly string[] Columns = ["n", "Ms", "Side", "What"];

    /// <summary>What the process had done when the row was made; printed only when asked.</summary>
    public static readonly string[] Counters = ["IoReadBytes", "IoReadOps", "CpuMs", "FileOpens"];

    /// <summary>The fields of a row in the order they read best; any other comes after them by name.</summary>
    static readonly string[] Order = ["Name", "Phase", "From", "To", "Change", "Parent", "Depth", "Type",
        "Index", "Count", "GameCount", "Progress", "ProgressTotal"];

    // KINDS (nil, or a set of lower-case kinds) is set before it.
    const string Read = """
        if not GK.Verbs.Has("StartupVideos.Timeline") then return { missing = true } end
        local rows = Ext.StartupVideos.Timeline()
        local out = {}
        for i, e in ipairs(rows) do
            if not KINDS or KINDS[string.lower(tostring(e.What))] then
                local r = { n = i }
                for k, v in pairs(e) do r[k] = v end
                out[#out + 1] = r
            end
        end
        return { rows = out, total = #rows, dropped = rows.Dropped or 0 }
        """;

    /// <summary>The kinds `--kind` names (`State,UIState`), or null for every kind. A kind is a word
    /// of letters; anything else is refused.</summary>
    public static IReadOnlyList<string>? Kinds(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var kinds = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var kind in kinds)
            if (!Word().IsMatch(kind))
                throw new CopyException($"--kind: '{kind}' is not a kind of row (a word such as State, UIState, HintShown)");
        return kinds.Length == 0 ? null : kinds;
    }

    /// <summary>The chunk that reads the rows of <paramref name="kinds"/> (null: every row).</summary>
    public static string Chunk(IReadOnlyList<string>? kinds)
    {
        var set = kinds is null ? "nil"
            : "{ " + string.Join(", ", kinds.Select(k => $"[\"{k.ToLowerInvariant()}\"] = true")) + " }";
        return $"local KINDS = {set}\n" + Read;
    }

    /// <summary>The rows of a running copy: `{ rows, total, dropped }`, each row its fields as the
    /// fork gives them and `n`, its place in the whole timeline (from 1).</summary>
    public static async Task<JsonObject> ReadAsync(GameSession session, IReadOnlyList<string>? kinds, CancellationToken cancel = default)
    {
        var answer = await session.LuaAsync("client", Chunk(kinds), TimeSpan.FromSeconds(60), cancel);
        if (answer.Field("missing") is JsonValue)
            throw new CopyException("this Script Extender build records no start's timeline (Ext.StartupVideos.Timeline); GateKit's build has it (gatekit se status)");
        var rows = answer.Field("rows") as JsonArray ?? [];
        return new JsonObject
        {
            ["rows"] = rows.DeepClone(),
            ["total"] = answer.Field("total")?.DeepClone() ?? 0,
            ["dropped"] = answer.Field("dropped")?.DeepClone() ?? 0,
        };
    }

    /// <summary>One line a row: its place, time (ms since the process was created), side and kind,
    /// then its fields as `Field=value`; the counters only with <paramref name="counters"/>. Last a
    /// line that says how many rows were shown of how many, and how many the record dropped.</summary>
    public static IEnumerable<string> Lines(JsonObject read, bool counters)
    {
        var rows = read["rows"] as JsonArray ?? [];
        foreach (var row in rows.OfType<JsonObject>())
        {
            var line = new StringBuilder();
            line.Append(CultureInfo.InvariantCulture, $"{Text(row["n"]),5} {Ms(row["Ms"]),10}  {Text(row["Side"]),-8}  {Text(row["What"]),-18}");
            var names = row.Select(p => p.Key).Where(k => !Columns.Contains(k) && !Counters.Contains(k)).ToList();
            var ordered = Order.Where(names.Contains).Concat(names.Where(k => !Order.Contains(k)).Order(StringComparer.Ordinal));
            foreach (var name in ordered) line.Append(' ').Append(name).Append('=').Append(Value(row[name]));
            if (counters)
                foreach (var name in Counters.Where(row.ContainsKey)) line.Append(' ').Append(name).Append('=').Append(Value(row[name]));
            yield return line.ToString().TrimEnd();
        }
        var dropped = read["dropped"] is JsonValue d && d.TryGetValue<double>(out var lost) ? lost : 0;
        yield return $"{rows.Count} rows of {Text(read["total"])}" + (dropped > 0 ? $"; the record was full and dropped {dropped:0} later rows" : "");
    }

    static string Ms(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<double>(out var ms) ? ms.ToString("0.0", CultureInfo.InvariantCulture) : Text(node);

    static string Text(JsonNode? node) => node switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<double>(out var d) => d.ToString(CultureInfo.InvariantCulture),
        _ => GateKit.Json.JsonText.Inline(node, compact: false),
    };

    /// <summary>A field's value: a text with a space or a quote in quotes, anything else as it is.</summary>
    static string Value(JsonNode? node)
    {
        var text = Text(node);
        return node is JsonValue v && v.TryGetValue<string>(out _) && (text.Length == 0 || text.Any(c => c is ' ' or '"' or '='))
            ? "\"" + text.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : text;
    }

    [GeneratedRegex("^[A-Za-z]+$")]
    private static partial Regex Word();
}
