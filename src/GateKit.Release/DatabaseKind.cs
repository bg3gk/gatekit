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

    /// <summary>Where it lives in a home (<see cref="GateKit.Home.GateKitHome"/>).</summary>
    public required Func<GateKit.Home.GateKitHome, GateKit.Home.Location> Locate { get; init; }

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
        Locate = home => home.Index,
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
        Locate = home => home.Symbols,
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

    /// <summary>Where the build command writes it and every query reads it: its variable, else its place in the home.</summary>
    public string DefaultPath => Locate(GateKit.Home.GateKitHome.Current).Path!;

    /// <summary>Base name of the assets of this database in a release: <c>index.sqlite.zst</c>.</summary>
    public string AssetBaseName => $"{Id}.sqlite.zst";

    /// <summary>The GateKit repository this program belongs to or is run from (holds GateKit.slnx), or null.</summary>
    public static string? RepositoryRoot(string? workingDirectory = null) =>
        GateKit.Home.GateKitHome.FindCheckout(GateKit.Home.GateKitHome.ProgramFolderOfProcess)
        ?? GateKit.Home.GateKitHome.FindCheckout(workingDirectory ?? Directory.GetCurrentDirectory());

    /// <summary>
    /// What an installed database remembers about where it came from: written beside it, so
    /// <c>gatekit doctor</c> can say "fetched" rather than "built here".
    /// </summary>
    public static string ReceiptPath(string databasePath) => databasePath + ".release.json";
}
