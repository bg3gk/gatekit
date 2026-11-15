using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using GateKit.Home;
using GateKit.Platform;

namespace GateKit.Copies;

/// <summary>
/// Where `gatekit` finds things and how it behaves. Each value comes from, in order: an
/// environment variable, a `gatekit.json` file (the nearest one that has the key; a relative path
/// is relative to that file), a default - the home (<see cref="GateKitHome"/>) for everything the
/// copies keep, or what is found on this machine (Steam).
///
/// | Setting         | Environment              | gatekit.json      | Default                                         |
/// |-----------------|--------------------------|-------------------|-------------------------------------------------|
/// | game folder     | BG3_GAME                 | "game"            | Baldur's Gate 3 in a Steam library              |
/// | executable      | GATEKIT_EXE              | "exe"             | bg3_dx11.exe                                    |
/// | real profile    | GATEKIT_PROFILE          | "profile"         | the platform's profile of the installed game    |
/// | instances       | GATEKIT_INSTANCES        | "instances"       | &lt;game's parent&gt;/Baldurs Gate 3 GateKit (copies need the game's volume) |
/// | state folder    | GATEKIT_STATE            | "state"           | &lt;home&gt;/state, the same for every checkout |
/// | Script Extender | GATEKIT_SE               | "scriptExtender"  | the build `se install` was last given, else stock (a folder with BG3ScriptExtender.dll, or "stock") |
/// | packer          | GATEKIT_PACKER           | "packer"          | "builtin" (LSLib in process); "divine" for Divine |
/// | Divine          | GATEKIT_DIVINE           | "divine"          | none: only the "divine" packer needs it         |
/// | .NET for Divine | GATEKIT_DOTNET_ROOT      | "dotnetRoot"      | none: Divine runs on the newest runtime installed |
/// | start timeout   | GATEKIT_START_TIMEOUT    | "startTimeout"    | 75 (seconds to the bridge's first heartbeat)    |
/// | bridge mods     | GATEKIT_BRIDGE           | "bridge"          | &lt;repo&gt;/mods (Weave and the GateKit bridge) |
/// | instance        | GATEKIT_INSTANCE / --instance | "instance"   | none: the installed game                        |
/// | deck backing    | GATEKIT_DECK_BACKING     | "deckBacking"     | "adopt" ("mirror": the frames, not the window)  |
/// | mirrored window | GATEKIT_DECK_CONCEAL     | "deckConceal"     | "offscreen" ("hidden", "none")                  |
/// | simple level    | GATEKIT_SIMPLE_LEVEL_CHARACTERS | "simpleLevelCharacters" | 12: a level with at most this many characters besides the party is a simple test level (the reset ladder cleans there instead of reloading) |
/// | simple levels   | GATEKIT_SIMPLE_LEVELS    | "simpleLevels"    | none: comma-separated levels taken as simple whatever their census |
///
/// The gatekit.json files are GATEKIT_CONFIG if set, then every gatekit.json in the working folder
/// and its parents, then in the program's folder and its parents, then
/// &lt;user config folder&gt;/GateKit/gatekit.json (Windows %APPDATA%, Linux ~/.config).
/// </summary>
/// <summary>Which <see cref="IModPacker"/> `gatekit` builds mods with.</summary>
public enum PackerKind
{
    /// <summary>LSLib, in this process: nothing to install.</summary>
    Builtin,
    /// <summary>LSLib's Divine command line tool, from GATEKIT_DIVINE.</summary>
    Divine,
}

public sealed record CopySettings
{
    public required string GameFolder { get; init; }
    public string ExeName { get; init; } = "bg3_dx11.exe";
    public required string Profile { get; init; }
    public required string InstancesRoot { get; init; }
    public required string StateRoot { get; init; }
    /// <summary>A folder holding BG3ScriptExtender.dll to load for runs; null = the stock build.</summary>
    public string? ScriptExtender { get; init; }
    /// <summary>The stock build was asked for outright (GATEKIT_SE=stock), not merely left unnamed.</summary>
    public bool ScriptExtenderStock { get; init; }
    /// <summary>Which packer `build` uses: the built-in LSLib one, or Divine.</summary>
    public PackerKind Packer { get; init; } = PackerKind.Builtin;
    public string? Divine { get; init; }
    public string? DotnetRoot { get; init; }
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(75);
    /// <summary>The bridge mods' source: a folder of mod roots (mods/GateKit, mods/Weave), or one mod root.</summary>
    public string? BridgeSource { get; init; }
    public string? Instance { get; init; }
    /// <summary>The installed game was named outright (`--installed-game`): only then does a command
    /// with no instance write into it (<see cref="CopyLayout.RequireNamed"/>).</summary>
    public bool InstalledGameNamed { get; init; }
    /// <summary>The option that names the installed game, a recursive option of the root command.</summary>
    public const string InstalledGameOption = "--installed-game";
    /// <summary>Which backing a deck fills a new tile with: "adopt" (the game's own window) or
    /// "mirror" (the frames it shares). `window mirror|adopt NAME` changes one tile at run time.</summary>
    public string DeckBacking { get; init; } = "adopt";
    /// <summary>What a deck does with the window of a copy it is mirroring: "offscreen" (off every
    /// screen and out of the taskbar, still a shown window), "hidden", or "none".</summary>
    public DeckConceal DeckConceal { get; init; } = DeckConceal.Offscreen;
    /// <summary>How a deck shares the machine between its copies: "off", a floor for the copies
    /// nobody is watching ("10"), or that and a cap for the one being watched ("10/60"). The deck
    /// parses it; a copy only carries it, because what it means belongs to the deck.</summary>
    public string DeckFps { get; init; } = "10";
    /// <summary>What a deck does about sound: "watched" (only the tile in view is heard) or "off".
    /// The deck parses it; a copy only carries it.</summary>
    public string DeckSound { get; init; } = "watched";
    /// <summary>Which copies a deck takes hold of: "mine" (only the ones this home made) or
    /// "all". The default is "mine", because a deck adopts a window, caps a frame rate and mutes
    /// the sound, and doing that to a game somebody else started is not something to do by
    /// accident.</summary>
    public string DeckAdopt { get; init; } = "mine";
    /// <summary>A level with at most this many characters besides the party counts as a simple test
    /// level (GK.Stage.Census): the reset ladder cleans it up instead of reloading it.</summary>
    public int SimpleLevelCharacters { get; init; } = 12;
    /// <summary>Levels taken as simple whatever their census says (GATEKIT_SIMPLE_LEVELS).</summary>
    public IReadOnlyList<string> SimpleLevels { get; init; } = [];
    /// <summary>The nearest gatekit.json that was read, if any (<see cref="Home"/> has them all).</summary>
    public string? ConfigFile => Home?.ConfigFiles.FirstOrDefault();

    /// <summary>The home these settings were resolved in: every location GateKit keeps, and where each came from.</summary>
    public GateKitHome? Home { get; init; }

    /// <summary>Where packed bridge mods are kept, one folder per game copy (a cache: rebuilt from
    /// the sources at will). Null: the copy's state folder, as for settings made by hand.</summary>
    public string? BuildCache { get; init; }

    /// <summary>Where downloaded Script Extender builds go, one folder per build.</summary>
    public string? SeBuilds { get; init; }

    /// <summary>Reads the settings. <paramref name="environment"/> defaults to the process environment;
    /// <paramref name="search"/>, to the process's own view of the home (a test passes one of its own).</summary>
    public static CopySettings Resolve(IGamePlatform platform, Func<string, string?>? environment = null,
        string? workingDirectory = null, string? instanceOverride = null, HomeSearch? search = null, bool installedGame = false)
    {
        environment ??= Environment.GetEnvironmentVariable;
        workingDirectory ??= Directory.GetCurrentDirectory();
        GateKitHome home;
        try
        {
            home = GateKitHome.Resolve(search ?? new HomeSearch(environment, workingDirectory, GateKitHome.ProgramFolderOfProcess,
                HomeSearch.UserConfigFile(), HomeSearch.PerUserDefault()));
        }
        catch (HomeException e)
        {
            throw new CopyException(e.Message);
        }

        string? Get(string env, string key) => home.Variable(env) ?? home.ConfigValue(key)?.Value;
        // A path: a variable is taken as it is, a relative path in a gatekit.json is relative to that file.
        string? GetPath(string env, string key) => home.Variable(env) is { } set ? Path.GetFullPath(set) : home.ConfigPath(key)?.Path;

        var repo = home.Checkout();
        var game = home.Game.Path ?? platform.FindGameFolder()
            ?? throw new CopyException("Baldur's Gate 3 was not found in any Steam library; set BG3_GAME to its folder (the one holding bin and Data)");
        game = Path.GetFullPath(game);
        var stateRoot = home.State.Path!;
        // Which Script Extender a run loads. Said outright it is obeyed, "stock" included; said
        // nowhere, the last build `gatekit se install <folder>` was given is used, so that choice
        // has to be made once rather than exported by every shell that starts a game.
        var asked = Get("GATEKIT_SE", "scriptExtender");
        var stock = asked is not null && asked.Equals("stock", StringComparison.OrdinalIgnoreCase);
        var se = asked is null ? RememberedScriptExtender(stateRoot)
            : stock ? null : GetPath("GATEKIT_SE", "scriptExtender");
        var timeout = Get("GATEKIT_START_TIMEOUT", "startTimeout");
        var bridge = GetPath("GATEKIT_BRIDGE", "bridge") ?? (repo is null ? null : Path.Combine(repo, "mods"));

        return new CopySettings
        {
            GameFolder = game,
            ExeName = Get("GATEKIT_EXE", "exe") ?? "bg3_dx11.exe",
            Profile = Path.GetFullPath(GetPath("GATEKIT_PROFILE", "profile") ?? platform.ProfileFolder(game)),
            InstancesRoot = GetPath("GATEKIT_INSTANCES", "instances")
                ?? Path.Combine(Path.GetDirectoryName(game)!, "Baldurs Gate 3 GateKit"),
            StateRoot = stateRoot,
            Home = home,
            BuildCache = home.BuildCache.Path,
            SeBuilds = home.SeBuilds.Path,
            ScriptExtender = se is null ? null : Path.GetFullPath(se),
            ScriptExtenderStock = asked is not null && asked.Equals("stock", StringComparison.OrdinalIgnoreCase),
            Packer = ReadPacker(Get("GATEKIT_PACKER", "packer")),
            Divine = Get("GATEKIT_DIVINE", "divine"),
            DotnetRoot = Get("GATEKIT_DOTNET_ROOT", "dotnetRoot"),
            StartTimeout = timeout is null ? TimeSpan.FromSeconds(75) : TimeSpan.FromSeconds(double.Parse(timeout, CultureInfo.InvariantCulture)),
            BridgeSource = bridge is null ? null : Path.GetFullPath(bridge),
            Instance = instanceOverride ?? Get("GATEKIT_INSTANCE", "instance"),
            InstalledGameNamed = installedGame,
            DeckBacking = ReadBacking(Get("GATEKIT_DECK_BACKING", "deckBacking")),
            DeckConceal = ReadConceal(Get("GATEKIT_DECK_CONCEAL", "deckConceal")),
            DeckFps = Get("GATEKIT_DECK_FPS", "deckFps") ?? "10",
            DeckSound = Get("GATEKIT_DECK_SOUND", "deckSound") ?? "watched",
            DeckAdopt = ReadAdopt(Get("GATEKIT_DECK_ADOPT", "deckAdopt")),
            SimpleLevelCharacters = Get("GATEKIT_SIMPLE_LEVEL_CHARACTERS", "simpleLevelCharacters") is { } simple
                ? int.Parse(simple, CultureInfo.InvariantCulture) : 12,
            SimpleLevels = (Get("GATEKIT_SIMPLE_LEVELS", "simpleLevels") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        };
    }

    /// <summary>Which backing a deck fills a new tile with. Adopt stays the default: it is proven,
    /// and mirror asks the game for something only GateKit's Script Extender build can give.</summary>
    static string ReadBacking(string? value) => value?.ToLowerInvariant() switch
    {
        null or "" or "adopt" => "adopt",
        "mirror" => "mirror",
        _ => throw new CopyException($"GATEKIT_DECK_BACKING is \"{value}\"; it must be \"adopt\" or \"mirror\""),
    };

    /// <summary>A deck takes hold of what it is given, and nothing else, unless it is told to.</summary>
    static string ReadAdopt(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "mine" or "own" => "mine",
        "all" or "any" => "all",
        _ => throw new CopyException($"GATEKIT_DECK_ADOPT is \"{value}\"; it must be \"mine\" or \"all\""),
    };

    static DeckConceal ReadConceal(string? value) => value?.ToLowerInvariant() switch
    {
        null or "" or "offscreen" => DeckConceal.Offscreen,
        "hidden" => DeckConceal.Hidden,
        "none" => DeckConceal.None,
        _ => throw new CopyException($"GATEKIT_DECK_CONCEAL is \"{value}\"; it must be \"offscreen\", \"hidden\" or \"none\""),
    };

    static PackerKind ReadPacker(string? value) => value?.ToLowerInvariant() switch
    {
        null or "" or "builtin" or "lslib" => PackerKind.Builtin,
        "divine" => PackerKind.Divine,
        _ => throw new CopyException($"GATEKIT_PACKER is \"{value}\"; it must be \"builtin\" or \"divine\""),
    };

    /// <summary>Where the remembered Script Extender build is written down, beside the other state.</summary>
    static string ScriptExtenderNote(string stateRoot) => Path.Combine(stateRoot, "script-extender.json");

    /// <summary>
    /// The build `gatekit se install &lt;folder&gt;` was last given, if its dll is still there.
    /// A build that has been moved or deleted is forgotten quietly: the run then uses the stock
    /// extender and `doctor` says so, which is better than failing to start over a stale note.
    /// </summary>
    static string? RememberedScriptExtender(string stateRoot)
    {
        var note = ScriptExtenderNote(stateRoot);
        if (!File.Exists(note)) return null;
        try
        {
            var folder = SharedFile.Read(note, ScriptExtenderNoteVersion)["scriptExtender"]?.ToString();
            return folder is { Length: > 0 } && File.Exists(Path.Combine(folder, "BG3ScriptExtender.dll")) ? folder : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        catch (HomeException e) { throw new CopyException(e.Message); }
    }

    /// <summary>Format of script-extender.json, shared by every checkout (<see cref="SharedFile"/>).</summary>
    public const int ScriptExtenderNoteVersion = 1;

    /// <summary>
    /// Remembers the build to load when nothing says otherwise, so the choice is made once instead
    /// of exported by every shell. GATEKIT_SE still wins, "stock" included.
    /// </summary>
    public static void RememberScriptExtender(string stateRoot, string folder)
    {
        try
        {
            SharedFile.Replace(ScriptExtenderNote(stateRoot), ScriptExtenderNoteVersion, new JsonObject
            {
                ["_"] = "The Script Extender build a run loads when GATEKIT_SE and the config say nothing. Written by `gatekit se install`.",
                ["scriptExtender"] = Path.GetFullPath(folder),
            });
        }
        catch (HomeException e) { throw new CopyException(e.Message); }
    }

    /// <summary>The GateKit repository this program belongs to or is run from (holds GateKit.slnx), or null.</summary>
    public static string? RepositoryRoot(string? workingDirectory = null) =>
        GateKit.Release.DatabaseKind.RepositoryRoot(workingDirectory);
}
