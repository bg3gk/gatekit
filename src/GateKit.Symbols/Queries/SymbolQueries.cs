using System.Text.RegularExpressions;
using GateKit.Symbols.Decomp;
using Microsoft.Data.Sqlite;

namespace GateKit.Symbols.Queries;

/// <summary>Read-only questions to the symbol database for other tools (the MCP server), answered
/// as data that serialises to JSON: lists of rows keyed by column name, and dictionaries with the
/// keys the Python tool (kit/gatekit.py) used. Open once, ask many times, dispose.</summary>
public sealed partial class SymbolQueries : IDisposable
{
    readonly SqliteConnection db;
    readonly bool ownsDb;
    readonly SymbolsEnvironment env;
    DecompQueries? decomp;

    /// <summary>Over a database file (read-only); a missing file is a <see cref="SymbolsException"/>.</summary>
    public SymbolQueries(string dbPath, SymbolsEnvironment? environment = null)
    {
        if (!File.Exists(dbPath))
            throw new SymbolsException($"{dbPath} does not exist; build it first:  gatekit symbols build");
        db = Sql.OpenReadOnly(dbPath);
        ownsDb = true;
        env = environment ?? SymbolsEnvironment.FromEnvironment();
    }

    /// <summary>Over an open connection (not disposed with this object).</summary>
    public SymbolQueries(SqliteConnection connection, SymbolsEnvironment? environment = null)
    {
        db = connection;
        env = environment ?? SymbolsEnvironment.FromEnvironment();
    }

    /// <summary>Over the database of the environment (BG3_SYMBOLS_DB).</summary>
    public SymbolQueries() : this(SymbolPaths.SymbolsDb) { }

    public SqliteConnection Connection => db;

    List<Dictionary<string, object?>> Rows(string sql, params object?[] args) => db.Rows(sql, args);

    long? GameId(string name = "bg3.exe") => PyText.Long(db.Scalar("SELECT id FROM dlls WHERE origin = 'game' AND name = ?", name));

    [GeneratedRegex(@"^(class|struct) ")]
    private static partial Regex ClassKeyword();

    /// <summary>Address in the form the about tool routes: binary+0xRVA.</summary>
    [GeneratedRegex(@"^([\w.]+?)\+(?:0x)?([0-9a-fA-F]+)$")]
    public static partial Regex Address();

    /// <summary>Classes, functions, ECS types, layouts, enumerations and labels whose name contains
    /// the text (the symbol part of the search tool); each row with `kind` and `more`, the command
    /// that tells more.</summary>
    public List<Dictionary<string, object?>> Search(string text, int limit = 10)
    {
        string pat = Sql.Like(text);
        var result = new List<Dictionary<string, object?>>();
        void Add(string kind, List<Dictionary<string, object?>> items, Func<Dictionary<string, object?>, string> more)
        {
            foreach (var item in items)
            {
                item["kind"] = kind;
                item["more"] = more(item);
                result.Add(item);
            }
        }
        Add("class", Rows("SELECT CASE WHEN namespace = '' THEN class ELSE namespace || '::' || class END AS name, count(*) AS members"
                          + " FROM symbols WHERE class <> '' AND (namespace || '::' || class) LIKE ? ESCAPE '\\'"
                          + " GROUP BY namespace, class ORDER BY length(name) LIMIT ?", pat, limit),
            r => $"gatekit about {r["name"]}");
        Add("function", Rows("SELECT full_name AS name, signature FROM symbols WHERE kind IN ('method','function') AND method LIKE ? ESCAPE '\\'"
                             + " GROUP BY full_name ORDER BY length(full_name) LIMIT ?", pat, limit),
            r => $"gatekit hook {r["name"]}");
        Add("ecs type", Rows("SELECT type AS name, kind AS type FROM type_names WHERE dll_id = ? AND kind <> 'query' AND type LIKE ? ESCAPE '\\'"
                             + " ORDER BY length(type) LIMIT ?", GameId(), pat, limit),
            r => $"gatekit about {ClassKeyword().Replace(PyText.Str(r["name"]), "")}");
        Add("layout", Rows("SELECT coalesce(engine_class, name) AS name, kind AS type FROM se_types WHERE name LIKE ? ESCAPE '\\'"
                           + " OR engine_class LIKE ? ESCAPE '\\' ORDER BY engine_class IS NULL, length(name) LIMIT ?", pat, pat, limit),
            r => $"symbols layout {r["name"]}");
        Add("enum", Rows("SELECT name, underlying AS type FROM enums WHERE name LIKE ? ESCAPE '\\' ORDER BY length(name) LIMIT ?", pat, limit),
            r => $"symbols enum {r["name"]}");
        Add("enum value", Rows("SELECT e.name || '::' || v.name AS name, v.value FROM enum_values v JOIN enums e ON e.id = v.enum_id"
                               + " WHERE v.name LIKE ? ESCAPE '\\' ORDER BY length(v.name) LIMIT ?", pat, limit),
            r => $"symbols enum {PyText.Str(r["name"]).Split("::")[0]}");
        return result;
    }

    static (string Namespace, string Class) Split(string name)
    {
        int cut = name.LastIndexOf("::", StringComparison.Ordinal);
        return cut >= 0 ? (name[..cut], name[(cut + 2)..]) : ("", name);
    }

    /// <summary>A C++ class of the engine: exports, ECS registration, layout, accessors, game functions
    /// and virtual tables, and the game functions that use its type index.</summary>
    public Dictionary<string, object?> AboutClass(string name)
    {
        var gid = GameId();
        var (ns, cls) = Split(name);
        var result = new Dictionary<string, object?> { ["kind"] = "class", ["name"] = name };
        result["exports"] = Rows("SELECT d.name AS dll, s.rva, s.signature, s.kind FROM symbols s JOIN dlls d ON d.id = s.dll_id"
                                 + " WHERE s.class = ? AND s.namespace = ? ORDER BY s.kind, s.method LIMIT 400", cls, ns);
        var ecs = Rows("SELECT context, index_rva FROM ecs_ids WHERE dll_id = ? AND type = ? GROUP BY index_rva", gid, name);
        result["ecs"] = ecs;
        result["type_name"] = Rows("SELECT kind, func_rva FROM type_names WHERE dll_id = ? AND type IN (?, ?)", gid, "class " + name, "struct " + name);
        result["layout"] = Rows("SELECT t.kind, t.ext_name, t.size, t.file, t.line, f.idx, f.name, f.type, f.offset, f.checked, f.legacy"
                                + " FROM se_types t LEFT JOIN se_fields f ON f.type_id = t.id WHERE t.engine_class = ? ORDER BY f.idx", name);
        result["accessors"] = Rows("SELECT field, kind, offset, width, type, game_offset FROM accessors WHERE class = ? ORDER BY offset, field", name);
        result["game_functions"] = Rows(
            "SELECT m.game_rva, s.full_name, s.signature, m.method, m.relation, m.evidence FROM matches m"
            + " JOIN symbols s ON s.dll_id = m.toolkit_dll AND s.rva = m.toolkit_rva WHERE m.game_dll = ? AND s.class = ? AND s.namespace = ?"
            + " ORDER BY s.method", gid, cls, ns);
        result["game_vtables"] = Rows(
            "SELECT gv.rva, gv.slots, m.support FROM vtab_matches m JOIN vtabs gv ON gv.id = m.game_vtab JOIN vtabs t ON t.id = m.toolkit_vtab"
            + " JOIN symbols s ON s.id = t.symbol_id WHERE gv.dll_id = ? AND s.class = ? AND s.namespace = ?", gid, cls, ns);
        var index = ecs.Select(e => e["index_rva"]).ToList();
        if (index.Count > 0)
            result["game_users"] = Rows("SELECT x.func_rva, f.size, group_concat(DISTINCT x.kind) AS access FROM xrefs x"
                                        + " LEFT JOIN functions f ON f.dll_id = x.dll_id AND f.rva = x.func_rva"
                                        + $" WHERE x.dll_id = ? AND x.target_rva IN ({string.Join(",", index.Select(_ => "?"))}) GROUP BY x.func_rva ORDER BY f.size LIMIT 200",
                                        [gid, .. index]);
        return result;
    }

    /// <summary>The Toolkit name of a game function (a match that is not only inlined code), or null.</summary>
    public string? NameAt(long? gameDll, long rva) =>
        db.Scalar("SELECT s.full_name FROM matches m JOIN symbols s ON s.dll_id = m.toolkit_dll AND s.rva = m.toolkit_rva"
                  + " WHERE m.game_dll = ? AND m.game_rva = ? AND (m.relation IS NULL OR m.relation <> 'inlined' OR m.method = 'vtable')"
                  + " ORDER BY length(s.full_name) LIMIT 1", gameDll, rva) as string;

    /// <summary>An address in a binary: the function there, its names, callers, callees, the globals it
    /// touches and its pseudo-code (when decomp.sqlite has it).</summary>
    public Dictionary<string, object?> AboutAddress(string binary, long rva)
    {
        var d = Rows("SELECT id, name, origin FROM dlls WHERE name LIKE ? ESCAPE '\\' ORDER BY length(name) LIMIT 1", Sql.Like(binary));
        if (d.Count == 0)
            return new Dictionary<string, object?> { ["kind"] = "address", ["error"] = $"no binary matches {binary}" };
        long dllId = PyText.L(d[0]["id"]);
        string dll = PyText.Str(d[0]["name"]);
        var result = new Dictionary<string, object?> { ["kind"] = "address", ["binary"] = dll, ["rva"] = rva };
        var f = Rows("SELECT rva, size, leaf FROM functions WHERE dll_id = ? AND rva <= ? ORDER BY rva DESC LIMIT 1", dllId, rva);
        long start = rva;
        if (f.Count > 0 && rva < PyText.L(f[0]["rva"]) + (PyText.Long(f[0]["size"]) ?? 0))
        {
            result["function"] = f[0];
            start = PyText.L(f[0]["rva"]);
        }
        result["exported_as"] = Rows("SELECT signature, mangled FROM symbols WHERE dll_id = ? AND rva = ?", dllId, start);
        result["matches"] = Rows(
            "SELECT t.name AS toolkit_dll, m.toolkit_rva, s.signature, m.method, m.relation, m.evidence, m.sample FROM matches m"
            + " JOIN dlls t ON t.id = m.toolkit_dll LEFT JOIN symbols s ON s.dll_id = m.toolkit_dll AND s.rva = m.toolkit_rva"
            + " WHERE m.game_dll = ? AND m.game_rva = ?", dllId, start);
        var labels = Rows("SELECT name, mapping FROM se_symbols WHERE dll_id = ? AND rva = ?", dllId, rva);
        labels.AddRange(Rows("SELECT 'TypeIndex<' || type || '>' AS name, context FROM ecs_ids WHERE dll_id = ? AND index_rva = ? GROUP BY type", dllId, rva));
        labels.AddRange(Rows("SELECT 'GetTypeName<' || type || '>' AS name FROM type_names WHERE dll_id = ? AND func_rva = ?", dllId, start));
        result["labels"] = labels;
        result["strings"] = Rows("SELECT t.text FROM string_refs r JOIN strings t ON t.id = r.string_id WHERE t.dll_id = ? AND r.func_rva = ? LIMIT 40", dllId, start);
        var callers = Rows("SELECT dll_id, caller_rva, sites FROM calls WHERE callee_dll = ? AND callee_rva = ? LIMIT 60", dllId, start);
        var callees = Rows("SELECT callee_dll, callee_rva, import, sites FROM calls WHERE dll_id = ? AND caller_rva = ? LIMIT 80", dllId, start);
        result["callers"] = callers;
        result["callees"] = callees;
        result["touches"] = Rows("SELECT x.target_rva, x.kind, e.type FROM xrefs x LEFT JOIN ecs_ids e ON e.dll_id = x.dll_id AND e.index_rva = x.target_rva"
                                 + " WHERE x.dll_id = ? AND x.func_rva = ? GROUP BY x.target_rva, x.kind LIMIT 80", dllId, start);
        if (PyText.Str(d[0]["origin"]) == "game")
        {
            foreach (var c in callers)
                c["name"] = NameAt(PyText.Long(c["dll_id"]), PyText.L(c["caller_rva"]));
            foreach (var c in callees)
                if (c["callee_rva"] != null)
                    c["name"] = NameAt(PyText.Long(c["callee_dll"]), PyText.L(c["callee_rva"]));
        }
        var code = Decomp()?.Code(dll, start);
        if (code?.Text != null)
            result["pseudo_code"] = code.Text;
        return result;
    }

    DecompQueries? Decomp()
    {
        if (decomp == null && File.Exists(env.DecompDb))
        {
            string work = Path.GetDirectoryName(Path.GetFullPath(env.DecompDb))!;
            decomp = new DecompQueries(new DecompSettings(work, SymbolPaths.DecompTools, env.Game, env.Toolkit, SymbolPaths.SymbolsDb, env.MacosSymbols));
        }
        return decomp;
    }

    /// <summary>Where a function lives in the game and what is needed to hook it: by part of a name
    /// (Toolkit functions found in the game) or bg3.exe+0xRVA.</summary>
    public Dictionary<string, object?> Hook(string name)
    {
        var gid = GameId();
        var result = new Dictionary<string, object?> { ["kind"] = "hook", ["query"] = name };
        var functions = new List<Dictionary<string, object?>>();
        result["functions"] = functions;
        var m = Address().Match(name);
        List<Dictionary<string, object?>> found = m.Success
            ? [new() { ["game_rva"] = SymbolContext.Rva(m), ["full_name"] = null, ["signature"] = null, ["method"] = "address", ["relation"] = null, ["evidence"] = null }]
            : Rows("SELECT m.game_rva, s.full_name, s.signature, s.mangled, m.method, m.relation, m.evidence, d.name AS toolkit_dll, m.toolkit_rva"
                   + " FROM matches m JOIN symbols s ON s.dll_id = m.toolkit_dll AND s.rva = m.toolkit_rva JOIN dlls d ON d.id = m.toolkit_dll"
                   + " WHERE m.game_dll = ? AND s.full_name LIKE ? ESCAPE '\\' ORDER BY length(s.full_name) LIMIT 20", gid, Sql.Like(name));
        foreach (var f in found)
        {
            long rva = PyText.L(f["game_rva"]);
            f["address"] = $"bg3.exe+0x{PyText.Hex(rva)}";
            string? relation = f.GetValueOrDefault("relation") as string;
            if (f.GetValueOrDefault("method") as string == "vtable")
                f["trust"] = "the function in the matching slot of the class's virtual table"
                             + (relation == "inlined" ? "; larger than in the Toolkit because its callees are inlined into it" : "");
            else
                f["trust"] = relation switch
                {
                    "same" => "function found, sizes agree",
                    "inlined" => "the game function is much larger: the code was probably inlined into this caller",
                    "partial" => "the game function is much smaller: probably only a part",
                    _ => "not compared",
                };
            f["other_names_at_address"] = Rows("SELECT DISTINCT s.full_name FROM matches m JOIN symbols s ON s.dll_id = m.toolkit_dll AND s.rva = m.toolkit_rva"
                                               + " WHERE m.game_dll = ? AND m.game_rva = ? AND s.full_name IS NOT ? LIMIT 8", gid, rva, f.GetValueOrDefault("full_name"))
                .Select(r => r["full_name"]).ToList();
            f["size"] = db.Scalar("SELECT size FROM functions WHERE dll_id = ? AND rva = ?", gid, rva);
            f["callers"] = Rows("SELECT caller_rva FROM calls WHERE callee_dll = ? AND callee_rva = ? LIMIT 25", gid, rva)
                .Select(c => new Dictionary<string, object?>
                {
                    ["address"] = $"bg3.exe+0x{PyText.Hex(PyText.L(c["caller_rva"]))}",
                    ["name"] = NameAt(gid, PyText.L(c["caller_rva"])),
                }).ToList();
            f["in_vtables"] = Rows("SELECT v.rva AS table_rva, s.idx AS slot FROM vtab_slots s JOIN vtabs v ON v.id = s.vtab_id WHERE v.dll_id = ? AND s.rva = ? LIMIT 10", gid, rva);
            f["signature_command"] = $"gatekit symbols signature bg3.exe+0x{PyText.Hex(rva)}";
            functions.Add(f);
        }
        if (found.Count == 0)
        {
            result["toolkit_only"] = Rows("SELECT d.name AS dll, s.rva, s.signature FROM symbols s JOIN dlls d ON d.id = s.dll_id"
                                          + " WHERE s.kind IN ('method','function') AND s.full_name LIKE ? ESCAPE '\\' ORDER BY length(s.full_name) LIMIT 20", Sql.Like(name));
            result["note"] = "not found in the game executable; the Toolkit addresses listed are editor addresses. Look for the game function among "
                             + "the users of the ECS types it touches: gatekit symbols users <component>";
        }
        return result;
    }

    /// <summary>Tables, columns (with the comments written in the schema) and row counts of this database.</summary>
    public Dictionary<string, object?> Schema(string? path = null) => Schema(db, path ?? db.DataSource);

    [GeneratedRegex(@"_(data|idx|content|docsize|config)$")]
    private static partial Regex FtsShadow();

    [GeneratedRegex(@"^\s*(\w+)\s+[A-Z].*?--\s*(.*)$")]
    private static partial Regex ColumnComment();

    /// <summary>Tables and views of any SQLite database (FTS shadow tables left out): type, row count
    /// and columns with the meaning written after `--` in the CREATE statement.</summary>
    public static Dictionary<string, object?> Schema(SqliteConnection connection, string path)
    {
        var tables = new Dictionary<string, object?>();
        foreach (var r in connection.Query("SELECT name, type, sql FROM sqlite_master WHERE type IN ('table','view') AND name NOT LIKE 'sqlite_%' ORDER BY type, name").ToList())
        {
            string name = PyText.Str(r[0]), type = PyText.Str(r[1]);
            if (FtsShadow().IsMatch(name))
                continue;
            var comments = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in Py.SplitLines(r[2] as string ?? ""))
            {
                var m = ColumnComment().Match(line);
                if (m.Success)
                    comments[m.Groups[1].Value] = Py.Strip(m.Groups[2].Value);
            }
            object? count = null;
            List<Dictionary<string, object?>> columns = [];
            try
            {
                count = type == "table" ? connection.Scalar($"SELECT count(*) FROM \"{name.Replace("\"", "\"\"")}\"") : null;
                columns = connection.Query($"PRAGMA table_info(\"{name.Replace("\"", "\"\"")}\")")
                    .Select(c => new Dictionary<string, object?> { ["name"] = c[1], ["type"] = c[2], ["comment"] = comments.GetValueOrDefault(PyText.Str(c[1]), "") })
                    .ToList();
            }
            catch (SqliteException)
            {
                count = null;
                columns = [];
            }
            tables[name] = new Dictionary<string, object?> { ["type"] = type, ["rows"] = count, ["columns"] = columns };
        }
        return new Dictionary<string, object?> { ["path"] = path, ["tables"] = tables };
    }

    /// <summary>One read-only SQL statement: {columns, rows, truncated} with at most <paramref name="limit"/> rows.</summary>
    public Dictionary<string, object?> RunSql(string query, int limit = 500) => RunSql(db, query, limit);

    /// <summary>The same over any connection (open it read-only).</summary>
    public static Dictionary<string, object?> RunSql(SqliteConnection connection, string query, int limit = 500)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = query;
        using var r = cmd.ExecuteReader();
        var columns = Enumerable.Range(0, r.FieldCount).Select(r.GetName).ToList();
        var rows = new List<List<object?>>();
        while (rows.Count < limit && r.Read())
        {
            var row = new List<object?>(r.FieldCount);
            for (int i = 0; i < r.FieldCount; i++)
                row.Add(r.IsDBNull(i) ? null : r.GetValue(i));
            rows.Add(row);
        }
        return new Dictionary<string, object?> { ["columns"] = columns, ["rows"] = rows, ["truncated"] = rows.Count == limit };
    }

    /// <summary>Everything known about the addresses of a game executable (export's data).</summary>
    public (string Module, List<GameLabel> Labels) GameLabels(string dll = "bg3.exe") => Queries.GameLabels.Collect(db, dll);

    public void Dispose()
    {
        decomp?.Dispose();
        if (ownsDb)
            db.Dispose();
    }
}
