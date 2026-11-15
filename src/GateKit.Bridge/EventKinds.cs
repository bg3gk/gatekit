using System.Text.Json.Nodes;
using GateKit.Json;

namespace GateKit.Bridge;

/// <summary>A family of the event log: always written, on at start, or on only when switched on.</summary>
public sealed record EventFamily(string Name, bool Always, bool Default, string About);

/// <summary>A kind of the event log as the registry declares it.</summary>
public sealed record EventKind(string Name, string Family, string Class, string Knows, bool KnowsPerRow, string? Attention,
    string? Ends, IReadOnlyCollection<string> Payload)
{
    /// <summary>A row may carry the class of the verb that made it (job_done: the GK.Act verb's).</summary>
    public bool ClassPerRow { get; init; }
}

/// <summary>
/// The registry of the event log (the bridge mod's Shared/EventKinds.json, schemas/events/kinds):
/// every family and kind in one place. The mod reads the file, refuses a row of any other kind and
/// copies what it loaded into the channel; the reader places rows by that copy (the registry of the
/// running game), and by the one embedded at build time only where the game wrote none. Adding a
/// family is an entry in the file and the code that writes its rows - nothing here changes.
/// </summary>
public sealed class EventKinds
{
    public IReadOnlyDictionary<string, EventFamily> Families { get; }
    public IReadOnlyDictionary<string, EventKind> Kinds { get; }

    EventKinds(Dictionary<string, EventFamily> families, Dictionary<string, EventKind> kinds)
    {
        Families = families;
        Kinds = kinds;
    }

    static readonly Lazy<EventKinds> EmbeddedKinds = new(() =>
    {
        using var stream = typeof(EventKinds).Assembly.GetManifestResourceStream("EventKinds.json")
            ?? throw new InvalidOperationException("EventKinds.json is not embedded in GateKit.Bridge");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd(), "EventKinds.json");
    });

    /// <summary>The registry this build was made with (the mod's file, embedded).</summary>
    public static EventKinds Embedded => EmbeddedKinds.Value;

    /// <summary>Reads a registry: held to events/kinds, then to itself - every family named exists,
    /// every begin and hold names an end kind that closes.</summary>
    public static EventKinds Parse(string text, string source)
    {
        var parsed = JsonText.Parse(text, source);
        JsonText.Check(parsed, "events/kinds");
        var root = parsed.Root!.AsObject();
        var families = new Dictionary<string, EventFamily>(StringComparer.Ordinal);
        foreach (var (name, node) in root["families"]!.AsObject())
        {
            var f = node!.AsObject();
            families[name] = new EventFamily(name, f["always"] is { } a && (bool)a, f["default"] is { } d && (bool)d, (string)f["about"]!);
        }
        var kinds = new Dictionary<string, EventKind>(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var (name, node) in root["kinds"]!.AsObject())
        {
            var k = node!.AsObject();
            var kind = new EventKind(name, (string)k["family"]!, (string)k["class"]!, (string)k["knows"]!,
                k["knowsPerRow"] is { } p && (bool)p, JsonText.Text(k["attention"]), JsonText.Text(k["ends"]),
                [.. k["payload"]!.AsObject().Select(f => f.Key)]) { ClassPerRow = k["classPerRow"] is { } c && (bool)c };
            if (!families.ContainsKey(kind.Family)) problems.Add($"kind {name}: no family {kind.Family}");
            kinds[name] = kind;
        }
        foreach (var kind in kinds.Values.Where(k => k.Attention is "begin" or "hold"))
        {
            if (kind.Ends is null) problems.Add($"kind {kind.Name} waits for an answer and names no `ends`");
            else if (!kinds.TryGetValue(kind.Ends, out var end) || end.Attention is not ("end" or "release"))
                problems.Add($"kind {kind.Name}: its end {kind.Ends} is not a kind whose attention is end or release");
        }
        if (problems.Count > 0) throw new FormatException($"{source}: {string.Join("; ", problems)}");
        return new EventKinds(families, kinds);
    }

    /// <summary>The family of a kind, or null for a kind the registry does not have.</summary>
    public string? FamilyOf(string kind) => Kinds.TryGetValue(kind, out var k) ? k.Family : null;

    /// <summary>What is wrong with a row against the registry: `unplaced` - a kind it does not have,
    /// `knows` or `attention` other than declared - makes the reader mark the row (it is kept);
    /// `noted` are payload fields the kind does not declare, said.</summary>
    public (List<string> Unplaced, List<string> Noted) Check(JsonObject row)
    {
        var unplaced = new List<string>();
        var noted = new List<string>();
        var name = (string?)row["kind"] ?? "";
        if (!Kinds.TryGetValue(name, out var kind))
        {
            unplaced.Add($"kind {name} is not in the registry");
            return (unplaced, noted);
        }
        var knows = (string?)row["knows"];
        if (knows != kind.Knows && !(kind.KnowsPerRow && knows == "omniscient"))
            unplaced.Add($"kind {name} is known by {kind.Knows}, the row says {knows}");
        if (JsonText.Text(row["class"]) is { } cls && cls != kind.Class && !kind.ClassPerRow)
            unplaced.Add($"kind {name} is of class {kind.Class} and carries no class per row, the row says {cls}");
        if (JsonText.Text(row["attention"]) != kind.Attention)
            unplaced.Add($"kind {name} has attention {kind.Attention ?? "none"}, the row says {JsonText.Text(row["attention"]) ?? "none"}");
        if (row["payload"] is JsonObject payload)
            foreach (var (field, _) in payload)
                if (!kind.Payload.Contains(field)) noted.Add($"kind {name}: payload field {field} is not declared");
        return (unplaced, noted);
    }

    /// <summary>What <paramref name="other"/> has that this registry has not, lacks, or declares
    /// otherwise, in one line (`kinds added: a; removed: b; changed: c (knows)`); null when they
    /// declare the same.</summary>
    public string? Difference(EventKinds other)
    {
        var parts = new List<string>();
        void Names(string what, IEnumerable<string> names)
        {
            var list = names.Order(StringComparer.Ordinal).ToList();
            if (list.Count > 0) parts.Add($"{what}: {string.Join(", ", list)}");
        }
        Names("families added", other.Families.Keys.Except(Families.Keys));
        Names("families removed", Families.Keys.Except(other.Families.Keys));
        Names("kinds added", other.Kinds.Keys.Except(Kinds.Keys));
        Names("kinds removed", Kinds.Keys.Except(other.Kinds.Keys));
        var changed = new List<string>();
        foreach (var (name, mine) in Kinds.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (!other.Kinds.TryGetValue(name, out var theirs)) continue;
            var fields = new List<string>();
            if (mine.Family != theirs.Family) fields.Add("family");
            if (mine.Class != theirs.Class) fields.Add("class");
            if (mine.Knows != theirs.Knows || mine.KnowsPerRow != theirs.KnowsPerRow) fields.Add("knows");
            if (mine.Attention != theirs.Attention || mine.Ends != theirs.Ends) fields.Add("attention");
            if (!mine.Payload.ToHashSet(StringComparer.Ordinal).SetEquals(theirs.Payload)) fields.Add("payload");
            if (fields.Count > 0) changed.Add($"{name} ({string.Join(", ", fields)})");
        }
        if (changed.Count > 0) parts.Add("kinds changed: " + string.Join(", ", changed));
        return parts.Count > 0 ? string.Join("; ", parts) : null;
    }
}
