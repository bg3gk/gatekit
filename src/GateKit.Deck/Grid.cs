namespace GateKit.Deck;

/// <summary>A place in the deck, in pixels of its client area: the caption is the top
/// <see cref="Grid.Caption"/> of it, the game fills the rest.</summary>
public readonly record struct TileBox(int X, int Y, int Width, int Height)
{
    /// <summary>Where the game's own window goes inside this tile.</summary>
    public TileBox Inside => new(X, Y + Grid.Caption, Width, Math.Max(1, Height - Grid.Caption));
}

/// <summary>
/// Where the tiles go. One game fills the deck, two sit side by side, three or four make two rows
/// of two, then three columns, and from ten on as square as it goes. A tile keeps its place while
/// its game is away, so a row does not jump about while you are watching it.
/// </summary>
public static class Grid
{
    /// <summary>Space between tiles and around them.</summary>
    public const int Gap = 4;

    /// <summary>Height of a tile's caption: instance, label and process id.</summary>
    public const int Caption = 20;

    public static int Columns(int count) => count switch
    {
        <= 1 => 1,
        <= 4 => 2,
        <= 9 => 3,
        _ => (int)Math.Ceiling(Math.Sqrt(count)),
    };

    public static int Rows(int count, int columns) => count <= 0 ? 0 : (count + columns - 1) / columns;

    /// <summary>The place of each of <paramref name="count"/> tiles in a client area of that size.
    /// The last row is as wide as the others: an empty quadrant is left empty on purpose.</summary>
    public static IReadOnlyList<TileBox> Lay(int count, int width, int height)
    {
        if (count <= 0) return [];
        var columns = Columns(count);
        var rows = Rows(count, columns);
        var boxes = new List<TileBox>(count);
        // Whole pixels, and the remainder spread over the first tiles, so nothing is left over.
        var cellWidth = Math.Max(1, (width - Gap * (columns + 1)) / columns);
        var cellHeight = Math.Max(1, (height - Gap * (rows + 1)) / rows);
        for (var index = 0; index < count; index++)
        {
            var column = index % columns;
            var row = index / columns;
            boxes.Add(new TileBox(
                Gap + column * (cellWidth + Gap),
                Gap + row * (cellHeight + Gap),
                cellWidth, cellHeight));
        }
        return boxes;
    }

    /// <summary>The place of a tile that is held open: the whole client area.</summary>
    public static TileBox Whole(int width, int height) =>
        new(Gap, Gap, Math.Max(1, width - 2 * Gap), Math.Max(1, height - 2 * Gap));

    /// <summary>Where a tile goes while another one is held open: exactly where the held one is,
    /// but at the back of the pile, so the held game covers it completely.
    ///
    /// Measured, and the reason it is not parked outside the deck instead: PrintWindow fails on a
    /// child window that lies wholly outside its parent, and `shot` must keep working on a copy
    /// nobody can see. A window that is merely covered by another is the case the toolkit has
    /// always handled.</summary>
    public static TileBox Behind(int width, int height) => Whole(width, height);
}
