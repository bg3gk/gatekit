using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace GateKit.Platform;

/// <summary>
/// Linux with the Windows game running under Proton. The game's profile lives in a Wine prefix
/// (steamapps/compatdata/1086940/pfx for the installed game; an instance gets a prefix of its own
/// under its folder). The game is started with `umu-run` when it is on PATH, otherwise with
/// Proton's own `proton run` (GATEKIT_PROTON names the proton script or its folder); either way
/// with WINEDLLOVERRIDES="DWrite=n,b" so Script Extender's loader (bin/DWrite.dll) is used.
///
/// Not done yet (no Linux machine to measure on): keys, screenshots and the console window -
/// they say so instead of guessing. `logs --story` (Script Extender's log files) works.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed partial class LinuxProtonPlatform : IGamePlatform
{
    const string PrefixUser = "steamuser";

    public string Name => "linux-proton";
    public StringComparison PathComparison => StringComparison.Ordinal;

    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    // --- where things are ---

    public string? SteamRoot()
    {
        string[] candidates =
        [
            Path.Combine(Home, ".steam", "steam"),
            Path.Combine(Home, ".local", "share", "Steam"),
            Path.Combine(Home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"),
            Path.Combine(Home, "snap", "steam", "common", ".local", "share", "Steam"),
        ];
        foreach (var candidate in candidates)
        {
            if (File.Exists(Path.Combine(candidate, "steamapps", "libraryfolders.vdf")) || Directory.Exists(Path.Combine(candidate, "steamapps")))
                return FileTree.ResolvePath(candidate);
        }
        return null;
    }

    public IReadOnlyList<string> SteamLibraries() => SteamRoot() is { } root ? Steam.Libraries(root) : [];

    /// <summary>steamapps/compatdata/1086940 of the library the game is installed in.</summary>
    static string CompatData(string gameFolder)
    {
        // <library>/steamapps/common/<game>
        var steamapps = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(gameFolder))!)!;
        return Path.Combine(steamapps, "compatdata", GamePlatform.AppId);
    }

    static string LocalIn(string compatData) => Path.Combine(compatData, "pfx", "drive_c", "users", PrefixUser, "AppData", "Local");

    public string ProfileFolder(string gameFolder) => Path.Combine(LocalIn(CompatData(gameFolder)), "Larian Studios", "Baldur's Gate 3");

    public string? ScriptExtenderCache(string gameFolder) => Path.Combine(LocalIn(CompatData(gameFolder)), "BG3ScriptExtender");

    /// <summary>Wine maps the whole Unix file system to drive Z:.</summary>
    public string ToGamePath(string hostPath) => "Z:" + Path.GetFullPath(hostPath).Replace('/', '\\');

    static string InstanceCompatData(string instanceRoot) => Path.Combine(instanceRoot, "prefix");

    public string InstanceProfileFolder(string instanceRoot) => Path.Combine(LocalIn(InstanceCompatData(instanceRoot)), "Larian Studios", "Baldur's Gate 3");

    public void PrepareInstanceHome(string instanceRoot)
    {
        var local = LocalIn(InstanceCompatData(instanceRoot));
        Directory.CreateDirectory(local);
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(local)!, "Roaming"));
    }

    // --- processes ---

    public void StartGame(GameLaunch launch)
    {
        var compat = launch.InstanceRoot is { } root ? InstanceCompatData(root) : CompatData(launch.SourceGameFolder);
        Directory.CreateDirectory(compat);
        var environment = new Dictionary<string, string>
        {
            ["WINEDLLOVERRIDES"] = "DWrite=n,b",
            ["STEAM_COMPAT_DATA_PATH"] = compat,
            ["WINEPREFIX"] = Path.Combine(compat, "pfx"),
            ["SteamAppId"] = GamePlatform.AppId,
            ["SteamGameId"] = GamePlatform.AppId,
        };
        if (SteamRoot() is { } steam) environment["STEAM_COMPAT_CLIENT_INSTALL_PATH"] = steam;
        // What the stand asked for, last, so it means what it says. SDL's own hints travel this
        // way under Proton as well: the X11 backend reads the same variable the Windows one does.
        // (Wayland natively cannot honour the no-activation hint; SDL says so, not us.)
        foreach (var (key, value) in launch.Environment ?? new Dictionary<string, string>()) environment[key] = value;

        var command = new List<string>();
        var proton = Environment.GetEnvironmentVariable("GATEKIT_PROTON");
        var umu = FindOnPath("umu-run");
        if (umu is not null && string.IsNullOrEmpty(proton))
        {
            environment["GAMEID"] = "umu-" + GamePlatform.AppId;
            command.Add(umu);
            command.Add(launch.Executable);
        }
        else
        {
            var script = FindProton(proton) ?? throw new IOException(
                "neither umu-run (on PATH) nor Proton was found; install umu-launcher or set GATEKIT_PROTON to Proton's folder");
            if (umu is not null) environment["PROTONPATH"] = Path.GetDirectoryName(script)!;
            command.Add(script);
            command.Add("run");
            command.Add(launch.Executable);
        }
        command.AddRange(launch.Arguments);

        // Through sh: the game gets no stdin/stdout of ours (a caller reading our output would
        // otherwise wait for the game to end), and setsid puts it in a session of its own.
        var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, WorkingDirectory = launch.WorkingDirectory };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("exec setsid \"$@\" </dev/null >/dev/null 2>&1 &");
        start.ArgumentList.Add("gatekit-launch");
        foreach (var part in command) start.ArgumentList.Add(part);
        foreach (var (key, value) in environment) start.Environment[key] = value;
        using var shell = Process.Start(start) ?? throw new IOException("could not start /bin/sh");
        shell.WaitForExit();
    }

    static string? FindOnPath(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var path = Path.Combine(dir, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>The proton script: GATEKIT_PROTON (the script or its folder), else the newest
    /// "Proton*" in the Steam libraries' steamapps/common.</summary>
    string? FindProton(string? configured)
    {
        if (!string.IsNullOrEmpty(configured))
        {
            var script = Directory.Exists(configured) ? Path.Combine(configured, "proton") : configured;
            return File.Exists(script) ? script : null;
        }
        return SteamLibraries()
            .Select(l => Path.Combine(l, "steamapps", "common"))
            .Where(Directory.Exists)
            .SelectMany(c => Directory.EnumerateDirectories(c, "Proton*"))
            .Select(d => Path.Combine(d, "proton"))
            .Where(File.Exists)
            .OrderByDescending(p => p, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>Wine processes show the Windows path of the exe as argv[0] (Z:\...\bg3_dx11.exe);
    /// they are matched by that, mapped back to a host path.</summary>
    public IReadOnlyList<GameProcess> GameProcesses()
    {
        var found = new List<GameProcess>();
        if (!Directory.Exists("/proc")) return found;
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;
            string[] argv;
            try { argv = File.ReadAllText(Path.Combine(dir, "cmdline")).Split('\0'); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            if (argv.Length == 0 || argv[0].Length == 0) continue;
            var first = argv[0];
            var name = first[(first.LastIndexOfAny(['\\', '/']) + 1)..];
            if (!GamePlatform.ProcessNames.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
            found.Add(new GameProcess(pid, ToHostPath(first)));
        }
        return found;
    }

    internal static string ToHostPath(string windowsPath)
    {
        if (windowsPath.Length > 2 && (windowsPath[0] == 'Z' || windowsPath[0] == 'z') && windowsPath[1] == ':')
            return windowsPath[2..].Replace('\\', '/');
        return windowsPath.Replace('\\', '/');
    }

    public void Kill(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill();
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
    }

    // --- links ---

    [LibraryImport("libc", EntryPoint = "link", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Link(string existing, string newPath);

    public void HardLink(string existingFile, string newLink)
    {
        if (Link(existingFile, newLink) != 0)
            throw new IOException($"could not hard-link {existingFile} as {newLink}: errno {Marshal.GetLastPInvokeError()}");
    }

    public void LinkFolder(string existingFolder, string newLink) => Directory.CreateSymbolicLink(newLink, existingFolder);

    // --- the game window: not yet ---

    public bool CanPostKeys => false;
    public bool CanCaptureWindow => false;
    public bool CanReadConsole => false;

    public string WhyNot(string capability) => capability switch
    {
        "key" => "posting keys to the game window is not implemented on Linux yet (it needs an X11/Wayland path into the Wine window); use the bridge's own input functions",
        "shot" => "screenshots of the game window are not implemented on Linux yet; the bridge can report what the screen shows",
        "console" => "the Script Extender console window cannot be read on Linux; the extender's log files are read instead (LogRuntime)",
        "deck" => "the deck holds game windows inside one window, which only the Windows path can do so far; "
            + "a window manager here tiles them by itself, and every copy is still named by `gatekit window list`",
        _ => $"{capability} is not implemented on Linux yet",
    };

    public void PostKey(int pid, int virtualKey) => throw new PlatformCapabilityException(WhyNot("key"));
    public Screenshot CaptureWindow(int pid) => throw new PlatformCapabilityException(WhyNot("shot"));
    public IReadOnlyList<string> ReadConsole(int pid) => throw new PlatformCapabilityException(WhyNot("console"));

    // --- windows in a deck: not here ---

    public bool CanHostWindows => false;
    public nint FindGameWindow(int pid) => 0;
    public WindowAdoption AdoptWindow(nint window, nint host) => throw new PlatformCapabilityException(WhyNot("deck"));
    public void PlaceWindow(nint window, int x, int y, int width, int height, bool front) => throw new PlatformCapabilityException(WhyNot("deck"));
    public void ReleaseWindow(WindowAdoption adoption) => throw new PlatformCapabilityException(WhyNot("deck"));

    /// <summary>Nothing to guard: the game is started detached and the window manager decides where
    /// its window goes.</summary>
    public IDisposable KeepForeground(string executable, TextWriter? say = null) => new NoGuard();

    /// <summary>Linux has no one firewall to read; the game needs no rule to listen locally.</summary>
    public bool? FirewallAllows(string executable) => null;

    sealed class NoGuard : IDisposable
    {
        public void Dispose() { }
    }
}
