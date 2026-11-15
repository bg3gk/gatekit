namespace GateKit.Sweep;

/// <summary>One call written in a stats field: a boost such as <c>AC(2)</c> or a functor such as
/// <c>ApplyStatus(SELF,BURNING,100,2)</c>, with what stood in front of it.</summary>
/// <param name="Name">The boost's or functor's name, as the stats write it.</param>
/// <param name="Arguments">What stands between its parentheses (empty when there are none).</param>
/// <param name="Text">The call alone, without a condition or a target prefix: what AddBoosts takes.</param>
/// <param name="Conditional">An <c>IF(...):</c> stood in front of it.</param>
/// <param name="Prefixes">Target or context prefixes in front of it (<c>TARGET</c>, <c>GROUND</c>, <c>AI_ONLY</c>...).</param>
public sealed record StatsCall(string Name, string Arguments, string Text, bool Conditional, IReadOnlyList<string> Prefixes);

/// <summary>
/// Reads the small languages of the stats files: boost lists (<c>Boosts</c>, <c>DefaultBoosts</c>,
/// <c>BoostsOnEquipMainHand</c>), functor lists (<c>SpellSuccess</c>, <c>OnApplyFunctors</c>,
/// <c>TickFunctors</c>...) and flag lists (<c>StatusPropertyFlags</c>, <c>SpellFlags</c>). Each is
/// a list split by <c>;</c> outside parentheses and quotes; a boost or functor may carry an
/// <c>IF(condition):</c> and, for functors, target prefixes such as <c>TARGET:</c>.
/// </summary>
public static class StatsText
{
    /// <summary>The fields whose values are boost lists.</summary>
    public static readonly IReadOnlyList<string> BoostFields = ["Boosts", "DefaultBoosts", "BoostsOnEquipMainHand", "BoostsOnEquipOffHand"];

    /// <summary>The fields whose values are functor lists.</summary>
    public static readonly IReadOnlyList<string> FunctorFields =
    [
        "SpellProperties", "SpellSuccess", "SpellFail", "OnApplyFunctors", "OnRemoveFunctors", "TickFunctors",
        "OnTickSuccess", "OnTickFail", "OnApplySuccess", "OnApplyFail", "OnSuccess", "StatsFunctors",
        "ToggleOnFunctors", "ToggleOffFunctors", "OriginSpellProperties", "ThrowableSpellProperties", "WeaponFunctors",
    ];

    /// <summary>Splits on <paramref name="separator"/> where no parenthesis is open and no quote either.</summary>
    public static List<string> SplitTop(string text, char separator = ';')
    {
        var parts = new List<string>();
        var depth = 0;
        char quote = '\0';
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '\'' or '"') quote = c;
            else if (c == '(') depth++;
            else if (c == ')') depth = Math.Max(0, depth - 1);
            else if (c == separator && depth == 0)
            {
                parts.Add(text[start..i]);
                start = i + 1;
            }
        }
        parts.Add(text[start..]);
        return [.. parts.Select(p => p.Trim()).Where(p => p.Length > 0)];
    }

    /// <summary>The calls of a boost or functor list; parts that are not a call are left out.</summary>
    public static List<StatsCall> Calls(string? text)
    {
        var calls = new List<StatsCall>();
        if (string.IsNullOrWhiteSpace(text)) return calls;
        foreach (var part in SplitTop(text))
        {
            if (Call(part) is { } call) calls.Add(call);
        }
        return calls;
    }

    /// <summary>One part of a list read as a call: prefixes and a condition stripped, then a name and its arguments.</summary>
    public static StatsCall? Call(string part)
    {
        var rest = part.Trim();
        var conditional = false;
        var prefixes = new List<string>();
        while (rest.Length > 0)
        {
            if (rest.StartsWith("IF(", StringComparison.Ordinal))
            {
                var close = Closing(rest, 2);
                if (close < 0) return null;
                var after = rest[(close + 1)..].TrimStart();
                if (!after.StartsWith(':')) return null;
                rest = after[1..].TrimStart();
                conditional = true;
                continue;
            }
            var name = Identifier(rest);
            // a condition with nothing after it (`IF(Tagged('PLAYABLE'))`) is not a call
            if (name.Length == 0 || name == "IF") return null;
            var tail = rest[name.Length..].TrimStart();
            if (tail.StartsWith(':') && !tail.StartsWith("::", StringComparison.Ordinal))
            {
                prefixes.Add(name);
                rest = tail[1..].TrimStart();
                continue;
            }
            if (tail.Length == 0) return new StatsCall(name, "", name, conditional, prefixes);
            if (!tail.StartsWith('(')) return null;
            var end = Closing(tail, 0);
            if (end < 0) return null;
            var arguments = tail[1..end].Trim();
            return new StatsCall(name, arguments, $"{name}({arguments})", conditional, prefixes);
        }
        return null;
    }

    /// <summary>The flags of a flag list (<c>StatusPropertyFlags</c>, <c>SpellFlags</c>).</summary>
    public static List<string> Flags(string? text) =>
        string.IsNullOrWhiteSpace(text) ? [] : [.. SplitTop(text).Where(f => Identifier(f).Length == f.Length)];

    /// <summary>The leading identifier of a text: a letter or underscore, then letters, digits, underscores.</summary>
    public static string Identifier(string text)
    {
        var i = 0;
        if (i < text.Length && (char.IsAsciiLetter(text[i]) || text[i] == '_'))
        {
            i++;
            while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] == '_')) i++;
        }
        return text[..i];
    }

    /// <summary>The index of the parenthesis that closes the one at <paramref name="open"/>, or -1.</summary>
    static int Closing(string text, int open)
    {
        var depth = 0;
        char quote = '\0';
        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '\'' or '"') quote = c;
            else if (c == '(') depth++;
            else if (c == ')' && --depth == 0) return i;
        }
        return -1;
    }
}
