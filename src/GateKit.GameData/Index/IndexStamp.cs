using System.Globalization;
using GateKit.GameData.Packages;
using GateKit.Platform;
using Microsoft.Data.Sqlite;

namespace GateKit.GameData.Index;

/// <summary>One input of an index as its <c>inputs</c> row recorded it, and what it is now.</summary>
/// <param name="Outcome">listed (read as recorded, every part's bytes in a reference list), unlisted
/// (read as recorded, a part in no list), changed (size or time differs from the record), absent.</param>
public sealed record IndexInput(string Name, string Recorded, string Outcome, string? Build, string Detail);

/// <summary>What `data stamp` established about an index.</summary>
public sealed record IndexStampResult(IReadOnlyList<IndexInput> Inputs, string? Build, string Verdict, string Evidence);

/// <summary>
/// Gives an index built without <c>game_build</c> its build from evidence: every input its
/// <c>inputs</c> row records (the packages and the Osiris header, by size and time) is found
/// again, compared with the record, hashed, and looked up in the reference lists. The build is
/// the one every listed input belongs to; the verdict is clean only when every input is listed.
/// </summary>
public static class IndexStamp
{
    /// <summary>Metadata key: how the build of an index was established, when it was stamped.</summary>
    public const string EvidenceKey = "game_evidence";

    static readonly HashSet<string> Options = new(StringComparer.Ordinal) { "header", "languages", "scenes-only", "all-keys", "schema" };

    public static IndexStampResult Examine(string database, string? dataFolder, IReadOnlyList<string> localization, BuildEvidence lists, int jobs)
    {
        string inputs, game;
        using (var db = GateKit.Home.HomeDatabase.OpenReadOnly(database))
        {
            inputs = Sql.Scalar(db, "SELECT value FROM meta WHERE key = 'inputs'") as string
                     ?? throw new InvalidOperationException($"{database} records no inputs (it was built before they were recorded): there is nothing to hold it to; build it again");
            game = Sql.Scalar(db, "SELECT value FROM meta WHERE key = 'game'") as string ?? "";
        }
        var data = dataFolder ?? Path.Combine(game, "Data");
        var gameFolder = Path.GetFullPath(Path.Combine(data, ".."));
        var recorded = inputs.Split('|').Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1], StringComparer.Ordinal);
        var languages = recorded.TryGetValue("languages", out var l) ? l.Split(',', StringSplitOptions.RemoveEmptyEntries) : [];

        using var packages = new GamePackages(data);
        var present = packages.Find(languages, localization);
        var work = new List<(string Name, string Recorded, IReadOnlyList<string>? Files, string? Now)>();
        foreach (var (name, stamp) in recorded.Where(p => !Options.Contains(p.Key)))
            work.Add(present.TryGetValue(name, out var file) ? (name, stamp, file.Parts(), file.Stamp()) : (name, stamp, null, null));
        if (recorded.TryGetValue("header", out var header) && header != "-")
        {
            var path = IndexBuilder.FindStoryHeader(data);
            work.Add(("header", header, path is null ? null : [path], path is null ? null : StampOf(path)));
        }

        var results = new IndexInput[work.Count];
        Parallel.For(0, work.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, jobs) }, i =>
        {
            var (name, stamp, files, now) = work[i];
            if (files is null)
            {
                results[i] = new IndexInput(name, stamp, "absent", null, "not in the game's Data folder or a --localization folder");
                return;
            }
            if (now != stamp)
            {
                results[i] = new IndexInput(name, stamp, "changed", null, $"recorded {stamp}, now {now}");
                return;
            }
            var builds = new List<string[]>();
            foreach (var f in files)
            {
                var sha = BuildEvidence.Sha256(f);
                var relative = Path.GetRelativePath(gameFolder, f).Replace('\\', '/');
                var listed = lists.Find(sha).Where(x => x.Path == relative).Select(x => x.Build).Distinct(StringComparer.Ordinal).ToArray();
                if (listed.Length == 0)
                {
                    results[i] = new IndexInput(name, stamp, "unlisted", null, $"{relative} sha256 {sha} is in none of {lists.Describe()}");
                    return;
                }
                builds.Add(listed);
            }
            var shared = string.Join(",", builds.Skip(1).Aggregate((IEnumerable<string>)builds[0], (a, b) => a.Intersect(b, StringComparer.Ordinal)).Order(StringComparer.Ordinal));
            results[i] = new IndexInput(name, stamp, "listed", shared.Length == 0 ? null : shared, $"{files.Count} file{(files.Count == 1 ? "" : "s")} as recorded, in {lists.Describe()}");
        });

        var ordered = results.OrderBy(r => r.Name, StringComparer.Ordinal).ToList();
        var listedInputs = ordered.Where(r => r.Outcome == "listed").ToList();
        var common = listedInputs.Count == 0 ? [] : listedInputs.Skip(1)
            .Aggregate((IEnumerable<string>)(listedInputs[0].Build?.Split(',') ?? []), (a, r) => a.Intersect(r.Build?.Split(',') ?? [], StringComparer.Ordinal)).ToList();
        var build = common.Count == 0 ? null : string.Join(",", common);
        var verdict = build is not null && listedInputs.Count == ordered.Count ? "clean" : GameFingerprint.Unverified;
        var others = ordered.Where(r => r.Outcome != "listed").GroupBy(r => r.Outcome)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(r => r.Name))}");
        var evidence = $"stamped {GameFingerprint.Time(DateTime.UtcNow)}: {listedInputs.Count} of {ordered.Count} inputs read as recorded and found by SHA-256 in {lists.Describe()}"
                       + (build is null ? "" : $" (build {build})") + string.Concat(others.Select(o => "; " + o));
        return new IndexStampResult(ordered, build, verdict, evidence);
    }

    /// <summary>Writes the stamp into the index's <c>meta</c>, in one transaction.</summary>
    public static void Write(string database, IndexStampResult result)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        db.Open();
        using var tx = db.BeginTransaction();
        foreach (var key in new[] { GameFingerprint.BuildKey, GameFingerprint.VerdictKey, EvidenceKey })
            Exec(db, tx, "DELETE FROM meta WHERE key = $k", ("$k", key));
        var rows = new List<(string, string)> { (GameFingerprint.VerdictKey, result.Verdict), (EvidenceKey, result.Evidence) };
        if (result.Build is not null) rows.Insert(0, (GameFingerprint.BuildKey, result.Build));
        foreach (var (k, v) in rows)
            Exec(db, tx, "INSERT INTO meta VALUES ($k, $v)", ("$k", k), ("$v", v));
        tx.Commit();
    }

    static void Exec(SqliteConnection db, SqliteTransaction tx, string sql, params (string Name, string Value)[] args)
    {
        using var c = db.CreateCommand();
        c.Transaction = tx;
        c.CommandText = sql;
        foreach (var (n, v) in args) c.Parameters.AddWithValue(n, v);
        c.ExecuteNonQuery();
    }

    static string StampOf(string path)
    {
        var info = new FileInfo(path);
        return string.Create(CultureInfo.InvariantCulture, $"{info.Length}:{new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds()}");
    }
}
