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
    /// The command a command line runs: its first word after the global options that come before
    /// it (<c>--instance NAME</c>, <c>--instance=NAME</c>). Every runner of the third run wrote
    /// <c>gatekit --instance &lt;copy&gt; mcp call ...</c>, and a check on the first word alone kept
    /// all of those in <c>bin</c>, holding the build output, so a C# fix waited for every game to
    /// close. Null when there is no command (only options, or an option this does not know).
    /// </summary>
    public static string? Verb(IReadOnlyList<string> args, IReadOnlyCollection<string>? valued = null) =>
        VerbAt(args, valued) is var at && at >= 0 ? args[at] : null;

    /// <summary>Where <see cref="Verb"/> stands in <paramref name="args"/>; -1 when there is none.</summary>
    public static int VerbAt(IReadOnlyList<string> args, IReadOnlyCollection<string>? valued = null)
    {
        valued ??= ["--instance"];
        for (var i = 0; i < args.Count; i++)
        {
            var word = args[i];
            if (!word.StartsWith('-')) return i;
            var equals = word.IndexOf('=', StringComparison.Ordinal);
            var name = equals < 0 ? word : word[..equals];
            if (!valued.Contains(name)) return -1;  // --help, --version, anything unknown: not ours to guess
            if (equals < 0) i++;                    // its value is the next word
        }
        return -1;
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
        string runs;
        try { runs = GateKit.Home.GateKitHome.Current.RunCache.Path!; }
        catch (GateKit.Home.HomeException e)
        {
            say?.WriteLine($"could not run from a copy ({e.Message}); the repository cannot be rebuilt while this is up");
            return null;
        }
        string to;
        string program;
        try
        {
            to = Path.Combine(runs, CopyName(name, from));
            Place(from, to);
            Sweep(runs, name, from, to);
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
        // The copy is told where it was copied from, so it reads the same gatekit.json and finds
        // the same checkout as the program it stands in for (GateKitHome.ProgramFolder).
        var set = new Dictionary<string, string> { [Marker] = to, [GateKit.Home.GateKitHome.ProgramFolderVariable] = from };
        if (!wait && OperatingSystem.IsWindows())
        {
            Windows.DetachedStart.Run(program, args, set);
            return 0;
        }

        var start = new ProcessStartInfo(program) { UseShellExecute = false, WorkingDirectory = Environment.CurrentDirectory };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        foreach (var (variable, value) in set) start.Environment[variable] = value;
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

    /// <summary>
    /// The copy's folder: <c>&lt;name&gt;-&lt;checkout&gt;-&lt;build&gt;</c>. One folder for every
    /// checkout and every build used to be shared - a worktree's `mcp` and the main checkout's
    /// mirrored into the same <c>run/mcp</c>, and after a rebuild the next call tried to overwrite
    /// assemblies a server of the last build still held, failed, and ran in place, holding the build
    /// output again. Now a build gets a copy of its own, and a copy in use is never written over.
    /// </summary>
    public static string CopyName(string name, string from)
    {
        var newest = 0L;
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            if (file.EndsWith(".locked", StringComparison.OrdinalIgnoreCase)) continue;
            count++;
            var at = File.GetLastWriteTimeUtc(file).Ticks;
            if (at > newest) newest = at;
        }
        return $"{name}-{Hash(Path.GetFullPath(from).ToUpperInvariant())}-{Hash($"{newest}/{count}")}";
    }

    static string Hash(string text) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..8];

    /// <summary>Removes this checkout's older copies of <paramref name="name"/>; one still running
    /// holds its files and stays until a later start finds it free.</summary>
    static void Sweep(string runs, string name, string from, string keep)
    {
        var mine = $"{name}-{Hash(Path.GetFullPath(from).ToUpperInvariant())}-";
        foreach (var old in Directory.EnumerateDirectories(runs, mine + "*"))
        {
            if (string.Equals(Path.GetFullPath(old), Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase)) continue;
            // Moved aside first: a folder whose assemblies a running copy has loaded cannot be
            // renamed, so it is left whole - deleting file by file would take from a running server
            // the assemblies it has not loaded yet.
            var gone = old + ".gone";
            try { Directory.Move(old, gone); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            try { Directory.Delete(gone, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Makes the copy at <paramref name="to"/> once: built beside it and moved into place, so
    /// two starts at the same moment never write over files the other has already loaded.</summary>
    public static void Place(string from, string to)
    {
        if (Directory.Exists(to)) return;
        var building = $"{to}.{Environment.ProcessId}.tmp";
        Mirror(from, building);
        try { Directory.Move(building, to); }
        catch (IOException) when (Directory.Exists(to))
        {
            try { Directory.Delete(building, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>Copies what has changed. A build that only touched one assembly costs one file.</summary>
    static void Mirror(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var source in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            // what a build moved aside because it was in use (Directory.Build.targets) is not the program
            if (source.EndsWith(".locked", StringComparison.OrdinalIgnoreCase)) continue;
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
