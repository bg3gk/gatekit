using System.Text;
using System.Text.Json.Nodes;
using GateKit.Platform;
using GateKit.Stand;

namespace GateKit.Tests;

public sealed class FileEditTests : IDisposable
{
    readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    static string ModList => FakeGame.ModList.Replace("\r\n", "\n");

    [Fact]
    public void Isolating_keeps_the_base_game_and_the_named_mods_only()
    {
        var text = ModSettings.Isolate(ModList, ["OtherMod"]);
        Assert.Equal(["GustavX", "OtherMod"], ModSettings.Folders(text));
        Assert.Equal(["GustavX"], ModSettings.Folders(ModSettings.Isolate(ModList, [])));
    }

    [Fact]
    public void Isolating_names_mods_that_are_not_in_the_list()
    {
        var error = Assert.Throws<StandException>(() => ModSettings.Isolate(ModList, ["Missing"]));
        Assert.Contains("[Missing]", error.Message);
    }

    static readonly BridgeMod Weave = new("w", "Gwynerva_Weave", FakeGame.WeaveUuid, "Weave", "36028797018963968", []);
    static readonly BridgeMod GateKit = new("g", "Gwynerva_GateKit", ModSettings.BridgeUuid, "GateKit Bridge", "36028797018963968", [FakeGame.WeaveUuid]);

    [Fact]
    public void The_bridge_mods_go_last_into_the_mods_list_once_and_in_order()
    {
        var text = ModSettings.EnableBridge(ModList, [Weave, GateKit]);
        Assert.Equal(["GustavX", "SomeMod", "OtherMod", "Gwynerva_Weave", "Gwynerva_GateKit"], ModSettings.Folders(text));
        Assert.Equal(text, ModSettings.EnableBridge(text, [Weave, GateKit]));
        Assert.Contains("\n" + ModSettings.Node(Weave) + ModSettings.Node(GateKit) + "                    </children>", text);
        // only GateKit there (an old list): both taken out and added again, Weave first
        var old = ModSettings.EnableBridge(ModList, [GateKit]);
        Assert.Equal(["GustavX", "SomeMod", "OtherMod", "Gwynerva_Weave", "Gwynerva_GateKit"],
            ModSettings.Folders(ModSettings.EnableBridge(old, [Weave, GateKit])));
        Assert.Throws<StandException>(() => ModSettings.EnableBridge("<save></save>", [GateKit]));
    }

    [Fact]
    public void Bridge_mods_are_read_from_meta_lsx_dependencies_first()
    {
        _temp.Write("mods/GateKit/Mods/Gwynerva_GateKit/meta.lsx", FakeGame.Meta("Gwynerva_GateKit", "GateKit Bridge", ModSettings.BridgeUuid, FakeGame.WeaveUuid));
        _temp.Write("mods/Weave/Mods/Gwynerva_Weave/meta.lsx", FakeGame.Meta("Gwynerva_Weave", "Weave", FakeGame.WeaveUuid, null));
        var mods = BridgeMods.Find(_temp["mods"]);
        Assert.Equal(["Gwynerva_Weave", "Gwynerva_GateKit"], mods.Select(m => m.Folder));
        Assert.Equal(_temp["mods/GateKit"], mods[1].Root);
        Assert.Equal([FakeGame.WeaveUuid], mods[1].Dependencies);
        Assert.Equal("GateKit Bridge", mods[1].Name);
        // one mod root (the old stand/bridge shape) works too
        Assert.Equal("Gwynerva_GateKit", Assert.Single(BridgeMods.Find(_temp["mods/GateKit"])).Folder);
        Assert.Throws<StandException>(() => BridgeMods.Find(_temp["mods/Weave"]));
    }

    [Fact]
    public void The_repositorys_own_mods_are_found()
    {
        var repo = StandSettings.RepositoryRoot();
        Assert.NotNull(repo);
        var mods = BridgeMods.Find(Path.Combine(repo, "mods"));
        Assert.Equal(["Gwynerva_Weave", "Gwynerva_GateKit"], mods.Select(m => m.Folder));
        Assert.Equal(FakeGame.WeaveUuid, mods[0].Uuid);
    }

    [Fact]
    public void Json_settings_keep_unknown_keys_and_lose_a_byte_order_mark()
    {
        var path = _temp["ScriptExtenderSettings.json"];
        File.WriteAllText(path, "{\"Mine\": \"é\", \"CreateConsole\": false}", new UTF8Encoding(true));
        JsonFiles.Edit(path, s => { s["CreateConsole"] = true; s["LogDirectory"] = @"C:\x y\logs"; });
        var bytes = File.ReadAllBytes(path);
        Assert.NotEqual(0xEF, bytes[0]);
        var text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("\n    \"Mine\": \"é\"", text);
        Assert.Contains("\"LogDirectory\": \"C:\\\\x y\\\\logs\"", text);
        Assert.True(JsonFiles.ReadObject(path)["CreateConsole"]!.GetValue<bool>());
        Assert.Empty(JsonFiles.ReadObject(_temp["missing.json"]));
        Assert.Empty(JsonFiles.ReadObject(_temp.Write("broken.json", "{nope")));
    }

    [Fact]
    public void A_local_script_extender_is_installed_and_the_users_loader_config_comes_back()
    {
        var bin = _temp["bin"];
        _temp.Write("bin/ScriptExtenderUpdaterConfig.json", "{\"Channel\": \"Devel\"}");
        _temp.Write("build/BG3ScriptExtender.dll", "fork");
        _temp.Write("build/BG3ScriptExtender.pdb", "symbols");
        var se = new ScriptExtenderSwitch(bin, _temp["state"]);

        var status = se.Install(_temp["build"]);
        Assert.True(status.Fork);
        Assert.True(status.Managed);
        Assert.Equal("fork", File.ReadAllText(se.Dll));
        Assert.True(File.Exists(se.Pdb));
        var config = JsonFiles.ReadObject(se.Config);
        Assert.Equal("Devel", config["Channel"]!.ToString());
        Assert.True(config["DebugLoadSE"]!.GetValue<bool>());

        var settings = _temp.Write("bin/ScriptExtenderSettings.json", "{}");
        Assert.NotNull(se.ApplyForRun(_temp["build"], settings));
        Assert.False(JsonFiles.ReadObject(settings)["SendCrashReports"]!.GetValue<bool>());

        status = se.Remove();
        Assert.False(status.Fork);
        Assert.False(File.Exists(se.Dll));
        Assert.False(File.Exists(se.Pdb));
        Assert.Equal("{\"Channel\": \"Devel\"}", File.ReadAllText(se.Config));
        Assert.False(File.Exists(se.Record));
    }

    [Fact]
    public void A_script_extender_dll_the_stand_did_not_put_there_is_refused()
    {
        _temp.Write("bin/BG3ScriptExtender.dll", "somebody's");
        _temp.Write("build/BG3ScriptExtender.dll", "fork");
        var se = new ScriptExtenderSwitch(_temp["bin"], _temp["state"]);
        Assert.Throws<StandException>(() => se.Install(_temp["build"]));
        Assert.Equal("somebody's", File.ReadAllText(se.Dll));
        se.Remove(); // nothing recorded: nothing touched
        Assert.True(File.Exists(se.Dll));
    }

    [Fact]
    public void Copies_keep_the_modification_time_and_copy_if_changed_skips_equal_files()
    {
        var source = _temp.Write("a.txt", "one");
        File.SetLastWriteTimeUtc(source, new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        var target = _temp["deep/b.txt"];
        Assert.True(FileTree.CopyIfChanged(source, target));
        Assert.Equal(File.GetLastWriteTimeUtc(source), File.GetLastWriteTimeUtc(target));
        File.WriteAllText(target, "two"); // same size, but now a newer time
        File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source));
        Assert.False(FileTree.CopyIfChanged(source, target));
        Assert.Equal("two", File.ReadAllText(target));
    }

    [Fact]
    public void Screenshots_are_scaled_by_area_and_written_as_jpeg_or_png()
    {
        var pixels = new byte[4 * 4 * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 200; pixels[i + 1] = 100; pixels[i + 2] = 50; }
        var shot = new Screenshot(4, 4, pixels);
        var (w, h, rgb) = ImageFiles.Shrink(shot, 2);
        Assert.Equal((2, 2), (w, h));
        Assert.Equal([50, 100, 200], rgb[..3]);
        var jpg = _temp["shots/a.jpg"];
        Assert.Equal((2, 2), ImageFiles.Save(shot, jpg, 2));
        Assert.Equal([0xFF, 0xD8], File.ReadAllBytes(jpg)[..2]);
        var png = _temp["shots/a.png"];
        ImageFiles.Save(shot, png);
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], File.ReadAllBytes(png)[..4]);
    }

    [Fact]
    public void Key_names_are_virtual_key_codes()
    {
        Assert.Equal(0x1B, GameKeys.Parse("Escape"));
        Assert.Equal(0x70, GameKeys.Parse("f1"));
        Assert.Equal(0x7B, GameKeys.Parse("F12"));
        Assert.Equal('A', GameKeys.Parse("a"));
        Assert.Equal('7', GameKeys.Parse("7"));
        Assert.Null(GameKeys.Parse("ctrl"));
    }

    [Fact]
    public void Json_objects_round_trip_through_the_writer()
    {
        var path = _temp["x.json"];
        JsonFiles.WriteObject(path, new JsonObject { ["a"] = 1, ["b"] = new JsonArray(1, 2) });
        Assert.Equal(2, JsonFiles.ReadObject(path)["b"]!.AsArray().Count);
    }
}
