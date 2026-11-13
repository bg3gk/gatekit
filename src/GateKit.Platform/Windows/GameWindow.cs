using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace GateKit.Platform.Windows;

/// <summary>Finds the game's own window of a process: class SDL_app, visible - never the Script
/// Extender console the same process opens. Falls back to the title "Baldur's Gate 3 (".</summary>
[SupportedOSPlatform("windows")]
internal static unsafe class GameWindow
{
    const string WindowClass = "SDL_app";
    const string TitlePrefix = "Baldur's Gate 3 (";

    sealed class Search(uint pid)
    {
        public readonly uint Pid = pid;
        public nint ByClass;
        public nint ByTitle;
    }

    public static nint? Find(int pid)
    {
        var search = new Search((uint)pid);
        var handle = GCHandle.Alloc(search);
        try
        {
            Native.EnumWindows(&Visit, GCHandle.ToIntPtr(handle));
            // A game held in a deck is a child window, which EnumWindows does not reach: its
            // windows are the top-level ones. Reading and driving the game must not care where
            // its window hangs, so the children of each window are searched as well.
            if (search.ByClass == 0) Native.EnumWindows(&VisitHost, GCHandle.ToIntPtr(handle));
        }
        finally { handle.Free(); }
        var found = search.ByClass != 0 ? search.ByClass : search.ByTitle;
        return found != 0 ? found : null;
    }

    /// <summary>Whether this window is a game's own window (its class), whichever process owns it.</summary>
    public static bool IsGame(nint window)
    {
        var buffer = stackalloc char[256];
        var length = Native.GetClassName(window, buffer, 256);
        return new string(buffer, 0, length) == WindowClass;
    }

    /// <summary>
    /// Whether this window belongs to one of <paramref name="pids"/> - a copy GateKit started.
    ///
    /// The class alone is not enough to act on. `SDL_app` is SDL's own window class, shared by
    /// every SDL program on the machine, so a guard that pushed back "a game window" would have
    /// pushed back somebody's unrelated SDL application the moment it opened. Reading a window is
    /// one thing; moving it is another, and nothing of anyone else's is moved.
    /// </summary>
    public static bool IsOurs(nint window, IReadOnlyCollection<int> pids)
    {
        if (pids.Count == 0 || !IsGame(window)) return false;
        Native.GetWindowThreadProcessId(window, out var owner);
        return pids.Contains((int)owner);
    }

    /// <summary>Looks through one top-level window's children for the game's window.</summary>
    [UnmanagedCallersOnly]
    static int VisitHost(nint window, nint param)
    {
        var search = (Search)GCHandle.FromIntPtr(param).Target!;
        if (search.ByClass != 0) return 0;
        Native.EnumChildWindows(window, &Visit, param);
        return search.ByClass != 0 ? 0 : 1;
    }

    [UnmanagedCallersOnly]
    static int Visit(nint window, nint param)
    {
        var search = (Search)GCHandle.FromIntPtr(param).Target!;
        Native.GetWindowThreadProcessId(window, out var owner);
        if (owner != search.Pid || Native.IsWindowVisible(window) == 0) return 1;
        var buffer = stackalloc char[256];
        var length = Native.GetClassName(window, buffer, 256);
        if (new string(buffer, 0, length) == WindowClass)
        {
            search.ByClass = window;
            return 0;
        }
        length = Native.GetWindowText(window, buffer, 256);
        if (search.ByTitle == 0 && new string(buffer, 0, length).StartsWith(TitlePrefix, StringComparison.Ordinal))
            search.ByTitle = window;
        return 1;
    }
}
