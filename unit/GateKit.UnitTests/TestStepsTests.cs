using System.Text.Json.Nodes;
using GateKit.Testing;

namespace GateKit.UnitTests;

/// <summary>Tests in a tree of folders, named steps with their class, and preconditions (need).</summary>
public sealed partial class GameTestTests
{
    [Fact]
    public void Tests_are_found_in_a_tree_and_named_by_their_path()
    {
        var root = _game.Temp["tree"];
        void Write(string relative, string code)
        {
            var path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, code);
        }
        Write("flat.lua", "function run(game) end");
        Write("combat/attack/melee_miss.lua", "function run(game) end");
        Write("rolls/pin/melee_miss.lua", "function run(game) end");   // the same file name elsewhere
        Write("rolls/pin/_shared.lua", "x = 1");
        Write("recorded/_suite.json", """{"schema": "tests/suite/1", "about": "read when named", "read": "named"}""");
        Write("recorded/apart.lua", "function run(game) end");

        var found = GameTest.Discover(root);
        Assert.Equal(["combat/attack/melee_miss", "flat", "rolls/pin/melee_miss"], found.Select(s => s.Name));
        Assert.Equal(["rolls/pin/melee_miss"], GameTest.Discover(root, "pin/melee").Select(s => s.Name));
        Assert.Equal(["combat/attack/melee_miss", "rolls/pin/melee_miss"], GameTest.Discover(root, "melee_miss").Select(s => s.Name));
        Assert.Equal(["apart"], GameTest.Discover(Path.Combine(root, "recorded")).Select(s => s.Name));
        Assert.Equal("rolls/pin/melee_miss#2", found[2].Again(2).Name);
    }

    [Fact]
    public void A_test_requires_the_shared_file_beside_it()
    {
        var root = _game.Temp["shared"];
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "_helpers.lua"), "return { answer = function() return 42 end }");
        File.WriteAllText(Path.Combine(root, "uses.lua"), """
            local helpers = require("_helpers")
            assert(helpers.answer() == 42)
            function run(game) end
            """);
        Directory.CreateDirectory(Path.Combine(root, "sibling"));
        Directory.CreateDirectory(Path.Combine(root, "other"));
        File.WriteAllText(Path.Combine(root, "sibling", "_shared.lua"), "return { answer = 7 }");
        File.WriteAllText(Path.Combine(root, "other", "across.lua"), """
            assert(require("sibling/_shared").answer == 7)
            function run(game) end
            """);
        Assert.Equal(["other/across", "uses"], GameTest.Discover(root).Select(s => s.Name));
    }

    [Fact]
    public async Task Two_tests_of_one_file_name_keep_their_own_output()
    {
        var root = _game.Temp["same"];
        foreach (var part in new[] { "a", "b" })
        {
            Directory.CreateDirectory(Path.Combine(root, part));
            File.WriteAllText(Path.Combine(root, part, "case.lua"), $"function run(game) game.save('mine.txt', '{part}') end");
        }
        var copy = CopyWithGame("same");
        Assert.Equal(0, await new TestRunner(copy, _game.Output).RunSuiteAsync(root, null, false));
        var run = Directory.EnumerateDirectories(Path.Combine(copy.Layout.State, "runs")).Single();
        Assert.Equal("a", File.ReadAllText(Path.Combine(run, "a", "case", "mine.txt")));
        Assert.Equal("b", File.ReadAllText(Path.Combine(run, "b", "case", "mine.txt")));
    }

    [Fact]
    public async Task Steps_carry_their_class_to_the_game_and_are_reported()
    {
        var folder = Tests(("stepped", """
            function run(game)
                step("stage the dummy", "cheat", function() game.server("return 'staged'") end)
                local hit = step("swing", "fair", function() return game.server("return 'swung'") end)
                eq(hit, "swung")
                step("read", "observation", function() game.server("return 'read'") end)
                game.server("return 'after'")
            end
            """));
        var copy = CopyWithGame("stepped", code => code.StartsWith("return '", StringComparison.Ordinal) ? JsonValue.Create(code[8..^1]) : null);
        var runner = new TestRunner(copy, _game.Output);

        Assert.Equal(0, await runner.RunSuiteAsync(folder, null, false));

        var server = _bridges.First(b => { lock (b.Received) return b.Received.Any(r => r.Code.EndsWith("return 'swung'", StringComparison.Ordinal)); });
        List<(string Code, string? Class)> seen;
        lock (server.Received) seen = [.. server.Received];
        string? ClassOf(string tail) => seen.First(r => r.Code.EndsWith(tail, StringComparison.Ordinal)).Class;
        Assert.Equal("cheat", ClassOf("return 'staged'"));
        Assert.Equal("fair", ClassOf("return 'swung'"));
        Assert.Equal("observation", ClassOf("return 'read'"));
        Assert.Null(ClassOf("return 'after'"));
        // each step is a pair of rows in the game's event log
        Assert.Contains(seen, r => r.Code.Contains("GK.Step(p.name, p.class, p.case)", StringComparison.Ordinal) && r.Class is null);
        Assert.Contains(seen, r => r.Code.Contains("GK.StepEnded(", StringComparison.Ordinal));
        var output = _game.Output.ToString();
        Assert.Contains("step swing (fair) pass", output);
        Assert.Contains("step read (observation) pass", output);
        var run = Directory.EnumerateDirectories(Path.Combine(copy.Layout.State, "runs")).Single();
        var results = GateKit.Json.JsonText.Read(Path.Combine(run, "results.json"));
        Assert.Equal("pass", (string)results["tests"]![0]!["outcome"]!);
        Assert.Equal(["cheat", "fair", "observation"], results["tests"]![0]!["steps"]!.AsArray().Select(s => (string)s!["class"]!));
    }

    [Fact]
    public async Task A_failure_names_its_step_and_stops_the_test_there()
    {
        var folder = Tests(("broken", """
            function run(game)
                step("swing", "fair", function() game.server("return boom") end)
                game.server("return 'never'")
            end
            """));
        var copy = CopyWithGame("broken", code => code == "return boom" ? throw new InvalidOperationException("a fair step called the cheat GK.Cheat.Attack") : null);
        Assert.Equal(1, await new TestRunner(copy, _game.Output).RunSuiteAsync(folder, null, false));
        var output = _game.Output.ToString();
        Assert.Contains("step swing (fair) failed:", output);
        Assert.Contains("a fair step called the cheat GK.Cheat.Attack", output);
        lock (_chunks) Assert.DoesNotContain(_chunks, c => c.Contains("'never'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_step_is_held_to_its_class_on_the_host_too()
    {
        var folder = Tests(
            ("fair_resets", "function run(game) step('reset', 'fair', function() game.reset() end) end"),
            ("observation_presses", "function run(game) step('press', 'observation', function() game.key('escape') end) end"),
            ("cheat_resets", "function run(game) step('reset', 'cheat', function() pcall(game.checkpoint) end) end"),
            ("nested", "function run(game) step('outer', 'cheat', function() step('inner', 'fair', function() end) end) end"),
            ("bad_class", "function run(game) step('x', 'honest', function() end) end"));
        var copy = CopyWithGame("held");
        Assert.Equal(1, await new TestRunner(copy, _game.Output).RunSuiteAsync(folder, null, false));
        var output = _game.Output.ToString();
        Assert.Contains("a fair step called the cheat game.reset", output);
        Assert.Contains("an observation step called the fair game.key", output);
        Assert.Contains("PASS  cheat_resets", output);
        Assert.Contains("steps do not nest", output);
        Assert.Contains("the class is fair, cheat or observation", output);
    }

    [Fact]
    public async Task An_unmet_precondition_is_a_third_outcome()
    {
        var folder = Tests(
            ("needs", """
                function run(game)
                    need(true, "always there")
                    step("check", "observation", function() need(false, "no thieves' tools in this copy") end)
                    error("not reached")
                end
                """),
            ("plain", "function run(game) end"));
        var copy = CopyWithGame("unmet");
        var runner = new TestRunner(copy, _game.Output) { Json = true };

        Assert.Equal(2, await runner.RunSuiteAsync(folder, null, false));

        var output = _game.Output.ToString();
        Assert.Contains("UNMET  needs", output);
        Assert.Contains("UNMET: no thieves' tools in this copy", output);
        Assert.Contains("1 passed, 0 failed, 1 unmet", output);
        Assert.Contains("step check (observation) unmet", output);
        Assert.Contains("\"outcome\": \"unmet\"", output);
        var run = Directory.EnumerateDirectories(Path.Combine(copy.Layout.State, "runs")).Single();
        Assert.False(File.Exists(Path.Combine(run, "needs", "failure.jpg")));
        var results = GateKit.Json.JsonText.Read(Path.Combine(run, "results.json"));
        Assert.Equal(2, (int)results["exit"]!);
        Assert.Equal(["unmet", "pass"], results["tests"]!.AsArray().Select(t => (string)t!["outcome"]!));
        // a failure as well makes the run fail
        Assert.Equal(1, TestRunner.ExitCode([new TestResult("a", false, "UNMET: x", TimeSpan.Zero) { Unmet = true },
            new TestResult("b", false, "FAIL: y", TimeSpan.Zero)]));
    }
}
