using System.Globalization;
using System.Text;
using GateKit.Symbols.Pe;
using Iced.Intel;

namespace GateKit.Symbols.Queries;

/// <summary>One instruction of a signature: its bytes, a mask (0 where the byte depends on where
/// things are placed) and its text.</summary>
public sealed record MaskedInstruction(byte[] Bytes, byte[] Mask, string Text);

/// <summary>Byte signatures of game functions, for hooks that survive a patch. A signature is the
/// function's first bytes with everything that depends on placement (rel32 call and jump targets,
/// RIP-relative displacements) replaced by wildcards: the shortest such pattern that occurs exactly
/// once in the code of the executable and at most once in each other one, in the syntax of Script
/// Extender's BinaryMappings.xml.</summary>
public static class Signature
{
    static IntelFormatter NewFormatter()
    {
        var f = new IntelFormatter();
        var o = f.Options;
        o.HexPrefix = "0x";
        o.HexSuffix = null;
        o.UppercaseHex = false;
        o.SmallHexNumbersInDecimal = true;
        o.SpaceAfterOperandSeparator = true;
        o.SpaceBetweenMemoryAddOperators = true;
        o.MemorySizeOptions = MemorySizeOptions.Always;
        o.RipRelativeAddresses = true;
        o.ShowBranchSize = false;
        o.BranchLeadingZeros = false;
        o.LeadingZeros = false;
        o.DisplacementLeadingZeros = false;
        return f;
    }

    /// <summary>The instructions of a piece of code with their masks, up to the first ret or int3
    /// (or the first byte that does not decode).</summary>
    public static List<MaskedInstruction> Masked(byte[] code, ulong address)
    {
        var decoder = Iced.Intel.Decoder.Create(64, new ByteArrayCodeReader(code), address);
        var formatter = NewFormatter();
        var list = new List<MaskedInstruction>();
        int offset = 0;
        while (offset < code.Length)
        {
            decoder.Decode(out var insn);
            if (insn.IsInvalid || decoder.LastError != DecoderError.None)
                break;
            var raw = code.AsSpan(offset, insn.Length).ToArray();
            var mask = Enumerable.Repeat((byte)0xFF, raw.Length).ToArray();
            var co = decoder.GetConstantOffsets(insn);
            if (insn.IsIPRelativeMemoryOperand && co.HasDisplacement)
                for (int i = co.DisplacementOffset; i < co.DisplacementOffset + co.DisplacementSize; i++)
                    mask[i] = 0;
            bool branch = insn.FlowControl is FlowControl.Call or FlowControl.IndirectCall or FlowControl.UnconditionalBranch
                or FlowControl.ConditionalBranch or FlowControl.IndirectBranch;
            if (branch && IsNearBranch(insn) && co.ImmediateSize == 4)     // rel32; short jumps stay, they are part of the shape
                for (int i = co.ImmediateOffset; i < co.ImmediateOffset + co.ImmediateSize; i++)
                    mask[i] = 0;
            var output = new StringOutput();
            formatter.Format(insn, output);
            list.Add(new MaskedInstruction(raw, mask, output.ToStringAndReset().Trim()));
            offset += insn.Length;
            if ((insn.Mnemonic == Mnemonic.Ret && !insn.HasRepPrefix && !insn.HasRepePrefix) || insn.Mnemonic == Mnemonic.Int3)
                break;
        }
        return list;
    }

    static bool IsNearBranch(in Instruction insn) => insn.Op0Kind is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64;

    /// <summary>Number of places (at most <paramref name="limit"/>) in the executable sections of an
    /// image where the pattern occurs, counted without overlaps as Python's re.finditer does.</summary>
    public static int Count(PeImage image, IReadOnlyList<MaskedInstruction> used, int limit = 3)
    {
        var (bytes, mask) = Flatten(used);
        int n = 0;
        foreach (var s in image.Sections)
        {
            if (!s.Executable)
                continue;
            int begin = (int)Math.Min(s.Raw, (uint)image.Data.Length);
            int end = (int)Math.Min((long)s.Raw + s.RawSize, image.Data.Length);
            n += CountIn(image.Data, begin, end, bytes, mask, limit - n);
            if (n >= limit)
                return n;
        }
        return n;
    }

    static (byte[] Bytes, byte[] Mask) Flatten(IReadOnlyList<MaskedInstruction> used) =>
        (used.SelectMany(u => u.Bytes).ToArray(), used.SelectMany(u => u.Mask).ToArray());

    /// <summary>Non-overlapping matches of a masked pattern in data[begin..end], at most <paramref name="limit"/>.</summary>
    internal static int CountIn(byte[] data, int begin, int end, byte[] bytes, byte[] mask, int limit)
    {
        int len = bytes.Length;
        if (len == 0 || limit <= 0)
            return 0;
        // the longest run of fixed bytes is searched for first
        int anchor = 0, anchorLen = 0;
        for (int i = 0; i < len;)
        {
            if (mask[i] == 0)
            {
                i++;
                continue;
            }
            int j = i;
            while (j < len && mask[j] != 0)
                j++;
            if (j - i > anchorLen)
                (anchor, anchorLen) = (i, j - i);
            i = j;
        }
        int n = 0, pos = begin;
        var key = bytes.AsSpan(anchor, anchorLen);
        while (pos + len <= end)
        {
            int start;
            if (anchorLen == 0)
                start = pos;
            else
            {
                int at = data.AsSpan(pos + anchor, end - pos - anchor).IndexOf(key);
                if (at < 0)
                    break;
                start = pos + at;
                if (start + len > end)
                    break;
            }
            bool ok = true;
            for (int k = 0; k < len; k++)
                if (mask[k] != 0 && data[start + k] != bytes[k])
                {
                    ok = false;
                    break;
                }
            if (ok)
            {
                if (++n >= limit)
                    return n;
                pos = start + len;
            }
            else
                pos = start + 1;
        }
        return n;
    }

    /// <summary>The instructions of the shortest unique signature of the function at rva and the
    /// matches per image (the image first, then the others); null instructions when there is none.</summary>
    public static (List<MaskedInstruction>? Used, List<int> Counts) Build(PeImage image, long rva, long size, IReadOnlyList<PeImage> others,
                                                                          int minBytes = 12, int maxBytes = 160)
    {
        int o = image.Offset(rva);
        if (o < 0)
            return (null, []);
        int take = (int)Math.Min(Math.Min(size, 400), image.Data.Length - o);
        var items = Masked(image.Data.AsSpan(o, take).ToArray(), (ulong)rva);
        var used = new List<MaskedInstruction>();
        int length = 0;
        List<int> CountAll() => [Count(image, used), .. others.Select(other => Count(other, used))];
        foreach (var item in items)
        {
            used.Add(item);
            length += item.Bytes.Length;
            if (length < minBytes)
                continue;
            var counts = CountAll();
            if (counts[0] == 1 && counts.Skip(1).All(c => c <= 1))
                return (used, counts);
            if (length > maxBytes)
                break;
        }
        return (null, used.Count > 0 ? CountAll() : []);
    }

    /// <summary>The signature as a BinaryMappings.xml mapping.</summary>
    public static string Render(string name, IReadOnlyList<MaskedInstruction> used)
    {
        var lines = new List<string> { $"<Mapping Name=\"{name}\">" };
        for (int i = 0; i < used.Count; i++)
        {
            var cells = string.Join(" ", used[i].Bytes.Select((b, k) => used[i].Mask[k] != 0 ? b.ToString("x2", CultureInfo.InvariantCulture) : "??"));
            lines.Add($"    {(i == 0 ? "@start " : "")}{cells} // {used[i].Text}");
        }
        lines.Add($"    <Target Type=\"Absolute\" Offset=\"@start\" Symbol=\"{name.Replace("::", "__")}\" />");
        lines.Add("</Mapping>");
        return string.Join("\n", lines);
    }
}

internal static partial class SymbolText
{
    /// <summary>A byte signature for a game function, unique in the game executables.</summary>
    public static void SignatureOf(SymbolContext c, string text, int limit)
    {
        var found = c.Targets(text);
        var games = c.Db.Query("SELECT id, name, path FROM dlls WHERE origin = 'game'").Select(r => (Id: PyText.L(r[0]), Name: PyText.Str(r[1]), Path: PyText.Str(r[2]))).ToList();
        var spots = new List<(long Dll, long Rva, string? Origin)>();
        foreach (var (dllId, rva) in found)
        {
            if (games.Any(g => g.Id == dllId))
                spots.Add((dllId, rva, null));
            else
                foreach (var r in c.Db.Query("SELECT game_dll, game_rva FROM matches WHERE toolkit_dll = ? AND toolkit_rva = ?", dllId, rva).ToList())
                    spots.Add((PyText.L(r[0]), PyText.L(r[1]), c.Label(dllId, rva)));
        }
        if (spots.Count == 0)
            throw new SymbolsException("no game function found for that (give bg3.exe+0xRVA, or a name that `gatekit symbols game` finds)");
        var recorded = c.Db.Scalar("SELECT value FROM meta WHERE key = 'game'") as string;
        var images = new Dictionary<long, PeImage>();
        foreach (var g in games)
        {
            string? path = new[] { c.Env.Game, recorded }.Where(root => !string.IsNullOrEmpty(root)).Select(root => Path.Combine(root!, g.Path)).FirstOrDefault(File.Exists);
            if (path == null)
                throw new SymbolsException($"{g.Path} not found under {c.Env.Game} (set BG3_GAME)");
            images[g.Id] = PeImage.Open(path);
        }
        foreach (var (dllId, rva, origin) in spots.Take(limit))
        {
            var size = c.Db.One("SELECT size FROM functions WHERE dll_id = ? AND rva = ?", dllId, rva);
            string name = c.Label(dllId, rva);
            c.WriteLine($"<!-- {name}" + (origin != null ? $"   (from {origin})" : "") + " -->");
            if (size == null)
            {
                c.WriteLine("<!-- not a function start -->");
                continue;
            }
            var others = images.Where(p => p.Key != dllId).ToList();
            var (used, counts) = Signature.Build(images[dllId], rva, PyText.L(size[0]), others.Select(p => p.Value).ToList());
            if (used == null)
            {
                c.WriteLine($"<!-- no unique signature within the first bytes (matches: {PyText.List(counts.Cast<object?>())}); the function is too short or too common -->");
                continue;
            }
            int gap = name.IndexOf("  ", StringComparison.Ordinal);
            string pretty = gap >= 0 ? name[(gap + 2)..].TrimStart('=', ' ') : $"sub_{X(rva)}";
            c.WriteLine(Signature.Render(pretty, used));
            var otherNames = others.Select(p => games.First(g => g.Id == p.Key).Name).ToList();
            var b = new StringBuilder($"<!-- {used.Sum(u => u.Bytes.Length)} bytes; unique in {games.First(g => g.Id == dllId).Name}");
            for (int i = 0; i < otherNames.Count && i + 1 < counts.Count; i++)
                b.Append($"; {counts[i + 1]} match in {otherNames[i]}");
            c.WriteLine(b.Append(" -->").ToString());
        }
    }
}
