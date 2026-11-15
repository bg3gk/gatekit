using System.CommandLine;
using System.Security.Cryptography;
using System.Text;
using GateKit.Platform;
using GateKit.Symbols.Decomp;

namespace GateKit.Symbols.UnitTests;

/// <summary>What the pseudo-code database records about the binaries it was made from: the
/// project folders' copies hashed and looked up in a reference list written here.</summary>
public sealed class ProvenanceTests : IDisposable
{
    readonly string root = GateKit.Testing.Scratch.NewPath("provenance");
    readonly DecompSettings settings;

    static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    void Binary(string name, byte[] bytes)
    {
        var folder = Path.Combine(root, "work", name);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, name), bytes);
        File.WriteAllText(Path.Combine(folder, "functions.jsonl"),
            "{\"rva\":\"1000\",\"name\":\"FUN_1000\",\"sig\":\"void FUN_1000(void)\",\"size\":4,\"thunk\":false,\"callees\":[],\"code\":\"void FUN_1000(void) {}\"}\n",
            new UTF8Encoding(false));
    }

    public ProvenanceTests()
    {
        var game = Encoding.ASCII.GetBytes("MZ the game of build 777");
        var toy = Encoding.ASCII.GetBytes("MZ a Toolkit library");
        Binary("Game.exe", game);
        Binary("Toy.dll", toy);
        Binary("Mac", Encoding.ASCII.GetBytes("another platform's build"));
        Directory.CreateDirectory(Path.Combine(root, "toolkit"));
        File.WriteAllBytes(Path.Combine(root, "toolkit", "Toy.dll"), toy);
        new ReferenceList
        {
            AppId = "42", BuildId = "777", MadeFrom = "steam", Made = DateTime.UtcNow, Folders = ["bin"],
            Files = [new ReferenceFile("bin/Game.exe", game.Length, Sha(game), null)],
        }.Write(Path.Combine(root, "references", "42-777.json"));
        settings = new DecompSettings(Path.Combine(root, "work"), Path.Combine(root, "tools"), Path.Combine(root, "game"),
            Path.Combine(root, "toolkit"), Path.Combine(root, "symbols.sqlite"), null) { References = Path.Combine(root, "references") };
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(root, true);
    }

    (int Exit, string Out, string Err) Run(params string[] args)
    {
        var output = new StringWriter { NewLine = "\n" };
        var error = new StringWriter { NewLine = "\n" };
        int exit = DecompCommands.Create(() => settings).Parse(args).Invoke(new InvocationConfiguration { Output = output, Error = error });
        return (exit, output.ToString(), error.ToString());
    }

    (Dictionary<string, BinaryStamp> Binaries, Dictionary<string, string> Meta) Read()
    {
        using var db = Sql.OpenReadOnly(settings.DbPath);
        var meta = db.Query("SELECT key, value FROM meta").ToDictionary(r => (string)r[0]!, r => (string)r[1]!);
        return (Provenance.Read(db)!.ToDictionary(s => s.Binary), meta);
    }

    [Fact]
    public void Load_records_each_binary_from_the_copy_its_project_imported()
    {
        var (exit, _, error) = Run("load");
        Assert.True(exit == 0, error);
        var (binaries, meta) = Read();
        var game = binaries["Game.exe"];
        Assert.Equal(("game", "777", "clean"), (game.Kind, game.Build, game.Verdict));
        Assert.Contains("bin/Game.exe of build 777 in 42-777.json", game.Evidence);
        var toy = binaries["Toy.dll"];
        Assert.Equal(("toolkit", null, "toolkit"), (toy.Kind, toy.Build, toy.Verdict));
        Assert.Equal(Sha(Encoding.ASCII.GetBytes("MZ a Toolkit library")), toy.Sha256);
        var mac = binaries["Mac"];
        Assert.Equal(("unknown", null, "unknown"), (mac.Kind, mac.Build, mac.Verdict));
        Assert.Contains("in none of 1 reference list (42-777.json)", mac.Evidence);
        // the database as a whole: its game binaries' build; the Toolkit and the unknown do not count
        Assert.Equal("777", meta[GameFingerprint.BuildKey]);
        Assert.Equal("clean", meta[GameFingerprint.VerdictKey]);
        Assert.Contains("made from: Game.exe: game, build 777, clean", Run("status").Out);
    }

    [Fact]
    public void Stamp_records_a_game_binary_of_unknown_bytes_and_takes_the_build_away()
    {
        Assert.Equal(0, Run("load").Exit);
        File.WriteAllText(Path.Combine(settings.Work, "Game.exe", "Game.exe"), "MZ a build no list holds");
        var (exit, output, error) = Run("stamp");
        Assert.True(exit == 0, error);
        Assert.Contains("game build unknown (its game binaries share none), unknown", output);
        var (binaries, meta) = Read();
        var game = binaries["Game.exe"];
        Assert.Equal(("game", null, "unknown"), (game.Kind, game.Build, game.Verdict));
        Assert.Contains("a file of the game has this name", game.Evidence);
        Assert.False(meta.ContainsKey(GameFingerprint.BuildKey));
        Assert.Equal("unknown", meta[GameFingerprint.VerdictKey]);

        var (missing, _, said) = Run("stamp", "Nope.dll");
        Assert.Equal(1, missing);
        Assert.Contains("Nope.dll is not in", said);
    }

    [Fact]
    public void Stamp_without_a_database_says_how_to_make_one()
    {
        var (exit, _, error) = Run("stamp");
        Assert.Equal(1, exit);
        Assert.Contains("decomp load", error);
    }
}
