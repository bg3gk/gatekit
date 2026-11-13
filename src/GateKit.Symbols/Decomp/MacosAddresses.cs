using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace GateKit.Symbols.Decomp;

/// <summary>The functions of a thin x86-64 Mach-O file, read from its symbol table.</summary>
internal static class MachO
{
    /// <summary>Functions as (address, size, mangled name), sorted by address: every defined
    /// symbol in the __text section, the shortest name per address, each running to the next.</summary>
    public static List<(ulong Start, ulong Size, string Name)> Functions(string path)
    {
        using var f = File.OpenRead(path);
        byte[] Read(long offset, int count)
        {
            var buffer = new byte[count];
            f.Position = offset;
            f.ReadExactly(buffer);
            return buffer;
        }
        var header = Read(0, 32);
        uint ncmds = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16));
        uint sizeofcmds = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20));
        var cmds = Read(32, (int)sizeofcmds);
        int o = 0, sectIndex = 0;
        (int Index, ulong Address, ulong Length)? text = null;
        (uint SymOff, uint NSyms, uint StrOff, uint StrSize)? symtab = null;
        for (uint i = 0; i < ncmds; i++)
        {
            uint cmd = BinaryPrimitives.ReadUInt32LittleEndian(cmds.AsSpan(o));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(cmds.AsSpan(o + 4));
            if (cmd == 0x19)
            {
                uint nsects = BinaryPrimitives.ReadUInt32LittleEndian(cmds.AsSpan(o + 64));
                for (int k = 0; k < nsects; k++)
                {
                    sectIndex++;
                    int so = o + 72 + 80 * k;
                    if (Encoding.ASCII.GetString(cmds, so, 16).TrimEnd('\0') == "__text")
                        text = (sectIndex, BinaryPrimitives.ReadUInt64LittleEndian(cmds.AsSpan(so + 32)), BinaryPrimitives.ReadUInt64LittleEndian(cmds.AsSpan(so + 40)));
                }
            }
            else if (cmd == 0x2)
                symtab = (BinaryPrimitives.ReadUInt32LittleEndian(cmds.AsSpan(o + 8)), BinaryPrimitives.ReadUInt32LittleEndian(cmds.AsSpan(o + 12)),
                          BinaryPrimitives.ReadUInt32LittleEndian(cmds.AsSpan(o + 16)), BinaryPrimitives.ReadUInt32LittleEndian(cmds.AsSpan(o + 20)));
            o += (int)size;
        }
        if (text == null || symtab == null)
            throw new InvalidDataException($"{path}: no __text section or no symbol table");
        var (symOff, nsyms, strOff, strSize) = symtab.Value;
        var symbols = Read(symOff, checked((int)(nsyms * 16)));
        var strings = Read(strOff, (int)strSize);
        var names = new Dictionary<ulong, string>();
        for (int i = 0; i < nsyms; i++)
        {
            var e = symbols.AsSpan(16 * i, 16);
            uint strx = BinaryPrimitives.ReadUInt32LittleEndian(e);
            byte ntype = e[4], nsect = e[5];
            ulong value = BinaryPrimitives.ReadUInt64LittleEndian(e[8..]);
            if ((ntype & 0xe0) != 0 || (ntype & 0x0e) != 0x0e || nsect != text.Value.Index || strx >= strings.Length)
                continue;
            int end = Array.IndexOf(strings, (byte)0, (int)strx);
            string name = Encoding.Latin1.GetString(strings, (int)strx, (end < 0 ? strings.Length : end) - (int)strx);
            if (!names.TryGetValue(value, out var known) || name.Length < known.Length)
                names[value] = name;
        }
        var ordered = names.Keys.Order().ToList();
        ulong limit = text.Value.Address + text.Value.Length;
        var functions = new List<(ulong, ulong, string)>(ordered.Count);
        for (int i = 0; i < ordered.Count; i++)
            functions.Add((ordered[i], (i + 1 < ordered.Count ? ordered[i + 1] : limit) - ordered[i], names[ordered[i]]));
        return functions;
    }
}

/// <summary>Addresses of the macOS universal file: the arm64 half's to the x86-64 half's.</summary>
internal static class MacosAddresses
{
    static readonly Lock Gate = new();
    static readonly Dictionary<string, Dictionary<long, long>> Tables = [];

    /// <summary>RVA in the x86-64 half of the macOS executable of the function that the arm64
    /// half has at this absolute address, or null. The two halves carry the same symbol names; the
    /// table is built once from the coverage project's symbol list of the arm64 half
    /// (BG3_MACOS_SYMBOLS/symbols.tsv) and the symbol table of the x86-64 half
    /// (&lt;work&gt;/bg3_macos_x64/bg3_macos_x64), and kept in the work folder.</summary>
    public static long? Arm64ToX64(DecompSession s, long address)
    {
        Dictionary<long, long> table;
        lock (Gate)
        {
            if (!Tables.TryGetValue(s.Settings.Work, out table!))
            {
                table = Build(s.Settings);
                Tables[s.Settings.Work] = table;
            }
        }
        return table.TryGetValue(address, out var rva) ? rva : null;
    }

    const uint Magic = 0x314D4B47;                       // "GKM1"

    static Dictionary<long, long> Build(DecompSettings settings)
    {
        var cache = Path.Combine(settings.Work, "macos-arm64-to-x64.bin");
        var table = new Dictionary<long, long>();
        if (File.Exists(cache))
        {
            using var r = new BinaryReader(File.OpenRead(cache));
            if (r.ReadUInt32() == Magic)
            {
                int n = r.ReadInt32();
                for (int i = 0; i < n; i++)
                    table[r.ReadInt64()] = r.ReadInt64();
            }
            return table;
        }
        var listing = settings.MacosSymbols == null ? null : Path.Combine(settings.MacosSymbols, "symbols.tsv");
        var half = Path.Combine(settings.Work, "bg3_macos_x64", "bg3_macos_x64");
        if (listing == null || !File.Exists(listing) || !File.Exists(half))
            return table;
        var byName = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (start, _, name) in MachO.Functions(half))
            byName.TryAdd(name, (long)start - 0x100000000);
        foreach (var line in File.ReadLines(listing, Encoding.UTF8))
        {
            var parts = line.Split('\t');
            // parts[1] in "tT", as the Python tool tested it: "", "t", "T" and "tT"
            if (parts.Length >= 3 && "tT".Contains(parts[1], StringComparison.Ordinal) && byName.TryGetValue(parts[2], out var rva)
                && ulong.TryParse(parts[0].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? parts[0][2..] : parts[0],
                                  NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var key))
                table[(long)key] = rva;
        }
        using (var w = new BinaryWriter(File.Create(cache)))
        {
            w.Write(Magic);
            w.Write(table.Count);
            foreach (var (key, value) in table)
            {
                w.Write(key);
                w.Write(value);
            }
        }
        return table;
    }
}
