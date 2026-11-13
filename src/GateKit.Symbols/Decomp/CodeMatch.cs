using System.Diagnostics;
using System.Globalization;
using System.IO.Hashing;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace GateKit.Symbols.Decomp;

/// <summary>Pairs functions of the Toolkit's DLLs with functions of the game by their pseudo-code.
///
/// The Toolkit's DLLs carry names; the game executable does not. This compares the functions
/// themselves: the numbers a function uses (field offsets, masks, sizes, magic values) and the
/// text constants it refers to survive a rebuild even when registers, types and variable names
/// do not. A function's features are those numbers, pairs of consecutive numbers (so order
/// counts) and its text constants. Rare features weigh more. For every Toolkit function the game
/// functions that share a rare feature are candidates; the score of a candidate is the weight of
/// the shared features over the weight of all features of both. A pair is kept when each side is
/// the other's best candidate, the score is high enough and the runner-up is clearly behind.
///
/// Text constants are hashed with XxHash64 of their UTF-8 bytes (the Python tool used an 8-byte
/// blake2b); numbers with the same multiplicative hashes as before. Features are only ever
/// compared with features made by the same tool, so the change only means the feature cache is
/// a new file (features.bin; the Python tool's features.pickle is not read).</summary>
internal static class CodeMatch
{
    static readonly Regex Comment = new(@"/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline);
    static readonly Regex Token = new(@"""((?:[^""\\]|\\.)*)""|(?<![\w.])(-\s*)?(0x[0-9a-fA-F]+|\d+)(?![\w.])", RegexOptions.Compiled);
    const ulong Mask = (1UL << 63) - 1;
    const ulong K1 = 0x9E3779B97F4A7C15, K2 = 0xC2B2AE3D27D4EB4F;

    /// <summary>A literal of the pseudo-code as (value modulo 2^64, whether it is 2^64 or more).</summary>
    static (ulong Low, bool Huge) Literal(string literal)
    {
        ulong v = 0;
        bool huge = false;
        if (literal.Length > 2 && literal[0] == '0' && (literal[1] == 'x' || literal[1] == 'X'))
        {
            for (int i = 2; i < literal.Length; i++)
            {
                huge |= (v >> 60) != 0;
                v = unchecked((v << 4) | (uint)HexDigit(literal[i]));
            }
        }
        else
        {
            foreach (char c in literal)
            {
                ulong digit = (ulong)(int)char.GetNumericValue(c);
                var next = (UInt128)v * 10 + digit;
                huge |= next > ulong.MaxValue;
                v = unchecked((ulong)next);
            }
        }
        return (v, huge);
    }

    static int HexDigit(char c) => c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;

    /// <summary>Sorted 63-bit feature hashes of one function's pseudo-code.</summary>
    public static long[] Features(string text)
    {
        text = Comment.Replace(text, " ");
        int brace = text.IndexOf('{');                   // skip the declaration: its types differ by build
        var body = brace >= 0 ? text[brace..] : text;
        var output = new HashSet<long>();
        ulong? last = null;
        foreach (Match m in Token.Matches(body))
        {
            if (m.Groups[1].Success)
            {
                var s = m.Groups[1].Value;
                if (Py.CodePoints(s) >= 4)
                    output.Add((long)(XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(s)) & Mask));
                continue;
            }
            var (value, huge) = Literal(m.Groups[3].Value);
            if (!huge && value < 2)
                continue;
            if (huge || value >= 1UL << 63)              // the same negative number is printed both ways
                value = unchecked(0 - value);
            else if (value >= 1UL << 31 && value < 1UL << 32)
                value = (1UL << 32) - value;
            // an address of the image, not a constant (a huge value became negative: never one)
            if (!huge && value >= 0x140000000 && value < 0x190000000)
                continue;
            output.Add((long)(unchecked(value * K1 + 1) & Mask));
            if (last != null)
                output.Add((long)(unchecked((last.Value * K1) ^ (value * K2 + 2)) & Mask));
            last = value;
        }
        var result = output.ToArray();
        Array.Sort(result);
        return result;
    }

    const uint CacheMagic = 0x31464B47;                  // "GKF1"

    /// <summary>{function id: feature hashes} of every function with at least four features,
    /// computed in parallel and kept in features.bin next to the database (rebuilt when the
    /// database is newer).</summary>
    public static Dictionary<long, long[]> Extract(string dbPath, string cache, int jobs, Action<string> log)
    {
        if (File.Exists(cache) && File.GetLastWriteTimeUtc(cache) >= File.GetLastWriteTimeUtc(dbPath))
        {
            try
            {
                return ReadCache(cache);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or EndOfStreamException)
            {
                // rebuilt below
            }
        }
        var output = new Dictionary<long, long[]>();
        using var db = Sql.OpenReadOnly(dbPath);
        long top = Sql.Long(db.Scalar("SELECT max(function_id) FROM code")) ?? 0;
        const long step = 4000;
        long blocks = top / step + 1;
        var started = Stopwatch.StartNew();
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, jobs) };
        for (long n = 0; n < blocks; n++)
        {
            var rows = db.Query("SELECT function_id, text FROM code WHERE function_id >= ? AND function_id < ?", n * step, (n + 1) * step)
                         .Select(r => (Id: Convert.ToInt64(r[0], CultureInfo.InvariantCulture), Blob: r[1] as byte[])).ToArray();
            var features = new long[rows.Length][];
            Parallel.For(0, rows.Length, options, i =>
            {
                var blob = rows[i].Blob;
                features[i] = blob == null ? [] : Features(DecompStore.Decompress(blob));
            });
            for (int i = 0; i < rows.Length; i++)
                if (features[i].Length >= 4)
                    output[rows[i].Id] = features[i];
            if ((n + 1) % 20 == 0)
                log($"  features: {n + 1}/{blocks} blocks, {started.Elapsed.TotalSeconds:F0} s");
        }
        WriteCache(cache, output);
        return output;
    }

    static Dictionary<long, long[]> ReadCache(string path)
    {
        using var r = new BinaryReader(new BufferedStream(File.OpenRead(path), 1 << 20));
        if (r.ReadUInt32() != CacheMagic)
            throw new InvalidDataException("not a feature cache");
        int count = r.ReadInt32();
        var output = new Dictionary<long, long[]>(count);
        for (int i = 0; i < count; i++)
        {
            long id = r.ReadInt64();
            var f = new long[r.ReadInt32()];
            for (int k = 0; k < f.Length; k++)
                f[k] = r.ReadInt64();
            output[id] = f;
        }
        return output;
    }

    static void WriteCache(string path, Dictionary<long, long[]> features)
    {
        using var w = new BinaryWriter(new BufferedStream(File.Create(path), 1 << 20));
        w.Write(CacheMagic);
        w.Write(features.Count);
        foreach (var (id, f) in features.OrderBy(p => p.Key))
        {
            w.Write(id);
            w.Write(f.Length);
            foreach (var x in f)
                w.Write(x);
        }
    }

    /// <summary>A proposed pair: Toolkit function, game function, score and lead over the runner-up.</summary>
    public readonly record struct Pair(long Toolkit, long Game, double Score, double Margin);

    /// <summary>The pairs that pass. See the class text.</summary>
    public static List<Pair> Match(IReadOnlyDictionary<long, long[]> feats, IReadOnlyList<long> toolkitIds, IReadOnlyList<long> gameIds,
                                   double minScore, double minMargin, double minWeight, int rare = 40, int jobs = 0, Action<string>? log = null)
    {
        var df = new Dictionary<long, int>();
        foreach (var fid in toolkitIds)
            foreach (var t in feats[fid])
                df[t] = df.GetValueOrDefault(t) + 1;
        var gameDf = new Dictionary<long, int>();
        foreach (var fid in gameIds)
            foreach (var t in feats[fid])
                gameDf[t] = gameDf.GetValueOrDefault(t) + 1;
        double total = toolkitIds.Count + gameIds.Count;
        var weight = new Dictionary<long, double>(df.Count + gameDf.Count);
        foreach (var t in df.Keys.Concat(gameDf.Keys))
            if (!weight.ContainsKey(t))
                weight[t] = Math.Log(total / (df.GetValueOrDefault(t) + gameDf.GetValueOrDefault(t)));
        var index = new Dictionary<long, List<long>>();
        foreach (var fid in gameIds)
            foreach (var t in feats[fid])
                if (gameDf[t] <= rare)
                {
                    if (!index.TryGetValue(t, out var list))
                        index[t] = list = [];
                    list.Add(fid);
                }
        var mass = new Dictionary<long, double>();
        foreach (var fid in toolkitIds.Concat(gameIds))
        {
            double m = 0;
            foreach (var t in feats[fid])
                m += weight[t];
            mass[fid] = m;
        }

        var proposals = new Pair?[toolkitIds.Count];
        var options = new ParallelOptions { MaxDegreeOfParallelism = jobs > 0 ? jobs : Environment.ProcessorCount };
        int doneCount = 0;
        var started = Stopwatch.StartNew();
        Parallel.For(0, toolkitIds.Count, options, n =>
        {
            long tid = toolkitIds[n];
            if (mass[tid] < minWeight)
                return;
            var mine = feats[tid];
            // candidates with their summed weight, in the order they were first seen
            var seen = new Dictionary<long, double>();
            var order = new List<long>();
            foreach (var t in mine)
            {
                if (!index.TryGetValue(t, out var games) || df[t] > rare)
                    continue;
                double w = weight[t];
                foreach (var gid in games)
                {
                    if (seen.TryGetValue(gid, out var sum))
                        seen[gid] = sum + w;
                    else
                    {
                        seen[gid] = w;
                        order.Add(gid);
                    }
                }
            }
            int finished = Interlocked.Increment(ref doneCount);
            if (finished % 50000 == 0)
                lock (options)
                    log?.Invoke($"  matching: {finished}/{toolkitIds.Count} Toolkit functions, {started.Elapsed.TotalSeconds:F0} s");
            if (seen.Count == 0)
                return;
            // Counter.most_common(12): by weight, ties in first-seen order
            var top = order.Select((gid, i) => (gid, i)).OrderByDescending(x => seen[x.gid]).ThenBy(x => x.i).Take(12);
            var scored = new List<(double Score, long Gid)>();
            foreach (var (gid, _) in top)
            {
                double shared = SharedWeight(mine, feats[gid], weight);
                scored.Add((shared / (mass[tid] + mass[gid] - shared), gid));
            }
            scored.Sort((a, b) => a.Score != b.Score ? b.Score.CompareTo(a.Score) : b.Gid.CompareTo(a.Gid));
            var (score, best) = scored[0];
            double margin = score - (scored.Count > 1 ? scored[1].Score : 0.0);
            proposals[n] = new Pair(tid, best, score, margin);
        });

        var bestForGame = new Dictionary<long, double>();
        foreach (var p in proposals)
            if (p is Pair q && (!bestForGame.TryGetValue(q.Game, out var b) || q.Score > b))
                bestForGame[q.Game] = q.Score;
        var output = new List<Pair>();
        foreach (var p in proposals)
            if (p is Pair q && q.Score >= minScore && q.Margin >= minMargin && bestForGame[q.Game] <= q.Score + 1e-9)
                output.Add(q);
        return output;
    }

    /// <summary>Sum of the weights of the features two sorted arrays share.</summary>
    static double SharedWeight(long[] a, long[] b, Dictionary<long, double> weight)
    {
        double sum = 0;
        int i = 0, k = 0;
        while (i < a.Length && k < b.Length)
        {
            if (a[i] < b[k])
                i++;
            else if (a[i] > b[k])
                k++;
            else
            {
                sum += weight[a[i]];
                i++;
                k++;
            }
        }
        return sum;
    }

    /// <summary>`match`: pairs Toolkit functions with game functions by pseudo-code and stores the pairs.</summary>
    public static int Run(DecompSession s, string binary, int jobs, string? source)
    {
        var db = s.OpenWrite();
        void Log(string text) => s.Err.WriteLine(text);
        var started = Stopwatch.StartNew();
        var feats = Extract(s.Settings.DbPath, Path.Combine(s.Settings.Work, "features.bin"), jobs, Log);
        var game = db.Scalar("SELECT id FROM binaries WHERE name = ?", binary) ?? throw new SymbolsException($"{binary} is not loaded");
        long gameId = Convert.ToInt64(game, CultureInfo.InvariantCulture);
        // Functions under 64 bytes are left out: a few numbers do not tell one small accessor from another.
        var info = new Dictionary<long, (long Binary, long Rva)>();
        foreach (var r in db.Query("SELECT id, binary_id, rva FROM functions WHERE size >= 64"))
            info[Convert.ToInt64(r[0], CultureInfo.InvariantCulture)] = (Convert.ToInt64(r[1], CultureInfo.InvariantCulture), Convert.ToInt64(r[2], CultureInfo.InvariantCulture));
        var names = db.Query("SELECT id, name FROM binaries").ToDictionary(r => Convert.ToInt64(r[0], CultureInfo.InvariantCulture), r => Py.Str(r[1]));
        var executables = new HashSet<string>(StringComparer.Ordinal) { "bg3.exe", "bg3_dx11.exe" };
        var ordered = feats.Keys.Order().ToList();
        var gameIds = ordered.Where(f => info.TryGetValue(f, out var i) && i.Binary == gameId).ToList();
        HashSet<long> sources;
        if (source != null)
        {
            // one named build of the game itself (the macOS executable) as the source of names
            var src = db.Scalar("SELECT id FROM binaries WHERE name = ?", source) ?? throw new SymbolsException($"{source} is not loaded");
            sources = [Convert.ToInt64(src, CultureInfo.InvariantCulture)];
        }
        else
            sources = names.Where(p => !executables.Contains(p.Value) && p.Value.ToLowerInvariant().EndsWith(".dll", StringComparison.Ordinal)).Select(p => p.Key).ToHashSet();
        var toolkitIds = ordered.Where(f => info.TryGetValue(f, out var i) && sources.Contains(i.Binary)).ToList();
        Log($"features of {toolkitIds.Count} source and {gameIds.Count} game functions, {started.Elapsed.TotalSeconds:F0} s");
        var pairs = Match(feats, toolkitIds, gameIds, 0.10, 0.05, 0.0, jobs: jobs, log: Log);
        db.Execute("BEGIN");
        {
            var list = sources.ToList();
            db.Execute("DELETE FROM code_matches WHERE game_binary = ? AND toolkit_binary IN (" + string.Join(",", list.Select(_ => "?")) + ")",
                       [gameId, .. list.Cast<object?>()]);
            using var insert = db.CreateCommand();
            insert.CommandText = "INSERT INTO code_matches VALUES ($a,$b,$c,$d,$e,$f)";
            var ps = "abcdef".Select(c => insert.Parameters.Add("$" + c, SqliteType.Integer)).ToArray();
            ps[4].SqliteType = ps[5].SqliteType = SqliteType.Real;
            foreach (var p in pairs)
            {
                ps[0].Value = info[p.Toolkit].Binary;
                ps[1].Value = info[p.Toolkit].Rva;
                ps[2].Value = gameId;
                ps[3].Value = info[p.Game].Rva;
                ps[4].Value = Math.Round(p.Score, 4);
                ps[5].Value = Math.Round(p.Margin, 4);
                insert.ExecuteNonQuery();
            }
        }
        db.Execute("COMMIT");
        int shown = pairs.Count(p => p.Score >= Annotations.MinScore && p.Margin >= Annotations.MinMargin);
        s.Out.WriteLine($"{pairs.Count} pairs stored (score >= 0.10, margin >= 0.05); {shown} pass the default thresholds"
                        + $" (score >= {Py.Float(Annotations.MinScore)}, margin >= {Py.Float(Annotations.MinMargin)}); {started.Elapsed.TotalSeconds:F0} s");
        var sym = s.Symbols;
        if (sym == null)
            return 0;
        if (source != null)
        {
            CheckByName(s, db, sym, binary, source, pairs, info);
            return 0;
        }
        // The check: pairs the symbol database found through text constants alone. Pairs it found
        // along calls and virtual tables may descend from pseudo-code seeds, so they are not used here.
        var known = new Dictionary<(string, long), long>();
        var taken = new HashSet<long>();
        foreach (var r in sym.Query("SELECT d.name, m.toolkit_rva, m.game_rva, m.relation FROM matches m JOIN dlls d ON d.id = m.toolkit_dll"
                                    + " JOIN dlls g ON g.id = m.game_dll WHERE g.name = ? AND m.method = 'strings'", binary))
        {
            long gameRva = Convert.ToInt64(r[2], CultureInfo.InvariantCulture);
            taken.Add(gameRva);
            if (r[3] as string == "same")
                known[(Py.Str(r[0]), Convert.ToInt64(r[1], CultureInfo.InvariantCulture))] = gameRva;
        }
        var named = db.Query("SELECT id FROM functions WHERE name NOT LIKE 'FUN!_%' ESCAPE '!' AND name NOT LIKE 'thunk!_FUN!_%' ESCAPE '!'")
                      .Select(r => Convert.ToInt64(r[0], CultureInfo.InvariantCulture)).ToHashSet();
        s.Out.WriteLine("check against the pairs the symbol database found by text constants, and what is new to those:");
        foreach (var (minScore, minMargin) in new[] { (0.10, 0.05), (0.15, 0.10), (0.20, 0.15), (0.30, 0.20), (0.50, 0.20) })
        {
            int right = 0, wrong = 0, fresh = 0, freshNamed = 0, count = 0;
            foreach (var p in pairs)
            {
                if (p.Score < minScore || p.Margin < minMargin)
                    continue;
                count++;
                if (known.TryGetValue((names[info[p.Toolkit].Binary], info[p.Toolkit].Rva), out var expected))
                {
                    if (expected == info[p.Game].Rva)
                        right++;
                    else
                        wrong++;
                }
                if (!taken.Contains(info[p.Game].Rva))
                {
                    fresh++;
                    if (named.Contains(p.Toolkit))
                        freshNamed++;
                }
            }
            s.Out.WriteLine($"  score >= {minScore:F2}, margin >= {minMargin:F2}: {count} pairs; of the known ones {right} agree, {wrong} differ"
                            + $" ({100.0 * right / Math.Max(1, right + wrong):F1}%); game functions without a pair before: {fresh}, {freshNamed} of them get a name");
        }
        return 0;
    }

    /// <summary>Last component of a qualified C++ name, without template arguments and parameters.</summary>
    public static string Unqualified(string name)
    {
        int depth = 0;
        var b = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (c == '<')
                depth++;
            else if (c == '>')
                depth--;
            else if (depth == 0)
                b.Append(c);
        }
        var head = Py.Strip(b.ToString().Split('(')[0]);
        return head.Split(' ')[^1];
    }

    /// <summary>For a source that names its functions itself: a game function the Toolkit already
    /// named (through text constants, calls or virtual tables, not pseudo-code) must get the same
    /// name from the source.</summary>
    static void CheckByName(DecompSession s, SqliteConnection db, SqliteConnection sym, string binary, string source, List<Pair> pairs,
                            Dictionary<long, (long Binary, long Rva)> info)
    {
        var known = new Dictionary<long, string>();
        foreach (var r in sym.Query(
                     "SELECT m.game_rva, min(s.full_name) FROM matches m JOIN symbols s ON s.dll_id = m.toolkit_dll AND s.rva = m.toolkit_rva"
                     + " JOIN dlls g ON g.id = m.game_dll WHERE g.name = ? AND m.relation = 'same' AND m.method IN ('strings', 'vtable', 'calls', 'vtable user')"
                     + " GROUP BY m.game_rva", binary))
            known[Convert.ToInt64(r[0], CultureInfo.InvariantCulture)] = Unqualified(Py.Str(r[1]));
        var sourceName = new Dictionary<long, string>();
        foreach (var r in db.Query("SELECT f.id, f.name FROM functions f JOIN binaries b ON b.id = f.binary_id WHERE b.name = ?", source))
            sourceName[Convert.ToInt64(r[0], CultureInfo.InvariantCulture)] = Py.Str(r[1]);
        s.Out.WriteLine($"check against {known.Count} game functions the Toolkit names (same class and method name counts as agreement):");
        foreach (var (minScore, minMargin) in new[] { (0.10, 0.05), (0.15, 0.10), (0.20, 0.15), (0.30, 0.20), (0.50, 0.20), (0.70, 0.30) })
        {
            int count = 0, same = 0, method = 0, differ = 0;
            foreach (var p in pairs)
            {
                if (p.Score < minScore || p.Margin < minMargin)
                    continue;
                count++;
                if (!known.TryGetValue(info[p.Game].Rva, out var expected))
                    continue;
                var got = Unqualified(sourceName.GetValueOrDefault(p.Toolkit, ""));
                if (got == expected)
                    same++;
                else if (got.Split("::")[^1] == expected.Split("::")[^1])
                    method++;
                else
                    differ++;
            }
            int checkedCount = same + method + differ;
            s.Out.WriteLine($"  score >= {minScore:F2}, margin >= {minMargin:F2}: {count} pairs; of {checkedCount} checked {same} same name,"
                            + $" {method} same method name in another class, {differ} differ ({100.0 * (same + method) / Math.Max(1, checkedCount):F1}% agree)");
        }
    }

    /// <summary>`matches`: the pairs found by pseudo-code for Toolkit functions whose name contains
    /// the text, or for one game address.</summary>
    public static int Matches(DecompSession s, string text, bool all, int limit)
    {
        var db = s.Db;
        var m = CodeCommand.Address.Match(text);
        string where = m.Success ? "m.game_rva = ?" : "f.name LIKE ?";
        object? param = m.Success ? Py.Hex(m.Groups[2].Value) ?? -1 : "%" + text + "%";
        var rows = db.Query(
            "SELECT m.score, m.margin, g.name, m.game_rva, t.name, m.toolkit_rva, f.name, f.size, gf.size FROM code_matches m"
            + " JOIN binaries g ON g.id = m.game_binary JOIN binaries t ON t.id = m.toolkit_binary"
            + " JOIN functions f ON f.binary_id = m.toolkit_binary AND f.rva = m.toolkit_rva"
            + " LEFT JOIN functions gf ON gf.binary_id = m.game_binary AND gf.rva = m.game_rva"
            + $" WHERE {where} AND m.score >= ? AND m.margin >= ? ORDER BY m.score DESC LIMIT ?",
            param, all ? 0.0 : Annotations.MinScore, all ? 0.0 : Annotations.MinMargin, limit).ToList();
        foreach (var r in rows)
        {
            double score = Convert.ToDouble(r[0], CultureInfo.InvariantCulture), margin = Convert.ToDouble(r[1], CultureInfo.InvariantCulture);
            s.Out.WriteLine($"{score:F2} +{margin:F2}  {Py.Str(r[2])}+0x{Convert.ToInt64(r[3], CultureInfo.InvariantCulture):X} ({Py.Str(r[8])} b)  ~  "
                            + $"{Py.Str(r[4])}+0x{Convert.ToInt64(r[5], CultureInfo.InvariantCulture):X} ({Py.Str(r[7])} b)  {Py.Str(r[6])}");
        }
        if (rows.Count == 0)
            s.Err.WriteLine("nothing found" + (all ? "" : " (--all also shows pairs below the default thresholds)"));
        return 0;
    }
}
