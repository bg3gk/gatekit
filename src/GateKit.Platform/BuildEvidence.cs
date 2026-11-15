using System.Security.Cryptography;

namespace GateKit.Platform;

/// <summary>A file of a reference list that has a given SHA-256: which build, under which path, in which list.</summary>
public sealed record ListedFile(string Build, string? AppId, string Path, string List);

/// <summary>
/// Which build of the game a file is, from its bytes alone: its SHA-256 looked up in every
/// reference list of a folder (<see cref="ReferenceList"/>). A database whose inputs were not
/// recorded when it was built is given its build this way, from the files it was made from.
/// </summary>
public sealed class BuildEvidence
{
    readonly Dictionary<string, List<ListedFile>> bySha = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The reference lists read, by file name; a list that cannot be read is in <see cref="Unreadable"/>.</summary>
    public IReadOnlyList<string> Lists { get; }
    public IReadOnlyList<string> Unreadable { get; }
    public string? Folder { get; }

    BuildEvidence(string? folder, IReadOnlyList<(string File, ReferenceList List)> lists, IReadOnlyList<string> unreadable)
    {
        Folder = folder;
        Lists = [.. lists.Select(l => l.File)];
        Unreadable = unreadable;
        foreach (var (file, list) in lists)
            foreach (var f in list.Files)
            {
                names.Add(System.IO.Path.GetFileName(f.Path));
                if (f.Sha256 is null) continue;
                if (!bySha.TryGetValue(f.Sha256, out var at)) bySha[f.Sha256] = at = [];
                at.Add(new ListedFile(list.BuildId, list.AppId, f.Path, file));
            }
    }

    /// <summary>Every reference list in <paramref name="folder"/> (<c>*.json</c>); none when the folder is null or missing.</summary>
    public static BuildEvidence Load(string? folder)
    {
        var lists = new List<(string, ReferenceList)>();
        var unreadable = new List<string>();
        if (folder is not null && Directory.Exists(folder))
            foreach (var file in Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.Ordinal))
            {
                try { lists.Add((System.IO.Path.GetFileName(file), ReferenceList.Read(file))); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or GateKit.Json.JsonProblemException or InvalidOperationException
                                          or FormatException or NullReferenceException or InvalidCastException)
                {
                    unreadable.Add($"{System.IO.Path.GetFileName(file)}: {e.Message}");
                }
            }
        return new BuildEvidence(folder, lists, unreadable);
    }

    /// <summary>The reference lists of the current home; none without a home.</summary>
    public static BuildEvidence OfHome()
    {
        try { return Load(ReferenceList.FolderIn(GateKit.Home.GateKitHome.Current.Folder)); }
        catch (GateKit.Home.HomeException) { return Load(null); }
    }

    /// <summary>The files of every list with this SHA-256.</summary>
    public IReadOnlyList<ListedFile> Find(string sha256) => bySha.TryGetValue(sha256, out var at) ? at : [];

    /// <summary>Whether a file of this name (the last part of its path) is in any list, whatever its bytes.</summary>
    public bool Names(string fileName) => names.Contains(fileName);

    /// <summary>The SHA-256 of a file, lower-case hex.</summary>
    public static string Sha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>What the lists were, for a sentence: "1 reference list (1086940-25605617.json)" or "no reference list in DIR".</summary>
    public string Describe() => Lists.Count == 0
        ? $"no reference list in {Folder ?? "(no home)"}"
        : $"{Lists.Count} reference list{(Lists.Count == 1 ? "" : "s")} ({string.Join(", ", Lists)})";
}
