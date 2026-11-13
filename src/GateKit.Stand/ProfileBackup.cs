using System.Globalization;
using GateKit.Platform;

namespace GateKit.Stand;

/// <summary>
/// The installed game only (an instance's profile is its own): a copy of the saves, the mod list
/// and Script Extender's settings as they were before the stand touched them, put back on `down`,
/// Steam's cloud mirror included.
/// </summary>
public sealed class ProfileBackup(StandLayout layout, IGamePlatform platform)
{
    string Marker => layout.Backup;

    /// <summary>Steam's cloud folders for the game: the Steam root's, and the game library's.</summary>
    IReadOnlyList<string> CloudFolders()
    {
        var roots = new List<string>();
        if (platform.SteamRoot() is { } root) roots.Add(root);
        // <library>/steamapps/common/<game>: the old stand looked here
        var library = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(layout.Settings.GameFolder)));
        if (library is not null) roots.Add(library);
        return roots.Distinct(StringComparer.FromComparison(platform.PathComparison)).SelectMany(Steam.CloudFolders).ToList();
    }

    /// <summary>Copies saves, mod list and SE settings; an existing backup (an `up` without `down`) is the original and stays.</summary>
    public void Save()
    {
        if (Directory.Exists(Marker)) return;
        var tmp = Marker + ".tmp";
        FileTree.TryRemoveTree(tmp);
        Directory.CreateDirectory(tmp);
        if (Directory.Exists(layout.Saves)) FileTree.CopyTree(layout.Saves, Path.Combine(tmp, "Story"));
        if (!File.Exists(layout.ModSettingsFile))
            throw new StandException($"{layout.ModSettingsFile} does not exist; start the game once normally first");
        FileTree.CopyFile(layout.ModSettingsFile, Path.Combine(tmp, "modsettings.lsx"));
        if (File.Exists(layout.SeSettings)) FileTree.CopyFile(layout.SeSettings, Path.Combine(tmp, Path.GetFileName(layout.SeSettings)));
        Directory.Move(tmp, Marker);
    }

    /// <summary>Puts everything back; what the run left in the saves is moved aside (the newest 3 kept).</summary>
    public void Restore()
    {
        if (!Directory.Exists(Marker)) return;
        var story = Path.Combine(Marker, "Story");
        if (Directory.Exists(story))
        {
            if (Directory.Exists(layout.Saves))
            {
                var asideRoot = Path.Combine(layout.State, "saves-after-run");
                var aside = Path.Combine(asideRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
                Directory.CreateDirectory(asideRoot);
                FileTree.Move(layout.Saves, aside);
                foreach (var old in Directory.EnumerateDirectories(asideRoot).Order(StringComparer.Ordinal).SkipLast(3).ToList())
                    FileTree.TryRemoveTree(old);
            }
            FileTree.CopyTree(story, layout.Saves);
        }
        var modSettings = Path.Combine(Marker, "modsettings.lsx");
        FileTree.CopyFile(modSettings, layout.ModSettingsFile);
        // The game mirrors profile and saves into Steam's cloud folder and brings back from there
        // whatever is missing locally - saves a run made included. The mirror is treated alike.
        var kept = Directory.Exists(story)
            ? Directory.EnumerateFileSystemEntries(story).Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal)
            : null;
        foreach (var remote in CloudFolders())
        {
            var mirror = Path.Combine(remote, "_SAVE_Public", "Savegames", "Story");
            if (kept is not null && Directory.Exists(mirror))
            {
                foreach (var save in Directory.EnumerateDirectories(mirror).ToList())
                    if (!kept.Contains(Path.GetFileName(save))) FileTree.TryRemoveTree(save);
            }
            var settings = Path.Combine(remote, "_PROFILE_Public", "modsettings.lsx");
            if (File.Exists(settings)) FileTree.CopyFile(modSettings, settings);
        }
        var seSettings = Path.Combine(Marker, Path.GetFileName(layout.SeSettings));
        if (File.Exists(seSettings)) FileTree.CopyFile(seSettings, layout.SeSettings);
        ReturnDebugProfiles(Path.Combine(Marker, "debug-profiles"));
        FileTree.RemoveTree(Marker);
    }

    /// <summary>A game started while another runs uses "Debug_Client_Profile_n", not "Public", with a
    /// mod list and saves of its own - it would start without the bridge. When other games run,
    /// those profiles get the stand's mod list (and saves) for the run; what was there is parked.</summary>
    public void LendDebugProfiles(bool withSaves)
    {
        if (platform.GameProcesses().Count == 0) return;
        var record = Path.Combine(Marker, "debug-profiles");
        Directory.CreateDirectory(record);
        foreach (var name in Instance.Players.Skip(1))
        {
            var folder = Path.Combine(layout.Profile, "PlayerProfiles", name);
            if (!FileTree.Exists(Path.Combine(record, name)) && !File.Exists(Path.Combine(record, name + ".absent")))
            {
                var mirrored = CloudFolders().Any(remote =>
                    Directory.Exists(Path.Combine(remote, "_SAVE_" + name)) || Directory.Exists(Path.Combine(remote, "_PROFILE_" + name)));
                if (!mirrored) File.WriteAllBytes(Path.Combine(record, name + ".nocloud"), []);
                if (Directory.Exists(folder)) FileTree.Move(folder, Path.Combine(record, name));
                else File.WriteAllBytes(Path.Combine(record, name + ".absent"), []);
            }
            FileTree.TryRemoveTree(folder);
            Directory.CreateDirectory(folder);
            FileTree.CopyFile(layout.ModSettingsFile, Path.Combine(folder, "modsettings.lsx"));
            if (withSaves && Directory.Exists(layout.Saves)) FileTree.CopyTree(layout.Saves, Path.Combine(folder, "Savegames", "Story"));
        }
    }

    void ReturnDebugProfiles(string record)
    {
        if (!Directory.Exists(record)) return;
        foreach (var name in Instance.Players.Skip(1))
        {
            var folder = Path.Combine(layout.Profile, "PlayerProfiles", name);
            var parked = Directory.Exists(Path.Combine(record, name));
            var absent = File.Exists(Path.Combine(record, name + ".absent"));
            if (!parked && !absent) continue;
            FileTree.TryRemoveTree(folder);
            if (parked) FileTree.Move(Path.Combine(record, name), folder);
            if (File.Exists(Path.Combine(record, name + ".nocloud")))
            {
                foreach (var remote in CloudFolders())
                {
                    FileTree.TryRemoveTree(Path.Combine(remote, "_SAVE_" + name));
                    FileTree.TryRemoveTree(Path.Combine(remote, "_PROFILE_" + name));
                }
            }
        }
    }
}
