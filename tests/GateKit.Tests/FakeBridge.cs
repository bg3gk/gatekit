using System.Text.Json;
using System.Text.Json.Nodes;

namespace GateKit.Tests;

/// <summary>
/// A stand-in for the game side of the bridge (the same rules as Shared/Bridge.lua): polls the
/// queue, runs every new line in order, writes answers and a heartbeat. "Running" a chunk means
/// calling <see cref="Handler"/> with its code. Lets the host be tested without the game.
/// </summary>
public sealed class FakeBridge : IAsyncDisposable
{
    readonly string _channel;
    readonly string _side;
    readonly CancellationTokenSource _stop = new();
    readonly Task _loop;
    long _done;
    double _boot = Environment.TickCount64;

    public FakeBridge(string channel, string side, Func<string, JsonNode?> handler)
    {
        _channel = channel;
        _side = side;
        Handler = handler;
        Directory.CreateDirectory(channel);
        _done = ReadQueue().Select(c => c.seq).DefaultIfEmpty(0).Max();
        Beat();
        _loop = Task.Run(Loop);
    }

    public Func<string, JsonNode?> Handler { get; set; }
    public int Ran { get; private set; }

    /// <summary>The game state the heartbeat reports.</summary>
    public string GameState { get; set; } = "Running";

    /// <summary>A new Lua state, as after a reset or a loaded save: a new boot, the queue so far skipped.</summary>
    public void Reboot()
    {
        _done = ReadQueue().Select(c => c.seq).DefaultIfEmpty(_done).Max();
        _boot += 1000;
    }

    async Task Loop()
    {
        while (!_stop.IsCancellationRequested)
        {
            foreach (var (seq, id, code) in ReadQueue())
            {
                if (seq <= _done) continue;
                _done = seq;
                JsonObject answer;
                try { answer = new JsonObject { ["id"] = id, ["side"] = _side, ["ok"] = true, ["result"] = Handler(code) }; }
                catch (Exception e) { answer = new JsonObject { ["id"] = id, ["side"] = _side, ["ok"] = false, ["error"] = e.Message }; }
                Ran++;
                File.WriteAllText(Path.Combine(_channel, $"res_{_side}_{id}.json"), answer.ToJsonString());
            }
            Beat();
            try { await Task.Delay(15, _stop.Token); } catch (OperationCanceledException) { }
        }
    }

    void Beat() => File.WriteAllText(Path.Combine(_channel, $"state_{_side}.json"),
        new JsonObject { ["side"] = _side, ["boot"] = _boot, ["time"] = Environment.TickCount64, ["gameState"] = GameState, ["queue"] = 1, ["done"] = _done }.ToJsonString());

    List<(long seq, string id, string code)> ReadQueue()
    {
        var path = Path.Combine(_channel, $"queue_{_side}.jsonl");
        var list = new List<(long, string, string)>();
        string text;
        try
        {
            using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            text = new StreamReader(s).ReadToEnd();
        }
        catch (IOException) { return list; }
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var node = JsonNode.Parse(line)!;
                list.Add((node["seq"]!.GetValue<long>(), node["id"]!.GetValue<string>(), node["code"]!.GetValue<string>()));
            }
            catch (Exception e) when (e is JsonException or NullReferenceException or InvalidOperationException)
            {
                break; // a line being written: nothing after it may run first
            }
        }
        return list;
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await _loop;
    }
}
