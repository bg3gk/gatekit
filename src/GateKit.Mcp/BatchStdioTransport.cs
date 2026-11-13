using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace GateKit.Mcp;

/// <summary>
/// The server's end of a stdio connection: one JSON message per line, as the old Python server
/// spoke it, with the two things it got wrong fixed (docs/contracts/mcp.md, section 3):
///
/// <list type="bullet">
/// <item>A JSON-RPC <b>batch</b> (an array on one line) killed the Python server. Here every
/// element is handed to the session on its own and the answers are collected back into one array,
/// written when the last one is ready - the order of the requests, as JSON-RPC asks. A batch of
/// notifications alone is answered with nothing at all.</item>
/// <item>Answers are written as they come, from whichever thread finishes first, so a long tool
/// (bg3_up, bg3_reset) no longer holds up `ping`, `tools/list` or another tool.</item>
/// </list>
///
/// A line that is not JSON at all is skipped when it cannot be a message (build output, a stray
/// print), and answered with a parse error when it looks like one (it starts with '{' or '[').
/// A byte-order mark is tolerated anywhere a line starts.
/// </summary>
public sealed class BatchStdioTransport : TransportBase
{
    readonly TextReader _input;
    readonly TextWriter _output;
    readonly SemaphoreSlim _writing = new(1, 1);
    readonly Dictionary<RequestId, Batch> _batches = [];
    readonly Lock _gate = new();
    readonly CancellationTokenSource _stop = new();
    Task? _reading;

    /// <summary>The answers of one batch, kept until every request of it has one.</summary>
    sealed class Batch
    {
        public HashSet<RequestId> Waiting { get; } = [];
        public List<JsonNode> Answers { get; } = [];
    }

    public BatchStdioTransport(TextReader input, TextWriter output, ILoggerFactory? loggerFactory = null)
        : base("GateKit stdio", loggerFactory ?? NullLoggerFactory.Instance)
    {
        _input = input;
        _output = output;
    }

    /// <summary>Starts reading the input. The task ends when the input is closed.</summary>
    public Task StartAsync()
    {
        SetConnected();
        return _reading = Task.Run(() => ReadLoopAsync(_stop.Token));
    }

    async Task ReadLoopAsync(CancellationToken cancel)
    {
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                string? line;
                try { line = await _input.ReadLineAsync(cancel); }
                catch (OperationCanceledException) { break; }
                if (line is null) break; // the client closed its end
                line = line.Trim().TrimStart('﻿').Trim();
                if (line.Length == 0) continue;
                JsonNode? node = null;
                try { node = JsonNode.Parse(line); }
                catch (JsonException) { }
                if (node is null)
                {
                    // Only what tried to be a message is worth an answer; anything else is noise
                    // on the same stream (a build line, a print) and is dropped.
                    if (line[0] is '{' or '[') await WriteNodeAsync(Failure(null, McpErrorCode.ParseError, "invalid JSON"), cancel);
                    continue;
                }
                if (node is JsonArray array) await ReceiveBatchAsync(array, cancel);
                else await ReceiveOneAsync(node, cancel);
            }
            SetDisconnected();
        }
        catch (Exception e)
        {
            SetDisconnected(e);
        }
    }

    async Task ReceiveOneAsync(JsonNode node, CancellationToken cancel)
    {
        if (Read(node) is { } message) await WriteMessageAsync(message, cancel);
        else await WriteNodeAsync(Failure(IdOf(node), McpErrorCode.InvalidRequest, "not a JSON-RPC message"), cancel);
    }

    async Task ReceiveBatchAsync(JsonArray array, CancellationToken cancel)
    {
        if (array.Count == 0)
        {
            await WriteNodeAsync(Failure(null, McpErrorCode.InvalidRequest, "empty batch"), cancel);
            return;
        }
        var batch = new Batch();
        var messages = new List<JsonRpcMessage>();
        foreach (var element in array)
        {
            if (element is not null && Read(element) is { } message)
            {
                if (message is JsonRpcRequest request) batch.Waiting.Add(request.Id);
                messages.Add(message);
            }
            else batch.Answers.Add(Failure(element is null ? null : IdOf(element), McpErrorCode.InvalidRequest, "not a JSON-RPC message"));
        }
        if (batch.Waiting.Count == 0)
        {
            // Nothing in it is a request: the notifications are delivered and, per JSON-RPC,
            // nothing is answered unless an element was malformed.
            foreach (var message in messages) await WriteMessageAsync(message, cancel);
            if (batch.Answers.Count > 0) await WriteNodeAsync(new JsonArray([.. batch.Answers]), cancel);
            return;
        }
        lock (_gate)
        {
            foreach (var id in batch.Waiting) _batches[id] = batch;
        }
        foreach (var message in messages) await WriteMessageAsync(message, cancel);
    }

    /// <summary>Sends one answer or notification; a batch's answers wait for their siblings.</summary>
    public override async Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancel = default)
    {
        var node = JsonSerializer.SerializeToNode(message, McpJsonUtilities.DefaultOptions)
            ?? throw new InvalidOperationException("a JSON-RPC message serialised to nothing");
        if (message is JsonRpcMessageWithId withId)
        {
            Batch? batch;
            lock (_gate)
            {
                if (_batches.Remove(withId.Id, out batch))
                {
                    batch.Waiting.Remove(withId.Id);
                    batch.Answers.Add(node);
                    if (batch.Waiting.Count > 0) return;
                    node = new JsonArray([.. batch.Answers]);
                }
            }
        }
        await WriteNodeAsync(node, cancel);
    }

    async Task WriteNodeAsync(JsonNode node, CancellationToken cancel)
    {
        var line = node.ToJsonString(McpJsonUtilities.DefaultOptions);
        await _writing.WaitAsync(cancel);
        try
        {
            await _output.WriteAsync(line.AsMemory(), cancel);
            await _output.WriteAsync("\n".AsMemory(), cancel);
            await _output.FlushAsync(cancel);
        }
        finally { _writing.Release(); }
    }

    static JsonRpcMessage? Read(JsonNode node)
    {
        if (node is not JsonObject) return null;
        try { return JsonSerializer.Deserialize<JsonRpcMessage>(node, McpJsonUtilities.DefaultOptions); }
        catch (JsonException) { return null; }
    }

    /// <summary>The id of something that was not a message, so the error can carry it.</summary>
    static JsonNode? IdOf(JsonNode node) => node is JsonObject o && o.TryGetPropertyValue("id", out var id) ? id?.DeepClone() : null;

    static JsonObject Failure(JsonNode? id, McpErrorCode code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = (int)code, ["message"] = message },
    };

    public override async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_reading is { } reading)
        {
            try { await reading; } catch (OperationCanceledException) { }
        }
        _stop.Dispose();
        _writing.Dispose();
        SetDisconnected();
    }
}
