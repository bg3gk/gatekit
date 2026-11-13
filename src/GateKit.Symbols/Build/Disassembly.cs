using GateKit.Symbols.Pe;
using Iced.Intel;

namespace GateKit.Symbols.Build;

/// <summary>
/// Disassembly of whole binaries with Iced: for every function of the exception table, the
/// addresses of data it refers to through [rip+x]: 'a' address taken (lea), 'r' read, 'w' written.
/// </summary>
public static class Disassembly
{
    static readonly HashSet<Mnemonic> Writes =
    [
        Mnemonic.Mov, Mnemonic.Movaps, Mnemonic.Movups, Mnemonic.Movdqa, Mnemonic.Movdqu, Mnemonic.Movss, Mnemonic.Movsd,
        Mnemonic.Movq, Mnemonic.Movd, Mnemonic.Add, Mnemonic.Sub, Mnemonic.And, Mnemonic.Or, Mnemonic.Xor, Mnemonic.Inc,
        Mnemonic.Dec, Mnemonic.Xchg, Mnemonic.Cmpxchg, Mnemonic.Movzx, Mnemonic.Movsx, Mnemonic.Movsxd,
    ];

    /// <summary>(begin, end) of every entry of the exception table, sorted, without duplicates.</summary>
    public static List<(uint Begin, uint End)> ListedRanges(PeImage pe)
    {
        var result = new List<(uint, uint)>();
        if (!pe.Pe64 || pe.Directories.Count <= 3 || pe.Directories[3].Rva == 0)
            return result;
        int o = pe.Offset(pe.Directories[3].Rva);
        if (o < 0)
            return result;
        var ranges = new Dictionary<uint, uint>();
        int count = (int)(pe.Directories[3].Size / 12);
        for (int i = 0; i < count; i++)
        {
            uint begin = BitConverter.ToUInt32(pe.Data, o + 12 * i), end = BitConverter.ToUInt32(pe.Data, o + 12 * i + 4);
            if (begin != 0 && end > begin)
                ranges[begin] = ranges.TryGetValue(begin, out var e) ? Math.Max(e, end) : end;
        }
        result.AddRange(ranges.OrderBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value)));
        return result;
    }

    /// <summary>Data references of one function: (kind, target rva) in instruction order. Decoding
    /// stops at the first invalid instruction, as capstone did.</summary>
    public static List<(char Kind, uint Target)> References(PeImage pe, uint begin, uint end)
    {
        var refs = new List<(char, uint)>();
        int o = pe.Offset(begin);
        if (o < 0 || end <= begin || end - begin > 4_000_000)
            return refs;
        int length = (int)Math.Min(end - begin, (uint)(pe.Data.Length - o));
        var reader = new ByteArrayCodeReader(pe.Data, o, length);
        var decoder = Decoder.Create(64, reader, begin, DecoderOptions.None);
        ulong stop = begin + (ulong)length;
        while (decoder.IP < stop)
        {
            decoder.Decode(out var insn);
            if (insn.IsInvalid)
                break;
            if (!insn.IsIPRelativeMemoryOperand)
                continue;
            uint target = (uint)insn.IPRelativeMemoryAddress;
            var mnemonic = insn.Mnemonic;
            if (mnemonic is Mnemonic.Call or Mnemonic.Jmp)
                continue;                                     // calls through [rip+x] are not data
            if (mnemonic == Mnemonic.Lea)
                refs.Add(('a', target));
            else if (insn.OpCount > 0 && insn.GetOpKind(0) == OpKind.Memory && Writes.Contains(mnemonic) && !insn.HasLockPrefix)
                refs.Add(('w', target));
            else
                refs.Add(('r', target));
        }
        return refs;
    }

    /// <summary>Every listed function's references to the wanted addresses, counted:
    /// (function start, target, kind, uses). Runs in parallel.</summary>
    public static List<(uint Func, uint Target, char Kind, int Uses)> Xrefs(PeImage pe, HashSet<uint> wanted, int jobs)
    {
        var ranges = ListedRanges(pe);
        var parts = new List<(uint, uint, char, int)>[ranges.Count];
        Parallel.For(0, ranges.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, jobs) }, i =>
        {
            var (b, e) = ranges[i];
            var seen = new Dictionary<(char, uint), int>();
            var order = new List<(char, uint)>();
            foreach (var (kind, target) in References(pe, b, e))
            {
                if (!wanted.Contains(target))
                    continue;
                if (seen.TryGetValue((kind, target), out var n))
                    seen[(kind, target)] = n + 1;
                else
                {
                    seen[(kind, target)] = 1;
                    order.Add((kind, target));
                }
            }
            parts[i] = order.Select(k => (b, k.Item2, k.Item1, seen[k])).ToList();
        });
        return parts.SelectMany(p => p).ToList();
    }
}
