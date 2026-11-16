using System.Security.Cryptography;
using System.Text.Json.Nodes;
using GateKit.Json;

namespace GateKit.Symbols.Registry;

/// <summary>What `registry check` takes from one file: for an entry, its schema's refusals or its id,
/// its members, its typed citations and the verdicts that depend on its content alone; for a text
/// file, its prefixed mentions.</summary>
public sealed class FileFacts
{
    public string? Id { get; set; }
    public HashSet<string> Members { get; } = new(StringComparer.Ordinal);
    public List<(string Kind, string Value, string Pointer)> Typed { get; } = [];
    public List<(string Rule, string Where, string What)> Refusals { get; } = [];
    public List<(string Rule, string Where, string What)> Owed { get; } = [];
    public List<(string Rule, string Where, string What)> Notes { get; } = [];
    public List<(string Kind, string Address, int Line)> Mentions { get; } = [];

    internal JsonObject ToJson()
    {
        static JsonArray Triples(IEnumerable<(string, string, string)> list) =>
            new([.. list.Select(t => (JsonNode)new JsonArray(t.Item1, t.Item2, t.Item3))]);
        var o = new JsonObject();
        if (Id is not null) o["id"] = Id;
        if (Members.Count > 0) o["members"] = new JsonArray([.. Members.Order(StringComparer.Ordinal).Select(m => (JsonNode)m)]);
        if (Typed.Count > 0) o["typed"] = Triples(Typed);
        if (Refusals.Count > 0) o["refusals"] = Triples(Refusals);
        if (Owed.Count > 0) o["owed"] = Triples(Owed);
        if (Notes.Count > 0) o["notes"] = Triples(Notes);
        if (Mentions.Count > 0) o["mentions"] = new JsonArray([.. Mentions.Select(m => (JsonNode)new JsonArray(m.Kind, m.Address, m.Line))]);
        return o;
    }

    internal static FileFacts FromJson(JsonObject o)
    {
        static IEnumerable<(string, string, string)> Triples(JsonNode? node) =>
            (node as JsonArray ?? []).OfType<JsonArray>().Select(t => (JsonText.Text(t[0])!, JsonText.Text(t[1])!, JsonText.Text(t[2])!));
        var facts = new FileFacts { Id = JsonText.Text(o["id"]) };
        foreach (var m in o["members"] as JsonArray ?? []) if (JsonText.Text(m) is { } name) facts.Members.Add(name);
        facts.Typed.AddRange(Triples(o["typed"]));
        facts.Refusals.AddRange(Triples(o["refusals"]));
        facts.Owed.AddRange(Triples(o["owed"]));
        facts.Notes.AddRange(Triples(o["notes"]));
        foreach (var m in (o["mentions"] as JsonArray ?? []).OfType<JsonArray>())
            facts.Mentions.Add((JsonText.Text(m[0])!, JsonText.Text(m[1])!, m[2]!.GetValue<int>()));
        return facts;
    }
}

/// <summary>
/// `registry check`'s memory of the files it read, in the home's cache (one file a checkout): for each
/// file, the SHA-256 of its content and what the check took from it (<see cref="FileFacts"/>). A file
/// whose content is unchanged is not parsed again. The whole memory is dropped when the program's
/// check or the schemas changed (the version of the assemblies that hold them). `--full` reads every
/// file and writes the memory anew; a check without a home keeps none.
/// </summary>
public sealed class CheckCache
{
    readonly string? file;
    readonly bool full;
    sealed record Held(string Hash, long Size, long Time, FileFacts Facts);

    readonly Dictionary<string, Held> before = new(StringComparer.Ordinal);
    readonly Dictionary<string, Held> now = new(StringComparer.Ordinal);

    CheckCache(string? file, bool full)
    {
        this.file = file;
        this.full = full;
        if (full || file is null || !File.Exists(file)) return;
        try
        {
            var root = JsonText.Read(file).AsObject();
            if (JsonText.Text(root["version"]) != Version) return;
            foreach (var (key, value) in root["files"] as JsonObject ?? [])
                if (value is JsonObject held && JsonText.Text(held["hash"]) is { } hash && held["facts"] is JsonObject facts)
                    before[key] = new(hash, held["size"]?.GetValue<long>() ?? -1, held["time"]?.GetValue<long>() ?? -1, FileFacts.FromJson(facts));
        }
        catch (Exception e) when (e is IOException or JsonProblemException or InvalidOperationException or FormatException) { before.Clear(); }
    }

    /// <summary>Every file read, nothing kept.</summary>
    public static CheckCache None() => new(null, full: true);

    /// <summary>The memory of the check of <paramref name="repository"/> in <paramref name="folder"/>; <paramref name="full"/> reads every file and writes it anew.</summary>
    public static CheckCache Open(string folder, string repository, bool full) =>
        new(Path.Combine(folder, Hash(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(repository).ToUpperInvariant()))[..16] + ".json"), full);

    /// <summary>What the check of this build of the program would take from the same content: the
    /// assemblies of the check and of the schemas.</summary>
    public static string Version { get; } =
        typeof(CheckCache).Assembly.ManifestModule.ModuleVersionId + "/" + typeof(JsonText).Assembly.ManifestModule.ModuleVersionId;

    /// <summary>Files read and parsed in this run.</summary>
    public int Read { get; private set; }

    /// <summary>Files whose content was unchanged and whose facts were taken from the memory.</summary>
    public int Kept { get; private set; }

    public string? Where => file;

    /// <summary>The facts of the file <paramref name="info"/> under <paramref name="key"/>: kept when its
    /// content has the hash remembered, else made from its bytes. A file whose size and time of
    /// writing are the ones remembered with the hash is taken to have that content without being read,
    /// as git's index does. Null when it cannot be read.</summary>
    public FileFacts? Of(string key, FileInfo info, Func<byte[], FileFacts> make)
    {
        var size = info.Length;
        var time = info.LastWriteTimeUtc.Ticks;
        if (!full && before.TryGetValue(key, out var held) && held.Size == size && held.Time == time)
        {
            Kept++;
            now[key] = held;
            return held.Facts;
        }
        byte[] bytes;
        try { bytes = File.ReadAllBytes(info.FullName); }
        catch (IOException) { return null; }
        var hash = Hash(bytes);
        changed = true;
        if (!full && before.TryGetValue(key, out held) && held.Hash == hash)
        {
            Kept++;
            now[key] = held with { Size = size, Time = time };
            return held.Facts;
        }
        Read++;
        var facts = make(bytes);
        now[key] = new(hash, size, time, facts);
        return facts;
    }

    bool changed;

    /// <summary>Writes what this run took from the files it met (a file gone is forgotten); nothing
    /// when every file was as remembered.</summary>
    public void Save()
    {
        if (file is null || (!changed && now.Count == before.Count)) return;
        var files = new JsonObject();
        foreach (var (key, held) in now.OrderBy(k => k.Key, StringComparer.Ordinal))
            files[key] = new JsonObject { ["hash"] = held.Hash, ["size"] = held.Size, ["time"] = held.Time, ["facts"] = held.Facts.ToJson() };
        var root = new JsonObject { ["version"] = Version, ["files"] = files };
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(temporary, JsonText.Inline(root), JsonText.Utf8);
        File.Move(temporary, file, overwrite: true);
    }

    static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes))[..32];
}
