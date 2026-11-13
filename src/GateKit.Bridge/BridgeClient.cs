using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GateKit.Bridge;

/// <summary>
/// Talks to the bridge mod inside a running game through its channel folder.
/// The protocol is described in docs/contracts/bridge-protocol.md; this class is its host side.
/// Thread-safe, and any number of processes may use the same channel at once.
/// </summary>
public sealed class BridgeClient
{
    /// <summary>Prepended to every chunk so it sees the bridge's API whatever context it runs in.</summary>
    public const string Prelude = "local GK = Mods.GateKit.GK\n";

    static readonly TimeSpan StaleLine = TimeSpan.FromMinutes(10);
    static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    static long _counter;

    public BridgeClient(string channelFolder) => Channel = channelFolder;

    /// <summary>The bridge's folder: &lt;profile&gt;/Script Extender/Gwynerva_GateKit.</summary>
    public string Channel { get; }

    string QueuePath(Side side) => Path.Combine(Channel, $"queue_{side.Name()}.jsonl");
    string LockPath(Side side) => Path.Combine(Channel, $"queue_{side.Name()}.lock");
    string AnswerPath(Side side, string id) => Path.Combine(Channel, $"res_{side.Name()}_{id}.json");
    string StatePath(Side side) => Path.Combine(Channel, $"state_{side.Name()}.json");

    /// <summary>The side's last heartbeat, or null if there is none or it is older than <paramref name="maxAge"/>.</summary>
    public Heartbeat? State(Side side, TimeSpan? maxAge = null)
    {
        var path = StatePath(side);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            if (maxAge is { } age && DateTime.UtcNow - info.LastWriteTimeUtc > age) return null;
            return JsonSerializer.Deserialize(FileRetry.ReadAllText(path), BridgeJson.Default.Heartbeat);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Queues a Lua chunk and returns its id without waiting for the answer.</summary>
    public string Send(Side side, string code, bool ui = false)
    {
        if (ui && side != Side.Client) throw new ArgumentException("ui chunks run on the client", nameof(ui));
        Directory.CreateDirectory(Channel);
        var id = $"{Environment.ProcessId}-{DateTime.UtcNow.Ticks}-{Interlocked.Increment(ref _counter)}";
        using (FileLock.Acquire(LockPath(side), TimeSpan.FromSeconds(30)))
        {
            var queue = QueuePath(side);
            var done = State(side)?.Done ?? 0;
            var now = DateTimeOffset.UtcNow;
            var kept = new List<string>();
            long last = 0;
            var lines = File.Exists(queue) ? FileRetry.ReadAllText(queue).Split('\n', StringSplitOptions.RemoveEmptyEntries) : [];
            foreach (var line in lines)
            {
                Command? entry;
                try { entry = JsonSerializer.Deserialize(line, BridgeJson.Default.Command); }
                catch (JsonException) { continue; }
                if (entry is null) continue;
                last = Math.Max(last, entry.Seq);
                var written = DateTimeOffset.FromUnixTimeMilliseconds((long)(entry.T * 1000));
                if (entry.Seq > done && now - written < StaleLine) kept.Add(line);
            }
            var command = new Command
            {
                Seq = Math.Max(last + 1, now.ToUnixTimeMilliseconds() * 1000 + now.Microsecond),
                Id = id,
                Code = Prelude + code,
                Ui = ui,
                T = now.ToUnixTimeMilliseconds() / 1000.0,
            };
            var newLine = JsonSerializer.Serialize(command, BridgeJson.Default.Command);
            if (kept.Count == lines.Length)
                FileRetry.Run(() => File.AppendAllText(queue, newLine + "\n", Utf8));
            else
            {
                kept.Add(newLine);
                FileRetry.Run(() => File.WriteAllText(queue, string.Concat(kept.Select(l => l + "\n")), Utf8));
            }
            SweepAnswers(side);
        }
        return id;
    }

    /// <summary>Waits for the answer to a command sent with <see cref="Send"/>.</summary>
    public async Task<Answer> WaitAsync(Side side, string id, TimeSpan timeout, CancellationToken cancel = default)
    {
        var path = AnswerPath(side, id);
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancel.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                try
                {
                    // Lua strings are bytes: a string cut mid-character is not valid UTF-8, so decode leniently.
                    var answer = JsonSerializer.Deserialize(FileRetry.ReadAllText(path), BridgeJson.Default.Answer);
                    if (answer?.Id == id)
                    {
                        FileRetry.TryDelete(path);
                        return answer;
                    }
                }
                catch (JsonException) { /* still being written */ }
            }
            await Task.Delay(PollInterval, cancel);
        }
        throw new BridgeTimeoutException(side, timeout, State(side));
    }

    /// <summary>Runs a chunk and returns its result. Throws <see cref="BridgeLuaException"/> on a Lua error.</summary>
    public async Task<JsonNode?> RunAsync(Side side, string code, TimeSpan? timeout = null, bool ui = false, CancellationToken cancel = default)
    {
        var id = Send(side, code, ui);
        var answer = await WaitAsync(side, id, timeout ?? TimeSpan.FromSeconds(20), cancel);
        if (!answer.Ok) throw new BridgeLuaException(side, answer.Error ?? "(no message)", answer.Log ?? []);
        return answer.Result;
    }

    void SweepAnswers(Side side)
    {
        foreach (var path in Directory.EnumerateFiles(Channel, $"res_{side.Name()}_*.json"))
        {
            try
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > StaleLine) File.Delete(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
}

public sealed class BridgeTimeoutException(Side side, TimeSpan timeout, Heartbeat? state)
    : TimeoutException($"no answer from {side.Name()} in {timeout.TotalSeconds:0} s (last heartbeat: {(state is null ? "none" : $"{state.GameState}, boot {state.Boot}")})")
{
    public Side Side { get; } = side;
    public Heartbeat? LastState { get; } = state;
}

public sealed class BridgeLuaException(Side side, string error, IReadOnlyList<string> log)
    : Exception($"Lua error on {side.Name()}:\n{error}")
{
    public Side Side { get; } = side;
    public string LuaError { get; } = error;
    public IReadOnlyList<string> Log { get; } = log;
}
