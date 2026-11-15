using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using GateKit.Json;
using GateKit.Platform;
using GateKit.Copies;
using Xunit;

namespace GateKit.UnitTests;

/// <summary>Writes depot manifests in Steam's format (sections of protobuf), for tests.</summary>
static class ManifestWriter
{
    public sealed record Entry(string Name, byte[]? Content, DepotFileFlags Flags = DepotFileFlags.None, string? Link = null);

    public static byte[] Write(uint depot, ulong manifest, IEnumerable<Entry> entries, bool encrypted = false)
    {
        var payload = new MemoryStream();
        foreach (var e in entries)
        {
            var mapping = new MemoryStream();
            Bytes(mapping, 1, Encoding.UTF8.GetBytes(e.Name));
            Varint(mapping, 2, (ulong)(e.Content?.Length ?? 0));
            Varint(mapping, 3, (ulong)e.Flags);
            Bytes(mapping, 4, new byte[20]);
            Bytes(mapping, 5, e.Content is null ? new byte[20] : SHA1.HashData(e.Content));
            Bytes(mapping, 6, [0x0A, 0x00]); // one empty chunk record, skipped by the reader
            if (e.Link is not null) Bytes(mapping, 7, Encoding.UTF8.GetBytes(e.Link));
            Bytes(payload, 1, mapping.ToArray());
        }
        var metadata = new MemoryStream();
        Varint(metadata, 1, depot);
        Varint(metadata, 2, manifest);
        Varint(metadata, 3, 1_700_000_000);
        Varint(metadata, 4, encrypted ? 1UL : 0UL);
        var file = new MemoryStream();
        Section(file, DepotManifest.PayloadMagic, payload.ToArray());
        Section(file, DepotManifest.MetadataMagic, metadata.ToArray());
        Section(file, DepotManifest.SignatureMagic, []);
        file.Write(BitConverter.GetBytes(DepotManifest.EndMagic));
        return file.ToArray();
    }

    public static byte[] Zip(byte[] manifest)
    {
        var archive = new MemoryStream();
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var stream = zip.CreateEntry("z").Open();
            stream.Write(manifest);
        }
        return archive.ToArray();
    }

    static void Section(Stream s, uint magic, byte[] body)
    {
        s.Write(BitConverter.GetBytes(magic));
        s.Write(BitConverter.GetBytes((uint)body.Length));
        s.Write(body);
    }

    static void Key(Stream s, int field, int wire) => Raw(s, (ulong)(field << 3 | wire));

    static void Varint(Stream s, int field, ulong value)
    {
        Key(s, field, 0);
        Raw(s, value);
    }

    static void Bytes(Stream s, int field, byte[] bytes)
    {
        Key(s, field, 2);
        Raw(s, (ulong)bytes.Length);
        s.Write(bytes);
    }

    static void Raw(Stream s, ulong value)
    {
        while (value >= 0x80)
        {
            s.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        s.WriteByte((byte)value);
    }
}

/// <summary>A Steam library with one app in it, its app manifest and its depot cache, all in a temp folder.</summary>
sealed class FakeSteamGame : IDisposable
{
    public TempFolder Temp { get; } = new();
    public string Steam => Temp["steam"];
    public string Game => Temp["steam/steamapps/common/Game"];
    public string Bin => Path.Combine(Game, "bin");

    public static readonly ManifestWriter.Entry[] Base =
    [
        new("bin", null, DepotFileFlags.Directory),
        new("bin\\game.exe", Encoding.UTF8.GetBytes("the executable"), DepotFileFlags.Executable),
        new("bin\\engine.dll", Encoding.UTF8.GetBytes("the engine")),
        new("Data", null, DepotFileFlags.Directory),
        new("Data\\Main.pak", Encoding.UTF8.GetBytes("main package bytes")),
        new("Data\\Empty.txt", []),
        new("Launcher\\launcher.cfg", Encoding.UTF8.GetBytes("cfg")),
    ];

    public static readonly ManifestWriter.Entry[] Language =
    [
        new("Data\\Localization\\English.pak", Encoding.UTF8.GetBytes("english")),
    ];

    public FakeSteamGame(bool languageManifest = true)
    {
        Temp.Write("steam/steamapps/appmanifest_42.acf", """
            "AppState"
            {
                "appid"     "42"
                "name"      "A Game"
                "installdir"    "Game"
                "buildid"   "777"
                "InstalledDepots"
                {
                    "43"
                    {
                        "manifest"  "9000000000000000001"
                        "size"      "100"
                    }
                    "44"
                    {
                        "manifest"  "12"
                        "size"      "7"
                        "dlcappid"  "50"
                    }
                }
                "SharedDepots"
                {
                    "228989"    "228980"
                }
                "UserConfig"
                {
                    "language"  "english"
                }
            }
            """);
        Directory.CreateDirectory(Temp["steam/depotcache"]);
        File.WriteAllBytes(Temp["steam/depotcache/43_9000000000000000001.manifest"], ManifestWriter.Write(43, 9000000000000000001, Base));
        if (languageManifest)
            File.WriteAllBytes(Temp["steam/depotcache/44_12.manifest"], ManifestWriter.Zip(ManifestWriter.Write(44, 12, Language)));
        foreach (var entry in Base.Concat(Language))
        {
            var path = Path.Combine(Game, entry.Name.Replace('\\', Path.DirectorySeparatorChar));
            if (entry.Content is null) Directory.CreateDirectory(path);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, entry.Content);
            }
        }
    }

    public VerifyReport Verify(VerifyMode mode = VerifyMode.Full, IReadOnlyList<string>? held = null, bool collect = false) => GameVerifier.Run(new VerifyRequest
    {
        Folder = Game, Install = Game, SteamRoot = Steam, BinFolder = Bin, Mode = mode, Held = held,
        HeldReason = held is null ? null : "a test copy", Threads = 2, CollectHashes = collect, References = Temp["references"],
    });

    public void Dispose() => Temp.Dispose();
}

public class GameVerifyTests
{
    [Fact]
    public void A_manifest_is_read_from_its_sections_plain_or_zipped()
    {
        var bytes = ManifestWriter.Write(43, 9000000000000000001, FakeSteamGame.Base);
        foreach (var form in new[] { bytes, ManifestWriter.Zip(bytes) })
        {
            var manifest = DepotManifest.Parse(form, "test");
            Assert.Equal(43u, manifest.DepotId);
            Assert.Equal(9000000000000000001UL, manifest.ManifestId);
            Assert.False(manifest.FilenamesEncrypted);
            Assert.Equal(new DateTime(2023, 11, 14, 22, 13, 20, DateTimeKind.Utc), manifest.Created);
            var exe = Assert.Single(manifest.Files, f => f.Name == "bin/game.exe");
            Assert.Equal(14UL, exe.Size);
            Assert.Equal(DepotFileFlags.Executable, exe.Flags);
            Assert.Equal(SHA1.HashData(Encoding.UTF8.GetBytes("the executable")), exe.Sha1);
            Assert.True(Assert.Single(manifest.Files, f => f.Name == "bin").IsDirectory);
        }
    }

    [Fact]
    public void A_file_that_is_no_manifest_is_refused_with_the_place()
    {
        var bytes = ManifestWriter.Write(1, 2, FakeSteamGame.Base);
        var unknown = (byte[])bytes.Clone();
        unknown[0] ^= 0xFF;
        Assert.Contains("unknown section magic", Assert.Throws<InvalidDataException>(() => DepotManifest.Parse(unknown, "x")).Message);
        Assert.Contains("says", Assert.Throws<InvalidDataException>(() => DepotManifest.Parse(bytes[..20], "x")).Message);
        Assert.Contains("binary manifest format", Assert.Throws<InvalidDataException>(() => DepotManifest.Parse(BitConverter.GetBytes(DepotManifest.BinaryV4Magic), "x")).Message);
    }

    [Fact]
    public void The_app_manifest_names_the_build_the_depots_and_the_shared_ones()
    {
        using var steam = new FakeSteamGame();
        var app = SteamAppState.ForFolder(steam.Game, StringComparison.OrdinalIgnoreCase)!;
        Assert.Equal("42", app.AppId);
        Assert.Equal("777", app.BuildId);
        Assert.Equal("english", app.Language);
        Assert.Equal([new SteamDepot("43", "9000000000000000001", 100, null), new SteamDepot("44", "12", 7, "50")], app.Depots);
        Assert.Equal(new SteamSharedDepot("228989", "228980"), Assert.Single(app.SharedDepots));
        Assert.Null(SteamAppState.ForFolder(steam.Bin, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_folder_as_Steam_delivered_it_is_clean()
    {
        using var steam = new FakeSteamGame();
        var report = steam.Verify();
        Assert.Equal("clean", report.Verdict);
        Assert.Equal(0, report.ExitCode);
        Assert.Equal(8, report.Listed);
        Assert.Equal(5, report.Hashed); // every file but the empty one
        Assert.Equal("steam", report.Source);
        Assert.All(report.Invariants!.Parts, d => Assert.Equal("read", d.Status));
        Assert.True(Assert.Single(report.Considered).Used);
        Assert.Contains(report.Notes, n => n.Contains("228989"));
        Assert.Equal(["bin/engine.dll", "bin/game.exe"], report.KeyFiles.Select(f => f.Path));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("the engine"))), report.KeyFiles[0].Sha256);
        Assert.Empty(SchemaSet.Embedded.Validate(report.ToJson(), VerifyReport.Kind));
    }

    [Fact]
    public void Modified_missing_and_extra_files_are_each_listed_and_benign_ones_named_by_their_rule()
    {
        using var steam = new FakeSteamGame();
        File.WriteAllText(Path.Combine(steam.Game, "Data", "Main.pak"), "MAIN package bytes");    // same size, other bytes
        File.WriteAllText(Path.Combine(steam.Bin, "engine.dll"), "a longer engine");               // other size
        File.Delete(Path.Combine(steam.Game, "Launcher", "launcher.cfg"));
        Directory.Delete(Path.Combine(steam.Game, "Data", "Localization"), recursive: true);
        steam.Temp.Write("steam/steamapps/common/Game/Data/Mods/Loose/meta.lsx", "loose");
        steam.Temp.Write("steam/steamapps/common/Game/bin/gold.2026-01-01.log", "log");
        steam.Temp.Write("steam/steamapps/common/Game/bin/DWrite.dll", "loader");
        steam.Temp.Write("steam/steamapps/common/Game/bin/steam_appid.txt", "42");

        var report = steam.Verify();
        Assert.Equal("dirty", report.Verdict);
        Assert.Equal(1, report.ExitCode);
        Assert.Equal([("Data/Main.pak", "content"), ("bin/engine.dll", "size")], report.Modified.Select(m => (m.Path, m.Why)));
        Assert.Equal(15, report.Modified[1].Size);
        Assert.Equal(10, report.Modified[1].ExpectedSize);
        Assert.Equal(["Data/Localization/English.pak", "Launcher/launcher.cfg"], report.Missing.Select(m => m.Path));
        Assert.Equal("44", report.Missing[0].Part);
        Assert.Equal("Data/Mods/Loose/meta.lsx", Assert.Single(report.Extras).Path);
        Assert.Equal([("bin/DWrite.dll", "script-extender-loader"), ("bin/gold.2026-01-01.log", "game-logs"), ("bin/steam_appid.txt", "steam-appid")],
            report.Benign.Select(b => (b.Path, b.Rule!)));
        Assert.Contains("bin/DWrite.dll", report.KeyFiles.Select(f => f.Path));
        var json = report.ToJson();
        Assert.Empty(SchemaSet.Embedded.Validate(json, VerifyReport.Kind));
        Assert.Contains(report.Lines(0), l => l.Contains("script-extender-loader (1)"));
    }

    [Fact]
    public void Only_benign_extras_keep_a_game_clean_and_any_other_makes_it_extras()
    {
        using var steam = new FakeSteamGame();
        steam.Temp.Write("steam/steamapps/common/Game/bin/ScriptExtenderSettings.json", "{}");
        Assert.Equal("clean", steam.Verify().Verdict);
        steam.Temp.Write("steam/steamapps/common/Game/bin/dxdiag.xml", "<x/>");
        var report = steam.Verify();
        Assert.Equal("extras", report.Verdict);
        Assert.Equal(0, report.ExitCode);
    }

    [Fact]
    public void Quick_compares_sizes_and_hashes_only_executables_and_libraries()
    {
        using var steam = new FakeSteamGame();
        File.WriteAllText(Path.Combine(steam.Game, "Data", "Main.pak"), "MAIN package bytes");
        Assert.Equal("clean", steam.Verify(VerifyMode.Quick).Verdict);
        Assert.Equal(2, steam.Verify(VerifyMode.Quick).Hashed);
        File.WriteAllText(Path.Combine(steam.Bin, "game.exe"), "THE executable");
        var report = steam.Verify(VerifyMode.Quick);
        Assert.Equal("bin/game.exe", Assert.Single(report.Modified).Path);
        Assert.Equal("quick", report.Mode);
    }

    [Fact]
    public void A_depot_whose_manifest_is_not_cached_makes_the_verdict_incomplete_and_says_so()
    {
        using var steam = new FakeSteamGame(languageManifest: false);
        var report = steam.Verify();
        Assert.Equal("incomplete", report.Verdict);
        Assert.Equal(2, report.ExitCode);
        var absent = Assert.Single(report.Invariants!.Parts, d => d.Status == "absent");
        Assert.Equal("44", absent.Id);
        Assert.Contains("44_12.manifest", absent.Detail);
        Assert.Equal("Data/Localization/English.pak", Assert.Single(report.Extras).Path); // held to nothing
    }

    [Fact]
    public void A_folder_outside_Steam_has_no_invariants_and_still_a_fingerprint()
    {
        using var temp = new TempFolder();
        temp.Write("game/bin/game.exe", "exe");
        temp.Write("game/Data/Main.pak", "pak");
        temp.Write("game/goggame-1456460669.info", "{}");
        var report = GameVerifier.Run(new VerifyRequest { Folder = temp["game"], Install = temp["game"], BinFolder = temp["game/bin"] });
        Assert.Equal("no-invariants", report.Verdict);
        Assert.Equal(2, report.ExitCode);
        Assert.Equal(["steam", "reference", "gog"], report.Considered.Select(c => c.Id));
        Assert.All(report.Considered, c => Assert.False(c.Used));
        Assert.Contains("not implemented", report.Considered[2].Why);
        Assert.Contains(report.Lines(0), l => l.Contains("no invariants for this install"));
        Assert.Equal("bin/game.exe", Assert.Single(report.KeyFiles).Path);
        Assert.Equal("none", report.Fingerprint().Source);
        Assert.Empty(SchemaSet.Embedded.Validate(report.ToJson(), VerifyReport.Kind));
    }

    [Fact]
    public void Folder_links_are_followed_and_one_whose_target_is_gone_is_an_extra()
    {
        using var steam = new FakeSteamGame();
        steam.Temp.Write("elsewhere/linked/file.txt", "linked");
        steam.Temp.Write("gone/x.txt", "x");
        GamePlatform.Current.LinkFolder(steam.Temp["elsewhere/linked"], Path.Combine(steam.Game, "Data", "Linked"));
        GamePlatform.Current.LinkFolder(steam.Temp["gone"], Path.Combine(steam.Game, "Data", "Dangling"));
        Directory.Delete(steam.Temp["gone"], recursive: true);
        var report = steam.Verify();
        Assert.Equal(["Data/Dangling", "Data/Linked/file.txt"], report.Extras.Select(e => e.Path));
        Assert.Contains(report.Notes, n => n.Contains("Data/Dangling is a folder link whose target is gone"));
    }

    [Fact]
    public void A_copy_is_held_only_to_what_it_holds()
    {
        using var steam = new FakeSteamGame();
        Directory.Delete(Path.Combine(steam.Game, "Launcher"), recursive: true);
        var report = steam.Verify(held: ["bin", "Data"]);
        Assert.Equal("clean", report.Verdict);
        Assert.Equal(1, report.Outside);
        Assert.Equal(["Launcher"], report.OutsideFolders);
        Assert.Empty(SchemaSet.Embedded.Validate(report.ToJson(), VerifyReport.Kind));
    }

    [Fact]
    public void A_fingerprint_is_written_read_back_and_tells_when_the_files_changed()
    {
        using var steam = new FakeSteamGame();
        var report = steam.Verify();
        var path = steam.Temp["state/" + GameFingerprint.FileName];
        report.Fingerprint().Write(path);
        Assert.Empty(SchemaSet.Embedded.Validate(JsonText.Read(path), GameFingerprint.Kind));
        var print = GameFingerprint.Read(path)!;
        Assert.Equal("777", print.BuildId);
        Assert.Equal("clean (full)", print.VerdictText);
        Assert.True(print.StillMatches(steam.Bin));
        Assert.Equal(("777", "clean (full)"), GameFingerprint.Describe(steam.Game, steam.Bin, steam.Temp["state"], StringComparison.OrdinalIgnoreCase));

        File.WriteAllText(Path.Combine(steam.Bin, "engine.dll"), "a longer engine");
        Assert.False(print.StillMatches(steam.Bin));
        Assert.Equal(("777", GameFingerprint.Unverified), GameFingerprint.Describe(steam.Game, steam.Bin, steam.Temp["state"], StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_reference_list_written_from_a_clean_Steam_install_verifies_an_install_outside_Steam()
    {
        using var steam = new FakeSteamGame();
        steam.Temp.Write("steam/steamapps/common/Game/bin/DWrite.dll", "loader");
        var proof = steam.Verify(collect: true);
        Assert.Equal("clean", proof.Verdict);
        var list = ReferenceLists.From(proof);
        Assert.Equal("42-777.json", list.FileName);
        Assert.Equal(["43:9000000000000000001", "44:12"], list.MadeFromParts);
        Assert.DoesNotContain(list.Files, f => f.Path == "bin/DWrite.dll");                         // only what the source lists
        Assert.Null(Assert.Single(list.Files, f => f.Path == "Data/Empty.txt").Sha256);
        var path = steam.Temp["references/" + list.FileName];
        list.Write(path);
        Assert.Empty(SchemaSet.Embedded.Validate(JsonText.Read(path), ReferenceList.Kind));

        // The same files in a folder no store describes: held to the list its executables match.
        FileTree.CopyTree(steam.Game, steam.Temp["other/Game"]);
        File.WriteAllText(steam.Temp["other/Game/Data/Main.pak"], "MAIN package bytes");
        var request = new VerifyRequest
        {
            Folder = steam.Temp["other/Game"], Install = steam.Temp["other/Game"], BinFolder = steam.Temp["other/Game/bin"],
            References = steam.Temp["references"], Threads = 2,
        };
        var report = GameVerifier.Run(request);
        Assert.Equal("reference", report.Source);
        Assert.Equal("777", report.Invariants!.BuildId);
        var changed = Assert.Single(report.Modified);
        Assert.Equal(("Data/Main.pak", "content", "42-777.json"), (changed.Path, changed.Why, changed.Part));
        Assert.Equal("bin/DWrite.dll", Assert.Single(report.Benign).Path);
        Assert.Empty(SchemaSet.Embedded.Validate(report.ToJson(), VerifyReport.Kind));
        Assert.Equal("42-777.json", report.Fingerprint().Reference);

        // Other executables: no list matches, so no verdict.
        File.WriteAllText(steam.Temp["other/Game/bin/game.exe"], "a store's own executable");
        var other = GameVerifier.Run(request);
        Assert.Equal("no-invariants", other.Verdict);
        Assert.Contains("none of the 1 reference lists", other.Considered[1].Why);
        // Named outright, it is used anyway and says what differs.
        Assert.Contains(GameVerifier.Run(request with { Reference = path }).Modified, f => f.Path == "bin/game.exe");
    }

    [Fact]
    public void A_reference_list_is_refused_from_anything_but_a_full_proof_of_a_whole_install()
    {
        using var steam = new FakeSteamGame();
        Assert.Contains("full pass", Assert.Throws<CopyException>(() => ReferenceLists.From(steam.Verify(VerifyMode.Quick, collect: true))).Message);
        Assert.Contains("a copy", Assert.Throws<CopyException>(() => ReferenceLists.From(steam.Verify(held: ["bin", "Data"], collect: true))).Message);
        File.WriteAllText(Path.Combine(steam.Bin, "game.exe"), "THE executable");
        Assert.Contains("dirty", Assert.Throws<CopyException>(() => ReferenceLists.From(steam.Verify(collect: true))).Message);
    }

    [Fact]
    public void A_copy_gets_a_fingerprint_when_made_and_doctor_says_it_is_not_verified()
    {
        using var game = new FakeGame();
        var copy = game.GameCopy("one");
        copy.CreateInstance();
        var print = GameFingerprint.Read(GameIntegrity.FingerprintPath(copy.Layout.State))!;
        Assert.Equal("one", print.Copy);
        Assert.Equal(GameFingerprint.Unverified, print.Verdict);
        Assert.Equal("none", print.Source);
        Assert.Equal(["bin/bg3_dx11.exe"], print.Files.Select(f => f.Path)); // the copy's bin, without the extender build
        var check = GameFilesReport.Game(copy.Layout, game.Platform);
        Assert.Equal(Doctor.Verdict.Warn, check.Verdict);
        Assert.Contains("copy one: not verified", check.Detail);

        var report = GameIntegrity.Verify(copy.Layout, GameIntegrity.Request(copy.Layout, game.Platform, VerifyMode.Quick));
        Assert.Equal("no-invariants", report.Verdict);
        Assert.Equal(report.FingerprintPath, GameIntegrity.FingerprintPath(copy.Layout.State));
        Assert.Contains("no-invariants (quick)", GameFilesReport.Game(copy.Layout, game.Platform).Detail);
    }

    [Fact]
    public void Doctor_says_which_build_a_database_was_made_from()
    {
        using var game = new FakeGame();
        var layout = game.GameCopy().Layout;
        string Db(string name, string sql)
        {
            var path = game.Temp[name];
            using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
            {
                db.Open();
                using var c = db.CreateCommand();
                c.CommandText = "CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT);" + sql;
                c.ExecuteNonQuery();
            }
            return path;
        }
        Doctor.Check? Check(string path) => GameFilesReport.Database("x", path, layout, game.Platform, "REMEDY");

        Assert.Null(Check(game.Temp["absent.sqlite"]));
        var none = Check(Db("none.sqlite", ""))!;
        Assert.Equal((Doctor.Verdict.Warn, "REMEDY"), (none.Verdict, none.Fix));
        Assert.Contains("records none", none.Detail);

        // The fake game is no Steam install: it has no build, so a database of build 777 is from another.
        var other = Check(Db("other.sqlite", """
            INSERT INTO meta VALUES ('game_build', '777'), ('game_verdict', 'clean');
            CREATE TABLE binary_meta (binary TEXT, sha256 TEXT, size INTEGER, kind TEXT, game_build TEXT, verdict TEXT, evidence TEXT, stamped TEXT);
            INSERT INTO binary_meta VALUES ('Game.exe', 'ab', 1, 'game', '777', 'clean', '', ''), ('Toy.dll', 'cd', 1, 'toolkit', NULL, 'toolkit', '', '');
            """))!;
        Assert.Equal(Doctor.Verdict.Warn, other.Verdict);
        Assert.Equal("made from build 777; the game is build none (Game.exe build 777, clean)", other.Detail);

        // Extras leave every listed file as delivered: the database is in order.
        var same = Check(Db("same.sqlite", "INSERT INTO meta VALUES ('game_verdict', 'extras (full)');"))!;
        Assert.Equal((Doctor.Verdict.Good, "build none, extras (full) when it was read"), (same.Verdict, same.Detail));
        var dirty = Check(Db("dirty.sqlite", "INSERT INTO meta VALUES ('game_verdict', 'dirty (full)');"))!;
        Assert.Equal(Doctor.Verdict.Warn, dirty.Verdict);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Theory]
    [InlineData("bin/*.log", "bin/gold.1.log", true)]
    [InlineData("bin/*.log", "bin/sub/gold.log", false)]
    [InlineData("**/steam_appid.txt", "steam_appid.txt", true)]
    [InlineData("**/steam_appid.txt", "bin/x/steam_appid.txt", true)]
    [InlineData("bin/DWrite.dll", "BIN/dwrite.DLL", true)]
    [InlineData("bin/DWrite.dll", "bin/DWrite.dll.bak", false)]
    public void Rule_patterns_match_segments(string pattern, string path, bool matches) =>
        Assert.Equal(matches, GameVerifier.Matches(pattern, path, StringComparison.OrdinalIgnoreCase));
}
