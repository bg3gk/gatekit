using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GateKit.Release;

/// <summary>
/// Which release the prebuilt databases are fetched from. Pinned in index-release.json at the
/// repository root, the way se-release.json pins the Script Extender build: a database and the
/// tools that read it belong together, so this is not resolved at run time.
/// </summary>
public sealed record ReleasePin(string Repository, string? Tag, string Manifest, string? BaseUrl)
{
    public const string DefaultRepository = "bg3gk/map";

    public const string FileName = "index-release.json";

    public static ReleasePin Read(string file)
    {
        JsonNode node;
        try
        {
            node = JsonNode.Parse(File.ReadAllText(file), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                ?? throw new ReleaseException($"{file} is empty");
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            throw new ReleaseException($"{file} cannot be read: {e.Message}");
        }
        return new ReleasePin(
            node["repository"]?.ToString() ?? DefaultRepository,
            node["tag"]?.ToString() is { Length: > 0 } tag ? tag : null,
            node["manifest"]?.ToString() is { Length: > 0 } manifest ? manifest : DatabaseManifest.FileName,
            node["baseUrl"]?.ToString() is { Length: > 0 } url ? url : null);
    }

    /// <summary>index-release.json of the repository this program was run from.</summary>
    public static ReleasePin ReadFromRepository(string? workingDirectory = null)
    {
        var repo = DatabaseKind.RepositoryRoot(workingDirectory)
            ?? throw new ReleaseException(
                $"{FileName} belongs to the GateKit repository; run gatekit from it, or pass --from with a folder or URL holding the assets");
        var file = Path.Combine(repo, FileName);
        if (!File.Exists(file))
            throw new ReleaseException($"{file} is missing; build the database yourself, or pass --from");
        return Read(file);
    }

    /// <summary>Where the assets are, or a plain refusal while nothing has been published yet.</summary>
    public IAssetSource Source(HttpClient? http = null)
    {
        if (BaseUrl is { } given) return AssetSources.For(given, http);
        if (Tag is null)
            throw new ReleaseException(
                $"{FileName} pins no release of {Repository} yet: nothing has been published.\n" +
                "Build the database on this machine instead, or pass --from with a folder or URL holding the assets.");
        return new HttpAssetSource(
            http ?? new HttpClient { Timeout = TimeSpan.FromHours(1) },
            $"https://github.com/{Repository}/releases/download/{Tag}",
            $"If release {Tag} of {Repository} does not exist yet, build the database on this machine instead.");
    }

    public string Describe => BaseUrl ?? (Tag is null ? $"{Repository} (no release pinned)" : $"{Repository} {Tag}");
}

/// <summary>
/// Fetches a prebuilt database from a release, checks every byte of it, and puts it where the
/// build command would have written it. Building stays the first-class path; this is the
/// alternative for someone who would rather not spend the minutes or own the game's files twice.
/// </summary>
public static class DatabaseInstall
{
    public static async Task<DatabaseAsset> InstallAsync(DatabaseKind kind, IAssetSource source, string manifestAsset,
        string destination, bool force, TextWriter log, CancellationToken cancel = default)
    {
        if (File.Exists(destination) && !force)
            throw new ReleaseException(
                $"{destination} is already here. Pass --force to replace it, or point {kind.EnvironmentVariable} elsewhere.");

        log.WriteLine($"{kind.Id}: reading {source.Describe(manifestAsset)}");
        var manifest = DatabaseManifest.Parse(await ReadTextAsync(source, manifestAsset, cancel), source.Describe(manifestAsset));
        var asset = manifest.Find(kind.Id)
            ?? throw new ReleaseException($"that release holds no \"{kind.Id}\" database; it has: " +
                (manifest.Databases.Count == 0 ? "nothing" : string.Join(", ", manifest.Databases.Select(d => d.Database))));
        if (!asset.Compression.Equals(DatabaseExport.Compression, StringComparison.OrdinalIgnoreCase))
            throw new ReleaseException($"{kind.Id} is compressed with \"{asset.Compression}\", which this build of GateKit cannot read");

        log.WriteLine($"  {asset.Source}");
        log.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  {Sizes.Format(asset.CompressedSize)} to fetch, {Sizes.Format(asset.Size)} unpacked, schema {asset.SchemaVersion}, made {asset.Created:yyyy-MM-dd}"));

        var clock = Stopwatch.StartNew();
        var scratch = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destination))!, ".gatekit-install");
        Directory.CreateDirectory(scratch);
        var fetched = new List<string>();
        var temporary = new List<string>();
        try
        {
            foreach (var part in asset.Parts)
            {
                cancel.ThrowIfCancellationRequested();
                var local = source.LocalPath(part.Name);
                if (local is null)
                {
                    local = Path.Combine(scratch, part.Name);
                    temporary.Add(local);
                    log.WriteLine($"  fetching {source.Describe(part.Name)} ({Sizes.Format(part.Size)})");
                    using var from = await source.OpenAsync(part.Name, cancel);
                    using var to = File.Create(local);
                    await from.CopyToAsync(to, cancel);
                }
                Verify(local, part, log);
                fetched.Add(local);
            }

            var incoming = destination + ".incoming";
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
            string unpacked;
            using (var joined = new ConcatStream(fetched))
            using (var zstd = new ZstdSharp.DecompressionStream(joined))
            using (var file = File.Create(incoming))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[1 << 20];
                int read;
                while ((read = zstd.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    hash.AppendData(buffer, 0, read);
                    file.Write(buffer, 0, read);
                }
                unpacked = Convert.ToHexStringLower(hash.GetHashAndReset());
            }
            if (!unpacked.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(incoming);
                throw new ReleaseException(
                    $"the unpacked {kind.FileName} is not what the manifest describes: expected sha256 {asset.Sha256}, got {unpacked}");
            }
            File.Move(incoming, destination, overwrite: true);
            WriteReceipt(destination, source, asset);
        }
        finally
        {
            foreach (var file in temporary)
            {
                try { File.Delete(file); } catch (IOException) { /* a leftover is better than a crash */ }
            }
            try { if (Directory.Exists(scratch) && Directory.GetFileSystemEntries(scratch).Length == 0) Directory.Delete(scratch); }
            catch (IOException) { /* likewise */ }
        }

        log.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  {destination}: {Sizes.Format(asset.Size)} in {clock.Elapsed.TotalSeconds:0.0} s, every part checked"));
        log.WriteLine($"  it came from {source.Describe(manifestAsset)}");
        log.WriteLine(kind.Attribution);
        return asset;
    }

    static void Verify(string file, AssetPart part, TextWriter log)
    {
        var info = new FileInfo(file);
        if (!info.Exists) throw new ReleaseException($"{part.Name} was not fetched");
        if (part.Size > 0 && info.Length != part.Size)
            throw new ReleaseException($"{part.Name} is {info.Length} bytes; the manifest says {part.Size}");
        string hash;
        using (var stream = File.OpenRead(file)) hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (!hash.Equals(part.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new ReleaseException($"{part.Name} is not what the manifest describes: expected sha256 {part.Sha256}, got {hash}");
        log.WriteLine($"  {part.Name}: sha256 {hash[..12]}… checked");
    }

    static async Task<string> ReadTextAsync(IAssetSource source, string asset, CancellationToken cancel)
    {
        using var stream = await source.OpenAsync(asset, cancel);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancel);
    }

    /// <summary>Leaves a note beside the database saying where it came from, for `gatekit doctor`.</summary>
    static void WriteReceipt(string destination, IAssetSource source, DatabaseAsset asset)
    {
        var receipt = new JsonObject
        {
            ["database"] = asset.Database,
            ["from"] = source is FolderAssetSource folder ? folder.Folder : source.Describe(DatabaseManifest.FileName),
            ["source"] = asset.Source,
            ["schemaVersion"] = asset.SchemaVersion,
            ["sha256"] = asset.Sha256,
            ["created"] = asset.Created.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["installed"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        };
        File.WriteAllText(DatabaseKind.ReceiptPath(destination),
            receipt.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    /// <summary>What <c>gatekit doctor</c> says about one database: whether it is here, and how it got here.</summary>
    public static string Describe(DatabaseKind kind, string? path = null)
    {
        path ??= kind.DefaultPath;
        if (!File.Exists(path))
            return $"not here; run `{kind.BuildCommand}` or `{kind.InstallCommand}`";
        var size = Sizes.Format(new FileInfo(path).Length);
        var receipt = DatabaseKind.ReceiptPath(path);
        if (!File.Exists(receipt)) return $"{path} ({size}), built on this machine";
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(receipt));
            var from = node?["from"]?.ToString() ?? "a release";
            var made = node?["source"]?.ToString();
            return $"{path} ({size}), installed from {from}" + (made is null ? "" : $"; {made}");
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return $"{path} ({size}), installed (its receipt cannot be read)";
        }
    }
}
