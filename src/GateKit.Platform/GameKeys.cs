namespace GateKit.Platform;

/// <summary>Key names the stand understands, as Windows virtual-key codes (Wine uses the same codes).</summary>
public static class GameKeys
{
    public static readonly IReadOnlyDictionary<string, int> Named = BuildNamed();

    static Dictionary<string, int> BuildNamed()
    {
        var keys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["escape"] = 0x1B, ["enter"] = 0x0D, ["space"] = 0x20, ["tab"] = 0x09, ["backspace"] = 0x08,
            ["left"] = 0x25, ["up"] = 0x26, ["right"] = 0x27, ["down"] = 0x28,
        };
        for (var n = 1; n <= 12; n++) keys[$"f{n}"] = 0x6F + n;
        return keys;
    }

    /// <summary>The virtual-key code of a key name or of one letter or digit, or null.</summary>
    public static int? Parse(string name)
    {
        if (Named.TryGetValue(name, out var code)) return code;
        if (name.Length == 1 && char.IsAsciiLetterOrDigit(name[0])) return char.ToUpperInvariant(name[0]);
        return null;
    }

    public static string Known => string.Join(", ", Named.Keys.Order(StringComparer.Ordinal)) + ", or one letter/digit";
}
