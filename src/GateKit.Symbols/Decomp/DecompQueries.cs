using System.Globalization;
using Microsoft.Data.Sqlite;

namespace GateKit.Symbols.Decomp;

/// <summary>Pseudo-code of one function.</summary>
/// <param name="Binary">The binary as loaded (bg3.exe, EoCClient.dll ...).</param>
/// <param name="Rva">Start of the function.</param>
/// <param name="Name">Ghidra's (or the export table's) name.</param>
/// <param name="Signature">Ghidra's prototype.</param>
/// <param name="Text">The pseudo-code, annotated unless asked plain; null when Ghidra failed.</param>
/// <param name="Error">Ghidra's message when it failed.</param>
/// <param name="Notes">How the address was resolved (jump stub, inside a function, arm64 half).</param>
public sealed record FunctionCode(string Binary, long Rva, string? Name, string? Signature, string? Text, string? Error, IReadOnlyList<string> Notes);

/// <summary>Where an ECS system is created.</summary>
public sealed record SystemFactory(string Binary, string System, long Factory, long? Constructor, long? Vtable, string? Note);

/// <summary>A system's per-frame update: the entry its factory stores and the function it calls.</summary>
public sealed record SystemUpdate(string Binary, string System, long Thunk, long? Updater, string? Note);

/// <summary>A global FixedString: its address, kind (entry, value, pointer), text and registering function.</summary>
public sealed record FixedStringGlobal(string Binary, long Rva, string Kind, string Text, long RegisteredIn);

/// <summary>Read-only questions to decomp.sqlite for other tools (the MCP server): no output is
/// printed, nothing is written. Open once, ask many times, dispose.</summary>
public sealed class DecompQueries : IDisposable
{
    readonly DecompSession session;

    /// <summary>Over the work folder of the environment (BG3_DECOMP_WORK and the other settings).</summary>
    public DecompQueries() : this(DecompSettings.FromEnvironment()) { }

    public DecompQueries(DecompSettings settings) => session = new DecompSession(settings, TextWriter.Null, TextWriter.Null);

    /// <summary>True when decomp.sqlite exists.</summary>
    public bool Available => File.Exists(session.Settings.DbPath);

    /// <summary>Pseudo-code of the function at (or containing, or reached by a jump stub from) binary+rva;
    /// null when the database does not exist or no function is found.</summary>
    public FunctionCode? Code(string binary, long rva, bool plain = false)
    {
        if (!Available)
            return null;
        var notes = new List<string>();
        var rows = CodeCommand.Resolve(session, string.Create(CultureInfo.InvariantCulture, $"{binary}+0x{rva:X}"), 1, notes.Add);
        if (rows.Count == 0)
            return null;
        var f = rows[0];
        var text = CodeCommand.Text(session, f.Id);
        if (text != null && !plain)
            text = Annotations.All(session, text, f.Name, f.Binary);
        return new FunctionCode(f.Binary, f.Rva, f.Name, f.Signature, text, f.Error, notes);
    }

    /// <summary>Pseudo-code of functions by exact name (else by part of the name).</summary>
    public IReadOnlyList<FunctionCode> CodeByName(string name, int limit = 40, bool plain = false)
    {
        if (!Available)
            return [];
        var list = new List<FunctionCode>();
        foreach (var f in CodeCommand.Resolve(session, name, limit, _ => { }))
        {
            var text = CodeCommand.Text(session, f.Id);
            if (text != null && !plain)
                text = Annotations.All(session, text, f.Name, f.Binary);
            list.Add(new FunctionCode(f.Binary, f.Rva, f.Name, f.Signature, text, f.Error, []));
        }
        return list;
    }

    /// <summary>'~ Name [pseudo-code 0.81]' for a game function paired with a named Toolkit
    /// function by pseudo-code (default thresholds), or null.</summary>
    public string? CodeMatchName(string binary, long rva) => Available ? Annotations.CodeMatchName(session, binary, rva) : null;

    static long L(object? v) => Convert.ToInt64(v, CultureInfo.InvariantCulture);

    /// <summary>Where systems whose name contains the text are created.</summary>
    public IReadOnlyList<SystemFactory> SystemFactories(string system)
    {
        if (!Available)
            return [];
        try
        {
            return session.Db.Query("SELECT b.name, s.system, s.factory, s.constructor, s.vtable, s.note FROM system_factories s JOIN binaries b ON b.id = s.binary_id"
                                    + " WHERE s.system LIKE ? ORDER BY s.system", "%" + system + "%")
                .Select(r => new SystemFactory(Py.Str(r[0]), Py.Str(r[1]), L(r[2]), r[3] == null ? null : L(r[3]), r[4] == null ? null : L(r[4]), r[5] as string))
                .ToList();
        }
        catch (SqliteException)
        {
            return [];
        }
    }

    /// <summary>The per-frame updates of one system (exact name).</summary>
    public IReadOnlyList<SystemUpdate> SystemUpdates(string system)
    {
        if (!Available)
            return [];
        try
        {
            return session.Db.Query("SELECT b.name, u.system, u.thunk, u.updater, u.note FROM system_updates u JOIN binaries b ON b.id = u.binary_id WHERE u.system = ?", system)
                .Select(r => new SystemUpdate(Py.Str(r[0]), Py.Str(r[1]), L(r[2]), r[3] == null ? null : L(r[3]), r[4] as string))
                .ToList();
        }
        catch (SqliteException)
        {
            return [];
        }
    }

    /// <summary>The systems whose per-frame update (or update entry) is the function at binary+rva.</summary>
    public IReadOnlyList<SystemUpdate> UpdatesAt(string binary, long rva)
    {
        if (!Available)
            return [];
        try
        {
            return session.Db.Query("SELECT b.name, u.system, u.thunk, u.updater, u.note FROM system_updates u JOIN binaries b ON b.id = u.binary_id"
                                    + " WHERE b.name = ? AND (u.updater = ? OR u.thunk = ?)", binary, rva, rva)
                .Select(r => new SystemUpdate(Py.Str(r[0]), Py.Str(r[1]), L(r[2]), r[3] == null ? null : L(r[3]), r[4] as string))
                .ToList();
        }
        catch (SqliteException)
        {
            return [];
        }
    }

    /// <summary>The global FixedString at binary+rva (an absolute address is accepted), or null.</summary>
    public FixedStringGlobal? FixedStringAt(string binary, long rva)
    {
        if (!Available)
            return null;
        foreach (long imageBase in new long[] { 0x180000000, 0x140000000 })
            if (rva >= imageBase)
            {
                rva -= imageBase;
                break;
            }
        try
        {
            var r = session.Db.One("SELECT b.name, s.rva, s.kind, s.text, s.registered_in FROM fixed_strings s JOIN binaries b ON b.id = s.binary_id"
                                   + " WHERE b.name LIKE ? AND s.rva = ?", binary + "%", rva);
            return r == null ? null : new FixedStringGlobal(Py.Str(r[0]), L(r[1]), Py.Str(r[2]), Py.Str(r[3]), L(r[4]));
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    public void Dispose() => session.Dispose();
}
