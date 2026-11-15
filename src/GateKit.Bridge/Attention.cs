using System.Text.Json.Nodes;

namespace GateKit.Bridge;

/// <summary>
/// What waits for the player, or changed - a view of the event log (docs/contracts/bridge-protocol.md,
/// "Attention"). A row whose `attention` is begin or hold opens the waiting state under its `key`, end
/// or release closes it; begin, end and news rows are what a caller is told. This says what a caller
/// has not been told yet: everything still waiting, and every such row after the caller's last look.
/// Reading costs the log's files (the changed ones) and no call into the game.
/// </summary>
public sealed class AttentionReader
{
    static readonly Side[] Sides = [Side.Server, Side.Client];
    static readonly HashSet<string> Told = ["begin", "end", "news"];
    static readonly HashSet<string> Opens = ["begin", "hold"];
    static readonly HashSet<string> Closes = ["end", "release"];

    public AttentionReader(string channel) : this(new EventLogReader(channel)) { }

    public AttentionReader(EventLogReader log)
    {
        Log = log;
        Stream = new EventStream(log);
    }

    /// <summary>The event log it is a view of.</summary>
    public EventLogReader Log { get; }

    /// <summary>The same log as one stream.</summary>
    public EventStream Stream { get; }

    /// <summary>Only rows a player could know (`knows = player`), in this view and in <see cref="Stream"/>.</summary>
    public bool Fair
    {
        get => Stream.Fair;
        init => Stream.Fair = value;
    }

    /// <summary>The bridge's folder.</summary>
    public string Channel => Log.Channel;

    static string? JsonText(JsonNode? node) => GateKit.Json.JsonText.Text(node);

    /// <summary>The attention rows of both sides folded in the stream's order (<see cref="EventStream.Compare"/>):
    /// what waits now (key -> the row that opened it) and the rows that are told. One waiting state for
    /// one key whichever side reports it: a begin of a key already open with the same kind on the other
    /// side, and an end of a key already closed, are the same state reported twice and are left out.</summary>
    public (Dictionary<string, JsonObject> Open, List<JsonObject> Told) Fold(IReadOnlyList<EventBoot> boots)
    {
        var rows = new List<JsonObject>();
        foreach (var boot in boots)
        {
            var kept = new HashSet<long>();
            foreach (var row in boot.Rows)
            {
                if (row["attention"] is null || !Stream.Shown(row)) continue;
                rows.Add(EventStream.WithSide(row, boot.Side));
                kept.Add((long)row["seq"]!);
            }
            // what still waits although its segment was dropped: the head keeps it
            foreach (var row in boot.Head.Open)
                if (!kept.Contains((long)row["seq"]!) && Stream.Shown(row)) rows.Add(EventStream.WithSide(row, boot.Side));
        }
        rows.Sort(EventStream.Compare);
        var open = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var told = new List<JsonObject>();
        foreach (var row in rows)
        {
            var attention = JsonText(row["attention"])!;
            var key = JsonText(row["key"]);
            if (key is not null && Opens.Contains(attention))
            {
                if (open.TryGetValue(key, out var was) && (string)was["kind"]! == (string)row["kind"]! && (string)was["side"]! != (string)row["side"]!)
                    continue;
                open[key] = row;
            }
            else if (key is not null && Closes.Contains(attention))
            {
                if (!open.Remove(key)) continue;
            }
            if (Told.Contains(attention)) told.Add(row);
        }
        return (open, told);
    }

    /// <summary>The items to show a caller whose last look is <paramref name="cursor"/>: the told rows
    /// since, then what waits now, both sides as one; the cursor moves past them. Empty when there is nothing.</summary>
    public JsonArray Report(AttentionCursor cursor, DateTime? now = null)
    {
        var items = new JsonArray();
        var at = now ?? DateTime.UtcNow;
        var boots = Stream.Boots();
        if (boots.Count == 0) return items;
        var (open, told) = Fold(boots);
        // the clock of the game: both sides read the same monotonic clock; the newest row and its file
        var last = boots.MaxBy(b => b.LastT)!;
        // rows the caller was not told went before it looked (the store is bounded): it is told that
        foreach (var boot in boots)
        {
            var seen = cursor.Seen(boot.Side, boot.Boot);
            if (seen >= 0 && seen + 1 < boot.FirstSeq)
                items.Add(new JsonObject { ["what"] = "dropped", ["side"] = boot.Side.Name(), ["after"] = seen, ["first"] = boot.FirstSeq });
        }
        // rows no registry places are said once per kind to each caller, given or withheld
        foreach (var boot in boots)
            foreach (var row in boot.Rows)
            {
                if (row["unplaced"] is not { } why || !cursor.TellUnplaced((string?)row["kind"] ?? "")) continue;
                var item = new JsonObject { ["what"] = "unplaced", ["kind"] = (string?)row["kind"] ?? "", ["error"] = (string)why!, ["side"] = boot.Side.Name() };
                if (Fair) item["withheld"] = true;
                items.Add(item);
            }
        foreach (var row in told)
        {
            var side = SideNames.Parse((string)row["side"]!);
            var boot = boots.First(b => b.Side == side);
            var seen = cursor.Seen(side, boot.Boot);
            // a caller's first look is told what waits, not the history before it
            if (seen >= 0 && (long)row["seq"]! > seen) items.Add(Compact(row, boot, last, at, waiting: false));
        }
        foreach (var row in open.Values.OrderBy(r => r, Comparer<JsonObject>.Create(EventStream.Compare)))
            items.Add(Compact(row, boots.First(b => b.Side.Name() == (string)row["side"]!), last, at, waiting: true));
        foreach (var boot in boots) cursor.Move(boot.Side, boot.Boot, boot.Seq);
        return items;
    }

    /// <summary>The last seq of each side right now, without moving any cursor.</summary>
    public IReadOnlyDictionary<Side, (long Boot, long Seq)> Marks()
    {
        var marks = new Dictionary<Side, (long, long)>();
        foreach (var side in Sides)
        {
            if (Log.Read(side) is { } boot) marks[side] = (boot.Boot, boot.Seq);
        }
        return marks;
    }

    /// <summary>The payload fields an item carries (common/attention).</summary>
    static readonly HashSet<string> Kept = ["window", "choices", "who", "name", "job", "kind", "status", "error", "level",
        "trader", "state", "gameOver", "combat", "answers", "speaker", "text", "ready", "canTrade",
        "check", "skill", "dc", "advantage", "modifiers", "bonus", "natural", "total", "success", "critical"];

    static JsonObject Compact(JsonObject row, EventBoot boot, EventBoot last, DateTime now, bool waiting)
    {
        var o = new JsonObject();
        if (!waiting) o["seq"] = (long)row["seq"]!;
        o["what"] = (string)row["kind"]!;
        if (row["payload"] is JsonObject payload)
        {
            foreach (var (key, value) in payload)
            {
                if (Kept.Contains(key) && value is not null) o[key] = value.DeepClone();
            }
        }
        if (waiting) o["waiting"] = true;
        o["side"] = boot.Side.Name();
        // how long ago: the game's clock from the row to the newest row of either side, then the
        // host's from the write of that row's file
        var t = (double)row["t"]!;
        o["since_ms"] = (long)Math.Max(0, last.LastT - t + (now - last.Written).TotalMilliseconds);
        o["since_ticks"] = Math.Max(0, boot.LastTick - (long)row["tick"]!);
        return o;
    }
}

/// <summary>How far a caller has been told, per side: a boot and the last seq of it. A new boot of
/// a side (a Lua reset, a load) is news from its start. Thread-safe.</summary>
public sealed class AttentionCursor
{
    readonly Dictionary<Side, (long Boot, long Seq)> _seen = [];
    readonly HashSet<string> _unplaced = new(StringComparer.Ordinal);
    readonly Lock _lock = new();

    /// <summary>Whether the caller is still to be told that rows of <paramref name="kind"/> are
    /// unplaced: true once per kind.</summary>
    public bool TellUnplaced(string kind)
    {
        lock (_lock) return _unplaced.Add(kind);
    }

    /// <summary>The last seq told for this boot of the side; 0 for a new boot of a side looked at
    /// before (everything of it is new); -1 when the side was never looked at.</summary>
    public long Seen(Side side, long boot)
    {
        lock (_lock)
        {
            if (!_seen.TryGetValue(side, out var s)) return -1;
            return s.Boot == boot ? s.Seq : 0;
        }
    }

    public void Move(Side side, long boot, long seq)
    {
        lock (_lock) _seen[side] = (boot, seq);
    }

    /// <summary>Where this cursor stands on a side, or null when it never looked at it.</summary>
    public (long Boot, long Seq)? Get(Side side)
    {
        lock (_lock) return _seen.TryGetValue(side, out var s) ? s : null;
    }
}

/// <summary>A wait that ends on an event rather than on a clock: the first new attention item (read
/// from the log every <see cref="Poll"/>), the first new rows a caller keeps, a condition coming
/// true, or the limit - and says which.</summary>
public static class AttentionWait
{
    /// <summary>How often the log is looked at: a few game frames.</summary>
    public static TimeSpan Poll { get; set; } = TimeSpan.FromMilliseconds(25);

    /// <summary>How often the condition is asked (each ask is a call into the game).</summary>
    public static TimeSpan ConditionPoll { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>What ended the wait: `attention`, `rows`, `condition` or `timeout` (with
    /// <see cref="Why"/>); the items, the rows kept and the cursor past them, the condition's value.</summary>
    public sealed record Outcome(string EndedBy, JsonArray Items, JsonNode? Value)
    {
        public IReadOnlyList<JsonObject> Rows { get; init; } = [];
        public string? Next { get; init; }
        public string? Why { get; init; }
    }

    /// <param name="reader">The view of the bridge's event log.</param>
    /// <param name="onAttention">End on any attention item that appears after the start.</param>
    /// <param name="condition">Asked every <see cref="ConditionPoll"/>; a truthy answer ends the wait.</param>
    /// <param name="timeout">The last resort; counted by <paramref name="guard"/> when one is given.</param>
    /// <param name="cancel">Ends the wait at once.</param>
    /// <param name="from">The caller's own cursor (what it has been told): a row it has not been told
    /// yet - one written between its last call and this wait - ends the wait at once instead of being
    /// skipped. Without it the wait starts at "now". It is read, never moved.</param>
    /// <param name="guard">The limit in the game's ticks (<see cref="TickGuard"/>); without one the
    /// timeout is the host's clock.</param>
    /// <param name="rows">End on the first rows after <c>Cursor</c> that <c>Keep</c> accepts (all rows
    /// when it is null): the outcome `rows`, with the rows and the cursor past every row read.</param>
    public static async Task<Outcome> RunAsync(AttentionReader reader, bool onAttention,
        Func<CancellationToken, Task<JsonNode?>>? condition, TimeSpan timeout, CancellationToken cancel = default,
        AttentionCursor? from = null, TickGuard? guard = null, (string? Cursor, Func<JsonObject, bool>? Keep)? rows = null)
    {
        var rowCursor = rows?.Cursor;
        // a cursor of its own: at the caller's last look where there is one, else at "now"
        var cursor = new AttentionCursor();
        foreach (var (side, mark) in reader.Marks())
        {
            if (from?.Get(side) is { } told) cursor.Move(side, told.Boot, told.Seq);
            else cursor.Move(side, mark.Boot, mark.Seq);
        }
        foreach (var side in new[] { Side.Client, Side.Server })
        {
            if (cursor.Seen(side, 0) < 0) cursor.Move(side, 0, 0);
        }
        var deadline = DateTime.UtcNow + timeout;
        Task<JsonNode?>? asking = null;
        var nextAsk = DateTime.UtcNow;
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            if (onAttention)
            {
                var items = new JsonArray();
                foreach (var item in reader.Report(cursor))
                {
                    // only what began or happened since the start: a state that was already waiting is not news;
                    // unplaced rows are said in the caller's own attention, not as an end of its wait
                    if (item?["waiting"] is null && (string?)item!["what"] != "unplaced") items.Add(item.DeepClone());
                }
                if (items.Count > 0) return new Outcome("attention", items, null);
            }
            if (rows is { } wanted)
            {
                var (found, next) = reader.Stream.Since(rowCursor, wanted.Keep);
                rowCursor = next;
                if (found.Count > 0) return new Outcome("rows", [], null) { Rows = found, Next = next };
            }
            if (condition is not null)
            {
                if (asking is { IsCompleted: true })
                {
                    var value = await asking;
                    asking = null;
                    if (Truthy(value)) return new Outcome("condition", [], value);
                }
                if (asking is null && DateTime.UtcNow >= nextAsk)
                {
                    asking = condition(cancel);
                    nextAsk = DateTime.UtcNow + ConditionPoll;
                }
            }
            var over = guard is not null ? guard.Over()
                : DateTime.UtcNow > deadline ? $"nothing came in {timeout.TotalSeconds:0.#} s" : null;
            if (over is not null) return new Outcome("timeout", [], null) { Next = rowCursor, Why = over };
            await Task.Delay(Poll, cancel);
        }
    }

    /// <summary>Lua's truth: nil and false are false, everything else true.</summary>
    public static bool Truthy(JsonNode? value) =>
        value is not null && !(value is JsonValue v && v.TryGetValue<bool>(out var b) && !b);
}
