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
    public static List<FunctionRow> Resolve(DecompSession s, string query, int limit, Action<string> note)
    {
        var db = s.Db;
        var m = Address.Match(query);
        if (!m.Success)
        {
            var rows = Rows(db, Select + " WHERE f.name = ? ORDER BY b.name LIMIT ?", query, limit);
            if (rows.Count == 0)
                rows = Rows(db, Select + " WHERE f.name LIKE ? ORDER BY length(f.name), b.name LIMIT ?", "%" + query + "%", limit);
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

    public static int Run(DecompSession s, string name, bool all, bool plain, string? grep, int context, int limit)
    {
        var rows = Resolve(s, name, limit, s.Err.WriteLine);
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
            s.Err.WriteLine($"{rows.Count} functions match; pick one by its binary+0xRVA, or add --all to print them all");
            return 0;
        }
        foreach (var f in rows)
        {
            s.Out.WriteLine($"// {f.Binary}+0x{f.Rva:X}  {Py.Str(f.Name)}");
            var text = Text(s, f.Id);
            if (text == null)
            {
                s.Out.WriteLine($"// not decompiled: {Py.Str(f.Error)}");
                continue;
            }
            if (!plain)
                text = Annotations.All(s, text, f.Name, f.Binary);
            s.Out.WriteLine(!string.IsNullOrEmpty(grep) ? Annotations.Excerpt(text, grep, context) : text);
        }
        return 0;
    }
}
