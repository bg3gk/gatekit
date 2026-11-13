using System.Globalization;
using System.Text;

namespace GateKit.GameData.Resources;

/// <summary>
/// Numbers of binary resources as text: the fewest digits that read back as the same number, in
/// plain notation, never with an exponent (0.00000011920929, not 1.1920929E-07). When two
/// spellings of that length both read back, the nearer one wins, and an exact tie goes away from
/// zero (341.953125 is 341.95313). The first index wrote numbers this way.
/// </summary>
public static class NumberText
{
    public static string Float(float v) =>
        float.IsFinite(v) ? Format(v.ToString("R", CultureInfo.InvariantCulture), v, s => float.Parse(s, CultureInfo.InvariantCulture) == v)
                          : v.ToString(CultureInfo.InvariantCulture);

    public static string Double(double v) =>
        double.IsFinite(v) ? Format(v.ToString("R", CultureInfo.InvariantCulture), v, s => double.Parse(s, CultureInfo.InvariantCulture) == v)
                           : v.ToString(CultureInfo.InvariantCulture);

    private static string Format(string shortest, double exact, Func<string, bool> readsBack)
    {
        var plain = Plain(shortest);
        var dot = plain.IndexOf('.', StringComparison.Ordinal);
        // .NET breaks a tie between two shortest spellings towards the even last digit. When the
        // last digit is even and the next spelling up also reads back, decide from the exact value.
        if (dot < 0 || (plain[^1] - '0') % 2 != 0 || !readsBack(Increment(plain)))
        {
            return plain;
        }
        var decimals = plain.Length - dot - 1;
        var full = Math.Abs(exact).ToString("F" + (decimals + 60).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        var cut = full.IndexOf('.', StringComparison.Ordinal) + decimals + 1;
        var rounded = full[..cut];
        if (full[cut] >= '5')
        {
            rounded = Increment(rounded);
        }
        rounded = (exact < 0 ? "-" : "") + rounded;
        return readsBack(rounded) ? rounded : plain;
    }

    /// <summary>Adds one to the last digit of a plain number (by magnitude), carrying as needed.</summary>
    private static string Increment(string plain)
    {
        var chars = plain.ToCharArray();
        for (var i = chars.Length - 1; i >= 0; i--)
        {
            if (!char.IsAsciiDigit(chars[i]))
            {
                continue;
            }
            if (chars[i] != '9')
            {
                chars[i]++;
                return new string(chars);
            }
            chars[i] = '0';
        }
        var sign = plain.StartsWith('-') ? 1 : 0;
        return new string(chars).Insert(sign, "1");
    }

    /// <summary>A number in G or R form rewritten without an exponent.</summary>
    internal static string Plain(string s)
    {
        var e = s.IndexOfAny(['E', 'e']);
        if (e < 0)
        {
            return s;
        }
        var exponent = int.Parse(s[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var mantissa = s[..e];
        var negative = mantissa.StartsWith('-');
        if (negative)
        {
            mantissa = mantissa[1..];
        }
        var dot = mantissa.IndexOf('.', StringComparison.Ordinal);
        var digits = dot < 0 ? mantissa : mantissa.Remove(dot, 1);
        var point = (dot < 0 ? mantissa.Length : dot) + exponent;    // position of the decimal point in digits
        var sb = new StringBuilder();
        if (negative)
        {
            sb.Append('-');
        }
        if (point <= 0)
        {
            sb.Append("0.").Append('0', -point).Append(digits);
        }
        else if (point >= digits.Length)
        {
            sb.Append(digits).Append('0', point - digits.Length);
        }
        else
        {
            sb.Append(digits, 0, point).Append('.').Append(digits, point, digits.Length - point);
        }
        return sb.ToString();
    }
}
