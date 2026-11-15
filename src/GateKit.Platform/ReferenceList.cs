using System.Text.Json.Nodes;
using GateKit.Json;

namespace GateKit.Platform;

/// <summary>One file of a reference list.</summary>
public sealed record ReferenceFile(string Path, long Size, string? Sha256, string? Sha1);

/// <summary>
/// GateKit's own list of every file of one build of the game (kind <c>reference/game-files/1</c>):
/// paths, sizes and hashes, and no content, so it can be published. Written by
/// `game verify --write-reference` from an install another source proved clean; read by the
/// verifier's reference source and by <see cref="BuildEvidence"/>.
/// </summary>
public sealed record ReferenceList
{
    public const string Kind = "reference/game-files/1";

    public string? AppId { get; init; }
    public required string BuildId { get; init; }
    /// <summary>The source that proved the install clean, and its parts (depot:manifest).</summary>
    public required string MadeFrom { get; init; }
    public IReadOnlyList<string> MadeFromParts { get; init; } = [];
    public required DateTime Made { get; init; }
    public required IReadOnlyList<string> Folders { get; init; }
    public required IReadOnlyList<ReferenceFile> Files { get; init; }

    /// <summary>The usual file name: <c>&lt;app&gt;-&lt;build&gt;.json</c>.</summary>
    public string FileName => $"{AppId ?? "game"}-{BuildId}.json";

    /// <summary>Where reference lists are kept in a home: <c>&lt;home&gt;/reference/game-files</c>.</summary>
    public static string FolderIn(string home) => System.IO.Path.Combine(home, "reference", "game-files");

    /// <summary>Whether every executable this list holds beside the game's is one of <paramref name="executables"/>
    /// (path to SHA-256), and the list holds at least one.</summary>
    public bool MatchesExecutables(IReadOnlyDictionary<string, string> executables)
    {
        var listed = Files.Where(f => f.Sha256 is not null && executables.ContainsKey(f.Path)).ToList();
        return listed.Count == executables.Count && listed.All(f => f.Sha256 == executables[f.Path]);
    }

    public static ReferenceList Read(string path)
    {
        var o = (JsonObject)JsonText.Read(path, Kind);
        return new ReferenceList
        {
            AppId = JsonText.Text(o["appId"]),
            BuildId = JsonText.Text(o["buildId"])!,
            MadeFrom = JsonText.Text(o["madeFrom"]!["source"])!,
            MadeFromParts = [.. (o["madeFrom"]!["parts"] as JsonArray ?? []).Select(p => JsonText.Text(p)!)],
            Made = DateTime.ParseExact(JsonText.Text(o["made"])!, "yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal),
            Folders = [.. (o["folders"] as JsonArray ?? []).Select(p => JsonText.Text(p)!)],
            Files = [.. (o["files"] as JsonArray ?? []).OfType<JsonObject>().Select(f => new ReferenceFile(
                JsonText.Text(f["path"])!, f["size"]!.GetValue<long>(), JsonText.Text(f["sha256"]), JsonText.Text(f["sha1"])))],
        };
    }

    public void Write(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        var o = new JsonObject
        {
            ["schema"] = Kind,
            ["about"] = "Every file of one build of the game as its store delivered it: paths, sizes and hashes, no content. `gatekit game verify` holds any install to it.",
        };
        if (AppId is not null) o["appId"] = AppId;
        o["buildId"] = BuildId;
        o["madeFrom"] = new JsonObject { ["source"] = MadeFrom, ["parts"] = new JsonArray([.. MadeFromParts.Select(p => (JsonNode)p)]) };
        o["made"] = GameFingerprint.Time(Made);
        o["folders"] = new JsonArray([.. Folders.Select(f => (JsonNode)f)]);
        o["files"] = new JsonArray([.. Files.Select(f =>
        {
            var x = new JsonObject { ["path"] = f.Path, ["size"] = f.Size };
            if (f.Sha256 is not null) x["sha256"] = f.Sha256;
            if (f.Sha1 is not null) x["sha1"] = f.Sha1;
            return (JsonNode)x;
        })]);
        JsonText.Write(path, o, Kind);
    }
}
