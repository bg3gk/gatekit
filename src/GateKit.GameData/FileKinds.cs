using System.Text.RegularExpressions;

namespace GateKit.GameData;

/// <summary>What a file of a package is to the index, by its path inside the package.</summary>
public static partial class FileKinds
{
    /// <summary>Text files read whole (and searchable).</summary>
    public static readonly string[] TextExtensions =
    [
        "txt", "lua", "khn", "div", "anc", "ann", "anm", "clc", "cln", "clm", "itemscript", "charscript",
        "gamescript", "xaml", "json", "xml",
    ];

    private static readonly HashSet<string> TextSet = new(TextExtensions, StringComparer.Ordinal);

    /// <summary>Every resource file and every text file. Models, textures, sound, video and effect
    /// graphs are only listed in the file catalog.</summary>
    [GeneratedRegex(@"\.(lsf|lsx|txt|lua|khn|div|anc|ann|anm|clc|cln|clm|itemscript|charscript|gamescript|xaml|json|xml)$", RegexOptions.IgnoreCase)]
    public static partial Regex Indexed();

    /// <summary>Only timelines, dialogs, templates, placed characters and items, speaker groups.</summary>
    [GeneratedRegex(
        @"^(Public/[^/]+/(Timeline/Generated|Generated/GeneratedDialogTimelines)/[^/]+\.lsf" +
        @"|Public/[^/]+/Content/Generated/\[PAK\]_GeneratedDialogTimelines/[^/]+\.lsf" +
        @"|Mods/[^/]+/Story/DialogsBinary/.*\.lsf" +
        @"|Public/[^/]+/RootTemplates/[^/]+\.lsf" +
        @"|Mods/[^/]+/(Globals|Levels)/[^/]+/(Characters|Items)/[^/]+\.lsf" +
        @"|Public/[^/]+/Voice/SpeakerGroups\.ls[fx])$", RegexOptions.IgnoreCase)]
    public static partial Regex ScenesOnly();

    /// <summary>Extension of a file name, lower case, without the dot; "" when it has none.</summary>
    public static string Extension(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        var dot = name.LastIndexOf('.');
        return dot < 0 ? "" : name[(dot + 1)..].ToLowerInvariant();
    }

    private static string LastExtension(string low)
    {
        var dot = low.LastIndexOf('.');
        return dot < 0 ? low : low[(dot + 1)..];
    }

    /// <summary>
    /// Kind of a file by its path inside a package, or null when it is not indexed. Resources are
    /// classified by their .lsx name (a binary .lsf is named as its text form would be).
    /// </summary>
    public static string? Classify(string rel)
    {
        var low = rel.ToLowerInvariant();
        if (!low.EndsWith(".lsx", StringComparison.Ordinal))
        {
            return TextSet.Contains(LastExtension(low)) ? "text" : null;
        }
        if (("/" + low).Contains("/localization/", StringComparison.Ordinal) && low.Contains("/soundbanks/", StringComparison.Ordinal))
        {
            return "voice";
        }
        if (low.Contains("/timeline/generated/", StringComparison.Ordinal) || low.Contains("/generated/generateddialogtimelines/", StringComparison.Ordinal))
        {
            if (low.Contains("/content/", StringComparison.Ordinal))
            {
                return "bank";
            }
            return low.EndsWith("_scene.lsx", StringComparison.Ordinal) ? "scene" : "timeline";
        }
        if (low.Contains("/content/generated/[pak]_generateddialogtimelines/", StringComparison.Ordinal))
        {
            return "bank";
        }
        if (low.Contains("/story/dialogsbinary/", StringComparison.Ordinal))
        {
            return "dialog";
        }
        if (low.Contains("/roottemplates/", StringComparison.Ordinal))
        {
            return "templates";
        }
        if ((low.Contains("/globals/", StringComparison.Ordinal) || low.Contains("/levels/", StringComparison.Ordinal))
            && (low.Contains("/characters/", StringComparison.Ordinal) || low.Contains("/items/", StringComparison.Ordinal)))
        {
            return "entities";
        }
        if (low.EndsWith("/voice/speakergroups.lsx", StringComparison.Ordinal))
        {
            return "speakergroups";
        }
        return "generic";
    }

    /// <summary>Kind of a text file: goal, osiris header, stats, interface, text or script.</summary>
    public static string TextKind(string rel)
    {
        var low = rel.ToLowerInvariant();
        if (low.Contains("/story/rawfiles/goals/", StringComparison.Ordinal))
        {
            return "goal";
        }
        if (low.EndsWith("story_header.div", StringComparison.Ordinal))
        {
            return "osiris header";
        }
        if (low.Contains("/stats/generated/", StringComparison.Ordinal))
        {
            return "stats";
        }
        if (low.EndsWith(".xaml", StringComparison.Ordinal))
        {
            return "interface";
        }
        if (low.EndsWith(".txt", StringComparison.Ordinal) || low.EndsWith(".json", StringComparison.Ordinal) || low.EndsWith(".xml", StringComparison.Ordinal))
        {
            return "text";
        }
        return "script";
    }

    /// <summary>The mod a path belongs to: Mods/X/..., Public/X/... or Generated/?/X/...; "" otherwise.</summary>
    public static string ModOf(string rel)
    {
        var parts = rel.Split('/');
        if (parts[0] is "Mods" or "Public" && parts.Length > 2)
        {
            return parts[1];
        }
        if (parts[0] == "Generated" && parts.Length > 3)
        {
            return parts[2];
        }
        return "";
    }

    /// <summary>Base packages in load order; the rest follow in natural order, Patch* last.</summary>
    public static readonly string[] BasePackages = ["Engine", "Game", "GamePlatform", "Shared", "Gustav", "GustavX"];

    /// <summary>Mods in load order: an object defined again by a later mod replaces the earlier one.</summary>
    public static readonly string[] ModOrder = ["Shared", "SharedDev", "Gustav", "GustavDev", "Honour", "GustavX", "HonourX"];

    public static int ModRank(string mod)
    {
        var i = Array.IndexOf(ModOrder, mod);
        return i >= 0 ? i : ModOrder.Length;
    }

    /// <summary>Package names in load order.</summary>
    public static List<string> PackageOrder(IEnumerable<string> names)
    {
        var all = names.ToList();
        var bases = BasePackages.Where(all.Contains).ToList();
        var patches = all.Where(n => n.StartsWith("patch", StringComparison.OrdinalIgnoreCase)).Order(NaturalComparer.Instance).ToList();
        var rest = all.Where(n => !bases.Contains(n) && !patches.Contains(n)).Order(NaturalComparer.Instance).ToList();
        return [.. bases, .. rest, .. patches];
    }
}

/// <summary>Natural order: digit runs compare as numbers, the rest case-insensitively.</summary>
public sealed partial class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    [GeneratedRegex(@"(\d+)")]
    private static partial Regex Digits();

    public int Compare(string? x, string? y)
    {
        var a = Digits().Split(x ?? "");
        var b = Digits().Split(y ?? "");
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            // Split with a capture group alternates text and numbers: odd positions are numbers.
            int c;
            if (i % 2 == 1)
            {
                c = decimal.Parse(a[i], System.Globalization.CultureInfo.InvariantCulture)
                    .CompareTo(decimal.Parse(b[i], System.Globalization.CultureInfo.InvariantCulture));
            }
            else
            {
                c = string.CompareOrdinal(a[i].ToLowerInvariant(), b[i].ToLowerInvariant());
            }
            if (c != 0)
            {
                return c;
            }
        }
        return a.Length.CompareTo(b.Length);
    }
}
