using GateKit.Release;

namespace GateKit.GameData;

/// <summary>Where the offline databases and the game data are, from options and the environment.</summary>
public static class DataPaths
{
    /// <summary>Per-user GateKit folder: %LOCALAPPDATA%\GateKit on Windows, ~/.local/share/GateKit elsewhere.</summary>
    public static string LocalFolder => DatabaseKind.LocalFolder;

    /// <summary>The game index: BG3_INDEX_DB, else <c>&lt;local&gt;/index/gameindex.sqlite</c>.</summary>
    public static string IndexDatabase => DatabaseKind.Index.DefaultPath;

    /// <summary>The code symbol database: BG3_SYMBOLS_DB, else <c>&lt;local&gt;/symbols/symbols.sqlite</c>.</summary>
    public static string SymbolsDatabase => DatabaseKind.Symbols.DefaultPath;

    /// <summary>The pseudo-code database: <c>&lt;BG3_DECOMP_WORK or local/decomp&gt;/decomp.sqlite</c>.</summary>
    public static string DecompDatabase =>
        Path.Combine(Environment.GetEnvironmentVariable("BG3_DECOMP_WORK") is { Length: > 0 } p ? p : Path.Combine(LocalFolder, "decomp"), "decomp.sqlite");

    /// <summary>
    /// The game's Data folder: <paramref name="data"/> when given, else <c>&lt;game&gt;/Data</c> with the game
    /// folder from <paramref name="game"/> or BG3_GAME. Null when none is known.
    /// </summary>
    public static string? GameData(string? data, string? game)
    {
        if (!string.IsNullOrEmpty(data))
        {
            return data;
        }
        game = string.IsNullOrEmpty(game) ? Environment.GetEnvironmentVariable("BG3_GAME") : game;
        return string.IsNullOrEmpty(game) ? null : Path.Combine(game, "Data");
    }
}
