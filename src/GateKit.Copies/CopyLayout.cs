using GateKit.Platform;

namespace GateKit.Copies;

/// <summary>The files a copy works on: the installed game and the player's profile, or an
/// instance's clone and private profile. Paths are host paths.</summary>
public sealed class CopyLayout
{
    public CopyLayout(CopySettings settings, IGamePlatform platform)
    {
        Settings = settings;
        Instance = settings.Instance is { Length: > 0 } name ? new Instance(name, settings, platform) : null;
        Game = Instance?.Game ?? settings.GameFolder;
        Profile = Instance?.Profile ?? settings.Profile;
        State = Instance?.State ?? settings.StateRoot;
    }

    public CopySettings Settings { get; }
    public Instance? Instance { get; }
    /// <summary>The game folder this home starts (the installed game or the instance's clone).</summary>
    public string Game { get; }
    public string Profile { get; }
    /// <summary>This copy's own records: backups, logs, checkpoint, links.</summary>
    public string State { get; }
    /// <summary>The installed game's state folder (instances read its backup and links).</summary>
    public string SharedState => Settings.StateRoot;

    public string Bin => Path.Combine(Game, "bin");
    public string Exe => Path.Combine(Bin, Settings.ExeName);
    public string Mods => Path.Combine(Profile, "Mods");
    public string PublicProfile => Path.Combine(Profile, "PlayerProfiles", "Public");
    public string ModSettingsFile => Path.Combine(PublicProfile, "modsettings.lsx");
    public string Saves => Path.Combine(PublicProfile, "Savegames", "Story");
    public string ScriptExtenderFolder => Path.Combine(Profile, "Script Extender");
    /// <summary>The bridge's channel folder (docs/contracts/bridge-protocol.md).</summary>
    public string Channel => Path.Combine(ScriptExtenderFolder, ModSettings.BridgeFolder);
    public string SeSettings => Path.Combine(Bin, "ScriptExtenderSettings.json");
    public string SeLogs => Path.Combine(State, "se-logs");
    public string LooseRecord => Path.Combine(State, "loose.json");
    /// <summary>The files a test put into the game folder for its own time (<see cref="TestFiles"/>).</summary>
    public string TestFilesRecord => Path.Combine(State, "test-files.json");
    public string CheckpointRecord => Path.Combine(State, "checkpoint.txt");
    /// <summary>The saves made right after a level's start, one line a level and mod list (<see cref="StartSaves"/>).</summary>
    public string StartSavesRecord => Path.Combine(State, "start-saves.txt");
    public string Backup => Path.Combine(State, "backup");
    public string CrashMarker => Path.Combine(Profile, "ModCrashSanityCheck");

    /// <summary>The one check of the rule that no command writes into the installed game unless it is
    /// named: without an instance, <paramref name="verb"/> goes on only with `--installed-game`.
    /// Called before anything is touched.</summary>
    public void RequireNamed(string verb)
    {
        if (Instance is null && !Settings.InstalledGameNamed) throw new CopyException(NotNamed(verb));
    }

    /// <summary>What a command that would write into an unnamed installed game says.</summary>
    public static string NotNamed(string verb) =>
        $"`{verb}` would write into the installed game and the player's own profile, which no option names: "
        + $"name a copy with --instance NAME (or GATEKIT_INSTANCE), or the installed game with {CopySettings.InstalledGameOption}; nothing was done";

    /// <summary>A mod's loose Lua in the game folder: wins over the same files in its .pak.</summary>
    public string LooseTarget(string modFolder) => Path.Combine(Game, "Data", "Mods", modFolder, "ScriptExtender", "Lua");
}
