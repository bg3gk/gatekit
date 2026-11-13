using System.Diagnostics;
using System.Text.Json.Nodes;
using GateKit.Bridge;

namespace GateKit.Tests;

public sealed class BridgeClientTests : IDisposable
{
    readonly string _channel = Path.Combine(Path.GetTempPath(), "gatekit-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_channel, recursive: true); } catch (IOException) { }
    }

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
        await Task.Delay(100); // a heartbeat carrying the last "done"
        client.Send(Side.Server, "last");
        var lines = File.ReadAllLines(Path.Combine(_channel, "queue_server.jsonl"));
        Assert.True(lines.Length <= 2, $"{lines.Length} lines kept");
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
