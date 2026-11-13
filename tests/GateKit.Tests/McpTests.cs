using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using GateKit.Mcp;

namespace GateKit.Tests;

/// <summary>
/// The MCP server over a pair of line channels: exactly what a client sees on stdio, so the
/// handshake, the tool list, a call against FakeBridge, batches, concurrency and the error shape
/// are checked on the wire and not through the C# API.
/// </summary>
public sealed class McpTests : IAsyncDisposable
{
    readonly FakeGame _game = new();
    readonly TempFolder _tools = new();
    readonly List<FakeBridge> _bridges = [];
    readonly CancellationTokenSource _stop = new();
    readonly Channel<string> _toServer = Channel.CreateUnbounded<string>();
    readonly Channel<string> _fromServer = Channel.CreateUnbounded<string>();
    Task? _serving;
    int _id;

    static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    public async ValueTask DisposeAsync()
    {
        _toServer.Writer.TryComplete();
        await _stop.CancelAsync();
        if (_serving is { } serving)
        {
            try { await serving.WaitAsync(TimeSpan.FromSeconds(10)); } catch (TimeoutException) { } catch (OperationCanceledException) { }
        }
        foreach (var bridge in _bridges) await bridge.DisposeAsync();
        _stop.Dispose();
        _game.Dispose();
        _tools.Dispose();
    }

    // --- the stand under the server ---

    Stand.Stand NewStand(TextWriter output) => new(_game.Settings(), _game.Platform, output, new CopyPacker());

    void Bridges(Func<string, JsonNode?> client, Func<string, JsonNode?> server)
    {
        var channel = NewStand(TextWriter.Null).Layout.Channel;
        _bridges.Add(new FakeBridge(channel, "client", client));
        _bridges.Add(new FakeBridge(channel, "server", server));
    }

    async Task<JsonObject> StartAsync()
    {
        var host = new ToolHost(NewStand, _tools.Path);
        _serving = new GateKitServer(host).ServeAsync(new ChannelReader(_toServer.Reader), new ChannelWriter(_fromServer.Writer), _stop.Token);
        var initialized = await RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = "2024-11-05",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "test", ["version"] = "1" },
        });
        Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" });
        return initialized;
    }

    // --- the wire ---

    void Send(JsonNode message) => Assert.True(_toServer.Writer.TryWrite(message.ToJsonString()));

    async Task<JsonNode> ReadAsync()
    {
        using var patience = new CancellationTokenSource(Patience);
        var line = await _fromServer.Reader.ReadAsync(patience.Token);
        return JsonNode.Parse(line) ?? throw new InvalidOperationException("null on the wire");
    }

    async Task<JsonObject> RequestAsync(string method, JsonNode? parameters = null)
    {
        var id = ++_id;
        Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters });
        var answer = (JsonObject)await ReadAsync();
        Assert.Equal(id, (int)answer["id"]!);
        Assert.Null(answer["error"]);
        return (JsonObject)answer["result"]!;
    }

    async Task<JsonObject> CallAsync(string tool, JsonObject? arguments = null) =>
        await RequestAsync("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = arguments ?? [] });

    static string TextOf(JsonObject result) => (string)result["content"]![0]!["text"]!;

    // --- tests ---

    [Fact]
    public async Task Initialize_answers_the_handshake()
    {
        var result = await StartAsync();
        Assert.Equal("gatekit", (string)result["serverInfo"]!["name"]!);
        Assert.False(string.IsNullOrEmpty((string?)result["protocolVersion"]));
        Assert.NotNull(result["capabilities"]!["tools"]);
    }

    [Fact]
    public async Task Tools_list_offers_every_core_tool_with_a_schema()
    {
        await StartAsync();
        var tools = (JsonArray)(await RequestAsync("tools/list"))["tools"]!;
        var names = tools.Select(t => (string)t!["name"]!).ToList();
        // The stand's own tools, the deck's, and the coverage loop's - an agent mapping the
        // engine must reach all of it through this server alone, with no Python stand behind it.
        foreach (var wanted in new[] { "bg3_up", "bg3_down", "bg3_status", "bg3_state", "bg3_events", "bg3_lua", "bg3_wait",
            "bg3_dialog", "bg3_fast_scenes", "bg3_logs", "bg3_reload", "bg3_key", "bg3_screenshot", "bg3_checkpoint",
            "bg3_reset", "bg3_sound", "bg3_call", "bg3_api", "bg3_windows", "bg3_window",
            "bg3_pause", "bg3_seen_start", "bg3_seen_mark", "bg3_seen_new", "bg3_seen_drain", "bg3_seen_status",
            "bg3_coverage_todo", "bg3_probe", "bg3_probe_fields", "bg3_probe_watch", "bg3_verify", "bg3_recipe", "bg3_run_report" })
            Assert.Contains(wanted, names);
        Assert.Equal(names.Count, names.Distinct().Count());
        foreach (var tool in tools)
        {
            Assert.False(string.IsNullOrWhiteSpace((string?)tool!["description"]));
            Assert.Equal("object", (string)tool["inputSchema"]!["type"]!);
        }
        // The gaps docs/contracts/mcp.md lists are filled.
        var up = tools.First(t => (string)t!["name"]! == "bg3_up")!;
        Assert.NotNull(up["inputSchema"]!["properties"]!["save"]);
        var wait = tools.First(t => (string)t!["name"]! == "bg3_wait")!;
        Assert.Contains("ui", wait["inputSchema"]!["properties"]!["side"]!["enum"]!.AsArray().Select(v => (string)v!));
    }

    [Fact]
    public async Task A_tool_call_reaches_the_bridge_and_comes_back()
    {
        Bridges(_ => JsonValue.Create(0), code => code.Contains("GK.Party", StringComparison.Ordinal)
            ? JsonNode.Parse("""[{"name": "Tav", "level": 1}]""") : null);
        await StartAsync();
        var call = await CallAsync("bg3_call", new JsonObject { ["side"] = "server", ["function"] = "Party" });
        Assert.Null(call["isError"]);
        Assert.Contains("Tav", TextOf(call), StringComparison.Ordinal);

        var lua = await CallAsync("bg3_lua", new JsonObject { ["side"] = "server", ["code"] = "return GK.Party()" });
        Assert.Contains("Tav", TextOf(lua), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bg3_sound_lists_the_categories_and_refuses_a_volume_out_of_range()
    {
        Bridges(code => code.Contains("GK.Sound.List", StringComparison.Ordinal)
            ? JsonNode.Parse("""[{"key": "MasterVolume", "name": "master", "rtpc": "RTPC_Volume_MAIN", "value": 100}]""")
            : code.Contains("GK.Sound.WhenUnfocused", StringComparison.Ordinal) ? JsonValue.Create(false)
            : JsonValue.Create(0), _ => null);
        await StartAsync();
        var list = await CallAsync("bg3_sound", new JsonObject());
        Assert.Null(list["isError"]);
        Assert.Contains("MasterVolume", TextOf(list), StringComparison.Ordinal);
        Assert.Contains("not in front: off", TextOf(list), StringComparison.Ordinal);

        var set = await CallAsync("bg3_sound", new JsonObject { ["action"] = "set", ["category"] = "music", ["volume"] = 40 });
        Assert.Null(set["isError"]);

        var loud = await CallAsync("bg3_sound", new JsonObject { ["action"] = "set", ["volume"] = 400 });
        Assert.True((bool)loud["isError"]!);
        Assert.Contains("out of range", TextOf(loud), StringComparison.Ordinal);

        var nonsense = await CallAsync("bg3_sound", new JsonObject { ["action"] = "turn_it_up" });
        Assert.True((bool)nonsense["isError"]!);
    }

    [Fact]
    public async Task An_argument_the_tool_does_not_know_is_refused_and_the_near_miss_named()
    {
        await StartAsync();
        // A run asked bg3_coverage_todo for one namespace and spelled it `namespace`; the tool
        // calls it `namespaces`, dropped the word and answered about everything, which the run
        // then reasoned from. Nothing is done now, and the answer says what was meant.
        var wrong = await CallAsync("bg3_coverage_todo", new JsonObject { ["namespace"] = "esv::lock" });
        Assert.True((bool)wrong["isError"]!);
        Assert.Contains("'namespace'", TextOf(wrong), StringComparison.Ordinal);
        Assert.Contains("'namespaces'", TextOf(wrong), StringComparison.Ordinal);

        var right = await CallAsync("bg3_coverage_todo", new JsonObject { ["group"] = "tally" });
        Assert.False(right["isError"] is JsonValue v && v.GetValue<bool>());
    }

    [Fact]
    public async Task Bg3_api_reads_the_bridges_lua_without_a_game()
    {
        _game.Temp.Write("mods/GateKit/Mods/Gwynerva_GateKit/ScriptExtender/Lua/Server/Api.lua",
            "--- The party.\nfunction GK.Party()\n  return {}\nend\n");
        await StartAsync();
        var result = await CallAsync("bg3_api", new JsonObject { ["filter"] = "Party" });
        Assert.Contains("GK.Party", TextOf(result), StringComparison.Ordinal);
        Assert.Contains("The party.", TextOf(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_batch_is_answered_as_one_array()
    {
        await StartAsync();
        Send(new JsonArray(
            new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 101, ["method"] = "ping" },
            new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" },
            new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 102, ["method"] = "tools/list" }));
        var answers = (JsonArray)await ReadAsync();
        Assert.Equal(2, answers.Count);
        var ids = answers.Select(a => (int)a!["id"]!).ToList();
        Assert.Contains(101, ids);
        Assert.Contains(102, ids);
        Assert.All(answers, a => Assert.Null(a!["error"]));
    }

    [Fact]
    public async Task A_batch_of_notifications_alone_is_answered_with_nothing()
    {
        await StartAsync();
        Send(new JsonArray(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }));
        Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 7, ["method"] = "ping" });
        var answer = (JsonObject)await ReadAsync();
        Assert.Equal(7, (int)answer["id"]!);
    }

    [Fact]
    public async Task A_slow_tool_does_not_hold_up_ping()
    {
        Bridges(_ => JsonValue.Create(true), code =>
        {
            if (code.Contains("sleep", StringComparison.Ordinal)) Thread.Sleep(2000);
            return JsonValue.Create(true);
        });
        await StartAsync();
        var slow = ++_id;
        Send(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = slow,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "bg3_lua",
                ["arguments"] = new JsonObject { ["side"] = "server", ["code"] = "return sleep()" },
            },
        });
        var ping = ++_id;
        Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = ping, ["method"] = "ping" });
        var first = (JsonObject)await ReadAsync();
        Assert.Equal(ping, (int)first["id"]!);
        var second = (JsonObject)await ReadAsync();
        Assert.Equal(slow, (int)second["id"]!);
    }

    [Fact]
    public async Task A_failed_call_is_a_result_with_isError_and_text()
    {
        await StartAsync();
        var unknown = await CallAsync("bg3_nonsense");
        Assert.True((bool)unknown["isError"]!);
        Assert.Equal("StandError: unknown tool bg3_nonsense", TextOf(unknown));

        var missing = await CallAsync("bg3_lua", new JsonObject { ["code"] = "return 1" });
        Assert.True((bool)missing["isError"]!);
        Assert.Equal("KeyError: 'side'", TextOf(missing));
    }

    [Fact]
    public async Task A_tool_file_in_the_tools_folder_is_offered_and_called()
    {
        Bridges(_ => JsonValue.Create(0), code => JsonValue.Create(code.Contains("\"Tav\"", StringComparison.Ordinal) ? 42 : 0));
        File.WriteAllText(Path.Combine(_tools.Path, "gold.json"), """
            {
              "name": "bg3_gold",
              "description": "The gold of one character.",
              "inputSchema": {"type": "object", "properties": {"who": {"type": "string"}}},
              "side": "server",
              "function": "Gold",
              "args": ["$who"]
            }
            """);
        await StartAsync();
        var tools = (JsonArray)(await RequestAsync("tools/list"))["tools"]!;
        Assert.Contains("bg3_gold", tools.Select(t => (string)t!["name"]!));
        var result = await CallAsync("bg3_gold", new JsonObject { ["who"] = "Tav" });
        Assert.Equal("42", TextOf(result));
    }

    [Fact]
    public async Task A_broken_tool_file_is_reported_and_leaves_the_rest_alone()
    {
        File.WriteAllText(Path.Combine(_tools.Path, "broken.json"), "{ not json");
        await StartAsync();
        var tools = (JsonArray)(await RequestAsync("tools/list"))["tools"]!;
        var names = tools.Select(t => (string)t!["name"]!).ToList();
        Assert.Contains("bg3_plugin_errors", names);
        Assert.Contains("bg3_up", names);
        Assert.Contains("broken.json", TextOf(await CallAsync("bg3_plugin_errors")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tool_added_while_the_server_runs_appears_without_a_restart()
    {
        await StartAsync();
        Assert.DoesNotContain("bg3_later", ((JsonArray)(await RequestAsync("tools/list"))["tools"]!).Select(t => (string)t!["name"]!));
        File.WriteAllText(Path.Combine(_tools.Path, "later.json"), """
            {"name": "bg3_later", "description": "Added while the server ran.", "side": "server", "lua": "return 1"}
            """);
        Assert.Contains("bg3_later", ((JsonArray)(await RequestAsync("tools/list"))["tools"]!).Select(t => (string)t!["name"]!));
    }

    [Fact]
    public async Task Noise_on_the_input_does_not_kill_the_server()
    {
        await StartAsync();
        Assert.True(_toServer.Writer.TryWrite("  Determining projects to restore..."));
        Assert.True(_toServer.Writer.TryWrite(""));
        var answer = await RequestAsync("ping");
        Assert.NotNull(answer);
    }

    // --- a pair of line channels in place of stdin and stdout ---

    sealed class ChannelReader(ChannelReader<string> lines) : TextReader
    {
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancel)
        {
            try { return await lines.ReadAsync(cancel); }
            catch (ChannelClosedException) { return null; }
            catch (OperationCanceledException) { return null; }
        }

        public override string? ReadLine() => ReadLineAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
    }

    sealed class ChannelWriter(ChannelWriter<string> lines) : TextWriter
    {
        readonly StringBuilder _line = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            if (value == '\n')
            {
                lines.TryWrite(_line.ToString());
                _line.Clear();
            }
            else if (value != '\r') _line.Append(value);
        }
    }
}
