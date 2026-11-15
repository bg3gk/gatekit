using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace GateKit.Symbols.Decomp;

/// <summary>What the two builds say about the queries of one system.</summary>
internal sealed class SystemQueries
{
    public string System = "";
    /// <summary>The Windows update entry (`system_updates.thunk`) and the updates it calls.</summary>
    public long? Entry;
    public List<long> Updates = [];
    /// <summary>The Windows functions read: the entry and the updates that have pseudo-code.</summary>
    public List<long> Functions = [];
    /// <summary>The macOS `SystemUpdate&lt;S&gt;`, and the methods of the system it calls that were read with it.</summary>
    public long? MacosEntry;
    public List<long> MacosFunctions = [];
    /// <summary>Every mention of a query global on the Windows side, in the order of use: the function it stands in and the global.</summary>
    public List<(long Function, long Global)> Globals = [];
    /// <summary>Every mention of a spec on the macOS side, in the order of use.</summary>
    public List<string> Specs = [];
    /// <summary>The values `index * N + base` the Windows functions assign.</summary>
    public List<(long Function, StrideValue Value)> Strides = [];
    /// <summary>Multiplications by the description's size whose operand is not an unnamed global.</summary>
    public int OtherDescriptions;

    /// <summary>The globals in the order of first use.</summary>
    public List<long> DistinctGlobals => Globals.Select(g => g.Global).Distinct().ToList();

    /// <summary>The specs in the order of first use.</summary>
    public List<string> DistinctSpecs => Specs.Distinct(StringComparer.Ordinal).ToList();
}

/// <summary>Both builds read for their queries: per system of the Windows build what its update
/// reads, and the specs of the macOS build by name.</summary>
internal sealed class QueryReading
{
    public string Target = "", Source = "";
    public List<SystemQueries> Systems = [];
    public Dictionary<string, QuerySpec> Specs = new(StringComparer.Ordinal);
    /// <summary>Per query global of the Windows build: the registrations found for it (one, unless two functions disagree).</summary>
    public Dictionary<long, List<QueryRegistration>> Registrations = [];
    /// <summary>How many specs the macOS symbol table names in all.</summary>
    public int SpecsNamed;
    public List<string> Notes = [];
}

/// <summary>Reads the systems' updates of a Windows build and the `SystemUpdate&lt;S&gt;` of the
/// macOS build for the queries they use (<see cref="QueryShapes"/>).</summary>
internal static class QueryReader
{
    static long L(object? v) => Convert.ToInt64(v, CultureInfo.InvariantCulture);

    /// <summary>The ledger's size of every component (coverage.json, `components[].name` and `inlineSize`).</summary>
    public static Dictionary<string, long> LedgerSizes(string? coverage)
    {
        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        if (coverage == null || !File.Exists(Path.Combine(coverage, "coverage.json")))
            return sizes;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(coverage, "coverage.json")));
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("components", out var list) || list.ValueKind != JsonValueKind.Array)
                return sizes;
            foreach (var c in list.EnumerateArray())
                if (c.ValueKind == JsonValueKind.Object && c.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                    && c.TryGetProperty("inlineSize", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out long size) && size > 0)
                    sizes.TryAdd(n.GetString()!, size);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            // a ledger this reader does not understand: no size is known
        }
        return sizes;
    }

    /// <summary>The items of a function in the order of its text, with the items of the callees
    /// it may be read together with put where the call stands (at the end when the call is not found).</summary>
    static List<T> InOrder<T>(string flat, List<(int At, T Item)> own, IEnumerable<(string Name, List<T> Items)> callees)
    {
        var all = own.Select(o => (o.At, Order: 0, o.Item)).ToList();
        int order = 1;
        foreach (var (name, items) in callees)
        {
            var call = Regex.Match(flat, @"(?<!\w)" + Regex.Escape(QueryShapes.Flat(PairCommand.Short(name))) + @"\(", RegexOptions.CultureInvariant);
            int at = call.Success ? call.Index : int.MaxValue;
            foreach (var item in items)
                all.Add((at, order++, item));
        }
        return all.OrderBy(a => a.At).ThenBy(a => a.Order).Select(a => a.Item).ToList();
    }

    public static QueryReading Read(DecompSession s, string target, string source, Action<string> log)
    {
        var db = s.Db;
        var reading = new QueryReading { Target = target, Source = source };
        long targetId = Sql.Long(db.Scalar("SELECT id FROM binaries WHERE name = ?", target)) ?? throw new SymbolsException($"{target} is not loaded (gatekit decomp status lists what is)");
        long? sourceId = Sql.Long(db.Scalar("SELECT id FROM binaries WHERE name = ?", source));
        long imageBase = DecompStore.ImageBase(target);

        (string Name, string Code)? Function(long binaryId, long rva)
        {
            var row = db.One("SELECT f.name, c.text FROM functions f LEFT JOIN code c ON c.function_id = f.id WHERE f.binary_id = ? AND f.rva = ?", binaryId, rva);
            return row == null || row[1] is not byte[] blob ? null : (Py.Str(row[0]), DecompStore.Decompress(blob));
        }

        var systems = new Dictionary<string, SystemQueries>(StringComparer.Ordinal);
        try
        {
            foreach (var r in db.Query("SELECT system, thunk, updater FROM system_updates WHERE binary_id = ? ORDER BY system, updater", targetId))
            {
                string name = Py.Str(r[0]);
                if (!systems.TryGetValue(name, out var q))
                    reading.Systems.Add(systems[name] = q = new SystemQueries { System = name });
                if (r[1] != null)
                    q.Entry ??= L(r[1]);
                if (r[2] != null && L(r[2]) != q.Entry && !q.Updates.Contains(L(r[2])))
                    q.Updates.Add(L(r[2]));
            }
        }
        catch (SqliteException)
        {
            throw new SymbolsException($"{s.Settings.DbPath} has no table of the systems' updates:  gatekit decomp systems --rebuild --binary {target}");
        }

        // the Windows side
        foreach (var q in reading.Systems)
        {
            var updates = new List<(string Name, List<(long, long)> Items)>();
            foreach (long rva in q.Updates)
            {
                if (Function(targetId, rva) is not var (name, code))
                    continue;
                q.Functions.Add(rva);
                string flat = QueryShapes.Flat(code);
                updates.Add((name, QueryShapes.QueryGlobals(flat, imageBase).Select(g => (rva, g.Rva)).ToList()));
                q.OtherDescriptions += QueryShapes.OtherDescriptions(flat);
                q.Strides.AddRange(QueryShapes.Strides(code).Select(v => (rva, v)));
            }
            if (q.Entry is long entry && Function(targetId, entry) is var (_, entryCode))
            {
                q.Functions.Insert(0, entry);
                string flat = QueryShapes.Flat(entryCode);
                q.Globals = InOrder(flat, QueryShapes.QueryGlobals(flat, imageBase).Select(g => (g.At, (entry, g.Rva))).ToList(), updates);
                q.OtherDescriptions += QueryShapes.OtherDescriptions(flat);
                q.Strides.InsertRange(0, QueryShapes.Strides(entryCode).Select(v => (entry, v)));
            }
            else
                q.Globals = updates.SelectMany(u => u.Items).ToList();
        }

        // the registrations: every function that mentions one of the globals is read for the queries it registers
        var parsed = new Dictionary<long, List<QueryRegistration>>();
        foreach (long global in reading.Systems.SelectMany(q => q.DistinctGlobals).Distinct().Order())
        {
            string token = "DAT_" + (imageBase + global).ToString("x", CultureInfo.InvariantCulture);
            List<long> mentioning;
            try
            {
                mentioning = db.Query("SELECT f.rva FROM code_fts JOIN functions f ON f.id = code_fts.rowid WHERE code_fts MATCH ? AND f.binary_id = ?", $"\"{token}\" OR \"_{token}\"", targetId)
                    .Select(r => L(r[0])).Order().ToList();
            }
            catch (SqliteException)
            {
                reading.Notes.Add($"{s.Settings.DbPath} has no full-text index: no registration was read");
                break;
            }
            foreach (long rva in mentioning)
            {
                if (!parsed.TryGetValue(rva, out var found))
                    parsed[rva] = found = Function(targetId, rva) is var (_, code) ? QueryShapes.Registrations(QueryShapes.Flat(code), imageBase) : [];
                foreach (var registration in found.Where(r => r.Global == global))
                {
                    if (!reading.Registrations.TryGetValue(global, out var list))
                        reading.Registrations[global] = list = [];
                    if (!list.Any(r => r.Types.SequenceEqual(registration.Types, StringComparer.Ordinal)))
                        list.Add(registration);
                }
            }
        }

        // the macOS side
        if (sourceId == null)
        {
            reading.Notes.Add($"{source} is not loaded: no spec is known");
            return reading;
        }
        if (s.IndexNames(s.Settings, source, m => reading.Notes.Add(m.Trim())) is not var (ofAddress, ofSlot))
            return reading;
        var specOfSlot = new Dictionary<long, string>();
        var specOfFlatName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in ofAddress.Values)
            if (QuerySpec.OfSymbol(name) is string spec)
            {
                specOfFlatName.TryAdd(QueryShapes.FlatName(name), spec);
                if (!reading.Specs.ContainsKey(spec) && QuerySpec.Parse(spec) is { } lists)
                    reading.Specs[spec] = lists;
            }
        foreach (var (slot, name) in ofSlot)
            if (QuerySpec.OfSymbol(name) is string spec)
                specOfSlot[slot] = spec;
        reading.SpecsNamed = reading.Specs.Count;
        log($"{source}: {reading.SpecsNamed} queries named by its symbol table");

        const string head = "ecs::_private::SystemRegistrationHelper::SystemUpdate<";
        var entries = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var r in db.Query("SELECT rva, name FROM functions WHERE binary_id = ? AND name LIKE ?", sourceId, head + "%"))
        {
            string name = Py.Str(r[1]);
            if (name.EndsWith('>'))
                entries.TryAdd(PairCommand.Normalize(name[head.Length..^1]), L(r[0]));
        }
        foreach (var q in reading.Systems)
        {
            if (!entries.TryGetValue(PairCommand.Normalize(q.System), out long entry) || Function(sourceId.Value, entry) is not var (_, code))
                continue;
            q.MacosEntry = entry;
            q.MacosFunctions.Add(entry);
            string flat = QueryShapes.Flat(code);
            // the methods of the system the entry calls are read with it: the Windows build has them inlined
            var methods = new List<(string, List<string>)>();
            // (no ORDER BY in the statement: with one SQLite walks every call of the binary by the other index)
            foreach (long callee in db.Query("SELECT callee FROM calls WHERE binary_id = ? AND caller = ?", sourceId, entry).Select(r => L(r[0])).Distinct().Order().ToList())
            {
                if (!Py.Str(db.Scalar("SELECT name FROM functions WHERE binary_id = ? AND rva = ?", sourceId, callee)).StartsWith(q.System + "::", StringComparison.Ordinal)
                    || Function(sourceId.Value, callee) is not var (name, methodCode))
                    continue;
                q.MacosFunctions.Add(callee);
                methods.Add((name, QueryShapes.SpecMentions(QueryShapes.Flat(methodCode), specOfSlot, specOfFlatName).Select(m => m.Spec).ToList()));
            }
            q.Specs = InOrder(flat, QueryShapes.SpecMentions(flat, specOfSlot, specOfFlatName), methods);
        }
        return reading;
    }
}
