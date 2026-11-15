using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace GateKit.Platform.Windows;

/// <summary>
/// The Script Extender console a copy may open on the desktop although `gatekit` writes
/// <c>CreateConsole=false</c>: the extender (and its updater, DWrite.dll) turns the console on
/// whenever Shift is held as the game starts - somebody typing a capital letter elsewhere is
/// enough. Such a window is <b>hidden</b>, never closed: closing a console ends the process it
/// belongs to, and that is the game.
///
/// Only a classic console window (<c>ConsoleWindowClass</c>) whose process is one of this copy's is
/// touched. A console hosted by Windows Terminal is a tab in somebody's terminal window, which
/// belongs to that terminal's process and may hold their own work; it is left alone, and the
/// extender's own setting is what keeps it from opening.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe class GameConsole
{
    const string ConsoleClass = "ConsoleWindowClass";

    /// <summary>Whether <paramref name="window"/> is the console of one of <paramref name="pids"/>.</summary>
    public static bool IsOurs(nint window, IReadOnlyCollection<int> pids)
    {
        if (pids.Count == 0) return false;
        var buffer = stackalloc char[64];
        var length = Native.GetClassName(window, buffer, 64);
        if (new string(buffer, 0, length) != ConsoleClass) return false;
        // For a console window this answers the process the console is for, not its host.
        Native.GetWindowThreadProcessId(window, out var owner);
        return pids.Contains((int)owner);
    }

    public static void Hide(nint window) => Native.ShowWindow(window, Native.SwHideCmd);

    sealed class Sweep(IReadOnlyCollection<int> pids)
    {
        public readonly IReadOnlyCollection<int> Pids = pids;
        public int Hidden;
    }

    /// <summary>Hides every visible console of <paramref name="pids"/>; how many there were.</summary>
    public static int HideAll(IReadOnlyCollection<int> pids)
    {
        if (pids.Count == 0) return 0;
        var sweep = new Sweep(pids);
        var handle = GCHandle.Alloc(sweep);
        try { Native.EnumWindows(&Visit, GCHandle.ToIntPtr(handle)); }
        finally { handle.Free(); }
        return sweep.Hidden;
    }

    [UnmanagedCallersOnly]
    static int Visit(nint window, nint param)
    {
        var sweep = (Sweep)GCHandle.FromIntPtr(param).Target!;
        if (Native.IsWindowVisible(window) != 0 && IsOurs(window, sweep.Pids))
        {
            Hide(window);
            sweep.Hidden++;
        }
        return 1;
    }
}
