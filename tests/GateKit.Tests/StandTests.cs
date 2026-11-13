using System.Text.Json;
using System.Text.Json.Nodes;
using GateKit.Bridge;
using GateKit.Stand;

namespace GateKit.Tests;

/// <summary>The stand against FakeBridge (the game stand-in) and FakePlatform (no real process).</summary>
public sealed class StandTests : IAsyncDisposable
{
    readonly FakeGame _game = new();
    readonly List<FakeBridge> _bridges = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var bridge in _bridges) await bridge.DisposeAsync();
        _game.Dispose();
    }

    static string Chunk(string code) => code[BridgeClient.Prelude.Length..];

    (FakeBridge client, FakeBridge server) Bridges(Stand.Stand stand, Func<string, JsonNode?> client, Func<string, JsonNode?> server)
    {
        var c = new FakeBridge(stand.Layout.Channel, "client", client);
        var s = new FakeBridge(stand.Layout.Channel, "server", server);
        _bridges.Add(c);
        _bridges.Add(s);
        return (c, s);
    }

    [Fact]
    public async Task Exec_returns_the_result_and_prints_the_chunks_log()
    {
        var stand = _game.Stand();
        Bridges(stand, code => JsonValue.Create(Chunk(code)), _ => JsonNode.Parse("{\"party\": [1, 2]}"));
        var result = await stand.ExecAsync("server", "return GK.Party()");
        Assert.Equal(2, result!["party"]!.AsArray().Count);
        Assert.Equal("return 1", (await stand.ExecAsync("ui", "return 1"))!.GetValue<string>());
        var file = _game.Temp.Write("chunk.lua", "﻿return 'from a file'");
        Assert.Equal("return 'from a file'", (await stand.ExecAsync("client", "@" + file))!.GetValue<string>());
    }

    [Fact]
    public async Task A_lua_error_or_silence_becomes_an_explained_error()
    {
        var stand = _game.Stand();
        Bridges(stand, _ => throw new InvalidOperationException("attempt to index a nil value"), _ => null);
        var error = await Assert.ThrowsAsync<StandException>(() => stand.ExecAsync("client", "x.y = 1"));
        Assert.StartsWith("Lua error on client:\nattempt to index", error.Message);
        var silent = new Stand.Stand(_game.Settings("nobody"), _game.Platform, _game.Output);
        var timeout = await Assert.ThrowsAsync<StandException>(() => silent.ExecAsync("server", "return 1", TimeSpan.FromMilliseconds(300)));
        Assert.StartsWith("no answer from server", timeout.Message);
    }

    [Fact]
    public async Task Checkpoint_saves_and_reset_loads_it_again()
    {
        var stand = _game.Stand();
        _game.Running(stand);
        var saves = Path.Combine(stand.Layout.Profile, "PlayerProfiles", "Public", "Savegames", "Story");
        var sent = new List<string>();
        FakeBridge? client = null, server = null;
        (client, server) = Bridges(stand,
            code => { lock (sent) sent.Add("client: " + Chunk(code)); return JsonValue.Create(0); },
            code =>
            {
                var chunk = Chunk(code);
                lock (sent) sent.Add("server: " + chunk);
                if (chunk == "Osi.AutoSave()")
                {
                    Directory.CreateDirectory(Path.Combine(saves, "AutoSave_7"));
                    File.WriteAllText(Path.Combine(saves, "AutoSave_7", "AutoSave_7.lsv"), "save");
                }
                if (chunk.StartsWith("Osi.LoadGame(", StringComparison.Ordinal))
                {
                    client!.Reboot();
                    server!.Reboot();
                }
                return null;
            });

        var session = stand.Session();
        Assert.Equal("AutoSave_7", await session.CheckpointAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("AutoSave_7", File.ReadAllText(stand.Layout.CheckpointRecord));
        lock (sent)
        {
            Assert.Contains("server: GK.AutoSave(true)", sent);
            Assert.Equal("server: GK.AutoSave(false)", sent.Last(s => s.StartsWith("server", StringComparison.Ordinal)));
        }

        var later = stand.Session(); // a later command: the name comes from the record
        var took = await later.ResetAsync(TimeSpan.FromSeconds(20), skipIntro: false);
        Assert.True(took < TimeSpan.FromSeconds(20));
        lock (sent) Assert.Contains("server: Osi.LoadGame(\"AutoSave_7\")", sent);
    }

    [Fact]
    public async Task Reset_without_a_checkpoint_says_so()
    {
        var stand = _game.Stand();
        var error = await Assert.ThrowsAsync<StandException>(() => stand.Session().ResetAsync());
        Assert.Contains("checkpoint", error.Message);
    }

    [Fact]
    public async Task Linked_lua_is_copied_in_and_both_states_restart()
    {
        var stand = _game.Stand();
        FakeBridge? client = null, server = null;
        (client, server) = Bridges(stand,
            code => { if (Chunk(code) == "Ext.Debug.Reset()") client!.Reboot(); return JsonValue.Create(true); },
            code => { if (Chunk(code) == "Ext.Debug.Reset()") server!.Reboot(); return JsonValue.Create(true); });
        _game.Temp.Write("mymod/Mods/MyMod/ScriptExtender/Lua/BootstrapClient.lua", "print('hi')");

        Assert.Equal(["MyMod"], stand.Link(_game.Temp["mymod"]));
        var loose = Path.Combine(stand.Layout.LooseTarget("MyMod"), "BootstrapClient.lua");
        Assert.Equal("print('hi')", File.ReadAllText(loose));
        File.WriteAllText(_game.Temp["mymod/Mods/MyMod/ScriptExtender/Lua/BootstrapClient.lua"], "print('changed')");
        var took = await stand.ReloadAsync();
        Assert.Equal("print('changed')", File.ReadAllText(loose));
        Assert.True(took < TimeSpan.FromSeconds(10));

        _game.Platform.Processes.Clear();
        stand.Down();
        Assert.False(File.Exists(loose));
        Assert.False(Directory.Exists(_game.Temp["game/Data/Mods/MyMod"]));
        Assert.True(Directory.Exists(_game.Temp["game/Data/Mods/Loose"]));
    }

    [Fact]
    public async Task Up_prepares_an_instance_starts_and_waits_and_down_cleans_up()
    {
        var stand = _game.Stand("one");
        var packer = (CopyPacker)stand.Packer;
        _game.Platform.OnStart = _ =>
        {
            var c = new FakeBridge(stand.Layout.Channel, "client", _ => null) { GameState = "Running" };
            var s = new FakeBridge(stand.Layout.Channel, "server", _ => null);
            _bridges.Add(c);
            _bridges.Add(s);
        };

        await stand.UpAsync(new UpOptions(Level: "GustavX:TUT_Avernus_C", Mods: ["SomeMod"], GameArgs: ["-extra"]));

        var launch = Assert.Single(_game.Platform.Started);
        Assert.Equal(["--skip-launcher", "-testLoadLevel", "GustavX:TUT_Avernus_C", "-extra"], launch.Arguments);
        Assert.Equal(stand.Layout.Exe, launch.Executable);
        Assert.Equal(stand.Instance!.Root, launch.InstanceRoot);
        Assert.Equal(2, packer.Packed);
        Assert.True(File.Exists(Path.Combine(stand.Layout.Mods, "Gwynerva_Weave.pak")));
        var mods = ModSettings.Folders(File.ReadAllText(stand.Layout.ModSettingsFile));
        Assert.Equal(["GustavX", "SomeMod", "Gwynerva_Weave", "Gwynerva_GateKit"], mods);
        Assert.Equal(mods, ModSettings.Folders(File.ReadAllText(Path.Combine(stand.Layout.Profile, "PlayerProfiles", "Debug_Client_Profile_3", "modsettings.lsx"))));
        Assert.False(Directory.Exists(stand.Layout.Saves)); // --level: no saves copied
        Assert.Equal(0, new FileInfo(stand.Layout.Splash).Length);
        var se = JsonFiles.ReadObject(stand.Layout.SeSettings);
        Assert.False(se["CreateConsole"]!.GetValue<bool>());   // no console window per game
        Assert.True(se["LogRuntime"]!.GetValue<bool>());       // its output goes to a log file instead
        Assert.False(se["EnableLogging"]!.GetValue<bool>());
        Assert.Equal(stand.Layout.SeLogs, se["LogDirectory"]!.ToString());
        Assert.True(se["KeepWindowInBackground"]!.GetValue<bool>());   // and it does not raise its own window
        Assert.False(se["KeepWindowOffScreen"]!.GetValue<bool>());     // no deck is open, so the window is where it can be seen
        // The game is told, in its own environment, not to activate its window when it shows it.
        // SDL reads this and uses SW_SHOWNA instead of SW_SHOW, so the window that has the
        // keyboard keeps it. A variable rather than a call because SDL gives the environment
        // override priority over anything the game sets for itself.
        Assert.Equal("1", launch.Environment!["SDL_WINDOW_NO_ACTIVATION_WHEN_SHOWN"]);
        Assert.Equal(1, _game.Platform.ForegroundKept);        // and the guard still watches, as a net
        Assert.Contains("playable after", _game.Output.ToString());
        Assert.Contains("Running", _game.Output.ToString());
        Assert.Equal("1086940", File.ReadAllText(_game.Temp["game/bin/steam_appid.txt"])); // the real game untouched

        await Assert.ThrowsAsync<StandException>(() => stand.UpAsync(new UpOptions()));  // already running

        Assert.Equal("instance one: game closed", stand.Down());
        Assert.Empty(stand.GamePids());
        Assert.False(File.Exists(stand.Layout.Splash));
        Assert.Contains("one", string.Join("\n", stand.InstanceList()));
    }

    [Fact]
    public async Task Up_on_the_installed_game_backs_up_and_down_restores()
    {
        var stand = _game.Stand();
        var original = File.ReadAllText(stand.Layout.ModSettingsFile);
        _game.Platform.OnStart = _ =>
        {
            _bridges.Add(new FakeBridge(stand.Layout.Channel, "client", _ => null));
            _bridges.Add(new FakeBridge(stand.Layout.Channel, "server", _ => null));
        };
        await stand.UpAsync(new UpOptions(Save: "Save1"));
        Assert.Equal(["--skip-launcher", "-loadSaveGame", "Save1"], _game.Platform.Started.Single().Arguments);
        // no --level, no --mods: the player's own list, plus the bridge
        Assert.Equal(["GustavX", "SomeMod", "OtherMod", "Gwynerva_Weave", "Gwynerva_GateKit"], ModSettings.Folders(File.ReadAllText(stand.Layout.ModSettingsFile)));
        Assert.True(Directory.Exists(stand.Layout.Backup));

        Assert.Equal("game closed, saves and mod list restored", stand.Down());
        Assert.Equal(original, File.ReadAllText(stand.Layout.ModSettingsFile));
        Assert.Equal("{\"Keep\": 1}", File.ReadAllText(stand.Layout.SeSettings));
        Assert.False(File.Exists(_game.Temp["game/Data/Video/Splash_Logo_Larian.bk2"])); // an empty stub is the stand's
        Assert.True(File.Exists(_game.Temp["game/Data/Video/Intro.bk2"]));
    }

    [Fact]
    public void Status_and_capabilities_are_reported_plainly()
    {
        var stand = _game.Stand("one");
        var status = stand.Status().ToList();
        Assert.StartsWith("instance: one", status[0]);
        Assert.Contains("script extender: stock", status);
        Assert.Contains("client none", status);
        Assert.Throws<StandException>(() => stand.Key("escape"));
        Assert.Throws<StandException>(() => stand.Key("ctrl"));
        Assert.Throws<StandException>(() => stand.Logs(story: false));
        Assert.Empty(stand.Logs(story: true));
    }

    [Fact]
    public void Story_logs_are_read_from_the_stands_log_folder()
    {
        var stand = _game.Stand();
        _game.Temp.Write("state/se-logs/Osiris Runtime 2026-11-13 10-00-00.log", "rule fired\n\nfact added\nOther\n");
        Assert.Equal(["fact added", "Other"], stand.Logs(story: true, lines: 2));
        Assert.Equal(["rule fired", "fact added"], stand.Logs(story: true, grep: "^(RULE|fact)"));
        _ = JsonSerializer.Serialize(stand.SeStatus());
    }

    [Fact]
    public void Console_output_comes_from_the_log_file_and_falls_back_to_a_console_window()
    {
        var stand = _game.Stand();
        _game.Temp.Write("state/se-logs/Extender Runtime 2026-11-13 10-00-00.log", "loaded\nLua error: x\n");
        Assert.Equal(["loaded", "Lua error: x"], stand.Logs(story: false));
        Assert.Empty(stand.Logs(story: true));   // the Osiris trace is a file of its own

        // A game started another way has no log file; its console window is read as before.
        GateKit.Platform.FileTree.RemoveTree(stand.Layout.SeLogs);
        _game.Platform.Window = true;
        _game.Running(stand);
        Assert.Equal(["a console line"], stand.Logs(story: false));
    }
}
