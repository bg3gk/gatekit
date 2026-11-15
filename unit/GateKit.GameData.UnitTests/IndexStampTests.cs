using System.Security.Cryptography;
using GateKit.GameData.Index;
using GateKit.GameData.Packages;
using GateKit.Platform;
using Microsoft.Data.Sqlite;

namespace GateKit.GameData.UnitTests;

/// <summary>An index built without a record of its build gets one from its recorded inputs: files
/// written here stand in for packages, a reference list written here for the store's record.</summary>
public sealed class IndexStampTests : IDisposable
{
    private readonly string _root = GateKit.Testing.Scratch.NewPath("stamp");
    private string Game => Path.Combine(_root, "Game");
    private string Data => Path.Combine(Game, "Data");

    private string Pak(string relative, string text)
    {
        var path = Path.Combine(Game, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private static string Sha(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private void List(params string[] relative) => new ReferenceList
    {
        AppId = "42", BuildId = "777", MadeFrom = "steam", Made = DateTime.UtcNow, Folders = ["Data"],
        Files = [.. relative.Select(r => new ReferenceFile(r, new FileInfo(Path.Combine(Game, r)).Length, Sha(Path.Combine(Game, r)), null))],
    }.Write(Path.Combine(_root, "references", "42-777.json"));

    /// <summary>An index holding only the metadata a build writes about its inputs.</summary>
    private string Index(string inputs)
    {
        var path = Path.Combine(_root, "index.sqlite");
        using var db = new SqliteConnection($"Data Source={path};Pooling=False");
        db.Open();
        using var c = db.CreateCommand();
        c.CommandText = "CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT); INSERT INTO meta VALUES ('inputs', $i), ('game', $g)";
        c.Parameters.AddWithValue("$i", inputs);
        c.Parameters.AddWithValue("$g", Game);
        c.ExecuteNonQuery();
        return path;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, true);
    }

    [Fact]
    public void Every_input_found_unchanged_and_listed_makes_a_clean_stamp_of_its_build()
    {
        var gustav = Pak("Data/Gustav.pak", "GUSTAV");
        var english = Pak("Data/Localization/English/English.pak", "ENGLISH");
        var stamp = (string p) => new PackageFile(Path.GetFileNameWithoutExtension(p), p).Stamp();
        var db = Index($"English={stamp(english)}|Gustav={stamp(gustav)}|header=-|languages=English|scenes-only=False|all-keys=False|schema=1");

        List("Data/Gustav.pak");
        var partly = IndexStamp.Examine(db, null, [], BuildEvidence.Load(Path.Combine(_root, "references")), 2);
        Assert.Equal(("777", GameFingerprint.Unverified), (partly.Build, partly.Verdict));
        Assert.Equal(["English unlisted", "Gustav listed"], partly.Inputs.Select(i => $"{i.Name} {i.Outcome}"));
        Assert.Contains("1 of 2 inputs", partly.Evidence);
        Assert.Contains("unlisted: English", partly.Evidence);

        List("Data/Gustav.pak", "Data/Localization/English/English.pak");
        var whole = IndexStamp.Examine(db, null, [], BuildEvidence.Load(Path.Combine(_root, "references")), 2);
        Assert.Equal(("777", "clean"), (whole.Build, whole.Verdict));
        IndexStamp.Write(db, whole);
        using var read = new SqliteConnection($"Data Source={db};Mode=ReadOnly;Pooling=False");
        read.Open();
        using var c = read.CreateCommand();
        c.CommandText = "SELECT group_concat(key || '=' || substr(value, 1, 5), ' ') FROM (SELECT * FROM meta WHERE key LIKE 'game_%' ORDER BY key)";
        Assert.Equal("game_build=777 game_evidence=stamp game_verdict=clean", c.ExecuteScalar());
    }

    [Fact]
    public void A_changed_or_absent_input_establishes_nothing()
    {
        var gustav = Pak("Data/Gustav.pak", "GUSTAV");
        var db = Index($"Gustav=1:1|Chinese=5:5|header=-|languages=English,Chinese|scenes-only=False|all-keys=False|schema=1");
        List("Data/Gustav.pak");
        var result = IndexStamp.Examine(db, null, [], BuildEvidence.Load(Path.Combine(_root, "references")), 1);
        Assert.Null(result.Build);
        Assert.Equal(GameFingerprint.Unverified, result.Verdict);
        Assert.Equal(("absent", "changed"), (result.Inputs[0].Outcome, result.Inputs[1].Outcome));
        Assert.Contains($"recorded 1:1, now {new PackageFile("Gustav", gustav).Stamp()}", result.Inputs[1].Detail);
    }

    [Fact]
    public void An_index_that_records_no_inputs_is_refused()
    {
        var path = Path.Combine(_root, "old.sqlite");
        Directory.CreateDirectory(Data);
        using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            db.Open();
            using var c = db.CreateCommand();
            c.CommandText = "CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT)";
            c.ExecuteNonQuery();
        }
        Assert.Contains("records no inputs", Assert.Throws<InvalidOperationException>(() => IndexStamp.Examine(path, Data, [], BuildEvidence.Load(null), 1)).Message);
    }
}
