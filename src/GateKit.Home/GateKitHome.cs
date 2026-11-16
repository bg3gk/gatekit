using System.Text.Json;
using System.Text.Json.Nodes;

namespace GateKit.Home;

/// <summary>A setting that cannot be read (a broken gatekit.json): the message is for the user.</summary>
public sealed class HomeException(string message) : Exception(message);

/// <summary>Where a location's path came from.</summary>
public enum LocationSource
{
    /// <summary>An environment variable named this one location (an override).</summary>
    Variable,
    /// <summary>A gatekit.json named it.</summary>
    Config,
    /// <summary>The configured home's own place for it.</summary>
    Home,
    /// <summary>The per-user default home's place for it: no home is configured.</summary>
    Default,
    /// <summary>Found by looking: the checkout this runs from.</summary>
    Discovered,
    /// <summary>Nothing names it and it has no place in the home (an optional outside location).</summary>
    Unset,
}

/// <summary>What sits at a location: a folder, or one database file.</summary>
public enum LocationKind { Folder, Database }

/// <summary>
/// One place GateKit reads or writes, as resolved: its path (null when <see cref="LocationSource.Unset"/>),
/// where that came from, and the setting that would move it.
/// </summary>
/// <param name="Id">Stable name, used by <c>gatekit doctor --json</c>: <c>index</c>, <c>state</c>, ...</param>
/// <param name="Path">The full path, or null when nothing names it.</param>
/// <param name="Source">Where the path came from.</param>
/// <param name="Origin">The variable or the file that named it; for the home's own places, how the home was found.</param>
/// <param name="Kind">A folder, or a database file.</param>
/// <param name="Change">The setting that would move it, as a person writes it.</param>
/// <param name="Purpose">One line: what lives there.</param>
public sealed record Location(string Id, string? Path, LocationSource Source, string Origin, LocationKind Kind, string Change, string Purpose)
{
    /// <summary>The source as doctor prints it: variable, gatekit.json, home, default, discovered, unset.</summary>
    public string SourceName => Source switch
    {
        LocationSource.Variable => "variable",
        LocationSource.Config => "gatekit.json",
        LocationSource.Home => "home",
        LocationSource.Default => "default",
        LocationSource.Discovered => "discovered",
        _ => "unset",
    };

    /// <summary>Whether the file (a database) or the folder is there.</summary>
    public bool Exists => Path is not null && (Kind == LocationKind.Database ? File.Exists(Path) : Directory.Exists(Path));

    /// <summary>The path; a location nothing names fails with a sentence that says how to name it.</summary>
    public string Require() => Path ?? throw new HomeException($"no {Purpose} is configured: set {Change} (`gatekit doctor` lists every location)");
}

/// <summary>
/// What the home is looked up from. <see cref="Process"/> is the running program's view; a test
/// builds its own so that nothing of the machine it runs on leaks in.
/// </summary>
/// <param name="Environment">The environment variables.</param>
/// <param name="WorkingDirectory">Where gatekit.json is looked for first (this folder and every parent).</param>
/// <param name="ProgramFolder">Where it is looked for next (the program's folder and every parent), or null.</param>
/// <param name="UserConfig">A per-user gatekit.json looked at last, or null.</param>
/// <param name="DefaultHome">The home when nothing names one.</param>
public sealed record HomeSearch(Func<string, string?> Environment, string WorkingDirectory, string? ProgramFolder, string? UserConfig, string DefaultHome)
{
    /// <summary>The running program: its environment, working folder and own folder, the user's config folder.</summary>
    public static HomeSearch Process() => new(System.Environment.GetEnvironmentVariable, Directory.GetCurrentDirectory(),
        GateKitHome.ProgramFolderOfProcess, UserConfigFile(), PerUserDefault());

    /// <summary>
    /// The home when none is configured: the per-user data folder's GateKit (Windows
    /// %LOCALAPPDATA%\GateKit, Linux $XDG_DATA_HOME/GateKit or ~/.local/share/GateKit).
    /// Kept only so that a fresh machine works; doctor says it is not configured.
    /// </summary>
    public static string PerUserDefault()
    {
        var root = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData, System.Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrEmpty(root))
            root = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".local", "share");
        return System.IO.Path.Combine(root, "GateKit");
    }

    /// <summary>&lt;user config folder&gt;/GateKit/gatekit.json (Windows %APPDATA%, Linux ~/.config): a
    /// place to name the home for a user who runs an installed gatekit from anywhere.</summary>
    public static string? UserConfigFile()
    {
        var root = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData, System.Environment.SpecialFolderOption.DoNotVerify);
        return string.IsNullOrEmpty(root) ? null : System.IO.Path.Combine(root, "GateKit", GateKitHome.ConfigName);
    }
}

/// <summary>
/// The one place that computes where GateKit keeps things. Everything lives in one home folder:
///
///     &lt;home&gt;/db/index/gameindex.sqlite   &lt;home&gt;/db/symbols/symbols.sqlite   &lt;home&gt;/db/decomp/
///     &lt;home&gt;/tools/  &lt;home&gt;/se-builds/  &lt;home&gt;/reference/macos-symbols/  &lt;home&gt;/downloads/{mods/}
///     &lt;home&gt;/mods/library/  &lt;home&gt;/mods/sets/&lt;name&gt;.json
///     &lt;home&gt;/play/characters/  &lt;home&gt;/play/saves/  &lt;home&gt;/state/  &lt;home&gt;/cache/{wiki,run,build,index-work}/
///     &lt;home&gt;/scratch/  &lt;home&gt;/backups/
///
/// The home is, in this order: GATEKIT_HOME; "home" in the nearest gatekit.json in the working
/// folder or a parent; the same in the program's folder or a parent; the per-user gatekit.json;
/// else the per-user default (<see cref="HomeSearch.PerUserDefault"/>). A variable of one location
/// (BG3_INDEX_DB, GATEKIT_STATE, ...) overrides that location alone. Any key of gatekit.json is
/// taken from the nearest file that has it, and a relative path in it is relative to that file.
/// See docs/design/home.md.
/// </summary>
public sealed class GateKitHome
{
    public const string ConfigName = "gatekit.json";

    /// <summary>A file whose folder ends the search for gatekit.json upwards: what lies below it does
    /// not see the settings above it (a sandbox; the tests' scratch). Its content means nothing.</summary>
    public const string IsolationMarker = ".gatekit-isolated";
    public const string HomeVariable = "GATEKIT_HOME";
    public const string ConfigVariable = "GATEKIT_CONFIG";

    /// <summary>Set by a program for the copy of itself it runs (RunCopy): the folder it was copied
    /// from, which the copy then takes for its own folder when looking for gatekit.json and the checkout.</summary>
    public const string ProgramFolderVariable = "GATEKIT_RUN_FROM";

    /// <summary>This program's folder as far as settings go: where it was copied from when it runs
    /// as a copy (<see cref="ProgramFolderVariable"/>), else its own.</summary>
    public static string ProgramFolderOfProcess =>
        System.Environment.GetEnvironmentVariable(ProgramFolderVariable) is { Length: > 0 } from ? from : AppContext.BaseDirectory;

    /// <summary>How paths compare on this system: without case on Windows and macOS, with it on Linux.</summary>
    public static StringComparison PathComparison { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    static StringComparer PathComparer => PathComparison == StringComparison.Ordinal ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    readonly Func<string, string?> _environment;
    readonly List<(string File, JsonObject Json)> _configs;

    GateKitHome(HomeSearch search, List<(string File, JsonObject Json)> configs)
    {
        _environment = search.Environment;
        _configs = configs;
        WorkingDirectory = search.WorkingDirectory;
        ProgramFolder = search.ProgramFolder;
        if (Variable(HomeVariable) is { } set)
            (Folder, Source, Origin) = (System.IO.Path.GetFullPath(set), LocationSource.Variable, HomeVariable);
        else if (ConfigPath("home") is { } named)
            (Folder, Source, Origin) = (named.Path, LocationSource.Config, named.File);
        else
            (Folder, Source, Origin) = (System.IO.Path.GetFullPath(search.DefaultHome), LocationSource.Default, "the per-user default");
    }

    /// <summary>The running program's home, read afresh (a few file checks): settings may change while a test runs.</summary>
    public static GateKitHome Current => Resolve(HomeSearch.Process());

    /// <summary>Resolves the home and reads every gatekit.json on the way.</summary>
    public static GateKitHome Resolve(HomeSearch search) => new(search, ReadConfigs(search));

    /// <summary>The home folder.</summary>
    public string Folder { get; }
    /// <summary>How the home was found.</summary>
    public LocationSource Source { get; }
    /// <summary>The variable or file that named the home.</summary>
    public string Origin { get; }
    /// <summary>A home was named (by GATEKIT_HOME or a gatekit.json), not the per-user default.</summary>
    public bool Configured => Source != LocationSource.Default;
    public string WorkingDirectory { get; }
    public string? ProgramFolder { get; }

    /// <summary>The gatekit.json files read, nearest first.</summary>
    public IReadOnlyList<string> ConfigFiles => _configs.Select(c => c.File).ToList();

    /// <summary>The home itself as a location.</summary>
    public Location HomeLocation => new("home", Folder, Source, Origin, LocationKind.Folder,
        $"{HomeVariable}, or \"home\" in {ConfigName}", "the home of everything GateKit keeps");

    // --- the environment and the config files ---

    /// <summary>An environment variable, trimmed; null when unset or blank.</summary>
    public string? Variable(string name) => _environment(name) is { } v && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    /// <summary>A key of gatekit.json as text, from the nearest file that has it, with that file.</summary>
    public (string Value, string File)? ConfigValue(string key)
    {
        foreach (var (file, json) in _configs)
        {
            if (json[key] is JsonValue value)
                return (value.ToString(), file);
        }
        return null;
    }

    /// <summary>A key of gatekit.json read as a path: a relative one is relative to the file's folder.</summary>
    public (string Path, string File)? ConfigPath(string key) => ConfigValue(key) is { } found
        ? (System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(found.File)!, found.Value)), found.File)
        : null;

    static List<(string File, JsonObject Json)> ReadConfigs(HomeSearch search)
    {
        var files = new List<string>();
        void Add(string? file)
        {
            if (file is null) return;
            var full = System.IO.Path.GetFullPath(file);
            if (File.Exists(full) && !files.Contains(full, PathComparer)) files.Add(full);
        }
        void Walk(string? start)
        {
            if (string.IsNullOrEmpty(start)) return;
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                Add(System.IO.Path.Combine(dir.FullName, ConfigName));
                if (File.Exists(System.IO.Path.Combine(dir.FullName, IsolationMarker))) break;
            }
        }

        if (search.Environment(ConfigVariable) is { Length: > 0 } explicitFile)
        {
            if (!File.Exists(explicitFile)) throw new HomeException($"{ConfigVariable} names {explicitFile}, which does not exist");
            Add(explicitFile);
        }
        Walk(search.WorkingDirectory);
        Walk(search.ProgramFolder);
        Add(search.UserConfig);
        return files.Select(f => (f, Parse(f))).ToList();
    }

    static JsonObject Parse(string file)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(file), documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }) as JsonObject ?? throw new HomeException($"{file}: expected a JSON object");
        }
        catch (JsonException e)
        {
            throw new HomeException($"{file}: {e.Message}");
        }
        catch (IOException e)
        {
            throw new HomeException($"{file}: {e.Message}");
        }
    }

    // --- the locations ---

    /// <summary>A place in the home, unless its own variable (or gatekit.json key) names another.</summary>
    Location InHome(string id, string relative, LocationKind kind, string? variable, string purpose, string? configKey = null)
    {
        var change = string.Join(", or ", new[] { variable, configKey is null ? null : $"\"{configKey}\" in {ConfigName}" }.OfType<string>()
            .Append($"the home ({HomeVariable} or \"home\" in {ConfigName})"));
        if (variable is not null && Variable(variable) is { } set)
            return new(id, System.IO.Path.GetFullPath(set), LocationSource.Variable, variable, kind, change, purpose);
        if (configKey is not null && ConfigPath(configKey) is { } named)
            return new(id, named.Path, LocationSource.Config, named.File, kind, change, purpose);
        var path = System.IO.Path.Combine([Folder, .. relative.Split('/')]);
        return new(id, path, Configured ? LocationSource.Home : LocationSource.Default, Origin, kind, change, purpose);
    }

    /// <summary>A place outside the home that only a setting names (the game, the Toolkit, a source tree).</summary>
    Location Outside(string id, string purpose, string configKey, params string[] variables)
    {
        var change = string.Join(" or ", variables) + $", or \"{configKey}\" in {ConfigName}";
        foreach (var variable in variables)
        {
            if (Variable(variable) is { } set)
                return new(id, System.IO.Path.GetFullPath(set), LocationSource.Variable, variable, LocationKind.Folder, change, purpose);
        }
        return ConfigPath(configKey) is { } named
            ? new(id, named.Path, LocationSource.Config, named.File, LocationKind.Folder, change, purpose)
            : new(id, null, LocationSource.Unset, "nothing names it", LocationKind.Folder, change, purpose);
    }

    /// <summary>The game index (BG3_INDEX_DB).</summary>
    public Location Index => InHome("index", "db/index/gameindex.sqlite", LocationKind.Database, "BG3_INDEX_DB", "game index");
    /// <summary>The symbol database (BG3_SYMBOLS_DB).</summary>
    public Location Symbols => InHome("symbols", "db/symbols/symbols.sqlite", LocationKind.Database, "BG3_SYMBOLS_DB", "symbol database");
    /// <summary>The pseudo-code pipeline's folder: decomp.sqlite, Ghidra projects, state files (BG3_DECOMP_WORK).</summary>
    public Location Decomp => InHome("decomp", "db/decomp", LocationKind.Folder, "BG3_DECOMP_WORK", "pseudo-code work folder");
    /// <summary>decomp.sqlite in <see cref="Decomp"/>.</summary>
    public Location DecompDb => Decomp with
    {
        Id = "decomp-db", Path = System.IO.Path.Combine(Decomp.Path!, "decomp.sqlite"), Kind = LocationKind.Database,
        Change = "BG3_DECOMP_WORK (its folder), or the home", Purpose = "pseudo-code database",
    };
    /// <summary>External tools: Ghidra, a JDK, ilspycmd (BG3_DECOMP_TOOLS).</summary>
    public Location Tools => InHome("tools", "tools", LocationKind.Folder, "BG3_DECOMP_TOOLS", "external tools (Ghidra, JDK, ilspycmd)");
    /// <summary>Script Extender builds, one folder per build (the pinned release goes here).</summary>
    public Location SeBuilds => InHome("se-builds", "se-builds", LocationKind.Folder, null, "Script Extender builds");
    /// <summary>Reference files of the macOS build: fieldmeta.json, savefields.json, symbols.tsv (BG3_MACOS_SYMBOLS).</summary>
    public Location MacosSymbols => InHome("macos-symbols", "reference/macos-symbols", LocationKind.Folder, "BG3_MACOS_SYMBOLS", "macOS reference symbols");
    public Location Downloads => InHome("downloads", "downloads", LocationKind.Folder, null, "downloads");
    /// <summary>Mods downloaded for study: read and scanned, never loaded by a game copy.</summary>
    public Location StudyMods => InHome("study-mods", "downloads/mods", LocationKind.Folder, null, "mods downloaded for study (never loaded)");
    /// <summary>The mod files game copies run with.</summary>
    public Location ModLibrary => InHome("mod-library", "mods/library", LocationKind.Folder, null, "mods a game copy runs with");
    /// <summary>Named mod sets, one &lt;name&gt;.json each: which mods, in what order, why.</summary>
    public Location ModSets => InHome("mod-sets", "mods/sets", LocationKind.Folder, null, "named mod sets");
    public Location Characters => InHome("characters", "play/characters", LocationKind.Folder, null, "characters to play");
    public Location Saves => InHome("saves", "play/saves", LocationKind.Folder, null, "saves to play from");
    /// <summary>The copies' one state: instances/, deck/, script-extender.json, runs, sweep (GATEKIT_STATE).</summary>
    public Location State => InHome("state", "state", LocationKind.Folder, "GATEKIT_STATE", "copy state", configKey: "state");
    /// <summary>Pages read from bg3.wiki (GATEKIT_WIKI_CACHE).</summary>
    public Location WikiCache => InHome("wiki-cache", "cache/wiki", LocationKind.Folder, "GATEKIT_WIKI_CACHE", "wiki cache");
    /// <summary>Copies of long-running programs, so a checkout can be rebuilt while they run.</summary>
    public Location RunCache => InHome("run-cache", "cache/run", LocationKind.Folder, null, "copies of running programs");
    /// <summary>The bridge mods packed for each game copy (rebuilt from the sources at will).</summary>
    public Location BuildCache => InHome("build-cache", "cache/build", LocationKind.Folder, null, "packed bridge mods");
    public Location IndexWork => InHome("index-work", "cache/index-work", LocationKind.Folder, null, "index build work");
    /// <summary>`registry check`'s verdicts of the files it read, by their content, one file a checkout.</summary>
    public Location CheckCache => InHome("check-cache", "cache/registry-check", LocationKind.Folder, null, "registry check's verdicts of unchanged files");
    /// <summary>Mods unpacked to be read (`gatekit mod unpack`); never loaded by a game.</summary>
    public Location UnpackCache => InHome("unpack-cache", "cache/unpack", LocationKind.Folder, null, "mods unpacked to be read (never loaded)");
    public Location Scratch => InHome("scratch", "scratch", LocationKind.Folder, null, "scratch space");
    public Location Backups => InHome("backups", "backups", LocationKind.Folder, null, "backups");

    /// <summary>MCP tools added without rebuilding (GATEKIT_MCP_TOOLS): the checkout's mcp-tools, else the home's.</summary>
    public Location McpTools
    {
        get
        {
            var home = InHome("mcp-tools", "mcp-tools", LocationKind.Folder, "GATEKIT_MCP_TOOLS", "MCP tool plugins");
            return home.Source != LocationSource.Variable && Checkout() is { } checkout
                ? home with { Path = System.IO.Path.Combine(checkout, "mcp-tools"), Source = LocationSource.Discovered, Origin = checkout }
                : home;
        }
    }

    /// <summary>The Script Extender source tree (GATEKIT_SE_FORK, BG3SE_SRC, "seFork").</summary>
    public Location SeFork => Outside("se-fork", "Script Extender source tree", "seFork", "GATEKIT_SE_FORK", "BG3SE_SRC");
    /// <summary>The installed game (BG3_GAME, "game"); unset means "look in the Steam libraries".</summary>
    public Location Game => Outside("game", "game folder", "game", "BG3_GAME");
    /// <summary>The official Toolkit (BG3_TOOLKIT, "toolkit"); unset means "look in the Steam libraries".</summary>
    public Location Toolkit => Outside("toolkit", "Toolkit folder", "toolkit", "BG3_TOOLKIT");

    /// <summary>Every location, the home first; the order doctor prints them in.</summary>
    public IReadOnlyList<Location> All =>
    [
        HomeLocation, Index, Symbols, Decomp, DecompDb, Tools, SeBuilds, MacosSymbols, Downloads, StudyMods, ModLibrary, ModSets, Characters, Saves,
        State, WikiCache, RunCache, BuildCache, IndexWork, CheckCache, UnpackCache, Scratch, Backups, McpTools, SeFork, Game, Toolkit,
    ];

    /// <summary>The databases among the locations.</summary>
    public IReadOnlyList<Location> Databases => [Index, Symbols, DecompDb];

    /// <summary>The location a database path is, if it is one of the known ones (for an error that names the setting).</summary>
    public Location? DatabaseAt(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        return Databases.FirstOrDefault(d => string.Equals(d.Path, full, PathComparison));
    }

    /// <summary>The GateKit checkout (the folder holding GateKit.slnx) of the working folder, else of the program, or null.</summary>
    public string? Checkout() => FindCheckout(WorkingDirectory) ?? FindCheckout(ProgramFolder);

    /// <summary>The GateKit checkout a folder is inside, or null.</summary>
    public static string? FindCheckout(string? folder)
    {
        if (string.IsNullOrEmpty(folder)) return null;
        for (var dir = new DirectoryInfo(folder); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "GateKit.slnx"))) return dir.FullName;
        }
        return null;
    }

    /// <summary>
    /// The checkouts of one repository: the main one and every worktree git knows of. Read from
    /// git's own files (<c>.git</c>, <c>worktrees/*/gitdir</c>), so it needs no git program.
    /// </summary>
    public static IReadOnlyList<string> Checkouts(string checkout)
    {
        var found = new List<string> { checkout };
        var dotGit = System.IO.Path.Combine(checkout, ".git");
        string? common = null;
        try
        {
            if (Directory.Exists(dotGit)) common = dotGit;
            else if (File.Exists(dotGit) && File.ReadAllText(dotGit).Trim() is var text && text.StartsWith("gitdir:", StringComparison.Ordinal))
            {
                var gitdir = System.IO.Path.GetFullPath(System.IO.Path.Combine(checkout, text["gitdir:".Length..].Trim()));
                var commonFile = System.IO.Path.Combine(gitdir, "commondir");
                common = File.Exists(commonFile)
                    ? System.IO.Path.GetFullPath(System.IO.Path.Combine(gitdir, File.ReadAllText(commonFile).Trim()))
                    : System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(gitdir));
            }
            if (common is null) return found;
            if (System.IO.Path.GetDirectoryName(common) is { } main && File.Exists(System.IO.Path.Combine(main, "GateKit.slnx"))) found.Add(main);
            var worktrees = System.IO.Path.Combine(common, "worktrees");
            if (Directory.Exists(worktrees))
            {
                foreach (var entry in Directory.EnumerateDirectories(worktrees))
                {
                    var file = System.IO.Path.Combine(entry, "gitdir");
                    if (!File.Exists(file)) continue;
                    var tree = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(File.ReadAllText(file).Trim()));
                    if (tree is not null && Directory.Exists(tree)) found.Add(tree);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return found.Select(f => f.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar))
            .Distinct(PathComparer).ToList();
    }

    /// <summary>Whether a path is inside the home (or is the home).</summary>
    public bool Contains(string path)
    {
        var full = System.IO.Path.GetFullPath(path).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        var home = Folder.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        return full.Equals(home, PathComparison) || full.StartsWith(home + System.IO.Path.DirectorySeparatorChar, PathComparison);
    }
}
