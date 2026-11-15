using GateKit.Platform;
using Microsoft.Data.Sqlite;

namespace GateKit.Symbols.Decomp;

/// <summary>What one binary of the pseudo-code database is, established from the file its
/// project imported (<c>&lt;work&gt;/&lt;binary&gt;/&lt;binary&gt;</c>).</summary>
/// <param name="Kind">game (a file of the game by name; its build known when its bytes are in a
/// reference list), toolkit (the installed Toolkit's file, byte for byte), unknown (neither).</param>
/// <param name="Build">The game builds whose reference list holds these bytes, comma-separated; null when none.</param>
/// <param name="Verdict">clean (in a reference list), toolkit, unknown.</param>
public sealed record BinaryStamp(string Binary, string? Sha256, long? Size, string Kind, string? Build, string Verdict, string Evidence);

/// <summary>
/// The pseudo-code database's record of what it was made from: a row of <c>binary_meta</c> per
/// binary and, in <c>meta</c>, <c>game_build</c> and <c>game_verdict</c> of its game binaries
/// together, the keys the other databases carry. Written by `decomp stamp`, `load` and
/// `refresh`; read by `status` and `gatekit doctor`.
/// </summary>
public static class Provenance
{
    public const string Table = """
        -- Per binary: the SHA-256 of the file its project imported, and what that file is.
        CREATE TABLE IF NOT EXISTS binary_meta (
            binary TEXT PRIMARY KEY, sha256 TEXT, size INTEGER, kind TEXT, game_build TEXT, verdict TEXT, evidence TEXT, stamped TEXT
        );
        CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT);
        """;

    /// <summary>Establishes what a binary is from the copy its project keeps.</summary>
    public static BinaryStamp Examine(DecompSettings settings, string binary, BuildEvidence lists)
    {
        var copy = Path.Combine(settings.Work, binary, binary);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var gameName = lists.Names(binary) || File.Exists(Path.Combine(settings.Game, "bin", binary));
        if (!File.Exists(copy))
            return new BinaryStamp(binary, null, null, gameName ? "game" : "unknown", null, "unknown",
                $"the project folder keeps no copy of the binary ({copy})");
        var size = new FileInfo(copy).Length;
        var sha = BuildEvidence.Sha256(copy);
        var listed = lists.Find(sha);
        if (listed.Count > 0)
        {
            var builds = listed.Select(l => l.Build).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            var where = string.Join("; ", listed.Select(l => $"{l.Path} of build {l.Build} in {l.List}"));
            return new BinaryStamp(binary, sha, size, "game", string.Join(",", builds), "clean",
                $"SHA-256 of {copy} is that of {where}");
        }
        var toolkitFile = Path.Combine(settings.Toolkit, binary);
        string? toolkitSha = File.Exists(toolkitFile) ? BuildEvidence.Sha256(toolkitFile) : null;
        if (toolkitSha == sha)
        {
            string? toolkitBuild = null;
            try { toolkitBuild = SteamAppState.ForFolder(settings.Toolkit, comparison)?.BuildId; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return new BinaryStamp(binary, sha, size, "toolkit", null, "toolkit",
                $"SHA-256 of {copy} is that of {toolkitFile}" + (toolkitBuild is null ? "" : $" (Toolkit build {toolkitBuild})"));
        }
        var why = new List<string> { $"SHA-256 of {copy} is in none of {lists.Describe()}" };
        if (toolkitSha is not null) why.Add($"{toolkitFile} differs");
        if (gameName) why.Add("a file of the game has this name");
        return new BinaryStamp(binary, sha, size, gameName ? "game" : "unknown", null, "unknown", string.Join("; ", why));
    }

    /// <summary>Writes a binary's row and the database-wide keys, in the caller's connection.</summary>
    public static void Write(SqliteConnection db, BinaryStamp stamp)
    {
        db.Execute(Table);
        db.Execute("INSERT OR REPLACE INTO binary_meta VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
            stamp.Binary, stamp.Sha256, stamp.Size, stamp.Kind, stamp.Build, stamp.Verdict, stamp.Evidence, Py.Now());
        Summarize(db);
    }

    /// <summary>
    /// <c>meta</c> from the rows of <c>binary_meta</c>: <c>game_build</c> is the build every game
    /// binary belongs to (left out when they share none), <c>game_verdict</c> clean when every game
    /// binary is clean, else unknown. Binaries of the Toolkit and binaries of no known kind do
    /// not count; a database without a game binary records none.
    /// </summary>
    public static void Summarize(SqliteConnection db)
    {
        var game = db.Query("SELECT game_build, verdict FROM binary_meta WHERE kind = 'game'")
            .Select(r => (Builds: (r[0] as string)?.Split(',') ?? [], Verdict: r[1] as string)).ToList();
        db.Execute("DELETE FROM meta WHERE key IN (?, ?)", GameFingerprint.BuildKey, GameFingerprint.VerdictKey);
        if (game.Count == 0)
            return;
        var shared = game.Skip(1).Aggregate((IEnumerable<string>)game[0].Builds, (common, g) => common.Intersect(g.Builds, StringComparer.Ordinal)).ToList();
        if (shared.Count > 0)
            db.Execute("INSERT INTO meta VALUES (?, ?)", GameFingerprint.BuildKey, string.Join(",", shared));
        db.Execute("INSERT INTO meta VALUES (?, ?)", GameFingerprint.VerdictKey,
            game.All(g => g.Verdict == "clean") && shared.Count > 0 ? "clean" : "unknown");
    }

    /// <summary>The rows of <c>binary_meta</c>, or null when the database has no such table.</summary>
    public static IReadOnlyList<BinaryStamp>? Read(SqliteConnection db)
    {
        try
        {
            return [.. db.Query("SELECT binary, sha256, size, kind, game_build, verdict, evidence FROM binary_meta ORDER BY binary").Select(r =>
                new BinaryStamp((string)r[0]!, r[1] as string, r[2] is null ? null : Convert.ToInt64(r[2], System.Globalization.CultureInfo.InvariantCulture),
                    (string)r[3]!, r[4] as string, (string)r[5]!, (string)r[6]!))];
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    /// <summary>One line of a binary's stamp, as `stamp` and `status` print it.</summary>
    public static string Line(BinaryStamp s) =>
        $"{s.Binary}: {s.Kind}, {(s.Build is null ? "" : $"build {s.Build}, ")}{s.Verdict}; sha256 {s.Sha256 ?? "-"}; {s.Evidence}";

    /// <summary>`decomp stamp [binary]`: every binary of the database (or one) examined again and written,
    /// in one transaction of an ordinary journal, so a reader sees the old record or the new one.</summary>
    internal static int Stamp(DecompSession s, string? binary)
    {
        var path = s.Settings.DbPath;
        if (!GateKit.Home.HomeDatabase.Usable(path))
            throw new SymbolsException($"{path}: no pseudo-code database to stamp (`gatekit decomp load` makes one)");
        var lists = BuildEvidence.Load(s.Settings.References);
        foreach (var bad in lists.Unreadable)
            s.Err.WriteLine($"reference list not read: {bad}");
        s.CloseDb();
        using var db = Sql.OpenWrite(path);
        var names = db.Query("SELECT name FROM binaries ORDER BY name").Select(r => (string)r[0]!)
            .Where(n => binary is null || string.Equals(n, binary, StringComparison.OrdinalIgnoreCase)).ToList();
        if (names.Count == 0)
            throw new SymbolsException(binary is null ? $"{path} holds no binary" : $"{binary} is not in {path}");
        var stamps = names.Select(n => Examine(s.Settings, n, lists)).ToList();
        db.Execute("BEGIN IMMEDIATE");
        db.Execute(Table);
        foreach (var stamp in stamps)
            db.Execute("INSERT OR REPLACE INTO binary_meta VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
                stamp.Binary, stamp.Sha256, stamp.Size, stamp.Kind, stamp.Build, stamp.Verdict, stamp.Evidence, Py.Now());
        Summarize(db);
        db.Execute("COMMIT");
        foreach (var stamp in stamps)
            s.Out.WriteLine(Line(stamp));
        var build = db.Scalar("SELECT value FROM meta WHERE key = ?", GameFingerprint.BuildKey) as string;
        var verdict = db.Scalar("SELECT value FROM meta WHERE key = ?", GameFingerprint.VerdictKey) as string;
        s.Out.WriteLine(verdict is null ? $"{path}: no game binary, so no game build"
            : $"{path}: game build {build ?? "unknown (its game binaries share none)"}, {verdict}");
        return 0;
    }

    /// <summary>Stamps one binary after `load` or `refresh` wrote it, in their connection.</summary>
    internal static void Record(DecompSession s, SqliteConnection db, string binary)
    {
        var stamp = Examine(s.Settings, binary, BuildEvidence.Load(s.Settings.References));
        Write(db, stamp);
        s.Err.WriteLine(Line(stamp));
    }
}
