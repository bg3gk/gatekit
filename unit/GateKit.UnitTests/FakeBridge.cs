using System.Text.Json;
using System.Text.Json.Nodes;

namespace GateKit.UnitTests;

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

    /// <summary>Thrown by a handler for a chunk the game never answers, as a hung game does; the
    /// chunks after it are run as usual.</summary>
    public sealed class NoAnswer : Exception;
    public int Ran { get; private set; }

    /// <summary>The game state the heartbeat reports.</summary>
    public string GameState { get; set; } = "Running";

    /// <summary>A new Lua state, as after a reset or a loaded save: a new boot, the queue so far skipped.</summary>
    public void Reboot()
    {
        _done = ReadQueue().Select(c => c.seq).DefaultIfEmpty(_done).Max();
        _boot += 1000;
    }

    /// <summary>The side's tick count, one a pass of the loop, as its heartbeat says it.</summary>
    public long Tick { get; private set; }

    /// <summary>A game that stands still: no tick runs, so nothing is run, answered or beaten.</summary>
    public bool Stalled { get; set; }

    async Task Loop()
    {
        while (!_stop.IsCancellationRequested)
        {
            if (Stalled)
            {
                try { await Task.Delay(15, _stop.Token); } catch (OperationCanceledException) { }
                continue;
            }
            Tick++;
            foreach (var (seq, id, code, cls) in ReadQueue())
            {
                if (seq <= _done) continue;
                _done = seq;
                lock (Received) Received.Add((code, cls));
                JsonObject answer;
                try { answer = new JsonObject { ["id"] = id, ["side"] = _side, ["ok"] = true, ["result"] = Handler(code) }; }
                catch (NoAnswer) { Ran++; continue; }
                catch (Exception e) { answer =new JsonObject { ["id"] = id, ["side"] = _side, ["ok"] = false, ["error"] = e.Message }; }
                Ran++;
                Answer(Path.Combine(_channel, $"res_{_side}_{id}.json"), answer.ToJsonString());
            }
            Beat();
            try { await Task.Delay(15, _stop.Token); } catch (OperationCanceledException) { }
        }
    }

    /// <summary>An answer, written whole: beside the file and moved over it. Two fakes on one channel
    /// (a start retried) answer the same chunk; the second finding the file busy or taken leaves the
    /// first one's answer, and the loop goes on.</summary>
    static void Answer(string path, string text)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $"fake_{Guid.NewGuid():N}.part");
        try
        {
            File.WriteAllText(temporary, text);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temporary); } catch (IOException) { }
        }
    }

    /// <summary>The heartbeat. The host reads this file while it is written, and a second start of
    /// the same game (GameCopy.UpAsync retries when the first heartbeat is late) puts a second fake on
    /// the same channel: a beat that finds the file busy is skipped - the next comes 15 ms later -
    /// rather than failing the test, as it did about once in five full runs.</summary>
    void Beat()
    {
        try
        {
            File.WriteAllText(Path.Combine(_channel, $"state_{_side}.json"),
                new JsonObject { ["side"] = _side, ["boot"] = _boot, ["time"] = Environment.TickCount64, ["gameState"] = GameState, ["queue"] = GateKit.Bridge.BridgeClient.QueueVersion, ["done"] = _done, ["tick"] = Tick }.ToJsonString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // a file being deleted reads as denied on Windows
    }

    /// <summary>Every chunk run, with the class it carried (a test's step), in order.</summary>
    public List<(string Code, string? Class)> Received { get; } = [];

    List<(long seq, string id, string code, string? cls)> ReadQueue()
    {
        var path = Path.Combine(_channel, $"queue_{_side}.jsonl");
        var list = new List<(long, string, string, string?)>();
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
                list.Add((node["seq"]!.GetValue<long>(), node["id"]!.GetValue<string>(), node["code"]!.GetValue<string>(), node["class"]?.GetValue<string>()));
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
