namespace GateKit.Platform;

/// <summary>
/// File operations the stand needs that work the same on every system: copies that keep the
/// modification time (like Python's copy2), and deletes that never go through a link.
/// </summary>
public static class FileTree
{
    /// <summary>True for a junction or a symbolic link (to a file or a folder), also a dangling one.</summary>
    public static bool IsLink(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.LinkTarget is not null) return true;
            var attributes = info.Attributes;
            return (int)attributes != -1 && (attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Whether anything (file, folder or link) has this name.</summary>
    public static bool Exists(string path) => File.Exists(path) || Directory.Exists(path) || IsLink(path);

    /// <summary>Deletes a file or a folder tree. A link is removed as a link; what it points at stays.</summary>
    public static void RemoveTree(string path)
    {
        if (IsLink(path))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(path);
            else File.Delete(path);
            return;
        }
        if (Directory.Exists(path))
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(path).ToList()) RemoveTree(child);
            Directory.Delete(path);
            return;
        }
        if (File.Exists(path))
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
    }

    /// <summary><see cref="RemoveTree"/> that leaves what it cannot delete (Python's ignore_errors).</summary>
    public static void TryRemoveTree(string path)
    {
        try { RemoveTree(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Copies a file with its modification time; creates the target's folder.</summary>
    public static void CopyFile(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
        File.Copy(source, target, overwrite: true);
        File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source));
    }

    /// <summary>Copies a file unless the target has the same size and modification time (whole seconds).</summary>
    public static bool CopyIfChanged(string source, string target)
    {
        var a = new FileInfo(source);
        var b = new FileInfo(target);
        if (b.Exists && a.Length == b.Length && WholeSeconds(a.LastWriteTimeUtc) == WholeSeconds(b.LastWriteTimeUtc)) return false;
        CopyFile(source, target);
        return true;
    }

    /// <summary>Copies a folder tree; the target must not exist yet (like Python's copytree).</summary>
    public static void CopyTree(string source, string target)
    {
        if (FileTree.Exists(target)) throw new IOException($"{target} already exists");
        Directory.CreateDirectory(target);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var name = Path.GetFileName(entry);
            if (Directory.Exists(entry)) CopyTree(entry, Path.Combine(target, name));
            else CopyFile(entry, Path.Combine(target, name));
        }
    }

    /// <summary>Moves a file or folder, across drives too.</summary>
    public static void Move(string source, string target)
    {
        try
        {
            if (Directory.Exists(source)) Directory.Move(source, target);
            else File.Move(source, target);
        }
        catch (IOException) when (Directory.Exists(source))
        {
            CopyTree(source, target);
            RemoveTree(source);
        }
    }

    /// <summary>Whether two names stand for the same file: a hard link has the size and the
    /// modification time of its file; a replaced file (a game update) does not.</summary>
    public static bool SameFileContent(string a, string b)
    {
        var x = new FileInfo(a);
        var y = new FileInfo(b);
        return x.Exists && y.Exists && x.Length == y.Length && x.LastWriteTimeUtc == y.LastWriteTimeUtc;
    }

    /// <summary>The path with every link on the way resolved (like Python's os.path.realpath).</summary>
    public static string ResolvePath(string path) => Resolve(Path.GetFullPath(path), 0);

    static string Resolve(string full, int depth)
    {
        if (depth > 40) return full;
        var root = Path.GetPathRoot(full) ?? "";
        var parts = full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var i = 0; i < parts.Length; i++)
        {
            current = Path.Combine(current, parts[i]);
            string? target = null;
            try { target = new FileInfo(current).LinkTarget; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            if (target is null) continue;
            var resolved = Path.GetFullPath(target, Path.GetDirectoryName(current) ?? root);
            var rest = string.Join(Path.DirectorySeparatorChar, parts[(i + 1)..]);
            return Resolve(rest.Length > 0 ? Path.Combine(resolved, rest) : resolved, depth + 1);
        }
        return current;
    }

    static long WholeSeconds(DateTime time) => time.Ticks / TimeSpan.TicksPerSecond;
}
