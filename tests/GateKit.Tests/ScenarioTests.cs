using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using GateKit.Scenarios;
using GateKit.Stand;
using MoonSharp.Interpreter;

namespace GateKit.Tests;

/// <summary>The scenario runner against FakeBridge: grouping, verdicts, failure artefacts.</summary>
public sealed class ScenarioTests : IAsyncDisposable
{
    readonly FakeGame _game = new();
    readonly List<FakeBridge> _bridges = [];
    readonly List<string> _chunks = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var bridge in _bridges) await bridge.DisposeAsync();
        _game.Dispose();
    }

    /// <summary>A stand whose game answers Lua: every chunk is recorded, `return 1 + 1` style
    /// arithmetic is not evaluated - a scenario asks for what the handler knows.</summary>
    Stand.Stand StandWithGame(string? instance = null, Func<string, JsonNode?>? answer = null)
    {
        var stand = _game.Stand(instance);
        _game.Platform.OnStart = _ =>
        {
            foreach (var side in new[] { "client", "server" })
                _bridges.Add(new FakeBridge(stand.Layout.Channel, side, code =>
                {
                    lock (_chunks) _chunks.Add(code[GateKit.Bridge.BridgeClient.Prelude.Length..]);
                    return answer?.Invoke(code[GateKit.Bridge.BridgeClient.Prelude.Length..]);
                }));
        };
        return stand;
    }

    string Scenarios(params (string name, string code)[] files)
    {
        var folder = _game.Temp["scenarios"];
        Directory.CreateDirectory(folder);
        foreach (var (name, code) in files) File.WriteAllText(Path.Combine(folder, name + ".lua"), code);
        return folder;
    }

    [Fact]
    public void A_scenario_declares_the_start_it_needs()
    {
        var folder = Scenarios(
            ("one", "scenario { level = 'A:B', mods = { 'M' }, clean = true }\nfunction run(game) end"),
            ("two", "function run(game) end"));
        var found = Scenario.Discover(folder);
        Assert.Equal(["one", "two"], found.Select(s => s.Name));
        var start = found[0].Start;
        Assert.Equal("A:B", start.Level);
        Assert.Equal(["M"], start.Mods!);
        Assert.True(start.Clean);
        Assert.Null(start.Save);
        Assert.Equal(("A:B", "M", false, null), start.Key);
        Assert.Equal((null, "", true, null), found[1].Start.Key);
        Assert.Equal("only", Scenario.Discover(Scenarios(("only", "function run(game) end"), ("other", "function run(game) end")), "onl").Single().Name);
        Assert.Throws<StandException>(() => Scenario.Discover(folder, "nothing"));
        Assert.Throws<StandException>(() => Scenario.Load(Scenarios(("bad", "-- no run function")) + "/bad.lua"));
    }

    [Fact]
    public async Task Scenarios_that_need_the_same_start_share_one_game()
    {
        var folder = Scenarios(
            ("a_first", "scenario { level = 'GustavX:TUT_Avernus_C', mods = {} }\nfunction run(game) game.server('return 1') end"),
            ("b_second", "scenario { level = 'GustavX:TUT_Avernus_C', mods = {} }\nfunction run(game) game.server('return 2') end"),
            ("c_other", "scenario { level = 'Other:Level', mods = {} }\nfunction run(game) game.server('return 3') end"));
        var stand = StandWithGame("grouped"); // an instance: its profile is its own, nothing to restore
        var runner = new ScenarioRunner(stand, _game.Output);

        Assert.Equal(0, await runner.RunSuiteAsync(folder, null, false));

        // What this asserts is the grouping — two starts' worth of levels for three scenarios —
        // and not the number of processes started. `Stand.UpAsync` starts a second time when the
        // bridge's first heartbeat does not arrive, so a loaded machine can legitimately make an
        // extra process for the same group, which is what made this test fail about once in ten
        // runs while real games were running (research/TODO.md). An extra start is only allowed
        // here if the stand said why: an unexplained one still fails.
        Assert.Contains("-testLoadLevel", _game.Platform.Started[0].Arguments);
        var levels = _game.Platform.Started
            .Select(s => s.Arguments[s.Arguments.ToList().IndexOf("-testLoadLevel") + 1]).ToList();
        var groups = levels.Where((level, i) => i == 0 || level != levels[i - 1]).ToList();
        Assert.Equal(["GustavX:TUT_Avernus_C", "Other:Level"], groups);
        var retries = levels.Count - groups.Count;
        Assert.Equal(retries, Regex.Matches(_game.Output.ToString(), "mods were off for this start").Count);
        // (the second group's game answers on the same channel as the first: each chunk once per bridge)
        Assert.Equal(["return 1", "return 2", "return 3"], _chunks.Where(c => c.StartsWith("return ", StringComparison.Ordinal)).Distinct());
        var output = _game.Output.ToString();
        Assert.Contains("PASS  a_first", output);
        Assert.Contains("3 passed, 0 failed", output);
        Assert.Empty(stand.GamePids()); // every group's game closed
    }

    [Fact]
    public async Task A_failing_scenario_is_reported_with_a_screenshot_and_the_console()
    {
        var folder = Scenarios(
            ("fails", "function run(game) check(game.server('return hp') > 10, 'too few hit points', 7) end"),
            ("passes", "function run(game) end"));
        var stand = StandWithGame(answer: code => code == "return hp" ? JsonValue.Create(7) : null);
        _game.Platform.Window = true;
        var runner = new ScenarioRunner(stand, _game.Output);

        Assert.Equal(1, await runner.RunSuiteAsync(folder, null, false));

        var output = _game.Output.ToString();
        Assert.Contains("FAIL  fails", output);
        Assert.Contains("too few hit points: 7", output);
        Assert.Contains("1 passed, 1 failed", output);
        var runs = Directory.EnumerateDirectories(Path.Combine(stand.Layout.State, "runs")).Single();
        Assert.True(File.Exists(Path.Combine(runs, "fails", "failure.jpg")));
        Assert.Equal("a console line", File.ReadAllText(Path.Combine(runs, "fails", "console.txt")));
        Assert.False(File.Exists(Path.Combine(runs, "passes", "failure.jpg")));
    }

    [Fact]
    public async Task A_game_that_does_not_start_fails_every_scenario_of_its_group()
    {
        var folder = Scenarios(("one", "function run(game) end"), ("two", "function run(game) end"));
        var stand = _game.Stand(); // no bridge: no heartbeat, so `up` gives up
        var settings = _game.Settings();
        Assert.Equal(TimeSpan.FromSeconds(75), settings.StartTimeout); // the default; shortened below

        var quick = new Stand.Stand(settings with { StartTimeout = TimeSpan.FromSeconds(1) }, _game.Platform, _game.Output, stand.Packer);
        Assert.Equal(1, await new ScenarioRunner(quick, _game.Output).RunSuiteAsync(folder, null, false));
        var output = _game.Output.ToString();
        Assert.Contains("FAIL  one", output);
        Assert.Contains("game did not start", output);
        Assert.Contains("0 passed, 2 failed", output);
    }

    [Fact]
    public async Task Clean_takes_a_checkpoint_once_and_resets_before_the_next_scenario()
    {
        var folder = Scenarios(
            ("a_dirty", "function run(game) game.server('return 1') end"),
            ("b_clean", "scenario { clean = true }\nfunction run(game) game.server('return 2') end"));
        var saves = Path.Combine(_game.Temp["profile"], "PlayerProfiles", "Public", "Savegames", "Story");
        var stand = _game.Stand();
        _game.Platform.OnStart = _ =>
        {
            FakeBridge? client = null, server = null;
            client = new FakeBridge(stand.Layout.Channel, "client", code =>
            {
                lock (_chunks) _chunks.Add(code);
                return code.Contains("GK.Scenes") ? JsonValue.Create(0) : null;
            });
            server = new FakeBridge(stand.Layout.Channel, "server", code =>
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
                return null;
            });
            _bridges.Add(client);
            _bridges.Add(server);
        };

        Assert.Equal(0, await new ScenarioRunner(stand, _game.Output).RunSuiteAsync(folder, null, false));

        Assert.Contains(_chunks, c => c.Contains("Osi.AutoSave()"));
        Assert.Contains(_chunks, c => c.Contains("Osi.LoadGame(\"AutoSave_1\")"));
        Assert.Contains("world reset before b_clean", _game.Output.ToString());
        Assert.Contains("2 passed, 0 failed", _game.Output.ToString());
    }

    [Fact]
    public async Task Run_runs_one_scenario_and_attach_needs_a_running_game()
    {
        var folder = Scenarios(("single", "scenario { level = 'GustavX:TUT_Avernus_C' }\nfunction run(game)\n" +
            "  check(game.server('return hp') == 7, 'hit points')\n  game.save('note.txt', 'written by the scenario')\nend"));
        var stand = StandWithGame(answer: code => code == "return hp" ? JsonValue.Create(7) : null);
        var runner = new ScenarioRunner(stand, _game.Output);

        Assert.Equal(0, await runner.RunFileAsync(Path.Combine(folder, "single.lua"), keep: false, attach: false));
        Assert.Contains("PASS single", _game.Output.ToString());
        var runs = Directory.EnumerateDirectories(Path.Combine(stand.Layout.State, "runs")).Single();
        Assert.Equal("written by the scenario", File.ReadAllText(Path.Combine(runs, "note.txt")));
        Assert.Empty(stand.GamePids());

        // --attach without a game says so, and does not start one
        Assert.Equal(1, await runner.RunFileAsync(Path.Combine(folder, "single.lua"), keep: false, attach: true));
        Assert.Contains("--attach needs a running game", _game.Output.ToString());
    }
}

/// <summary>The Lua side: what the game answers becomes Lua values, and the prelude's helpers.</summary>
public sealed class LuaBindingTests
{
    static Script New() => Scenario.NewScript(TextWriter.Null);

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
    public void A_scenario_file_is_read_without_running_its_body()
    {
        using var temp = new TempFolder();
        var path = temp.Write("later.lua", """
            scenario { level = "A:B", save = "Save1" }
            function run(game) error("the body must not run while reading the declaration") end
            """);
        var scenario = Scenario.Load(path);
        Assert.Equal("Save1", scenario.Start.Save);
        Assert.False(scenario.Start.Clean);
    }

    [Fact]
    public void Every_scenario_in_the_repository_reads()
    {
        var folder = Repository("scenarios");
        var found = Scenario.Discover(folder);
        Assert.Equal(11, found.Count);
        Assert.All(found, s => Assert.Equal("GustavX:TUT_Avernus_C", s.Start.Level));
    }

    /// <summary>A folder of the repository, found from the test assembly's place.</summary>
    static string Repository(string relative)
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            var candidate = Path.Combine(folder.FullName, relative);
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(folder.FullName, "GateKit.slnx"))) return candidate;
        }
        throw new DirectoryNotFoundException($"no {relative} above {AppContext.BaseDirectory}");
    }
}
