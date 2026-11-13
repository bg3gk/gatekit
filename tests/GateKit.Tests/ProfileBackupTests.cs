using GateKit.Platform;
using GateKit.Stand;

namespace GateKit.Tests;

/// <summary>The installed game's backup and restore, on a temp profile.</summary>
public sealed class ProfileBackupTests : IDisposable
{
    readonly FakeGame _game = new();

    public void Dispose() => _game.Dispose();

    [Fact]
    public void A_run_is_undone_saves_mod_list_settings_and_the_steam_cloud_mirror()
    {
        _game.Platform.Steam = _game.Temp["steam"];
        var remote = _game.Temp["steam/userdata/42/1086940/remote"];
        _game.Temp.Write("steam/userdata/42/1086940/remote/_SAVE_Public/Savegames/Story/Save1/a.lsv", "kept");
        _game.Temp.Write("steam/userdata/42/1086940/remote/_PROFILE_Public/modsettings.lsx", "mirror");
        var layout = new StandLayout(_game.Settings(), _game.Platform);
        var backup = new ProfileBackup(layout, _game.Platform);
        var original = File.ReadAllText(layout.ModSettingsFile);

        backup.Save();
        // the run: a cut-down mod list, a new save, changed SE settings, a save mirrored to the cloud
        File.WriteAllText(layout.ModSettingsFile, "cut down");
        _game.Temp.Write("profile/PlayerProfiles/Public/Savegames/Story/RunSave/r.lsv", "run");
        File.WriteAllText(layout.SeSettings, "{\"CreateConsole\": true}");
        _game.Temp.Write("steam/userdata/42/1086940/remote/_SAVE_Public/Savegames/Story/RunSave/r.lsv", "run");
        backup.Save(); // an `up` without `down`: the first backup stays the original

        backup.Restore();
        Assert.Equal(original, File.ReadAllText(layout.ModSettingsFile));
        Assert.Equal("{\"Keep\": 1}", File.ReadAllText(layout.SeSettings));
        Assert.True(Directory.Exists(Path.Combine(layout.Saves, "Save1")));
        Assert.False(Directory.Exists(Path.Combine(layout.Saves, "RunSave")));
        var aside = Directory.GetDirectories(Path.Combine(layout.State, "saves-after-run")).Single();
        Assert.True(Directory.Exists(Path.Combine(aside, "RunSave")));
        Assert.True(Directory.Exists(Path.Combine(remote, "_SAVE_Public", "Savegames", "Story", "Save1")));
        Assert.False(Directory.Exists(Path.Combine(remote, "_SAVE_Public", "Savegames", "Story", "RunSave")));
        Assert.Equal(original, File.ReadAllText(Path.Combine(remote, "_PROFILE_Public", "modsettings.lsx")));
        Assert.False(Directory.Exists(layout.Backup));
        backup.Restore(); // nothing to do any more
    }

    [Fact]
    public void Debug_profiles_are_lent_while_other_games_run_and_given_back()
    {
        var layout = new StandLayout(_game.Settings(), _game.Platform);
        var backup = new ProfileBackup(layout, _game.Platform);
        _game.Temp.Write("profile/PlayerProfiles/Debug_Client_Profile_1/modsettings.lsx", "players own");
        backup.Save();
        backup.LendDebugProfiles(withSaves: true);    // no other game: nothing lent
        Assert.Equal("players own", File.ReadAllText(_game.Temp["profile/PlayerProfiles/Debug_Client_Profile_1/modsettings.lsx"]));

        _game.Platform.Processes.Add(new GameProcess(1, _game.Temp["elsewhere/bin/bg3_dx11.exe"]));
        backup.LendDebugProfiles(withSaves: true);
        for (var n = 1; n <= 4; n++)
        {
            var folder = _game.Temp[$"profile/PlayerProfiles/Debug_Client_Profile_{n}"];
            Assert.Equal(File.ReadAllText(layout.ModSettingsFile), File.ReadAllText(Path.Combine(folder, "modsettings.lsx")));
            Assert.True(Directory.Exists(Path.Combine(folder, "Savegames", "Story", "Save1")));
        }

        backup.Restore();
        Assert.Equal("players own", File.ReadAllText(_game.Temp["profile/PlayerProfiles/Debug_Client_Profile_1/modsettings.lsx"]));
        Assert.False(Directory.Exists(_game.Temp["profile/PlayerProfiles/Debug_Client_Profile_2"]));
    }
}
