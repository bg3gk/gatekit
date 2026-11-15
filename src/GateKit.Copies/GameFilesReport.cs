using GateKit.Home;
using GateKit.Platform;

namespace GateKit.Copies;

/// <summary>
/// What `gatekit doctor` says about the game's files and the databases read from them: the
/// verdict of the last `game verify`, and whether each database was built from this Steam build
/// of a game verified clean. It reads the fingerprint and the databases' metadata; it hashes nothing.
/// </summary>
public static class GameFilesReport
{
    /// <summary>The game's own check: its verdict, or why there is none that still holds.</summary>
    public static Doctor.Check Game(CopyLayout layout, IGamePlatform platform)
    {
        var build = CurrentBuild(layout, platform);
        GameFingerprint? print;
        try { print = GameFingerprint.Read(GameIntegrity.FingerprintPath(layout.State)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or GateKit.Json.JsonProblemException)
        {
            return new Doctor.Check("game files", Doctor.Verdict.Warn, $"the fingerprint cannot be read: {e.Message}", Verify(layout));
        }
        var copy = layout.Instance is null ? "" : $"copy {layout.Instance.Name}: ";
        if (print is null || print.Verdict == GameFingerprint.Unverified)
        {
            var from = print?.From is { } f ? $"; made from the installed game, which was {f.Verdict}{(f.Mode is null ? "" : $" ({f.Mode})")}" : "";
            return new Doctor.Check("game files", Doctor.Verdict.Warn, $"{copy}not verified{(build is null ? "" : $", build {build}")}{from}", Verify(layout));
        }
        if (print.BuildId != build)
            return new Doctor.Check("game files", Doctor.Verdict.Warn,
                $"{copy}{print.VerdictText} at build {print.BuildId ?? "none"}, and the game is now build {build ?? "none"}", Verify(layout));
        if (!print.StillMatches(layout.Bin))
            return new Doctor.Check("game files", Doctor.Verdict.Warn,
                $"{copy}{print.VerdictText} at {GameFingerprint.Time(print.Verified ?? print.Recorded)}, and its executables or libraries changed since", Verify(layout));
        var detail = $"{copy}{print.VerdictText}{(build is null ? "" : $", build {build}")}, verified {GameFingerprint.Time(print.Verified ?? print.Recorded)}";
        return print.Verdict == "clean"
            ? new Doctor.Check("game files", Doctor.Verdict.Good, detail)
            : new Doctor.Check("game files", Doctor.Verdict.Warn, detail + Counts(print),
                print.Verdict == "no-invariants" ? null : "`gatekit game verify` lists every file; Steam's own \"Verify integrity of game files\" restores Steam's");
    }

    /// <summary>
    /// A database's check of the game it was read from, from <c>game_build</c> and <c>game_verdict</c>
    /// in its <c>meta</c> (several builds are comma-separated: bytes every one of them has) and, where
    /// the database keeps one, the <c>binary_meta</c> row of each game binary. Good when it was read
    /// from the installed game's build with every listed file as delivered (verdict clean or extras);
    /// null when there is no database. <paramref name="remedy"/> is how this database comes to record
    /// its build.
    /// </summary>
    public static Doctor.Check? Database(string name, string path, CopyLayout layout, IGamePlatform platform, string remedy)
    {
        if (HomeDatabase.Problem(path) is not null) return null;
        string? built, verdict;
        IReadOnlyList<string> binaries;
        try
        {
            using var db = HomeDatabase.OpenReadOnly(path);
            built = Meta(db, GameFingerprint.BuildKey);
            verdict = Meta(db, GameFingerprint.VerdictKey);
            binaries = GameBinaries(db);
        }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or MissingDatabaseException)
        {
            return new Doctor.Check($"{name} game", Doctor.Verdict.Warn, $"its metadata cannot be read: {e.Message}");
        }
        var parts = binaries.Count == 0 ? "" : $" ({string.Join("; ", binaries)})";
        if (verdict is null)
            return new Doctor.Check($"{name} game", Doctor.Verdict.Warn,
                "the game build it was made from is unknown: it records none" + parts, remedy);
        var build = CurrentBuild(layout, platform);
        var builds = built?.Split(',') ?? [];
        if (build is null ? built is not null : !builds.Contains(build, StringComparer.Ordinal))
            return new Doctor.Check($"{name} game", Doctor.Verdict.Warn,
                $"made from build {built ?? "unknown"}; the game is build {build ?? "none"}" + parts, remedy);
        if (!AsDelivered(verdict))
            return new Doctor.Check($"{name} game", Doctor.Verdict.Warn,
                $"made from build {built ?? "none"} of a game whose verdict was {verdict} when it was read" + parts,
                "`gatekit game verify`; then " + remedy);
        return new Doctor.Check($"{name} game", Doctor.Verdict.Good, $"build {build ?? "none"}, {verdict} when it was read");
    }

    /// <summary>A verdict under which every file the source lists was as delivered: clean, or extras
    /// (files listed nowhere, which `game files` names).</summary>
    static bool AsDelivered(string verdict) =>
        verdict.StartsWith("clean", StringComparison.Ordinal) || verdict.StartsWith("extras", StringComparison.Ordinal);

    static string? Meta(Microsoft.Data.Sqlite.SqliteConnection db, string key)
    {
        using var command = db.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        try { return command.ExecuteScalar() as string; }
        catch (Microsoft.Data.Sqlite.SqliteException) { return null; }
    }

    /// <summary>"bg3.exe build 25605617, clean" per game binary of a <c>binary_meta</c> table; none without one.</summary>
    static IReadOnlyList<string> GameBinaries(Microsoft.Data.Sqlite.SqliteConnection db)
    {
        using var command = db.CreateCommand();
        command.CommandText = "SELECT binary, game_build, verdict, sha256 FROM binary_meta WHERE kind = 'game' ORDER BY binary";
        var rows = new List<string>();
        try
        {
            using var reader = command.ExecuteReader();
            while (reader.Read())
                rows.Add(reader.IsDBNull(1)
                    ? $"{reader.GetString(0)} build unknown, sha256 {(reader.IsDBNull(3) ? "-" : reader.GetString(3))}"
                    : $"{reader.GetString(0)} build {reader.GetString(1)}, {reader.GetString(2)}");
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { return []; }
        return rows;
    }

    /// <summary>The installed game's build: Steam's, else that of the reference list its last
    /// verification matched while its executables are unchanged.</summary>
    static string? CurrentBuild(CopyLayout layout, IGamePlatform platform)
    {
        try
        {
            if (SteamAppState.ForFolder(layout.Settings.GameFolder, platform.PathComparison)?.BuildId is { } steam) return steam;
            var print = GameFingerprint.Read(GameIntegrity.FingerprintPath(layout.Settings.StateRoot));
            return print is { Source: "reference" } && print.StillMatches(Path.Combine(layout.Settings.GameFolder, "bin")) ? print.BuildId : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or GateKit.Json.JsonProblemException) { return null; }
    }

    static string Counts(GameFingerprint print)
    {
        if (print.Counts is not { } c) return "";
        var parts = new[] { "modified", "missing", "extras" }
            .Select(k => (k, n: c[k]?.GetValue<int>() ?? 0)).Where(p => p.n > 0).Select(p => $"{p.n} {p.k}").ToList();
        return parts.Count == 0 ? "" : $" ({string.Join(", ", parts)})";
    }

    static string Verify(CopyLayout layout) =>
        $"`gatekit{(layout.Instance is null ? "" : $" --instance {layout.Instance.Name}")} game verify` (--quick: sizes and executables only)";
}
