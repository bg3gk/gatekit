using GateKit.Symbols.Decomp;
using GateKit.Symbols.Pe;
using Iced.Intel;
using Microsoft.Data.Sqlite;

namespace GateKit.Symbols.Queries;

/// <summary>One component operation a system performs: it adds, removes, gets or asks whether an
/// entity has a component, through a classified helper (`gatekit decomp helpers`).</summary>
/// <param name="Kind">add, remove, get or has: the helper's family.</param>
/// <param name="Function">The function that calls the helper: the system's update, or a function it calls.</param>
/// <param name="Site">The address of the call instruction, or null when the binary's file was not found.</param>
/// <param name="Depth">1 when the update calls the helper itself, 2 when a function the update calls does.</param>
internal sealed record ComponentFact(string System, string Kind, string Type, string Binary, long Function, long Helper, long? Site, int Depth, string Confidence);

/// <summary>
/// The component operations of each system, walked from its per-frame update entries
/// (`system_updates` of decomp.sqlite) to call depth 2 through the classified helpers
/// (`ecs_helpers`). The walk does not go into another system's update, nor into the engine's own
/// functions (names in ecs::, ls::, std::): what those do is not the system's. Operations the
/// compiler inlined, and those reached deeper, are not seen.
/// </summary>
internal static class ComponentFacts
{
    static readonly string[] Engine = ["ecs::", "ls::", "std::"];

    /// <summary>The operations of every system with update entries in the binary, by system; empty
    /// when decomp.sqlite, the systems table or the helpers of the binary are missing.</summary>
    public static IReadOnlyDictionary<string, List<ComponentFact>> Of(SymbolContext c, string binary)
    {
        if (c.Facts.TryGetValue(binary, out var known))
            return known;
        var facts = new Dictionary<string, List<ComponentFact>>(StringComparer.Ordinal);
        c.Facts[binary] = facts;
        var db = c.Decomp.Db;
        if (db == null)
            return facts;
        try
        {
            var helpers = HelpersCommand.Load(db, binary);
            var binaryId = db.Scalar("SELECT id FROM binaries WHERE name = ?", binary);
            if (helpers.Count == 0 || binaryId == null)
                return facts;
            var updates = db.Query("SELECT system, updater FROM system_updates WHERE binary_id = ? AND updater IS NOT NULL", binaryId)
                .Select(r => (System: Py.Str(r[0]), Updater: PyText.L(r[1]))).ToList();
            if (updates.Count == 0)
                return facts;
            var updaters = updates.Select(u => u.Updater).ToHashSet();
            var callees = new Dictionary<long, List<long>>();
            List<long> CalleesOf(long f)
            {
                if (!callees.TryGetValue(f, out var list))
                    callees[f] = list = db.Query("SELECT DISTINCT callee FROM calls INDEXED BY calls_caller WHERE binary_id = ? AND caller = ?", binaryId, f)
                        .Select(r => PyText.L(r[0])).ToList();
                return list;
            }
            var names = new Dictionary<long, string>();
            string NameOf(long f)
            {
                if (!names.TryGetValue(f, out var n))
                    names[f] = n = Py.Str(db.Scalar("SELECT name FROM functions WHERE binary_id = ? AND rva = ?", binaryId, f));
                return n;
            }
            var sites = new CallSites(c, binary);
            foreach (var (system, updater) in updates)
            {
                var list = facts.TryGetValue(system, out var l) ? l : facts[system] = [];
                var depth2 = new List<long>();
                foreach (var (caller, depth) in new[] { (updater, 1) }.Concat(CalleesOf(updater).Where(x => !helpers.ContainsKey(x)).Select(x => (x, 2))))
                {
                    if (depth == 2 && (updaters.Contains(caller) || Engine.Any(e => NameOf(caller).StartsWith(e, StringComparison.Ordinal))))
                        continue;
                    foreach (var callee in CalleesOf(caller))
                    {
                        if (!helpers.TryGetValue(callee, out var h) || h.Family is not ("add" or "remove" or "get" or "has"))
                            continue;
                        var at = sites.Of(caller, callee);
                        if (at.Count == 0)
                            list.Add(new ComponentFact(system, h.Family, h.Type, binary, caller, callee, null, depth, h.Confidence));
                        foreach (var site in at)
                            list.Add(new ComponentFact(system, h.Family, h.Type, binary, caller, callee, site, depth, h.Confidence));
                    }
                }
            }
            foreach (var key in facts.Keys.ToList())
                facts[key] = facts[key].DistinctBy(f => (f.Kind, f.Type, f.Function, f.Site)).OrderBy(f => f.Kind, StringComparer.Ordinal)
                    .ThenBy(f => f.Type, StringComparer.Ordinal).ThenBy(f => f.Function).ThenBy(f => f.Site).ToList();
        }
        catch (SqliteException)
        {
            // a database made before the tables existed: nothing known
        }
        return facts;
    }

    /// <summary>The functions that call a helper adding the type, with the systems whose updates reach
    /// them (to depth 2): (binary, function, function name, helper, systems).</summary>
    public static List<(string Binary, long Function, string Name, long Helper, List<string> Systems)> Creators(SymbolContext c, string type, IEnumerable<string> binaries)
    {
        var result = new List<(string, long, string, long, List<string>)>();
        var db = c.Decomp.Db;
        if (db == null)
            return result;
        foreach (var binary in binaries)
        {
            try
            {
                var binaryId = db.Scalar("SELECT id FROM binaries WHERE name = ?", binary);
                if (binaryId == null)
                    continue;
                var bySystem = Of(c, binary).Values.SelectMany(f => f).Where(f => f.Kind == "add" && f.Type == type)
                    .GroupBy(f => f.Function).ToDictionary(g => g.Key, g => g.Select(f => f.System).Distinct().Order(StringComparer.Ordinal).ToList());
                foreach (var h in HelpersCommand.Load(db, binary).Values.Where(h => h.Family == "add" && h.Type == type).OrderBy(h => h.Rva))
                    foreach (var r in db.Query("SELECT DISTINCT c.caller, f.name FROM calls c INDEXED BY calls_callee LEFT JOIN functions f ON f.binary_id = c.binary_id AND f.rva = c.caller"
                                               + " WHERE c.binary_id = ? AND c.callee = ? ORDER BY c.caller", binaryId, h.Rva))
                    {
                        long caller = PyText.L(r[0]);
                        result.Add((binary, caller, Py.Str(r[1]), h.Rva, bySystem.GetValueOrDefault(caller) ?? []));
                    }
            }
            catch (SqliteException)
            {
                // no helpers table
            }
        }
        return result;
    }

    /// <summary>Where a function calls another: the near calls and jumps of its machine code, read with
    /// Iced from the binary's file (the game folder's bin). Without the file, no sites.</summary>
    internal sealed class CallSites(SymbolContext c, string binary)
    {
        PeImage? pe;
        bool tried;
        readonly Dictionary<long, List<(long Site, long Target)>> calls = [];

        public List<long> Of(long caller, long callee)
        {
            if (!tried)
            {
                tried = true;
                var path = Path.Combine(c.Env.Game, "bin", binary);
                try
                {
                    pe = File.Exists(path) ? PeImage.Open(path) : null;
                }
                catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    pe = null;
                }
            }
            if (pe == null)
                return [];
            if (!calls.TryGetValue(caller, out var list))
                calls[caller] = list = Decode(caller);
            return list.Where(x => x.Target == callee).Select(x => x.Site).ToList();
        }

        List<(long, long)> Decode(long caller)
        {
            var list = new List<(long, long)>();
            var game = c.Game(binary);
            long size = 0;
            if (game != null && c.Db.One("SELECT size FROM functions WHERE dll_id = ? AND rva = ?", game.Value.Id, caller) is { } row)
                size = PyText.L(row[0]);
            if (size <= 0 && c.Decomp.Db?.Scalar("SELECT f.size FROM functions f JOIN binaries b ON b.id = f.binary_id WHERE b.name = ? AND f.rva = ?", binary, caller) is { } s)
                size = PyText.L(s);
            int o = pe!.Offset(caller);
            if (o < 0 || size <= 0 || size > 4_000_000)
                return list;
            int length = (int)Math.Min(size, pe.Data.Length - o);
            var decoder = Iced.Intel.Decoder.Create(64, new ByteArrayCodeReader(pe.Data, o, length), (ulong)caller, DecoderOptions.None);
            ulong stop = (ulong)(caller + length);
            while (decoder.IP < stop)
            {
                decoder.Decode(out var insn);
                if (insn.IsInvalid)
                    break;
                if (insn.Mnemonic is Mnemonic.Call or Mnemonic.Jmp && insn.Op0Kind is OpKind.NearBranch64 or OpKind.NearBranch32)
                    list.Add(((long)insn.IP, (long)insn.NearBranchTarget));
            }
            return list;
        }
    }
}
