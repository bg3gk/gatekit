using System.Text.Json.Nodes;
using GateKit.Json;

namespace GateKit.Symbols.Registry;

/// <summary>One act of the registry's tools on an id, as the journal keeps it (schema <c>state/registry-journal/1</c>).</summary>
public sealed record JournalLine(DateTime When, string Act, string Id)
{
    public string? Member { get; init; }
    public string? From { get; init; }
    public string? To { get; init; }
    public string? Why { get; init; }
    public string? Store { get; init; }
    public string? Tie { get; init; }
    public string? Name { get; init; }
    public string? Into { get; init; }
    public int? Count { get; init; }
    public int? Left { get; init; }
    public int? Qualified { get; init; }

    public JsonObject ToJson()
    {
        var o = new JsonObject { ["when"] = LiveLayer.Time(When), ["act"] = Act, ["id"] = Id };
        void Put(string key, string? value) { if (value is { Length: > 0 }) o[key] = value; }
        Put("member", Member); Put("from", From); Put("to", To); Put("why", Why); Put("store", Store); Put("tie", Tie); Put("name", Name); Put("into", Into);
        if (Count is { } count) o["count"] = count;
        if (Left is { } left) o["left"] = left;
        if (Qualified is { } qualified) o["qualified"] = qualified;
        return o;
    }

    public static JournalLine FromJson(JsonObject o) => new(LiveLayer.ParseTime(JsonText.Text(o["when"])!), JsonText.Text(o["act"])!, JsonText.Text(o["id"])!)
    {
        Member = JsonText.Text(o["member"]), From = JsonText.Text(o["from"]), To = JsonText.Text(o["to"]), Why = JsonText.Text(o["why"]),
        Store = JsonText.Text(o["store"]), Tie = JsonText.Text(o["tie"]), Name = JsonText.Text(o["name"]), Into = JsonText.Text(o["into"]),
        Count = o["count"]?.GetValue<int>(), Left = o["left"]?.GetValue<int>(), Qualified = o["qualified"]?.GetValue<int>(),
    };

    /// <summary>The line a command prints when the act happens: one line, the id first.</summary>
    public string Said() => Act switch
    {
        "lowered" or "raised" => $"  {Act}: {Id}{(Member is null ? "" : "." + Member)} {From} -> {To}: {Why}",
        "matched" => $"  name found: {Id} - {Store} holds it" + (Tie is null ? "" : $" ({TieWords(Tie)})"),
        "differs" => $"  another name: {Id} - {Store} names this function {Name} (`gatekit registry rename {Id} {Name}` if it is the same thing)",
        "folded" => $"  folded: {Id} into {Into}: one function, the evidence kept",
        "turned" => $"  addresses: {Id} - {Count} turned into ids, {Qualified ?? 0} written with binary and build, {Left} not placed",
        "stale" => $"  stale: {Id} - {Count} {Why}",
        "collapsed" => $"  repeated: {Id} - {Count} sentence{(Count == 1 ? "" : "s")} of evidence said twice, kept once",
        _ => $"  {Act}: {Id}: {Why}",
    };

    public static string TieWords(string? tie) => tie switch
    {
        "match" => "the symbol database matches the export to this function",
        "pair" => "a stored pair joins that function to this one",
        _ => "by the name alone",
    };
}

/// <summary>
/// What the registry's tools did by themselves, appended as it happens to
/// <c>&lt;live layer&gt;/journal.jsonl</c>: `registry digest` reads it instead of a person
/// reconciling a night's entries by hand. A journal with no folder keeps its lines in memory only
/// (a dry run).
/// </summary>
public sealed class Journal(string? folder)
{
    public const string Schema = "state/registry-journal/1";

    readonly List<JournalLine> lines = [];
    int written;

    /// <summary>Every line added through this object, written or not.</summary>
    public IReadOnlyList<JournalLine> Lines => lines;

    public string? Path { get; } = folder is null ? null : System.IO.Path.Combine(folder, "journal.jsonl");

    public JournalLine Add(JournalLine line)
    {
        lines.Add(line);
        return line;
    }

    /// <summary>Takes back the lines added from an index on, not yet written: the entry they are about was not written either.</summary>
    public void Forget(int from) => lines.RemoveRange(Math.Max(from, written), lines.Count - Math.Max(from, written));

    /// <summary>Appends what was added since the last flush, under the journal's lock.</summary>
    public void Flush()
    {
        if (Path is not null && lines.Count > written) JsonText.Append(Path, [.. lines.Skip(written).Select(l => (JsonNode)l.ToJson())], Schema);
        written = lines.Count;
    }

    /// <summary>The journal's lines from a moment on, in the order written.</summary>
    public IReadOnlyList<JournalLine> Since(DateTime? since) =>
        Path is null || !File.Exists(Path) ? []
            : JsonText.ReadLines(Path, Schema, _ => { }).Select(JournalLine.FromJson).Where(l => since is null || l.When >= since).ToList();
}
