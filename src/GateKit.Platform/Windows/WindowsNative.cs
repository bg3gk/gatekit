using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace GateKit.Platform.Windows;

/// <summary>The Win32 functions the Windows platform calls.</summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class Native
{
    // --- processes ---
    public const uint ProcessQueryLimitedInformation = 0x1000;
    public const uint DetachedProcess = 0x00000008;
    public const uint CreateNewProcessGroup = 0x00000200;
    public const uint CreateUnicodeEnvironment = 0x00000400;
    public const int StartfUseShowWindow = 0x00000001;
    public const short SwShowNoActivate = 4;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint OpenProcess(uint access, int inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    public static partial int QueryFullProcessImageName(nint process, uint flags, char* name, ref uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial int CloseHandle(nint handle);

    [StructLayout(LayoutKind.Sequential)]
    public struct StartupInfo
    {
        public int cb;
        public nint lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessInformation
    {
        public nint hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int CreateProcess(string? applicationName, char* commandLine, nint processAttributes, nint threadAttributes,
        int inheritHandles, uint creationFlags, char* environment, string? currentDirectory, StartupInfo* startupInfo, ProcessInformation* processInformation);

    // --- files ---
    public const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;
    public const uint FileShareRead = 1, FileShareWrite = 2;
    public const uint OpenExisting = 3;
    public const uint FileFlagBackupSemantics = 0x02000000, FileFlagOpenReparsePoint = 0x00200000;
    public const uint FsctlSetReparsePoint = 0x000900A4;
    public const uint ReparseTagMountPoint = 0xA0000003;
    public static readonly nint InvalidHandle = -1;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial int DeviceIoControl(nint device, uint code, void* input, uint inputSize, void* output, uint outputSize, out uint returned, nint overlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int CreateHardLink(string newLink, string existingFile, nint security);

    // --- console ---
    [StructLayout(LayoutKind.Sequential)]
    public struct ConsoleScreenBufferInfo
    {
        public short SizeX, SizeY, CursorX, CursorY;
        public ushort Attributes;
        public short Left, Top, Right, Bottom, MaxX, MaxY;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial int FreeConsole();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial int AttachConsole(uint processId);

    [LibraryImport("kernel32.dll")]
    public static partial nint GetConsoleWindow();

    /// <summary>How many processes share this console. One is us alone: the console was made for
    /// this program and is nobody else's.</summary>
    [LibraryImport("kernel32.dll")]
    public static partial uint GetConsoleProcessList(uint* list, uint count);

    public const int SwHideCmd = 0;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial int GetConsoleScreenBufferInfo(nint console, out ConsoleScreenBufferInfo info);

    /// <summary>The COORD is passed by value: X in the low 16 bits, Y in the high ones.</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "ReadConsoleOutputCharacterW", SetLastError = true)]
    public static partial int ReadConsoleOutputCharacter(nint console, char* buffer, uint length, uint coord, out uint read);

    // --- windows ---
    public const uint WmKeyDown = 0x0100, WmKeyUp = 0x0101;
    public const uint PwRenderFullContent = 2;
    public static readonly nint DpiAwarenessPerMonitorV2 = -4;

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }

    [LibraryImport("user32.dll")]
    public static partial int EnumWindows(delegate* unmanaged<nint, nint, int> callback, nint param);

    [LibraryImport("user32.dll")]
    public static partial int EnumChildWindows(nint parent, delegate* unmanaged<nint, nint, int> callback, nint param);

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll")]
    public static partial int IsWindowVisible(nint window);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")]
    public static partial int GetClassName(nint window, char* buffer, int max);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")]
    public static partial int GetWindowText(nint window, char* buffer, int max);

    [LibraryImport("user32.dll")]
    public static partial int GetWindowRect(nint window, out Rect rect);

    [LibraryImport("user32.dll")]
    public static partial int PrintWindow(nint window, nint dc, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    public static partial int PostMessage(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "MapVirtualKeyW")]
    public static partial uint MapVirtualKey(uint code, uint mapType);

    [LibraryImport("user32.dll")]
    public static partial nint SetThreadDpiAwarenessContext(nint context);

    // --- windows: moving them about, and keeping the foreground where it is ---
    public const int GwlStyle = -16;
    public const uint WsChild = 0x40000000, WsPopup = 0x80000000, WsCaption = 0x00C00000, WsThickFrame = 0x00040000,
        WsSysMenu = 0x00080000, WsMinimizeBox = 0x00020000, WsMaximizeBox = 0x00010000, WsBorder = 0x00800000, WsDlgFrame = 0x00400000;
    public const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010,
        SwpFrameChanged = 0x0020, SwpShowWindow = 0x0040, SwpNoOwnerZOrder = 0x0200;
    public static readonly nint HwndTop = 0, HwndBottom = 1;
    public const int SwShowNoActivateCmd = 4, SwRestore = 9;

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint SetParent(nint child, nint newParent);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    public static partial nint GetWindowLongPtr(nint window, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static partial nint SetWindowLongPtr(nint window, int index, nint value);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial int SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    public static partial int ShowWindow(nint window, int command);

    [LibraryImport("user32.dll")]
    public static partial int IsWindow(nint window);

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    public static partial int SetForegroundWindow(nint window);

    [LibraryImport("user32.dll")]
    public static partial int AttachThreadInput(uint attach, uint to, int join);

    // --- being told about a window instead of asking every so often ---
    public const uint EventSystemForeground = 0x0003, EventObjectShow = 0x8002, EventObjectCreate = 0x8000;
    public const uint WinEventOutOfContext = 0x0000, WinEventSkipOwnProcess = 0x0002;
    public const int ObjIdWindow = 0;

    /// <summary>The callback the system makes on a window event. Out of context, so it arrives on
    /// the registering thread's message loop rather than inside the other process.</summary>
    public delegate void WinEventProcedure(nint hook, uint eventId, nint window, int objectId, int childId,
        uint thread, uint time);

    [LibraryImport("user32.dll")]
    public static partial nint SetWinEventHook(uint from, uint to, nint module,
        [MarshalAs(UnmanagedType.FunctionPtr)] WinEventProcedure callback, uint process, uint thread, uint flags);

    [LibraryImport("user32.dll")]
    public static partial int UnhookWinEvent(nint hook);

    [StructLayout(LayoutKind.Sequential)]
    public struct Message
    {
        public nint Window;
        public uint Id;
        public nint WParam, LParam;
        public uint Time;
        public int X, Y;
    }

    public const uint WmQuit = 0x0012;

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    public static partial int GetMessage(out Message message, nint window, uint first, uint last);

    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW")]
    public static partial int PostThreadMessage(uint thread, uint message, nint wParam, nint lParam);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")]
    public static partial nint GetDC(nint window);

    [LibraryImport("user32.dll")]
    public static partial int ReleaseDC(nint window, nint dc);

    // --- gdi ---
    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfo
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant;
        public uint Colors; // one RGBQUAD of the colour table: unused at 32 bits, but part of BITMAPINFO
    }

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateCompatibleDC(nint dc);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateCompatibleBitmap(nint dc, int width, int height);

    [LibraryImport("gdi32.dll")]
    public static partial nint SelectObject(nint dc, nint gdiObject);

    [LibraryImport("gdi32.dll")]
    public static partial int DeleteObject(nint gdiObject);

    [LibraryImport("gdi32.dll")]
    public static partial int DeleteDC(nint dc);

    [LibraryImport("gdi32.dll")]
    public static partial int GetDIBits(nint dc, nint bitmap, uint start, uint lines, void* bits, BitmapInfo* info, uint usage);
}
