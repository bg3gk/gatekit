using System.Diagnostics;

namespace GateKit.Platform;

/// <summary>
/// Lets the repository be rebuilt while GateKit's own long-running programs are up.
///
/// A program run straight out of <c>bin</c> keeps every assembly it loaded mapped, and the next
/// <c>dotnet build</c> cannot write over them: it fails with MSB3021/MSB3027, which reads like a
/// broken build and is a locked file. For the deck and the MCP server - the two that are meant to
/// stay up for hours while the tools around them change - that turned "add a tool and reload" into
/// "close everything, build, start everything again", and worse, it produced red builds that were
/// then reported as real breakages.
///
/// So they do not run out of <c>bin</c>. The first time one starts there it copies its folder
/// somewhere of its own and starts that copy with the same arguments, inheriting the same console
/// and the same pipes - which is what lets the MCP server keep speaking over stdio - and waits for
/// it. The build output is then held by nobody.
///
/// It is deliberately narrow. Only programs that stay up do this; a short command would pay the
/// copy for nothing. A program that is not in a build output - an installed GateKit, or one
/// already running from a copy - is left alone, so this never recurses and never gets in the way
/// of a release. <c>GATEKIT_RUN_IN_PLACE=1</c> turns it off, for anyone debugging the thing itself.
/// </summary>
public static class RunCopy
{
    /// <summary>Set in the copy, so it knows not to copy itself again.</summary>
    public const string Marker = "GATEKIT_RUN_COPY";

    /// <summary>
    /// Gets rid of the console window Windows made for this program, when it made one for this
    /// program alone - which is what happens when it is started by a click or by another program
    /// rather than typed into a terminal.
    ///
    /// For a command that opens a window and goes, that console is pure harm: it appears, it
    /// takes the keyboard because a new console always does, and a moment later it is gone again.
    /// Hiding it hands the foreground back to the window underneath, which is the one the person
    /// was using.
    ///
    /// It is called before anything else, because the window exists from the moment the process
    /// starts: a few milliseconds of it cannot be helped from inside this program at all. Only a
    /// launcher built for the window subsystem would never create one, and that is a separate
    /// executable, not a flag.
    ///
    /// A console shared with anything else is somebody's terminal. It was there first, it is
    /// theirs, and it is left exactly as it is.
    /// </summary>
    public static void HideOwnConsoleWindow()
    {
        if (!OperatingSystem.IsWindows()) return;
        Windows.ConsoleWindow.HideIfOurs();
    }

    /// <summary>
    /// Null to carry on in this process. Otherwise the exit code to leave with.
    ///
    /// <paramref name="wait"/> false lets this process go as soon as the copy is up, which leaves
    /// nothing at all of the build output held - the right thing for a program that owns a window
    /// and is done talking to whoever started it. True keeps this process alive around the copy,
    /// which is what a server speaking over stdio needs: the pipes belong to the process its
    /// client started. It then still holds this program's own two assemblies, and nothing else -
    /// so everything the server is actually made of can be rebuilt under it.
    /// </summary>
    public static int? Relaunch(string name, IReadOnlyList<string> args, TextWriter? say = null, bool wait = true)
    {
        if (!ShouldCopy()) return null;
        var from = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var to = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GateKit", "run", name);
        string program;
        try
        {
            Mirror(from, to);
            program = Path.Combine(to, Path.GetFileName(Environment.ProcessPath!));
            if (!File.Exists(program)) return null;     // not laid out as expected: carry on in place
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not being able to make the copy is not a reason not to run. The only thing lost is
            // being able to build while this is up, and the build says so clearly enough.
            say?.WriteLine($"could not run from a copy ({e.Message}); the repository cannot be rebuilt while this is up");
            return null;
        }

        // A copy that is not waited on must inherit nothing at all. .NET starts a child with
        // handle inheritance on, so the copy ended up holding the terminal's own output pipe -
        // and a shell waits for every holder of that pipe to let go, so the terminal that opened
        // the deck sat there looking hung for as long as the deck was up. Redirecting the three
        // standard handles does not help: every inheritable handle goes across, not only those.
        // So it is started the way the game is - detached, in a process group of its own, with
        // inheritance off - and it writes to its log rather than to anybody's console.
        if (!wait && OperatingSystem.IsWindows())
        {
            Windows.DetachedStart.Run(program, args, to);
            return 0;
        }

        var start = new ProcessStartInfo(program) { UseShellExecute = false, WorkingDirectory = Environment.CurrentDirectory };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        start.Environment[Marker] = to;
        using var child = Process.Start(start);
        if (child is null) return null;
        if (!wait) return 0;
        child.WaitForExit();
        return child.ExitCode;
    }

    static bool ShouldCopy()
    {
        if (Environment.GetEnvironmentVariable(Marker) is { Length: > 0 }) return false;
        if (Environment.GetEnvironmentVariable("GATEKIT_RUN_IN_PLACE") is { Length: > 0 } stay
            && !stay.Equals("0", StringComparison.Ordinal)) return false;
        if (Environment.ProcessPath is not { Length: > 0 }) return false;
        // Only a build output. The folder names are MSBuild's own, and an installed copy has
        // neither, so this is off for everyone but the people building GateKit.
        var parts = AppContext.BaseDirectory.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(p => p.Equals("bin", StringComparison.OrdinalIgnoreCase))
            && parts.Any(p => p.Equals("Debug", StringComparison.OrdinalIgnoreCase)
                || p.Equals("Release", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Copies what has changed. A build that only touched one assembly costs one file.</summary>
    static void Mirror(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var source in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(from, source);
            var target = Path.Combine(to, relative);
            var old = new FileInfo(target);
            var now = new FileInfo(source);
            if (old.Exists && old.Length == now.Length && old.LastWriteTimeUtc == now.LastWriteTimeUtc) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
            File.SetLastWriteTimeUtc(target, now.LastWriteTimeUtc);
        }
    }
}
