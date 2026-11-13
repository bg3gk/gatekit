using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using GateKit.Platform;
using GateKit.Stand;
using ModelContextProtocol.Protocol;

namespace GateKit.Mcp;

/// <summary>What a tool runs against: a fresh stand per call, each with its own output buffer, so
/// calls that run side by side never mix their log lines.</summary>
public sealed class ToolHost
{
    readonly Func<TextWriter, Stand.Stand> _stand;

    public ToolHost(Func<TextWriter, Stand.Stand> stand, string? pluginFolder = null)
    {
        _stand = stand;
        PluginFolder = pluginFolder ?? DefaultPluginFolder();
    }

    /// <summary>The settings the CLI uses: GATEKIT_INSTANCE (or BG3_GATEKIT_INSTANCE) unless an
    /// instance is named here.</summary>
    public static ToolHost FromEnvironment(string? instance = null)
    {
        var platform = GamePlatform.Current;
        var settings = StandSettings.Resolve(platform, instanceOverride: instance);
        return new ToolHost(output => new Stand.Stand(settings, platform, output)) { Instance = settings.Instance };
    }

    /// <summary>The instance every tool of this server works on; null is the installed game.</summary>
    public string? Instance { get; init; }

    /// <summary>Where tools added without rebuilding the server live (see the project's README).</summary>
    public string PluginFolder { get; }

    static string DefaultPluginFolder()
    {
        foreach (var name in new[] { "GATEKIT_MCP_TOOLS", "BG3_GATEKIT_MCP_TOOLS" })
        {
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } folder) return Path.GetFullPath(folder);
        }
        return Path.Combine(StandSettings.RepositoryRoot()
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GateKit"), "mcp-tools");
    }

    /// <summary>A stand for one call, and the buffer its output goes to.</summary>
    public (Stand.Stand Stand, StringWriter Log) Begin()
    {
        var log = new StringWriter();
        return (_stand(log), log);
    }
}

/// <summary>One tool: what `tools/list` says about it and what `tools/call` runs.</summary>
/// <param name="Definition">The entry of `tools/list`.</param>
/// <param name="Invoke">What the call does.</param>
public sealed record GateKitTool(Tool Definition, Func<Args, CancellationToken, Task<CallToolResult>> Invoke)
{
    public string Name => Definition.Name;
}

/// <summary>The result shapes of the old server: text, or indented JSON for anything else.</summary>
public static class Results
{
    static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        IndentSize = 1,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>JSON as the old server wrote it: one space of indentation, nothing escaped.</summary>
    public static string Json(JsonNode? value) => value?.ToJsonString(Indented) ?? "null";

    /// <summary>A string verbatim; anything else as indented JSON.</summary>
    public static string Render(JsonNode? value) =>
        value is JsonValue v && v.TryGetValue<string>(out var text) ? text : Json(value);

    public static CallToolResult Text(string text) => new() { Content = [new TextContentBlock { Text = text }] };

    public static CallToolResult Text(JsonNode? value) => Text(Render(value));

    public static CallToolResult Image(byte[] bytes, string mimeType) =>
        new() { Content = [new ImageContentBlock { Data = Encoding.UTF8.GetBytes(Convert.ToBase64String(bytes)), MimeType = mimeType }] };

    /// <summary>A failed call, as the old server wrote it: `&lt;kind&gt;: &lt;message&gt;` and isError.</summary>
    public static CallToolResult Error(Exception e) => new()
    {
        Content = [new TextContentBlock { Text = $"{Kind(e)}: {e.Message}" }],
        IsError = true,
    };

    /// <summary>The old server showed Python's exception class; these are the same names for the
    /// errors an agent actually meets (StandError for the stand, KeyError for a missing argument).</summary>
    static string Kind(Exception e) => e switch
    {
        StandException => "StandError",
        PlatformCapabilityException => "StandError",
        KeyNotFoundException => "KeyError",
        FormatException => "ValueError",
        ArgumentException => "ValueError",
        _ => e.GetType().Name,
    };
}
