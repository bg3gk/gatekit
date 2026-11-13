using System.Text.Json;
using System.Text.Json.Nodes;
using GateKit.Stand;
using ModelContextProtocol.Protocol;

namespace GateKit.Mcp;

/// <summary>
/// Tools a modder or an agent adds without rebuilding the server: one JSON file per tool (or an
/// array of them) in the tools folder (GATEKIT_MCP_TOOLS, by default &lt;repo&gt;/mcp-tools). The
/// folder is read again whenever a file changes, exactly as the old server re-imported
/// `stand/mcp_tools/*.py` by its modification time, so a new tool is live on the next tools/list.
///
/// A tool is the description an agent reads plus one bridge call:
/// <code>
/// {
///   "name": "bg3_gold",
///   "description": "How much gold the party carries.",
///   "inputSchema": {"type": "object", "properties": {"who": {"type": "string"}}},
///   "side": "server",
///   "function": "Character",
///   "args": ["$who"]
/// }
/// </code>
/// or, for more than one call, a Lua chunk with the call's arguments in the table `args`:
/// <code>
/// { "name": "bg3_gold", "description": "...", "side": "server",
///   "lua": "local who = GK.Character(args.who); return who.Gold" }
/// </code>
/// A file that is wrong never stops the server: its error is listed by `bg3_plugin_errors`, which
/// appears among the tools only while something failed.
/// </summary>
public sealed class PluginTools
{
    readonly ToolHost _host;
    readonly Dictionary<string, (DateTime Stamp, IReadOnlyList<GateKitTool> Tools)> _loaded = [];
    readonly Dictionary<string, string> _errors = [];
    readonly Lock _gate = new();

    public PluginTools(ToolHost host, IReadOnlySet<string> reserved)
    {
        _host = host;
        Reserved = reserved;
    }

    /// <summary>Names a plugin may not take (the core tools).</summary>
    public IReadOnlySet<string> Reserved { get; }

    public string Folder => _host.PluginFolder;

    /// <summary>The tools of the folder as it is now, re-reading what changed.</summary>
    public IReadOnlyList<GateKitTool> Current()
    {
        lock (_gate)
        {
            Refresh();
            var tools = new List<GateKitTool>();
            var taken = new HashSet<string>(Reserved, StringComparer.Ordinal);
            foreach (var file in _loaded.Keys.Order(StringComparer.Ordinal))
            {
                foreach (var tool in _loaded[file].Tools)
                {
                    if (taken.Add(tool.Name)) tools.Add(tool);
                    else _errors[Path.GetFileName(file)] = $"tool name '{tool.Name}' is already taken";
                }
            }
            if (_errors.Count > 0) tools.Add(ErrorsTool());
            return tools;
        }
    }

    /// <summary>The tool of this name, or null; `bg3_plugin_errors` answers even when not listed.</summary>
    public GateKitTool? Find(string name)
    {
        if (name == "bg3_plugin_errors") return ErrorsTool();
        return Current().FirstOrDefault(t => t.Name == name);
    }

    void Refresh()
    {
        var folder = Folder;
        var files = Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.json").Where(f => !Path.GetFileName(f).StartsWith('_')).ToList()
            : [];
        foreach (var gone in _loaded.Keys.Except(files, StringComparer.Ordinal).ToList()) _loaded.Remove(gone);
        foreach (var file in files)
        {
            var stamp = File.GetLastWriteTimeUtc(file);
            if (_loaded.TryGetValue(file, out var known) && known.Stamp == stamp) continue;
            var name = Path.GetFileName(file);
            try
            {
                _loaded[file] = (stamp, Read(file));
                _errors.Remove(name);
            }
            catch (Exception e) when (e is IOException or JsonException or StandException or FormatException or KeyNotFoundException)
            {
                _loaded.Remove(file);
                _errors[name] = $"{e.GetType().Name}: {e.Message}";
            }
        }
    }

    IReadOnlyList<GateKitTool> Read(string file)
    {
        var text = File.ReadAllText(file);
        var root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
            ?? throw new FormatException("the file is empty");
        var entries = root as JsonArray ?? [root.DeepClone()];
        return [.. entries.Select(e => Build(e as JsonObject ?? throw new FormatException("a tool must be a JSON object")))];
    }

    GateKitTool Build(JsonObject entry)
    {
        var name = Text(entry, "name") ?? throw new KeyNotFoundException("'name'");
        var description = Text(entry, "description") ?? throw new KeyNotFoundException("'description'");
        var side = Text(entry, "side") ?? "server";
        if (side is not ("client" or "server" or "ui")) throw new FormatException($"side '{side}' is not client, server or ui");
        var function = Text(entry, "function");
        var lua = Text(entry, "lua");
        if ((function is null) == (lua is null)) throw new FormatException("a tool needs either `function` (a GK.* name) or `lua` (a chunk), not both");
        var template = entry["args"] as JsonArray;
        var timeout = TimeSpan.FromSeconds(entry["timeout"] is JsonValue t && t.TryGetValue<double>(out var seconds) ? seconds : 20);
        var schema = entry["inputSchema"] is JsonObject input ? input.ToJsonString() : """{"type": "object", "properties": {}}""";
        var definition = new Tool
        {
            Name = name,
            Description = description,
            InputSchema = JsonDocument.Parse(schema).RootElement,
        };
        return new GateKitTool(definition, async (args, cancel) =>
        {
            var (stand, _) = _host.Begin();
            var session = stand.Session();
            if (function is not null)
            {
                var values = new JsonArray([.. (template ?? []).Select(v => Fill(v, args))]);
                return Results.Text(await CoreTools.CallFunctionAsync(session, side, function, values, timeout, cancel));
            }
            var table = new JsonObject();
            foreach (var key in args.Names) table[key] = args.Node(key);
            var payload = table.ToJsonString();
            var level = 1;
            while (level < 8 && payload.Contains($"]{new string('=', level)}]", StringComparison.Ordinal)) level++;
            var (opening, closing) = ($"[{new string('=', level)}[", $"]{new string('=', level)}]");
            var code = $"local args = Ext.Json.Parse({opening}{payload}{closing})\n{lua}";
            return Results.Text(await session.LuaAsync(side, code, timeout, cancel));
        });
    }

    /// <summary>`"$who"` is the call's argument `who`; anything else is passed as written.</summary>
    static JsonNode? Fill(JsonNode? value, Args args) =>
        value is JsonValue v && v.TryGetValue<string>(out var text) && text.StartsWith('$')
            ? args.Node(text[1..])
            : value?.DeepClone();

    static string? Text(JsonObject entry, string key) =>
        entry[key] is JsonValue v && v.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;

    GateKitTool ErrorsTool()
    {
        string Describe()
        {
            lock (_gate) return string.Join("; ", _errors.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}: {p.Value}"));
        }
        var definition = new Tool
        {
            Name = "bg3_plugin_errors",
            Description = $"Tool files in {Folder} that failed to load: {Describe()}",
            InputSchema = JsonDocument.Parse("""{"type": "object", "properties": {}}""").RootElement,
        };
        return new GateKitTool(definition, (_, _) =>
        {
            lock (_gate)
            {
                var report = new JsonObject();
                foreach (var (file, error) in _errors.OrderBy(p => p.Key, StringComparer.Ordinal)) report[file] = error;
                return Task.FromResult(Results.Text(_errors.Count == 0 ? "no errors" : Results.Json(report)));
            }
        });
    }
}
