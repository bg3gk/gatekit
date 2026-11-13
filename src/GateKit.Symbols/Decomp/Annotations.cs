using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace GateKit.Symbols.Decomp;

/// <summary>What `code` adds to Ghidra's text: field names of `this + offset`, hints for unnamed
/// functions, the texts of global FixedStrings, the stored values of offset globals; and the
/// excerpt that `code --grep` prints.</summary>
internal static class Annotations
{
    /// <summary>Pairs by pseudo-code shown by default (see the pairing in <see cref="CodeMatch"/>).</summary>
    public const double MinScore = 0.15, MinMargin = 0.10;

    static readonly Regex ThisOffset = new(@"(?:this|param_1) \+ (0x[0-9a-fA-F]+|\d+)\)|(?:this|param_1)\[(0x[0-9a-fA-F]+|\d+)\]", RegexOptions.Compiled);
    static readonly Regex Unnamed = new(@"\bFUN_([0-9a-f]{9,16})\b", RegexOptions.Compiled);
    static readonly Regex FixedGlobal = new(@"\bDAT_(1[0-9a-f]{8})\b", RegexOptions.Compiled);
    static readonly Regex OffsetGlobal = new(@"\(_?DAT_(1[0-9a-f]{8}) \+ |\+ _?DAT_(1[0-9a-f]{8})\)", RegexOptions.Compiled);

    /// <summary>All of them, in the order the Python tool applied them.</summary>
    public static string All(DecompSession s, string text, string? name, string binary) =>
        NameOffsetGlobals(s, NameFixedStrings(s, HintUnnamed(s, Annotate(s, text, name), binary), binary), binary);

    /// <summary>{offset: 'Name [source]'} for a class, from the symbol database: fields read from
    /// the Toolkit's accessors (the game's offset when it was read there) and Script Extender's layout.</summary>
    public static Dictionary<long, string> ClassFields(DecompSession s, string? className)
    {
        var fields = new Dictionary<long, string>();
        var db = s.Symbols;
        if (db == null || string.IsNullOrEmpty(className))
            return fields;
        try
        {
            foreach (var r in db.Query("SELECT f.name, f.offset, f.checked FROM se_fields f JOIN se_types t ON t.id = f.type_id"
                                       + " WHERE t.engine_class = ? AND f.offset IS NOT NULL ORDER BY f.idx", className))
            {
                long offset = Convert.ToInt64(r[1], CultureInfo.InvariantCulture);
                fields.TryAdd(offset, $"{Py.Str(r[0])} [SE layout{(Truthy(r[2]) ? "" : ", offset computed")}]");
            }
            foreach (var r in db.Query("SELECT field, offset, game_offset, bit FROM accessors WHERE class = ? ORDER BY bit IS NOT NULL, field", className))
            {
                string name = Py.Str(r[0]);
                object? whereValue = r[2] ?? r[1];
                if (whereValue == null)
                    continue;                             // no offset: nothing on a line can point at it
                long where = Convert.ToInt64(whereValue, CultureInfo.InvariantCulture);
                string label = $"{name} [accessor{(r[2] != null ? ", game offset" : ", Toolkit offset")}]";
                bool has = fields.TryGetValue(where, out var current);
                if (r[3] != null)
                    fields[where] = (current ?? "") + (has ? "; " : "") + $"bit {Py.Str(r[3])} = {name}";
                else if (has && current!.Contains(name + " [accessor", StringComparison.Ordinal))
                    continue;                             // getter and setter of the same field
                else if (!has || current!.Contains("SE layout", StringComparison.Ordinal))
                    fields[where] = !has ? label
                        : current!.StartsWith(name + " ", StringComparison.Ordinal) ? $"{name} [accessor + SE layout]"
                        : current + $" / {label}";
            }
        }
        catch (SqliteException)
        {
            // a symbol database without these tables
        }
        return fields;
    }

    static bool Truthy(object? v) => v switch
    {
        null => false,
        long l => l != 0,
        double d => d != 0,
        string t => t.Length > 0,
        byte[] b => b.Length > 0,
        _ => true,
    };

    /// <summary>Adds the field name to every line that touches `this + offset`, when the function
    /// is a method of a class whose fields are known.</summary>
    public static string Annotate(DecompSession s, string text, string? functionName)
    {
        string? className = functionName != null && functionName.Contains("::", StringComparison.Ordinal)
            ? functionName[..functionName.LastIndexOf("::", StringComparison.Ordinal)] : null;
        var fields = ClassFields(s, className);
        if (fields.Count == 0)
            return text;
        var lines = Py.SplitLines(text);
        var b = new StringBuilder(text.Length + 256);
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var notes = new List<string>();
            foreach (Match m in ThisOffset.Matches(line))
            {
                var offset = Py.Int0(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
                if (offset is long o && fields.TryGetValue(o, out var field) && !notes.Contains(field))
                    notes.Add($"+0x{o:X} {field}");
            }
            if (i > 0)
                b.Append('\n');
            b.Append(line);
            if (notes.Count > 0)
                b.Append("   // ").Append(string.Join(" | ", notes));
        }
        return b.ToString();
    }

    /// <summary>'~ Name [pseudo-code 0.81]' for a game function paired with a named Toolkit
    /// function by its pseudo-code, or null.</summary>
    public static string? CodeMatchName(DecompSession s, string binary, long rva)
    {
        var db = s.DbIfExists;
        if (db == null)
            return null;
        try
        {
            var row = db.One(
                "SELECT f.name, m.score FROM code_matches m JOIN binaries g ON g.id = m.game_binary"
                + " JOIN functions f ON f.binary_id = m.toolkit_binary AND f.rva = m.toolkit_rva"
                + " WHERE g.name = ? AND m.game_rva = ? AND m.score >= ? AND m.margin >= ? AND f.name NOT LIKE 'FUN!_%' ESCAPE '!'"
                + " AND f.name NOT LIKE 'thunk!_FUN!_%' ESCAPE '!' ORDER BY m.score DESC LIMIT 1", binary, rva, MinScore, MinMargin);
            return row == null ? null : string.Create(CultureInfo.InvariantCulture, $"~ {Py.Str(row[0])} [pseudo-code {Convert.ToDouble(row[1], CultureInfo.InvariantCulture):F2}]");
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    /// <summary>For every function Ghidra could not name (FUN_&lt;address&gt;), adds at its first
    /// mention what is known about it from the symbol database: a match to a Toolkit function,
    /// or the text constant only that function refers to. A hint, clearly marked, not a name.</summary>
    public static string HintUnnamed(DecompSession s, string text, string binary)
    {
        var db = s.Symbols;
        if (db == null)
            return text;
        object? dllId;
        try
        {
            dllId = db.Scalar("SELECT id FROM dlls WHERE name = ?", binary);
        }
        catch (SqliteException)
        {
            return text;
        }
        if (dllId == null)
            return text;
        long imageBase = DecompStore.ImageBase(binary);
        var seen = new Dictionary<long, string?>();

        object?[]? One(string sql, params object?[] args)
        {
            try
            {
                return db.One(sql, args);
            }
            catch (SqliteException)
            {
                return null;
            }
        }

        string? Lookup(long rva)
        {
            var exported = One("SELECT min(full_name) FROM symbols WHERE dll_id = ? AND rva = ? AND kind IN ('method', 'function')", dllId, rva);
            if (exported != null && exported[0] is string e && e.Length > 0)
                return $"= {e} [export]";
            var named = One(
                "SELECT s.full_name, m.method FROM matches m JOIN symbols s ON s.dll_id = m.toolkit_dll AND s.rva = m.toolkit_rva"
                + " WHERE m.game_dll = ? AND m.game_rva = ? AND (m.relation IS NOT 'inlined' OR m.method = 'vtable')"
                + " ORDER BY length(s.full_name) LIMIT 1", dllId, rva);
            if (named != null)
                return $"= {Py.Str(named[0])} [{Py.Str(named[1])}]";
            var only = One(
                "SELECT t.text FROM string_refs r JOIN strings t ON t.id = r.string_id WHERE t.dll_id = ? AND r.func_rva = ?"
                + " AND (SELECT count(*) FROM string_refs r2 WHERE r2.string_id = t.id) = 1 AND t.text NOT LIKE '%GetTypeName<%'"
                + " ORDER BY length(t.text) DESC LIMIT 1", dllId, rva);
            if (only != null)
                return "only user of \"" + Py.Left(Py.Str(only[0]), 60).Replace("*/", "* /", StringComparison.Ordinal) + "\"";
            var alike = CodeMatchName(s, binary, rva);
            if (alike != null)
                return alike;
            var typeName = One("SELECT type FROM type_names WHERE dll_id = ? AND func_rva = ? LIMIT 1", dllId, rva);
            return typeName != null ? $"returns the type name {Py.Left(Py.Str(typeName[0]), 80)}" : null;
        }

        return Unnamed.Replace(text, m =>
        {
            var address = Py.Hex(m.Groups[1].Value);
            if (address is not long a || seen.ContainsKey(a))
                return m.Value;
            var hint = Lookup(a - imageBase);
            seen[a] = hint;
            return m.Value + (hint != null ? $" /* {hint} */" : "");
        });
    }

    /// <summary>Adds the text of a global FixedString after the first mention of its global.</summary>
    public static string NameFixedStrings(DecompSession s, string text, string binary)
    {
        if (!s.FixedStringTables.TryGetValue(binary, out var table))
        {
            table = [];
            var db = s.DbIfExists;
            if (db != null)
            {
                try
                {
                    foreach (var r in db.Query("SELECT s.rva, s.kind, s.text FROM fixed_strings s JOIN binaries b ON b.id = s.binary_id WHERE b.name = ?", binary))
                        if (r[0] != null)
                            table[Convert.ToInt64(r[0], CultureInfo.InvariantCulture)] = (Py.Str(r[1]), Py.Str(r[2]));
                }
                catch (SqliteException)
                {
                    table.Clear();
                }
            }
            s.FixedStringTables[binary] = table;
        }
        if (table.Count == 0)
            return text;
        long imageBase = DecompStore.ImageBase(binary);
        var seen = new HashSet<long>();
        return FixedGlobal.Replace(text, m =>
        {
            long rva = Py.Hex(m.Groups[1].Value)!.Value - imageBase;
            if (!table.TryGetValue(rva, out var hit) || seen.Contains(rva))
                return m.Value;
            string? what = hit.Kind switch { "value" => "FixedString", "pointer" => "-> FixedString", "entry" => "FixedString entry", _ => null };
            if (what == null)
                return m.Value;
            seen.Add(rva);
            return m.Value + $" /* {what} \"{hit.Text}\" */";
        });
    }

    /// <summary>Writes the stored value after a global that is added to a pointer, when the
    /// pseudo-code of the whole binary never assigns it: such a global is a member offset the
    /// compiler kept as data.</summary>
    public static string NameOffsetGlobals(DecompSession s, string text, string binary)
    {
        var wanted = new List<string>();
        foreach (Match m in OffsetGlobal.Matches(text))
        {
            var address = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            if (!wanted.Contains(address))
                wanted.Add(address);
        }
        if (wanted.Count == 0 || wanted.Count > 16)
            return text;
        SqliteConnection? db;
        object? binaryId;
        try
        {
            db = s.DbIfExists;
            binaryId = db?.Scalar("SELECT id FROM binaries WHERE name = ?", binary);
        }
        catch (SqliteException)
        {
            return text;
        }
        if (db == null || binaryId == null)
            return text;
        long imageBase = DecompStore.ImageBase(binary);
        foreach (var address in wanted)
        {
            string token = "DAT_" + address;
            var assignment = new Regex($@"_?{token}\s*(=(?!=)|\+=|-=|\+\+|--)");
            bool assigned = false;
            try
            {
                foreach (var r in db.Query("SELECT c.text FROM code_fts JOIN functions f ON f.id = code_fts.rowid JOIN code c ON c.function_id = f.id"
                                           + " WHERE code_fts MATCH ? AND f.binary_id = ? LIMIT 200", $"\"{token}\" OR \"_{token}\"", binaryId))
                {
                    if (DecompStore.Decompress(r[0]) is string body && assignment.IsMatch(body))
                    {
                        assigned = true;
                        break;
                    }
                }
            }
            catch (SqliteException)
            {
                return text;
            }
            if (assigned)
                continue;
            ulong? value;
            try
            {
                value = s.ReadValue(binary, Py.Hex(address)!.Value - imageBase);
            }
            catch (FileNotFoundException)
            {
                return text;
            }
            string note;
            if (value == null)
                note = "/* never assigned; zero-filled data, so 0 */";
            else
            {
                ulong low = value.Value & 0xFFFFFFFF;
                note = $"/* never assigned; in the file: 0x{low:X}" + (value.Value >> 32 != 0 ? $" (8 bytes: 0x{value.Value:X})" : "") + " */";
            }
            text = new Regex($@"(_?{token})\b(?! /\*)").Replace(text, m => m.Groups[1].Value + " " + note, 1);
        }
        return text;
    }

    /// <summary>Lines of a listing that contain the text (case does not matter), with line numbers
    /// and `context` lines around each; `...` between pieces. `a|b` finds either text.</summary>
    public static string Excerpt(string text, string needle, int context)
    {
        var lines = Py.SplitLines(text);
        var wanted = needle.Split('|').Where(w => w.Length > 0).Select(w => w.ToLowerInvariant()).ToList();
        var keep = new SortedSet<int>();
        for (int i = 0; i < lines.Count; i++)
        {
            var low = lines[i].ToLowerInvariant();
            if (wanted.Any(w => low.Contains(w, StringComparison.Ordinal)))
                for (int k = Math.Max(0, i - context); k < Math.Min(lines.Count, i + context + 1); k++)
                    keep.Add(k);
        }
        var b = new StringBuilder();
        int? last = null;
        foreach (int i in keep)
        {
            if (b.Length > 0)
                b.Append('\n');
            if (last != null && i != last + 1)
                b.Append("   ...\n");
            b.Append(string.Create(CultureInfo.InvariantCulture, $"{i + 1,5}  ")).Append(lines[i]);
            last = i;
        }
        return keep.Count > 0 ? b.ToString() : $"// no line contains {Py.Repr(needle)} ({lines.Count} lines)";
    }
}
