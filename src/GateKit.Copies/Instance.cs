using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GateKit.Platform;

namespace GateKit.Copies;

/// <summary>
/// A copy of the game of its own, so several games run side by side without seeing each other:
///
///     &lt;instances&gt;/&lt;name&gt;/game/bin    a copy of the game's bin (about 310 MB): own Script Extender
///                                    settings and build, own logs
///     &lt;instances&gt;/&lt;name&gt;/game/Data   a real folder whose entries stand for the game's Data: hard
///                                    links for files, folder links for folders; nothing copied.
///                                    Data/Mods and Data/Video are real, for GateKit's loose files
///     &lt;instances&gt;/&lt;name&gt;/home or prefix   the private profile (platform: Windows USERPROFILE,
///                                    Linux a Wine prefix)
///     &lt;state&gt;/instances/&lt;name&gt;   GateKit's records and logs for it
///
/// Instances must be on the game's drive (hard links). The real game folder and profile are only read.
/// </summary>
public sealed partial class Instance
{
    /// <summary>Not taken over from the real bin: logs, the crash store, and what `gatekit` may have
    /// put there for the installed game (a local Script Extender build and its loader switch).</summary>
    static readonly HashSet<string> BinSkip = new(StringComparer.OrdinalIgnoreCase)
        { "db", "bg3scriptextender.dll", "bg3scriptextender.pdb", "scriptextenderupdaterconfig.json" };

    /// <summary>Folders of Data that are real in the clone, because `gatekit` writes loose files there.</summary>
    static readonly string[] DataOwn = ["Mods", "Video"];

    /// <summary>A Steam copy mirrors each player profile into Steam's cloud folder, which is one for
    /// every copy on the machine. The clone gets an app id that is not Baldur's Gate 3, so Steam's
    /// interface does not start for it.</summary>
    public const string NotAnApp = "2147483646";

    /// <summary>The player profiles a game may pick: "Public" when it is the only game running,
    /// "Debug_Client_Profile_n" when another one runs.</summary>
    public static readonly IReadOnlyList<string> Players =
        ["Public", "Debug_Client_Profile_1", "Debug_Client_Profile_2", "Debug_Client_Profile_3", "Debug_Client_Profile_4"];

    /// <summary>Copied once from the real profile, so the clone starts with the same window and options.</summary>
    static readonly string[] ProfileSeed =
    [
        "graphicSettings.lsx", "PlayerProfiles/playerprofiles8.lsf", "PlayerProfiles/Public/UILayout.lsx",
        "PlayerProfiles/Public/config.lsf", "PlayerProfiles/Public/profile8.lsf", "PlayerProfiles/Public/analyticsUserData.lsf",
    ];

    readonly IGamePlatform _platform;
    readonly CopySettings _settings;

    public Instance(string name, CopySettings settings, IGamePlatform platform)
    {
        if (!ValidName().IsMatch(name)) throw new CopyException($"instance name '{name}': use 1-32 letters, digits, '_' or '-'");
        _platform = platform;
        _settings = settings;
        Name = name;
        SourceGame = settings.GameFolder;
        Root = Path.Combine(settings.InstancesRoot, name);
        Game = Path.Combine(Root, "game");
        Profile = platform.InstanceProfileFolder(Root);
        State = Path.Combine(settings.StateRoot, "instances", name);
    }

    public string Name { get; }
    public string SourceGame { get; }
    public string Root { get; }
    public string Game { get; }
    public string Profile { get; }
    public string State { get; }

    /// <summary>Whether this home is the one that made this instance: its own records for it are
    /// there. The instance folders are shared between everyone on the machine, but the records
    /// are not - a second checkout, a second session, another person's `gatekit` each keep their
    /// own - so this is what separates "a copy of mine" from "a copy somebody else started".
    /// <see cref="GameCopy.UpAsync"/> makes the folder and <see cref="Destroy"/> takes it away.</summary>
    public bool Mine => Directory.Exists(State);

    [GeneratedRegex("^[A-Za-z0-9_-]{1,32}$")]
    private static partial Regex ValidName();

    /// <summary>Names of the instances under a root (folders holding game/bin), sorted.</summary>
    public static IReadOnlyList<string> Names(string instancesRoot) =>
        Directory.Exists(instancesRoot)
            ? Directory.EnumerateDirectories(instancesRoot)
                .Where(d => Directory.Exists(Path.Combine(d, "game", "bin")))
                .Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToList()
            : [];

    // --- the game folder ---

    /// <summary>Creates the clone or brings it up to date (seconds the first time, well under one
    /// afterwards). The instance's game must not be running.</summary>
    /// <param name="skipMods">Data/Mods entries not to link (the installed game's hot-reload copies).</param>
    /// <param name="seCache">The real Script Extender build cache (game path), so the clone does not download again.</param>
    /// <param name="seForkActive">A local Script Extender build is installed in the clone (its loader config is the switcher's).</param>
    public void SyncGame(IReadOnlySet<string> skipMods, string? seCache, bool seForkActive)
    {
        var sourceBin = Path.Combine(SourceGame, "bin");
        var targetBin = Path.Combine(Game, "bin");
        if (!File.Exists(Path.Combine(sourceBin, "steam_appid.txt"))
            && !(Directory.Exists(sourceBin) && Directory.EnumerateFiles(sourceBin, "bg3*.exe").Any()))
            throw new CopyException($"no game in {SourceGame}");
        Directory.CreateDirectory(targetBin);
        foreach (var source in Directory.EnumerateFiles(sourceBin, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceBin, source);
            var first = relative.Split(Path.DirectorySeparatorChar)[0];
            if (BinSkip.Contains(first) || relative.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) continue;
            var target = Path.Combine(targetBin, relative);
            var name = Path.GetFileName(relative);
            if (name.Equals("ScriptExtenderSettings.json", StringComparison.OrdinalIgnoreCase) && File.Exists(target))
                continue; // the instance's own from now on
            if (name.Equals("steam_appid.txt", StringComparison.OrdinalIgnoreCase))
            {
                if (!File.Exists(target) || File.ReadAllText(target) != NotAnApp) File.WriteAllText(target, NotAnApp);
                continue;
            }
            FileTree.CopyIfChanged(source, target);
        }

        // The loader keeps downloaded builds in the profile; with a profile of its own the clone
        // would download them again. Point it at the cache that is there.
        var config = Path.Combine(targetBin, "ScriptExtenderUpdaterConfig.json");
        if (seCache is not null && !seForkActive)
        {
            var settings = JsonFiles.ReadObject(Path.Combine(sourceBin, "ScriptExtenderUpdaterConfig.json"));
            settings.Remove("DebugLoadSE");
            settings["CachePath"] = seCache;
            var text = JsonFiles.Format(settings);
            if (!File.Exists(config) || File.ReadAllText(config) != text) JsonFiles.WriteText(config, text);
        }

        var sourceData = Path.Combine(SourceGame, "Data");
        var targetData = Path.Combine(Game, "Data");
        Directory.CreateDirectory(targetData);
        foreach (var source in Directory.EnumerateFileSystemEntries(sourceData))
        {
            var name = Path.GetFileName(source);
            if (!DataOwn.Contains(name, StringComparer.OrdinalIgnoreCase)) LinkEntry(source, Path.Combine(targetData, name));
        }
        foreach (var own in DataOwn)
        {
            Directory.CreateDirectory(Path.Combine(targetData, own));
            if (!Directory.Exists(Path.Combine(sourceData, own))) continue;
            foreach (var source in Directory.EnumerateFileSystemEntries(Path.Combine(sourceData, own)))
            {
                var name = Path.GetFileName(source);
                if (own == "Mods" && skipMods.Contains(name)) continue;            // the installed game's hot reload
                LinkEntry(source, Path.Combine(targetData, own, name));
            }
        }
    }

    /// <summary>Makes <paramref name="target"/> stand for <paramref name="source"/>: a folder link or a hard link.</summary>
    void LinkEntry(string source, string target)
    {
        if (Directory.Exists(source))
        {
            if (FileTree.IsLink(target))
            {
                if (SamePlace(FileTree.ResolvePath(target), FileTree.ResolvePath(source))) return;
                Directory.Delete(target);
            }
            else if (Directory.Exists(target))
            {
                return; // a real folder the instance made its own (OwnFolder)
            }
            _platform.LinkFolder(source, target);
            return;
        }
        if (File.Exists(target))
        {
            if (FileTree.SameFileContent(source, target)) return;
            File.Delete(target); // the game was updated: the old name still pointed at the old file
        }
        try
        {
            _platform.HardLink(source, target);
        }
        catch (IOException e)
        {
            throw new CopyException($"could not hard-link {source} into {Path.GetDirectoryName(target)} ({e.Message}). "
                + "Instances must be on the game's drive; set GATEKIT_INSTANCES to a folder there.", e);
        }
    }

    bool SamePlace(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), _platform.PathComparison);

    /// <summary>Before loose files go under <paramref name="path"/> (inside the clone's Data): every
    /// link on the way becomes an empty real folder, so nothing is written into the real game.</summary>
    public void OwnFolder(string path)
    {
        var data = Path.Combine(Game, "Data");
        var relative = Path.GetRelativePath(data, Path.GetFullPath(path));
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new CopyException($"{path} is not inside {data}");
        var current = data;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (FileTree.IsLink(current)) Directory.Delete(current);
            Directory.CreateDirectory(current);
        }
    }

    /// <summary>Before a file goes under <paramref name="folder"/> (inside the clone): every folder link
    /// on the way becomes a real folder whose entries stand for what the link held - hard links for
    /// files, folder links for folders, as Data/Mods and Data/Video are made - so the game sees the
    /// same files and nothing is written into the real game. Returns each folder made real with the
    /// folder it stood for, outermost first; <see cref="LinkAgain"/> undoes one.</summary>
    public IReadOnlyList<(string Folder, string Source)> OwnFolderLinked(string folder)
    {
        var relative = Path.GetRelativePath(Game, Path.GetFullPath(folder));
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new CopyException($"{folder} is not inside {Game}");
        var made = new List<(string, string)>();
        var current = Game;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            current = Path.Combine(current, part);
            if (!FileTree.IsLink(current))
            {
                if (Directory.Exists(current)) continue;
                break;
            }
            var source = FileTree.ResolvePath(current);
            if (!Directory.Exists(source)) throw new CopyException($"{current} is a link to {source}, which is no folder");
            Directory.Delete(current);
            Directory.CreateDirectory(current);
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(source)) LinkEntry(entry, Path.Combine(current, Path.GetFileName(entry)));
            }
            catch
            {
                LinkAgain(current, source);
                throw;
            }
            made.Add((current, source));
        }
        return made;
    }

    /// <summary>Undoes <see cref="OwnFolderLinked"/> for one folder: when it holds nothing but the
    /// entries of <paramref name="source"/>, it becomes the folder link it was. False, and the folder
    /// left real, when anything else is in it.</summary>
    public bool LinkAgain(string folder, string source)
    {
        if (FileTree.IsLink(folder) || !Directory.Exists(folder) || !Directory.Exists(source)) return false;
        var entries = Directory.EnumerateFileSystemEntries(folder).ToList();
        foreach (var entry in entries)
        {
            var from = Path.Combine(source, Path.GetFileName(entry));
            var same = FileTree.IsLink(entry)
                ? Directory.Exists(from) && SamePlace(FileTree.ResolvePath(entry), FileTree.ResolvePath(from))
                : File.Exists(entry) && FileTree.SameFileContent(entry, from);
            if (!same) return false;
        }
        foreach (var entry in entries)
        {
            if (FileTree.IsLink(entry)) Directory.Delete(entry);
            else File.Delete(entry);   // a hard link's name; the game's file stays
        }
        Directory.Delete(folder);
        _platform.LinkFolder(source, folder);
        return true;
    }

    /// <summary>True if <paramref name="path"/>, links resolved, lies in this instance (not in the real game).</summary>
    public bool Inside(string path)
    {
        var root = Path.TrimEndingDirectorySeparator(FileTree.ResolvePath(Root)) + Path.DirectorySeparatorChar;
        return FileTree.ResolvePath(path).StartsWith(root, _platform.PathComparison);
    }

    // --- the profile ---

    /// <summary>A profile for the next start: the player's options as they are now, no saves (the
    /// mod list is written by `gatekit`, see <see cref="Spread"/>). The player's mod files only
    /// when <paramref name="withMods"/>; otherwise the copy's Mods folder holds the bridge's paks
    /// alone, so no mod of the player's loads, not even one that overrides without being listed.</summary>
    public void SyncProfile(string realProfile, IReadOnlySet<string> bridgePaks, bool withMods)
    {
        _platform.PrepareInstanceHome(Root);
        foreach (var name in ProfileSeed)
        {
            var source = Path.Combine(realProfile, name);
            var target = Path.Combine(Profile, name);
            if (File.Exists(source) && !File.Exists(target)) FileTree.CopyFile(source, target);
        }
        // The game's options and agreements (config.lsf: WasLarianModPolicyAccepted,
        // ShowFirstTimeSetup, ShowTutorialsAnswered...) live per player profile, and a copy started
        // while another game runs plays as a Debug_Client_Profile_n that never had any: the mod
        // policy and the first-time screens came back at every start (D9 of the day run), and an
        // acceptance given there was lost as soon as the next start took another n. Each such
        // profile gets the player's options once; what the game writes there afterwards stays.
        var options = Path.Combine(Profile, "PlayerProfiles", "Public", "config.lsf");
        foreach (var player in Players)
        {
            var folder = Path.Combine(Profile, "PlayerProfiles", player);
            Directory.CreateDirectory(folder);
            // The copy's own saves stay (D23 of the day run: a copy that plays the story forward
            // saves after every event, and the next start used to wipe them all). What it saved as
            // a Debug_Client_Profile_n is gathered into Public, so every start sees it.
            if (player != "Public")
                SaveStore.Merge(Path.Combine(folder, "Savegames", "Story"), Path.Combine(Profile, "PlayerProfiles", "Public", "Savegames", "Story"));
            var own = Path.Combine(folder, "config.lsf");
            if (player != "Public" && File.Exists(options) && !File.Exists(own)) FileTree.CopyFile(options, own);
        }

        // Mod files are copied, not linked: a mod author rebuilds a .pak in the real folder
        // while an instance has its own copy open.
        var mods = Path.Combine(Profile, "Mods");
        Directory.CreateDirectory(mods);
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var realMods = Path.Combine(realProfile, "Mods");
        if (withMods && Directory.Exists(realMods))
        {
            foreach (var source in Directory.EnumerateFiles(realMods, "*.pak"))
            {
                var name = Path.GetFileName(source);
                if (bridgePaks.Contains(name)) continue; // every instance builds its own
                wanted.Add(name);
                FileTree.CopyIfChanged(source, Path.Combine(mods, name));
            }
        }
        foreach (var old in Directory.EnumerateFiles(mods, "*.pak").ToList())
        {
            var name = Path.GetFileName(old);
            if (!wanted.Contains(name) && !bridgePaks.Contains(name)) File.Delete(old);
        }
    }

    /// <summary>Gives every player profile the game may pick the mod list and saves of "Public".</summary>
    public void Spread()
    {
        var publicFolder = Path.Combine(Profile, "PlayerProfiles", "Public");
        foreach (var player in Players.Skip(1))
        {
            var folder = Path.Combine(Profile, "PlayerProfiles", player);
            FileTree.CopyFile(Path.Combine(publicFolder, "modsettings.lsx"), Path.Combine(folder, "modsettings.lsx"));
            // merged, never wiped: a save the copy made under this profile stays
            SaveStore.Merge(Path.Combine(publicFolder, "Savegames", "Story"), Path.Combine(folder, "Savegames", "Story"));
        }
    }

    /// <summary>Deletes the clone, the private profile and GateKit's records (links removed as links).</summary>
    /// <summary>
    /// Gives this copy another name: its clone and its records move, and everything else about it
    /// stays as it was - the saves in it, the mod list, the Script Extender it was given.
    ///
    /// Worth having because the alternative was `create` and `destroy`, and `create` clones the
    /// whole game again. So a copy made for one piece of work kept that name for ever while it was
    /// used for everything else, and the deck's tiles ended up saying what a copy had once been
    /// for rather than what it was. A folder move is what this actually is.
    /// </summary>
    public void RenameTo(string newName)
    {
        if (newName == Name) return;
        var instance = new Instance(newName, _settings, _platform);
        if (Directory.Exists(instance.Root) || Directory.Exists(instance.State))
            throw new CopyException($"there is already an instance called {newName}");
        Directory.CreateDirectory(Path.GetDirectoryName(instance.Root)!);
        Directory.Move(Root, instance.Root);
        if (Directory.Exists(State))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(instance.State)!);
            Directory.Move(State, instance.State);
        }
    }

    public void Destroy()
    {
        FileTree.RemoveTree(Root);
        FileTree.RemoveTree(State);
        var parent = Path.GetDirectoryName(Root)!;
        if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
    }
}
