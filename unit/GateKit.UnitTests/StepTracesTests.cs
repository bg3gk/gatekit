using GateKit.Testing;

namespace GateKit.UnitTests;

/// <summary>`test --trace`: each step's trace has a file of its own, in the order the steps ran.</summary>
public sealed class StepTracesTests
{
    [Theory]
    [InlineData(1, null, "the scene: out of a fight, the weapon away", "01-the-scene-out-of-a-fight-the-weapon-away.json")]
    [InlineData(12, "wpn_dagger", "attack it", "12-wpn-dagger-attack-it.json")]
    [InlineData(3, null, "???", "03-step.json")]
    public void A_steps_trace_file_is_its_number_and_its_name(int n, string? inCase, string step, string file) =>
        Assert.Equal(file, StepTraces.FileName(n, inCase, step));

    [Fact]
    public void A_long_name_is_cut()
    {
        var name = StepTraces.FileName(2, null, new string('a', 200));
        Assert.True(name.Length <= "02-".Length + 60 + ".json".Length, name);
    }
}
