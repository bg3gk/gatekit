namespace GateKit.Symbols.Build;

/// <summary>
/// A function in the call graph: a function of an indexed binary (Dll, Rva), or an import from
/// any other DLL (Import set, Dll = 0).
/// </summary>
public readonly record struct Node(int Dll, uint Rva, string? Import)
{
    public bool IsImport => Import != null;
    public static Node Function(int dll, uint rva) => new(dll, rva, null);
    public static Node Imported(string name) => new(0, 0, name);
}

/// <summary>
/// Matching virtual tables of the Toolkit with virtual tables of the game. The game is linked with
/// heavy inlining and the Toolkit DLLs are not, so the body of the same function looks different
/// on the two sides; virtual methods are the exception: their address sits in a table, so each
/// one exists as a function, and the tables keep their length and order.
/// <para>A Toolkit table T and a game table G are taken as the same table when they have the same
/// number of slots, every slot of T whose function is already matched holds, in G, the function it
/// is matched to, G is the only game table for which that is true, and no other Toolkit table
/// claims G with more support. Once two tables are paired, the functions in equal slots are paired,
/// which lets further tables be recognised by the slots they inherit.</para>
/// </summary>
public static class VtableMatcher
{
    /// <summary>Returns (tables {tk id: (game id, support)}, functions {tk function: (game function, votes, tk table id)}).</summary>
    public static (Dictionary<long, (long Game, int Support)> Tables, Dictionary<Node, (uint Game, int Votes, long Table)> Functions) Match(
        IReadOnlyDictionary<long, List<Node>> tkTables, IReadOnlyDictionary<long, List<uint>> gameTables,
        IReadOnlyDictionary<Node, uint> seeds, int minSupport = 1, int rounds = 60)
    {
        var bySlot = new Dictionary<(int, uint), HashSet<long>>();
        var byLen = new Dictionary<int, HashSet<long>>();
        foreach (var (gid, slots) in gameTables)
        {
            if (!byLen.TryGetValue(slots.Count, out var l))
                byLen[slots.Count] = l = [];
            l.Add(gid);
            for (int i = 0; i < slots.Count; i++)
            {
                if (!bySlot.TryGetValue((i, slots[i]), out var s))
                    bySlot[(i, slots[i])] = s = [];
                s.Add(gid);
            }
        }
        var forward = new Dictionary<Node, uint>(seeds);
        var found = new Dictionary<Node, (uint Game, int Votes, long Table)>();
        var tables = new Dictionary<long, (long, int)>();
        var taken = new Dictionary<long, long>();
        for (int round = 0; round < rounds; round++)
        {
            var proposals = new List<(long Tid, long Gid, int Support)>();
            foreach (var (tid, slots) in tkTables)
            {
                if (tables.ContainsKey(tid))
                    continue;
                var known = new List<(int, uint)>();
                for (int i = 0; i < slots.Count; i++)
                    if (forward.TryGetValue(slots[i], out var g))
                        known.Add((i, g));
                if (known.Count < minSupport)
                    continue;
                HashSet<long>? candidates = null;
                foreach (var key in known)
                {
                    if (!bySlot.TryGetValue(key, out var hit) || hit.Count == 0)
                    {
                        candidates = [];
                        break;
                    }
                    if (candidates == null)
                        candidates = [.. hit];
                    else
                        candidates.IntersectWith(hit);
                    if (candidates.Count == 0)
                        break;
                }
                if (candidates == null || candidates.Count == 0)
                    continue;
                if (byLen.TryGetValue(slots.Count, out var sameLength))
                    candidates.IntersectWith(sameLength);
                else
                    candidates.Clear();
                if (candidates.Count == 1)
                    proposals.Add((tid, candidates.First(), known.Count));
            }
            var claims = new Dictionary<long, List<(int Support, long Tid)>>();
            foreach (var (tid, gid, support) in proposals)
            {
                if (!claims.TryGetValue(gid, out var l))
                    claims[gid] = l = [];
                l.Add((support, tid));
            }
            int accepted = 0;
            foreach (var (gid, items) in claims)
            {
                if (taken.ContainsKey(gid))
                    continue;
                items.Sort((a, b) => a.Support != b.Support ? b.Support.CompareTo(a.Support) : b.Tid.CompareTo(a.Tid));
                // Several Toolkit tables with identical content (same class exported by two DLLs, or
                // folded identical tables) may all point at one game table; they must agree slot by slot.
                var best = tkTables[items[0].Tid];
                var same = items.Where(it => tkTables[it.Tid].SequenceEqual(best) || Agree(tkTables[it.Tid], best, forward))
                    .Select(it => it.Tid).ToHashSet();
                if (same.Count != items.Count && items.Count > 1 && items[0].Support == items[1].Support)
                    continue;
                long chosen = items[0].Tid;
                var gSlots = gameTables[gid];
                var tSlots = tkTables[chosen];
                bool conflict = false;
                for (int i = 0; i < Math.Min(tSlots.Count, gSlots.Count); i++)
                    if (forward.TryGetValue(tSlots[i], out var g) && g != gSlots[i])
                    {
                        conflict = true;
                        break;
                    }
                if (conflict)
                    continue;
                taken[gid] = chosen;
                foreach (var (support, other) in items)
                    if (other == chosen || same.Contains(other))
                        tables[other] = (gid, support);
                accepted++;
                for (int i = 0; i < Math.Min(tSlots.Count, gSlots.Count); i++)
                {
                    var f = tSlots[i];
                    var g = gSlots[i];
                    if (!forward.ContainsKey(f))
                    {
                        forward[f] = g;
                        found[f] = (g, 1, chosen);
                    }
                    else if (found.TryGetValue(f, out var v) && v.Game == g)
                        found[f] = (v.Game, v.Votes + 1, v.Table);
                }
            }
            if (accepted == 0)
                break;
        }
        return (tables, found);
    }

    static bool Agree(List<Node> a, List<Node> b, Dictionary<Node, uint> forward)
    {
        for (int i = 0; i < Math.Min(a.Count, b.Count); i++)
        {
            var x = a[i];
            var y = b[i];
            bool hx = forward.TryGetValue(x, out var gx), hy = forward.TryGetValue(y, out var gy);
            if (!(hx == hy && (!hx || gx == gy)) && x != y)
                return false;
        }
        return true;
    }

    /// <summary>Hides every fifth seed that sits in a Toolkit table, matches without them and reports
    /// how many come back, and how many of those with the game function the seed had.</summary>
    public static void Check(IReadOnlyDictionary<long, List<Node>> tkTables, IReadOnlyDictionary<long, List<uint>> gameTables,
        IReadOnlyDictionary<Node, uint> seeds, TextWriter log)
    {
        var inTables = tkTables.Values.SelectMany(s => s).ToHashSet();
        var virtualSeeds = seeds.Keys.Where(inTables.Contains).OrderBy(n => n.Dll).ThenBy(n => n.Rva).ToList();
        var hidden = new Dictionary<Node, uint>();
        for (int i = 0; i < virtualSeeds.Count; i += 5)
            hidden[virtualSeeds[i]] = seeds[virtualSeeds[i]];
        var kept = seeds.Where(kv => !hidden.ContainsKey(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        var (tables, found) = Match(tkTables, gameTables, kept);
        var again = hidden.Keys.Where(found.ContainsKey).ToList();
        int right = again.Count(f => found[f].Game == hidden[f]);
        log.WriteLine($"  vtable matching check: {virtualSeeds.Count} seeds are virtual methods, {hidden.Count} hidden, {again.Count} found again,"
                      + $" {right} with the same game function ({100.0 * right / Math.Max(1, again.Count):F1}%);"
                      + $" {tables.Count} tables and {found.Count} functions matched in this run");
    }
}
