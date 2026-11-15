using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace GateKit.Symbols.Decomp;

/// <summary>What `tally` counts for one binary. A count that could not be made (its source is
/// missing) is null, and <see cref="Notes"/> says why.</summary>
internal sealed class BinaryTally
{
    public string Binary = "";
    public long Functions, Named, DefaultNamed, WithCode;
    /// <summary>`p->Name` with a name somebody gave; `p->field_8`-like placeholders apart.</summary>
    public long NamedFieldAccesses, PlaceholderFieldAccesses;
    /// <summary>Of the named ones, those whose name is a field of a structure of the record.</summary>
    public long? RecordFieldAccesses;
    /// <summary>`(x + 0xNN)`: a dereference the decompiler had no structure for.</summary>
    public long RawDereferences;
    /// <summary>Functions that declare a local or a parameter of a structure the project was given.</summary>
    public long? TypedFunctions, TypedSignatures;
    public SortedDictionary<string, (int Count, int TypedReturn)> Helpers = new(StringComparer.Ordinal);
    public long? Vtables, VtablesNamed;
    public List<string> Notes = [];
}

/// <summary>The guards of the systems' digests, as `symbols digest --all` last counted them.</summary>
internal sealed record GuardTally(string Time, int Systems, int Operations, int Extracted, int Naming, int Always, int NotExtracted);

/// <summary>
/// `tally`: how much of a binary reads like code. Functions (all, named, still default-named),
/// field accesses (by name, and raw dereferences), functions with a typed component local or
/// parameter, the component helpers by family and how many have a typed return, virtual tables
/// (all, named), and the guards of the systems' digests. Reads only; every later improvement of
/// the pseudo-code is reported against these numbers.
/// </summary>
internal static partial class TallyCommand
{
    /// <summary>The file `symbols digest --all` leaves in its folder when it wrote every digest.</summary>
    public const string GuardsFile = "summary.json";

    [GeneratedRegex(@"->([A-Za-z_]\w*)")]
    private static partial Regex Arrow();

    [GeneratedRegex(@"\(\w+ \+ (?:0x[0-9a-fA-F]+|\d+)\)")]
    private static partial Regex RawDereference();

    [GeneratedRegex(@"\b([A-Za-z_]\w*) \*+ ?\w")]
    private static partial Regex PointerDeclaration();

    [GeneratedRegex(@"^(?:field\d*_|_Pad|_Base|_\d|unk|padding)", RegexOptions.IgnoreCase)]
    private static partial Regex Placeholder();

    /// <summary>True for a name the decompiler made up: FUN_ and the thunks of those.</summary>
    public static bool DefaultName(string name) =>
        name.Length == 0 || name.StartsWith("FUN_", StringComparison.Ordinal) || name.StartsWith("thunk_FUN_", StringComparison.Ordinal);

    /// <summary>The counts one function's pseudo-code adds: named and placeholder field accesses,
    /// raw dereferences, and whether a structure of <paramref name="structures"/> is declared (in
    /// the signature: 2, in the body only: 1, not at all: 0).</summary>
    internal static (int Named, int Placeholders, int Raw, int Typed, int Record) Count(string code, HashSet<string> structures, HashSet<string> fields)
    {
        int named = 0, placeholders = 0, record = 0;
        var isField = fields.GetAlternateLookup<ReadOnlySpan<char>>();
        foreach (var m in Arrow().EnumerateMatches(code))
        {
            var name = code.AsSpan(m.Index + 2, m.Length - 2);
            if (Placeholder().IsMatch(name))
                placeholders++;
            else
            {
                named++;
                if (isField.Contains(name))
                    record++;
            }
        }
        int raw = RawDereference().Count(code);
        int typed = 0;
        if (structures.Count > 0)
        {
            int body = code.IndexOf('{', StringComparison.Ordinal);
            var alt = structures.GetAlternateLookup<ReadOnlySpan<char>>();
            foreach (var m in PointerDeclaration().EnumerateMatches(code))
            {
                var span = code.AsSpan(m.Index, m.Length);
                var word = span[..span.IndexOf(' ')];
                if (alt.Contains(word))
                {
                    if (body < 0 || m.Index < body)
                        return (named, placeholders, raw, 2, record);
                    typed = 1;
                }
            }
        }
        return (named, placeholders, raw, typed, record);
    }

    /// <summary>The structures the binary's project was last given (`types.tsv` of its work
    /// folder): their names as the decompiler prints them, without the namespace, and the names
    /// of their fields that are not placeholders.</summary>
    internal static (HashSet<string> Names, HashSet<string> Fields)? Structures(string work, string binary)
    {
        var file = Path.Combine(work, binary, RefreshCommand.TypesState);
        if (!File.Exists(file))
            return null;
        var names = new HashSet<string>(StringComparer.Ordinal);
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(file, Encoding.UTF8))
        {
            var p = line.Split('\t');
            if (p.Length < 6 || p[0].Length == 0)
                continue;
            string name = p[0];
            int cut = name.Contains('<', StringComparison.Ordinal) ? -1 : name.LastIndexOf("::", StringComparison.Ordinal);
            names.Add(cut < 0 ? name : name[(cut + 2)..]);
            if (p[5] != RecordTypes.SizeOnly && !Placeholder().IsMatch(p[5]))
                fields.Add(p[5]);
        }
        return (names, fields);
    }

    public static BinaryTally Measure(DecompSession s, string binary, int jobs)
    {
        var db = s.Db;
        long id = Sql.Long(db.Scalar("SELECT id FROM binaries WHERE name = ?", binary))
                  ?? throw new SymbolsException($"{binary} is not loaded in {s.Settings.DbPath} (gatekit decomp status lists what is)");
        var t = new BinaryTally { Binary = binary };
        var structures = Structures(s.Settings.Work, binary);
        if (structures == null)
            t.Notes.Add($"typed locals not counted: {Path.Combine(s.Settings.Work, binary, RefreshCommand.TypesState)} does not exist (no refresh gave the project a structure)");
        var (known, fields) = structures ?? ([], []);

        long named = 0, placeholders = 0, raw = 0, typed = 0, signatures = 0, record = 0;
        var queue = new BlockingCollection<byte[]>(4096);
        var workers = Enumerable.Range(0, Math.Max(1, jobs)).Select(_ => Task.Run(() =>
        {
            long n = 0, p = 0, r = 0, ty = 0, sg = 0, rc = 0;
            foreach (var blob in queue.GetConsumingEnumerable())
            {
                var (a, b, c, d, e) = Count(DecompStore.Decompress(blob), known, fields);
                n += a; p += b; r += c; rc += e;
                if (d > 0) ty++;
                if (d == 2) sg++;
            }
            Interlocked.Add(ref named, n); Interlocked.Add(ref placeholders, p); Interlocked.Add(ref raw, r);
            Interlocked.Add(ref typed, ty); Interlocked.Add(ref signatures, sg); Interlocked.Add(ref record, rc);
        })).ToArray();
        try
        {
            foreach (var r in db.Query("SELECT f.name, c.text FROM functions f LEFT JOIN code c ON c.function_id = f.id WHERE f.binary_id = ?", id))
            {
                t.Functions++;
                if (DefaultName(Py.Str(r[0])))
                    t.DefaultNamed++;
                else
                    t.Named++;
                if (r[1] is byte[] blob)
                {
                    t.WithCode++;
                    queue.Add(blob);
                }
            }
        }
        finally
        {
            queue.CompleteAdding();
        }
        Task.WaitAll(workers);
        (t.NamedFieldAccesses, t.PlaceholderFieldAccesses, t.RawDereferences) = (named, placeholders, raw);
        if (structures != null)
            (t.TypedFunctions, t.TypedSignatures, t.RecordFieldAccesses) = (typed, signatures, record);

        // helpers: the families of ecs_helpers; a typed return is what the project was last given
        var returns = new HashSet<long>();
        var state = Path.Combine(s.Settings.Work, binary, RefreshCommand.HelpersState);
        if (File.Exists(state))
            foreach (var line in File.ReadLines(state, Encoding.UTF8))
            {
                var p = line.Split('\t');
                if (p.Length >= 5 && p[4].Length > 0 && Py.Hex(p[0]) is long rva)
                    returns.Add(rva);
            }
        foreach (var h in HelpersCommand.Load(db, binary).Values)
        {
            var (count, typedReturn) = t.Helpers.GetValueOrDefault(h.Family);
            t.Helpers[h.Family] = (count + 1, typedReturn + (returns.Contains(h.Rva) ? 1 : 0));
        }
        if (t.Helpers.Count == 0)
            t.Notes.Add($"no helpers: ecs_helpers has no row of {binary} (gatekit decomp helpers --rebuild --binary {binary})");

        // virtual tables: the symbol database's, named by a symbol, a matched Toolkit table or `vtable_names`
        if (s.Symbols is { } symbols && symbols.Scalar("SELECT id FROM dlls WHERE name = ?", binary) is { } dll)
        {
            try
            {
                t.Vtables = Sql.Long(symbols.Scalar("SELECT count(*) FROM vtabs WHERE dll_id = ?", dll));
                var namedTables = new HashSet<long>();
                foreach (var r in symbols.Query("SELECT rva FROM vtabs v WHERE dll_id = ? AND (symbol_id IS NOT NULL OR EXISTS (SELECT 1 FROM vtab_matches m WHERE m.game_vtab = v.id))", dll))
                    namedTables.Add(Convert.ToInt64(r[0], CultureInfo.InvariantCulture));
                try
                {
                    foreach (var r in db.Query("SELECT rva FROM vtable_names WHERE dll = ?", binary))
                        namedTables.Add(Convert.ToInt64(r[0], CultureInfo.InvariantCulture));
                }
                catch (SqliteException)
                {
                    // no vtable_names yet (gatekit decomp vtables)
                }
                t.VtablesNamed = namedTables.Count;
            }
            catch (SqliteException)
            {
                t.Notes.Add($"virtual tables not counted: {s.Settings.SymbolsDb} has no vtabs table");
            }
        }
        else
            t.Notes.Add($"virtual tables not counted: {binary} is not in the symbol database {s.Settings.SymbolsDb}");
        return t;
    }

    /// <summary>The guard counts `symbols digest --all` left in <paramref name="folder"/>, or null.</summary>
    internal static GuardTally? Guards(string folder)
    {
        var file = Path.Combine(folder, GuardsFile);
        if (!File.Exists(file))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
            var r = doc.RootElement;
            int I(string k) => r.TryGetProperty(k, out var v) && v.TryGetInt32(out int n) ? n : 0;
            return new GuardTally(r.TryGetProperty("time", out var t) ? t.GetString() ?? "" : "", I("systems"), I("operations"), I("extracted"), I("naming"), I("always"), I("not_extracted"));
        }
        catch (Exception e) when (e is JsonException or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Writes the guard counts of a whole `symbols digest --all` beside its digests.</summary>
    public static void WriteGuards(string folder, GuardTally g)
    {
        var o = new JsonObject
        {
            ["time"] = g.Time, ["systems"] = g.Systems, ["operations"] = g.Operations, ["extracted"] = g.Extracted,
            ["naming"] = g.Naming, ["always"] = g.Always, ["not_extracted"] = g.NotExtracted,
        };
        File.WriteAllText(Path.Combine(folder, GuardsFile), o.ToJsonString() + "\n", new UTF8Encoding(false));
    }

    static string Pct(long part, long whole) => whole <= 0 ? "-" : string.Create(CultureInfo.InvariantCulture, $"{100.0 * part / whole:F1} %");

    static string N(long? v) => v is long n ? n.ToString("N0", CultureInfo.InvariantCulture) : "not counted";

    public static int Run(DecompSession s, string[] binaries, bool json, int jobs)
    {
        var clock = Stopwatch.StartNew();
        var tallies = binaries.Select(b => Measure(s, Path.GetFileName(b), jobs)).ToList();
        var folder = Path.Combine(s.Settings.Work, "digests");
        var guards = Guards(folder);
        // the registry against what is known
        var store = CodeCommand.Store();
        var entries = store?.All() ?? [];
        var registryFunctions = tallies.Select(t =>
        {
            var build = store?.BuildFor(t.Binary, FunctionNames.BuildAt(s, t.Binary));
            var placed = build is null ? new HashSet<string>(StringComparer.Ordinal) : store!.Table(build, t.Binary).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
            return (t.Binary, Tally: Completeness.Functions(entries, placed, t.Functions));
        }).ToList();
        var (knownTypes, knownEnums) = Completeness.Known(s, RecordTypes.FindCoverage(null));
        var others = Completeness.Others(entries, knownTypes, knownEnums);
        if (json)
        {
            var root = new JsonObject();
            var list = new JsonArray();
            foreach (var t in tallies)
            {
                var helpers = new JsonObject();
                foreach (var (family, (count, typedReturn)) in t.Helpers)
                    helpers[family] = new JsonObject { ["count"] = count, ["typed_return"] = typedReturn };
                list.Add(new JsonObject
                {
                    ["binary"] = t.Binary,
                    ["functions"] = new JsonObject { ["total"] = t.Functions, ["named"] = t.Named, ["default_named"] = t.DefaultNamed, ["with_code"] = t.WithCode },
                    ["fields"] = new JsonObject { ["named_accesses"] = t.NamedFieldAccesses, ["record_field_accesses"] = t.RecordFieldAccesses, ["placeholder_accesses"] = t.PlaceholderFieldAccesses, ["raw_dereferences"] = t.RawDereferences },
                    ["typed"] = new JsonObject { ["functions"] = t.TypedFunctions, ["in_signature"] = t.TypedSignatures },
                    ["helpers"] = helpers,
                    ["vtables"] = new JsonObject { ["total"] = t.Vtables, ["named"] = t.VtablesNamed },
                    ["notes"] = new JsonArray([.. t.Notes.Select(n => (JsonNode)n)]),
                });
            }
            root["binaries"] = list;
            root["guards"] = guards == null ? null : new JsonObject
            {
                ["time"] = guards.Time, ["systems"] = guards.Systems, ["operations"] = guards.Operations, ["extracted"] = guards.Extracted,
                ["naming"] = guards.Naming, ["always"] = guards.Always, ["not_extracted"] = guards.NotExtracted,
            };
            var registry = new JsonObject();
            var functions = new JsonObject();
            foreach (var (binary, tally) in registryFunctions) functions[binary] = tally.ToJson();
            registry["functions"] = functions;
            foreach (var t in others) registry[t.Kind] = t.ToJson();
            root["registry"] = store is null ? null : registry;
            root["seconds"] = Math.Round(clock.Elapsed.TotalSeconds, 1);
            s.Out.WriteLine(root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, IndentSize = 1 }).ReplaceLineEndings("\n"));
            return 0;
        }
        foreach (var t in tallies)
        {
            s.Out.WriteLine(t.Binary);
            s.Out.WriteLine($"  functions            {N(t.Functions)}: {N(t.Named)} named ({Pct(t.Named, t.Functions)}), {N(t.DefaultNamed)} default-named ({Pct(t.DefaultNamed, t.Functions)})");
            s.Out.WriteLine($"  field accesses       {N(t.NamedFieldAccesses)} by name" + (t.RecordFieldAccesses is long rf ? $" ({N(rf)} by a field name of the record)" : "")
                            + $", {N(t.PlaceholderFieldAccesses)} by a placeholder name, {N(t.RawDereferences)} raw dereferences"
                            + $" ({Pct(t.NamedFieldAccesses, t.NamedFieldAccesses + t.PlaceholderFieldAccesses + t.RawDereferences)} by name)");
            s.Out.WriteLine(t.TypedFunctions is long tf ? $"  typed functions      {N(tf)} declare a local or a parameter of a record structure ({N(t.TypedSignatures)} in the signature)"
                : "  typed functions      not counted");
            if (t.Helpers.Count > 0)
            {
                s.Out.WriteLine($"  helpers              {N(t.Helpers.Values.Sum(h => h.Count))}, {N(t.Helpers.Values.Sum(h => h.TypedReturn))} with a typed return");
                foreach (var (family, (count, typedReturn)) in t.Helpers.OrderByDescending(h => h.Value.Count))
                    s.Out.WriteLine($"    {family,-13}      {count,6}  typed return {typedReturn,5}, without {count - typedReturn,5}");
            }
            s.Out.WriteLine($"  virtual tables       {N(t.Vtables)}: {N(t.VtablesNamed)} named" + (t.Vtables is long v && t.VtablesNamed is long vn ? $" ({Pct(vn, v)})" : ""));
            foreach (var note in t.Notes)
                s.Out.WriteLine("  note: " + note);
        }
        s.Out.WriteLine(guards == null
            ? $"digest guards          not counted: {Path.Combine(folder, GuardsFile)} does not exist (gatekit symbols digest --all --force writes it)"
            : $"digest guards          {N(guards.Operations)} operations of {N(guards.Systems)} systems: a guard for {N(guards.Extracted)}, {N(guards.Naming)} naming a component, a field or a call"
              + $" ({Pct(guards.Naming, guards.Operations)}), {N(guards.Always)} unconditional, {N(guards.NotExtracted)} not extracted  [as of {guards.Time}]");
        if (store is null)
            s.Out.WriteLine("the registry           not counted: no registry found (run inside a checkout, or set GATEKIT_REGISTRY)");
        else
        {
            s.Out.WriteLine("the registry, described against known");
            foreach (var (binary, tally) in registryFunctions)
                s.Out.WriteLine(tally.Line().Replace("  functions    ", $"  functions of {binary}: ", StringComparison.Ordinal));
            foreach (var t in others) s.Out.WriteLine(t.Line());
        }
        s.Err.WriteLine(string.Create(CultureInfo.InvariantCulture, $"counted in {clock.Elapsed.TotalSeconds:F1} s"));
        return 0;
    }
}
