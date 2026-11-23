using GateKit.Bridge;

namespace GateKit.UnitTests;

/// <summary>The host's reading of the event log (docs/contracts/bridge-protocol.md, "The event
/// log"): the head, the segments of the current boot, rows held to the schema, old boots pruned.</summary>
public sealed class EventLogTests : IDisposable
{
    readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    /// <summary>Writes the side's head and its segments as the mod does: one row per line.</summary>
    internal static void WriteLog(string channel, string side, long boot, int rowsPerSegment, params string[] rows)
    {
        var segments = Math.Max(1, (rows.Length + rowsPerSegment - 1) / rowsPerSegment);
        File.WriteAllText(Path.Combine(channel, $"events_{side}.json"),
            $$"""{"side":"{{side}}","boot":{{boot}},"segment":{{segments}},"seq":{{rows.Length}},"rows":{{rowsPerSegment}}}""");
        for (var n = 1; n <= segments; n++)
        {
            var lines = rows.Skip((n - 1) * rowsPerSegment).Take(rowsPerSegment);
            File.WriteAllText(Path.Combine(channel, $"events_{side}_{boot}_{n}.jsonl"), string.Join("\n", lines) + "\n");
        }
    }

    internal static string Row(long seq, string kind, double t, string extra = "", long tick = 0) =>
        $$"""{"seq":{{seq}},"tick":{{tick}},"t":{{t}},"kind":"{{kind}}","knows":"{{(EventKinds.Embedded.Kinds.TryGetValue(kind, out var k) ? k.Knows : "player")}}"{{extra}}}""";

    [Fact]
    public void The_rows_of_the_current_boot_come_from_every_segment_in_order()
    {
        WriteLog(_folder.Path, "server", 3, 2, Row(1, "game_state", 10), Row(2, "note", 20, ""","payload":{"note":"a"}"""),
            Row(3, "combat_started", 30, ""","key":"combat","attention":"begin" """), Row(4, "combat_ended", 40, ""","key":"combat","attention":"end" """),
            Row(5, "job_done", 50, ""","attention":"news" """));
        var reader = new EventLogReader(_folder.Path);
        var boot = reader.Read(Side.Server)!;
        Assert.Equal(3, boot.Boot);
        Assert.Equal([1L, 2, 3, 4, 5], boot.Rows.Select(r => (long)r["seq"]!));
        Assert.Equal(5, boot.Seq);
        Assert.Equal(50, boot.LastT);
        Assert.Equal(["job_done"], boot.After(4).Select(r => (string)r["kind"]!));
        Assert.Null(reader.Read(Side.Client));
    }

    [Fact]
    public void A_segment_caught_while_the_game_rewrites_it_does_not_lose_rows()
    {
        // the game rewrites the head's segment in place, whole, every tick it has rows: a read in
        // that moment finds it empty or cut at a line's end, while the head says how far it goes
        WriteLog(_folder.Path, "client", 432, 128, Row(1, "game_state", 10), Row(2, "game_state", 20), Row(3, "note", 30, ""","payload":{"note":"a"}"""));
        var reader = new EventLogReader(_folder.Path);
        Assert.Equal(3, reader.Read(Side.Client)!.Seq);
        var segment = reader.SegmentPath(Side.Client, 432, 1);
        File.WriteAllText(segment, "");
        Assert.Equal([1L, 2, 3], reader.Read(Side.Client)!.Rows.Select(r => (long)r["seq"]!));
        File.WriteAllText(segment, Row(1, "game_state", 10) + "\n" + Row(2, "game_state", 20) + "\n");
        Assert.Equal(3, reader.Read(Side.Client)!.Seq);
        // written whole again, with a row more: read
        File.WriteAllText(segment, string.Join("\n", Row(1, "game_state", 10), Row(2, "game_state", 20),
            Row(3, "note", 30, ""","payload":{"note":"a"}"""), Row(4, "game_state", 40)) + "\n");
        Assert.Equal(4, reader.Read(Side.Client)!.Seq);
        // a reader that never read it whole takes what is there, after a few reads
        File.WriteAllText(segment, "");
        Assert.Equal(0, new EventLogReader(_folder.Path).Read(Side.Client)!.Seq);
    }

    [Fact]
    public void A_row_outside_the_vocabulary_is_left_out_and_said()
    {
        WriteLog(_folder.Path, "client", 1, 8, Row(1, "trade", 10, ""","key":"window:trade","attention":"begin" """), Row(2, "DialogStarted", 20),
            Row(3, "trade_ended", 30, ""","key":"window:trade","attention":"end" """));
        var reader = new EventLogReader(_folder.Path);
        Assert.Equal(["trade", "trade_ended"], reader.Read(Side.Client)!.Rows.Select(r => (string)r["kind"]!));
        Assert.Contains(reader.Problems, p => p.Contains("events_client_1_1.jsonl:2", StringComparison.Ordinal));
    }

    [Fact]
    public void A_new_boot_prunes_the_files_of_boots_before_the_previous_one()
    {
        WriteLog(_folder.Path, "server", 1, 4, Row(1, "note", 1));
        WriteLog(_folder.Path, "server", 2, 4, Row(1, "note", 1));
        WriteLog(_folder.Path, "server", 3, 4, Row(1, "note", 1), Row(2, "note", 2));
        var reader = new EventLogReader(_folder.Path);
        Assert.Equal(2, reader.Read(Side.Server)!.Rows.Count);
        Assert.False(File.Exists(Path.Combine(_folder.Path, "events_server_1_1.jsonl")));
        Assert.True(File.Exists(Path.Combine(_folder.Path, "events_server_2_1.jsonl")));
    }

    [Fact]
    public void Both_sides_are_one_stream_ordered_by_the_game_clock_with_the_side_on_each_row()
    {
        // ticks differ between the sides: the order is the shared clock t, then the server, then tick, seq
        WriteLog(_folder.Path, "server", 2, 8, Row(1, "game_state", 100, tick: 900), Row(2, "job_done", 300, ""","attention":"news" """, tick: 950));
        WriteLog(_folder.Path, "client", 4, 8, Row(1, "game_state", 100, tick: 10), Row(2, "note", 200, tick: 20), Row(3, "note", 400, tick: 30));
        var stream = new EventStream(new EventLogReader(_folder.Path));
        var (rows, next) = stream.Since(null);
        Assert.Equal(["server:1", "client:1", "client:2", "server:2", "client:3"], rows.Select(r => $"{r["side"]}:{r["seq"]}"));
        Assert.Equal("client:4.3,server:2.2", next);
        Assert.Empty(stream.Since(next).Rows);
        // the rows held to the schema with their side
        var answer = new System.Text.Json.Nodes.JsonObject { ["rows"] = new System.Text.Json.Nodes.JsonArray([.. rows]), ["next"] = next };
        Assert.Empty(GateKit.Json.SchemaSet.Embedded.Validate(answer, "events/answer"));
    }

    [Fact]
    public void A_fair_stream_gives_only_what_a_player_could_know()
    {
        WriteLog(_folder.Path, "server", 1, 8, Row(1, "osiris", 10), Row(2, "game_damage", 20),
            Row(3, "game_damage", 30).Replace("\"player\"", "\"omniscient\"", StringComparison.Ordinal));
        var stream = new EventStream(new EventLogReader(_folder.Path)) { Fair = true };
        Assert.Equal([2L], stream.Since(null).Rows.Select(r => (long)r["seq"]!));
    }

    [Fact]
    public void A_cursor_behind_the_oldest_row_kept_is_told_what_went()
    {
        // segments 1 and 2 went (first = 3): rows 1..8 of 4 a segment
        File.WriteAllText(Path.Combine(_folder.Path, "events_server.json"),
            """{"side":"server","boot":1,"segment":3,"first":3,"seq":9,"rows":4,"open":[],"counts":{"game":{"rows":9,"bytes":900,"dropped":8}}}""");
        File.WriteAllText(Path.Combine(_folder.Path, "events_server_1_3.jsonl"), Row(9, "dropped", 90) + "\n");
        File.WriteAllText(Path.Combine(_folder.Path, "events_server_1_1.jsonl"), "");
        var stream = new EventStream(new EventLogReader(_folder.Path));
        var gap = Assert.Single(stream.Dropped("server:1.2"));
        Assert.Equal(2, (long)gap!["after"]!);
        Assert.Equal(9, (long)gap["first"]!);
        Assert.Empty(stream.Dropped("server:1.8"));
        Assert.Equal(8, (long)stream.Counts()["game"]!["dropped"]!);
        Assert.False(File.Exists(Path.Combine(_folder.Path, "events_server_1_1.jsonl")));
    }

    [Fact]
    public void A_segment_that_grew_is_read_again()
    {
        WriteLog(_folder.Path, "client", 5, 8, Row(1, "note", 1));
        var reader = new EventLogReader(_folder.Path);
        Assert.Single(reader.Read(Side.Client)!.Rows);
        WriteLog(_folder.Path, "client", 5, 8, Row(1, "note", 1), Row(2, "film", 2, ""","key":"film","attention":"begin" """));
        Assert.Equal(2, reader.Read(Side.Client)!.Rows.Count);
    }
}
