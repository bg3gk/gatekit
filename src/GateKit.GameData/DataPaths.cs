using GateKit.Home;
using GateKit.Platform;

namespace GateKit.GameData;

/// <summary>Where the offline databases and the game data are: options first, then the home
/// (<see cref="GateKitHome"/>), whose locations each have their own override variable.</summary>
public static class DataPaths
{
    /// <summary>The game index: BG3_INDEX_DB, else <c>&lt;home&gt;/db/index/gameindex.sqlite</c>.</summary>
    public static string IndexDatabase => GateKitHome.Current.Index.Path!;

    /// <summary>The code symbol database: BG3_SYMBOLS_DB, else <c>&lt;home&gt;/db/symbols/symbols.sqlite</c>.</summary>
    public static string SymbolsDatabase => GateKitHome.Current.Symbols.Path!;

    /// <summary>The pseudo-code database: <c>&lt;BG3_DECOMP_WORK or home/db/decomp&gt;/decomp.sqlite</c>.</summary>
    public static string DecompDatabase => GateKitHome.Current.DecompDb.Path!;

    /// <summary>
    /// The game's Data folder: <paramref name="data"/> when given, else <c>&lt;game&gt;/Data</c> with the game
    /// folder from <paramref name="game"/>, BG3_GAME or "game" in gatekit.json, else the game found
    /// in a Steam library (as `gatekit` finds it). Null when none is known.
    /// </summary>
    public static string? GameData(string? data, string? game)
    {
        if (!string.IsNullOrEmpty(data))
        {
            return data;
        }
        game = string.IsNullOrEmpty(game) ? GateKitHome.Current.Game.Path ?? FindGameInSteam() : game;
        return string.IsNullOrEmpty(game) ? null : Path.Combine(game, "Data");
    }

    /// <summary>Where <see cref="GameData"/> looks when nothing is given, in words: for a message when it finds nothing.</summary>
    public static string GameSearch => GateKitHome.Current.Game is { Path: { } named } location
        ? $"{named} ({location.SourceName}: {location.Origin})"
        : "BG3_GAME and \"game\" in gatekit.json (both unset), then the Steam libraries: " + string.Join(", ", SteamLibraries());

    static string? FindGameInSteam()
    {
        try { return GateKit.Platform.GamePlatform.Current.FindGameFolder(); }
        catch (Exception e) when (e is PlatformNotSupportedException or IOException or UnauthorizedAccessException) { return null; }
    }

    static IReadOnlyList<string> SteamLibraries()
    {
        try { return GateKit.Platform.GamePlatform.Current.SteamLibraries(); }
        catch (PlatformNotSupportedException) { return []; }
    }
}
