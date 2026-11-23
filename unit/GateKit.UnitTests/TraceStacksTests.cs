using GateKit.Cli;

namespace GateKit.UnitTests;

/// <summary>`trace-stacks`: a load trace's sampled stacks summed per stage and thread, with the share
/// of named groups of functions counted once per sample.</summary>
public sealed class TraceStacksTests
{
    static readonly List<TraceStacksCommand.Row> Rows =
    [
        new(38, "LoadLevel", 10, "7", ["bg3_dx11.exe+0x10", "bg3_dx11.exe+0x20", "bg3_dx11.exe+0x30"]),
        new(38, "LoadLevel", 5, "7", ["bg3_dx11.exe+0x11", "bg3_dx11.exe+0x10", "bg3_dx11.exe+0x30"]),
        new(38, "LoadLevel", 5, "8", ["bg3_dx11.exe+0x10"]),
        new(39, "SwapLevel", 100, "7", ["bg3_dx11.exe+0x10"]),
    ];

    [Fact]
    public void A_group_counts_a_sample_once_whatever_how_many_of_its_functions_are_on_the_stack()
    {
        var lines = TraceStacksCommand.Sum(Rows, [38], ["7"], [], [("gc", ["0x10", "0x11"])], 5, null).ToList();
        Assert.StartsWith("15 samples in 2 distinct stacks", lines[0]);
        // 15 samples have 0x10 or 0x11 on the stack; 15 have one of them at the top
        Assert.Contains(lines, l => l.Contains("15 100.0%") && l.Contains("(leaf 15, 100.0%)") && l.EndsWith("gc"));
    }

    [Fact]
    public void Inclusive_counts_each_function_once_per_stack_and_leaf_only_the_top()
    {
        var lines = TraceStacksCommand.Sum(Rows, [38], [], [], [], 5, null).ToList();
        var inclusive = lines.IndexOf("inclusive:");
        var leaf = lines.IndexOf("leaf:");
        Assert.Contains(lines.Skip(inclusive).Take(leaf - inclusive), l => l.Contains("20 100.0%") && l.EndsWith("bg3_dx11.exe+0x10"));
        Assert.Contains(lines.Skip(leaf), l => l.Contains("15  75.0%") && l.EndsWith("bg3_dx11.exe+0x10"));
        Assert.Contains(lines.Skip(leaf), l => l.Contains("5  25.0%") && l.EndsWith("bg3_dx11.exe+0x11"));
    }

    [Fact]
    public void Within_keeps_only_the_stacks_through_a_function_and_a_bare_rva_matches_any_module()
    {
        var lines = TraceStacksCommand.Sum(Rows, [], [], ["0x20"], [], 5, null).ToList();
        Assert.StartsWith("10 samples in 1 distinct stacks", lines[0]);
        Assert.True(TraceStacksCommand.Matches("bg3_dx11.exe+0x4D7A580", "0x4d7a580"));
        Assert.False(TraceStacksCommand.Matches("bg3_dx11.exe+0x4D7A580", "bg3.exe+0x4D7A580"));
    }
}
