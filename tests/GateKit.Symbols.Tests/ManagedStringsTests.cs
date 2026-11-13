using System.CommandLine;
using GateKit.Symbols.Managed;

namespace GateKit.Symbols.Tests;

/// <summary>A type of this test assembly whose literals and members the reader must find.</summary>
public class ManagedProbe
{
    public static string Probe(int count) => count > 0 ? "gatekit-managed-probe" : "other";

    public List<T> Wrap<T>(T item) => [item];

    public sealed class Inner
    {
        public override string ToString() => "inner-literal";
    }
}

/// <summary>The managed string reader on the test assembly itself.</summary>
public class ManagedStringsTests
{
    static readonly string Assembly = typeof(ManagedProbe).Assembly.Location;

    [Fact]
    public void ListsTypes()
    {
        using var reader = new ManagedStrings(Assembly);
        var types = reader.Types("ManagedProbe");
        Assert.Equal(["GateKit.Symbols.Tests.ManagedProbe", "GateKit.Symbols.Tests.ManagedProbe+Inner"], types);
    }

    [Fact]
    public void FindsLiteralsPerMethod()
    {
        using var reader = new ManagedStrings(Assembly);
        var found = reader.Strings("^GateKit.Symbols.Tests.ManagedProbe");
        var probe = Assert.Single(found, m => m.Method == "Probe");
        Assert.Equal("GateKit.Symbols.Tests.ManagedProbe", probe.Type);
        Assert.Contains("gatekit-managed-probe", probe.Strings);
        Assert.Contains("other", probe.Strings);
        Assert.Contains(found, m => m.Type.EndsWith("+Inner", StringComparison.Ordinal) && m.Strings.SequenceEqual(["inner-literal"]));
        Assert.Equal(["gatekit-managed-probe"], reader.Strings("ManagedProbe", "PROBE").Single().Strings);
    }

    [Fact]
    public void ListsMembersWithSignatures()
    {
        using var reader = new ManagedStrings(Assembly);
        var lines = reader.Members("^GateKit.Symbols.Tests.ManagedProbe$");
        Assert.Equal("== GateKit.Symbols.Tests.ManagedProbe : System.Object", lines[0]);
        Assert.Contains("   static String Probe(Int32 count)", lines);
        Assert.Contains("   List`1 Wrap(T item)", lines);
    }

    [Fact]
    public void CommandPrintsTheOldScriptsLines()
    {
        var output = new StringWriter { NewLine = "\n" };
        int exit = SymbolsCommands.CreateManagedStringsCommand().Parse([Assembly, "--type", "ManagedProbe$", "--match", "probe"])
            .Invoke(new InvocationConfiguration { Output = output, Error = new StringWriter() });
        Assert.Equal(0, exit);
        Assert.Equal("GateKit.Symbols.Tests.ManagedProbe::Probe  ->  gatekit-managed-probe\n", output.ToString());
    }
}
