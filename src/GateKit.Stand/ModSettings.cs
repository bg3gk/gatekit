using System.Text.RegularExpressions;

namespace GateKit.Stand;

/// <summary>Edits of the game's mod list (PlayerProfiles/Public/modsettings.lsx), as text, so
/// everything the stand does not touch stays byte for byte.</summary>
public static partial class ModSettings
{
    public const string BridgeFolder = "Gwynerva_GateKit";
    public const string BridgeUuid = "b1d5c0de-7e57-4b1d-9e0a-6d6576646b69";
    public const string BridgePak = BridgeFolder + ".pak";

    /// <summary>A mod's entry in the list, as the old stand wrote the bridge's (24-space indent, \n endings).</summary>
    public static string Node(BridgeMod mod) =>
        "                        <node id=\"ModuleShortDesc\">\n" +
        "                            <attribute id=\"Folder\" type=\"LSString\" value=\"" + mod.Folder + "\"/>\n" +
        "                            <attribute id=\"MD5\" type=\"LSString\" value=\"\"/>\n" +
        "                            <attribute id=\"Name\" type=\"LSString\" value=\"" + mod.Name + "\"/>\n" +
        "                            <attribute id=\"PublishHandle\" type=\"uint64\" value=\"0\"/>\n" +
        "                            <attribute id=\"UUID\" type=\"guid\" value=\"" + mod.Uuid + "\"/>\n" +
        "                            <attribute id=\"Version64\" type=\"int64\" value=\"" + mod.Version64 + "\"/>\n" +
        "                        </node>\n";

    /// <summary>The base game: always kept.</summary>
    public static readonly IReadOnlySet<string> BaseMods = new HashSet<string> { "GustavX" };

    [GeneratedRegex("[ \\t]*<node id=\"ModuleShortDesc\">.*?</node>\\r?\\n", RegexOptions.Singleline)]
    private static partial Regex ModNode();

    [GeneratedRegex("id=\"Folder\"[^>]*value=\"([^\"]*)\"")]
    private static partial Regex FolderValue();

    /// <summary>Leaves only the base game and the named mod folders in the list. Throws when a
    /// wanted folder is not in it.</summary>
    public static string Isolate(string text, IEnumerable<string> keep)
    {
        var wanted = new HashSet<string>(BaseMods);
        wanted.UnionWith(keep);
        var found = new HashSet<string>();
        var result = ModNode().Replace(text, match =>
        {
            var folder = FolderValue().Match(match.Value);
            if (folder.Success && wanted.Contains(folder.Groups[1].Value))
            {
                found.Add(folder.Groups[1].Value);
                return match.Value;
            }
            return "";
        });
        var missing = wanted.Except(found).Order(StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
            throw new StandException($"not in the mod list, enable them in the game once first: [{string.Join(", ", missing)}]");
        return result;
    }

    /// <summary>Adds the bridge mods, in their order, at the end of the Mods list. When all are
    /// there the text stays as it is; when only some are, they are taken out and added again,
    /// so a dependency never loads after the mod that needs it.</summary>
    public static string EnableBridge(string text, IReadOnlyList<BridgeMod> mods)
    {
        if (mods.All(m => text.Contains(m.Uuid, StringComparison.OrdinalIgnoreCase))) return text;
        var uuids = mods.Select(m => m.Uuid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        text = ModNode().Replace(text, match =>
            uuids.Any(u => match.Value.Contains(u, StringComparison.OrdinalIgnoreCase)) ? "" : match.Value);
        var last = text.LastIndexOf("</children>", StringComparison.Ordinal);
        var modsEnd = last <= 0 ? -1 : text.LastIndexOf("</children>", last - 1, StringComparison.Ordinal);
        if (modsEnd < 0) throw new StandException("modsettings.lsx does not look as expected; the bridge was not enabled");
        var lineStart = text.LastIndexOf('\n', modsEnd) + 1;
        return text[..lineStart] + string.Concat(mods.Select(Node)) + text[lineStart..];
    }

    /// <summary>The mod folders in the list, in order.</summary>
    public static IReadOnlyList<string> Folders(string text) =>
        ModNode().Matches(text).Select(m => FolderValue().Match(m.Value)).Where(m => m.Success).Select(m => m.Groups[1].Value).ToList();
}
