using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace GateKit.Symbols.Queries;

/// <summary>Where the symbol queries find the files besides the symbol database: the pseudo-code
/// database, the reference files of the macOS build, and the game and Toolkit folders (to read a
/// binary itself). <see cref="FromEnvironment"/> takes them from <see cref="SymbolPaths"/>.</summary>
/// <param name="DecompDb">decomp.sqlite (optional: annotations and pseudo-code appear when it exists).</param>
/// <param name="MacosSymbols">Folder with fieldmeta.json and savefields.json (BG3_MACOS_SYMBOLS), or null.</param>
/// <param name="Game">The installed game.</param>
/// <param name="Toolkit">The installed Toolkit.</param>
public sealed record SymbolsEnvironment(string DecompDb, string? MacosSymbols, string Game, string Toolkit)
{
    public static SymbolsEnvironment FromEnvironment() =>
        new(SymbolPaths.DecompDb, SymbolPaths.MacosSymbols, SymbolPaths.Game, SymbolPaths.Toolkit);
}

/// <summary>One query's view: the symbol database (read-only), the output streams and the
/// pseudo-code database. The helpers fold function parts and name addresses as symbols.py did.</summary>
internal sealed partial class SymbolContext : IDisposable
{
    public SqliteConnection Db { get; }
    public TextWriter Out { get; }
    public TextWriter Err { get; }
    public SymbolsEnvironment Env { get; }
    public DecompLink Decomp { get; }
    readonly bool ownsDb;

    public SymbolContext(SqliteConnection db, TextWriter output, TextWriter error, SymbolsEnvironment env, bool ownsDb = false)
    {
        Db = db;
        Out = output;
        Err = error;
        Env = env;
        Decomp = new DecompLink(env.DecompDb);
        this.ownsDb = ownsDb;
    }

    /// <summary>Opens the symbol database read-only. A missing file, or one built before the table of
    /// function parts existed, is an expected failure.</summary>
    public static SqliteConnection Connect(string path)
    {
        if (!File.Exists(path))
            throw new SymbolsException($"{path} does not exist; run:  gatekit symbols build");
        var db = Sql.OpenReadOnly(path);
        if (db.Scalar("SELECT 1 FROM sqlite_master WHERE name = 'chunks'") == null)
        {
            db.Dispose();
            throw new SymbolsException($"{path} was built before function parts were recorded (no table 'chunks'); build it again:  gatekit symbols build");
        }
        db.Execute("PRAGMA case_sensitive_like=OFF");
        return db;
    }

    /// <summary>binary+0xRVA (or binary+RVA).</summary>
    [GeneratedRegex(@"^(.+?)\+(?:0x)?([0-9a-fA-F]+)$")]
    public static partial Regex Address();

    /// <summary>The address part of a binary+0xRVA match.</summary>
    public static long Rva(Match m) => GateKit.Symbols.Decomp.Py.Hex(m.Groups[2].Value) ?? throw new SymbolsException($"address too large: {m.Groups[2].Value}");

    /// <summary>Start of the function an address of a function part belongs to (the address itself otherwise).</summary>
    public long Whole(long dllId, long rva) => PyText.Long(Db.Scalar("SELECT parent FROM chunks WHERE dll_id = ? AND rva = ?", dllId, rva)) ?? rva;

    /// <summary>The function start and the starts of its later parts.</summary>
    public List<long> Parts(long dllId, long rva)
    {
        var list = new List<long> { rva };
        list.AddRange(Db.Query("SELECT rva FROM chunks WHERE dll_id = ? AND parent = ?", dllId, rva).Select(r => PyText.L(r[0])));
        return list;
    }

    /// <summary>Size of a function with all its parts, or null when it is not a known function.</summary>
    public long? WholeSize(long dllId, long rva)
    {
        var own = Parts(dllId, rva);
        var args = new object?[own.Count + 1];
        args[0] = dllId;
        for (int i = 0; i < own.Count; i++)
            args[i + 1] = own[i];
        return PyText.Long(Db.Scalar($"SELECT sum(size) FROM functions WHERE dll_id = ? AND rva IN ({string.Join(",", own.Select(_ => "?"))})", args));
    }

    readonly Dictionary<long, string> dllNames = [];

    public string DllName(long dllId)
    {
        if (!dllNames.TryGetValue(dllId, out var name))
            dllNames[dllId] = name = PyText.Str(Db.Scalar("SELECT name FROM dlls WHERE id = ?", dllId));
        return name;
    }

    /// <summary>binary+0xRVA and what is known about the function there: its exported name, the
    /// Toolkit function it was matched to, or what the pseudo-code says about it.</summary>
    public string Label(long dllId, long rva)
    {
        string dll = DllName(dllId);
        var name = Db.Scalar("SELECT full_name FROM symbols WHERE dll_id = ? AND rva = ? AND kind IN ('method','function')"
                             + " ORDER BY length(full_name) LIMIT 1", dllId, rva) as string;
        name ??= Db.Scalar("SELECT '= ' || s.full_name FROM matches m JOIN symbols s ON s.dll_id = m.toolkit_dll"
                           + " AND s.rva = m.toolkit_rva WHERE m.game_dll = ? AND m.game_rva = ? LIMIT 1", dllId, rva) as string;
        name ??= Decomp.SystemUpdate(dll, rva) ?? Decomp.CodeMatch(dll, rva);
        return $"{dll}+0x{PyText.Hex(rva)}" + (name != null ? "  " + name : "");
    }

    /// <summary>Functions a search text stands for: 'binary+0xRVA', else exported functions whose
    /// name contains the text.</summary>
    public List<(long Dll, long Rva)> Targets(string text, int limit = 50)
    {
        var m = Address().Match(text);
        if (m.Success)
        {
            var id = Db.Scalar("SELECT id FROM dlls WHERE name LIKE ? ESCAPE '\\' ORDER BY length(name) LIMIT 1", Sql.Like(m.Groups[1].Value));
            return id == null ? [] : [(PyText.L(id), Rva(m))];
        }
        return Db.Query("SELECT DISTINCT dll_id, rva FROM symbols WHERE full_name LIKE ? ESCAPE '\\' AND rva IS NOT NULL"
                        + " AND kind IN ('method','function') LIMIT ?", Sql.Like(text), limit)
            .Select(r => (PyText.L(r[0]), PyText.L(r[1]))).ToList();
    }

    /// <summary>(id, name) of the game executable whose name contains the text (shortest first), or null.</summary>
    public (long Id, string Name)? Game(string? dll)
    {
        var r = Db.One("SELECT id, name FROM dlls WHERE origin = 'game' AND name LIKE ? ESCAPE '\\' ORDER BY length(name) LIMIT 1", Sql.Like(dll ?? "bg3.exe"));
        return r == null ? null : (PyText.L(r[0]), PyText.Str(r[1]));
    }

    public void WriteLine(string text) => Out.WriteLine(text);

    public void Note(string text) => Err.WriteLine(text);

    public void Dispose()
    {
        Decomp.Dispose();
        if (ownsDb)
            Db.Dispose();
    }
}
