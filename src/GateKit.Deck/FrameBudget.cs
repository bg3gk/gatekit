using System.Globalization;
using GateKit.Stand;

namespace GateKit.Deck;

/// <summary>
/// How much of the machine each tile is worth. Four games drawing flat out need a computer most
/// people do not have; four games where the one you are watching draws at sixty and the other
/// three at ten need far less and lose nothing, because a copy nobody is looking at exists to run
/// a scenario or let an agent think and does not care how smoothly it draws.
///
/// The rule is short on purpose: <b>the tile in view is uncapped, every other tile gets the
/// floor, and one copy on its own is never capped at all.</b> Which tile is in view is the one
/// held open, or - in a grid - the one with the keyboard; with a grid and no keyboard anywhere,
/// nobody is being watched in particular and every tile gets the floor.
///
/// It is written as a function from the tiles to a cap each because that is the only way to be
/// sure of it without a machine full of games: <see cref="Caps"/> has no window, no process and no
/// bridge in it, and the deck applies what it returns.
/// </summary>
/// <param name="Floor">Frames a second for a copy nobody is watching; 0 switches the whole thing
/// off and leaves every copy as the engine would have it.</param>
/// <param name="Watched">Frames a second for the tile in view; 0 is no cap, which is the point of
/// capping the others.</param>
public sealed record FrameBudget(int Floor = 10, int Watched = 0)
{
    /// <summary>Nothing is capped and nothing is lifted: the deck does not touch frame rates.</summary>
    public static readonly FrameBudget Off = new(0, 0);

    public bool Enabled => Floor > 0 || Watched > 0;

    /// <summary>What each tile should be capped at. Only the tiles whose game is actually in the
    /// deck are in the answer: a tile that is waiting for its game has nothing to tell.</summary>
    /// <param name="tiles">The deck's tiles, in its own order.</param>
    /// <param name="maximized">The instance held open over the whole deck, or null for a grid.</param>
    /// <param name="focused">The instance whose tile has the keyboard, or null.</param>
    public IReadOnlyDictionary<string, int> Caps(
        IReadOnlyList<Tile> tiles, string? maximized, string? focused)
    {
        var caps = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (!Enabled) return caps;

        var live = tiles.Where(t => t.State == TileState.Shown).ToList();
        if (live.Count == 0) return caps;

        // One copy is the whole machine's business; capping it would be a policy nobody asked for.
        if (live.Count == 1)
        {
            caps[live[0].Instance] = Watched;
            return caps;
        }

        var watched = maximized ?? focused;
        foreach (var tile in live)
        {
            caps[tile.Instance] = string.Equals(tile.Instance, watched, StringComparison.OrdinalIgnoreCase)
                ? Watched
                : Floor;
        }
        return caps;
    }

    /// <summary>What a tile's caption says about its cap, or null while it is not capped.</summary>
    public static string? Note(int fps) =>
        fps > 0 ? string.Create(CultureInfo.InvariantCulture, $"{fps} fps") : null;

    /// <summary>Reads a budget as a person writes one: "off", a floor ("10"), or a floor and a cap
    /// for the tile in view ("10/60").</summary>
    public static FrameBudget Parse(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) return new FrameBudget();
        if (text.Equals("off", StringComparison.OrdinalIgnoreCase)) return Off;

        var parts = text.Split('/', 2);
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var floor) || floor > 1000)
            throw new StandException($"a frame budget is \"off\", a floor like \"10\", or \"10/60\"; \"{value}\" is neither");
        var watched = 0;
        if (parts.Length == 2
            && (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out watched) || watched > 1000))
            throw new StandException($"a frame budget is \"off\", a floor like \"10\", or \"10/60\"; \"{value}\" is neither");
        return new FrameBudget(floor, watched);
    }

    public override string ToString() => !Enabled
        ? "off"
        : string.Create(CultureInfo.InvariantCulture,
            $"{(Watched == 0 ? "no cap" : Watched + " a second")} for the tile in view, {Floor} a second for the rest");
}
