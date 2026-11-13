using System.Globalization;
using System.Text.RegularExpressions;
using LSLib.LS;

namespace GateKit.GameData.Packages;

/// <summary>A package file of the game, by its name without extension.</summary>
public sealed record PackageFile(string Name, string Path)
{
    /// <summary>size:mtime of the file and of its parts; tells whether it changed since the last build.</summary>
    public string Stamp()
    {
        var parts = new List<string>();
        for (var i = 0; ; i++)
        {
            var p = i == 0 ? Path : Package.MakePartFilename(Path, i);
            var info = new FileInfo(p);
            if (!info.Exists)
            {
                break;
            }
            parts.Add(string.Create(CultureInfo.InvariantCulture,
                $"{info.Length}:{new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds()}"));
        }
        return string.Join("+", parts);
    }
}

/// <summary>A file inside a package: its path and size, as the package lists it. Info is null when
/// the package could only be listed, not opened (its contents cannot be read).</summary>
public sealed record PackagedFile(string Path, long Size, PackagedFileInfo? Info)
{
    /// <summary>Whether the bytes of the file can be read.</summary>
    public bool Readable => Info != null && !Info.IsDeletion();
}

/// <summary>Finds the packages of a game's Data folder and opens them. Thread-safe for reading files.</summary>
public sealed partial class GamePackages : IDisposable
{
    private readonly Dictionary<string, Package> _open = [];
    private readonly Lock _lock = new();

    [GeneratedRegex(@"^(.+)_(\d+)$")]
    private static partial Regex PartName();

    /// <summary>The game's Data folder.</summary>
    public string DataFolder { get; }

    public GamePackages(string dataFolder)
    {
        if (!Directory.Exists(dataFolder))
        {
            throw new DirectoryNotFoundException($"game data folder not found: {dataFolder}");
        }
        DataFolder = dataFolder;
    }

    /// <summary>
    /// Packages by name: <c>Data/*.pak</c>, <c>Data/Localization/*.pak</c> and
    /// <c>Data/Localization/&lt;language&gt;/*.pak</c> for each language. Later folders replace a
    /// name found earlier. The extra parts of a large package (<c>Textures_1.pak</c>) are part of it, not packages.
    /// </summary>
    public Dictionary<string, PackageFile> Find(IEnumerable<string> languages)
    {
        var found = new Dictionary<string, PackageFile>(StringComparer.Ordinal);
        var folders = new List<string> { DataFolder, System.IO.Path.Combine(DataFolder, "Localization") };
        folders.AddRange(languages.Select(l => System.IO.Path.Combine(DataFolder, "Localization", l)));
        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }
            var paks = Directory.GetFiles(folder, "*.pak").Where(p => p.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)).ToList();
            var stems = new HashSet<string>(paks.Select(p => System.IO.Path.GetFileNameWithoutExtension(p)), StringComparer.OrdinalIgnoreCase);
            foreach (var pak in paks.Order(StringComparer.Ordinal))
            {
                var stem = System.IO.Path.GetFileNameWithoutExtension(pak);
                var m = PartName().Match(stem);
                if (m.Success && stems.Contains(m.Groups[1].Value))
                {
                    continue;
                }
                found[stem] = new PackageFile(stem, pak);
            }
        }
        return found;
    }

    /// <summary>Opens a package (once) and returns its file list.</summary>
    public Package Open(PackageFile file)
    {
        lock (_lock)
        {
            if (!_open.TryGetValue(file.Path, out var package))
            {
                package = new PackageReader().Read(file.Path);
                _open[file.Path] = package;
            }
            return package;
        }
    }

    /// <summary>
    /// Every file a package lists, sorted by path, with its size. A "solid" package LSLib cannot
    /// open (it rejects one whose file list is not in the order of the data) is still listed.
    /// </summary>
    public List<PackagedFile> List(PackageFile file)
    {
        IEnumerable<PackagedFile> files;
        try
        {
            files = Open(file).Files.Select(f => new PackagedFile(f.Name.Replace('\\', '/'), (long)f.Size(), f));
        }
        catch (InvalidDataException) when (ListOnly(file.Path) is { } listed)
        {
            files = listed;
        }
        return files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
    }

    private sealed class ListedFile : PackagedFileInfoCommon;

    /// <summary>The file list of a BG3 package (v18) without opening its data; null for other versions.</summary>
    internal static List<PackagedFile>? ListOnly(string path)
    {
        PackageHeaderCommon header;
        using (var meta = new PackageReader().Read(path, metadataOnly: true))
        {
            header = meta.Metadata;
        }
        if (header.Version != (uint)LSLib.LS.Enums.PackageVersion.V18)
        {
            return null;
        }
        using var stream = File.OpenRead(path);
        stream.Position = (long)header.FileListOffset;
        using var reader = new BinaryReader(stream);
        var count = reader.ReadInt32();
        var compressed = reader.ReadBytes(reader.ReadInt32());
        var raw = CompressionHelpers.Decompress(compressed, count * System.Runtime.InteropServices.Marshal.SizeOf<FileEntry18>(),
            CompressionFlags.MethodLZ4);
        var entries = new FileEntry18[count];
        using (var list = new BinaryReader(new MemoryStream(raw)))
        {
            BinUtils.ReadStructs(list, entries);
        }
        return entries.Select(e =>
        {
            var info = new ListedFile();
            e.ToCommon(info);
            var size = info.Flags.Method() == CompressionMethod.None ? info.SizeOnDisk : info.UncompressedSize;
            return new PackagedFile(info.Name.Replace('\\', '/'), (long)size, null);
        }).ToList();
    }

    /// <summary>The bytes of a file inside a package.</summary>
    public static byte[] ReadAll(PackagedFileInfo info)
    {
        using var stream = info.CreateContentReader();
        var size = (int)info.Size();
        var buffer = new byte[size];
        var done = 0;
        while (done < size)
        {
            var n = stream.Read(buffer, done, size - done);
            if (n <= 0)
            {
                break;
            }
            done += n;
        }
        return done == size ? buffer : buffer[..done];
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var p in _open.Values)
            {
                p.Dispose();
            }
            _open.Clear();
        }
    }
}
