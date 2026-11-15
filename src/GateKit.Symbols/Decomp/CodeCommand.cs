using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace GateKit.Symbols.Decomp;

/// <summary>One decompiled function as the database has it.</summary>
internal sealed record FunctionRow(long Id, string Binary, long Rva, string? Name, string? Signature, string? Error);

/// <summary>`code`: the pseudo-code of a function by name, binary+0xRVA, an absolute address,
/// an address inside a function, a jump stub, or an address of the macOS arm64 half.</summary>
internal static class CodeCommand
{
    public static readonly Regex Address = new(@"^(.+?)\+(?:0x)?([0-9a-fA-F]+)$", RegexOptions.Compiled);

    const string Select = "SELECT f.id, b.name, f.rva, f.name, f.signature, f.error FROM functions f JOIN binaries b ON b.id = f.binary_id";

    static List<FunctionRow> Rows(SqliteConnection db, string sql, params object?[] args) =>
        db.Query(sql, args).Select(r => new FunctionRow(Convert.ToInt64(r[0], CultureInfo.InvariantCulture), Py.Str(r[1]),
            Convert.ToInt64(r[2], CultureInfo.InvariantCulture), r[3] as string, r[4] as string, r[5] as string)).ToList();

    /// <summary>The functions a query names; notes on how an address was resolved go to `note`.</summary>
    /// <param name="onlyBinary">For a name: only functions of this binary (N13 of the third coverage run:
    /// only bg3_macos_x64+0x... reached the macOS half by name).</param>
    public static List<FunctionRow> Resolve(DecompSession s, string query, int limit, Action<string> note, string? onlyBinary = null)
    {
        var db = s.Db;
        var m = Address.Match(query);
        if (!m.Success)
        {
            const string In = " AND (? IS NULL OR b.name LIKE ?)";
            var rows = Rows(db, Select + " WHERE f.name = ?" + In + " ORDER BY b.name LIMIT ?", query, onlyBinary, (onlyBinary ?? "") + "%", limit);
            if (rows.Count == 0)
                rows = Rows(db, Select + " WHERE f.name LIKE ?" + In + " ORDER BY length(f.name), b.name LIMIT ?", "%" + query + "%", onlyBinary, (onlyBinary ?? "") + "%", limit);
            return rows;
        }
        string binary = m.Groups[1].Value;
        long rva = Py.Hex(m.Groups[2].Value) ?? -1;
        bool macos = binary.ToLowerInvariant().StartsWith("bg3_macos", StringComparison.Ordinal);
        if (macos && rva >= 0)
        {
            // The decompiled macOS binary is the x86-64 half of the universal file. Addresses taken
            // from the symbol tables of the arm64 half (the coverage project's tables) are different
            // numbers for the same functions: translated through the names.
            long absolute = rva >= 0x100000000 ? rva : rva + 0x100000000;
            rva = absolute - 0x100000000;
            if (db.One("SELECT 1 FROM functions f JOIN binaries b ON b.id = f.binary_id WHERE b.name LIKE ? AND f.rva = ?", binary + "%", rva) == null)
            {
                var other = MacosAddresses.Arm64ToX64(s, absolute);
                if (other != null)
                {
                    note($"// 0x{absolute:X} is an address of the arm64 half; the same function in the decompiled x86-64 half is +0x{other:X}");
                    rva = other.Value;
                }
            }
        }
        foreach (long imageBase in new long[] { 0x180000000, 0x140000000 })      // an absolute address, as in FUN_14189ae00
        {
            if (rva >= imageBase && !macos)
            {
                rva -= imageBase;
                break;
            }
        }
        var found = Rows(db, Select + " WHERE b.name LIKE ? AND f.rva = ?", binary + "%", rva);
        if (found.Count == 0)
        {
            // a jump stub (5 bytes: jmp target): the function it leads to is what is wanted
            var target = FollowJump(s, binary, rva);
            if (target != null)
            {
                note($"// +0x{rva:X} is a jump to +0x{target:X}");
                rva = target.Value;
                found = Rows(db, Select + " WHERE b.name LIKE ? AND f.rva = ?", binary + "%", rva);
            }
        }
        if (found.Count == 0)
        {
            var inside = Containing(s, binary, rva);
            if (inside != null)
            {
                note($"// {inside.Value.Binary}+0x{rva:X} is not a function start: {inside.Value.How}");
                found = Rows(db, Select + " WHERE b.name = ? AND f.rva = ?", inside.Value.Binary, inside.Value.Start);
            }
        }
        return found;
    }

    /// <summary>The binary name, the start of the decompiled function that contains the address,
    /// and how it was found; or null. The symbol database knows which part of a split function
    /// belongs to which function; without it the nearest function start before the address is taken.</summary>
    public static (string Binary, long Start, string How)? Containing(DecompSession s, string binary, long rva)
    {
        var db = s.Db;
        var row = db.One("SELECT id, name FROM binaries WHERE name LIKE ? ORDER BY length(name) LIMIT 1", binary + "%");
        if (row == null)
            return null;
        long binaryId = Convert.ToInt64(row[0], CultureInfo.InvariantCulture);
        string name = Py.Str(row[1]);
        var sym = s.Symbols;
        if (sym != null)
        {
            try
            {
                var dll = sym.Scalar("SELECT id FROM dlls WHERE name = ?", name);
                if (dll != null && sym.One("SELECT 1 FROM sqlite_master WHERE name = 'chunks'") != null)
                {
                    var part = sym.One("SELECT rva, size FROM functions WHERE dll_id = ? AND rva <= ? ORDER BY rva DESC LIMIT 1", dll, rva);
                    if (part != null && part[0] != null && part[1] != null
                        && rva < Convert.ToInt64(part[0], CultureInfo.InvariantCulture) + Convert.ToInt64(part[1], CultureInfo.InvariantCulture))
                    {
                        long partStart = Convert.ToInt64(part[0], CultureInfo.InvariantCulture);
                        var parent = sym.Scalar("SELECT parent FROM chunks WHERE dll_id = ? AND rva = ?", dll, partStart);
                        long start = parent != null ? Convert.ToInt64(parent, CultureInfo.InvariantCulture) : partStart;
                        if (db.One("SELECT 1 FROM functions WHERE binary_id = ? AND rva = ?", binaryId, start) != null)
                            return (name, start, "the address is inside this function");
                    }
                }
            }
            catch (SqliteException)
            {
                // an older symbol database without these tables: fall back to the nearest start
            }
        }
        var near = db.Scalar("SELECT rva FROM functions WHERE binary_id = ? AND rva <= ? ORDER BY rva DESC LIMIT 1", binaryId, rva);
        if (near != null && rva - Convert.ToInt64(near, CultureInfo.InvariantCulture) < 0x20000)
            return (name, Convert.ToInt64(near, CultureInfo.InvariantCulture), "nearest function start before the address; not proven to contain it");
        return null;
    }

    /// <summary>Where a chain of `jmp rel32` stubs starting at the address ends, or null when the
    /// address is not such a stub. Read from the file of a game executable or Toolkit DLL.</summary>
    public static long? FollowJump(DecompSession s, string binary, long rva)
    {
        var name = s.Db.Scalar("SELECT name FROM binaries WHERE name LIKE ? ORDER BY length(name) LIMIT 1", binary + "%") as string;
        if (name == null || !(name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
            return null;
        var pe = s.Image(name);
        if (pe == null)
            return null;
        long here = rva;
        int hops = 0;
        while (hops < 4)
        {
            int o = pe.Offset(here);
            if (o < 0 || o + 5 > pe.Data.Length || pe.Data[o] != 0xE9)
                break;
            here = (here + 5 + System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(pe.Data.AsSpan(o + 1, 4))) & 0xFFFFFFFF;
            hops++;
        }
        return hops > 0 ? here : null;
    }

    /// <summary>The pseudo-code text of a function, or null when Ghidra did not decompile it.</summary>
    public static string? Text(DecompSession s, long functionId) =>
        DecompStore.Decompress(s.Db.Scalar("SELECT text FROM code WHERE function_id = ?", functionId));

    /// <summary>The registry a command reads (GATEKIT_REGISTRY, else the one beside the coverage record), or null.</summary>
    internal static Registry.RegistryStore? Store()
    {
        try { return Registry.RegistryStore.Find(null) is { Exists: true } store ? store : null; }
        catch (SymbolsException) { return null; }
    }

    /// <summary>The live layer's newest claims; none when the home has no live layer or it cannot be read.</summary>
    internal static IReadOnlyList<Registry.Claim> LiveClaims()
    {
        try { return FunctionNames.HomeLive()?.Latest() ?? []; }
        catch (Exception e) when (e is IOException or GateKit.Json.JsonProblemException or UnauthorizedAccessException or Registry.RegistryException) { return []; }
    }

    /// <summary>The functions an id names - its entry's or another source's name in the registry, or a
    /// live claim - through the table of the build each binary's pseudo-code is stamped with; all of
    /// them, one a binary. Empty when the registry knows no such name.</summary>
    internal static List<FunctionRow> ResolveId(DecompSession s, string query, Registry.RegistryStore? store, IReadOnlyList<Registry.Claim> claims, string? onlyBinary)
    {
        var places = new List<(string Binary, long Rva)>();
        if (!Address.IsMatch(query))
        {
            var entries = store?.Lookup(query) ?? [];
            foreach (var entry in entries)
                foreach (var (build, bin, row) in store!.Places(entry.Id))
                    if (FunctionNames.BuildAt(s, bin) is not { } stamped || stamped == build)
                        places.Add((bin, row.Rva));
            var ids = entries.Select(e => e.Id).Append(query).ToHashSet(StringComparer.Ordinal);
            foreach (var claim in claims)
                if (ids.Contains(claim.Id) && claim.Binary is not null && claim.Rva is { } rva
                    && (claim.Build is null || FunctionNames.BuildAt(s, claim.Binary) is not { } b || b == claim.Build))
                    places.Add((claim.Binary, rva));
        }
        var rows = new List<FunctionRow>();
        foreach (var (bin, rva) in places.Distinct())
        {
            if (onlyBinary is not null && !bin.StartsWith(onlyBinary, StringComparison.OrdinalIgnoreCase)) continue;
            rows.AddRange(Rows(s.Db, Select + " WHERE b.name = ? COLLATE NOCASE AND f.rva = ?", bin, rva).Where(r => !rows.Any(x => x.Id == r.Id)));
        }
        return rows;
    }

    /// <summary>The decompiler's own real names of a function and the functions it calls: rva -> name.</summary>
    static Dictionary<long, string> TextNames(DecompSession s, FunctionRow f)
    {
        var names = new Dictionary<long, string>();
        if (f.Name is { Length: > 0 } own) names[f.Rva] = own;
        try
        {
            foreach (var r in s.Db.Query("SELECT c.callee, g.name FROM calls c JOIN binaries b ON b.id = c.binary_id JOIN functions g ON g.binary_id = c.binary_id AND g.rva = c.callee"
                                         + " WHERE b.name = ? AND c.caller = ?", f.Binary, f.Rva))
                if (r[1] is string name && name.Length > 0) names[Convert.ToInt64(r[0], CultureInfo.InvariantCulture)] = name;
        }
        catch (SqliteException)
        {
            // a database without the call graph: callees are found by their machine names only
        }
        return names;
    }

    /// <summary>What `code` says when a query names more than one function: when they lie one in
    /// each binary, that `--binary` picks one; otherwise to pick one by its address.</summary>
    internal static string Ambiguity(IReadOnlyList<FunctionRow> rows)
    {
        var binaries = rows.Select(r => r.Binary).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return binaries.Count == rows.Count
            ? $"{rows.Count} functions match, one in each binary: add --binary {string.Join(" or --binary ", binaries)} to pick one, or --all to print them all"
            : $"{rows.Count} functions match; pick one by its binary+0xRVA, or add --all to print them all";
    }

    /// <summary>`code`: our reading of each function the query names (the header, the text by the rule
    /// of certainty, the legend), the decompiler's text untouched with <paramref name="raw"/>, or both
    /// as JSON with <paramref name="json"/>.</summary>
    public static int Run(DecompSession s, string name, bool all, bool raw, bool json, string? grep, int context, int limit, string? binary = null)
    {
        var store = Store();
        var claims = LiveClaims();
        var rows = ResolveId(s, name, store, claims, binary);
        if (rows.Count == 0)
            rows = Resolve(s, name, limit, s.Err.WriteLine, binary);
        if (rows.Count == 0)
            throw new SymbolsException("no such function");
        if (rows.Count > 1 && !all)
        {
            foreach (var f in rows)
            {
                var size = s.Db.Scalar("SELECT size FROM functions WHERE id = ?", f.Id);
                long bytes = size == null ? 0 : Convert.ToInt64(size, CultureInfo.InvariantCulture);
                s.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{f.Binary}+0x{f.Rva:X}  {bytes,6} bytes  {Py.Str(f.Name)}")
                                + (f.Signature != null && f.Signature.Length > 0 && Py.CodePoints(f.Signature) < 160 ? $"   {f.Signature}" : ""));
            }
            s.Err.WriteLine(Ambiguity(rows));
            return 1;
        }
        var knowledge = new Dictionary<string, ReadingKnowledge>(StringComparer.OrdinalIgnoreCase);
        var answers = new System.Text.Json.Nodes.JsonArray();
        foreach (var f in rows)
        {
            var text = Text(s, f.Id);
            if (!knowledge.TryGetValue(f.Binary, out var k))
                knowledge[f.Binary] = k = ReadingKnowledge.Of(store, claims, f.Binary, FunctionNames.BuildAt(s, f.Binary), FunctionNames.HomeLive());
            // the decompiler kept open answers with what is applied of the registry typed in; else the cached text
            if (DecompServer.Ready(s.Settings, f.Binary))
            {
                var entry = k.At(f.Rva) is { Count: > 0 } ids ? k.Entry(ids[0].Id) : null;
                try
                {
                    if (DecompServer.Ask(s.Settings, f.Binary, DecompServer.Request(f.Rva, entry, k.Entry), TimeSpan.FromSeconds(15)) is { } served)
                    {
                        text = served.Text;
                        s.Err.WriteLine($"// decompiled again by `decomp serve` in {served.RoundTripMs} ms (types {served.ApplyMs} ms, decompiler {served.DecompileMs} ms)");
                    }
                    else s.Err.WriteLine("// `decomp serve` did not answer within 15 s: the cached text is shown");
                }
                catch (SymbolsException e) { s.Err.WriteLine($"// {e.Message}: the cached text is shown"); }
            }
            if (raw)
            {
                if (rows.Count > 1) s.Out.WriteLine($"// {f.Binary}+0x{f.Rva:X}");
                s.Out.WriteLine(text is null ? $"// not decompiled: {Py.Str(f.Error)}"
                    : !string.IsNullOrEmpty(grep) ? Annotations.Excerpt(text, grep, context) : text);
                continue;
            }
            if (text is null)
            {
                if (json) answers.Add(new System.Text.Json.Nodes.JsonObject { ["binary"] = f.Binary, ["rva"] = Registry.RegistryStore.Hex(f.Rva), ["error"] = Py.Str(f.Error) });
                else s.Out.WriteLine($"// {f.Binary}+0x{f.Rva:X}  {Py.Str(f.Name)}\n// not decompiled: {Py.Str(f.Error)}");
                continue;
            }
            var reading = OurReading.Make(text, f.Rva, k, FunctionNames.SignatureAt(s, f.Binary, f.Rva), TextNames(s, f));
            if (json)
            {
                var one = OurReading.ToJson(f.Binary, f.Rva, k.Build, text, reading);
                if (Standings.Word(k.Standing(f.Rva).Standing) is { } standing) one["standing"] = standing;
                answers.Add(one);
                continue;
            }
            s.Out.WriteLine($"// {f.Binary}+0x{f.Rva:X}  {Py.Str(f.Name)}" + (k.Build is null ? "" : $"  (build {k.Build})"));
            foreach (var line in reading.Header) s.Out.WriteLine(line);
            var shown = Annotations.All(s, reading.Text, f.Name, f.Binary);
            s.Out.WriteLine(!string.IsNullOrEmpty(grep) ? Annotations.Excerpt(shown, grep, context) : shown);
            if (reading.Legend.Count > 0)
            {
                s.Out.WriteLine("// the structures it uses:");
                foreach (var line in reading.Legend) s.Out.WriteLine(line);
            }
        }
        if (json) s.Out.Write(GateKit.Json.JsonText.Format(new System.Text.Json.Nodes.JsonObject { ["functions"] = answers }));
        return 0;
    }
}
