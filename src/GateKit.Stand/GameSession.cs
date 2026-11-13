using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using GateKit.Bridge;
using GateKit.Platform;

namespace GateKit.Stand;

/// <summary>
/// A running game as the stand sees it: the bridge's heartbeats and Lua on either side, plus the
/// things built from them (checkpoint, reset, scene skipping). Timings are the old stand's.
/// </summary>
public sealed class GameSession
{
    readonly StandLayout _layout;
    readonly Func<bool> _alive;
    readonly TextWriter _out;

    public GameSession(StandLayout layout, Func<bool> gameAlive, TextWriter output)
    {
        _layout = layout;
        _alive = gameAlive;
        _out = output;
        Bridge = new BridgeClient(layout.Channel);
    }

    public BridgeClient Bridge { get; }

    /// <summary>The name of the last checkpoint, also kept in the state folder for later commands.</summary>
    public string? CheckpointName { get; set; }

    /// <summary>Default heartbeat age: older than this means the side is not there.</summary>
    public static readonly TimeSpan Fresh = TimeSpan.FromSeconds(5);

    public Heartbeat? State(Side side, TimeSpan? maxAge) => Bridge.State(side, maxAge);
    public Heartbeat? State(Side side) => Bridge.State(side, Fresh);

    static string Describe(Heartbeat? state) => state is null ? "none"
        : $"{{gameState: {state.GameState}, boot: {state.Boot.ToString(CultureInfo.InvariantCulture)}, done: {state.Done}}}";

    /// <summary>Waits for a game state; with <paramref name="since"/> (a start) prints when each state was first seen.</summary>
    public async Task<TimeSpan> WaitStateAsync(string wanted, Side side, TimeSpan timeout, Stopwatch? since = null, CancellationToken cancel = default)
    {
        var watch = Stopwatch.StartNew();
        string? last = null;
        while (watch.Elapsed < timeout)
        {
            var current = State(side)?.GameState;
            if (since is not null && current is not null && current != last)
            {
                _out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {since.Elapsed.TotalSeconds,5:0.0} s  {current}"));
                last = current;
            }
            if (current == wanted) return watch.Elapsed;
            if (!_alive()) throw new StandException("the game process is gone");
            await Task.Delay(500, cancel);
        }
        throw new StandException($"{side.Name()} did not reach state {wanted} in {timeout.TotalSeconds:0} s; last: {Describe(State(side, null))}");
    }

    /// <summary>Runs a Lua chunk on "client", "server" or "ui" (the client inside the UI's own
    /// context, needed for Ext.UI) and returns its result. Log lines are printed as "  [side] line".</summary>
    public async Task<JsonNode?> LuaAsync(string side, string code, TimeSpan? timeout = null, CancellationToken cancel = default)
    {
        var ui = side.Equals("ui", StringComparison.OrdinalIgnoreCase);
        var target = ui ? Side.Client : SideNames.Parse(side);
        var limit = timeout ?? TimeSpan.FromSeconds(20);
        string id;
        try { id = Bridge.Send(target, code, ui); }
        catch (IOException e) { throw new StandException(e.Message, e); }
        Answer answer;
        try { answer = await Bridge.WaitAsync(target, id, limit, cancel); }
        catch (BridgeTimeoutException)
        {
            throw new StandException($"no answer from {target.Name()} in {limit.TotalSeconds:0} s (state: {Describe(State(target, null))})");
        }
        foreach (var line in answer.Log ?? []) _out.WriteLine($"  [{target.Name()}] {line}");
        if (!answer.Ok) throw new StandException($"Lua error on {target.Name()}:\n{answer.Error}");
        return answer.Result;
    }

    public Task<JsonNode?> ClientAsync(string code, TimeSpan? timeout = null) => LuaAsync("client", code, timeout);
    public Task<JsonNode?> ServerAsync(string code, TimeSpan? timeout = null) => LuaAsync("server", code, timeout);
    public Task<JsonNode?> UiAsync(string code, TimeSpan? timeout = null) => LuaAsync("ui", code, timeout);

    /// <summary>Queues a chunk without waiting (for chunks that tear the Lua state down).</summary>
    public void Post(Side side, string code)
    {
        try { Bridge.Send(side, code); }
        catch (IOException e) { throw new StandException(e.Message, e); }
    }

    // --- a clean world without a restart ---
    // Loading a save made right after the start gives the world back as it was in 12-14 s
    // (no module load at all), against 37-45 s for a fresh process.

    IReadOnlySet<string> SaveFolders()
    {
        var profiles = Path.Combine(_layout.Profile, "PlayerProfiles");
        if (!Directory.Exists(profiles)) return new HashSet<string>();
        return Directory.EnumerateDirectories(profiles)
            .Select(p => Path.Combine(p, "Savegames", "Story"))
            .Where(Directory.Exists)
            .SelectMany(Directory.EnumerateDirectories)
            .ToHashSet();
    }

    /// <summary>Saves the world as it is now; <see cref="ResetAsync"/> returns to it. The save is the stand's own.</summary>
    public async Task<string> CheckpointAsync(TimeSpan? timeout = null)
    {
        await WaitStateAsync("Running", Side.Server, TimeSpan.FromSeconds(60));
        var before = SaveFolders();
        foreach (var side in new[] { "client", "server" }) await LuaAsync(side, "GK.AutoSave(true)"); // the bridge keeps autosaves off
        List<string> found = [];
        try
        {
            await ServerAsync("Osi.AutoSave()");
            var deadline = Stopwatch.StartNew();
            while (found.Count == 0)
            {
                if (deadline.Elapsed > (timeout ?? TimeSpan.FromSeconds(40))) throw new StandException("the game made no save for the checkpoint");
                await Task.Delay(500);
                found = SaveFolders().Except(before).Where(p => Directory.EnumerateFiles(p, "*.lsv").Any()).ToList();
            }
        }
        finally
        {
            foreach (var side in new[] { "client", "server" }) await LuaAsync(side, "GK.AutoSave(false)");
        }
        await Task.Delay(1000); // let the game finish writing it
        CheckpointName = found.Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).First();
        Directory.CreateDirectory(_layout.State);
        File.WriteAllText(_layout.CheckpointRecord, CheckpointName, JsonFiles.Utf8);
        return CheckpointName;
    }

    /// <summary>Brings back the world of the last checkpoint without restarting the game. Returns
    /// the time until both sides answer again and (unless <paramref name="skipIntro"/> is false)
    /// the level's opening scene has been played through.</summary>
    public async Task<TimeSpan> ResetAsync(TimeSpan? timeout = null, bool skipIntro = true)
    {
        var name = CheckpointName;
        if (string.IsNullOrEmpty(name) && File.Exists(_layout.CheckpointRecord)) name = File.ReadAllText(_layout.CheckpointRecord).Trim();
        if (string.IsNullOrEmpty(name)) throw new StandException("no checkpoint to return to; run `checkpoint` first");
        var limit = timeout ?? TimeSpan.FromSeconds(120);
        var watch = Stopwatch.StartNew();
        var boots = new Dictionary<Side, double?> { [Side.Client] = State(Side.Client)?.Boot, [Side.Server] = State(Side.Server)?.Boot };
        // No answer is awaited: the load tears the Lua state down before one is written.
        Post(Side.Server, $"Osi.LoadGame(\"{name}\")");
        foreach (var side in new[] { Side.Client, Side.Server })
        {
            while (true)
            {
                var state = State(side);
                if (state is not null && state.Boot != boots[side] && state.GameState == "Running") break;
                if (!_alive()) throw new StandException("the game closed during the reset");
                if (watch.Elapsed > limit)
                    throw new StandException($"{side.Name()} did not come back from the reset in {limit.TotalSeconds:0} s; last: {Describe(State(side, null))}");
                await Task.Delay(300);
            }
            await LuaAsync(side.Name(), "return true");
        }
        if (skipIntro) await SkipScenesAsync();
        return watch.Elapsed;
    }

    /// <summary>Plays whatever dialogue or cutscene runs twenty times faster and returns when none
    /// is left (a level's opening scene starts again after every reset).</summary>
    public async Task SkipScenesAsync(TimeSpan? appear = null, TimeSpan? timeout = null)
    {
        const string count = "local n = 0; for _, s in ipairs(GK.Scenes()) do if not s.gameplay then n = n + 1 end end; return n";
        await ClientAsync("return GK.FastScenes(20)");
        try
        {
            var started = Stopwatch.StartNew();
            TimeSpan? seenAt = null;
            while (started.Elapsed < (timeout ?? TimeSpan.FromSeconds(60)))
            {
                var running = await ClientAsync(count);
                if (running is JsonValue v && v.TryGetValue<double>(out var n) && n > 0) seenAt ??= started.Elapsed;
                else if (seenAt is not null || started.Elapsed > (appear ?? TimeSpan.FromSeconds(3))) break;
                await Task.Delay(400);
            }
        }
        finally
        {
            await ClientAsync("return GK.FastScenes(1)");
        }
    }

    /// <summary>Restarts both Lua states in place (after loose files changed) and waits until both answer.</summary>
    public async Task<TimeSpan> ResetLuaAsync(TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(30);
        var watch = Stopwatch.StartNew();
        var boots = new Dictionary<Side, double?> { [Side.Client] = State(Side.Client)?.Boot, [Side.Server] = State(Side.Server)?.Boot };
        foreach (var side in new[] { Side.Server, Side.Client }) Post(side, "Ext.Debug.Reset()");
        foreach (var side in new[] { Side.Client, Side.Server })
        {
            while (true)
            {
                var state = State(side);
                if (state is not null && state.Boot != boots[side]) break;
                if (watch.Elapsed > limit)
                    throw new StandException($"{side.Name()} Lua did not come back after the reset (a Lua error in the mod? see `gatekit logs`)");
                await Task.Delay(200);
            }
            await LuaAsync(side.Name(), "return true");
        }
        return watch.Elapsed;
    }
}
