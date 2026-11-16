using System.Text.RegularExpressions;
using GateKit.Json;

namespace GateKit.Bridge;

/// <summary>A function of the bridge's Lua: its path under GK, parameters, file, side and comment.</summary>
public sealed record BridgeVerb(string Path, string Parameters, string Where, string Side, string About);

/// <summary>A verb's class: fair, cheat or observation, and why where its name does not say.</summary>
public sealed record VerbClass(string Class, string? Why);

/// <summary>
/// The functions the bridge mod's Lua defines, read from its source: <c>function GK.X(...)</c>,
/// <c>function Alias.X(...)</c> and <c>Alias.X = function(...)</c>, where an alias is a table bound
/// to a part of GK in the same file (<c>local Act = GK.Act</c>, or <c>GK.Probe = Probe</c> after the
/// fact). What bg3_api lists, and what the registry of classes is held to.
/// </summary>
public static partial class BridgeVerbs
{
    /// <summary>Every function under <paramref name="luaRoot"/> (the mod's ScriptExtender/Lua folder), in file order.</summary>
    public static List<BridgeVerb> Read(string luaRoot)
    {
        var found = new List<BridgeVerb>();
        if (!Directory.Exists(luaRoot)) return found;
        foreach (var file in Directory.EnumerateFiles(luaRoot, "*.lua", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var where = System.IO.Path.GetRelativePath(luaRoot, file).Replace('\\', '/');
            var lines = File.ReadAllLines(file);
            // a table bound to GK after its functions were written (`GK.Probe = Probe`)
            var late = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in lines)
                if (LateAlias().Match(line) is { Success: true } m) late[m.Groups[2].Value] = m.Groups[1].Value;
            var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
            var comment = new List<string>();
            foreach (var line in lines)
            {
                var stripped = line.Trim();
                if (stripped.StartsWith("---", StringComparison.Ordinal))
                {
                    comment.Add(stripped[3..].Trim());
                    continue;
                }
                if (AliasLine().Match(line) is { Success: true } alias && alias.Groups[2].Value != "GK")
                    aliases[alias.Groups[1].Value] = alias.Groups[2].Value;
                var match = FunctionLine().Match(line);
                if (!match.Success) match = AssignedLine().Match(line);
                if (match.Success)
                {
                    var path = match.Groups[1].Value;
                    var head = path[..path.IndexOf('.')];
                    string? resolved = head == "GK" ? path
                        : aliases.TryGetValue(head, out var bound) || late.TryGetValue(head, out bound) ? bound + path[head.Length..]
                        : null;
                    // a local table that is not part of GK is private to the file
                    if (resolved is not null)
                        found.Add(new BridgeVerb(resolved, match.Groups[2].Value.Trim(), where, SideOf(where), string.Join(" ", comment)));
                }
                comment.Clear();
            }
        }
        return found;
    }

    /// <summary>The client files whose functions run inside the UI context (lua-api.md, "The files and
    /// what each defines", "(UI)"): bg3_call and bg3_lua reach them with side "ui".</summary>
    static readonly HashSet<string> UiFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Client/UI.lua", "Client/Stage.lua", "Client/Windows.lua", "Client/WindowReaders.lua", "Client/Popups.lua", "Client/Places.lua",
    };

    /// <summary>The side a bridge function is called on, from the file it is written in.</summary>
    public static string SideOf(string where) =>
        where.StartsWith("Server/", StringComparison.OrdinalIgnoreCase) ? "server"
        : where.StartsWith("Shared/", StringComparison.OrdinalIgnoreCase) ? "client or server (each its own copy)"
        : UiFiles.Contains(where) ? "ui"
        : "client";

    [GeneratedRegex(@"^function\s+([A-Za-z_]\w*(?:\.\w+)+)\s*\(([^)]*)\)")]
    private static partial Regex FunctionLine();

    [GeneratedRegex(@"^([A-Za-z_]\w*(?:\.\w+)+)\s*=\s*function\s*\(([^)]*)\)")]
    private static partial Regex AssignedLine();

    /// <summary>`local Act = GK.Act`, with or without a trailing `or {}`.</summary>
    [GeneratedRegex(@"^local\s+([A-Za-z_]\w*)\s*=\s*(GK(?:\.\w+)*)\b")]
    private static partial Regex AliasLine();

    /// <summary>`GK.Probe = Probe`: the whole right side one name.</summary>
    [GeneratedRegex(@"^(GK(?:\.\w+)+)\s*=\s*([A-Za-z_]\w*)\s*$")]
    private static partial Regex LateAlias();
}

/// <summary>
/// The registry of the verbs' classes (the bridge mod's Shared/VerbClasses.json, schema
/// bridge/verbs): the class of every function under GK, by its path (Act.Attack, Key), and of every
/// function of a namespace it names as a whole (Cheat). The mod reads the same file to refuse, inside
/// a step, a verb the step's class does not allow.
/// </summary>
public sealed class VerbClasses
{
    public IReadOnlyDictionary<string, VerbClass> Verbs { get; }
    public IReadOnlyDictionary<string, VerbClass> Namespaces { get; }

    VerbClasses(Dictionary<string, VerbClass> verbs, Dictionary<string, VerbClass> namespaces)
    {
        Verbs = verbs;
        Namespaces = namespaces;
    }

    /// <summary>The name of the registry beside the mod's Lua.</summary>
    public const string File = "Shared/VerbClasses.json";

    static readonly Lazy<VerbClasses> EmbeddedClasses = new(() =>
    {
        using var stream = typeof(VerbClasses).Assembly.GetManifestResourceStream("VerbClasses.json")
            ?? throw new InvalidOperationException("VerbClasses.json is not embedded in GateKit.Bridge");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd(), "VerbClasses.json");
    });

    /// <summary>The registry this build was made with (the mod's file, embedded).</summary>
    public static VerbClasses Embedded => EmbeddedClasses.Value;

    /// <summary>The registry of a mod's Lua folder, else the embedded one.</summary>
    public static VerbClasses Of(string luaRoot)
    {
        var path = System.IO.Path.Combine(luaRoot, File);
        return System.IO.File.Exists(path) ? Parse(System.IO.File.ReadAllText(path), path) : Embedded;
    }

    /// <summary>Reads a registry, held to bridge/verbs.</summary>
    public static VerbClasses Parse(string text, string source)
    {
        var parsed = JsonText.Parse(text, source);
        JsonText.Check(parsed, "bridge/verbs");
        var root = parsed.Root!.AsObject();
        static Dictionary<string, VerbClass> Entries(System.Text.Json.Nodes.JsonNode? node)
        {
            var out_ = new Dictionary<string, VerbClass>(StringComparer.Ordinal);
            if (node is System.Text.Json.Nodes.JsonObject entries)
                foreach (var (name, entry) in entries)
                    out_[name] = new VerbClass((string)entry!["class"]!, JsonText.Text(entry["why"]));
            return out_;
        }
        return new VerbClasses(Entries(root["verbs"]), Entries(root["namespaces"]));
    }

    /// <summary>The class of a path under GK ("Act.Attack" or "GK.Act.Attack"), or null for one the registry does not have.</summary>
    public VerbClass? ClassOf(string path)
    {
        if (path.StartsWith("GK.", StringComparison.Ordinal)) path = path[3..];
        if (Verbs.TryGetValue(path, out var known)) return known;
        var dot = path.LastIndexOf('.');
        return dot > 0 && Namespaces.TryGetValue(path[..dot], out var whole) ? whole : null;
    }

    /// <summary>What does not agree between the registry and the functions: a function it does not
    /// class, and an entry no function has.</summary>
    public (List<string> Unclassed, List<string> Gone) Against(IEnumerable<BridgeVerb> functions)
    {
        var paths = functions.Select(f => f.Path.StartsWith("GK.", StringComparison.Ordinal) ? f.Path[3..] : f.Path)
            .ToHashSet(StringComparer.Ordinal);
        var unclassed = paths.Where(p => ClassOf(p) is null).Order(StringComparer.Ordinal).ToList();
        var gone = Verbs.Keys.Where(p => !paths.Contains(p)).Order(StringComparer.Ordinal).ToList();
        return (unclassed, gone);
    }
}
