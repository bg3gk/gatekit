using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace GateKit.Deck.Windows;

/// <summary>The Win32 the deck's own window needs: a window class, a message loop, and enough GDI
/// to draw a caption over each tile. Everything about the game windows themselves is the
/// platform's (IGamePlatform.AdoptWindow and friends).</summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class Native
{
    public const uint CsVRedraw = 0x0001, CsHRedraw = 0x0002;
    public const uint WsOverlappedWindow = 0x00CF0000, WsClipChildren = 0x02000000, WsVisible = 0x10000000,
        WsChild = 0x40000000;
    public const int CwUseDefault = unchecked((int)0x80000000);

    public const uint WmDestroy = 0x0002, WmSize = 0x0005, WmPaint = 0x000F, WmClose = 0x0010,
        WmEraseBackground = 0x0014, WmKeyDown = 0x0100, WmKeyUp = 0x0101, WmChar = 0x0102,
        WmSysKeyDown = 0x0104, WmSysKeyUp = 0x0105, WmTimer = 0x0113, WmLButtonDown = 0x0201,
        WmParentNotify = 0x0210, WmEndSession = 0x0016, WmUser = 0x0400;

    /// <summary>What the deck posts to itself to close from another thread (Ctrl+C).</summary>
    public const uint WmDeckQuit = WmUser + 1;

    /// <summary>A window that is never activated by appearing, and is in the taskbar all the same.</summary>
    public const uint WsExNoActivate = 0x08000000, WsExAppWindow = 0x00040000, WsExToolWindow = 0x00000080;
    public const int GwlExStyle = -20;

    public const int SwShowNoActivate = 4, SwHide = 0;

    /// <summary>SetWindowPos: never activate, and the pieces of it the deck leaves alone.</summary>
    public const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoZOrder = 0x0004,
        SwpNoActivate = 0x0010, SwpNoOwnerZOrder = 0x0200;

    public static readonly nint HwndTop = 0, HwndBottom = 1;
    public const uint IdcArrow = 32512;
    public const int DefaultGuiFont = 17;
    public const uint TransparentBackground = 1;
    public const uint DtSingleLine = 0x20, DtVCenter = 0x04, DtCenter = 0x01, DtEndEllipsis = 0x8000, DtLeft = 0x00;
    public static readonly nint DpiAwarenessPerMonitorV2 = -4;

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct WindowClass
    {
        public uint Size, Style;
        public nint WindowProcedure;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background, MenuName, ClassName, SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Message
    {
        public nint Window;
        public uint Value;
        public nint WParam, LParam;
        public uint Time;
        public Point Where;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PaintStruct
    {
        public nint Dc;
        public int Erase;
        public Rect Paint;
        public int Restore, IncUpdate;
        public fixed byte Reserved[32];
    }

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    public static partial ushort RegisterClassEx(WindowClass* windowClass);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateWindowEx(uint exStyle, string className, string? title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    public static partial nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    public static partial int GetMessage(Message* message, nint window, uint first, uint last);

    [LibraryImport("user32.dll")]
    public static partial int TranslateMessage(Message* message);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    public static partial nint DispatchMessage(Message* message);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    public static partial int PostMessage(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    public static partial void PostQuitMessage(int exitCode);

    [LibraryImport("user32.dll")]
    public static partial int DestroyWindow(nint window);

    [LibraryImport("user32.dll")]
    public static partial int ShowWindow(nint window, int command);

    [LibraryImport("user32.dll")]
    public static partial nint SetTimer(nint window, nint id, uint milliseconds, nint callback);

    [LibraryImport("user32.dll")]
    public static partial int KillTimer(nint window, nint id);

    [LibraryImport("user32.dll")]
    public static partial int GetClientRect(nint window, out Rect rect);

    [LibraryImport("user32.dll")]
    public static partial int GetWindowRect(nint window, out Rect rect);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial int SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    public static partial int InvalidateRect(nint window, nint rect, int erase);

    [LibraryImport("user32.dll", EntryPoint = "LoadCursorW")]
    public static partial nint LoadCursor(nint instance, nint cursor);

    public const uint ImageIcon = 1, LrLoadFromFile = 0x0010, LrDefaultSize = 0x0040, LrShared = 0x8000;

    [LibraryImport("user32.dll", EntryPoint = "LoadImageW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint LoadImage(nint instance, string name, uint type, int width, int height, uint load);

    [LibraryImport("user32.dll")]
    public static partial nint BeginPaint(nint window, PaintStruct* paint);

    [LibraryImport("user32.dll")]
    public static partial int EndPaint(nint window, PaintStruct* paint);

    [LibraryImport("user32.dll")]
    public static partial int FillRect(nint dc, Rect* rect, nint brush);

    [LibraryImport("user32.dll", EntryPoint = "DrawTextW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int DrawText(nint dc, string text, int length, Rect* rect, uint format);

    [LibraryImport("user32.dll")]
    public static partial nint SetFocus(nint window);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    public static partial nint GetWindowLongPtr(nint window, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static partial nint SetWindowLongPtr(nint window, int index, nint value);

    [LibraryImport("user32.dll")]
    public static partial int SetForegroundWindow(nint window);

    [LibraryImport("user32.dll")]
    public static partial int IsWindow(nint window);

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    public static partial nint GetFocus();

    [LibraryImport("user32.dll")]
    public static partial int AttachThreadInput(uint attach, uint to, int join);

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll")]
    public static partial nint SetThreadDpiAwarenessContext(nint context);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SetWindowText(nint window, string text);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint GetModuleHandle(string? name);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateSolidBrush(uint color);

    [LibraryImport("gdi32.dll")]
    public static partial int DeleteObject(nint handle);

    [LibraryImport("gdi32.dll")]
    public static partial nint GetStockObject(int index);

    [LibraryImport("gdi32.dll")]
    public static partial nint SelectObject(nint dc, nint handle);

    [LibraryImport("gdi32.dll")]
    public static partial uint SetTextColor(nint dc, uint color);

    [LibraryImport("gdi32.dll")]
    public static partial uint SetBkMode(nint dc, uint mode);

    /// <summary>A colour as GDI wants it: blue in the high byte.</summary>
    public static uint Colour(byte red, byte green, byte blue) => (uint)(red | (green << 8) | (blue << 16));

    // --- the console this program may have been given ---

    /// <summary>How many processes share this console. One is us alone, which means the console
    /// was made for this program and goes when it goes - a window nobody asked for.</summary>
    [LibraryImport("kernel32.dll")]
    public static partial uint GetConsoleProcessList(uint* list, uint count);

    [LibraryImport("kernel32.dll")]
    public static partial int FreeConsole();
}
