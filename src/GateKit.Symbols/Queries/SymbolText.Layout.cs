namespace GateKit.Symbols.Queries;

internal static partial class SymbolText
{
    static string TypeDisplay(object?[] t) =>
        PyText.Truthy(t[4]) ? PyText.Str(t[4]) : (PyText.Truthy(t[2]) ? PyText.Str(t[2]) + "::" : "") + PyText.Str(t[1]);

    /// <summary>Fields of a type as Script Extender describes them, with offsets where they can be computed.</summary>
    public static void Layout(SymbolContext c, string name, int limit)
    {
        var types = c.Db.Query("SELECT * FROM se_types WHERE engine_class = ? OR name = ? OR ext_name = ? ORDER BY engine_class IS NULL",
                               name, name, name).ToList();
        if (types.Count == 0)
        {
            types = c.Db.Query("SELECT * FROM se_types WHERE engine_class LIKE ? ESCAPE '\\' OR name LIKE ? ESCAPE '\\'"
                               + " ORDER BY engine_class IS NULL, name LIMIT ?", Sql.Like(name), Sql.Like(name), limit).ToList();
            if (types.Count > 1)
            {
                foreach (var t in types)
                    c.WriteLine($"{PyText.Left(t[5], 20)} {TypeDisplay(t)}");
                return;
            }
        }
        foreach (var t in types)
        {
            c.WriteLine($"{PyText.Str(t[5])}  {TypeDisplay(t)}"
                        + (PyText.Truthy(t[3]) ? $"   (Lua: {PyText.Str(t[3])})" : "")
                        + (t[9] != null ? $"   size 0x{X(PyText.L(t[9]))}" : "")
                        + (PyText.Truthy(t[6]) ? $"   : {PyText.Str(t[6])}" : "") + $"   [{PyText.Str(t[7])}:{PyText.Str(t[8])}]");
            foreach (var f in c.Db.Query("SELECT idx, name, type, legacy, hidden, offset, size, checked FROM se_fields WHERE type_id = ? ORDER BY idx", t[0]).ToList())
            {
                string where = f[5] != null ? $"+0x{PyText.Hex(PyText.L(f[5]), 3)}" : "  ?   ";
                string mark = PyText.Long(f[7]) switch { 1 => " =", 2 => " ~", _ => "  " };
                c.WriteLine($"  {where}{mark} {PyText.Str(f[2])} {PyText.Str(f[1])}" + (PyText.Truthy(f[3]) ? $"   (was {PyText.Str(f[3])})" : "")
                            + (PyText.Truthy(f[4]) ? "   [hidden]" : ""));
            }
        }
        if (types.Count == 0)
            c.Note("nothing found");
        else
            c.Note("  '=' offset confirmed by a field named after its offset; '~' offset taken from such a name; '?' unknown");
    }

    /// <summary>Fields of a class from every source: accessors of the Toolkit and Script Extender's layout.</summary>
    public static void Fields(SymbolContext c, string name, int limit)
    {
        var rows = c.Db.Query(
            "SELECT a.class, a.field, a.kind, a.offset, a.width, a.type, a.game_offset, g.name, a.game_rva, a.bit FROM accessors a"
            + " LEFT JOIN dlls g ON g.id = a.game_dll WHERE a.class = ? OR a.class LIKE ? ESCAPE '\\'"
            + " ORDER BY a.class, a.offset, a.field, a.kind LIMIT ?", name, "%" + Sql.Like("::" + name)[1..^1], limit).ToList();
        string? last = null;
        foreach (var r in rows)
        {
            string cls = PyText.Str(r[0]);
            if (cls != last)
            {
                c.WriteLine($"{cls}   (from accessors exported by the Toolkit; offsets are the Toolkit's unless a game offset is shown)");
                last = cls;
            }
            string game = r[6] != null ? $"   game +0x{X(PyText.L(r[6]))} ({PyText.Str(r[7])}+0x{X(PyText.L(r[8]))})" : "";
            string where = r[9] != null ? $"[bit {PyText.Str(r[9])}, mask 0x{X(1L << (int)PyText.L(r[9]))}]"
                : PyText.Truthy(r[4]) ? $"[{PyText.Str(r[4])} bytes]" : "";
            c.WriteLine($"  +0x{PyText.Hex(PyText.L(r[3]), 3)}  {PyText.Left(r[2], 3)} {PyText.Str(r[1])}" + (PyText.Truthy(r[5]) ? $" : {PyText.Str(r[5])}" : "")
                        + $"  {where}" + game);
        }
        int cut = name.LastIndexOf("::", StringComparison.Ordinal);
        string shortName = cut >= 0 ? name[(cut + 2)..] : name;
        if (c.Db.Scalar("SELECT 1 FROM se_types WHERE engine_class = ? OR name = ? LIMIT 1", name, shortName) != null)
        {
            c.WriteLine("");
            if (c.Db.Scalar("SELECT 1 FROM se_types WHERE engine_class = ?", name) == null)
                name = shortName;
            Layout(c, name, limit);
        }
        else if (rows.Count == 0)
            c.Note("nothing found");
    }

    /// <summary>An enumeration or bit mask with its values; or the enumerations that have a given label.</summary>
    public static void Enum(SymbolContext c, string name, int limit)
    {
        var rows = c.Db.Query("SELECT * FROM enums WHERE name = ?", name).ToList();
        if (rows.Count == 0)
            rows = c.Db.Query("SELECT * FROM enums WHERE name LIKE ? ESCAPE '\\' ORDER BY name LIMIT ?", Sql.Like(name), limit).ToList();
        if (rows.Count != 1)
        {
            foreach (var r in rows)
                c.WriteLine($"{(PyText.Truthy(r[4]) ? "bitmask" : "enum"),-8} {PyText.Str(r[1])}  ({PyText.Str(r[3])})");
            foreach (var v in c.Db.Query("SELECT e.name, v.name, v.value FROM enum_values v JOIN enums e ON e.id = v.enum_id"
                                         + " WHERE v.name LIKE ? ESCAPE '\\' ORDER BY e.name LIMIT ?", Sql.Like(name), limit).ToList())
                c.WriteLine($"label    {PyText.Str(v[0])}::{PyText.Str(v[1])} = {PyText.Str(v[2])}");
            return;
        }
        var e = rows[0];
        bool bitmask = PyText.Truthy(e[4]);
        c.WriteLine($"{(bitmask ? "bitmask" : "enum")} {(PyText.Truthy(e[2]) ? PyText.Str(e[2]) + "::" : "")}{PyText.Str(e[1])} : {PyText.Str(e[3])}   [{PyText.Str(e[5])}]");
        foreach (var v in c.Db.Query("SELECT name, value FROM enum_values WHERE enum_id = ? ORDER BY value, rowid", e[0]).ToList())
        {
            long? value = PyText.Long(v[1]);
            c.WriteLine($"  {(value != null ? PyText.Str(value) : "?"),6}  " + (bitmask && value != null ? $"0x{X(value.Value)}  " : "") + PyText.Str(v[0]));
        }
    }
}
