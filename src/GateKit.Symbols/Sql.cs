using Microsoft.Data.Sqlite;

namespace GateKit.Symbols;

/// <summary>Small helpers over Microsoft.Data.Sqlite shared by the symbol and pseudo-code databases.</summary>
public static class Sql
{
    /// <summary>Opens a database read-only (the file must exist).</summary>
    public static SqliteConnection OpenReadOnly(string path)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        c.Open();
        return c;
    }

    /// <summary>Opens (creating when needed) a database for writing.</summary>
    public static SqliteConnection OpenWrite(string path)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        c.Open();
        return c;
    }

    /// <summary>A LIKE pattern that matches the text anywhere; use with ESCAPE '\'.</summary>
    public static string Like(string text) =>
        "%" + text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    /// <summary>Runs a statement; parameters are bound positionally to ?1, ?2 ... or plain '?' marks.</summary>
    public static int Execute(this SqliteConnection db, string sql, params object?[] args)
    {
        using var cmd = Prepare(db, sql, args);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Rows of a query as object arrays (DBNull becomes null).</summary>
    public static IEnumerable<object?[]> Query(this SqliteConnection db, string sql, params object?[] args)
    {
        using var cmd = Prepare(db, sql, args);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var row = new object?[r.FieldCount];
            for (int i = 0; i < row.Length; i++)
                row[i] = r.IsDBNull(i) ? null : r.GetValue(i);
            yield return row;
        }
    }

    /// <summary>Rows of a query as dictionaries keyed by column name.</summary>
    public static List<Dictionary<string, object?>> Rows(this SqliteConnection db, string sql, params object?[] args)
    {
        using var cmd = Prepare(db, sql, args);
        using var r = cmd.ExecuteReader();
        var list = new List<Dictionary<string, object?>>();
        while (r.Read())
        {
            var row = new Dictionary<string, object?>(r.FieldCount);
            for (int i = 0; i < r.FieldCount; i++)
                row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
            list.Add(row);
        }
        return list;
    }

    /// <summary>First row, or null.</summary>
    public static object?[]? One(this SqliteConnection db, string sql, params object?[] args) =>
        db.Query(sql, args).FirstOrDefault();

    /// <summary>First column of the first row, or null.</summary>
    public static object? Scalar(this SqliteConnection db, string sql, params object?[] args)
    {
        using var cmd = Prepare(db, sql, args);
        var v = cmd.ExecuteScalar();
        return v is DBNull ? null : v;
    }

    public static long? Long(object? v) => v == null ? null : Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Turns '?' marks into numbered parameters, so that SQL reads as in the original tools.</summary>
    internal static SqliteCommand Prepare(SqliteConnection db, string sql, object?[] args)
    {
        var cmd = db.CreateCommand();
        if (args.Length > 0 && sql.Contains('?'))
        {
            var b = new System.Text.StringBuilder(sql.Length + 16);
            int n = 0;
            bool quoted = false;
            for (int i = 0; i < sql.Length; i++)
            {
                char c = sql[i];
                if (c == '\'')
                    quoted = !quoted;
                if (c == '?' && !quoted && (i + 1 >= sql.Length || !char.IsDigit(sql[i + 1])))
                {
                    b.Append("$p").Append(++n);
                    continue;
                }
                b.Append(c);
            }
            sql = b.ToString();
            for (int i = 0; i < args.Length; i++)
                cmd.Parameters.AddWithValue("$p" + (i + 1), args[i] ?? DBNull.Value);
        }
        cmd.CommandText = sql;
        return cmd;
    }
}
