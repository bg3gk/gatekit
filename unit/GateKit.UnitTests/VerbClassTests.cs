using System.Text.Json.Nodes;
using GateKit.Bridge;
using GateKit.Copies;
using MoonSharp.Interpreter;

namespace GateKit.UnitTests;

/// <summary>
/// The class of every verb (Shared/VerbClasses.json): held to the functions the mod's Lua defines,
/// both ways; each namespace held to the classes it may hold; and the rule a step is held to, run
/// in a Lua interpreter on the mod's own Shared/Verbs.lua.
/// </summary>
public sealed class VerbClassTests
{
    static string Lua => Path.Combine(CopySettings.RepositoryRoot()!, "mods", "GateKit", "Mods", "Gwynerva_GateKit", "ScriptExtender", "Lua");

    [Fact]
    public void Every_function_of_the_bridge_has_a_class_and_every_class_a_function()
    {
        var classes = VerbClasses.Of(Lua);
        var (unclassed, gone) = classes.Against(BridgeVerbs.Read(Lua));
        Assert.True(unclassed.Count == 0, "functions with no class in Shared/VerbClasses.json: " + string.Join(", ", unclassed));
        Assert.True(gone.Count == 0, "classes of functions the Lua does not define: " + string.Join(", ", gone));
        // the registry the host carries is the mod's
        Assert.Equal(classes.Verbs.Count, VerbClasses.Embedded.Verbs.Count);
    }

    /// <summary>A name decides what a verb may be: GK.Act and GK.Gui go the player's own path or only
    /// read; a cheat lives in GK.Cheat or a staging namespace.</summary>
    [Fact]
    public void Each_namespace_holds_only_the_classes_it_may()
    {
        var allowed = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Act"] = ["fair", "observation"],
            ["Gui"] = ["fair", "observation"],
            ["Cheat"] = ["cheat", "observation"],
            ["Stage"] = ["cheat", "observation"],
            ["Fixture"] = ["cheat", "observation"],
            ["Speed"] = ["cheat", "observation"],
            ["Memory"] = ["cheat", "observation"],
            ["See"] = ["cheat", "observation"],
            ["Probe"] = ["observation"],
            ["Seen"] = ["observation"],
            ["Conditions"] = ["observation"],
            ["GameLog"] = ["observation"],
            ["Heard"] = ["observation"],
            ["Hints"] = ["observation"],
        };
        var wrong = VerbClasses.Embedded.Verbs
            .Where(v => v.Key.Contains('.') && allowed.TryGetValue(v.Key[..v.Key.IndexOf('.')], out var may) && !may.Contains(v.Value.Class))
            .Select(v => $"{v.Key} is {v.Value.Class}").ToList();
        Assert.True(wrong.Count == 0, string.Join("; ", wrong));
        Assert.All(VerbClasses.Embedded.Verbs.Where(v => v.Value.Class == "cheat" && v.Key.StartsWith("Act.", StringComparison.Ordinal)), v => Assert.Fail(v.Key));
    }

    [Fact]
    public void The_scripted_actions_have_a_cheat_of_the_same_name_and_the_fair_name_refuses()
    {
        var classes = VerbClasses.Embedded;
        foreach (var name in new[] { "Attack", "Cast", "Move", "Use", "Talk", "Trade" })
        {
            Assert.Equal("cheat", classes.ClassOf("Cheat." + name)!.Class);
            Assert.Equal("fair", classes.ClassOf("GK.Act." + name)!.Class);
        }
        Assert.Equal("cheat", classes.ClassOf("Cheat.Teleport")!.Class);   // the catalogue, by the namespace
        Assert.Null(classes.ClassOf("Act.NoSuchVerb"));
    }

    /// <summary>No test calls a GK.Act or GK.Gui name that does nothing: one that refuses (its player's
    /// path is not built; Server/Act.lua answers it with refuse) or one the registry does not have
    /// (a verb that moved to GK.Cheat without a fair twin). A line that calls a refusing name to test
    /// the refusal itself says so: "the refusal is what is tested".</summary>
    [Fact]
    public void No_test_calls_a_fair_name_that_refuses_or_is_gone()
    {
        var root = CopySettings.RepositoryRoot()!;
        var refusing = System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(Path.Combine(Lua, "Server", "Act.lua")),
                @"function Act\.(\w+)\(\)\s*return refuse\(")
            .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("Jump", refusing);
        Assert.Contains("Talk", refusing);
        var classes = VerbClasses.Embedded;
        var call = new System.Text.RegularExpressions.Regex(@"GK\.(Act|Gui)\.(\w+)\s*[\(\{]");
        var files = new[] { "tests" }.Select(d => Path.Combine(root, d)).Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.*", SearchOption.AllDirectories))
            .Concat(Directory.Exists(Path.Combine(root, "research", "coverage", "tours"))
                ? Directory.EnumerateFiles(Path.Combine(root, "research", "coverage", "tours"), "*.json", SearchOption.AllDirectories) : [])
            .Where(f => f.EndsWith(".lua", StringComparison.Ordinal) || f.EndsWith(".json", StringComparison.Ordinal));
        var wrong = new List<string>();
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("--", StringComparison.Ordinal) || lines[i].Contains("the refusal is what is tested", StringComparison.Ordinal)) continue;
                foreach (System.Text.RegularExpressions.Match m in call.Matches(lines[i]))
                {
                    var (space, name) = (m.Groups[1].Value, m.Groups[2].Value);
                    var where = $"{Path.GetRelativePath(root, file)}:{i + 1}: GK.{space}.{name}";
                    if (space == "Act" && refusing.Contains(name)) wrong.Add(where + " refuses (GK.Cheat." + name + " is the scripted form)");
                    else if (classes.ClassOf(space + "." + name) is null)
                        wrong.Add(where + (classes.ClassOf("Cheat." + name) is not null ? " is gone (GK.Cheat." + name + ")" : " is not a verb of the registry"));
                }
            }
        }
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    [Fact]
    public void A_function_bound_to_GK_after_it_was_written_is_found()
    {
        using var temp = new TempFolder();
        temp.Write("Shared/Late.lua", """
            Late = {}
            --- Reads.
            function Late.Read() end
            Late.Assigned = function(x) end
            local Private = {}
            function Private.Hidden() end
            GK.Late = Late
            """);
        var found = BridgeVerbs.Read(temp.Path);
        Assert.Equal(["GK.Late.Read", "GK.Late.Assigned"], found.Select(f => f.Path));
        Assert.Equal("Reads.", found[0].About);
    }

    // --- the rule a step is held to, on the mod's own Lua -----------------------------------------

    const string World = """
        Ext = {
            Utils = { MonotonicTime = function() return 0 end, PrintError = function() end },
            IO = { LoadFile = function() return nil end },
            Json = { Parse = function(text) return json.parse(text) end },
        }
        GK = {}
        """;

    static Script Game()
    {
        var script = new Script(CoreModules.Preset_Complete);
        script.DoString(World, null, "world");
        script.DoString(File.ReadAllText(Path.Combine(Lua, "Shared", "Bridge.lua")), null, "Bridge.lua");
        script.DoString(File.ReadAllText(Path.Combine(Lua, "Shared", "Verbs.lua")), null, "Verbs.lua");
        script.Globals["registry"] = File.ReadAllText(Path.Combine(Lua, "Shared", "VerbClasses.json"));
        script.DoString("""
            GK.Verbs.Load(registry)
            GK.Act = { Select = function(who) return "selected " .. tostring(who) end,
                       Attack = function() return { ok = false } end, Job = function() return "job" end }
            GK.Cheat = { Attack = function() return GK.Act.Job() .. " struck" end, Teleport = function() return "moved" end }
            GK.Gui = { Click = function() return "clicked" end, Read = function() return "read" end }
            GK.Verbs.Guard()
            """);
        return script;
    }

    static Table Run(Script game, string code, string? cls) =>
        game.Call(game.Globals.Get("GK").Table.Get("Run"), DynValue.NewString(code), cls is null ? DynValue.Nil : DynValue.NewString(cls)).Table;

    [Fact]
    public void A_fair_step_refuses_a_cheat_and_allows_what_is_fair_or_reads()
    {
        var game = Game();
        var refused = Run(game, "return GK.Cheat.Attack()", "fair");
        Assert.False(refused.Get("ok").Boolean);
        Assert.Contains("a fair step called the cheat GK.Cheat.Attack", refused.Get("error").String);
        Assert.Equal("selected me", Run(game, "return GK.Act.Select('me')", "fair").Get("result").String);
        Assert.Equal("read", Run(game, "return GK.Gui.Read()", "fair").Get("result").String);
        // the catalogue's functions are cheats by their namespace
        Assert.Contains("the cheat GK.Cheat.Teleport", Run(game, "return GK.Cheat.Teleport()", "fair").Get("error").String);
    }

    [Fact]
    public void An_observation_step_allows_only_observations()
    {
        var game = Game();
        Assert.Contains("an observation step called the fair GK.Gui.Click", Run(game, "return GK.Gui.Click()", "observation").Get("error").String);
        Assert.Equal("job", Run(game, "return GK.Act.Job()", "observation").Get("result").String);
    }

    [Fact]
    public void A_cheat_step_and_a_chunk_without_a_class_allow_everything()
    {
        var game = Game();
        // what a verb calls inside itself is not judged: Cheat.Attack calls Act.Job
        Assert.Equal("job struck", Run(game, "return GK.Cheat.Attack()", "cheat").Get("result").String);
        Assert.Equal("job struck", Run(game, "return GK.Cheat.Attack()", null).Get("result").String);
        Assert.Contains("is fair, cheat or observation", Run(game, "return 1", "honest").Get("error").String);
    }

    [Fact]
    public void A_refusal_caught_by_the_chunk_still_fails_it_and_every_cheat_is_recorded()
    {
        var game = Game();
        var caught = Run(game, "local ok = pcall(GK.Cheat.Attack) return 'went on'", "fair");
        Assert.False(caught.Get("ok").Boolean);
        Assert.Equal("a fair step called the cheat GK.Cheat.Attack", caught.Get("error").String);
        game.DoString("GK.Cheat.Log = {}");
        Run(game, "GK.Cheat.Teleport() GK.Cheat.Attack()", null);
        Assert.Equal(["Teleport", "Attack"], game.DoString("local out = {} for _, row in ipairs(GK.Cheat.Log) do out[#out + 1] = row.name end return out")
            .Table.Values.Select(v => v.String));
        // each namespace carries its classes as a table, as GK.Memory.Class always did
        Assert.Equal("fair", game.DoString("return GK.Act.Class.Select").String);
        Assert.Equal("cheat", game.DoString("return GK.Cheat.Class.Attack").String);
    }

    [Fact]
    public void A_job_row_carries_the_class_of_the_verb_that_made_it()
    {
        var kinds = EventKinds.Embedded;
        Assert.True(kinds.Kinds["job_done"].ClassPerRow);
        Assert.Equal("cheat", kinds.Kinds["job_done"].Class);
        JsonObject Row(string kind, string cls) => new()
        {
            ["seq"] = 1, ["tick"] = 0, ["t"] = 0, ["kind"] = kind, ["knows"] = "player", ["attention"] = "news", ["class"] = cls,
        };
        Assert.Empty(kinds.Check(Row("job_done", "fair")).Unplaced);
        Assert.NotEmpty(kinds.Check(Row("death", "cheat")).Unplaced);
        Assert.Contains("step", kinds.Kinds.Keys);
        Assert.Equal("note", kinds.Kinds["step_ended"].Family);
    }
}
