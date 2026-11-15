using System.Globalization;
using System.Text.Json.Nodes;

namespace GateKit.Bridge;

/// <summary>
/// The event log as a caller sees it: ONE stream, the two sides' logs merged (docs/contracts/
/// bridge-protocol.md, "One stream"). Every row carries its `side`. The order is the game's own
/// clock first: `t`, the monotonic milliseconds both Lua states of the one game process read from
/// the same clock; then `tick` and `seq` within a side (a side's rows keep their order); rows of
/// both sides in the same millisecond put the server's first. The sides' ticks are counters of
/// their own Lua states and are not compared with each other.
/// A cursor is one text, `client:&lt;boot&gt;.&lt;seq&gt;,server:&lt;boot&gt;.&lt;seq&gt;`; a caller never names a side.
/// </summary>
public sealed class EventStream(EventLogReader log)
{
    static readonly Side[] Sides = [Side.Server, Side.Client];

    public EventLogReader Log { get; } = log;

    /// <summary>Only rows a player could know (`knows = player`): what a host gives a client it
    /// serves as fair. The filter is here, in the host, never the client's.</summary>
    public bool Fair { get; set; }

    /// <summary>Whether this stream gives the row at all. A fair stream takes an `unplaced` row as
    /// omniscient: what its registry would say of it is not known.</summary>
    public bool Shown(JsonObject row) => !Fair || ((string?)row["knows"] == "player" && row["unplaced"] is null);

    /// <summary>The rows after <paramref name="since"/> (up to <paramref name="until"/>, a cursor; absent:
    /// to the end) that no registry places, one entry per kind: { kind, rows, why, withheld? } -
    /// `withheld` when this stream is fair and did not give them. Whatever a caller filtered, these
    /// are said.</summary>
    public JsonArray Unplaced(string? since, string? until = null)
    {
        var from = ParseCursor(since);
        var to = until is null ? null : ParseCursor(until);
        var byKind = new SortedDictionary<string, (long Rows, string Why)>(StringComparer.Ordinal);
        foreach (var boot in Boots())
        {
            var after = from.TryGetValue(boot.Side, out var p) && p.Boot == boot.Boot ? p.Seq : 0;
            var upTo = to is not null && to.TryGetValue(boot.Side, out var q) && q.Boot == boot.Boot ? q.Seq : long.MaxValue;
            foreach (var row in boot.After(after))
            {
                if ((long)row["seq"]! > upTo || row["unplaced"] is not { } why) continue;
                var kind = (string?)row["kind"] ?? "";
                var was = byKind.GetValueOrDefault(kind, (0, (string)why!));
                byKind[kind] = (was.Rows + 1, was.Why);
            }
        }
        var list = new JsonArray();
        foreach (var (kind, (rows, why)) in byKind)
        {
            var item = new JsonObject { ["kind"] = kind, ["rows"] = rows, ["why"] = why };
            if (Fair) item["withheld"] = true;
            list.Add(item);
        }
        return list;
    }

    /// <summary>For each side the cursor is behind the oldest row kept: { side, after, first } -
    /// the rows between went (the store is bounded); never a silent gap.</summary>
    public JsonArray Dropped(string? cursor)
    {
        var at = ParseCursor(cursor);
        var gaps = new JsonArray();
        foreach (var boot in Boots())
        {
            var after = at.TryGetValue(boot.Side, out var p) && p.Boot == boot.Boot ? p.Seq : 0;
            if (after + 1 < boot.FirstSeq)
                gaps.Add(new JsonObject { ["side"] = boot.Side.Name(), ["after"] = after, ["first"] = boot.FirstSeq });
        }
        return gaps;
    }

    /// <summary>The merged order: t, then the server before the client, then tick, then seq.</summary>
    public static int Compare(JsonObject a, JsonObject b)
    {
        var byTime = ((double)a["t"]!).CompareTo((double)b["t"]!);
        if (byTime != 0) return byTime;
        var sa = (string)a["side"]! == "server" ? 0 : 1;
        var sb = (string)b["side"]! == "server" ? 0 : 1;
        if (sa != sb) return sa.CompareTo(sb);
        var byTick = ((long)a["tick"]!).CompareTo((long)b["tick"]!);
        return byTick != 0 ? byTick : ((long)a["seq"]!).CompareTo((long)b["seq"]!);
    }

    /// <summary>A cursor's positions; an empty or absent text is "from the start of each boot".</summary>
    public static Dictionary<Side, (long Boot, long Seq)> ParseCursor(string? cursor)
    {
        var at = new Dictionary<Side, (long, long)>();
        if (string.IsNullOrWhiteSpace(cursor)) return at;
        foreach (var part in cursor.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = part.IndexOf(':');
            var dot = part.IndexOf('.', colon + 1);
            if (colon < 0 || dot < 0
                || !long.TryParse(part[(colon + 1)..dot], NumberStyles.None, CultureInfo.InvariantCulture, out var boot)
                || !long.TryParse(part[(dot + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var seq))
                throw new ArgumentException($"a cursor is client:<boot>.<seq>,server:<boot>.<seq> (what an answer gave as `next`), not '{cursor}'");
            at[SideNames.Parse(part[..colon])] = (boot, seq);
        }
        return at;
    }

    public static string FormatCursor(IReadOnlyDictionary<Side, (long Boot, long Seq)> at) =>
        string.Join(",", new[] { Side.Client, Side.Server }.Where(at.ContainsKey)
            .Select(s => string.Create(CultureInfo.InvariantCulture, $"{s.Name()}:{at[s].Boot}.{at[s].Seq}")));

    /// <summary>The current boot of each side that has a log.</summary>
    public IReadOnlyList<EventBoot> Boots() => [.. Sides.Select(Log.Read).OfType<EventBoot>()];

    /// <summary>A row with its side, as the stream gives it.</summary>
    public static JsonObject WithSide(JsonObject row, Side side)
    {
        var copy = (JsonObject)row.DeepClone();
        copy["side"] = side.Name();
        return copy;
    }

    /// <summary>The rows after the cursor, merged, filtered; `next` is the cursor past every row
    /// read (filtered out or not). A side whose boot changed is read from its start.</summary>
    public (List<JsonObject> Rows, string Next) Since(string? cursor, Func<JsonObject, bool>? keep = null, int? limit = null)
    {
        var at = ParseCursor(cursor);
        var rows = new List<JsonObject>();
        var next = new Dictionary<Side, (long, long)>(at);
        foreach (var boot in Boots())
        {
            var from = at.TryGetValue(boot.Side, out var p) && p.Boot == boot.Boot ? p.Seq : 0;
            foreach (var row in boot.After(from))
                if (Shown(row) && (keep is null || keep(row))) rows.Add(WithSide(row, boot.Side));
            next[boot.Side] = (boot.Boot, boot.Seq);
        }
        rows.Sort(Compare);
        if (limit is { } n && rows.Count > n)
        {
            // the cursor stops after the last row given, on each side
            rows = rows.Take(n).ToList();
            foreach (var boot in Boots())
            {
                var last = rows.LastOrDefault(r => (string)r["side"]! == boot.Side.Name());
                var from = at.TryGetValue(boot.Side, out var p) && p.Boot == boot.Boot ? p.Seq : 0;
                next[boot.Side] = (boot.Boot, last is null ? from : (long)last["seq"]!);
            }
        }
        return (rows, FormatCursor(next));
    }

    /// <summary>Per family, both sides summed: { rows, bytes, dropped } in the current boots.</summary>
    public JsonObject Counts()
    {
        var sums = new SortedDictionary<string, (long Rows, long Bytes, long Dropped)>(StringComparer.Ordinal);
        foreach (var boot in Boots())
        {
            foreach (var (family, node) in boot.Head.Counts)
            {
                if (node is not JsonObject c) continue;
                var s = sums.GetValueOrDefault(family);
                sums[family] = (s.Rows + (long)c["rows"]!, s.Bytes + (long)c["bytes"]!, s.Dropped + (long)c["dropped"]!);
            }
        }
        var o = new JsonObject();
        foreach (var (family, s) in sums) o[family] = new JsonObject { ["rows"] = s.Rows, ["bytes"] = s.Bytes, ["dropped"] = s.Dropped };
        return o;
    }

    /// <summary>The switchable families on now, on either side.</summary>
    public JsonArray On() => new([.. Boots().SelectMany(b => b.Head.On).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
        .Select(n => (JsonNode)JsonValue.Create(n))]);

    /// <summary>The cursor at the end of both logs now.</summary>
    public string Mark() => FormatCursor(Boots().ToDictionary(b => b.Side, b => (b.Boot, b.Seq)));
}
