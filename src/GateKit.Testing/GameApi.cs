using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using GateKit.Bridge;
using GateKit.Copies;
using MoonSharp.Interpreter;

namespace GateKit.Testing;

/// <summary>
/// The `game` object a test sees: the running game, the copy's commands and a few file
/// helpers (docs/writing-tests.md, "The instruments at hand").
/// </summary>
public sealed class GameApi
{
    readonly Copies.GameCopy _copy;
    readonly GameSession _session;
    readonly Script _script;

    public GameApi(Script script, Copies.GameCopy copy, GameSession session, string outFolder, Steps? steps = null)
    {
        _script = script;
        _copy = copy;
        _session = session;
        OutFolder = outFolder;
        Steps = steps ?? new Steps();
        Table = Build();
    }

    /// <summary>The test's named steps: the class every command carries while one runs.</summary>
    public Steps Steps { get; }

    /// <summary>Where this test's screenshots and files go.</summary>
    public string OutFolder { get; set; }

    public Table Table { get; }

    public GameSession Session => _session;

    /// <summary>The kinds of the event log the test named: to game.events, to game.wait_attention,
    /// and the dialogue's through game.dialog and game.answer. What it asked for is no surprise
    /// (<see cref="Surprises"/>).</summary>
    public HashSet<string> Asked { get; } = new(StringComparer.Ordinal);

    AttentionReader? _attention;

    /// <summary>The event log of this game as one stream and its attention view, kept for the run.</summary>
    AttentionReader Attention => _attention ??= new AttentionReader(_copy.Layout.Channel);

    /// <summary>A cursor at the end of the log now, as each side of the game says it (its boot and
    /// its last seq): a row the game has made and not yet written to its file is before the mark
    /// too. A side that does not answer is marked by its files.</summary>
    public string Mark()
    {
        var at = EventStream.ParseCursor(Attention.Stream.Mark());
        foreach (var side in new[] { Side.Server, Side.Client })
        {
            if (_session.State(side, GameSession.Fresh) is null) continue;
            try
            {
                var answer = _session.LuaAsync(side.Name(), "return { GK.EventLog.Boot, GK.EventLog.Seq }", TimeSpan.FromSeconds(5), "observation")
                    .GetAwaiter().GetResult();
                if (answer is JsonArray { Count: 2 } pair) at[side] = ((long)pair[0]!.GetValue<double>(), (long)pair[1]!.GetValue<double>());
            }
            catch (Exception e) when (e is CopyException or BridgeLuaException or BridgeTimeoutException or InvalidOperationException or FormatException) { }
        }
        return EventStream.FormatCursor(at);
    }

    // --- helpers ---

    static TimeSpan? Seconds(DynValue value) =>
        value.Type == DataType.Number ? TimeSpan.FromSeconds(value.Number) : null;

    static string Text(DynValue value, string what) => value.Type switch
    {
        DataType.String => value.String,
        DataType.Number => value.Number.ToString(CultureInfo.InvariantCulture),
        _ => throw new ScriptRuntimeException($"{what} must be a string"),
    };

    /// <summary>Everything `gatekit` says "no" to becomes a Lua error, catchable with pcall.</summary>
    static T Guard<T>(Func<T> action)
    {
        try { return action(); }
        catch (CopyException e) { throw new ScriptRuntimeException(e.Message); }
        catch (BridgeLuaException e) { throw new ScriptRuntimeException(e.Message); }
        catch (BridgeTimeoutException e) { throw new ScriptRuntimeException(e.Message); }
        catch (Platform.PlatformCapabilityException e) { throw new ScriptRuntimeException(e.Message); }
        catch (IOException e) { throw new ScriptRuntimeException(e.Message); }
    }

    static void Guard(Action action) => Guard<bool>(() => { action(); return true; });

    DynValue Lua(string side, string code, TimeSpan? timeout)
    {
        var asked = Stopwatch.StartNew();
        try { return LuaValues.ToLua(_script, Guard(() => _session.LuaAsync(side, code, timeout, Steps.Class).GetAwaiter().GetResult())); }
        finally { if (_waiting == 0) AnswerSeconds += asked.Elapsed.TotalSeconds; }
    }

    /// <summary>The class of each function of `game` that does not run code in the game (that code
    /// carries the step's class itself): a fair step may not reset the world, an observation step
    /// may not press a key. A name missing here is an observation.</summary>
    static readonly Dictionary<string, string> HostClasses = new(StringComparer.Ordinal)
    {
        ["checkpoint"] = "cheat", ["reset"] = "cheat", ["reload"] = "cheat", ["skip_scenes"] = "cheat",
        ["fast_scenes"] = "cheat", ["burst"] = "cheat", ["answer"] = "fair", ["key"] = "fair",
    };

    static readonly HashSet<string> CarriesClass = new(StringComparer.Ordinal) { "lua", "client", "server", "ui", "wait" };

    /// <summary>A path a test gave: relative ones live in the test's output folder.</summary>
    public string Resolve(string path) =>
        Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(OutFolder, path));

    void Register(Table table, string name, Func<ScriptExecutionContext, CallbackArguments, DynValue> function)
    {
        if (CarriesClass.Contains(name)) { table.Set(name, DynValue.NewCallback(function, name)); return; }
        var cls = HostClasses.GetValueOrDefault(name, "observation");
        table.Set(name, DynValue.NewCallback((context, args) =>
        {
            Steps.Check("game." + name, cls);
            return function(context, args);
        }, name));
    }

    // --- the table itself ---

    Table Build()
    {
        var game = new Table(_script);

        Register(game, "lua", (_, a) => Lua(Text(a[0], "side"), Text(a[1], "code"), Seconds(a[2])));
        Register(game, "client", (_, a) => Lua("client", Text(a[0], "code"), Seconds(a[1])));
        Register(game, "server", (_, a) => Lua("server", Text(a[0], "code"), Seconds(a[1])));
        Register(game, "ui", (_, a) => Lua("ui", Text(a[0], "code"), Seconds(a[1])));

        Register(game, "wait", (_, a) => Wait(Text(a[0], "side"), Text(a[1], "expression"),
            Seconds(a[2]) ?? TimeSpan.FromSeconds(30), Seconds(a[3]) ?? TimeSpan.FromSeconds(0.3)));
        Register(game, "wait_state", (_, a) =>
        {
            var wanted = Text(a[0], "state");
            var side = a[1].Type == DataType.String ? SideNames.Parse(a[1].String) : Side.Client;
            var waited = WaitBegins();
            try
            {
                var took = Guard(() => _session.WaitStateAsync(wanted, side, Seconds(a[2]) ?? TimeSpan.FromSeconds(300)).GetAwaiter().GetResult());
                return DynValue.NewNumber(took.TotalSeconds);
            }
            finally { Waited("wait_state", waited.Elapsed); }
        });
        Register(game, "state", (_, a) =>
        {
            var side = a[0].Type == DataType.String ? SideNames.Parse(a[0].String) : Side.Client;
            var state = _session.State(side, Seconds(a[1]) ?? GameSession.Fresh);
            if (state is null) return DynValue.Nil;
            return LuaValues.ToLua(_script, new JsonObject
            {
                ["gameState"] = state.GameState,
                ["boot"] = state.Boot,
                ["done"] = state.Done,
                ["queue"] = state.Queue,
            });
        });

        Register(game, "checkpoint", (_, a) => DynValue.NewString(Guard(() => _session.CheckpointAsync(Seconds(a[0])).GetAwaiter().GetResult())));
        Register(game, "reset", (_, a) =>
        {
            var skipIntro = a[1].Type == DataType.Nil || a[1].CastToBool();
            return DynValue.NewNumber(Guard(() => _session.ResetAsync(Seconds(a[0]), skipIntro).GetAwaiter().GetResult()).TotalSeconds);
        });
        Register(game, "reload", (_, _) => DynValue.NewNumber(Guard(() => _copy.ReloadAsync().GetAwaiter().GetResult()).TotalSeconds));
        Register(game, "skip_scenes", (_, a) =>
        {
            Guard(() => _session.SkipScenesAsync(Seconds(a[0]), Seconds(a[1])).GetAwaiter().GetResult());
            return DynValue.Nil;
        });
        Register(game, "fast_scenes", (_, a) =>
        {
            var speed = a[0].Type == DataType.Number ? a[0].Number : 20;
            return Lua("client", string.Create(CultureInfo.InvariantCulture, $"return GK.FastScenes({speed})"), null);
        });

        Register(game, "dialog", (_, _) =>
        {
            Asked.UnionWith(Surprises.DialogueKinds);
            return Lua("ui", "return GK.Dialog()", null);
        });
        Register(game, "answer", (_, a) =>
        {
            Asked.UnionWith(Surprises.DialogueKinds);
            return Answer(a[0].Type == DataType.Number ? (int)a[0].Number : 1, Seconds(a[1]) ?? TimeSpan.FromSeconds(15));
        });

        // game.key(name[, hold]): hold is seconds the key stays down (N49 of the third coverage run)
        Register(game, "key", (_, a) =>
        {
            Guard(() => _copy.Key(Text(a[0], "key"), a[1].Type == DataType.Number ? a[1].Number : (double?)null));
            return DynValue.Nil;
        });
        Register(game, "shot", (_, a) =>
        {
            var path = Resolve(Text(a[0], "path"));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var width = a[1].Type == DataType.Number ? (int)a[1].Number : 1150;
            // game.shot(path [, width [, overlay]]): overlay = true takes the frame with the extender's layer
            var overlay = a[2].Type == DataType.Boolean && a[2].Boolean;
            return DynValue.NewString(Guard(() => _copy.Shot(path, width, overlay)));
        });
        // The copies running and their tiles in a deck, if one is running (docs/design/deck.md).
        // A test uses it to prove that reading the game never depends on a window being seen.
        Register(game, "windows", (_, _) =>
            LuaValues.ToLua(_script, DeckApi.Report(_copy.Settings, _copy.Platform)));
        Register(game, "logs", (_, a) =>
        {
            var options = a[0].Type == DataType.Table ? a[0].Table : null;
            var lines = Guard(() => _copy.Logs(LuaValues.Flag(options, "story"),
                (int)(LuaValues.OptionalNumber(options, "lines") ?? 80), LuaValues.OptionalString(options, "grep")));
            var table = new Table(_script);
            for (var i = 0; i < lines.Count; i++) table.Set(i + 1, DynValue.NewString(lines[i]));
            return DynValue.NewTable(table);
        });

        Register(game, "sleep", (_, a) =>
        {
            var spent = TimeSpan.FromSeconds(a[0].Type == DataType.Number ? a[0].Number : 0);
            WaitBegins();
            Thread.Sleep(spent);
            Waited("sleep", spent);
            return DynValue.Nil;
        });
        // game.process(): the game process's memory, for a test that measures a leak:
        // { pid, working_mb, private_mb } (nil when no game of this copy runs)
        Register(game, "process", (_, _) =>
        {
            var pid = _copy.GamePids().FirstOrDefault();
            if (pid == 0) return DynValue.Nil;
            try
            {
                using var process = Process.GetProcessById(pid);
                return LuaValues.ToLua(_script, new JsonObject
                {
                    ["pid"] = pid,
                    ["working_mb"] = Math.Round(process.WorkingSet64 / 1048576.0, 1),
                    ["private_mb"] = Math.Round(process.PrivateMemorySize64 / 1048576.0, 1),
                });
            }
            catch (ArgumentException) { return DynValue.Nil; }   // gone in between
        });
        Register(game, "now", (_, _) => DynValue.NewNumber(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency));

        // --- files (the game's folders, the profile's channel, the test's output) ---
        game.Set("channel", DynValue.NewString(_copy.Layout.Channel));
        game.Set("extender", DynValue.NewString(_copy.Layout.ScriptExtenderFolder));
        game.Set("weave", DynValue.NewString(Path.Combine(_copy.Layout.ScriptExtenderFolder, "Gwynerva_Weave")));
        game.Set("folder", DynValue.NewString(_copy.Layout.Game));
        game.Set("profile", DynValue.NewString(_copy.Layout.Profile));
        game.Set("out", DynValue.NewString(OutFolder));
        Register(game, "path", (_, a) => DynValue.NewString(Resolve(Text(a[0], "path"))));
        Register(game, "save", (_, a) => { Save(Text(a[0], "path"), Text(a[1], "text")); return DynValue.Nil; });
        Register(game, "read", (_, a) =>
        {
            var path = Resolve(Text(a[0], "path"));
            return File.Exists(path) ? DynValue.NewString(File.ReadAllText(path, Encoding.UTF8)) : DynValue.Nil;
        });
        Register(game, "exists", (_, a) => DynValue.NewBoolean(File.Exists(Resolve(Text(a[0], "path"))) || Directory.Exists(Resolve(Text(a[0], "path")))));
        Register(game, "mkdir", (_, a) => { Directory.CreateDirectory(Resolve(Text(a[0], "path"))); return DynValue.Nil; });
        Register(game, "remove", (_, a) =>
        {
            var path = Resolve(Text(a[0], "path"));
            if (Directory.Exists(path)) Platform.FileTree.TryRemoveTree(path);
            else if (File.Exists(path)) FileRetry.TryDelete(path);
            return DynValue.Nil;
        });
        Register(game, "glob", (_, a) =>
        {
            var folder = Resolve(Text(a[0], "folder"));
            var table = new Table(_script);
            if (Directory.Exists(folder))
            {
                var i = 0;
                foreach (var found in Directory.EnumerateFileSystemEntries(folder, Text(a[1], "pattern")).Order(StringComparer.Ordinal))
                    table.Set(++i, DynValue.NewString(found));
            }
            return DynValue.NewTable(table);
        });
        Register(game, "testimage", (_, a) =>
        {
            var path = Resolve(Text(a[0], "path"));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, TestPng((int)a[1].Number, (int)a[2].Number));
            return DynValue.NewString(path);
        });

        // game.pixel(path, x, y): the colour of a picture file at a point given in parts of its
        // width and height (0 to 1), as { r, g, b } from 0 to 255, with the picture's width and height
        Register(game, "pixel", (_, a) =>
        {
            var path = Resolve(Text(a[0], "path"));
            if (!File.Exists(path)) throw new ScriptRuntimeException("game.pixel: no file " + path);
            var picture = GateKit.Platform.ImageReader.Read(File.ReadAllBytes(path));
            var x = Math.Clamp((int)(a[1].Number * picture.Width), 0, picture.Width - 1);
            var y = Math.Clamp((int)(a[2].Number * picture.Height), 0, picture.Height - 1);
            var at = (y * picture.Width + x) * 4;
            return LuaValues.ToLua(_script, new JsonObject
            {
                ["r"] = picture.Bgra[at + 2], ["g"] = picture.Bgra[at + 1], ["b"] = picture.Bgra[at],
                ["width"] = picture.Width, ["height"] = picture.Height,
            });
        });

        Register(game, "burst", (_, a) => Burst((int)a[0].CastToNumber()!, (int)a[1].CastToNumber()!));

        // the event log as one stream (bridge-protocol.md, "One stream"): observation, read from
        // the log's files, asking nothing of the game
        Register(game, "mark", (_, _) => DynValue.NewString(Mark()));
        Register(game, "events", (_, a) =>
        {
            var since = a[0].Type == DataType.String ? a[0].String : null;
            HashSet<string>? kinds = null;
            if (a[1].Type == DataType.Table) kinds = [.. a[1].Table.Values.Select(v => v.String)];
            else if (a[1].Type == DataType.String) kinds = [a[1].String];
            if (kinds is not null) Asked.UnionWith(kinds);
            var (rows, next) = Attention.Stream.Since(since, kinds is null ? null : row => kinds.Contains((string)row["kind"]!));
            var answer = new JsonObject { ["rows"] = new JsonArray([.. rows]), ["next"] = next };
            if (Attention.Stream.Dropped(since) is { Count: > 0 } dropped) answer["dropped"] = dropped;
            return LuaValues.ToLua(_script, answer);
        });
        Register(game, "attention", (_, _) => LuaValues.ToLua(_script, Attention.Report(new AttentionCursor())));
        // game.wait_attention(kinds, timeout, since): the first told item of one of `kinds` after
        // `since` (a game.mark(); default: game.mark() now), whichever side wrote it
        Register(game, "wait_attention", (_, a) =>
        {
            var kinds = Kinds(a[0]);
            Asked.UnionWith(kinds);
            var timeout = Seconds(a[1]) ?? TimeSpan.FromSeconds(30);
            var cursor = new AttentionCursor();
            var from = EventStream.ParseCursor(a[2].Type == DataType.String ? a[2].String : Mark());
            foreach (var (side, at) in from) cursor.Move(side, at.Boot, at.Seq);
            foreach (var side in new[] { Side.Client, Side.Server })
                if (cursor.Seen(side, 0) < 0) cursor.Move(side, 0, 0);
            var guard = Limit(timeout);
            var waited = WaitBegins();
            try
            {
                while (true)
                {
                    foreach (var item in Attention.Report(cursor))
                        if (item?["waiting"] is null && kinds.Contains((string)item!["what"]!)) return LuaValues.ToLua(_script, item);
                    if (guard.Over() is { } why) throw new ScriptRuntimeException($"no {string.Join(" or ", kinds)} came in the event log: {why}");
                    Thread.Sleep(AttentionWait.Poll);
                }
            }
            finally { Waited("wait_attention", waited.Elapsed); }
        });
        // game.wait_row(since, kinds, match, seconds): the first row of one of `kinds` after the cursor
        // `since` (default: game.mark() now) that `match(payload, row)` accepts (every row when it is
        // nil), whichever side wrote it; `seconds` (default 30) of a running game, counted in the
        // game's ticks, only guard against a hang (docs/api-rules.md, rule 3)
        Register(game, "wait_row", (_, a) =>
        {
            var since = a[0].Type == DataType.String ? a[0].String : Mark();
            var kinds = Kinds(a[1]);
            Asked.UnionWith(kinds);
            var match = a[2].Type == DataType.Function || a[2].Type == DataType.ClrFunction ? a[2] : null;
            var waited = WaitBegins();
            try { return WaitRow(since, kinds, match, Seconds(a[3]) ?? TimeSpan.FromSeconds(30)); }
            finally { Waited("wait_row", waited.Elapsed); }
        });
        return game;
    }

    static HashSet<string> Kinds(DynValue value) => value.Type == DataType.Table
        ? value.Table.Values.Select(v => v.String).ToHashSet(StringComparer.Ordinal)
        : new HashSet<string>(StringComparer.Ordinal) { Text(value, "kinds") };

    /// <summary>A limit of a wait in the game's ticks, read from the heads and heartbeats of this copy.</summary>
    TickGuard Limit(TimeSpan limit) => new(_session.Bridge, limit, () => _session.Running, Attention.Log);

    /// <summary>The time the test spent inside a wait of `game`, by the wait's name (seconds): `wait`,
    /// `wait_row`, `wait_attention`, `answer`, `sleep`.</summary>
    public Dictionary<string, double> WaitSeconds { get; } = new(StringComparer.Ordinal);

    /// <summary>The time the test spent in the game's own answers to its chunks (game.server, client,
    /// ui, lua) outside any wait, in seconds.</summary>
    public double AnswerSeconds { get; private set; }

    /// <summary>Both counters as they stand: what a step reads at its begin and its end.</summary>
    public (double Waits, double Answers) Spent => (WaitSeconds.Values.Sum(), AnswerSeconds);

    int _waiting;

    void Waited(string name, TimeSpan spent)
    {
        _waiting = Math.Max(0, _waiting - 1);
        if (_waiting == 0) WaitSeconds[name] = WaitSeconds.GetValueOrDefault(name) + spent.TotalSeconds;
    }

    /// <summary>A wait begins: the chunks it sends count as the wait's time, not as answers.</summary>
    Stopwatch WaitBegins()
    {
        _waiting++;
        return Stopwatch.StartNew();
    }

    /// <summary>The first row of <paramref name="kinds"/> after <paramref name="since"/> that
    /// <paramref name="match"/> accepts: the host's own wait on the event log (AttentionWait), the
    /// match asked here, on the script's thread.</summary>
    DynValue WaitRow(string since, HashSet<string> kinds, DynValue? match, TimeSpan limit)
    {
        var guard = Limit(limit);
        var cursor = since;
        while (true)
        {
            var outcome = AttentionWait.RunAsync(Attention, false, null, limit, guard: guard,
                rows: (cursor, row => kinds.Contains((string)row["kind"]!))).GetAwaiter().GetResult();
            if (outcome.EndedBy != "rows")
                throw new ScriptRuntimeException($"no {string.Join(" or ", kinds)} row came in the event log: {outcome.Why}");
            foreach (var row in outcome.Rows)
            {
                var lua = LuaValues.ToLua(_script, row);
                if (match is null) return lua;
                var payload = lua.Table.Get("payload");
                if (payload.IsNil()) payload = DynValue.NewTable(_script);
                if (_script.Call(match, payload, lua).CastToBool()) return lua;
            }
            cursor = outcome.Next!;
        }
    }

    // --- the parts with a body of their own ---

    DynValue Wait(string side, string expression, TimeSpan timeout, TimeSpan every)
    {
        var guard = Limit(timeout);
        var waited = WaitBegins();
        try
        {
            while (true)
            {
                var value = Lua(side, "return (" + expression + ")", null);
                if (value.CastToBool()) return value;
                if (guard.Over() is { } why) throw new ScriptRuntimeException($"timed out waiting on {side} for: {expression} ({why})");
                Thread.Sleep(every);
            }
        }
        finally { Waited("wait", waited.Elapsed); }
    }

    /// <summary>Chooses answer n and waits until the dialogue shows something else (or ends).</summary>
    DynValue Answer(int n, TimeSpan timeout)
    {
        var before = LuaValues.Describe(Lua("ui", "return GK.Dialog()", null));
        Lua("ui", $"return GK.Answer({n})", null);
        var guard = Limit(timeout);
        var waited = WaitBegins();
        try
        {
            while (guard.Over() is null)
            {
                var now = Lua("ui", "return GK.Dialog()", null);
                if (LuaValues.Describe(now) != before) return now;
                Thread.Sleep(300);
            }
        }
        finally { Waited("answer", waited.Elapsed); }
        throw new ScriptRuntimeException($"the dialogue did not change after answer {n}: {before}");
    }

    /// <summary>Writes a file in one step, as an editor does: the game never reads half of it.</summary>
    void Save(string path, string text)
    {
        var full = Resolve(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + ".tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, full, overwrite: true);
    }

    /// <summary>
    /// Several senders talking to both sides at once: <paramref name="workers"/> of them, each
    /// with its own bridge client and <paramref name="each"/> commands. Every answer must come
    /// back to the sender that asked and carry its own number. Returns the round-trip times.
    /// (The Python test started separate processes; the queue, the lock and the answer
    /// files are the same either way.)
    /// </summary>
    DynValue Burst(int workers, int each)
    {
        var times = new List<double>[workers];
        var tasks = new Task[workers];
        for (var w = 0; w < workers; w++)
        {
            var me = w;
            times[me] = [];
            tasks[me] = Task.Run(async () =>
            {
                var bridge = new BridgeClient(_copy.Layout.Channel);
                for (var i = 0; i < each; i++)
                {
                    var side = (me + i) % 2 != 0 ? Side.Server : Side.Client;
                    var watch = Stopwatch.StartNew();
                    var got = await bridge.RunAsync(side, $"return {{ who = {me}, n = {i} }}", TimeSpan.FromSeconds(30));
                    var who = got?["who"]?.GetValue<double>();
                    var n = got?["n"]?.GetValue<double>();
                    if (who != me || n != i) throw new ScriptRuntimeException($"worker {me} command {i} got {got?.ToJsonString() ?? "nothing"}");
                    times[me].Add(watch.Elapsed.TotalSeconds);
                }
            });
        }
        try { Task.WaitAll(tasks); }
        catch (AggregateException e) { throw new ScriptRuntimeException(e.InnerExceptions[0].Message); }
        var all = times.SelectMany(t => t).Order().ToList();
        var table = new Table(_script);
        for (var i = 0; i < all.Count; i++) table.Set(i + 1, DynValue.NewNumber(all[i]));
        return DynValue.NewTable(table);
    }

    /// <summary>A small RGBA picture made without any imaging library: red grows to the right,
    /// green downwards, with an opaque white frame (the Python test's test_png).</summary>
    public static byte[] TestPng(int width, int height)
    {
        var rows = new List<byte>();
        for (var y = 0; y < height; y++)
        {
            rows.Add(0); // PNG filter type of the row: none
            for (var x = 0; x < width; x++)
            {
                var edge = x == 0 || x == width - 1 || y == 0 || y == height - 1;
                if (edge) rows.AddRange([(byte)255, 255, 255, 255]);
                else rows.AddRange([(byte)(255 * x / width), (byte)(255 * y / height), 64, 255]);
            }
        }
        var header = new List<byte>();
        header.AddRange(BigEndian(width));
        header.AddRange(BigEndian(height));
        header.AddRange([(byte)8, 6, 0, 0, 0]);
        var png = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        png.AddRange(Chunk("IHDR", [.. header]));
        png.AddRange(Chunk("IDAT", Zlib([.. rows])));
        png.AddRange(Chunk("IEND", []));
        return [.. png];

        static byte[] BigEndian(int value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

        static byte[] Chunk(string kind, byte[] data)
        {
            var name = Encoding.ASCII.GetBytes(kind);
            var body = new List<byte>();
            body.AddRange(name);
            body.AddRange(data);
            return [.. BigEndian(data.Length), .. body, .. BigEndian((int)Crc32([.. body]))];
        }

        static uint Crc32(byte[] data)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var value in data)
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(crc & 1));
            }
            return crc ^ 0xFFFFFFFFu;
        }

        static byte[] Zlib(byte[] data)
        {
            using var output = new MemoryStream();
            output.WriteByte(0x78);
            output.WriteByte(0x01);
            using (var deflate = new System.IO.Compression.DeflateStream(output, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
                deflate.Write(data, 0, data.Length);
            uint a = 1, b = 0;
            foreach (var value in data)
            {
                a = (a + value) % 65521;
                b = (b + a) % 65521;
            }
            var adler = (b << 16) | a;
            output.Write([(byte)(adler >> 24), (byte)(adler >> 16), (byte)(adler >> 8), (byte)adler]);
            return output.ToArray();
        }
    }
}
