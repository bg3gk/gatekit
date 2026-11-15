using GateKit.Symbols.Registry;

namespace GateKit.Symbols.UnitTests;

/// <summary>`registry reanchor`: what the pairs move by themselves and what is left for a person.</summary>
public sealed class ReanchorTests
{
    static readonly List<BuildRow> Source =
    [
        new("toy::One", 0x1000, 64, "aa"),
        new("toy::Two", 0x2000, 64, "bb"),
        new("toy::Lonely", 0x3000, 64, null),
        new("toy::Twice", 0x4000, 64, null),
        new("toy::Held", 0x5000, 64, null),
        new("toy::Folded", 0x1000, 64, "aa"),
    ];

    static IReadOnlyList<(long Rva, string How)> Partners(long rva) => rva switch
    {
        0x1000 => [(0x1100, "accepted by bag"), (0x1100, "seed by label")],   // one partner, said twice
        0x2000 => [(0x2100, "accepted by calls")],
        0x4000 => [(0x4100, "accepted by bag"), (0x4200, "accepted by calls")],
        0x5000 => [(0x5100, "seed by label")],
        _ => [],
    };

    static (long?, string?) Place(long rva) => rva switch
    {
        0x1100 => (64, "aa"),
        0x2100 => (70, "cc"),
        _ => (64, null),
    };

    [Fact]
    public void A_function_with_one_partner_above_the_bar_moves_with_the_targets_size_and_body()
    {
        var (moved, unmoved) = Reanchor.Plan(Source, Partners, [new BuildRow("toy::Held", 0x5200, null, null)], Place);
        Assert.Equal(["toy::Folded", "toy::One", "toy::Two"], moved.Select(m => m.Id));
        var one = moved.Single(m => m.Id == "toy::One");
        Assert.Equal((0x1100L, 64L, "aa", true), (one.Row.Rva, one.Row.Size!.Value, one.Row.Body, one.BodyAgrees!.Value));
        Assert.False(moved.Single(m => m.Id == "toy::Two").BodyAgrees);            // the pair holds; the differing body is said
        Assert.Equal(0x1100, moved.Single(m => m.Id == "toy::Folded").Row.Rva);    // folded ids move together

        Assert.Equal(
        [
            ("toy::Held", "the target's table holds it at 0x5200, the pair says 0x5100"),
            ("toy::Lonely", "no pair above the measured bar"),
            ("toy::Twice", "paired with 0x4100, 0x4200"),
        ], unmoved.Select(u => (u.Id, u.Why)));
    }

    [Fact]
    public void A_row_the_target_holds_at_the_same_place_is_moved_again_as_it_is()
    {
        var (moved, unmoved) = Reanchor.Plan([new BuildRow("toy::Two", 0x2000, 64, null)], Partners, [new BuildRow("toy::Two", 0x2100, 70, "cc")], Place);
        Assert.Equal(new BuildRow("toy::Two", 0x2100, 70, "cc"), Assert.Single(moved).Row);
        Assert.Null(moved[0].BodyAgrees);                                           // nothing to compare on the source side
        Assert.Empty(unmoved);
    }
}
