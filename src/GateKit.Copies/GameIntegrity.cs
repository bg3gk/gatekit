using GateKit.Platform;

namespace GateKit.Copies;

/// <summary>
/// `gatekit game verify` for the game of this copy, the installed one or an instance, and the fingerprint
/// each leaves: in the state folder for the installed game, in the copy's record for a copy.
/// Reads the game folder and Steam's folders; writes only the fingerprint.
/// </summary>
public static class GameIntegrity
{
    /// <summary>What a copy holds of the game; the rest of the manifests is outside it by design.</summary>
    public static readonly IReadOnlyList<string> CopyHolds = ["bin", "Data"];

    public static string FingerprintPath(string stateFolder) => Path.Combine(stateFolder, GameFingerprint.FileName);

    /// <summary>Where reference lists are kept: <c>&lt;home&gt;/reference/game-files</c>; null without a home.</summary>
    public static string? ReferenceFolder(CopySettings settings) =>
        settings.Home is { } home ? ReferenceList.FolderIn(home.Folder) : null;

    /// <summary>Writes the reference list a verification proves (<see cref="ReferenceLists.From"/>) and
    /// says where; <paramref name="path"/> null: <c>&lt;references&gt;/&lt;app&gt;-&lt;build&gt;.json</c>.</summary>
    public static string WriteReference(VerifyReport report, string? path)
    {
        var list = ReferenceLists.From(report);
        path ??= report.Request.References is { } folder ? Path.Combine(folder, list.FileName)
            : throw new CopyException("no home to keep reference lists in: name a file with --out");
        list.Write(path);
        report.ReferencePath = Path.GetFullPath(path);
        return report.ReferencePath;
    }

    /// <summary>The request for this copy's game: a copy is held to the installed game's Steam manifests.</summary>
    public static VerifyRequest Request(CopyLayout layout, IGamePlatform platform, VerifyMode mode) => new()
    {
        Folder = layout.Game,
        Copy = layout.Instance?.Name,
        Install = layout.Settings.GameFolder,
        SteamRoot = platform.SteamRoot(),
        References = ReferenceFolder(layout.Settings),
        BinFolder = layout.Bin,
        Mode = mode,
        Held = layout.Instance is null ? null : CopyHolds,
        HeldReason = layout.Instance is null ? null
            : "a copy holds only the game's bin and Data; the rest stays in the installed game",
        Comparison = platform.PathComparison,
    };

    /// <summary>Verifies this copy's game (<see cref="Request"/>, adjusted as the caller likes) and
    /// writes its fingerprint. A copy whose record does not exist (a copy this home did not make)
    /// is read, but its record is not created.</summary>
    public static VerifyReport Verify(CopyLayout layout, VerifyRequest request,
        TextWriter? progress = null, bool live = false, CancellationToken cancel = default)
    {
        if (layout.Instance is { } copy && !Directory.Exists(Path.Combine(copy.Game, "bin")))
            throw new CopyException($"instance {copy.Name} has no game folder ({copy.Game}); `gatekit instances create {copy.Name}` makes one");
        if (!Directory.Exists(layout.Game)) throw new CopyException($"{layout.Game}: no such folder");
        var report = GameVerifier.Run(request, progress, live, cancel);
        if (layout.Instance is null || layout.Instance.Mine)
        {
            var path = FingerprintPath(layout.State);
            report.Fingerprint().Write(path);
            report.FingerprintPath = path;
        }
        return report;
    }

    /// <summary>
    /// The fingerprint of a copy that was just made or refreshed: its executables and libraries,
    /// and the installed game's verdict when the files both have are the same. Nothing is
    /// verified here; the copy's own verdict stays "unverified" until `game verify` runs on it.
    /// Rehashes only when the copy's files changed since its last fingerprint.
    /// </summary>
    public static GameFingerprint? RecordCopy(CopyLayout layout, IGamePlatform platform)
    {
        if (layout.Instance is not { } copy) return null;
        var path = FingerprintPath(layout.State);
        var existing = GameFingerprint.Read(path);
        if (existing is not null && existing.StillMatches(layout.Bin)) return existing;

        var files = GameFingerprint.KeyFiles(layout.Game, layout.Bin).Select(f => GameFingerprint.Hash(layout.Game, f)).ToList();
        var installed = GameFingerprint.Read(FingerprintPath(layout.Settings.StateRoot));
        // The installed game's verdict carries over only while it still describes the installed game.
        var current = installed is not null && installed.Verdict != GameFingerprint.Unverified
            && installed.StillMatches(Path.Combine(layout.Settings.GameFolder, "bin")) && Agree(installed.Files, files)
            ? installed : null;
        var print = new GameFingerprint
        {
            Folder = Path.GetFullPath(layout.Game),
            Copy = copy.Name,
            Source = current?.Source ?? "none",
            AppId = current?.AppId,
            BuildId = current?.BuildId,
            Depots = current?.Depots ?? [],
            Reference = current?.Reference,
            Files = files,
            Verdict = GameFingerprint.Unverified,
            From = current is null ? null : new FingerprintVerdict(current.Verdict, current.Mode, current.Verified),
            Recorded = DateTime.UtcNow,
        };
        print.Write(path);
        return print;
    }

    /// <summary>Whether a copy's executables and libraries are the installed game's: every
    /// executable of the installed game is in the copy, and every file both have is the same.</summary>
    static bool Agree(IReadOnlyList<FingerprintFile> installed, IReadOnlyList<FingerprintFile> copy)
    {
        var mine = copy.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var file in installed)
        {
            if (mine.TryGetValue(file.Path, out var other))
            {
                if (other.Sha256 != file.Sha256) return false;
            }
            else if (file.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }
}
