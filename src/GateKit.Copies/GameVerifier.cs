using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using GateKit.Platform;

namespace GateKit.Copies;

/// <summary>How much of each file is read: <c>Quick</c> compares every size and hashes the
/// executables and libraries; <c>Full</c> hashes everything.</summary>
public enum VerifyMode { Quick, Full }

/// <summary>A kind of file that may be in a game folder without being the store's, named so that it
/// is never dropped silently: the report lists every file it matched, under its id.</summary>
/// <param name="Id">Short name, as the report shows it.</param>
/// <param name="Patterns">Paths inside the game folder, '/' between segments; <c>*</c> within a segment, <c>**/</c> across segments.</param>
/// <param name="Reason">Why such a file does not make the game other than its store delivered it.</param>
public sealed record BenignRule(string Id, IReadOnlyList<string> Patterns, string Reason);

/// <summary>What <see cref="GameVerifier.Run"/> reads and how.</summary>
public sealed record VerifyRequest
{
    /// <summary>The folder read: the installed game or a copy's game folder.</summary>
    public required string Folder { get; init; }
    /// <summary>The copy's name, when <see cref="Folder"/> is a copy.</summary>
    public string? Copy { get; init; }
    /// <summary>The installed game <see cref="Folder"/> is or was copied from: where a store keeps its own files.</summary>
    public required string Install { get; init; }
    /// <summary>The folder of the game's executable, inside <see cref="Folder"/>.</summary>
    public required string BinFolder { get; init; }
    /// <summary>Steam's own folder, for its depot cache; null when Steam is not installed.</summary>
    public string? SteamRoot { get; init; }
    /// <summary>The folder of reference lists (<see cref="ReferenceList"/>).</summary>
    public string? References { get; init; }
    /// <summary>A reference list to hold the folder to, named outright.</summary>
    public string? Reference { get; init; }
    /// <summary>Only this source of invariants; null asks each in turn.</summary>
    public string? Source { get; init; }
    public IReadOnlyList<IInvariantSource> Sources { get; init; } = InvariantSources.All;
    public VerifyMode Mode { get; init; } = VerifyMode.Full;
    /// <summary>The top-level entries a copy holds; null for the installed game, which holds everything.</summary>
    public IReadOnlyList<string>? Held { get; init; }
    /// <summary>Why a copy holds only <see cref="Held"/>.</summary>
    public string? HeldReason { get; init; }
    public StringComparison Comparison { get; init; } = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    /// <summary>Files hashed at once.</summary>
    public int Threads { get; init; } = Math.Clamp(Environment.ProcessorCount, 1, 16);
    public IReadOnlyList<BenignRule> Rules { get; init; } = GameVerifier.Benign;
    /// <summary>Keep SHA-1 and SHA-256 of every listed file hashed (what a reference list is written from).</summary>
    public bool CollectHashes { get; init; }

    public InstallContext Context => new(Folder, Install, BinFolder, SteamRoot, Comparison, References, Reference);
}

/// <summary>A file whose bytes, kind or link are not what its source says.</summary>
public sealed record ModifiedFile(string Path, string Why, string Part, long? ExpectedSize, long? Size, bool UserConfig);

/// <summary>A file or folder the source lists that is not there.</summary>
public sealed record MissingFile(string Path, string Part, long? ExpectedSize, bool Folder);

/// <summary>A file no source lists, and the rule that names it when one does.</summary>
public sealed record ExtraFile(string Path, long Size, string? Rule);

/// <summary>What <see cref="GameVerifier.Run"/> found.</summary>
public sealed class VerifyReport
{
    public const string Kind = "game/verify/1";

    public required VerifyRequest Request { get; init; }
    /// <summary>What the folder was held to; null when no source applied.</summary>
    public Invariants? Invariants { get; init; }
    public required IReadOnlyList<SourceConsidered> Considered { get; init; }
    public required IReadOnlyList<ModifiedFile> Modified { get; init; }
    public required IReadOnlyList<MissingFile> Missing { get; init; }
    public required IReadOnlyList<ExtraFile> Extras { get; init; }
    public required IReadOnlyList<ExtraFile> Benign { get; init; }
    public required int Listed { get; init; }
    public required int Checked { get; init; }
    public required int Hashed { get; init; }
    public required long BytesHashed { get; init; }
    public required int Outside { get; init; }
    public required IReadOnlyList<string> OutsideFolders { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
    public required IReadOnlyList<FingerprintFile> KeyFiles { get; init; }
    /// <summary>With <see cref="VerifyRequest.CollectHashes"/>: lowercase hex SHA-1 and SHA-256 of each listed file hashed.</summary>
    public required IReadOnlyDictionary<string, (string? Sha1, string? Sha256)> Hashes { get; init; }
    public required TimeSpan Took { get; init; }
    public required DateTime Finished { get; init; }
    /// <summary>Where the fingerprint was written, once it was.</summary>
    public string? FingerprintPath { get; set; }
    /// <summary>Where a reference list was written, once it was.</summary>
    public string? ReferencePath { get; set; }

    public string Mode => Request.Mode == VerifyMode.Quick ? "quick" : "full";
    public string Source => Invariants?.Source ?? "none";

    /// <summary>dirty, incomplete, extras or clean; no-invariants when no source applied.</summary>
    public string Verdict =>
        Invariants is null ? "no-invariants"
        : Modified.Count > 0 || Missing.Count > 0 ? "dirty"
        : !Invariants.Complete ? "incomplete"
        : Extras.Count > 0 ? "extras"
        : "clean";

    /// <summary>1 when anything is modified or missing, 2 when there is nothing (or not all) to hold the folder to, else 0.</summary>
    public int ExitCode => Verdict switch { "dirty" => 1, "incomplete" or "no-invariants" => 2, _ => 0 };

    public JsonObject Counts()
    {
        var o = new JsonObject
        {
            ["listed"] = Listed, ["checked"] = Checked, ["hashed"] = Hashed, ["bytesHashed"] = BytesHashed,
            ["modified"] = Modified.Count, ["missing"] = Missing.Count, ["extras"] = Extras.Count, ["benign"] = Benign.Count,
        };
        if (Request.Held is not null) o["outside"] = Outside;
        return o;
    }

    /// <summary>The fingerprint this verification leaves.</summary>
    public GameFingerprint Fingerprint() => new()
    {
        Folder = Path.GetFullPath(Request.Folder),
        Copy = Request.Copy,
        Source = Source,
        AppId = Invariants?.AppId,
        BuildId = Invariants?.BuildId,
        Depots = Invariants?.Depots ?? [],
        Reference = Invariants?.Source == "reference" ? Invariants.Parts[0].Id : null,
        Files = KeyFiles,
        Verdict = Verdict,
        Mode = Mode,
        Verified = Finished,
        Counts = Counts(),
        Recorded = Finished,
    };

    public JsonObject ToJson()
    {
        var o = new JsonObject { ["schema"] = Kind, ["folder"] = Path.GetFullPath(Request.Folder) };
        if (Request.Copy is not null) o["copy"] = Request.Copy;
        o["source"] = Source;
        o["sources"] = new JsonArray([.. Considered.Select(c => (JsonNode)new JsonObject { ["id"] = c.Id, ["used"] = c.Used, ["why"] = c.Why })]);
        if (Invariants?.AppId is { } app) o["appId"] = app;
        if (Invariants?.BuildId is { } build) o["buildId"] = build;
        o["mode"] = Mode;
        o["verdict"] = Verdict;
        o["parts"] = new JsonArray([.. (Invariants?.Parts ?? []).Select(d =>
        {
            var x = new JsonObject { ["id"] = d.Id };
            if (d.Version is not null) x["version"] = d.Version;
            if (d.Dlc is { } dlc) x["dlc"] = dlc;
            x["status"] = d.Status;
            if (d.Status == "read") x["entries"] = d.Entries;
            if (d.File is not null) x["file"] = d.File;
            if (d.Detail is not null) x["detail"] = d.Detail;
            return (JsonNode)x;
        })]);
        o["counts"] = Counts();
        o["modified"] = new JsonArray([.. Modified.Select(m =>
        {
            var x = new JsonObject { ["path"] = m.Path, ["why"] = m.Why, ["part"] = m.Part };
            if (m.ExpectedSize is { } e) x["expectedSize"] = e;
            if (m.Size is { } s) x["size"] = s;
            if (m.UserConfig) x["userConfig"] = true;
            return (JsonNode)x;
        })]);
        o["missing"] = new JsonArray([.. Missing.Select(m =>
        {
            var x = new JsonObject { ["path"] = m.Path, ["part"] = m.Part };
            if (m.ExpectedSize is { } e) x["expectedSize"] = e;
            if (m.Folder) x["folder"] = true;
            return (JsonNode)x;
        })]);
        o["extras"] = new JsonArray([.. Extras.Select(e => (JsonNode)new JsonObject { ["path"] = e.Path, ["size"] = e.Size })]);
        o["benign"] = new JsonArray([.. Benign.Select(e => (JsonNode)new JsonObject { ["path"] = e.Path, ["size"] = e.Size, ["rule"] = e.Rule })]);
        o["rules"] = new JsonArray([.. Request.Rules.Select(r => (JsonNode)new JsonObject
        {
            ["id"] = r.Id, ["patterns"] = new JsonArray([.. r.Patterns.Select(p => (JsonNode)p)]), ["reason"] = r.Reason,
            ["matched"] = Benign.Count(b => b.Rule == r.Id),
        })]);
        if (Request.Held is not null)
            o["outside"] = new JsonObject
            {
                ["count"] = Outside, ["reason"] = Request.HeldReason ?? "a copy holds only " + string.Join(" and ", Request.Held),
                ["folders"] = new JsonArray([.. OutsideFolders.Select(f => (JsonNode)f)]),
            };
        if (Notes.Count > 0) o["notes"] = new JsonArray([.. Notes.Select(n => (JsonNode)n)]);
        o["seconds"] = Math.Round(Took.TotalSeconds, 1);
        if (FingerprintPath is not null) o["fingerprint"] = FingerprintPath;
        if (ReferencePath is not null) o["reference"] = ReferencePath;
        return o;
    }

    /// <summary>The report for a person; each list cut at <paramref name="limit"/> lines (0: all).</summary>
    public IEnumerable<string> Lines(int limit)
    {
        yield return $"game: {Path.GetFullPath(Request.Folder)}" + (Request.Copy is null ? "" : $" (copy {Request.Copy})");
        foreach (var c in Considered) yield return $"  source {c.Id}: {(c.Used ? "used" : "not used")} - {c.Why}";
        if (Invariants is null)
            yield return "  no invariants for this install: nothing to hold it to; the fingerprint records its executables and libraries";
        else
        {
            yield return $"  {Invariants.Source}: " + (Invariants.AppId is null ? "" : $"app {Invariants.AppId}, ")
                + $"build {Invariants.BuildId ?? "(none recorded)"}, {Invariants.Parts.Count} parts";
            foreach (var d in Invariants.Parts)
                yield return $"    {d.Id,-9} {(d.Version is null ? "" : $"version {d.Version,-20} ")}"
                    + (d.Status == "read" ? $"read, {d.Entries} entries" : d.Status + (d.Detail is null ? "" : $": {d.Detail}"))
                    + (d.Dlc is { } dlc ? $" (DLC {dlc})" : "");
        }
        foreach (var note in Notes) yield return "  note: " + note;
        yield return string.Create(CultureInfo.InvariantCulture,
            $"  {Mode}: {Checked} of {Listed} listed entries checked, {Hashed} files hashed ({BytesHashed / 1e9:0.0} GB) in {Took.TotalSeconds:0.0} s");
        if (Request.Held is not null)
            yield return $"  outside the copy: {Outside} listed entries in {string.Join(", ", OutsideFolders)} ({Request.HeldReason})";

        foreach (var line in Section("modified", Modified, m => $"{m.Path}  ({m.Why}"
                     + (m.Why == "size" ? $": {m.Size} bytes, listed {m.ExpectedSize}" : "") + (m.UserConfig ? ", marked user config" : "")
                     + $"; {m.Part})", limit))
            yield return line;
        foreach (var line in Section("missing", Missing, m => $"{m.Path}  ({(m.Folder ? "folder" : $"{m.ExpectedSize} bytes")}; {m.Part})", limit))
            yield return line;
        foreach (var line in Section("extra, listed nowhere", Extras, e => $"{e.Path}  ({e.Size} bytes)", limit))
            yield return line;
        if (Benign.Count > 0)
        {
            yield return $"  known-benign extras ({Benign.Count}):";
            foreach (var rule in Request.Rules)
            {
                var matched = Benign.Where(b => b.Rule == rule.Id).ToList();
                if (matched.Count == 0) continue;
                yield return $"    {rule.Id} ({matched.Count}): {rule.Reason}";
                foreach (var b in matched.Take(limit > 0 ? limit : int.MaxValue)) yield return $"      {b.Path}";
                if (limit > 0 && matched.Count > limit) yield return $"      ... and {matched.Count - limit} more (--json lists all)";
            }
        }
        yield return $"verdict: {Verdict} ({Mode})" + Verdict switch
        {
            "clean" => " - every file as delivered",
            "extras" => " - every listed file as delivered, with files listed nowhere",
            "dirty" => " - files modified or missing",
            "incomplete" => " - a part of the source could not be read; the files it lists were held to nothing",
            _ => " - no invariants for this install",
        };
        if (FingerprintPath is not null) yield return $"fingerprint: {FingerprintPath}";
        if (ReferencePath is not null) yield return $"reference list: {ReferencePath}";
    }

    static IEnumerable<string> Section<T>(string title, IReadOnlyList<T> items, Func<T, string> line, int limit)
    {
        if (items.Count == 0) yield break;
        yield return $"  {title} ({items.Count}):";
        foreach (var item in items.Take(limit > 0 ? limit : int.MaxValue)) yield return "    " + line(item);
        if (limit > 0 && items.Count > limit) yield return $"    ... and {items.Count - limit} more (--limit 0, or --json, lists all)";
    }
}

/// <summary>
/// Holds a game folder to the file list a source of invariants gives (<see cref="IInvariantSource"/>):
/// walks the folder, hashes, compares, and sorts what differs into modified, missing, extra and
/// benign extra. It knows no store. Observation only: nothing in the folder is opened for writing.
/// </summary>
public static class GameVerifier
{
    /// <summary>The files that may be in the game folder without making it other than its store delivered it.</summary>
    public static readonly IReadOnlyList<BenignRule> Benign =
    [
        new("game-logs", ["bin/*.log", "Launcher/*.log"],
            "the game's and its launcher's own logs, written beside their executables at every start"),
        new("script-extender-loader", ["bin/DWrite.dll"],
            "Script Extender's loader: Windows loads it into the game from beside the executable; it changes no file of the game"),
        new("script-extender-settings", ["bin/ScriptExtenderSettings.json", "bin/ScriptExtenderUpdaterConfig.json"],
            "Script Extender's settings, read by its loader and the extender"),
        new("steam-appid", ["**/steam_appid.txt"],
            "names the Steam app to the Steam API when the game is started outside Steam; a GateKit copy writes a number that is no app"),
    ];

    /// <summary>Reads the folder and holds it to the first source that applies. <paramref name="progress"/>
    /// gets a line now and then (overwritten in place when <paramref name="live"/>).</summary>
    public static VerifyReport Run(VerifyRequest request, TextWriter? progress = null, bool live = false, CancellationToken cancel = default)
    {
        var clock = Stopwatch.StartNew();
        var cmp = request.Comparison;
        var comparer = cmp == StringComparison.OrdinalIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var folder = Path.GetFullPath(request.Folder);
        var binRelative = Path.GetRelativePath(folder, Path.GetFullPath(request.BinFolder)).Replace('\\', '/');

        var (invariants, considered) = InvariantSources.Pick(request.Context, request.Sources, request.Source);
        var notes = new List<string>(invariants?.Notes ?? []);
        var expected = new Dictionary<string, List<ExpectedEntry>>(comparer);
        foreach (var entry in invariants?.Entries ?? [])
        {
            if (!expected.TryGetValue(entry.Path, out var list)) expected[entry.Path] = list = [];
            list.Add(entry);
        }
        var overlaps = expected.Where(p => p.Value.Count > 1 && !p.Value.All(x => x.Kind == EntryKind.Folder))
            .OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
        if (overlaps.Count > 0)
            notes.Add($"{overlaps.Count} files are listed by more than one part, and match when they are any one of them: "
                + string.Join(", ", overlaps.Take(10).Select(p => $"{p.Key} ({string.Join(", ", p.Value.Select(x => x.Part).Distinct())})"))
                + (overlaps.Count > 10 ? ", ..." : ""));

        // What is on disk, links followed (a copy's Data is links into the installed game).
        var disk = new Dictionary<string, long>(comparer);
        var broken = new List<string>();
        Walk(folder, "", disk, broken, 0, cancel);
        foreach (var link in broken)
        {
            notes.Add($"{link} is a folder link whose target is gone; it is listed as an extra");
            disk[link] = 0;
        }

        bool InScope(string path) => request.Held is null || request.Held.Contains(path.Split('/')[0], comparer);

        var modified = new ConcurrentBag<ModifiedFile>();
        var missing = new List<MissingFile>();
        var jobs = new List<(string Path, long Size, List<ExpectedEntry> Candidates)>();
        var outsideFolders = new SortedSet<string>(comparer);
        int listed = 0, outside = 0, checkedCount = 0;
        foreach (var (path, candidates) in expected)
        {
            listed++;
            if (!InScope(path))
            {
                outside++;
                outsideFolders.Add(path.Split('/')[0]);
                continue;
            }
            checkedCount++;
            var first = candidates[0];
            var full = Path.Combine(folder, path);
            if (candidates.All(c => c.Kind == EntryKind.Folder))
            {
                if (Directory.Exists(full)) continue;
                if (File.Exists(full)) modified.Add(new ModifiedFile(path, "kind", first.Part, null, null, false));
                else missing.Add(new MissingFile(path, first.Part, null, true));
                continue;
            }
            if (!disk.TryGetValue(path, out var size))
            {
                if (Directory.Exists(full)) modified.Add(new ModifiedFile(path, "kind", first.Part, first.Size, null, first.UserConfig));
                else missing.Add(new MissingFile(path, first.Part, first.Size, false));
                continue;
            }
            if (candidates.Any(c => c.Kind == EntryKind.Link))
            {
                var target = new FileInfo(full).LinkTarget;
                if (target is null || !candidates.Any(c => c.Kind == EntryKind.Link
                        && string.Equals(DepotManifest.Normalize(c.LinkTarget ?? ""), DepotManifest.Normalize(target), cmp)))
                    modified.Add(new ModifiedFile(path, "link", first.Part, null, null, first.UserConfig));
                continue;
            }
            var sameSize = candidates.Where(c => c.Kind == EntryKind.File && c.Size == size).ToList();
            if (sameSize.Count == 0)
            {
                modified.Add(new ModifiedFile(path, "size", first.Part, first.Size, size, first.UserConfig));
                continue;
            }
            var hash = request.Mode == VerifyMode.Full || GameFingerprint.IsKeyFile(path) || sameSize.Any(c => c.Executable);
            if (hash && size > 0) jobs.Add((path, size, sameSize));
        }

        // Key files of the fingerprint: the executables and libraries beside the executable.
        var keyFiles = GameFingerprint.KeyFiles(folder, Path.Combine(folder, binRelative)).ToHashSet(comparer);
        var sha256 = new ConcurrentDictionary<string, string>(comparer);
        var collected = new ConcurrentDictionary<string, (string? Sha1, string? Sha256)>(comparer);

        // Hash, largest first, so the longest file does not start last.
        jobs.Sort((a, b) => b.Size.CompareTo(a.Size));
        var total = jobs.Sum(j => j.Size);
        long done = 0;
        int hashed = 0;
        using (var reporter = progress is null ? null : new Reporter(progress, live, () =>
                   string.Create(CultureInfo.InvariantCulture, $"hashing: {Volatile.Read(ref hashed)} of {jobs.Count} files, "
                       + $"{Interlocked.Read(ref done) / 1e9:0.0} of {total / 1e9:0.0} GB, {clock.Elapsed.TotalSeconds:0} s")))
        {
            var partitioner = Partitioner.Create(jobs, EnumerablePartitionerOptions.NoBuffering);
            Parallel.ForEach(partitioner, new ParallelOptions { MaxDegreeOfParallelism = request.Threads, CancellationToken = cancel }, job =>
            {
                var wantSha1 = request.CollectHashes || job.Candidates.Any(c => c.Sha256 is null && c.Sha1 is not null);
                var wantSha256 = request.CollectHashes || keyFiles.Contains(job.Path) || job.Candidates.Any(c => c.Sha256 is not null);
                var (sha1, sha2) = HashFile(Path.Combine(folder, job.Path), wantSha1, wantSha256, n => Interlocked.Add(ref done, n));
                if (sha2 is not null && keyFiles.Contains(job.Path)) sha256[job.Path] = Convert.ToHexStringLower(sha2);
                if (request.CollectHashes)
                    collected[job.Path] = (sha1 is null ? null : Convert.ToHexStringLower(sha1), sha2 is null ? null : Convert.ToHexStringLower(sha2));
                if (!job.Candidates.Any(c => c.Sha256 is not null ? sha2 is not null && c.Sha256.AsSpan().SequenceEqual(sha2)
                        : c.Sha1 is null || (sha1 is not null && c.Sha1.AsSpan().SequenceEqual(sha1))))
                {
                    var c = job.Candidates[0];
                    modified.Add(new ModifiedFile(job.Path, "content", c.Part, c.Size, job.Size, c.UserConfig));
                }
                Interlocked.Increment(ref hashed);
            });
        }

        // Files no source lists: named by a rule, or extras.
        var extras = new List<ExtraFile>();
        var benign = new List<ExtraFile>();
        if (invariants is not null)
        {
            foreach (var (path, size) in disk)
            {
                if (expected.ContainsKey(path)) continue;
                var rule = request.Rules.FirstOrDefault(r => r.Patterns.Any(p => Matches(p, path, cmp)));
                (rule is null ? extras : benign).Add(new ExtraFile(path, size, rule?.Id));
            }
        }

        var prints = new List<FingerprintFile>();
        foreach (var relative in keyFiles.Order(StringComparer.Ordinal))
        {
            var info = new FileInfo(Path.Combine(folder, relative));
            prints.Add(sha256.TryGetValue(relative, out var known)
                ? new FingerprintFile(relative, info.Length, new DateTime(info.LastWriteTimeUtc.Ticks - info.LastWriteTimeUtc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc), known)
                : GameFingerprint.Hash(folder, relative));
        }

        clock.Stop();
        return new VerifyReport
        {
            Request = request,
            Invariants = invariants,
            Considered = considered,
            Modified = [.. modified.OrderBy(m => m.Path, StringComparer.Ordinal)],
            Missing = [.. missing.OrderBy(m => m.Path, StringComparer.Ordinal)],
            Extras = [.. extras.OrderBy(e => e.Path, StringComparer.Ordinal)],
            Benign = [.. benign.OrderBy(e => e.Path, StringComparer.Ordinal)],
            Listed = listed,
            Checked = checkedCount,
            Hashed = hashed,
            BytesHashed = done,
            Outside = outside,
            OutsideFolders = [.. outsideFolders],
            Notes = notes,
            KeyFiles = prints,
            Hashes = collected,
            Took = clock.Elapsed,
            Finished = DateTime.UtcNow,
        };
    }

    /// <summary>The SHA-1 and SHA-256 asked for, of a file, in one read.</summary>
    static (byte[]? Sha1, byte[]? Sha256) HashFile(string path, bool wantSha1, bool wantSha256, Action<long> read)
    {
        using var sha1 = wantSha1 ? IncrementalHash.CreateHash(HashAlgorithmName.SHA1) : null;
        using var sha2 = wantSha256 ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            int n;
            while ((n = stream.Read(buffer, 0, 1 << 20)) > 0)
            {
                sha1?.AppendData(buffer, 0, n);
                sha2?.AppendData(buffer, 0, n);
                read(n);
            }
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
        return (sha1?.GetHashAndReset(), sha2?.GetHashAndReset());
    }

    static readonly EnumerationOptions Flat = new() { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false };

    /// <summary>Every file under a folder with its size, following folder links. A folder link whose
    /// target is gone is put in <paramref name="broken"/> instead.</summary>
    static void Walk(string folder, string prefix, Dictionary<string, long> into, List<string> broken, int depth, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        if (depth > 64) throw new IOException($"{folder}: folders nested more than 64 deep (a link loop?)");
        var dir = new DirectoryInfo(folder);
        foreach (var file in dir.EnumerateFiles("*", Flat)) into[prefix + file.Name] = file.Length;
        foreach (var sub in dir.EnumerateDirectories("*", Flat))
        {
            if (sub.LinkTarget is not null && !Directory.Exists(sub.ResolveLinkTarget(returnFinalTarget: true)?.FullName))
            {
                broken.Add(prefix + sub.Name);
                continue;
            }
            Walk(sub.FullName, prefix + sub.Name + "/", into, broken, depth + 1, cancel);
        }
    }

    /// <summary>Whether a path (segments split by '/') matches a pattern: <c>*</c> within a segment, <c>**</c> any number of segments.</summary>
    public static bool Matches(string pattern, string path, StringComparison comparison) =>
        Match(pattern.Split('/'), 0, path.Split('/'), 0, comparison);

    static bool Match(string[] pattern, int p, string[] path, int s, StringComparison cmp)
    {
        if (p == pattern.Length) return s == path.Length;
        if (pattern[p] == "**")
        {
            for (var k = s; k <= path.Length; k++)
                if (Match(pattern, p + 1, path, k, cmp)) return true;
            return false;
        }
        return s < path.Length && Segment(pattern[p], path[s], cmp) && Match(pattern, p + 1, path, s + 1, cmp);
    }

    static bool Segment(string pattern, string text, StringComparison cmp)
    {
        var star = pattern.IndexOf('*');
        if (star < 0) return string.Equals(pattern, text, cmp);
        var head = pattern[..star];
        var rest = pattern[(star + 1)..];
        if (!text.StartsWith(head, cmp)) return false;
        for (var i = head.Length; i <= text.Length; i++)
            if (Segment(rest, text[i..], cmp)) return true;
        return false;
    }

    /// <summary>Writes a progress line about once a second: in place on a terminal, else every ten seconds.</summary>
    sealed class Reporter : IDisposable
    {
        readonly TextWriter _output;
        readonly bool _live;
        readonly Func<string> _line;
        readonly Timer _timer;
        int _width;
        bool _stopped;

        public Reporter(TextWriter output, bool live, Func<string> line)
        {
            _output = output;
            _live = live;
            _line = line;
            var period = live ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(10);
            _timer = new Timer(_ => Write(), null, period, period);
        }

        void Write()
        {
            lock (_timer)
            {
                if (_stopped) return;
                var text = _line();
                if (_live)
                {
                    _output.Write("\r" + text.PadRight(_width));
                    _width = text.Length;
                }
                else _output.WriteLine(text);
                _output.Flush();
            }
        }

        public void Dispose()
        {
            _timer.Dispose();
            lock (_timer)
            {
                _stopped = true;
                if (_live && _width > 0) _output.Write("\r" + new string(' ', _width) + "\r");
                _output.Flush();
            }
        }
    }
}
