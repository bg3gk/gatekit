using GateKit.Symbols.Build;

namespace GateKit.Symbols.Tests;

/// <summary>Runs only when BG3_TOOLKIT names an installed Toolkit folder (the binaries are never in the repository).</summary>
public sealed class ToolkitFactAttribute : FactAttribute
{
    public ToolkitFactAttribute()
    {
        var folder = Environment.GetEnvironmentVariable("BG3_TOOLKIT");
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            Skip = "set BG3_TOOLKIT to the installed Toolkit folder to run this test";
    }
}

public sealed class ToolkitIntegrationTests
{
    [ToolkitFact]
    public void ExportsOfTheInstalledToolkitAreUndecorated()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gk-symbols-it-" + Guid.NewGuid().ToString("N"));
        try
        {
            var db = Path.Combine(dir, "symbols.sqlite");
            SymbolBuilder.Run(new BuildOptions
            {
                Db = db, Toolkit = Environment.GetEnvironmentVariable("BG3_TOOLKIT")!, Game = Path.Combine(dir, "no-game"),
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
