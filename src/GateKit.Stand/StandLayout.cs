using GateKit.Platform;

namespace GateKit.Stand;

/// <summary>The files the stand works on: the installed game and the player's profile, or an
/// instance's clone and private profile. Paths are host paths.</summary>
public sealed class StandLayout
{
    public StandLayout(StandSettings settings, IGamePlatform platform)
    {
        Settings = settings;
        Instance = settings.Instance is { Length: > 0 } name ? new Instance(name, settings, platform) : null;
        Game = Instance?.Game ?? settings.GameFolder;
        Profile = Instance?.Profile ?? settings.Profile;
        State = Instance?.State ?? settings.StateRoot;
    }

    public StandSettings Settings { get; }
    public Instance? Instance { get; }
    /// <summary>The game folder this stand starts (the installed game or the instance's clone).</summary>
    public string Game { get; }
    public string Profile { get; }
    /// <summary>This stand's own records: backups, logs, checkpoint, links.</summary>
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
    public string Splash => Path.Combine(Game, "Data", "Video", "Splash_Logo_Larian.bk2");
    public string LooseRecord => Path.Combine(State, "loose.json");
    public string CheckpointRecord => Path.Combine(State, "checkpoint.txt");
    public string Backup => Path.Combine(State, "backup");
    public string CrashMarker => Path.Combine(Profile, "ModCrashSanityCheck");

    /// <summary>A mod's loose Lua in the game folder: wins over the same files in its .pak.</summary>
    public string LooseTarget(string modFolder) => Path.Combine(Game, "Data", "Mods", modFolder, "ScriptExtender", "Lua");
}
