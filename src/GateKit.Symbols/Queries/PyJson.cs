using System.Collections;
using System.Globalization;
using System.Text;

namespace GateKit.Symbols.Queries;

/// <summary>json.dumps(value, indent=N) of Python: key order as given, ", " / ": " separators,
/// every character outside ASCII escaped as \uXXXX. The files the export verb writes are then
/// the same bytes the Python tool wrote.</summary>
internal static class PyJson
{
    public static string Dumps(object? value, int indent)
    {
        var b = new StringBuilder();
        Write(b, value, indent, 0);
        return b.ToString();
    }

    static void Write(StringBuilder b, object? value, int indent, int level)
    {
        switch (value)
        {
            case null:
                b.Append("null");
                break;
            case bool v:
                b.Append(v ? "true" : "false");
                break;
            case string s:
                String(b, s);
                break;
            case double d:
                b.Append(Decomp.Py.Float(d));
                break;
            case IEnumerable<KeyValuePair<string, object?>> map:
            {
                var items = map.ToList();
                if (items.Count == 0)
                {
                    b.Append("{}");
                    break;
                }
                b.Append('{');
                for (int i = 0; i < items.Count; i++)
                {
                    b.Append(i == 0 ? "" : ",").Append('\n').Append(' ', indent * (level + 1));
                    String(b, items[i].Key);
                    b.Append(": ");
                    Write(b, items[i].Value, indent, level + 1);
                }
                b.Append('\n').Append(' ', indent * level).Append('}');
                break;
            }
            case IFormattable f:
                b.Append(f.ToString(null, CultureInfo.InvariantCulture));
                break;
            case IEnumerable list:
            {
                var items = list.Cast<object?>().ToList();
                if (items.Count == 0)
                {
                    b.Append("[]");
                    break;
                }
                b.Append('[');
                for (int i = 0; i < items.Count; i++)
                {
                    b.Append(i == 0 ? "" : ",").Append('\n').Append(' ', indent * (level + 1));
                    Write(b, items[i], indent, level + 1);
                }
                b.Append('\n').Append(' ', indent * level).Append(']');
                break;
            }
            default:
                String(b, value.ToString() ?? "");
                break;
        }
    }

    static void String(StringBuilder b, string s)
    {
        b.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': b.Append("\\\""); break;
                case '\\': b.Append("\\\\"); break;
                case '\n': b.Append("\\n"); break;
                case '\r': b.Append("\\r"); break;
                case '\t': b.Append("\\t"); break;
                case '\b': b.Append("\\b"); break;
                case '\f': b.Append("\\f"); break;
                default:
                    if (c < 0x20 || c > 0x7E)
                        b.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        b.Append(c);
                    break;
            }
        }
        b.Append('"');
    }
}
