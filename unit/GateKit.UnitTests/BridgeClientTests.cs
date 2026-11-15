using System.Diagnostics;
using System.Text.Json.Nodes;
using GateKit.Bridge;

namespace GateKit.UnitTests;

public sealed class BridgeClientTests : IDisposable
{
    readonly TempFolder _temp = new();
    string _channel => _temp.Path;

    public void Dispose() => _temp.Dispose();

    static JsonNode? Echo(string code) => JsonValue.Create(code[BridgeClient.Prelude.Length..]);

    [Fact]
    public async Task Runs_a_chunk_and_returns_its_result()
    {
        await using var game = new FakeBridge(_channel, "server", Echo);
        var client = new BridgeClient(_channel);
        var result = await client.RunAsync(Side.Server, "return 1", TimeSpan.FromSeconds(5));
        Assert.Equal("return 1", result!.GetValue<string>());
        Assert.Empty(Directory.GetFiles(_channel, "res_*"));
    }

    [Fact]
    public async Task A_lua_error_becomes_an_exception()
    {
        await using var game = new FakeBridge(_channel, "client", _ => throw new InvalidOperationException("boom"));
        var client = new BridgeClient(_channel);
        var error = await Assert.ThrowsAsync<BridgeLuaException>(() => client.RunAsync(Side.Client, "error('boom')", TimeSpan.FromSeconds(5)));
        Assert.Contains("boom", error.LuaError);
    }

    [Fact]
    public async Task Concurrent_callers_each_get_their_own_answer()
    {
        await using var game = new FakeBridge(_channel, "server", Echo);
        var client = new BridgeClient(_channel);
        var calls = Enumerable.Range(0, 200).Select(i => Task.Run(async () =>
        {
            var result = await client.RunAsync(Side.Server, $"return {i}", TimeSpan.FromSeconds(30));
            Assert.Equal($"return {i}", result!.GetValue<string>());
        }));
        await Task.WhenAll(calls);
        Assert.Equal(200, game.Ran);
    }

    [Fact]
    public async Task Commands_queued_before_the_game_side_started_are_not_run()
    {
        var client = new BridgeClient(_channel);
        client.Send(Side.Server, "stale");
        await using var game = new FakeBridge(_channel, "server", Echo);
        var result = await client.RunAsync(Side.Server, "fresh", TimeSpan.FromSeconds(5));
        Assert.Equal("fresh", result!.GetValue<string>());
        Assert.Equal(1, game.Ran);
    }

    [Fact]
    public async Task The_queue_is_pruned_of_lines_the_game_has_run()
    {
        await using var game = new FakeBridge(_channel, "server", Echo);
        var client = new BridgeClient(_channel);
        for (var i = 0; i < 20; i++) await client.RunAsync(Side.Server, $"return {i}", TimeSpan.FromSeconds(5));
        // a heartbeat carrying the last "done", however late it is written
        for (var wait = 0; (client.State(Side.Server) is null || client.Pending(Side.Server) > 0) && wait < 600; wait++) await Task.Delay(15);
        client.Send(Side.Server, "last");
        var lines = File.ReadAllLines(Path.Combine(_channel, "queue_server.jsonl"));
        Assert.True(lines.Length <= 2, $"{lines.Length} lines kept");
    }

    [Fact]
    public async Task An_empty_lua_table_reads_as_the_empty_form_of_what_the_reader_expects()
    {
        // Script Extender writes an empty table as []; a record with no field set is one.
        await using var game = new FakeBridge(_channel, "client", _ => new JsonArray());
        var empty = await new BridgeClient(_channel).RunAsync(Side.Client, "return {}", TimeSpan.FromSeconds(5));
        Assert.Null(empty.Field("stuck"));
        Assert.Empty(empty.AsFields()!);
        Assert.Empty(empty.AsItems()!);
        Assert.Empty(new JsonObject().AsItems()!);
        Assert.Null(((JsonNode?)null).Field("stuck"));
        Assert.Equal("mainmenu", JsonNode.Parse("""{"stuck": "mainmenu"}""").Field("stuck")!.GetValue<string>());
        // A table of the other kind is an error that says what came.
        var list = Assert.Throws<LuaShapeException>(() => JsonNode.Parse("[1, 2]").Field("stuck"));
        Assert.Equal("expected a table of fields from Lua, got a list of 2", list.Message);
        var record = Assert.Throws<LuaShapeException>(() => JsonNode.Parse("""{"a": 1}""").AsItems());
        Assert.Contains("fields a", record.Message);
        Assert.Throws<LuaShapeException>(() => JsonValue.Create("text").AsFields());
    }

    [Fact]
    public void The_bridge_the_host_and_the_contract_name_one_queue_version()
    {
        var repo = GateKit.Copies.CopySettings.RepositoryRoot()!;
        var lua = File.ReadAllText(Path.Combine(repo, "mods", "GateKit", "Mods", "Gwynerva_GateKit", "ScriptExtender", "Lua", "Shared", "Bridge.lua"));
        var contract = File.ReadAllText(Path.Combine(repo, "docs", "contracts", "bridge-protocol.md"));
        var spoken = System.Text.RegularExpressions.Regex.Match(lua, @"\bqueue = (\d+)").Groups[1].Value;
        var stated = System.Text.RegularExpressions.Regex.Match(contract, @"- `queue` — protocol version the bridge speaks: `(\d+)`").Groups[1].Value;
        Assert.Equal(BridgeClient.QueueVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), spoken);
        Assert.Equal(spoken, stated);
    }

    [Fact]
    public async Task A_game_that_stands_still_eats_nothing_of_the_hosts_limit()
    {
        // the limit is 300 ms of a running game; the game stands still for a second and then answers
        await using var game = new FakeBridge(_channel, "server", Echo);
        var client = new BridgeClient(_channel);
        for (var wait = 0; client.State(Side.Server)?.Tick is null && wait < 200; wait++) await Task.Delay(15);
        game.Stalled = true;
        var call = client.RunAsync(Side.Server, "return 7", TimeSpan.FromMilliseconds(300));
        await Task.Delay(1000);
        Assert.False(call.IsCompleted);
        game.Stalled = false;
        Assert.Equal("return 7", (await call)!.GetValue<string>());
    }

    [Fact]
    public async Task A_game_that_runs_and_does_not_answer_is_given_up_in_its_own_ticks()
    {
        await using var game = new FakeBridge(_channel, "server", _ => throw new FakeBridge.NoAnswer());
        var client = new BridgeClient(_channel);
        for (var wait = 0; client.State(Side.Server)?.Tick is null && wait < 200; wait++) await Task.Delay(15);
        var error = await Assert.ThrowsAsync<BridgeTimeoutException>(() => client.RunAsync(Side.Server, "x", TimeSpan.FromMilliseconds(300)));
        Assert.Contains("ticks of the side", error.Why, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_side_whose_lua_state_started_again_is_given_up_at_once()
    {
        await using var game = new FakeBridge(_channel, "server", _ => throw new FakeBridge.NoAnswer());
        var client = new BridgeClient(_channel);
        for (var wait = 0; client.State(Side.Server)?.Tick is null && wait < 200; wait++) await Task.Delay(15);
        var call = client.RunAsync(Side.Server, "x", TimeSpan.FromSeconds(60));
        await Task.Delay(100);
        game.Reboot();
        var watch = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<BridgeTimeoutException>(() => call);
        Assert.Contains("started again", error.Why, StringComparison.Ordinal);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Waiting_times_out_with_the_last_heartbeat()
    {
        var client = new BridgeClient(_channel);
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<BridgeTimeoutException>(() => client.RunAsync(Side.Client, "x", TimeSpan.FromMilliseconds(300)));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3));
    }
}
