using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace GateKit.Symbols.Decomp;

/// <summary>
/// The virtual tables of a build as the pairing uses them: per table the functions in its slots,
/// in slot order. A Windows build's are in the symbol database (`vtabs`, `vtab_slots`). The macOS
/// build names its own: every symbol `vtable for X` of its symbol table, whose slots are the
/// pointers into the code that follow the two header words. Two tables of the same class hold the
/// same virtual functions in the same order (destructors and overloads apart), so a matched table
/// lines the functions of its slots up exactly as a paired caller lines up its calls.
/// </summary>
internal static class VirtualTables
{
    /// <summary>Reads the tables of a side: from the symbol database when the binary is there, else from its Mach-O file.</summary>
    public static void Add(DecompSession s, PairSide side, Action<string> log)
    {
        if (s.Symbols is { } symbols && symbols.Scalar("SELECT id FROM dlls WHERE name = ?", side.Binary) is { } dll)
        {
            try
            {
                var slots = new Dictionary<long, List<long>>();
                foreach (var r in symbols.Query("SELECT v.rva, s.rva FROM vtab_slots s JOIN vtabs v ON v.id = s.vtab_id WHERE v.dll_id = ? ORDER BY v.rva, s.idx", dll))
                {
                    long table = Convert.ToInt64(r[0], CultureInfo.InvariantCulture);
                    if (!slots.TryGetValue(table, out var list))
                        slots[table] = list = [];
                    if (r[1] != null && side.ByRva.ContainsKey(Convert.ToInt64(r[1], CultureInfo.InvariantCulture)))
                        list.Add(Convert.ToInt64(r[1], CultureInfo.InvariantCulture));
                }
                foreach (var (table, list) in slots)
                    if (list.Count >= 2)
                        side.Tables[table] = [.. list];
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                // a symbol database without virtual tables
            }
            return;
        }
        var file = Path.Combine(s.Settings.Work, side.Binary, side.Binary);
        if (!File.Exists(file))
            return;
        var names = MacNames(s.Settings, "vtable for ");
        if (names.Count == 0)
        {
            log($"  no virtual tables of {side.Binary}: BG3_MACOS_SYMBOLS with symbols.tsv and symbols-demangled.tsv names them");
            return;
        }
        foreach (var (address, slots, mangled) in MachO.Tables(file))
        {
            if (!names.TryGetValue(mangled, out var name))
                continue;
            long rva = (long)address - 0x100000000;
            var functions = slots.Select(v => (long)v - 0x100000000).Where(side.ByRva.ContainsKey).ToArray();
            if (functions.Length < 2)
                continue;
            side.Tables[rva] = functions;
            side.TableNames[rva] = name["vtable for ".Length..];
        }
    }

    /// <summary>Mangled name -> demangled name of the macOS symbols whose demangled name starts with
    /// <paramref name="prefix"/>, from the coverage project's two lists (joined by their addresses).</summary>
    public static Dictionary<string, string> MacNames(DecompSettings settings, string prefix)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var mangledList = settings.MacosSymbols == null ? null : Path.Combine(settings.MacosSymbols, "symbols.tsv");
        var demangledList = settings.MacosSymbols == null ? null : Path.Combine(settings.MacosSymbols, "symbols-demangled.tsv");
        if (mangledList == null || !File.Exists(mangledList) || !File.Exists(demangledList))
            return names;
        var byAddress = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(demangledList!, Encoding.UTF8))
        {
            var q = line.Split('\t');
            if (q.Length >= 3 && q[2].StartsWith(prefix, StringComparison.Ordinal))
                byAddress.TryAdd(q[0], q[2]);
        }
        foreach (var line in File.ReadLines(mangledList, Encoding.UTF8))
        {
            var p = line.Split('\t');
            if (p.Length >= 3 && byAddress.TryGetValue(p[0], out var name))
                names.TryAdd(p[2], name);
        }
        return names;
    }

    /// <summary>How the matched tables agree with the tables the Toolkit names (symbols.sqlite
    /// `vtab_matches`): same class, another class, and a few of the others (ours &lt;- the Toolkit's).</summary>
    public static (int Same, int Other, List<string> Sample)? Check(DecompSession s, PairSide target, List<TablePair> tables)
    {
        if (s.Symbols is not { } symbols || symbols.Scalar("SELECT id FROM dlls WHERE name = ?", target.Binary) is not { } dll)
            return null;
        var known = new Dictionary<long, string>();
        try
        {
            foreach (var r in symbols.Query("SELECT gv.rva, min(s.full_name) FROM vtab_matches m JOIN vtabs gv ON gv.id = m.game_vtab JOIN vtabs tv ON tv.id = m.toolkit_vtab"
                                            + " JOIN symbols s ON s.id = tv.symbol_id WHERE gv.dll_id = ? GROUP BY gv.rva HAVING count(DISTINCT s.full_name) = 1", dll))
                known[Convert.ToInt64(r[0], CultureInfo.InvariantCulture)] = Py.Str(r[1]);
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            return null;
        }
        int same = 0, other = 0;
        var sample = new List<string>();
        foreach (var t in tables)
        {
            if (t.Name.Length == 0 || !known.TryGetValue(t.Target, out var toolkit))
                continue;
            int cut = toolkit.LastIndexOf("::", StringComparison.Ordinal);
            string theirs = PairCommand.Normalize(cut < 0 ? toolkit : toolkit[..cut]);
            if (theirs == PairCommand.Normalize(t.Name))
                same++;
            else
            {
                other++;
                sample.Add($"{t.Name} <- {(cut < 0 ? toolkit : toolkit[..cut])} ({t.Support} slots)");
            }
        }
        return (same, other, sample);
    }
}

internal static partial class MachO
{
    /// <summary>The virtual tables of a thin x86-64 Mach-O file: every defined symbol whose mangled
    /// name starts with `__ZTV` (address, the pointers into `__text` that follow its two header
    /// words up to the next symbol, the name).</summary>
    public static List<(ulong Address, List<ulong> Slots, string Name)> Tables(string path)
    {
        var (symbols, _) = Data(path);
        using var f = File.OpenRead(path);
        var header = new byte[32];
        f.ReadExactly(header);
        uint ncmds = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16));
        var cmds = new byte[BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20))];
        f.ReadExactly(cmds);
        var segments = new List<(ulong Address, ulong FileOffset, ulong FileSize)>();
        (ulong Start, ulong End) text = (0, 0);
        int o = 0;
        for (uint i = 0; i < ncmds; i++)
        {
            uint cmd = BinaryPrimitives.ReadUInt32LittleEndian(cmds.AsSpan(o));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(cmds.AsSpan(o + 4));
            if (cmd == 0x19)
            {
                segments.Add((BinaryPrimitives.ReadUInt64LittleEndian(cmds.AsSpan(o + 24)), BinaryPrimitives.ReadUInt64LittleEndian(cmds.AsSpan(o + 40)),
                              BinaryPrimitives.ReadUInt64LittleEndian(cmds.AsSpan(o + 48))));
                uint nsects = BinaryPrimitives.ReadUInt32LittleEndian(cmds.AsSpan(o + 64));
                for (int k = 0; k < nsects; k++)
                {
                    int so = o + 72 + 80 * k;
                    if (Encoding.ASCII.GetString(cmds, so, 16).TrimEnd('\0') == "__text")
                    {
                        ulong start = BinaryPrimitives.ReadUInt64LittleEndian(cmds.AsSpan(so + 32));
                        text = (start, start + BinaryPrimitives.ReadUInt64LittleEndian(cmds.AsSpan(so + 40)));
                    }
                }
            }
            o += (int)size;
        }
        var ordered = symbols.Select(x => x.Address).Order().ToList();
        var tables = new List<(ulong, List<ulong>, string)>();
        foreach (var (address, name) in symbols)
        {
            if (!name.StartsWith("__ZTV", StringComparison.Ordinal))
                continue;
            int at = ordered.BinarySearch(address);
            ulong end = at >= 0 && at + 1 < ordered.Count ? ordered[at + 1] : address + 16;
            long length = (long)Math.Min(end - address, 0x10000);
            var segment = segments.FirstOrDefault(g => address >= g.Address && address + (ulong)length <= g.Address + g.FileSize);
            if (segment.FileSize == 0 || length < 24)
                continue;
            var bytes = new byte[length];
            f.Position = (long)(segment.FileOffset + address - segment.Address);
            f.ReadExactly(bytes);
            var slots = new List<ulong>();
            for (int i = 16; i + 8 <= bytes.Length; i += 8)
            {
                ulong value = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(i));
                if (value >= text.Start && value < text.End)
                    slots.Add(value);
            }
            tables.Add((address, slots, name));
        }
        return tables;
    }
}
