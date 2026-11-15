using GateKit.Home;
using GateKit.Platform;

namespace GateKit.Symbols;

/// <summary>
/// Where the inputs and outputs of the symbol and pseudo-code tools live, read from the home
/// (<see cref="GateKitHome"/>): each location's own variable, else its place in the home. The game
/// and the Toolkit default to Steam's standard library folder. Nothing here points into a
/// particular machine.
/// </summary>
public static class SymbolPaths
{
    static GateKitHome Home => GateKitHome.Current;

    /// <summary>Steam's common folder, where the game and the Toolkit are installed by default.</summary>
    public static string SteamCommon
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                return Path.Combine(pf, "Steam", "steamapps", "common");
            }
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (var steam in new[] { Path.Combine(home, ".steam", "steam"), Path.Combine(home, ".local", "share", "Steam") })
            {
                var common = Path.Combine(steam, "steamapps", "common");
                if (Directory.Exists(common))
                    return common;
            }
            return Path.Combine(home, ".steam", "steam", "steamapps", "common");
        }
    }

    /// <summary>The installed game (BG3_GAME or "game" in gatekit.json), else found in a Steam
    /// library as `gatekit` finds it, else where Steam's default library would hold it.</summary>
    public static string Game => Home.Game.Path ?? FindInSteam(p => p.FindGameFolder()) ?? Path.Combine(SteamCommon, "Baldurs Gate 3");

    /// <summary>The official Toolkit (BG3_TOOLKIT or "toolkit"), else found in a Steam library,
    /// else where Steam's default library would hold it.</summary>
    public static string Toolkit => Home.Toolkit.Path ?? FindToolkit() ?? Path.Combine(SteamCommon, Platform.GamePlatform.ToolkitInstallDir);

    /// <summary>The Toolkit when it is really there: named by a setting, or found in a Steam library; else null.</summary>
    public static string? FindToolkit() => Home.Toolkit.Path is { } named
        ? (Directory.Exists(named) ? named : null)
        : FindInSteam(p => p.FindToolkitFolder());

    /// <summary>Where <see cref="FindToolkit"/> looks, said in words, for a message when it finds nothing.</summary>
    public static string ToolkitSearch => Home.Toolkit.Path is { } named
        ? $"{named} ({Home.Toolkit.SourceName}: {Home.Toolkit.Origin})"
        : "BG3_TOOLKIT and \"toolkit\" in gatekit.json (both unset), then the Steam libraries: "
          + string.Join(", ", Libraries());

    static IReadOnlyList<string> Libraries()
    {
        try { return Platform.GamePlatform.Current.SteamLibraries(); }
        catch (PlatformNotSupportedException) { return []; }
    }

    static string? FindInSteam(Func<Platform.IGamePlatform, string?> find)
    {
        try { return find(Platform.GamePlatform.Current); }
        catch (Exception e) when (e is PlatformNotSupportedException or IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The symbol database (BG3_SYMBOLS_DB); default &lt;home&gt;/db/symbols/symbols.sqlite.</summary>
    public static string SymbolsDb => Home.Symbols.Path!;

    /// <summary>Work folder of the pseudo-code pipeline (BG3_DECOMP_WORK); default &lt;home&gt;/db/decomp.</summary>
    public static string DecompWork => Home.Decomp.Path!;

    /// <summary>The pseudo-code database.</summary>
    public static string DecompDb => Home.DecompDb.Path!;

    /// <summary>Folder with external tools (BG3_DECOMP_TOOLS): ghidra_*, jdk-*, ilspycmd; default &lt;home&gt;/tools.</summary>
    public static string DecompTools => Home.Tools.Path!;

    /// <summary>The Script Extender source tree (GATEKIT_SE_FORK, BG3SE_SRC or "seFork"), or null when none is
    /// configured. A folder that holds the tree one level down (bg3se/, bg3se-main/) is accepted.</summary>
    public static string? Bg3Se
    {
        get
        {
            if (Home.SeFork.Path is not { } set)
                return null;
            if (File.Exists(Path.Combine(set, ScriptExtender.SePatterns.MappingsPath)))
                return set;
            if (Directory.Exists(set))
                foreach (var candidate in Directory.EnumerateDirectories(set).Order(StringComparer.Ordinal))
                    if (File.Exists(Path.Combine(candidate, ScriptExtender.SePatterns.MappingsPath)))
                        return candidate;
            return set;
        }
    }

    /// <summary>Optional reference files of the macOS build made by the coverage project
    /// (fieldmeta.json, savefields.json, symbols.tsv) (BG3_MACOS_SYMBOLS); default
    /// &lt;home&gt;/reference/macos-symbols when that folder exists, else null.</summary>
    public static string? MacosSymbols => Home.MacosSymbols is { Path: { } folder } location
        && (location.Source == LocationSource.Variable || Directory.Exists(folder)) ? folder : null;
}
