using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GateKit.Copies;

/// <summary>
/// The ledgers of the coverage record (<c>research/coverage/record/*.jsonl</c>, and
/// <c>research/coverage/findings.jsonl</c>): append-only, one JSON object per line, written by the
/// tools under one cross-process lock, every closed field held to its vocabulary on write.
///
/// The summary files beside them (<c>seen/index.json</c>, <c>coverage.json</c>) say what is known;
/// these say when, by which copy, in which role and run - the questions two unattended runs could
/// not answer from the record (docs/contracts/cli.md 8.11).
/// </summary>
public static class Record
{
    /// <summary>The ledgers and the file each one is, relative to the coverage root.</summary>
    public static readonly IReadOnlyList<string> Ledgers = ["sightings", "replays", "wishes", "counts", "findings"];

    /// <summary>The closed fields of each ledger and the values they may hold.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string[]>> Vocabulary =
        new Dictionary<string, IReadOnlyDictionary<string, string[]>>(StringComparer.Ordinal)
        {
            ["sightings"] = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["side"] = ["client", "server"],
                ["source"] = ["tick", "trace"],
                ["state"] = ["sighted", "repeated"],
            },
            ["replays"] = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["via"] = ["test", "systems check"],
            },
            ["wishes"] = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["state"] = ["open", "sent", "fixed", "seen_live", "dropped"],
            },
            ["counts"] = new Dictionary<string, string[]>(StringComparer.Ordinal),
            ["findings"] = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["evidence"] = ["sighting", "repeated", "replayed", "field_value"],
                ["reached_by"] = ["play", "cheat", "play_with_cheat_setup"],
                ["kind"] = ["finding", "lead"],
            },
        };

    /// <summary>The fields every row of a ledger carries.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Required = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["sightings"] = ["type", "side", "time", "instance", "role", "run", "stretch", "source", "state"],
        ["replays"] = ["recipe", "time", "instance", "by", "ok"],
        ["wishes"] = ["id", "time", "from", "text", "state"],
        ["counts"] = ["time", "run", "definition"],
        ["findings"] = ["component", "evidence", "reached_by", "kind"],
    };

    /// <summary>The path of a ledger under <paramref name="coverageRoot"/> (research/coverage).</summary>
    public static string PathOf(string coverageRoot, string ledger) => ledger == "findings"
        ? Path.Combine(coverageRoot, "findings.jsonl")
        : Path.Combine(coverageRoot, "record", ledger + ".jsonl");

    /// <summary>The time a row is written: UTC, ISO 8601, to the second.</summary>
    public static string Now() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    /// <summary>Role, run and stretch: what was said, else GATEKIT_ROLE / GATEKIT_RUN /
    /// GATEKIT_STRETCH, else "unknown". The record never guesses a run's name from free text.</summary>
    public static (string Role, string Run, string Stretch) Who(string? role = null, string? run = null, string? stretch = null)
    {
        static string Pick(string? said, string variable) =>
            said is { Length: > 0 } ? said
            : Environment.GetEnvironmentVariable(variable) is { Length: > 0 } set ? set
            : "unknown";
        return (Pick(role, "GATEKIT_ROLE"), Pick(run, "GATEKIT_RUN"), Pick(stretch, "GATEKIT_STRETCH"));
    }

    /// <summary>What is wrong with a row of <paramref name="ledger"/> (empty: nothing).</summary>
    public static IReadOnlyList<string> Problems(string ledger, JsonObject row)
    {
        if (!Required.TryGetValue(ledger, out var required)) return [$"no ledger called {ledger}"];
        var problems = new List<string>();
        foreach (var field in required)
            if (row[field] is null) problems.Add($"`{field}` is missing");
        foreach (var (field, allowed) in Vocabulary[ledger])
        {
            if (row[field] is not { } value) continue;
            if (value is not JsonValue text || !text.TryGetValue<string>(out var said) || !allowed.Contains(said))
                problems.Add($"`{field}` is {value.ToJsonString()}; it is one of {string.Join(", ", allowed)}");
        }
        if (ledger == "replays" && row["ok"] is { } ok && (ok is not JsonValue flag || !flag.TryGetValue<bool>(out _)))
            problems.Add("`ok` is true or false");
        if (ledger == "wishes" && row["id"] is JsonValue id && id.TryGetValue<string>(out var name) && !IsWishId(name))
            problems.Add($"`id` is W-0001 and on, not {name}");
        return problems;
    }

    /// <summary>A wish's id: W- and four digits or more.</summary>
    public static bool IsWishId(string id) =>
        id.Length >= 6 && id.StartsWith("W-", StringComparison.Ordinal) && id[2..].All(char.IsAsciiDigit);

    /// <summary>Refuses a row that does not fit its ledger, saying what and why; nothing is written.</summary>
    public static void Validate(string ledger, JsonObject row)
    {
        var problems = Problems(ledger, row);
        if (problems.Count > 0)
            throw new ArgumentException($"a {ledger} row that does not fit the ledger ({string.Join("; ", problems)}); nothing was written");
    }

    /// <summary>Appends rows to a ledger, each validated first (all or none), with LF endings.
    /// Takes the record's lock unless the caller says it holds it.</summary>
    public static void Append(string coverageRoot, string ledger, IReadOnlyCollection<JsonObject> rows, bool held = false)
    {
        if (rows.Count == 0) return;
        foreach (var row in rows) Validate(ledger, row);
        var text = new StringBuilder();
        foreach (var row in rows) text.Append(row.ToJsonString(Line)).Append('\n');
        using var lockHeld = held ? null : Lock(coverageRoot);
        var path = PathOf(coverageRoot, ledger);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // The record is committed while a run goes on, and git maps a file it is reading: for that
        // moment Windows refuses the write. Somebody else's reader is waited out, briefly.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.AppendAllText(path, text.ToString(), new UTF8Encoding(false));
                return;
            }
            catch (IOException) when (attempt < 40)
            {
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>One row, appended.</summary>
    public static void Append(string coverageRoot, string ledger, JsonObject row, bool held = false) =>
        Append(coverageRoot, ledger, [row], held);

    /// <summary>The rows of a ledger in the order written; a line that is not a JSON object is
    /// reported through <paramref name="unreadable"/> with its number and left out.</summary>
    public static List<JsonObject> Read(string coverageRoot, string ledger, Action<int, string>? unreadable = null)
    {
        var path = PathOf(coverageRoot, ledger);
        var rows = new List<JsonObject>();
        if (!File.Exists(path)) return rows;
        var number = 0;
        foreach (var line in File.ReadLines(path))
        {
            number++;
            if (line.Trim().Length == 0) continue;
            try
            {
                if (JsonNode.Parse(line) is JsonObject row) { rows.Add(row); continue; }
            }
            catch (JsonException) { }
            unreadable?.Invoke(number, line);
        }
        return rows;
    }

    /// <summary>Adds a wish under the next id (W-0001 and on), under the record's lock unless the
    /// caller holds it. Returns the row written.</summary>
    public static JsonObject AddWish(string root, string from, string text, string state = "open", string? note = null,
        string? commit = null, string? was = null, bool held = false)
    {
        using var lockHeld = held ? null : Lock(root);
        static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        var last = Read(root, "wishes").Select(w => Text(w["id"]))
            .Where(id => id is not null && IsWishId(id)).Select(id => int.Parse(id![2..], CultureInfo.InvariantCulture))
            .DefaultIfEmpty(0).Max();
        var row = new JsonObject
        {
            ["id"] = $"W-{last + 1:D4}",
            ["time"] = Now(),
            ["from"] = from,
            ["text"] = text,
            ["state"] = state,
        };
        if (commit is { Length: > 0 }) row["commit"] = commit;
        if (note is { Length: > 0 }) row["note"] = note;
        if (was is { Length: > 0 }) row["was"] = was;
        Append(root, "wishes", row, held: true);
        return row;
    }

    static readonly JsonSerializerOptions Line = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// One writer of the committed record at a time, across processes: every game copy has a server
    /// of its own, and they all write the same few files. Held as an open file nobody else may open,
    /// so a process that dies lets go by dying. Outside the repository, so nothing is left in it.
    /// Not re-entrant: a caller that holds it passes <c>held</c> to <see cref="Append(string, string, IReadOnlyCollection{JsonObject}, bool)"/>.
    /// </summary>
    public static IDisposable Lock(string coverageRoot, int seconds = 120)
    {
        var name = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(Path.GetFullPath(coverageRoot).ToUpperInvariant())))[..16];
        var path = Path.Combine(Path.GetTempPath(), $"gatekit-record-{name}.lock");
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            // A lock file being closed (it deletes itself on close) or held by a scanner answers
            // "access denied" rather than a sharing violation for a moment: that is "held" too.
            // Taken as a failure, the second writer once got in while the first held the record
            // (the test of this failed about one full run in three).
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && DateTime.UtcNow < until)
            {
                Thread.Sleep(50);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new CopyException($"another session has been writing the coverage record for over {seconds} s; "
                    + "nothing was written here - ask again, and if it repeats, a server died holding it open");
            }
        }
    }

    /// <summary>The coverage root of a checkout: the one the command is run in (a worktree's own
    /// files), else the one the program was built in.</summary>
    public static string? CoverageRoot()
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "GateKit.slnx"))) return Path.Combine(dir.FullName, "research", "coverage");
        return CopySettings.RepositoryRoot() is { } repository ? Path.Combine(repository, "research", "coverage") : null;
    }

    /// <summary>The coverage root a recipe file belongs to: <c>&lt;repo&gt;/tests/_coverage/x.lua</c>
    /// gives <c>&lt;repo&gt;/research/coverage</c> when that folder exists, else null (not a recipe).</summary>
    public static string? CoverageRootOfRecipe(string recipe)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(recipe));
        if (folder is null || Path.GetFileName(folder) != "_coverage") return null;
        var tests = Path.GetDirectoryName(folder);
        if (tests is null || Path.GetFileName(tests) != "tests") return null;
        var root = Path.Combine(Path.GetDirectoryName(tests)!, "research", "coverage");
        return Directory.Exists(root) ? root : null;
    }
}
