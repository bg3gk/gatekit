using GateKit.Testing;
using GateKit.Copies;

namespace GateKit.UnitTests;

/// <summary>Cases inside a test: `case(name, fn)` at its top level, each from a clean scene, a
/// failing one not stopping the others.</summary>
public sealed partial class GameTestTests
{
    const string Cased = """
        test { level = 'GustavX:TUT_Avernus_C', mods = {}, before = 'stage' }
        function stage(game) step("the shared staging", "cheat", function() game.server("stage") end) end
        case("bow", function(game) step("shoot", "fair", function() game.server("bow") end) end)
        case("crossbow", function(game) step("shoot", "fair", function() error("the bolt went wide") end) end)
        case("sling", function(game) step("look", "observation", function() need(false, "no sling here") end) end)
        case("dart", function(game) step("throw", "fair", function() game.server("dart") end) end)
        """;

    [Fact]
    public async Task Cases_run_each_after_the_staging_and_a_failing_one_does_not_stop_the_others()
    {
        var folder = Tests(("cased", Cased));
        var copy = CopyWithGame("cases");
        var exit = await new TestRunner(copy, _game.Output).RunSuiteAsync(folder, null, false);
        Assert.Equal(1, exit);
        var output = _game.Output.ToString();
        Assert.Contains("case bow: pass", output, StringComparison.Ordinal);
        Assert.Contains("case crossbow: fail", output, StringComparison.Ordinal);
        Assert.Contains("the bolt went wide", output, StringComparison.Ordinal);
        Assert.Contains("case sling: unmet", output, StringComparison.Ordinal);
        Assert.Contains("case dart: pass", output, StringComparison.Ordinal);
        lock (_chunks)
        {
            Assert.Equal(4, _chunks.Count(c => c == "stage"));          // the staging again before every case
            Assert.Contains("dart", _chunks);                            // after a failure and an unmet
            Assert.Contains(_chunks, c => c.Contains("GK.Step(p.name, p.class, p.case)", StringComparison.Ordinal));
        }
        var results = Directory.GetFiles(copy.Layout.State, "results.json", SearchOption.AllDirectories).OrderBy(File.GetLastWriteTimeUtc).Last();
        var data = GateKit.Json.JsonText.Read(results, "record/test-run");
        var test = data["tests"]![0]!;
        Assert.Equal("fail", (string)test["outcome"]!);
        Assert.Equal(["bow", "crossbow", "sling", "dart"], test["cases"]!.AsArray().Select(c => (string)c!["name"]!));
        Assert.Equal("shoot", (string)test["cases"]![0]!["steps"]![1]!["name"]!);
        Assert.StartsWith("cased/", (string)data["slowest"]![0]!["step"]!, StringComparison.Ordinal);
        Assert.Equal(3, ((string)data["slowest"]![0]!["step"]!).Split('/').Length);
    }

    [Fact]
    public async Task K_with_a_case_address_runs_that_case_alone()
    {
        var folder = Tests(("cased", Cased));
        var copy = CopyWithGame("onecase");
        Assert.Equal(0, await new TestRunner(copy, _game.Output).RunSuiteAsync(folder, "cased/dart", false));
        lock (_chunks)
        {
            Assert.Contains("dart", _chunks);
            Assert.DoesNotContain("bow", _chunks);
        }
        Assert.Throws<CopyException>(() => GameTest.Discover(folder, "cased/harpoon"));
    }

    [Fact]
    public void A_case_is_named_for_an_address_and_a_test_has_cases_or_steps_not_both()
    {
        string Error(string code) => Assert.ThrowsAny<CopyException>(() => GameTest.Load(Tests(("bad", code)) + "/bad.lua")).Message;
        Assert.Contains("lower case", Error("case('Bow', function() end)"), StringComparison.Ordinal);
        Assert.Contains("no `/`", Error("case('a/b', function() end)"), StringComparison.Ordinal);
        Assert.Contains("given twice", Error("case('a', function() end) case('a', function() end)"), StringComparison.Ordinal);
        Assert.Contains("has cases or it does not", Error("case('a', function() end) function run(game) end"), StringComparison.Ordinal);
        Assert.Contains("names no function", Error("test { before = 'nothing' } case('a', function() end)"), StringComparison.Ordinal);
        Assert.Equal(["a", "b"], GameTest.Load(Tests(("good", "case('a', function() end) case('b', function() end)")) + "/good.lua").Cases);
    }

    [Fact]
    public void Inside_a_test_with_cases_a_step_outside_a_case_a_step_named_as_a_case_and_a_case_in_a_case_are_refused()
    {
        var steps = new Steps { CaseNames = new HashSet<string>(["bow"], StringComparer.Ordinal) };
        var script = GameTest.NewScript(TextWriter.Null);
        steps.Install(script);
        var outside = Assert.Throws<MoonSharp.Interpreter.ScriptRuntimeException>(() => script.DoString("step('x', 'fair', function() end)"));
        Assert.Contains("inside them", outside.Message, StringComparison.Ordinal);
        steps.BeginCase("bow");
        var named = Assert.Throws<MoonSharp.Interpreter.ScriptRuntimeException>(() => script.DoString("step('bow', 'fair', function() end)"));
        Assert.Contains("not named as a case", named.Message, StringComparison.Ordinal);
        Assert.Throws<MoonSharp.Interpreter.ScriptRuntimeException>(() => steps.BeginCase("bow"));
        script.DoString("step('shoot', 'fair', function() end)");
        Assert.Equal("bow", steps.All[0].Case);
        Assert.Null(steps.EndCase());
    }
}
