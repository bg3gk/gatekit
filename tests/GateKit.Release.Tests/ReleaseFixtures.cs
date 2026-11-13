using System.Net;
using Microsoft.Data.Sqlite;

namespace GateKit.Release.Tests;

/// <summary>A folder of its own for one test, removed with it.</summary>
public sealed class TempFolder : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("gatekit-release-").FullName;

    public string this[string name] => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { /* a leftover in the temp folder is not a failed test */ }
        catch (UnauthorizedAccessException) { }
    }
}

public static class Fixtures
{
    /// <summary>
    /// A small database shaped like the real ones: a meta table and enough rows to compress. No
    /// game data is involved, so the test runs anywhere.
    /// </summary>
    public static string WriteDatabase(string path, int rows = 2000, string built = "2026-01-02 03:04:05")
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        File.Delete(path);
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
        {
            db.Open();
            using (var create = db.CreateCommand())
            {
                create.CommandText =
                    "CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT);" +
                    "CREATE TABLE lines (id INTEGER PRIMARY KEY, text TEXT);";
                create.ExecuteNonQuery();
            }
            using var transaction = db.BeginTransaction();
            using (var meta = db.CreateCommand())
            {
                meta.CommandText = "INSERT INTO meta VALUES ('built', $built), ('packages', 'Engine, Gustav, Patch8_HotFix10'), " +
                    "('languages', 'English'), ('game', 'C:\\Somewhere\\Baldurs Gate 3'), ('game_file_version', '4.76.31.656')";
                meta.Parameters.AddWithValue("$built", built);
                meta.ExecuteNonQuery();
            }
            using (var insert = db.CreateCommand())
            {
                insert.CommandText = "INSERT INTO lines (text) VALUES ($text)";
                var text = insert.Parameters.Add("$text", SqliteType.Text);
                for (var i = 0; i < rows; i++)
                {
                    text.Value = $"line {i}: the same words again and again so the file compresses";
                    insert.ExecuteNonQuery();
                }
            }
            transaction.Commit();
        }
        SqliteConnection.ClearAllPools();
        return path;
    }

    /// <summary>Everything an exported folder holds, as a release would serve it.</summary>
    public static Dictionary<string, byte[]> AsAssets(string folder) =>
        Directory.GetFiles(folder).ToDictionary(f => System.IO.Path.GetFileName(f)!, File.ReadAllBytes, StringComparer.Ordinal);
}

/// <summary>Serves a fixed set of assets by name, so no test touches the network.</summary>
public sealed class ServeAssets(IReadOnlyDictionary<string, byte[]> assets) : HttpMessageHandler
{
    public List<string> Asked { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        Asked.Add(url);
        var name = url[(url.LastIndexOf('/') + 1)..];
        return Task.FromResult(assets.TryGetValue(name, out var body)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
            : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new ByteArrayContent([]) });
    }
}
