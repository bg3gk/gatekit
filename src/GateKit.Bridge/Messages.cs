using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace GateKit.Bridge;

/// <summary>A game side: each has its own Lua state and its own channel files.</summary>
public enum Side { Server, Client }

public static class SideNames
{
    public static string Name(this Side side) => side == Side.Server ? "server" : "client";

    public static Side Parse(string text) => text.ToLowerInvariant() switch
    {
        "server" => Side.Server,
        "client" => Side.Client,
        _ => throw new ArgumentException($"unknown side '{text}' (server or client)"),
    };
}

/// <summary>One line of queue_&lt;side&gt;.jsonl (schemas/bridge/command.schema.json).</summary>
public sealed record Command
{
    [JsonPropertyName("seq")] public long Seq { get; init; }
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("code")] public required string Code { get; init; }
    [JsonPropertyName("ui")] public bool Ui { get; init; }
    [JsonPropertyName("t")] public double T { get; init; }
}

/// <summary>res_&lt;side&gt;_&lt;id&gt;.json (schemas/bridge/answer.schema.json).</summary>
public sealed record Answer
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("side")] public string? Side { get; init; }
    [JsonPropertyName("ok")] public bool Ok { get; init; }
    [JsonPropertyName("result")] public JsonNode? Result { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
    [JsonPropertyName("log")] public List<string>? Log { get; init; }
}

/// <summary>state_&lt;side&gt;.json (schemas/bridge/state.schema.json).</summary>
public sealed record Heartbeat
{
    [JsonPropertyName("side")] public string? Side { get; init; }
    [JsonPropertyName("boot")] public double Boot { get; init; }
    [JsonPropertyName("time")] public double Time { get; init; }
    [JsonPropertyName("gameState")] public string? GameState { get; init; }
    [JsonPropertyName("queue")] public int Queue { get; init; }
    [JsonPropertyName("done")] public long Done { get; init; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(Command))]
[JsonSerializable(typeof(Answer))]
[JsonSerializable(typeof(Heartbeat))]
internal partial class BridgeJson : JsonSerializerContext;
