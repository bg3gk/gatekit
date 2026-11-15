using GateKit.GameData.Packages;
using LSLib.LS;

namespace GateKit.Mods;

/// <summary>A failure the user can act on: printed as it is, exit code 1.</summary>
public sealed class ModsException(string message) : Exception(message);

/// <summary>One file of a mod: its path inside the mod (forward slashes) and a way to read its bytes.</summary>
public sealed record ModFile(string Path, Func<byte[]> Read);

/// <summary>
/// The files of a mod, from a folder (unpacked, or holding .pak files) or a .pak read directly with
/// GateKit's own package code. Files are only read as data: nothing is executed or loaded, and an
/// archive of another kind (.zip, .7z, .rar) or a binary is left alone. A .pak that cannot be opened
/// is reported, not fatal.
/// </summary>
public sealed class ModSource : IDisposable
{
    readonly List<IDisposable> open = [];
    public List<ModFile> Files { get; } = [];
    public List<(string Path, string Reason)> Unreadable { get; } = [];

    public static ModSource Open(string path)
    {
        var source = new ModSource();
        if (File.Exists(path))
        {
            if (!path.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
                throw new ModsException($"{path} is neither a folder nor a .pak");
            source.AddPackage(path, System.IO.Path.GetFileName(path));
        }
        else if (Directory.Exists(path))
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                var rel = System.IO.Path.GetRelativePath(path, file).Replace('\\', '/');
                if (file.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)) source.AddPackage(file, rel);
                else source.Files.Add(new ModFile(rel, () => File.ReadAllBytes(file)));
            }
        }
        else throw new ModsException($"not found: {path}");
        return source;
    }

    void AddPackage(string file, string label)
    {
        try
        {
            var package = new PackageReader().Read(file);
            open.Add(package);
            foreach (var entry in package.Files.Where(f => !f.IsDeletion()))
            {
                var info = entry;
                Files.Add(new ModFile(label + "!/" + info.Name.Replace('\\', '/'), () => GamePackages.ReadAll(info)));
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Unreadable.Add((label, "the package cannot be read: " + e.Message));
        }
    }

    public void Dispose()
    {
        foreach (var item in open) item.Dispose();
        open.Clear();
    }

    /// <summary>The path as the game would see it, from its first top-level folder of a package
    /// (Mods, Public, Localization, Game, Generated, Engine) on; null when it has none.</summary>
    public static string? PackagePath(string path)
    {
        var inside = path.Contains("!/", StringComparison.Ordinal) ? path[(path.IndexOf("!/", StringComparison.Ordinal) + 2)..] : path;
        var parts = inside.Split('/');
        for (int i = 0; i < parts.Length; i++)
            if (parts[i] is "Mods" or "Public" or "Localization" or "Game" or "Generated" or "Engine")
                return string.Join('/', parts[i..]);
        return null;
    }
}
