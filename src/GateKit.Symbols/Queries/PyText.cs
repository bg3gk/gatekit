using System.Globalization;
using System.Text;
using GateKit.Symbols.Decomp;

namespace GateKit.Symbols.Queries;

/// <summary>Python's way of printing values (str, repr, dict and list display, format specs), so
/// that the symbol queries print the same text the old Python tool printed.</summary>
internal static class PyText
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>str(value).</summary>
    public static string Str(object? value) => Py.Str(value);

    /// <summary>f"{value:X}": upper-case hexadecimal, a minus sign before negative numbers.</summary>
    public static string Hex(long value) =>
        value < 0 ? "-" + (value == long.MinValue ? "8000000000000000" : (-value).ToString("X", Inv)) : value.ToString("X", Inv);

    /// <summary>f"{value:0NX}".</summary>
    public static string Hex(long value, int width) => value < 0 ? Hex(value) : value.ToString("X" + width.ToString(Inv), Inv);

    /// <summary>f"{text:&lt;n}".</summary>
    public static string Left(object? value, int width) => Str(value).PadRight(width);

    /// <summary>f"{text:&gt;n}".</summary>
    public static string Right(object? value, int width) => Str(value).PadLeft(width);

    /// <summary>text if it has at most <paramref name="max"/> characters, else its first <paramref name="keep"/> and "...".</summary>
    public static string Cut(string text, int max, int keep) => text.Length > max ? Py.Left(text, keep) + "..." : text;

    /// <summary>repr() of a value read from SQLite.</summary>
    public static string Repr(object? value) => value switch
    {
        null => "None",
        string s => Repr(s),
        bool b => b ? "True" : "False",
        double d => Py.Float(d),
        IFormattable f => f.ToString(null, Inv),
        _ => value.ToString() ?? "",
    };

    /// <summary>repr() of a string: the quote Python picks, escapes for what is not printable.</summary>
    public static string Repr(string s)
    {
        char quote = s.Contains('\'') && !s.Contains('"') ? '"' : '\'';
        var b = new StringBuilder(s.Length + 2).Append(quote);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == quote || c == '\\')
                b.Append('\\').Append(c);
            else if (c == '\n')
                b.Append("\\n");
            else if (c == '\r')
                b.Append("\\r");
            else if (c == '\t')
                b.Append("\\t");
            else if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                int cp = char.ConvertToUtf32(c, s[i + 1]);
                if (Printable(CharUnicodeInfo.GetUnicodeCategory(cp)))
                    b.Append(c).Append(s[i + 1]);
                else
                    b.Append("\\U").Append(cp.ToString("x8", Inv));
                i++;
            }
            else if (c < 0x20 || c == 0x7F || (c >= 0x80 && c <= 0xA0))
                b.Append("\\x").Append(((int)c).ToString("x2", Inv));
            else if (c > 0xA0 && (char.IsSurrogate(c) || !Printable(CharUnicodeInfo.GetUnicodeCategory(c))))
                b.Append(c <= 0xFF ? "\\x" + ((int)c).ToString("x2", Inv) : "\\u" + ((int)c).ToString("x4", Inv));
            else
                b.Append(c);
        }
        return b.Append(quote).ToString();
    }

    /// <summary>str.isprintable() for one character category (the space is printable).</summary>
    static bool Printable(UnicodeCategory c) => c is not (UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate
        or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned or UnicodeCategory.LineSeparator
        or UnicodeCategory.ParagraphSeparator or UnicodeCategory.SpaceSeparator);

    /// <summary>str(dict): {'a': 1, 'b': 2}.</summary>
    public static string Dict(IEnumerable<(object? Key, object? Value)> items) =>
        "{" + string.Join(", ", items.Select(p => Repr(p.Key) + ": " + Repr(p.Value))) + "}";

    /// <summary>str(list) of values.</summary>
    public static string List(IEnumerable<object?> items) => "[" + string.Join(", ", items.Select(Repr)) + "]";

    /// <summary>int(text, 0): decimal, 0x / 0o / 0b forms, an optional sign; null where Python raises ValueError.</summary>
    public static long? Int0(string text)
    {
        text = text.Trim();
        bool negative = false;
        if (text.StartsWith('-') || text.StartsWith('+'))
        {
            negative = text[0] == '-';
            text = text[1..];
        }
        text = text.Replace("_", "");
        long? value = null;
        if (text.Length > 2 && text[0] == '0' && (text[1] is 'x' or 'X'))
            value = ulong.TryParse(text[2..], NumberStyles.AllowHexSpecifier, Inv, out var h) ? unchecked((long)h) : null;
        else if (text.Length > 2 && text[0] == '0' && (text[1] is 'o' or 'O'))
            value = TryBase(text[2..], 8);
        else if (text.Length > 2 && text[0] == '0' && (text[1] is 'b' or 'B'))
            value = TryBase(text[2..], 2);
        else if (text.Length > 0 && text.All(char.IsAsciiDigit) && (text.Length == 1 || text[0] != '0' || text.All(c => c == '0')))
            value = long.TryParse(text, NumberStyles.None, Inv, out var d) ? d : null;
        return value == null ? null : negative ? -value : value;
    }

    static long? TryBase(string digits, int radix)
    {
        if (digits.Length == 0)
            return null;
        long v = 0;
        foreach (char c in digits)
        {
            int d = c - '0';
            if (d < 0 || d >= radix)
                return null;
            v = v * radix + d;
        }
        return v;
    }

    /// <summary>The value of an integer column (or null).</summary>
    public static long? Long(object? v) => v == null ? null : Convert.ToInt64(v, Inv);

    /// <summary>The value of an integer column that is never null.</summary>
    public static long L(object? v) => Convert.ToInt64(v, Inv);

    /// <summary>Python truthiness of a value read from SQLite.</summary>
    public static bool Truthy(object? v) => v switch
    {
        null => false,
        string s => s.Length > 0,
        long l => l != 0,
        int i => i != 0,
        double d => d != 0,
        bool b => b,
        byte[] a => a.Length > 0,
        _ => true,
    };
}
