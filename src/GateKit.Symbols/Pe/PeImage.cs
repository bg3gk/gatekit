using System.Buffers.Binary;
using System.Text;

namespace GateKit.Symbols.Pe;

/// <summary>One section of a PE image. Size is the larger of the virtual and the raw size.</summary>
public readonly record struct PeSection(uint Va, uint Size, uint Raw, uint RawSize, uint Flags)
{
    public bool Executable => (Flags & 0x20000000) != 0;
    public bool Readable => (Flags & 0x40000000) != 0;
    public bool Writable => (Flags & 0x80000000) != 0;
    /// <summary>Read-only data: readable, neither executable nor writable.</summary>
    public bool ReadOnlyData => Readable && !Executable && !Writable;
}

/// <summary>An export: ordinal, address (null for a forwarder), name (null when exported by ordinal only).</summary>
public readonly record struct PeExport(uint Ordinal, uint? Rva, string? Name, string? Forwarder);

/// <summary>An import: the DLL it comes from, its name, or its ordinal.</summary>
public readonly record struct PeImport(string FromDll, string? Name, uint? Ordinal);

/// <summary>An entry of the function list: [Begin, End); Leaf when found between listed functions.</summary>
public readonly record struct FunctionRange(uint Begin, uint End, bool Leaf);

/// <summary>
/// A callee of a direct call: a function start of the same image (<see cref="Rva"/>), or an import
/// (<see cref="Dll"/> in lower case and <see cref="Name"/>, "#ordinal" for an import by ordinal).
/// </summary>
public readonly record struct Callee(uint Rva, string? Dll, string? Name)
{
    public bool IsImport => Dll != null;
    public static Callee Function(uint rva) => new(rva, null, null);
    public static Callee Import(string dll, string name) => new(0, dll, name);
}

/// <summary>
/// Just enough of the PE format for the symbol database: exports, imports, function starts, text
/// constants, direct calls and virtual tables. The whole file is kept in memory.
/// </summary>
public sealed class PeImage
{
    public byte[] Data { get; }
    public string Machine { get; }
    public uint Timestamp { get; }
    public bool Pe64 { get; }
    public ulong ImageBase { get; }
    public IReadOnlyList<(uint Rva, uint Size)> Directories { get; }
    public IReadOnlyList<PeSection> Sections { get; }
    /// <summary>A .NET assembly (it has a CLR header).</summary>
    public bool Managed { get; }

    List<FunctionRange>? _ranges;
    uint[]? _starts;
    uint[]? _ends;

    /// <summary>Addresses in read-only data that code loads with lea reg, [rip+x] (filled by <see cref="StringRefs"/>).</summary>
    public HashSet<uint> LeaTargets { get; } = [];
    /// <summary>Address in read-only data -> starts of the functions that load it (filled by <see cref="StringRefs"/>).</summary>
    public Dictionary<uint, HashSet<uint>> LeaRefs { get; } = [];

    public static PeImage Open(string path) => new(File.ReadAllBytes(path));

    public PeImage(byte[] data)
    {
        Data = data;
        if (data.Length < 0x40 || data[0] != (byte)'M' || data[1] != (byte)'Z')
            throw new InvalidDataException("not an MZ file");
        int pe = (int)U32(0x3C);
        if (pe < 0 || pe + 24 > data.Length || data[pe] != (byte)'P' || data[pe + 1] != (byte)'E' || data[pe + 2] != 0 || data[pe + 3] != 0)
            throw new InvalidDataException("no PE signature");
        ushort machine = U16(pe + 4);
        int nsect = U16(pe + 6);
        Timestamp = U32(pe + 8);
        int optsize = U16(pe + 20);
        Machine = machine switch { 0x8664 => "x64", 0x14C => "x86", 0xAA64 => "arm64", _ => "0x" + machine.ToString("x") };
        int opt = pe + 24;
        Pe64 = U16(opt) == 0x20B;
        ImageBase = Pe64 ? U64(opt + 24) : U32(opt + 28);
        int dirs = opt + (Pe64 ? 112 : 96);
        uint ndirs = U32(dirs - 4);
        var list = new List<(uint, uint)>();
        for (int i = 0; i < Math.Min(ndirs, 16); i++)
            list.Add((U32(dirs + 8 * i), U32(dirs + 8 * i + 4)));
        Directories = list;
        var sections = new List<PeSection>();
        int sect = opt + optsize;
        for (int i = 0; i < nsect; i++)
        {
            int s = sect + 40 * i;
            uint vsize = U32(s + 8), va = U32(s + 12), rsize = U32(s + 16), raw = U32(s + 20);
            sections.Add(new PeSection(va, Math.Max(vsize, rsize), raw, rsize, U32(s + 36)));
        }
        Sections = sections;
        Managed = Directories.Count > 14 && Directories[14].Rva != 0;
    }

    ushort U16(int o) => BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(Check(o, 2)));
    uint U32(int o) => BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(Check(o, 4)));
    ulong U64(int o) => BinaryPrimitives.ReadUInt64LittleEndian(Data.AsSpan(Check(o, 8)));
    int I32(int o) => BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan(Check(o, 4)));

    int Check(int o, int n)
    {
        if (o < 0 || o + n > Data.Length)
            throw new InvalidDataException($"read past the end of the file at 0x{o:X}");
        return o;
    }

    /// <summary>File offset of an RVA, or -1 when it has no bytes in the file.</summary>
    public int Offset(long rva)
    {
        foreach (var s in Sections)
            if (s.Va <= rva && rva < (long)s.Va + s.Size)
            {
                long o = rva - s.Va;
                return o < s.RawSize ? (int)(s.Raw + o) : -1;
            }
        return -1;
    }

    /// <summary>The zero-terminated Latin-1 text at an RVA.</summary>
    public string? CString(uint rva)
    {
        int o = Offset(rva);
        if (o < 0)
            return null;
        int end = Array.IndexOf(Data, (byte)0, o);
        if (end < 0)
            throw new InvalidDataException("unterminated string");
        return Encoding.Latin1.GetString(Data, o, end - o);
    }

    public IEnumerable<PeExport> Exports()
    {
        if (Directories.Count == 0 || Directories[0].Rva == 0)
            yield break;
        var (dirRva, dirSize) = Directories[0];
        int o = Offset(dirRva);
        if (o < 0)
            yield break;
        uint @base = U32(o + 16), nfuncs = U32(o + 20), nnames = U32(o + 24);
        uint afuncs = U32(o + 28), anames = U32(o + 32), aords = U32(o + 36);
        int fo = Offset(afuncs);
        if (fo < 0 || nfuncs == 0)
            yield break;
        var funcs = new uint[nfuncs];
        for (int i = 0; i < nfuncs; i++)
            funcs[i] = U32(fo + 4 * i);
        var names = new Dictionary<int, List<string?>>();
        if (nnames > 0)
        {
            int no = Offset(anames), oo = Offset(aords);
            for (int i = 0; i < nnames; i++)
            {
                uint nameRva = U32(no + 4 * i);
                int index = U16(oo + 2 * i);
                if (!names.TryGetValue(index, out var l))
                    names[index] = l = [];
                l.Add(CString(nameRva));
            }
        }
        for (int index = 0; index < funcs.Length; index++)
        {
            uint rva = funcs[index];
            if (rva == 0)
                continue;
            string? forwarder = null;
            uint? at = rva;
            if (dirRva <= rva && rva < dirRva + dirSize)
            {
                forwarder = CString(rva);
                at = null;
            }
            if (names.TryGetValue(index, out var l))
                foreach (var name in l)
                    yield return new PeExport(@base + (uint)index, at, name, forwarder);
            else
                yield return new PeExport(@base + (uint)index, at, null, forwarder);
        }
    }

    /// <summary>Regular and delay-load imports.</summary>
    public IEnumerable<PeImport> Imports() => ImportsOf(1, 20, 12, 0, 16).Concat(ImportsOf(13, 32, 4, 16, 12));

    IEnumerable<PeImport> ImportsOf(int directory, int entrySize, int nameAt, int lookupAt, int iatAt)
    {
        if (Directories.Count <= directory || Directories[directory].Rva == 0)
            yield break;
        int o = Offset(Directories[directory].Rva);
        if (o < 0)
            yield break;
        int width = Pe64 ? 8 : 4;
        while (o + entrySize <= Data.Length)
        {
            if (Data.AsSpan(o, entrySize).IndexOfAnyExcept((byte)0) < 0)
                break;
            int entry = o;
            o += entrySize;
            uint nameRva = U32(entry + nameAt);
            uint lookup = U32(entry + lookupAt);
            if (lookup == 0)
                lookup = U32(entry + iatAt);
            string? dll = CString(nameRva);
            int t = Offset(lookup);
            if (dll == null || t < 0)
                continue;
            while (true)
            {
                ulong value = Pe64 ? U64(t) : U32(t);
                if (value == 0)
                    break;
                t += width;
                ulong flag = Pe64 ? 1UL << 63 : 1UL << 31;
                if ((value & flag) != 0)
                    yield return new PeImport(dll, null, (uint)(value & 0xFFFF));
                else
                {
                    string? name = CString((uint)((value & 0x7FFFFFFF) + 2));
                    if (name != null)
                        yield return new PeImport(dll, name, null);
                }
            }
        }
    }

    bool HasExceptionTable => Pe64 && Machine == "x64" && Directories.Count > 3 && Directories[3].Rva != 0;

    /// <summary>
    /// Sorted functions of an x64 image. The exception table lists every function that has a stack
    /// frame. Leaf functions (small getters, thunks) are not in it: they are taken from the gaps
    /// between listed functions, one per run of bytes that starts on a 16-byte boundary after int3
    /// padding.
    /// </summary>
    public IReadOnlyList<FunctionRange> FunctionRanges()
    {
        if (_ranges != null)
            return _ranges;
        _ranges = [];
        if (!HasExceptionTable)
            return _ranges;
        int o = Offset(Directories[3].Rva);
        if (o < 0)
            return _ranges;
        var listed = new Dictionary<uint, uint>();
        int count = (int)(Directories[3].Size / 12);
        for (int i = 0; i < count; i++)
        {
            uint begin = U32(o + 12 * i), end = U32(o + 12 * i + 4);
            if (begin != 0 && end > begin)
                listed[begin] = listed.TryGetValue(begin, out var e) ? Math.Max(e, end) : end;
        }
        var outList = new List<FunctionRange>(listed.Count * 2);
        foreach (var (b, e) in listed)
            outList.Add(new FunctionRange(b, e, false));
        var ordered = listed.OrderBy(kv => kv.Key).ToArray();
        foreach (var s in Sections)
        {
            if (!s.Executable)
                continue;
            uint va = s.Va, rsize = s.RawSize, raw = s.Raw;
            var inside = ordered.Where(kv => va <= kv.Key && kv.Key < va + rsize).Select(kv => (kv.Key, kv.Value)).ToList();
            inside.Add((va + rsize, va + rsize));
            uint cursor = va;
            foreach (var (b, e) in inside)
            {
                if (b > cursor)
                {
                    // runs of bytes other than int3 (0xCC) in the gap [cursor, b)
                    long gapStart = raw + (long)cursor - va, gapEnd = raw + (long)b - va;
                    gapEnd = Math.Min(gapEnd, Data.Length);
                    uint? start = null;
                    uint segEnd = 0;
                    long i = gapStart;
                    while (i < gapEnd)
                    {
                        if (Data[i] == 0xCC) { i++; continue; }
                        long runStart = i;
                        while (i < gapEnd && Data[i] != 0xCC)
                            i++;
                        uint seg = (uint)(cursor + (runStart - gapStart));
                        if (seg % 16 == 0 || start == null)
                        {
                            if (start != null)
                                outList.Add(new FunctionRange(start.Value, segEnd, true));
                            start = seg;
                        }
                        segEnd = (uint)(cursor + (i - gapStart));
                    }
                    if (start != null)
                        outList.Add(new FunctionRange(start.Value, segEnd, true));
                }
                cursor = Math.Max(cursor, e);
            }
        }
        outList.Sort((x, y) => x.Begin != y.Begin ? x.Begin.CompareTo(y.Begin) : x.End != y.End ? x.End.CompareTo(y.End) : x.Leaf.CompareTo(y.Leaf));
        _ranges = outList;
        return _ranges;
    }

    /// <summary>
    /// {start of a function part: start of the function it belongs to}. The compiler splits a
    /// function into several entries of the exception table; the unwind information of each later
    /// part is chained to the entry of the part before it.
    /// </summary>
    public Dictionary<uint, uint> ChunkParents()
    {
        var result = new Dictionary<uint, uint>();
        if (!HasExceptionTable)
            return result;
        int o = Offset(Directories[3].Rva);
        if (o < 0)
            return result;
        int count = (int)(Directories[3].Size / 12);
        for (int i = 0; i < count; i++)
        {
            uint begin = U32(o + 12 * i), unwind = U32(o + 12 * i + 8);
            uint parent = begin;
            for (int steps = 0; steps < 16; steps++)
            {
                int u = Offset(unwind);
                if (u < 0 || u + 4 > Data.Length || ((Data[u] >> 3) & 4) == 0)
                    break;
                int chained = u + 4 + ((Data[u + 2] + 1) & ~1) * 2;
                if (chained + 12 > Data.Length)
                    break;
                parent = U32(chained);
                unwind = U32(chained + 8);
            }
            if (begin != 0 && parent != begin)
                result[begin] = parent;
        }
        return result;
    }

    /// <summary>Sorted start addresses of the functions (see <see cref="FunctionRanges"/>).</summary>
    public uint[] Functions()
    {
        if (_starts == null)
        {
            var ranges = FunctionRanges();
            _starts = ranges.Select(r => r.Begin).ToArray();
            _ends = ranges.Select(r => r.End).ToArray();
        }
        return _starts;
    }

    /// <summary>Start of the function that contains the address, or null.</summary>
    public uint? Owner(uint address)
    {
        var starts = Functions();
        int i = UpperBound(starts, address) - 1;
        if (i >= 0 && address < _ends![i])
            return starts[i];
        return null;
    }

    static int UpperBound(uint[] a, uint value)
    {
        int lo = 0, hi = a.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (a[mid] <= value) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    public IEnumerable<(uint Begin, uint End)> CodeRanges() =>
        Sections.Where(s => s.Executable).Select(s => (s.Va, s.Va + s.Size));

    /// <summary>
    /// Text constants in read-only data that code loads the address of (lea reg, [rip+x]), at least
    /// five printable ASCII characters. Returns ({rva: text}, {rva: function starts}). Also fills
    /// <see cref="LeaTargets"/> and <see cref="LeaRefs"/> with the other read-only addresses code loads.
    /// </summary>
    public (Dictionary<uint, string> Texts, Dictionary<uint, HashSet<uint>> Refs) StringRefs()
    {
        var strings = new Dictionary<uint, (int Offset, int Length)>();
        foreach (var s in Sections)
        {
            if (s.Executable || s.Writable || !s.Readable)
                continue;
            long end = Math.Min((long)s.Raw + s.RawSize, Data.Length);
            long run = -1;
            for (long i = s.Raw; i < end; i++)
            {
                byte c = Data[i];
                if (c >= 0x20 && c <= 0x7E)
                {
                    if (run < 0) run = i;
                }
                else
                {
                    if (c == 0 && run >= 0 && i - run >= 5)
                        strings[(uint)(s.Va + (run - s.Raw))] = ((int)run, (int)(i - run));
                    run = -1;
                }
            }
        }
        var refs = new Dictionary<uint, HashSet<uint>>();
        LeaTargets.Clear();
        LeaRefs.Clear();
        var readOnly = Sections.Where(s => s.ReadOnlyData).Select(s => (s.Va, End: s.Va + s.RawSize)).ToArray();
        foreach (var s in Sections)
        {
            if (!s.Executable)
                continue;
            long limit = Math.Min((long)s.Raw + s.RawSize, Data.Length);
            long last = (long)s.Raw + s.RawSize - 7;
            long i = s.Raw;
            while (i + 2 < limit)
            {
                byte b0 = Data[i];
                if ((b0 == 0x48 || b0 == 0x4C) && Data[i + 1] == 0x8D && (Data[i + 2] & 0xC7) == 0x05)
                {
                    if (i > last)
                        break;
                    uint here = (uint)(s.Va + (i - s.Raw));
                    uint target = (uint)(here + 7 + I32((int)i + 3));
                    if (strings.ContainsKey(target))
                    {
                        if (Owner(here) is uint f)
                            Add(refs, target, f);
                    }
                    else if (readOnly.Any(r => r.Va <= target && target < r.End))
                    {
                        LeaTargets.Add(target);
                        if (Owner(here) is uint f)
                            Add(LeaRefs, target, f);
                    }
                    i += 3;
                }
                else
                    i++;
            }
        }
        var texts = new Dictionary<uint, string>(refs.Count);
        foreach (var rva in refs.Keys)
        {
            var (off, len) = strings[rva];
            texts[rva] = Encoding.Latin1.GetString(Data, off, len);
        }
        return (texts, refs);
    }

    static void Add(Dictionary<uint, HashSet<uint>> map, uint key, uint value)
    {
        if (!map.TryGetValue(key, out var set))
            map[key] = set = [];
        set.Add(value);
    }

    /// <summary>
    /// Virtual tables found by shape: an 8-aligned address in read-only data that code takes the
    /// address of and that holds pointers to function starts. Run <see cref="StringRefs"/> first.
    /// </summary>
    public SortedDictionary<uint, List<uint>> FindVtables(ISet<uint> stop)
    {
        var starts = new HashSet<uint>(Functions());
        var targets = LeaTargets.Where(t => t % 8 == 0).Order().ToArray();
        var bounds = new HashSet<uint>(targets);
        bounds.UnionWith(stop);
        var result = new SortedDictionary<uint, List<uint>>();
        foreach (var rva in targets)
        {
            int o = Offset(rva);
            var slots = new List<uint>();
            while (o >= 0 && o + 8 <= Data.Length && slots.Count < 2048)
            {
                if (slots.Count > 0 && bounds.Contains((uint)(rva + 8 * slots.Count)))
                    break;
                long target = (long)(U64(o) - ImageBase);
                if (target < 0 || target > uint.MaxValue || !starts.Contains((uint)target))
                    break;
                slots.Add((uint)target);
                o += 8;
            }
            if (slots.Count > 0)
                result[rva] = slots;
        }
        return result;
    }

    /// <summary>Function addresses in the virtual table at rva, up to the first value that is not
    /// code or the next address in <paramref name="stop"/>.</summary>
    public List<uint> Vtable(uint rva, ISet<uint> stop)
    {
        int o = Offset(rva);
        var code = CodeRanges().ToArray();
        var slots = new List<uint>();
        while (o >= 0 && o + 8 <= Data.Length && slots.Count < 2048)
        {
            if (slots.Count > 0 && stop.Contains((uint)(rva + 8 * slots.Count)))
                break;
            long target = (long)(U64(o) - ImageBase);
            if (!code.Any(c => c.Begin <= target && target < c.End))
                break;
            slots.Add((uint)target);
            o += 8;
        }
        return slots;
    }

    /// <summary>{address of an import slot: (dll in lower case, name or '#ordinal')} for regular and delay-load imports.</summary>
    public Dictionary<uint, (string Dll, string Name)> Iat()
    {
        var slots = new Dictionary<uint, (string, string)>();
        foreach (var (directory, entrySize, nameAt, lookupAt, iatAt) in new[] { (1, 20, 12, 0, 16), (13, 32, 4, 16, 12) })
        {
            if (Directories.Count <= directory || Directories[directory].Rva == 0)
                continue;
            int o = Offset(Directories[directory].Rva);
            while (o >= 0 && o + entrySize <= Data.Length)
            {
                if (Data.AsSpan(o, entrySize).IndexOfAnyExcept((byte)0) < 0)
                    break;
                int entry = o;
                o += entrySize;
                string? dll = CString(U32(entry + nameAt));
                uint first = U32(entry + iatAt);
                uint lookup = U32(entry + lookupAt);
                int t = Offset(lookup != 0 ? lookup : first);
                if (dll == null || t < 0)
                    continue;
                for (int i = 0; ; i++)
                {
                    ulong value = U64(t + 8 * i);
                    if (value == 0)
                        break;
                    string? name = (value >> 63) != 0 ? "#" + (value & 0xFFFF) : CString((uint)((value & 0x7FFFFFFF) + 2));
                    slots[(uint)(first + 8 * i)] = (dll.ToLowerInvariant(), name!);
                }
            }
        }
        return slots;
    }

    /// <summary>
    /// Direct calls: ({function: callees in address order}, {function: what it only jumps to}).
    /// Only call rel32, call [import], and functions that are a single jump (thunks) are seen.
    /// </summary>
    public (Dictionary<uint, List<Callee>> Calls, Dictionary<uint, Callee> Alias) Calls()
    {
        var functions = Functions();
        var starts = new HashSet<uint>(functions);
        var slots = Iat();
        var sites = new List<(uint Here, Callee Target)>();
        var alias = new Dictionary<uint, Callee>();
        foreach (var s in Sections)
        {
            if (!s.Executable)
                continue;
            long end = Math.Min((long)s.Raw + s.RawSize, Data.Length);
            long i = s.Raw;
            while (i < end)
            {
                byte b = Data[i];
                if (b == 0xE8)
                {
                    uint here = (uint)(s.Va + (i - s.Raw));
                    if (i + 5 <= end)
                    {
                        uint target = (uint)(here + 5 + I32((int)i + 1));
                        if (starts.Contains(target))
                            sites.Add((here, Callee.Function(target)));
                    }
                    i += 1;
                }
                else if (b == 0xFF && i + 1 < end && (Data[i + 1] == 0x15 || Data[i + 1] == 0x25))
                {
                    uint here = (uint)(s.Va + (i - s.Raw));
                    if (i + 6 <= end)
                    {
                        uint target = (uint)(here + 6 + I32((int)i + 2));
                        if (slots.TryGetValue(target, out var imp))
                        {
                            if (Data[i + 1] == 0x15)
                                sites.Add((here, Callee.Import(imp.Dll, imp.Name)));
                            else if (starts.Contains(here) || starts.Contains(here - 1))
                                alias[starts.Contains(here) ? here : here - 1] = Callee.Import(imp.Dll, imp.Name);
                        }
                    }
                    i += 2;
                }
                else
                    i++;
            }
        }
        foreach (var start in functions)
        {
            int o = Offset(start);
            if (o >= 0 && o + 5 <= Data.Length && Data[o] == 0xE9)
            {
                uint target = (uint)(start + 5 + I32(o + 1));
                if (starts.Contains(target) && target != start)
                    alias[start] = Callee.Function(target);
            }
        }
        sites.Sort((a, b) => a.Here.CompareTo(b.Here));
        var calls = new Dictionary<uint, List<Callee>>();
        foreach (var (here, target) in sites)
            if (Owner(here) is uint f)
            {
                if (!calls.TryGetValue(f, out var l))
                    calls[f] = l = [];
                l.Add(target);
            }
        return (calls, alias);
    }

    /// <summary>The version resource's fixed file version (a.b.c.d), or "" when there is none.</summary>
    public string FileVersion()
    {
        if (Directories.Count <= 2 || Directories[2].Rva == 0)
            return "";
        int o = Offset(Directories[2].Rva);
        if (o < 0)
            return "";
        // VS_FIXEDFILEINFO starts with the signature 0xFEEF04BD; search the resource section for it.
        var sig = new byte[] { 0xBD, 0x04, 0xEF, 0xFE };
        int end = Math.Min(Data.Length, o + (int)Math.Min(Directories[2].Size + 0x100000, int.MaxValue / 2));
        int at = Data.AsSpan(o, end - o).IndexOf(sig);
        if (at < 0 || o + at + 16 > Data.Length)
            return "";
        uint ms = U32(o + at + 8), ls = U32(o + at + 12);
        return $"{ms >> 16}.{ms & 0xFFFF}.{ls >> 16}.{ls & 0xFFFF}";
    }
}
