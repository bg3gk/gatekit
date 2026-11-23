using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using GateKit.Bridge;
using GateKit.Platform;

namespace GateKit.Copies;

/// <summary>
/// A running game as `gatekit` sees it: the bridge's heartbeats and Lua on either side, plus the
/// things built from them (checkpoint, reset, scene skipping).
/// </summary>
public sealed class GameSession
{
    readonly CopyLayout _layout;
    readonly Func<bool> _alive;
    readonly TextWriter _out;
    readonly Func<string>? _whyGone;

    /// <param name="whyGone">Why the game process is gone, from what witnessed its end (GameCopy.GoneReason).</param>
    public GameSession(CopyLayout layout, Func<bool> gameAlive, TextWriter output, Func<string>? whyGone = null)
    {
        _layout = layout;
        _alive = gameAlive;
        _out = output;
        _whyGone = whyGone;
        Bridge = new BridgeClient(layout.Channel);
    }

    /// <summary>"the game process is gone", with what is known of how it ended.</summary>
    public string Gone() => _whyGone?.Invoke() ?? "the game process is gone";

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
    /// With <paramref name="inMenu"/>, every couple of seconds the side spends in "Menu" (while
    /// another state is wanted) it is called with how long the menu has lasted; it may throw to
    /// give up, and answers true when it moved the game on (the menu time then starts again).
    /// <paramref name="lookEvery"/> (default 2 s) is how often; zero calls it again as soon as it
    /// answers, for a call that itself waits for the game's next event.
    public async Task<TimeSpan> WaitStateAsync(string wanted, Side side, TimeSpan timeout, Stopwatch? since = null, CancellationToken cancel = default,
        Func<TimeSpan, Task<bool>>? inMenu = null, TimeSpan? lookEvery = null)
    {
        var every = lookEvery ?? TimeSpan.FromSeconds(2);
        var watch = Stopwatch.StartNew();
        string? last = null;
        Stopwatch? menu = null;
        var asked = TimeSpan.Zero;
        while (watch.Elapsed < timeout)
        {
            var current = State(side)?.GameState;
            if (since is not null && current is not null && current != last)
            {
                _out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {since.Elapsed.TotalSeconds,5:0.0} s  {current}"));
                last = current;
            }
            if (current == wanted) return watch.Elapsed;
            if (current == "Menu" && wanted != "Menu") menu ??= Stopwatch.StartNew(); else { menu = null; asked = TimeSpan.Zero; }
            if (inMenu is not null && menu is not null && menu.Elapsed - asked >= every)
            {
                asked = menu.Elapsed;
                if (await inMenu(menu.Elapsed)) { menu.Restart(); asked = TimeSpan.Zero; }
            }
            if (!_alive()) throw new CopyException(Gone());
            await Task.Delay(500, cancel);
        }
        throw new CopyException($"{side.Name()} did not reach state {wanted} in {timeout.TotalSeconds:0} s; last: {Describe(State(side, null))}");
    }

    /// <summary>Runs a Lua chunk on "client", "server" or "ui" (the client inside the UI's own
    /// context, needed for Ext.UI) and returns its result. Log lines are printed as "  [side] line".</summary>
    public Task<JsonNode?> LuaAsync(string side, string code, TimeSpan? timeout = null, CancellationToken cancel = default) =>
        LuaAsync(side, code, timeout, null, cancel);

    /// <summary>As <see cref="LuaAsync(string, string, TimeSpan?, CancellationToken)"/>, with the verbs the chunk
    /// calls held to <paramref name="class"/> (fair, cheat or observation: a test's step).</summary>
    public async Task<JsonNode?> LuaAsync(string side, string code, TimeSpan? timeout, string? @class, CancellationToken cancel = default)
    {
        // Every chunk for a game goes through here, whoever sends it (a command, a test, an MCP
        // tool, the deck), and a chunk can change anything: the installed game takes one only when
        // it was named.
        _layout.RequireNamed("a chunk of Lua for the game");
        var ui = side.Equals("ui", StringComparison.OrdinalIgnoreCase);
        var target = ui ? Side.Client : SideNames.Parse(side);
        var limit = timeout ?? TimeSpan.FromSeconds(20);
        // No game of this copy - no process, and no fresh heartbeat on its channel: said at once,
        // rather than a chunk queued for nobody and a wait that ends in "no answer". A call that
        // names no copy is for the installed game's channel, which no instance ever writes.
        if (!(Bridge.HeartbeatAge(target) < Fresh) && !_alive()) throw new CopyException(NotRunning());
        string id;
        try { id = Bridge.Send(target, code, ui, @class); }
        catch (IOException e) { throw new CopyException(e.Message, e); }
        Answer answer;
        try { answer = await Bridge.WaitAsync(target, id, limit, cancel, _alive); }
        catch (BridgeTimeoutException e)
        {
            var why = e.Why == "the game process is gone" ? Gone() : e.Why;
            throw new CopyException($"no answer from {target.Name()}: {why} (state: {Describe(State(target, null))})");
        }
        foreach (var line in answer.Log ?? []) _out.WriteLine($"  [{target.Name()}] {line}");
        if (!answer.Ok) throw new CopyException($"Lua error on {target.Name()}:\n{answer.Error}");
        return answer.Result;
    }

    /// <summary>Which game this session is for, as a person reads it.</summary>
    public string Target => _layout.Instance is { } instance ? $"instance {instance.Name}" : "the installed game (no --instance)";

    /// <summary>Whether a game process of this copy runs now.</summary>
    public bool Running => _alive();

    string NotRunning() => $"no game is running for {Target}; nothing was sent"
        + (_layout.Instance is null ? " (an instance is reached only with --instance <name> or GATEKIT_INSTANCE)" : "");

    public Task<JsonNode?> ClientAsync(string code, TimeSpan? timeout = null) => LuaAsync("client", code, timeout);
    public Task<JsonNode?> ServerAsync(string code, TimeSpan? timeout = null) => LuaAsync("server", code, timeout);
    public Task<JsonNode?> UiAsync(string code, TimeSpan? timeout = null) => LuaAsync("ui", code, timeout);

    /// <summary>Queues a chunk without waiting (for chunks that tear the Lua state down).</summary>
    public void Post(Side side, string code)
    {
        try { Bridge.Send(side, code); }
        catch (IOException e) { throw new CopyException(e.Message, e); }
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

    /// <summary>Saves the world as it is now; <see cref="ResetAsync"/> returns to it. The save is the copy's own.</summary>
    public async Task<string> CheckpointAsync(TimeSpan? timeout = null)
    {
        _layout.RequireNamed("checkpoint");
        await WaitStateAsync("Running", Side.Server, TimeSpan.FromSeconds(60));
        // The level it is made in, so a reset ladder never loads a checkpoint of another level.
        string? level = null;
        try { level = await ServerAsync("return GK.Stage.CurrentLevel()", TimeSpan.FromSeconds(10)) is JsonValue v && v.TryGetValue<string>(out var n) ? n : null; }
        catch (CopyException) { }
        var before = SaveFolders();
        foreach (var side in new[] { "client", "server" }) await LuaAsync(side, "GK.AutoSave(true)"); // the bridge keeps autosaves off
        List<string> found = [];
        try
        {
            await ServerAsync("Osi.AutoSave()");
            var deadline = Stopwatch.StartNew();
            while (found.Count == 0)
            {
                if (deadline.Elapsed > (timeout ?? TimeSpan.FromSeconds(40))) throw new CopyException("the game made no save for the checkpoint");
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
        if (level is not null) File.WriteAllText(CheckpointLevelRecord, level, JsonFiles.Utf8);
        else if (File.Exists(CheckpointLevelRecord)) File.Delete(CheckpointLevelRecord);
        return CheckpointName;
    }

    string CheckpointLevelRecord => Path.ChangeExtension(_layout.CheckpointRecord, ".level.txt");

    /// <summary>The level the recorded checkpoint was made in (null: not known).</summary>
    public string? CheckpointLevel => File.Exists(CheckpointLevelRecord) ? File.ReadAllText(CheckpointLevelRecord).Trim() : null;

    /// <summary>Whether there is a checkpoint to return to at all.</summary>
    public bool HasCheckpoint => !string.IsNullOrEmpty(CheckpointName) || File.Exists(_layout.CheckpointRecord);

    /// <summary>Brings back the world of the last checkpoint without restarting the game. Returns
    /// the time until both sides answer again and (unless <paramref name="skipIntro"/> is false)
    /// the level's opening scene has been played through (<paramref name="appear"/>: how long the
    /// scene is given to appear, as <see cref="SkipScenesAsync"/>).</summary>
    public async Task<TimeSpan> ResetAsync(TimeSpan? timeout = null, bool skipIntro = true, TimeSpan? appear = null)
    {
        _layout.RequireNamed("reset");
        var name = CheckpointName;
        if (string.IsNullOrEmpty(name) && File.Exists(_layout.CheckpointRecord)) name = File.ReadAllText(_layout.CheckpointRecord).Trim();
        if (string.IsNullOrEmpty(name)) throw new CopyException("no checkpoint to return to; run `checkpoint` first");
        return await LoadSaveAsync(name, timeout, skipIntro, appear);
    }

    /// <summary>Removes the answers in the channel that no caller waits for any more (those of chunks
    /// posted without a wait, `res_*.json`) written before <paramref name="before"/>: what a new
    /// process's `up` clears, for a game that is kept and taken into another start instead.</summary>
    public int DropUnclaimedAnswers(DateTime before)
    {
        if (!Directory.Exists(_layout.Channel)) return 0;
        var dropped = 0;
        foreach (var answer in Directory.EnumerateFiles(_layout.Channel, "res_*.json").ToList())
            if (File.GetLastWriteTimeUtc(answer) < before)
            {
                FileRetry.TryDelete(answer);
                if (!File.Exists(answer)) dropped++;
            }
        return dropped;
    }

    /// <summary>Takes an existing save of the copy, made in <paramref name="level"/>, as the checkpoint
    /// <see cref="ResetAsync"/> returns to (a save just loaded is the world as it is).</summary>
    public void AdoptCheckpoint(string save, string? level)
    {
        CheckpointName = save;
        Directory.CreateDirectory(_layout.State);
        File.WriteAllText(_layout.CheckpointRecord, save, JsonFiles.Utf8);
        if (level is not null) File.WriteAllText(CheckpointLevelRecord, level, JsonFiles.Utf8);
        else if (File.Exists(CheckpointLevelRecord)) File.Delete(CheckpointLevelRecord);
    }

    /// <summary>Loads the save folder <paramref name="name"/> in the running session by the game's
    /// own load (Osi.LoadGame), as a quick load does: no new process, no main menu. Returns the time
    /// until both sides answer again in Running and (unless <paramref name="skipIntro"/> is false)
    /// the level's opening scene has been played through.</summary>
    public async Task<TimeSpan> LoadSaveAsync(string name, TimeSpan? timeout = null, bool skipIntro = true, TimeSpan? appear = null)
    {
        _layout.RequireNamed("a save's load");
        var limit = timeout ?? TimeSpan.FromSeconds(120);
        var watch = Stopwatch.StartNew();
        var boots = new Dictionary<Side, double?> { [Side.Client] = State(Side.Client)?.Boot, [Side.Server] = State(Side.Server)?.Boot };
        var fromMenu = State(Side.Client)?.GameState == "Menu";
        if (fromMenu)
            // the main menu has no server: the save is loaded through the game's own load window
            await LoadFromMenuAsync(name, limit, watch);
        else
            // No answer is awaited: the load tears the Lua state down before one is written.
            Post(Side.Server, $"Osi.LoadGame(\"{name}\")");
        foreach (var side in new[] { Side.Client, Side.Server })
        {
            while (true)
            {
                var state = State(side);
                // from the menu the client's Lua may live on: its state need only reach Running
                if (state is not null && (state.Boot != boots[side] || (fromMenu && side == Side.Client)) && state.GameState == "Running") break;
                if (!_alive()) throw new CopyException("the game closed during the reset: " + Gone());
                if (watch.Elapsed > limit)
                    throw new CopyException($"{side.Name()} did not come back from the reset in {limit.TotalSeconds:0} s; last: {Describe(State(side, null))}");
                // the state file is written by the bridge at the game state's change (Bridge.lua),
                // so this looks at a file, it does not wait for a heartbeat
                await Task.Delay(50);
            }
            await LuaAsync(side.Name(), "return true");
        }
        if (skipIntro)
        {
            var known = OpeningScene(name);
            var scene = await SkipScenesAsync(known == false ? TimeSpan.Zero : appear);
            if (known is null || known != scene) RecordOpeningScene(name, scene);
        }
        return watch.Elapsed;
    }

    /// <summary>From the main menu: the save folder <paramref name="name"/> chosen in the game's load
    /// window (GK.Gui.LoadSave: its title, the folder's name after "__", and the time the window
    /// shows, the minute its .lsv was written, one either side) and its Load pressed, as a player
    /// does. Each step is answered by what the window did (the window opened, the load began).</summary>
    async Task LoadFromMenuAsync(string name, TimeSpan limit, Stopwatch watch)
    {
        var folder = Path.Combine(_layout.Saves, name);
        var lsv = Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.lsv").FirstOrDefault() : null;
        if (lsv is null) throw new CopyException($"no save {name} in {_layout.Saves}");
        var at = File.GetLastWriteTime(lsv);
        var times = new[] { at, at.AddMinutes(-1), at.AddMinutes(1) }
            .Select(t => "\"" + t.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture) + "\"");
        var cut = name.LastIndexOf("__", StringComparison.Ordinal);
        var title = cut >= 0 ? name[(cut + 2)..] : name;
        var call = $"return GK.Gui.LoadSave([==[{title}]==], {{ {string.Join(", ", times)} }})";
        for (var step = 0; step < 4; step++)
        {
            if (watch.Elapsed > limit) throw new CopyException($"the load window did not load {name} in {limit.TotalSeconds:0} s");
            var answer = await LuaAsync("ui", call, TimeSpan.FromSeconds(20)) as JsonObject
                ?? throw new CopyException("GK.Gui.LoadSave gave no answer (a bridge without it?)");
            if (answer["ok"]?.GetValue<bool>() != true)
                throw new CopyException("the load window: " + (answer["error"]?.ToString() ?? GateKit.Json.JsonText.Inline(answer)));
            if (answer["waiting"]?.GetValue<bool>() != true) return;
        }
        throw new CopyException("the load window never got to loading " + name);
    }

    /// <summary>Plays whatever dialogue or cutscene runs twenty times faster and returns when none
    /// is left (a level's opening scene starts again after every reset).</summary>
    public async Task<bool> SkipScenesAsync(TimeSpan? appear = null, TimeSpan? timeout = null)
    {
        const string count = "local n = 0; for _, s in ipairs(GK.Scenes()) do if not s.gameplay then n = n + 1 end end; return n";
        static bool Any(JsonNode? running) => running is JsonValue v && v.TryGetValue<double>(out var n) && n > 0;
        // Known to come with no scene (appear 0): one look, and the scene speed is not touched.
        if (appear == TimeSpan.Zero && !Any(await ClientAsync(count))) return false;
        await ClientAsync("return GK.FastScenes(20)");
        TimeSpan? seenAt = null;
        try
        {
            var started = Stopwatch.StartNew();
            while (started.Elapsed < (timeout ?? TimeSpan.FromSeconds(60)))
            {
                var running = await ClientAsync(count);
                if (Any(running)) seenAt ??= started.Elapsed;
                else if (seenAt is not null || started.Elapsed > (appear ?? TimeSpan.FromSeconds(3))) break;
                await Task.Delay(400);
            }
        }
        finally
        {
            await ClientAsync("return GK.FastScenes(1)");
        }
        return seenAt is not null;
    }

    // Whether a save, loaded before in this copy, came with an opening scene: the same save's story
    // does the same at every load, so the time given a scene to appear (no game event says that none
    // will) is spent only at a save's first load. state/opening-scenes.json { save: true|false }.
    string OpeningScenesRecord => Path.Combine(_layout.State, "opening-scenes.json");

    bool? OpeningScene(string save)
    {
        return File.Exists(OpeningScenesRecord) && JsonFiles.ReadObject(OpeningScenesRecord)[save] is JsonValue v
            && v.TryGetValue<bool>(out var b) ? b : null;
    }

    void RecordOpeningScene(string save, bool scene)
    {
        try
        {
            JsonFiles.Edit(OpeningScenesRecord, o => o[save] = scene);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Restarts both Lua states in place (after loose files changed) and waits until both answer.</summary>
    public async Task<TimeSpan> ResetLuaAsync(TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(30);
        var watch = Stopwatch.StartNew();
        var boots = new Dictionary<Side, double?> { [Side.Client] = State(Side.Client)?.Boot, [Side.Server] = State(Side.Server)?.Boot };
        // What the standing capture has gathered is spilled first (GK.Seen.Spill): a reset of the
        // Lua state took it with it (D27 of the 2026-11-14 day run), and an armed capture starts
        // again by itself after it. A bridge without Spill just resets.
        foreach (var side in new[] { Side.Server, Side.Client })
            Post(side, "pcall(function() if GK and GK.Seen and GK.Seen.Spill then GK.Seen.Spill(\"lua reset\") end end)\nExt.Debug.Reset()");
        foreach (var side in new[] { Side.Client, Side.Server })
        {
            while (true)
            {
                var state = State(side);
                if (state is not null && state.Boot != boots[side]) break;
                if (watch.Elapsed > limit)
                    throw new CopyException($"{side.Name()} Lua did not come back after the reset (a Lua error in the mod? see `gatekit logs`)");
                await Task.Delay(200);
            }
            await LuaAsync(side.Name(), "return true");
        }
        return watch.Elapsed;
    }
}
