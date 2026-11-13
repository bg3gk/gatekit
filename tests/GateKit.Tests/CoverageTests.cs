using System.Text.Json.Nodes;
using GateKit.Mcp;

namespace GateKit.Tests;

/// <summary>
/// The parts of the coverage loop that decide whether a layout is called right: what a field
/// should hold, whether what Script Extender returned is the same thing, and what moved between
/// two readings. These are checked here rather than against a game because a claim about the
/// engine is only as good as the comparison behind it, and a comparison that is wrong in a way
/// the game happens not to show is the worst thing this toolkit could ship: a confident wrong
/// name travels into other people's mods.
/// </summary>
public class CoverageTests
{
    static byte[] Bytes(params byte[] values) => values;

    static LayoutCheck.Field Field(string type, int offset = 0) => new("F", offset, type);

    [Fact]
    public void A_float_field_is_read_off_the_bytes_and_compared_with_a_tolerance()
    {
        var raw = BitConverter.GetBytes(1.25f);
        var want = LayoutCheck.Expected(Field("float"), raw);
        Assert.NotNull(want);
        Assert.True(LayoutCheck.Same(want, JsonValue.Create(1.25)));
        Assert.True(LayoutCheck.Same(want, JsonValue.Create(1.2500001)));
        Assert.False(LayoutCheck.Same(want, JsonValue.Create(1.26)));
    }

    [Fact]
    public void An_entity_handle_survives_the_trip_through_json_without_a_double()
    {
        // 72057598332905975 is an ordinary handle and is past 2^53. Read through a double it
        // would land on its neighbour, and two different entities would compare equal.
        const ulong handle = 72057598332905975;
        var want = LayoutCheck.Expected(Field("EntityHandle"), BitConverter.GetBytes(handle));
        Assert.NotNull(want);
        Assert.True(LayoutCheck.Same(want, new JsonObject { ["Entity"] = handle }));
        Assert.False(LayoutCheck.Same(want, new JsonObject { ["Entity"] = handle - 1 }));
    }

    [Fact]
    public void The_null_handle_is_the_nil_script_extender_returns()
    {
        var want = LayoutCheck.Expected(Field("EntityHandle"), BitConverter.GetBytes(0xFFC0000000000000));
        Assert.NotNull(want);
        Assert.True(LayoutCheck.Same(want, null));
        Assert.True(LayoutCheck.Same(LayoutCheck.Expected(Field("EntityHandle"), new byte[8])!, null));
    }

    [Fact]
    public void An_arrays_length_is_read_from_where_the_array_keeps_it()
    {
        // Array<T>: pointer, capacity at +8, size at +0xC.
        var raw = new byte[16];
        BitConverter.GetBytes(7u).CopyTo(raw, 12);
        var want = LayoutCheck.Expected(Field("Array<EntityHandle>"), raw);
        Assert.NotNull(want);
        Assert.True(LayoutCheck.Same(want, new JsonObject { ["Length"] = 7 }));
        Assert.True(LayoutCheck.Same(want, new JsonObject { ["Count"] = 7 }));
        Assert.False(LayoutCheck.Same(want, new JsonObject { ["Length"] = 6 }));
    }

    [Fact]
    public void A_signed_primitive_narrower_than_eight_bytes_keeps_its_sign()
    {
        var want = LayoutCheck.Expected(Field("int16_t"), Bytes(0xFF, 0xFF));
        Assert.NotNull(want);
        Assert.True(LayoutCheck.Same(want, JsonValue.Create(-1)));
    }

    [Fact]
    public void A_type_the_comparison_cannot_read_is_not_compared_rather_than_passed()
    {
        Assert.Null(LayoutCheck.Expected(Field("SomeNestedStruct"), new byte[64]));
        Assert.Null(LayoutCheck.Expected(Field("TranslatedString"), new byte[64]));
        // A field past the end of the sample has nothing to read, and claiming agreement there
        // would mark a layout verified on bytes that were never seen.
        Assert.Null(LayoutCheck.Expected(Field("float", 60), new byte[8]));
    }

    static JsonNode Reading(long entity, string hex) => new JsonObject
    {
        ["Instances"] = new JsonArray(new JsonObject { ["Entity"] = entity, ["Hex"] = hex, ["Slots"] = new JsonArray() }),
    };

    [Fact]
    public void What_moved_names_the_offset_and_reads_it_as_a_float_when_it_looks_like_one()
    {
        var before = Reading(5, "0000a040");   // 5.0f
        var after = Reading(5, "0000c040");    // 6.0f
        var text = Survey.RenderMoved(before, after, "x::C");
        Assert.Contains("+0x000", text, StringComparison.Ordinal);
        Assert.Contains("float 5 -> 6", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_moving_is_reported_as_a_result_with_what_to_check()
    {
        var text = Survey.RenderMoved(Reading(5, "0000a040"), Reading(5, "0000a040"), "x::C");
        Assert.Contains("nothing moved", text, StringComparison.Ordinal);
        Assert.Contains("unpause", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_component_that_came_or_went_is_the_event_and_is_reported()
    {
        var text = Survey.RenderMoved(Reading(5, "00000000"), Reading(9, "00000000"), "x::OneFrame");
        Assert.Contains("appeared: 9", text, StringComparison.Ordinal);
        Assert.Contains("went away: 5", text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_offset_into_the_executable_is_named_against_the_binary_that_is_running()
    {
        // The stand starts bin/bg3_dx11.exe. The symbol database also holds bg3.exe, which is a
        // different build at different addresses, so naming an offset against it would give a
        // plausible name for the wrong function.
        var dumped = new JsonObject
        {
            ["TotalSize"] = 8,
            ["InlineSize"] = 8,
            ["Instances"] = new JsonArray(new JsonObject
            {
                ["Entity"] = 1,
                ["Hex"] = "0011223344556677",
                ["Slots"] = new JsonArray(new JsonObject { ["Kind"] = "module", ["Rva"] = 0x1234, ["Offset"] = 0 }),
            }),
        };
        var text = Survey.Render(dumped, "x::C", raw: false);
        Assert.Contains("bg3_dx11.exe+0x1234", text, StringComparison.Ordinal);
        Assert.DoesNotContain("bg3.exe+0x1234", text, StringComparison.Ordinal);
    }
}
