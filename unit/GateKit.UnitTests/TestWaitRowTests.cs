using GateKit.Testing;

namespace GateKit.UnitTests;

/// <summary>game.wait_row and the prelude's row(): the host's own wait on the event log, its limit
/// counted in the game's ticks.</summary>
public sealed class TestWaitRowTests : FakeGameRuns
{
    [Fact]
    public async Task A_mark_is_where_the_game_says_its_log_ends_not_where_its_files_have_got_to()
    {
        // the night's defect: a mark read from the files, behind rows the game had made and not
        // yet written, took those rows for new ones
        string? channel = null;
        var copy = CopyWithGame("markahead", code =>
        {
            if (code.Contains("GK.EventLog.Boot, GK.EventLog.Seq", StringComparison.Ordinal))
                return new System.Text.Json.Nodes.JsonArray(1, 3);
            if (code == "emit")
                EventLogTests.WriteLog(channel!, "server", 1, 128, EventLogTests.Row(1, "game_state", 10),
                    EventLogTests.Row(2, "note", 20, ""","payload":{"note":"old"}"""), EventLogTests.Row(3, "note", 30, ""","payload":{"note":"old"}"""),
                    EventLogTests.Row(4, "note", 40, ""","payload":{"note":"new"}"""));
            return null;
        });
        channel = copy.Layout.Channel;
        Directory.CreateDirectory(channel);
        EventLogTests.WriteLog(channel, "server", 1, 128, EventLogTests.Row(1, "game_state", 10));
        var folder = Tests(("marks", """
            test { level = 'GustavX:TUT_Avernus_C', mods = {} }
            function run(game)
                local m = game.mark()
                game.server("emit")
                game.server("first " .. row(m, "note").payload.note)
            end
            """));
        await Runner(copy).RunSuiteAsync(folder, null, false);
        lock (_chunks) Assert.Contains("first new", _chunks);
    }

    [Fact]
    public async Task A_speed_ceiling_is_held_for_every_test_that_declares_it_not_only_the_first()
    {
        // the night's defect: the ceiling was looked at once, for the first test of a run
        var folder = Tests(
            ("a_free", "test { level = 'GustavX:TUT_Avernus_C', mods = {} }\nfunction run(game) game.server('a') end"),
            ("b_capped", "test { level = 'GustavX:TUT_Avernus_C', mods = {}, speed_ceiling = 1, ceiling_finding = 'a study' }\nfunction run(game) game.server('b') end"),
            ("c_capped", "test { level = 'GustavX:TUT_Avernus_C', mods = {}, speed_ceiling = 2, ceiling_finding = 'a study' }\nfunction run(game) game.server('c') end"));
        var copy = CopyWithGame("ceilings");
        await new TestRunner(copy, _game.Output) { Speed = "working", Limits = Quick }.RunSuiteAsync(folder, null, false);
        lock (_chunks)
        {
            int At(string marker) => _chunks.IndexOf(marker);
            int Held(string top, int after) => _chunks.FindIndex(after, c => c.Contains("local want, top, had", StringComparison.Ordinal) && c.Contains(", " + top + ", ", StringComparison.Ordinal));
            Assert.InRange(Held("1", At("a")), At("a"), At("b"));
            Assert.InRange(Held("2", At("b")), At("b"), At("c"));
        }
    }

    [Fact]
    public async Task Under_attach_the_runs_speed_is_held_before_every_test()
    {
        var folder = Tests(
            ("a_one", "test { level = 'GustavX:TUT_Avernus_C', mods = {} }\nfunction run(game) game.server('one') end"),
            ("b_two", "test { level = 'GustavX:TUT_Avernus_C', mods = {} }\nfunction run(game) game.server('two') end"));
        var copy = CopyWithGame("attachspeed");
        await copy.UpAsync(new GateKit.Copies.UpOptions(Level: "GustavX:TUT_Avernus_C"));
        await new TestRunner(copy, _game.Output) { Speed = "working", Limits = Quick }.RunSuiteAsync(folder, null, false, attach: true);
        lock (_chunks)
        {
            int At(string marker) => _chunks.IndexOf(marker);
            int Held(int after) => _chunks.FindIndex(after, c => c.Contains("local want, top, had", StringComparison.Ordinal));
            Assert.InRange(Held(0), 0, At("one"));
            Assert.InRange(Held(At("one")), At("one"), At("two"));
        }
    }

    [Fact]
    public async Task Wait_row_ends_on_the_first_row_its_match_accepts_and_its_limit_is_the_games_ticks()
    {
        string? channel = null;
        var copy = CopyWithGame("waitrow", code =>
        {
            if (code == "emit")
                _ = Task.Run(async () =>
                {
                    await Task.Delay(300);
                    EventLogTests.WriteLog(channel!, "server", 1, 128, EventLogTests.Row(1, "game_state", 10),
                        EventLogTests.Row(2, "note", 20, ""","payload":{"note":"a"}"""),
                        EventLogTests.Row(3, "note", 30, ""","payload":{"note":"b"}"""));
                });
            return null;
        });
        channel = copy.Layout.Channel;
        Directory.CreateDirectory(channel);
        EventLogTests.WriteLog(channel, "server", 1, 128, EventLogTests.Row(1, "game_state", 10));
        var folder = Tests(("waits", """
            test { level = 'GustavX:TUT_Avernus_C', mods = {} }
            function run(game)
                local m = game.mark()
                game.server("emit")
                local r = row(m, "note", function(p) return p.note == "b" end)
                game.server("found " .. r.seq .. " " .. r.side)
                local any = game.wait_row(m, { "note", "game_state" })
                game.server("first " .. any.seq)
                local ok, why = pcall(row, game.mark(), "note", nil, 1)
                game.server("limit " .. tostring(ok) .. " " .. tostring(why))
            end
            """));
        await Runner(copy).RunSuiteAsync(folder, null, false);
        lock (_chunks)
        {
            Assert.Contains("found 3 server", _chunks);
            Assert.Contains("first 2", _chunks);
            var limit = Assert.Single(_chunks, c => c.StartsWith("limit ", StringComparison.Ordinal));
            Assert.StartsWith("limit false", limit, StringComparison.Ordinal);
            Assert.Contains("ticks of the game", limit, StringComparison.Ordinal);
        }
    }
}
