using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GateKit.Testing;

/// <summary>One thing the event log holds that a test did not ask for.</summary>
/// <param name="Sort">blocking (a state that waits for the player, neither asked for nor allowed),
/// participant (a creature in the rows that is not of the cast) or unplaced (a row no registry places).</param>
/// <param name="What">The kind of the row (blocking, unplaced) or the creature's uuid (participant).</param>
/// <param name="Rows">How many rows.</param>
/// <param name="Detail">A line for a person: how many did not end, in which kinds a stranger appears, why a row is unplaced.</param>
public sealed record Surprise(string Sort, string What, int Rows, string Detail)
{
    public string Line() => string.Create(CultureInfo.InvariantCulture, $"      surprise: {Sort} {What} ({Rows} rows): {Detail}");
}

/// <summary>
/// What a test did not ask for, read from the event log after it ran (docs/contracts/cli.md, "test").
/// A report beside the verdict, never the verdict.
///
/// <list type="bullet">
/// <item><b>blocking</b>: a row whose attention is `begin` - a dialogue, a roll window, a tutorial, the
/// game-over box, a fight - of a kind the test never named to <c>game.events</c> or
/// <c>game.wait_attention</c> (<c>game.dialog</c> and <c>game.answer</c> name the dialogue's kinds).</item>
/// <item><b>participant</b>: a creature named by `who` or `by` of a row that is neither of the party
/// (the scope's baseline) nor staged through the ledger (scope_track, roll_pin).</item>
/// <item><b>unplaced</b>: a row the host's registry cannot place.</item>
/// </list>
///
/// A test allows a surprise in one line: <c>test{ surprises = { "combat_started" } }</c>, or the
/// same list in a folder's _suite.json, which holds for every test under it. An entry is a kind of a
/// blocking state, or <see cref="Participants"/> for every creature outside the cast. An unplaced
/// row cannot be allowed.
/// </summary>
public static partial class Surprises
{
    /// <summary>The entry that allows every participant outside the cast.</summary>
    public const string Participants = "participants";

    const string Nobody = "00000000-0000-0000-0000-000000000000";

    /// <summary>The kinds <c>game.dialog</c> and <c>game.answer</c> stand for.</summary>
    public static readonly IReadOnlyList<string> DialogueKinds = ["dialogue", "dialogue_choice", "dialogue_continue", "dialogue_line"];

    [GeneratedRegex("([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})$")]
    private static partial Regex UuidAtEnd();

    static string? Uuid(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && UuidAtEnd().Match(text) is { Success: true } m
            ? m.Groups[1].Value.ToLowerInvariant() : null;

    static long? Whole(JsonNode? node) => node is not JsonValue value ? null
        : value.TryGetValue<long>(out var l) ? l
        : value.TryGetValue<int>(out var i) ? i
        : value.TryGetValue<double>(out var d) ? (long)d : null;

    static void Collect(JsonNode? node, HashSet<string> into)
    {
        switch (node)
        {
            case JsonArray list:
                foreach (var item in list) Collect(item, into);
                break;
            case JsonObject map:
                foreach (var (key, value) in map)
                {
                    if (UuidAtEnd().Match(key) is { Success: true } m) into.Add(m.Groups[1].Value.ToLowerInvariant());
                    Collect(value, into);
                }
                break;
            default:
                if (Uuid(node) is { } id) into.Add(id);
                break;
        }
    }

    /// <summary>The cast as the log knows it: the party of every scope's baseline and whatever was
    /// staged through the ledger, in any row of <paramref name="log"/>.</summary>
    public static HashSet<string> Cast(IEnumerable<JsonObject> log)
    {
        var cast = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in log)
        {
            if (row["payload"] is not JsonObject payload) continue;
            switch ((string?)row["kind"])
            {
                case "scope_begin": Collect(payload["party"], cast); break;
                case "scope_track": Collect(payload["entity"], cast); break;
                case "roll_pin": Collect(payload["who"], cast); break;
            }
        }
        return cast;
    }

    /// <summary>The surprises among <paramref name="rows"/> (the rows of one test, in order).</summary>
    /// <param name="log">Every row the log still holds, the test's included: where the cast is read.</param>
    /// <param name="unplaced">What <c>EventStream.Unplaced</c> answered for the test's rows.</param>
    /// <param name="asked">The kinds the test named to the event log.</param>
    /// <param name="allowed">The entries its header and the _suite.json files above it allow.</param>
    public static IReadOnlyList<Surprise> Find(IReadOnlyList<JsonObject> log, IReadOnlyList<JsonObject> rows, JsonArray? unplaced,
        IReadOnlySet<string> asked, IReadOnlySet<string> allowed)
    {
        var found = new List<Surprise>();
        // a test that named one of the dialogue's kinds asked for the dialogue
        if (DialogueKinds.Any(asked.Contains)) asked = new HashSet<string>(asked.Concat(DialogueKinds), StringComparer.Ordinal);

        // blocking states nobody asked for, by kind
        var ended = new HashSet<(string Side, long Seq)>();
        foreach (var row in rows)
            if ((string?)row["attention"] is "end" or "release" && Whole(row["payload"]?["opened"]) is { } seq)
                ended.Add(((string?)row["side"] ?? "", seq));
        var blocking = new SortedDictionary<string, (int Rows, int Open, string? Window)>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if ((string?)row["attention"] != "begin" || (string?)row["kind"] is not { } kind) continue;
            if (asked.Contains(kind) || allowed.Contains(kind)) continue;
            var open = ended.Contains(((string?)row["side"] ?? "", Whole(row["seq"]) ?? -1)) ? 0 : 1;
            var was = blocking.GetValueOrDefault(kind);
            var window = row["payload"]?["window"] is JsonValue w && w.TryGetValue<string>(out var name) ? name : null;
            blocking[kind] = (was.Rows + 1, was.Open + open, was.Window ?? window);
        }
        foreach (var (kind, (count, open, window)) in blocking)
            found.Add(new Surprise("blocking", kind, count,
                (open == 0 ? "began and ended inside the test" : string.Create(CultureInfo.InvariantCulture, $"{open} not ended when the test ended"))
                + (window is null || window == kind ? "" : "; window " + window)));

        // creatures outside the cast, by creature
        if (!allowed.Contains(Participants))
        {
            var cast = Cast(log);
            var strangers = new SortedDictionary<string, (int Rows, SortedSet<string> Kinds)>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                if (row["payload"] is not JsonObject payload || (string?)row["kind"] is not { } kind) continue;
                if (kind is "scope_track" or "scope_begin" or "roll_pin" or "roll_unpin") continue;
                foreach (var field in new[] { "who", "by" })
                {
                    // the null uuid is the game's "nobody" (a status with no source), not a creature
                    if (Uuid(payload[field]) is not { } id || id == Nobody || cast.Contains(id)) continue;
                    var was = strangers.GetValueOrDefault(id, (0, new SortedSet<string>(StringComparer.Ordinal)));
                    was.Item2.Add(kind);
                    strangers[id] = (was.Item1 + 1, was.Item2);
                }
            }
            foreach (var (id, (count, kinds)) in strangers)
                found.Add(new Surprise("participant", id, count, "not of the party and not staged through the ledger; in " + string.Join(", ", kinds)));
        }

        // rows the host could not place
        foreach (var item in unplaced ?? [])
            found.Add(new Surprise("unplaced", (string?)item?["kind"] ?? "", (int)(Whole(item?["rows"]) ?? 0), (string?)item?["why"] ?? ""));

        return found;
    }

    /// <summary>The entries the _suite.json files allow from <paramref name="folder"/> up to
    /// <paramref name="root"/> (the folder given to `gatekit test`), both included.</summary>
    public static IEnumerable<string> AllowedBySuites(string folder, string root)
    {
        var stop = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (var at = Path.GetFullPath(folder); at is not null; at = Path.GetDirectoryName(at))
        {
            var suite = Path.Combine(at, "_suite.json");
            if (File.Exists(suite) && GateKit.Json.JsonText.Read(suite, "tests/suite")["surprises"] is JsonArray list)
                foreach (var entry in list)
                    if ((string?)entry is { Length: > 0 } text) yield return text;
            if (string.Equals(at.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), stop, StringComparison.OrdinalIgnoreCase)) break;
            if (!at.StartsWith(stop, StringComparison.OrdinalIgnoreCase)) break;
        }
    }
}
