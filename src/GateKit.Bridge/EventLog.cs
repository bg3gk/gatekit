using System.Globalization;
using System.Text.Json.Nodes;
using GateKit.Json;

namespace GateKit.Bridge;

/// <summary>The head of a side's event log (events_&lt;side&gt;.json, schemas/events/head).</summary>
public sealed record EventHead(Side Side, long Boot, int Segment, int First, long Seq, int Rows,
    IReadOnlyList<JsonObject> Open, JsonObject Counts, IReadOnlyList<string> On)
{
    /// <summary>The side's tick count when the head was written; null from a bridge that does not say it.</summary>
    public long? Tick { get; init; }

    /// <summary>The oldest seq the kept segments can hold.</summary>
    public long FirstSeq => (long)(First - 1) * Rows + 1;
}

/// <summary>The rows of one boot of a side, as read from its segments.</summary>
public sealed class EventBoot(Side side, long boot, IReadOnlyList<JsonObject> rows, DateTime written, EventHead head)
{
    public Side Side { get; } = side;
    public long Boot { get; } = boot;
    /// <summary>The head it was read through: the rows still waiting, the counters, the families on.</summary>
    public EventHead Head { get; } = head;
    /// <summary>The oldest seq still kept: a reader asking from before it has missed rows.</summary>
    public long FirstSeq => Math.Max(Head.FirstSeq, 1);
    /// <summary>Every row of the boot that is kept, in order of seq.</summary>
    public IReadOnlyList<JsonObject> Rows { get; } = rows;
    /// <summary>When the last segment was written (the tick of the last row, or later).</summary>
    public DateTime Written { get; } = written;
    public long Seq => Rows.Count > 0 ? Seq0(Rows[^1]) : 0;
    /// <summary>The side's clock (ms) at the last row.</summary>
    public double LastT => Rows.Count > 0 ? (double)Rows[^1]["t"]! : 0;
    /// <summary>The side's tick at the last row.</summary>
    public long LastTick => Rows.Count > 0 ? (long)Rows[^1]["tick"]! : 0;

    static long Seq0(JsonObject row) => (long)row["seq"]!;

    /// <summary>The rows after <paramref name="seq"/>.</summary>
    public IEnumerable<JsonObject> After(long seq) => Rows.Where(r => Seq0(r) > seq);
}

/// <summary>
/// The bridge mod's event log as the host reads it (docs/contracts/bridge-protocol.md, "The event
/// log"): each side's head names its boot and last segment, the segments hold the rows. Full
/// segments never change and are read once; the last is read again when its file changed. Every
/// row is held to schemas/events/row; one that does not fit is left out and kept in
/// <see cref="Problems"/>. Rows are placed by the registry the side's game loaded
/// (events_&lt;side&gt;_kinds.json, read again when it changes), by <see cref="Built"/> where the game
/// wrote none; a row neither places is kept and marked `unplaced`, never left out. Reading asks
/// nothing of the game. Thread-safe.
/// </summary>
/// <param name="built">The registry this host was built with (default: the embedded one).</param>
public sealed class EventLogReader(string channel, EventKinds? built = null)
{
    /// <summary>The registry this host was built with: what rows are placed by when the game wrote no
    /// registry (a log read with no game, a bridge older than the copy), and for a kind the game's lacks.</summary>
    public EventKinds Built { get; } = built ?? EventKinds.Embedded;

    readonly Lock _lock = new();
    readonly Dictionary<string, (long Length, DateTime Written, EventKinds Kinds, List<JsonObject> Rows)> _segments = new(StringComparer.Ordinal);
    readonly Dictionary<Side, long> _pruned = [];
    readonly Dictionary<Side, (long Length, DateTime Written, EventKinds? Kinds)> _gameKinds = [];

    /// <summary>The bridge's folder.</summary>
    public string Channel { get; } = channel;

    /// <summary>Rows that did not fit the schema or a file that could not be read, newest last (at most 50).</summary>
    public List<string> Problems { get; } = [];

    public string HeadPath(Side side) => Path.Combine(Channel, $"events_{side.Name()}.json");

    /// <summary>The registry the side's game loaded, as the mod copied it at the start of its Lua state.</summary>
    public string KindsPath(Side side) => Path.Combine(Channel, $"events_{side.Name()}_kinds.json");

    /// <summary>The registry the side's running game loaded, or null when it wrote none (or an empty
    /// file: the mod could not read its own). Read again when the file changes.</summary>
    public EventKinds? GameKinds(Side side)
    {
        var path = KindsPath(side);
        var info = new FileInfo(path);
        lock (_lock)
        {
            if (!info.Exists) { _gameKinds.Remove(side); return null; }
            if (_gameKinds.TryGetValue(side, out var cached) && cached.Length == info.Length && cached.Written == info.LastWriteTimeUtc)
                return cached.Kinds;
            EventKinds? kinds = null;
            try
            {
                var text = System.Text.Encoding.UTF8.GetString(FileRetry.ReadAllBytes(path));
                if (text.Trim().Length > 0) kinds = EventKinds.Parse(text, path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonProblemException or FormatException)
            {
                Problem($"{path}: {e.Message} - rows of the {side.Name()} are placed by this build's registry");
            }
            _gameKinds[side] = (info.Length, info.LastWriteTimeUtc, kinds);
            return kinds;
        }
    }

    /// <summary>The registry the side's rows are placed by: its game's, else <see cref="Built"/>.</summary>
    public EventKinds KindsOf(Side side) => GameKinds(side) ?? Built;

    /// <summary>The registry of this copy: the server's game's, else the client's, else <see cref="Built"/>.</summary>
    public EventKinds Kinds => GameKinds(Side.Server) ?? GameKinds(Side.Client) ?? Built;

    /// <summary>The family of a kind by the registry in use, else by <see cref="Built"/>; null for a kind neither has.</summary>
    public string? FamilyOf(string kind) => Kinds.FamilyOf(kind) ?? Built.FamilyOf(kind);

    /// <summary>Which registry this copy's rows are placed by, in one line, naming how the game's
    /// differs from <see cref="Built"/>.</summary>
    public string KindsLine()
    {
        var server = GameKinds(Side.Server);
        var client = GameKinds(Side.Client);
        if (server is null && client is null) return "event kinds: this build's registry (the game wrote none to the channel)";
        var game = server ?? client!;
        var line = Built.Difference(game) is { } difference
            ? "event kinds: the game's registry, which differs from this build's (the host follows the game): " + difference
            : "event kinds: the game's registry, the same as this build's";
        if (server is not null && client is not null && server.Difference(client) is { } sides)
            line += $"; the client's differs from the server's: {sides}";
        return line;
    }

    /// <summary><see cref="KindsLine"/> when the game's registry differs from <see cref="Built"/>, else null.</summary>
    public string? KindsMismatch()
    {
        var server = GameKinds(Side.Server);
        var client = GameKinds(Side.Client);
        var differs = (server is not null && Built.Difference(server) is not null) || (client is not null && Built.Difference(client) is not null);
        return differs ? KindsLine() : null;
    }

    public string SegmentPath(Side side, long boot, int n) =>
        Path.Combine(Channel, string.Create(CultureInfo.InvariantCulture, $"events_{side.Name()}_{boot}_{n}.jsonl"));

    /// <summary>The side's head, or null when there is none or it cannot be read now.</summary>
    public EventHead? Head(Side side)
    {
        var path = HeadPath(side);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var parsed = JsonText.Parse(FileRetry.ReadAllBytes(path), path);
                JsonText.Check(parsed, "events/head");
                var root = parsed.Root!;
                var kinds = KindsOf(side);
                var open = (root["open"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
                foreach (var row in open) Place(row, kinds, path);
                var on = (root["on"] as JsonArray)?.Select(n => (string)n!).ToList() ?? [];
                return new EventHead(side, (long)root["boot"]!, (int)root["segment"]!, root["first"] is { } first ? (int)first : 1,
                    (long)root["seq"]!, (int)root["rows"]!, open, root["counts"] as JsonObject ?? [], on)
                { Tick = root["tick"] is { } tick ? (long)tick.GetValue<double>() : null };
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonProblemException)
            {
                // the game rewrites the head in place: read it again a moment later
                if (attempt == 2) Problem($"{path}: {e.Message}");
                Thread.Sleep(10);
            }
        }
        return null;
    }

    /// <summary>The current boot of the side with all its rows, or null when the side has no log.</summary>
    public EventBoot? Read(Side side)
    {
        if (Head(side) is not { } head) return null;
        lock (_lock)
        {
            if (!_pruned.TryGetValue(side, out var pruned) || pruned != head.Boot)
            {
                Prune(side, head.Boot);
                _pruned[side] = head.Boot;
            }
            var rows = new List<JsonObject>();
            var written = DateTime.MinValue;
            var kinds = KindsOf(side);
            // the head is written before the segment it begins: one more may already be there
            for (var n = Math.Max(1, head.First); n <= head.Segment + 1; n++)
            {
                var path = SegmentPath(side, head.Boot, n);
                var info = new FileInfo(path);
                if (!info.Exists) continue;
                if (!_segments.TryGetValue(path, out var cached) || cached.Length != info.Length || cached.Written != info.LastWriteTimeUtc
                    || cached.Kinds != kinds)
                {
                    cached = (info.Length, info.LastWriteTimeUtc, kinds, ReadSegment(path, kinds));
                    _segments[path] = cached;
                }
                rows.AddRange(cached.Rows);
                if (cached.Written > written) written = cached.Written;
            }
            // the oldest segments that went: their files are empty and are deleted here
            for (var n = 1; n < head.First; n++)
            {
                var path = SegmentPath(side, head.Boot, n);
                if (File.Exists(path)) { FileRetry.TryDelete(path); _segments.Remove(path); }
            }
            return new EventBoot(side, head.Boot, rows, written == DateTime.MinValue ? DateTime.UtcNow : written, head);
        }
    }

    List<JsonObject> ReadSegment(string path, EventKinds kinds)
    {
        for (var attempt = 0; ; attempt++)
        {
            var torn = false;
            var problems = new List<JsonProblem>();
            List<JsonObject> rows;
            try
            {
                rows = JsonText.ReadLines(path, "events/row", p =>
                {
                    if (p.Rule == "torn") torn = true;
                    else problems.Add(p);
                });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 3) { Problem($"{path}: {e.Message}"); return []; }
                Thread.Sleep(10);
                continue;
            }
            // a torn last line: the game is writing the file; read it again
            if (torn && attempt < 3) { Thread.Sleep(10); continue; }
            foreach (var p in problems) Problem(p.ToString());
            foreach (var row in rows) Place(row, kinds, path);
            return rows;
        }
    }

    /// <summary>A row placed by the side's registry, or by <see cref="Built"/> for a kind only it
    /// has. One neither places - a kind in neither, a `knows` or `attention` the kind does not
    /// declare - is kept and marked: `unplaced` says why. Undeclared payload fields are said.</summary>
    void Place(JsonObject row, EventKinds kinds, string source)
    {
        var name = (string?)row["kind"] ?? "";
        var by = kinds.Kinds.ContainsKey(name) ? kinds : Built.Kinds.ContainsKey(name) ? Built : null;
        List<string> unplaced, noted;
        if (by is null)
        {
            unplaced = [kinds == Built ? $"kind {name} is not in the registry" : $"kind {name} is in neither the game's registry nor this build's"];
            noted = [];
        }
        else
        {
            (unplaced, noted) = by.Check(row);
            if (by != kinds) noted.Add($"kind {name} is not in the game's registry; placed by this build's");
        }
        foreach (var n in noted) Problem($"{source}: seq {row["seq"]}: {n}");
        if (unplaced.Count == 0) return;
        row["unplaced"] = string.Join("; ", unplaced);
        Problem($"{source}: seq {row["seq"]}: {row["unplaced"]} - kept, marked unplaced");
    }

    void Problem(string text)
    {
        lock (Problems)
        {
            Problems.Add(text);
            if (Problems.Count > 50) Problems.RemoveAt(0);
        }
    }

    /// <summary>Deletes the side's segments of boots older than the one before <paramref name="boot"/>
    /// (the game cannot delete). Returns how many went.</summary>
    public int Prune(Side side, long boot)
    {
        if (!Directory.Exists(Channel)) return 0;
        var prefix = $"events_{side.Name()}_";
        var gone = 0;
        foreach (var file in Directory.EnumerateFiles(Channel, prefix + "*.jsonl"))
        {
            var parts = Path.GetFileNameWithoutExtension(file)[prefix.Length..].Split('_');
            if (parts.Length == 2 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var b) && b < boot - 1)
            {
                FileRetry.TryDelete(file);
                _segments.Remove(file);
                gone++;
            }
        }
        return gone;
    }
}
