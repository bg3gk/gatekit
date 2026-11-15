using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace GateKit.Symbols.Decomp;

/// <summary>`load` (the decompiler's export of finished runs into decomp.sqlite) and `status`.</summary>
internal static class LoadCommand
{
    /// <summary>Loads every finished output (or the one of `binary`) into the database; returns the lines it printed.</summary>
    public static int Run(DecompSession s, string? binary)
    {
        if (!Directory.Exists(s.Settings.Work))
            return 0;
        var outputs = Directory.EnumerateDirectories(s.Settings.Work)
            .Select(d => DecompStore.ExportPath(d, s.Err)).Where(File.Exists).Order(Py.PathOrder).ToList();
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
            int count, failed;
            using (var writer = new FunctionWriter(db, binaryId))
            {
                using var reader = new StreamReader(output, new UTF8Encoding(false, false), false, 1 << 20);
                string? line;
                while ((line = reader.ReadLine()) != null)
                    writer.Write(line, replace: false);
                (count, failed) = (writer.Count, writer.Failed);
            }
            db.Execute("UPDATE binaries SET loaded = ?, functions = ?, failed = ? WHERE id = ?", Py.Now(), count, failed, binaryId);
            db.Execute("COMMIT");
            int renamed = NameExports(s, db, name);
            Provenance.Record(s, db, name);
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
                var output = DecompStore.ExistingExport(folder) ?? Path.Combine(folder, DecompStore.ExportName);
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
            try
            {
                foreach (var r in db.Query("SELECT b.name, r.finished, r.names, r.structures, r.functions FROM republishes r JOIN binaries b ON b.id = r.binary_id"
                                           + " WHERE r.rowid IN (SELECT max(rowid) FROM republishes GROUP BY binary_id)"))
                    s.Out.WriteLine($"  refreshed: {Py.Str(r[0])}: {Py.Str(r[2])} names, {Py.Str(r[3])} structures, {Py.Str(r[4])} functions decompiled again ({Py.Str(r[1])})");
            }
            catch (SqliteException)
            {
                // a database without the table of refreshes
            }
            if (Provenance.Read(db) is { } stamps)
                foreach (var stamp in stamps)
                    s.Out.WriteLine("  made from: " + Provenance.Line(stamp));
            else
                s.Out.WriteLine("  made from: not recorded (`gatekit decomp stamp` records it from the binaries the projects keep)");
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
