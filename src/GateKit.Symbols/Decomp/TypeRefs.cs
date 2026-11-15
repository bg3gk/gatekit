using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace GateKit.Symbols.Decomp;

/// <summary>
/// Which function reads which type index (`TypeIndex&lt;T&gt;` of a context: a component, a system, a
/// replicated type ...), per build, under one spelling: <c>type|Context</c>, the type as
/// <see cref="PairCommand.Normalize"/> writes it and the last word of the context. A Windows build
/// has it in the symbol database (`ecs_ids` and `xrefs`). The macOS build names every index in its
/// symbol table (`ls::TypeId&lt;T, Context&gt;::m_TypeIndex`); its pseudo-code mentions an index
/// either by that name or through a pointer slot (`PTR_m_TypeIndex_&lt;address&gt;`), which is resolved
/// with the slot's value in the file. These are the anchors both builds share where no function
/// name is known: the set of types a function touches.
/// </summary>
internal static partial class TypeRefs
{
    /// <summary>`type|Context` of an index by the type and the context as either build spells them.</summary>
    internal static string Key(string type, string context)
    {
        int cut = context.LastIndexOf("::", StringComparison.Ordinal);
        return PairCommand.Normalize(type) + "|" + (cut < 0 ? context : context[(cut + 2)..]).Trim();
    }

    /// <summary>The key of a macOS index symbol by its demangled name
    /// (`ls::TypeId&lt;T, Context&gt;::m_TypeIndex`, with or without spaces), or null.</summary>
    internal static string? KeyOfSymbol(string demangled)
    {
        const string head = "ls::TypeId<", tail = ">::m_TypeIndex";
        if (!demangled.StartsWith(head, StringComparison.Ordinal) || !demangled.EndsWith(tail, StringComparison.Ordinal))
            return null;
        var inner = demangled.AsSpan(head.Length, demangled.Length - head.Length - tail.Length);
        int depth = 0, comma = -1;
        for (int i = 0; i < inner.Length; i++)
        {
            char c = inner[i];
            if (c is '<' or '(')
                depth++;
            else if (c is '>' or ')')
                depth--;
            else if (c == ',' && depth == 0)
                comma = i;
        }
        return comma <= 0 ? null : Key(inner[..comma].ToString(), inner[(comma + 1)..].ToString());
    }

    /// <summary>Per function of a Windows build (by rva): the keys of the indices it refers to.</summary>
    public static Dictionary<long, HashSet<string>> Windows(SqliteConnection symbols, PairSide side)
    {
        var refs = new Dictionary<long, HashSet<string>>();
        if (symbols.Scalar("SELECT id FROM dlls WHERE name = ?", side.Binary) is not { } dll)
            return refs;
        var index = new Dictionary<long, string>();
        foreach (var r in symbols.Query("SELECT index_rva, type, context FROM ecs_ids WHERE dll_id = ? AND context IS NOT NULL AND context <> '' AND index_rva IS NOT NULL", dll))
            index.TryAdd(Convert.ToInt64(r[0], CultureInfo.InvariantCulture), Key(Py.Str(r[1]), Py.Str(r[2])));
        var chunks = new Dictionary<long, long>();
        foreach (var r in symbols.Query("SELECT rva, parent FROM chunks WHERE dll_id = ?", dll))
            chunks.TryAdd(Convert.ToInt64(r[0], CultureInfo.InvariantCulture), Convert.ToInt64(r[1], CultureInfo.InvariantCulture));
        foreach (var r in symbols.Query("SELECT func_rva, target_rva FROM xrefs WHERE dll_id = ?", dll))
        {
            if (!index.TryGetValue(Convert.ToInt64(r[1], CultureInfo.InvariantCulture), out var key))
                continue;
            long rva = Convert.ToInt64(r[0], CultureInfo.InvariantCulture);
            if (!side.ByRva.ContainsKey(rva) && !(chunks.TryGetValue(rva, out rva) && side.ByRva.ContainsKey(rva)))
                continue;
            if (!refs.TryGetValue(rva, out var set))
                refs[rva] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(key);
        }
        return refs;
    }

    [GeneratedRegex(@"PTR_m_TypeIndex_([0-9a-fA-F]{6,})")]
    private static partial Regex Slot();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Space();

    /// <summary>The keys one function's pseudo-code mentions: pointer slots resolved through
    /// <paramref name="slots"/>, and index symbols written out (the decompiler breaks long names
    /// over lines, so white space is dropped first).</summary>
    internal static HashSet<string>? Mentions(string code, IReadOnlyDictionary<long, string> slots)
    {
        if (!code.Contains("m_TypeIndex", StringComparison.Ordinal))
            return null;
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Slot().Matches(code))
            if (long.TryParse(m.Groups[1].Value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out long address) && slots.TryGetValue(address, out var key))
                set.Add(key);
        if (code.Contains("TypeId<", StringComparison.Ordinal))
        {
            string flat = Space().Replace(code, "");
            const string head = "ls::TypeId<", tail = ">::m_TypeIndex";
            for (int at = flat.IndexOf(head, StringComparison.Ordinal); at >= 0; at = flat.IndexOf(head, at + head.Length, StringComparison.Ordinal))
            {
                int depth = 1, i = at + head.Length;
                for (; i < flat.Length && depth > 0 && i - at < 4000; i++)
                {
                    char c = flat[i];
                    if (c == '<') depth++;
                    else if (c == '>') depth--;
                    else if (c is ';' or '=' or '{' or '}') break;
                }
                if (depth == 0 && string.CompareOrdinal(flat, i - 1, tail, 0, tail.Length) == 0 && KeyOfSymbol(flat[at..(i - 1 + tail.Length)]) is string key)
                    set.Add(key);
            }
        }
        return set.Count > 0 ? set : null;
    }

    /// <summary>The type indices the macOS build's symbol table names, by the demangled name of
    /// the index (`ls::TypeId&lt;T, Context&gt;::m_TypeIndex`): per address of the index, and per
    /// pointer slot of `__got` that holds such an address. Needs the build's file in the work
    /// folder and BG3_MACOS_SYMBOLS (symbols.tsv and symbols-demangled.tsv, joined by address);
    /// says what is missing through <paramref name="log"/> and returns null then.</summary>
    public static (Dictionary<long, string> OfAddress, Dictionary<long, string> OfSlot)? IndexNames(DecompSettings settings, string binary, Action<string> log)
    {
        var file = Path.Combine(settings.Work, binary, binary);
        var mangledList = settings.MacosSymbols == null ? null : Path.Combine(settings.MacosSymbols, "symbols.tsv");
        var demangledList = settings.MacosSymbols == null ? null : Path.Combine(settings.MacosSymbols, "symbols-demangled.tsv");
        if (!File.Exists(file) || mangledList == null || !File.Exists(mangledList) || !File.Exists(demangledList))
        {
            log($"  no type references of {binary}: needs {file} and BG3_MACOS_SYMBOLS with symbols.tsv and symbols-demangled.tsv");
            return null;
        }
        // mangled name -> demangled, from the two lists of the other half (the names are the same in both halves)
        var ofMangled = new Dictionary<string, string>(StringComparer.Ordinal);
        // the lists are of the other half and not line for line: they are joined by that half's addresses
        var ofAddressThere = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(demangledList!, Encoding.UTF8))
        {
            if (!line.EndsWith("::m_TypeIndex", StringComparison.Ordinal))
                continue;
            var q = line.Split('\t');
            if (q.Length >= 3 && KeyOfSymbol(q[2]) != null)
                ofAddressThere.TryAdd(q[0], q[2]);
        }
        foreach (var line in File.ReadLines(mangledList, Encoding.UTF8))
        {
            if (!line.EndsWith("m_TypeIndexE", StringComparison.Ordinal))
                continue;
            var p = line.Split('\t');
            if (p.Length >= 3 && !p[2].StartsWith("__ZGV", StringComparison.Ordinal) && ofAddressThere.TryGetValue(p[0], out var name))
                ofMangled.TryAdd(p[2], name);
        }
        var (symbols, slots) = MachO.Data(file);
        var ofAddress = new Dictionary<long, string>();
        foreach (var (address, mangled) in symbols)
            if (ofMangled.TryGetValue(mangled, out var name))
                ofAddress[(long)address] = name;
        var ofSlot = new Dictionary<long, string>();
        foreach (var (slot, target) in slots)
            if (ofAddress.TryGetValue((long)target, out var name))
                ofSlot[(long)slot] = name;
        return (ofAddress, ofSlot);
    }

    /// <summary>Per function of the macOS build (by rva): the keys of the indices its pseudo-code
    /// mentions. Needs the build's file in the work folder and the coverage project's two symbol
    /// lists (BG3_MACOS_SYMBOLS: symbols.tsv and symbols-demangled.tsv, joined by address); says what
    /// is missing through <paramref name="log"/> and returns nothing then.</summary>
    public static Dictionary<long, HashSet<string>> Macos(DecompSettings settings, SqliteConnection db, PairSide side, int jobs, Action<string> log)
    {
        var refs = new Dictionary<long, HashSet<string>>();
        if (IndexNames(settings, side.Binary, log) is not var (nameOfAddress, nameOfSlot))
            return refs;
        var keyOfSlot = new Dictionary<long, string>();
        foreach (var (slot, name) in nameOfSlot)
            if (KeyOfSymbol(name) is string key)
                keyOfSlot[slot] = key;
        log($"  {side.Binary}: {nameOfAddress.Count} type indices named by its symbol table, {keyOfSlot.Count} pointer slots to them");

        var queue = new BlockingCollection<(long Rva, byte[] Blob)>(4096);
        var found = new ConcurrentBag<(long, HashSet<string>)>();
        var workers = Enumerable.Range(0, Math.Max(1, jobs)).Select(_ => Task.Run(() =>
        {
            foreach (var (rva, blob) in queue.GetConsumingEnumerable())
                if (Mentions(DecompStore.Decompress(blob), keyOfSlot) is { } set)
                    found.Add((rva, set));
        })).ToArray();
        try
        {
            foreach (var r in db.Query("SELECT f.rva, c.text FROM functions f JOIN code c ON c.function_id = f.id WHERE f.binary_id = ?", side.BinaryId))
                if (r[1] is byte[] blob)
                    queue.Add((Convert.ToInt64(r[0], CultureInfo.InvariantCulture), blob));
        }
        finally
        {
            queue.CompleteAdding();
        }
        Task.WaitAll(workers);
        foreach (var (rva, set) in found)
            if (side.ByRva.ContainsKey(rva))
                refs[rva] = set;
        return refs;
    }
}

internal static partial class MachO
{
    /// <summary>The defined symbols of a thin Mach-O file (address, mangled name; the first name
    /// per address) and the pointers its `__got` section holds (slot address, the address stored).</summary>
    public static (List<(ulong Address, string Name)> Symbols, List<(ulong Slot, ulong Target)> Slots) Data(string path)
    {
        using var f = File.OpenRead(path);
        byte[] Read(long offset, long count)
        {
            var buffer = new byte[count];
            f.Position = offset;
            f.ReadExactly(buffer);
            return buffer;
        }
        var header = Read(0, 32);
        uint ncmds = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16));
        uint sizeofcmds = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20));
        var cmds = Read(32, sizeofcmds);
        int o = 0;
        (ulong Address, ulong Size, uint Offset)? got = null;
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
                    int so = o + 72 + 80 * k;
                    if (Encoding.ASCII.GetString(cmds, so, 16).TrimEnd('\0') == "__got")
                        got = (BinaryPrimitives.ReadUInt64LittleEndian(cmds.AsSpan(so + 32)), BinaryPrimitives.ReadUInt64LittleEndian(cmds.AsSpan(so + 40)),
                               BinaryPrimitives.ReadUInt32LittleEndian(cmds.AsSpan(so + 48)));
                }
            }
            else if (cmd == 0x2)
                symtab = (BinaryPrimitives.ReadUInt32LittleEndian(cmds.AsSpan(o + 8)), BinaryPrimitives.ReadUInt32LittleEndian(cmds.AsSpan(o + 12)),
                          BinaryPrimitives.ReadUInt32LittleEndian(cmds.AsSpan(o + 16)), BinaryPrimitives.ReadUInt32LittleEndian(cmds.AsSpan(o + 20)));
            o += (int)size;
        }
        var symbols = new List<(ulong, string)>();
        if (symtab is var (symOff, nsyms, strOff, strSize))
        {
            var table = Read(symOff, (long)nsyms * 16);
            var strings = Read(strOff, strSize);
            var seen = new HashSet<ulong>();
            for (int i = 0; i < nsyms; i++)
            {
                var e = table.AsSpan(16 * i, 16);
                uint strx = BinaryPrimitives.ReadUInt32LittleEndian(e);
                byte ntype = e[4];
                ulong value = BinaryPrimitives.ReadUInt64LittleEndian(e[8..]);
                // not a debugging entry, defined in a section
                if ((ntype & 0xe0) != 0 || (ntype & 0x0e) != 0x0e || strx >= strings.Length || !seen.Add(value))
                    continue;
                int end = Array.IndexOf(strings, (byte)0, (int)strx);
                symbols.Add((value, Encoding.Latin1.GetString(strings, (int)strx, (end < 0 ? strings.Length : end) - (int)strx)));
            }
        }
        var slots = new List<(ulong, ulong)>();
        if (got is var (address, length, offset))
        {
            var bytes = Read(offset, (long)length);
            for (int i = 0; i + 8 <= bytes.Length; i += 8)
                slots.Add((address + (ulong)i, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(i))));
        }
        return (symbols, slots);
    }
}
