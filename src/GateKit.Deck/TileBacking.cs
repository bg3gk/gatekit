namespace GateKit.Deck;

/// <summary>What came of trying to show a game in its tile.</summary>
public enum TileShow
{
    /// <summary>It is in the tile now.</summary>
    Shown,

    /// <summary>Not yet - the game is not far enough along (no window, no bridge). Try again later.</summary>
    NotYet,

    /// <summary>This backing cannot show this game at all; the tile says where the game went instead.</summary>
    Refused,
}

/// <summary>
/// Where a tile's picture comes from. The deck decides <em>where</em> a tile is - the grid, the
/// one held open, what happens when a game restarts - and a backing decides <em>how</em> the game
/// gets into it. The two know nothing of each other beyond this.
///
/// There is one backing today, <see cref="Windows.AdoptBacking"/>: the game's own window is taken
/// into the deck (SetParent). The design (docs/design/deck.md) names a second, <c>mirror</c>: draw
/// the game's own frames into the tile and send input back through the game's own code, which
/// needs no window system's permission and would work where adopting a window is forbidden -
/// natively on Wayland, it always will be. It is not built here. This interface is the seam it
/// goes behind, so adding it touches nothing of the grid, the tiles, the commands or the API.
/// </summary>
public interface ITileBacking
{
    /// <summary>What it is called, for `window list` and the tile's own caption: "adopt", later "mirror".</summary>
    string Name { get; }

    /// <summary>Starts showing that tile's game inside <paramref name="host"/>.</summary>
    TileShow Show(Tile tile, nint host, out string? why);

    /// <summary>Puts the tile's content where the deck says, without activating anything.
    /// <paramref name="front"/> is false for a tile another one is held open over.</summary>
    void Place(Tile tile, TileBox box, bool front);

    /// <summary>Stops showing it, giving back anything borrowed (a window goes back to the desktop).</summary>
    void Drop(Tile tile);

    /// <summary>Gives that tile's game the keyboard, as far as this backing can.</summary>
    void Focus(Tile tile);

    /// <summary>A key the deck received while this tile had the keyboard.</summary>
    void Key(Tile tile, uint message, nint wParam, nint lParam);

    /// <summary>Draws the tile's content, for a backing that has to paint it itself. A backing that
    /// puts a real window in the tile draws nothing: the window draws itself.</summary>
    void Draw(nint deviceContext, Tile tile, TileBox box);
}
