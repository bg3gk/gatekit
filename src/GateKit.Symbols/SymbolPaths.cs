namespace GateKit.Symbols;

/// <summary>
/// Where the inputs and outputs of the symbol and pseudo-code tools live. Every location can be
/// set through an environment variable; the defaults are Steam's standard library folder and the
/// user's local application data folder. Nothing here points into a particular machine.
/// </summary>
public static class SymbolPaths
{
    /// <summary>GateKit's per-user folder: %LOCALAPPDATA%\GateKit on Windows, ~/.local/share/GateKit elsewhere.
    /// The folder's former name (GwynervaDevKit) is used while only that one exists.</summary>
    public static string LocalFolder => GateKit.Release.DatabaseKind.LocalFolder;

    static string? Env(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

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

    /// <summary>The installed game (BG3_GAME).</summary>
    public static string Game => Env("BG3_GAME") ?? Path.Combine(SteamCommon, "Baldurs Gate 3");

    /// <summary>The official Toolkit (BG3_TOOLKIT).</summary>
    public static string Toolkit => Env("BG3_TOOLKIT") ?? Path.Combine(SteamCommon, "Baldurs Gate 3 Toolkit");

    /// <summary>The symbol database (BG3_SYMBOLS_DB); default &lt;local&gt;/symbols/symbols.sqlite.</summary>
    public static string SymbolsDb => GateKit.Release.DatabaseKind.Symbols.DefaultPath;

    /// <summary>Work folder of the pseudo-code pipeline (BG3_DECOMP_WORK); holds decomp.sqlite.</summary>
    public static string DecompWork => Env("BG3_DECOMP_WORK") ?? Path.Combine(LocalFolder, "decomp");

    /// <summary>The pseudo-code database.</summary>
    public static string DecompDb => Path.Combine(DecompWork, "decomp.sqlite");

    /// <summary>Folder with external tools (BG3_DECOMP_TOOLS): ghidra_*, jdk-*, ilspycmd.</summary>
    public static string DecompTools => Env("BG3_DECOMP_TOOLS") ?? Path.Combine(LocalFolder, "tools");

    /// <summary>Script Extender source tree (BG3SE_SRC), or null when none is configured or found.</summary>
    public static string? Bg3Se
    {
        get
        {
            var set = Env("BG3SE_SRC");
            if (set != null)
                return set;
            var tools = Env("BG3_GATEKIT_TOOLS") ?? Env("BG3_DEVKIT_TOOLS");
            if (tools == null)
                return null;
            foreach (var candidate in new[] { Path.Combine(tools, "bg3se-src", "bg3se-main"), Path.Combine(tools, "bg3se") })
                if (File.Exists(Path.Combine(candidate, ScriptExtender.SePatterns.MappingsPath)))
                    return candidate;
            return null;
        }
    }

    /// <summary>Optional reference files of the macOS build made by the coverage project
    /// (fieldmeta.json, savefields.json, symbols.tsv) (BG3_MACOS_SYMBOLS).</summary>
    public static string? MacosSymbols => Env("BG3_MACOS_SYMBOLS");
}
