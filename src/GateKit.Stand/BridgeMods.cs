using System.Text.RegularExpressions;

namespace GateKit.Stand;

/// <summary>One mod the stand installs into every test game (the GateKit bridge, Weave).</summary>
/// <param name="Root">The folder holding Mods/&lt;Folder&gt; (what is packed).</param>
public sealed record BridgeMod(string Root, string Folder, string Uuid, string Name, string Version64, IReadOnlyList<string> Dependencies)
{
    public string Pak => Folder + ".pak";
}

/// <summary>
/// The mods the stand builds, installs and enables: read from their meta.lsx under the bridge
/// source (GATEKIT_BRIDGE, default &lt;repo&gt;/mods). The source is either a mods folder whose
/// subfolders are mod roots (mods/GateKit, mods/Weave) or one mod root (holding Mods/ itself,
/// like the old stand/bridge). Dependencies come first, so Weave is enabled before GateKit.
/// </summary>
public static partial class BridgeMods
{
    public static IReadOnlyList<BridgeMod> Find(string source)
    {
        var roots = Directory.Exists(Path.Combine(source, "Mods"))
            ? [source]
            : Directory.Exists(source)
                ? Directory.EnumerateDirectories(source).Where(d => Directory.Exists(Path.Combine(d, "Mods"))).Order(StringComparer.Ordinal).ToList()
                : [];
        var mods = new List<BridgeMod>();
        foreach (var root in roots)
        {
            foreach (var folder in Directory.EnumerateDirectories(Path.Combine(root, "Mods")).Order(StringComparer.Ordinal))
            {
                var meta = Path.Combine(folder, "meta.lsx");
                if (File.Exists(meta)) mods.Add(Parse(root, File.ReadAllText(meta)));
            }
        }
        if (!mods.Any(m => m.Folder == ModSettings.BridgeFolder))
            throw new StandException($"no bridge mod under {source}: expected Mods/{ModSettings.BridgeFolder}/meta.lsx there or one level down; "
                + "set GATEKIT_BRIDGE to the repository's mods folder");
        return Ordered(mods);
    }

    /// <summary>Reads a mod's identity from the ModuleInfo of its meta.lsx, and its dependencies.</summary>
    public static BridgeMod Parse(string root, string metaText)
    {
        var info = ModuleInfo().Match(metaText);
        if (!info.Success) throw new StandException($"meta.lsx under {root} has no ModuleInfo");
        string Attribute(string text, string id) =>
            Regex.Match(text, $"<attribute id=\"{id}\"[^>]*value=\"([^\"]*)\"").Groups[1].Value;
        var block = info.Value;
        var dependencies = Dependencies().Match(metaText) is { Success: true } deps
            ? UuidValue().Matches(deps.Value).Select(m => m.Groups[1].Value).ToList()
            : [];
        return new BridgeMod(root, Attribute(block, "Folder"), Attribute(block, "UUID"), Attribute(block, "Name"),
            Attribute(block, "Version64") is { Length: > 0 } v ? v : "36028797018963968", dependencies);
    }

    /// <summary>Dependencies before the mods that need them; otherwise by folder name.</summary>
    static IReadOnlyList<BridgeMod> Ordered(List<BridgeMod> mods)
    {
        var done = new List<BridgeMod>();
        var left = mods.OrderBy(m => m.Folder, StringComparer.Ordinal).ToList();
        while (left.Count > 0)
        {
            var next = left.FirstOrDefault(m => m.Dependencies.All(d => !left.Any(o => o.Uuid == d && o != m))) ?? left[0];
            done.Add(next);
            left.Remove(next);
        }
        return done;
    }

    [GeneratedRegex("<node id=\"ModuleInfo\">.*?(?=<children>|</node>)", RegexOptions.Singleline)]
    private static partial Regex ModuleInfo();

    [GeneratedRegex("<node id=\"Dependencies\">.*?</node>\\s*</children>", RegexOptions.Singleline)]
    private static partial Regex Dependencies();

    [GeneratedRegex("<attribute id=\"UUID\"[^>]*value=\"([^\"]*)\"")]
    private static partial Regex UuidValue();
}
