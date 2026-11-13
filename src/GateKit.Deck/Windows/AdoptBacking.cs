using System.Runtime.Versioning;
using GateKit.Platform;

namespace GateKit.Deck.Windows;

/// <summary>
/// The backing that takes the game's own window into the tile: SetParent, the frame styles off,
/// and every move made with SWP_NOACTIVATE. The game keeps rendering and keeps the keyboard it is
/// given; it is never activated, hidden or told it was clicked.
///
/// Everything window-shaped about the deck is here or behind <see cref="IGamePlatform"/>; the
/// grid, the tiles and the commands never see a window handle.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AdoptBacking(IGamePlatform platform) : ITileBacking
{
    readonly Dictionary<string, WindowAdoption> _held = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Where each tile's window was last actually put. The deck places every tile on
    /// every tick, and a window told to go where it already is still redraws, which is seen.</summary>
    readonly Dictionary<string, (int X, int Y, int Width, int Height, bool Front)> _placed = new(StringComparer.OrdinalIgnoreCase);

    public string Name => "adopt";

    public TileShow Show(Tile tile, nint host, out string? why)
    {
        why = null;
        if (_held.ContainsKey(tile.Instance)) return TileShow.Shown;
        var window = tile.Pid > 0 ? platform.FindGameWindow(tile.Pid) : 0;
        if (window == 0) return TileShow.NotYet;     // the game has not opened its window yet
        try
        {
            _held[tile.Instance] = platform.AdoptWindow(window, host);
            tile.Window = window;
            return TileShow.Shown;
        }
        catch (Exception e) when (e is IOException or PlatformCapabilityException)
        {
            why = e.Message;
            return TileShow.Refused;
        }
    }

    public void Place(Tile tile, TileBox box, bool front)
    {
        if (!_held.ContainsKey(tile.Instance)) return;
        var inside = box.Inside;
        var where = (inside.X, inside.Y, inside.Width, inside.Height, front);
        if (_placed.TryGetValue(tile.Instance, out var last) && last == where) return;
        _placed[tile.Instance] = where;
        platform.PlaceWindow(tile.Window, inside.X, inside.Y, inside.Width, inside.Height, front);
    }

    public void Drop(Tile tile)
    {
        _placed.Remove(tile.Instance);
        if (!_held.Remove(tile.Instance, out var adoption)) return;
        try { platform.ReleaseWindow(adoption); }
        catch (Exception e) when (e is IOException or PlatformCapabilityException) { }
        tile.Window = 0;
    }

    public void Focus(Tile tile)
    {
        if (tile.Window != 0) TakeFocus(tile.Window);
    }

    public void Key(Tile tile, uint message, nint wParam, nint lParam)
    {
        if (tile.Window != 0) Native.PostMessage(tile.Window, message, wParam, lParam);
    }

    /// <summary>Nothing: the game's own window draws the tile.</summary>
    public void Draw(nint deviceContext, Tile tile, TileBox box) { }

    /// <summary>Gives the keyboard to a window of another process: our input queue is attached to
    /// its thread's for the moment it takes to say so.</summary>
    static void TakeFocus(nint child)
    {
        var mine = Native.GetCurrentThreadId();
        var theirs = Native.GetWindowThreadProcessId(child, out _);
        var attached = theirs != 0 && theirs != mine && Native.AttachThreadInput(mine, theirs, 1) != 0;
        try { Native.SetFocus(child); }
        finally { if (attached) Native.AttachThreadInput(mine, theirs, 0); }
    }
}
