using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace GateKit.Symbols.Decomp;

/// <summary>Writes lines of the decompiler's export (pseudocode.jsonl, what ExportDecomp.java prints) into decomp.sqlite as
/// the functions of one binary: the row, the compressed text, the full-text entry and the calls.
/// `load` adds every line to an emptied binary; `refresh` replaces the functions it decompiled again.</summary>
internal sealed class FunctionWriter : IDisposable
{
    readonly SqliteConnection db;
    readonly long binaryId;
    readonly SqliteCommand function, code, fts, call, find;

    public FunctionWriter(SqliteConnection db, long binaryId)
    {
        this.db = db;
        this.binaryId = binaryId;
        function = Prepare("INSERT INTO functions (binary_id, rva, name, signature, size, thunk, lines, error) VALUES ($1,$2,$3,$4,$5,$6,$7,$8) RETURNING id",
                           SqliteType.Integer, SqliteType.Integer, SqliteType.Text, SqliteType.Text, SqliteType.Integer, SqliteType.Integer,
                           SqliteType.Integer, SqliteType.Text);
        code = Prepare("INSERT INTO code VALUES ($1,$2)", SqliteType.Integer, SqliteType.Blob);
        fts = Prepare("INSERT INTO code_fts (rowid, name, code) VALUES ($1,$2,$3)", SqliteType.Integer, SqliteType.Text, SqliteType.Text);
        call = Prepare("INSERT INTO calls VALUES ($1,$2,$3)", SqliteType.Integer, SqliteType.Integer, SqliteType.Integer);
        find = Prepare("SELECT id FROM functions WHERE binary_id = $1 AND rva = $2", SqliteType.Integer, SqliteType.Integer);
    }

    /// <summary>Lines written.</summary>
    public int Count { get; private set; }

    /// <summary>Of those, lines without pseudo-code (the decompiler gave up).</summary>
    public int Failed { get; private set; }

    /// <summary>Of those written with <c>replace</c>, functions the database did not have before.</summary>
    public int Added { get; private set; }

    SqliteCommand Prepare(string sql, params SqliteType[] types)
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

    /// <summary>The address a line is about, read without parsing the line: ExportDecomp.java
    /// always writes <c>{"rva":"HEX"</c> first. Null for anything else.</summary>
    public static long? RvaOf(string line)
    {
        const string head = "{\"rva\":\"";
        if (!line.StartsWith(head, StringComparison.Ordinal))
            return null;
        int end = line.IndexOf('"', head.Length);
        return end < 0 ? null : Py.Hex(line[head.Length..end]);
    }

    /// <summary>Writes one line. With <paramref name="replace"/> the function already at that
    /// address goes first, with its text and calls. False for a line that is not a function
    /// (one cut short by an interrupted run).</summary>
    public bool Write(string line, bool replace)
    {
        long rva;
        string? name, signature, error, text;
        object size;
        bool thunk;
        List<long> callees = [];
        try
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || Py.Hex(r.GetProperty("rva").GetString() ?? "") is not long parsed)
                return false;
            rva = parsed;
            name = Get(r, "name")?.GetString();
            signature = Get(r, "sig")?.GetString();
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
            return false;
        }
        if (replace)
        {
            find.Parameters[0].Value = binaryId;
            find.Parameters[1].Value = rva;
            var old = new List<long>();
            using (var rows = find.ExecuteReader())
                while (rows.Read())
                    old.Add(rows.GetInt64(0));
            if (old.Count == 0)
                Added++;
            foreach (long id in old)
            {
                db.Execute("DELETE FROM code_fts WHERE rowid = ?", id);
                db.Execute("DELETE FROM code WHERE function_id = ?", id);
                db.Execute("DELETE FROM functions WHERE id = ?", id);
            }
            db.Execute("DELETE FROM calls WHERE binary_id = ? AND caller = ?", binaryId, rva);
        }
        function.Parameters[0].Value = binaryId;
        function.Parameters[1].Value = rva;
        function.Parameters[2].Value = (object?)name ?? DBNull.Value;
        function.Parameters[3].Value = (object?)signature ?? DBNull.Value;
        function.Parameters[4].Value = size;
        function.Parameters[5].Value = thunk ? 1 : 0;
        function.Parameters[6].Value = string.IsNullOrEmpty(text) ? DBNull.Value : text.Count(c => c == '\n');
        function.Parameters[7].Value = (object?)error ?? DBNull.Value;
        long written = Convert.ToInt64(function.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (!string.IsNullOrEmpty(text))
        {
            code.Parameters[0].Value = written;
            code.Parameters[1].Value = DecompStore.Compress(text);
            code.ExecuteNonQuery();
            fts.Parameters[0].Value = written;
            fts.Parameters[1].Value = string.IsNullOrEmpty(name) ? "" : name;
            fts.Parameters[2].Value = text;
            fts.ExecuteNonQuery();
        }
        else
            Failed++;
        foreach (var callee in callees)
        {
            call.Parameters[0].Value = binaryId;
            call.Parameters[1].Value = rva;
            call.Parameters[2].Value = callee;
            call.ExecuteNonQuery();
        }
        Count++;
        return true;
    }

    public void Dispose()
    {
        function.Dispose();
        code.Dispose();
        fts.Dispose();
        call.Dispose();
        find.Dispose();
    }
}
