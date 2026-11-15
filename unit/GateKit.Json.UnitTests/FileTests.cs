using System.Text.Json.Nodes;

namespace GateKit.Json.UnitTests;

/// <summary>Reading and writing files: nothing silent, nothing half-written.</summary>
public class FileTests
{
    static JsonObject Wish(int n) => new()
    {
        ["id"] = $"W-{n:D4}", ["time"] = "2026-11-14T07:59:45Z", ["from"] = "keeper", ["text"] = $"wish {n}", ["state"] = "open",
    };

    [Fact]
    public void A_missing_file_is_null_only_when_asked_and_a_broken_one_always_throws()
    {
        using var temp = new TempFolder();
        Assert.Throws<FileNotFoundException>(() => JsonText.Read(temp.File("none.json")));
        Assert.Null(JsonText.ReadIfExists(temp.File("none.json")));
        var broken = temp.Write("broken.json", "{\"a\": 1,\n}\n");
        var e = Assert.Throws<JsonProblemException>(() => JsonText.ReadIfExists(broken));
        Assert.StartsWith(broken + ":2:1:", e.Message);
    }

    [Fact]
    public void A_bound_file_is_held_to_its_schema_on_reading()
    {
        using var temp = new TempFolder();
        var path = temp.Write("me/gatekit.json", "{\n  \"schema\": \"settings/gatekit/1\",\n  \"startTimeout\": \"75\"\n}\n");
        var e = Assert.Throws<JsonProblemException>(() => JsonText.Read(path));
        var p = Assert.Single(e.Problems);
        Assert.Equal((3, 3), (p.Line, p.Column));
        Assert.Contains("expected an integer", p.What);
        // the document's own `schema` field names its kind where no binding does
        var mine = temp.Write("mine.json", "{\"schema\": \"settings/gatekit/1\", \"color\": 1}");
        Assert.Contains("`color`", Assert.Throws<JsonProblemException>(() => JsonText.Read(mine)).Message);
        // and nothing at all reads as strict JSON only
        Assert.Equal(1, (int)JsonText.Read(temp.Write("free.json", "{\"color\": 1}"))["color"]!);
    }

    [Fact]
    public void Write_refuses_a_misfit_and_writes_the_canonical_text_atomically()
    {
        using var temp = new TempFolder();
        var path = temp.File("x/gatekit.json");
        var settings = new JsonObject { ["startTimeout"] = 75, ["schema"] = "settings/gatekit/1", ["home"] = "/srv/gk" };
        JsonText.Write(path, settings);
        Assert.Equal("{\n  \"schema\": \"settings/gatekit/1\",\n  \"home\": \"/srv/gk\",\n  \"startTimeout\": 75\n}\n", File.ReadAllText(path));
        settings["packer"] = "zip";
        Assert.Throws<JsonProblemException>(() => JsonText.Write(path, settings));
        Assert.DoesNotContain("zip", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(temp.File("x")));   // no temporary file left
        Assert.Throws<ArgumentException>(() => JsonText.Write(temp.File("unbound.json"), new JsonObject()));
    }

    [Fact]
    public async Task Appends_from_two_writers_at_once_give_whole_rows()
    {
        using var temp = new TempFolder();
        var path = temp.File("research/coverage/record/wishes.jsonl");
        const int each = 150;
        void Writer(int from)
        {
            for (var i = 0; i < each; i++) JsonText.Append(path, Wish(from + i));
        }
        await Task.WhenAll(Task.Run(() => Writer(1)), Task.Run(() => Writer(1001)));
        var rows = JsonText.ReadLines(path);
        Assert.Equal(2 * each, rows.Count);
        Assert.Equal(2 * each, rows.Select(r => (string)r["id"]!).Distinct().Count());
        Assert.All(File.ReadAllLines(path), line => Assert.StartsWith("{\"id\":\"W-", line));
    }

    [Fact]
    public void Append_takes_the_callers_lock_and_refuses_a_misfit_whole()
    {
        using var temp = new TempFolder();
        var path = temp.File("research/coverage/record/wishes.jsonl");
        using (var held = JsonLock.Take(temp.File("research/coverage")))
            JsonText.Append(path, [Wish(1), Wish(2)], held: held);
        var bad = Wish(3);
        bad["state"] = "maybe";
        Assert.Throws<JsonProblemException>(() => JsonText.Append(path, [Wish(4), bad]));
        Assert.Equal(2, JsonText.ReadLines(path).Count);
    }

    [Fact]
    public void A_torn_last_line_is_reported_at_its_line_and_blocks_appending()
    {
        using var temp = new TempFolder();
        var path = temp.File("research/coverage/record/wishes.jsonl");
        JsonText.Append(path, [Wish(1), Wish(2)]);
        File.AppendAllText(path, "{\"id\":\"W-0003\",\"time\":\"2026-11-14T07:5");
        var e = Assert.Throws<JsonProblemException>(() => JsonText.ReadLines(path));
        var p = Assert.Single(e.Problems);
        Assert.Equal(3, p.Line);
        Assert.Equal("torn", p.Rule);
        // told, not hidden: with a listener the whole rows still come back
        var told = new List<JsonProblem>();
        Assert.Equal(2, JsonText.ReadLines(path, onProblem: told.Add).Count);
        Assert.Single(told);
        var refused = Assert.Throws<JsonProblemException>(() => JsonText.Append(path, Wish(4)));
        Assert.Contains("no newline", refused.Message);
        Assert.Equal(3, refused.Problems[0].Line);
    }

    [Fact]
    public void A_whole_row_without_its_newline_and_an_empty_line_are_problems_too()
    {
        using var temp = new TempFolder();
        var path = temp.Write("research/coverage/record/wishes.jsonl",
            JsonText.FormatRow(Wish(1)) + "\n\n" + JsonText.FormatRow(Wish(2)));
        var problems = new List<JsonProblem>();
        Assert.Single(JsonText.ReadLines(path, onProblem: problems.Add));
        Assert.Equal(["empty", "torn"], problems.Select(p => p.Rule));
        Assert.Equal([2, 3], problems.Select(p => p.Line));
    }

    [Fact]
    public void WriteLines_replaces_a_ledger_in_canonical_rows()
    {
        using var temp = new TempFolder();
        var path = temp.File("research/coverage/record/wishes.jsonl");
        JsonText.WriteLines(path, [Wish(2), Wish(1)]);
        Assert.Equal(["W-0002", "W-0001"], JsonText.ReadLines(path).Select(r => (string)r["id"]!));
        Assert.EndsWith("\"state\":\"open\"}\n", File.ReadAllText(path));
    }
}
