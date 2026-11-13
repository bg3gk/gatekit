using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace GateKit.Release;

/// <summary>How an export is cut and compressed.</summary>
public sealed record ExportOptions
{
    /// <summary>Largest part written. GitHub refuses a release asset over 2 GB.</summary>
    public long PartBytes { get; init; } = 1_900_000_000;

    /// <summary>Zstandard level. 3 is the standard one; higher is smaller and much slower.</summary>
    public int Level { get; init; } = 3;

    /// <summary>Threads the compressor may use; 0 lets it choose.</summary>
    public int Workers { get; init; }
}

/// <summary>
/// Turns a built database into the files of a release: one Zstandard stream, cut into numbered
/// parts small enough to upload, with a manifest entry that says what it is and where it came from.
/// </summary>
public static class DatabaseExport
{
    public const string Compression = "zstd";

    /// <summary>
    /// Compresses <paramref name="database"/> into <paramref name="into"/> and adds its entry to
    /// the manifest there. Returns the entry.
    /// </summary>
    public static DatabaseAsset Export(DatabaseKind kind, string database, string into, ExportOptions options,
        TextWriter log, CancellationToken cancel = default)
    {
        if (!File.Exists(database))
            throw new ReleaseException($"{database} is not there; build it first with `{kind.BuildCommand}`");
        if (options.PartBytes < 1024) throw new ReleaseException("a part must hold at least a kilobyte");

        var (meta, schemaVersion) = ReadMeta(database);
        Directory.CreateDirectory(into);

        var size = new FileInfo(database).Length;
        log.WriteLine($"{kind.Id}: {Sizes.Format(size)} from {database}");

        var clock = Stopwatch.StartNew();
        var writer = new SplitWriter(into, kind.AssetBaseName, options.PartBytes);
        IReadOnlyList<AssetPart> parts;
        string sha256;
        try
        {
            using (var source = new FileStream(database, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                using (var zstd = new ZstdSharp.CompressionStream(writer, options.Level, leaveOpen: true))
                {
                    if (options.Workers > 0) TrySetWorkers(zstd, options.Workers);
                    var buffer = new byte[1 << 20];
                    long read, done = 0, nextReport = 0;
                    while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancel.ThrowIfCancellationRequested();
                        hash.AppendData(buffer, 0, (int)read);
                        zstd.Write(buffer, 0, (int)read);
                        done += read;
                        if (done >= nextReport)
                        {
                            log.WriteLine($"  {done * 100 / Math.Max(1, size),3}%  {Sizes.Format(writer.Total)} written");
                            nextReport = done + size / 10;
                        }
                    }
                }
                sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
            }
            parts = writer.Finish();
        }
        catch (Exception)
        {
            writer.Abandon();
            throw;
        }
        finally
        {
            writer.Dispose();
        }

        var asset = new DatabaseAsset
        {
            Database = kind.Id,
            FileName = kind.FileName,
            Size = size,
            Sha256 = sha256,
            Compression = Compression,
            CompressedSize = parts.Sum(p => p.Size),
            Parts = parts,
            Source = Source(kind, meta),
            SchemaVersion = schemaVersion,
            Created = DateTimeOffset.UtcNow,
            Meta = meta,
        };

        var manifestFile = Path.Combine(into, DatabaseManifest.FileName);
        var manifest = DatabaseManifest.Read(manifestFile).With(asset) with { ManifestVersion = DatabaseManifest.CurrentVersion };
        manifest.Write(manifestFile);

        log.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  {Sizes.Format(asset.CompressedSize)} in {parts.Count} part{(parts.Count == 1 ? "" : "s")}, " +
            $"{asset.Ratio:0.0}x smaller, {clock.Elapsed.TotalSeconds:0.0} s"));
        foreach (var part in parts)
            log.WriteLine($"  {part.Name}  {Sizes.Format(part.Size)}  sha256 {part.Sha256}");
        log.WriteLine($"  source: {asset.Source}");
        log.WriteLine($"  schema: {asset.SchemaVersion}");
        log.WriteLine($"  manifest: {manifestFile}");
        log.WriteLine(DatabaseKind.Withdrawal);
        return asset;
    }

    /// <summary>Multi-threaded compression where the port offers it; one thread is only slower, never wrong.</summary>
    static void TrySetWorkers(ZstdSharp.CompressionStream stream, int workers)
    {
        try
        {
            stream.SetParameter(ZstdSharp.Unsafe.ZSTD_cParameter.ZSTD_c_nbWorkers, workers);
        }
        catch (Exception e) when (e is ZstdSharp.ZstdException or NotSupportedException or ArgumentException)
        {
            // this build of the port compresses on the calling thread; nothing else changes
        }
    }

    /// <summary>
    /// What the database's own <c>meta</c> table says, with anything naming a machine left out, and
    /// a digest of the schema the file actually has.
    /// </summary>
    internal static (IReadOnlyDictionary<string, string> Meta, string SchemaVersion) ReadMeta(string database)
    {
        var meta = new Dictionary<string, string>(StringComparer.Ordinal);
        var schema = new StringBuilder();
        try
        {
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
                   {
                       DataSource = database,
                       Mode = SqliteOpenMode.ReadOnly,
                   }.ToString()))
            {
                db.Open();
                using (var command = db.CreateCommand())
                {
                    command.CommandText = "SELECT key, value FROM meta";
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        var key = reader.GetString(0);
                        var value = reader.IsDBNull(1) ? "" : reader.GetString(1);
                        if (!IsMachineSpecific(key, value)) meta[key] = value;
                    }
                }
                using (var command = db.CreateCommand())
                {
                    command.CommandText = "SELECT type, name, COALESCE(sql, '') FROM sqlite_master ORDER BY name, type";
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                        schema.Append(reader.GetString(0)).Append('\u001f').Append(reader.GetString(1))
                            .Append('\u001f').Append(reader.GetString(2)).Append('\u001e');
                }
            }
            SqliteConnection.ClearAllPools();
        }
        catch (SqliteException e)
        {
            throw new ReleaseException($"{database} is not a database GateKit can export: {e.Message}");
        }
        if (schema.Length == 0) throw new ReleaseException($"{database} holds no tables");
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(schema.ToString())))[..16];
        return (meta, digest);
    }

    /// <summary>A path on the machine that built the database tells a stranger nothing and says too much.</summary>
    static bool IsMachineSpecific(string key, string value) =>
        key is "game" or "toolkit" or "bg3se"
        || value.Contains(":\\", StringComparison.Ordinal)
        || value.StartsWith('/');

    /// <summary>The build this was read from, in the words the database itself kept.</summary>
    static string Source(DatabaseKind kind, IReadOnlyDictionary<string, string> meta)
    {
        string? Get(string key) => meta.TryGetValue(key, out var v) && v.Length > 0 ? v : null;
        if (kind.Id == DatabaseKind.Symbols.Id)
        {
            var parts = new List<string>();
            if (Get("game_file_version") is { } game) parts.Add($"Baldur's Gate 3 {game}");
            if (Get("toolkit_file_version") is { } toolkit) parts.Add($"Toolkit {toolkit}");
            return parts.Count > 0 ? string.Join(", ", parts) : "an unnamed build";
        }
        var packages = Get("packages");
        var patch = packages?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(p => p.StartsWith("Patch", StringComparison.OrdinalIgnoreCase));
        var languages = Get("languages");
        return "Baldur's Gate 3" + (patch is null ? "" : $" {patch}")
            + (languages is null ? "" : $", localizations: {languages}")
            + (Get("built") is { } built ? $", read {built}" : "");
    }
}

/// <summary>Sizes a person reads rather than counts.</summary>
public static class Sizes
{
    public static string Format(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} KB"),
        < 1024L * 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):0.#} MB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024 * 1024):0.##} GB"),
    };
}
