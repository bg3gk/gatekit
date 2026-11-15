using System.Diagnostics;
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
    /// <summary>The protocol version this host speaks, as the bridge announces it in its heartbeat
    /// (<c>queue</c>, docs/contracts/bridge-protocol.md).</summary>
    public const int QueueVersion = 1;

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
    /// <summary>The heartbeat file of a side.</summary>
    public string StatePath(Side side) => Path.Combine(Channel, $"state_{side.Name()}.json");

    /// <summary>The side's last heartbeat, or null if there is none or it is older than <paramref name="maxAge"/>.</summary>
    public Heartbeat? State(Side side, TimeSpan? maxAge = null)
    {
        var path = StatePath(side);
        // A heartbeat caught while it is being rewritten reads empty or cut short; that is a live
        // side mid-write, not a missing one, so it is read again before nothing is answered. A held
        // key once fell back to "needs the bridge" on exactly that moment.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return null;
                if (maxAge is { } age && DateTime.UtcNow - info.LastWriteTimeUtc > age) return null;
                return JsonSerializer.Deserialize(FileRetry.ReadAllText(path), BridgeJson.Default.Heartbeat);
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            {
                if (attempt >= 3) return null;
                Thread.Sleep(15);
            }
        }
    }

    /// <summary>How long ago a side last wrote its heartbeat (null: never). The file's time alone, so a
    /// heartbeat caught half-written still counts as a live one.</summary>
    public TimeSpan? HeartbeatAge(Side side)
    {
        try
        {
            var info = new FileInfo(StatePath(side));
            return info.Exists ? DateTime.UtcNow - info.LastWriteTimeUtc : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>How many queued chunks the side has not run yet (a call in flight).</summary>
    public int Pending(Side side)
    {
        try
        {
            var queue = QueuePath(side);
            if (!File.Exists(queue)) return 0;
            var done = State(side)?.Done ?? long.MaxValue;
            var pending = 0;
            foreach (var line in FileRetry.ReadAllText(queue).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var entry = JsonSerializer.Deserialize(line, BridgeJson.Default.Command);
                    if (entry is not null && entry.Seq > done && DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds((long)(entry.T * 1000)) < StaleLine)
                        pending++;
                }
                catch (JsonException) { }
            }
            return pending;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    /// <summary>Queues a Lua chunk and returns its id without waiting for the answer. <paramref name="class"/>:
    /// fair, cheat or observation - the class the verbs the chunk calls are held to (a test's step).</summary>
    public string Send(Side side, string code, bool ui = false, string? @class = null)
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
                Class = @class,
                T =now.ToUnixTimeMilliseconds() / 1000.0,
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

    /// <summary>The length of a tick the bridge counts its limits in (Shared/Bridge.lua, NOMINAL_TICK_MS).</summary>
    public static readonly TimeSpan NominalTick = TimeSpan.FromMilliseconds(1000.0 / 30);

    /// <summary>How long a side may show no progress at all before a wait gives up on it, at the
    /// least: a guard against a hang (a game that stopped ticking, a chunk that never returns), never
    /// the ordinary end of a wait. A wait whose own limit is longer stands still that long.</summary>
    public static TimeSpan StallGuard { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>A limit as the side's ticks at the nominal rate.</summary>
    public static long LimitTicks(TimeSpan limit) => Math.Max(1, (long)Math.Ceiling(limit / NominalTick));

    /// <summary>Waits for the answer to a command sent with <see cref="Send"/>. The limit is counted
    /// in the side's own ticks, as its heartbeat reports them (<see cref="LimitTicks"/>): a game that
    /// stands still - a load, a stall, a window the system starves - eats nothing of it. A side that
    /// shows no progress for <see cref="StallGuard"/> (or the limit, if longer) is given up as hung,
    /// and a side whose Lua state started again (a new boot) will never answer and is given up at
    /// once. Without a heartbeat that counts ticks the limit is the host's clock.</summary>
    public async Task<Answer> WaitAsync(Side side, string id, TimeSpan timeout, CancellationToken cancel = default, Func<bool>? alive = null)
    {
        var path = AnswerPath(side, id);
        var progress = new Progress(this, side, timeout, alive);
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            if (Collect(path, id) is { } answer) return answer;
            if (progress.Over() is { } why)
            {
                // an answer written in the moment the wait ended is still the answer
                if (Collect(path, id) is { } late) return late;
                throw new BridgeTimeoutException(side, timeout, State(side), why);
            }
            await Task.Delay(PollInterval, cancel);
        }
    }

    static Answer? Collect(string path, string id)
    {
        if (!File.Exists(path)) return null;
        try
        {
            // Lua strings are bytes: a string cut mid-character is not valid UTF-8, so decode leniently.
            var answer = JsonSerializer.Deserialize(FileRetry.ReadAllText(path), BridgeJson.Default.Answer);
            if (answer?.Id != id) return null;
            FileRetry.TryDelete(path);
            return answer;
        }
        catch (JsonException) { return null; /* still being written */ }
    }

    /// <summary>How far a side has come since a wait began: its ticks, read from its heartbeat.</summary>
    internal sealed class Progress(BridgeClient bridge, Side side, TimeSpan limit, Func<bool>? alive = null)
    {
        readonly Stopwatch _since = Stopwatch.StartNew();
        readonly Stopwatch _still = Stopwatch.StartNew();
        readonly Stopwatch _asked = Stopwatch.StartNew();
        readonly Heartbeat? _first = bridge.State(side);
        readonly long _limit = LimitTicks(limit);
        double? _tick = bridge.State(side)?.Tick;

        /// <summary>Ticks the side ran since the wait began (null: its heartbeat counts none).</summary>
        public long? Ticks { get; private set; }

        /// <summary>Why the wait is over without an answer, or null while it goes on.</summary>
        public string? Over()
        {
            if (alive is not null && _asked.Elapsed >= TimeSpan.FromSeconds(1))
            {
                _asked.Restart();
                if (!alive()) return "the game process is gone";
            }
            var now = bridge.State(side);
            if (_first is not null && now is not null && now.Boot != _first.Boot)
                return "the side's Lua state started again (a reset or a load): the command was dropped";
            if (_first?.Tick is not { } start || now?.Tick is not { } tick)
                return _since.Elapsed >= limit ? $"no answer in {limit.TotalSeconds:0.#} s" : null;
            if (_tick != tick) { _tick = tick; _still.Restart(); }
            Ticks = (long)(tick - start);
            if (Ticks >= _limit) return $"no answer in {Ticks} ticks of the side ({limit.TotalSeconds:0.#} s of a running game)";
            var guard = limit > StallGuard ? limit : StallGuard;
            return _still.Elapsed >= guard ? $"the side made no progress for {_still.Elapsed.TotalSeconds:0} s (a hang guard)" : null;
        }
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

public sealed class BridgeTimeoutException(Side side, TimeSpan timeout, Heartbeat? state, string? why = null)
    : TimeoutException($"{side.Name()}: {why ?? $"no answer in {timeout.TotalSeconds:0} s"} (last heartbeat: {(state is null ? "none" : $"{state.GameState}, boot {state.Boot}")})")
{
    /// <summary>Why the wait ended without an answer.</summary>
    public string Why { get; } = why ?? $"no answer in {timeout.TotalSeconds:0} s";

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
