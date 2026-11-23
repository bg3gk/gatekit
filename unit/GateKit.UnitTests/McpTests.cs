using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using GateKit.Mcp;

namespace GateKit.UnitTests;

/// <summary>
/// The MCP server over a pair of line channels: exactly what a client sees on stdio, so the
/// handshake, the tool list, a call against FakeBridge, batches, concurrency and the error shape
/// are checked on the wire and not through the C# API.
/// </summary>
public sealed class McpTests : IAsyncLifetime
{
    readonly FakeGame _game = new();
    readonly TempFolder _tools = new();
    readonly TempFolder _record = new();
    string? _coverage;
    readonly List<FakeBridge> _bridges = [];
    readonly CancellationTokenSource _stop = new();
    readonly Channel<string> _toServer = Channel.CreateUnbounded<string>();
    readonly Channel<string> _fromServer = Channel.CreateUnbounded<string>();
    Task? _serving;
    int _id;

    static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
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
        _record.Dispose();
    }

    // --- the copy under the server ---

    Copies.GameCopy NewCopy(TextWriter output) => new(_game.Settings(), _game.Platform, output, new CopyPacker());

    void Bridges(Func<string, JsonNode?> client, Func<string, JsonNode?> server)
    {
        var channel = NewCopy(TextWriter.Null).Layout.Channel;
        _bridges.Add(new FakeBridge(channel, "client", client));
        _bridges.Add(new FakeBridge(channel, "server", server));
    }

    async Task<JsonObject> StartAsync()
    {
        var host = new ToolHost(NewCopy, _tools.Path) { CoverageRoot = _coverage };
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
        // The core tools, the deck's, and the coverage loop's - an agent mapping the
        // engine must reach all of it through this server alone.
        foreach (var wanted in new[] { "bg3_up", "bg3_down", "bg3_status", "bg3_state", "bg3_events", "bg3_lua", "bg3_wait",
            "bg3_dialog", "bg3_fast_scenes", "bg3_logs", "bg3_timeline", "bg3_reload", "bg3_key", "bg3_screenshot", "bg3_checkpoint",
            "bg3_reset", "bg3_sound", "bg3_call", "bg3_api", "bg3_windows", "bg3_window",
            "bg3_pause", "bg3_seen_start", "bg3_seen_mark", "bg3_seen_new", "bg3_seen_drain", "bg3_seen_status",
            "bg3_coverage_todo", "bg3_probe", "bg3_probe_fields", "bg3_probe_watch", "bg3_verify", "bg3_recipe", "bg3_run_report",
            "bg3_names", "bg3_lead", "bg3_wiki", "bg3_wish", "bg3_record" })
            Assert.Contains(wanted, names);
        // A copy runs bg3_dx11.exe and the symbol database holds both builds at different addresses:
        // a hint that names bg3.exe hands back a plausible name for the wrong function.
        Assert.DoesNotContain("signature bg3.exe", (string)tools.First(t => (string)t!["name"]! == "bg3_probe")!["description"]!);
        // every rung of the names ladder is explained to the agent that writes names
        var namesTool = (string)tools.First(t => (string)t!["name"]! == "bg3_names")!["description"]!;
        foreach (var rung in GateKit.Names.FieldNames.Ladder)
            Assert.Contains($"`{rung}`", namesTool);
        Assert.Equal(names.Count, names.Distinct().Count());
        foreach (var tool in tools)
        {
            Assert.False(string.IsNullOrWhiteSpace((string?)tool!["description"]));
            Assert.Equal("object", (string)tool["inputSchema"]!["type"]!);
        }
        // The gaps docs/contracts/mcp.md lists are filled.
        var up = tools.First(t => (string)t!["name"]! == "bg3_up")!;
        Assert.NotNull(up["inputSchema"]!["properties"]!["save"]);
        Assert.NotNull(up["inputSchema"]!["properties"]!["until_lua"]);
        Assert.NotNull(up["inputSchema"]!["properties"]!["first_launch"]);
        var wait = tools.First(t => (string)t!["name"]! == "bg3_wait")!;
        Assert.Contains("ui", wait["inputSchema"]!["properties"]!["side"]!["enum"]!.AsArray().Select(v => (string)v!));
    }

    [Fact]
    public async Task Bg3_status_never_reports_an_old_heartbeat_as_a_running_game()
    {
        // D5 of the day run: with no --instance, bg3_status answered gameState Running from a
        // heartbeat two days old in the installed game's channel, with no game up.
        var channel = NewCopy(TextWriter.Null).Layout.Channel;
        Directory.CreateDirectory(channel);
        foreach (var side in new[] { "client", "server" })
        {
            var path = Path.Combine(channel, $"state_{side}.json");
            File.WriteAllText(path, $$"""{"side": "{{side}}", "boot": 1, "time": 2, "gameState": "Running"}""");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-2));
        }
        await StartAsync();
        var status = JsonNode.Parse(TextOf(await CallAsync("bg3_status", new JsonObject())))!;
        Assert.Null(status["instance"]);
        Assert.Equal("the installed game (no --instance)", (string)status["drives"]!);
        Assert.Null(status["client"]!["gameState"]);
        Assert.Contains("no game of this copy is running", (string)status["client"]!["stale"]!);
        Assert.False((bool)status["client_alive"]!);

        var lua = await CallAsync("bg3_lua", new JsonObject { ["side"] = "server", ["code"] = "return 1" });
        Assert.True((bool)lua["isError"]!);
        Assert.Contains("no game is running for the installed game", TextOf(lua));
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
    public async Task A_game_call_carries_what_waits_and_bg3_wait_ends_on_the_next_event()
    {
        Bridges(_ => JsonValue.Create(1), _ => JsonValue.Create(1));
        var channel = NewCopy(TextWriter.Null).Layout.Channel;
        EventLogTests.WriteLog(channel, "client", 1, 128,
            EventLogTests.Row(1, "dialogue", 900, ""","key":"window:dialogue","attention":"begin","payload":{"window":"dialogue"}"""));
        await StartAsync();
        var lua = await CallAsync("bg3_lua", new JsonObject { ["side"] = "server", ["code"] = "return 1" });
        var content = lua["content"]!.AsArray();
        Assert.Equal("1", (string)content[0]!["text"]!);
        var note = (string)content[^1]!["text"]!;
        Assert.StartsWith("attention: ", note, StringComparison.Ordinal);
        Assert.Contains("\"dialogue\"", note, StringComparison.Ordinal);

        var waiting = CallAsync("bg3_wait", new JsonObject { ["timeout"] = 20 });
        await Task.Delay(300);
        EventLogTests.WriteLog(channel, "server", 5, 128,
            EventLogTests.Row(1, "combat_started", 49, ""","key":"combat","attention":"begin","payload":{"combat":"c1"}"""));
        var ended = JsonNode.Parse(TextOf(await waiting))!;
        Assert.Equal("attention", (string)ended["endedBy"]!);
        Assert.Equal("combat_started", (string)ended["attention"]![0]!["what"]!);
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

        // wish 11: mute answered a bare number
        var mute = await CallAsync("bg3_sound", new JsonObject { ["action"] = "mute" });
        Assert.StartsWith("muted MasterVolume", TextOf(mute), StringComparison.Ordinal);
        var unmute = await CallAsync("bg3_sound", new JsonObject { ["action"] = "unmute" });
        Assert.StartsWith("nothing to unmute", TextOf(unmute), StringComparison.Ordinal);

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
    public async Task A_required_argument_left_out_is_refused_naming_what_is_needed()
    {
        await StartAsync();
        // bg3_recipe '{}' answered a bare "KeyError: 'component'".
        var bare = await CallAsync("bg3_recipe", new JsonObject());
        Assert.True((bool)bare["isError"]!);
        Assert.Contains("bg3_recipe needs 'component'", TextOf(bare), StringComparison.Ordinal);
        Assert.Contains("It takes:", TextOf(bare), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_drain_never_lowers_the_sample_count_of_a_type()
    {
        // The raw samples are not committed: a fresh worktree holds none of a type the record has
        // drained ten of, and a drain from it took "samples": 10 to 1 (fixed in 5d64299).
        _coverage = _record.Path;
        _record.Write("seen/index.json", """
            {"esv::a::XComponent": {"server": {"max": 2, "labels": ["old"], "levels": ["WLD_A"], "samples": 10}}}
            """);
        Bridges(code => code.Contains("GK.Seen.Status", StringComparison.Ordinal) ? JsonNode.Parse("""{"Running": false}""") : null,
            code => code switch
            {
                _ when code.Contains("GK.Seen.Status", StringComparison.Ordinal) => JsonNode.Parse("""{"Running": true}"""),
                _ when code.Contains("GK.Seen.Drain", StringComparison.Ordinal) => JsonNode.Parse(
                    """{"Rows": [{"Name": "esv::a::XComponent", "Hex": "00ff", "Size": 2, "Entity": "e1", "Label": "new"}], "Left": 0}"""),
                _ when code.Contains("GK.Seen.Types", StringComparison.Ordinal) => JsonNode.Parse("""{"esv::a::XComponent": {"Max": 1, "Label": "new"}}"""),
                _ => null,
            });
        await StartAsync();
        var drain = await CallAsync("bg3_seen_drain", new JsonObject { ["level"] = "WLD_B" });
        Assert.True(drain["isError"] is null, TextOf(drain));

        var index = JsonNode.Parse(File.ReadAllText(_record["seen/index.json"]))!;
        var server = index["esv::a::XComponent"]!["server"]!;
        Assert.Equal(10, (int)server["samples"]!);
        Assert.Equal(2, (int)server["max"]!);
        Assert.Contains("WLD_B", server["levels"]!.AsArray().Select(l => (string)l!));
        // the one sample of this checkout went to its raw file, and the count was not taken from it
        var raw = JsonNode.Parse(File.ReadAllText(Directory.GetFiles(_record["seen/server"], "*.json").Single()))!;
        Assert.Single(raw["samples"]!.AsArray());
        // the client, with no capture running, is named and does not stop the server's drain
        Assert.Contains("no capture is running on client", TextOf(drain), StringComparison.Ordinal);
    }

    [Fact]
    public void A_side_whose_capture_stopped_is_named_not_failed()
    {
        // After bg3_reset GK.Seen.Status() answered {Running = false} and the drain fell over on
        // "The node must be of type 'JsonObject'".
        Assert.Null(GateKit.Mcp.SeenCapture.NotRunning(new JsonObject { ["Running"] = true }, "server"));
        Assert.Contains("no capture is running on server", GateKit.Mcp.SeenCapture.NotRunning(new JsonObject { ["Running"] = false }, "server"), StringComparison.Ordinal);
        Assert.Contains("bg3_seen_start", GateKit.Mcp.SeenCapture.NotRunning(new JsonArray(), "client"), StringComparison.Ordinal);
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
        // the slow tool is held until ping has been answered, however long that takes
        using var pinged = new ManualResetEventSlim();
        Bridges(_ => JsonValue.Create(true), code =>
        {
            if (code.Contains("sleep", StringComparison.Ordinal)) pinged.Wait(TimeSpan.FromSeconds(15));
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
        pinged.Set();
        var second = (JsonObject)await ReadAsync();
        Assert.Equal(slow, (int)second["id"]!);
    }

    [Fact]
    public async Task A_failed_call_is_a_result_with_isError_and_text()
    {
        await StartAsync();
        var unknown = await CallAsync("bg3_nonsense");
        Assert.True((bool)unknown["isError"]!);
        Assert.Equal("CopyError: unknown tool bg3_nonsense", TextOf(unknown));

        var missing = await CallAsync("bg3_lua", new JsonObject { ["code"] = "return 1" });
        Assert.True((bool)missing["isError"]!);
        Assert.StartsWith("CopyError: bg3_lua needs 'side'", TextOf(missing), StringComparison.Ordinal);
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

    [Fact]
    public void A_registration_names_its_copy_and_starts_what_is_already_built()
    {
        // Several agents each drive a copy of the game. A registration that names no copy drives the
        // installed game, and one that starts through `dotnet run` builds under the running games.
        var registration = McpCommand.Registration("world", "/opt/gatekit/gatekit", null)["mcpServers"]!["gatekit"]!;
        Assert.Equal("/opt/gatekit/gatekit", (string)registration["command"]!);
        Assert.Equal(["--instance", "world", "mcp"], registration["args"]!.AsArray().Select(a => (string)a!));

        var throughDotnet = McpCommand.Registration("menus", "/usr/bin/dotnet", "/opt/gatekit/gatekit.dll")["mcpServers"]!["gatekit"]!;
        Assert.Equal(["/opt/gatekit/gatekit.dll", "--instance", "menus", "mcp"], throughDotnet["args"]!.AsArray().Select(a => (string)a!));

        var refused = Assert.Throws<GateKit.Copies.CopyException>(() => McpCommand.Registration(null, "/opt/gatekit/gatekit", null));
        Assert.Contains("installed game", refused.Message);
    }

    [Fact]
    public void The_repositorys_own_registration_neither_builds_nor_drives_the_installed_game()
    {
        var text = File.ReadAllText(Path.Combine(GateKit.Copies.CopySettings.RepositoryRoot()!, ".mcp.json"));
        var server = JsonNode.Parse(text)!["mcpServers"]!["gatekit"]!;
        var arguments = server["args"]!.AsArray().Select(a => (string)a!).ToList();
        if (arguments.Contains("run")) Assert.Contains("--no-build", arguments);
        Assert.False(string.IsNullOrWhiteSpace((string?)server["env"]?["GATEKIT_INSTANCE"]));
    }
}
