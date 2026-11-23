using System.Text.Json;
using System.Text.Json.Nodes;
using GateKit.Bridge;
using GateKit.Copies;

namespace GateKit.UnitTests;

/// <summary>A game copy against FakeBridge (the game stand-in) and FakePlatform (no real process).</summary>
public sealed class GameCopyTests : IAsyncLifetime
{
    readonly FakeGame _game = new();
    readonly List<FakeBridge> _bridges = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var bridge in _bridges) await bridge.DisposeAsync();
        _game.Dispose();
    }

    static string Chunk(string code) => code[BridgeClient.Prelude.Length..];

    (FakeBridge client, FakeBridge server) Bridges(Copies.GameCopy copy, Func<string, JsonNode?> client, Func<string, JsonNode?> server)
    {
        var c = new FakeBridge(copy.Layout.Channel, "client", client);
        var s = new FakeBridge(copy.Layout.Channel, "server", server);
        _bridges.Add(c);
        _bridges.Add(s);
        return (c, s);
    }

    [Theory]
    [InlineData("GustavX:WLD_Campfire_E", true)]
    [InlineData("WLD_Campfire_E", false)]
    [InlineData(":WLD_Campfire_E", false)]
    [InlineData("GustavX:", false)]
    [InlineData("A:B:C", false)]
    [InlineData("GustavX: WLD", false)]
    public void A_level_must_name_its_module(string level, bool fine)
    {
        Assert.Equal(fine, GateKit.Copies.GameCopy.LevelProblem(level) is null);
        if (level == "WLD_Campfire_E") Assert.Contains("GustavX:WLD_Campfire_E", GateKit.Copies.GameCopy.LevelProblem(level), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exec_returns_the_result_and_prints_the_chunks_log()
    {
        var copy = _game.GameCopy();
        Bridges(copy, code => JsonValue.Create(Chunk(code)), _ => JsonNode.Parse("{\"party\": [1, 2]}"));
        var result = await copy.ExecAsync("server", "return GK.Party()");
        Assert.Equal(2, result!["party"]!.AsArray().Count);
        Assert.Equal("return 1", (await copy.ExecAsync("ui", "return 1"))!.GetValue<string>());
        var file = _game.Temp.Write("chunk.lua", "﻿return 'from a file'");
        Assert.Equal("return 'from a file'", (await copy.ExecAsync("client", "@" + file))!.GetValue<string>());
    }

    [Fact]
    public async Task A_lua_error_or_silence_becomes_an_explained_error()
    {
        var copy = _game.GameCopy();
        Bridges(copy, _ => throw new InvalidOperationException("attempt to index a nil value"), _ => null);
        var error = await Assert.ThrowsAsync<CopyException>(() => copy.ExecAsync("client", "x.y = 1"));
        Assert.StartsWith("Lua error on client:\nattempt to index", error.Message);
        var silent = new Copies.GameCopy(_game.Settings("nobody"), _game.Platform, _game.Output);
        // No game of that copy at all: said at once, and nothing is queued (D5 of the day run).
        var none = await Assert.ThrowsAsync<CopyException>(() => silent.ExecAsync("server", "return 1", TimeSpan.FromMilliseconds(300)));
        Assert.StartsWith("no game is running for instance nobody; nothing was sent", none.Message);
        Assert.False(File.Exists(Path.Combine(silent.Layout.Channel, "queue_server.jsonl")));
        // A game that runs and does not answer: the wait says so.
        _game.Running(silent);
        var timeout = await Assert.ThrowsAsync<CopyException>(() => silent.ExecAsync("server", "return 1", TimeSpan.FromMilliseconds(300)));
        Assert.StartsWith("no answer from server", timeout.Message);
    }

    [Fact]
    public async Task Checkpoint_saves_and_reset_loads_it_again()
    {
        var copy = _game.GameCopy();
        _game.Running(copy);
        var saves = Path.Combine(copy.Layout.Profile, "PlayerProfiles", "Public", "Savegames", "Story");
        var sent = new List<string>();
        FakeBridge? client = null, server = null;
        (client, server) = Bridges(copy,
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

        var session = copy.Session();
        Assert.Equal("AutoSave_7", await session.CheckpointAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("AutoSave_7", File.ReadAllText(copy.Layout.CheckpointRecord));
        lock (sent)
        {
            Assert.Contains("server: GK.AutoSave(true)", sent);
            Assert.Equal("server: GK.AutoSave(false)", sent.Last(s => s.StartsWith("server", StringComparison.Ordinal)));
        }

        var later = copy.Session(); // a later command: the name comes from the record
        var took = await later.ResetAsync(TimeSpan.FromSeconds(20), skipIntro: false);
        Assert.True(took < TimeSpan.FromSeconds(20));
        lock (sent) Assert.Contains("server: Osi.LoadGame(\"AutoSave_7\")", sent);
    }

    [Fact]
    public async Task Reset_without_a_checkpoint_says_so()
    {
        var copy = _game.GameCopy();
        var error = await Assert.ThrowsAsync<CopyException>(() => copy.Session().ResetAsync());
        Assert.Contains("checkpoint", error.Message);
    }

    [Fact]
    public async Task Linked_lua_is_copied_in_and_both_states_restart()
    {
        var copy = _game.GameCopy();
        FakeBridge? client = null, server = null;
        (client, server) = Bridges(copy,
            code => { if (Chunk(code).EndsWith("Ext.Debug.Reset()", StringComparison.Ordinal)) client!.Reboot(); return JsonValue.Create(true); },
            code => { if (Chunk(code).EndsWith("Ext.Debug.Reset()", StringComparison.Ordinal)) server!.Reboot(); return JsonValue.Create(true); });
        _game.Temp.Write("mymod/Mods/MyMod/ScriptExtender/Lua/BootstrapClient.lua", "print('hi')");

        Assert.Equal(["MyMod"], copy.Link(_game.Temp["mymod"]));
        var loose = Path.Combine(copy.Layout.LooseTarget("MyMod"), "BootstrapClient.lua");
        Assert.Equal("print('hi')", File.ReadAllText(loose));
        File.WriteAllText(_game.Temp["mymod/Mods/MyMod/ScriptExtender/Lua/BootstrapClient.lua"], "print('changed')");
        var took = await copy.ReloadAsync();
        Assert.Equal("print('changed')", File.ReadAllText(loose));
        Assert.True(took < TimeSpan.FromSeconds(10));

        _game.Platform.Processes.Clear();
        copy.Down();
        Assert.False(File.Exists(loose));
        Assert.False(Directory.Exists(_game.Temp["game/Data/Mods/MyMod"]));
        Assert.True(Directory.Exists(_game.Temp["game/Data/Mods/Loose"]));
    }

    [Fact]
    public async Task Up_prepares_an_instance_starts_and_waits_and_down_cleans_up()
    {
        var copy = _game.GameCopy("one");
        var packer = (CopyPacker)copy.Packer;
        _game.Platform.OnStart = _ =>
        {
            var c = new FakeBridge(copy.Layout.Channel, "client", _ => null) { GameState = "Running" };
            var s = new FakeBridge(copy.Layout.Channel, "server", _ => null);
            _bridges.Add(c);
            _bridges.Add(s);
        };

        await copy.UpAsync(new UpOptions(Level: "GustavX:TUT_Avernus_C", Mods: ["SomeMod"], GameArgs: ["-extra"]));

        var launch = Assert.Single(_game.Platform.Started);
        Assert.Equal(["--skip-launcher", "-testLoadLevel", "GustavX:TUT_Avernus_C", "-extra"], launch.Arguments);
        Assert.Equal(copy.Layout.Exe, launch.Executable);
        Assert.Equal(copy.Instance!.Root, launch.InstanceRoot);
        Assert.Equal(2, packer.Packed);
        Assert.True(File.Exists(Path.Combine(copy.Layout.Mods, "Gwynerva_Weave.pak")));
        var mods = ModSettings.Folders(File.ReadAllText(copy.Layout.ModSettingsFile));
        Assert.Equal(["GustavX", "SomeMod", "Gwynerva_Weave", "Gwynerva_GateKit"], mods);
        Assert.Equal(mods, ModSettings.Folders(File.ReadAllText(Path.Combine(copy.Layout.Profile, "PlayerProfiles", "Debug_Client_Profile_3", "modsettings.lsx"))));
        Assert.False(Directory.Exists(copy.Layout.Saves)); // --level: no saves copied
        Assert.False(File.Exists(Path.Combine(copy.Layout.Game, "Data", "Video", "Splash_Logo_Larian.bk2"))); // the switch hides the videos, no file shadows them
        Assert.False(_game.Output.ToString().Contains("SkipSplashScreen", StringComparison.Ordinal)); // a level's start has no menu
        var se = JsonFiles.ReadObject(copy.Layout.SeSettings);
        Assert.False(se["CreateConsole"]!.GetValue<bool>());   // no console window per game
        Assert.False(se["ShiftOpensConsole"]!.GetValue<bool>()); // not even with Shift held as it starts (D2)
        Assert.True(se["LogRuntime"]!.GetValue<bool>());       // its output goes to a log file instead
        Assert.False(se["EnableLogging"]!.GetValue<bool>());
        Assert.Equal(copy.Layout.SeLogs, se["LogDirectory"]!.ToString());
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

        await Assert.ThrowsAsync<CopyException>(() => copy.UpAsync(new UpOptions()));  // already running

        Assert.Equal("instance one: game closed", copy.Down());
        Assert.Empty(copy.GamePids());
        Assert.Contains("one", string.Join("\n", copy.InstanceList()));

        // The loader's own Shift switch (D2) is off in the copy's loader config, which is written as
        // a new file - a hard link to the installed game's would carry the change into it.
        var installed = _game.Temp["game/bin/ScriptExtenderUpdaterConfig.json"];
        File.WriteAllText(installed, "{\"UpdateChannel\": \"Release\"}");
        var loader = Path.Combine(copy.Layout.Bin, "ScriptExtenderUpdaterConfig.json");
        if (File.Exists(loader)) File.Delete(loader);
        GateKit.Platform.GamePlatform.Current.HardLink(installed, loader);
        await copy.UpAsync(new UpOptions(Level: "GustavX:TUT_Avernus_C"));
        Assert.False(JsonFiles.ReadObject(loader)["ShiftOpensConsole"]!.GetValue<bool>());
        Assert.Equal("{\"UpdateChannel\": \"Release\"}", File.ReadAllText(installed));
        copy.Down();
    }

    [Fact]
    public async Task A_local_extender_build_in_the_copy_keeps_its_loader_switch_without_a_record()
    {
        // A copy given a local build from another checkout (its `se install` record lives in that
        // checkout's .state) lost DebugLoadSE on the next start from a fresh worktree and started
        // on the cached stock extender: no Ext.Debug.ReadMemory, no capture.
        var copy = _game.GameCopy("fork");
        _game.Platform.OnStart = _ =>
        {
            _bridges.Add(new FakeBridge(copy.Layout.Channel, "client", _ => null) { GameState = "Running" });
            _bridges.Add(new FakeBridge(copy.Layout.Channel, "server", _ => null));
        };
        await copy.UpAsync(new UpOptions(Level: "GustavX:TUT_Avernus_C"));
        copy.Down();

        _game.Platform.SeCache = _game.Temp["cache"];
        File.WriteAllText(Path.Combine(copy.Layout.Bin, "BG3ScriptExtender.dll"), "a local build");
        var loader = Path.Combine(copy.Layout.Bin, "ScriptExtenderUpdaterConfig.json");
        File.Delete(loader);
        File.WriteAllText(loader, "{\"CachePath\": \"elsewhere\", \"DebugLoadSE\": true}");

        await copy.UpAsync(new UpOptions(Level: "GustavX:TUT_Avernus_C"));
        var config = JsonFiles.ReadObject(loader);
        Assert.True(config["DebugLoadSE"]!.GetValue<bool>());
        Assert.False(config["ShiftOpensConsole"]!.GetValue<bool>());
        copy.Down();

        // Without a build in the copy the loader config is the clone's again: stock, no switch.
        File.Delete(Path.Combine(copy.Layout.Bin, "BG3ScriptExtender.dll"));
        await copy.UpAsync(new UpOptions(Level: "GustavX:TUT_Avernus_C"));
        Assert.Null(JsonFiles.ReadObject(loader)["DebugLoadSE"]);
        copy.Down();
    }

    [Fact]
    public async Task Down_returns_only_when_the_extender_dll_is_let_go()
    {
        // D11 of the day run: `up` right after `down` failed on BG3ScriptExtender.dll "being used by
        // another process" - the process had left the list, its files were still held.
        var copy = _game.GameCopy("held");
        var dll = Path.Combine(copy.Layout.Bin, "BG3ScriptExtender.dll");
        Directory.CreateDirectory(copy.Layout.Bin);
        File.WriteAllText(dll, "extender");
        _game.Running(copy);
        var held = new FileStream(dll, FileMode.Open, FileAccess.Read, FileShare.Read);
        var letGo = Task.Run(async () => { await Task.Delay(1500); held.Dispose(); });
        var watch = System.Diagnostics.Stopwatch.StartNew();
        copy.KillGame();
        Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(1.2), $"returned after {watch.Elapsed}");
        Assert.Contains("BG3ScriptExtender.dll was let go", _game.Output.ToString());
        await letGo;
    }

    [Fact]
    public async Task Up_can_stop_in_the_main_menu()
    {
        // D10 of the day run: recipes of the menus had no start of their own.
        var copy = _game.GameCopy("menu");
        _game.Platform.OnStart = _ =>
        {
            _bridges.Add(new FakeBridge(copy.Layout.Channel, "client",
                code => code.Contains("GK.Gui.Stuck()", StringComparison.Ordinal) ? JsonNode.Parse("""{"stuck": "mainmenu"}""") : null)
                { GameState = "Menu" });
        };
        await Assert.ThrowsAsync<CopyException>(() => copy.UpAsync(new UpOptions(Level: "GustavX:WLD_Main_A", MainMenu: true)));
        await copy.UpAsync(new UpOptions(MainMenu: true));
        Assert.Equal(["--skip-launcher"], _game.Platform.Started.Single().Arguments);   // no -continueGame: the menu stays
        Assert.Contains("in the main menu after", _game.Output.ToString());
        copy.Down();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_start_through_the_menu_sets_the_games_page_switches_at_the_first_Lua(bool splash)
    {
        // The game's own switches skip its pages before the main menu: the splash page unless the
        // start is to stop on it, the first-launch pages in an instance. The fake plays Lua's part.
        var copy = _game.GameCopy(splash ? "switch-splash" : "switch-menu");
        string? sent = null;
        _game.Platform.OnStart = _ =>
        {
            _bridges.Add(new FakeBridge(copy.Layout.Channel, "client", code =>
            {
                if (code.Contains("SkipSplashScreen", StringComparison.Ordinal))
                {
                    sent = code;
                    return JsonNode.Parse($$"""{"skipSplash": {{(splash ? "false" : "true")}}, "firstTimeSetup": false, "state": "LoadModule"}""");
                }
                return code.Contains("GK.Gui.Stuck()", StringComparison.Ordinal)
                    ? JsonNode.Parse(splash ? """{"stuck": "splash", "widget": "SplashScreen"}""" : """{"stuck": "mainmenu"}""") : null;
            }) { GameState = "Menu" });
        };
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await copy.UpAsync(new UpOptions(MainMenu: !splash, Splash: splash), limit.Token);
        Assert.NotNull(sent);
        Assert.Contains($"local FIRST, SKIP = true, {(splash ? "false" : "true")}", sent);
        Assert.Contains($"SkipSplashScreen {(splash ? "off" : "on")}, ShowFirstTimeSetup off at the first Lua (client in LoadModule)", _game.Output.ToString());
        copy.Down();
    }

    [Fact]
    public void A_save_folder_that_is_not_ASCII_is_named_to_the_game_by_its_name_after_the_unique_id()
    {
        // The game reads its command line in the ANSI code page: a Cyrillic folder name never
        // matches, the name after the unique id does when no other save has it.
        const string cyrillic = "\u0410\u0441\u0442\u0430\u0440\u0438\u043e\u043d-717126141013__QuickSave_0";
        Assert.Equal("Tav-1__QuickSave_0", GameCopy.LoadSaveArgument("Tav-1__QuickSave_0", ["Tav-1__QuickSave_0", "Tav-2__QuickSave_0"]));
        Assert.Equal("QuickSave_0", GameCopy.LoadSaveArgument(cyrillic, [cyrillic, "Tav-2__AutoSave_0"]));
        Assert.Throws<CopyException>(() => GameCopy.LoadSaveArgument(cyrillic, [cyrillic, "Tav-2__QuickSave_0"]));
        const string save = "\u0421\u0435\u0439\u0432";
        Assert.Throws<CopyException>(() => GameCopy.LoadSaveArgument(save, [save]));
    }

    [Fact]
    public void A_save_folder_in_any_alphabet_is_passed_as_it_is_to_a_build_that_reads_the_wide_command_line()
    {
        // GateKit's Script Extender build gives the game -loadSaveGame in UTF-8 (WideCommandLine).
        const string cyrillic = "\u0410\u0441\u0442\u0430\u0440\u0438\u043e\u043d-717126141013__QuickSave_0";
        Assert.Equal(cyrillic, GameCopy.LoadSaveArgument(cyrillic, [cyrillic, "Tav-2__QuickSave_0"], wideCommandLine: true));
        const string save = "\u0421\u0435\u0439\u0432";
        Assert.Equal(save, GameCopy.LoadSaveArgument(save, [save], wideCommandLine: true));
        Assert.Equal("Tav-1__QuickSave_0", GameCopy.LoadSaveArgument("Tav-1__QuickSave_0", ["Tav-1__QuickSave_0"], wideCommandLine: true));
    }

    [Fact]
    public async Task A_menu_probe_that_names_nothing_is_an_empty_table_and_the_start_waits_on()
    {
        // The probe's table has no field set while the game shows nothing it knows: Lua's empty
        // table arrives as [], and the start must read it as "nothing yet", not fail on it.
        var copy = _game.GameCopy("menu-empty");
        var looks = 0;
        _game.Platform.OnStart = _ =>
        {
            _bridges.Add(new FakeBridge(copy.Layout.Channel, "client",
                code => !code.Contains("GK.Gui.Stuck()", StringComparison.Ordinal) ? null
                    : Interlocked.Increment(ref looks) < 3 ? new JsonArray() : JsonNode.Parse("""{"stuck": "mainmenu"}"""))
                { GameState = "Menu" });
        };
        await copy.UpAsync(new UpOptions(MainMenu: true));
        Assert.True(looks >= 3);
        Assert.Contains("in the main menu after", _game.Output.ToString());
        copy.Down();
    }

    [Theory]
    [InlineData("splash")]
    [InlineData(null)]
    public async Task A_start_to_the_main_menu_passes_the_splash_screen_by_itself(string? instance)
    {
        // The splash screen comes before the main menu on every start, the installed game's too;
        // the probe passes it with the event its page raises on a key. The fake plays Lua's part:
        // the screen goes only when the probe's chunk presses it.
        var copy = instance is null ? _game.GameCopy() : _game.GameCopy(instance);
        var passed = false;
        _game.Platform.OnStart = _ =>
        {
            _bridges.Add(new FakeBridge(copy.Layout.Channel, "client", code =>
            {
                if (!code.Contains("GK.Gui.Stuck()", StringComparison.Ordinal)) return null;
                if (passed) return JsonNode.Parse("""{"stuck": "mainmenu"}""");
                if (!code.Contains("stuck == \"splash\"", StringComparison.Ordinal)) return JsonNode.Parse("""{"stuck": "splash", "widget": "SplashScreen"}""");
                passed = true;
                return JsonNode.Parse("""{"stuck": "splash", "widget": "SplashScreen", "ok": true, "did": "splash screen: SplashScreen raised OpenMainMenu, as its key does"}""");
            }) { GameState = "Menu" });
        };
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await copy.UpAsync(new UpOptions(MainMenu: true), limit.Token);
        Assert.True(passed);
        var said = _game.Output.ToString();
        Assert.Contains("splash screen: SplashScreen raised OpenMainMenu", said);
        Assert.Contains("in the main menu after", said);
        copy.Down();
    }

    [Fact]
    public async Task A_start_to_the_splash_screen_leaves_it_standing()
    {
        // The fake plays Lua's part: the probe presses only when its chunk sets SPLASH true.
        var copy = _game.GameCopy("splash-stays");
        var pressed = false;
        _game.Platform.OnStart = _ =>
        {
            _bridges.Add(new FakeBridge(copy.Layout.Channel, "client", code =>
            {
                if (!code.Contains("GK.Gui.Stuck()", StringComparison.Ordinal)) return null;
                if (code.Contains("SPLASH = true, true", StringComparison.Ordinal)) pressed = true;
                return JsonNode.Parse("""{"stuck": "splash", "widget": "SplashScreen"}""");
            }) { GameState = "Menu" });
        };
        await Assert.ThrowsAsync<CopyException>(() => copy.UpAsync(new UpOptions(MainMenu: true, Splash: true)));
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await copy.UpAsync(new UpOptions(Splash: true), limit.Token);
        Assert.False(pressed);
        Assert.Equal(["--skip-launcher"], _game.Platform.Started.Single().Arguments);
        Assert.Contains("on the splash screen after", _game.Output.ToString());
        copy.Down();
    }

    [Fact]
    public async Task A_copy_starts_bare_unless_mods_are_named()
    {
        // The player's own mods turned up in copies nobody had asked them for: a copy started
        // with no level and no mods now loads the base game and the bridge, and holds no other pak.
        var copy = _game.GameCopy("bare");
        _game.Platform.OnStart = _ =>
        {
            _bridges.Add(new FakeBridge(copy.Layout.Channel, "client", _ => null));
            _bridges.Add(new FakeBridge(copy.Layout.Channel, "server", _ => null));
        };
        await copy.UpAsync(new UpOptions());
        Assert.Equal(["GustavX", "Gwynerva_Weave", "Gwynerva_GateKit"], ModSettings.Folders(File.ReadAllText(copy.Layout.ModSettingsFile)));
        Assert.False(File.Exists(Path.Combine(copy.Layout.Mods, "SomeMod.pak")));
        copy.Down();

        Assert.DoesNotContain(Report().Findings, f => f.Kind == "copy-mods");

        await copy.UpAsync(new UpOptions(Mods: ["SomeMod"]));
        Assert.Equal(["GustavX", "SomeMod", "Gwynerva_Weave", "Gwynerva_GateKit"], ModSettings.Folders(File.ReadAllText(copy.Layout.ModSettingsFile)));
        Assert.True(File.Exists(Path.Combine(copy.Layout.Mods, "SomeMod.pak")));
        copy.Down();

        // doctor shows a copy whose mod list is not bare, and which mods make it so
        var report = Report();
        Assert.Equal(["SomeMod"], report.Copies.Single(c => c.Name == "bare").Extra);
        var finding = Assert.Single(report.Findings, f => f.Kind == "copy-mods");
        Assert.Contains("copy bare is not bare: its mod list loads SomeMod", finding.Detail, StringComparison.Ordinal);
        var text = new StringWriter();
        report.Write(text);
        Assert.Contains("mods: GustavX, SomeMod, Gwynerva_Weave, Gwynerva_GateKit - not bare", text.ToString());

        HomeReport Report()
        {
            string? Variable(string name) => name switch
            {
                "BG3_GAME" => _game.Temp["game"], "GATEKIT_STATE" => _game.Temp["state"],
                "GATEKIT_INSTANCES" => _game.Temp["instances"], "GATEKIT_BRIDGE" => _game.Temp["mods"], _ => null,
            };
            return HomeReport.Read(GateKit.Home.GateKitHome.Resolve(_game.Search(Variable)), _game.Settings("bare"), _game.Platform);
        }
    }

    [Fact]
    public void Doctor_shows_the_game_and_the_toolkit_where_they_were_found_and_every_copy_on_disk()
    {
        // No setting names the game or the Toolkit: the tools find them in a Steam library, and
        // doctor said "unset" for both. It now shows the folder and the library it came from.
        _game.Platform.Steam = _game.Temp["steam"];
        _game.Temp.Write("steam/steamapps/common/Baldurs Gate 3/bin/bg3.exe");
        Directory.CreateDirectory(_game.Temp[$"steam/steamapps/common/{GateKit.Platform.GamePlatform.ToolkitInstallDir}"]);
        // a copy on disk the state has no record of
        _game.Temp.Write("instances/menus/game/bin/bg3_dx11.exe");
        string? Variable(string name) => name switch
        {
            "BG3_GAME" => _game.Temp["game"], "GATEKIT_STATE" => _game.Temp["state"],
            "GATEKIT_INSTANCES" => _game.Temp["instances"], "GATEKIT_BRIDGE" => _game.Temp["mods"], _ => null,
        };
        var home = GateKit.Home.GateKitHome.Resolve(_game.Search(name => name == "BG3_GAME" ? null : Variable(name)));
        var report = HomeReport.Read(home, _game.Settings(), _game.Platform);

        var game = report.Rows.Single(r => r.Location.Id == "game").Location;
        Assert.Equal("discovered", game.SourceName);
        Assert.Equal(_game.Temp["steam/steamapps/common/Baldurs Gate 3"], game.Path);
        Assert.Equal($"the Steam library {_game.Temp["steam"]}", game.Origin);
        var toolkit = report.Rows.Single(r => r.Location.Id == "toolkit").Location;
        Assert.Equal("discovered", toolkit.SourceName);
        Assert.True(report.Rows.Single(r => r.Location.Id == "toolkit").Exists);

        var menus = report.Copies.Single(c => c.Name == "menus");
        Assert.Equal(_game.Temp["instances/menus"], menus.Root);
        Assert.Null(menus.OwnerFrom);
        var text = new StringWriter();
        report.Write(text);
        Assert.Contains($"{_game.Temp["steam/steamapps/common/Baldurs Gate 3"]}  (found in the Steam library {_game.Temp["steam"]})", text.ToString());
        Assert.Contains($"copy menus ({_game.Temp["instances/menus"]}): no owner record (looked in {Path.Combine(_game.Temp["state"], "instances", "menus", "owner.json")}", text.ToString());
    }

    [Fact]
    public void Se_install_without_an_instance_records_the_build_and_writes_into_no_game()
    {
        // `se install <folder>` with no --instance put the build and DebugLoadSE into the installed
        // game's bin. It now only records the default; a game is written when it is named.
        var build = _game.Temp.Write("se-build/BG3ScriptExtender.dll", "GateKit's build");
        var bin = _game.Temp["game/bin"];
        Dictionary<string, string> Snapshot() => Directory.EnumerateFiles(bin, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, File.ReadAllText, StringComparer.Ordinal);
        var before = Snapshot();

        var choice = _game.GameCopy().SeInstall(Path.GetDirectoryName(build)!);
        Assert.Null(choice.Into);
        Assert.Null(choice.Installed);
        Assert.Equal(before, Snapshot());
        Assert.Equal(Path.GetDirectoryName(build), _game.Settings().ScriptExtender);   // the default every start loads
        var remove = Assert.Throws<CopyException>(() => _game.GameCopy().SeRemove());
        Assert.Contains("--installed-game", remove.Message, StringComparison.Ordinal);
        Assert.Equal(before, Snapshot());

        // a copy named gets the build in its own bin; the installed game still not
        var copy = _game.GameCopy("se");
        Directory.CreateDirectory(copy.Layout.Bin);
        var installed = copy.SeInstall(Path.GetDirectoryName(build)!);
        Assert.Equal(copy.Layout.Bin, installed.Into);
        Assert.Equal("GateKit's build", File.ReadAllText(Path.Combine(copy.Layout.Bin, "BG3ScriptExtender.dll")));
        Assert.Equal(before, Snapshot());
        Assert.Throws<CopyException>(() => copy.SeInstall(Path.GetDirectoryName(build)!, installedGame: true));
    }

    [Fact]
    public async Task Up_on_the_installed_game_backs_up_and_down_restores()
    {
        var copy = _game.GameCopy();
        var original = File.ReadAllText(copy.Layout.ModSettingsFile);
        _game.Platform.OnStart = _ =>
        {
            _bridges.Add(new FakeBridge(copy.Layout.Channel, "client", _ => null));
            _bridges.Add(new FakeBridge(copy.Layout.Channel, "server", _ => null));
        };
        await copy.UpAsync(new UpOptions(Save: "Save1"));
        Assert.Equal(["--skip-launcher", "-loadSaveGame", "Save1"], _game.Platform.Started.Single().Arguments);
        // a save asked for is loaded without the main menu by GateKit's build
        Assert.Equal(true, (bool?)JsonFiles.ReadObject(copy.Layout.SeSettings)["DirectSaveLoad"]);
        // every start says the fast-load switches: off unless asked
        foreach (var name in GameCopy.FastLoadSwitches)
            Assert.Equal(false, (bool?)JsonFiles.ReadObject(copy.Layout.SeSettings)[name]);
        // no --level, no --mods: the player's own list, plus the bridge
        Assert.Equal(["GustavX", "SomeMod", "OtherMod", "Gwynerva_Weave", "Gwynerva_GateKit"], ModSettings.Folders(File.ReadAllText(copy.Layout.ModSettingsFile)));
        Assert.True(Directory.Exists(copy.Layout.Backup));

        Assert.Equal("game closed, saves and mod list restored", copy.Down());
        Assert.Equal(original, File.ReadAllText(copy.Layout.ModSettingsFile));
        Assert.Equal("{\"Keep\": 1}", File.ReadAllText(copy.Layout.SeSettings));
        Assert.True(File.Exists(_game.Temp["game/Data/Video/Intro.bk2"]));
    }

    [Fact]
    public async Task Fast_load_switches_are_written_on_asked_and_off_otherwise()
    {
        var copy = _game.GameCopy();
        _game.Platform.OnStart = _ =>
        {
            _bridges.Add(new FakeBridge(copy.Layout.Channel, "client", _ => null));
            _bridges.Add(new FakeBridge(copy.Layout.Channel, "server", _ => null));
        };
        await copy.UpAsync(new UpOptions(Save: "Save1", FastLoad: true));
        var settings = JsonFiles.ReadObject(copy.Layout.SeSettings);
        foreach (var name in GameCopy.FastLoadSwitches) Assert.Equal(true, (bool?)settings[name]);
        copy.Down();
    }

    [Fact]
    public async Task A_headless_start_draws_nothing_from_its_first_frame_unless_it_goes_through_the_menu_or_asks()
    {
        var copy = _game.GameCopy();
        _game.Platform.OnStart = _ =>
        {
            _bridges.Add(new FakeBridge(copy.Layout.Channel, "client", _ => null));
            _bridges.Add(new FakeBridge(copy.Layout.Channel, "server", _ => null));
        };
        await copy.UpAsync(new UpOptions(Save: "Save1", Headless: true));
        var settings = JsonFiles.ReadObject(copy.Layout.SeSettings);
        foreach (var name in GameCopy.HeadlessSwitches) Assert.Equal(true, (bool?)settings[name]);
        Assert.Equal(false, (bool?)settings["RenderCutFromRunning"]);
        Assert.Equal(GameCopy.DefaultSkipPaceUs, (int?)settings["RenderCutSkipPaceUs"]);
        Assert.Equal(true, (bool?)settings["RenderCutSkipPaceStartOnly"]);
        copy.Down();
        Assert.Null(GameCopy.DrawnStartWhy(drawnStartAsked: false, menuStart: false));
        Assert.NotNull(GameCopy.DrawnStartWhy(drawnStartAsked: false, menuStart: true));
        Assert.NotNull(GameCopy.DrawnStartWhy(drawnStartAsked: true, menuStart: false));
    }

    [Fact]
    public void Status_and_capabilities_are_reported_plainly()
    {
        var copy = _game.GameCopy("one");
        var status = copy.Status().ToList();
        Assert.StartsWith("instance: one", status[0]);
        Assert.Contains("script extender: stock", status);
        Assert.Contains("client none", status);
        Assert.Throws<CopyException>(() => copy.Key("escape"));
        Assert.Throws<CopyException>(() => copy.Key("ctrl"));
        Assert.Throws<CopyException>(() => copy.Logs(story: false));
        Assert.Empty(copy.Logs(story: true));
    }

    [Fact]
    public void Story_logs_are_read_from_the_copys_log_folder()
    {
        var copy = _game.GameCopy();
        _game.Temp.Write("state/se-logs/Osiris Runtime 2026-11-13 10-00-00.log", "rule fired\n\nfact added\nOther\n");
        Assert.Equal(["fact added", "Other"], copy.Logs(story: true, lines: 2));
        Assert.Equal(["rule fired", "fact added"], copy.Logs(story: true, grep: "^(RULE|fact)"));
        _ = JsonSerializer.Serialize(copy.SeStatus());
    }

    [Fact]
    public void Console_output_comes_from_the_log_file_and_falls_back_to_a_console_window()
    {
        var copy = _game.GameCopy();
        _game.Temp.Write("state/se-logs/Extender Runtime 2026-11-13 10-00-00.log", "loaded\nLua error: x\n");
        Assert.Equal(["loaded", "Lua error: x"], copy.Logs(story: false));
        Assert.Empty(copy.Logs(story: true));   // the Osiris trace is a file of its own

        // A game started another way has no log file; its console window is read as before.
        GateKit.Platform.FileTree.RemoveTree(copy.Layout.SeLogs);
        _game.Platform.Window = true;
        _game.Running(copy);
        Assert.Equal(["a console line"], copy.Logs(story: false));
    }
}
