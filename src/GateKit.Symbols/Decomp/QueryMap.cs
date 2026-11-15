using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace GateKit.Symbols.Decomp;

/// <summary>A query global of a Windows build tied to the spec the macOS build names.</summary>
/// <param name="Rva">The global that holds the query's index.</param>
/// <param name="Spec">The spec (<see cref="QuerySpec.Name"/>).</param>
/// <param name="Systems">How many systems' updates say so.</param>
/// <param name="Round">0: by position where a system has as many globals as specs; later: by position between globals already tied.</param>
internal sealed record QueryGlobal(long Rva, string Spec, int Systems, int Round);

/// <summary>What tying the globals found, for the report and the tests.</summary>
internal sealed class QueryMapResult
{
    public List<QueryGlobal> Kept = [];
    /// <summary>Globals two systems tie to different specs: (global, the specs and how many systems say each).</summary>
    public List<(long Rva, List<(string Spec, int Systems)> Specs)> Disagree = [];
    /// <summary>Globals dropped because another global is tied to the same spec.</summary>
    public List<long> SharedSpec = [];
    /// <summary>Systems whose queries were lined up by position, per round.</summary>
    public List<int> SystemsUsed = [];
    /// <summary>Globals more than one system speaks of, and those of them every system agrees on.</summary>
    public int Checked, CheckedAgree;
    /// <summary>Statements (a system says a global is a spec) about the checked globals, and those that agree with the global's most frequent spec.</summary>
    public int Statements, StatementsAgree;
    /// <summary>Globals tied by several systems / by one, whose Windows registration stores the
    /// spec's types [0] or other types [1].</summary>
    public int[] RegisteredSeveral = new int[2], RegisteredOne = new int[2];
    /// <summary>Tied globals with no single registration to check against (kept, unchecked by it).</summary>
    public List<long> Unregistered = [];
    /// <summary>Tied globals dropped because their registration stores other types than the spec's.</summary>
    public List<QueryGlobal> RegistrationDiffers = [];
}

/// <summary>
/// Ties each query global of a Windows build to a spec of the macOS build. In the two builds of
/// one system's update the queries are used in the same order: where the Windows side reads as
/// many distinct globals as the macOS side mentions distinct specs, the n-th is the n-th. A global
/// that several systems use must get the same spec from each: that agreement is the measured
/// precision, and a global with two answers is dropped. Globals tied that way then anchor the
/// systems whose counts differ: between two anchors that stand in the same order on both sides,
/// as many unknown globals as unknown specs are again tied by position.
/// </summary>
internal static class QueryMap
{
    public const string Table = """
        -- A query global of a Windows build (the query's index): the spec the macOS build names
        -- for it (spec, its type lists as JSON, its tags, how many systems agree, the round) where
        -- one was tied, and what the build's own registration stores into the query's description
        -- (registered: the component types of each of its ten groups as JSON; counts: the ten
        -- sizes) where that was read whole (gatekit decomp queries map).
        CREATE TABLE IF NOT EXISTS query_globals (binary_id INTEGER, rva INTEGER, spec TEXT, lists TEXT, tags TEXT, systems INTEGER, round INTEGER, registered TEXT, counts TEXT);
        CREATE INDEX IF NOT EXISTS query_globals_rva ON query_globals(binary_id, rva);
        """;

    /// <summary>The longest run of anchors that stand in the same order in both lists: pairs (place among the globals, place among the specs).</summary>
    static List<(int G, int S)> Anchors(List<long> globals, List<string> specs, IReadOnlyDictionary<long, string> known)
    {
        var place = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < specs.Count; i++)
            place[specs[i]] = i;
        var candidates = new List<(int G, int S)>();
        for (int i = 0; i < globals.Count; i++)
            if (known.TryGetValue(globals[i], out var spec) && place.TryGetValue(spec, out int at))
                candidates.Add((i, at));
        // longest increasing run of S (the candidates are in the order of G)
        var best = new int[candidates.Count];
        var before = new int[candidates.Count];
        int end = -1;
        for (int i = 0; i < candidates.Count; i++)
        {
            best[i] = 1;
            before[i] = -1;
            for (int k = 0; k < i; k++)
                if (candidates[k].S < candidates[i].S && best[k] + 1 > best[i])
                    (best[i], before[i]) = (best[k] + 1, k);
            if (end < 0 || best[i] > best[end])
                end = i;
        }
        var run = new List<(int, int)>();
        for (int i = end; i >= 0; i = before[i])
            run.Add(candidates[i]);
        run.Reverse();
        return run;
    }

    /// <summary>What one system says: (global, spec) by position, over the whole lists when the
    /// counts agree, else inside every gap between anchors that holds as many of the one as of the other.</summary>
    static List<(long Global, string Spec)> Statements(SystemQueries q, IReadOnlyDictionary<long, string> known, bool anchored)
    {
        var globals = q.DistinctGlobals;
        var specs = q.DistinctSpecs;
        var said = new List<(long, string)>();
        if (globals.Count == 0 || specs.Count == 0)
            return said;
        if (!anchored)
        {
            if (globals.Count == specs.Count)
                for (int i = 0; i < globals.Count; i++)
                    said.Add((globals[i], specs[i]));
            return said;
        }
        var anchors = Anchors(globals, specs, known);
        if (anchors.Count == 0)
            return said;
        anchors.Add((globals.Count, specs.Count));
        int g = 0, s = 0;
        foreach (var (ag, asp) in anchors)
        {
            if (ag - g == asp - s)
                for (int i = 0; i < ag - g; i++)
                    said.Add((globals[g + i], specs[s + i]));
            if (ag < globals.Count)
                said.Add((globals[ag], specs[asp]));
            (g, s) = (ag + 1, asp + 1);
        }
        return said;
    }

    public static QueryMapResult Map(QueryReading reading, int rounds)
    {
        var result = new QueryMapResult();
        var known = new Dictionary<long, string>();
        var roundOf = new Dictionary<long, int>();
        Dictionary<long, Dictionary<string, int>> votes = [];
        for (int round = 0; round <= rounds; round++)
        {
            votes = [];
            int used = 0;
            foreach (var q in reading.Systems)
            {
                // a system whose counts agree speaks for itself; the others only through anchors
                bool equal = q.DistinctGlobals.Count == q.DistinctSpecs.Count;
                var said = Statements(q, known, anchored: !equal);
                if (said.Count == 0 || (round == 0 && !equal))
                    continue;
                used++;
                foreach (var (global, spec) in said)
                {
                    if (!votes.TryGetValue(global, out var bySpec))
                        votes[global] = bySpec = new Dictionary<string, int>(StringComparer.Ordinal);
                    bySpec[spec] = bySpec.GetValueOrDefault(spec) + 1;
                }
            }
            result.SystemsUsed.Add(used);
            var next = new Dictionary<long, string>();
            foreach (var (global, bySpec) in votes)
                if (bySpec.Count == 1)
                    next[global] = bySpec.Keys.First();
            // one spec is one query: two globals tied to it cannot both be right
            foreach (var shared in next.GroupBy(p => p.Value, StringComparer.Ordinal).Where(g => g.Count() > 1).SelectMany(g => g.Select(p => p.Key)).ToList())
                next.Remove(shared);
            foreach (long global in next.Keys)
                roundOf.TryAdd(global, round);
            bool same = next.Count == known.Count && next.All(p => known.TryGetValue(p.Key, out var spec) && spec == p.Value);
            known = next;
            if (same)
                break;
        }

        var agreed = votes.Where(v => v.Value.Count == 1).ToDictionary(v => v.Key, v => v.Value.First());
        var sharedSpecs = agreed.GroupBy(p => p.Value.Key, StringComparer.Ordinal).Where(g => g.Count() > 1).SelectMany(g => g.Select(p => p.Key)).ToHashSet();
        foreach (var (global, bySpec) in votes.OrderBy(v => v.Key))
        {
            int total = bySpec.Values.Sum();
            if (total > 1)
            {
                result.Checked++;
                result.Statements += total;
                result.StatementsAgree += bySpec.Values.Max();
                if (bySpec.Count == 1)
                    result.CheckedAgree++;
            }
            if (bySpec.Count > 1)
                result.Disagree.Add((global, bySpec.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Select(p => (p.Key, p.Value)).ToList()));
            else if (sharedSpecs.Contains(global))
                result.SharedSpec.Add(global);
            else
                result.Kept.Add(new QueryGlobal(global, bySpec.Keys.First(), total, roundOf.GetValueOrDefault(global)));
        }

        // the independent check: the Windows build's own registration of the query stores the
        // component types into its description, and they must be the types of the spec
        foreach (var g in result.Kept.ToList())
        {
            if (!reading.Registrations.TryGetValue(g.Rva, out var registered) || registered.Count != 1)
            {
                result.Unregistered.Add(g.Rva);
                continue;
            }
            bool same = SameTypes(reading.Specs.GetValueOrDefault(g.Spec) ?? QuerySpec.Parse(g.Spec), registered[0]);
            var count = g.Systems > 1 ? result.RegisteredSeveral : result.RegisteredOne;
            count[same ? 0 : 1]++;
            if (!same)
            {
                result.Kept.Remove(g);
                result.RegistrationDiffers.Add(g);
            }
        }
        return result;
    }

    /// <summary>True when the spec's lists hold exactly the component types the registration stores.</summary>
    internal static bool SameTypes(QuerySpec? spec, QueryRegistration registration) =>
        spec != null && spec.Types.Select(PairCommand.Normalize).ToHashSet(StringComparer.Ordinal).SetEquals(registration.Types.Select(PairCommand.Normalize));

    public static void Store(SqliteConnection db, string binary, QueryReading reading, QueryMapResult result)
    {
        long id = Sql.Long(db.Scalar("SELECT id FROM binaries WHERE name = ?", binary)) ?? throw new SymbolsException($"{binary} is not loaded");
        db.Execute(Table);
        // a table of an earlier shape is made again: its rows are all replaced below anyway
        if (db.Query("SELECT name FROM pragma_table_info('query_globals')").Select(r => Py.Str(r[0])).ToList() is var columns && !columns.Contains("registered"))
        {
            db.Execute("DROP TABLE query_globals");
            db.Execute(Table);
        }
        db.Execute("BEGIN");
        db.Execute("DELETE FROM query_globals WHERE binary_id = ?", id);
        var kept = result.Kept.ToDictionary(g => g.Rva);
        var registered = reading.Registrations.Where(r => r.Value.Count == 1 && r.Value[0].Whole).ToDictionary(r => r.Key, r => r.Value[0]);
        foreach (long rva in kept.Keys.Union(registered.Keys).Order())
        {
            var g = kept.GetValueOrDefault(rva);
            var spec = g == null ? null : reading.Specs.GetValueOrDefault(g.Spec) ?? QuerySpec.Parse(g.Spec);
            var r = registered.GetValueOrDefault(rva);
            db.Execute("INSERT INTO query_globals VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)", id, rva, g?.Spec, spec == null ? null : JsonSerializer.Serialize(spec.Lists),
                       spec == null ? null : string.Join(",", spec.Tags), g?.Systems ?? 0, g?.Round,
                       r == null ? null : JsonSerializer.Serialize(Enumerable.Range(0, 10).Select(r.Group)), r == null ? null : string.Join(",", r.Counts!.Select(n => n.ToString(CultureInfo.InvariantCulture))));
        }
        db.Execute("COMMIT");
    }
}
