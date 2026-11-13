using System.Runtime.Versioning;
using GateKit.Stand;

namespace GateKit.Deck.Windows;

/// <summary>
/// What was done to a mirrored copy's own window, and how to put it back.
///
/// Mirroring is the one thing in GateKit that makes a game's window unnecessary: the frame comes
/// off the graphics card, so the window need not be looked at. It still has to exist - a swap chain
/// hangs on one, and taking that away would mean fighting the engine rather than reading it - but
/// it can be anywhere, including nowhere anybody can see.
///
/// Two ways, because they are not the same thing to a game:
///
/// - <see cref="DeckConceal.Offscreen"/> moves it past the right edge of every screen and makes it
///   a tool window, which is what keeps it out of the taskbar. As far as the game is told it is a
///   shown window of the usual size, so nothing about its rendering changes - and GateKit's own
///   window paths (`shot`'s fallback, `key`'s fallback, `window list`) still find it, because they
///   look for a *visible* window.
/// - <see cref="DeckConceal.Hidden"/> hides it outright. Whether a game keeps drawing then is a
///   question about the engine, not about Windows, and the answer is in docs/design/deck.md.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class Concealment
{
    /// <summary>Where a window goes to be on no screen. Far to the right of any desktop, and not
    /// the -32000 that Windows uses to park a minimized window.</summary>
    const int FarAway = 30000;

    readonly nint _window;
    readonly DeckConceal _how;
    readonly nint _formerExStyle;
    readonly (int X, int Y, int Width, int Height) _formerPlace;

    Concealment(nint window, DeckConceal how, nint formerExStyle, (int, int, int, int) formerPlace)
    {
        _window = window;
        _how = how;
        _formerExStyle = formerExStyle;
        _formerPlace = formerPlace;
    }

    public static Concealment? Apply(nint window, DeckConceal how)
    {
        if (window == 0 || how == DeckConceal.None) return null;
        var exStyle = Native.GetWindowLongPtr(window, Native.GwlExStyle);
        Native.GetWindowRect(window, out var rect);
        var place = (rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        var concealment = new Concealment(window, how, exStyle, place);

        if (how == DeckConceal.Hidden)
        {
            Native.ShowWindow(window, Native.SwHide);
            return concealment;
        }

        // A window's place in the taskbar is only read when it is shown, so the styles are changed
        // while it is hidden and it is shown again without ever being activated.
        Native.ShowWindow(window, Native.SwHide);
        var wanted = (nint)((((ulong)exStyle) & ~(ulong)Native.WsExAppWindow) | Native.WsExToolWindow);
        Native.SetWindowLongPtr(window, Native.GwlExStyle, wanted);
        Native.SetWindowPos(window, 0, FarAway, FarAway, 0, 0,
            Native.SwpNoActivate | Native.SwpNoSize | Native.SwpNoZOrder | Native.SwpNoOwnerZOrder);
        Native.ShowWindow(window, Native.SwShowNoActivate);
        return concealment;
    }

    /// <summary>The window back where it was, with the frame it had, and never activated on the way.</summary>
    public void Undo()
    {
        if (_how == DeckConceal.Hidden)
        {
            Native.ShowWindow(_window, Native.SwShowNoActivate);
            return;
        }
        Native.ShowWindow(_window, Native.SwHide);
        Native.SetWindowLongPtr(_window, Native.GwlExStyle, _formerExStyle);
        Native.SetWindowPos(_window, 0, _formerPlace.X, _formerPlace.Y, _formerPlace.Width, _formerPlace.Height,
            Native.SwpNoActivate | Native.SwpNoZOrder | Native.SwpNoOwnerZOrder);
        Native.ShowWindow(_window, Native.SwShowNoActivate);
    }
}
