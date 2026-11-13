using System.Text.RegularExpressions;
using GateKit.Symbols.Decomp;
using Microsoft.Data.Sqlite;

namespace GateKit.Symbols.Queries;

/// <summary>What the symbol queries take from the pseudo-code database (decomp.sqlite), when it
/// exists: names of game functions paired by pseudo-code, system updates and factories, the
/// pseudo-code of a function. Opened read-only on first use; every question answers "nothing"
/// when the file is missing or a table is not there.</summary>
internal sealed partial class DecompLink(string path) : IDisposable
{
    SqliteConnection? db;
    bool tried;
    readonly Dictionary<string, Dictionary<long, string>> updates = new(StringComparer.Ordinal);
    readonly Dictionary<(string, long), string?> matches = [];

    public string Path { get; } = path;

    /// <summary>The database, or null when it does not exist.</summary>
    public SqliteConnection? Db
    {
        get
        {
            if (!tried)
            {
                tried = true;
                if (File.Exists(Path))
                {
                    try
                    {
                        db = Sql.OpenReadOnly(Path);
                    }
                    catch (SqliteException)
                    {
                        db = null;
                    }
                }
            }
            return db;
        }
    }

    [GeneratedRegex(@"^step (\d+) of (\d+)")]
    private static partial Regex StepNote();

    /// <summary>'~ per-frame update of &lt;System&gt; [factory]' (or its update entry) for a function
    /// the pseudo-code shows as a system's update, else null.</summary>
    public string? SystemUpdate(string binary, long rva)
    {
        if (!updates.TryGetValue(binary, out var table))
        {
            table = [];
            try
            {
                if (Db != null)
                    foreach (var r in Db.Query("SELECT u.system, u.thunk, u.updater, u.note FROM system_updates u JOIN binaries b ON b.id = u.binary_id WHERE b.name = ?", binary))
                    {
                        string system = PyText.Str(r[0]);
                        long thunk = PyText.L(r[1]);
                        long? updater = PyText.Long(r[2]);
                        table.TryAdd(thunk, $"~ update entry of {system} [factory]");
                        if (updater != null && updater != thunk)
                        {
                            var step = StepNote().Match(r[3] as string ?? "");
                            table[updater.Value] = $"~ per-frame update of {system}" + (step.Success ? $", step {step.Groups[1].Value}/{step.Groups[2].Value}" : "") + " [factory]";
                        }
                        else if (updater == thunk)
                            table[thunk] = $"~ per-frame update of {system} (with its entry) [factory]";
                    }
            }
            catch (SqliteException)
            {
            }
            updates[binary] = table;
        }
        return table.GetValueOrDefault(rva);
    }

    /// <summary>'~ Name [pseudo-code 0.81]' for a game function paired with a named Toolkit function
    /// by pseudo-code, else null.</summary>
    public string? CodeMatch(string binary, long rva)
    {
        if (Db == null)
            return null;
        if (matches.TryGetValue((binary, rva), out var known))
            return known;
        string? found = null;
        try
        {
            var row = Db.One(
                "SELECT f.name, m.score FROM code_matches m JOIN binaries g ON g.id = m.game_binary"
                + " JOIN functions f ON f.binary_id = m.toolkit_binary AND f.rva = m.toolkit_rva"
                + " WHERE g.name = ? AND m.game_rva = ? AND m.score >= 0.15 AND m.margin >= 0.10 AND f.name NOT LIKE 'FUN!_%' ESCAPE '!'"
                + " AND f.name NOT LIKE 'thunk!_FUN!_%' ESCAPE '!' ORDER BY m.score DESC LIMIT 1", binary, rva);
            if (row != null)
                found = $"~ {PyText.Str(row[0])} [pseudo-code {Convert.ToDouble(row[1], System.Globalization.CultureInfo.InvariantCulture).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}]";
        }
        catch (SqliteException)
        {
        }
        matches[(binary, rva)] = found;
        return found;
    }

    /// <summary>Pseudo-code of the function that starts at the address, as Ghidra wrote it; "" when there is none.</summary>
    public string PseudoCode(string binary, long rva)
    {
        if (Db == null)
            return "";
        try
        {
            var blob = Db.Scalar("SELECT c.text FROM functions f JOIN binaries b ON b.id = f.binary_id JOIN code c ON c.function_id = f.id"
                                 + " WHERE b.name = ? AND f.rva = ?", binary, rva);
            return DecompStore.Decompress(blob) ?? "";
        }
        catch (SqliteException)
        {
            return "";
        }
    }

    /// <summary>[(factory, constructor, vtable, note)] where a system is created.</summary>
    public List<(long Factory, long? Constructor, long? Vtable, string? Note)> CreatedIn(string binary, string system)
    {
        if (Db == null)
            return [];
        try
        {
            return Db.Query("SELECT s.factory, s.constructor, s.vtable, s.note FROM system_factories s JOIN binaries b ON b.id = s.binary_id"
                            + " WHERE b.name = ? AND s.system = ?", binary, system)
                .Select(r => (PyText.L(r[0]), PyText.Long(r[1]), PyText.Long(r[2]), r[3] as string)).ToList();
        }
        catch (SqliteException)
        {
            return [];
        }
    }

    /// <summary>[(thunk, updater, note)]: a system's per-frame updates.</summary>
    public List<(long Thunk, long? Updater, object? Note)> Updates(string binary, string system)
    {
        if (Db == null)
            return [];
        try
        {
            return Db.Query("SELECT u.thunk, u.updater, u.note FROM system_updates u JOIN binaries b ON b.id = u.binary_id"
                            + " WHERE b.name = ? AND u.system = ?", binary, system)
                .Select(r => (PyText.L(r[0]), PyText.Long(r[1]), r[2])).ToList();
        }
        catch (SqliteException)
        {
            return [];
        }
    }

    public void Dispose() => db?.Dispose();
}
