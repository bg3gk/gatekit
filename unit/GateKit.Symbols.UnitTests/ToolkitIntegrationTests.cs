using GateKit.Symbols.Build;

namespace GateKit.Symbols.UnitTests;

/// <summary>Runs when the Toolkit is installed (the binaries are never in the repository): found the
/// way every command finds it - BG3_TOOLKIT, "toolkit" in gatekit.json, then the Steam libraries.
/// Skips only when it is really not there, and says where it looked.</summary>
public sealed class ToolkitFactAttribute : FactAttribute
{
    public ToolkitFactAttribute()
    {
        if (SymbolPaths.FindToolkit() is null)
            Skip = "no Toolkit found; looked at " + SymbolPaths.ToolkitSearch;
    }
}

public sealed class ToolkitIntegrationTests
{
    [ToolkitFact]
    public void ExportsOfTheInstalledToolkitAreUndecorated()
    {
        var dir = GateKit.Testing.Scratch.NewPath("symbols-it");
        try
        {
            var db = Path.Combine(dir, "symbols.sqlite");
            SymbolBuilder.Run(new BuildOptions
            {
                Db = db, Toolkit = SymbolPaths.FindToolkit()!, Game = Path.Combine(dir, "no-game"),
                Bg3Se = null, ExportsOnly = true, NoPseudoCode = true,
            }, TextWriter.Null, TextWriter.Null);
            using var c = Sql.OpenReadOnly(db);
            Assert.True((long)c.Scalar("SELECT count(*) FROM symbols")! > 1000);
            Assert.True((long)c.Scalar("SELECT count(*) FROM symbols WHERE kind = 'method' AND class <> ''")! > 1000);
            // undecodable names are the compiler's hashed long names only
            Assert.Equal(0L, c.Scalar("SELECT count(*) FROM symbols WHERE kind = 'undecoded' AND mangled NOT LIKE '??@%'"));
            Assert.True((long)c.Scalar("SELECT count(*) FROM dlls WHERE managed = 1")! > 0);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
