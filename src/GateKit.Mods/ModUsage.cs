using System.Text.Json.Nodes;

namespace GateKit.Mods;

/// <summary>What one mod uses of one engine type.</summary>
public sealed class ComponentUse(string type, string? ext)
{
    public string Type { get; } = type;
    public string? Ext { get; } = ext;
    public bool Read, Write, Create, Subscribe;
    public SortedSet<string> Via { get; } = new(StringComparer.Ordinal);
    public SortedSet<string> Fields { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// The usage document of one mod (<c>gatekit mod usage --json</c>), schema
/// <c>gatekit.mod-usage/1</c>. Every list is sorted, so the same mod gives the same bytes.
/// Facts only: names the mod uses and counts; none of the mod's code or text.
/// </summary>
public sealed class ModUsage
{
    public const string Schema = "gatekit.mod-usage/1";
    /// <summary>Raised whenever what the scanner finds in the same files changes.</summary>
    public const int ScannerVersion = 1;

    public string Source { get; set; } = "";
    public List<(string Name, string? Uuid, string? Version, string? Folder)> Modules { get; } = [];
    public int FilesTotal;
    public SortedDictionary<string, int> FilesRead { get; } = new(StringComparer.Ordinal);
    public List<(string Path, string Reason)> Unreadable { get; } = [];
    public SortedDictionary<string, ComponentUse> Components { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, SortedSet<int>> OsirisCalls { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, SortedSet<int>> OsirisLuaArguments { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, SortedSet<int>> OsirisListeners { get; } = new(StringComparer.Ordinal);
    public SortedSet<string> GoalCalls { get; } = new(StringComparer.Ordinal);
    public SortedSet<string> Api { get; } = new(StringComparer.Ordinal);
    public SortedSet<string> Events { get; } = new(StringComparer.Ordinal);
    public SortedSet<string> NetChannels { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, int> StatsTypes { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, int> Boosts { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, int> Functors { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, SortedSet<string>> Flags { get; } = new(StringComparer.Ordinal);
    public SortedSet<string> StatsFieldsTouched { get; } = new(StringComparer.Ordinal);
    public SortedSet<string> XamlBindings { get; } = new(StringComparer.Ordinal);
    public SortedSet<string> XamlCommands { get; } = new(StringComparer.Ordinal);
    public SortedSet<string> Overrides { get; } = new(StringComparer.Ordinal);
    /// <summary>Whether the game's data index was there to tell overrides and goal calls.</summary>
    public bool HadIndex;

    /// <summary>The mod's name: its module's, or the modules' joined, else the source's.</summary>
    public string Name => Modules.Count switch
    {
        0 => Source,
        1 => Modules[0].Name,
        _ => string.Join("+", Modules.Select(m => m.Name).Distinct().Order(StringComparer.Ordinal)),
    };

    public ComponentUse Component(string type, string? ext)
    {
        if (!Components.TryGetValue(type, out var use)) Components[type] = use = new ComponentUse(type, ext);
        return use;
    }

    public static void Add(SortedDictionary<string, SortedSet<int>> into, string name, int? arity = null)
    {
        if (!into.TryGetValue(name, out var set)) into[name] = set = [];
        if (arity is { } a) set.Add(a);
    }

    static JsonArray Strings(IEnumerable<string> items) => new([.. items.Select(s => (JsonNode)s)]);
    static JsonArray Ints(IEnumerable<int> items) => new([.. items.Select(s => (JsonNode)s)]);
    static JsonObject Counts(SortedDictionary<string, int> map) => new(map.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value)));
    static JsonArray Arities(SortedDictionary<string, SortedSet<int>> map, string key = "arities") =>
        new([.. map.Select(p => (JsonNode)new JsonObject { ["name"] = p.Key, [key] = Ints(p.Value) })]);

    public JsonObject ToJson() => new()
    {
        ["schema"] = Schema,
        ["scanner"] = ScannerVersion,
        ["name"] = Name,
        ["source"] = Source,
        ["modules"] = new JsonArray([.. Modules.Select(m => (JsonNode)new JsonObject { ["name"] = m.Name, ["uuid"] = m.Uuid, ["version"] = m.Version, ["folder"] = m.Folder })]),
        ["files"] = new JsonObject
        {
            ["total"] = FilesTotal,
            ["read"] = new JsonObject(FilesRead.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value))),
            ["unreadable"] = new JsonArray([.. Unreadable.Select(u => (JsonNode)new JsonObject { ["path"] = u.Path, ["reason"] = u.Reason })]),
        },
        ["index"] = HadIndex,
        ["components"] = new JsonArray([.. Components.Values.Select(c => (JsonNode)new JsonObject
        {
            ["type"] = c.Type, ["ext_name"] = c.Ext, ["read"] = c.Read, ["write"] = c.Write, ["create"] = c.Create, ["subscribe"] = c.Subscribe,
            ["via"] = Strings(c.Via), ["fields"] = Strings(c.Fields),
        })]),
        ["osiris"] = new JsonObject
        {
            ["calls"] = new JsonArray([.. OsirisCalls.Select(p => (JsonNode)new JsonObject
            {
                ["name"] = p.Key,
                ["lua_arguments"] = Ints(OsirisLuaArguments.GetValueOrDefault(p.Key) ?? []),
            })]),
            ["listeners"] = Arities(OsirisListeners),
            ["goal_calls"] = Strings(GoalCalls),
        },
        ["api"] = Strings(Api),
        ["events"] = Strings(Events),
        ["net_channels"] = Strings(NetChannels),
        ["stats"] = new JsonObject
        {
            ["entry_types"] = Counts(StatsTypes),
            ["boosts"] = Counts(Boosts),
            ["functors"] = Counts(Functors),
            ["flags"] = new JsonObject(Flags.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)Strings(p.Value)))),
            ["fields_touched_from_lua"] = Strings(StatsFieldsTouched),
        },
        ["xaml"] = new JsonObject { ["bindings"] = Strings(XamlBindings), ["commands"] = Strings(XamlCommands) },
        ["overrides"] = Strings(Overrides),
    };

    static IEnumerable<string> Texts(JsonNode? node) => (node as JsonArray ?? []).Select(n => n?.GetValue<string>()).OfType<string>();
    static IEnumerable<int> Numbers(JsonNode? node) => (node as JsonArray ?? []).Select(n => n?.GetValue<int>() ?? 0);

    /// <summary>A usage document read back; what demand needs of it.</summary>
    public static ModUsage FromJson(JsonObject json, string where)
    {
        if (json["schema"]?.GetValue<string>() != Schema)
            throw new ModsException($"{where} is not a usage document ({Schema}); `gatekit mod usage --json` writes one");
        var usage = new ModUsage { Source = json["source"]?.GetValue<string>() ?? where };
        foreach (var m in (json["modules"] as JsonArray ?? []).OfType<JsonObject>())
            usage.Modules.Add((m["name"]?.GetValue<string>() ?? "?", m["uuid"]?.GetValue<string>(), m["version"]?.GetValue<string>(), m["folder"]?.GetValue<string>()));
        usage.HadIndex = json["index"]?.GetValue<bool>() ?? false;
        foreach (var c in (json["components"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var use = usage.Component(c["type"]!.GetValue<string>(), c["ext_name"]?.GetValue<string>());
            use.Read = c["read"]?.GetValue<bool>() ?? false;
            use.Write = c["write"]?.GetValue<bool>() ?? false;
            use.Create = c["create"]?.GetValue<bool>() ?? false;
            use.Subscribe = c["subscribe"]?.GetValue<bool>() ?? false;
            foreach (var v in Texts(c["via"])) use.Via.Add(v);
            foreach (var f in Texts(c["fields"])) use.Fields.Add(f);
        }
        var osiris = json["osiris"] as JsonObject;
        foreach (var c in (osiris?["calls"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var name = c["name"]!.GetValue<string>();
            Add(usage.OsirisCalls, name);
            foreach (var a in Numbers(c["lua_arguments"])) Add(usage.OsirisLuaArguments, name, a);
        }
        foreach (var c in (osiris?["listeners"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var name = c["name"]!.GetValue<string>();
            Add(usage.OsirisListeners, name);
            foreach (var a in Numbers(c["arities"])) Add(usage.OsirisListeners, name, a);
        }
        foreach (var g in Texts(osiris?["goal_calls"])) usage.GoalCalls.Add(g);
        foreach (var a in Texts(json["api"])) usage.Api.Add(a);
        foreach (var e in Texts(json["events"])) usage.Events.Add(e);
        var stats = json["stats"] as JsonObject;
        foreach (var (k, v) in stats?["boosts"] as JsonObject ?? []) usage.Boosts[k] = v?.GetValue<int>() ?? 0;
        foreach (var (k, v) in stats?["functors"] as JsonObject ?? []) usage.Functors[k] = v?.GetValue<int>() ?? 0;
        return usage;
    }
}
