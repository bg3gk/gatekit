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

/// <summary>
/// Reads the installed game: builds a scenes-only index into a temporary folder (a few minutes).
/// </summary>
public class GameIntegrationTests
{
    [GameFact]
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
