using GateKit.GameData.Index;
using GateKit.GameData.Query;

namespace GateKit.GameData.UnitTests;

/// <summary>Runs when the game is installed: found the way every command finds it - BG3_GAME,
/// "game" in gatekit.json, then the Steam libraries. Skips only when it is really not there, and
/// says where it looked.</summary>
public sealed class GameFactAttribute : FactAttribute
{
    public GameFactAttribute()
    {
        if (DataPaths.GameData(null, null) is not { } data || !File.Exists(Path.Combine(data, "Gustav.pak")))
            Skip = "no installed game found; looked at " + DataPaths.GameSearch;
    }
}

/// <summary>A test of the installed game that takes minutes: run only when GATEKIT_SLOW_TESTS is 1, and
/// then only where the game is installed. Its trait (Speed=slow) selects the slow tests alone:
/// <c>GATEKIT_SLOW_TESTS=1 dotnet test --filter Speed=slow</c>.</summary>
public sealed class SlowGameFactAttribute : FactAttribute
{
    public const string Variable = "GATEKIT_SLOW_TESTS";

    public SlowGameFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) != "1")
            Skip = $"a slow test, run when {Variable}=1 (dotnet test --filter Speed=slow)";
        else if (new GameFactAttribute().Skip is { } missing)
            Skip = missing;
    }
}

/// <summary>
/// Reads the installed game: builds a scenes-only index into a temporary folder (a few minutes).
/// </summary>
public class GameIntegrationTests
{
    [SlowGameFact, Trait("Speed", "slow")]
    public void ScenesOnlyIndexOfTheInstalledGame()
    {
        var data = DataPaths.GameData(null, null)!;
        var folder = GateKit.Testing.Scratch.NewPath("game-index");
        var database = Path.Combine(folder, "gameindex.sqlite");
        try
        {
            var result = IndexBuilder.Build(new IndexBuildOptions { DataFolder = data, Database = database, ScenesOnly = true, Force = true });
            Assert.False(result.UpToDate);
            using var db = ReadOnlyDb.Open(database);
            long Count(string table) => Convert.ToInt64(db.Scalar($"SELECT count(*) FROM {table}"));
            Assert.True(Count("timelines") > 9000);
            Assert.True(Count("dialogs") > 9000);
            Assert.True(Count("templates") > 10000);
            Assert.True(Count("files") > 500000);
            Assert.True(Count("loca") > 100000);
            Assert.Equal(0, Count("problems"));
            Assert.True(Convert.ToInt64(db.Scalar("SELECT count(*) FROM v_tracks WHERE type = 'TLShowWeapon' AND hides > 0")) > 100);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }
}
