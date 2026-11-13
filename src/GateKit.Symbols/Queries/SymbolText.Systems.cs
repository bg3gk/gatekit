using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GateKit.Symbols.Queries;

internal static partial class SymbolText
{
    /// <summary>Splits template arguments on the commas that are not inside &lt;&gt; or ().</summary>
    static List<string> SplitArgs(string text)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch is '<' or '(')
                depth++;
            else if (ch is '>' or ')')
                depth--;
            else if (ch == ',' && depth == 0)
            {
                parts.Add(text[start..i]);
                start = i + 1;
            }
        }
        parts.Add(text[start..]);
        return parts.Select(Decomp.Py.Strip).Where(p => p.Length > 0).ToList();
    }

    [GeneratedRegex(@"ecs::(?:WorldView|EntityRefView|View)<")]
    private static partial Regex WorldView();

    [GeneratedRegex(@"^(class|struct|enum) ")]
    private static partial Regex LeadingKeyword();

    [GeneratedRegex(@"^(class|struct) ")]
    private static partial Regex ClassKeyword();

    [GeneratedRegex(@"ecs::(WorldView|EntityRefView)<.*?>(?= [&*]|,|\))")]
    private static partial Regex ViewArguments();

    /// <summary>{component: read | write} named in the WorldView / View parameters of a declaration.</summary>
    internal static Dictionary<string, string> ComponentsOf(string signature)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in WorldView().Matches(signature))
        {
            int depth = 1, i = m.Index + m.Length;
            while (i < signature.Length && depth > 0)
            {
                if (signature[i] == '<')
                    depth++;
                if (signature[i] == '>')
                    depth--;
                i++;
            }
            int end = m.Index + m.Length;
            foreach (var arg in SplitArgs(signature[end..Math.Max(end, i - 1)]))
            {
                bool isConst = arg.EndsWith(" const", StringComparison.Ordinal) || (arg + " ").Contains(" const ", StringComparison.Ordinal);
                string name = Decomp.Py.Strip(LeadingKeyword().Replace(arg.Replace(" const", ""), ""));
                if (name.Length > 0 && result.GetValueOrDefault(name) != "write")
                    result[name] = isConst ? "read" : "write";
            }
        }
        return result;
    }

    /// <summary>One ECS system from every angle: registration, components, methods, virtual table, game addresses.</summary>
    public static void System(SymbolContext c, string name, QueryOptions o)
    {
        var game = c.Game(o.Dll);
        var names = c.Db.Query("SELECT DISTINCT type FROM type_names WHERE kind = 'system' AND (type = ? OR type = ? OR type = ?)",
                               "class " + name, "struct " + name, name).Select(r => PyText.Str(r[0])).ToList();
        if (names.Count == 0)
        {
            var rows = c.Db.Query("SELECT DISTINCT type FROM type_names WHERE kind = 'system' AND type LIKE ? ESCAPE '\\' ORDER BY type LIMIT ?",
                                  Sql.Like(name), o.Limit).Select(r => PyText.Str(r[0])).ToList();
            if (rows.Count != 1)
            {
                foreach (var r in rows)
                    c.WriteLine(r);
                c.Note(rows.Count > 0 ? $"{rows.Count} systems match" : "no such system in the game");
                return;
            }
            names = [rows[0]];
        }
        string sys = ClassKeyword().Replace(names[0], "");
        int cut = sys.LastIndexOf("::", StringComparison.Ordinal);
        string ns = cut >= 0 ? sys[..cut] : "", cls = cut >= 0 ? sys[(cut + 2)..] : sys;
        long gameId = game?.Id ?? -1;
        string gameName = game?.Name ?? "";
        c.WriteLine($"system {sys}");
        if (game != null)
        {
            foreach (var r in c.Db.Query("SELECT index_rva, context FROM ecs_ids WHERE dll_id = ? AND type = ? GROUP BY index_rva", gameId, sys).ToList())
                c.WriteLine($"  registered in {(PyText.Truthy(r[1]) ? PyText.Str(r[1]) : "?")}; its index is kept at {gameName}+0x{X(PyText.L(r[0]))} (assigned when the game starts)");
            foreach (var r in c.Db.Query("SELECT func_rva FROM type_names WHERE dll_id = ? AND type = ? AND func_rva IS NOT NULL", gameId, names[0]).ToList())
                c.WriteLine($"  name string returned by {gameName}+0x{X(PyText.L(r[0]))}");
        }
        var t = c.Db.One("SELECT ext_name, file, line FROM se_types WHERE engine_class = ?", sys);
        if (t != null)
            c.WriteLine($"  Script Extender: Ext.System.{PyText.Str(t[0])}  (fields: gatekit symbols layout {sys})   [{PyText.Str(t[1])}:{PyText.Str(t[2])}]");
        var methods = c.Db.Query("SELECT s.id, d.name, s.dll_id, s.rva, s.signature, s.method, s.kind FROM symbols s JOIN dlls d ON d.id = s.dll_id"
                                 + " WHERE s.class = ? AND s.namespace = ? ORDER BY s.kind, s.method", cls, ns).ToList();
        var access = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var mr in methods)
            foreach (var (comp, how) in ComponentsOf(PyText.Str(mr[4])))
                if (access.GetValueOrDefault(comp) != "write")
                    access[comp] = how;
        if (access.Count > 0)
        {
            c.WriteLine($"  components it touches, from the parameter types of its methods ({access.Count}):");
            foreach (var how in new[] { "write", "read" })
            {
                var group = access.Where(p => p.Value == how).Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
                if (group.Count > 0)
                    c.WriteLine($"    {how}: " + string.Join(", ", group));
            }
        }
        if (methods.Count > 0)
            c.WriteLine($"  methods the Toolkit exports ({methods.Count}), with the game address where the function was found:");
        foreach (var mr in methods)
        {
            if (PyText.Str(mr[6]) == "vftable")
                continue;
            var hit = mr[3] != null
                ? c.Db.One("SELECT m.game_rva, m.method, m.relation FROM matches m WHERE m.toolkit_dll = ? AND m.toolkit_rva = ? AND m.game_dll = ?", mr[2], mr[3], gameId)
                : null;
            string signature = PyText.Str(mr[4]);
            string shortSig = o.Full ? signature : ViewArguments().Replace(signature, "ecs::$1<...>");
            shortSig = o.Full || shortSig.Length < 200 ? shortSig : Decomp.Py.Left(shortSig, 197) + "...";
            string where = hit != null
                ? $"{gameName}+0x{X(PyText.L(hit[0]))} [{PyText.Str(hit[1])}{(PyText.Truthy(hit[2]) && PyText.Str(hit[2]) != "same" ? ", " + PyText.Str(hit[2]) : "")}]"
                : "-";
            c.WriteLine($"    {where,-34} {shortSig}");
        }
        foreach (var v in c.Db.Query("SELECT v.id, d.name, v.rva, v.slots FROM vtabs v JOIN symbols s ON s.id = v.symbol_id JOIN dlls d ON d.id = v.dll_id"
                                     + " WHERE s.class = ? AND s.namespace = ?", cls, ns).ToList())
        {
            var g = c.Db.One("SELECT gv.rva, m.support FROM vtab_matches m JOIN vtabs gv ON gv.id = m.game_vtab WHERE m.toolkit_vtab = ? AND gv.dll_id = ?", v[0], gameId);
            c.WriteLine($"  virtual table: {PyText.Str(v[1])}+0x{X(PyText.L(v[2]))}, {PyText.Str(v[3])} slots; in the game: "
                        + (g != null ? $"{gameName}+0x{X(PyText.L(g[0]))} (recognised by {PyText.Str(g[1])} slots)" : "not recognised")
                        + $"   (gatekit symbols vtable {sys})");
        }
        if (game != null)
        {
            var index = c.Db.One("SELECT index_rva FROM ecs_ids WHERE dll_id = ? AND type = ? AND context LIKE '%SystemsContext'", gameId, sys);
            var rows = c.Db.Query("SELECT x.func_rva, group_concat(x.kind, ''), f.size FROM xrefs x LEFT JOIN functions f ON f.dll_id = x.dll_id"
                                  + " AND f.rva = x.func_rva WHERE x.dll_id = ? AND x.target_rva = ? GROUP BY x.func_rva ORDER BY f.size",
                                  gameId, index != null ? index[0] : -1).ToList();
            if (rows.Count > 0)
                c.WriteLine($"  game functions that use the system's index ({rows.Count}): "
                            + string.Join(", ", rows.Take(12).Select(r => $"{gameName}+0x{X(PyText.L(r[0]))} ({PyText.Str(r[2])} b)")) + (rows.Count > 12 ? " ..." : ""));
        }
        foreach (var (factory, constructor, table, note) in c.Decomp.CreatedIn(gameName, sys))
        {
            c.WriteLine($"  created in {gameName}+0x{X(factory)}" + (constructor is long k && k != 0 ? $", constructor {gameName}+0x{X(k)}" : "")
                        + (!string.IsNullOrEmpty(note) ? $"   ({note})" : "") + "   [read from pseudo-code, to check]");
            if (table is long tv && tv != 0)
            {
                c.WriteLine($"  its virtual table in the game: {gameName}+0x{X(tv)}:");
                var row = c.Db.One("SELECT id FROM vtabs WHERE dll_id = ? AND rva = ?", gameId, tv);
                foreach (var s in c.Db.Query("SELECT idx, rva FROM vtab_slots WHERE vtab_id = ? ORDER BY idx", row != null ? row[0] : -1).ToList())
                {
                    long slot = PyText.L(s[1]);
                    c.WriteLine($"    [{PyText.L(s[0]),2}] {c.Label(gameId, slot),-50} {c.WholeSize(gameId, slot) ?? 0,6} bytes");
                }
                if (row == null)
                    c.WriteLine($"    (gatekit symbols vtable {gameName}+0x{X(tv)})");
            }
        }
        foreach (var (thunk, updater, why) in c.Decomp.Updates(gameName, sys))
            c.WriteLine("  per-frame update: " + (updater != null ? $"{gameName}+0x{X(updater.Value)}  ({c.WholeSize(gameId, updater.Value) ?? 0} bytes)" : "?")
                        + $"   entry the factory stores: {gameName}+0x{X(thunk)}; {PyText.Str(why)}   [read from pseudo-code, to check]");
        long queries = PyText.L(c.Db.Scalar("SELECT count(*) FROM type_names WHERE kind = 'query' AND dll_id = ? AND type LIKE ? ESCAPE '\\'", gameId, Sql.Like(sys)));
        if (queries > 0)
            c.WriteLine($"  {queries} ECS queries of the game mention this type   (gatekit symbols types \"{sys}\" --kind query)");
    }

    [GeneratedRegex(@"FieldMeta<(.+?) ([\w:]+)::\*,(\d+)>")]
    private static partial Regex FieldMeta();

    [GeneratedRegex(@"\b(class|struct|enum|union) ")]
    private static partial Regex AnyKeyword();

    [GeneratedRegex(@"\b(class|struct|enum) ")]
    private static partial Regex Keyword3();

    [GeneratedRegex(@"= &PTR_\w+?_(1[0-9a-f]{8});")]
    private static partial Regex TableAssignment();

    [GeneratedRegex(@"\(\w+ \+ 0xe8,(0x[0-9a-f]+|\d+),0,")]
    private static partial Regex ObjectSize();

    /// <summary>A type name reduced to what MSVC and clang spell the same way, for comparing them.</summary>
    static string PlainType(string text)
    {
        text = AnyKeyword().Replace(text, "");
        text = text.Replace("unsigned __int64", "unsigned long long").Replace("__int64", "long long");
        return Spaces().Replace(text, "");
    }

    /// <summary>{name: kind} of one class from a JSON file of the macOS references, in file order.</summary>
    static List<(string Field, string Kind)> MacFields(string? file, string name)
    {
        var list = new List<(string, string)>();
        if (file == null || !File.Exists(file))
            return list;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out var cls) && cls.ValueKind == JsonValueKind.Object)
                foreach (var p in cls.EnumerateObject())
                    list.Add((p.Name, p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText()));
        }
        catch (JsonException)
        {
        }
        return list;
    }

    /// <summary>A replicated component in one answer: where the game registers its replication, the
    /// handler objects and their virtual tables, the fields the game names by type and offset, and
    /// the field names the macOS build carries for the same class.</summary>
    public static void Replication(SymbolContext c, string name, string? dll, bool code)
    {
        var game = c.Game(dll) ?? throw new SymbolsException($"no game executable matches {PyText.Repr(dll ?? "bg3.exe")}");
        var index = c.Db.One("SELECT index_rva FROM ecs_ids WHERE dll_id = ? AND type = ? AND context LIKE '%ReplicatedTypeContext' LIMIT 1", game.Id, name);
        c.WriteLine($"component {name}");
        if (index == null)
            c.WriteLine("  not registered for replication in this executable (no ReplicatedTypeContext index)");
        var d = c.Decomp.Db;
        if (index != null)
        {
            long indexRva = PyText.L(index[0]);
            c.WriteLine($"  replication index kept at {game.Name}+0x{X(indexRva)}");
            var users = c.Db.Query("SELECT func_rva FROM xrefs WHERE dll_id = ? AND target_rva = ?", game.Id, indexRva)
                .Select(r => c.Whole(game.Id, PyText.L(r[0]))).Distinct().Order().ToList();
            if (d == null)
                c.WriteLine("  (no pseudo-code database: set BG3_DECOMP_WORK to see the handler objects)");
            var shown = new HashSet<long>();
            foreach (var func in users)
            {
                string text = c.Decomp.PseudoCode(game.Name, func);
                var tables = TableAssignment().Matches(text).Select(m => long.Parse(m.Groups[1].Value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture) - 0x140000000).ToList();
                var size = ObjectSize().Match(text);
                if (tables.Count == 0)
                    continue;
                c.WriteLine($"  {c.Label(game.Id, func)}  ({c.WholeSize(game.Id, func) ?? 0} bytes) creates an object"
                            + (size.Success ? $" of {PyText.Int0(size.Groups[1].Value)} bytes" : "") + " with the virtual table:");
                foreach (var table in tables)
                {
                    if (!shown.Add(table))
                        continue;
                    var row = c.Db.One("SELECT id FROM vtabs WHERE dll_id = ? AND rva = ?", game.Id, table);
                    var slots = c.Db.Query("SELECT rva FROM vtab_slots WHERE vtab_id = ? ORDER BY idx", row != null ? row[0] : -1).Select(r => PyText.L(r[0])).ToList();
                    c.WriteLine($"    {game.Name}+0x{X(table)}  {slots.Count} slots" + (slots.Count > 0 ? "" : "  (gatekit symbols vtable ...)"));
                    for (int i = 0; i < slots.Count; i++)
                        c.WriteLine($"      [{i}] {c.Label(game.Id, slots[i]),-46} {c.WholeSize(game.Id, slots[i]) ?? 0,6} bytes");
                    if (code)
                        foreach (var slot in slots.Skip(1).Take(2))
                        {
                            string body = c.Decomp.PseudoCode(game.Name, slot);
                            c.WriteLine($"\n// ---- slot function {game.Name}+0x{X(slot)}\n" + (body.Length > 0 ? body : "// no pseudo-code"));
                        }
                }
            }
        }
        // fields the game names itself
        var windows = new SortedDictionary<long, string>();
        foreach (var r in c.Db.Query("SELECT DISTINCT text FROM strings WHERE dll_id = ? AND text LIKE '%FieldMeta<%' AND text LIKE ?",
                                     game.Id, "%" + name + "::*%").ToList())
            foreach (Match m in FieldMeta().Matches(PyText.Str(r[0])))
                if (m.Groups[2].Value == name)
                    windows[long.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture)] = Decomp.Py.Strip(Keyword3().Replace(m.Groups[1].Value, ""));
        // names from the macOS build, as the coverage project extracted them
        string? macFile = c.Env.MacosSymbols == null ? null : Path.Combine(c.Env.MacosSymbols, "fieldmeta.json");
        var mac = MacFields(macFile, name);
        var byType = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (field, kind) in mac)
        {
            string key = PlainType(kind);
            if (!byType.TryGetValue(key, out var list))
                byType[key] = list = [];
            list.Add(field);
        }
        var used = new HashSet<string>(StringComparer.Ordinal);
        if (windows.Count > 0)
        {
            c.WriteLine("  replicated collection fields named by the game itself (type and offset from ls::FieldMeta<Type Class::*, offset>):");
            foreach (var (offset, type) in windows)
            {
                var candidates = byType.GetValueOrDefault(PlainType(type)) ?? [];
                string? field = candidates.Count == 1 ? candidates[0] : null;
                if (field != null)
                    used.Add(field);
                string note = field ?? (candidates.Count > 0 ? "one of " + string.Join(", ", candidates) : "?");
                c.WriteLine($"    +0x{PyText.Hex(offset, 3)}  {note,-44} {type}");
            }
        }
        var rest = mac.Where(f => !used.Contains(f.Field)).ToList();
        if (rest.Count > 0)
        {
            c.WriteLine($"  field names the macOS build has for this class, in its order ({(used.Count > 0 ? "the rest; " : "")}no offset known from here):");
            foreach (var (field, kind) in rest)
                c.WriteLine($"           {field,-44} {kind}");
        }
        if (mac.Count == 0)
            c.WriteLine("  no macOS field names for this class" + (macFile != null && File.Exists(macFile) ? ""
                : macFile != null ? $" ({macFile} not found)" : " (BG3_MACOS_SYMBOLS is not set)"));
    }

    static readonly (string Key, Regex Pattern)[] ModelFacts =
    [
        ("name", new Regex("""\+ 8\) = "([^"]+)";""", RegexOptions.Compiled)),
        ("size", new Regex(@"\+ 0x18\) = (-?\w+);", RegexOptions.Compiled)),
        ("alignment", new Regex(@"\+ 0x20\) = (-?\w+);", RegexOptions.Compiled)),
        ("hash", new Regex(@"\+ 0x28\) = (-?\w+);", RegexOptions.Compiled)),
    ];

    [GeneratedRegex(@"<(ls::model::[\w:]+)>")]
    private static partial Regex ModelArgument();

    [GeneratedRegex(@",(ls::model::[\w:]+)>")]
    private static partial Regex OlderModel();

    /// <summary>What the builds say about how a component is written to a savegame: save/load
    /// functions of the macOS build, model versions with name, size and alignment, the Windows
    /// functions that use each model's type index, converters between versions.</summary>
    public static void SaveModel(SymbolContext c, string text)
    {
        var d = c.Decomp.Db;
        const string source = "bg3_macos_x64";
        object? binaryId = null;
        try
        {
            binaryId = d?.Scalar("SELECT id FROM binaries WHERE name = ?", source);
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
        }
        if (d == null || binaryId == null)
            throw new SymbolsException("needs the pseudo-code of the macOS build (decomp/README.md: the macOS and Linux builds)");
        int cutShort = text.LastIndexOf("::", StringComparison.Ordinal);
        string shortName = cutShort >= 0 ? text[(cutShort + 2)..] : text;
        long gameId = PyText.Long(c.Db.Scalar("SELECT id FROM dlls WHERE name = 'bg3.exe'")) ?? -1;

        List<(long Func, long Size, string What)> WindowsUsers(string model)
        {
            var index = c.Db.One("SELECT index_rva FROM ecs_ids WHERE dll_id = ? AND type = ? AND context LIKE '%model::TypeIdContext' LIMIT 1", gameId, model);
            var outList = new List<(long, long, string)>();
            if (index == null)
                return outList;
            var funcs = c.Db.Query("SELECT func_rva, kind FROM xrefs WHERE dll_id = ? AND target_rva = ? AND kind = 'r'", gameId, index[0])
                .Select(r => c.Whole(gameId, PyText.L(r[0]))).Distinct().Order().ToList();
            foreach (var func in funcs)
            {
                string code = c.Decomp.PseudoCode("bg3.exe", func);
                string what = code.Contains("ComponentFrameStorageAllocRaw", StringComparison.Ordinal) ? "Windows loader: copies each model offset into a component offset"
                    : code.Contains("ReallocateModels", StringComparison.Ordinal) || code.Contains("AddModels", StringComparison.Ordinal) ? "Windows saver"
                    : "uses the model's type index";
                outList.Add((func, c.WholeSize(gameId, func) ?? 0, what));
            }
            return outList.OrderBy(r => !r.Item3.StartsWith("Windows loader", StringComparison.Ordinal)).ThenBy(r => r.Item1).ToList();
        }

        // the functions that save and load the component
        var ops = d.Query("SELECT name, rva, size FROM functions WHERE binary_id = ? AND name LIKE ? ESCAPE '!' AND (name LIKE '%::Save' OR name LIKE '%::Load')"
                          + " ORDER BY name", binaryId, "ls::savegame::Framework::Ops<%" + text.Replace("_", "!_") + "%").ToList();
        var models = new HashSet<string>(StringComparer.Ordinal);
        if (ops.Count > 0)
            c.WriteLine($"saved through (macOS build; gatekit decomp code {source}+0x...):");
        foreach (var op in ops)
        {
            c.WriteLine($"  {source}+0x{X(PyText.L(op[1]))}  {PyText.Right(op[2], 6)} bytes  {PyText.Str(op[0])}");
            foreach (var callee in d.Query("SELECT f.name FROM calls c JOIN functions f ON f.binary_id = c.binary_id AND f.rva = c.callee"
                                           + " WHERE c.binary_id = ? AND c.caller = ? AND (f.name LIKE 'ls::model::Writer::AddModels<%' OR f.name LIKE 'ls::model::Reader::%<%')",
                                           binaryId, op[1]).ToList())
            {
                var m = ModelArgument().Match(PyText.Str(callee[0]));
                if (m.Success)
                    models.Add(m.Groups[1].Value);
            }
        }
        static string Last(string s)
        {
            int k = s.LastIndexOf("::", StringComparison.Ordinal);
            return k >= 0 ? s[(k + 2)..] : s;
        }
        if (models.Count > 0)
        {
            var own = models.Where(m => Last(m) == shortName).OrderBy(m => m, StringComparer.Ordinal).ToList();
            var nested = models.Where(m => Last(m) != shortName).OrderBy(m => m, StringComparer.Ordinal).ToList();
            if (own.Count > 0)
                c.WriteLine("  model it is copied into: " + string.Join(", ", own));
            if (nested.Count > 0)
                c.WriteLine("  other models written along with it: " + string.Join(", ", nested));
        }
        // every version of the models by that name
        var found = d.Query("SELECT name, rva FROM functions WHERE binary_id = ? AND name LIKE 'ls::model::Registry::RegisterType<ls::model::%'"
                            + " AND (name LIKE ?) ORDER BY name", binaryId, "%::" + shortName + ">").ToList();
        if (found.Count == 0)
            // a part of the name: models whose own name contains it
            found = d.Query("SELECT name, rva FROM functions WHERE binary_id = ? AND name LIKE 'ls::model::Registry::RegisterType<ls::model::%'"
                            + " AND name LIKE ? ORDER BY name LIMIT 40", binaryId, "%" + shortName + "%>").ToList();
        // A component's namespaces narrow the models down: esv::ready_check::StateComponent is saved
        // as ls::model::game::ready_check::vN::StateComponent, not as every model called StateComponent.
        var all = text.Split("::");
        var inner = all.Length > 2 ? all[1..^1] : [];
        if (inner.Length > 0)
        {
            string marker = "::" + string.Join("::", inner) + "::v";
            var narrowed = found.Where(f => PyText.Str(f[0]).Contains(marker, StringComparison.Ordinal)).ToList();
            if (narrowed.Count > 0)
                found = narrowed;
            else if (found.Count > 1)
                c.WriteLine($"(no model in a namespace {string.Join("::", inner)}; showing every model named {shortName})");
        }
        // Member names: the macOS symbols name the members of a model in the template arguments of
        // its visitor (ls::model::Spec<Model>::_Field_m_<Name>); the coverage project collected them.
        Dictionary<string, List<string>> memberNames = [];
        string? namesFile = c.Env.MacosSymbols == null ? null : Path.Combine(c.Env.MacosSymbols, "savefields.json");
        if (namesFile != null && File.Exists(namesFile))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(namesFile));
                foreach (var p in doc.RootElement.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.Array)
                        memberNames[p.Name] = p.Value.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText()).ToList();
            }
            catch (JsonException)
            {
                memberNames = [];
            }
        }
        if (found.Count > 0)
            c.WriteLine("model versions registered (name as written in a save, size and alignment in bytes):");
        foreach (var f in found)
        {
            long rva = PyText.L(f[1]);
            string code = c.Decomp.PseudoCode(source, rva);
            var facts = ModelFacts.ToDictionary(p => p.Key, p => { var m = p.Pattern.Match(code); return m.Success ? m.Groups[1].Value : null; });
            long? size = PyText.Int0(facts["size"] ?? ""), alignment = PyText.Int0(facts["alignment"] ?? ""), schema = PyText.Int0(facts["hash"] ?? "");
            string model = ModelArgument().Match(PyText.Str(f[0])).Groups[1].Value;
            c.WriteLine($"  {model}\n      \"{PyText.Str(facts["name"])}\"  size {PyText.Str(size)}  alignment {PyText.Str(alignment)}"
                        + (schema != null ? $"  schema hash 0x{unchecked((ulong)schema.Value):X16}" : "") + $"   [{source}+0x{X(rva)}]");
            if (memberNames.TryGetValue(model, out var members) && members.Count > 0)
                c.WriteLine($"      members in declaration order ({members.Count}): " + string.Join(", ", members));
            // the Windows side: the functions of bg3.exe that use this model's type index
            foreach (var (func, fsize, what) in WindowsUsers(model))
                c.WriteLine($"      bg3.exe+0x{X(func)}  {fsize,5} bytes  {what}");
            foreach (var conv in d.Query("SELECT name, rva, size FROM functions WHERE binary_id = ? AND name LIKE ? AND name LIKE '%::ConvertItems'",
                                         binaryId, "ls::model::conv::_private::Converter<" + model + ",%").ToList())
            {
                var older = OlderModel().Match(PyText.Str(conv[0]));
                c.WriteLine($"      built from {(older.Success ? older.Groups[1].Value : "?")} by {source}+0x{X(PyText.L(conv[1]))} ({PyText.Str(conv[2])} bytes)");
            }
        }
        if (ops.Count == 0 && found.Count == 0)
            c.Note(text.StartsWith("ecl::", StringComparison.Ordinal)
                ? "nothing found: client components (ecl::) are not saved - every save function belongs to esv::, eoc:: or ls::"
                : "nothing found: no save functions and no model by that name (the component may not be saved)");
        else
            c.WriteLine("member names are known for the models whose visitor is named in the macOS symbols (275 of 1,043); offsets are not:"
                        + " the Windows loader listed under a model copies each model offset into a Windows component offset, the macOS Save/Load"
                        + " functions show the macOS ones; model offsets are expected"
                        + " to be the same on every platform, since a save is read by all of them (not checked)");
    }
}
