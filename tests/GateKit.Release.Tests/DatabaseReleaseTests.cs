using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace GateKit.Release.Tests;

public sealed class DatabaseReleaseTests : IDisposable
{
    readonly TempFolder _temp = new();
    readonly StringWriter _log = new();

    public void Dispose() => _temp.Dispose();

    static readonly DatabaseKind Kind = DatabaseKind.Index;

    DatabaseAsset ExportSmall(string assets, long partBytes = long.MaxValue / 2, int rows = 2000)
    {
        var database = Fixtures.WriteDatabase(_temp["built/gameindex.sqlite"], rows);
        return DatabaseExport.Export(Kind, database, assets, new ExportOptions { PartBytes = partBytes, Level = 1 }, _log);
    }

    [Fact]
    public void A_manifest_survives_being_written_and_read()
    {
        var asset = ExportSmall(_temp["assets"]);
        var file = Path.Combine(_temp["assets"], DatabaseManifest.FileName);

        var read = DatabaseManifest.Read(file).Find("index");

        Assert.NotNull(read);
        Assert.Equal(asset.FileName, read.FileName);
        Assert.Equal(asset.Size, read.Size);
        Assert.Equal(asset.Sha256, read.Sha256);
        Assert.Equal(asset.CompressedSize, read.CompressedSize);
        Assert.Equal(asset.SchemaVersion, read.SchemaVersion);
        Assert.Equal(asset.Created.ToUnixTimeSeconds(), read.Created.ToUnixTimeSeconds());
        Assert.Equal(asset.Parts.Select(p => (p.Name, p.Size, p.Sha256)), read.Parts.Select(p => (p.Name, p.Size, p.Sha256)));
        Assert.Equal("zstd", read.Compression);
    }

    [Fact]
    public void An_export_says_what_it_was_made_from_and_keeps_no_machine_path()
    {
        var asset = ExportSmall(_temp["assets"]);

        Assert.Contains("Patch8_HotFix10", asset.Source);
        Assert.Equal("2026-01-02 03:04:05", asset.Meta["built"]);
        Assert.False(asset.Meta.ContainsKey("game"), "the folder the database was built from names a machine");
        Assert.All(asset.Meta.Values, v => Assert.DoesNotContain(":\\", v, StringComparison.Ordinal));
        Assert.Equal(16, asset.SchemaVersion.Length);
    }

    [Fact]
    public void A_second_export_replaces_only_its_own_entry()
    {
        var assets = _temp["assets"];
        ExportSmall(assets);
        var other = Fixtures.WriteDatabase(_temp["built/symbols.sqlite"], 100);
        DatabaseExport.Export(DatabaseKind.Symbols, other, assets, new ExportOptions { Level = 1 }, _log);
        ExportSmall(assets);

        var manifest = DatabaseManifest.Read(Path.Combine(assets, DatabaseManifest.FileName));

        Assert.Equal(2, manifest.Databases.Count);
        Assert.NotNull(manifest.Find("index"));
        Assert.NotNull(manifest.Find("symbols"));
    }

    [Fact]
    public async Task A_database_too_large_for_one_asset_is_split_and_joined_back_together()
    {
        var assets = _temp["assets"];
        var asset = ExportSmall(assets, partBytes: 4096, rows: 20_000);

        Assert.True(asset.Parts.Count > 2, $"a 4 KB part size should cut this into several parts, not {asset.Parts.Count}");
        Assert.Equal(["index.sqlite.zst.001", "index.sqlite.zst.002"], asset.Parts.Take(2).Select(p => p.Name));
        Assert.All(asset.Parts.SkipLast(1), p => Assert.Equal(4096, p.Size));
        Assert.Equal(asset.CompressedSize, asset.Parts.Sum(p => p.Size));

        var into = _temp["joined/gameindex.sqlite"];
        await DatabaseInstall.InstallAsync(Kind, new FolderAssetSource(assets), DatabaseManifest.FileName, into, force: false, _log);

        Assert.Equal(asset.Size, new FileInfo(into).Length);
        Assert.Equal(asset.Sha256, Sha256Of(into));
        Assert.Equal(20_000, Rows(into));
    }

    [Fact]
    public async Task A_single_part_keeps_the_plain_name()
    {
        var asset = ExportSmall(_temp["assets"]);

        Assert.Equal(["index.sqlite.zst"], asset.Parts.Select(p => p.Name));
        await DatabaseInstall.InstallAsync(Kind, new FolderAssetSource(_temp["assets"]), DatabaseManifest.FileName,
            _temp["back/gameindex.sqlite"], force: false, _log);
        Assert.Equal(asset.Sha256, Sha256Of(_temp["back/gameindex.sqlite"]));
    }

    [Fact]
    public async Task An_asset_that_does_not_match_its_hash_is_refused()
    {
        var assets = _temp["assets"];
        var asset = ExportSmall(assets, partBytes: 8192, rows: 20_000);
        var part = Path.Combine(assets, asset.Parts[1].Name);
        var bytes = File.ReadAllBytes(part);
        bytes[10] ^= 0xFF;                       // same length, different content
        File.WriteAllBytes(part, bytes);

        var error = await Assert.ThrowsAsync<ReleaseException>(() => DatabaseInstall.InstallAsync(
            Kind, new FolderAssetSource(assets), DatabaseManifest.FileName, _temp["back/gameindex.sqlite"], force: false, _log));

        Assert.Contains(asset.Parts[1].Name, error.Message);
        Assert.Contains("not what the manifest describes", error.Message);
        Assert.False(File.Exists(_temp["back/gameindex.sqlite"]), "nothing is left behind by an install that failed its check");
    }

    [Fact]
    public async Task An_asset_of_the_wrong_length_is_refused_before_it_is_read()
    {
        var assets = _temp["assets"];
        var asset = ExportSmall(assets);
        File.AppendAllText(Path.Combine(assets, asset.Parts[0].Name), "extra");

        var error = await Assert.ThrowsAsync<ReleaseException>(() => DatabaseInstall.InstallAsync(
            Kind, new FolderAssetSource(assets), DatabaseManifest.FileName, _temp["back/gameindex.sqlite"], force: false, _log));

        Assert.Contains("the manifest says", error.Message);
    }

    [Fact]
    public async Task Installing_fetches_the_manifest_and_every_part_over_http()
    {
        var assets = _temp["assets"];
        var asset = ExportSmall(assets, partBytes: 8192, rows: 20_000);
        var serve = new ServeAssets(Fixtures.AsAssets(assets));
        var source = new HttpAssetSource(new HttpClient(serve), "https://github.com/bg3gk/map/releases/download/index-2026-01-02");

        var installed = await DatabaseInstall.InstallAsync(Kind, source, DatabaseManifest.FileName,
            _temp["fetched/gameindex.sqlite"], force: false, _log);

        Assert.Equal(asset.Sha256, Sha256Of(_temp["fetched/gameindex.sqlite"]));
        Assert.Equal(asset.Parts.Count, installed.Parts.Count);
        Assert.Equal("https://github.com/bg3gk/map/releases/download/index-2026-01-02/manifest.json", serve.Asked[0]);
        Assert.Equal(asset.Parts.Count + 1, serve.Asked.Count);
        Assert.Contains("Larian", _log.ToString());
        Assert.Contains("it came from https://github.com/bg3gk/map", _log.ToString());
    }

    [Fact]
    public async Task A_release_that_is_not_there_says_what_to_do_instead()
    {
        var source = new HttpAssetSource(new HttpClient(new ServeAssets(new Dictionary<string, byte[]>())),
            "https://github.com/bg3gk/map/releases/download/index-2026-01-02",
            "If release index-2026-01-02 of bg3gk/map does not exist yet, build the database on this machine instead.");

        var error = await Assert.ThrowsAsync<ReleaseException>(() => DatabaseInstall.InstallAsync(
            Kind, source, DatabaseManifest.FileName, _temp["fetched/gameindex.sqlite"], force: false, _log));

        Assert.Contains("404", error.Message);
        Assert.Contains("build the database on this machine", error.Message);
    }

    [Fact]
    public async Task A_release_without_this_database_says_what_it_does_hold()
    {
        var assets = _temp["assets"];
        var other = Fixtures.WriteDatabase(_temp["built/symbols.sqlite"], 100);
        DatabaseExport.Export(DatabaseKind.Symbols, other, assets, new ExportOptions { Level = 1 }, _log);

        var error = await Assert.ThrowsAsync<ReleaseException>(() => DatabaseInstall.InstallAsync(
            Kind, new FolderAssetSource(assets), DatabaseManifest.FileName, _temp["back/gameindex.sqlite"], force: false, _log));

        Assert.Contains("no \"index\" database", error.Message);
        Assert.Contains("symbols", error.Message);
    }

    [Fact]
    public async Task A_database_that_is_already_here_is_kept_unless_force_is_given()
    {
        var assets = _temp["assets"];
        ExportSmall(assets);
        var into = _temp["back/gameindex.sqlite"];
        Directory.CreateDirectory(Path.GetDirectoryName(into)!);
        File.WriteAllText(into, "mine");

        var error = await Assert.ThrowsAsync<ReleaseException>(() => DatabaseInstall.InstallAsync(
            Kind, new FolderAssetSource(assets), DatabaseManifest.FileName, into, force: false, _log));
        Assert.Contains("--force", error.Message);
        Assert.Equal("mine", File.ReadAllText(into));

        await DatabaseInstall.InstallAsync(Kind, new FolderAssetSource(assets), DatabaseManifest.FileName, into, force: true, _log);
        Assert.Equal(2000, Rows(into));
    }

    [Fact]
    public async Task An_install_leaves_a_receipt_that_doctor_reads()
    {
        var assets = _temp["assets"];
        ExportSmall(assets);
        var into = _temp["back/gameindex.sqlite"];

        Assert.Contains("not here", DatabaseInstall.Describe(Kind, into));
        await DatabaseInstall.InstallAsync(Kind, new FolderAssetSource(assets), DatabaseManifest.FileName, into, force: false, _log);

        var described = DatabaseInstall.Describe(Kind, into);
        Assert.Contains("installed from", described);
        Assert.Contains("Patch8_HotFix10", described);

        File.Delete(DatabaseKind.ReceiptPath(into));
        Assert.Contains("built on this machine", DatabaseInstall.Describe(Kind, into));
    }

    [Fact]
    public void A_pin_without_a_tag_refuses_to_pretend_there_is_a_release()
    {
        var file = _temp["index-release.json"];
        File.WriteAllText(file, """{ "repository": "bg3gk/map", "tag": null, "manifest": "manifest.json" }""");

        var pin = ReleasePin.Read(file);
        Assert.Null(pin.Tag);
        Assert.Equal("manifest.json", pin.Manifest);

        var error = Assert.Throws<ReleaseException>(() => pin.Source());
        Assert.Contains("nothing has been published", error.Message);
        Assert.Contains("Build the database", error.Message);
    }

    [Fact]
    public void A_pin_with_a_tag_points_at_that_release()
    {
        var file = _temp["index-release.json"];
        File.WriteAllText(file, """{ "repository": "bg3gk/map", "tag": "index-2026-01-02" }""");

        var source = ReleasePin.Read(file).Source(new HttpClient(new ServeAssets(new Dictionary<string, byte[]>())));

        Assert.Equal("https://github.com/bg3gk/map/releases/download/index-2026-01-02/manifest.json",
            source.Describe("manifest.json"));
    }

    [Fact]
    public void A_pin_may_point_at_a_folder_or_a_file_url_instead()
    {
        var file = _temp["index-release.json"];
        var folder = _temp["assets"];
        Directory.CreateDirectory(folder);
        File.WriteAllText(file, $$"""{ "repository": "bg3gk/map", "baseUrl": {{System.Text.Json.JsonSerializer.Serialize(new Uri(folder).AbsoluteUri)}} }""");

        var source = ReleasePin.Read(file).Source();

        Assert.IsType<FolderAssetSource>(source);
        Assert.Equal(Path.Combine(folder, "manifest.json"), source.Describe("manifest.json"));
    }

    [Fact]
    public void A_manifest_from_a_newer_GateKit_is_refused_rather_than_half_read()
    {
        var error = Assert.Throws<ReleaseException>(() =>
            DatabaseManifest.Parse("""{ "manifestVersion": 99, "databases": [] }""", "manifest.json"));
        Assert.Contains("Update GateKit", error.Message);
    }

    [Fact]
    public void Exporting_a_database_that_is_not_there_names_the_build_command()
    {
        var error = Assert.Throws<ReleaseException>(() =>
            DatabaseExport.Export(Kind, _temp["nothing.sqlite"], _temp["assets"], new ExportOptions(), _log));
        Assert.Contains("gatekit data build", error.Message);
    }

    static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    static long Rows(string path)
    {
        long count;
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString()))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT count(*) FROM lines";
            count = (long)command.ExecuteScalar()!;
        }
        SqliteConnection.ClearAllPools();
        return count;
    }
}
