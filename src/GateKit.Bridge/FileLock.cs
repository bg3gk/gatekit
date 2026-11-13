namespace GateKit.Bridge;

/// <summary>
/// An exclusive lock between processes: a lock file opened with no sharing. Works the same on
/// Windows and Linux (on Linux .NET emulates the sharing mode with advisory locks between .NET
/// processes, which is all the host needs: the game never touches the lock file).
/// </summary>
public sealed class FileLock : IDisposable
{
    readonly FileStream _stream;

    FileLock(FileStream stream) => _stream = stream;

    public static FileLock Acquire(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                return new FileLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(10);
            }
            catch (IOException e)
            {
                throw new IOException($"the lock {Path.GetFileName(path)} stays taken", e);
            }
        }
    }

    public void Dispose() => _stream.Dispose();
}

/// <summary>File operations retried while the game holds the file (Windows sharing violations).</summary>
public static class FileRetry
{
    const int Attempts = 40;
    static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(50);

    public static void Run(Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { action(); return; }
            catch (FileNotFoundException) { throw; }
            catch (DirectoryNotFoundException) { throw; }
            catch (IOException) when (attempt < Attempts) { Thread.Sleep(Pause); }
            catch (UnauthorizedAccessException) when (attempt < Attempts) { Thread.Sleep(Pause); }
        }
    }

    public static T Run<T>(Func<T> action)
    {
        T result = default!;
        Run(() => { result = action(); });
        return result;
    }

    public static string ReadAllText(string path) => Run(() =>
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, BridgeClient.Utf8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    });

    public static byte[] ReadAllBytes(string path) => Run(() =>
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    });

    public static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
