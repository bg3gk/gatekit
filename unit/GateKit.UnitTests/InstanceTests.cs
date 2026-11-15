using GateKit.Platform;
using GateKit.Copies;

namespace GateKit.UnitTests;

/// <summary>Instance cloning on a temp tree, with the real links of this system.</summary>
public sealed class InstanceTests : IDisposable
{
    readonly FakeGame _game = new();

    public void Dispose() => _game.Dispose();

    Instance Make() => new("one", _game.Settings(), _game.Platform);

    [Fact]
    public void The_clone_copies_bin_and_links_data()
    {
        var instance = Make();
        instance.SyncGame(new HashSet<string>(), seCache: _game.Temp["cache"], seForkActive: false);
        var bin = Path.Combine(instance.Game, "bin");
        var data = Path.Combine(instance.Game, "Data");

        Assert.Equal("exe", File.ReadAllText(Path.Combine(bin, "bg3_dx11.exe")));
        Assert.Equal("dll", File.ReadAllText(Path.Combine(bin, "sub", "library.dll")));
        Assert.Equal(Instance.NotAnApp, File.ReadAllText(Path.Combine(bin, "steam_appid.txt")));
        Assert.False(File.Exists(Path.Combine(bin, "BG3ScriptExtender.dll")));
        Assert.False(File.Exists(Path.Combine(bin, "crash.log")));
        var loader = JsonFiles.ReadObject(Path.Combine(bin, "ScriptExtenderUpdaterConfig.json"));
        Assert.Equal(_game.Temp["cache"], loader["CachePath"]!.ToString());

        Assert.False(FileTree.IsLink(Path.Combine(data, "Gustav.pak")));                // a hard link: a plain file
        Assert.True(FileTree.SameFileContent(_game.Temp["game/Data/Gustav.pak"], Path.Combine(data, "Gustav.pak")));
        Assert.True(FileTree.IsLink(Path.Combine(data, "Localization")));
        Assert.Equal("loca", File.ReadAllText(Path.Combine(data, "Localization", "English.pak")));
        Assert.False(FileTree.IsLink(Path.Combine(data, "Mods")));                      // real folders `gatekit` writes into
        Assert.False(FileTree.IsLink(Path.Combine(data, "Video")));
        Assert.True(FileTree.IsLink(Path.Combine(data, "Mods", "Loose")));
        Assert.True(File.Exists(Path.Combine(data, "Video", "Intro.bk2")));
        Assert.False(File.Exists(Path.Combine(data, "Video", "Splash_Logo_Larian.bk2"))); // the installed game's empty stub
    }

    [Fact]
    public void A_second_sync_keeps_the_instances_own_files_and_follows_game_updates()
    {
        var instance = Make();
        instance.SyncGame(new HashSet<string>(), null, false);
        var settings = Path.Combine(instance.Game, "bin", "ScriptExtenderSettings.json");
        File.WriteAllText(settings, "{\"Own\": true}");
        File.Delete(_game.Temp["game/Data/Gustav.pak"]);       // a game update replaces the file
        _game.Temp.Write("game/Data/Gustav.pak", "new pak data!");
        instance.SyncGame(new HashSet<string> { "Loose" }, null, false);
        Assert.Equal("{\"Own\": true}", File.ReadAllText(settings));
        Assert.Equal("new pak data!", File.ReadAllText(Path.Combine(instance.Game, "Data", "Gustav.pak")));
    }

    [Fact]
    public void Owning_a_folder_replaces_links_on_the_way_and_writes_stay_inside()
    {
        var instance = Make();
        instance.SyncGame(new HashSet<string>(), null, false);
        var linked = Path.Combine(instance.Game, "Data", "Mods", "Loose");
        Assert.False(instance.Inside(Path.Combine(linked, "meta.lsx")));   // through the link: the real game
        var target = Path.Combine(linked, "ScriptExtender", "Lua");
        instance.OwnFolder(target);
        Assert.False(FileTree.IsLink(linked));
        Assert.True(instance.Inside(target));
        Assert.True(File.Exists(_game.Temp["game/Data/Mods/Loose/meta.lsx"]));  // the real one untouched
        Assert.Throws<CopyException>(() => instance.OwnFolder(_game.Temp["elsewhere"]));
    }

    [Fact]
    public void The_profile_gets_the_players_options_and_mods_but_not_the_bridge_and_keeps_its_own_saves()
    {
        var instance = Make();
        Directory.CreateDirectory(Path.Combine(instance.Profile, "Mods"));
        File.WriteAllText(Path.Combine(instance.Profile, "Mods", "Gone.pak"), "removed from the real profile");
        File.WriteAllText(Path.Combine(instance.Profile, "Mods", "Gwynerva_GateKit.pak"), "the instance's own bridge");
        _game.Temp.Write("instances/one/home/profile/PlayerProfiles/Debug_Client_Profile_2/Savegames/Story/Old/x.lsv", "old");
        _game.Temp.Write("profile/PlayerProfiles/Public/config.lsf", "options");

        instance.SyncProfile(_game.Temp["profile"], new HashSet<string> { ModSettings.BridgePak }, withMods: true);

        Assert.Equal("graphics", File.ReadAllText(Path.Combine(instance.Profile, "graphicSettings.lsx")));
        Assert.Equal("layout", File.ReadAllText(Path.Combine(instance.Profile, "PlayerProfiles", "Public", "UILayout.lsx")));
        Assert.Equal("mod", File.ReadAllText(Path.Combine(instance.Profile, "Mods", "SomeMod.pak")));
        Assert.False(File.Exists(Path.Combine(instance.Profile, "Mods", "Gone.pak")));
        Assert.Equal("the instance's own bridge", File.ReadAllText(Path.Combine(instance.Profile, "Mods", "Gwynerva_GateKit.pak")));
        // D23: the copy's own saves stay; one made under a debug profile is gathered into Public
        Assert.True(File.Exists(Path.Combine(instance.Profile, "PlayerProfiles", "Debug_Client_Profile_2", "Savegames", "Story", "Old", "x.lsv")));
        Assert.True(File.Exists(Path.Combine(instance.Profile, "PlayerProfiles", "Public", "Savegames", "Story", "Old", "x.lsv")));
        foreach (var player in Instance.Players) Assert.True(Directory.Exists(Path.Combine(instance.Profile, "PlayerProfiles", player)));
        // Every profile the game may play as has the player's options and agreements (D9)...
        foreach (var player in Instance.Players)
            Assert.Equal("options", File.ReadAllText(Path.Combine(instance.Profile, "PlayerProfiles", player, "config.lsf")));
        // ...once: what the game wrote there since is kept.
        File.WriteAllText(Path.Combine(instance.Profile, "PlayerProfiles", "Debug_Client_Profile_3", "config.lsf"), "accepted here");
        instance.SyncProfile(_game.Temp["profile"], new HashSet<string> { ModSettings.BridgePak }, withMods: true);
        Assert.Equal("accepted here", File.ReadAllText(Path.Combine(instance.Profile, "PlayerProfiles", "Debug_Client_Profile_3", "config.lsf")));
    }

    [Fact]
    public void A_bare_profile_holds_the_bridge_and_none_of_the_players_mod_files()
    {
        // A pak in the Mods folder can load without being in the mod list (an override), so a bare
        // copy has none of the player's paks at all.
        var instance = Make();
        instance.SyncProfile(_game.Temp["profile"], new HashSet<string> { ModSettings.BridgePak }, withMods: true);
        File.WriteAllText(Path.Combine(instance.Profile, "Mods", ModSettings.BridgePak), "the instance's own bridge");
        instance.SyncProfile(_game.Temp["profile"], new HashSet<string> { ModSettings.BridgePak }, withMods: false);
        Assert.Equal([ModSettings.BridgePak], Directory.EnumerateFiles(Path.Combine(instance.Profile, "Mods"), "*.pak").Select(Path.GetFileName));
    }

    [Fact]
    public void A_save_goes_from_one_copy_to_another_and_the_newest_copy_of_a_name_wins()
    {
        // D23 of the day run: one copy plays the story forward and saves; the others start from those saves.
        var from = Path.Combine(_game.Temp["saves-from"], "profile");
        var to = Path.Combine(_game.Temp["saves-to"], "profile");
        _game.Temp.Write("saves-from/profile/PlayerProfiles/Public/Savegames/Story/Act1_Gate/Act1_Gate.lsv", "old");
        _game.Temp.Write("saves-from/profile/PlayerProfiles/Debug_Client_Profile_1/Savegames/Story/Act1_Gate/Act1_Gate.lsv", "newer");
        File.SetLastWriteTimeUtc(Path.Combine(from, "PlayerProfiles", "Public", "Savegames", "Story", "Act1_Gate", "Act1_Gate.lsv"), DateTime.UtcNow.AddHours(-1));
        Directory.CreateDirectory(Path.Combine(to, "PlayerProfiles", "Debug_Client_Profile_2"));

        var save = SaveStore.Find(from, "act1_gate");
        Assert.Equal("Debug_Client_Profile_1", save.Player);
        var exported = SaveStore.Export(from, "Act1_Gate", _game.Temp["exported"]);
        Assert.Equal("newer", File.ReadAllText(Path.Combine(exported, "Act1_Gate.lsv")));

        var into = SaveStore.Import(to, exported, Instance.Players);
        Assert.Equal(["Public", "Debug_Client_Profile_2"], into);   // the profiles the copy has
        Assert.Equal("newer", File.ReadAllText(Path.Combine(to, "PlayerProfiles", "Public", "Savegames", "Story", "Act1_Gate", "Act1_Gate.lsv")));
        Assert.Equal(["Act1_Gate"], SaveStore.List(to).Select(s => s.Name));
        Assert.Throws<CopyException>(() => SaveStore.Find(to, "nothing"));
        Assert.Throws<CopyException>(() => SaveStore.Import(to, _game.Temp["empty"], Instance.Players));
    }

    [Fact]
    public void Spreading_gives_every_player_profile_the_same_mod_list_and_saves()
    {
        var instance = Make();
        instance.SyncProfile(_game.Temp["profile"], new HashSet<string> { ModSettings.BridgePak }, withMods: false);
        var publicFolder = Path.Combine(instance.Profile, "PlayerProfiles", "Public");
        File.WriteAllText(Path.Combine(publicFolder, "modsettings.lsx"), "list");
        Directory.CreateDirectory(Path.Combine(publicFolder, "Savegames", "Story", "S"));
        instance.Spread();
        foreach (var player in Instance.Players.Skip(1))
        {
            Assert.Equal("list", File.ReadAllText(Path.Combine(instance.Profile, "PlayerProfiles", player, "modsettings.lsx")));
            Assert.True(Directory.Exists(Path.Combine(instance.Profile, "PlayerProfiles", player, "Savegames", "Story", "S")));
        }
    }

    [Fact]
    public void Destroying_removes_links_as_links()
    {
        var instance = Make();
        instance.SyncGame(new HashSet<string>(), null, false);
        Directory.CreateDirectory(instance.State);
        Assert.Contains("one", Instance.Names(_game.Temp["instances"]));
        instance.Destroy();
        Assert.False(Directory.Exists(instance.Root));
        Assert.False(Directory.Exists(instance.State));
        Assert.False(Directory.Exists(_game.Temp["instances"]));                  // the empty root goes too
        Assert.Equal("pak data", File.ReadAllText(_game.Temp["game/Data/Gustav.pak"]));
        Assert.Equal("loca", File.ReadAllText(_game.Temp["game/Data/Localization/English.pak"]));
        Assert.True(File.Exists(_game.Temp["game/Data/Mods/Loose/meta.lsx"]));
    }

    [Fact]
    public void Resolving_a_path_follows_folder_links()
    {
        var instance = Make();
        instance.SyncGame(new HashSet<string>(), null, false);
        var through = Path.Combine(instance.Game, "Data", "Localization", "English.pak");
        Assert.Equal(Path.GetFullPath(_game.Temp["game/Data/Localization/English.pak"]), FileTree.ResolvePath(through), ignoreCase: OperatingSystem.IsWindows());
    }
}
