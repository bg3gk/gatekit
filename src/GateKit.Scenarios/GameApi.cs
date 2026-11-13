using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using GateKit.Bridge;
using GateKit.Stand;
using MoonSharp.Interpreter;

namespace GateKit.Scenarios;

/// <summary>
/// The `game` object a scenario sees: the running game, the stand's commands and a few file
/// helpers. The names are the old Python <c>Game</c> object's (docs/contracts/cli.md, 6.4), so
/// a Python scenario translates line by line.
/// </summary>
public sealed class GameApi
{
    readonly Stand.Stand _stand;
    readonly GameSession _session;
    readonly Script _script;

    public GameApi(Script script, Stand.Stand stand, GameSession session, string outFolder)
    {
        _script = script;
        _stand = stand;
        _session = session;
        OutFolder = outFolder;
        Table = Build();
    }

    /// <summary>Where this scenario's screenshots and files go.</summary>
    public string OutFolder { get; set; }

    public Table Table { get; }

    public GameSession Session => _session;

    // --- helpers ---

    static TimeSpan? Seconds(DynValue value) =>
        value.Type == DataType.Number ? TimeSpan.FromSeconds(value.Number) : null;

    static string Text(DynValue value, string what) => value.Type switch
    {
        DataType.String => value.String,
        DataType.Number => value.Number.ToString(CultureInfo.InvariantCulture),
        _ => throw new ScriptRuntimeException($"{what} must be a string"),
    };

    /// <summary>Everything the stand says "no" to becomes a Lua error, catchable with pcall.</summary>
    static T Guard<T>(Func<T> action)
    {
        try { return action(); }
        catch (StandException e) { throw new ScriptRuntimeException(e.Message); }
        catch (BridgeLuaException e) { throw new ScriptRuntimeException(e.Message); }
        catch (BridgeTimeoutException e) { throw new ScriptRuntimeException(e.Message); }
        catch (Platform.PlatformCapabilityException e) { throw new ScriptRuntimeException(e.Message); }
        catch (IOException e) { throw new ScriptRuntimeException(e.Message); }
    }

    static void Guard(Action action) => Guard<bool>(() => { action(); return true; });

    DynValue Lua(string side, string code, TimeSpan? timeout) =>
        LuaValues.ToLua(_script, Guard(() => _session.LuaAsync(side, code, timeout).GetAwaiter().GetResult()));

    /// <summary>A path a scenario gave: relative ones live in the scenario's output folder.</summary>
    public string Resolve(string path) =>
        Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(OutFolder, path));

    void Register(Table table, string name, Func<ScriptExecutionContext, CallbackArguments, DynValue> function) =>
        table.Set(name, DynValue.NewCallback(function, name));

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
            var took = Guard(() => _session.WaitStateAsync(wanted, side, Seconds(a[2]) ?? TimeSpan.FromSeconds(300)).GetAwaiter().GetResult());
            return DynValue.NewNumber(took.TotalSeconds);
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
        Register(game, "reload", (_, _) => DynValue.NewNumber(Guard(() => _stand.ReloadAsync().GetAwaiter().GetResult()).TotalSeconds));
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

        Register(game, "dialog", (_, _) => Lua("ui", "return GK.Dialog()", null));
        Register(game, "answer", (_, a) => Answer(a[0].Type == DataType.Number ? (int)a[0].Number : 1,
            Seconds(a[1]) ?? TimeSpan.FromSeconds(15)));

        Register(game, "key", (_, a) => { Guard(() => _stand.Key(Text(a[0], "key"))); return DynValue.Nil; });
        Register(game, "shot", (_, a) =>
        {
            var path = Resolve(Text(a[0], "path"));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var width = a[1].Type == DataType.Number ? (int)a[1].Number : 1150;
            return DynValue.NewString(Guard(() => _stand.Shot(path, width)));
        });
        // The copies running and their tiles in a deck, if one is running (docs/design/deck.md).
        // A scenario uses it to prove that reading the game never depends on a window being seen.
        Register(game, "windows", (_, _) =>
            LuaValues.ToLua(_script, DeckApi.Report(_stand.Settings, _stand.Platform)));
        Register(game, "logs", (_, a) =>
        {
            var options = a[0].Type == DataType.Table ? a[0].Table : null;
            var lines = Guard(() => _stand.Logs(LuaValues.Flag(options, "story"),
                (int)(LuaValues.OptionalNumber(options, "lines") ?? 80), LuaValues.OptionalString(options, "grep")));
            var table = new Table(_script);
            for (var i = 0; i < lines.Count; i++) table.Set(i + 1, DynValue.NewString(lines[i]));
            return DynValue.NewTable(table);
        });

        Register(game, "sleep", (_, a) =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(a[0].Type == DataType.Number ? a[0].Number : 0));
            return DynValue.Nil;
        });
        Register(game, "now", (_, _) => DynValue.NewNumber(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency));

        // --- files (the game's folders, the profile's channel, the scenario's output) ---
        game.Set("channel", DynValue.NewString(_stand.Layout.Channel));
        game.Set("extender", DynValue.NewString(_stand.Layout.ScriptExtenderFolder));
        game.Set("weave", DynValue.NewString(Path.Combine(_stand.Layout.ScriptExtenderFolder, "Gwynerva_Weave")));
        game.Set("folder", DynValue.NewString(_stand.Layout.Game));
        game.Set("profile", DynValue.NewString(_stand.Layout.Profile));
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

        Register(game, "burst", (_, a) => Burst((int)a[0].CastToNumber()!, (int)a[1].CastToNumber()!));
        return game;
    }

    // --- the parts with a body of their own ---

    DynValue Wait(string side, string expression, TimeSpan timeout, TimeSpan every)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var value = Lua(side, "return (" + expression + ")", null);
            if (value.CastToBool()) return value;
            if (watch.Elapsed > timeout) throw new ScriptRuntimeException($"timed out waiting on {side} for: {expression}");
            Thread.Sleep(every);
        }
    }

    /// <summary>Chooses answer n and waits until the dialogue shows something else (or ends).</summary>
    DynValue Answer(int n, TimeSpan timeout)
    {
        var before = LuaValues.Describe(Lua("ui", "return GK.Dialog()", null));
        Lua("ui", $"return GK.Answer({n})", null);
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            var now = Lua("ui", "return GK.Dialog()", null);
            if (LuaValues.Describe(now) != before) return now;
            Thread.Sleep(300);
        }
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
    /// (The Python scenario started separate processes; the queue, the lock and the answer
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
                var bridge = new BridgeClient(_stand.Layout.Channel);
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
    /// green downwards, with an opaque white frame (the Python scenario's test_png).</summary>
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
