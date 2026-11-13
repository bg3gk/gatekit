using System.Globalization;
using GateKit.GameData.Index;
using Microsoft.Data.Sqlite;

namespace GateKit.GameData.Query;

/// <summary>A query could not be answered; the message is meant for the user.</summary>
public sealed class DataQueryException(string message) : Exception(message);

/// <summary>A result row: columns by name in query order, and by position.</summary>
public sealed class Row : Dictionary<string, object?>
{
    private readonly object?[] _values;

    public Row(string[] names, object?[] values) : base(StringComparer.Ordinal)
    {
        _values = values;
        for (var i = 0; i < names.Length; i++)
        {
            this[names[i]] = values[i];
        }
    }

    /// <summary>The value of the i-th column.</summary>
    public object? At(int i) => _values[i];

    public int Width => _values.Length;

    public string? S(string name) => TryGetValue(name, out var v) ? v as string ?? (v == null ? null : Convert.ToString(v, CultureInfo.InvariantCulture)) : null;

    public long? L(string name) => TryGetValue(name, out var v) && v != null ? Convert.ToInt64(v, CultureInfo.InvariantCulture) : null;

    public double? D(string name) => TryGetValue(name, out var v) && v != null ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : null;
}

/// <summary>A database opened read-only. Statements are written with <c>?</c> placeholders.</summary>
public sealed class ReadOnlyDb : IDisposable
{
    private readonly SqliteConnection _db;

    public string Path { get; }

    private ReadOnlyDb(string path)
    {
        Path = path;
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        _db.Open();
    }

    /// <summary>Opens a database that must exist, or explains how to build it.</summary>
    public static ReadOnlyDb Open(string path, string missing)
    {
        if (!File.Exists(path))
        {
            throw new DataQueryException(missing);
        }
        return new ReadOnlyDb(path);
    }

    /// <summary>Opens a database when it exists; null otherwise.</summary>
    public static ReadOnlyDb? TryOpen(string path) => File.Exists(path) ? new ReadOnlyDb(path) : null;

    public SqliteCommand Command(string sql, IReadOnlyList<object?> args)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = Sql.Positional(sql);
        for (var i = 0; i < args.Count; i++)
        {
            cmd.Parameters.AddWithValue("$" + (i + 1).ToString(CultureInfo.InvariantCulture), args[i] ?? DBNull.Value);
        }
        return cmd;
    }

    /// <summary>Every row (or at most <paramref name="limit"/>) of a query.</summary>
    public List<Row> Rows(string sql, params object?[] args) => RowsLimited(sql, int.MaxValue, args);

    public List<Row> RowsLimited(string sql, int limit, params object?[] args)
    {
        using var cmd = Command(sql, args);
        using var reader = cmd.ExecuteReader();
        var names = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        var rows = new List<Row>();
        while (rows.Count < limit && reader.Read())
        {
            var values = new object?[reader.FieldCount];
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            rows.Add(new Row(names, values));
        }
        return rows;
    }

    /// <summary>The first row, or null.</summary>
    public Row? One(string sql, params object?[] args) => RowsLimited(sql, 1, args).FirstOrDefault();

    /// <summary>The first column of the first row, or null.</summary>
    public object? Scalar(string sql, params object?[] args) => One(sql, args)?.At(0);

    /// <summary>Runs one statement; columns, up to <paramref name="limit"/> rows, and whether more were cut.</summary>
    public (string[] Columns, List<object?[]> Rows, bool Truncated) Run(string sql, int limit)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        var rows = new List<object?[]>();
        while (rows.Count < limit && reader.Read())
        {
            var values = new object?[reader.FieldCount];
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            rows.Add(values);
        }
        return (columns, rows, rows.Count == limit);
    }

    public void Dispose() => _db.Dispose();
}

/// <summary>Helpers for writing queries the way the first version did.</summary>
public static class Q
{
    /// <summary>A LIKE pattern that finds the text anywhere (with ESCAPE '\').</summary>
    public static string Like(string text) =>
        "%" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";

    public static bool IsUuid(string? s) => s != null && UuidRegex().IsMatch(s);

    private static System.Text.RegularExpressions.Regex? _uuid;

    private static System.Text.RegularExpressions.Regex UuidRegex() => _uuid ??= new(
        "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>A value printed as Python's str() prints it (floats keep ".0", null is "None").</summary>
    public static string Py(object? v) => v switch
    {
        null => "None",
        double d when double.IsFinite(d) && d == Math.Floor(d) && Math.Abs(d) < 1e16 => d.ToString("0.0", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => Py((double)f),
        bool b => b ? "True" : "False",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    /// <summary>A number with a fixed count of decimals.</summary>
    public static string F(double? v, int decimals) => (v ?? 0).ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    /// <summary>A whole number with thousands separators (Python's {:,}).</summary>
    public static string Thousands(long? v) => (v ?? 0).ToString("#,0", CultureInfo.InvariantCulture);

    /// <summary>Text cut to at most n characters.</summary>
    public static string Cut(string? s, int n) => s == null ? "" : s.Length <= n ? s : s[..n];

    /// <summary>"?, ?, ?" for n values; "NULL" for none.</summary>
    public static string Marks(int n) => n == 0 ? "NULL" : string.Join(",", Enumerable.Repeat("?", n));
}
