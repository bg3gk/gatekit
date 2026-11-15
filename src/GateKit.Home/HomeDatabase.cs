using Microsoft.Data.Sqlite;

namespace GateKit.Home;

/// <summary>A database that a query needs is not there, or is an empty stub. The message names the
/// path, the setting that moves it and `gatekit doctor`.</summary>
public sealed class MissingDatabaseException(string path, string message) : Exception(message)
{
    public string DatabasePath { get; } = path;
}

/// <summary>
/// The one way a database is opened for reading. A query against a database that is not there must
/// fail with a sentence, never answer "no rows": a missing file, an empty file and a file with no
/// tables (what opening a missing path for writing leaves behind) are all refused here.
/// </summary>
public static class HomeDatabase
{
    /// <summary>Opens a database read-only, or throws <see cref="MissingDatabaseException"/>.</summary>
    /// <param name="path">The database file.</param>
    /// <param name="home">The home the path was resolved in, for naming its setting; null is <see cref="GateKitHome.Current"/>.</param>
    public static SqliteConnection OpenReadOnly(string path, GateKitHome? home = null) =>
        TryOpen(path, out var problem) ?? throw new MissingDatabaseException(path, Explain(path, problem!, home));

    /// <summary>What is wrong with a database ("does not exist", "holds no tables ..."), or null when it can be read.</summary>
    public static string? Problem(string path)
    {
        using var db = TryOpen(path, out var problem);
        return problem;
    }

    /// <summary>Whether the database is there and has tables: for the answers that only get richer with it.</summary>
    public static bool Usable(string path) => Problem(path) is null;

    /// <summary>
    /// The sentence for a database that is not there: its path, what is wrong, the setting that
    /// moves it, what makes it, and `gatekit doctor`.
    /// </summary>
    public static string Explain(string path, string problem, GateKitHome? home = null)
    {
        home ??= GateKitHome.Current;
        var full = Path.GetFullPath(path);
        var known = home.DatabaseAt(full);
        var setting = known is null
            ? "it was named on the command line or by a caller"
            : $"it is the {known.Purpose} ({known.SourceName}: {known.Origin}); {known.Change} moves it";
        var make = known?.Id switch
        {
            "index" => "; `gatekit data build` makes it, `gatekit data install` fetches the published one",
            "symbols" => "; `gatekit symbols build` makes it, `gatekit symbols install` fetches the published one",
            "decomp-db" => "; `gatekit decomp run <binary>` then `gatekit decomp load` make it",
            _ => "",
        };
        return $"{full} {problem}: {setting}{make}. `gatekit doctor` shows where every location comes from.";
    }

    /// <summary>A note for a database that only enriches an answer, when it cannot be read: what is
    /// left out and why. Null when the database is fine.</summary>
    public static string? LeftOut(string path, string what, GateKitHome? home = null) =>
        Problem(path) is { } problem ? $"note: {what} left out - " + Explain(path, problem, home) : null;

    static SqliteConnection? TryOpen(string path, out string? problem)
    {
        problem = !File.Exists(path) ? "does not exist"
            : new FileInfo(path).Length == 0 ? "is an empty file (a stub, not a database)"
            : null;
        if (problem is not null) return null;
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
        }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM sqlite_master";
            if (Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0)
                return connection;
            problem = "holds no tables (an empty stub)";
        }
        catch (SqliteException e)
        {
            problem = $"cannot be read as a database ({e.Message})";
        }
        connection.Dispose();
        return null;
    }
}
