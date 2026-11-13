using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace GateKit.Symbols.Decomp;

/// <summary>`load` (functions.jsonl of finished runs into decomp.sqlite) and `status`.</summary>
internal static class LoadCommand
{
    static SqliteCommand Prepare(SqliteConnection db, string sql, params SqliteType[] types)
    {
        var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        for (int i = 0; i < types.Length; i++)
            cmd.Parameters.Add("$" + (i + 1), types[i]);
        cmd.Prepare();
        return cmd;
    }

    static object Value(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? (object)DBNull.Value,
        JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
        JsonValueKind.True => 1L,
        JsonValueKind.False => 0L,
        _ => DBNull.Value,
    };

    static JsonElement? Get(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;

    /// <summary>Loads every finished output (or the one of `binary`) into the database; returns the lines it printed.</summary>
    public static int Run(DecompSession s, string? binary)
    {
        if (!Directory.Exists(s.Settings.Work))
            return 0;
        var outputs = Directory.EnumerateDirectories(s.Settings.Work)
            .Select(d => Path.Combine(d, "functions.jsonl")).Where(File.Exists).Order(Py.PathOrder).ToList();
        if (outputs.Count == 0)
            return 0;
        var db = s.OpenWrite();
        foreach (var output in outputs)
        {
            string name = Path.GetFileName(Path.GetDirectoryName(output)!);
            if (binary != null && !string.Equals(binary.ToLowerInvariant(), name.ToLowerInvariant(), StringComparison.Ordinal))
                continue;
            db.Execute("BEGIN");
            long binaryId;
            var row = db.Scalar("SELECT id FROM binaries WHERE name = ?", name);
            if (row != null)
            {
                binaryId = Convert.ToInt64(row, CultureInfo.InvariantCulture);
                db.Execute("DELETE FROM code_fts WHERE rowid IN (SELECT id FROM functions WHERE binary_id = ?)", binaryId);
                db.Execute("DELETE FROM code WHERE function_id IN (SELECT id FROM functions WHERE binary_id = ?)", binaryId);
                db.Execute("DELETE FROM functions WHERE binary_id = ?", binaryId);
                db.Execute("DELETE FROM calls WHERE binary_id = ?", binaryId);
            }
            else
            {
                db.Execute("INSERT INTO binaries (name) VALUES (?)", name);
                binaryId = Convert.ToInt64(db.Scalar("SELECT last_insert_rowid()"), CultureInfo.InvariantCulture);
            }
            using var function = Prepare(db, "INSERT INTO functions (binary_id, rva, name, signature, size, thunk, lines, error) VALUES ($1,$2,$3,$4,$5,$6,$7,$8)"
                                             + " RETURNING id",
                                         SqliteType.Integer, SqliteType.Integer, SqliteType.Text, SqliteType.Text, SqliteType.Integer, SqliteType.Integer,
                                         SqliteType.Integer, SqliteType.Text);
            using var code = Prepare(db, "INSERT INTO code VALUES ($1,$2)", SqliteType.Integer, SqliteType.Blob);
            using var fts = Prepare(db, "INSERT INTO code_fts (rowid, name, code) VALUES ($1,$2,$3)", SqliteType.Integer, SqliteType.Text, SqliteType.Text);
            using var call = Prepare(db, "INSERT INTO calls VALUES ($1,$2,$3)", SqliteType.Integer, SqliteType.Integer, SqliteType.Integer);
            int count = 0, failed = 0;
            using (var reader = new StreamReader(output, new UTF8Encoding(false, false), false, 1 << 20))
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    long rva;
                    string? fname, sig, error, text;
                    object size;
                    bool thunk;
                    List<long> callees = [];
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var r = doc.RootElement;
                        if (r.ValueKind != JsonValueKind.Object || Py.Hex(r.GetProperty("rva").GetString() ?? "") is not long parsed)
                            continue;
                        rva = parsed;
                        fname = Get(r, "name")?.GetString();
                        sig = Get(r, "sig")?.GetString();
                        error = Get(r, "error")?.GetString();
                        text = Get(r, "code")?.GetString();
                        size = Get(r, "size") is JsonElement z ? Value(z) : DBNull.Value;
                        thunk = Get(r, "thunk") is JsonElement t && t.ValueKind == JsonValueKind.True;
                        if (Get(r, "callees") is JsonElement list && list.ValueKind == JsonValueKind.Array)
                            foreach (var c in list.EnumerateArray())
                                if (Py.Hex(c.GetString() ?? "") is long callee)
                                    callees.Add(callee);
                    }
                    catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
                    {
                        continue;                         // a line cut short by an interrupted run
                    }
                    function.Parameters[0].Value = binaryId;
                    function.Parameters[1].Value = rva;
                    function.Parameters[2].Value = (object?)fname ?? DBNull.Value;
                    function.Parameters[3].Value = (object?)sig ?? DBNull.Value;
                    function.Parameters[4].Value = size;
                    function.Parameters[5].Value = thunk ? 1 : 0;
                    function.Parameters[6].Value = string.IsNullOrEmpty(text) ? DBNull.Value : text.Count(c => c == '\n');
                    function.Parameters[7].Value = (object?)error ?? DBNull.Value;
                    long id = Convert.ToInt64(function.ExecuteScalar(), CultureInfo.InvariantCulture);
                    if (!string.IsNullOrEmpty(text))
                    {
                        code.Parameters[0].Value = id;
                        code.Parameters[1].Value = DecompStore.Compress(text);
                        code.ExecuteNonQuery();
                        fts.Parameters[0].Value = id;
                        fts.Parameters[1].Value = string.IsNullOrEmpty(fname) ? "" : fname;
                        fts.Parameters[2].Value = text;
                        fts.ExecuteNonQuery();
                    }
                    else
                        failed++;
                    foreach (var callee in callees)
                    {
                        call.Parameters[0].Value = binaryId;
                        call.Parameters[1].Value = rva;
                        call.Parameters[2].Value = callee;
                        call.ExecuteNonQuery();
                    }
                    count++;
                }
            }
            db.Execute("UPDATE binaries SET loaded = ?, functions = ?, failed = ? WHERE id = ?", Py.Now(), count, failed, binaryId);
            db.Execute("COMMIT");
            int renamed = NameExports(s, db, name);
            s.Out.WriteLine($"{name}: {count} functions, {failed} without pseudo-code" + (renamed > 0 ? $", {renamed} named from the export table" : ""));
        }
        return 0;
    }

    /// <summary>Gives functions Ghidra left as FUN_... the name their address is exported under
    /// (Ghidra's demangler gives up on some long decorated names). Returns how many were named.</summary>
    public static int NameExports(DecompSession s, SqliteConnection db, string binary)
    {
        var sym = s.Symbols;
        var id = db.Scalar("SELECT id FROM binaries WHERE name = ?", binary);
        if (sym == null || id == null)
            return 0;
        var names = new Dictionary<long, string>();
        try
        {
            foreach (var r in sym.Query("SELECT s.rva, min(s.full_name) FROM symbols s JOIN dlls d ON d.id = s.dll_id WHERE d.name = ? AND s.rva IS NOT NULL"
                                        + " AND s.kind IN ('method', 'function') GROUP BY s.rva", binary))
                if (r[1] != null)
                    names[Convert.ToInt64(r[0], CultureInfo.InvariantCulture)] = Py.Str(r[1]);
        }
        catch (SqliteException)
        {
            return 0;
        }
        var todo = new List<(string, long)>();
        foreach (var r in db.Query("SELECT id, rva FROM functions WHERE binary_id = ? AND (name LIKE 'FUN!_%' ESCAPE '!' OR name LIKE 'thunk!_FUN!_%' ESCAPE '!')", id))
            if (names.TryGetValue(Convert.ToInt64(r[1], CultureInfo.InvariantCulture), out var name))
                todo.Add((name, Convert.ToInt64(r[0], CultureInfo.InvariantCulture)));
        db.Execute("BEGIN");
        foreach (var (name, fid) in todo)
            db.Execute("UPDATE functions SET name = ? WHERE id = ?", name, fid);
        db.Execute("COMMIT");
        return todo.Count;
    }

    /// <summary>What has been decompiled and loaded.</summary>
    public static int Status(DecompSession s)
    {
        var work = s.Settings.Work;
        if (Directory.Exists(work))
        {
            foreach (var folder in Directory.EnumerateDirectories(work).Order(Py.PathOrder))
            {
                var output = Path.Combine(folder, "functions.jsonl");
                var last = Path.Combine(folder, "last_run.json");
                long size = File.Exists(output) ? new FileInfo(output).Length : 0;
                string info = ", running or never finished";
                if (File.Exists(last))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(last, Encoding.UTF8));
                    string Field(string key) => doc.RootElement.TryGetProperty(key, out var v) ? JsonStr(v) : "None";
                    info = $", last run {Field("seconds")} s, exit {Field("exit")}, finished {Field("finished")}";
                }
                s.Out.WriteLine($"{Path.GetFileName(folder)}: output {size / 1e6:F0} MB" + info);
            }
        }
        var path = s.Settings.DbPath;
        if (File.Exists(path))
        {
            using var db = Sql.OpenReadOnly(path);
            foreach (var r in db.Query("SELECT name, loaded, functions, failed FROM binaries"))
                s.Out.WriteLine($"  in database: {Py.Str(r[0])}: {Py.Str(r[2])} functions, {Py.Str(r[3])} without pseudo-code (loaded {Py.Str(r[1])})");
            s.Out.WriteLine($"  {path}: {new FileInfo(path).Length / 1e6:F0} MB");
        }
        return 0;
    }

    /// <summary>How Python prints a value read with json.loads.</summary>
    static string JsonStr(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? "",
        JsonValueKind.Number => v.TryGetInt64(out var l) ? l.ToString(CultureInfo.InvariantCulture) : Py.Float(v.GetDouble()),
        JsonValueKind.True => "True",
        JsonValueKind.False => "False",
        JsonValueKind.Null => "None",
        _ => v.GetRawText(),
    };
}
