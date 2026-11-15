using System.CommandLine;

namespace GateKit.Json.UnitTests;

/// <summary>`gatekit json` on a temporary tree with the repository's schemas.</summary>
public sealed class CommandTests : IDisposable
{
    readonly TempFolder _tree = new();

    public CommandTests()
    {
        foreach (var file in Directory.EnumerateFiles(Repo.Schemas, "*.json", SearchOption.AllDirectories))
            _tree.Write("schemas/" + Path.GetRelativePath(Repo.Schemas, file).Replace('\\', '/'), File.ReadAllText(file));
    }

    public void Dispose() => _tree.Dispose();

    (int Exit, string Output, string Error) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var root = new RootCommand { JsonCommands.CreateCommand() };
        var exit = root.Parse(["json", "--root", _tree.Path, .. args]).Invoke(new InvocationConfiguration { Output = output, Error = error });
        return (exit, output.ToString().Replace("\r\n", "\n"), error.ToString());
    }

    const string GoodRow = """{"type":"T::A","side":"server","time":"2026-11-14T09:14:50Z","instance":"w","role":"r","run":"u","stretch":"s","source":"tick","state":"sighted"}""";

    [Fact]
    public void Check_walks_the_tree_through_the_registry_and_names_every_problem()
    {
        _tree.Write("research/coverage/record/sightings.jsonl", GoodRow + "\n" + GoodRow.Replace("server", "both") + "\n{\"type\":\n");
        _tree.Write("research/coverage/notes.json", "{ not even json");   // not bound: not read
        _tree.Write("deep/x/gatekit.json", "{\"schema\": \"settings/gatekit/1\", \"home\": \"\"}\n");
        var (exit, output, _) = Run("check");
        Assert.Equal(1, exit);
        Assert.Contains("research/coverage/record/sightings.jsonl:2:", output);
        Assert.Contains("`/side` is \"both\"", output);
        Assert.Contains("research/coverage/record/sightings.jsonl:3:", output);
        Assert.Contains("deep/x/gatekit.json:1:", output);
        Assert.DoesNotContain("notes.json", output);
        Assert.Contains("3 problems in 2 files", output);

        var counts = Run("check", "--counts").Output;
        Assert.Contains("record/sightings/1", counts);
        Assert.Contains("/side", counts);

        var any = Run("check", "--any").Output;
        Assert.Contains("notes.json:1:3:", any);
    }

    [Fact]
    public void Check_of_a_clean_tree_exits_0_and_answers_in_json_when_asked()
    {
        _tree.Write("research/coverage/record/sightings.jsonl", GoodRow + "\n");
        var (exit, output, _) = Run("check", "--json");
        Assert.Equal(0, exit);
        Assert.Contains("\"problems\": 0", output);
    }

    [Fact]
    public void Fmt_checks_and_writes_the_canonical_layout()
    {
        var ledger = _tree.Write("research/coverage/record/sightings.jsonl", """{"state":"sighted","type":"T::A","side":"server","time":"2026-11-14T09:14:50Z","instance":"w","role":"r","run":"u","stretch":"s","source":"tick"}""" + "\n");
        var settings = _tree.Write("gatekit.json", "{\"home\":\"/srv\",\"schema\":\"settings/gatekit/1\"}");
        var check = Run("fmt", "--check");
        Assert.Equal(1, check.Exit);
        Assert.Contains("research/coverage/record/sightings.jsonl: not in the canonical layout", check.Output);
        Assert.Equal(0, Run("fmt").Exit);
        Assert.Equal(GoodRow + "\n", File.ReadAllText(ledger));
        Assert.Equal("{\n  \"schema\": \"settings/gatekit/1\",\n  \"home\": \"/srv\"\n}\n", File.ReadAllText(settings));
        Assert.Equal(0, Run("fmt", "--check").Exit);
    }

    [Fact]
    public void Show_prints_a_ledger_as_a_table_and_a_document_as_an_outline()
    {
        var ledger = _tree.Write("research/coverage/record/sightings.jsonl", GoodRow + "\n" + GoodRow.Replace("T::A", "T::B") + "\n");
        var table = Run("show", ledger).Output.Split('\n');
        Assert.StartsWith("type  side    time", table[0]);
        Assert.StartsWith("T::B", table[3]);
        var doc = _tree.Write("doc.json", """{"name": "x", "level": 3, "inventory": [{"item": "a", "count": 1}], "tags": ["p", "q"]}""");
        var outline = Run("show", doc).Output;
        Assert.Contains("name   x\nlevel  3\ninventory  (1)\n  item  count\n", outline);
        Assert.Contains("tags  (2)\n  - p\n  - q\n", outline);
        Assert.Equal("item  count\n----  -----\na     1\n", Run("show", doc, "/inventory").Output);
        Assert.Equal(1, Run("show", doc, "/nothing").Exit);
    }

    [Fact]
    public void Schema_lists_kinds_with_their_bindings_and_prints_one()
    {
        var list = Run("schema", "--list").Output;
        Assert.Contains("record/sightings/1", list);
        Assert.Contains("    file research/coverage/record/sightings.jsonl", list);
        var one = Run("schema", "bridge/command");
        Assert.Equal(0, one.Exit);
        Assert.Contains("\"$id\": \"bridge/command/1\"", one.Output);
        Assert.Equal(1, Run("schema", "no/such").Exit);
    }
}
