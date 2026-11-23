namespace GateKit.Copies;

/// <summary>
/// A copy's saves made right after a level's start (`up --level`, before anything is staged), one
/// for each level and mod list: a running game of the same mods goes into that level by loading
/// the save (the game's own load in the session), not by a new process. A line of
/// <see cref="CopyLayout.StartSavesRecord"/> is `level TAB mods TAB save folder`; mods is the list
/// asked for, comma-separated, or `*` for the copy's own. A save whose folder is gone is not found.
/// </summary>
public sealed class StartSaves(CopyLayout layout)
{
    /// <summary>The mod list as the record writes it.</summary>
    public static string ModsKey(IReadOnlyList<string>? mods) => mods is null ? "*" : string.Join(",", mods);

    /// <summary>The level part of MODULE:LEVEL (the save is of the level; the module is the start's).</summary>
    public static string LevelKey(string level) => level[(level.IndexOf(':', StringComparison.Ordinal) + 1)..];

    IEnumerable<string[]> Lines() => File.Exists(layout.StartSavesRecord)
        ? File.ReadAllLines(layout.StartSavesRecord).Select(l => l.Split('\t')).Where(p => p.Length == 3)
        : [];

    /// <summary>The save of <paramref name="level"/>'s start with <paramref name="mods"/>, when its folder is still there.</summary>
    public string? Find(string level, IReadOnlyList<string>? mods)
    {
        var (l, m) = (LevelKey(level), ModsKey(mods));
        var found = Lines().LastOrDefault(p => string.Equals(p[0], l, StringComparison.OrdinalIgnoreCase) && p[1] == m)?[2];
        return found is not null && Directory.Exists(Path.Combine(layout.Saves, found)) ? found : null;
    }

    /// <summary>Records <paramref name="save"/> as the start of <paramref name="level"/> with <paramref name="mods"/>.</summary>
    public void Record(string level, IReadOnlyList<string>? mods, string save)
    {
        var (l, m) = (LevelKey(level), ModsKey(mods));
        var kept = Lines().Where(p => !(string.Equals(p[0], l, StringComparison.OrdinalIgnoreCase) && p[1] == m))
            .Select(p => string.Join('\t', p)).ToList();
        kept.Add(string.Join('\t', l, m, save));
        Directory.CreateDirectory(layout.State);
        File.WriteAllLines(layout.StartSavesRecord, kept, JsonFiles.Utf8);
    }
}
