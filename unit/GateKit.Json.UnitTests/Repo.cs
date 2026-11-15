namespace GateKit.Json.UnitTests;

/// <summary>The checkout the tests were built in, and temporary folders that clean up after themselves.</summary>
static class Repo
{
    public static string Root
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "GateKit.slnx"))) return dir.FullName;
            throw new InvalidOperationException("the tests run outside a checkout");
        }
    }

    public static string Schemas => Path.Combine(Root, "schemas");
}

sealed class TempFolder : IDisposable
{
    public string Path { get; } = GateKit.Testing.Scratch.NewPath("json");

    public TempFolder() => Directory.CreateDirectory(Path);

    public string File(string relative) => System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

    public string Write(string relative, string text)
    {
        var path = File(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, text, JsonText.Utf8);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
