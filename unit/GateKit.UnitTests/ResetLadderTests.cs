using System.Text.Json.Nodes;
using GateKit.Testing;
using GateKit.Copies;

namespace GateKit.UnitTests;

/// <summary>The reset ladder's decisions: which rung a test needs, where it starts, when it climbs.</summary>
public sealed class ResetLadderTests
{
    /// <summary>Rungs that do what they are told and remember the order they were asked in.</summary>
    sealed class FakeRungs : IResetActions
    {
        public List<string> Calls { get; } = [];
        public Queue<bool> CleanAnswers { get; } = new();
        public string? NoVenue { get; set; }
        public string? NoGame { get; set; }
        public bool VenueThrows { get; set; }
        public bool Kept { get; set; }

        public Task<bool> BeginAsync(bool fresh) { Calls.Add(fresh ? "begin fresh" : "begin"); return Task.FromResult(Kept); }

        public Task<(bool Clean, IReadOnlyList<string> Problems)> CleanAsync()
        {
            Calls.Add("clean");
            var clean = CleanAnswers.Count == 0 || CleanAnswers.Dequeue();
            return Task.FromResult<(bool, IReadOnlyList<string>)>(clean ? (true, []) : (false, ["1 object(s) that were not there: x"]));
        }

        public string? VenueProblem() => NoVenue;
        public Task ReloadVenueAsync()
        {
            Calls.Add("venue");
            return VenueThrows ? Task.FromException(new CopyException("server did not come back from the reset")) : Task.CompletedTask;
        }
        public string? GameProblem() => NoGame;
        public Task RestartGameAsync() { Calls.Add("game"); return Task.CompletedTask; }
    }

    [Theory]
    [InlineData(null, true, ResetRung.Fixture)]
    [InlineData(null, false, ResetRung.Venue)]
    [InlineData(null, null, ResetRung.Venue)]
    [InlineData(ResetRung.Game, true, ResetRung.Game)]
    [InlineData(ResetRung.None, false, ResetRung.None)]
    public void A_test_needs_what_it_declares_else_fixture_in_a_simple_level_and_venue_elsewhere(ResetRung? declared, bool? simple, ResetRung wanted) =>
        Assert.Equal(wanted, ResetLadder.Needed(declared, simple));

    [Fact]
    public async Task A_fixture_that_verifies_is_all_that_is_done()
    {
        var rungs = new FakeRungs();
        var outcome = await new ResetLadder(rungs, TextWriter.Null).PrepareAsync(ResetRung.Fixture);
        Assert.Equal(ResetRung.Fixture, outcome.Used);
        Assert.Equal(["clean"], rungs.Calls);
        Assert.StartsWith("fixture ", outcome.Line());
    }

    [Fact]
    public async Task A_dirty_fixture_climbs_one_rung_to_the_venue_and_says_why()
    {
        var rungs = new FakeRungs();
        rungs.CleanAnswers.Enqueue(false);
        var outcome = await new ResetLadder(rungs, TextWriter.Null).PrepareAsync(ResetRung.Fixture);
        Assert.Equal(ResetRung.Venue, outcome.Used);
        Assert.Equal(["clean", "venue", "begin fresh"], rungs.Calls);
        Assert.Contains("fixture failed", outcome.Line());
        Assert.Contains("not there", outcome.Line());
        Assert.Single(outcome.NotUndone);
    }

    [Fact]
    public async Task A_venue_that_cannot_be_reloaded_goes_to_a_restart_and_a_failed_reload_too()
    {
        var rungs = new FakeRungs { NoVenue = "no checkpoint of this copy to reload" };
        rungs.CleanAnswers.Enqueue(false);
        var outcome = await new ResetLadder(rungs, TextWriter.Null).PrepareAsync(ResetRung.Fixture);
        Assert.Equal(ResetRung.Game, outcome.Used);
        Assert.Equal(["clean", "game", "begin fresh"], rungs.Calls);
        Assert.Contains("no checkpoint", outcome.Line());

        var failing = new FakeRungs { VenueThrows = true };
        var second = await new ResetLadder(failing, TextWriter.Null).PrepareAsync(ResetRung.Venue);
        Assert.Equal(ResetRung.Game, second.Used);
        Assert.Equal(["venue", "game", "begin fresh"], failing.Calls);   // a venue need never tries a clean
    }

    [Fact]
    public async Task With_no_rung_left_the_test_is_not_run()
    {
        var rungs = new FakeRungs { NoVenue = "the checkpoint is of WLD_Main_A", NoGame = "not allowed here" };
        rungs.CleanAnswers.Enqueue(false);
        var outcome = await new ResetLadder(rungs, TextWriter.Null).PrepareAsync(ResetRung.Fixture);
        Assert.False(outcome.Ok);
        Assert.Equal(3, outcome.Steps.Count);
        Assert.All(outcome.Steps, s => Assert.False(s.Ok));
    }

    [Fact]
    public async Task Under_attach_with_no_checkpoint_one_test_fails_its_reset_with_the_remedy_and_the_next_gets_its_own()
    {
        // --attach: no restart; a fresh copy has no checkpoint of its level
        var rungs = new FakeRungs { NoVenue = "no checkpoint of this copy to reload", NoGame = "not allowed here (a game this command did not start)" };
        rungs.CleanAnswers.Enqueue(false);
        var ladder = new ResetLadder(rungs, TextWriter.Null);
        var first = await ladder.PrepareAsync(ResetRung.Fixture);
        Assert.False(first.Ok);
        Assert.True(first.Rebased);
        Assert.Equal(["clean", "clean", "begin fresh"], rungs.Calls);   // cleaned once more, then the world as it is is the new baseline
        var remedy = ResetLadder.Remedy(first);
        Assert.Contains("`checkpoint`", remedy);
        Assert.Contains("without --attach", remedy);

        rungs.Calls.Clear();
        var next = await ladder.PrepareAsync(ResetRung.Fixture);   // judged from the new baseline: clean holds
        Assert.Equal(ResetRung.Fixture, next.Used);
        Assert.Equal(["clean"], rungs.Calls);
        Assert.Null(ResetLadder.Remedy(next));
    }

    [Fact]
    public async Task An_untouched_world_only_opens_the_scope()
    {
        var rungs = new FakeRungs();
        var outcome = await new ResetLadder(rungs, TextWriter.Null).PrepareAsync(ResetRung.Venue, untouched: true);
        Assert.Equal(ResetRung.None, outcome.Used);
        Assert.Equal(["begin fresh"], rungs.Calls);
        Assert.Equal(["begin"], await Opened(new FakeRungs { Kept = true }));
    }

    static async Task<List<string>> Opened(FakeRungs rungs)
    {
        Assert.True(await new ResetLadder(rungs, TextWriter.Null).OpenAsync());
        return rungs.Calls;
    }

    [Fact]
    public void A_level_is_simple_by_its_census_or_by_the_settings_list()
    {
        using var game = new FakeGame();
        var settings = game.Settings();
        Assert.Equal(12, settings.SimpleLevelCharacters);
        Assert.True(SimpleLevel.Judge(JsonNode.Parse("""{"level": "Basic_Level_A", "characters": 4}""")!.AsObject(), settings));
        Assert.False(SimpleLevel.Judge(JsonNode.Parse("""{"level": "WLD_Main_A", "characters": 900}""")!.AsObject(), settings));
        Assert.True(SimpleLevel.Judge(JsonNode.Parse("""{"level": "WLD_Main_A", "characters": 900}""")!.AsObject(),
            settings with { SimpleLevels = ["wld_main_a"] }));
        Assert.Null(SimpleLevel.Judge(null, settings));
    }

    [Fact]
    public void The_timing_line_counts_each_rung_and_the_seconds_a_run()
    {
        var results = new List<TestResult>
        {
            new("a", true, "PASS", TimeSpan.FromSeconds(3)) { ResetSteps = [] },
            new("b", true, "PASS", TimeSpan.FromSeconds(3)) { ResetSteps = [new ResetStep(ResetRung.Fixture, true, TimeSpan.FromSeconds(1), "")] },
            new("c", true, "PASS", TimeSpan.FromSeconds(3)) { ResetSteps = [new ResetStep(ResetRung.Fixture, false, TimeSpan.FromSeconds(4), "dirty"),
                new ResetStep(ResetRung.Venue, true, TimeSpan.FromSeconds(20), "")] },
        };
        var line = TestRunner.Timing(results, TimeSpan.FromSeconds(37));
        Assert.Equal("timing: 3 runs, 12.3 s a run in all (run 3.0 s); fixture held 1 (1.0 s, 1.0-1.0), fixture failed 1 (4.0 s, 4.0-4.0), "
            + "venue held 1 (20.0 s, 20.0-20.0)", line);
    }

    [Fact]
    public void A_test_carries_its_description_from_the_top_comments_or_about()
    {
        Assert.Equal("Shield: +5 AC until the start of your next turn.\nA reaction to being hit.",
            GameTest.Leading("-- Shield: +5 AC until the start of your next turn.\n--   A reaction to being hit.\n\ntest {}\n-- not this"));
        Assert.Equal("", GameTest.Leading("test {}\n-- after the code"));
        var folder = GateKit.Testing.Scratch.NewPath("about");
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "a.lua"), "-- top\ntest { about = 'Opportunity attack: leaving reach provokes one.' }\nfunction run(game) end");
            Assert.Equal("Opportunity attack: leaving reach provokes one.", GameTest.Load(Path.Combine(folder, "a.lua")).About);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void A_test_declares_its_reset_by_the_rung_names_and_the_old_names_are_refused()
    {
        var folder = GateKit.Testing.Scratch.NewPath("reset");
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "a.lua"), "test { reset = 'fixture' }\nfunction run(game) end");
            File.WriteAllText(Path.Combine(folder, "b.lua"), "test { reset = 'venue' }\nfunction run(game) end");
            File.WriteAllText(Path.Combine(folder, "c.lua"), "function run(game) end");
            var found = GameTest.Discover(folder);
            Assert.Equal([ResetRung.Fixture, ResetRung.Venue, null], found.Select(s => s.Start.Needs));
            File.WriteAllText(Path.Combine(folder, "d.lua"), "test { reset = 'sometimes' }\nfunction run(game) end");
            Assert.Contains("not one of none, fixture, venue, game",
                Assert.ThrowsAny<CopyException>(() => GameTest.Load(Path.Combine(folder, "d.lua"))).Message);
            foreach (var old in new[] { "clean", "level", "process" })
                Assert.Throws<CopyException>(() => ResetRungs.Parse(old));
        }
        finally { Directory.Delete(folder, true); }
    }

    /// <summary>The runner's tests against the fake game shorten the waits; a real run, `systems check`
    /// and anything that does not say otherwise keep these.</summary>
    [Fact]
    public void A_real_game_gets_the_ladders_own_waits()
    {
        Assert.Equal((TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(3)),
            (ResetLimits.Default.Settle, ResetLimits.Default.VerifyFor, ResetLimits.Default.PendingFor, ResetLimits.Default.SceneAppear));
        using var game = new FakeGame();
        var copy = game.GameCopy("limits");
        Assert.Same(ResetLimits.Default, new TestRunner(copy, game.Output).Limits);
        var actions = new GameResetActions(copy, copy.Session(), restart: null);
        Assert.Equal((ResetLimits.Default.Settle, ResetLimits.Default.VerifyFor, ResetLimits.Default.PendingFor),
            (actions.Settle, actions.VerifyFor, actions.PendingFor));
    }
}
