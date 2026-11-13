using System.IO.Compression;
using System.Text;
using Microsoft.Data.Sqlite;

namespace GateKit.Symbols.Decomp;

/// <summary>Where the pseudo-code pipeline reads and writes. <see cref="FromEnvironment"/> takes
/// every location from <see cref="SymbolPaths"/>; tests pass their own folders.</summary>
/// <param name="Work">Work folder: Ghidra projects, functions.jsonl per binary, decomp.sqlite.</param>
/// <param name="Tools">External tools: ghidra_*, jdk-*, ilspycmd.</param>
/// <param name="Game">The installed game.</param>
/// <param name="Toolkit">The installed Toolkit.</param>
/// <param name="SymbolsDb">The symbol database (read only here).</param>
/// <param name="MacosSymbols">Folder with the coverage project's symbols.tsv of the macOS arm64 half, or null.</param>
public sealed record DecompSettings(string Work, string Tools, string Game, string Toolkit, string SymbolsDb, string? MacosSymbols)
{
    public static DecompSettings FromEnvironment() =>
        new(SymbolPaths.DecompWork, SymbolPaths.DecompTools, SymbolPaths.Game, SymbolPaths.Toolkit, SymbolPaths.SymbolsDb, SymbolPaths.MacosSymbols);

    /// <summary>decomp.sqlite in the work folder.</summary>
    public string DbPath => Path.Combine(Work, "decomp.sqlite");
}

/// <summary>The pseudo-code database: schema (the same as the Python tool's, so either can read
/// the other's file) and the zlib-compressed text blobs.</summary>
internal static class DecompStore
{
    public const string Schema = """
        CREATE TABLE IF NOT EXISTS binaries (id INTEGER PRIMARY KEY, name TEXT UNIQUE, loaded TEXT, functions INTEGER, failed INTEGER);
        CREATE TABLE IF NOT EXISTS functions (
            id INTEGER PRIMARY KEY, binary_id INTEGER, rva INTEGER, name TEXT, signature TEXT, size INTEGER,
            thunk INTEGER, lines INTEGER, error TEXT
        );
        CREATE TABLE IF NOT EXISTS code (function_id INTEGER PRIMARY KEY, text BLOB);       -- zlib
        CREATE TABLE IF NOT EXISTS calls (binary_id INTEGER, caller INTEGER, callee INTEGER);  -- rva -> rva
        CREATE VIRTUAL TABLE IF NOT EXISTS code_fts USING fts5(name, code, content='', contentless_delete=1, tokenize="unicode61 tokenchars '_'");
        -- C# sources of the Toolkit's managed assemblies (the editor), decompiled with ILSpy.
        CREATE TABLE IF NOT EXISTS managed_files (id INTEGER PRIMARY KEY, assembly TEXT, path TEXT, lines INTEGER);
        CREATE TABLE IF NOT EXISTS managed_code (file_id INTEGER PRIMARY KEY, text BLOB);    -- zlib
        CREATE VIRTUAL TABLE IF NOT EXISTS managed_fts USING fts5(path, code, content='', contentless_delete=1, tokenize="unicode61 tokenchars '_'");
        CREATE INDEX IF NOT EXISTS managed_assembly ON managed_files(assembly);
        -- Where each ECS system is created, read from the pseudo-code (gatekit decomp systems --rebuild).
        CREATE TABLE IF NOT EXISTS system_factories (
            binary_id INTEGER, system TEXT, factory INTEGER, constructor INTEGER, vtable INTEGER, note TEXT
        );
        CREATE INDEX IF NOT EXISTS system_factories_name ON system_factories(system);
        -- The per-frame entry of a system: the function pointer its factory stores (the engine's
        -- SystemRegistrationHelper::SystemUpdate<System>) and the update function that one calls.
        CREATE TABLE IF NOT EXISTS system_updates (binary_id INTEGER, system TEXT, thunk INTEGER, updater INTEGER, note TEXT);
        CREATE INDEX IF NOT EXISTS system_updates_name ON system_updates(system);
        -- Toolkit function <-> game function, paired by their pseudo-code (gatekit decomp match).
        -- score: weight of shared features over weight of all; margin: lead over the runner-up.
        CREATE TABLE IF NOT EXISTS code_matches (
            toolkit_binary INTEGER, toolkit_rva INTEGER, game_binary INTEGER, game_rva INTEGER, score REAL, margin REAL
        );
        CREATE INDEX IF NOT EXISTS code_matches_game ON code_matches(game_binary, game_rva);
        -- Global FixedStrings of the game (gatekit decomp fixedstrings --rebuild). The engine keeps one
        -- registry entry per string literal; kind 'entry' is that entry (the text pointer), 'value' the
        -- FixedString it is resolved into at start-up (entry + 0x18), 'pointer' a global that holds the
        -- address of the value.
        CREATE TABLE IF NOT EXISTS fixed_strings (binary_id INTEGER, rva INTEGER, kind TEXT, text TEXT, registered_in INTEGER);
        CREATE INDEX IF NOT EXISTS fixed_strings_rva ON fixed_strings(binary_id, rva);
        CREATE INDEX IF NOT EXISTS fixed_strings_text ON fixed_strings(text);
        CREATE INDEX IF NOT EXISTS code_matches_toolkit ON code_matches(toolkit_binary, toolkit_rva);
        CREATE INDEX IF NOT EXISTS functions_rva ON functions(binary_id, rva);
        CREATE INDEX IF NOT EXISTS functions_name ON functions(name);
        CREATE INDEX IF NOT EXISTS calls_caller ON calls(binary_id, caller);
        CREATE INDEX IF NOT EXISTS calls_callee ON calls(binary_id, callee);
        """;

    /// <summary>Opens the database for writing, creating it and its tables when needed.</summary>
    public static SqliteConnection OpenWrite(string path)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (folder != null)
            Directory.CreateDirectory(folder);
        var db = Sql.OpenWrite(path);
        db.Execute("PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF;" + Schema);
        return db;
    }

    /// <summary>Opens an existing database read-only; a missing file is an expected failure.</summary>
    public static SqliteConnection OpenRead(string path)
    {
        if (!File.Exists(path))
            throw new SymbolsException($"{path} does not exist; run:  gatekit decomp run <binary>  then  gatekit decomp load");
        return Sql.OpenReadOnly(path);
    }

    /// <summary>zlib.compress(text.encode("utf-8"), 6).</summary>
    public static byte[] Compress(string text)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, new ZLibCompressionOptions { CompressionLevel = 6 }, leaveOpen: true))
            z.Write(Encoding.UTF8.GetBytes(text));
        return ms.ToArray();
    }

    /// <summary>zlib.decompress(blob).decode("utf-8").</summary>
    public static string Decompress(byte[] blob)
    {
        using var z = new ZLibStream(new MemoryStream(blob), CompressionMode.Decompress);
        using var ms = new MemoryStream(blob.Length * 4);
        z.CopyTo(ms);
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    public static string? Decompress(object? blob) => blob is byte[] b ? Decompress(b) : null;

    /// <summary>Where Ghidra loads the binary: the game executables at 0x140000000, the DLLs (and
    /// everything else) at 0x180000000.</summary>
    public static long ImageBase(string binary) =>
        binary.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? 0x140000000 : 0x180000000;
}
