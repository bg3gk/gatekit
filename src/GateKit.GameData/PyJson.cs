using System.Globalization;
using System.Text;

namespace GateKit.GameData;

/// <summary>
/// JSON text written the way the first version of the index wrote it (Python's
/// <c>json.dumps(d, sort_keys=True)</c>): keys sorted, ", " and ": " separators, every character
/// outside ASCII escaped. Track attributes stored in the database keep one spelling that way.
/// </summary>
public static class PyJson
{
    public static string Dumps(IEnumerable<KeyValuePair<string, string?>> values)
    {
        var sb = new StringBuilder("{");
        var first = true;
        foreach (var (k, v) in values.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!first)
            {
                sb.Append(", ");
            }
            first = false;
            String(sb, k);
            sb.Append(": ");
            if (v == null)
            {
                sb.Append("null");
            }
            else
            {
                String(sb, v);
            }
        }
        return sb.Append('}').ToString();
    }

    /// <summary>A flat object of numbers, in the given order.</summary>
    public static string Numbers(IEnumerable<KeyValuePair<string, double>> values) =>
        "{" + string.Join(", ", values.Select(p =>
        {
            var sb = new StringBuilder();
            String(sb, p.Key);
            return sb + ": " + (p.Value == Math.Floor(p.Value) && Math.Abs(p.Value) < 1e15
                ? ((long)p.Value).ToString(CultureInfo.InvariantCulture)
                : p.Value.ToString("R", CultureInfo.InvariantCulture));
        })) + "}";

    /// <summary>A list of strings.</summary>
    public static string Strings(IEnumerable<string> values) =>
        "[" + string.Join(", ", values.Select(v =>
        {
            var sb = new StringBuilder();
            String(sb, v);
            return sb.ToString();
        })) + "]";

    private static void String(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20 || c > 0x7e)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        sb.Append('"');
    }
}
