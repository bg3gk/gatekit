using System.Reflection;
using GateKit.Stand;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GateKit.Mcp;

/// <summary>
/// GateKit as an MCP server: the stand's tools (CoreTools) plus whatever the tools folder adds
/// (PluginTools), served over stdio by the official C# SDK (NuGet `ModelContextProtocol`).
/// The transport is ours (BatchStdioTransport) because batches are not in the SDK; everything
/// above it - the handshake, capabilities, tools/list, tools/call, ping, cancellation - is the SDK's.
/// </summary>
public sealed class GateKitServer
{
    readonly IReadOnlyList<GateKitTool> _core;
    readonly PluginTools _plugins;

    public GateKitServer(ToolHost host)
    {
        _core = [.. CoreTools.Create(host), .. DeckTools.Create(host), .. CoverageTools.Create(host)];
        _plugins = new PluginTools(host, _core.Select(t => t.Name).ToHashSet(StringComparer.Ordinal));
    }

    public PluginTools Plugins => _plugins;

    /// <summary>What `tools/list` answers: the core tools, then the folder's.</summary>
    public IReadOnlyList<GateKitTool> Tools() => [.. _core, .. _plugins.Current()];

    /// <summary>What `tools/call` answers; a failure is a result with isError, never a protocol error.</summary>
    public async Task<CallToolResult> CallAsync(string name, Args args, CancellationToken cancel = default)
    {
        try
        {
            var tool = _core.FirstOrDefault(t => t.Name == name)
                ?? _plugins.Find(name)
                ?? throw new StandException($"unknown tool {name}");
            Refuse.UnknownArguments(tool, args);
            return await tool.Invoke(args, cancel);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            return Results.Error(e);
        }
    }

    public McpServerOptions Options() => new()
    {
        ServerInfo = new Implementation
        {
            Name = "gatekit",
            Title = "GateKit: the Baldur's Gate 3 test stand",
            Version = typeof(GateKitServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
                ?? "0.1.0",
        },
        ServerInstructions = "Drives a real Baldur's Gate 3 through the GateKit bridge mod: bg3_up starts the game, bg3_state and "
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
