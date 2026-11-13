using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace GateKit.Platform.Windows;

/// <summary>
/// Adopting a window into another program's window: SetParent plus a style change, which is how
/// one window comes to hold several games. The game is never activated, never asked anything and
/// never told it was clicked; as far as it knows its window moved.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowHost
{
    /// <summary>The frame a top-level window has and a tile must not: it would be drawn inside the tile.</summary>
    const uint Frame = Native.WsPopup | Native.WsCaption | Native.WsThickFrame | Native.WsSysMenu
        | Native.WsMinimizeBox | Native.WsMaximizeBox | Native.WsBorder | Native.WsDlgFrame;

    public static WindowAdoption Adopt(nint window, nint host)
    {
        if (Native.IsWindow(window) == 0) throw new IOException("the window is gone");
        var style = Native.GetWindowLongPtr(window, Native.GwlStyle);
        Native.GetWindowRect(window, out var rect);
        var place = (rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        var former = Native.SetParent(window, host);
        if (former == 0 && Marshal.GetLastPInvokeError() != 0)
            throw new IOException($"SetParent failed: Win32 error {Marshal.GetLastPInvokeError()}");
        Native.SetWindowLongPtr(window, Native.GwlStyle, (nint)((((ulong)style) & ~Frame) | Native.WsChild));
        Native.SetWindowPos(window, 0, 0, 0, place.Item3, place.Item4,
            Native.SwpNoZOrder | Native.SwpNoActivate | Native.SwpFrameChanged | Native.SwpShowWindow);
        return new WindowAdoption(window, former, style, place);
    }

    public static void Release(WindowAdoption adoption)
    {
        var window = adoption.Window;
        if (Native.IsWindow(window) == 0) return;
        Native.SetParent(window, adoption.FormerParent);
        Native.SetWindowLongPtr(window, Native.GwlStyle, adoption.FormerStyle);
        var (x, y, width, height) = adoption.Place;
        Native.SetWindowPos(window, 0, x, y, width, height,
            Native.SwpNoZOrder | Native.SwpNoActivate | Native.SwpFrameChanged | Native.SwpShowWindow);
        Native.ShowWindow(window, Native.SwShowNoActivateCmd);
    }

    public static void Place(nint window, int x, int y, int width, int height, bool front)
    {
        if (Native.IsWindow(window) == 0) return;
        Native.SetWindowPos(window, front ? Native.HwndTop : Native.HwndBottom, x, y, Math.Max(1, width), Math.Max(1, height),
            Native.SwpNoActivate | Native.SwpNoOwnerZOrder);
    }
}
