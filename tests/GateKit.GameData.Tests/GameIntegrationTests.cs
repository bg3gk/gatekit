using GateKit.GameData.Index;
using GateKit.GameData.Query;

namespace GateKit.GameData.Tests;

/// <summary>
/// Reads the installed game. Runs only when BG3_GAME names the game folder; otherwise it passes
/// without doing anything. Builds a scenes-only index into a temporary folder (a few minutes).
/// </summary>
public class GameIntegrationTests
{
    [Fact]
    public void ScenesOnlyIndexOfTheInstalledGame()
    {
        var data = DataPaths.GameData(null, null);
        if (data == null || !File.Exists(Path.Combine(data, "Gustav.pak")))
        {
            return;                                    // no game here: nothing to check
        }
        var folder = Path.Combine(Path.GetTempPath(), "gatekit-game-index-" + Guid.NewGuid().ToString("N"));
        var database = Path.Combine(folder, "gameindex.sqlite");
        try
        {
            var result = IndexBuilder.Build(new IndexBuildOptions { DataFolder = data, Database = database, ScenesOnly = true, Force = true });
            Assert.False(result.UpToDate);
            using var db = ReadOnlyDb.Open(database, "missing");
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
