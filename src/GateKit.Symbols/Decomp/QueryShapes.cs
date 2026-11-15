using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace GateKit.Symbols.Decomp;

/// <summary>A query as the macOS build names it: `ecs::query::spec::Spec&lt;TypeList&lt;...&gt;, ..., tags&gt;`.</summary>
/// <param name="Name">The whole spec as the demangler writes it.</param>
/// <param name="Lists">Its type lists in order, each the component types it holds (without `const`).</param>
/// <param name="Tags">The arguments that are not lists (the query's kind).</param>
internal sealed record QuerySpec(string Name, List<List<string>> Lists, List<string> Tags)
{
    const string Head = "ecs::query::spec::Spec<";

    /// <summary>The arguments of a template at the top level of <paramref name="inner"/>.</summary>
    static List<string> Arguments(string inner)
    {
        var list = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < inner.Length; i++)
        {
            char c = inner[i];
            if (c is '<' or '(')
                depth++;
            else if (c is '>' or ')')
                depth--;
            else if (c == ',' && depth == 0)
            {
                list.Add(inner[start..i].Trim());
                start = i + 1;
            }
        }
        if (inner[start..].Trim() is { Length: > 0 } last)
            list.Add(last);
        return list;
    }

    /// <summary>A type as the ledger writes it: without a trailing or leading `const`.</summary>
    internal static string Plain(string type)
    {
        type = type.Trim();
        if (type.EndsWith(" const", StringComparison.Ordinal))
            type = type[..^6].TrimEnd();
        if (type.StartsWith("const ", StringComparison.Ordinal))
            type = type[6..].TrimStart();
        return type;
    }

    /// <summary>The spec written as <paramref name="name"/>, or null when it is not one.</summary>
    public static QuerySpec? Parse(string name)
    {
        name = name.Trim();
        if (!name.StartsWith(Head, StringComparison.Ordinal) || !name.EndsWith('>'))
            return null;
        var lists = new List<List<string>>();
        var tags = new List<string>();
        foreach (var argument in Arguments(name[Head.Length..^1]))
        {
            const string list = "ls::TypeList<";
            if (argument.StartsWith(list, StringComparison.Ordinal) && argument.EndsWith('>'))
                lists.Add(Arguments(argument[list.Length..^1]).Select(Plain).ToList());
            else
                tags.Add(argument);
        }
        return new QuerySpec(name, lists, tags);
    }

    /// <summary>The spec an index symbol is of (`ls::TypeId&lt;Spec&lt;...&gt;, ecs::QueryRegistry&gt;::m_TypeIndex`), or null.</summary>
    public static string? OfSymbol(string demangled)
    {
        const string head = "ls::TypeId<", tail = ">::m_TypeIndex";
        if (!demangled.StartsWith(head, StringComparison.Ordinal) || !demangled.EndsWith(tail, StringComparison.Ordinal))
            return null;
        var arguments = Arguments(demangled[head.Length..^tail.Length]);
        return arguments.Count == 2 && arguments[1].EndsWith("QueryRegistry", StringComparison.Ordinal) && arguments[0].StartsWith(Head, StringComparison.Ordinal)
            ? arguments[0] : null;
    }

    /// <summary>Every component type of every list, once.</summary>
    public IEnumerable<string> Types => Lists.SelectMany(l => l).Distinct(StringComparer.Ordinal);
}

/// <summary>A query as a Windows build registers it: the component types it stores into the
/// query's description, in the order stored, and the sizes of the description's ten groups.</summary>
/// <param name="Global">The global that holds the query's index.</param>
/// <param name="Counts">The ten group sizes the registration asks for, or null when the call was not read.</param>
/// <param name="Types">The component types in the order stored (a type stands once per group it is in).</param>
internal sealed record QueryRegistration(long Global, int[]? Counts, List<string> Types)
{
    /// <summary>True when the counts were read and add up to the number of types stored.</summary>
    public bool Whole => Counts != null && Counts.Sum() == Types.Count;

    /// <summary>The types of group <paramref name="group"/> (0-based), when the registration is whole.</summary>
    public List<string> Group(int group) => !Whole ? [] : Types.Skip(Counts!.Take(group).Sum()).Take(Counts![group]).ToList();
}

/// <summary>A value the pseudo-code computes as an index times a constant plus a base.</summary>
/// <param name="Target">The local it is assigned to, or empty when it is used inside a larger expression.</param>
/// <param name="Index">The index expression.</param>
/// <param name="Stride">The constant in bytes (a pointer base's element size counted in); -1 when the base is a pointer to a structure.</param>
/// <param name="Base">The base expression.</param>
/// <param name="Offset">True when a constant is added too (the address of a field, not of an element).</param>
/// <param name="At">Where the statement starts in the text the assignments were read from.</param>
internal sealed record StrideValue(string Target, string Index, long Stride, string Base, bool Offset, int At);

/// <summary>
/// What a system's update looks like in pseudo-code where it walks a query, read from the text:
/// the query globals a Windows function reads (an unnamed global times 0x58 plus the world's
/// query table is the query's description), the specs a macOS function mentions, and the values
/// of the shape `index * N + base` (an element of a column of N-byte components, or of any other
/// array). Nothing here knows the engine's layout; the commands of <see cref="QueriesCommand"/>
/// count these shapes and check them against the ledger's sizes.
/// </summary>
internal static partial class QueryShapes
{
    /// <summary>The size of one query description in the world's query table.</summary>
    public const int DescriptionSize = 0x58;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Space();

    /// <summary>The text without any white space: the decompiler breaks long names over lines.</summary>
    public static string Flat(string code) => Space().Replace(code, "");

    [GeneratedRegex(@"\b_?DAT_([0-9a-fA-F]{8,16})\*0x58\b")]
    private static partial Regex GlobalTimesDescription();

    [GeneratedRegex(@"\*0x58\b")]
    private static partial Regex TimesDescription();

    /// <summary>The query globals a Windows function reads, in the order of the text: (place in
    /// the flat text, rva of the global), every mention.</summary>
    public static List<(int At, long Rva)> QueryGlobals(string flat, long imageBase)
    {
        var list = new List<(int, long)>();
        foreach (Match m in GlobalTimesDescription().Matches(flat))
            if (long.TryParse(m.Groups[1].Value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out long address) && address >= imageBase)
                list.Add((m.Index, address - imageBase));
        return list;
    }

    /// <summary>How many multiplications by the description's size the text holds whose operand
    /// is not a global the decompiler left unnamed (a query this reader does not see).</summary>
    public static int OtherDescriptions(string flat) => TimesDescription().Count(flat) - GlobalTimesDescription().Count(flat);

    [GeneratedRegex(@"=TypeIndex<(?<type>[^;=&]+?)>_ComponentTypeIdContext&0x7fff;|\(&\w+(?<counts>(?:,(?:0x[0-9a-fA-F]+|\d+)){10})\)|\b_?DAT_(?<global>[0-9a-fA-F]{8,16})\*0x58\b")]
    private static partial Regex RegistrationEvent();

    /// <summary>The queries a Windows function registers, read from its flat text. A registration
    /// sizes the description's ten groups of component indices (a call with ten constants), stores
    /// the type index of each component in order, and then takes the query's place in the table
    /// (the global times 0x58). Each such stretch is one query: (global, the ten counts or null,
    /// the component types in the order stored). A mention of a global with no store before it is
    /// a use, not a registration, and is left out.</summary>
    public static List<QueryRegistration> Registrations(string flat, long imageBase)
    {
        var list = new List<QueryRegistration>();
        var types = new List<string>();
        int[]? counts = null;
        foreach (Match m in RegistrationEvent().Matches(flat))
        {
            if (m.Groups["type"].Success)
                types.Add(m.Groups["type"].Value);
            else if (m.Groups["counts"].Success)
            {
                counts = m.Groups["counts"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(n => (int)Math.Min(Number(n), int.MaxValue)).ToArray();
                types = [];
            }
            else if (long.TryParse(m.Groups["global"].Value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out long address) && address >= imageBase)
            {
                if (types.Count > 0)
                    list.Add(new QueryRegistration(address - imageBase, counts, types));
                types = [];
                counts = null;
            }
        }
        return list;
    }

    [GeneratedRegex(@"PTR_m_TypeIndex_([0-9a-fA-F]{6,})")]
    private static partial Regex Slot();

    /// <summary>The specs a macOS function mentions, in the order of the text: (place in the flat
    /// text, the spec). A mention is a pointer slot (`PTR_m_TypeIndex_&lt;address&gt;`, resolved
    /// through <paramref name="specOfSlot"/>) or the index's own name, which the flat text spells
    /// without spaces (<paramref name="specOfFlatName"/> gives the spec back).</summary>
    public static List<(int At, string Spec)> SpecMentions(string flat, IReadOnlyDictionary<long, string> specOfSlot, IReadOnlyDictionary<string, string> specOfFlatName)
    {
        var list = new List<(int, string)>();
        if (!flat.Contains("m_TypeIndex", StringComparison.Ordinal))
            return list;
        foreach (Match m in Slot().Matches(flat))
            if (long.TryParse(m.Groups[1].Value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out long address) && specOfSlot.TryGetValue(address, out var spec))
                list.Add((m.Index, spec));
        const string head = "TypeId<ecs::query::spec::Spec<", tail = ">::m_TypeIndex";
        for (int at = flat.IndexOf(head, StringComparison.Ordinal); at >= 0; at = flat.IndexOf(head, at + head.Length, StringComparison.Ordinal))
        {
            int depth = 1, i = at + "TypeId<".Length;
            for (; i < flat.Length && depth > 0 && i - at < 8000; i++)
            {
                char c = flat[i];
                if (c == '<') depth++;
                else if (c == '>') depth--;
                else if (c is ';' or '=' or '{' or '}') break;
            }
            if (depth == 0 && string.CompareOrdinal(flat, i - 1, tail, 0, tail.Length) == 0
                && specOfFlatName.TryGetValue(FlatName("ls::" + flat[at..(i - 1 + tail.Length)]), out var spec))
                list.Add((at, spec));
        }
        list.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return list;
    }

    /// <summary>A name as the flat text spells it, for looking a mention up.</summary>
    public static string FlatName(string name) => PairCommand.Normalize(name);

    /// <summary>The locals a function declares before its first statement: name -> (type, stars).
    /// A declaration is a line `type [*]name [\[n\]];`; the block ends at the first empty line.</summary>
    public static Dictionary<string, (string Type, int Stars)> Declarations(string code)
    {
        var locals = new Dictionary<string, (string, int)>(StringComparer.Ordinal);
        int brace = code.IndexOf('{');
        if (brace < 0)
            return locals;
        bool any = false;
        foreach (var raw in code[(brace + 1)..].Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0)
            {
                if (any)
                    break;
                continue;
            }
            any = true;
            if (!line.EndsWith(';') || line.Contains('=') || line.Contains('('))
                continue;
            line = line[..^1].TrimEnd();
            while (line.EndsWith(']') && line.LastIndexOf(" [", StringComparison.Ordinal) is > 0 and var bracket)
                line = line[..bracket].TrimEnd();
            int space = line.LastIndexOf(' ');
            if (space <= 0)
                continue;
            string name = line[(space + 1)..];
            int stars = name.Length - name.TrimStart('*').Length;
            name = name[stars..];
            if (name.Length > 0 && Identifier().IsMatch(name))
                locals.TryAdd(name, (line[..space].Trim(), stars));
        }
        return locals;
    }

    /// <summary>Bytes of a type the decompiler writes for a scalar; 0 for anything else.</summary>
    internal static int ScalarSize(string type) => type.Split(' ')[^1] switch
    {
        "longlong" or "ulonglong" or "undefined8" or "double" or "code" or "long" or "ulong" => 8,
        "int" or "uint" or "undefined4" or "float" => 4,
        "short" or "ushort" or "undefined2" => 2,
        "char" or "uchar" or "byte" or "undefined1" or "undefined" or "bool" or "void" => 1,
        _ => 0,
    };

    /// <summary>The parts of an expression that its top-level `+` separates.</summary>
    static List<string> Terms(string expression)
    {
        var list = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < expression.Length; i++)
        {
            char c = expression[i];
            if (c is '(' or '[')
                depth++;
            else if (c is ')' or ']')
                depth--;
            else if (c == '+' && depth == 0 && i > 0 && expression[i - 1] == ' ' && i + 1 < expression.Length && expression[i + 1] == ' ')
            {
                list.Add(expression[start..(i - 1)]);
                start = i + 2;
            }
        }
        list.Add(expression[start..]);
        return list;
    }

    /// <summary>Where the parenthesis opened at <paramref name="open"/> closes, or -1.</summary>
    static int Close(string text, int open)
    {
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            if (text[i] == '(')
                depth++;
            else if (text[i] == ')' && --depth == 0)
                return i;
        }
        return -1;
    }

    [GeneratedRegex(@"^\((?<type>[A-Za-z_][\w:<>, ]*?) ?(?<stars>\*+)\)")]
    private static partial Regex PointerCast();

    /// <summary>An expression without the parentheses and the pointer cast around the whole of it.</summary>
    static string Unwrap(string expression)
    {
        expression = expression.Trim();
        while (true)
        {
            if (expression.StartsWith('(') && Close(expression, 0) == expression.Length - 1)
            {
                expression = expression[1..^1].Trim();
                continue;
            }
            var cast = PointerCast().Match(expression);
            if (cast.Success && cast.Length < expression.Length && expression[cast.Length] == '(' && Close(expression, cast.Length) == expression.Length - 1)
            {
                expression = expression[(cast.Length + 1)..^1].Trim();
                continue;
            }
            return expression;
        }
    }

    [GeneratedRegex(@"^(?<index>.+) \* (?<n>0x[0-9a-fA-F]+|\d+)$")]
    private static partial Regex Scaled();

    [GeneratedRegex(@"^(?:0x[0-9a-fA-F]+|\d+)$")]
    private static partial Regex Constant();

    [GeneratedRegex(@"^[A-Za-z_]\w*$")]
    private static partial Regex Identifier();

    [GeneratedRegex(@"^(?<target>[A-Za-z_][\w.]*(?:\[\w+\])?) = (?<value>.+)$", RegexOptions.Singleline)]
    private static partial Regex Assignment();

    static long Number(string text) => text.StartsWith("0x", StringComparison.Ordinal)
        ? long.Parse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) : long.Parse(text, CultureInfo.InvariantCulture);

    /// <summary>`index * N + base` read from one expression, or null: the index, the stride in
    /// bytes, the base, and whether a constant is added too.</summary>
    static (string Index, long Stride, string Base, bool Offset)? Stride(string expression, IReadOnlyDictionary<string, (string Type, int Stars)> locals)
    {
        var terms = Terms(Unwrap(expression));
        if (terms.Count is < 2 or > 3)
            return null;
        int scaled = terms.FindIndex(t => Scaled().IsMatch(t));
        if (scaled < 0 || terms.FindLastIndex(t => Scaled().IsMatch(t)) != scaled)
            return null;
        var rest = terms.Where((_, i) => i != scaled).ToList();
        bool offset = false;
        if (rest.Count == 2)
        {
            int constant = rest.FindIndex(t => Constant().IsMatch(t));
            if (constant < 0)
                return null;
            rest.RemoveAt(constant);
            offset = true;
        }
        string b = rest[0];
        if (Constant().IsMatch(b))
            return null;
        var m = Scaled().Match(terms[scaled]);
        long n = Number(m.Groups["n"].Value);
        // arithmetic on a pointer counts in elements: the base's type says how many bytes one is
        int element = 1;
        var cast = PointerCast().Match(b);
        if (cast.Success && cast.Length < b.Length && (b[cast.Length] != '(' || Close(b, cast.Length) == b.Length - 1))
            element = cast.Groups["stars"].Length > 1 ? 8 : ScalarSize(cast.Groups["type"].Value);
        else if (Identifier().IsMatch(b) && locals.TryGetValue(b, out var declared) && declared.Stars > 0)
            element = declared.Stars > 1 ? 8 : ScalarSize(declared.Type);
        return (m.Groups["index"].Value, element == 0 ? -1 : n * element, b, offset);
    }

    [GeneratedRegex(@"[;{}]")]
    private static partial Regex StatementEnd();

    /// <summary>The values of the shape `index * N + base` that a function assigns to a local
    /// (the whole right side of an assignment), in the order of the text.</summary>
    public static List<StrideValue> Strides(string code)
    {
        var list = new List<StrideValue>();
        var locals = Declarations(code);
        int body = code.IndexOf('{');
        if (body < 0)
            return list;
        string text = Space().Replace(code[body..], " ");
        int start = 0;
        foreach (Match end in StatementEnd().Matches(text))
        {
            string statement = text[start..end.Index].Trim();
            int at = start;
            start = end.Index + 1;
            if (!statement.Contains(" * ", StringComparison.Ordinal) || !statement.Contains(" + ", StringComparison.Ordinal))
                continue;
            var a = Assignment().Match(statement);
            if (!a.Success || a.Groups["value"].Value.Contains(" = ", StringComparison.Ordinal))
                continue;
            if (Stride(a.Groups["value"].Value, locals) is var (index, stride, b, offset))
                list.Add(new StrideValue(a.Groups["target"].Value, index, stride, b, offset, at));
        }
        return list;
    }

    [GeneratedRegex(@"\b(?:local|[a-z]{1,3}Stack)_[0-9a-f]+\b")]
    private static partial Regex StackLocal();

    [GeneratedRegex(@"\b[a-z]{1,3}Var\d+\b")]
    private static partial Regex RegisterLocal();

    [GeneratedRegex(@"\bparam_\d+\b")]
    private static partial Regex Parameter();

    [GeneratedRegex(@"\b_?DAT_[0-9a-fA-F]+\b")]
    private static partial Regex Global();

    /// <summary>True when an index expression is an unnamed global, with or without a cast.</summary>
    public static bool IsGlobal(string index) => Global().Match(index) is { Success: true } m && m.Index + m.Length == index.Length
        && (m.Index == 0 || index[m.Index - 1] == ')');

    /// <summary>The form of a base expression with its locals, parameters and globals replaced
    /// by a letter (S: a stack local, V: another local, P, G), for counting the forms there are.</summary>
    public static string Form(string expression)
    {
        string form = StackLocal().Replace(expression, "S");
        form = RegisterLocal().Replace(form, "V");
        form = Parameter().Replace(form, "P");
        return Global().Replace(form, "G");
    }
}
