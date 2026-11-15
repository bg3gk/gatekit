using GateKit.Testing;
using GateKit.Copies;

namespace GateKit.UnitTests;

/// <summary>What the runner holds a test's file to: `require` of shared files only, a speed ceiling
/// with its finding, and where a run's time went.</summary>
public sealed class RunnerRulesTests : IDisposable
{
    readonly string _root = GateKit.Testing.Scratch.NewPath("runner-rules");

    public RunnerRulesTests() => Directory.CreateDirectory(Path.Combine(_root, "a"));

    public void Dispose() => Directory.Delete(_root, true);

    string Write(string relative, string code)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, code);
        return path;
    }

    [Fact]
    public void Require_gives_shared_files_only_and_no_other_way_of_loading_code()
    {
        Write("a/_shared.lua", "return { n = 1 }");
        Write("b/_other.lua", "return { n = 2 }");
        Write("a/plain.lua", "return { n = 3 }");
        var ok = GameTest.Load(Write("a/ok.lua", """
            local S = require("_shared") local O = require("b/_other")
            assert(require("_shared") == S, "a shared file runs once")
            N = S.n + O.n
            function run(game) end
            """));
        Assert.NotNull(ok);
        foreach (var (call, why) in new[] { ("require('plain')", "only shared files"), ("require('../a/_shared')", "a shared file beside the test"),
                     ("dofile('x.lua')", "attempt to call a nil"), ("loadfile('x.lua')", "attempt to call a nil"), ("load('return 1')", "attempt to call a nil") })
        {
            var error = Assert.ThrowsAny<CopyException>(() => GameTest.Load(Write("a/bad.lua", "function run(game) end\n" + call)));
            Assert.Contains(why, error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_header_field_nobody_knows_is_refused_by_name()
    {
        var unknown = Assert.ThrowsAny<CopyException>(() => GameTest.Load(Write("a/clean.lua", "test { level = 'A:B', clean = true }\nfunction run(game) end")));
        Assert.Contains("does not know 'clean'", unknown.Message, StringComparison.Ordinal);
        Assert.Contains("it knows " + string.Join(", ", GameTest.HeaderFields), unknown.Message, StringComparison.Ordinal);
        Assert.Equal("A:B", GameTest.Load(Write("a/known.lua", "test { level = 'A:B', about = 'x' }\nfunction run(game) end")).Start.Level);
    }

    [Fact]
    public void A_speed_ceiling_without_its_finding_is_refused()
    {
        var bare = Assert.ThrowsAny<CopyException>(() => GameTest.Load(Write("a/fast.lua", "test { speed_ceiling = 1 }\nfunction run(game) end")));
        Assert.Contains("names its finding", bare.Message, StringComparison.Ordinal);
        var named = GameTest.Load(Write("a/slow.lua", "test { speed_ceiling = 1, ceiling_finding = 'research/studies/x.md' }\nfunction run(game) end"));
        Assert.Equal(1, named.SpeedCeiling);
        Assert.Equal(1, named.Again(2).SpeedCeiling);
    }

    [Fact]
    public void A_run_names_its_slowest_steps_by_address_and_the_time_in_waits_apart_from_the_games_answers()
    {
        static StepRecord Step(string name, double seconds, double waits, double answers) =>
            new(name, "observation") { Outcome = "pass", Took = TimeSpan.FromSeconds(seconds), Waits = waits, Answers = answers };
        var results = new List<TestResult>
        {
            new("combat/melee/hit", true, "PASS", TimeSpan.FromSeconds(9))
            {
                Steps = [Step("the scene", 1, 0.2, 0.7), Step("the hit came", 6, 5.5, 0.4)],
                Waits = new Dictionary<string, double> { ["wait_row"] = 5.5, ["wait"] = 0.2 }, Answers = 1.1,
            },
            new("combat/melee/miss", true, "PASS", TimeSpan.FromSeconds(3)) { Steps = [Step("aim", 2, 0, 1.9)], Answers = 1.9 },
        };
        var slowest = TestRunner.Slowest(results).Select(s => s.Step).ToList();
        Assert.Equal(["combat/melee/hit/the hit came", "combat/melee/miss/aim", "combat/melee/hit/the scene"], slowest);
        var lines = TestRunner.Slowness(results).ToList();
        Assert.StartsWith("slowest steps: combat/melee/hit/the hit came 6.0 s (waits 5.5 s, answers 0.4 s)", lines[0], StringComparison.Ordinal);
        Assert.Equal("time in waits 5.7 s (wait_row 5.5 s, wait 0.2 s); in the game's answers 3.0 s", lines[1]);
        var data = TestRunner.Results(results, TimeSpan.FromSeconds(12), 0);
        Assert.Empty(GateKit.Json.SchemaSet.Embedded.Validate(data, "record/test-run"));
        Assert.Equal("combat/melee/hit/the hit came", (string)data["slowest"]![0]!["step"]!);
        Assert.Equal(5.5, (double)data["waits"]!["wait_row"]!);
    }
}
