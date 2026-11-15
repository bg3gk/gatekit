using GateKit.Copies;
using GateKit.Testing;

namespace GateKit.UnitTests;

/// <summary>Tests write into the home's scratch, one folder per run, and leave nothing behind:
/// folders of the system temp folder piled up by the thousand, one per run and never removed.</summary>
public sealed class ScratchTests
{
    [Fact]
    public void A_run_writes_under_the_homes_scratch_and_a_gone_runs_folder_is_swept()
    {
        var root = Path.Combine(GateKit.Home.GateKitHome.Current.Scratch.Require(), "unit");
        Assert.Equal(root, Path.GetDirectoryName(Scratch.Run));
        Assert.StartsWith(Environment.ProcessId + "-", Path.GetFileName(Scratch.Run), StringComparison.Ordinal);
        using (var temp = new TempFolder()) Assert.StartsWith(Scratch.Run, temp.Path, StringComparison.Ordinal);

        // a run whose process is gone, and one of a name no run has: both go; this run stays
        var gone = Path.Combine(root, "999999999-20000101000000000");
        Directory.CreateDirectory(Path.Combine(gone, "left", "behind"));
        var stray = Path.Combine(root, "not-a-run");
        Directory.CreateDirectory(stray);
        Assert.True(Scratch.Sweep(root) >= 2);
        Assert.False(Directory.Exists(gone));
        Assert.False(Directory.Exists(stray));
        Assert.True(Directory.Exists(Scratch.Run));
    }

    [Fact]
    public void No_test_writes_into_the_system_temp_folder()
    {
        var tests = Path.Combine(CopySettings.RepositoryRoot()!, "unit");
        var offenders = Directory.EnumerateFiles(tests, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("GetTemp" + "Path(", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(tests, f)).ToList();
        Assert.Empty(offenders);
    }
}
