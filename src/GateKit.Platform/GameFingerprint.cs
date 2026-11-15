using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using GateKit.Json;

namespace GateKit.Platform;

/// <summary>An executable or library of the game as it was seen: enough to tell later whether it is the same file.</summary>
public sealed record FingerprintFile(string Path, long Size, DateTime Written, string Sha256);

/// <summary>A verdict as a fingerprint carries it, for a copy made from a verified game.</summary>
public sealed record FingerprintVerdict(string Verdict, string? Mode, DateTime? Verified);

/// <summary>
/// What a game folder was when GateKit last looked at it (<c>game-fingerprint.json</c>, kind
/// <c>state/game-fingerprint/1</c>): the Steam build and depot manifests, the SHA-256 of the
/// executables and libraries beside the executable, and the verdict of <c>gatekit game verify</c>.
/// The installed game's is in the state folder, a copy's in that copy's record.
/// </summary>
public sealed record GameFingerprint
{
    public const string FileName = "game-fingerprint.json";
    public const string Kind = "state/game-fingerprint/1";
    public const string Unverified = "unverified";

    public required string Folder { get; init; }
    public string? Copy { get; init; }
    /// <summary>The source of invariants the verdict came from: "steam", "reference", or "none".</summary>
    public required string Source { get; init; }
    public string? AppId { get; init; }
    public string? BuildId { get; init; }
    public IReadOnlyList<SteamDepot> Depots { get; init; } = [];
    /// <summary>The reference list the folder was held to, for source "reference".</summary>
    public string? Reference { get; init; }
    public required IReadOnlyList<FingerprintFile> Files { get; init; }
    public required string Verdict { get; init; }
    public string? Mode { get; init; }
    public DateTime? Verified { get; init; }
    /// <summary>The counts of the verification, as the report has them (kind game/verify#/$defs/counts).</summary>
    public JsonObject? Counts { get; init; }
    public FingerprintVerdict? From { get; init; }
    public required DateTime Recorded { get; init; }

    /// <summary>The verdict with how it was reached: "clean (full)", "unverified".</summary>
    public string VerdictText => Mode is null ? Verdict : $"{Verdict} ({Mode})";

    /// <summary>The executables and libraries in <paramref name="binFolder"/>, as paths inside
    /// <paramref name="gameFolder"/> with '/' between segments, sorted.</summary>
    public static IReadOnlyList<string> KeyFiles(string gameFolder, string binFolder)
    {
        if (!Directory.Exists(binFolder)) return [];
        return Directory.EnumerateFiles(binFolder)
            .Where(IsKeyFile)
            .Select(f => System.IO.Path.GetRelativePath(gameFolder, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>Whether a file is one a fingerprint keeps: an executable or a library.</summary>
    public static bool IsKeyFile(string path) =>
        path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads and hashes one file of the game for a fingerprint.</summary>
    public static FingerprintFile Hash(string gameFolder, string relative)
    {
        var full = System.IO.Path.Combine(gameFolder, relative);
        var info = new FileInfo(full);
        using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan);
        return new FingerprintFile(relative, info.Length, Second(info.LastWriteTimeUtc), Convert.ToHexStringLower(SHA256.HashData(stream)));
    }

    /// <summary>Whether the key files of <see cref="Folder"/> are still the ones this fingerprint
    /// saw, by size and time written (no file is read): none gone, none added, none changed.</summary>
    public bool StillMatches(string binFolder)
    {
        var now = KeyFiles(Folder, binFolder);
        if (now.Count != Files.Count) return false;
        var seen = Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var relative in now)
        {
            if (!seen.TryGetValue(relative, out var then)) return false;
            var info = new FileInfo(System.IO.Path.Combine(Folder, relative));
            if (!info.Exists || info.Length != then.Size || Second(info.LastWriteTimeUtc) != then.Written) return false;
        }
        return true;
    }

    /// <summary>Whether two fingerprints saw the same executables and libraries (by SHA-256).</summary>
    public bool SameFilesAs(GameFingerprint other) =>
        Files.Count == other.Files.Count
        && Files.Zip(other.Files).All(p => string.Equals(p.First.Path, p.Second.Path, StringComparison.OrdinalIgnoreCase) && p.First.Sha256 == p.Second.Sha256);

    /// <summary>
    /// What a database builder records about the game it reads: Steam's build id (null when the
    /// folder is no Steam install) and the verdict of the fingerprint in <paramref name="stateRoot"/>,
    /// which counts only while that fingerprint is of this folder and build and its key files are
    /// unchanged; otherwise "unverified".
    /// </summary>
    public static (string? Build, string Verdict) Describe(string gameFolder, string binFolder, string? stateRoot, StringComparison comparison)
    {
        string? build = null;
        try { build = SteamAppState.ForFolder(gameFolder, comparison)?.BuildId; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (stateRoot is null) return (build, Unverified);
        GameFingerprint? print;
        try { print = Read(System.IO.Path.Combine(stateRoot, FileName)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonProblemException) { return (build, Unverified); }
        if (print is null || !string.Equals(System.IO.Path.GetFullPath(print.Folder), System.IO.Path.GetFullPath(gameFolder), comparison)
            || !print.StillMatches(binFolder))
            return (build, Unverified);
        // An install outside Steam has the build of the reference list its executables matched.
        if (build is null && print.Source == "reference") build = print.BuildId;
        return print.BuildId == build ? (build, print.VerdictText) : (build, Unverified);
    }

    /// <summary>Metadata key of a database: the Steam build of the game it was built from.</summary>
    public const string BuildKey = "game_build";

    /// <summary>Metadata key of a database: the verdict of the game it was built from, at build time.</summary>
    public const string VerdictKey = "game_verdict";

    /// <summary>The rows a database builder adds to its metadata for the game folder it reads
    /// (its executables in <c>bin</c>), against the fingerprint in the home's state:
    /// <see cref="BuildKey"/> when the folder is a Steam install, and <see cref="VerdictKey"/>.</summary>
    public static IReadOnlyList<(string Key, string Value)> BuildMetadata(string gameFolder)
    {
        string? state = null;
        try { state = GateKit.Home.GateKitHome.Current.State.Path; }
        catch (GateKit.Home.HomeException) { }
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var (build, verdict) = Describe(gameFolder, System.IO.Path.Combine(gameFolder, "bin"), state, comparison);
        return build is null ? [(VerdictKey, verdict)] : [(BuildKey, build), (VerdictKey, verdict)];
    }

    /// <summary>The fingerprint in a file, or null when there is none; a broken one throws.</summary>
    public static GameFingerprint? Read(string path) => JsonText.ReadIfExists(path, Kind) is JsonObject o ? FromJson(o) : null;

    /// <summary>Writes the fingerprint (atomically, held to its schema).</summary>
    public void Write(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        JsonText.Write(path, ToJson(), Kind);
    }

    public JsonObject ToJson()
    {
        var o = new JsonObject { ["schema"] = Kind, ["folder"] = Folder };
        if (Copy is not null) o["copy"] = Copy;
        o["source"] = Source;
        if (AppId is not null) o["appId"] = AppId;
        if (BuildId is not null) o["buildId"] = BuildId;
        if (Depots.Count > 0)
            o["depots"] = new JsonArray([.. Depots.Select(d => (JsonNode)new JsonObject { ["depot"] = d.DepotId, ["manifest"] = d.ManifestId })]);
        if (Reference is not null) o["reference"] = Reference;
        o["files"] = new JsonArray([.. Files.Select(f => (JsonNode)new JsonObject
        {
            ["path"] = f.Path, ["size"] = f.Size, ["written"] = Time(f.Written), ["sha256"] = f.Sha256,
        })]);
        o["verdict"] = Verdict;
        if (Mode is not null) o["mode"] = Mode;
        if (Verified is { } verified) o["verified"] = Time(verified);
        if (Counts is not null) o["counts"] = Counts.DeepClone();
        if (From is not null)
        {
            var from = new JsonObject { ["verdict"] = From.Verdict };
            if (From.Mode is not null) from["mode"] = From.Mode;
            if (From.Verified is { } at) from["verified"] = Time(at);
            o["from"] = from;
        }
        o["recorded"] = Time(Recorded);
        return o;
    }

    public static GameFingerprint FromJson(JsonObject o)
    {
        FingerprintVerdict? from = null;
        if (o["from"] is JsonObject f)
            from = new FingerprintVerdict(JsonText.Text(f["verdict"])!, JsonText.Text(f["mode"]), ParseTime(JsonText.Text(f["verified"])));
        return new GameFingerprint
        {
            Folder = JsonText.Text(o["folder"])!,
            Copy = JsonText.Text(o["copy"]),
            Source = JsonText.Text(o["source"])!,
            AppId = JsonText.Text(o["appId"]),
            BuildId = JsonText.Text(o["buildId"]),
            Depots = (o["depots"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(d => new SteamDepot(JsonText.Text(d["depot"])!, JsonText.Text(d["manifest"])!, null, null)).ToList(),
            Reference = JsonText.Text(o["reference"]),
            Files = (o["files"] as JsonArray ?? []).OfType<JsonObject>().Select(x => new FingerprintFile(
                JsonText.Text(x["path"])!, x["size"]!.GetValue<long>(), ParseTime(JsonText.Text(x["written"]))!.Value, JsonText.Text(x["sha256"])!)).ToList(),
            Verdict = JsonText.Text(o["verdict"])!,
            Mode = JsonText.Text(o["mode"]),
            Verified = ParseTime(JsonText.Text(o["verified"])),
            Counts = o["counts"]?.DeepClone() as JsonObject,
            From = from,
            Recorded = ParseTime(JsonText.Text(o["recorded"]))!.Value,
        };
    }

    /// <summary>A time in the one spelling of data-format.md: UTC to the second.</summary>
    public static string Time(DateTime utc) => utc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    static DateTime? ParseTime(string? text) =>
        text is null ? null : DateTime.ParseExact(text, "yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    static DateTime Second(DateTime utc) => new(utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
}
