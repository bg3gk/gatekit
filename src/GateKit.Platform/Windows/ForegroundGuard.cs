using System.Runtime.Versioning;

namespace GateKit.Platform.Windows;

/// <summary>
/// The net under the thing that actually stops focus theft.
///
/// What stops it is the game's own toolkit: GateKit starts every copy with
/// <c>SDL_WINDOW_NO_ACTIVATION_WHEN_SHOWN=1</c> in its environment, and SDL then shows the window
/// with <c>SW_SHOWNA</c> - "the window that is currently active remains active" - instead of
/// <c>SW_SHOW</c>. Nothing is hooked and nothing is patched; SDL documents the hint and gives the
/// environment override priority over anything the game sets for itself.
///
/// This class is what notices if that ever stops being true - a patch to the game, a different
/// SDL, a copy started another way. It watches for a window of <em>this copy</em> taking the
/// foreground, puts the foreground back, and <b>says so</b>. A line in the output is the point:
/// silence here used to mean "we fought it off again", and now it means "it did not happen".
///
/// It is told, not asked: a window event hook arrives as the change happens. The 120 ms poll it
/// began as meant the game really did come to the front and hold the keyboard for a tenth of a
/// second before being put back.
///
/// It gives way to the person at once: the moment the foreground goes to a window that is not
/// this copy's, that is them changing windows on purpose, and nothing of ours may fight that.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ForegroundGuard : IDisposable
{
    readonly nint _keep;
    readonly string _executable;
    readonly Func<IReadOnlyList<GameProcess>> _processes;
    readonly TextWriter? _say;
    readonly Thread _thread;
    // Held for as long as the hook is registered: the system calls it from native code, and a
    // delegate that only the hook refers to is collected while the hook still points at it.
    readonly Native.WinEventProcedure _callback;
    uint _loop;
    volatile bool _done;

    /// <summary>How many times it had to put the foreground back. Should be nought.</summary>
    public int Caught { get; private set; }

    public ForegroundGuard(string executable, Func<IReadOnlyList<GameProcess>> processes, TextWriter? say = null)
    {
        _executable = executable;
        _processes = processes;
        _say = say;
        _keep = Native.GetForegroundWindow();
        _callback = OnEvent;
        _thread = new Thread(Watch) { IsBackground = true, Name = "gatekit-foreground" };
        _thread.Start();
    }

    void Watch()
    {
        _loop = Native.GetCurrentThreadId();
        // Both events: a copy that takes the foreground, and one whose window is merely shown -
        // a window can appear in front without the foreground changing, and that flash is seen too.
        var foreground = Native.SetWinEventHook(Native.EventSystemForeground, Native.EventSystemForeground,
            0, _callback, 0, 0, Native.WinEventOutOfContext | Native.WinEventSkipOwnProcess);
        var shown = Native.SetWinEventHook(Native.EventObjectShow, Native.EventObjectShow,
            0, _callback, 0, 0, Native.WinEventOutOfContext | Native.WinEventSkipOwnProcess);
        try
        {
            while (!_done && Native.GetMessage(out var message, 0, 0, 0) > 0)
            {
                if (message.Id == Native.WmQuit) break;
            }
        }
        finally
        {
            if (shown != 0) Native.UnhookWinEvent(shown);
            if (foreground != 0) Native.UnhookWinEvent(foreground);
        }
    }

    /// <summary>The copies of this executable running now. Only their windows are ever touched.</summary>
    IReadOnlyCollection<int> Ours()
    {
        try
        {
            return [.. _processes()
                .Where(p => string.Equals(p.ExecutablePath, _executable, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Pid)];
        }
        catch (Exception e) when (e is IOException or InvalidOperationException) { return []; }
    }

    void OnEvent(nint hook, uint eventId, nint window, int objectId, int childId, uint thread, uint time)
    {
        if (_done || window == 0 || objectId != Native.ObjIdWindow) return;
        if (eventId == Native.EventObjectShow)
        {
            // Only while it is the window in front: a copy drawing its windows behind somebody
            // else's work is not something to interfere with.
            if (Native.GetForegroundWindow() != window) return;
        }
        else if (window == _keep)
        {
            return;
        }
        if (!GameWindow.IsOurs(window, Ours()))
        {
            // Not ours. On a foreground change that means the person went somewhere else on
            // purpose, and the watch is over.
            if (eventId == Native.EventSystemForeground) Stop();
            return;
        }
        Caught++;
        _say?.WriteLine($"a copy took the foreground as it started, and it was put back (this should not happen any "
            + $"more: the game is told not to activate its window). Times so far: {Caught}.");
        Native.SetWindowPos(window, Native.HwndBottom, 0, 0, 0, 0,
            Native.SwpNoMove | Native.SwpNoSize | Native.SwpNoActivate);
        if (_keep != 0 && Native.IsWindow(_keep) != 0) Restore(_keep);
    }

    void Stop()
    {
        if (_done) return;
        _done = true;
        if (_loop != 0) Native.PostThreadMessage(_loop, Native.WmQuit, 0, 0);
    }

    /// <summary>SetForegroundWindow only obeys a process that already has the foreground, so this
    /// borrows the input queue of the window that took it.</summary>
    static void Restore(nint window)
    {
        var mine = Native.GetCurrentThreadId();
        var theirs = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out _);
        var attached = theirs != 0 && theirs != mine && Native.AttachThreadInput(mine, theirs, 1) != 0;
        try { Native.SetForegroundWindow(window); }
        finally { if (attached) Native.AttachThreadInput(mine, theirs, 0); }
    }

    public void Dispose()
    {
        Stop();
        _thread.Join(TimeSpan.FromSeconds(1));
    }
}
