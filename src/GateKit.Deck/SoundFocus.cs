using GateKit.Stand;

namespace GateKit.Deck;

/// <summary>
/// Which copy is heard. Four games playing at once is a problem the deck did not create and is
/// the only thing able to fix, because it is the only thing that knows which one you are looking
/// at.
///
/// The rule matches the one the frame budget uses, for the same reason: <b>the tile in view is
/// heard and the rest are silent.</b> In view means the tile held open over the deck, or - in a
/// grid - the one with the keyboard; with a grid and nothing clicked, no tile is being watched in
/// particular and the deck leaves every copy as it is rather than silencing the lot.
///
/// It is a choice and not a law. A tile whose sound a person has touched is pinned and the deck
/// stops policing it, so listening to two fights at once stays possible; and
/// <c>GATEKIT_DECK_SOUND=off</c> switches the whole thing off.
///
/// Like <see cref="FrameBudget"/> this is a function from the tiles to an answer, with no window,
/// no process and no game in it.
/// </summary>
/// <param name="Enabled">False leaves every copy's sound alone.</param>
public sealed record SoundFocus(bool Enabled = true)
{
    public static readonly SoundFocus Off = new(false);

    /// <summary>Which copies should be heard and which silenced. Only the copies the deck has
    /// something to say about are in the answer: a tile waiting for its game, and a tile whose
    /// sound a person has taken over, are both left out.</summary>
    public IReadOnlyDictionary<string, bool> Heard(
        IReadOnlyList<Tile> tiles, string? maximized, string? focused)
    {
        var heard = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (!Enabled) return heard;

        var live = tiles.Where(t => t.State == TileState.Shown && !t.SoundPinned).ToList();
        if (live.Count == 0) return heard;

        var watched = maximized ?? focused;
        // Nothing is being watched in particular: silencing everything would be a policy nobody
        // asked for, and one copy on its own is simply the game.
        if (watched is null || live.Count == 1)
        {
            foreach (var tile in live) heard[tile.Instance] = true;
            return heard;
        }

        foreach (var tile in live)
            heard[tile.Instance] = string.Equals(tile.Instance, watched, StringComparison.OrdinalIgnoreCase);
        return heard;
    }

    /// <summary>Reads the setting: "watched" (the default) or "off".</summary>
    public static SoundFocus Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "watched" => new SoundFocus(),
        "off" or "all" => Off,
        _ => throw new StandException($"GATEKIT_DECK_SOUND is \"{value}\"; it must be \"watched\" or \"off\""),
    };

    public override string ToString() =>
        Enabled ? "only the tile in view is heard" : "off (every copy is left as it is)";
}
