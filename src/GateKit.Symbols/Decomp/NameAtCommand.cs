using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace GateKit.Symbols.Decomp;

/// <summary>What is known about one address: the function it falls in and that function's name,
/// taken from the decompiler database (Ghidra's or one a refresh gave it) and the symbol database
/// (an export, a paired Toolkit name, or a Script Extender label). Pure observation: it reads the
/// databases and prints, changing nothing. The address is `module+0xRVA` as any debugger, the
/// extender's watchpoint, or another external tool reports it.</summary>
internal static class NameAtCommand
{
    /// <summary>One address resolved to a function and its names.</summary>
    internal sealed record Named(string Binary, long Rva, long Start, string? DecompName, string? Signature,
        string? SymbolName, IReadOnlyList<string> Notes);

    /// <summary>Several addresses in one call (a profiler's or a stack's list): each resolved and printed
    /// in turn, as one would be; with --json one array of them. Returns 1 when any address resolved to
    /// nothing, else 0.</summary>
    public static int RunMany(DecompSession s, IReadOnlyList<string> queries, bool json)
    {
        if (queries.Count == 1) return Run(s, queries[0], json);
        if (!json)
        {
            int code = 0;
            foreach (var query in queries) code |= Run(s, query, false);
            return code;
        }
        var all = new JsonArray();
        int status = 0;
        foreach (var query in queries)
        {
            var text = new StringWriter();
            if (Run(s, query, true, text) != 0) { status = 1; all.Add(new JsonObject { ["query"] = query, ["found"] = false }); continue; }
            all.Add(GateKit.Json.JsonText.Parse(text.ToString(), query).Root);
        }
        s.Out.WriteLine(GateKit.Json.JsonText.Format(all));
        return status;
    }

    /// <summary>Resolve `query` (module+0xRVA, an absolute address, or a function name) and print.
    /// Returns 0 when a function was named, 1 when nothing could be resolved.</summary>
    public static int Run(DecompSession s, string query, bool json, TextWriter? output = null)
    {
        var w = output ?? s.Out;
        var named = Resolve(s, query);
        if (named == null)
        {
            s.Err.WriteLine($"no function found at {query}");
            return 1;
        }

        var live = FunctionNames.HomeLive();
        var there = FunctionNames.WithLive(FunctionNames.ReadOrNone(RecordTypes.FindCoverage(null)), live)
            .Where(r => r.Rva == named.Start && string.Equals(r.Binary, named.Binary, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => Registry.Rungs.Rank(r.Source)).ThenBy(r => r.Live).ToList();
        var given = there.FirstOrDefault();
        var (standing, keep) = FunctionNames.StandingOf(there, live);
        if (json)
        {
            var o = new JsonObject
            {
                ["binary"] = named.Binary,
                ["rva"] = named.Rva,
                ["start"] = named.Start,
                ["decomp_name"] = named.DecompName,
                ["signature"] = named.Signature,
                ["symbol_name"] = named.SymbolName,
                ["given_name"] = given?.Name,
                ["given_live"] = given?.Live,
                ["given_rung"] = given?.Source,
                ["given_others"] = new JsonArray([.. there.Skip(1).Select(r => (JsonNode)new JsonObject { ["name"] = r.Name, ["rung"] = r.Source, ["live"] = r.Live })]),
                ["standing"] = Standings.Word(standing),
                ["notes"] = new JsonArray(named.Notes.Select(n => (JsonNode)n).ToArray()),
            };
            w.WriteLine(GateKit.Json.JsonText.Format(o));
            return 0;
        }

        w.WriteLine($"{named.Binary}+0x{named.Rva:X}");
        string at = named.Start == named.Rva ? "" : string.Create(CultureInfo.InvariantCulture, $" (function at +0x{named.Start:X})");
        w.WriteLine($"  decompiler: {named.DecompName ?? "(unnamed)"}{at}");
        if (named.Signature != null)
            w.WriteLine($"  signature:  {named.Signature}");
        w.WriteLine($"  symbols:    {named.SymbolName ?? "(none)"}");
        if (given is not null)
        {
            w.WriteLine($"  named:      {given.Name} [{given.Source}]{(given.Live ? " (live)" : "")}");
            if (given.Does is { Length: > 0 }) w.WriteLine($"  does:       {given.Does}");
            if (given.Evidence.Length > 0) w.WriteLine($"  evidence:   {given.Evidence}");
            if (given.Guess is not null) w.WriteLine($"  guess:      {given.Guess}");
            // the lower rungs as well, each with its own: a guess beside an id is a claim to see
            foreach (var other in there.Skip(1))
            {
                w.WriteLine($"  also:       {other.Name} [{other.Source}]{(other.Live ? " (live)" : "")}");
                if (other.Does is { Length: > 0 }) w.WriteLine($"    does:     {other.Does}");
                if (other.Guess is not null) w.WriteLine($"    guess:    {other.Guess}");
            }
            if (Standings.Line(standing, keep, there.Select(r => r.Name).Distinct(StringComparer.Ordinal).Count(), named.Binary, named.Start) is { } line)
                w.WriteLine($"  {(standing == IdStanding.Clash ? "clash:" : "together:"),-11} {line}");
        }
        foreach (var note in named.Notes)
            w.WriteLine($"  note: {note.TrimStart('/', ' ')}");
        return 0;
    }

    /// <summary>The names known for an address, or null when no function was resolved. The decompiler
    /// database locates the function (handling a jump stub or an address inside a function); the
    /// symbol database adds the export / paired / label name at the function start.</summary>
    public static Named? Resolve(DecompSession s, string query)
    {
        var notes = new List<string>();
        var rows = s.DbIfExists != null ? CodeCommand.Resolve(s, query, 1, notes.Add) : [];
        string binary;
        long rva, start;
        string? decompName = null, signature = null;

        if (rows.Count > 0)
        {
            var f = rows[0];
            binary = f.Binary;
            start = f.Rva;
            decompName = f.Name;
            signature = f.Signature;
            // The queried address, normalised the same way CodeCommand does, for display.
            rva = ParseRva(query) ?? start;
        }
        else
        {
            // No decompiled function (or no decomp database): still answer from the symbol database.
            var m = CodeCommand.Address.Match(query);
            if (!m.Success)
                return null;
            binary = m.Groups[1].Value;
            rva = ParseRva(query) ?? -1;
            if (rva < 0)
                return null;
            start = rva;
            notes.Add("no decompiled function at this address; the symbol database was asked for the nearest function");
        }

        // the decompiler's own start of the function is what a given name is keyed by: the symbol
        // database may move `start` to the symbol it found, and a row at the function's start was then missed
        long functionStart = start;
        string? symbolName = SymbolName(s.Symbols, binary, ref start, rva, notes, rows.Count == 0);
        if (rows.Count > 0) start = functionStart;

        if (decompName == null && symbolName == null && rows.Count == 0)
            return null;

        return new Named(binary, rva, start, decompName, signature, symbolName, notes);
    }

    /// <summary>The queried RVA with an absolute image base removed, matching CodeCommand's own rule.</summary>
    static long? ParseRva(string query)
    {
        var m = CodeCommand.Address.Match(query);
        if (!m.Success)
            return null;
        long rva = Py.Hex(m.Groups[2].Value) ?? -1;
        if (rva < 0)
            return null;
        foreach (long imageBase in new long[] { 0x180000000, 0x140000000 })
            if (rva >= imageBase)
            {
                rva -= imageBase;
                break;
            }
        return rva;
    }

    /// <summary>The best name the symbol database holds for the function at (binary, start): an
    /// exported signature, a paired Toolkit full name, or a Script Extender label. When the decompiler
    /// did not locate the function (findStart), the nearest function start at or before rva is used
    /// and `start` is set to it. Null when the symbol database is absent or knows no name.</summary>
    static string? SymbolName(SqliteConnection? symbols, string binary, ref long start, long rva, List<string> notes, bool findStart)
    {
        if (symbols == null)
            return null;
        try
        {
            var dllId = symbols.Scalar("SELECT id FROM dlls WHERE name LIKE ? ESCAPE '\\' ORDER BY length(name) LIMIT 1", binary + "%");
            if (dllId == null)
                return null;
            long gid = Convert.ToInt64(dllId, CultureInfo.InvariantCulture);

            if (findStart)
            {
                var near = symbols.Scalar("SELECT rva, size FROM functions WHERE dll_id = ? AND rva <= ? ORDER BY rva DESC LIMIT 1", gid, rva);
                var row = symbols.One("SELECT rva, size FROM functions WHERE dll_id = ? AND rva <= ? ORDER BY rva DESC LIMIT 1", gid, rva);
                if (row != null && row[0] != null)
                {
                    long near0 = Convert.ToInt64(row[0], CultureInfo.InvariantCulture);
                    long size = row[1] == null ? 0 : Convert.ToInt64(row[1], CultureInfo.InvariantCulture);
                    if (rva < near0 + size || rva - near0 < 0x4000)
                        start = near0;
                }
            }

            // An export or the demangled signature registered at the function start.
            var exported = symbols.Scalar("SELECT signature FROM symbols WHERE dll_id = ? AND rva = ? ORDER BY length(signature) LIMIT 1", gid, start) as string;
            if (exported != null)
                return exported;

            // A Toolkit function paired to this game function by the matcher (not purely inlined).
            var matched = symbols.Scalar(
                "SELECT s.full_name FROM matches m JOIN symbols s ON s.dll_id = m.toolkit_dll AND s.rva = m.toolkit_rva"
                + " WHERE m.game_dll = ? AND m.game_rva = ? AND (m.relation IS NULL OR m.relation <> 'inlined' OR m.method = 'vtable')"
                + " ORDER BY length(s.full_name) LIMIT 1", gid, start) as string;
            if (matched != null)
                return matched;

            // A Script Extender symbol label (its own name for the function).
            var label = symbols.Scalar("SELECT name FROM se_symbols WHERE dll_id = ? AND rva = ? ORDER BY length(name) LIMIT 1", gid, start) as string;
            return label;
        }
        catch (SqliteException)
        {
            return null;
        }
    }
}
