using System.Reflection;
using GateKit.Copies;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GateKit.Mcp;

/// <summary>
/// GateKit as an MCP server: the core tools (CoreTools) plus whatever the tools folder adds
/// (PluginTools), served over stdio by the official C# SDK (NuGet `ModelContextProtocol`).
/// The transport is ours (BatchStdioTransport) because batches are not in the SDK; everything
/// above it - the handshake, capabilities, tools/list, tools/call, ping, cancellation - is the SDK's.
/// </summary>
public sealed class GateKitServer
{
    readonly IReadOnlyList<GateKitTool> _core;
    readonly PluginTools _plugins;
    readonly ToolHost _host;

    /// <summary>The tools that act in or read a running game: their answers carry the `attention`
    /// note (docs/contracts/mcp.md). bg3_wait adds its own.</summary>
    public static readonly IReadOnlySet<string> GameTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "bg3_lua", "bg3_call", "bg3_state", "bg3_events", "bg3_dialog", "bg3_key", "bg3_screenshot", "bg3_status",
        "bg3_checkpoint", "bg3_reset", "bg3_reload", "bg3_fast_scenes", "bg3_sound",
    };

    public GateKitServer(ToolHost host)
    {
        _host = host;
        _core = [.. CoreTools.Create(host), .. DeckTools.Create(host), .. CoverageTools.Create(host), .. RecordTools.Create(host), .. NamesTools.Create(), .. RegistryTools.Create(), .. LeadTools.Create(), .. WikiTools.Create()];
        _plugins = new PluginTools(host, _core.Select(t => t.Name).ToHashSet(StringComparer.Ordinal));
    }

    public PluginTools Plugins => _plugins;

    /// <summary>What `tools/list` answers: the core tools, then the folders'; with
    /// <see cref="ToolHost.Only"/>, those whose name begins with it and no other.</summary>
    public IReadOnlyList<GateKitTool> Tools()
    {
        IReadOnlyList<GateKitTool> all = [.. _core, .. _plugins.Current()];
        return _host.Only is { Length: > 0 } only ? [.. all.Where(t => t.Name.StartsWith(only, StringComparison.Ordinal))] : all;
    }

    /// <summary>What `tools/call` answers; a failure is a result with isError, never a protocol error.</summary>
    public async Task<CallToolResult> CallAsync(string name, Args args, CancellationToken cancel = default)
    {
        try
        {
            if (_host.Only is { Length: > 0 } only && !name.StartsWith(only, StringComparison.Ordinal))
                throw new CopyException($"unknown tool {name}: this server offers the tools whose name begins with {only}");
            var tool = _core.FirstOrDefault(t => t.Name == name)
                ?? _plugins.Find(name)
                ?? throw new CopyException($"unknown tool {name}");
            Refuse.UnknownArguments(tool, args);
            Refuse.MissingArguments(tool, args);
            CallToolResult result;
            try { result = await tool.Invoke(args, cancel); }
            catch (MissingArgumentException missing) { throw Refuse.Missing(tool, [missing.Name]); }
            if (GameTools.Contains(name) || (_plugins.Find(name) is not null && !_plugins.SaysAttentionItself(name))) AddAttention(result);
            return result;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            return Results.Error(e);
        }
    }

    /// <summary>Appends what waits in the game, or changed since the caller's last call, as one more
    /// text block "attention: [...]"; nothing when there is nothing. The tool's own answer is untouched.</summary>
    void AddAttention(CallToolResult result)
    {
        try
        {
            var note = _host.AttentionNote();
            if (note.Count == 0) return;
            // held to common/attention: a misfit is said, not hidden
            var misfits = SchemaCheck.Misfits("common/attention", note);
            var text = GateKit.Json.JsonText.Inline(note);
            result.Content.Add(new TextContentBlock { Text = misfits.Count == 0 ? "attention: " + text
                : "attention (does not fit common/attention: " + string.Join("; ", misfits.Take(5)) + "): " + text });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CopyException) { }
    }

    public McpServerOptions Options() => new()
    {
        ServerInfo = new Implementation
        {
            Name = "gatekit",
            Title = "GateKit: Baldur's Gate 3 and its copies",
            Version = typeof(GateKitServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
                ?? "0.1.0",
        },
        // a server narrowed to one set of tools (--only) names none of the others: each tool says what it is for
        ServerInstructions = _host.Only is { Length: > 0 }
            ? "Drives a real Baldur's Gate 3 through the GateKit bridge mod; each tool's description says what it does."
            : "Drives a real Baldur's Gate 3 through the GateKit bridge mod: bg3_up starts the game, bg3_state and "
            + "bg3_events read it, bg3_lua and bg3_call run anything the bridge offers (bg3_api lists it), bg3_down closes it again.",
        Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
        Handlers = new McpServerHandlers
        {
            ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = [.. Tools().Select(t => t.Definition)] }),
            CallToolHandler = async (request, cancel) =>
                await CallAsync(request.Params?.Name ?? "", new Args(request.Params?.Arguments), cancel),
        },
    };

    /// <summary>Serves on one pair of streams until the input ends or <paramref name="cancel"/> fires.</summary>
    public async Task ServeAsync(TextReader input, TextWriter output, CancellationToken cancel = default)
    {
        await using var transport = new BatchStdioTransport(input, output);
        var reading = transport.StartAsync();
        await using var server = McpServer.Create(transport, Options());
        var serving = server.RunAsync(cancel);
        await Task.WhenAny(reading, serving);
    }
}
