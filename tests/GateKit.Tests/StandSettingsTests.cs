using GateKit.Platform;
using GateKit.Stand;

namespace GateKit.Tests;

public sealed class StandSettingsTests : IDisposable
{
    readonly FakeGame _game = new();

    public void Dispose() => _game.Dispose();

    StandSettings Resolve(Dictionary<string, string> env, string? workingDirectory = null) =>
        StandSettings.Resolve(_game.Platform, name => env.GetValueOrDefault(name), workingDirectory ?? _game.Temp.Path);

    [Fact]
    public void Environment_variables_name_the_folders()
    {
        var s = Resolve(new() { ["BG3_GAME"] = _game.Temp["game"], ["GATEKIT_STATE"] = _game.Temp["st"], ["GATEKIT_SE"] = _game.Temp["se"] });
        Assert.Equal(_game.Temp["game"], s.GameFolder);
        Assert.Equal(_game.Temp["profile"], Path.GetFullPath(s.Profile));
        Assert.Equal(_game.Temp["st"], s.StateRoot);
        Assert.Equal(_game.Temp["se"], s.ScriptExtender);
        Assert.Equal(Path.Combine(_game.Temp.Path, "Baldurs Gate 3 GateKit"), s.InstancesRoot);
        Assert.Equal("bg3_dx11.exe", s.ExeName);
        Assert.Equal(TimeSpan.FromSeconds(75), s.StartTimeout);
    }

    [Fact]
    public void The_old_stands_variable_names_still_work_and_the_new_ones_win()
    {
        var s = Resolve(new()
        {
            ["BG3_GAME"] = _game.Temp["game"],
            ["BG3_GATEKIT_INSTANCES"] = _game.Temp["old"],
            ["BG3_GATEKIT_START_TIMEOUT"] = "240",
            ["GATEKIT_START_TIMEOUT"] = "100",
            ["BG3_DEVKIT_INSTANCE"] = "legacy",
        });
        Assert.Equal(_game.Temp["old"], s.InstancesRoot);
        Assert.Equal(TimeSpan.FromSeconds(100), s.StartTimeout);
        Assert.Equal("legacy", s.Instance);
    }

    [Fact]
    public void Stock_means_no_local_script_extender()
    {
        var s = Resolve(new() { ["BG3_GAME"] = _game.Temp["game"], ["GATEKIT_SE"] = "Stock" });
        Assert.Null(s.ScriptExtender);
    }

    /// <summary>A folder that looks like a built Script Extender, for the tests below.</summary>
    string Built(string name)
    {
        var folder = _game.Temp[name];
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "BG3ScriptExtender.dll"), "not really");
        return folder;
    }

    [Fact]
    public void The_build_se_install_was_given_is_what_later_runs_load()
    {
        var state = _game.Temp["st"];
        StandSettings.RememberScriptExtender(state, Built("build"));
        var s = Resolve(new() { ["BG3_GAME"] = _game.Temp["game"], ["GATEKIT_STATE"] = state });
        Assert.Equal(_game.Temp["build"], s.ScriptExtender);
    }

    [Fact]
    public void What_the_shell_says_beats_what_was_remembered_stock_included()
    {
        var state = _game.Temp["st"];
        StandSettings.RememberScriptExtender(state, Built("build"));

        var named = Resolve(new() { ["BG3_GAME"] = _game.Temp["game"], ["GATEKIT_STATE"] = state, ["GATEKIT_SE"] = Built("other") });
        Assert.Equal(_game.Temp["other"], named.ScriptExtender);

        var stock = Resolve(new() { ["BG3_GAME"] = _game.Temp["game"], ["GATEKIT_STATE"] = state, ["GATEKIT_SE"] = "stock" });
        Assert.Null(stock.ScriptExtender);
    }

    [Fact]
    public void A_remembered_build_that_is_no_longer_there_is_forgotten_quietly()
    {
        var state = _game.Temp["st"];
        StandSettings.RememberScriptExtender(state, Built("build"));
        Directory.Delete(_game.Temp["build"], recursive: true);

        var s = Resolve(new() { ["BG3_GAME"] = _game.Temp["game"], ["GATEKIT_STATE"] = state });
        Assert.Null(s.ScriptExtender);
    }

    [Fact]
    public void A_gatekit_json_in_a_parent_folder_fills_what_the_environment_leaves_out()
    {
        _game.Temp.Write("gatekit.json", $$"""
            {
              // comments are allowed
              "game": {{System.Text.Json.JsonSerializer.Serialize(_game.Temp["game"])}},
              "instances": {{System.Text.Json.JsonSerializer.Serialize(_game.Temp["from-file"])}},
              "startTimeout": 30,
            }
            """);
        Directory.CreateDirectory(_game.Temp["deep/er"]);
        var s = Resolve(new() { ["GATEKIT_INSTANCES"] = _game.Temp["from-env"] }, _game.Temp["deep/er"]);
        Assert.Equal(_game.Temp["game"], s.GameFolder);
        Assert.Equal(_game.Temp["from-env"], s.InstancesRoot);
        Assert.Equal(TimeSpan.FromSeconds(30), s.StartTimeout);
        Assert.Equal(_game.Temp["gatekit.json"], s.ConfigFile);
    }

    [Fact]
    public void Without_a_setting_the_game_is_looked_up_in_the_steam_libraries()
    {
        var library = _game.Temp["library"];
        _game.Temp.Write("steam/steamapps/libraryfolders.vdf", $$"""
            "libraryfolders"
            {
                "0" { "path" "{{_game.Temp["steam"].Replace("\\", "\\\\")}}" }
                "1" { "path" "{{library.Replace("\\", "\\\\")}}" "apps" { "1086940" "123" } }
            }
            """);
        _game.Temp.Write("library/steamapps/appmanifest_1086940.acf", "\"AppState\"\n{\n\t\"appid\"\t\t\"1086940\"\n\t\"installdir\"\t\t\"BG3 Custom\"\n}\n");
        _game.Temp.Write("library/steamapps/common/BG3 Custom/bin/bg3.exe");
        _game.Platform.Steam = _game.Temp["steam"];
        var s = Resolve(new());
        Assert.Equal(_game.Temp["library/steamapps/common/BG3 Custom"], s.GameFolder);
    }

    [Fact]
    public void No_game_anywhere_is_an_explained_error()
    {
        var error = Assert.Throws<StandException>(() => Resolve(new()));
        Assert.Contains("BG3_GAME", error.Message);
    }

    [Fact]
    public void Library_paths_are_unescaped()
    {
        var paths = Steam.LibraryPaths("\"0\" { \"path\" \"C:\\\\Program Files (x86)\\\\Steam\" }\n\"1\" { \"PATH\" \"/home/u/Games\" }");
        Assert.Equal(["C:\\Program Files (x86)\\Steam", "/home/u/Games"], paths);
    }

    [Fact]
    public void Instance_names_are_checked()
    {
        var settings = _game.Settings();
        Assert.Throws<StandException>(() => new Instance("bad name!", settings, _game.Platform));
        Assert.Throws<StandException>(() => new Instance(new string('a', 33), settings, _game.Platform));
        var good = new Instance("port_2-x", settings, _game.Platform);
        Assert.Equal(Path.Combine(settings.InstancesRoot, "port_2-x", "game"), good.Game);
        Assert.Equal(Path.Combine(settings.StateRoot, "instances", "port_2-x"), good.State);
    }

    [Fact]
    public void The_layout_points_at_the_instance_when_one_is_chosen()
    {
        var installed = new StandLayout(_game.Settings(), _game.Platform);
        var instance = new StandLayout(_game.Settings("one"), _game.Platform);
        Assert.Equal(Path.Combine(_game.Temp["game"], "bin", "bg3_dx11.exe"), installed.Exe);
        Assert.Equal(Path.Combine(_game.Temp["profile"], "Script Extender", "Gwynerva_GateKit"), Path.GetFullPath(installed.Channel));
        Assert.Equal(Path.Combine(_game.Temp["instances"], "one", "game", "bin", "bg3_dx11.exe"), instance.Exe);
        Assert.StartsWith(Path.Combine(_game.Temp["instances"], "one"), instance.Profile);
        Assert.Equal(Path.Combine(_game.Temp["state"], "instances", "one", "se-logs"), instance.SeLogs);
    }
}
