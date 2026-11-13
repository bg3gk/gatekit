namespace GateKit.Platform;

/// <summary>
/// Everything the stand does that depends on the operating system. The rest of GateKit only
/// talks to the game through files and through this interface, so a new platform is one class.
/// Paths given to and returned by it are host paths (what this process sees); a path the game
/// itself reads (Script Extender settings, say) goes through <see cref="ToGamePath"/>.
/// </summary>
public interface IGamePlatform
{
    /// <summary>"windows" or "linux-proton".</summary>
    string Name { get; }

    /// <summary>How paths compare on this system (case-insensitive on Windows).</summary>
    StringComparison PathComparison { get; }

    // --- where things are ------------------------------------------------------------

    /// <summary>Steam's own folder, or null when Steam is not installed.</summary>
    string? SteamRoot();

    /// <summary>Every Steam library folder (the ones holding steamapps), Steam's own first.</summary>
    IReadOnlyList<string> SteamLibraries();

    /// <summary>The player's profile folder of the installed game in <paramref name="gameFolder"/>
    /// (Larian Studios/Baldur's Gate 3).</summary>
    string ProfileFolder(string gameFolder);

    /// <summary>The Script Extender's cache of downloaded builds for the installed game, or null.</summary>
    string? ScriptExtenderCache(string gameFolder);

    /// <summary>A host path as the game sees it (on Linux: inside Wine, "Z:\..."). </summary>
    string ToGamePath(string hostPath);

    /// <summary>The profile folder of an instance whose files live in <paramref name="instanceRoot"/>.</summary>
    string InstanceProfileFolder(string instanceRoot);

    /// <summary>Creates the folders an instance's game expects in its private home or prefix.</summary>
    void PrepareInstanceHome(string instanceRoot);

    // --- processes -------------------------------------------------------------------

    /// <summary>Starts the game detached from this process (it outlives it).</summary>
    void StartGame(GameLaunch launch);

    /// <summary>Every running game process on this machine, whichever folder it was started from.</summary>
    IReadOnlyList<GameProcess> GameProcesses();

    /// <summary>Ends a process at once (no chance to save).</summary>
    void Kill(int pid);

    // --- links -----------------------------------------------------------------------

    /// <summary>A second name for an existing file (same drive / file system).</summary>
    void HardLink(string existingFile, string newLink);

    /// <summary>A folder that stands for another one: a junction on Windows, a symbolic link elsewhere.</summary>
    void LinkFolder(string existingFolder, string newLink);

    // --- optional: the game window ---------------------------------------------------

    /// <summary>Whether <see cref="PostKey"/> works here; if not, <see cref="WhyNot"/> says why.</summary>
    bool CanPostKeys { get; }

    /// <summary>Presses a key in the game's window without bringing it forward.</summary>
    void PostKey(int pid, int virtualKey);

    bool CanCaptureWindow { get; }

    /// <summary>The game window's picture, taken even when it is behind other windows.</summary>
    Screenshot CaptureWindow(int pid);

    bool CanReadConsole { get; }

    /// <summary>The text on Script Extender's console window of that game process, one row per line.</summary>
    IReadOnlyList<string> ReadConsole(int pid);

    // --- optional: windows in a deck ---

    /// <summary>Whether the windows of other programs can be adopted here (<see cref="AdoptWindow"/>).</summary>
    bool CanHostWindows { get; }

    /// <summary>The game's own window of that process as an opaque handle, or 0 while it has none.</summary>
    nint FindGameWindow(int pid);

    /// <summary>Makes <paramref name="window"/> a child of <paramref name="host"/> without activating
    /// it; the result is what <see cref="ReleaseWindow"/> needs to put it back.</summary>
    WindowAdoption AdoptWindow(nint window, nint host);

    /// <summary>Moves and sizes a window without activating it. <paramref name="front"/> puts it
    /// in front of its siblings, false behind them; neither changes which window is active.</summary>
    void PlaceWindow(nint window, int x, int y, int width, int height, bool front);

    /// <summary>Gives an adopted window back to the desktop, with the frame and place it had.</summary>
    void ReleaseWindow(WindowAdoption adoption);

    /// <summary>
    /// While the returned object is held, a game window that appears must not take the foreground
    /// from whatever the user is typing in. Disposing it stops the watch.
    ///
    /// <paramref name="executable"/> is the copy being started: only a window owned by a process
    /// running that exact executable is ever touched. The window class alone (SDL's `SDL_app`) is
    /// shared by every SDL program on the machine, and nothing of anyone else's is moved.
    ///
    /// Since the game is told through its own environment not to activate its window
    /// (SDL_WINDOW_NO_ACTIVATION_WHEN_SHOWN), this should now never have anything to do. It stays
    /// as a net that says so out loud when it does: that is how a patch to the game or to SDL
    /// taking the behaviour away would be noticed rather than silently endured.
    /// </summary>
    IDisposable KeepForeground(string executable, TextWriter? say = null);

    /// <summary>Whether a firewall rule already lets <paramref name="executable"/> listen; null when
    /// that cannot be read here. Read-only: GateKit never changes a firewall rule.</summary>
    bool? FirewallAllows(string executable);

    /// <summary>The message for a capability this platform lacks.</summary>
    string WhyNot(string capability);
}

/// <summary>A window that was adopted into a host window, and what it looked like before.</summary>
/// <param name="Window">The adopted window.</param>
/// <param name="FormerParent">Its parent before (0: the desktop).</param>
/// <param name="FormerStyle">Its window style before.</param>
/// <param name="Place">Where it was on the screen: left, top, width, height.</param>
public sealed record WindowAdoption(nint Window, nint FormerParent, nint FormerStyle, (int X, int Y, int Width, int Height) Place);

/// <summary>A running game: its process id and the full path of its executable (host path).</summary>
public sealed record GameProcess(int Pid, string ExecutablePath);

/// <summary>What <see cref="IGamePlatform.StartGame"/> starts.</summary>
/// <param name="Executable">The game's exe (host path).</param>
/// <param name="Arguments">Its command line arguments.</param>
/// <param name="WorkingDirectory">Its working folder (the game's bin).</param>
/// <param name="InstanceRoot">An instance's folder: the game then gets that instance's private
/// profile (Windows: USERPROFILE, Linux: Wine prefix). Null for the installed game.</param>
/// <param name="GameFolder">The game folder the exe belongs to (installed game or clone).</param>
/// <param name="SourceGameFolder">The installed game (where Steam keeps its prefix on Linux).</param>
/// <param name="Environment">Variables to add to the game's own environment, over the ones this
/// process has and the ones the platform sets for an instance. This is how the stand says things
/// to the game that the game's own libraries read - the SDL hint that stops a new window taking
/// the keyboard, for one - without patching anything or passing it through the extender.</param>
public sealed record GameLaunch(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    string? InstanceRoot,
    string GameFolder,
    string SourceGameFolder,
    IReadOnlyDictionary<string, string>? Environment = null);

/// <summary>Pixels of a window: 4 bytes per pixel, blue-green-red-unused, rows top to bottom.</summary>
public sealed record Screenshot(int Width, int Height, byte[] Bgra);

/// <summary>A capability the current platform does not have.</summary>
public sealed class PlatformCapabilityException(string message) : NotSupportedException(message);
