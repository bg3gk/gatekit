using System.Text;
using System.Text.RegularExpressions;

namespace GateKit.Platform;

/// <summary>Steam's own files: library folders and app manifests (Valve's KeyValues text format).</summary>
public static partial class Steam
{
    /// <summary>The library folders listed in steamapps/libraryfolders.vdf of a Steam root, the root first.</summary>
    public static IReadOnlyList<string> Libraries(string steamRoot)
    {
        var list = new List<string> { steamRoot };
        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
        {
            foreach (var path in LibraryPaths(File.ReadAllText(vdf)))
            {
                if (!list.Any(l => SameFolder(l, path))) list.Add(path);
            }
        }
        return list;
    }

    /// <summary>The "path" values of a libraryfolders.vdf text (backslashes unescaped).</summary>
    public static IReadOnlyList<string> LibraryPaths(string vdfText) =>
        PathEntry().Matches(vdfText).Select(m => Unescape(m.Groups[1].Value)).ToList();

    /// <summary>The value of a top-level key in an app manifest (appmanifest_*.acf), or null.</summary>
    public static string? ManifestValue(string acfText, string key)
    {
        foreach (Match m in KeyValue().Matches(acfText))
        {
            if (string.Equals(m.Groups[1].Value, key, StringComparison.OrdinalIgnoreCase)) return Unescape(m.Groups[2].Value);
        }
        return null;
    }

    /// <summary>Baldur's Gate 3 in this library (a folder holding steamapps), or null.</summary>
    public static string? GameFolderIn(string library)
    {
        var steamapps = Path.Combine(library, "steamapps");
        var dir = GamePlatform.DefaultInstallDir;
        var manifest = Path.Combine(steamapps, $"appmanifest_{GamePlatform.AppId}.acf");
        if (File.Exists(manifest))
        {
            try { dir = ManifestValue(File.ReadAllText(manifest), "installdir") ?? dir; }
            catch (IOException) { }
        }
        var folder = Path.Combine(steamapps, "common", dir);
        return Directory.Exists(Path.Combine(folder, "bin")) ? folder : null;
    }

    /// <summary>Steam's cloud folders for the game, one per Steam account (userdata/*/1086940/remote).</summary>
    public static IReadOnlyList<string> CloudFolders(string steamRoot)
    {
        var userdata = Path.Combine(steamRoot, "userdata");
        if (!Directory.Exists(userdata)) return [];
        return Directory.EnumerateDirectories(userdata)
            .Select(user => Path.Combine(user, GamePlatform.AppId, "remote"))
            .Where(Directory.Exists)
            .ToList();
    }

    static bool SameFolder(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    static string Unescape(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length) i++;
            sb.Append(text[i]);
        }
        return sb.ToString();
    }

    [GeneratedRegex("\"path\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex PathEntry();

    [GeneratedRegex("^\\s*\"([^\"]+)\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Multiline)]
    private static partial Regex KeyValue();
}
