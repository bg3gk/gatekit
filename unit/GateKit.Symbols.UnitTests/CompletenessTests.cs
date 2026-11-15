using System.Text.Json.Nodes;
using GateKit.Symbols.Decomp;
using GateKit.Symbols.Registry;

namespace GateKit.Symbols.UnitTests;

/// <summary>`decomp tally`'s count of the registry: described against known, by kind, the two states
/// of the unknown apart, by rung.</summary>
public sealed class CompletenessTests
{
    static RegistryEntry Entry(EntryKind kind, string json) => new(kind, "x", (JsonObject)JsonNode.Parse(json)!);

    static readonly List<RegistryEntry> Entries =
    [
        Entry(EntryKind.Function, """{"id": "toy::A", "state": "described", "rung": "confirmed", "does": "Does A."}"""),
        Entry(EntryKind.Function, """{"id": "toy::B", "state": "looked_at", "rung": "unknown"}"""),
        Entry(EntryKind.Function, """{"id": "toy::C", "state": "unlooked", "rung": "unknown"}"""),
        Entry(EntryKind.Function, """{"id": "toy::Elsewhere", "state": "described", "rung": "inferred", "does": "Not in this binary."}"""),
        Entry(EntryKind.Type, """{"id": "toy::Widget", "kind": "struct", "fields": [{"offset": 0, "name": "Owner", "rung": "game"}, {"offset": 8, "name": "field_8", "rung": "inferred"}, {"offset": 16, "name": "Timer", "rung": "unknown"}]}"""),
        Entry(EntryKind.Type, """{"id": "toy::Blank", "kind": "struct", "state": "looked_at", "fields": [{"offset": 0, "name": "unk0"}]}"""),
        Entry(EntryKind.Type, """{"id": "toy::Mode", "kind": "enum", "rung": "game", "values": [{"name": "Off", "value": "0"}]}"""),
        Entry(EntryKind.Global, """{"id": "toy::gWorld", "kind": "variable", "state": "described", "rung": "inferred", "about": "The world."}"""),
    ];

    [Fact]
    public void Functions_are_counted_through_the_binarys_table_with_the_two_states_of_the_unknown_apart()
    {
        var t = Completeness.Functions(Entries, new HashSet<string> { "toy::A", "toy::B", "toy::C" }, 100);
        Assert.Equal((100L, 3L, 1L, 1L, 1L), (t.Known!.Value, t.InRegistry, t.Described, t.LookedAt, t.Unlooked));
        Assert.Equal(98, t.NeverLookedAt);                                      // 97 not in the registry, 1 in it as never looked at
        Assert.Equal(["confirmed", "unknown"], t.ByRung.Keys);                  // strongest first
        Assert.Equal(2, t.ByRung["unknown"]);
        Assert.Null(Completeness.Functions(Entries, new HashSet<string>(), null).NeverLookedAt);
    }

    [Fact]
    public void Types_fields_globals_and_enumerations_are_counted_against_the_names_the_databases_declare()
    {
        var all = Completeness.Others(Entries, new HashSet<string> { "toy::Widget", "toy::Gadget", "toy::Sprocket" }, new HashSet<string> { "toy::Mode", "toy::Other" });
        var (types, fields, globals, enums) = (all[0], all[1], all[2], all[3]);
        // types: Widget names a field; Blank was looked at and holds only a placeholder; Gadget and Sprocket never looked at
        Assert.Equal((4L, 2L, 1L, 1L, 2L), (types.Known!.Value, types.InRegistry, types.Described, types.LookedAt, types.NeverLookedAt!.Value));
        // fields: Owner described; field_8 a placeholder; Timer on the rung unknown; unk0 a placeholder
        Assert.Equal((4L, 1L), (fields.Known!.Value, fields.Described));
        Assert.Equal((1L, 1L, 1L, 1L), (fields.ByRung["game"], fields.ByRung["inferred"], fields.ByRung["unknown"], fields.ByRung["no rung"]));
        Assert.Null(globals.Known);
        Assert.Equal((1L, 1L), (globals.InRegistry, globals.Described));
        Assert.Equal((2L, 1L, 1L, 1L), (enums.Known!.Value, enums.InRegistry, enums.Described, enums.NeverLookedAt!.Value));

        var unknown = Completeness.Others(Entries, null, null);
        Assert.Null(unknown[0].Known);
        Assert.Contains("never looked at not counted", unknown[0].Line());
    }
}
