using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using GateKit.Platform;
using GateKit.Copies;
using ModelContextProtocol.Protocol;

namespace GateKit.Mcp;

/// <summary>What a tool runs against: a fresh copy object per call, each with its own output buffer, so
/// calls that run side by side never mix their log lines.</summary>
public sealed class ToolHost
{
    readonly Func<TextWriter, Copies.GameCopy> _copy;

    public ToolHost(Func<TextWriter, Copies.GameCopy> copy, string? pluginFolder = null)
    {
        _copy = copy;
        PluginFolder = pluginFolder ?? DefaultPluginFolder();
    }

    /// <summary>The settings the CLI uses: GATEKIT_INSTANCE unless an instance is named here.</summary>
    public static ToolHost FromEnvironment(string? instance = null, bool installedGame = false)
    {
        var platform = GamePlatform.Current;
        var settings = CopySettings.Resolve(platform, instanceOverride: instance, installedGame: installedGame);
        return new ToolHost(output => new Copies.GameCopy(settings, platform, output))
        {
            Instance = settings.Instance,
            Fair = Environment.GetEnvironmentVariable("GATEKIT_FAIR") is "1" or "true",
        };
    }

    /// <summary>The client is served as fair: the event log and attention give it rows a player
    /// could know (`knows = player`) only. Set by whoever starts the server (GATEKIT_FAIR=1), never by
    /// the client; the filter is the host's.</summary>
    public bool Fair { get; init; }

    /// <summary>The instance every tool of this server works on; null is the installed game.</summary>
    public string? Instance { get; init; }

    /// <summary>The coverage record the capture tools (bg3_seen_new, bg3_seen_drain) write to; null
    /// is research/coverage of the repository. A test points it at a folder of its own.</summary>
    public string? CoverageRoot { get; init; }

    /// <summary>Where tools added without rebuilding the server live (see the project's README).</summary>
    public string PluginFolder { get; }

    /// <summary>GATEKIT_MCP_TOOLS, else the checkout's mcp-tools, else &lt;home&gt;/mcp-tools.</summary>
    static string DefaultPluginFolder() => GateKit.Home.GateKitHome.Current.McpTools.Path!;

    /// <summary>How far this server's caller has been told what waits in the game (bg3_wait and the
    /// `attention` note after every game call).</summary>
    public GateKit.Bridge.AttentionCursor Attention { get; } = new();

    /// <summary>The attention items the caller has not seen, read from the bridge's files; empty when
    /// no game answers (no heartbeat in the last 15 s) or nothing waits or changed.</summary>
    public System.Text.Json.Nodes.JsonArray AttentionNote()
    {
        var (copy, _) = Begin();
        var channel = copy.Layout.Channel;
        var bridge = new GateKit.Bridge.BridgeClient(channel);
        if (bridge.HeartbeatAge(GateKit.Bridge.Side.Client) is not { } age || age > TimeSpan.FromSeconds(15)) return [];
        return AttentionOf(channel).Report(Attention);
    }

    GateKit.Bridge.AttentionReader? _attention;

    /// <summary>The view of the event log in the bridge's folder, kept between calls so full segments
    /// are read once.</summary>
    public GateKit.Bridge.AttentionReader AttentionOf(string channel)
    {
        var reader = _attention;
        if (reader is null || !string.Equals(reader.Channel, channel, StringComparison.Ordinal))
            _attention = reader = new GateKit.Bridge.AttentionReader(channel) { Fair = Fair };
        return reader;
    }

    /// <summary>A copy object for one call, and the buffer its output goes to.</summary>
    public (Copies.GameCopy GameCopy, StringWriter Log) Begin()
    {
        var log = new StringWriter();
        return (_copy(log), log);
    }
}

/// <summary>One tool: what `tools/list` says about it and what `tools/call` runs.</summary>
/// <param name="Definition">The entry of `tools/list`.</param>
/// <param name="Invoke">What the call does.</param>
public sealed record GateKitTool(Tool Definition, Func<Args, CancellationToken, Task<CallToolResult>> Invoke)
{
    public string Name => Definition.Name;
}

/// <summary>The result shapes of the tools: text, or indented JSON for anything else.</summary>
public static class Results
{
    static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        IndentSize = 1,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>JSON as the tools write it: one space of indentation, nothing escaped.</summary>
    public static string Json(JsonNode? value) => value?.ToJsonString(Indented) ?? "null";

    /// <summary>A string verbatim; anything else as indented JSON.</summary>
    public static string Render(JsonNode? value) =>
        value is JsonValue v && v.TryGetValue<string>(out var text) ? text : Json(value);

    public static CallToolResult Text(string text) => new() { Content = [new TextContentBlock { Text = text }] };

    public static CallToolResult Text(JsonNode? value) => Text(Render(value));

    public static CallToolResult Image(byte[] bytes, string mimeType) =>
        new() { Content = [new ImageContentBlock { Data = Encoding.UTF8.GetBytes(Convert.ToBase64String(bytes)), MimeType = mimeType }] };

    /// <summary>A failed call: `&lt;kind&gt;: &lt;message&gt;` and isError.</summary>
    public static CallToolResult Error(Exception e) => new()
    {
        Content = [new TextContentBlock { Text = $"{Kind(e)}: {e.Message}" }],
        IsError = true,
    };

    /// <summary>The kind of a failed call, named for the errors an agent actually meets (CopyError
    /// for a copy, KeyError for a missing argument); anything else by its exception type.</summary>
    static string Kind(Exception e) => e switch
    {
        CopyException => "CopyError",
        PlatformCapabilityException => "CopyError",
        KeyNotFoundException => "KeyError",
        FormatException => "ValueError",
        ArgumentException => "ValueError",
        _ => e.GetType().Name,
    };
}
