using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GateKit.Home;

/// <summary>
/// A JSON file in the home that several GateKit builds share - two worktrees, two agents, a deck
/// and a command line - written so that none of them loses another's update or reads half a file:
///
/// - every change happens under a cross-process lock (<c>&lt;file&gt;.lock</c>, held open exclusively
///   and deleted when let go of). On Linux an exclusive open is advisory between .NET processes
///   only (it is a flock), which covers GateKit's own builds;
/// - the new text is written beside the file and moved over it, so a reader sees the old file or
///   the new one, never a torn one;
/// - the file carries <c>"version"</c>: the format it is written in. A build that meets a newer
///   format than it understands refuses with a sentence and leaves the file alone; an older file
///   (or one without a version) is upgraded by the first newer build that changes it, once.
/// </summary>
public static class SharedFile
{
    public const string VersionKey = "version";

    static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Takes the file's lock, waiting up to <paramref name="wait"/> (default 10 s) for another process to let go.</summary>
    public static IDisposable Lock(string path, TimeSpan? wait = null)
    {
        var lockPath = Path.GetFullPath(path) + ".lock";
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        var deadline = DateTime.UtcNow + (wait ?? TimeSpan.FromSeconds(10));
        while (true)
        {
            try
            {
                // Deleted when let go of, so no lock file is left beside the files it guards.
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            // On Windows a lock file that its holder is letting go of is "pending delete" for a
            // moment, and opening it then is refused as access denied: the same wait as a held one.
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(20);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new HomeException($"{path} is locked by another GateKit process ({lockPath}) and was not let go of: {e.Message}");
            }
        }
    }

    /// <summary>Writes text beside the file and moves it over the file: all or nothing.</summary>
    public static void WriteAtomic(string path, string text)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary = $"{full}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporary, text, new UTF8Encoding(false));
        try
        {
            File.Move(temporary, full, overwrite: true);
        }
        catch
        {
            try { File.Delete(temporary); } catch (IOException) { }
            throw;
        }
    }

    /// <summary>The file's object (empty when there is none), refused when it is in a newer format than
    /// <paramref name="understood"/>. Reading never rewrites it.</summary>
    public static JsonObject Read(string path, int understood)
    {
        if (!File.Exists(path)) return [];
        JsonObject value;
        try
        {
            value = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonObject ?? [];
        }
        catch (JsonException e)
        {
            throw new HomeException($"{path} is not readable JSON ({e.Message}); it was left as it is");
        }
        var version = VersionOf(value);
        if (version > understood)
            throw new HomeException($"{path} was written by a newer GateKit (format {version}; this build understands up to {understood}): "
                + "update this checkout, or use the build that wrote it. The file was left as it is.");
        return value;
    }

    /// <summary>The format a file's object says it is in; 0 for one written before formats were numbered.</summary>
    public static int VersionOf(JsonObject value) =>
        value[VersionKey] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0;

    /// <summary>Reads, changes and writes the file under its lock, stamping <paramref name="version"/>.
    /// A newer file is refused and left alone; an older one is upgraded by this write.</summary>
    public static JsonObject Update(string path, int version, Action<JsonObject> change)
    {
        using var held = Lock(path);
        var value = Read(path, version);
        change(value);
        value[VersionKey] = version;
        WriteAtomic(path, value.ToJsonString(Indented));
        return value;
    }

    /// <summary>Replaces the file's object under its lock (still refusing to overwrite a newer format).</summary>
    public static void Replace(string path, int version, JsonObject value) => Update(path, version, existing =>
    {
        foreach (var key in existing.Select(p => p.Key).ToList()) existing.Remove(key);
        foreach (var (key, node) in value.ToList())
        {
            value.Remove(key);
            existing[key] = node;
        }
    });
}
