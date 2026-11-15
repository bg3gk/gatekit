using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using GateKit.Testing;
using GateKit.Copies;
using Record = GateKit.Copies.Record;
using MoonSharp.Interpreter;

namespace GateKit.UnitTests;

/// <summary>The test runner against FakeBridge: grouping, verdicts, failure artefacts.</summary>
public sealed partial class GameTestTests : IAsyncLifetime
{
    readonly FakeGame _game = new();
    readonly List<FakeBridge> _bridges = [];
    readonly List<string> _chunks = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var bridge in _bridges) await bridge.DisposeAsync();
        _game.Dispose();
    }

    /// <summary>A copy whose game answers Lua: every chunk is recorded, `return 1 + 1` style
    /// arithmetic is not evaluated - a test asks for what the handler knows.</summary>
    Copies.GameCopy CopyWithGame(string? instance = null, Func<string, JsonNode?>? answer = null)
    {
        var copy = _game.GameCopy(instance);
        _game.Platform.OnStart = _ =>
        {
            foreach (var side in new[] { "client", "server" })
                _bridges.Add(new FakeBridge(copy.Layout.Channel, side, code =>
                {
                    lock (_chunks) _chunks.Add(code[GateKit.Bridge.BridgeClient.Prelude.Length..]);
                    return answer?.Invoke(code[GateKit.Bridge.BridgeClient.Prelude.Length..]);
                }));
        };
        return copy;
    }

    string Tests(params (string name, string code)[] files)
    {
        var folder = _game.Temp["tests"];
        Directory.CreateDirectory(folder);
        foreach (var (name, code) in files) File.WriteAllText(Path.Combine(folder, name + ".lua"), code);
        return folder;
    }

    [Fact]
    public void A_test_declares_the_start_it_needs()
    {
        var folder = Tests(
            ("one", "test { level = 'A:B', mods = { 'M' }, reset = 'venue' }\nfunction run(game) end"),
            ("two", "function run(game) end"));
        var found = GameTest.Discover(folder);
        Assert.Equal(["one", "two"], found.Select(s => s.Name));
        var start = found[0].Start;
        Assert.Equal("A:B", start.Level);
        Assert.Equal(["M"], start.Mods!);
        Assert.Equal(ResetRung.Venue, start.Needs);
        Assert.Null(start.Save);
        Assert.Equal(("A:B", "M", false, null), start.Key);
        Assert.Equal((null, "", true, null), found[1].Start.Key);
        Assert.Equal("only", GameTest.Discover(Tests(("only", "function run(game) end"), ("other", "function run(game) end")), "onl").Single().Name);
        Assert.Throws<CopyException>(() => GameTest.Discover(folder, "nothing"));
        Assert.ThrowsAny<CopyException>(() => GameTest.Load(Tests(("bad", "-- no run function")) + "/bad.lua"));
    }

    [Fact]
    public void K_picks_a_whole_id_alone_and_otherwise_every_id_that_contains_the_text()
    {
        var folder = Tests(("event_log", "function run(game) end"), ("event_log_load", "function run(game) end"),
            ("other", "function run(game) end"));
        Assert.Equal(["event_log"], GameTest.Discover(folder, "event_log").Select(s => s.Name));
        Assert.Equal(["event_log"], GameTest.Discover(folder, "EVENT_LOG").Select(s => s.Name));
        Assert.Equal(["event_log", "event_log_load"], GameTest.Discover(folder, "event_lo").Select(s => s.Name));
        Assert.Equal(["event_log_load"], GameTest.Discover(folder, "log_load").Select(s => s.Name));
        Assert.Equal(3, GameTest.Discover(folder).Count);
        Assert.True(GameTest.Selects("a/b", "a/b", ["a/b", "a/b/c"]));
        Assert.False(GameTest.Selects("a/b", "a/b/c", ["a/b", "a/b/c"]));
        Assert.True(GameTest.Selects("b", "a/b/c", ["a/b", "a/b/c"]));
    }

    [Fact]
    public async Task Tests_that_need_the_same_start_share_one_game()
    {
        var folder = Tests(
            ("a_first", "test { level = 'GustavX:TUT_Avernus_C', mods = {} }\nfunction run(game) game.server('return 1') end"),
            ("b_second", "test { level = 'GustavX:TUT_Avernus_C', mods = {} }\nfunction run(game) game.server('return 2') end"),
            ("c_other", "test { level = 'Other:Level', mods = {} }\nfunction run(game) game.server('return 3') end"));
        var copy = CopyWithGame("grouped"); // an instance: its profile is its own, nothing to restore
        var runner = new TestRunner(copy, _game.Output);

        Assert.Equal(0, await runner.RunSuiteAsync(folder, null, false));

        // What this asserts is the grouping — two starts' worth of levels for three tests —
        // and not the number of processes started. `GameCopy.UpAsync` starts a second time when the
        // bridge's first heartbeat does not arrive, so a loaded machine can legitimately make an
        // extra process for the same group, which is what made this test fail about once in ten
        // runs while real games were running. An extra start is only allowed
        // here if the copy said why: an unexplained one still fails.
        Assert.Contains("-testLoadLevel", _game.Platform.Started[0].Arguments);
        var levels = _game.Platform.Started
            .Select(s => s.Arguments[s.Arguments.ToList().IndexOf("-testLoadLevel") + 1]).ToList();
        var groups = levels.Where((level, i) => i == 0 || level != levels[i - 1]).ToList();
        Assert.Equal(["GustavX:TUT_Avernus_C", "Other:Level"], groups);
        var retries = levels.Count - groups.Count;
        Assert.Equal(retries, Regex.Matches(_game.Output.ToString(), "mods were off for this start").Count);
        // (the second group's game answers on the same channel as the first: each chunk once per bridge)
        // (the reset ladder's own GK.Stage.* calls are left out: this asserts the tests' chunks)
        Assert.Equal(["return 1", "return 2", "return 3"], _chunks.Where(c => c.StartsWith("return ", StringComparison.Ordinal)
            && !c.Contains("GK.Stage.", StringComparison.Ordinal) && !c.Contains("GK.EventLog.", StringComparison.Ordinal)).Distinct());
        var output = _game.Output.ToString();
        Assert.Contains("PASS  a_first", output);
        Assert.Contains("3 passed, 0 failed", output);
        Assert.Empty(copy.GamePids()); // every group's game closed
    }

    [Fact]
    public async Task A_failing_test_is_reported_with_a_screenshot_and_the_console()
    {
        var folder = Tests(
            ("fails", "function run(game) check(game.server('return hp') > 10, 'too few hit points', 7) end"),
            ("passes", "function run(game) end"));
        var copy = CopyWithGame(answer: code => code == "return hp" ? JsonValue.Create(7) : null);
        _game.Platform.Window = true;
        var runner = new TestRunner(copy, _game.Output);

        Assert.Equal(1, await runner.RunSuiteAsync(folder, null, false));

        var output = _game.Output.ToString();
        Assert.Contains("FAIL  fails", output);
        Assert.Contains("too few hit points: 7", output);
        Assert.Contains("1 passed, 1 failed", output);
        var runs = Directory.EnumerateDirectories(Path.Combine(copy.Layout.State, "runs")).Single();
        Assert.True(File.Exists(Path.Combine(runs, "fails", "failure.jpg")));
        Assert.Equal("a console line", File.ReadAllText(Path.Combine(runs, "fails", "console.txt")));
        Assert.False(File.Exists(Path.Combine(runs, "passes", "failure.jpg")));
    }

    [Fact]
    public async Task A_game_that_does_not_start_fails_every_test_of_its_group()
    {
        var folder = Tests(("one", "function run(game) end"), ("two", "function run(game) end"));
        var copy = _game.GameCopy(); // no bridge: no heartbeat, so `up` gives up
        var settings = _game.Settings();
        Assert.Equal(TimeSpan.FromSeconds(75), settings.StartTimeout); // the default; shortened below

        var quick = new Copies.GameCopy(settings with { StartTimeout = TimeSpan.FromSeconds(1) }, _game.Platform, _game.Output, copy.Packer);
        Assert.Equal(1, await new TestRunner(quick, _game.Output).RunSuiteAsync(folder, null, false));
        var output = _game.Output.ToString();
        Assert.Contains("FAIL  one", output);
        Assert.Contains("game did not start", output);
        Assert.Contains("0 passed, 2 failed", output);
    }

    [Fact]
    public async Task A_venue_need_takes_a_checkpoint_once_and_resets_before_the_next_test()
    {
        var folder = Tests(
            ("a_dirty", "function run(game) game.server('return 1') end"),
            ("b_venue", "test { reset = 'venue' }\nfunction run(game) game.server('return 2') end"));
        var saves = Path.Combine(_game.Temp["profile"], "PlayerProfiles", "Public", "Savegames", "Story");
        var copy = _game.GameCopy();
        _game.Platform.OnStart = _ =>
        {
            FakeBridge? client = null, server = null;
            client = new FakeBridge(copy.Layout.Channel, "client", code =>
            {
                lock (_chunks) _chunks.Add(code);
                return code.Contains("GK.Scenes") ? JsonValue.Create(0) : null;
            });
            server = new FakeBridge(copy.Layout.Channel, "server", code =>
            {
                lock (_chunks) _chunks.Add(code);
                if (code.Contains("Osi.AutoSave()"))
                {
                    Directory.CreateDirectory(Path.Combine(saves, "AutoSave_1"));
                    File.WriteAllText(Path.Combine(saves, "AutoSave_1", "AutoSave_1.lsv"), "save");
                }
                if (code.Contains("Osi.LoadGame("))
                {
                    client!.Reboot();
                    server!.Reboot();
                }
                // what lives outside the level, as the old baseline had it: carried across the load
                if (code.Contains("GK.Stage.BaselineOutOfLevel()")) return JsonNode.Parse("""{"speed": {"enabled": false, "engine": 1}, "switches": {"AiEnableSwarm": true}}""");
                return null;
            });
            _bridges.Add(client);
            _bridges.Add(server);
        };

        Assert.Equal(0, await new TestRunner(copy, _game.Output).RunSuiteAsync(folder, null, false));
        var load = _chunks.FindIndex(c => c.Contains("Osi.LoadGame(", StringComparison.Ordinal));
        var restore = _chunks.FindIndex(c => c.Contains("GK.Stage.RestoreOutOfLevel(", StringComparison.Ordinal) && c.Contains("AiEnableSwarm", StringComparison.Ordinal));
        Assert.True(load >= 0 && restore > load, "the out-of-level state is set again after the load");

        Assert.Contains(_chunks, c => c.Contains("Osi.AutoSave()"));
        Assert.Contains(_chunks, c => c.Contains("Osi.LoadGame(\"AutoSave_1\")"));
        Assert.Contains("reset before b_venue: venue", _game.Output.ToString());
        Assert.Contains("2 passed, 0 failed", _game.Output.ToString());
    }

    /// <summary>A recipe as bg3_recipe writes it: a component, its side, steps.</summary>
    static string Recipe(double within = 10) => GateKit.Mcp.RecipeTest.Write("-- esv::Test: header\n\n", "GustavX:TUT_Avernus_C",
        "esv::lock::LockComponent", "server", GateKit.Mcp.RecipeTest.Steps(JsonNode.Parse("""
            [{"side": "server", "lua": "Osi.Lock(\"chest\", \"\") -- a ]] inside", "note": "lock it"},
             {"key": "Escape", "wait": 0.1},
             {"key": "w", "hold": 0.05, "note": "N49: a held key"},
             {"side": "client", "until": "true", "timeout": 5}]
            """)), within);

    [Fact]
    public async Task A_recipe_replays_and_passes_only_if_its_component_came_alive()
    {
        // Wish 35: every recipe bg3_recipe wrote crashed `gatekit test` in GameTest.Declare, being
        // Lua for the game (`Osi.GetHostCharacter()` at the top level) rather than a test.
        var alive = true;
        var folder = Tests(("lock_recipe", Recipe(within: 0.5)),
            ("old_free_form", "-- a recipe from before\nlocal W = Osi.GetHostCharacter()\nGK.Act.Lockpick(W, 'x')\n"));
        var copy = CopyWithGame("recipes", code =>
            code.Contains("Ext.Events.Tick:Subscribe", StringComparison.Ordinal) ? JsonNode.Parse("""{"Before": 0}""")
            : code.Contains("Watched =", StringComparison.Ordinal)
                ? JsonNode.Parse(alive ? """{"Watched": true, "Most": 1, "Ticks": 9, "First": 4, "Before": 0, "Now": 0}"""
                                       : """{"Watched": true, "Most": 0, "Ticks": 9, "Before": 0, "Now": 0}""")
            : code.StartsWith("return (", StringComparison.Ordinal) ? JsonValue.Create(true) // the `until` step
            : null);
        var runner = new TestRunner(copy, _game.Output);

        var code = await runner.RunSuiteAsync(folder, null, false);
        var output = _game.Output.ToString();
        Assert.True(code == 0, output);
        Assert.Contains("SKIP  old_free_form: not replayable: a free-form recipe from before " + GameTest.RecipesReplayableSince, output);
        Assert.Contains("PASS  lock_recipe", output);
        Assert.Contains("1 passed, 0 failed", output);
        Assert.Contains(_chunks, c => c.Contains("Osi.Lock(\"chest\", \"\") -- a ]] inside", StringComparison.Ordinal));
        Assert.Contains(_chunks, c => c.Contains("GK.Key(\"Escape\")", StringComparison.Ordinal));
        Assert.Contains(_chunks, c => c.Contains("GK.Key(\"w\", nil, 0.05)", StringComparison.Ordinal));

        alive = false;
        Assert.Equal(1, await runner.RunSuiteAsync(folder, "lock", false));
        var again = _game.Output.ToString();
        Assert.True(again.Contains("esv::lock::LockComponent never came alive on server", StringComparison.Ordinal), again[output.Length..]);

        // Only free-form files asked for: said, not a crash.
        var only = Assert.Throws<CopyException>(() => GameTest.Discover(folder, "old"));
        Assert.Contains("1 file(s) skipped", only.Message);
        Assert.Contains("not replayable", Assert.Throws<NotATestException>(() => GameTest.Load(Path.Combine(folder, "old_free_form.lua"))).Message);
    }

    /// <summary>A copy whose game answers `alive` with <paramref name="seen"/> (the JSON of its read).</summary>
    Copies.GameCopy CopySeeing(string instance, Func<string> seen) => CopyWithGame(instance, code =>
        code.Contains("Ext.Events.Tick:Subscribe", StringComparison.Ordinal) ? JsonNode.Parse("""{"Before": 0}""")
        : code.Contains("Watched =", StringComparison.Ordinal) ? JsonNode.Parse(seen())
        : code.StartsWith("return (", StringComparison.Ordinal) ? JsonValue.Create(true)
        : null);

    [Fact]
    public async Task A_type_held_before_the_steps_fails_its_replay_unless_it_may_exist_and_more_holders_come()
    {
        // `alive` used to pass whenever the type was held at the end, so a recipe for a type that
        // was there all along "replayed" whatever its steps did.
        var seen = """{"Watched": true, "Most": 2, "Ticks": 9, "Before": 2, "Now": 2, "New": false}""";
        var folder = Tests(("strict", Recipe(within: 0.3)));
        var runner = new TestRunner(CopySeeing("held", () => seen), _game.Output);
        Assert.Equal(1, await runner.RunSuiteAsync(folder, null, false));
        Assert.Contains("esv::lock::LockComponent was already held by 2 entities on server before the steps", _game.Output.ToString());

        var persistent = GateKit.Mcp.RecipeTest.Write("-- esv::Test: header\n\n", "GustavX:TUT_Avernus_C", "esv::lock::LockComponent", "server",
            GateKit.Mcp.RecipeTest.Steps(JsonNode.Parse("""[{"wait": 0.01}]""")), 0.3, mayExist: true);
        Assert.Contains("alive(\"server\", \"esv::lock::LockComponent\", 0.3, { may_exist = true })", persistent);
        File.WriteAllText(Path.Combine(folder, "strict.lua"), persistent);
        var before = _game.Output.ToString().Length;
        Assert.Equal(1, await runner.RunSuiteAsync(folder, null, false));
        Assert.Contains("held by 2 entities on server before the steps and the steps added none", _game.Output.ToString()[before..]);

        seen = """{"Watched": true, "Most": 3, "Ticks": 9, "Before": 2, "Now": 2, "New": false}""";   // one more holder
        Assert.Equal(0, await runner.RunSuiteAsync(folder, null, false));
        seen = """{"Watched": true, "Most": 2, "Ticks": 9, "Before": 2, "Now": 2, "New": true}""";    // the same count, a new holder
        Assert.Equal(0, await runner.RunSuiteAsync(folder, null, false));
    }

    [Fact]
    public async Task A_replay_of_a_coverage_recipe_is_written_into_the_record()
    {
        // A replay's verdict is written into the repository, not only into the copy's state folder:
        // that a recipe was replayed, where, and under which build.
        var repository = _game.Temp["checkout"];
        var recipes = Path.Combine(repository, "tests", "_coverage");
        var root = Path.Combine(repository, "research", "coverage");
        Directory.CreateDirectory(recipes);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(recipes, "lock_recipe.lua"), Recipe(within: 0.3));
        var alive = true;
        var runner = new TestRunner(CopySeeing("replays", () => alive
            ? """{"Watched": true, "Most": 1, "Ticks": 9, "First": 4, "Before": 0, "Now": 0}"""
            : """{"Watched": true, "Most": 0, "Ticks": 9, "Before": 0, "Now": 0}"""), _game.Output);

        Assert.Equal(0, await runner.RunSuiteAsync(recipes, null, false));
        alive = false;
        Assert.Equal(1, await runner.RunSuiteAsync(recipes, null, false));

        var replays = Record.Read(root, "replays");
        Assert.Equal(2, replays.Count);
        Assert.Equal(("tests/_coverage/lock_recipe.lua", true, 0, 1, "replays", "test"),
            ((string)replays[0]["recipe"]!, (bool)replays[0]["ok"]!, (int)replays[0]["before"]!, (int)replays[0]["most"]!,
             (string)replays[0]["instance"]!, (string)replays[0]["via"]!));
        Assert.False((bool)replays[1]["ok"]!);
        Assert.NotNull(replays[0]["gatekit"]);
        // The passed replay confirmed the type again; the failed one did not.
        var sighting = Assert.Single(Record.Read(root, "sightings"));
        Assert.Equal(("esv::lock::LockComponent", "server", "repeated", "replay of tests/_coverage/lock_recipe.lua"),
            ((string)sighting["type"]!, (string)sighting["side"]!, (string)sighting["state"]!, (string)sighting["label"]!));
        Assert.Empty(RecordProblems(root));
    }

    static IEnumerable<string> RecordProblems(string root) =>
        Record.Read(root, "replays").Concat(Record.Read(root, "sightings")).SelectMany(r => Record.Problems(r.ContainsKey("recipe") ? "replays" : "sightings", r));

    /// <summary>A game with the stage ledger: GK.Stage.* answered as the bridge does, in a level of
    /// <paramref name="characters"/> characters; <paramref name="dirty"/> makes the next Verify fail.
    /// A checkpoint makes a save in <paramref name="saves"/>(), and its load restarts both Lua states.</summary>
    Func<string, JsonNode?> Ledger(string level, int characters, Func<string> saves, Func<bool>? dirty = null) => code =>
        code.Contains("Osi.AutoSave()", StringComparison.Ordinal) ? Saved(saves())
        : code.Contains("Osi.LoadGame(", StringComparison.Ordinal) ? Reloaded()
        : code.Contains("GK.Stage.CurrentLevel()", StringComparison.Ordinal) ? JsonValue.Create(level)
        : code.Contains("GK.Stage.Begin(", StringComparison.Ordinal) ? JsonNode.Parse($$"""{"level": "{{level}}", "kept": false}""")
        : code.Contains("GK.Stage.Census()", StringComparison.Ordinal) ? JsonNode.Parse($$"""{"level": "{{level}}", "characters": {{characters}}}""")
        : code.Contains("GK.Stage.Clean()", StringComparison.Ordinal) ? JsonNode.Parse("""{"notUndone": [], "sideEffects": ["combat_started"]}""")
        : code.Contains("GK.Stage.Verify()", StringComparison.Ordinal)
            ? JsonNode.Parse(dirty?.Invoke() == true ? """{"clean": false, "differences": ["a goblin that was not there"]}""" : """{"clean": true, "differences": []}""")
        : null;

    static JsonNode? Saved(string saves)
    {
        Directory.CreateDirectory(Path.Combine(saves, "AutoSave_1"));
        File.WriteAllText(Path.Combine(saves, "AutoSave_1", "AutoSave_1.lsv"), "save");
        return null;
    }

    JsonNode? Reloaded()
    {
        lock (_bridges) foreach (var bridge in _bridges) bridge.Reboot();
        return null;
    }

    [Fact]
    public async Task In_a_simple_level_tests_are_cleaned_between_not_reloaded()
    {
        var folder = Tests(
            ("a_first", "test { level = 'GustavX:Basic_Level_A' }\nfunction run(game) game.server('return 1') end"),
            ("b_second", "test { level = 'GustavX:Basic_Level_A' }\nfunction run(game) game.server('return 2') end"));
        Copies.GameCopy? copy = null;
        copy = CopyWithGame("simple", Ledger("Basic_Level_A", 4, () => copy!.Layout.Saves));
        Assert.Equal(0, await new TestRunner(copy, _game.Output).RunSuiteAsync(folder, null, false));
        var output = _game.Output.ToString();
        Assert.Contains("a simple test level", output);
        Assert.Contains("reset before b_second: fixture", output);
        Assert.Contains("side effects of the test before, taken back: server: combat_started", output);
        Assert.DoesNotContain(_chunks, c => c.Contains("Osi.LoadGame(", StringComparison.Ordinal));
        Assert.Single(_chunks, c => c.Contains("Osi.AutoSave()", StringComparison.Ordinal)); // one checkpoint, for a rung never needed
        Assert.Single(_chunks, c => c.Contains("GK.Stage.Clean()", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Test_uses_a_copy_already_in_the_level_and_leaves_it_running_and_clean()
    {
        var folder = Tests(("here", "test { level = 'GustavX:Basic_Level_A' }\nfunction run(game) game.server('return 1') end"));
        Copies.GameCopy? copy = null;
        copy = CopyWithGame("in-level", Ledger("Basic_Level_A", 4, () => copy!.Layout.Saves));
        await copy.UpAsync(new UpOptions(Level: "GustavX:Basic_Level_A"));
        var started = _game.Platform.Started.Count;

        Assert.Equal(0, await new TestRunner(copy, _game.Output).RunSuiteAsync(folder, null, false));
        var output = _game.Output.ToString();
        Assert.Contains("in the copy already running (Basic_Level_A)", output);
        Assert.Contains("after the suite: cleaned", output);
        Assert.Equal(started, _game.Platform.Started.Count);
        Assert.NotEmpty(copy.GamePids());
        copy.Down();
    }

    [Fact]
    public async Task A_fixture_that_stays_dirty_climbs_to_the_venue_checkpoint()
    {
        var folder = Tests(
            ("a_first", "test { level = 'GustavX:Basic_Level_A', reset = 'fixture' }\nfunction run(game) end"),
            ("b_second", "test { level = 'GustavX:Basic_Level_A', reset = 'fixture' }\nfunction run(game) end"));
        Copies.GameCopy? copy = null;
        copy = CopyWithGame("dirty", Ledger("Basic_Level_A", 4, () => copy!.Layout.Saves, dirty: () => true));
        var runner = new TestRunner(copy, _game.Output);
        // the checkpoint made at the start is the next rung: a reload, not a restart
        Assert.Equal(0, await runner.RunSuiteAsync(folder, null, false));
        var output = _game.Output.ToString();
        Assert.Contains("reset before b_second: fixture failed", output);
        Assert.Contains("a goblin that was not there", output);
        Assert.Contains("-> venue", output);
        Assert.DoesNotContain("-> game", output);
        Assert.Contains(_chunks, c => c.Contains("Osi.LoadGame(\"AutoSave_1\")", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Test_attaches_to_a_running_game_and_never_closes_one_it_did_not_start()
    {
        // N18: `test` against a copy started by bg3_up failed to start, and its `down` after the
        // failure closed the copy - so `run --attach` then found no game.
        var folder = Tests(("attached", "test { level = 'GustavX:TUT_Avernus_C' }\nfunction run(game) game.server('return 1') end"));
        var copy = CopyWithGame("attach");
        await copy.UpAsync(new UpOptions(Level: "Other:Level"));
        var started = _game.Platform.Started.Count;
        var runner = new TestRunner(copy, _game.Output);

        var refused = await Assert.ThrowsAsync<CopyException>(() => runner.RunSuiteAsync(folder, null, false));
        Assert.Contains("--attach", refused.Message);
        Assert.NotEmpty(copy.GamePids());

        Assert.Equal(0, await runner.RunSuiteAsync(folder, null, false, attach: true));
        Assert.Contains("PASS  attached", _game.Output.ToString());
        Assert.Equal(started, _game.Platform.Started.Count); // nothing started
        Assert.NotEmpty(copy.GamePids());                    // nothing closed
        copy.Down();
        await Assert.ThrowsAsync<CopyException>(() => runner.RunSuiteAsync(folder, null, false, attach: true));
    }

    [Fact]
    public async Task An_attached_test_declaring_another_level_is_not_run_and_each_verdict_is_written_as_it_comes()
    {
        // Third run: `menus_finish_creating` declares the character-creation level, was replayed
        // in Act 1 and counted as a failed recipe; and the suite, cut by a time limit before its
        // summary, left only screenshots - nobody could read why the other eight failed.
        var folder = Tests(
            ("a_elsewhere", "test { level = 'GustavX:SYS_CC_I' }\nfunction run(game) game.server('return 1') end"),
            ("b_here", "test { level = 'GustavX:WLD_Main_A' }\nfunction run(game) game.server('return 2') end"),
            ("c_fails", "test { level = 'GustavX:WLD_Main_A' }\nfunction run(game) check(false, 'what went wrong') end"));
        var copy = CopyWithGame("level", code => code.Contains("GK.Stage.CurrentLevel()") ? JsonValue.Create("WLD_Main_A") : null);
        await copy.UpAsync(new UpOptions(Level: "GustavX:WLD_Main_A"));
        var runner = new TestRunner(copy, _game.Output);

        Assert.Equal(1, await runner.RunSuiteAsync(folder, null, false, attach: true));

        var output = _game.Output.ToString();
        Assert.Contains("SKIP  a_elsewhere", output);
        Assert.Contains("declares GustavX:SYS_CC_I and the running game is in WLD_Main_A", output);
        Assert.DoesNotContain(_chunks, c => c == "return 1");
        Assert.Contains(_chunks, c => c == "return 2");
        Assert.Contains("1 passed, 1 failed, 1 not run", output);
        var runs = Directory.EnumerateDirectories(Path.Combine(copy.Layout.State, "runs")).Single();
        Assert.StartsWith("SKIP", File.ReadAllText(Path.Combine(runs, "a_elsewhere", "verdict.txt")));
        Assert.StartsWith("PASS", File.ReadAllText(Path.Combine(runs, "b_here", "verdict.txt")));
        Assert.Contains("what went wrong", File.ReadAllText(Path.Combine(runs, "c_fails", "verdict.txt")));
        copy.Down();
    }

    [Fact]
    public async Task Watching_a_namespace_instead_of_a_type_is_refused_by_name()
    {
        // Seven recipes of the third run watched "esv": the replay failed as if the component never came.
        var folder = Tests(("bare", "function run(game) watch('server', 'esv') end"));
        var copy = CopyWithGame();
        Assert.Equal(1, await new TestRunner(copy, _game.Output).RunSuiteAsync(folder, null, false));
        Assert.Contains("not a component type: esv", _game.Output.ToString());
        Assert.DoesNotContain(_chunks, c => c.Contains("GKTestWatches", StringComparison.Ordinal));
    }

    [Fact]
    public void A_recipe_whose_component_is_not_a_type_is_refused_with_what_was_meant()
    {
        var root = GateKit.Testing.Scratch.NewPath("recipe");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "coverage.json"), """
                {"components": [
                 {"name": "esv::camp::EndTheDayResultEventOneFrameComponent", "side": "server"},
                 {"name": "esv::camp::TriggerComponent", "side": "server"},
                 {"name": "esv::lock::LockComponent", "side": "server"}]}
                """);
            var bare = Assert.Throws<ArgumentException>(() =>
                GateKit.Mcp.CoverageTools.CheckComponent(root, "esv", "camp_EndTheDayResultEventOneFrame"));
            Assert.Contains("esv is not a type name", bare.Message);
            Assert.Contains("Did you mean: esv::camp::EndTheDayResultEventOneFrameComponent", bare.Message);
            Assert.Contains("not one the coverage index knows", Assert.Throws<ArgumentException>(() =>
                GateKit.Mcp.CoverageTools.CheckComponent(root, "esv::camp::Trigger", "camp_Trigger")).Message);
            Assert.Throws<ArgumentException>(() => GateKit.Mcp.CoverageTools.CheckComponent(root, "esv::camp::EndTheDaySystem", "x"));
            GateKit.Mcp.CoverageTools.CheckComponent(root, "esv::camp::TriggerComponent", "camp_Trigger");
            File.Delete(Path.Combine(root, "coverage.json")); // without the index only the shape is held
            GateKit.Mcp.CoverageTools.CheckComponent(root, "esv::anything::NewComponent", "x");
            Assert.Throws<ArgumentException>(() => GateKit.Mcp.CoverageTools.CheckComponent(root, "esv", "x"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void A_recipe_is_refused_for_what_cannot_replay()
    {
        Assert.Throws<ArgumentException>(() => GateKit.Mcp.RecipeTest.Steps(JsonNode.Parse("[]")));
        Assert.Contains("step 1", Assert.Throws<ArgumentException>(() => GateKit.Mcp.RecipeTest.Steps(JsonNode.Parse("""[{"lua": "x"}]"""))).Message);
        Assert.Contains("one of lua, key or until", Assert.Throws<ArgumentException>(() =>
            GateKit.Mcp.RecipeTest.Steps(JsonNode.Parse("""[{"side": "server", "lua": "x", "key": "Escape"}]"""))).Message);
        Assert.Contains("`hold` goes with `key`", Assert.Throws<ArgumentException>(() =>
            GateKit.Mcp.RecipeTest.Steps(JsonNode.Parse("""[{"wait": 1, "hold": 2}]"""))).Message);
        Assert.Contains("unknown field", Assert.Throws<ArgumentException>(() =>
            GateKit.Mcp.RecipeTest.Steps(JsonNode.Parse("""[{"side": "server", "code": "x"}]"""))).Message);
        Assert.Equal("client", GateKit.Mcp.RecipeTest.ComponentSide("ecl::Thing", null));
        Assert.Throws<ArgumentException>(() => GateKit.Mcp.RecipeTest.ComponentSide("ls::Thing", null));
        Assert.Contains("MODULE:LEVEL", Assert.Throws<ArgumentException>(() => GateKit.Mcp.RecipeTest.Write("", "WLD_Main_A",
            "esv::X", "server", [new("server", "x", null, null, null, null, null)], 10)).Message);
    }

    [Fact]
    public async Task A_claim_about_a_system_is_checked_in_the_game_and_its_verdict_kept_beside_it()
    {
        // "does" items carry a check the game runs.
        var root = _game.Temp["claims"];
        var recipes = Path.Combine(root, "recipes");
        var systems = Path.Combine(root, "systems");
        Directory.CreateDirectory(recipes);
        Directory.CreateDirectory(Path.Combine(systems, "esv__toy"));
        File.WriteAllText(Path.Combine(recipes, "toy_poke.lua"), "test { level = 'GustavX:WLD_Main_A' }\nfunction run(game) game.server('return 1') end\n");
        var description = Path.Combine(systems, "esv__toy", "PokeSystem.json");
        File.WriteAllText(description, """
            {"system": "esv::toy::PokeSystem", "namespace": "esv::toy", "symbols": {"reads": [], "writes": [], "methods": []},
             "does": [
              {"method": "Update", "text": "a poke creates a PokedComponent with Count 5", "evidence": "read",
               "check": {"when": "toy_poke", "expect": [
                 {"appears": {"component": "esv::toy::PokedComponent", "side": "server"}},
                 {"equals": {"component": "esv::toy::PokedComponent", "field": "Count", "value": 5}},
                 {"equals": {"component": "esv::toy::PokedComponent", "field": "Count", "value": 6, "within": 0}}]}},
              {"method": "Update", "text": "inside one frame", "evidence": "read", "check": "not checkable live: it leaves nothing behind"},
              {"method": "Other", "text": "no check yet", "evidence": "read"}],
             "unknown": [], "sure": "no"}
            """);
        // The game's side of the watch: every tick it saw entity 77 come to hold the component with Count 5.
        var copy = CopyWithGame("claims", code =>
            code.Contains("GKExpect = GKExpect or {}", StringComparison.Ordinal) ? JsonNode.Parse("""{"Ticks": 0}""")
            : code.Contains("local w = (GKExpect or {})", StringComparison.Ordinal)
                ? JsonNode.Parse("""{"Ticks": 7, "Entities": {"77": {"AtBegin": false, "First": 3, "Values": [[3, 5]]}}, "Errors": []}""")
            : code.Contains("GameVersion", StringComparison.Ordinal) ? JsonValue.Create("v4.1.1.test")
            : null);
        await copy.UpAsync(new UpOptions(Level: "GustavX:WLD_Main_A"));
        var runner = new TestRunner(copy, _game.Output);

        Assert.Equal(1, await SystemClaims.CheckAsync(runner, "esv::toy::PokeSystem", systems, recipes, clean: false));

        var file = JsonNode.Parse(File.ReadAllText(description))!;
        var verdict = file["does"]![0]!["verdict"]!;
        Assert.Equal("failed", (string)verdict["last"]!);                      // the third expectation is false
        var expects = verdict["cases"]![0]!["expect"]!.AsArray();
        Assert.Equal([true, true, false], expects.Select(v => (bool)v!["ok"]!));
        Assert.Equal("ok", (string)verdict["cases"]![0]!["recipe"]!);
        Assert.Equal("v4.1.1.test", (string)verdict["cases"]![0]!["game"]!);
        Assert.Null(file["does"]![1]!["verdict"]);                              // not checkable: left as it is
        Assert.Equal("a poke creates a PokedComponent with Count 5", (string)file["does"]![0]!["text"]!);
        Assert.Contains("SKIP  esv::toy::PokeSystem claim 2: not checkable live", _game.Output.ToString());

        var tally = SystemClaims.Tally(systems);
        Assert.Equal(3, (int)tally["claims"]!);
        Assert.Equal(1, (int)tally["with_a_check"]!);
        Assert.Equal(1, (int)tally["not_checkable_live"]!);
        Assert.Equal(1, (int)tally["failed"]!);
        Assert.Contains("needs a field", SystemClaims.Problem(JsonNode.Parse("""{"when": "x", "expect": [{"changes": {"component": "c"}}]}""")));
        copy.Down();
    }

    [Fact]
    public async Task The_verbs_judge_what_the_tick_watch_saw_and_bind_the_new_entity()
    {
        // D22 of the day run: equals on a one-frame type, "added then removed inside the recipe",
        // a new holder alone passing changes, and "the same new entity" across expectations.
        var folder = Tests(("verbs", """
            function run(game)
              local list = {
                { appears = { component = "esv::toy::BComponent", as = "E", within = 0 } },
                { changes = { component = "esv::toy::BComponent", field = "Count", on = "E", within = 0 } },
                { changes = { component = "esv::toy::BComponent", field = "Count", within = 0 } },
                { gone = { component = "esv::toy::BComponent", within = 0 } },
                { equals = { component = "esv::toy::BComponent", field = "Count", value = 9, on = "E", within = 0 } },
                { equals = { component = "esv::toy::BComponent", field = "Count", value = 1, on = "E", within = 0 } },
                { gone = { component = "esv::toy::BComponent", on = "F", within = 0 } },
              }
              expect.begin(list)
              local v = expect.finish(list)
              check(v[1].ok and v[1].entity == "11", "appears binds the entity that came to hold it", v[1])
              check(not v[2].ok, "a new holder alone does not change", v[2])
              check(v[3].ok and v[3].entity == "10", "the same entity's value went 1 then 2", v[3])
              check(v[4].ok and v[4].entity == "10", "it held it and stopped", v[4])
              check(v[5].ok, "equals on the bound entity", v[5])
              check(not v[6].ok, "1 was another entity's value", v[6])
              check(not v[7].ok and contains(v[7].detail, "no earlier"), "an unbound name is said", v[7])
            end
            """));
        var copy = CopyWithGame(answer: code =>
            code.Contains("GKExpect = GKExpect or {}", StringComparison.Ordinal) ? JsonNode.Parse("""{"Ticks": 0}""")
            : code.Contains("local w = (GKExpect or {})", StringComparison.Ordinal)
                ? JsonNode.Parse("""{"Ticks": 9, "Entities": {"10": {"AtBegin": true, "First": 0, "Gone": 5, "Values": [[0, 1], [4, 2]]}, "11": {"AtBegin": false, "First": 3, "Values": [[3, 9]]}}, "Errors": []}""")
            : null);
        var runner = new TestRunner(copy, _game.Output);
        Assert.True(await runner.RunSuiteAsync(folder, null, false) == 0, _game.Output.ToString());
    }

    [Fact]
    public void The_layouts_go_to_the_bridge_as_data_for_named_reads()
    {
        var layouts = _game.Temp["layouts"];
        Directory.CreateDirectory(Path.Combine(layouts, "alive"));
        File.WriteAllText(Path.Combine(layouts, "alive", "a.json"), """
            {"component": "esv::toy::PokedComponent", "ext_name": "ServerPoked", "size": 8, "status": "confirmed",
             "fields": [{"name": "Count", "offset": 0, "type": "int32_t"}, {"name": "_Pad4", "offset": 4, "type": "uint32_t"}]}
            """);
        File.WriteAllText(Path.Combine(layouts, "alive", "broken.json"), "{ not json");
        var data = LayoutData.Build(layouts);
        var poked = data["components"]!["esv::toy::PokedComponent"]!;
        Assert.Equal(8L, (long)poked["size"]!);
        Assert.Equal("confirmed", (string)poked["status"]!);
        Assert.Equal(0L, (long)poked["fields"]!["Count"]![0]!);
        Assert.Equal("int32_t", (string)poked["fields"]!["Count"]![1]!);
        Assert.Null(poked["fields"]!["_Pad4"]);
        Assert.Single(data["components"]!.AsObject());
    }

    [Fact]
    public void The_registrys_types_made_from_layouts_go_to_the_bridge_before_the_layout_files()
    {
        var repository = _game.Temp["repository"];
        var types = Path.Combine(repository, "registry", "types", "esv", "toy");
        Directory.CreateDirectory(types);
        Directory.CreateDirectory(Path.Combine(repository, "research", "coverage", "layouts"));
        File.WriteAllText(Path.Combine(types, "PokedComponent.json"), """
            {"schema": "registry/type/1", "id": "esv::toy::PokedComponent", "kind": "struct", "size": 8,
             "fields": [{"offset": 0, "name": "Count", "type": "int32_t", "rung": "inferred"}],
             "concept": {"kind": "component"}, "survey": {"batch": "alive", "status": "declared"}}
            """);
        File.WriteAllText(Path.Combine(types, "PokeSystem.json"), """
            {"schema": "registry/type/1", "id": "esv::toy::PokeSystem", "kind": "class", "fields": [], "concept": {"kind": "system"}, "survey": {"batch": "alive"}}
            """);
        File.WriteAllText(Path.Combine(types, "Stream.json"), """
            {"schema": "registry/type/1", "id": "esv::toy::Stream", "kind": "struct", "fields": [{"offset": 0, "name": "Count", "type": "int32_t"}]}
            """);
        Assert.Equal(Path.Combine(repository, "registry", "types"), LayoutData.Folder(repository));
        var components = LayoutData.Build(LayoutData.Folder(repository)!)["components"]!.AsObject();
        var poked = Assert.Single(components).Value!;                          // the system and the type that is no layout are left out
        Assert.Equal(("declared", 8L, 0L), ((string)poked["status"]!, (long)poked["size"]!, (long)poked["fields"]!["Count"]![0]!));
    }

    [Fact]
    public void A_recipe_can_start_in_the_main_menu()
    {
        var lua = GateKit.Mcp.RecipeTest.Write("", GateKit.Mcp.RecipeTest.MainMenu, "ecl::X", "client",
            [new("ui", "GK.Gui.Open()", null, null, null, null, null)], 5);
        Assert.Contains("test { menu = true }", lua);
        Assert.Contains("game.wait_state(\"Menu\", \"client\", 120)", lua);
        var folder = Tests(("menu_recipe", lua));
        var start = GameTest.Discover(folder).Single().Start;
        Assert.True(start.MainMenu);
        Assert.Equal("<main menu>", start.Key.Item1);
    }

    [Fact]
    public void Every_coverage_recipe_reads_or_is_skipped_as_not_replayable()
    {
        var folder = LuaBindingTests.Repository(Path.Combine("tests", "_coverage"));
        var skipped = new StringWriter();
        try { GameTest.Discover(folder, null, skipped); }
        catch (CopyException e) when (e.Message.StartsWith("no tests", StringComparison.Ordinal)) { }
        foreach (var line in skipped.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries))
            Assert.Contains("not replayable", line);
    }

    [Fact]
    public async Task Run_runs_one_test_and_attach_needs_a_running_game()
    {
        var folder = Tests(("single", "test { level = 'GustavX:TUT_Avernus_C' }\nfunction run(game)\n" +
            "  check(game.server('return hp') == 7, 'hit points')\n  game.save('note.txt', 'written by the test')\nend"));
        var copy = CopyWithGame(answer: code => code == "return hp" ? JsonValue.Create(7) : null);
        var runner = new TestRunner(copy, _game.Output);

        Assert.Equal(0, await runner.RunFileAsync(Path.Combine(folder, "single.lua"), keep: false, attach: false));
        Assert.Contains("PASS single", _game.Output.ToString());
        var runs = Directory.EnumerateDirectories(Path.Combine(copy.Layout.State, "runs")).Single();
        Assert.Equal("written by the test", File.ReadAllText(Path.Combine(runs, "note.txt")));
        Assert.Empty(copy.GamePids());

        // --attach without a game says so, and does not start one
        Assert.Equal(1, await runner.RunFileAsync(Path.Combine(folder, "single.lua"), keep: false, attach: true));
        Assert.Contains("--attach needs a running game", _game.Output.ToString());
    }
}

/// <summary>The Lua side: what the game answers becomes Lua values, and the prelude's helpers.</summary>
public sealed class LuaBindingTests
{
    static Script New() => GameTest.NewScript(TextWriter.Null);

    [Fact]
    public void Json_from_the_game_becomes_tables_lists_and_nil()
    {
        var script = New();
        script.Globals["answer"] = LuaValues.ToLua(script, JsonNode.Parse(
            """{"hp": 7, "name": "Tav", "alive": true, "position": [1, 2.5, -3], "nothing": null}"""));
        Assert.True(script.DoString("""
            return answer.hp == 7 and answer.name == "Tav" and answer.alive == true
               and #answer.position == 3 and answer.position[2] == 2.5 and answer.nothing == nil
            """).Boolean);
        Assert.Equal(DataType.Nil, LuaValues.ToLua(script, null).Type);
        Assert.Equal(2, LuaValues.ToLua(script, JsonNode.Parse("[10, 20]")).Table.Length);
    }

    [Fact]
    public void The_prelude_compares_and_renders_values()
    {
        var script = New();
        foreach (var expression in new[]
        {
            "same({ 1, { a = 2 } }, { 1, { a = 2 } })", "not same({ 1 }, { 1, 2 })",
            "near({ 0.5004, 1 }, { 0.5, 1 }, 0.001)", "not near({ 0.6 }, { 0.5 }, 0.001)",
            "fmt({ 3, b = 'x' }) == '{3, b = \"x\"}'",
            "has({ 'a', 'b' }, 'b')", "count({ x = 1, y = 2 }) == 2",
            "contains('hello world', 'lo w')", "startswith('abc', 'ab')", "endswith('abc', 'bc')",
            "join({ 'a', 'b' }) == 'a, b'", "join({}, ', ', 'nothing') == 'nothing'",
            // gatekit:allow-non-english  `bytes` counts bytes, not characters, and only a string
            // outside ASCII can tell the two apart - which is the whole of what this asserts.
            "lua_list({ 'a', 'b' }) == '{\"a\", \"b\"}'", "bytes('hello мир') == 12",
        })
            Assert.True(script.DoString("return " + expression).CastToBool(), expression);
        var failed = Assert.Throws<ScriptRuntimeException>(() => script.DoString("check(false, 'wrong', { 1, 2 })"));
        Assert.Contains("wrong: {1, 2}", failed.Message);
        Assert.Contains("got {1}, wanted {2}", Assert.Throws<ScriptRuntimeException>(() => script.DoString("eq({ 1 }, { 2 })")).Message);
    }

    [Fact]
    public void A_test_file_is_read_without_running_its_body()
    {
        using var temp = new TempFolder();
        var path = temp.Write("later.lua", """
            test { level = "A:B", save = "Save1" }
            function run(game) error("the body must not run while reading the declaration") end
            """);
        var test = GameTest.Load(path);
        Assert.Equal("Save1", test.Start.Save);
        Assert.Null(test.Start.Needs);
    }

    [Fact]
    public void Every_test_in_the_repository_reads()
    {
        var folder = Repository("tests");
        var selftests = Path.Combine(folder, "_selftest");
        var found = GameTest.Discover(folder).Where(s => Path.GetDirectoryName(s.Path) == selftests).ToList();
        Assert.Equal(19, found.Count);
        // aim_and_observe, fixtures_and_attention, event_log and rolls_pin stage their own crowd, and the
        // two stage_ measurements clean and reload a scene: they want the empty basic level
        string[] basic = ["aim_and_observe", "fixtures_and_attention", "event_log", "memory", "rolls_pin", "stage_clean_cycles", "stage_level_survivors"];
        Assert.All(found, s => Assert.Equal(basic.Contains(Path.GetFileNameWithoutExtension(s.Path))
            ? "GustavX:Basic_Level_A" : "GustavX:TUT_Avernus_C", s.Start.Level));
    }

    [Fact]
    public void Every_header_field_the_repository_uses_is_known()
    {
        // every file of the tree, folders read only when named included; a free-form recipe
        // declares no header and has nothing to check
        var files = Directory.EnumerateFiles(Repository("tests"), "*.lua", SearchOption.AllDirectories)
            .Where(p => !Path.GetFileName(p).StartsWith('_')).ToList();
        Assert.NotEmpty(files);
        var refused = new List<string>();
        foreach (var file in files)
        {
            try { GameTest.Load(file); }
            catch (NotATestException) { }
            catch (CopyException e) when (e.Message.Contains("does not know", StringComparison.Ordinal)) { refused.Add(e.Message); }
        }
        Assert.Empty(refused);
    }

    /// <summary>A folder of the repository, found from the test assembly's place.</summary>
    internal static string Repository(string relative)
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            var candidate = Path.Combine(folder.FullName, relative);
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(folder.FullName, "GateKit.slnx"))) return candidate;
        }
        throw new DirectoryNotFoundException($"no {relative} above {AppContext.BaseDirectory}");
    }
}

/// <summary>The bridge's own Lua: every file at least compiles (MoonSharp's parser, no game needed).</summary>
public sealed class BridgeLuaSyntaxTests
{
    [Fact]
    public void Every_bridge_lua_file_compiles()
    {
        var folder = LuaBindingTests.Repository(Path.Combine("mods", "GateKit", "Mods", "Gwynerva_GateKit", "ScriptExtender", "Lua"));
        var broken = new List<string>();
        // Lua 5.3's integer operators (`//`, `|`), which the game's Lua has and MoonSharp (5.2) does not.
        string[] newer = ["UI.lua", "Situation.lua"];
        foreach (var file in Directory.EnumerateFiles(folder, "*.lua", SearchOption.AllDirectories))
        {
            if (newer.Contains(Path.GetFileName(file))) continue;
            try { new Script(CoreModules.None).LoadString(File.ReadAllText(file), null, Path.GetFileName(file)); }
            catch (SyntaxErrorException e) { broken.Add(Path.GetRelativePath(folder, file) + ": " + e.DecoratedMessage); }
        }
        Assert.Empty(broken);
    }
}
