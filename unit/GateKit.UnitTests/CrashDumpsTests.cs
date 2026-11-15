using GateKit.Copies;

namespace GateKit.UnitTests;

/// <summary>The crash dumps of a copy, as GateKit's Script Extender writes their text.</summary>
public sealed class CrashDumpsTests
{
    [Fact]
    public void The_text_beside_a_dump_gives_its_exception_address_and_thread()
    {
        var dump = CrashDumps.Parse("x/1-20261115-120000.txt",
            "Exception: 0xC0000005\r\nAddress: BG3ScriptExtender.dll+0x131054\r\nAbsolute: 0x7FFE90601054\r\nThread: 24548\r\n", DateTime.UtcNow);
        Assert.Equal("0xC0000005", dump.Exception);
        Assert.Equal("BG3ScriptExtender.dll+0x131054", dump.Address);
        Assert.Equal("0x7FFE90601054", dump.Absolute);
        Assert.Equal("24548", dump.Thread);
        Assert.EndsWith(".dmp", dump.ToJson()["dump"]!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_address_no_database_names_says_which_module_it_is_in_and_where_its_symbols_are()
    {
        using var temp = new TempFolder();
        var missing = temp["build/BG3ScriptExtender.pdb"];
        var extender = CrashDumps.WhyUnnamed("BG3ScriptExtender.dll+0x131054", null, [missing]);
        Assert.Contains("inside Script Extender's own module (BG3ScriptExtender.dll); GateKit keeps no database of it", extender, StringComparison.Ordinal);
        Assert.Contains("no .pdb of this build was found", extender, StringComparison.Ordinal);
        var pdb = temp.Write("build/BG3ScriptExtender.pdb", "pdb");
        Assert.Contains($"its symbols are {pdb}: a debugger opened on the .dmp", CrashDumps.WhyUnnamed("bg3scriptextender.dll+0x10", null, [pdb]), StringComparison.Ordinal);
        // another module: what `decomp at` said
        Assert.Equal("`decomp at ntdll.dll+0x10` named none: no function found at ntdll.dll+0x10",
            CrashDumps.WhyUnnamed("ntdll.dll+0x10", "no function found at ntdll.dll+0x10", []));
        // a dump at the extender's address is said so, and no function is made up for it
        var dump = CrashDumps.Named(CrashDumps.Parse("x/1.txt", "Exception: 0xC0000005\nAddress: BG3ScriptExtender.dll+0x131054\n", DateTime.UtcNow), [pdb]);
        Assert.Null(dump.Function);
        Assert.Contains("no function named: the address is inside Script Extender's own module", dump.Line(), StringComparison.Ordinal);
        Assert.Equal(dump.Unnamed, dump.ToJson()["unnamed"]!.ToString());
    }

    [Fact]
    public void Only_the_dumps_written_after_a_moment_are_read_newest_first()
    {
        using var temp = new TempFolder();
        var old = temp.Write("1-old.txt", "Exception: 0x1\nAddress: a.dll+0x1\n");
        var fresh = temp.Write("2-new.txt", "Exception: 0x2\nAddress: a.dll+0x2\n");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(fresh, DateTime.UtcNow);
        Assert.Equal(["0x2", "0x1"], CrashDumps.Read(temp.Path).Select(d => d.Exception));
        Assert.Equal(["0x2"], CrashDumps.Read(temp.Path, DateTime.UtcNow.AddHours(-1)).Select(d => d.Exception));
        Assert.Empty(CrashDumps.Read(Path.Combine(temp.Path, "none")));
    }
}
