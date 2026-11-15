using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit.Abstractions;

namespace GateKit.Json.UnitTests;

/// <summary>What strictness and validation cost on the largest ledger the repository has: the new
/// reader (strict, every row held to its schema) against JsonNode.Parse per line with nothing
/// checked (GateKit.Copies.Record.Read). The times are printed; run with
/// <c>dotnet test --filter SpeedTests --logger "console;verbosity=detailed"</c>.</summary>
public class SpeedTests(ITestOutputHelper output)
{
    [Fact]
    public void Reading_the_sightings_ledger_old_and_new()
    {
        var path = Path.Combine(Repo.Root, "research", "coverage", "record", "sightings.jsonl");
        if (!File.Exists(path)) return;

        static List<JsonObject> Old(string path)
        {
            var rows = new List<JsonObject>();
            foreach (var line in File.ReadLines(path))
            {
                if (line.Trim().Length == 0) continue;
                try { if (JsonNode.Parse(line) is JsonObject row) rows.Add(row); }
                catch (JsonException) { }
            }
            return rows;
        }

        // one of each first, so that neither pays for loading code
        Old(path);
        JsonText.ReadLines(path, onProblem: _ => { });
        var best = (Old: double.MaxValue, New: double.MaxValue, Strict: double.MaxValue, Schema: double.MaxValue);
        int oldRows = 0, newRows = 0, problems = 0;
        for (var round = 0; round < 3; round++)
        {
            var clock = Stopwatch.StartNew();
            oldRows = Old(path).Count;
            best.Old = Math.Min(best.Old, clock.Elapsed.TotalMilliseconds);

            clock.Restart();
            var told = 0;
            newRows = JsonText.ReadLines(path, onProblem: _ => told++).Count;
            best.New = Math.Min(best.New, clock.Elapsed.TotalMilliseconds);
            problems = told;

            clock.Restart();
            var parsed = JsonText.ParseLines(File.ReadAllBytes(path), path, kind: null, SchemaSet.Embedded, []);
            best.Strict = Math.Min(best.Strict, clock.Elapsed.TotalMilliseconds);

            clock.Restart();
            foreach (var row in parsed) SchemaSet.Embedded.Validate(row, "record/sightings");
            best.Schema = Math.Min(best.Schema, clock.Elapsed.TotalMilliseconds);
        }
        output.WriteLine($"the schema check alone over the parsed rows, misfits not placed on lines: {best.Schema:0} ms");
        output.WriteLine($"{path}: {oldRows} rows");
        output.WriteLine($"old (JsonNode.Parse per line, nothing checked): {best.Old:0} ms");
        output.WriteLine($"new, strict parse only (no schema): {best.Strict:0} ms");
        output.WriteLine($"new (strict, every row held to record/sightings/1): {best.New:0} ms - {newRows} rows fit, {problems} problems");
        Assert.True(newRows + problems >= oldRows, "every row is either read or reported");
    }
}
