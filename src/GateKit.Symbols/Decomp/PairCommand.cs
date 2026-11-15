using System.Diagnostics;
using System.Globalization;
using System.IO.Hashing;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace GateKit.Symbols.Decomp;

/// <summary>One function of a binary as the pairing sees it.</summary>
internal sealed record PairFunction(long Id, long Rva, string Name, long Size);

/// <summary>One side of a pairing: a binary's functions, its call graph and the labels that say
/// what a function is in words both builds share.</summary>
internal sealed class PairSide
{
    public string Binary = "";
    public long BinaryId;
    public Dictionary<long, PairFunction> ByRva = [];
    public Dictionary<long, long> RvaOfId = [];
    public Dictionary<long, List<long>> Callees = [], Callers = [];
    /// <summary>rva -> label: a name both builds spell alike, `helper:get:T`, a system's update entry.</summary>
    public Dictionary<long, string> Labels = [];
    /// <summary>rva -> the name an earlier pairing gave the function; it is read as nameless here.</summary>
    public Dictionary<long, string> GivenNames = [];
    /// <summary>rva -> tokens of its own that both builds can have: the type indices it reads.</summary>
    public Dictionary<long, long[]> Extra = [];
    /// <summary>The binary's virtual tables: table rva -> the functions in its slots, in slot order.</summary>
    public Dictionary<long, long[]> Tables = [];
    /// <summary>Table rva -> the class the table is of, where the build says (the macOS symbol `vtable for X`).</summary>
    public Dictionary<long, string> TableNames = [];
}

/// <summary>A virtual table of the target build matched to one of the source build.</summary>
/// <param name="Support">Slots that hold paired functions in the same order in both.</param>
internal sealed record TablePair(long Source, long Target, string Name, int Support, int SourceSlots, int TargetSlots);

/// <summary>A proposed pair of the two builds' functions.</summary>
/// <param name="Score">By `bag`: weight of the shared tokens over the weight of all. By `calls`: the share of texts, numbers and type indices the two have in common.</param>
/// <param name="Margin">By `bag`: the lead over the runner-up. By `calls`: the share of the votes for the target that named this source.</param>
/// <param name="State">seed (a label only one function of each build carries), accepted or candidate.</param>
/// <param name="Rule">label (a seed), bag (the most alike of all), calls (the one unpaired call between the same paired calls of paired callers).</param>
/// <param name="Votes">By `calls`: in how many paired callers the two stand at the same place.</param>
internal sealed record FunctionPair(long Source, long Target, double Score, double Margin, int Round, string State, string Evidence, string Rule = "bag", int Votes = 0);

/// <summary>What a pairing run found, for the report and the tests.</summary>
internal sealed class PairResult
{
    public List<FunctionPair> Pairs = [];
    /// <summary>Virtual tables of the target matched to tables of the source (the run with every name).</summary>
    public List<TablePair> Tables = [];
    /// <summary>Target tables that fit two source tables equally well, and were left unmatched.</summary>
    public int TablesAmbiguous;
    /// <summary>The bar of the `bag` rule.</summary>
    public double MinScore, MinMargin;
    /// <summary>The bar of the `calls` rule: votes and likeness.</summary>
    public double MinVotes, MinLikeness;
    /// <summary>Held-out pairs the bars accept: how many got the name the truth set says.</summary>
    public int HeldAgree, HeldDiffer, Held, HeldProposed;
    public int CallsAgree, CallsDiffer, CallsProposed;
    /// <summary>The bar of the `order` rule (the calls rule where two or three unpaired calls stand in a stretch).</summary>
    public double OrderVotes, OrderLikeness;
    public int OrderAgree, OrderDiffer, OrderProposed;
    public bool Calibrated, CallsCalibrated, OrderCalibrated;
    public List<string> Table = [];
    /// <summary>Held-out names that were proposed another name: truth, proposed, score, margin.</summary>
    public List<(string Truth, string Got, double Score, double Margin, string Rule, int Votes)> Misses = [];
}

/// <summary>
/// `pair`: which function of one build of the game is which function of another. The builds are
/// compiled from the same source, so three things survive: the texts and numbers a function uses
/// (<see cref="CodeMatch.Features"/>), the names both builds know (a Toolkit name here, a symbol
/// there), and the calls between functions. Every function gets a bag of tokens: its own texts and
/// numbers, the labels of the functions it calls and is called by, and the pairs already made
/// among them. Labels are the anchors: a name spelled alike in both builds, a component helper
/// (`helper:get:T`: the Windows helper keyed by `TypeIndex&lt;T&gt;` and the macOS template of the
/// same family and T), a system's update entry. A label only one function of each build carries
/// is a pair from the start (a seed). Then, in rounds, each unpaired function of the target build
/// looks for the source function whose bag is most like its own (weight of shared tokens over the
/// weight of all, rare tokens weighing more); a pair is made when each is the other's best with a
/// clear lead, and the pairs of one round are tokens of the next, so pairing spreads along the
/// call graph from the anchors (the `bag` rule).
///
/// The second rule, `calls`, needs no likeness at all. Two paired functions call their callees in
/// the same order (the order of first mention in the pseudo-code); the calls already paired are
/// lined up (the longest common order), and where exactly one unpaired call of each build stands
/// between the same two paired calls, those two are the same function. Each paired caller that
/// says so is a vote; a pair is proposed when each is the other's most voted.
///
/// How far to trust a pair is measured, not assumed: a share of the target functions whose name
/// both builds spell alike is held out (treated as nameless everywhere), and the bar - the lowest
/// score and margin at which the held-out functions still get their own name at the asked
/// precision - is read off them. Pairs above the bar are `accepted`; the rest are kept as
/// `candidate` and never shown as names.
/// </summary>
internal static partial class PairCommand
{
    public const string Table = """
        -- Functions of two builds of the game paired by `gatekit decomp pair`. state: seed (a label
        -- one function of each build carries), accepted (above the bar measured on held-out names),
        -- candidate (below it: never a name). evidence says what the two share.
        CREATE TABLE IF NOT EXISTS function_pairs (
            source_binary INTEGER, source_rva INTEGER, target_binary INTEGER, target_rva INTEGER,
            score REAL, margin REAL, round INTEGER, state TEXT, evidence TEXT, rule TEXT, votes INTEGER
        );
        CREATE INDEX IF NOT EXISTS function_pairs_target ON function_pairs(target_binary, target_rva);
        CREATE INDEX IF NOT EXISTS function_pairs_source ON function_pairs(source_binary, source_rva);
        -- Virtual tables named after the table of another build they were matched to (gatekit decomp pair):
        -- support is the number of slots that hold paired functions in the same order in both.
        CREATE TABLE IF NOT EXISTS vtable_names (dll TEXT, rva INTEGER, name TEXT, source TEXT, source_rva INTEGER, support INTEGER, slots INTEGER, source_slots INTEGER);
        CREATE INDEX IF NOT EXISTS vtable_names_rva ON vtable_names(dll, rva);
        -- One row per run: the bar and how it was measured.
        CREATE TABLE IF NOT EXISTS function_pair_runs (
            source_binary INTEGER, target_binary INTEGER, finished TEXT, min_score REAL, min_margin REAL,
            held INTEGER, held_agree INTEGER, held_differ INTEGER, seeds INTEGER, accepted INTEGER, candidates INTEGER,
            min_votes REAL, min_likeness REAL, calls_agree INTEGER, calls_differ INTEGER
        );
        """;

    const ulong Mask = (1UL << 63) - 1;

    static long Token(string kind, string text) => (long)(XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(kind + "\u0001" + text)) & Mask);

    static long Token(string kind, long value) => (long)(unchecked(((ulong)value * 0x9E3779B97F4A7C15) ^ XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(kind))) & Mask);

    [GeneratedRegex(@"\b(?:struct|class|enum|union)[ _]")]
    private static partial Regex Keyword();

    /// <summary>A name as both demanglers would spell it: without `struct`/`class`, spaces and quotes.</summary>
    internal static string Normalize(string name)
    {
        name = Keyword().Replace(name, "");
        var b = new StringBuilder(name.Length);
        foreach (char c in name)
            if (c != ' ' && c != '`' && c != '\'' && c != '_')
                b.Append(c);
        return b.ToString();
    }

    static long L(object? v) => Convert.ToInt64(v, CultureInfo.InvariantCulture);

    /// <summary>True for a name a tool of ours made up: it says nothing the other build could repeat.</summary>
    static bool Synthetic(string name) =>
        TallyCommand.DefaultName(name) || name.StartsWith("ecs::GetComponent<", StringComparison.Ordinal) || name.StartsWith("ecs::HasComponent<", StringComparison.Ordinal)
        || name.StartsWith("ecs::AddComponent<", StringComparison.Ordinal) || name.StartsWith("ecs::RemoveComponent<", StringComparison.Ordinal)
        || name.StartsWith("ecs::RegisterComponent<", StringComparison.Ordinal) || name.StartsWith("ecs::ComponentHelper<", StringComparison.Ordinal)
        || name.StartsWith("switchD_", StringComparison.Ordinal) || name.StartsWith("caseD_", StringComparison.Ordinal)
        || name.Contains(".DLL::", StringComparison.OrdinalIgnoreCase) || name.StartsWith("FID_conflict", StringComparison.Ordinal)
        || name.StartsWith("Unwind@", StringComparison.Ordinal) || name.StartsWith("Catch", StringComparison.Ordinal);

    /// <summary>The functions of a binary whose name a pair gave: the addresses whose line in the
    /// names the project was last given (`names.tsv` of the binary's work folder) says so in its
    /// source column. Such a name is the outcome of pairing and must not be its evidence: the
    /// function is read as nameless, or a later run would hold out and confirm what an earlier one
    /// concluded.</summary>
    internal static HashSet<long> GivenByPairs(string work, string binary)
    {
        var given = new HashSet<long>();
        var file = Path.Combine(work, binary, RefreshCommand.NamesState);
        if (File.Exists(file))
            foreach (var line in File.ReadLines(file, Encoding.UTF8))
            {
                var p = line.Split('\t');
                if (p.Length >= 5 && RefreshCommand.IsPairBasis(p[^1]) && Py.Hex(p[0]) is long rva)
                    given.Add(rva);
            }
        return given;
    }

    /// <summary>Reads one binary: functions (thunks apart), calls, and its labels. A function in
    /// <paramref name="given"/> is read as nameless (see <see cref="GivenByPairs"/>).</summary>
    public static PairSide Load(SqliteConnection db, string binary, ISet<long>? given = null)
    {
        var side = new PairSide { Binary = binary };
        side.BinaryId = Sql.Long(db.Scalar("SELECT id FROM binaries WHERE name = ?", binary)) ?? throw new SymbolsException($"{binary} is not loaded (gatekit decomp status lists what is)");
        foreach (var r in db.Query("SELECT id, rva, name, size, thunk FROM functions WHERE binary_id = ?", side.BinaryId))
        {
            if (r[4] != null && L(r[4]) != 0)
                continue;
            var f = new PairFunction(L(r[0]), L(r[1]), Py.Str(r[2]), r[3] == null ? 0 : L(r[3]));
            if (given != null && given.Contains(f.Rva) && !Synthetic(f.Name))
            {
                side.GivenNames[f.Rva] = f.Name;
                f = f with { Name = "FUN_" + f.Rva.ToString("x", CultureInfo.InvariantCulture) };
            }
            side.ByRva.TryAdd(f.Rva, f);
            side.RvaOfId[f.Id] = f.Rva;
        }
        foreach (var r in db.Query("SELECT DISTINCT caller, callee FROM calls WHERE binary_id = ?", side.BinaryId))
        {
            long caller = L(r[0]), callee = L(r[1]);
            if (caller == callee || !side.ByRva.ContainsKey(caller) || !side.ByRva.ContainsKey(callee))
                continue;
            if (!side.Callees.TryGetValue(caller, out var a))
                side.Callees[caller] = a = [];
            a.Add(callee);
            if (!side.Callers.TryGetValue(callee, out var b))
                side.Callers[callee] = b = [];
            b.Add(caller);
        }

        // labels: the helpers by family and type, the systems' update entries, then every other real name
        var helpers = HelpersCommand.Load(db, binary);
        foreach (var h in helpers.Values)
            if (h.Family is "get" or "has" or "add" or "remove" && side.ByRva.ContainsKey(h.Rva))
                side.Labels[h.Rva] = $"helper:{h.Family}:{h.Type}";
        try
        {
            foreach (var r in db.Query("SELECT system, thunk FROM system_updates WHERE binary_id = ?", side.BinaryId))
                if (r[1] != null && side.ByRva.TryGetValue(L(r[1]), out var f) && Synthetic(f.Name))
                    side.Labels.TryAdd(f.Rva, Normalize($"ecs::_private::SystemRegistrationHelper::SystemUpdate<{Py.Str(r[0])}>"));
        }
        catch (SqliteException)
        {
            // no systems table
        }
        foreach (var f in side.ByRva.Values)
        {
            if (side.Labels.ContainsKey(f.Rva) || helpers.ContainsKey(f.Rva) || Synthetic(f.Name))
                continue;
            side.Labels[f.Rva] = HelpersCommand.Template(f.Name) is var (family, type) ? $"helper:{family}:{type}" : Normalize(f.Name);
        }
        return side;
    }

    /// <summary>The deterministic share of rvas that is held out: the same functions every run.</summary>
    internal static bool HeldOut(long rva, double share) => (unchecked((ulong)rva * 0x9E3779B97F4A7C15) >> 40) % 1000 < share * 1000;

    sealed class Pairing
    {
        public required PairSide Source, Target;
        public required IReadOnlyDictionary<long, long[]> Features;   // function id -> texts and numbers
        public Dictionary<long, string> SourceLabels = [], TargetLabels = [];
        public Dictionary<long, string> Truth = [];                   // held-out target rva -> its label
        public Dictionary<long, long> SourceOf = [], TargetOf = [];   // target rva -> source rva and back
        public Dictionary<long, FunctionPair> Proposals = [];         // by target rva
        public Func<PairSide, long, string?> Code = (_, _) => null;   // pseudo-code of a function by rva
        public Dictionary<long, long[]> SourceOrder = [], TargetOrder = [];
        public Dictionary<long, string> CallEvidence = [];            // by target rva, for pairs of the calls rule
        public List<TablePair> Tables = [];
        public int TablesAmbiguous;
        public int Jobs;
        public Action<string> Log = _ => { };
    }

    /// <summary>The last component of a qualified name, outside template arguments and parameters:
    /// what the decompiler writes at a call whatever namespace it stands in.</summary>
    internal static string Short(string name)
    {
        int depth = 0, at = 0;
        for (int i = 0; i + 1 < name.Length; i++)
        {
            char c = name[i];
            if (c is '<' or '(')
                depth++;
            else if (c is '>' or ')')
                depth--;
            else if (depth == 0 && c == ':' && name[i + 1] == ':')
                at = i + 2;
        }
        return name[at..];
    }

    static string Flat(string text)
    {
        var b = new StringBuilder(text.Length);
        foreach (char c in text)
            if (!char.IsWhiteSpace(c) && c != '_')
                b.Append(c);
        return b.ToString();
    }

    /// <summary>The callees of a function in the order its pseudo-code first mentions them. A
    /// callee the text does not mention, or whose short name another callee shares, is left out.</summary>
    internal static long[] CallOrder(PairSide side, long rva, string? code)
    {
        if (code == null || !side.Callees.TryGetValue(rva, out var callees))
            return [];
        int brace = code.IndexOf('{');
        string flat = Flat(brace >= 0 ? code[brace..] : code);
        var needles = new Dictionary<string, long>(StringComparer.Ordinal);
        var shared = new HashSet<string>(StringComparer.Ordinal);
        foreach (long c in callees.Distinct())
        {
            // the text shows the name the function has now, also when it is read as nameless here
            string needle = Flat(Short(side.GivenNames.GetValueOrDefault(c) ?? side.ByRva[c].Name));
            if (needle.Length < 3 || !needles.TryAdd(needle, c))
                shared.Add(needle);
        }
        var found = new List<(int At, long Rva)>();
        foreach (var (needle, c) in needles)
        {
            if (shared.Contains(needle))
                continue;
            int at = -1;
            for (int i = flat.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = flat.IndexOf(needle, i + 1, StringComparison.Ordinal))
            {
                // a whole name: not the tail of a longer one, and not the head of one
                bool head = i == 0 || !char.IsLetterOrDigit(flat[i - 1]);
                int end = i + needle.Length;
                bool tail = end >= flat.Length || !char.IsLetterOrDigit(flat[end]);
                if (head && tail)
                {
                    at = i;
                    break;
                }
            }
            if (at >= 0)
                found.Add((at, c));
        }
        found.Sort();
        return [.. found.Select(f => f.Rva)];
    }

    static long[] Order(Pairing run, bool source, long rva)
    {
        var cache = source ? run.SourceOrder : run.TargetOrder;
        if (!cache.TryGetValue(rva, out var order))
        {
            var side = source ? run.Source : run.Target;
            cache[rva] = order = CallOrder(side, rva, run.Code(side, rva));
        }
        return order;
    }

    /// <summary>The indices of a longest common subsequence of two sequences, as pairs (index in a, index in b).</summary>
    internal static List<(int A, int B)> CommonOrder(IReadOnlyList<long> a, IReadOnlyList<long> b)
    {
        int n = a.Count, m = b.Count;
        var table = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int k = m - 1; k >= 0; k--)
                table[i, k] = a[i] == b[k] ? table[i + 1, k + 1] + 1 : Math.Max(table[i + 1, k], table[i, k + 1]);
        var result = new List<(int, int)>();
        for (int i = 0, k = 0; i < n && k < m;)
        {
            if (a[i] == b[k]) { result.Add((i, k)); i++; k++; }
            else if (table[i + 1, k] >= table[i, k + 1]) i++;
            else k++;
        }
        return result;
    }

    static double Likeness(Pairing run, long source, long target)
    {
        IEnumerable<long> Own(PairSide side, long rva) =>
            (run.Features.GetValueOrDefault(side.ByRva[rva].Id) ?? []).Concat(side.Extra.GetValueOrDefault(rva) ?? []);
        var a = Own(run.Source, source).ToHashSet();
        var b = Own(run.Target, target).ToHashSet();
        if (a.Count == 0 || b.Count == 0)
            return 0;
        int both = a.Count(b.Contains);
        return (double)both / (a.Count + b.Count - both);
    }

    /// <summary>Matches the target's virtual tables to the source's from the pairs made so far: a
    /// target table goes to the source table with which it shares the most paired functions in the
    /// same slot order (at least two), alone at the top (then the nearer in slot count); a source
    /// table two target tables claim equally is given to neither.</summary>
    static void MatchTables(Pairing run)
    {
        run.Tables.Clear();
        run.TablesAmbiguous = 0;
        if (run.Source.Tables.Count == 0 || run.Target.Tables.Count == 0)
            return;
        var tablesOf = new Dictionary<long, List<long>>();
        foreach (var (table, slots) in run.Source.Tables)
            foreach (long f in slots.Distinct())
            {
                if (!tablesOf.TryGetValue(f, out var list))
                    tablesOf[f] = list = [];
                list.Add(table);
            }
        var best = new Dictionary<long, (long Source, int Support, int Distance, bool Tie)>();   // by target table
        foreach (var (table, slots) in run.Target.Tables.OrderBy(p => p.Key))
        {
            var images = slots.Select(f => run.SourceOf.GetValueOrDefault(f, -1)).ToArray();
            var count = new Dictionary<long, int>();
            foreach (long image in images.Where(i => i >= 0).Distinct())
                if (tablesOf.TryGetValue(image, out var list))
                    foreach (long s in list)
                        count[s] = count.GetValueOrDefault(s) + 1;
            (long Source, int Support, int Distance, bool Tie) top = (-1, 0, 0, false);
            foreach (var (s, n) in count.Where(c => c.Value >= 2).OrderByDescending(c => c.Value).ThenBy(c => c.Key).Take(16))
            {
                if (n < top.Support)
                    break;                                // the common order is never longer than the common functions
                var theirs = run.Source.Tables[s];
                int support = CommonOrder(theirs, images).Count, distance = Math.Abs(theirs.Length - slots.Length);
                if (support < 2)
                    continue;
                if (support > top.Support || support == top.Support && distance < top.Distance)
                    top = (s, support, distance, false);
                else if (support == top.Support && distance == top.Distance)
                    top = top with { Tie = true };
            }
            if (top.Source < 0)
                continue;
            if (top.Tie)
                run.TablesAmbiguous++;
            else
                best[table] = top;
        }
        foreach (var g in best.GroupBy(b => b.Value.Source))
        {
            var ordered = g.OrderByDescending(b => b.Value.Support).ThenBy(b => b.Value.Distance).ToList();
            if (ordered.Count > 1 && ordered[0].Value.Support == ordered[1].Value.Support && ordered[0].Value.Distance == ordered[1].Value.Distance)
            {
                run.TablesAmbiguous += ordered.Count;
                continue;
            }
            var (target, top) = (ordered[0].Key, ordered[0].Value);
            run.Tables.Add(new TablePair(top.Source, target, run.Source.TableNames.GetValueOrDefault(top.Source, ""), top.Support,
                                         run.Source.Tables[top.Source].Length, run.Target.Tables[target].Length));
        }
        run.Tables.Sort((a, b) => a.Target.CompareTo(b.Target));
    }

    /// <summary>The pairs whose calls are lined up: every pair made so far, and for a paired target
    /// function that only forwards to one nameless function (a system's update entry and its
    /// update, which the other build folded into the entry) that function in its place.</summary>
    static List<(long[] Source, long[] Target, string What)> Contexts(Pairing run)
    {
        var list = new List<(long[], long[], string)>();
        void Add(long source, long target)
        {
            var os = Order(run, true, source);
            var ot = Order(run, false, target);
            if (os.Length > 0 && ot.Length > 0)
                list.Add((os, ot, $"+0x{target:X}"));
        }
        MatchTables(run);
        foreach (var t in run.Tables)
            list.Add((run.Source.Tables[t.Source], run.Target.Tables[t.Target], $"virtual table +0x{t.Target:X}"));
        foreach (var (target, source) in run.SourceOf.OrderBy(p => p.Key))
        {
            Add(source, target);
            long at = target;
            for (int depth = 0; depth < 2; depth++)
            {
                if (!run.Target.Callees.TryGetValue(at, out var callees) || callees.Distinct().Take(2).Count() != 1)
                    break;
                long only = callees[0];
                if (run.SourceOf.ContainsKey(only) || run.TargetLabels.ContainsKey(only) || run.Source.Callees.GetValueOrDefault(source)?.Distinct().Take(3).Count() < 3)
                    break;
                Add(source, only);
                at = only;
            }
        }
        return list;
    }

    /// <summary>The calls rule over every pair made so far. Returns how many pairs it made.</summary>
    static int Align(Pairing run, int round, int roundVotes, double roundLikeness)
    {
        var votes = new Dictionary<(long Source, long Target), List<string>>();   // -> the paired callers and matched tables that say so
        var alone = new HashSet<(long, long)>();                                // pairs some caller has as the one unpaired call
        foreach (var (os, ot, what) in Contexts(run))
        {
            if (os.Length == 0 || ot.Length == 0 || os.Length > 600 || ot.Length > 600)
                continue;
            // what lines the two up: a call paired already (spelled by its source rva), or one both builds label alike
            var keyT = new long[ot.Length];
            var keysT = new HashSet<long>();
            for (int i = 0; i < ot.Length; i++)
            {
                keyT[i] = run.SourceOf.TryGetValue(ot[i], out long image) ? image : run.TargetLabels.TryGetValue(ot[i], out var label) ? Token("label", label) : 0;
                if (keyT[i] != 0)
                    keysT.Add(keyT[i]);
            }
            var keyS = new long[os.Length];
            var keysS = new HashSet<long>();
            for (int i = 0; i < os.Length; i++)
            {
                long key = run.TargetOf.ContainsKey(os[i]) ? os[i] : run.SourceLabels.TryGetValue(os[i], out var label) ? Token("label", label) : 0;
                keyS[i] = keysT.Contains(key) ? key : 0;
                if (keyS[i] != 0)
                    keysS.Add(key);
            }
            var a = new List<long>(); var ai = new List<int>();
            var b = new List<long>(); var bi = new List<int>();
            for (int i = 0; i < os.Length; i++)
                if (keyS[i] != 0)
                {
                    a.Add(keyS[i]); ai.Add(i);
                }
            for (int i = 0; i < ot.Length; i++)
                if (keyT[i] != 0 && keysS.Contains(keyT[i]))
                {
                    b.Add(keyT[i]); bi.Add(i);
                }
            var common = CommonOrder(a, b);
            // the stretches between the calls that stand in the same order
            int fromS = 0, fromT = 0;
            var freeS = new List<long>();
            var freeT = new List<long>();
            for (int c = 0; c <= common.Count; c++)
            {
                int toS = c < common.Count ? ai[common[c].A] : os.Length, toT = c < common.Count ? bi[common[c].B] : ot.Length;
                freeS.Clear();
                freeT.Clear();
                bool clean = true;
                for (int i = fromT; i < toT; i++)
                {
                    if (keyT[i] == 0)
                        freeT.Add(ot[i]);
                    else if (keysS.Contains(keyT[i]))
                        clean = false;                    // a lined-up call out of order: the stretch is not trusted
                }
                for (int i = fromS; i < toS; i++)
                {
                    if (keyS[i] != 0)
                        clean = false;
                    else if (!run.TargetOf.ContainsKey(os[i]))
                        freeS.Add(os[i]);
                }
                if (clean && freeS.Count == freeT.Count && freeS.Count is >= 1 and <= 3)
                    for (int i = 0; i < freeS.Count; i++)
                    {
                        if (!votes.TryGetValue((freeS[i], freeT[i]), out var list))
                            votes[(freeS[i], freeT[i])] = list = [];
                        list.Add(what);
                        if (freeS.Count == 1)
                            alone.Add((freeS[i], freeT[i]));
                    }
                fromS = toS + 1; fromT = toT + 1;
            }
        }
        // each the other's most voted, alone at the top
        var bestOfTarget = new Dictionary<long, (long Source, int Votes, bool Tie, int All)>();
        var bestOfSource = new Dictionary<long, (long Target, int Votes, bool Tie)>();
        foreach (var ((source, target), list) in votes.OrderBy(v => v.Key))
        {
            int n = list.Count;
            var t = bestOfTarget.GetValueOrDefault(target, (-1, 0, false, 0));
            bestOfTarget[target] = n > t.Item2 ? (source, n, false, t.Item4 + n) : (t.Item1, t.Item2, t.Item3 || n == t.Item2, t.Item4 + n);
            var s = bestOfSource.GetValueOrDefault(source, (-1, 0, false));
            bestOfSource[source] = n > s.Item2 ? (target, n, false) : (s.Item1, s.Item2, s.Item3 || n == s.Item2);
        }
        int made = 0, proposed = 0;
        foreach (var (target, best) in bestOfTarget.OrderBy(p => p.Key))
        {
            if (best.Tie || run.SourceOf.ContainsKey(target) || run.TargetOf.ContainsKey(best.Source))
                continue;
            var back = bestOfSource[best.Source];
            if (back.Tie || back.Target != target)
                continue;
            double likeness = Likeness(run, best.Source, target);
            var callers = votes[(best.Source, target)];
            bool one = alone.Contains((best.Source, target));
            run.Proposals[target] = new FunctionPair(best.Source, target, likeness, (double)best.Votes / best.All, round, "candidate", "", one ? "calls" : "order", best.Votes);
            run.CallEvidence[target] = (one ? "the one unpaired call" : "the unpaired call at the same place among two or three")
                                       + $" between the same lined-up calls in {callers.Count} paired caller{(callers.Count == 1 ? "" : "s")} or matched table{(callers.Count == 1 ? "" : "s")} ("
                                       + string.Join(", ", callers.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(3)) + (callers.Count > 3 ? ", ..." : "") + ")";
            proposed++;
            if (one && (best.Votes >= roundVotes || likeness >= roundLikeness) || !one && likeness >= roundLikeness)
            {
                run.SourceOf[target] = best.Source;
                run.TargetOf[best.Source] = target;
                made++;
            }
        }
        run.Log($"  round {round}, by calls: {votes.Count} places where one unpaired call of each build stands, {proposed} proposals, {made} paired");
        return made;
    }

    /// <summary>The bag of one function: its texts and numbers, its neighbours' labels and pairs.
    /// A pair is spelled by its source function's rva on both sides.</summary>
    static long[] Bag(PairSide side, long rva, IReadOnlyDictionary<long, long[]> features, Dictionary<long, string> labels, Dictionary<long, long> pairKey)
    {
        var set = new HashSet<long>();
        if (features.TryGetValue(side.ByRva[rva].Id, out var own))
            foreach (var t in own)
                set.Add(t);
        if (side.Extra.TryGetValue(rva, out var extra))
            foreach (var t in extra)
                set.Add(t);
        if (side.Callees.TryGetValue(rva, out var callees))
            foreach (var c in callees)
            {
                if (labels.TryGetValue(c, out var label))
                    set.Add(Token("calls", label));
                if (pairKey.TryGetValue(c, out var key))
                    set.Add(Token("calls pair", key));
            }
        if (side.Callers.TryGetValue(rva, out var callers))
            foreach (var c in callers)
            {
                if (labels.TryGetValue(c, out var label))
                    set.Add(Token("called by", label));
                if (pairKey.TryGetValue(c, out var key))
                    set.Add(Token("called by pair", key));
            }
        var bag = set.ToArray();
        Array.Sort(bag);
        return bag;
    }

    static void Seed(Pairing run)
    {
        var bySource = run.SourceLabels.GroupBy(p => p.Value, p => p.Key, StringComparer.Ordinal).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var g in run.TargetLabels.GroupBy(p => p.Value, p => p.Key, StringComparer.Ordinal))
        {
            if (g.Count() != 1 || !bySource.TryGetValue(g.Key, out long source))
                continue;
            long target = g.First();
            run.SourceOf[target] = source;
            run.TargetOf[source] = target;
            run.Proposals[target] = new FunctionPair(source, target, 1, 1, 0, "seed", "the label " + g.Key, "label");
        }
    }

    /// <summary>One round: every unpaired target function against the unpaired source functions.
    /// Returns how many pairs the round made (those at or above the round's own bar).</summary>
    static int Round(Pairing run, int round, double roundScore, double roundMargin)
    {
        // a pair is spelled by its source rva on both sides
        var sourceKey = run.TargetOf.Keys.ToDictionary(s => s, s => s);
        var targetKey = run.SourceOf;
        var bags = new Dictionary<long, long[]>();
        var sourceIds = new List<long>();
        var targetIds = new List<long>();
        var idToSource = new Dictionary<long, long>();
        var idToTarget = new Dictionary<long, long>();
        void Add(PairSide side, Dictionary<long, string> labels, Dictionary<long, long> keys, Dictionary<long, long> taken, List<long> ids, Dictionary<long, long> back, long idBase)
        {
            var rvas = side.ByRva.Keys.Where(r => !taken.ContainsKey(r)).Order().ToArray();
            var made = new long[rvas.Length][];
            Parallel.For(0, rvas.Length, new ParallelOptions { MaxDegreeOfParallelism = run.Jobs }, i => made[i] = Bag(side, rvas[i], run.Features, labels, keys));
            for (int i = 0; i < rvas.Length; i++)
            {
                if (made[i].Length < 3)
                    continue;
                long id = idBase + i;
                bags[id] = made[i];
                ids.Add(id);
                back[id] = rvas[i];
            }
        }
        Add(run.Source, run.SourceLabels, sourceKey, run.TargetOf, sourceIds, idToSource, 0);
        Add(run.Target, run.TargetLabels, targetKey, run.SourceOf, targetIds, idToTarget, 1L << 40);
        // a labelled target function looks only for its label: it is paired by a seed or not at all
        targetIds = [.. targetIds.Where(id => !run.TargetLabels.ContainsKey(idToTarget[id]))];
        if (targetIds.Count == 0 || sourceIds.Count == 0)
            return 0;
        var pairs = CodeMatch.Match(bags, targetIds, sourceIds, 0.10, 0.02, 0.0, jobs: run.Jobs);
        int accepted = 0;
        foreach (var p in pairs)
        {
            long target = idToTarget[p.Toolkit], source = idToSource[p.Game];
            run.Proposals[target] = new FunctionPair(source, target, p.Score, p.Margin, round, "candidate", "");
            run.CallEvidence.Remove(target);
            if (p.Score >= roundScore && p.Margin >= roundMargin)
            {
                run.SourceOf[target] = source;
                run.TargetOf[source] = target;
                accepted++;
            }
        }
        run.Log($"  round {round}: {targetIds.Count} target and {sourceIds.Count} source functions unpaired, {pairs.Count} proposals, {accepted} paired");
        return accepted;
    }

    /// <summary>The bars a pair must pass to be built upon in the next round.</summary>
    internal sealed record RoundBars(double Score = 0.6, double Margin = 0.3, int Votes = 2, double Likeness = 0.25);

    static Pairing Execute(PairSide source, PairSide target, IReadOnlyDictionary<long, long[]> features, Func<PairSide, long, string?> code, double holdout, int rounds,
                           RoundBars bars, int jobs, Action<string> log)
    {
        var run = new Pairing { Source = source, Target = target, Features = features, Jobs = jobs, Log = log, Code = code };
        run.SourceLabels = new Dictionary<long, string>(source.Labels);
        var sourceNames = new HashSet<string>(source.Labels.Values, StringComparer.Ordinal);
        foreach (var (rva, label) in target.Labels)
        {
            // only a plain name both builds have can be checked afterwards
            if (holdout > 0 && HeldOut(rva, holdout) && !label.StartsWith("helper:", StringComparison.Ordinal) && sourceNames.Contains(label))
                run.Truth[rva] = label;
            else
                run.TargetLabels[rva] = label;
        }
        Seed(run);
        log($"  {run.SourceOf.Count} seeds (a label one function of each build carries)" + (holdout > 0 ? $", {run.Truth.Count} names held out" : ""));
        for (int round = 1; round <= rounds; round++)
        {
            int byCalls = Align(run, round, bars.Votes, bars.Likeness);
            int byBag = Round(run, round, bars.Score, bars.Margin);
            if (byCalls + byBag == 0)
                break;
        }
        return run;
    }

    static readonly double[] Scores = [0.15, 0.20, 0.25, 0.30, 0.35, 0.40, 0.45, 0.50, 0.55, 0.60, 0.65, 0.70, 0.75, 0.80, 0.85, 0.90, 0.95];
    static readonly double[] Margins = [0.02, 0.05, 0.10, 0.15, 0.20, 0.25, 0.30, 0.40, 0.50, 0.60];
    static readonly double[] VoteSteps = [1, 2, 3, 4, 6], LikenessSteps = [0, 0.02, 0.05, 0.10, 0.15, 0.20, 0.30, 0.40, 0.50, 0.70];

    /// <summary>The lowest bar at which the held-out functions get their own name at the precision
    /// asked for, with the most pairs; null when no bar with at least <paramref name="minChecked"/> checked pairs does.</summary>
    internal static (double Score, double Margin, int Agree, int Differ)? Calibrate(IEnumerable<(double Score, double Margin, bool Agree)> held, double precision, int minChecked,
                                                                                    double[]? scores = null, double[]? margins = null)
    {
        var list = held.ToList();
        (double, double, int, int)? best = null;
        foreach (double s in scores ?? Scores)
            foreach (double m in margins ?? Margins)
            {
                int agree = 0, differ = 0;
                foreach (var h in list)
                    if (h.Score >= s && h.Margin >= m)
                    {
                        if (h.Agree) agree++;
                        else differ++;
                    }
                if (agree + differ < minChecked || agree < precision * (agree + differ))
                    continue;
                if (best == null || agree + differ > best.Value.Item3 + best.Value.Item4)
                    best = (s, m, agree, differ);
            }
        return best;
    }

    /// <summary>What two paired functions share, in words: labelled and paired neighbours, texts and numbers.</summary>
    static string Evidence(Pairing run, long source, long target)
    {
        var parts = new List<string>();
        void Neighbours(string what, Dictionary<long, List<long>> ofSource, Dictionary<long, List<long>> ofTarget)
        {
            var s = ofSource.GetValueOrDefault(source) ?? [];
            var t = ofTarget.GetValueOrDefault(target) ?? [];
            var labels = s.Select(r => run.SourceLabels.GetValueOrDefault(r)).OfType<string>().ToHashSet(StringComparer.Ordinal);
            var shared = t.Select(r => run.TargetLabels.GetValueOrDefault(r)).OfType<string>().Where(labels.Contains).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            var paired = t.Where(r => run.SourceOf.TryGetValue(r, out long sr) && s.Contains(sr)).Distinct().Order().ToList();
            if (shared.Count > 0)
                parts.Add($"{what} {shared.Count} labelled alike ({string.Join(", ", shared.Take(3))}{(shared.Count > 3 ? ", ..." : "")})");
            if (paired.Count > 0)
                parts.Add($"{what} {paired.Count} paired ({string.Join(", ", paired.Take(3).Select(r => $"+0x{r:X}=+0x{run.SourceOf[r]:X}"))}{(paired.Count > 3 ? ", ..." : "")})");
        }
        Neighbours("calls", run.Source.Callees, run.Target.Callees);
        Neighbours("called by", run.Source.Callers, run.Target.Callers);
        var a = run.Features.GetValueOrDefault(run.Source.ByRva[source].Id) ?? [];
        var b = run.Features.GetValueOrDefault(run.Target.ByRva[target].Id) ?? [];
        int both = 0, i = 0, k = 0;
        while (i < a.Length && k < b.Length)
        {
            if (a[i] < b[k]) i++;
            else if (a[i] > b[k]) k++;
            else { both++; i++; k++; }
        }
        if (a.Length + b.Length > 0)
            parts.Add($"{both} texts and numbers shared of {a.Length} and {b.Length}");
        return string.Join("; ", parts);
    }

    /// <summary>The whole pairing: a calibration run with names held out, then the run with every
    /// name, judged by the bars the first one measured (one per rule).</summary>
    public static PairResult Pair(PairSide source, PairSide target, IReadOnlyDictionary<long, long[]> features, Func<PairSide, long, string?> code, double holdout, double precision,
                                  int rounds, RoundBars bars, int jobs, Action<string> log, int minChecked = 30)
    {
        var result = new PairResult();
        log($"calibration: {target.Binary} against {source.Binary} with {holdout:P0} of the shared names held out");
        var trial = Execute(source, target, features, code, holdout, rounds, bars, jobs, log);
        var bag = new List<(double, double, bool)>();
        var calls = new List<(double, double, bool)>();
        var order = new List<(double, double, bool)>();
        foreach (var (rva, truth) in trial.Truth)
        {
            if (!trial.Proposals.TryGetValue(rva, out var p))
                continue;
            if (!source.Labels.TryGetValue(p.Source, out var got))
                continue;                                 // the source function has no name: nothing to compare
            if (p.Rule == "calls")
                calls.Add((p.Votes, p.Score, got == truth));
            else if (p.Rule == "order")
                order.Add((p.Votes, p.Score, got == truth));
            else
                bag.Add((p.Score, p.Margin, got == truth));
            if (got != truth)
                result.Misses.Add((truth, got, p.Score, p.Margin, p.Rule, p.Votes));
        }
        result.Held = trial.Truth.Count;
        result.HeldProposed = bag.Count;
        result.CallsProposed = calls.Count;
        result.OrderProposed = order.Count;
        foreach (var (s, m) in new[] { (0.15, 0.05), (0.30, 0.10), (0.40, 0.20), (0.50, 0.20), (0.60, 0.30), (0.70, 0.30), (0.80, 0.40), (0.90, 0.50) })
        {
            int agree = bag.Count(h => h.Item1 >= s && h.Item2 >= m && h.Item3), all = bag.Count(h => h.Item1 >= s && h.Item2 >= m);
            result.Table.Add(string.Create(CultureInfo.InvariantCulture, $"  bag: score >= {s:F2}, margin >= {m:F2}: {all} held-out names proposed, {agree} right ({(all == 0 ? 0 : 100.0 * agree / all):F1} %)"));
        }
        foreach (var (v, l) in new[] { (1, 0.0), (1, 0.05), (1, 0.15), (1, 0.30), (2, 0.0), (2, 0.15), (3, 0.0) })
        {
            int agree = calls.Count(h => h.Item1 >= v && h.Item2 >= l && h.Item3), all = calls.Count(h => h.Item1 >= v && h.Item2 >= l);
            result.Table.Add(string.Create(CultureInfo.InvariantCulture, $"  calls: votes >= {v}, likeness >= {l:F2}: {all} held-out names proposed, {agree} right ({(all == 0 ? 0 : 100.0 * agree / all):F1} %)"));
        }
        foreach (var (v, l) in new[] { (1, 0.0), (1, 0.10), (1, 0.20), (1, 0.40), (2, 0.0), (2, 0.20) })
        {
            int agree = order.Count(h => h.Item1 >= v && h.Item2 >= l && h.Item3), all = order.Count(h => h.Item1 >= v && h.Item2 >= l);
            result.Table.Add(string.Create(CultureInfo.InvariantCulture, $"  order: votes >= {v}, likeness >= {l:F2}: {all} held-out names proposed, {agree} right ({(all == 0 ? 0 : 100.0 * agree / all):F1} %)"));
        }
        // no bar reaches the precision: nothing of that rule is accepted
        (result.MinScore, result.MinMargin, result.MinVotes, result.MinLikeness, result.OrderVotes, result.OrderLikeness) = (2, 2, int.MaxValue, 2, int.MaxValue, 2);
        if (Calibrate(order, precision, minChecked, VoteSteps, LikenessSteps) is var (votes3, likeness3, right3, wrong3))
            (result.OrderVotes, result.OrderLikeness, result.OrderAgree, result.OrderDiffer, result.OrderCalibrated) = (votes3, likeness3, right3, wrong3, true);
        if (Calibrate(bag, precision, minChecked) is var (score, margin, right, wrong))
            (result.MinScore, result.MinMargin, result.HeldAgree, result.HeldDiffer, result.Calibrated) = (score, margin, right, wrong, true);
        if (Calibrate(calls, precision, minChecked, VoteSteps, LikenessSteps) is var (votes, likeness, right2, wrong2))
            (result.MinVotes, result.MinLikeness, result.CallsAgree, result.CallsDiffer, result.CallsCalibrated) = (votes, likeness, right2, wrong2, true);

        log($"the run with every name: {target.Binary} against {source.Binary}");
        var full = Execute(source, target, features, code, 0, rounds, bars, jobs, log);
        foreach (var p in full.Proposals.Values.OrderBy(p => p.Target))
        {
            bool passes = p.Rule == "calls" ? p.Votes >= result.MinVotes && p.Score >= result.MinLikeness
                : p.Rule == "order" ? p.Votes >= result.OrderVotes && p.Score >= result.OrderLikeness : p.Score >= result.MinScore && p.Margin >= result.MinMargin;
            string state = p.State == "seed" ? "seed" : passes ? "accepted" : "candidate";
            string evidence = p.State == "seed" ? p.Evidence
                : p.Rule is "calls" or "order" ? full.CallEvidence.GetValueOrDefault(p.Target, "") + "; " + Evidence(full, p.Source, p.Target) : Evidence(full, p.Source, p.Target);
            result.Pairs.Add(p with { State = state, Evidence = evidence });
        }
        MatchTables(full);
        result.Tables = full.Tables;
        result.TablesAmbiguous = full.TablesAmbiguous;
        return result;
    }

    /// <summary>Replaces the stored pairs of the two binaries.</summary>
    public static void Store(SqliteConnection db, PairSide source, PairSide target, PairResult result)
    {
        db.Execute(Table);
        db.Execute("BEGIN");
        db.Execute("DELETE FROM function_pairs WHERE source_binary = ? AND target_binary = ?", source.BinaryId, target.BinaryId);
        using (var insert = db.CreateCommand())
        {
            insert.CommandText = "INSERT INTO function_pairs VALUES ($a,$b,$c,$d,$e,$f,$g,$h,$i,$j,$k)";
            var ps = "abcdefghijk".Select(c => insert.Parameters.Add("$" + c, SqliteType.Integer)).ToArray();
            ps[4].SqliteType = ps[5].SqliteType = SqliteType.Real;
            ps[7].SqliteType = ps[8].SqliteType = ps[9].SqliteType = SqliteType.Text;
            foreach (var p in result.Pairs)
            {
                ps[0].Value = source.BinaryId; ps[1].Value = p.Source; ps[2].Value = target.BinaryId; ps[3].Value = p.Target;
                ps[4].Value = Math.Round(p.Score, 4); ps[5].Value = Math.Round(p.Margin, 4); ps[6].Value = p.Round; ps[7].Value = p.State; ps[8].Value = p.Evidence; ps[9].Value = p.Rule; ps[10].Value = p.Votes;
                insert.ExecuteNonQuery();
            }
        }
        db.Execute("DELETE FROM vtable_names WHERE dll = ? AND source = ?", target.Binary, source.Binary);
        foreach (var t in result.Tables)
            if (t.Name.Length > 0)
                db.Execute("INSERT INTO vtable_names VALUES (?,?,?,?,?,?,?,?)", target.Binary, t.Target, t.Name, source.Binary, t.Source, t.Support, t.TargetSlots, t.SourceSlots);
        db.Execute("INSERT INTO function_pair_runs VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)", source.BinaryId, target.BinaryId, Py.Now(), result.MinScore, result.MinMargin,
                   result.Held, result.HeldAgree, result.HeldDiffer, result.Pairs.Count(p => p.State == "seed"), result.Pairs.Count(p => p.State == "accepted"),
                   result.Pairs.Count(p => p.State == "candidate"), result.MinVotes, result.MinLikeness, result.CallsAgree, result.CallsDiffer);
        db.Execute("COMMIT");
    }

    /// <summary>The functions of other builds paired with functions of a binary (a seed or an accepted
    /// pair, whichever of the two the binary is): rva -> (the other binary, its rva, its function's
    /// name, the pair's state, rule, score, margin and evidence). The macOS build's first, then by score.</summary>
    public static Dictionary<long, List<(string Binary, long Rva, string Name, string State, string Rule, double Score, double Margin, string Evidence)>> Partners(SqliteConnection db, string binary)
    {
        var partners = new Dictionary<long, List<(string, long, string, string, string, double, double, string)>>();
        try
        {
            foreach (var r in db.Query("""
                SELECT * FROM (
                    SELECT p.target_rva AS rva, f.name AS name, o.name AS other, p.state, p.score AS score, p.margin, p.evidence, p.source_rva AS other_rva, p.rule
                    FROM function_pairs p JOIN binaries me ON me.id = p.target_binary JOIN binaries o ON o.id = p.source_binary
                    JOIN functions f ON f.binary_id = p.source_binary AND f.rva = p.source_rva
                    WHERE me.name = ? AND p.state IN ('seed', 'accepted')
                    UNION ALL
                    SELECT p.source_rva, f.name, o.name, p.state, p.score, p.margin, p.evidence, p.target_rva, p.rule
                    FROM function_pairs p JOIN binaries me ON me.id = p.source_binary JOIN binaries o ON o.id = p.target_binary
                    JOIN functions f ON f.binary_id = p.target_binary AND f.rva = p.target_rva
                    WHERE me.name = ? AND p.state IN ('seed', 'accepted'))
                ORDER BY rva, other LIKE 'bg3!_macos%' ESCAPE '!' DESC, score DESC, other_rva
                """, binary, binary))
            {
                if (!partners.TryGetValue(L(r[0]), out var list))
                    partners[L(r[0])] = list = [];
                list.Add((Py.Str(r[2]), L(r[7]), Py.Str(r[1]), Py.Str(r[3]), Py.Str(r[8]), Convert.ToDouble(r[4], CultureInfo.InvariantCulture), Convert.ToDouble(r[5], CultureInfo.InvariantCulture), Py.Str(r[6])));
            }
        }
        catch (SqliteException)
        {
            // no pairs yet
        }
        return partners;
    }

    /// <summary>The names the pairs give to functions of a binary that have none: rva -> (name,
    /// where from). The name of the first partner that has a real one; the source is `macos-anchor`
    /// for the macOS build (its names are the compiler's own) and `pair &lt;binary&gt;` for a sibling
    /// build. A function that has a name of its own keeps it: it is listed only while its name is
    /// the decompiler's, or <paramref name="given"/> says a pair gave it its name before (then the pair of today names it).</summary>
    public static Dictionary<long, (string Name, string Basis)> Names(SqliteConnection db, string binary, ISet<long>? given = null)
    {
        var names = new Dictionary<long, (string, string)>();
        var partners = Partners(db, binary);
        if (partners.Count == 0)
            return names;
        var own = new Dictionary<long, string>();
        foreach (var r in db.Query("SELECT f.rva, f.name FROM functions f JOIN binaries b ON b.id = f.binary_id WHERE b.name = ?", binary))
            own[L(r[0])] = Py.Str(r[1]);
        foreach (var (rva, list) in partners)
        {
            if (!own.TryGetValue(rva, out var mine))
                continue;
            foreach (var p in list)
            {
                if (Synthetic(p.Name))
                    continue;
                if (TallyCommand.DefaultName(mine) || given != null && given.Contains(rva))
                {
                    string source = p.Binary.StartsWith("bg3_macos", StringComparison.Ordinal) ? "macos-anchor" : "pair " + p.Binary;
                    string basis = string.Create(CultureInfo.InvariantCulture, $"{source}: {p.Binary}+0x{p.Rva:X}, {p.State} by {p.Rule}, score {p.Score:F2}, margin {p.Margin:F2}; {p.Evidence}");
                    names[rva] = (p.Name, basis.Length > 400 ? basis[..400] : basis);
                }
                break;
            }
        }
        return names;
    }

    /// <summary>Gives each function of a side the tokens of the type indices it reads.</summary>
    public static int AddTypeRefs(DecompSession s, SqliteConnection db, PairSide side, int jobs, Action<string> log)
    {
        var refs = s.Symbols is { } symbols && symbols.Scalar("SELECT id FROM dlls WHERE name = ?", side.Binary) != null
            ? TypeRefs.Windows(symbols, side) : TypeRefs.Macos(s.Settings, db, side, jobs, log);
        foreach (var (rva, keys) in refs)
            side.Extra[rva] = [.. keys.Select(k => Token("reads index", k))];
        return refs.Count;
    }

    public static int Run(DecompSession s, string source, string target, double holdout, double precision, int rounds, RoundBars bars, int jobs, bool dryRun, int misses = 0)
    {
        var clock = Stopwatch.StartNew();
        void Log(string text) => s.Err.WriteLine(text);
        PairSide a, b;
        using (var read = Sql.OpenReadOnly(s.Settings.DbPath))
        {
            a = Load(read, source, GivenByPairs(s.Settings.Work, source));
            b = Load(read, target, GivenByPairs(s.Settings.Work, target));
            if (a.GivenNames.Count + b.GivenNames.Count > 0)
                Log($"{a.GivenNames.Count} functions of {a.Binary} and {b.GivenNames.Count} of {b.Binary} carry a name an earlier pairing gave: read as nameless");
            int ra = AddTypeRefs(s, read, a, jobs, Log), rb = AddTypeRefs(s, read, b, jobs, Log);
            foreach (var side in new[] { a, b })
            {
                VirtualTables.Add(s, side, Log);
                if (side.Tables.Count > 0)
                    Log($"{side.Binary}: {side.Tables.Count} virtual tables, {side.TableNames.Count} named by the build");
            }
            Log($"{a.Binary}: {a.ByRva.Count} functions, {a.Labels.Count} labelled, {ra} read a type index; {b.Binary}: {b.ByRva.Count} functions, {b.Labels.Count} labelled, {rb} read a type index");
        }
        var all = CodeMatch.Extract(s.Settings.DbPath, Path.Combine(s.Settings.Work, "features.bin"), jobs, Log);
        var features = new Dictionary<long, long[]>();
        foreach (var side in new[] { a, b })
            foreach (var f in side.ByRva.Values)
                if (all.TryGetValue(f.Id, out var list))
                    features[f.Id] = list;
        all = null;
        PairResult result;
        using (var read = Sql.OpenReadOnly(s.Settings.DbPath))
        {
            string? Code(PairSide side, long rva) =>
                DecompStore.Decompress(read.Scalar("SELECT text FROM code WHERE function_id = ?", side.ByRva[rva].Id));
            result = Pair(a, b, features, Code, holdout, precision, rounds, bars, jobs, Log);
        }
        if (!dryRun)
        {
            // beside the database, then over it, as refresh does: a reader never sees half the pairs
            RefreshCommand.ThroughACopy(s.Settings, ".pairing", 60, db => Store(db, a, b, result));
        }
        int seeds = result.Pairs.Count(p => p.State == "seed"), accepted = result.Pairs.Count(p => p.State == "accepted"), candidates = result.Pairs.Count(p => p.State == "candidate");
        bool Nameless(FunctionPair p) => Synthetic(b.ByRva[p.Target].Name);
        bool Names(FunctionPair p) => !Synthetic(a.ByRva[p.Source].Name) || a.GivenNames.ContainsKey(p.Source);
        s.Out.WriteLine($"{b.Binary} paired with {a.Binary}: {seeds} seeds, {accepted} accepted, {candidates} candidates (kept, never names)" + (dryRun ? "  [dry run: nothing stored]" : ""));
        string Pct(int right, int wrong) => string.Create(CultureInfo.InvariantCulture, $"{100.0 * right / Math.Max(1, right + wrong):F1} %");
        s.Out.WriteLine($"of {result.Held} names held out, {result.HeldProposed} were proposed a pair by likeness (bag) and {result.CallsProposed} by their place among calls (calls); asked {100 * precision:F0} % right");
        s.Out.WriteLine(result.Calibrated
            ? string.Create(CultureInfo.InvariantCulture, $"the bar of bag: score >= {result.MinScore:F2}, margin >= {result.MinMargin:F2}: {result.HeldAgree + result.HeldDiffer} held-out names above it, {result.HeldAgree} right ({Pct(result.HeldAgree, result.HeldDiffer)})")
            : "the bar of bag: none reaches the precision asked for: no pair of this rule is accepted");
        s.Out.WriteLine(result.CallsCalibrated
            ? string.Create(CultureInfo.InvariantCulture, $"the bar of calls: votes >= {result.MinVotes:F0}, likeness >= {result.MinLikeness:F2}: {result.CallsAgree + result.CallsDiffer} held-out names above it, {result.CallsAgree} right ({Pct(result.CallsAgree, result.CallsDiffer)})")
            : "the bar of calls: none reaches the precision asked for: no pair of this rule is accepted");
        s.Out.WriteLine(result.OrderCalibrated
            ? string.Create(CultureInfo.InvariantCulture, $"the bar of order: votes >= {result.OrderVotes:F0}, likeness >= {result.OrderLikeness:F2}: {result.OrderAgree + result.OrderDiffer} of {result.OrderProposed} held-out names above it, {result.OrderAgree} right ({Pct(result.OrderAgree, result.OrderDiffer)})")
            : $"the bar of order: none reaches the precision asked for on the {result.OrderProposed} held-out names it proposed: no pair of this rule is accepted");
        foreach (var line in result.Table)
            s.Out.WriteLine(line);
        int newNames = result.Pairs.Count(p => p.State != "candidate" && Nameless(p) && Names(p));
        s.Out.WriteLine($"nameless functions of {b.Binary} that get a name: {newNames} ({result.Pairs.Count(p => p.State == "seed" && Nameless(p) && Names(p))} by a seed, "
                        + $"{result.Pairs.Count(p => p.State == "accepted" && p.Rule == "bag" && Nameless(p) && Names(p))} by bag, {result.Pairs.Count(p => p.State == "accepted" && p.Rule == "calls" && Nameless(p) && Names(p))} by calls, {result.Pairs.Count(p => p.State == "accepted" && p.Rule == "order" && Nameless(p) && Names(p))} by order)");
        int unproposed = b.ByRva.Values.Count(f => Synthetic(f.Name)) - result.Pairs.Count(Nameless);
        s.Out.WriteLine($"nameless functions of {b.Binary} with no pair proposed at all: {unproposed}");
        if (a.Tables.Count > 0 && b.Tables.Count > 0)
        {
            s.Out.WriteLine($"virtual tables of {b.Binary} matched to a table of {a.Binary}: {result.Tables.Count} of {b.Tables.Count} ({result.Tables.Count(t => t.Name.Length > 0)} get a class name, "
                            + $"{result.Tables.Count(t => t.Support >= 4)} on four or more slots in order); {result.TablesAmbiguous} fit two tables equally and were left");
            if (VirtualTables.Check(s, b, result.Tables) is var (same, other, sample) && same + other > 0)
                s.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  of the {same + other} that the Toolkit also names, {same} have the same class ({100.0 * same / (same + other):F1} %)")
                                + (sample.Count > 0 ? "; differ: " + string.Join("; ", sample.Take(5)) : ""));
        }
        if (misses > 0)
        {
            s.Out.WriteLine($"held-out names proposed another name ({result.Misses.Count}; the first {Math.Min(misses, result.Misses.Count)} by score):");
            foreach (var m in result.Misses.OrderBy(m => m.Rule, StringComparer.Ordinal).ThenByDescending(m => m.Votes).ThenByDescending(m => m.Score).Take(misses))
                s.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {m.Rule} {m.Score:F2} {m.Margin:F2} votes {m.Votes}  {m.Truth}  <-  {m.Got}"));
        }
        s.Err.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{clock.Elapsed.TotalSeconds:F0} s"));
        return 0;
    }
}
