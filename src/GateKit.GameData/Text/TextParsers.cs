using System.Text.RegularExpressions;

namespace GateKit.GameData.Text;

/// <summary>A stats entry of a stats file (<c>new entry</c> ... <c>data</c> lines).</summary>
public sealed record StatEntry(string Name, string? Type, string? Parent, Dictionary<string, string> Fields);

/// <summary>An Osiris function of the story header.</summary>
public sealed record OsirisFunction(string Name, string Kind, string Params, string Ids, int Arity);

/// <summary>An Osiris type (alias_type, enum_type) of the story header.</summary>
public sealed record OsirisType(string Name, string Kind, string Definition);

/// <summary>Readers of the text formats: stats, the Osiris story header and story goals.</summary>
public static partial class TextParsers
{
    [GeneratedRegex("^\\s*(new entry|type|using|data)\\s+\"([^\"]*)\"(?:\\s+\"(.*)\")?\\s*$")]
    private static partial Regex StatLine();

    [GeneratedRegex(@"^(event|call|query|syscall|sysquery|sysevent)\s+(\w+)\((.*)\)\s*\(([\d,\s]+)\)\s*$")]
    private static partial Regex OsiHeader();

    [GeneratedRegex(@"\b([A-Za-z_]\w*)\s*\(")]
    private static partial Regex OsiCall();

    private static readonly HashSet<string> OsiNotAName = new(StringComparer.Ordinal)
    {
        "IF", "AND", "NOT", "THEN", "PROC", "QRY", "INTEGER", "INTEGER64", "REAL", "STRING", "GUIDSTRING",
    };

    /// <summary>Each <c>new entry</c> of a stats file with its type, parent and fields.</summary>
    public static IEnumerable<StatEntry> ReadStats(string content)
    {
        StatEntry? entry = null;
        foreach (var line in content.Split('\n'))
        {
            var m = StatLine().Match(line);
            if (!m.Success)
            {
                continue;
            }
            var word = m.Groups[1].Value;
            var a = m.Groups[2].Value;
            var b = m.Groups[3].Success ? m.Groups[3].Value : null;
            if (word == "new entry")
            {
                if (entry != null)
                {
                    yield return entry;
                }
                entry = new StatEntry(a, null, null, new Dictionary<string, string>(StringComparer.Ordinal));
            }
            else if (entry == null)
            {
                continue;
            }
            else if (word == "type")
            {
                entry = entry with { Type = a };
            }
            else if (word == "using")
            {
                entry = entry with { Parent = a };
            }
            else if (b != null)
            {
                entry.Fields[a] = b;
            }
        }
        if (entry != null)
        {
            yield return entry;
        }
    }

    /// <summary>The functions and types of <c>story_header.div</c>.</summary>
    public static (List<OsirisFunction> Functions, List<OsirisType> Types) ReadOsirisHeader(string content)
    {
        var functions = new List<OsirisFunction>();
        var types = new List<OsirisType>();
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            var m = OsiHeader().Match(line);
            if (m.Success)
            {
                var parameters = m.Groups[3].Value;
                functions.Add(new OsirisFunction(m.Groups[2].Value, m.Groups[1].Value, parameters,
                    m.Groups[4].Value.Replace(" ", "", StringComparison.Ordinal),
                    parameters.Split(',').Count(x => x.Trim().Length > 0)));
            }
            else if (line.StartsWith("alias_type", StringComparison.Ordinal) || line.StartsWith("enum_type", StringComparison.Ordinal))
            {
                var space = line.IndexOf(' ', StringComparison.Ordinal);
                var kind = space < 0 ? line : line[..space];
                var rest = space < 0 ? "" : line[(space + 1)..];
                var body = rest.Trim().Trim('{', '}');
                types.Add(new OsirisType(body.Split(',', 2)[0].Trim(), kind, body));
            }
        }
        return (functions, types);
    }

    /// <summary>Counts the names a story goal uses, by role: (name, role) -&gt; uses, in order of first use.</summary>
    public static Dictionary<(string Name, string Role), int> ReadGoal(string content)
    {
        var uses = new Dictionary<(string, string), int>();
        var section = "init";
        string? role = "init";
        foreach (var raw in content.Split('\n'))
        {
            var cut = raw.IndexOf("//", StringComparison.Ordinal);
            var line = (cut >= 0 ? raw[..cut] : raw).Trim();
            if (line.Length == 0)
            {
                continue;
            }
            if (line is "INITSECTION" or "KBSECTION" or "EXITSECTION" or "ENDEXITSECTION")
            {
                section = line switch { "INITSECTION" => "init", "KBSECTION" => "kb", "EXITSECTION" => "exit", _ => "end" };
                role = section != "kb" ? section : null;
                continue;
            }
            if (section == "kb")
            {
                switch (line)
                {
                    case "IF": role = "trigger"; continue;
                    case "PROC" or "QRY": role = "define"; continue;
                    case "AND": role = "condition"; continue;
                    case "THEN": role = "action"; continue;
                }
            }
            if (role == null || section == "end")
            {
                continue;
            }
            foreach (Match m in OsiCall().Matches(line))
            {
                var name = m.Groups[1].Value;
                if (!OsiNotAName.Contains(name) && !IsUpper(name))
                {
                    uses[(name, role)] = uses.GetValueOrDefault((name, role)) + 1;
                }
            }
            if (role is "trigger" or "define")
            {
                role = "condition";
            }
        }
        return uses;
    }

    /// <summary>Python's str.isupper(): at least one cased letter and no lower-case one.</summary>
    private static bool IsUpper(string s)
    {
        var cased = false;
        foreach (var c in s)
        {
            if (char.IsLower(c))
            {
                return false;
            }
            cased |= char.IsUpper(c);
        }
        return cased;
    }

}
