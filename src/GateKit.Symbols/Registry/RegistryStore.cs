using System.Globalization;
using System.Text.Json.Nodes;
using GateKit.Json;

namespace GateKit.Symbols.Registry;

/// <summary>The three kinds of entity the registry holds, each in its own folder with its own schema.</summary>
public enum EntryKind { Function, Type, Global }

/// <summary>One entry as read from its file.</summary>
public sealed record RegistryEntry(EntryKind Kind, string Path, JsonObject Json)
{
    public string Id => JsonText.Text(Json["id"])!;
}

/// <summary>A row of a build table: where an id is in one build of one binary, its size and the hash of its masked body.</summary>
public sealed record BuildRow(string Id, long Rva, long? Size, string? Body)
{
    public JsonObject ToJson()
    {
        var o = new JsonObject { ["id"] = Id, ["rva"] = RegistryStore.Hex(Rva) };
        if (Size is { } size) o["size"] = size;
        if (Body is not null) o["body"] = Body;
        return o;
    }

    public static BuildRow FromJson(JsonObject o) => new(JsonText.Text(o["id"])!, RegistryStore.ParseHex(JsonText.Text(o["rva"])!),
        o["size"]?.GetValue<long>(), JsonText.Text(o["body"]));
}

/// <summary>Which build a folder of tables is of, and the SHA-256 of each binary its tables are of.</summary>
public sealed record BuildInfo(string Build, IReadOnlyDictionary<string, string?> Binaries)
{
    public const string Schema = "registry/build-info/1";

    public JsonObject ToJson() => new()
    {
        ["schema"] = Schema,
        ["build"] = Build,
        ["binaries"] = new JsonArray([.. Binaries.OrderBy(b => b.Key, StringComparer.Ordinal).Select(b =>
        {
            var o = new JsonObject { ["name"] = b.Key };
            if (b.Value is not null) o["sha256"] = b.Value;
            return (JsonNode)o;
        })]),
    };

    public static BuildInfo FromJson(JsonObject o) => new(JsonText.Text(o["build"])!,
        (o["binaries"] as JsonArray ?? []).OfType<JsonObject>().ToDictionary(b => JsonText.Text(b["name"])!, b => JsonText.Text(b["sha256"]), StringComparer.Ordinal));
}

/// <summary>
/// The registry of our reading of the game's code, <c>research/coverage/registry</c>: one file an
/// entity under <c>functions/</c>, <c>types/</c> and <c>globals/</c>, at the path its id gives
/// (<see cref="RegistryId.ToPath"/>), and one table a build and binary under
/// <c>builds/&lt;build&gt;/&lt;binary&gt;.jsonl</c>. One id, one file, across the three kinds; two ids
/// that differ only by case are refused. research/coverage/registry/README.md is the rule.
/// </summary>
public sealed class RegistryStore(string root)
{
    public const string FolderName = "registry";

    public string Root { get; } = System.IO.Path.GetFullPath(root);

    /// <summary>The variable that names the registry's folder outright.</summary>
    public const string Variable = "GATEKIT_REGISTRY";

    /// <summary>The registry beside a coverage record: <c>registry/</c> at the root of the repository
    /// whose <c>research/coverage</c> it is; for a record elsewhere (a test's), <c>registry/</c> inside it.</summary>
    public static RegistryStore Of(string coverage)
    {
        var full = System.IO.Path.GetFullPath(coverage).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        var parent = System.IO.Path.GetDirectoryName(full);
        if (System.IO.Path.GetFileName(full) == "coverage" && parent is not null && System.IO.Path.GetFileName(parent) == "research"
            && System.IO.Path.GetDirectoryName(parent) is { } repository)
            return new(System.IO.Path.Combine(repository, FolderName));
        return new(System.IO.Path.Combine(full, FolderName));
    }

    /// <summary>The registry a command works on: GATEKIT_REGISTRY, else the one beside the coverage
    /// record it finds (<see cref="Decomp.RecordTypes.FindCoverage"/>); null when there is neither.</summary>
    public static RegistryStore? Find(string? coverageGiven)
    {
        if (coverageGiven is null && Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } named) return new(named);
        return Decomp.RecordTypes.FindCoverage(coverageGiven) is { } coverage ? Of(coverage) : null;
    }

    /// <summary>The repository the registry lies in (its parent folder).</summary>
    public string Repository => System.IO.Path.GetDirectoryName(Root)!;

    /// <summary>Whether anything has been written into the registry yet.</summary>
    public bool Exists => Directory.Exists(Root) && Directory.EnumerateFiles(Root, "*.json*", SearchOption.AllDirectories).Any();

    public static string Folder(EntryKind kind) => kind switch
    {
        EntryKind.Function => "functions",
        EntryKind.Type => "types",
        _ => "globals",
    };

    public static string Schema(EntryKind kind) => kind switch
    {
        EntryKind.Function => "registry/function/1",
        EntryKind.Type => "registry/type/1",
        _ => "registry/global/1",
    };

    public const string BuildSchema = "registry/build/1";

    public static EntryKind ParseKind(string text) => text.ToLowerInvariant() switch
    {
        "function" or "functions" => EntryKind.Function,
        "type" or "types" => EntryKind.Type,
        "global" or "globals" => EntryKind.Global,
        _ => throw new RegistryException($"{text}: an entry is a function, a type or a global"),
    };

    public static string Name(EntryKind kind) => kind.ToString().ToLowerInvariant();

    public string PathOf(EntryKind kind, string id) =>
        System.IO.Path.Combine([Root, Folder(kind), .. RegistryId.ToPath(id).Split('/')]);

    public static string Hex(long value) => string.Create(CultureInfo.InvariantCulture, $"0x{value:X}");

    public static long ParseHex(string text) =>
        long.Parse(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    // --- entries ---

    List<RegistryEntry>? cache;

    /// <summary>Every entry, read and held to its schema; a file that does not fit throws with its place.</summary>
    public IReadOnlyList<RegistryEntry> All()
    {
        if (cache is not null) return cache;
        var list = new List<RegistryEntry>();
        foreach (var kind in Enum.GetValues<EntryKind>())
        {
            var folder = System.IO.Path.Combine(Root, Folder(kind));
            if (!Directory.Exists(folder)) continue;
            foreach (var path in Directory.EnumerateFiles(folder, "*.json", SearchOption.AllDirectories)
                         .OrderBy(p => p, StringComparer.Ordinal))
                list.Add(new RegistryEntry(kind, path, JsonText.Read(path, Schema(kind)).AsObject()));
        }
        return cache = list;
    }

    /// <summary>Forgets what was read, so that the next read sees the files again.</summary>
    public void Refresh() => cache = null;

    /// <summary>The entry of an id (an entity's, or the owner of a member), or null.</summary>
    public RegistryEntry? Get(string id)
    {
        var entity = RegistryId.EntityOf(id);
        foreach (var kind in Enum.GetValues<EntryKind>())
        {
            var path = PathOf(kind, entity);
            if (File.Exists(path) && ExactName(path))
                return cache?.FirstOrDefault(e => e.Path == path) ?? new RegistryEntry(kind, path, JsonText.Read(path, Schema(kind)).AsObject());
        }
        return null;
    }

    /// <summary>Whether the file at a path has exactly that name (a file system without case finds it by another case too).</summary>
    static bool ExactName(string path)
    {
        var folder = System.IO.Path.GetDirectoryName(path)!;
        var name = System.IO.Path.GetFileName(path);
        return Directory.EnumerateFiles(folder, name).Any(f => System.IO.Path.GetFileName(f) == name);
    }

    /// <summary>The entries a name finds: the entry of that id, else those that carry it among the names other sources give.</summary>
    public IReadOnlyList<RegistryEntry> Lookup(string name)
    {
        if (RegistryId.IsValid(name) && Get(name) is { } exact) return [exact];
        return All().Where(e => (e.Json["known_as"] as JsonArray ?? []).Any(k => JsonText.Text(k?["name"]) == name)).ToList();
    }

    /// <summary>
    /// Writes an entry at the path of its id, held to its kind's schema. With <paramref name="create"/>
    /// an id that exists is refused, naming the file that holds it; an id that differs from an
    /// existing one only by case is always refused.
    /// </summary>
    public string Write(EntryKind kind, JsonObject entry, bool create)
    {
        var id = JsonText.Text(entry["id"]) ?? throw new RegistryException("an entry carries its id");
        var path = PathOf(kind, id);
        foreach (var other in Enum.GetValues<EntryKind>())
        {
            var there = PathOf(other, id);
            if (File.Exists(there) && ExactName(there))
            {
                if (other != kind) throw new RegistryException($"{id} is already a {Name(other)}: {Relative(there)} holds it");
                if (create) throw new RegistryException($"{id} exists: {Relative(there)} holds it; one id, one file");
            }
            else if (CaseTwin(there) is { } twin)
                throw new RegistryException($"{id} differs only by case from the id of {Relative(twin)}: on a file system without case they would be one file");
        }
        var ordered = new JsonObject { ["schema"] = Schema(kind) };
        foreach (var (key, value) in entry)
            if (key != "schema") ordered[key] = value?.DeepClone();
        JsonText.Write(path, ordered, Schema(kind));
        cache = null;
        return path;
    }

    /// <summary>A file in the same folder whose name equals this one's but for case, or null.</summary>
    static string? CaseTwin(string path)
    {
        var folder = System.IO.Path.GetDirectoryName(path)!;
        if (!Directory.Exists(folder)) return null;
        var name = System.IO.Path.GetFileName(path);
        return Directory.EnumerateFiles(folder).FirstOrDefault(f =>
            string.Equals(System.IO.Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase) && System.IO.Path.GetFileName(f) != name);
    }

    /// <summary>Removes an entry's file and the folders it leaves empty.</summary>
    public void Delete(RegistryEntry entry)
    {
        File.Delete(entry.Path);
        var folder = System.IO.Path.GetDirectoryName(entry.Path);
        var top = System.IO.Path.Combine(Root, Folder(entry.Kind));
        while (folder is not null && folder.Length > top.Length && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
        {
            Directory.Delete(folder);
            folder = System.IO.Path.GetDirectoryName(folder);
        }
        cache = null;
    }

    public string Relative(string path) => System.IO.Path.GetRelativePath(Root, path).Replace('\\', '/');

    // --- build tables ---

    public string BuildsFolder => System.IO.Path.Combine(Root, "builds");

    /// <summary>The folder of one binary's tables in one build.</summary>
    public string TableFolder(string build, string binary) => System.IO.Path.Combine(BuildsFolder, build, binary);

    /// <summary>The shard of a table an id's row lives in: the top level of the id's scope.</summary>
    public string ShardPath(string build, string binary, string id) =>
        System.IO.Path.Combine(TableFolder(build, binary), RegistryId.EscapePart(RegistryId.Parse(id).Scope[0]) + ".jsonl");

    public string InfoPath(string build) => System.IO.Path.Combine(BuildsFolder, build, "build.json");

    /// <summary>The (build, binary) of every table there is.</summary>
    public IReadOnlyList<(string Build, string Binary)> Tables()
    {
        if (!Directory.Exists(BuildsFolder)) return [];
        return Directory.EnumerateDirectories(BuildsFolder).OrderBy(d => d, StringComparer.Ordinal)
            .SelectMany(d => Directory.EnumerateDirectories(d).OrderBy(f => f, StringComparer.Ordinal)
                .Select(f => (System.IO.Path.GetFileName(d), System.IO.Path.GetFileName(f)))).ToList();
    }

    /// <summary>The shards of one table, by path.</summary>
    public IReadOnlyList<string> Shards(string build, string binary)
    {
        var folder = TableFolder(build, binary);
        return Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.jsonl").Order(StringComparer.Ordinal).ToList() : [];
    }

    /// <summary>The rows of one table, every shard; none when it is not there.</summary>
    public List<BuildRow> Table(string build, string binary) =>
        Shards(build, binary).SelectMany(s => JsonText.ReadLines(s, BuildSchema)).Select(BuildRow.FromJson).ToList();

    /// <summary>The build's record of itself, or null.</summary>
    public BuildInfo? Info(string build) => JsonText.ReadIfExists(InfoPath(build), BuildInfo.Schema) is JsonObject o ? BuildInfo.FromJson(o) : null;

    /// <summary>Records the SHA-256 of a binary a build's tables are of; a different one already recorded is refused.</summary>
    public void RecordBinary(string build, string binary, string? sha256)
    {
        var info = Info(build);
        var binaries = new Dictionary<string, string?>(info?.Binaries ?? new Dictionary<string, string?>(), StringComparer.Ordinal);
        if (binaries.TryGetValue(binary, out var known) && known is not null && sha256 is not null && known != sha256)
            throw new RegistryException($"build {build} of {binary} is recorded with SHA-256 {known}, and these bytes are {sha256}: not the same file");
        if (binaries.TryGetValue(binary, out known) && (known == sha256 || sha256 is null)) return;
        binaries[binary] = sha256;
        JsonText.Write(InfoPath(build), new BuildInfo(build, binaries).ToJson(), BuildInfo.Schema);
    }

    /// <summary>Writes a table whole, one shard a top-level scope, each sorted by id; an id twice is refused.</summary>
    public void WriteTable(string build, string binary, IEnumerable<BuildRow> rows)
    {
        var sorted = rows.OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
        for (var i = 1; i < sorted.Count; i++)
            if (sorted[i].Id == sorted[i - 1].Id)
                throw new RegistryException($"{sorted[i].Id} twice in the table of {binary} of build {build}: an id has one place in a build");
        var shards = sorted.GroupBy(r => ShardPath(build, binary, r.Id)).ToDictionary(g => g.Key, g => g.ToList());
        using var held = JsonLock.Take(TableFolder(build, binary));
        foreach (var old in Shards(build, binary).Where(s => !shards.ContainsKey(s))) File.Delete(old);
        foreach (var (path, list) in shards)
            JsonText.WriteLines(path, list.Select(r => (JsonNode)r.ToJson()), BuildSchema);
        if (Info(build)?.Binaries.ContainsKey(binary) != true) RecordBinary(build, binary, null);
    }

    /// <summary>
    /// Puts a table that a merge of two branches left untidy back in order: every shard read line by
    /// line (a conflict's marker lines are dropped), rows sorted, a row written twice kept once, each
    /// row in the shard of its id. Two different rows for one id are refused, naming both. Returns the
    /// rows written.
    /// </summary>
    public int Mend(string build, string binary, TextWriter output)
    {
        var rows = new List<BuildRow>();
        foreach (var shard in Shards(build, binary))
        {
            var number = 0;
            foreach (var line in File.ReadAllLines(shard))
            {
                number++;
                if (line.StartsWith("<<<<<<<", StringComparison.Ordinal) || line.StartsWith("=======", StringComparison.Ordinal)
                    || line.StartsWith(">>>>>>>", StringComparison.Ordinal) || line.StartsWith("|||||||", StringComparison.Ordinal) || line.Trim().Length == 0)
                {
                    output.WriteLine($"{Relative(shard)}:{number}: a merge's marker or an empty line, dropped");
                    continue;
                }
                var parsed = JsonText.Parse(line, shard, number);
                JsonText.Check(parsed, BuildSchema);
                rows.Add(BuildRow.FromJson(parsed.Root!.AsObject()));
            }
        }
        var kept = new List<BuildRow>();
        foreach (var group in rows.GroupBy(r => r.Id))
        {
            var distinct = group.Distinct().ToList();
            if (distinct.Count > 1)
                throw new RegistryException($"{group.Key} has {distinct.Count} different rows in build {build} of {binary}: "
                    + string.Join("; ", distinct.Select(r => JsonText.Inline(r.ToJson()))) + " - one id has one place; keep the right one by hand");
            kept.Add(distinct[0]);
        }
        WriteTable(build, binary, kept);
        return kept.Count;
    }

    /// <summary>Puts one row into a table, replacing the id's earlier row.</summary>
    public void Place(string build, string binary, BuildRow row)
    {
        var rows = Table(build, binary);
        rows.RemoveAll(r => r.Id == row.Id);
        rows.Add(row);
        WriteTable(build, binary, rows);
    }

    /// <summary>The ids at an address of one binary, in every build that has a table for it (or only <paramref name="build"/>).</summary>
    public IReadOnlyList<(string Build, BuildRow Row)> At(string binary, long rva, string? build = null) =>
        Tables().Where(t => string.Equals(t.Binary, binary, StringComparison.OrdinalIgnoreCase) && (build is null || t.Build == build))
            .SelectMany(t => Table(t.Build, t.Binary).Where(r => r.Rva == rva || (r.Size is { } size && rva > r.Rva && rva < r.Rva + size)).Select(r => (t.Build, r)))
            .ToList();

    /// <summary>Where an id is, in every table.</summary>
    public IReadOnlyList<(string Build, string Binary, BuildRow Row)> Places(string id) =>
        Tables().SelectMany(t => Table(t.Build, t.Binary).Where(r => r.Id == id).Select(r => (t.Build, t.Binary, r))).ToList();

    /// <summary>The build a table of <paramref name="binary"/> is read from: the one given when it has a table, else the only one that has.</summary>
    public string? BuildFor(string binary, string? preferred)
    {
        var builds = Tables().Where(t => string.Equals(t.Binary, binary, StringComparison.OrdinalIgnoreCase)).Select(t => t.Build).ToList();
        if (preferred is not null && builds.Contains(preferred)) return preferred;
        return builds.Count == 1 ? builds[0] : null;
    }
}
