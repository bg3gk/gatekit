using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using GateKit.Platform;
using LSLib.LS;
using LSLib.LS.Enums;

namespace GateKit.Copies;

/// <summary>
/// The files a test puts into its copy's game folder for its own time (the header's `files`,
/// docs/writing-tests.md). Each is written only where nothing stands, inside the copy, and recorded
/// in &lt;state&gt;/test-files.json before it is written; <see cref="Remove"/> takes out every one with
/// the folders made for it. A record whose writer is gone - a run killed before it took its files
/// out - is cleaned by the next `test`, `up` and `down` (<see cref="CleanStale"/>).
/// </summary>
public sealed class TestFiles(CopyLayout layout)
{
    public string Record => layout.TestFilesRecord;

    /// <summary>Why <paramref name="destination"/> is not the form of a place in the game folder
    /// (null = it is): relative, its parts joined by `/`, none of them empty, `.` or `..`.</summary>
    public static string? DestinationProblem(string destination)
    {
        var parts = destination.Split('/');
        if (destination.Length > 0 && !destination.Contains('\\') && !destination.Contains(':') && !Path.IsPathRooted(destination)
            && parts.All(p => p.Length > 0 && p != "." && p != ".."))
            return null;
        return $"\"{destination}\" is not a place in the copy's game folder: a relative path, its parts joined by `/`, "
            + "no `.` or `..` (Data/Video/GK_ONE.bk2)";
    }

    /// <summary>The full path <paramref name="destination"/> names in <paramref name="game"/>.</summary>
    public static string Target(string game, string destination)
    {
        if (DestinationProblem(destination) is { } problem) throw new CopyException("files: " + problem);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(game));
        var full = Path.GetFullPath(Path.Combine([root, .. destination.Split('/')]));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new CopyException($"files: {destination} is outside the copy's game folder {root}");
        return full;
    }

    /// <summary>Whether a file is converted on its way into the copy: a source in the text form of
    /// the game's resources (`.lsx`) placed where the game reads the binary form (`.lsf`).</summary>
    public static bool Converts(string source, string destination) =>
        source.EndsWith(".lsx", StringComparison.OrdinalIgnoreCase) && destination.EndsWith(".lsf", StringComparison.OrdinalIgnoreCase);

    /// <summary>The bytes of an LSX resource written as the game's LSF (the version the game's own
    /// files have), with the built-in LSLib the mod packer uses. A source that is not a resource is a
    /// <see cref="CopyException"/> naming it.</summary>
    public static byte[] ToLsf(string source)
    {
        try
        {
            Resource resource;
            using (var input = File.OpenRead(source))
            using (var reader = new LSXReader(input))
                resource = reader.Read();
            using var output = new MemoryStream();
            new LSFWriter(output)
            {
                Version = Game.BaldursGate3.LSFVersion(),
                MetadataFormat = resource.MetadataFormat ?? LSFMetadataFormat.None,
            }.Write(resource);
            return output.ToArray();
        }
        // LSLib's reader wraps any parsing error of the text in a plain Exception with its line and column
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
        {
            throw new CopyException($"files: {source} is not a resource in LSX the LSF writer takes: {e.Message}", e);
        }
    }

    /// <summary>Writes each file (a destination relative to the game folder, a source on the host)
    /// for <paramref name="test"/>; an `.lsx` source placed at an `.lsf` destination is written as
    /// LSF (<see cref="Converts"/>). Everything is checked and converted before anything is written;
    /// on a failure what was written is taken out again. Refused: the installed game, a destination
    /// that exists (through a link too: the game's own file), a place that resolves outside the copy.</summary>
    public void Place(string test, IReadOnlyList<(string Destination, string Source)> files)
    {
        if (layout.Instance is not { } instance)
            throw new CopyException("files: a test's files go into a copy of the game (--instance NAME), never into the installed game; nothing was written");
        var record = File.Exists(Record) ? JsonFiles.ReadObject(Record) : [];
        if (record.Count > 0 && !Mine(record))
            throw new CopyException($"files: {Record} names files of another run that is still going ({Text(record["test"])}); nothing was written");
        var targets = files.Select(f => (Path: Target(layout.Game, f.Destination), f.Source, f.Destination)).ToList();
        var converted = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (path, source, destination) in targets)
        {
            if (!File.Exists(source)) throw new CopyException($"files: no file {source} for {destination}; nothing was written");
            if (FileTree.Exists(path))
                throw new CopyException($"files: {destination} already exists in the copy and no test wrote it; it is never overwritten, nothing was written");
            if (Converts(source, destination))
            {
                try { converted[destination] = ToLsf(source); }
                catch (CopyException e) { throw new CopyException(e.Message + "; nothing was written", e); }
            }
        }
        record["pid"] = Environment.ProcessId;
        record["started"] = Started(Process.GetCurrentProcess());
        record["test"] = test;
        var written = Array(record, "files");
        var folders = Array(record, "folders");
        var linked = Array(record, "linked");
        try
        {
            foreach (var (path, source, destination) in targets)
            {
                var folder = Path.GetDirectoryName(path)!;
                foreach (var (real, stood) in instance.OwnFolderLinked(folder))
                {
                    linked.Add(new JsonObject { ["folder"] = real, ["source"] = stood });
                    JsonFiles.WriteObject(Record, record);
                }
                var existing = folder;
                var missing = new List<string>();
                while (!Directory.Exists(existing))
                {
                    missing.Insert(0, existing);
                    existing = Path.GetDirectoryName(existing)!;
                }
                if (!instance.Inside(existing))
                    throw new CopyException($"files: {destination} would land outside instance {instance.Name} ({FileTree.ResolvePath(existing)})");
                foreach (var made in missing)
                {
                    folders.Add(made);
                    JsonFiles.WriteObject(Record, record);
                    Directory.CreateDirectory(made);
                }
                if (converted.TryGetValue(destination, out var bytes))
                {
                    written.Add(new JsonObject { ["path"] = path, ["length"] = bytes.LongLength, ["sha256"] = Convert.ToHexString(SHA256.HashData(bytes)) });
                    JsonFiles.WriteObject(Record, record);
                    File.WriteAllBytes(path, bytes);
                    continue;
                }
                written.Add(new JsonObject { ["path"] = path, ["length"] = new FileInfo(source).Length, ["sha256"] = Hash(source) });
                JsonFiles.WriteObject(Record, record);
                FileTree.CopyFile(source, path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CopyException)
        {
            var left = Remove();
            var message = e.Message.StartsWith("files: ", StringComparison.Ordinal) ? e.Message : "files: " + e.Message;
            throw new CopyException(message + "; what was written is taken out again" + (left.Count > 0 ? " but for: " + string.Join("; ", left) : ""), e);
        }
    }

    /// <summary>Takes out every file the record names - only while it still holds what was written -
    /// then the folders made for them, when empty, then links again the folders made real. The
    /// record goes when nothing of it is left. Returns what stays, a line each.</summary>
    public IReadOnlyList<string> Remove()
    {
        if (!File.Exists(Record)) return [];
        var record = JsonFiles.ReadObject(Record);
        var left = new List<string>();
        var keptFiles = new JsonArray();
        foreach (var file in Array(record, "files").OfType<JsonObject>())
        {
            if (Text(file["path"]) is not { } path || !File.Exists(path)) continue;
            try
            {
                if (new FileInfo(path).Length != (file["length"]?.GetValue<long>() ?? -1) || Hash(path) != Text(file["sha256"]))
                {
                    left.Add($"{path}: changed since the test wrote it, left alone");
                    continue;
                }
                File.Delete(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                left.Add($"{path}: {e.Message}");
                keptFiles.Add(file.DeepClone());
            }
        }
        var keptFolders = new JsonArray();
        foreach (var folder in Array(record, "folders").Select(Text).OfType<string>().Reverse())
        {
            try
            {
                if (!Directory.Exists(folder) || FileTree.IsLink(folder)) continue;
                if (Directory.EnumerateFileSystemEntries(folder).Any()) { left.Add($"{folder}: not empty, left"); continue; }
                Directory.Delete(folder);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                left.Add($"{folder}: {e.Message}");
                keptFolders.Add(folder);
            }
        }
        var keptLinked = new JsonArray();
        foreach (var made in Array(record, "linked").OfType<JsonObject>().Reverse())
        {
            if (Text(made["folder"]) is not { } folder || Text(made["source"]) is not { } source || layout.Instance is not { } instance) continue;
            try
            {
                if (!instance.LinkAgain(folder, source) && Directory.Exists(folder) && !FileTree.IsLink(folder))
                    left.Add($"{folder}: stays a real folder, something besides the game's own entries is in it");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                left.Add($"{folder}: {e.Message}");
                keptLinked.Add(made.DeepClone());
            }
        }
        try
        {
            if (keptFiles.Count + keptFolders.Count + keptLinked.Count == 0) File.Delete(Record);
            else
            {
                record["files"] = keptFiles;
                record["folders"] = keptFolders;
                record["linked"] = keptLinked;
                JsonFiles.WriteObject(Record, record);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { left.Add($"{Record}: {e.Message}"); }
        return left;
    }

    /// <summary>A record whose writer is gone is cleaned (<see cref="Remove"/>). Returns the line to
    /// say, or null when there was none, or its writer still runs.</summary>
    public string? CleanStale()
    {
        if (!File.Exists(Record)) return null;
        var record = JsonFiles.ReadObject(Record);
        if (Alive(record)) return null;
        var count = Array(record, "files").Count;
        var left = Remove();
        return $"test files: {count} left in the copy by a run of {Text(record["test"]) ?? "a test"} that ended before taking them out, taken out"
            + (left.Count > 0 ? "; left: " + string.Join("; ", left) : "");
    }

    /// <summary>Whether the record is this process's own.</summary>
    static bool Mine(JsonObject record) =>
        record["pid"] is JsonValue pid && pid.TryGetValue<int>(out var id) && id == Environment.ProcessId
        && Text(record["started"]) == Started(Process.GetCurrentProcess());

    /// <summary>Whether the process that wrote the record still runs: its id and its start time.</summary>
    static bool Alive(JsonObject record)
    {
        if (record["pid"] is not JsonValue pid || !pid.TryGetValue<int>(out var id)) return false;
        try
        {
            using var process = Process.GetProcessById(id);
            return Started(process) == Text(record["started"]);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    static string Started(Process process) => process.StartTime.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture);

    static JsonArray Array(JsonObject record, string key)
    {
        if (record[key] is JsonArray array) return array;
        var made = new JsonArray();
        record[key] = made;
        return made;
    }

    static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
