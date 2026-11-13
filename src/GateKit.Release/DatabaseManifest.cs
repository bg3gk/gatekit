using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GateKit.Release;

/// <summary>One file of a release: a whole asset, or one numbered part of a larger one.</summary>
public sealed record AssetPart(string Name, long Size, string Sha256);

/// <summary>
/// What a release says about one database: the files it is made of, what it weighs whole, the
/// build of the game or the Toolkit it was read from, its schema, and when it was made.
/// </summary>
public sealed record DatabaseAsset
{
    /// <summary><see cref="DatabaseKind.Id"/>.</summary>
    public required string Database { get; init; }

    /// <summary>Name the file gets once it is unpacked.</summary>
    public required string FileName { get; init; }

    /// <summary>Size of the database itself, before compression.</summary>
    public required long Size { get; init; }

    /// <summary>sha256 of the database itself, checked after unpacking.</summary>
    public required string Sha256 { get; init; }

    /// <summary>How the parts are compressed; only <c>zstd</c> today.</summary>
    public required string Compression { get; init; }

    /// <summary>Sum of the parts.</summary>
    public required long CompressedSize { get; init; }

    /// <summary>The files to fetch, in the order they are joined back together.</summary>
    public required IReadOnlyList<AssetPart> Parts { get; init; }

    /// <summary>The build of the game or the Toolkit this was read from, in words.</summary>
    public required string Source { get; init; }

    /// <summary>A digest of the database's own schema: two files with the same one answer the same queries.</summary>
    public required string SchemaVersion { get; init; }

    /// <summary>When the asset was exported (UTC).</summary>
    public required DateTimeOffset Created { get; init; }

    /// <summary>What the database's own <c>meta</c> table says, without anything naming a machine.</summary>
    public required IReadOnlyDictionary<string, string> Meta { get; init; }

    public double Ratio => CompressedSize > 0 ? (double)Size / CompressedSize : 0;
}

/// <summary>
/// The <c>manifest.json</c> of a release: every database in it, written by <c>export-release</c>
/// and read by <c>install</c>. Hand-written JSON, so a person can read it in the release page.
/// </summary>
public sealed record DatabaseManifest(int ManifestVersion, IReadOnlyList<DatabaseAsset> Databases)
{
    public const int CurrentVersion = 1;

    public const string FileName = "manifest.json";

    public DatabaseAsset? Find(string database) =>
        Databases.FirstOrDefault(d => d.Database.Equals(database, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds or replaces the entry for one database, keeping the others in order.</summary>
    public DatabaseManifest With(DatabaseAsset asset) => this with
    {
        Databases = [.. Databases.Where(d => !d.Database.Equals(asset.Database, StringComparison.OrdinalIgnoreCase)), asset],
    };

    public string ToJson()
    {
        var databases = new JsonArray();
        foreach (var d in Databases)
        {
            var parts = new JsonArray();
            foreach (var p in d.Parts)
                parts.Add(new JsonObject { ["name"] = p.Name, ["size"] = p.Size, ["sha256"] = p.Sha256 });
            var meta = new JsonObject();
            foreach (var (key, value) in d.Meta) meta[key] = value;
            databases.Add(new JsonObject
            {
                ["database"] = d.Database,
                ["fileName"] = d.FileName,
                ["size"] = d.Size,
                ["sha256"] = d.Sha256,
                ["compression"] = d.Compression,
                ["compressedSize"] = d.CompressedSize,
                ["parts"] = parts,
                ["source"] = d.Source,
                ["schemaVersion"] = d.SchemaVersion,
                ["created"] = d.Created.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                ["meta"] = meta,
            });
        }
        var root = new JsonObject { ["manifestVersion"] = ManifestVersion, ["databases"] = databases };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    public static DatabaseManifest Parse(string json, string origin)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException e)
        {
            throw new ReleaseException($"{origin} is not valid JSON: {e.Message}");
        }
        if (node is not JsonObject root)
            throw new ReleaseException($"{origin} is not a manifest object");

        var version = root["manifestVersion"]?.GetValue<int>() ?? CurrentVersion;
        if (version > CurrentVersion)
            throw new ReleaseException(
                $"{origin} is manifest version {version}; this build of GateKit reads {CurrentVersion}. Update GateKit.");

        var databases = new List<DatabaseAsset>();
        foreach (var entry in root["databases"] as JsonArray ?? [])
        {
            if (entry is not JsonObject d) throw new ReleaseException($"{origin}: a database entry is not an object");
            string Need(string key) => d[key]?.ToString() ?? throw new ReleaseException($"{origin}: an entry has no \"{key}\"");
            var parts = new List<AssetPart>();
            foreach (var part in d["parts"] as JsonArray ?? [])
            {
                if (part is not JsonObject p) throw new ReleaseException($"{origin}: a part is not an object");
                parts.Add(new AssetPart(
                    p["name"]?.ToString() ?? throw new ReleaseException($"{origin}: a part has no name"),
                    p["size"]?.GetValue<long>() ?? 0,
                    p["sha256"]?.ToString() ?? throw new ReleaseException($"{origin}: a part has no sha256")));
            }
            if (parts.Count == 0) throw new ReleaseException($"{origin}: \"{Need("database")}\" lists no parts");
            var meta = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in d["meta"] as JsonObject ?? []) meta[key] = value?.ToString() ?? "";
            databases.Add(new DatabaseAsset
            {
                Database = Need("database"),
                FileName = Need("fileName"),
                Size = d["size"]?.GetValue<long>() ?? 0,
                Sha256 = Need("sha256"),
                Compression = d["compression"]?.ToString() ?? "zstd",
                CompressedSize = d["compressedSize"]?.GetValue<long>() ?? parts.Sum(p => p.Size),
                Parts = parts,
                Source = d["source"]?.ToString() ?? "unknown",
                SchemaVersion = d["schemaVersion"]?.ToString() ?? "unknown",
                Created = d["created"] is { } created
                    ? DateTimeOffset.Parse(created.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)
                    : default,
                Meta = meta,
            });
        }
        return new DatabaseManifest(version, databases);
    }

    public static DatabaseManifest Read(string file) =>
        File.Exists(file) ? Parse(File.ReadAllText(file), file) : new DatabaseManifest(CurrentVersion, []);

    public void Write(string file) => File.WriteAllText(file, ToJson());
}
