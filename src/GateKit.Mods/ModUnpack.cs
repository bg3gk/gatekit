using GateKit.GameData.Packages;
using LSLib.LS;

namespace GateKit.Mods;

/// <summary>
/// A .pak written out as plain files, to be read. A mod from elsewhere is untrusted data: its
/// files are written and nothing else - nothing is run, loaded or installed - and they never land
/// where a game would load them: not in a game folder (one with a bin/bg3*.exe above it, the
/// installed game and every copy alike) and not in a game profile (one with PlayerProfiles). An
/// entry whose path would leave the target folder is refused.
/// </summary>
public static class ModUnpack
{
    /// <summary>Where a .pak is unpacked when no folder is named: &lt;home&gt;/cache/unpack/&lt;pak name&gt;.</summary>
    public static string DefaultFolder(string pak, GateKit.Home.GateKitHome? home = null) =>
        Path.Combine((home ?? GateKit.Home.GateKitHome.Current).UnpackCache.Require(), Path.GetFileNameWithoutExtension(pak));

    /// <summary>Writes every file of <paramref name="pak"/> under <paramref name="folder"/>, which
    /// must be empty or absent (<paramref name="replace"/> empties it first); returns the full
    /// folder and the number of files.</summary>
    public static (string Folder, int Files) Unpack(string pak, string folder, bool replace = false)
    {
        if (!File.Exists(pak)) throw new ModsException($"not found: {pak}");
        if (!pak.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)) throw new ModsException($"{pak} is not a .pak");
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (WhereAGameLoads(target) is { } why)
            throw new ModsException($"refused: {target} is {why}, where a game would load a mod from; a mod is unpacked to be read, "
                + "never into a game (leave out the folder to unpack into the home's cache)");
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
        {
            if (!replace) throw new ModsException($"{target} is not empty; --replace empties it first");
            Directory.Delete(target, recursive: true);
        }
        Directory.CreateDirectory(target);
        var root = target + Path.DirectorySeparatorChar;
        var count = 0;
        Package package;
        try { package = new PackageReader().Read(pak); }
        catch (Exception e) when (e is not OutOfMemoryException) { throw new ModsException($"{pak}: the package cannot be read: {e.Message}"); }
        using (package)
        {
            foreach (var entry in package.Files.Where(f => !f.IsDeletion()))
            {
                var relative = entry.Name.Replace('\\', '/');
                var path = Path.GetFullPath(Path.Combine(target, relative));
                if (Path.IsPathRooted(relative) || relative.Split('/').Contains("..") || !path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new ModsException($"{pak}: the entry '{entry.Name}' would be written outside {target}; nothing more is unpacked");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, GamePackages.ReadAll(entry));
                count++;
            }
        }
        return (target, count);
    }

    /// <summary>Why a game would load mods from this folder or one above it (null: it would not).</summary>
    public static string? WhereAGameLoads(string folder)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(folder)); current is not null; current = current.Parent)
        {
            if (!current.Exists) continue;
            var bin = Path.Combine(current.FullName, "bin");
            if (Directory.Exists(bin) && Directory.EnumerateFiles(bin, "bg3*.exe").Any()) return $"inside the game folder {current.FullName}";
            if (Directory.Exists(Path.Combine(current.FullName, "PlayerProfiles"))) return $"inside the game profile {current.FullName}";
        }
        return null;
    }
}
