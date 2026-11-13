using GateKit.Symbols.Pe;
using GateKit.Symbols.ScriptExtender;
using Microsoft.Data.Sqlite;

namespace GateKit.Symbols.Build;

public static partial class SymbolBuilder
{
    /// <summary>A row of the matches table.</summary>
    internal sealed record MatchRow(int ToolkitDll, uint ToolkitRva, int GameDll, uint GameRva, long Evidence, string? Sample, string Method,
        long? ToolkitSize, long? GameSize, string? Relation);

    /// <summary>
    /// Call graphs, then Toolkit function &lt;-&gt; game function: by text constants, by pseudo-code pairs
    /// (when decomp.sqlite has them), then in rounds along direct calls, through virtual tables and by
    /// the users of matched tables. Writes calls, vtab_matches and matches; returns the matches.
    /// </summary>
    static List<MatchRow> Match(BuildOptions options, List<FileResult> files, List<ExportRow> rows,
        Dictionary<long, (int Dll, List<uint> Slots)> allTables, Dictionary<long, uint> tableRva, Dictionary<long, string> tableName,
        Dictionary<string, Dictionary<string, HashSet<(int, uint)>>> byText, SqliteConnection db, TextWriter log)
    {
        var byId = files.ToDictionary(f => f.Id);
        var graphs = files.Where(f => f.Folded != null).ToDictionary(f => f.Id);
        var exportAt = new Dictionary<(string, string), (int, uint)>();
        // later rows win, as in a Python dict built in order
        foreach (var r in rows)
            if (r.Rva != null && r.Name != null)
                exportAt[(byId[r.Dll].NameLower, r.Name)] = (r.Dll, r.Rva.Value);

        // Every callee becomes a function of an indexed binary or an import from anywhere else;
        // thunks are replaced by what they jump to.
        Node Resolve(int dll, Callee callee, int depth = 0)
        {
            uint rva = callee.Rva;
            if (callee.IsImport)
            {
                if (!exportAt.TryGetValue((callee.Dll!, callee.Name!), out var target))
                    return Node.Imported(callee.Name!);
                (dll, rva) = target;
            }
            if (graphs.TryGetValue(dll, out var g) && g.Alias!.TryGetValue(rva, out var alias) && depth < 4)
                return Resolve(dll, alias, depth + 1);
            return Node.Function(dll, rva);
        }
        Node Canon(int dll, uint rva) => Resolve(dll, Callee.Function(rva));

        var callees = new Dictionary<Node, List<Node>>();
        using (var tx = db.BeginTransaction())
        {
            using var ins = db.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT INTO calls VALUES ($1,$2,$3,$4,$5,$6)";
            for (int i = 1; i <= 6; i++)
                ins.Parameters.Add(new SqliteParameter("$" + i, null));
            foreach (var (dll, f) in graphs)
                foreach (var (func, targets) in f.Folded!)
                {
                    var resolved = targets.Select(t => Resolve(dll, t)).ToList();
                    callees[Node.Function(dll, func)] = resolved;
                    var counts = new Dictionary<Node, int>();
                    var order = new List<Node>();
                    foreach (var c in resolved)
                        if (counts.TryGetValue(c, out var n)) counts[c] = n + 1;
                        else { counts[c] = 1; order.Add(c); }
                    foreach (var c in order)
                        if (c.IsImport) Run(ins, dll, func, null, null, c.Import, counts[c]);
                        else Run(ins, dll, func, c.Dll, c.Rva, null, counts[c]);
                }
            tx.Commit();
        }
        // The calls of each part, for split functions: two builds do not always split a function the
        // same way, so the whole call lists may differ where single parts agree.
        var calleesFirst = new Dictionary<Node, List<List<Node>>>();
        foreach (var f in files)
            foreach (var (func, lists) in f.FirstParts)
                calleesFirst[Node.Function(f.Id, func)] = lists.Select(l => l.Select(t => Resolve(f.Id, t)).ToList()).ToList();

        var gameIds = files.Where(f => f.Origin == "game").Select(f => f.Id).ToList();
        var output = new List<MatchRow>();
        var allTablePairs = new List<(long, long, int)>();
        foreach (var gameId in gameIds)
        {
            var game = byId[gameId];
            (long? A, long? B, string? Relation) Relation(Node tk, uint g)
            {
                long? a = byId.TryGetValue(tk.Dll, out var tf) && tf.Sizes.TryGetValue(tk.Rva, out var x) ? x : null;
                long? b = game.Sizes.TryGetValue(g, out var y) ? y : null;
                if (a is null or 0 || b is null or 0)
                    return (a, b, null);
                return (a, b, b > 3 * a + 64 ? "inlined" : a > 3 * b + 64 ? "partial" : "same");
            }
            MatchRow Row(Node tk, uint g, long evidence, string? sample, string method)
            {
                var (a, b, rel) = Relation(tk, g);
                return new MatchRow(tk.Dll, tk.Rva, gameId, g, evidence, sample, method, a, b, rel);
            }

            // By text constants: a constant that exactly one function refers to on each side ties the
            // two together; pairs that conflict with another pair are dropped.
            var pairs = new Dictionary<(Node, uint), int>();
            var sample = new Dictionary<(Node, uint), string>();
            var gameTexts = byText["game"];
            foreach (var (text, users) in byText["toolkit"])
            {
                if (users.Count != 1 || !gameTexts.TryGetValue(text, out var thereAll))
                    continue;
                (int, uint)? there = null;
                int count = 0;
                foreach (var u in thereAll)
                    if (u.Item1 == gameId) { there = u; count++; }
                if (count != 1)
                    continue;
                var (ud, uf) = users.First();
                var tk = Canon(ud, uf);
                var gNode = Canon(gameId, there!.Value.Item2);
                if (tk.IsImport || gNode.IsImport)
                    continue;
                var key = (tk, gNode.Rva);
                pairs[key] = pairs.GetValueOrDefault(key) + 1;
                sample.TryAdd(key, text.Length > 200 ? text[..200] : text);
            }
            var left = new Dictionary<Node, int>();
            var right = new Dictionary<uint, int>();
            foreach (var (tk, g) in pairs.Keys)
            {
                left[tk] = left.GetValueOrDefault(tk) + 1;
                right[g] = right.GetValueOrDefault(g) + 1;
            }
            var seeds = new Dictionary<Node, uint>();
            foreach (var (tk, g) in pairs.Keys)
                if (left[tk] == 1 && right[g] == 1)
                    seeds[tk] = g;
            var takenGame = new HashSet<uint>(seeds.Values);
            var outRows = seeds.Select(kv => Row(kv.Key, kv.Value, pairs[(kv.Key, kv.Value)], sample[(kv.Key, kv.Value)], "strings")).ToList();
            if (options.CheckMatching)
                CheckMatching(seeds, callees, gameId, calleesFirst, log);
            var tkTables = new Dictionary<long, List<Node>>();
            var gameTables = new Dictionary<long, List<uint>>();
            foreach (var (tid, (dll, slots)) in allTables)
            {
                if (!gameIds.Contains(dll))
                    tkTables[tid] = slots.Select(s => Canon(dll, s)).ToList();
                else if (dll == gameId)
                    gameTables[tid] = slots.Select(s => Canon(gameId, s) is { IsImport: false } n ? n.Rva : s).ToList();
            }
            // Pairs of similar size are trusted as seeds: a Toolkit function that was inlined into its
            // caller in the game must not drag the caller into a table or a call chain.
            var known = seeds.Where(kv => Relation(kv.Key, kv.Value).Relation == "same").ToDictionary(kv => kv.Key, kv => kv.Value);
            if (options.CheckMatching)
                VtableMatcher.Check(tkTables, gameTables, known, log);
            var have = new Dictionary<Node, uint>(seeds);
            // Pairs `decomp match` found by comparing pseudo-code: further seeds at strict thresholds,
            // so that calls and virtual tables are followed from them too (method 'pseudo-code').
            if (!options.NoPseudoCode)
            {
                var byName = new Dictionary<string, int>();
                foreach (var f in files)
                    byName[f.NameLower] = f.Id;
                var taken = new HashSet<uint>(have.Values);
                int added = 0;
                foreach (var (binary, toolkitRva, gRaw, score, margin) in CodePairs(options.DecompDb ?? SymbolPaths.DecompDb, game.NameLower, 0.30, 0.20, log))
                {
                    if (!byName.TryGetValue(binary, out var tdll))
                        continue;
                    var tk = Canon(tdll, toolkitRva);
                    var gNode = Canon(gameId, gRaw);
                    if (tk.IsImport || gNode.IsImport || have.ContainsKey(tk) || taken.Contains(gNode.Rva))
                        continue;
                    var g = gNode.Rva;
                    have[tk] = g;
                    taken.Add(g);
                    outRows.Add(Row(tk, g, (long)Math.Round(score * 100, MidpointRounding.ToEven),
                        $"score {score.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}, lead {margin.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}", "pseudo-code"));
                    if (Relation(tk, g).Relation == "same")
                        known[tk] = g;
                    added++;
                }
                log.WriteLine($"  {game.NameLower}: {added} seeds from pseudo-code");
            }
            var tablePairs = new Dictionary<long, (long Game, int Support)>();
            for (int round = 0; round < 6; round++)
            {
                int added = 0;
                // 1. along direct calls
                foreach (var (tk, (g, votes, parent)) in Propagate(known, callees, gameId, calleesFirst))
                    if (!have.ContainsKey(tk) && !tk.IsImport)
                    {
                        have[tk] = g;
                        outRows.Add(Row(tk, g, votes, parent, "calls"));
                        if (Relation(tk, g).Relation == "same")
                            known[tk] = g;
                        added++;
                    }
                // 2. through virtual tables
                var (tables, byTable) = VtableMatcher.Match(tkTables, gameTables, known);
                foreach (var (k, v) in tables)
                    tablePairs[k] = v;
                foreach (var (tk, (g, votes, tid)) in byTable)
                    if (!have.ContainsKey(tk) && !tk.IsImport)
                    {
                        have[tk] = g;
                        outRows.Add(Row(tk, g, votes, tableName.GetValueOrDefault(tid, $"table {tid}"), "vtable"));
                        if (Relation(tk, g).Relation != "inlined")
                            known[tk] = g;
                        added++;
                    }
                // 3. the functions that take the address of a matched table (constructor, destructor):
                //    when exactly one function does so on each side, they are the same function.
                foreach (var (tid, (gid, support)) in tables)
                {
                    int dll = allTables[tid].Dll;
                    var a = byId[dll].LeaRefs.GetValueOrDefault(tableRva[tid]);
                    var b = game.LeaRefs.GetValueOrDefault(tableRva[gid]);
                    if (a is not { Count: 1 } || b is not { Count: 1 })
                        continue;
                    var tk = Canon(dll, a.First());
                    var gNode = Canon(gameId, b.First());
                    if (tk.IsImport || gNode.IsImport || have.ContainsKey(tk) || takenGame.Contains(gNode.Rva))
                        continue;
                    var g = gNode.Rva;
                    have[tk] = g;
                    takenGame.Add(g);
                    outRows.Add(Row(tk, g, support, tableName.GetValueOrDefault(tid, $"table {tid}"), "vtable user"));
                    if (Relation(tk, g).Relation == "same")
                        known[tk] = g;
                    added++;
                }
                log.WriteLine($"  {game.NameLower} round {round + 1}: {added} new pairs, {tablePairs.Count} tables");
                if (added == 0)
                    break;
            }
            allTablePairs.AddRange(tablePairs.Select(kv => (kv.Key, kv.Value.Game, kv.Value.Support)));
            using var tx = db.BeginTransaction();
            using var insT = db.CreateCommand();
            insT.Transaction = tx;
            insT.CommandText = "INSERT INTO vtab_matches VALUES ($1,$2,$3)";
            for (int i = 1; i <= 3; i++)
                insT.Parameters.Add(new SqliteParameter("$" + i, null));
            foreach (var (tid, (gid, n)) in tablePairs)
                Run(insT, tid, gid, n);
            using var insM = db.CreateCommand();
            insM.Transaction = tx;
            insM.CommandText = "INSERT INTO matches VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)";
            for (int i = 1; i <= 10; i++)
                insM.Parameters.Add(new SqliteParameter("$" + i, null));
            foreach (var r in outRows)
                Run(insM, r.ToolkitDll, r.ToolkitRva, r.GameDll, r.GameRva, r.Evidence, r.Sample, r.Method, r.ToolkitSize, r.GameSize, r.Relation);
            tx.Commit();
            output.AddRange(outRows);
        }
        return output;
    }

    /// <summary>(toolkit binary in lower case, toolkit rva, game rva, score, margin) from the table
    /// `decomp match` wrote, best first; empty when there is none.</summary>
    static List<(string, uint, uint, double, double)> CodePairs(string path, string game, double minScore, double minMargin, TextWriter log)
    {
        var result = new List<(string, uint, uint, double, double)>();
        if (!File.Exists(path))
            return result;
        try
        {
            using var d = Sql.OpenReadOnly(path);
            foreach (var r in d.Query(
                         "SELECT t.name, m.toolkit_rva, m.game_rva, m.score, m.margin FROM code_matches m JOIN binaries t ON t.id = m.toolkit_binary"
                         + " JOIN binaries g ON g.id = m.game_binary WHERE lower(g.name) = ? AND m.score >= ? AND m.margin >= ? ORDER BY m.score DESC",
                         game, minScore, minMargin))
                result.Add((((string)r[0]!).ToLowerInvariant(), (uint)Convert.ToInt64(r[1]), (uint)Convert.ToInt64(r[2]), Convert.ToDouble(r[3]), Convert.ToDouble(r[4])));
        }
        catch (SqliteException e)
        {
            log.WriteLine($"  pairs by pseudo-code not read from {path}: {e.Message}");
            result.Clear();
        }
        return result;
    }

    /// <summary>
    /// Extends toolkit->game function pairs along calls. For a matched pair whose two functions make
    /// the same number of direct calls, with every import and every already matched callee in the
    /// same position, the n-th callee of one is taken as the n-th callee of the other. A new pair is
    /// accepted when no other candidate competes for either of its functions. A function split into
    /// parts is compared whole; when the whole call lists do not line up, its parts are compared one
    /// by one in address order. Returns {toolkit: (game, votes, via)} in the order found.
    /// </summary>
    internal static List<(Node Tk, (uint Game, int Votes, string Via) Hit)> Propagate(IReadOnlyDictionary<Node, uint> seeds,
        Dictionary<Node, List<Node>> callees, int gameId, Dictionary<Node, List<List<Node>>>? first = null, int rounds = 40)
    {
        var forward = new Dictionary<Node, uint>(seeds);
        var backward = new Dictionary<uint, Node>();
        foreach (var (tk, g) in seeds)
            backward[g] = tk;
        var found = new List<(Node, (uint, int, string))>();
        first ??= [];

        List<(Node, uint)>? Aligned(List<Node>? a, List<Node>? b)
        {
            if (a == null || b == null || a.Count == 0 || a.Count != b.Count)
                return null;
            var fresh = new List<(Node, uint)>();
            for (int i = 0; i < a.Count; i++)
            {
                var x = a[i];
                var y = b[i];
                if (x.IsImport != y.IsImport)
                    return null;
                if (x.IsImport)
                {
                    if (x.Import != y.Import)
                        return null;
                    continue;
                }
                if (y.Dll != gameId)
                    return null;
                bool isKnown = forward.TryGetValue(x, out var knownG);
                if (isKnown || backward.ContainsKey(y.Rva))
                {
                    if (!isKnown || knownG != y.Rva)
                        return null;
                    continue;
                }
                fresh.Add((x, y.Rva));
            }
            return fresh;
        }

        var frontier = seeds.Select(kv => (kv.Key, kv.Value)).ToList();
        var waiting = new HashSet<(Node, uint)>();      // matched pairs whose callees were contested
        for (int r = 0; r < rounds; r++)
        {
            var votes = new Dictionary<(Node, uint), int>();
            var via = new Dictionary<(Node, uint), string>();
            var parents = new Dictionary<(Node, uint), HashSet<(Node, uint)>>();
            var visit = new HashSet<(Node, uint)>(frontier);
            visit.UnionWith(waiting);
            foreach (var (tk, g) in visit)
            {
                var gn = Node.Function(gameId, g);
                var a = callees.GetValueOrDefault(tk);
                var b = callees.GetValueOrDefault(gn);
                var fresh = Aligned(a, b);
                if (fresh == null && (first.ContainsKey(tk) || first.ContainsKey(gn)))
                {
                    fresh = [];
                    var pa = first.TryGetValue(tk, out var fa) ? fa : [a!];
                    var pb = first.TryGetValue(gn, out var fb) ? fb : [b!];
                    for (int i = 0; i < Math.Min(pa.Count, pb.Count); i++)
                        fresh.AddRange(Aligned(pa[i], pb[i]) ?? []);
                }
                if (fresh == null)
                    continue;
                foreach (var pair in fresh.Distinct())
                {
                    votes[pair] = votes.GetValueOrDefault(pair) + 1;
                    via.TryAdd(pair, $"called from game+0x{g:X}");
                    if (!parents.TryGetValue(pair, out var ps))
                        parents[pair] = ps = [];
                    ps.Add((tk, g));
                }
            }
            // A pair is accepted when nothing competes for either of its functions, or when it has
            // more votes than every competitor. The parents of the rest wait for a later round.
            var bestLeft = new Dictionary<Node, List<int>>();
            var bestRight = new Dictionary<uint, List<int>>();
            foreach (var ((tk, g), n) in votes)
            {
                if (!bestLeft.TryGetValue(tk, out var l)) bestLeft[tk] = l = [];
                l.Add(n);
                if (!bestRight.TryGetValue(g, out var rr)) bestRight[g] = rr = [];
                rr.Add(n);
            }
            static bool UniqueMax(List<int> list, int n) => n == list.Max() && list.Count(v => v == n) == 1;
            frontier = [];
            waiting = [];
            foreach (var ((tk, g), n) in votes)
            {
                if (UniqueMax(bestLeft[tk], n) && UniqueMax(bestRight[g], n) && !forward.ContainsKey(tk) && !backward.ContainsKey(g))
                {
                    forward[tk] = g;
                    backward[g] = tk;
                    found.Add((tk, (g, n, via[(tk, g)])));
                    frontier.Add((tk, g));
                }
                else
                    waiting.UnionWith(parents[(tk, g)]);
            }
            if (frontier.Count == 0)
                break;
        }
        return found;
    }

    /// <summary>Hides a fifth of the text-matched pairs, runs the call matching on the rest and reports
    /// how many of the hidden functions it finds again and how many of those it gets right.</summary>
    static void CheckMatching(Dictionary<Node, uint> seeds, Dictionary<Node, List<Node>> callees, int gameId,
        Dictionary<Node, List<List<Node>>> first, TextWriter log)
    {
        var items = seeds.OrderBy(kv => kv.Key.Dll).ThenBy(kv => kv.Key.Rva).ToList();
        var hidden = new Dictionary<Node, uint>();
        for (int i = 0; i < items.Count; i += 5)
            hidden[items[i].Key] = items[i].Value;
        var kept = items.Where(kv => !hidden.ContainsKey(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        var found = Propagate(kept, callees, gameId, first).ToDictionary(x => x.Tk, x => x.Hit);
        var again = hidden.Keys.Where(found.ContainsKey).ToList();
        int right = again.Count(tk => found[tk].Game == hidden[tk]);
        var taken = found.Values.Select(v => v.Game).ToHashSet();
        int wrongSide = hidden.Count(kv => !found.ContainsKey(kv.Key) && taken.Contains(kv.Value));
        log.WriteLine($"  matching check: {hidden.Count} pairs hidden, {again.Count} found again by calls, {right} of them correct"
                      + $" ({100.0 * right / Math.Max(1, again.Count):F1}%); {wrongSide} hidden game functions were given to another function;"
                      + $" {found.Count} functions matched by calls in this run");
    }

    /// <summary>Fields from the bodies of trivial accessors; the game's offset where the accessor was found in the game.</summary>
    static void WriteAccessors(SqliteConnection db, List<FileResult> files,
        List<ExportRow> rows, List<(int Dll, uint? Rva, string Kind, string FullName, string Namespace, string Class, string Method, string Signature)> symbols,
        List<MatchRow> matches)
    {
        var pes = files.Where(f => f.Analysed && f.Pe != null).ToDictionary(f => f.Id);
        var accRows = new List<object?[]>();
        for (int i = 0; i < symbols.Count; i++)
        {
            var s = symbols[i];
            if (s.Kind != "method" || s.Class.Length == 0 || s.Rva is not uint rva)
                continue;
            var named = Accessors.FieldName(s.Method);
            if (named == null || !pes.TryGetValue(s.Dll, out var f) || !f.Sizes.TryGetValue(rva, out var size) || size == 0 || size > 32)
                continue;
            int o = f.Pe!.Offset(rva);
            var got = o >= 0 ? Accessors.Decode(f.Pe.Data.AsSpan(o, (int)Math.Min(size, f.Pe.Data.Length - o))) : null;
            if (got == null)
                continue;
            var (kind, offset, width, bit) = got.Value;
            if ((named.Value.Kind == "set") != (kind == "set"))
                continue;
            var rtype = kind != "set" ? Accessors.ReturnType(s.Signature) : null;
            accRows.Add([i + 1, s.Dll, (s.Namespace.Length > 0 ? s.Namespace + "::" : "") + s.Class, named.Value.Field, kind, offset, width,
                rtype, null, null, null, rva, bit]);
        }
        var gameOf = new Dictionary<(int, uint), List<(int, uint)>>();
        foreach (var m in matches)
            if (m.Relation is "same" or null)
            {
                if (!gameOf.TryGetValue((m.ToolkitDll, m.ToolkitRva), out var l))
                    gameOf[(m.ToolkitDll, m.ToolkitRva)] = l = [];
                l.Add((m.GameDll, m.GameRva));
            }
        foreach (var row in accRows)
        {
            foreach (var (gDll, gRva) in gameOf.GetValueOrDefault(((int)row[1]!, (uint)row[11]!)) ?? [])
            {
                if (!pes.TryGetValue(gDll, out var g) || !g.Sizes.TryGetValue(gRva, out var size) || size == 0 || size > 32)
                    continue;
                int o = g.Pe!.Offset(gRva);
                var got = o >= 0 ? Accessors.Decode(g.Pe.Data.AsSpan(o, (int)Math.Min(size, g.Pe.Data.Length - o))) : null;
                if (got != null && got.Value.Kind == (string)row[4]!)
                {
                    row[8] = gDll;
                    row[9] = gRva;
                    row[10] = got.Value.Offset;
                    break;
                }
            }
        }
        using var tx = db.BeginTransaction();
        using var ins = Insert(db, tx, "accessors", 12);
        foreach (var r in accRows)
            Run(ins, r[0], r[1], r[2], r[3], r[4], r[5], r[6], r[7], r[8], r[9], r[10], r[12]);
        tx.Commit();
    }

    static void WriteLayouts(SqliteConnection db, string bg3se)
    {
        using var tx = db.BeginTransaction();
        using var insType = db.CreateCommand();
        insType.Transaction = tx;
        insType.CommandText = "INSERT INTO se_types (id, name, namespace, ext_name, engine_class, kind, base, file, line, size) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)";
        for (int i = 1; i <= 10; i++)
            insType.Parameters.Add(new SqliteParameter("$" + i, null));
        using var insField = Insert(db, tx, "se_fields", 9);
        long id = 0;
        foreach (var t in SeLayouts.ReadHeaders(bg3se))
        {
            Run(insType, ++id, t.Name, t.Namespace, t.ExtName, t.EngineClass, t.Kind, t.Base, t.File, t.Line, t.Size);
            for (int i = 0; i < t.Fields.Count; i++)
            {
                var f = t.Fields[i];
                Run(insField, id, i, f.Name, f.Type, f.Legacy, f.Hidden, f.Offset, f.Size, f.Checked);
            }
        }
        using var insEnum = db.CreateCommand();
        insEnum.Transaction = tx;
        insEnum.CommandText = "INSERT INTO enums (id, name, namespace, underlying, bitmask, file) VALUES ($1,$2,$3,$4,$5,$6)";
        for (int i = 1; i <= 6; i++)
            insEnum.Parameters.Add(new SqliteParameter("$" + i, null));
        using var insValue = Insert(db, tx, "enum_values", 3);
        id = 0;
        foreach (var e in SeLayouts.ReadEnums(bg3se))
        {
            Run(insEnum, ++id, e.Name, e.Namespace, e.Underlying, e.Bitmask, e.File);
            foreach (var (label, value) in e.Values)
                Run(insValue, id, label, value);
        }
        using var insClass = db.CreateCommand();
        insClass.Transaction = tx;
        insClass.CommandText = "INSERT OR IGNORE INTO lua_classes VALUES ($1,$2)";
        insClass.Parameters.Add(new SqliteParameter("$1", null));
        insClass.Parameters.Add(new SqliteParameter("$2", null));
        using var insLua = Insert(db, tx, "lua_fields", 3);
        foreach (var c in SeLayouts.ReadLua(bg3se))
        {
            Run(insClass, c.Name, c.Parent);
            foreach (var (field, type) in c.Fields)
                Run(insLua, c.Name, field, type);
        }
        tx.Commit();
    }
}
