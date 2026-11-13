using System.Runtime.Versioning;

namespace GateKit.Platform.Windows;

/// <summary>The console window Windows may have made for this program, and getting rid of it.</summary>
[SupportedOSPlatform("windows")]
internal static unsafe class ConsoleWindow
{
    /// <summary>Hides it when it belongs to this program alone. See RunCopy.HideOwnConsoleWindow.</summary>
    public static void HideIfOurs()
    {
        var window = Native.GetConsoleWindow();
        if (window == 0) return;
        uint one;
        if (Native.GetConsoleProcessList(&one, 1) != 1) return;   // somebody's terminal: theirs
        Native.ShowWindow(window, Native.SwHideCmd);
    }
}
