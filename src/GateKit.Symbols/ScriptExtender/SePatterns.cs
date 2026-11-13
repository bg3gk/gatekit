using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using GateKit.Symbols.Pe;

namespace GateKit.Symbols.ScriptExtender;

/// <summary>A byte pattern with wildcards: Mask[i] false means any byte.</summary>
public sealed class BytePattern(byte[] bytes, bool[] mask)
{
    public byte[] Bytes { get; } = bytes;
    public bool[] Mask { get; } = mask;
    public int Length => Bytes.Length;

    int Anchor => Array.IndexOf(Mask, true);

    bool At(byte[] data, long pos)
    {
        for (int i = 0; i < Bytes.Length; i++)
            if (Mask[i] && data[pos + i] != Bytes[i])
                return false;
        return true;
    }

    /// <summary>Non-overlapping matches in data[from, to), as file offsets, left to right.</summary>
    public IEnumerable<long> Matches(byte[] data, long from, long to)
    {
        int anchor = Anchor;
        long last = to - Bytes.Length;
        long pos = from;
        while (pos <= last)
        {
            if (anchor >= 0)
            {
                int hit = data.AsSpan((int)(pos + anchor), (int)(last - pos + 1)).IndexOf(Bytes[anchor]);
                if (hit < 0)
                    yield break;
                pos += hit;
            }
            if (At(data, pos))
            {
                yield return pos;
                pos += Math.Max(1, Bytes.Length);
            }
            else
                pos++;
        }
    }
}

/// <summary>
/// Script Extender's knowledge of the game executable, applied offline. Script Extender finds what
/// it needs in bg3.exe by byte patterns kept in BG3Extender/GameHooks/BinaryMappings.xml. This
/// reads that file from a bg3se source tree, runs the patterns over an executable on disk and
/// returns the symbols they locate, including the static registration records of ECS components,
/// systems and replicated types.
/// <para>The pattern language (CoreLib/SymbolMapper.cpp, Pattern::FromString): two hex digits per
/// byte, '??' for any byte, '@name' marks the offset of the next byte, '//' starts a comment. A
/// Target is the match address plus an offset ('Absolute') or the address the instruction there
/// refers to ('Indirect'). A Condition compares the text an instruction refers to.</para>
/// </summary>
public static class SePatterns
{
    public const string MappingsPath = "BG3Extender/GameHooks/BinaryMappings.xml";

    public sealed record Symbol(string Name, uint Rva, string Mapping, string Note);
    public sealed record EcsRecord(string Type, string? Context, uint IndexRva, uint? ContextRva, string Source);
    public sealed record Report(string Mapping, int Matches, string Status);
    public sealed record Result(List<Symbol> Symbols, List<EcsRecord> Ecs, List<Report> Report, string Version);

    /// <summary>Displacement from the instruction at file offset pos to what it refers to, after
    /// AsmResolveInstructionRef; null when the instruction is not one of the supported forms.</summary>
    internal static long? Resolve(byte[] d, long pos)
    {
        if (pos < 0 || pos + 3 > d.Length)
            return null;
        byte b0 = d[pos], b1 = d[pos + 1], b2 = d[pos + 2];
        long I32(int at) => pos + at + 4 <= d.Length ? BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan((int)(pos + at))) : throw new InvalidDataException("past the end");
        if (b0 is 0xE8 or 0xE9) return I32(1) + 5;
        if (b0 == 0x8B && b1 < 0x20) return I32(2) + 6;
        if (b0 is 0x44 or 0x48 or 0x4C && b1 is 0x8D or 0x8B or 0x89) return I32(3) + 7;
        if (b0 == 0x48 && b1 == 0x63) return I32(3) + 7;
        if (b0 == 0x44 && b1 == 0x0F && b2 == 0xB7) return I32(4) + 8;
        if (b0 == 0x0F && b1 == 0xB7) return I32(3) + 7;
        if (b0 == 0x48 && b1 == 0x3B && (b2 & 0x0F) == 0x0D) return I32(3) + 7;
        if (b0 == 0x48 && b1 == 0x83 && b2 == 0x3D) return I32(3) + 8;
        if (b0 == 0xC7 && b1 == 0x05) return I32(2) + 10;
        if (b0 == 0x66 && b1 == 0x0B) return I32(3) + 7;
        if (b0 == 0x3B) return I32(2) + 6;
        if (b0 == 0xFF && b1 == 0x05) return I32(2) + 6;
        return null;
    }

    sealed class Mapping
    {
        public string Name = "";
        public string Scope = "Text";
        public Dictionary<string, int> Anchors = [];
        public BytePattern? Pattern;
        public List<Dictionary<string, string>> Targets = [];
        public List<Dictionary<string, string>> Conditions = [];

        public int OffsetOf(string? value)
        {
            if (value == null)
                return 0;
            if (value.StartsWith('@'))
                return Anchors[value];
            return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? int.Parse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                : int.Parse(value, CultureInfo.InvariantCulture);
        }
    }

    static readonly Regex HexByte = new("^[0-9a-fA-F]{2}$", RegexOptions.Compiled);

    static Mapping Parse(XElement node)
    {
        var m = new Mapping { Name = (string?)node.Attribute("Name") ?? "", Scope = (string?)node.Attribute("Scope") ?? "Text" };
        // the text of the node itself and the tails after its children, as ElementTree sees it
        var text = string.Concat(node.Nodes().OfType<XText>().Select(t => t.Value));
        var bytes = new List<byte>();
        var mask = new List<bool>();
        foreach (var line in text.Split('\n'))
        {
            var code = line.Split("//", 2)[0];
            foreach (var token in code.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.StartsWith('@'))
                    m.Anchors[token] = bytes.Count;
                else if (token == "??")
                {
                    bytes.Add(0);
                    mask.Add(false);
                }
                else if (HexByte.IsMatch(token))
                {
                    bytes.Add(byte.Parse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    mask.Add(true);
                }
            }
        }
        m.Pattern = bytes.Count > 0 ? new BytePattern([.. bytes], [.. mask]) : null;
        foreach (var child in node.Elements())
        {
            var attrs = child.Attributes().ToDictionary(a => a.Name.LocalName, a => a.Value);
            if (child.Name.LocalName == "Target")
                m.Targets.Add(attrs);
            else if (child.Name.LocalName == "Condition")
                m.Conditions.Add(attrs);
        }
        return m;
    }

    static (List<Mapping>, string) Load(string bg3se)
    {
        var doc = XDocument.Load(Path.Combine(bg3se, MappingsPath));
        var root = doc.Root!;
        var node = root.DescendantsAndSelf("Mappings").FirstOrDefault(n => (string?)n.Attribute("Default") == "true") ?? root;
        return (node.Descendants("Mapping").Select(Parse).ToList(), (string?)node.Attribute("Version") ?? "");
    }

    sealed class Image(PeImage pe)
    {
        public readonly PeImage Pe = pe;
        public readonly byte[] D = pe.Data;
        public readonly List<PeSection> Text = pe.Sections.Where(s => s.Executable).ToList();

        public uint? Rva(long pos)
        {
            foreach (var s in Text)
                if (s.Raw <= pos && pos < (long)s.Raw + s.RawSize)
                    return (uint)(s.Va + pos - s.Raw);
            return null;
        }

        public uint? Ref(long pos)
        {
            var delta = Resolve(D, pos);
            var here = Rva(pos);
            return delta == null || here == null ? null : (uint)(here.Value + delta.Value);
        }

        public string? CString(uint? rva, bool wide = false)
        {
            if (rva == null)
                return null;
            int o = Pe.Offset(rva.Value);
            if (o < 0)
                return null;
            if (wide)
            {
                int end = o;
                while (end + 1 < D.Length && !(D[end] == 0 && D[end + 1] == 0))
                    end += 2;
                return Encoding.Unicode.GetString(D, o, Math.Min(end, D.Length) - o);
            }
            int stop = Array.IndexOf(D, (byte)0, o, Math.Min(4096, D.Length - o));
            return stop > 0 ? Encoding.Latin1.GetString(D, o, stop - o) : null;
        }

        public IEnumerable<long> Matches(BytePattern p) =>
            Text.SelectMany(s => p.Matches(D, s.Raw, Math.Min((long)s.Raw + s.RawSize, D.Length)));
    }

    static bool? Condition(Image image, Mapping mapping, long pos, Dictionary<string, string> cond)
    {
        var kind = cond.GetValueOrDefault("Type");
        if (kind is not ("String" or "WString"))
            return null;
        var target = image.Ref(pos + mapping.OffsetOf(cond.GetValueOrDefault("Offset")));
        return image.CString(target, kind == "WString") == cond.GetValueOrDefault("Value");
    }

    static readonly Regex Simplify = new(@"ls::GetTypeName<(.*)>\(void\)$", RegexOptions.Compiled);
    static readonly Regex Keyword = new(@"\b(class|struct|enum) ", RegexOptions.Compiled);

    /// <summary>'class ls::_StringView&lt;char&gt; __cdecl ls::GetTypeName&lt;struct X&gt;(void)' -> 'X'.</summary>
    internal static string? TypeName(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        var m = Simplify.Match(text);
        var name = m.Success ? m.Groups[1].Value : text;
        return Keyword.Replace(name, "").Trim();
    }

    /// <summary>Runs every mapping over the image.</summary>
    public static Result Scan(PeImage pe, string bg3se)
    {
        var (mappings, version) = Load(bg3se);
        var image = new Image(pe);
        var symbols = new List<Symbol>();
        var report = new List<Report>();
        var contexts = new Dictionary<uint, string?>();
        var indexRows = new List<(string? Name, uint? Index, uint? Context, string Source)>();
        var stringCtors = new Dictionary<long, string?>();
        var registrants = new List<(uint? Func, uint? Index, uint? Context)>();
        foreach (var mapping in mappings)
        {
            if (mapping.Pattern == null || mapping.Scope != "Text")
            {
                report.Add(new Report(mapping.Name, 0, "skipped: not a code pattern"));
                continue;
            }
            var callbacks = mapping.Targets.Select(t => t.GetValueOrDefault("EngineCallback")).Where(c => c != null).ToHashSet();
            var hits = image.Matches(mapping.Pattern).ToList();
            if (callbacks.Count > 0)
            {
                foreach (var pos in hits)
                {
                    uint? Ref(int at) => image.Ref(pos + at);
                    if (callbacks.Contains("BindECSContext"))
                    {
                        if (Ref(7) is uint c)
                            contexts[c] = TypeName(image.CString(Ref(0x63)));
                    }
                    else if (callbacks.Contains("BindECSIndex"))
                        indexRows.Add((TypeName(image.CString(Ref(0x46))), Ref(0x1C), Ref(0), "id registrant"));
                    else if (callbacks.Contains("BindComponentReplicationIDRef"))
                        indexRows.Add((TypeName(image.CString(Ref(0x31))), Ref(7), Ref(0), "replication id"));
                    else if (callbacks.Contains("BindECSStaticStringConstructor"))
                    {
                        var start = image.Rva(pos);
                        stringCtors[start != null ? start.Value - 0x65L : -1] = TypeName(image.CString(Ref(9)));
                    }
                    else if (callbacks.Contains("BindECSStaticRegistrant"))
                        registrants.Add((Ref(0x13), Ref(0), Ref(0x1B)));
                }
                report.Add(new Report(mapping.Name, hits.Count, "ecs records"));
                continue;
            }
            var good = new List<long>();
            bool? unverified = false;
            foreach (var pos in hits)
            {
                var results = mapping.Conditions.Select(c => Condition(image, mapping, pos, c)).ToList();
                if (results.Contains(false))
                    continue;
                if (unverified == false && results.Contains(null))
                    unverified = null;
                good.Add(pos);
            }
            if (good.Count == 0)
            {
                report.Add(new Report(mapping.Name, hits.Count, "not found"));
                continue;
            }
            var first = good[0];
            var note = new List<string>();
            if (good.Count > 1)
                note.Add($"first of {good.Count} matches");
            if (unverified == null)
                note.Add("FixedString condition not checked");
            int found = 0;
            foreach (var t in mapping.Targets)
            {
                var name = t.GetValueOrDefault("Symbol") ?? t.GetValueOrDefault("Name");
                if (string.IsNullOrEmpty(name))
                    continue;
                long at = first + mapping.OffsetOf(t.GetValueOrDefault("Offset"));
                var rva = t.GetValueOrDefault("Type") == "Indirect" ? image.Ref(at) : image.Rva(at);
                if (rva != null)
                {
                    symbols.Add(new Symbol(name, rva.Value, mapping.Name, string.Join("; ", note)));
                    found++;
                }
            }
            report.Add(new Report(mapping.Name, hits.Count, $"{found} symbols" + (note.Count > 0 ? "; " + string.Join("; ", note) : "")));
        }
        foreach (var (func, index, context) in registrants)
            if (func != null && stringCtors.TryGetValue(func.Value, out var name))
                indexRows.Add((name, index, context, "static registrant"));
        var ecs = new List<EcsRecord>();
        foreach (var (name, index, context, source) in indexRows)
            if (!string.IsNullOrEmpty(name) && index != null)
                ecs.Add(new EcsRecord(name, context != null ? contexts.GetValueOrDefault(context.Value) : null, index.Value, context, source));
        return new Result(symbols, ecs, report, version);
    }
}
