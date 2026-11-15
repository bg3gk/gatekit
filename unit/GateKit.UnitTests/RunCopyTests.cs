using GateKit.Platform;

namespace GateKit.UnitTests;

/// <summary>Which command a command line runs, past the global options in front of it.</summary>
public sealed class RunCopyTests
{
    [Fact]
    public void The_command_is_found_past_the_instance_option()
    {
        // Runners write `gatekit --instance <copy> mcp call ...`; a check on the first word kept
        // every such call in bin, holding the build output while the games ran.
        Assert.Equal("mcp", RunCopy.Verb(["--instance", "world", "mcp", "call", "bg3_up"]));
        Assert.Equal("test", RunCopy.Verb(["--instance=world", "test", "tests/_coverage", "--attach"]));
        Assert.Equal("mcp", RunCopy.Verb(["mcp", "--instance", "world"]));
        Assert.Equal(2, RunCopy.VerbAt(["--instance", "deck", "deck", "close"])); // an instance may be named like a command
        Assert.Null(RunCopy.Verb(["--instance", "world"]));
        Assert.Null(RunCopy.Verb(["--help"]));
        Assert.Null(RunCopy.Verb([]));
    }

    [Fact]
    public void Each_checkout_and_each_build_gets_a_copy_of_its_own()
    {
        var root = GateKit.Testing.Scratch.NewPath("runcopy");
        try
        {
            var a = Path.Combine(root, "a", "bin");
            var b = Path.Combine(root, "b", "bin");
            foreach (var folder in new[] { a, b })
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "x.dll"), "one");
                File.SetLastWriteTimeUtc(Path.Combine(folder, "x.dll"), new DateTime(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc));
            }
            var first = RunCopy.CopyName("mcp", a);
            Assert.StartsWith("mcp-", first);
            Assert.Equal(first, RunCopy.CopyName("mcp", a));
            Assert.NotEqual(first, RunCopy.CopyName("mcp", b));       // another checkout
            File.SetLastWriteTimeUtc(Path.Combine(a, "x.dll"), new DateTime(2026, 11, 14, 1, 0, 0, DateTimeKind.Utc));
            Assert.NotEqual(first, RunCopy.CopyName("mcp", a));       // another build

            var to = Path.Combine(root, "run", first);
            RunCopy.Place(a, to);
            Assert.Equal("one", File.ReadAllText(Path.Combine(to, "x.dll")));
            File.WriteAllText(Path.Combine(a, "x.dll"), "two");
            RunCopy.Place(a, to);                                       // a placed copy is never written over
            Assert.Equal("one", File.ReadAllText(Path.Combine(to, "x.dll")));
            Assert.Empty(Directory.EnumerateDirectories(Path.Combine(root, "run"), "*.tmp"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
