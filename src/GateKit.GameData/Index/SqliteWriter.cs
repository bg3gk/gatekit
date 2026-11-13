using Microsoft.Data.Sqlite;

namespace GateKit.GameData.Index;

/// <summary>A prepared INSERT with positional parameters, reused for every row.</summary>
internal sealed class Insert : IDisposable
{
    private readonly SqliteCommand _command;
    private readonly SqliteParameter[] _parameters;

    public Insert(SqliteConnection connection, SqliteTransaction? transaction, string sql, int count)
    {
        _command = connection.CreateCommand();
        _command.CommandText = sql;
        _command.Transaction = transaction;
        _parameters = new SqliteParameter[count];
        for (var i = 0; i < count; i++)
        {
            _parameters[i] = _command.CreateParameter();
            _parameters[i].ParameterName = "$" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            _command.Parameters.Add(_parameters[i]);
        }
        _command.Prepare();
    }

    public void Run(params ReadOnlySpan<object?> values)
    {
        for (var i = 0; i < _parameters.Length; i++)
        {
            _parameters[i].Value = values[i] ?? DBNull.Value;
        }
        _command.ExecuteNonQuery();
    }

    public void Dispose() => _command.Dispose();
}

internal static class Sql
{
    /// <summary>Runs a script of several statements.</summary>
    public static void Exec(SqliteConnection db, string sql, SqliteTransaction? transaction = null)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = transaction;
        cmd.ExecuteNonQuery();
    }

    public static object? Scalar(SqliteConnection db, string sql, params object?[] args)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        for (var i = 0; i < args.Length; i++)
        {
            cmd.Parameters.AddWithValue("$" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), args[i] ?? DBNull.Value);
        }
        var v = cmd.ExecuteScalar();
        return v is DBNull ? null : v;
    }

    /// <summary>Numbers <c>?</c> placeholders as $1, $2, ... so a statement reads like the old one.</summary>
    public static string Positional(string sql)
    {
        var sb = new System.Text.StringBuilder(sql.Length + 16);
        var n = 0;
        var quote = '\0';
        foreach (var c in sql)
        {
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                sb.Append(c);
            }
            else if (c is '\'' or '"')
            {
                quote = c;
                sb.Append(c);
            }
            else if (c == '?')
            {
                sb.Append('$').Append(++n);
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
