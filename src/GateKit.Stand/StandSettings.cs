using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using GateKit.Platform;

namespace GateKit.Stand;

/// <summary>
/// Where the stand finds things and how it behaves. Each value comes from, in order: an
/// environment variable, a `gatekit.json` file, a default found on this machine (Steam).
///
/// | Setting         | Environment              | gatekit.json      | Default                                         |
/// |-----------------|--------------------------|-------------------|-------------------------------------------------|
/// | game folder     | BG3_GAME                 | "game"            | Baldur's Gate 3 in a Steam library              |
/// | executable      | GATEKIT_EXE              | "exe"             | bg3_dx11.exe                                    |
/// | real profile    | GATEKIT_PROFILE          | "profile"         | the platform's profile of the installed game    |
/// | instances       | GATEKIT_INSTANCES        | "instances"       | &lt;game's parent&gt;/Baldurs Gate 3 GateKit    |
/// | state folder    | GATEKIT_STATE            | "state"           | &lt;repo&gt;/.state, else local app data/GateKit/state |
/// | Script Extender | GATEKIT_SE               | "scriptExtender"  | the build `se install` was last given, else stock (a folder with BG3ScriptExtender.dll, or "stock") |
/// | packer          | GATEKIT_PACKER           | "packer"          | "builtin" (LSLib in process); "divine" for Divine |
/// | Divine          | GATEKIT_DIVINE           | "divine"          | none: only the "divine" packer needs it         |
/// | .NET for Divine | GATEKIT_DOTNET_ROOT      | "dotnetRoot"      | none: Divine runs on the newest runtime installed |
/// | start timeout   | GATEKIT_START_TIMEOUT    | "startTimeout"    | 75 (seconds to the bridge's first heartbeat)    |
/// | bridge mods     | GATEKIT_BRIDGE           | "bridge"          | &lt;repo&gt;/mods (Weave and the GateKit bridge) |
/// | instance        | GATEKIT_INSTANCE / --instance | "instance"   | none: the installed game                        |
/// | deck backing    | GATEKIT_DECK_BACKING     | "deckBacking"     | "adopt" ("mirror": the frames, not the window)  |
/// | mirrored window | GATEKIT_DECK_CONCEAL     | "deckConceal"     | "offscreen" ("hidden", "none")                  |
///
/// Every GATEKIT_X may also be spelled BG3_GATEKIT_X (the old stand's names). gatekit.json is
/// GATEKIT_CONFIG if set, else the first gatekit.json in the working folder or a parent, else
/// &lt;user config folder&gt;/GateKit/gatekit.json (Windows %APPDATA%, Linux ~/.config).
/// </summary>
/// <summary>Which <see cref="IModPacker"/> the stand builds mods with.</summary>
public enum PackerKind
{
    /// <summary>LSLib, in this process: nothing to install.</summary>
    Builtin,
    /// <summary>LSLib's Divine command line tool, from GATEKIT_DIVINE.</summary>
    Divine,
}

public sealed record StandSettings
{
    public required string GameFolder { get; init; }
    public string ExeName { get; init; } = "bg3_dx11.exe";
    public required string Profile { get; init; }
    public required string InstancesRoot { get; init; }
    public required string StateRoot { get; init; }
    /// <summary>A folder holding BG3ScriptExtender.dll to load for runs; null = the stock build.</summary>
    public string? ScriptExtender { get; init; }
    /// <summary>Which packer `build` uses: the built-in LSLib one, or Divine.</summary>
    public PackerKind Packer { get; init; } = PackerKind.Builtin;
    public string? Divine { get; init; }
    public string? DotnetRoot { get; init; }
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(75);
    /// <summary>The bridge mods' source: a folder of mod roots (mods/GateKit, mods/Weave), or one mod root.</summary>
    public string? BridgeSource { get; init; }
    public string? Instance { get; init; }
    /// <summary>Which backing a deck fills a new tile with: "adopt" (the game's own window) or
    /// "mirror" (the frames it shares). `window mirror|adopt NAME` changes one tile at run time.</summary>
    public string DeckBacking { get; init; } = "adopt";
    /// <summary>What a deck does with the window of a copy it is mirroring: "offscreen" (off every
    /// screen and out of the taskbar, still a shown window), "hidden", or "none".</summary>
    public DeckConceal DeckConceal { get; init; } = DeckConceal.Offscreen;
    /// <summary>How a deck shares the machine between its copies: "off", a floor for the copies
    /// nobody is watching ("10"), or that and a cap for the one being watched ("10/60"). The deck
    /// parses it; the stand only carries it, because what it means belongs to the deck.</summary>
    public string DeckFps { get; init; } = "10";
    /// <summary>What a deck does about sound: "watched" (only the tile in view is heard) or "off".
    /// The deck parses it; the stand only carries it.</summary>
    public string DeckSound { get; init; } = "watched";
    /// <summary>Which copies a deck takes hold of: "mine" (only the ones this stand made) or
    /// "all". The default is "mine", because a deck adopts a window, caps a frame rate and mutes
    /// the sound, and doing that to a game somebody else started is not something to do by
    /// accident.</summary>
    public string DeckAdopt { get; init; } = "mine";
    /// <summary>The gatekit.json that was read, if any.</summary>
    public string? ConfigFile { get; init; }

    /// <summary>Reads the settings. <paramref name="environment"/> defaults to the process environment.</summary>
    public static StandSettings Resolve(IGamePlatform platform, Func<string, string?>? environment = null,
        string? workingDirectory = null, string? instanceOverride = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        workingDirectory ??= Directory.GetCurrentDirectory();
        var configPath = FindConfig(environment, workingDirectory);
        var config = ReadConfig(configPath);

        string? Get(string env, string key)
        {
            foreach (var name in Aliases(env))
            {
                var value = environment(name);
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }
            return config?[key] is JsonValue v ? v.ToString() : null;
        }

        var repo = RepositoryRoot(workingDirectory);
        var game = Get("BG3_GAME", "game") ?? platform.FindGameFolder()
            ?? throw new StandException("Baldur's Gate 3 was not found in any Steam library; set BG3_GAME to its folder (the one holding bin and Data)");
        game = Path.GetFullPath(game);
        var stateRoot = Path.GetFullPath(Get("GATEKIT_STATE", "state")
            ?? (repo is not null ? Path.Combine(repo, ".state")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GateKit", "state")));
        // Which Script Extender a run loads. Said outright it is obeyed, "stock" included; said
        // nowhere, the last build `gatekit se install <folder>` was given is used, so that choice
        // has to be made once rather than exported by every shell that starts a game.
        var asked = Get("GATEKIT_SE", "scriptExtender");
        var se = asked is null ? RememberedScriptExtender(stateRoot)
            : asked.Equals("stock", StringComparison.OrdinalIgnoreCase) ? null : asked;
        var timeout = Get("GATEKIT_START_TIMEOUT", "startTimeout");
        var bridge = Get("GATEKIT_BRIDGE", "bridge") ?? (repo is null ? null : Path.Combine(repo, "mods"));

        return new StandSettings
        {
            GameFolder = game,
            ExeName = Get("GATEKIT_EXE", "exe") ?? environment("BG3_EXE") ?? "bg3_dx11.exe",
            Profile = Path.GetFullPath(Get("GATEKIT_PROFILE", "profile") ?? platform.ProfileFolder(game)),
            InstancesRoot = Path.GetFullPath(Get("GATEKIT_INSTANCES", "instances")
                ?? Path.Combine(Path.GetDirectoryName(game)!, "Baldurs Gate 3 GateKit")),
            StateRoot = stateRoot,
            ScriptExtender = se is null ? null : Path.GetFullPath(se),
            Packer = ReadPacker(Get("GATEKIT_PACKER", "packer")),
            Divine = Get("GATEKIT_DIVINE", "divine"),
            DotnetRoot = Get("GATEKIT_DOTNET_ROOT", "dotnetRoot"),
            StartTimeout = timeout is null ? TimeSpan.FromSeconds(75) : TimeSpan.FromSeconds(double.Parse(timeout, CultureInfo.InvariantCulture)),
            BridgeSource = bridge is null ? null : Path.GetFullPath(bridge),
            Instance = instanceOverride ?? Get("GATEKIT_INSTANCE", "instance"),
            DeckBacking = ReadBacking(Get("GATEKIT_DECK_BACKING", "deckBacking")),
            DeckConceal = ReadConceal(Get("GATEKIT_DECK_CONCEAL", "deckConceal")),
            DeckFps = Get("GATEKIT_DECK_FPS", "deckFps") ?? "10",
            DeckSound = Get("GATEKIT_DECK_SOUND", "deckSound") ?? "watched",
            DeckAdopt = ReadAdopt(Get("GATEKIT_DECK_ADOPT", "deckAdopt")),
            ConfigFile = configPath,
        };
    }

    /// <summary>Which backing a deck fills a new tile with. Adopt stays the default: it is proven,
    /// and mirror asks the game for something only GateKit's Script Extender build can give.</summary>
    static string ReadBacking(string? value) => value?.ToLowerInvariant() switch
    {
        null or "" or "adopt" => "adopt",
        "mirror" => "mirror",
        _ => throw new StandException($"GATEKIT_DECK_BACKING is \"{value}\"; it must be \"adopt\" or \"mirror\""),
    };

    /// <summary>A deck takes hold of what it is given, and nothing else, unless it is told to.</summary>
    static string ReadAdopt(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "mine" or "own" => "mine",
        "all" or "any" => "all",
        _ => throw new StandException($"GATEKIT_DECK_ADOPT is \"{value}\"; it must be \"mine\" or \"all\""),
    };

    static DeckConceal ReadConceal(string? value) => value?.ToLowerInvariant() switch
    {
        null or "" or "offscreen" => DeckConceal.Offscreen,
        "hidden" => DeckConceal.Hidden,
        "none" => DeckConceal.None,
        _ => throw new StandException($"GATEKIT_DECK_CONCEAL is \"{value}\"; it must be \"offscreen\", \"hidden\" or \"none\""),
    };

    static PackerKind ReadPacker(string? value) => value?.ToLowerInvariant() switch
    {
        null or "" or "builtin" or "lslib" => PackerKind.Builtin,
        "divine" => PackerKind.Divine,
        _ => throw new StandException($"GATEKIT_PACKER is \"{value}\"; it must be \"builtin\" or \"divine\""),
    };

    /// <summary>GATEKIT_X, then the old stand's BG3_GATEKIT_X and BG3_DEVKIT_X.</summary>
    static IEnumerable<string> Aliases(string name)
    {
        yield return name;
        if (name.StartsWith("GATEKIT_", StringComparison.Ordinal))
        {
            yield return "BG3_" + name;
            yield return "BG3_DEVKIT_" + name["GATEKIT_".Length..];
        }
    }

    static string? FindConfig(Func<string, string?> environment, string workingDirectory)
    {
        var explicitPath = environment("GATEKIT_CONFIG");
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return File.Exists(explicitPath) ? Path.GetFullPath(explicitPath) : throw new StandException($"GATEKIT_CONFIG names {explicitPath}, which does not exist");
        for (var dir = new DirectoryInfo(workingDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "gatekit.json");
            if (File.Exists(candidate)) return candidate;
        }
        var user = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GateKit", "gatekit.json");
        return File.Exists(user) ? user : null;
    }

    static JsonObject? ReadConfig(string? path)
    {
        if (path is null) return null;
        try
        {
            return JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                ?? throw new StandException($"{path}: expected a JSON object");
        }
        catch (JsonException e)
        {
            throw new StandException($"{path}: {e.Message}");
        }
    }

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
            var folder = JsonFiles.ReadObject(note)["scriptExtender"]?.ToString();
            return folder is { Length: > 0 } && File.Exists(Path.Combine(folder, "BG3ScriptExtender.dll")) ? folder : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Remembers the build to load when nothing says otherwise, so the choice is made once instead
    /// of exported by every shell. GATEKIT_SE still wins, "stock" included.
    /// </summary>
    public static void RememberScriptExtender(string stateRoot, string folder)
    {
        Directory.CreateDirectory(stateRoot);
        JsonFiles.WriteObject(ScriptExtenderNote(stateRoot), new JsonObject
        {
            ["_"] = "The Script Extender build a run loads when GATEKIT_SE and the config say nothing. Written by `gatekit se install`.",
            ["scriptExtender"] = Path.GetFullPath(folder),
        });
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
}
