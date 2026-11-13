namespace GateKit.Release;

/// <summary>
/// A database that can travel as a release asset instead of being built: where it lives, what
/// builds it, and what it is called in a manifest. Only the two the project publishes are here;
/// the pseudo-code database is never published and has no kind.
/// </summary>
public sealed record DatabaseKind
{
    /// <summary>Name in a manifest and on the command line: <c>index</c>, <c>symbols</c>.</summary>
    public required string Id { get; init; }

    /// <summary>One line a person reads: what this database holds.</summary>
    public required string Title { get; init; }

    /// <summary>File name the build command writes, and the install command restores.</summary>
    public required string FileName { get; init; }

    /// <summary>Folder under the per-user GateKit folder where it lives by default.</summary>
    public required string Folder { get; init; }

    /// <summary>Environment variable that moves it elsewhere.</summary>
    public required string EnvironmentVariable { get; init; }

    /// <summary>The command that builds it from the files on this machine.</summary>
    public required string BuildCommand { get; init; }

    /// <summary>The command that fetches it instead.</summary>
    public required string InstallCommand { get; init; }

    /// <summary>What an install says out loud, so nobody is in doubt about whose material this is.</summary>
    public required string Attribution { get; init; }

    public static readonly DatabaseKind Index = new()
    {
        Id = "index",
        Title = "the index of the game's own content (dialogue, stats, templates, localized strings)",
        FileName = "gameindex.sqlite",
        Folder = "index",
        EnvironmentVariable = "BG3_INDEX_DB",
        BuildCommand = "gatekit data build",
        InstallCommand = "gatekit data install",
        Attribution =
            "This is Baldur's Gate 3's own content, read out of the game's files and rearranged so it can be\n" +
            "queried. It belongs to Larian Studios; GateKit only indexes it, and holds no code of the game.\n" +
            Withdrawal,
    };

    public static readonly DatabaseKind Symbols = new()
    {
        Id = "symbols",
        Title = "the symbol database of the engine's binaries (names, addresses, sizes, layouts, calls, strings)",
        FileName = "symbols.sqlite",
        Folder = "symbols",
        EnvironmentVariable = "BG3_SYMBOLS_DB",
        BuildCommand = "gatekit symbols build",
        InstallCommand = "gatekit symbols install",
        Attribution =
            "This describes Baldur's Gate 3's own binaries, which are Larian Studios'. It holds facts about\n" +
            "that code - names, addresses, sizes, layouts, calls and strings - and no code of the game, no\n" +
            "decompiled output and nothing the game ships.\n" +
            Withdrawal,
    };

    /// <summary>The promise the published data carries, said wherever the data is handed over.</summary>
    public const string Withdrawal =
        "The data is published separately from the tools and is withdrawn the same day Larian, or anyone\n" +
        "acting for them, asks for it: https://github.com/bg3gk/map";

    public static IReadOnlyList<DatabaseKind> All { get; } = [Index, Symbols];

    /// <summary>Where the build command writes it: the environment variable, else the default place.</summary>
    public string DefaultPath =>
        System.Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } set
            ? set
            : Path.Combine(LocalFolder, Folder, FileName);

    /// <summary>Base name of the assets of this database in a release: <c>index.sqlite.zst</c>.</summary>
    public string AssetBaseName => $"{Id}.sqlite.zst";

    /// <summary>
    /// GateKit's per-user folder: %LOCALAPPDATA%\GateKit on Windows, ~/.local/share/GateKit elsewhere.
    /// The folder's former name (GwynervaDevKit) is used while only that one exists.
    /// </summary>
    public static string LocalFolder
    {
        get
        {
            var root = System.Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (string.IsNullOrEmpty(root))
                root = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData, System.Environment.SpecialFolderOption.DoNotVerify);
            if (string.IsNullOrEmpty(root))
                root = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".local", "share");
            var current = Path.Combine(root, "GateKit");
            var old = Path.Combine(root, "GwynervaDevKit");
            return Directory.Exists(old) && !Directory.Exists(current) ? old : current;
        }
    }

    /// <summary>The GateKit repository this program belongs to or is run from (holds GateKit.slnx), or null.</summary>
    public static string? RepositoryRoot(string? workingDirectory = null)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, workingDirectory ?? Directory.GetCurrentDirectory() })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "GateKit.slnx"))) return dir.FullName;
            }
        }
        return null;
    }

    /// <summary>
    /// What an installed database remembers about where it came from: written beside it, so
    /// <c>gatekit doctor</c> can say "fetched" rather than "built here".
    /// </summary>
    public static string ReceiptPath(string databasePath) => databasePath + ".release.json";
}
