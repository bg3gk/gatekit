using System.Text.Json;
using System.Text.Json.Nodes;
using GateKit.Copies;
using ModelContextProtocol.Protocol;

namespace GateKit.Mcp;

/// <summary>
/// Tools a modder or an agent adds without rebuilding the server: one JSON file per tool (or an
/// array of them) in the tools folder (GATEKIT_MCP_TOOLS, by default &lt;repo&gt;/mcp-tools). The
/// folder is read again whenever a file changes, by its modification time, so a new tool is live
/// on the next tools/list.
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

    readonly HashSet<string> _quiet = new(StringComparer.Ordinal);

    /// <summary>Whether the tool declared `"attention": false`: its answer carries what waits in the
    /// game by itself, so the host's `attention` note is not added to it.</summary>
    public bool SaysAttentionItself(string name)
    {
        lock (_quiet) return _quiet.Contains(name);
    }

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
        var files = new List<string>();
        foreach (var folder in (IEnumerable<string>)[Folder, .. _host.ModToolFolders()])
        {
            if (Directory.Exists(folder))
                files.AddRange(Directory.EnumerateFiles(folder, "*.json").Where(f => !Path.GetFileName(f).StartsWith('_')).Order(StringComparer.Ordinal));
        }
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
            catch (Exception e) when (e is IOException or JsonException or CopyException or FormatException or KeyNotFoundException)
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
        // a path a tool is given is read from the folder that holds the tools folder (mods/AI for mods/AI/tools)
        var home = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(file))) ?? Directory.GetCurrentDirectory();
        return [.. entries.Select(e => Build(e as JsonObject ?? throw new FormatException("a tool must be a JSON object"), home))];
    }

    /// <summary>The most characters of a file a tool reads for its caller (`files`).</summary>
    public const int MaxFileCharacters = 16000;

    /// <summary>`"files": {"prompt_file": "prompt"}`: when the call gives the argument on the left, it
    /// is a path of a UTF-8 text file on this machine - a relative one under <paramref name="home"/> -
    /// which the host reads; its text goes to the chunk as the argument on the right, and the
    /// argument on the left becomes the full path. The game never opens the file.</summary>
    static void ReadFiles(string tool, JsonObject files, JsonObject table, string home)
    {
        foreach (var (from, into) in files)
        {
            if (table[from] is not JsonValue given || !given.TryGetValue<string>(out var path) || path.Length == 0) continue;
            var target = into is JsonValue name && name.TryGetValue<string>(out var text) ? text : throw new CopyException($"{tool}: `files` maps an argument to an argument's name");
            var full = Path.GetFullPath(path, home);
            if (!File.Exists(full))
                throw new CopyException($"{tool}: {from} names no file: {full} (a relative path is read from {home})");
            var content = File.ReadAllText(full, System.Text.Encoding.UTF8);
            if (content.Length > MaxFileCharacters)
                throw new CopyException($"{tool}: {full} has {content.Length} characters; a file given as {from} has at most {MaxFileCharacters}");
            table[target] = content;
            table[from] = full;
        }
    }

    GateKitTool Build(JsonObject entry, string home)
    {
        var files = entry["files"] as JsonObject;
        var name = Text(entry, "name") ?? throw new KeyNotFoundException("'name'");
        var description = Text(entry, "description") ?? throw new KeyNotFoundException("'description'");
        var side = Text(entry, "side") ?? "server";
        if (side is not ("client" or "server" or "ui")) throw new FormatException($"side '{side}' is not client, server or ui");
        var function = Text(entry, "function");
        var lua = Text(entry, "lua");
        if ((function is null) == (lua is null)) throw new FormatException("a tool needs either `function` (a GK.* name) or `lua` (a chunk), not both");
        var template = entry["args"] as JsonArray;
        // "attention": false - the tool's own answer says what waits; the host adds no note to it
        if (entry["attention"] is JsonValue attention && attention.TryGetValue<bool>(out var noted) && !noted)
            lock (_quiet) _quiet.Add(name);
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
            var (copy, _) = _host.Begin();
            var session = copy.Session();
            if (function is not null)
            {
                var values = new JsonArray([.. (template ?? []).Select(v => Fill(v, args))]);
                return Results.Text(await CoreTools.CallFunctionAsync(session, side, function, values, timeout, cancel));
            }
            var table = new JsonObject();
            foreach (var key in args.Names) table[key] = args.Node(key);
            if (files is not null) ReadFiles(name, (JsonObject)files.DeepClone(), table, home);
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
