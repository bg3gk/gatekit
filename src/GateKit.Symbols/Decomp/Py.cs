using System.Globalization;
using System.Text;

namespace GateKit.Symbols.Decomp;

/// <summary>The few Python string behaviours the pseudo-code commands rely on, so that their
/// output is the same text the old Python tool printed.</summary>
internal static class Py
{
    /// <summary>Whitespace as Python's str.split() and str.strip() see it.</summary>
    public static bool IsSpace(char c) => char.IsWhiteSpace(c) || (c >= '\x1c' && c <= '\x1f');

    /// <summary>str.splitlines(): no empty last element for a trailing line break.</summary>
    public static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        int start = 0, i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c is '\n' or '\r' or '\v' or '\f' or '\x1c' or '\x1d' or '\x1e' or '\x85' or (char)0x2028 or (char)0x2029)
            {
                lines.Add(text[start..i]);
                i += c == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
                start = i;
            }
            else
                i++;
        }
        if (start < text.Length)
            lines.Add(text[start..]);
        return lines;
    }

    /// <summary>" ".join(text.split()): runs of whitespace become one space, none at the ends.</summary>
    public static string Collapse(string text)
    {
        var b = new StringBuilder(text.Length);
        bool space = false;
        foreach (char c in text)
        {
            if (IsSpace(c))
            {
                space = b.Length > 0;
                continue;
            }
            if (space)
                b.Append(' ');
            space = false;
            b.Append(c);
        }
        return b.ToString();
    }

    /// <summary>str.strip().</summary>
    public static string Strip(string text)
    {
        int a = 0, z = text.Length;
        while (a < z && IsSpace(text[a]))
            a++;
        while (z > a && IsSpace(text[z - 1]))
            z--;
        return text[a..z];
    }

    /// <summary>text[:n], without cutting a surrogate pair.</summary>
    public static string Left(string text, int n)
    {
        if (text.Length <= n)
            return text;
        if (n > 0 && char.IsHighSurrogate(text[n - 1]))
            n++;
        return text[..n];
    }

    /// <summary>How Python prints a value read from SQLite with str() or an f-string.</summary>
    public static string Str(object? value) => value switch
    {
        null => "None",
        string s => s,
        bool b => b ? "True" : "False",
        double d => Float(d),
        float f => Float(f),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    /// <summary>repr() of a float.</summary>
    public static string Float(double d)
    {
        if (double.IsNaN(d))
            return "nan";
        if (double.IsInfinity(d))
            return d > 0 ? "inf" : "-inf";
        var r = d.ToString("R", CultureInfo.InvariantCulture);
        if (r.Contains('E'))
        {
            int e = r.IndexOf('E');
            string mant = r[..e], exp = r[(e + 1)..];
            char sign = exp[0] == '-' ? '-' : '+';
            exp = exp.TrimStart('+', '-').TrimStart('0');
            return mant + "e" + sign + exp.PadLeft(2, '0');
        }
        return r.Contains('.') ? r : r + ".0";
    }

    /// <summary>repr() of a string.</summary>
    public static string Repr(string s)
    {
        char quote = s.Contains('\'') && !s.Contains('"') ? '"' : '\'';
        var b = new StringBuilder(s.Length + 2).Append(quote);
        foreach (char c in s)
        {
            if (c == quote || c == '\\')
                b.Append('\\').Append(c);
            else if (c == '\n')
                b.Append("\\n");
            else if (c == '\r')
                b.Append("\\r");
            else if (c == '\t')
                b.Append("\\t");
            else if (c < 0x20 || c == 0x7F)
                b.Append("\\x").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
            else
                b.Append(c);
        }
        return b.Append(quote).ToString();
    }

    /// <summary>Number of characters as Python counts them (code points).</summary>
    public static int CodePoints(string s)
    {
        int n = s.Length;
        foreach (char c in s)
            if (char.IsLowSurrogate(c))
                n--;
        return n;
    }

    /// <summary>A hexadecimal number as int(x, 16) reads it; null when it does not fit in 63 bits.</summary>
    public static long? Hex(string text)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            text = text[2..];
        return ulong.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var v) && v <= long.MaxValue ? (long)v : null;
    }

    /// <summary>int(x, 0) for the decimal and 0x forms the pseudo-code prints.</summary>
    public static long? Int0(string text)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return Hex(text);
        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>time.strftime("%Y-%m-%d %H:%M:%S").</summary>
    public static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Sorts file system paths the way Python's pathlib does on this platform.</summary>
    public static IComparer<string> PathOrder { get; } = OperatingSystem.IsWindows()
        ? Comparer<string>.Create((a, b) => string.CompareOrdinal(a.ToLowerInvariant(), b.ToLowerInvariant()))
        : StringComparer.Ordinal;
}
