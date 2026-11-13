using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace GateKit.Symbols.ScriptExtender;

/// <summary>A field of a Script Extender struct; Offset/Size are computed, null where unknown.</summary>
public sealed class SeField
{
    public required string Name;
    public required string Type;
    public string? Legacy;
    public int Hidden;
    public long? Offset;
    public long? Size;
    /// <summary>1: computed offset equals the offset in the field's name; 2: taken from the name.</summary>
    public int? Checked;
}

/// <summary>A struct, component, system, tag or boost described by the Script Extender headers.</summary>
public sealed class SeType
{
    public required string Name;
    public required string Namespace;
    public string? ExtName;
    public string? EngineClass;
    public required string Kind;
    public string? Base;
    public required string File;
    public int Line;
    public List<SeField> Fields = [];
    public bool Virtual;
    internal bool Visited;
    internal bool Resynced;
    public long? Size;
    public long? Align;
}

/// <summary>An enumeration or bit mask: values are null where the source does not give a number.</summary>
public sealed record SeEnum(string Name, string Namespace, string Underlying, int Bitmask, string File, List<(string Label, long? Value)> Values);

/// <summary>A class of the Lua API with its fields.</summary>
public sealed record LuaClass(string Name, string? Parent, List<(string Field, string Type)> Fields);

/// <summary>
/// Data layouts of engine types, read from a Script Extender (bg3se) source tree. bg3se describes
/// the game's ECS components, systems and many helper types as C++ structs
/// (BG3Extender/GameDefinitions), its enumerations as lists (Enumerations/*.inl) and the Lua view
/// of everything in IdeHelpers/ExtIdeHelpers.lua. This reads those files as data.
/// <para>Offsets are not written in the headers. They are computed from the field types with the
/// usual x64 alignment rules, for as long as every type so far has a known size, and checked
/// against the fields bg3se still names after their offset (field_98, or [[bg3::legacy(field_98)]]).</para>
/// </summary>
public static class SeLayouts
{
    public const string Defs = "BG3Extender/GameDefinitions";
    public const string Lua = "BG3Extender/IdeHelpers/ExtIdeHelpers.lua";

    static readonly Dictionary<string, (long Size, long Align)> Sizes = new()
    {
        ["bool"] = (1, 1), ["char"] = (1, 1), ["uint8_t"] = (1, 1), ["int8_t"] = (1, 1), ["uint16_t"] = (2, 2), ["int16_t"] = (2, 2),
        ["uint32_t"] = (4, 4), ["int32_t"] = (4, 4), ["int"] = (4, 4), ["float"] = (4, 4), ["uint64_t"] = (8, 8), ["int64_t"] = (8, 8),
        ["double"] = (8, 8), ["__int64"] = (8, 8), ["unsigned int"] = (4, 4), ["unsigned __int64"] = (8, 8), ["size_t"] = (8, 8),
        ["FixedString"] = (4, 4), ["EntityHandle"] = (8, 8), ["ecs::EntityRef"] = (16, 8), ["EntityRef"] = (16, 8),
        ["Guid"] = (16, 8), ["STDString"] = (24, 8), ["ComponentHandle"] = (8, 8), ["NetId"] = (4, 4), ["UserId"] = (4, 4),
        ["glm::vec2"] = (8, 4), ["glm::vec3"] = (12, 4), ["glm::vec4"] = (16, 4), ["glm::quat"] = (16, 4), ["glm::mat4"] = (64, 4),
        ["glm::mat3"] = (36, 4), ["glm::ivec2"] = (8, 4), ["TranslatedString"] = (16, 4), ["Version"] = (8, 8),
        ["TemplateHandle"] = (8, 8), ["PathId"] = (4, 4), ["Transform"] = (40, 4),
        ["__int8"] = (1, 1), ["__int16"] = (2, 2), ["__int32"] = (4, 4), ["glm::fvec2"] = (8, 4), ["glm::fvec3"] = (12, 4),
        ["glm::fvec4"] = (16, 4), ["bg3se::Guid"] = (16, 8), ["UnknownSignal"] = (24, 8), ["UnknownFunction"] = (64, 8),
        ["UnknownSignalSubscriber"] = (80, 8), ["CRITICAL_SECTION"] = (40, 8), ["SRWLOCK"] = (8, 8),
        ["glm::aligned_highp_vec4"] = (16, 16),
    };

    static readonly Dictionary<string, (long, long)> Templates = new()
    {
        ["Array"] = (16, 8), ["StaticArray"] = (16, 8), ["ObjectSet"] = (24, 8), ["HashSet"] = (48, 8), ["HashMap"] = (64, 8),
        ["LegacyArray"] = (16, 8), ["Queue"] = (24, 8), ["UniquePtr"] = (8, 8),
    };

    static readonly Dictionary<string, long> EnumSizes = new()
    {
        ["uint8_t"] = 1, ["int8_t"] = 1, ["uint16_t"] = 2, ["int16_t"] = 2, ["uint32_t"] = 4, ["int32_t"] = 4, ["int"] = 4,
        ["uint64_t"] = 8, ["int64_t"] = 8, ["__int64"] = 8,
    };

    const RegexOptions O = RegexOptions.Compiled | RegexOptions.CultureInvariant;
    static readonly Regex Struct = new(@"^\s*(?:struct|class)\s+(?:\[\[[^\]]*\]\]\s*)?(\w+)\s*(?:final\s*)?(?::\s*(?:public\s+)?([\w:<>, ]+?))?\s*$", O);
    static readonly Regex StructOpen = new(@"^\s*(?:struct|class)\s+(?:\[\[[^\]]*\]\]\s*)?(\w+)\s*(?:final\s*)?(?::\s*(?:public\s+)?([\w:<>, ]+?))?\s*\{\s*$", O);
    static readonly Regex Define = new(@"DEFINE_(PROXY_|ONEFRAME_)?(COMPONENT|SYSTEM)\(\s*(\w+)\s*,\s*""([^""]+)""\s*\)", O);
    static readonly Regex Tag = new(@"^\s*DEFINE_(ONEFRAME_)?TAG_COMPONENT\(\s*([\w:]+)\s*,\s*(\w+)\s*,\s*(\w+)\s*\)", O);
    static readonly Regex Boost = new(@"^\s*DEFN_BOOST\(\s*(\w+)\s*,\s*(\w+)\s*,\s*\{", O);
    static readonly Regex Field = new(@"^\s*((?:\[\[[^\]]*\]\]\s*)*)((?:const\s+)?[\w:]+(?:<.*>)?(?:\s*\*+|\s*&)?)\s+(\w+)\s*(\[[^\]]*\])?\s*(?:\{[^}]*\}|=\s*[^;]+)?;", O);
    static readonly Regex Ns = new(@"^\s*BEGIN_NS\(\s*([\w:]+)\s*\)|^\s*namespace\s+([\w:]+)\s*\{?\s*$", O);
    static readonly Regex VirtualMethod = new(@"^\s*virtual\b", O);
    static readonly Regex LegacyAttr = new(@"bg3::legacy\((\w+)\)", O);
    static readonly Regex ConstPrefix = new(@"^const\s+", O);
    static readonly Regex TemplateType = new(@"^(?:std::)?(\w+)<(.*)>$", O | RegexOptions.Singleline);
    static readonly Regex ArrayType = new(@"^(.*)\[(\d+)\]$", O | RegexOptions.Singleline);
    static readonly Regex OffsetName = new(@"^field_([0-9A-Fa-f]+)$", O);
    static readonly Regex EnumBegin = new(@"^\s*BEGIN_(ENUM|BITMASK)(_NS)?\(\s*([^)]*)\)", O);
    static readonly Regex EnumValue = new(@"^\s*(E|EV)\(\s*(\w+)\s*(?:,\s*([^)]+))?\)", O);
    static readonly HashSet<string> NotTypes = ["return", "using", "typedef", "friend", "static", "constexpr", "inline", "virtual", "delete", "template",
        "public", "private", "protected", "case", "goto", "throw", "else"];

    static int Count(string s, char c)
    {
        int n = 0;
        foreach (var ch in s)
            if (ch == c) n++;
        return n;
    }

    /// <summary>Files under a folder sorted the way Python's sorted(Path) does on this platform.</summary>
    static IEnumerable<string> Sorted(IEnumerable<string> files, string root) =>
        files.OrderBy(f => Path.GetRelativePath(root, f).Replace('\\', '/'), Decomp.Py.PathOrder);

    static string Rel(string bg3se, string path) => Path.GetRelativePath(bg3se, path).Replace('\\', '/');

    /// <summary>(size, alignment) of a C++ type, or null.</summary>
    internal static (long Size, long Align)? SizeOf(string typeName, Dictionary<string, (long, long)> known)
    {
        var t = ConstPrefix.Replace(typeName.Trim(), "", 1);
        if (t.EndsWith('*') || t.EndsWith('&'))
            return (8, 8);
        if (Sizes.TryGetValue(t, out var s))
            return s;
        var m = TemplateType.Match(t);
        if (m.Success)
        {
            string outer = m.Groups[1].Value, inner = m.Groups[2].Value;
            if (Templates.TryGetValue(outer, out var ts))
                return ts;
            if (outer == "optional")
            {
                var si = SizeOf(inner, known);
                if (si is var (size, align))
                    return (((size + 1) + align - 1) / align * align, align);
            }
            if (outer == "array")
            {
                int comma = inner.LastIndexOf(',');
                if (comma >= 0)
                {
                    var si = SizeOf(inner[..comma], known);
                    var count = inner[(comma + 1)..].Trim();
                    if (si != null && count.Length > 0 && count.All(char.IsAsciiDigit))
                        return (si.Value.Size * long.Parse(count, CultureInfo.InvariantCulture), si.Value.Align);
                }
            }
            return null;
        }
        if (known.TryGetValue(t, out var k))
            return k;
        int cut = t.LastIndexOf("::", StringComparison.Ordinal);
        return known.TryGetValue(cut >= 0 ? t[(cut + 2)..] : t, out k) ? k : null;
    }

    /// <summary>Every type of the headers under GameDefinitions, with computed offsets.</summary>
    public static List<SeType> ReadHeaders(string bg3se)
    {
        var root = Path.Combine(bg3se, Defs);
        var types = new List<SeType>();
        if (!Directory.Exists(root))
            return types;
        foreach (var path in Sorted(Directory.EnumerateFiles(root, "*.h", SearchOption.AllDirectories), root))
        {
            var rel = Rel(bg3se, path);
            var lines = ReadLines(path);
            var namespaces = new List<string>();
            var stack = new List<(SeType Type, int Depth)>();
            int depth = 0, skipIf0 = 0;
            for (int number = 1; number <= lines.Length; number++)
            {
                var raw = lines[number - 1];
                int cmt = raw.IndexOf("//", StringComparison.Ordinal);
                var line = cmt >= 0 ? raw[..cmt] : raw;
                var stripped = line.Trim();
                if (stripped.StartsWith("#if 0", StringComparison.Ordinal))
                {
                    skipIf0++;
                    continue;
                }
                if (skipIf0 > 0)
                {
                    if (stripped.StartsWith("#if", StringComparison.Ordinal))
                        skipIf0++;
                    else if (stripped.StartsWith("#endif", StringComparison.Ordinal))
                        skipIf0--;
                    continue;
                }
                var m = Ns.Match(line);
                if (m.Success && stack.Count == 0)
                {
                    namespaces.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
                    if (line.Contains('{'))
                        depth++;
                    continue;
                }
                if (stripped.StartsWith("END_NS()", StringComparison.Ordinal))
                {
                    if (namespaces.Count > 0)
                        namespaces.RemoveAt(namespaces.Count - 1);
                    continue;
                }
                m = Tag.Match(line);
                if (m.Success && stack.Count == 0)
                {
                    string ns = m.Groups[2].Value, name = m.Groups[3].Value;
                    types.Add(new SeType
                    {
                        Name = name, Namespace = ns, ExtName = m.Groups[4].Value, EngineClass = $"{ns}::{name}",
                        Kind = m.Groups[1].Success ? "oneframe tag" : "tag", Base = "BaseComponent", File = rel, Line = number,
                        Fields = [new SeField { Name = "Dummy", Type = "uint8_t" }],
                    });
                    continue;
                }
                m = Boost.Match(line);
                if (m.Success && stack.Count == 0)
                {
                    var name = m.Groups[1].Value;
                    var t = new SeType
                    {
                        Name = $"{name}BoostComponent", Namespace = string.Join("::", namespaces), ExtName = $"{name}Boost",
                        EngineClass = $"eoc::{name}BoostComponent", Kind = "boost", Base = "BaseComponent", File = rel, Line = number,
                    };
                    types.Add(t);
                    stack.Add((t, depth));
                    depth += Count(line, '{') - Count(line, '}');
                    continue;
                }
                m = StructOpen.Match(line);
                if (!m.Success && number < lines.Length && lines[number].Trim() == "{")
                    m = Struct.Match(line);
                if (m.Success && !line.Contains(';'))
                {
                    var b = m.Groups[2].Success ? m.Groups[2].Value.Trim() : "";
                    var t = new SeType
                    {
                        Name = m.Groups[1].Value, Namespace = string.Join("::", namespaces.Concat(stack.Select(s => s.Type.Name))),
                        Kind = "struct", Base = b.Length > 0 ? b : null, File = rel, Line = number,
                    };
                    types.Add(t);
                    stack.Add((t, depth));
                    depth += Count(line, '{') - Count(line, '}');
                    continue;
                }
                if (stack.Count > 0)
                {
                    var (current, opened) = stack[^1];
                    if (depth == opened + 1)
                    {
                        if (VirtualMethod.IsMatch(line))
                            current.Virtual = true;    // a virtual method: the object starts with a vtable pointer
                        var d = Define.Match(line);
                        if (d.Success)
                        {
                            current.ExtName = d.Groups[3].Value;
                            current.EngineClass = d.Groups[4].Value;
                            current.Kind = d.Groups[2].Value == "SYSTEM" ? "system"
                                : d.Groups[1].Value == "PROXY_" ? "proxy component"
                                : d.Groups[1].Value == "ONEFRAME_" ? "oneframe component" : "component";
                        }
                        else if (!line.Split('=')[0].Replace("[[bg3::legacy(", "").Replace("bg3::", "").Contains('(') || line.Contains("[[bg3::legacy("))
                        {
                            var f = Field.Match(line);
                            if (f.Success)
                            {
                                var head = f.Groups[2].Value.Split('<')[0];
                                int sep = head.IndexOf("::", StringComparison.Ordinal);
                                head = (sep >= 0 ? head[..sep] : head).Trim();
                                if (!NotTypes.Contains(head) && !f.Groups[3].Value.Contains('('))
                                {
                                    var attrs = f.Groups[1].Value;
                                    var legacy = LegacyAttr.Match(attrs);
                                    current.Fields.Add(new SeField
                                    {
                                        Name = f.Groups[3].Value, Type = f.Groups[2].Value.Trim() + (f.Groups[4].Success ? f.Groups[4].Value : ""),
                                        Legacy = legacy.Success ? legacy.Groups[1].Value : null, Hidden = attrs.Contains("bg3::hidden") ? 1 : 0,
                                    });
                                }
                            }
                        }
                    }
                    depth += Count(line, '{') - Count(line, '}');
                    while (stack.Count > 0 && depth <= stack[^1].Depth)
                        stack.RemoveAt(stack.Count - 1);
                }
                else
                {
                    depth += Count(line, '{') - Count(line, '}');
                    depth = Math.Max(depth, 0);
                }
            }
        }
        Layout(types, ReadEnums(bg3se));
        return types;
    }

    static string[] ReadLines(string path)
    {
        // Python's read_text().splitlines(): \r\n, \r and \n end lines; a final newline adds no empty line.
        var text = File.ReadAllText(path);
        var lines = new List<string>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '\n' or '\r' or '\v' or '\f' or '\x1c' or '\x1d' or '\x1e' or '\x85' || c == (char)0x2028 || c == (char)0x2029)
            {
                lines.Add(text[start..i]);
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                start = i + 1;
            }
        }
        if (start < text.Length)
            lines.Add(text[start..]);
        return [.. lines];
    }

    /// <summary>Fills offset/size of fields and size of types where every type involved is known. A
    /// field named after its offset re-synchronises the count.</summary>
    internal static void Layout(List<SeType> types, List<SeEnum> enums)
    {
        var byName = new Dictionary<string, SeType>();
        foreach (var t in types)
            byName.TryAdd(t.Name, t);
        var known = new Dictionary<string, (long, long)>();
        foreach (var e in enums)
            if (EnumSizes.TryGetValue(e.Underlying, out var size))
                known.TryAdd(e.Name, (size, size));

        (long Size, long Align)? SizeOfType(SeType t, HashSet<string> seen)
        {
            if (t.Visited)
                return t.Size is long s0 ? (s0, t.Align!.Value) : null;
            t.Visited = true;
            t.Size = t.Align = null;
            long? offset;
            long align;
            if (t.Virtual) { offset = 8; align = 8; } else { offset = 0; align = 1; }
            var b = (t.Base ?? "").Split('<')[0].Split(',')[0].Trim();
            int cut = b.LastIndexOf("::", StringComparison.Ordinal);
            b = cut >= 0 ? b[(cut + 2)..] : b;
            if (b.Length > 0 && b is not ("BaseComponent" or "BaseProxyComponent" or "ProtectedGameObject" or "Noncopyable"))
            {
                byName.TryGetValue(b, out var parent);
                (long, long)? s = parent != null && !seen.Contains(parent.Name) ? SizeOfType(parent, [.. seen, t.Name]) : null;
                if (s == null)
                    offset = null;
                else
                {
                    (offset, align) = s.Value;
                    if (t.Virtual && !(parent != null && parent.Virtual) && offset != null)
                    {
                        offset += 8;              // the first virtual method comes in this class: its vtable pointer
                        align = Math.Max(align, 8);
                    }
                }
                if (parent != null && parent.Virtual)
                    t.Virtual = true;             // inherited: classes derived from this one must not add another
            }
            foreach (var f in t.Fields)
            {
                var ftype = f.Type;
                long count = 1;
                var am = ArrayType.Match(ftype);
                if (am.Success)
                {
                    ftype = am.Groups[1].Value;
                    count = long.Parse(am.Groups[2].Value, CultureInfo.InvariantCulture);
                }
                var s = SizeOf(ftype, known);
                if (s == null)
                {
                    var bare = ftype.Trim();
                    int c2 = bare.LastIndexOf("::", StringComparison.Ordinal);
                    byName.TryGetValue(c2 >= 0 ? bare[(c2 + 2)..] : bare, out var inner);
                    if (inner != null && inner != t && !seen.Contains(inner.Name) && !ftype.Contains('<'))
                        s = SizeOfType(inner, [.. seen, t.Name]);
                }
                long? named = null;
                foreach (var candidate in new[] { f.Name, f.Legacy ?? "" })
                {
                    var om = OffsetName.Match(candidate);
                    if (om.Success)
                    {
                        named = long.Parse(om.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                        break;
                    }
                }
                f.Checked = null;
                if (s != null && offset != null)
                    offset = (offset + s.Value.Align - 1) / s.Value.Align * s.Value.Align;
                if (named != null)
                {
                    f.Checked = offset == named ? 1 : 2;
                    if (offset != named)
                        t.Resynced = true;
                    offset = named;
                }
                if (offset == null)
                {
                    f.Offset = f.Size = null;
                    continue;
                }
                f.Offset = offset;
                if (s == null)
                {
                    f.Size = null;
                    offset = null;
                    continue;
                }
                f.Size = s.Value.Size * count;
                offset += s.Value.Size * count;
                align = Math.Max(align, s.Value.Align);
            }
            if (offset != null && !t.Resynced)
            {
                t.Size = (offset + align - 1) / align * align;
                t.Align = align;
                known.TryAdd(t.Name, (t.Size.Value, align));
            }
            return t.Size is long s1 ? (s1, t.Align!.Value) : null;
        }

        foreach (var t in types)
            SizeOfType(t, []);
    }

    /// <summary>Python's int(text, 0) for the literal forms enumerations use; null when it is not one.</summary>
    internal static long? ParseInt(string text)
    {
        var s = text.Replace("_", "");
        bool negative = false;
        if (s.StartsWith('-') || s.StartsWith('+'))
        {
            negative = s[0] == '-';
            s = s[1..].TrimStart();
        }
        if (s.Length == 0)
            return null;
        BigInteger value;
        if (s.Length > 2 && s[0] == '0' && (s[1] is 'x' or 'X'))
        {
            if (!s[2..].All(char.IsAsciiHexDigit))
                return null;
            value = BigInteger.Parse("0" + s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
        else if (s.Length > 2 && s[0] == '0' && (s[1] is 'b' or 'B'))
        {
            if (!s[2..].All(c => c is '0' or '1'))
                return null;
            value = 0;
            foreach (var c in s[2..])
                value = value * 2 + (c - '0');
        }
        else if (s.Length > 2 && s[0] == '0' && (s[1] is 'o' or 'O'))
        {
            if (!s[2..].All(c => c is >= '0' and <= '7'))
                return null;
            value = 0;
            foreach (var c in s[2..])
                value = value * 8 + (c - '0');
        }
        else
        {
            if (!s.All(char.IsAsciiDigit) || (s.Length > 1 && s[0] == '0' && s.Any(c => c != '0')))
                return null;
            value = BigInteger.Parse(s, CultureInfo.InvariantCulture);
        }
        if (negative)
            value = -value;
        return value >= long.MinValue && value <= long.MaxValue ? (long)value : (long)(ulong)(value & ulong.MaxValue);
    }

    /// <summary>Enumerations and bit masks of Enumerations/*.inl and ExternalEnumerations/*.inl.</summary>
    public static List<SeEnum> ReadEnums(string bg3se)
    {
        var result = new List<SeEnum>();
        foreach (var folder in new[] { "Enumerations", "ExternalEnumerations" })
        {
            var dir = Path.Combine(bg3se, Defs, folder);
            if (!Directory.Exists(dir))
                continue;
            foreach (var path in Sorted(Directory.EnumerateFiles(dir, "*.inl", SearchOption.TopDirectoryOnly), dir))
            {
                var rel = Rel(bg3se, path);
                SeEnum? current = null;
                long value = 0;
                foreach (var line in ReadLines(path))
                {
                    var m = EnumBegin.Match(line);
                    if (m.Success)
                    {
                        var args = m.Groups[3].Value.Split(',').Select(a => a.Trim()).ToArray();
                        string ns, name, underlying = args[^1];
                        if (m.Groups[2].Success) { ns = args[0]; name = args[1]; }
                        else { ns = ""; name = args[0]; }
                        current = new SeEnum(name, ns, underlying, m.Groups[1].Value == "BITMASK" ? 1 : 0, rel, []);
                        value = 0;
                        result.Add(current);
                        continue;
                    }
                    m = EnumValue.Match(line);
                    if (m.Success && current != null)
                    {
                        if (m.Groups[1].Value == "EV" && m.Groups[3].Success)
                        {
                            var parsed = ParseInt(m.Groups[3].Value.Trim().TrimEnd('u', 'U', 'l', 'L'));
                            if (parsed == null)
                            {
                                current.Values.Add((m.Groups[2].Value, null));
                                continue;
                            }
                            value = parsed.Value;
                        }
                        current.Values.Add((m.Groups[2].Value, value));
                        value++;
                    }
                    else if (line.Trim().StartsWith("END_ENUM", StringComparison.Ordinal))
                        current = null;
                }
            }
        }
        return result;
    }

    /// <summary>Classes and fields of the Lua API from ExtIdeHelpers.lua.</summary>
    public static List<LuaClass> ReadLua(string bg3se)
    {
        var path = Path.Combine(bg3se, Lua);
        var result = new List<LuaClass>();
        if (!File.Exists(path))
            return result;
        LuaClass? current = null;
        foreach (var line in ReadLines(path))
        {
            if (line.StartsWith("--- @class ", StringComparison.Ordinal))
            {
                var rest = line[11..].Trim();
                int colon = rest.IndexOf(':');
                string name = colon >= 0 ? rest[..colon] : rest, parent = colon >= 0 ? rest[(colon + 1)..] : "";
                current = new LuaClass(name.Trim(), parent.Trim().Length > 0 ? parent.Trim() : null, []);
                result.Add(current);
            }
            else if (line.StartsWith("--- @field ", StringComparison.Ordinal) && current != null)
            {
                var parts = line[11..].Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2)
                    current.Fields.Add((parts[0], parts[1].TrimStart()));
            }
            else if (!line.StartsWith("---", StringComparison.Ordinal))
                current = null;
        }
        return result;
    }
}
