using System.Text.Json.Nodes;
using GateKit.Bridge;
using static GateKit.UnitTests.EventLogTests;

namespace GateKit.UnitTests;

/// <summary>Attention as a view of the event log (docs/contracts/bridge-protocol.md, "Attention"):
/// what a caller is told, and a wait that ends on the first row it was not told.</summary>
public sealed class AttentionTests : IDisposable
{
    readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    void Log(string side, long boot, params string[] rows) => WriteLog(_folder.Path, side, boot, 128, rows);

    static string Begin(long seq, string kind, string key, double t, long tick = 0) =>
        Row(seq, kind, t, $$""","key":"{{key}}","attention":"begin","payload":{"window":"{{kind}}"}""", tick);

    static string End(long seq, string kind, string key, double t) => Row(seq, kind, t, $$""","key":"{{key}}","attention":"end" """);

    static string News(long seq, string kind, double t, long tick = 0) => Row(seq, kind, t, ""","attention":"news" """, tick);

    [Fact]
    public void A_first_look_tells_what_waits_and_not_the_history_before_it()
    {
        Log("client", 1, News(1, "job_done", 1000), Begin(2, "dialogue", "window:dialogue", 4000), Row(3, "game_state", 5000));
        var reader = new AttentionReader(_folder.Path);
        var items = reader.Report(new AttentionCursor());
        var only = Assert.Single(items);
        Assert.Equal("dialogue", (string)only!["what"]!);
        Assert.Equal("dialogue", (string)only["window"]!);
        Assert.True((bool)only["waiting"]!);
        Assert.Equal("client", (string)only["side"]!);
        Assert.True((long)only["since_ms"]! >= 1000);
        Assert.Null(only["key"]);
    }

    [Fact]
    public void A_page_of_the_first_launch_is_told_with_its_controls_and_the_switch()
    {
        Log("client", 1,
            Row(1, "setup", 1000, """
                ,"key":"window:Eula","attention":"begin","payload":{"window":"Eula","widget":"Eula","firstLaunch":true,"controls":[{"handle":"AcceptBtn","kind":"button","command":"CustomEvent","parameter":"OpenSensitiveSettings"}]}
                """.Trim()),
            Row(2, "splash", 2000, """
                ,"key":"window:SplashScreen","attention":"begin","payload":{"window":"SplashScreen","controls":[],"keys":"any key"}
                """.Trim()));
        var items = new AttentionReader(_folder.Path).Report(new AttentionCursor());
        var eula = items.Single(i => (string)i!["what"]! == "setup")!;
        Assert.True((bool)eula["firstLaunch"]!);
        var accept = eula["controls"]!.AsArray().Single()!;
        Assert.Equal("AcceptBtn", (string)accept["handle"]!);
        Assert.Equal("CustomEvent", (string)accept["command"]!);
        var splash = items.Single(i => (string)i!["what"]! == "splash")!;
        Assert.Equal("any key", (string)splash["keys"]!);
        Assert.Empty(splash["controls"]!.AsArray());
    }

    [Fact]
    public void Later_looks_tell_the_rows_since_the_last_and_a_new_boot_from_its_start()
    {
        var reader = new AttentionReader(_folder.Path);
        var cursor = new AttentionCursor();
        Log("server", 7, Begin(1, "combat_started", "combat", 50));
        Assert.Equal(["combat_started"], reader.Report(cursor).Select(i => (string)i!["what"]!));   // waiting

        Log("server", 7, Begin(1, "combat_started", "combat", 50), News(2, "death", 200), Row(3, "game_damage", 250),
            End(4, "combat_ended", "combat", 290));
        var items = reader.Report(cursor);
        Assert.Equal(["death", "combat_ended"], items.Select(i => (string)i!["what"]!));
        Assert.Equal(2, (long)items[0]!["seq"]!);
        Assert.Empty(reader.Report(cursor));

        // a reset or a load: the side starts counting again, and all of the new boot is news
        Log("server", 8, News(1, "job_done", 5));
        Assert.Equal("job_done", (string)Assert.Single(reader.Report(cursor))!["what"]!);
    }

    [Fact]
    public void Hold_and_release_change_what_waits_without_being_told()
    {
        var reader = new AttentionReader(_folder.Path);
        var cursor = new AttentionCursor();
        Log("client", 2, Begin(1, "dialogue", "window:dialogue", 10));
        reader.Report(cursor);
        Log("client", 2, Begin(1, "dialogue", "window:dialogue", 10),
            Row(2, "dialogue_line", 20, ""","key":"dialogue:phase","attention":"hold","payload":{"ready":false}"""));
        var items = reader.Report(cursor);
        Assert.All(items, i => Assert.True((bool)i!["waiting"]!));
        Assert.Contains(items, i => (string)i!["what"]! == "dialogue_line" && !(bool)i["ready"]!);
        Log("client", 2, Begin(1, "dialogue", "window:dialogue", 10),
            Row(2, "dialogue_line", 20, ""","key":"dialogue:phase","attention":"hold" """),
            Row(3, "dialogue_phase_ended", 30, ""","key":"dialogue:phase","attention":"release" """));
        Assert.Equal(["dialogue"], reader.Report(cursor).Select(i => (string)i!["what"]!));
    }

    [Fact]
    public void A_state_both_sides_report_is_one_item_and_the_sides_are_one_list()
    {
        var reader = new AttentionReader(_folder.Path);
        var cursor = new AttentionCursor();
        Log("server", 1, Row(1, "game_state", 1));
        Log("client", 1, Row(1, "game_state", 1));
        reader.Report(cursor);
        Log("server", 1, Row(1, "game_state", 1), Begin(2, "loading", "state", 100), End(3, "loading_ended", "state", 300), News(4, "level_started", 310));
        Log("client", 1, Row(1, "game_state", 1), Begin(2, "loading", "state", 105), End(3, "loading_ended", "state", 302));
        var items = reader.Report(cursor);
        Assert.Equal(["loading:server", "loading_ended:server", "level_started:server"], items.Select(i => $"{i!["what"]}:{i["side"]}"));
    }

    [Fact]
    public void A_cursor_behind_what_went_is_told_so()
    {
        var reader = new AttentionReader(_folder.Path);
        var cursor = new AttentionCursor();
        Log("server", 1, Row(1, "game_state", 1));
        reader.Report(cursor);
        File.WriteAllText(Path.Combine(_folder.Path, "events_server.json"),
            """{"side":"server","boot":1,"segment":2,"first":2,"seq":129,"rows":128,"open":[]}""");
        File.WriteAllText(Path.Combine(_folder.Path, "events_server_1_2.jsonl"), News(129, "job_done", 500) + "\n");
        var items = reader.Report(cursor);
        Assert.Equal("dropped", (string)items[0]!["what"]!);
        Assert.Equal(129, (long)items[0]!["first"]!);
        Assert.Equal("job_done", (string)items[1]!["what"]!);
        Assert.Empty(GateKit.Json.SchemaSet.Embedded.Validate(items, "common/attention"));
    }

    [Fact]
    public async Task Rows_no_registry_places_are_told_once_per_kind_and_withheld_from_a_fair_caller()
    {
        Log("server", 1, Row(1, "thunder", 10), Row(2, "thunder", 20), News(3, "lightning", 30), Begin(4, "trade", "window:trade", 40));
        var reader = new AttentionReader(_folder.Path);
        var cursor = new AttentionCursor();
        var items = reader.Report(cursor);
        Assert.Equal(["unplaced:thunder", "unplaced:lightning", "trade:"], items.Select(i => $"{i!["what"]}:{i["kind"]}"));
        Assert.Null(items[0]!["withheld"]);
        Assert.Empty(GateKit.Json.SchemaSet.Embedded.Validate(items, "common/attention"));
        // once per kind: the next look says only what waits
        Assert.Equal(["trade"], reader.Report(cursor).Select(i => (string)i!["what"]!));
        // the unplaced news row still counts as news to a caller that is not fair
        Assert.Contains(reader.Fold(reader.Stream.Boots()).Told, r => (string)r["kind"]! == "lightning");

        var fair = new AttentionReader(_folder.Path) { Fair = true };
        var told = fair.Report(new AttentionCursor());
        Assert.Equal(["unplaced:thunder:true", "unplaced:lightning:true", "trade::"], told.Select(i => $"{i!["what"]}:{i["kind"]}:{i["withheld"]}"));
        Assert.DoesNotContain(fair.Fold(fair.Stream.Boots()).Told, r => (string)r["kind"]! == "lightning");
        // an unplaced row is never what ends a wait
        var outcome = await AttentionWait.RunAsync(fair, onAttention: true, condition: null, TimeSpan.FromMilliseconds(150), from: new AttentionCursor());
        Assert.Equal("timeout", outcome.EndedBy);
    }

    [Fact]
    public async Task A_wait_ends_on_the_first_new_item_and_says_so()
    {
        Log("client", 1, Begin(1, "trade", "window:trade", 90));
        var reader = new AttentionReader(_folder.Path);
        var waiting = AttentionWait.RunAsync(reader, onAttention: true, condition: null, TimeSpan.FromSeconds(10));
        await Task.Delay(150);
        // what was already waiting when the wait began is not news
        Assert.False(waiting.IsCompleted);
        Log("client", 1, Begin(1, "trade", "window:trade", 90), Begin(2, "roll", "window:roll", 290));
        var outcome = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("attention", outcome.EndedBy);
        Assert.Equal("roll", (string)Assert.Single(outcome.Items)!["what"]!);
    }

    [Fact]
    public async Task A_wait_ends_on_its_condition_or_on_the_timeout()
    {
        var reader = new AttentionReader(_folder.Path);
        var asked = 0;
        var outcome = await AttentionWait.RunAsync(reader, onAttention: true,
            _ => Task.FromResult<JsonNode?>(++asked >= 2 ? JsonValue.Create(42) : JsonValue.Create(false)), TimeSpan.FromSeconds(10));
        Assert.Equal("condition", outcome.EndedBy);
        Assert.Equal(42, (int)outcome.Value!);

        var timeout = await AttentionWait.RunAsync(reader, onAttention: true, condition: null, TimeSpan.FromMilliseconds(200));
        Assert.Equal("timeout", timeout.EndedBy);
        Assert.Empty(timeout.Items);
    }

    [Fact]
    public async Task A_wait_from_the_callers_cursor_ends_at_once_on_what_it_was_not_told()
    {
        var reader = new AttentionReader(_folder.Path);
        var told = new AttentionCursor();
        Log("client", 1, Begin(1, "dialogue", "window:dialogue", 90));
        reader.Report(told);
        // between the caller's last call and its wait the roll opened and closed
        Log("client", 1, Begin(1, "dialogue", "window:dialogue", 90), Begin(2, "roll", "window:roll", 200), End(3, "roll_ended", "window:roll", 250));
        var outcome = await AttentionWait.RunAsync(reader, onAttention: true, condition: null, TimeSpan.FromSeconds(5), from: told);
        Assert.Equal("attention", outcome.EndedBy);
        Assert.Equal(["roll", "roll_ended"], outcome.Items.Select(i => (string)i!["what"]!));
        // the caller's cursor is read, not moved
        Assert.Equal(1, told.Get(Side.Client)!.Value.Seq);

        // without it the wait starts at now and lets the history be
        var fresh = await AttentionWait.RunAsync(reader, onAttention: true, condition: null, TimeSpan.FromMilliseconds(200));
        Assert.Equal("timeout", fresh.EndedBy);
    }

    [Fact]
    public void A_roll_tells_its_difficulty_modifiers_and_at_its_end_the_die_and_the_verdict()
    {
        var reader = new AttentionReader(_folder.Path);
        var cursor = new AttentionCursor();
        Log("client", 1, Row(1, "game_state", 1));
        reader.Report(cursor);
        Log("client", 1, Row(1, "game_state", 1),
            Row(2, "roll", 100, ""","key":"window:roll","attention":"begin","payload":{"window":"roll","widget":"ActiveRoll","who":"c7c13742-bacd-460a-8f65-f864fe41f255","check":"Charisma check","skill":"Persuasion","dc":15,"target":13,"modifiers":[{"name":"Persuasion","value":2}],"bonus":2}"""),
            Row(3, "roll_ended", 200, ""","key":"window:roll","attention":"end","payload":{"opened":2,"window":"roll","dc":15,"bonus":2,"dice":[20],"natural":20,"total":22,"success":true,"critical":"success","state":"ResultReady"}"""));
        var items = reader.Report(cursor);
        Assert.Equal(["roll", "roll_ended"], items.Select(i => (string)i!["what"]!));
        Assert.Equal(15, (int)items[0]!["dc"]!);
        Assert.Equal("Persuasion", (string)items[0]!["modifiers"]![0]!["name"]!);
        Assert.Null(items[0]!["target"]);
        Assert.Equal(20, (int)items[1]!["natural"]!);
        Assert.Equal(22, (int)items[1]!["total"]!);
        Assert.Equal("success", (string)items[1]!["critical"]!);
        Assert.True((bool)items[1]!["success"]!);
        Assert.Empty(GateKit.Json.SchemaSet.Embedded.Validate(items, "common/attention"));
        // the rows hold to the registry: every field is declared
        var log = new EventLogReader(_folder.Path);
        Assert.Equal(3, log.Read(Side.Client)!.Rows.Count);
        Assert.DoesNotContain(log.Problems, p => p.Contains("not declared", StringComparison.Ordinal));
    }

    [Fact]
    public void A_load_through_a_reset_of_the_clients_Lua_is_told_once_begun_and_once_ended()
    {
        // the client's Lua is reset inside a save's load: the old boot told `loading`; the new boot
        // opens it again with `continues` naming that row, and ends it
        static string Loading(long seq, double t, string continues = "") => Row(seq, "loading", t,
            $$""","key":"state","attention":"begin","payload":{"state":"StartLoading","from":"Running"{{continues}}}""");
        var reader = new AttentionReader(_folder.Path);
        var told = new AttentionCursor();        // looked before the load and during it
        var late = new AttentionCursor();        // looked before the load only
        Log("client", 404, Row(1, "game_state", 100));
        reader.Report(told);
        reader.Report(late);
        Log("client", 404, Row(1, "game_state", 100), Loading(2, 200));
        Assert.Equal(["loading"], reader.Report(told).Where(i => i!["waiting"] is null).Select(i => (string)i!["what"]!));

        // the new boot's head is written before its first row: a look then moves into the new boot
        Log("client", 405);
        Assert.Empty(reader.Report(told));
        Log("client", 405, Loading(1, 900, ""","continues":{"boot":404,"seq":2}"""),
            Row(2, "loading_ended", 950, ""","key":"state","attention":"end","payload":{"opened":1,"to":"PrepareRunning"}"""));
        var items = reader.Report(told);
        var ended = Assert.Single(items);
        Assert.Equal("loading_ended", (string)ended!["what"]!);
        Assert.Equal("PrepareRunning", (string)ended["to"]!);
        Assert.Empty(GateKit.Json.SchemaSet.Embedded.Validate(items, "common/attention"));
        // a caller that never saw the old boot's row is told the load by the new boot
        Assert.Equal(["loading", "loading_ended"], reader.Report(late).Select(i => (string)i!["what"]!));
        var log = new EventLogReader(_folder.Path);
        Assert.DoesNotContain(log.Problems, p => p.Contains("not declared", StringComparison.Ordinal));
    }

    [Fact]
    public void A_segment_read_while_the_game_rewrites_it_tells_nothing_twice()
    {
        // the run that failed (new_game): the cursor at client:432.11; one look found the boot's
        // segment empty (the game was rewriting it) and moved the cursor to 432.0, and the next
        // look told the whole boot again, the start's own `loading` first
        static string Loading(long seq, double t, string from) => Row(seq, "loading", t,
            $$""","key":"state","attention":"begin","payload":{"state":"StartLoading","from":"{{from}}"}""");
        var rows = new[] { Loading(1, 100, "InitMenu"), Row(2, "loading_ended", 200, ""","key":"state","attention":"end","payload":{"to":"Menu"}"""),
            Loading(3, 300, "Menu"), Row(4, "game_state", 400) };
        Log("client", 432, rows);
        var reader = new AttentionReader(_folder.Path);
        var cursor = new AttentionCursor();
        cursor.Move(Side.Client, 432, 3);
        Assert.DoesNotContain(reader.Report(cursor), i => i!["waiting"] is null);
        File.WriteAllText(reader.Log.SegmentPath(Side.Client, 432, 1), "");
        Assert.DoesNotContain(reader.Report(cursor), i => i!["waiting"] is null);
        Log("client", 432, [.. rows, Row(5, "game_state", 500)]);
        Assert.DoesNotContain(reader.Report(cursor), i => i!["waiting"] is null);
        Assert.Equal((432L, 5L), cursor.Get(Side.Client));
    }

    [Fact]
    public void A_cursor_never_goes_back_within_a_boot()
    {
        var cursor = new AttentionCursor();
        cursor.Move(Side.Client, 432, 11);
        cursor.Move(Side.Client, 432, 0);
        Assert.Equal((432L, 11L), cursor.Get(Side.Client));
        Assert.Equal(11, cursor.Seen(Side.Client, 432));
        // a new boot is news from its start, and the boot left is remembered
        cursor.Move(Side.Client, 433, 0);
        Assert.Equal(0, cursor.Seen(Side.Client, 432 + 1));
        Assert.True(cursor.Told(Side.Client, 432, 11));
    }

    [Fact]
    public void An_item_says_how_many_ticks_of_its_side_before_the_last_row_it_happened()
    {
        var reader = new AttentionReader(_folder.Path);
        var cursor = new AttentionCursor();
        Log("client", 1, Row(1, "game_state", 100));
        reader.Report(cursor);
        Log("client", 1, Row(1, "game_state", 100), Begin(2, "reaction", "window:reaction", 300, tick: 40), Row(3, "note", 400, "", tick: 41));
        var item = reader.Report(cursor).First(i => i!["waiting"] is null)!;
        Assert.Equal("reaction", (string)item["what"]!);
        Assert.Equal(1, (long)item["since_ticks"]!);
        Assert.True((long)item["since_ms"]! >= 100);
    }
}
