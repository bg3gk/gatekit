using System.Text;

namespace GateKit.Symbols.Queries;

/// <summary>Common options of the searching verbs (symbols.py's query() options).</summary>
internal sealed record QueryOptions(string? Dll = null, string? Kind = null, int Limit = 200, bool Mangled = false, bool Full = false);

/// <summary>The text output of every query verb of `gatekit symbols`, line for line what symbols.py printed.</summary>
internal static partial class SymbolText
{
    static string X(long v) => PyText.Hex(v);

    static void Show(SymbolContext c, List<object?[]> rows, QueryOptions o, long? total = null)
    {
        foreach (var r in rows)
        {
            string dll = PyText.Str(r[0]);
            string where = r[1] != null ? $"{dll}+0x{X(PyText.L(r[1]))}" : $"{dll} -> {PyText.Str(r[4])}";
            string signature = PyText.Str(r[2]);
            if (!o.Full)
                signature = PyText.Cut(signature, 260, 257);
            c.WriteLine($"{where,-32} {signature}");
            if (o.Mangled && PyText.Truthy(r[3]))
                c.WriteLine($"{"",-32} {r[3]}");
        }
        if (total != null && total > rows.Count)
            c.Note($"... {rows.Count} of {total} shown; raise --limit or narrow the search");
        else if (rows.Count == 0)
            c.Note("nothing found");
    }

    static void Filters(QueryOptions o, List<string> where, List<object?> args)
    {
        if (!string.IsNullOrEmpty(o.Dll))
        {
            where.Add("d.name LIKE ? ESCAPE '\\'");
            args.Add(Sql.Like(o.Dll));
        }
        if (!string.IsNullOrEmpty(o.Kind))
        {
            where.Add("s.kind = ?");
            args.Add(o.Kind);
        }
    }

    static object?[] With(List<object?> args, params object?[] more) => [.. args, .. more];

    static void FirstShown(SymbolContext c, int count, int limit)
    {
        if (count == 0)
            c.Note("nothing found");
        else if (count == limit)
            c.Note($"... first {limit} shown; raise --limit or narrow the search");
    }

    public static void Find(SymbolContext c, IReadOnlyList<string> text, QueryOptions o)
    {
        var where = new List<string>();
        var args = new List<object?>();
        foreach (var t in text)
        {
            where.Add("(s.signature LIKE ? ESCAPE '\\' OR s.mangled LIKE ? ESCAPE '\\')");
            args.Add(Sql.Like(t));
            args.Add(Sql.Like(t));
        }
        Filters(o, where, args);
        string cond = string.Join(" AND ", where);
        long total = PyText.L(c.Db.Scalar($"SELECT count(*) FROM symbols s JOIN dlls d ON d.id=s.dll_id WHERE {cond}", [.. args]));
        var rows = c.Db.Query("SELECT d.name, s.rva, s.signature, s.mangled, s.forwarder FROM symbols s JOIN dlls d ON d.id=s.dll_id"
                              + $" WHERE {cond} ORDER BY s.namespace, s.class, s.method, d.name LIMIT ?", With(args, o.Limit)).ToList();
        Show(c, rows, o, total);
    }

    public static void Class(SymbolContext c, string name, QueryOptions o)
    {
        int cut = name.LastIndexOf("::", StringComparison.Ordinal);
        string ns = cut >= 0 ? name[..cut] : "", cls = cut >= 0 ? name[(cut + 2)..] : name;
        var where = new List<string> { "(s.class = ? AND s.namespace = ?)" };
        var args = new List<object?> { cls, ns };
        if (c.Db.Scalar("SELECT 1 FROM symbols s WHERE " + where[0] + " LIMIT 1", [.. args]) == null)
        {
            // Not an exact namespace::class: accept any class whose qualified name ends with the text.
            where = ["(s.namespace || '::' || s.class) LIKE ? ESCAPE '\\'"];
            args = ["%" + Sql.Like(name)[1..^1]];
        }
        Filters(o, where, args);
        var rows = c.Db.Query("SELECT d.name, s.rva, s.signature, s.mangled, s.forwarder FROM symbols s JOIN dlls d ON d.id=s.dll_id"
                              + $" WHERE {string.Join(" AND ", where)} ORDER BY s.namespace, s.class, d.name, s.kind, s.method, s.signature LIMIT ?",
                              With(args, o.Limit)).ToList();
        Show(c, rows, o);
    }

    public static void Classes(SymbolContext c, IReadOnlyList<string> text, QueryOptions o)
    {
        var where = new List<string> { "s.class <> ''" };
        var args = new List<object?>();
        foreach (var t in text)
        {
            where.Add("(s.namespace || '::' || s.class) LIKE ? ESCAPE '\\'");
            args.Add(Sql.Like(t));
        }
        Filters(o, where, args);
        var rows = c.Db.Query("SELECT CASE WHEN s.namespace='' THEN s.class ELSE s.namespace || '::' || s.class END AS c,"
                              + " group_concat(DISTINCT d.name), count(*), sum(s.kind='vftable')"
                              + $" FROM symbols s JOIN dlls d ON d.id=s.dll_id WHERE {string.Join(" AND ", where)}"
                              + " GROUP BY s.namespace, s.class ORDER BY c LIMIT ?", With(args, o.Limit)).ToList();
        foreach (var r in rows)
            c.WriteLine($"{PyText.Right(r[2], 5)} {(PyText.Truthy(r[3]) ? "V" : " ")} {PyText.Str(r[0])}   [{PyText.Str(r[1])}]");
        FirstShown(c, rows.Count, o.Limit);
    }

    public static void Importers(SymbolContext c, string text, int limit)
    {
        var rows = c.Db.Query(
            "SELECT s.signature, e.name, group_concat(DISTINCT d.name) FROM symbols s JOIN dlls e ON e.id=s.dll_id"
            + " JOIN imports i ON i.mangled = s.mangled JOIN dlls d ON d.id = i.dll_id AND lower(i.from_dll) = lower(e.name)"
            + " WHERE s.signature LIKE ? ESCAPE '\\' GROUP BY s.id ORDER BY s.signature LIMIT ?", Sql.Like(text), limit).ToList();
        foreach (var r in rows)
        {
            c.WriteLine(PyText.Str(r[0]));
            c.WriteLine($"    exported by {PyText.Str(r[1])}; imported by {PyText.Str(r[2])}");
        }
        if (rows.Count == 0)
            c.Note("nothing found (no matching export is imported by another Toolkit binary)");
    }

    public static void Strings(SymbolContext c, IReadOnlyList<string> text, QueryOptions o)
    {
        var where = new List<string>();
        var args = new List<object?>();
        foreach (var t in text)
        {
            where.Add("t.text LIKE ? ESCAPE '\\'");
            args.Add(Sql.Like(t));
        }
        if (!string.IsNullOrEmpty(o.Dll))
        {
            where.Add("d.name LIKE ? ESCAPE '\\'");
            args.Add(Sql.Like(o.Dll));
        }
        var rows = c.Db.Query("SELECT t.id, d.id, d.name, t.rva, t.text FROM strings t JOIN dlls d ON d.id = t.dll_id"
                              + $" WHERE {string.Join(" AND ", where)} ORDER BY d.origin, d.name, t.text LIMIT ?", With(args, o.Limit)).ToList();
        foreach (var r in rows)
        {
            long dllId = PyText.L(r[1]);
            string dll = PyText.Str(r[2]);
            string s = PyText.Str(r[4]);
            if (!o.Full)
                s = PyText.Cut(s, 200, 197);
            c.WriteLine($"{dll}+0x{X(PyText.L(r[3]))}  {PyText.Repr(s)}");
            foreach (var f in c.Db.Query("SELECT func_rva FROM string_refs WHERE string_id = ? ORDER BY 1 LIMIT 8", r[0]).ToList())
            {
                long func = PyText.L(f[0]);
                var names = c.Db.Query("SELECT full_name FROM symbols WHERE dll_id = ? AND rva = ? LIMIT 2", dllId, func).Select(n => PyText.Str(n[0])).ToList();
                c.WriteLine($"      used by {dll}+0x{X(func)}" + (names.Count > 0 ? "  " + string.Join(" = ", names) : ""));
            }
        }
        FirstShown(c, rows.Count, o.Limit);
    }

    public static void Types(SymbolContext c, IReadOnlyList<string> text, QueryOptions o)
    {
        var where = new List<string>();
        var args = new List<object?>();
        foreach (var t in text)
        {
            where.Add("t.type LIKE ? ESCAPE '\\'");
            args.Add(Sql.Like(t));
        }
        if (!string.IsNullOrEmpty(o.Kind))
        {
            where.Add("t.kind = ?");
            args.Add(o.Kind);
        }
        where.Add("d.name LIKE ? ESCAPE '\\'");
        args.Add(Sql.Like(string.IsNullOrEmpty(o.Dll) ? "bg3.exe" : o.Dll));
        var rows = c.Db.Query("SELECT d.name, t.kind, t.type, t.func_rva FROM type_names t JOIN dlls d ON d.id = t.dll_id"
                              + $" WHERE {string.Join(" AND ", where)} ORDER BY t.kind, t.type LIMIT ?", With(args, o.Limit)).ToList();
        foreach (var r in rows)
        {
            string name = PyText.Str(r[2]);
            if (!o.Full)
                name = PyText.Cut(name, 300, 297);
            string where_ = r[3] != null ? $"{PyText.Str(r[0])}+0x{X(PyText.L(r[3]))}" : PyText.Str(r[0]);
            c.WriteLine($"{PyText.Left(r[1], 10)} {where_,-24} {name}");
        }
        FirstShown(c, rows.Count, o.Limit);
    }

    public static void Game(SymbolContext c, IReadOnlyList<string> text, QueryOptions o, bool unnamed)
    {
        var where = new List<string> { "g.name LIKE ? ESCAPE '\\'" };
        var args = new List<object?> { Sql.Like(string.IsNullOrEmpty(o.Dll) ? "bg3.exe" : o.Dll) };
        foreach (var t in text)
        {
            where.Add("(s.full_name LIKE ? ESCAPE '\\' OR m.sample LIKE ? ESCAPE '\\')");
            args.Add(Sql.Like(t));
            args.Add(Sql.Like(t));
        }
        if (!unnamed)
            where.Add("s.id IS NOT NULL");
        var rows = c.Db.Query(
            "SELECT g.name, m.game_rva, t.name, m.toolkit_rva, m.evidence, s.signature, m.sample, m.method, m.relation FROM matches m"
            + " JOIN dlls g ON g.id = m.game_dll JOIN dlls t ON t.id = m.toolkit_dll"
            + " LEFT JOIN symbols s ON s.dll_id = m.toolkit_dll AND s.rva = m.toolkit_rva"
            + $" WHERE {string.Join(" AND ", where)} ORDER BY s.full_name IS NULL, s.full_name, m.evidence DESC LIMIT ?", With(args, o.Limit)).ToList();
        foreach (var r in rows)
        {
            string signature = r[5] is string s && s.Length > 0 ? s : $"(not exported)  {PyText.Repr(r[6])}";
            if (!o.Full)
                signature = PyText.Cut(signature, 220, 217);
            string method = PyText.Str(r[7]);
            // A function found through a virtual table slot is that function whatever its size: it is
            // larger in the game when its own callees were inlined into it.
            string sign = method == "vtable" ? "=" : (r[8] as string) switch { "inlined" => "contains", "partial" => "part of", _ => "=" };
            c.WriteLine($"{PyText.Str(r[0])}+0x{X(PyText.L(r[1]))}  {sign}  {PyText.Str(r[2])}+0x{X(PyText.L(r[3]))}  [{method} {PyText.Str(r[4])}]  {signature}");
        }
        FirstShown(c, rows.Count, o.Limit);
    }

    public static void Se(SymbolContext c, IReadOnlyList<string> text, QueryOptions o, bool report)
    {
        string dllLike = Sql.Like(string.IsNullOrEmpty(o.Dll) ? "bg3.exe" : o.Dll);
        var rows = c.Db.Query("SELECT d.name, s.rva, s.name, s.mapping, s.note FROM se_symbols s JOIN dlls d ON d.id = s.dll_id"
                              + " WHERE s.name LIKE ? ESCAPE '\\' AND d.name LIKE ? ESCAPE '\\' ORDER BY s.name LIMIT ?",
                              Sql.Like(string.Join(" ", text)), dllLike, o.Limit).ToList();
        foreach (var r in rows)
            c.WriteLine($"{PyText.Str(r[0])}+0x{X(PyText.L(r[1]))}  {PyText.Str(r[2])}" + (PyText.Truthy(r[4]) ? $"   ({PyText.Str(r[4])})" : ""));
        if (report)
            foreach (var r in c.Db.Query("SELECT d.name, m.mapping, m.matches, m.status FROM se_mappings m JOIN dlls d ON d.id = m.dll_id"
                                         + " WHERE d.name LIKE ? ESCAPE '\\' ORDER BY m.mapping", dllLike).ToList())
                c.WriteLine($"  {PyText.Left(r[1], 60)} {PyText.Right(r[2], 6)} matches  {PyText.Str(r[3])}");
        if (rows.Count == 0 && !report)
            c.Note("nothing found");
    }

    public static void Ecs(SymbolContext c, IReadOnlyList<string> text, QueryOptions o, string? context)
    {
        var where = new List<string> { "d.name LIKE ? ESCAPE '\\'" };
        var args = new List<object?> { Sql.Like(string.IsNullOrEmpty(o.Dll) ? "bg3.exe" : o.Dll) };
        foreach (var t in text)
        {
            where.Add("e.type LIKE ? ESCAPE '\\'");
            args.Add(Sql.Like(t));
        }
        if (!string.IsNullOrEmpty(context))
        {
            where.Add("e.context LIKE ? ESCAPE '\\'");
            args.Add(Sql.Like(context));
        }
        var rows = c.Db.Query("SELECT d.name, e.type, e.context, e.index_rva, e.source FROM ecs_ids e JOIN dlls d ON d.id = e.dll_id"
                              + $" WHERE {string.Join(" AND ", where)} AND e.type NOT LIKE 'ecs::query::spec::Spec<%'"
                              + " GROUP BY d.name, e.type, e.index_rva ORDER BY e.type, e.context LIMIT ?", With(args, o.Limit)).ToList();
        foreach (var r in rows)
        {
            string ctx = r[2] is string s && s.Length > 0 ? s : "?";
            c.WriteLine($"{PyText.Str(r[0])}+0x{X(PyText.L(r[3]))}  {ctx.Replace("ecs::", ""),-34} {PyText.Str(r[1])}");
        }
        FirstShown(c, rows.Count, o.Limit);
    }

    public static void Export(SymbolContext c, string? dll, string format, string? outPath)
    {
        var (module, labels) = GameLabels.Collect(c.Db, string.IsNullOrEmpty(dll) ? "bg3.exe" : dll);
        string text = GameLabels.Render(module, labels, format);
        if (!string.IsNullOrEmpty(outPath))
        {
            File.WriteAllText(outPath, text, new UTF8Encoding(false));
            var kinds = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var l in labels)
                kinds[l.Kind] = kinds.GetValueOrDefault(l.Kind) + 1;
            c.Note($"{outPath}: {labels.Count} names for {module} ({PyText.Dict(kinds.Select(k => ((object?)k.Key, (object?)k.Value)))})");
        }
        else
        {
            // The text ends with a line break or is JSON without one, as sys.stdout.write printed it.
            var lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (i == lines.Length - 1)
                {
                    if (lines[i].Length > 0)
                        c.Out.Write(lines[i]);
                }
                else
                    c.Out.WriteLine(lines[i]);
            }
        }
    }

    public static void Stats(SymbolContext c)
    {
        var db = c.Db;
        long One(string q) => PyText.L(db.Scalar(q));
        string Dict(string q, params object?[] args) => PyText.Dict(db.Query(q, args).Select(r => (r[0], r[1])).ToList());
        foreach (var r in db.Query("SELECT key, value FROM meta").ToList())
            c.WriteLine($"{PyText.Str(r[0])}: {PyText.Str(r[1])}");
        c.WriteLine($"files: {One("SELECT count(*) FROM dlls")} | with exports: {One("SELECT count(*) FROM dlls WHERE exports>0")}"
                    + $" | .NET assemblies: {One("SELECT count(*) FROM dlls WHERE managed=1")}"
                    + $" | unreadable: {One("SELECT count(*) FROM dlls WHERE note IS NOT NULL")}");
        c.WriteLine($"exports: {One("SELECT count(*) FROM symbols")} | imports: {One("SELECT count(*) FROM imports")}"
                    + $" | classes: {One("SELECT count(*) FROM (SELECT 1 FROM symbols WHERE class<>'' GROUP BY namespace, class)")}");
        c.WriteLine("by kind: " + Dict("SELECT kind, count(*) FROM symbols GROUP BY kind ORDER BY 2 DESC"));
        c.WriteLine($"text constants referred to by code: {One("SELECT count(*) FROM strings")}"
                    + $" | virtual tables read: {One("SELECT count(DISTINCT symbol_id) FROM vtable_slots")}"
                    + $" | slots: {One("SELECT count(*) FROM vtable_slots")}");
        foreach (var r in db.Query("SELECT d.name, d.origin, d.functions, (SELECT count(*) FROM strings s WHERE s.dll_id = d.id),"
                                   + " (SELECT count(*) FROM type_names t WHERE t.dll_id = d.id) FROM dlls d WHERE d.origin = 'game'").ToList())
        {
            string name = PyText.Str(r[0]);
            c.WriteLine($"game {name}: {PyText.Str(r[2])} functions, {PyText.Str(r[3])} text constants, {PyText.Str(r[4])} type names, "
                        + Dict("SELECT kind, count(*) FROM type_names t JOIN dlls d ON d.id = t.dll_id WHERE d.name = ? GROUP BY kind", name));
            foreach (var m in db.Query("SELECT m.method, coalesce(m.relation, '?'), count(*),"
                                       + " sum(EXISTS (SELECT 1 FROM symbols s WHERE s.dll_id = m.toolkit_dll AND s.rva = m.toolkit_rva))"
                                       + " FROM matches m JOIN dlls d ON d.id = m.game_dll WHERE d.name = ? GROUP BY 1, 2", name).ToList())
                c.WriteLine($"     matched by {PyText.Str(m[0])}, {PyText.Str(m[1])}: {PyText.Str(m[2])}, of them with an exported name: {PyText.Str(m[3])}");
            c.WriteLine($"     virtual tables: {PyText.Str(db.Scalar("SELECT count(*) FROM vtabs v JOIN dlls d ON d.id = v.dll_id WHERE d.name = ?", name))} found, "
                        + PyText.Str(db.Scalar("SELECT count(DISTINCT m.game_vtab) FROM vtab_matches m JOIN vtabs v ON v.id = m.game_vtab"
                                               + " JOIN dlls d ON d.id = v.dll_id WHERE d.name = ?", name)) + " matched to Toolkit tables");
        }
        c.WriteLine("Script Extender: symbols located " + Dict("SELECT d.name, count(*) FROM se_symbols s JOIN dlls d ON d.id = s.dll_id GROUP BY 1")
                    + " | ECS registration records " + Dict("SELECT d.name, count(*) FROM ecs_ids e JOIN dlls d ON d.id = e.dll_id GROUP BY 1"));
        c.WriteLine($"layouts: {One("SELECT count(*) FROM se_types")} types, {One("SELECT count(*) FROM se_fields")} fields, "
                    + $"{One("SELECT count(*) FROM se_fields WHERE offset IS NOT NULL")} with an offset, "
                    + Dict("SELECT CASE checked WHEN 1 THEN 'offset confirmed' WHEN 2 THEN 'offset taken from name' END, count(*)"
                           + " FROM se_fields WHERE checked IS NOT NULL GROUP BY 1")
                    + $" | enumerations: {One("SELECT count(*) FROM enums")} with {One("SELECT count(*) FROM enum_values")} values"
                    + $" | Lua classes: {One("SELECT count(*) FROM lua_classes")}");
        c.WriteLine($"fields from accessors: {One("SELECT count(*) FROM accessors")} in {One("SELECT count(DISTINCT class) FROM accessors")} classes; "
                    + $"{One("SELECT count(*) FROM accessors WHERE game_offset IS NOT NULL")} with the offset read in the game too, "
                    + $"{One("SELECT count(*) FROM accessors WHERE game_offset IS NOT NULL AND game_offset <> offset")} of them different");
        c.WriteLine("largest export tables:");
        foreach (var r in db.Query("SELECT name, exports FROM dlls ORDER BY exports DESC LIMIT 15").ToList())
            c.WriteLine($"  {PyText.Right(r[1], 7)}  {PyText.Str(r[0])}");
    }
}
