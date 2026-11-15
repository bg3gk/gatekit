using System.Text.Json.Nodes;

namespace GateKit.Json.UnitTests;

/// <summary>The event log's files: the registry of families and kinds (the bridge mod's
/// Shared/EventKinds.json) fits events/kinds, a row fits events/row, the files are bound.</summary>
public class EventLogSchemaTests
{
    static SchemaSet Set => SchemaSet.Embedded;

    static string Registry => Path.Combine(Repo.Root, "mods", "GateKit", "Mods", "Gwynerva_GateKit", "ScriptExtender", "Lua", "Shared", "EventKinds.json");

    [Fact]
    public void The_registry_fits_its_schema_through_the_registry_of_paths()
    {
        Assert.Equal("events/kinds", Set.ForPath(Registry.Replace('\\', '/'))!.Schema.Kind);
        var registry = JsonText.Read(Registry);   // held to the kind its path is bound to
        Assert.True(registry["kinds"]!.AsObject().Count > 50);
    }

    [Fact]
    public void A_row_fits_and_one_without_who_could_know_it_does_not()
    {
        var row = JsonNode.Parse("""{"seq": 3, "tick": 120, "t": 195329100, "kind": "trade", "knows": "player", "key": "window:trade", "attention": "begin", "payload": {"window": "trade"}}""");
        Assert.Empty(Set.Validate(row, "events/row"));
        // Lua writes an empty table as [], read as the empty payload object
        Assert.Empty(Set.Validate(JsonNode.Parse("""{"seq": 1, "tick": 0, "t": 5, "kind": "note", "knows": "omniscient", "payload": []}"""), "events/row"));
        Assert.Contains(Set.Validate(JsonNode.Parse("""{"seq": 1, "tick": 0, "t": 5, "kind": "Dialog Started", "knows": "player"}"""), "events/row"), m => m.Rule == "pattern");
        Assert.Contains(Set.Validate(JsonNode.Parse("""{"seq": 0, "tick": 0, "t": 5, "kind": "note", "knows": "player"}"""), "events/row"), m => m.Rule == "minimum");
        Assert.Contains(Set.Validate(JsonNode.Parse("""{"seq": 1, "tick": 0, "t": 5, "kind": "note"}"""), "events/row"), m => m.Rule == "required");
    }

    [Fact]
    public void The_files_of_the_log_are_bound_in_the_registry()
    {
        Assert.Equal("events/row", Set.ForPath("profile/Script Extender/Gwynerva_GateKit/events_server_4_2.jsonl")!.Schema.Kind);
        Assert.Equal("events/row", Set.ForPath("Gwynerva_GateKit/events_client_1_1.jsonl")!.Schema.Kind);
        Assert.Equal("events/head", Set.ForPath("Gwynerva_GateKit/events_client.json")!.Schema.Kind);
    }
}
