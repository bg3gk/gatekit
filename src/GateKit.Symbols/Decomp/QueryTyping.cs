using System.Globalization;
using System.Text;

namespace GateKit.Symbols.Decomp;

/// <summary>One value of a column's form in a system's function, and what was decided of it.</summary>
/// <param name="Group">The group of the description the pointer's place stands for, or -1.</param>
/// <param name="Outcome"><see cref="QueryTyping.Typed"/>, or why the value is left alone.</param>
/// <param name="Type">The component the value points at, when one was determined.</param>
/// <param name="Global">A query of the system whose column gives that type.</param>
internal sealed record ColumnCase(string System, long Function, ColumnValue Value, int Group, string Outcome, string? Type, long? Global)
{
    /// <summary>True when the group was not read from the pointer's place (the index is not a
    /// stack local) and the type is the only one that fits among the groups the state points at.</summary>
    public bool ByFit { get; init; }
}

/// <summary>What <see cref="QueryTyping.Decide"/> found.</summary>
internal sealed class QueryTypingResult
{
    /// <summary>Every value whose base and index are each one stack local, whatever its form (for the rule's table).</summary>
    public List<(SystemQueries System, long Function, ColumnValue Value)> Values = [];
    /// <summary>The values of a column's form, each with its outcome.</summary>
    public List<ColumnCase> Cases = [];
    /// <summary>The stride check: cases whose column exists and is sized in some query of the
    /// system, and those whose stride is that size; the same for systems with exactly one query.</summary>
    public int Checked, Agree, OneQueryChecked, OneQueryAgree;
    /// <summary>Values typed with the pointer's place known: how many the rule without the place
    /// would type the same, and how many it would type differently (the rest it leaves alone).</summary>
    public int BlindChecked, BlindAgree, BlindDiffer;
}

/// <summary>
/// Decides which component a value taken from a query's column points at. The query's own
/// registration in the Windows build gives the component types of each group of its description
/// in order (<see cref="QueryRegistration"/>); the place of the pointer in the iterator's state
/// gives the group and the index it applies gives the column (<see cref="QueryColumns"/>); and
/// the stride the code multiplies by must be the ledger's size of that type - the check every
/// single case has to pass. A system may walk several queries and the text does not say which
/// iterator a local belongs to: a value is typed only when every query of the system has its
/// registration read and exactly one type fits among the k-th types of that group. A wrong type
/// is worse than none.
/// </summary>
internal static class QueryTyping
{
    public const string Typed = "typed";

    /// <summary>The registration of a system's only query, when it has one query and that one was read whole.</summary>
    public static QueryRegistration? OnlyQuery(SystemQueries q, QueryReading reading) =>
        q.OtherDescriptions == 0 && q.DistinctGlobals is { Count: 1 } one && reading.Registrations.TryGetValue(one[0], out var r) && r.Count == 1 && r[0].Whole ? r[0] : null;

    public static QueryTypingResult Decide(DecompSession s, QueryReading reading, IReadOnlyDictionary<string, long> sizes)
    {
        var result = new QueryTypingResult();
        long id = Sql.Long(s.Db.Scalar("SELECT id FROM binaries WHERE name = ?", reading.Target)) ?? throw new SymbolsException($"{reading.Target} is not loaded");
        var owners = reading.Systems.SelectMany(q => q.Functions.Select(f => (f, q.System))).GroupBy(p => p.f).ToDictionary(g => g.Key, g => g.Select(p => p.System).Distinct(StringComparer.Ordinal).Count());
        foreach (var q in reading.Systems)
        {
            var globals = q.DistinctGlobals;
            var registrations = globals.Select(g => reading.Registrations.TryGetValue(g, out var r) && r.Count == 1 && r[0].Whole ? r[0] : null).ToList();
            // every query the system walks must be known, or the one a value belongs to may be the unknown one
            bool all = registrations.Count > 0 && registrations.All(r => r != null) && q.OtherDescriptions == 0;
            var candidates = registrations.OfType<QueryRegistration>().ToList();
            var only = OnlyQuery(q, reading);
            foreach (long function in q.Functions)
            {
                if (s.Db.Scalar("SELECT c.text FROM functions f JOIN code c ON c.function_id = f.id WHERE f.binary_id = ? AND f.rva = ?", id, function) is not byte[] blob)
                    continue;
                foreach (var v in QueryColumns.Read(DecompStore.Decompress(blob)))
                {
                    result.Values.Add((q, function, v));
                    if (!QueryColumns.IsColumnForm(v.Form))
                        continue;
                    ColumnCase Case(int group, string outcome, string? type = null, long? global = null) => new(q.System, function, v, group, outcome, type, global);
                    if (owners[function] > 1)
                    {
                        result.Cases.Add(Case(-1, "the function is shared by two systems"));
                        continue;
                    }
                    // the k-th type of a group in every query of the system that has that column
                    List<(long Global, int Group, string Type)> Kth(IEnumerable<int> groups) =>
                        [.. groups.SelectMany(g => candidates.Select(r => (r.Global, Group: g, Type: r.Group(g).ElementAtOrDefault(v.Column)))).Where(c => c.Type != null).Select(c => (c.Global, c.Group, c.Type!))];
                    List<(long Global, int Group, string Type)> Fit(List<(long Global, int Group, string Type)> kth) =>
                        [.. kth.Where(c => sizes.TryGetValue(c.Type, out long size) && size == v.Value.Stride)];
                    // without the distance: any group the state keeps pointers of
                    var blind = Fit(Kth(QueryColumns.Groups.Values));
                    var blindTypes = blind.Select(c => c.Type).Distinct(StringComparer.Ordinal).ToList();
                    int group = -1;
                    if (v.Distance is long distance && !QueryColumns.Groups.TryGetValue(distance, out group))
                    {
                        result.Cases.Add(Case(-1, "the pointer is not at a known place of the state"));
                        continue;
                    }
                    if (candidates.Count == 0)
                    {
                        result.Cases.Add(Case(group, "no registration of the system's queries was read"));
                        continue;
                    }
                    var kth = Kth(v.Distance == null ? QueryColumns.Groups.Values : [group]);
                    if (kth.Count == 0)
                    {
                        result.Cases.Add(Case(group, "no query of the system has that column"));
                        continue;
                    }
                    var fit = v.Distance == null ? blind : Fit(kth);
                    if (v.Distance != null && kth.All(c => sizes.ContainsKey(c.Type)))
                    {
                        result.Checked++;
                        if (fit.Count > 0) result.Agree++;
                        if (only != null)
                        {
                            result.OneQueryChecked++;
                            if (fit.Count > 0) result.OneQueryAgree++;
                        }
                    }
                    var types = fit.Select(c => c.Type).Distinct(StringComparer.Ordinal).ToList();
                    // how the rule without the distance does where the distance is known
                    if (v.Distance != null && types.Count == 1 && all)
                    {
                        result.BlindChecked++;
                        if (blindTypes.Count == 1 && blindTypes[0] == types[0]) result.BlindAgree++;
                        else if (blindTypes.Count == 1) result.BlindDiffer++;
                    }
                    if (types.Count == 0)
                        result.Cases.Add(Case(group, kth.Any(c => !sizes.ContainsKey(c.Type)) ? "the column's type is not sized by the ledger" : "the stride is not the size of the column's type"));
                    else if (types.Count > 1)
                        result.Cases.Add(Case(group, "two columns of the system's queries give different types of that size"));
                    else if (!all)
                        result.Cases.Add(Case(fit[0].Group, "one type fits, but a query of the system has no registration read", types[0], fit[0].Global));
                    else
                        result.Cases.Add(Case(fit[0].Group, Typed, types[0], fit[0].Global) with { ByFit = v.Distance == null });
                }
            }
        }
        return result;
    }

    /// <summary>The typed values for the pass, one line each: function rva (hex), the stack offset
    /// of the pointer local (hex), the column, the stride, the structure, the query global (hex),
    /// the group. A value two lines would type differently is in neither.</summary>
    public static string Lines(QueryTypingResult result)
    {
        var b = new StringBuilder();
        foreach (var same in result.Cases.Where(c => c.Outcome == Typed)
                     .GroupBy(c => (c.Function, c.Value.Local, c.Value.Column, c.Value.Value.Stride)).OrderBy(g => g.Key.Function).ThenBy(g => g.Key.Local).ThenBy(g => g.Key.Column).ThenBy(g => g.Key.Stride))
        {
            if (same.Select(c => c.Type).Distinct(StringComparer.Ordinal).Count() != 1)
                continue;
            var c = same.First();
            b.Append(CultureInfo.InvariantCulture, $"{c.Function:X}\t{c.Value.Local:X}\t{c.Value.Column}\t{c.Value.Value.Stride}\t{c.Type}\t{c.Global:X}\t{c.Group}\n");
        }
        return b.ToString();
    }
}
