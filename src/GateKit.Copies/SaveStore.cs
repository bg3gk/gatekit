using GateKit.Platform;

namespace GateKit.Copies;

/// <summary>
/// The saves of a profile, across its player profiles (Public and the Debug_Client_Profile_n a copy
/// plays as while another game runs), and moving them between copies (D23 of the 2026-11-14 day
/// run: one copy plays the story forward and saves; the others start from those saves).
///
/// A save is a folder under <c>PlayerProfiles/&lt;player&gt;/Savegames/Story</c>; its name is the
/// folder's, which is what <c>up --save NAME</c> (-loadSaveGame) takes. The same name in several
/// player profiles is one save: the newest copy of it wins.
/// </summary>
public static class SaveStore
{
    public sealed record Save(string Name, string Player, string Folder, DateTime Written);

    static string Story(string profile, string player) => Path.Combine(profile, "PlayerProfiles", player, "Savegames", "Story");

    /// <summary>When a save was last written: its newest file.</summary>
    public static DateTime Written(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Select(File.GetLastWriteTimeUtc)
            .DefaultIfEmpty(Directory.GetLastWriteTimeUtc(folder)).Max();

    /// <summary>Every save of the profile, newest first; one row per name (its newest copy).</summary>
    public static IReadOnlyList<Save> List(string profile)
    {
        var players = Path.Combine(profile, "PlayerProfiles");
        if (!Directory.Exists(players)) return [];
        var all = new List<Save>();
        foreach (var player in Directory.EnumerateDirectories(players))
        {
            var story = Path.Combine(player, "Savegames", "Story");
            if (!Directory.Exists(story)) continue;
            foreach (var save in Directory.EnumerateDirectories(story))
                all.Add(new Save(Path.GetFileName(save), Path.GetFileName(player), save, Written(save)));
        }
        return [.. all.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.MaxBy(s => s.Written)!)
            .OrderByDescending(s => s.Written)];
    }

    /// <summary>The newest copy of save <paramref name="name"/>, or the reason there is none.</summary>
    public static Save Find(string profile, string name)
    {
        var saves = List(profile);
        return saves.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new CopyException($"no save '{name}' in {profile}; there are: [{string.Join(", ", saves.Take(30).Select(s => s.Name))}]");
    }

    /// <summary>Copies each save of <paramref name="fromStory"/> into <paramref name="toStory"/> where it is
    /// missing or older there; nothing is deleted. Returns how many were copied.</summary>
    public static int Merge(string fromStory, string toStory)
    {
        if (!Directory.Exists(fromStory)) return 0;
        Directory.CreateDirectory(toStory);
        var copied = 0;
        foreach (var save in Directory.EnumerateDirectories(fromStory))
        {
            var target = Path.Combine(toStory, Path.GetFileName(save));
            if (Directory.Exists(target) && Written(target) >= Written(save)) continue;
            Place(save, target);
            copied++;
        }
        return copied;
    }

    /// <summary>Puts a copy of a save folder at <paramref name="target"/>, replacing what was there.</summary>
    static void Place(string save, string target)
    {
        var building = target + ".importing";
        FileTree.TryRemoveTree(building);
        FileTree.CopyTree(save, building);
        if (Directory.Exists(target)) FileTree.TryRemoveTree(target);
        Directory.Move(building, target);
    }

    /// <summary>Writes save <paramref name="name"/> of the profile to <c>&lt;out&gt;/&lt;name&gt;</c>; returns that folder.</summary>
    public static string Export(string profile, string name, string output)
    {
        var save = Find(profile, name);
        var target = Path.Combine(output, save.Name);
        Directory.CreateDirectory(output);
        Place(save.Folder, target);
        return target;
    }

    /// <summary>Puts the save folder <paramref name="folder"/> into every player profile of the copy
    /// that exists (Public first), so a game running as any of them sees it; returns the players.</summary>
    public static IReadOnlyList<string> Import(string profile, string folder, IEnumerable<string> players)
    {
        if (!Directory.Exists(folder)) throw new CopyException($"no save folder {folder}");
        if (!Directory.EnumerateFiles(folder, "*.lsv", SearchOption.AllDirectories).Any())
            throw new CopyException($"{folder} holds no .lsv: not a save folder (Savegames/Story/<name>)");
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)));
        var into = new List<string>();
        foreach (var player in players)
        {
            var playerFolder = Path.Combine(profile, "PlayerProfiles", player);
            if (player != "Public" && !Directory.Exists(playerFolder)) continue;
            Place(folder, Path.Combine(Story(profile, player), name));
            into.Add(player);
        }
        return into;
    }
}
