using GateKit.Symbols.Pe;
using Microsoft.Data.Sqlite;

namespace GateKit.Symbols.Decomp;

/// <summary>One command's view of the pipeline: settings, output streams, and the databases and
/// binary images it opens on first use (each at most once).</summary>
internal sealed class DecompSession(DecompSettings settings, TextWriter output, TextWriter error) : IDisposable
{
    public DecompSettings Settings { get; } = settings;
    public TextWriter Out { get; } = output;
    public TextWriter Err { get; } = error;

    SqliteConnection? db, symbols;
    bool ownsDb, symbolsTried;
    readonly Dictionary<string, PeImage?> images = new(StringComparer.Ordinal);

    /// <summary>Fixed string tables by binary name, for <see cref="Annotations.NameFixedStrings"/>.</summary>
    public Dictionary<string, Dictionary<long, (string Kind, string Text)>> FixedStringTables { get; } = new(StringComparer.Ordinal);

    /// <summary>decomp.sqlite, read-only; a missing file is an expected failure.</summary>
    public SqliteConnection Db
    {
        get
        {
            if (db == null)
            {
                db = DecompStore.OpenRead(Settings.DbPath);
                ownsDb = true;
            }
            return db;
        }
    }

    /// <summary>decomp.sqlite read-only when it exists, else null.</summary>
    public SqliteConnection? DbIfExists => db ?? (File.Exists(Settings.DbPath) ? Db : null);

    /// <summary>Opens decomp.sqlite for writing; later reads of this session go through it.</summary>
    public SqliteConnection OpenWrite()
    {
        if (db != null && ownsDb)
            db.Dispose();
        db = DecompStore.OpenWrite(Settings.DbPath);
        ownsDb = true;
        return db;
    }

    /// <summary>The symbol database read-only, or null when it does not exist.</summary>
    public SqliteConnection? Symbols
    {
        get
        {
            if (!symbolsTried)
            {
                symbolsTried = true;
                if (File.Exists(Settings.SymbolsDb))
                    symbols = Sql.OpenReadOnly(Settings.SymbolsDb);
            }
            return symbols;
        }
    }

    /// <summary>The file of a game executable or Toolkit binary: the game's bin folder, the
    /// Toolkit folder, or a path; null when none exists.</summary>
    public string? Locate(string name)
    {
        foreach (var candidate in new[] { Path.Combine(Settings.Game, "bin", name), Path.Combine(Settings.Toolkit, name), name })
            if (File.Exists(candidate))
                return candidate;
        return null;
    }

    /// <summary>The PE image of a binary by name (loaded once), or null when the file is missing
    /// or is not a PE file.</summary>
    public PeImage? Image(string binary)
    {
        if (images.TryGetValue(binary, out var image))
            return image;
        image = null;
        var path = Locate(binary);
        if (path != null)
        {
            try
            {
                image = PeImage.Open(path);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                image = null;
            }
        }
        images[binary] = image;
        return image;
    }

    /// <summary>The zero-terminated printable ASCII text (at most 255 characters) at an address of
    /// a binary's image, or null.</summary>
    public string? ReadText(string binary, long rva)
    {
        var pe = Image(binary);
        if (pe == null)
            return null;
        int o = pe.Offset(rva);
        if (o < 0 || o >= pe.Data.Length)
            return null;
        var d = pe.Data;
        int limit = Math.Min(d.Length, o + 256);
        int end = Array.IndexOf(d, (byte)0, o, limit - o);
        if (end <= o)
            return null;
        for (int i = o; i < end; i++)
            if (d[i] < 32 || d[i] >= 127)
                return null;
        return System.Text.Encoding.ASCII.GetString(d, o, end - o);
    }

    /// <summary>The 8 bytes stored in the file at an address of the image; null when the address
    /// has no bytes in the file. Throws <see cref="FileNotFoundException"/> when the binary is missing.</summary>
    public ulong? ReadValue(string binary, long rva)
    {
        var pe = Image(binary) ?? throw new FileNotFoundException(binary);
        int o = pe.Offset(rva);
        if (o < 0 || o + 8 > pe.Data.Length)
            return null;
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(pe.Data.AsSpan(o, 8));
    }

    public void Dispose()
    {
        if (ownsDb)
            db?.Dispose();
        symbols?.Dispose();
    }
}
