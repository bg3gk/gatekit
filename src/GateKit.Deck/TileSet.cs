using GateKit.Stand;

namespace GateKit.Deck;

/// <summary>What a tile is doing.</summary>
public enum TileState
{
    /// <summary>The slot is kept, but there is no game in it yet: starting, restarting, or gone.</summary>
    Waiting,

    /// <summary>The game is in the tile.</summary>
    Shown,

    /// <summary>The game runs, but this deck cannot show it; it is in a window of its own.</summary>
    Loose,
}

/// <summary>One game's place in the deck. What is known about the game here is what any backing
/// would need: which instance it is, what it is called, and which process it is.</summary>
public sealed class Tile(string instance)
{
    public string Instance { get; } = instance;
    public string? Label { get; set; }
    public int Pid { get; set; }

    /// <summary>The game's own window, where the backing works with one; 0 otherwise.</summary>
    public nint Window { get; set; }

    public TileState State { get; set; } = TileState.Waiting;

    /// <summary>When the tile last became what it is now (how long it has been waiting).</summary>
    public DateTime Since { get; set; } = DateTime.UtcNow;

    /// <summary>The backing said it cannot show this game; do not keep asking.</summary>
    public bool Stubborn { get; set; }

    /// <summary>Which backing fills this tile (`adopt`, `mirror`); null until the deck has decided,
    /// which it does from its setting and from `window mirror|adopt`.</summary>
    public string? Backing { get; set; }

    /// <summary>A word from the backing for the caption - a mirrored tile's frame rate, say.</summary>
    public string? Note { get; set; }

    /// <summary>What this tile is waiting for, or why its game is in a window of its own instead.
    /// It goes into the record the `window` commands read, so a tile that is not showing its game
    /// says why without anyone having to go and find the deck's own output.</summary>
    public string? Why { get; set; }

    /// <summary>The frame cap the deck's budget has given this copy, as the caption says it, or
    /// null while the copy draws as fast as it likes. See <see cref="FrameBudget"/>.</summary>
    public string? Capped { get; set; }

    /// <summary>How loud this copy is, 0 to 100, as the deck last set it. Its master volume; the
    /// game's other eleven categories are `gatekit sound`'s business, not a tile caption's.</summary>
    public int Volume { get; set; } = 100;

    /// <summary>Silenced, whether by the deck's policy or by hand.</summary>
    public bool Muted { get; set; }

    /// <summary>A person has taken this copy's sound over from the deck (the caption's slider or
    /// its mute). The deck then leaves it alone: listening to two fights at once is a legitimate
    /// thing to want. See <see cref="SoundFocus"/>.</summary>
    public bool SoundPinned { get; set; }

    public string Caption => (Label is { Length: > 0 } label ? $"{Instance} - {label}" : Instance)
        + (Pid > 0 ? $"  (pid {Pid})" : "")
        + (Note is { Length: > 0 } note ? $"  {note}" : "")
        + (Capped is { Length: > 0 } cap ? $"  [{cap}]" : "");

    public DeckTile Record(int column, int row, string? state = null) =>
        new(Instance, Label, Pid, state ?? State.ToString().ToLowerInvariant(), column, row, Why);
}

/// <summary>Where one tile is, and whether it is the one in view (false: another is held open over
/// it, so it sits behind, the same size and still rendering).</summary>
public sealed record TilePlace(Tile Tile, TileBox Box, bool Front);

/// <summary>What the deck should ask its backing after the tiles were brought up to date.</summary>
public sealed record TileChanges(IReadOnlyList<Tile> ToShow, IReadOnlyList<Tile> ToDrop);

/// <summary>
/// The tiles and what happens to them: a new game takes the first free slot, a game that goes away
/// leaves its slot waiting (a restart finds it again), and one tile may be held open over the whole
/// deck. Nothing here shows anything - the deck asks its <see cref="ITileBacking"/> to do that with
/// what <see cref="Observe"/> returns - so all of it is testable on any operating system.
/// </summary>
public sealed class TileSet
{
    readonly List<Tile> _tiles = [];
    readonly HashSet<string> _released = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How long an empty slot is kept for its game to come back. A held tile is kept
    /// however long it takes.</summary>
    public TimeSpan Grace { get; init; } = TimeSpan.FromMinutes(10);

    public IReadOnlyList<Tile> Tiles => _tiles;

    /// <summary>The instance whose tile fills the deck, or null when everything is in the grid.</summary>
    public string? Maximized { get; private set; }

    public Tile? Find(string instance) => _tiles.FirstOrDefault(t => string.Equals(t.Instance, instance, StringComparison.OrdinalIgnoreCase));

    /// <summary>Brings the tiles up to date with the games that are running now.</summary>
    public TileChanges Observe(IReadOnlyList<GameCopy> copies, IReadOnlyDictionary<string, string> labels, DateTime now)
    {
        var show = new List<Tile>();
        var drop = new List<Tile>();

        foreach (var copy in copies)
        {
            if (_released.Contains(copy.Instance)) continue;   // let go of on purpose: leave it alone
            var tile = Find(copy.Instance);
            if (tile is null)
            {
                tile = new Tile(copy.Instance) { Since = now };
                _tiles.Add(tile);
            }
            if (tile.Pid != copy.Pid)
            {
                // Another game in the same instance: whatever was in the tile is not it any more.
                if (tile.State != TileState.Waiting) drop.Add(tile);
                tile.Stubborn = false;
                tile.Pid = copy.Pid;
                Become(tile, TileState.Waiting, now);
            }
            tile.Label = labels.TryGetValue(tile.Instance, out var label) ? label : null;
            if (tile.State == TileState.Waiting && !tile.Stubborn) show.Add(tile);
        }

        foreach (var tile in _tiles.ToList())
        {
            if (copies.Any(c => string.Equals(c.Instance, tile.Instance, StringComparison.OrdinalIgnoreCase))) continue;
            if (tile.State != TileState.Waiting) drop.Add(tile);   // the game went, and took its window
            tile.Pid = 0;
            tile.Stubborn = false;
            Become(tile, TileState.Waiting, now);
            var held = string.Equals(Maximized, tile.Instance, StringComparison.OrdinalIgnoreCase);
            if (!held && now - tile.Since > Grace) _tiles.Remove(tile);
        }

        return new TileChanges(show, drop);
    }

    static void Become(Tile tile, TileState state, DateTime now)
    {
        if (tile.State == state) return;
        tile.State = state;
        tile.Since = now;
    }

    /// <summary>The backing has the game in the tile.</summary>
    public void Shown(Tile tile, DateTime now) => Become(tile, TileState.Shown, now);

    /// <summary>The backing cannot show this game; it keeps its own window and is not asked again.</summary>
    public void Stubborn(Tile tile, DateTime now)
    {
        tile.Stubborn = true;
        Become(tile, TileState.Loose, now);
    }

    /// <summary>Hold one tile open over the whole deck; an unknown instance gets a waiting slot, so
    /// a game that is still starting is already held. Asking for a tile takes back a copy that was
    /// released earlier.</summary>
    public void Maximize(string instance)
    {
        _released.Remove(instance);
        if (Find(instance) is null) _tiles.Add(new Tile(instance));
        Maximized = Find(instance)!.Instance;
    }

    /// <summary>Everything back in the grid.</summary>
    public void ShowGrid() => Maximized = null;

    /// <summary>Takes a tile out of the deck. The copy is then left alone - a deck that took it
    /// again on its next tick would make this useless - until a tile is asked for it again
    /// (<see cref="Maximize"/>). The tile is returned so the backing can let its content go.</summary>
    public Tile? Release(string instance, bool keepAway = true)
    {
        if (keepAway) _released.Add(instance);
        var tile = Find(instance);
        if (tile is null) return null;
        if (string.Equals(Maximized, tile.Instance, StringComparison.OrdinalIgnoreCase)) Maximized = null;
        _tiles.Remove(tile);
        return tile;
    }

    /// <summary>Empties the deck, for letting everything go at once.</summary>
    public IReadOnlyList<Tile> Clear()
    {
        var all = _tiles.ToList();
        _tiles.Clear();
        Maximized = null;
        return all;
    }

    /// <summary>Where each tile goes in a client area of that size, in the order of the tiles.
    /// With one tile held open it fills the deck and the others sit behind it - the same size,
    /// still rendering, simply covered.</summary>
    public IReadOnlyList<TilePlace> Layout(int width, int height)
    {
        if (Maximized is { } held && Find(held) is { } one)
        {
            return [.. _tiles.Select(t => new TilePlace(t,
                t == one ? Grid.Whole(width, height) : Grid.Behind(width, height), t == one))];
        }
        var boxes = Grid.Lay(_tiles.Count, width, height);
        return [.. _tiles.Select((t, i) => new TilePlace(t, boxes[i], true))];
    }

    /// <summary>The tiles as the rest of GateKit reads them (`gatekit window list`). While one is
    /// held open it is "held" and the ones it covers are "behind" - both are shown, one is in view.</summary>
    public IReadOnlyList<DeckTile> Records()
    {
        var columns = Grid.Columns(_tiles.Count);
        return [.. _tiles.Select((t, i) => t.Record(i % columns, i / columns, Held(t)))];
    }

    string? Held(Tile tile)
    {
        if (Maximized is null || tile.State != TileState.Shown) return null;
        return string.Equals(Maximized, tile.Instance, StringComparison.OrdinalIgnoreCase) ? "held" : "behind";
    }
}
