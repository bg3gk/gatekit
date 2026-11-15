using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace GateKit.Symbols.Decomp;

/// <summary>One small function keyed by one component's type index (a row of `ecs_helpers`).</summary>
/// <param name="Family">get, has, add, remove, registration or unknown.</param>
/// <param name="Returns">component (it hands back the component: its return type may be set), bool, nothing, or value.</param>
/// <param name="Confidence">high (two kinds of evidence agree), medium (one rule), none (unknown).</param>
/// <param name="Basis">What the family was read from, in words.</param>
internal sealed record EcsHelper(long Rva, string Family, string Type, string Returns, string Confidence, string Basis);

/// <summary>
/// `helpers`: the out-of-line helpers through which a system reaches a component. The compiler
/// keeps the engine's small templates (get a component, has it, add one, remove one, register the
/// type) as separate functions, each keyed by one `TypeIndex&lt;T&gt;` global; Ghidra knows them
/// only as FUN_ returning an untyped value. A helper is a function of at most
/// <see cref="MaxSize"/> bytes whose code refers to exactly one type-index global of the symbol
/// database (`ecs_ids` and `xrefs`), a component's; one no rule recognises is kept only when
/// something calls it directly (else it is a system's own method, called through its table). Its family is read from what can be established
/// without reading the code as a person would: whether it writes the index, whether it allocates
/// component frame storage (and with the record's size of the type), whether it looks the
/// component up through the engine's own named functions, and what it returns. Each family is
/// then checked against the macOS build, whose templates carry their names
/// (`ecs::EntityWorld::GetComponent&lt;T&gt;` and the like): a helper whose type the macOS build
/// has the same family of is `high`. Whatever no rule establishes is `unknown`.
/// </summary>
internal static class HelpersCommand
{
    /// <summary>The largest function taken for a helper: the engine's add (with its inlined lookup) stays under it.</summary>
    public const int MaxSize = 1600;

    static readonly HashSet<string> ComponentContexts = new(StringComparer.Ordinal)
    {
        "ecs::ComponentTypeIdContext", "ecs::UncheckedComponentTypeIdContext", "ecs::OneFrameComponentTypeIdContext",
    };

    const string AllocRaw = "ecs::ComponentFrameStorageAllocRaw";
    const string GetChange = "ecs::legacy::ImmediateWorldCache::GetChange";
    const string IsNull = "ecs::EntityRef::IsNull";

    /// <summary>The macOS templates of each family, by the start of their names.</summary>
    static readonly (string Family, string Prefix)[] MacTemplates =
    [
        ("get", "ecs::EntityWorld::GetComponent<"), ("get", "ecs::legacy::ImmediateWorldCache::GetComponent<"),
        ("has", "ecs::EntityWorld::HasComponent<"), ("has", "ecs::EntityRef::Has<"),
        ("add", "ecs::legacy::ImmediateWorldCache::AddComponent<"), ("add", "ecs::EntityCommandBuffer::AccessAddStorage<"),
        ("add", "ecs::EntityCommandBuffer::AddComponentUnchecked<"), ("add", "ecs::EntityCommandBuffer::AddComponent<"),
        ("add", "ecs::EntityWorld::CreateComponent<"),
        ("remove", "ecs::legacy::ImmediateWorldCache::RemoveComponent<"), ("remove", "ecs::EntityWorld::RemoveComponentImmediate<"),
        ("remove", "ecs::EntityCommandBuffer::RemoveComponent<"),
    ];

    /// <summary>The families a helper's name says, as the engine's own templates are called.</summary>
    public static string Name(string family, string type) => family switch
    {
        "get" => $"ecs::GetComponent<{type}>",
        "has" => $"ecs::HasComponent<{type}>",
        "add" => $"ecs::AddComponent<{type}>",
        "remove" => $"ecs::RemoveComponent<{type}>",
        "registration" => $"ecs::RegisterComponent<{type}>",
        _ => $"ecs::ComponentHelper<{type}>",
    };

    static long L(object? v) => Convert.ToInt64(v, CultureInfo.InvariantCulture);

    /// <summary>The first template argument of a name that starts with <paramref name="prefix"/>,
    /// without const (the macOS names say `T_const` or `T const`).</summary>
    internal static string? FirstArgument(string name, string prefix)
    {
        if (!name.StartsWith(prefix, StringComparison.Ordinal))
            return null;
        int depth = 0, i = prefix.Length;
        for (; i < name.Length; i++)
        {
            char c = name[i];
            if (c == '<')
                depth++;
            else if (c == '>' && depth-- == 0)
                break;
            else if (c == ',' && depth == 0)
                break;
        }
        var type = name[prefix.Length..i].Trim();
        foreach (var suffix in new[] { "_const", " const" })
            if (type.EndsWith(suffix, StringComparison.Ordinal))
                type = type[..^suffix.Length];
        return type.Length > 0 ? type : null;
    }

    /// <summary>The family and the type of a macOS template instance by its name, or null.</summary>
    internal static (string Family, string Type)? Template(string name)
    {
        if (!name.StartsWith("ecs::", StringComparison.Ordinal))
            return null;
        foreach (var (family, prefix) in MacTemplates)
            if (FirstArgument(name, prefix) is string t)
                return (family, t);
        return null;
    }

    /// <summary>The families the macOS build has an out-of-line template of, by type; and where they came from.</summary>
    internal static (Dictionary<string, HashSet<string>> Families, string Source) MacFamilies(DecompSession s, SqliteConnection db)
    {
        var families = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        void Add(string name)
        {
            foreach (var (family, prefix) in MacTemplates)
                if (FirstArgument(name, prefix) is string t)
                {
                    if (!families.TryGetValue(t, out var set))
                        families[t] = set = new HashSet<string>(StringComparer.Ordinal);
                    set.Add(family);
                }
        }
        if (db.Scalar("SELECT id FROM binaries WHERE name LIKE 'bg3!_macos%' ESCAPE '!' LIMIT 1") is { } mac)
        {
            foreach (var r in db.Query("SELECT name FROM functions WHERE binary_id = ? AND name LIKE 'ecs::%<%'", mac))
                Add(Py.Str(r[0]));
            if (families.Count > 0)
                return (families, "the macOS build's pseudo-code");
        }
        var tsv = s.Settings.MacosSymbols == null ? null : Path.Combine(s.Settings.MacosSymbols, "symbols-demangled.tsv");
        if (tsv != null && File.Exists(tsv))
        {
            foreach (var line in File.ReadLines(tsv))
            {
                var p = line.Split('\t');
                if (p.Length >= 3 && p[2].StartsWith("ecs::", StringComparison.Ordinal))
                    Add(p[2]);
            }
            return (families, "BG3_MACOS_SYMBOLS");
        }
        return (families, "nothing (no macOS build loaded, BG3_MACOS_SYMBOLS not set)");
    }

    /// <summary>The return type and the number of parameters of the function the pseudo-code declares.</summary>
    internal static (string Returns, int Parameters) Head(string code)
    {
        foreach (var raw in code.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("/*", StringComparison.Ordinal))
                continue;
            int open = line.IndexOf('(');
            if (open < 0)
                return ("", 0);
            var head = line[..open].TrimEnd();
            int space = head.LastIndexOf(' ');
            var returns = space < 0 ? "" : head[..space].Trim();
            int close = line.LastIndexOf(')');
            var args = close > open ? line[(open + 1)..close].Trim() : "";
            int parameters = args.Length == 0 || args == "void" ? 0 : args.Count(c => c == ',') + 1;
            return (returns, parameters);
        }
        return ("", 0);
    }

    static readonly Regex BoolReturn = new(@"\breturn CONCAT71\(", RegexOptions.Compiled);
    static readonly Regex AllocCall = new(Regex.Escape(AllocRaw) + @"\s*\(([^;]*?)\);", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>True when one of the arguments of a frame storage allocation is the size (in hex or decimal).</summary>
    internal static bool AllocatesSize(string code, long size)
    {
        var hex = new Regex(@"(?<![0-9a-fA-Fx])0x" + size.ToString("x", CultureInfo.InvariantCulture) + @"(?![0-9a-fA-F])");
        var dec = new Regex(@"(?<![\w.])" + size.ToString(CultureInfo.InvariantCulture) + @"(?![\w.])");
        foreach (Match m in AllocCall.Matches(code))
            if (hex.IsMatch(m.Groups[1].Value) || dec.IsMatch(m.Groups[1].Value))
                return true;
        return false;
    }

    static readonly string[] EnginePrefixes = ["ecs::", "ls::", "std::", "_", "`", "VCRUNTIME", "API-MS", "KERNEL", "atexit", "operator", "Concurrency::", "FUN_", "thunk_FUN_"];

    /// <summary>True for a callee the engine's templates call: the engine's own functions, the
    /// runtime's, unnamed ones, and the component's constructors and destructors.</summary>
    internal static bool EngineName(string name)
    {
        if (EnginePrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)))
            return true;
        int cut = name.LastIndexOf("::", StringComparison.Ordinal);
        if (cut < 0)
            return false;
        var owner = name[..cut];
        var method = name[(cut + 2)..].TrimStart('~');
        // X::X and X::~X (a member's type, often not the component's own)
        return owner.EndsWith("::" + method, StringComparison.Ordinal) || owner == method;
    }

    /// <summary>The family of one helper from what it calls, what it returns and whether it writes the index.</summary>
    internal static EcsHelper ClassifyOne(long rva, string type, string code, ISet<string> callees, bool writesIndex,
                                         long? layoutSize, IReadOnlySet<string>? mac)
    {
        var (returns, parameters) = Head(code);
        bool isVoid = returns == "void";
        bool isBool = returns is "bool" or "char" or "byte" or "uchar" or "undefined1" || BoolReturn.IsMatch(code);
        string family, basis, confidence = "medium";
        bool sizeMatch = false;
        if (writesIndex)
        {
            (family, basis, confidence) = ("registration", "writes the type index", "high");
        }
        else if (callees.Contains(AllocRaw))
        {
            family = "add";
            basis = "allocates component frame storage";
            if (layoutSize is long size && size > 0)
            {
                sizeMatch = AllocatesSize(code, size);
                basis += sizeMatch ? $" of the record's size of the type ({size} bytes)" : $" (the record's size, {size} bytes, is not among its arguments)";
            }
            if (isVoid)
                basis += "; returns nothing";
        }
        else if (isVoid && callees.Contains(GetChange))
        {
            // the engine's remove asks the immediate cache for the pending change of the component
            // first; a function that returns nothing after only looking the entity up does
            // something else with the component, and is left unknown
            (family, basis) = ("remove", $"returns nothing; looks the pending change of the component up ({GetChange})");
        }
        else if (!isVoid && (callees.Contains(GetChange) || callees.Contains(IsNull)))
        {
            var through = callees.Contains(GetChange) ? GetChange : IsNull;
            (family, basis) = isBool ? ("has", $"returns a bool; looks the component up ({through})")
                : ("get", $"returns a value; looks the component up ({through})");
        }
        else
        {
            (family, basis, confidence) = ("unknown", "no rule applies", "none");
        }
        if (family is "get" or "has" or "add" or "remove")
        {
            if (mac == null)
            {
                basis += "; the macOS build has no out-of-line template of the type";
                if (sizeMatch)
                    confidence = "high";
            }
            else if (mac.Contains(family))
            {
                confidence = "high";
                basis += $"; the macOS build has a {family} template of the type";
            }
            else
            {
                basis += $"; the macOS build has {string.Join("/", mac.Order(StringComparer.Ordinal))} templates of the type, not {family}";
                if (sizeMatch)
                    confidence = "high";
            }
        }
        string kind = isVoid ? "nothing" : isBool ? "bool"
            : family is "get" or "add" && parameters <= 3 ? "component" : "value";
        return new EcsHelper(rva, family, type, kind, confidence, basis);
    }

    /// <summary>Per family: helpers whose type the macOS build has any of these templates of, and of
    /// those, how many it has the same family of.</summary>
    internal sealed record Validation(SortedDictionary<string, (int Checked, int Agreed)> Families, string Source);

    /// <summary>Finds and classifies the helpers of one binary.</summary>
    public static (List<EcsHelper> Helpers, Validation Check) Classify(DecompSession s, SqliteConnection db, string binary, IReadOnlyDictionary<string, long> sizes)
    {
        var symbols = s.Symbols ?? throw new SymbolsException($"{s.Settings.SymbolsDb} does not exist: the type indices come from the symbol database (gatekit symbols build or install)");
        var dll = symbols.Scalar("SELECT id FROM dlls WHERE name = ?", binary) ?? throw new SymbolsException($"{binary} is not in the symbol database");
        var binaryId = db.Scalar("SELECT id FROM binaries WHERE name = ?", binary) ?? throw new SymbolsException($"{binary} is not loaded in {s.Settings.DbPath}");

        // every type index of the binary, any context: a helper refers to exactly one
        var index = new Dictionary<long, (string Type, string Context)>();
        foreach (var r in symbols.Query("SELECT index_rva, type, context FROM ecs_ids WHERE dll_id = ? AND context IS NOT NULL AND index_rva IS NOT NULL", dll))
            index.TryAdd(L(r[0]), (Py.Str(r[1]), Py.Str(r[2])));
        if (index.Count == 0)
            throw new SymbolsException($"the symbol database {s.Settings.SymbolsDb} has no type indices of {binary} (ecs_ids is empty for it), so no helper can be found. "
                                       + "It was built without a Script Extender tree: build it again with --bg3se or BG3SE_SRC (gatekit symbols build), or install a database that has them.");
        var chunks = new Dictionary<long, long>();
        foreach (var r in symbols.Query("SELECT rva, parent FROM chunks WHERE dll_id = ?", dll))
            chunks.TryAdd(L(r[0]), L(r[1]));
        var functions = new SortedList<long, (long Size, string Name)>();
        foreach (var r in db.Query("SELECT rva, size, name FROM functions WHERE binary_id = ?", binaryId))
            functions.TryAdd(L(r[0]), (r[1] == null ? 0 : L(r[1]), Py.Str(r[2])));
        var starts = functions.Keys;

        long? Owner(long rva)
        {
            if (functions.ContainsKey(rva))
                return rva;
            if (chunks.TryGetValue(rva, out var parent) && functions.ContainsKey(parent))
                return parent;
            int lo = 0, hi = starts.Count - 1, at = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (starts[mid] <= rva) { at = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return at >= 0 && starts[at] + functions.Values[at].Size > rva ? starts[at] : null;
        }

        var refs = new Dictionary<long, HashSet<long>>();
        var writes = new HashSet<(long, long)>();
        foreach (var r in symbols.Query("SELECT func_rva, target_rva, kind FROM xrefs WHERE dll_id = ?", dll))
        {
            long target = L(r[1]);
            if (!index.ContainsKey(target) || Owner(L(r[0])) is not long owner)
                continue;
            if (!refs.TryGetValue(owner, out var set))
                refs[owner] = set = [];
            set.Add(target);
            if (Py.Str(r[2]) == "w")
                writes.Add((owner, target));
        }
        if (refs.Count == 0)
            throw new SymbolsException($"no function of {binary} in {s.Settings.DbPath} refers to one of its {index.Count} type indices (symbols.sqlite `xrefs`), "
                                       + "so no helper can be found: are both databases of the same build of the game?");

        // the systems' own entries: a system's update or factory that touches one component is not a helper
        var entries = new HashSet<long>();
        try
        {
            foreach (var r in db.Query("SELECT thunk, updater FROM system_updates WHERE binary_id = ?", binaryId))
                foreach (var v in r)
                    if (v != null)
                        entries.Add(L(v));
            foreach (var r in db.Query("SELECT factory, constructor FROM system_factories WHERE binary_id = ?", binaryId))
                foreach (var v in r)
                    if (v != null)
                        entries.Add(L(v));
        }
        catch (SqliteException)
        {
            // no systems tables yet
        }

        var (mac, source) = MacFamilies(s, db);
        var found = new List<(EcsHelper Helper, List<long> Calls, bool Called)>();
        foreach (var (rva, targets) in refs.OrderBy(p => p.Key))
        {
            if (targets.Count != 1)
                continue;
            long target = targets.First();
            var (type, context) = index[target];
            if (!ComponentContexts.Contains(context) || functions[rva].Size > MaxSize)
                continue;
            if (entries.Contains(rva))
                continue;
            bool writesIndex = writes.Contains((rva, target));
            var code = DecompStore.Decompress(db.Scalar("SELECT c.text FROM functions f JOIN code c ON c.function_id = f.id WHERE f.binary_id = ? AND f.rva = ?", binaryId, rva)) ?? "";
            var callees = new HashSet<string>(StringComparer.Ordinal);
            var calls = new List<long>();
            foreach (var r in db.Query("SELECT c.callee, f.name FROM calls c INDEXED BY calls_caller LEFT JOIN functions f ON f.binary_id = c.binary_id AND f.rva = c.callee"
                                       + " WHERE c.binary_id = ? AND c.caller = ?", binaryId, rva))
            {
                calls.Add(L(r[0]));
                if (r[1] != null)
                    callees.Add(Py.Str(r[1]));
            }
            var helper = ClassifyOne(rva, type, code, callees, writesIndex, sizes.TryGetValue(type, out var size) ? size : null, mac.GetValueOrDefault(type));
            // a function the game's own code is called from, other than a component's constructor
            // or destructor, does more than the engine's template: no family
            if (helper.Family is "get" or "has" or "add" or "remove" && callees.FirstOrDefault(n => !EngineName(n)) is string game)
                helper = helper with { Family = "unknown", Returns = helper.Returns == "component" ? "value" : helper.Returns, Confidence = "none", Basis = $"calls the game's {game}: does more than a {helper.Family}" };
            bool called = db.Scalar("SELECT 1 FROM calls INDEXED BY calls_callee WHERE binary_id = ? AND callee = ? LIMIT 1", binaryId, rva) != null;
            found.Add((helper, calls, called));
        }

        // A helper is keyed by one type: a function that calls a helper of another type uses two
        // components and is left out. One nothing calls directly is a method called through a
        // table (a system's), except a registration (run at start-up) and a remove (the engine
        // calls those through its own tables).
        var byRva = found.ToDictionary(f => f.Helper.Rva, f => f.Helper);
        var helpers = new List<EcsHelper>();
        var check = new SortedDictionary<string, (int, int)>(StringComparer.Ordinal);
        foreach (var (helper, calls, called) in found)
        {
            if (calls.Any(c => byRva.TryGetValue(c, out var other) && other.Type != helper.Type))
                continue;
            if (!called && helper.Family is not ("registration" or "remove"))
                continue;
            helpers.Add(helper);
            if (helper.Family is "get" or "has" or "add" or "remove" && mac.TryGetValue(helper.Type, out var known))
            {
                var (n, agreed) = check.GetValueOrDefault(helper.Family);
                check[helper.Family] = (n + 1, agreed + (known.Contains(helper.Family) ? 1 : 0));
            }
        }
        return (helpers, new Validation(check, source));
    }

    /// <summary>What a sibling build knows of a helper this one could not classify: a helper of
    /// family unknown whose function is paired (gatekit decomp pair) with a helper of the same
    /// type and a known family takes that family, at confidence medium, with the pair as its basis.
    /// A family that was itself taken through a pair is not passed on. Returns how many were taken.</summary>
    internal static int FromPairs(SqliteConnection db, string binary, List<EcsHelper> helpers)
    {
        var partners = PairCommand.Partners(db, binary);
        if (partners.Count == 0)
            return 0;
        var others = new Dictionary<string, Dictionary<long, EcsHelper>>(StringComparer.Ordinal);
        int taken = 0;
        for (int i = 0; i < helpers.Count; i++)
        {
            var h = helpers[i];
            if (h.Family != "unknown" || !partners.TryGetValue(h.Rva, out var list))
                continue;
            foreach (var p in list)
            {
                if (!others.TryGetValue(p.Binary, out var theirs))
                    others[p.Binary] = theirs = Load(db, p.Binary);
                if (!theirs.TryGetValue(p.Rva, out var other) || other.Family == "unknown" || other.Type != h.Type || other.Basis.StartsWith("paired with ", StringComparison.Ordinal))
                    continue;
                helpers[i] = new EcsHelper(h.Rva, other.Family, h.Type, other.Returns, "medium", $"paired with {p.Binary}+0x{p.Rva:X} ({p.State} by {p.Rule}): {other.Basis}");
                taken++;
                break;
            }
        }
        return taken;
    }

    /// <summary>Replaces the binary's rows of `ecs_helpers`.</summary>
    public static void Store(SqliteConnection db, string binary, IEnumerable<EcsHelper> helpers)
    {
        db.Execute("BEGIN");
        db.Execute("DELETE FROM ecs_helpers WHERE dll = ?", binary);
        foreach (var h in helpers)
            db.Execute("INSERT INTO ecs_helpers (dll, rva, family, type, returns, confidence, basis) VALUES (?,?,?,?,?,?,?)",
                       binary, h.Rva, h.Family, h.Type, h.Returns, h.Confidence, h.Basis);
        db.Execute("COMMIT");
    }

    /// <summary>The helpers of a binary as stored, by RVA; empty when the table is missing.</summary>
    public static Dictionary<long, EcsHelper> Load(SqliteConnection db, string binary)
    {
        var helpers = new Dictionary<long, EcsHelper>();
        try
        {
            foreach (var r in db.Query("SELECT rva, family, type, returns, confidence, basis FROM ecs_helpers WHERE dll = ?", binary))
                helpers[L(r[0])] = new EcsHelper(L(r[0]), Py.Str(r[1]), Py.Str(r[2]), Py.Str(r[3]), Py.Str(r[4]), Py.Str(r[5]));
        }
        catch (SqliteException)
        {
            // a database made before the table existed
        }
        return helpers;
    }

    public static int Run(DecompSession s, string? type, bool rebuild, string binary, string? coverage)
    {
        var db = s.Db;
        if (rebuild || !SystemsCommand.HasRows(db, "ecs_helpers", binary, "dll"))
        {
            var folder = RecordTypes.FindCoverage(coverage);
            var sizes = folder == null ? new Dictionary<string, long>() : RecordTypes.Read(folder).Values.ToDictionary(t => t.Name, t => t.Size, StringComparer.Ordinal);
            var (helpers, check) = Classify(s, db, binary, sizes);
            if (helpers.Count == 0)
                throw new SymbolsException($"no helper of {binary} was recognised: ecs_helpers was left as it was. The type indices are there and referred to, "
                                           + "but no function that refers to one passes the rules (is the pseudo-code loaded whole?)");
            int taken = FromPairs(db, binary, helpers);
            // written on a copy that is moved over the database: a reader never sees half the helpers
            s.CloseDb();
            RefreshCommand.ThroughACopy(s.Settings, ".helpers", 60, copy => Store(copy, binary, helpers));
            db = s.Db;
            if (taken > 0)
                s.Err.WriteLine($"{taken} helpers no rule recognised take their family from the helper paired with them in another build");
            s.Err.WriteLine($"{helpers.Count} helpers of {binary}: "
                            + string.Join(", ", helpers.GroupBy(h => h.Family).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}")));
            s.Err.WriteLine($"checked against {check.Source}: "
                            + (check.Families.Count == 0 ? "nothing to check"
                                : string.Join(", ", check.Families.Select(p => string.Create(CultureInfo.InvariantCulture,
                                    $"{p.Key} {p.Value.Agreed} of {p.Value.Checked} ({100.0 * p.Value.Agreed / Math.Max(1, p.Value.Checked):F1} %)")))));
        }
        var rows = db.Query("SELECT rva, family, type, returns, confidence, basis FROM ecs_helpers WHERE dll = ? AND type LIKE ? ORDER BY type, family, rva",
                            binary, "%" + (type ?? "") + "%").ToList();
        if (type == null)
        {
            foreach (var g in rows.GroupBy(r => Py.Str(r[1])).OrderByDescending(g => g.Count()))
                s.Out.WriteLine($"{g.Key,-13} {g.Count(),6} helpers, {g.Select(r => Py.Str(r[2])).Distinct().Count(),5} types, "
                                + $"{g.Count(r => Py.Str(r[4]) == "high")} high, {g.Count(r => Py.Str(r[3]) == "component")} return the component");
            return 0;
        }
        foreach (var r in rows)
            s.Out.WriteLine($"{binary}+0x{L(r[0]):X}  {Name(Py.Str(r[1]), Py.Str(r[2]))}  [{Py.Str(r[1])}, {Py.Str(r[4])}; returns {Py.Str(r[3])}]  {Py.Str(r[5])}");
        if (rows.Count == 0)
            s.Err.WriteLine("nothing found");
        return 0;
    }
}
