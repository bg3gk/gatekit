using System.Collections;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using GateKit.Platform.Windows;
using Microsoft.Win32;

namespace GateKit.Platform;

/// <summary>
/// Windows: Steam from the registry, the profile under %LOCALAPPDATA%, instances given their own
/// %USERPROFILE%, NTFS junctions and hard links, and the game window driven through Win32
/// (PrintWindow and posted key messages, so the window never has to come forward).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsPlatform : IGamePlatform
{
    public string Name => "windows";
    public StringComparison PathComparison => StringComparison.OrdinalIgnoreCase;

    // --- where things are ---

    public string? SteamRoot()
    {
        string? path = null;
        try
        {
            path = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
                ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string
                ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath", null) as string;
        }
        catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException) { }
        return path is not null && Directory.Exists(path) ? Path.GetFullPath(path) : null;
    }

    public IReadOnlyList<string> SteamLibraries() => SteamRoot() is { } root ? Steam.Libraries(root) : [];

    static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public string ProfileFolder(string gameFolder) => Path.Combine(LocalAppData, "Larian Studios", "Baldur's Gate 3");

    public string? ScriptExtenderCache(string gameFolder) => Path.Combine(LocalAppData, "BG3ScriptExtender");

    public string ToGamePath(string hostPath) => Path.GetFullPath(hostPath);

    static string Home(string instanceRoot) => Path.Combine(instanceRoot, "home");

    public string InstanceProfileFolder(string instanceRoot) =>
        Path.Combine(Home(instanceRoot), "AppData", "Local", "Larian Studios", "Baldur's Gate 3");

    public void PrepareInstanceHome(string instanceRoot)
    {
        var home = Home(instanceRoot);
        foreach (var folder in new[] { Path.Combine(home, "AppData", "Local"), Path.Combine(home, "AppData", "Roaming"), Path.Combine(home, "Documents") })
            Directory.CreateDirectory(folder);
    }

    // --- processes ---

    public void StartGame(GameLaunch launch)
    {
        var environment = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "", StringComparer.OrdinalIgnoreCase);
        if (launch.InstanceRoot is { } root)
        {
            // The game finds its profile through the known folders, which follow %USERPROFILE%
            // of the process (measured: LOCALAPPDATA alone is not enough).
            var home = Home(root);
            environment["USERPROFILE"] = home;
            environment["LOCALAPPDATA"] = Path.Combine(home, "AppData", "Local");
            environment["APPDATA"] = Path.Combine(home, "AppData", "Roaming");
        }
        // What the stand asked for, last, so it means what it says.
        foreach (var (key, value) in launch.Environment ?? new Dictionary<string, string>()) environment[key] = value;
        var block = new StringBuilder();
        foreach (var (key, value) in environment.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
            block.Append(key).Append('=').Append(value).Append('\0');
        block.Append('\0');
        var commandLine = new StringBuilder(Quote(launch.Executable));
        foreach (var argument in launch.Arguments) commandLine.Append(' ').Append(Quote(argument));
        commandLine.Append('\0');

        // Nothing is said about how the window should be shown, because nothing said here would
        // be heard. STARTUPINFO's wShowWindow reaches a program only through ShowWindow's
        // SW_SHOWDEFAULT, and SDL - which is what makes this game's window - always passes a
        // command of its own. This used to ask for SW_SHOWNOACTIVATE and was quietly ignored for
        // as long as it existed, which is worse than asking for nothing: it read like the window
        // was being handled. What actually keeps the window from taking the keyboard is the SDL
        // hint GateKit puts in the game's environment (Stand.GameEnvironment).
        var startup = new Native.StartupInfo { cb = sizeof(Native.StartupInfo) };
        Native.ProcessInformation info;
        var line = commandLine.ToString().ToCharArray();
        var env = block.ToString().ToCharArray();
        fixed (char* linePointer = line)
        fixed (char* envPointer = env)
        {
            // Detached and in a group of its own, as the old stand did: no console of ours is
            // inherited (Script Extender opens its own), and Ctrl+C here never reaches the game.
            var ok = Native.CreateProcess(launch.Executable, linePointer, 0, 0, 0,
                Native.DetachedProcess | Native.CreateNewProcessGroup | Native.CreateUnicodeEnvironment,
                envPointer, launch.WorkingDirectory, &startup, &info);
            if (ok == 0) throw new IOException($"could not start {launch.Executable}: Win32 error {Marshal.GetLastPInvokeError()}");
        }
        Native.CloseHandle(info.hThread);
        Native.CloseHandle(info.hProcess);
    }

    /// <summary>Quotes one argument by the rules of CommandLineToArgvW.</summary>
    internal static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"', '\n', '\v']) < 0) return argument;
        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') sb.Append('\\', backslashes * 2 + 1);
            else sb.Append('\\', backslashes);
            backslashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    public IReadOnlyList<GameProcess> GameProcesses()
    {
        var found = new List<GameProcess>();
        foreach (var name in GamePlatform.ProcessNames)
        {
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(name)))
            {
                using (process)
                {
                    var path = ImagePath(process.Id);
                    if (path is not null && string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase))
                        found.Add(new GameProcess(process.Id, path));
                }
            }
        }
        return found;
    }

    static string? ImagePath(int pid)
    {
        var handle = Native.OpenProcess(Native.ProcessQueryLimitedInformation, 0, (uint)pid);
        if (handle == 0) return null;
        try
        {
            var buffer = stackalloc char[1024];
            uint size = 1024;
            return Native.QueryFullProcessImageName(handle, 0, buffer, ref size) != 0 ? new string(buffer, 0, (int)size) : null;
        }
        finally { Native.CloseHandle(handle); }
    }

    public void Kill(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill();
        }
        catch (ArgumentException) { }          // already gone
        catch (InvalidOperationException) { }  // exited meanwhile
    }

    // --- links ---

    public void HardLink(string existingFile, string newLink)
    {
        if (Native.CreateHardLink(newLink, existingFile, 0) == 0)
            throw new IOException($"could not hard-link {existingFile} as {newLink}: Win32 error {Marshal.GetLastPInvokeError()}");
    }

    public void LinkFolder(string existingFolder, string newLink) => Junction.Create(Path.GetFullPath(existingFolder), Path.GetFullPath(newLink));

    // --- the game window ---

    public bool CanPostKeys => true;
    public bool CanCaptureWindow => true;
    public bool CanReadConsole => true;
    public string WhyNot(string capability) => $"{capability} is available on Windows";

    public void PostKey(int pid, int virtualKey)
    {
        var window = GameWindow.Find(pid) ?? throw new IOException("game window not found");
        var scan = Native.MapVirtualKey((uint)virtualKey, 0);
        nint down = (nint)(1 | (scan << 16));
        nint up = (nint)(1L | ((long)scan << 16) | 0xC0000000L);
        Native.PostMessage(window, Native.WmKeyDown, virtualKey, down);
        Thread.Sleep(80);
        Native.PostMessage(window, Native.WmKeyUp, virtualKey, up);
    }

    public Screenshot CaptureWindow(int pid)
    {
        var old = Native.SetThreadDpiAwarenessContext(Native.DpiAwarenessPerMonitorV2); // real pixels, not scaled ones
        try
        {
            var window = GameWindow.Find(pid) ?? throw new IOException("game window not found");
            Native.GetWindowRect(window, out var rect);
            int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0) throw new IOException("the game window has no size (minimized?)");
            var screen = Native.GetDC(0);
            var dc = Native.CreateCompatibleDC(screen);
            var bitmap = Native.CreateCompatibleBitmap(screen, width, height);
            var previous = Native.SelectObject(dc, bitmap);
            try
            {
                // PrintWindow asks the window for its own picture: it may sit behind other
                // windows and is neither moved nor brought forward. Full content: DirectX.
                if (Native.PrintWindow(window, dc, Native.PwRenderFullContent) == 0) throw new IOException("PrintWindow failed");
                var pixels = new byte[width * height * 4];
                var info = new Native.BitmapInfo
                {
                    Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32, Compression = 0,
                };
                fixed (byte* bits = pixels)
                {
                    if (Native.GetDIBits(dc, bitmap, 0, (uint)height, bits, &info, 0) == 0) throw new IOException("GetDIBits failed");
                }
                return new Screenshot(width, height, pixels);
            }
            finally
            {
                Native.SelectObject(dc, previous);
                Native.DeleteObject(bitmap);
                Native.DeleteDC(dc);
                Native.ReleaseDC(0, screen);
            }
        }
        finally
        {
            if (old != 0) Native.SetThreadDpiAwarenessContext(old);
        }
    }

    public IReadOnlyList<string> ReadConsole(int pid) => ConsoleReader.ReadInChild(pid);

    // --- windows in a deck ---

    public bool CanHostWindows => true;

    public nint FindGameWindow(int pid) => GameWindow.Find(pid) ?? 0;

    public WindowAdoption AdoptWindow(nint window, nint host) => WindowHost.Adopt(window, host);

    public void PlaceWindow(nint window, int x, int y, int width, int height, bool front) => WindowHost.Place(window, x, y, width, height, front);

    public void ReleaseWindow(WindowAdoption adoption) => WindowHost.Release(adoption);

    public IDisposable KeepForeground(string executable, TextWriter? say = null) =>
        new ForegroundGuard(executable, GameProcesses, say);

    public bool? FirewallAllows(string executable) => Firewall.Allows(executable);
}
