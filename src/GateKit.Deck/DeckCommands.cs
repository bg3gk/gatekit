using System.CommandLine;
using System.Runtime.Versioning;
using GateKit.Deck.Windows;
using GateKit.Platform;
using GateKit.Stand;

namespace GateKit.Deck;

/// <summary>`gatekit deck`: the window that holds every running game.</summary>
public static class DeckCommands
{
    public static Command CreateCommand()
    {
        var all = new Option<bool>("--all")
        {
            Description = "take hold of every copy running on this machine, not only the ones this stand started "
                + "(also GATEKIT_DECK_ADOPT=all). Without it a game somebody else started is listed and left alone.",
        };
        var command = new Command("deck", "one window that holds every running game: tiles, names, and no window thrown over your work") { all };
        command.SetAction(parse => Run(parse.GetValue(all)));
        var close = new Command("close", "close the deck that is running; every window goes back to the desktop, no game is touched");
        close.SetAction(_ => Run(settings => Console.Out.WriteLine(DeckApi.Close(settings))));
        command.Add(close);
        return command;
    }

    static int Run(bool all) => Run(settings =>
    {
        var platform = GamePlatform.Current;
        if (!platform.CanHostWindows) throw new StandException(platform.WhyNot("deck"));
        // Out of the build output and into a copy of its own, so the repository can be rebuilt
        // while the deck is up (RunCopy says why). The copy is a window program with no console
        // at all - a console appearing would take the keyboard, which is the thing the deck is
        // for - so this is where anyone watching is told where it writes instead.
        if (RunCopy.Relaunch("deck", Environment.GetCommandLineArgs().Skip(1).ToList(), Console.Error, wait: false) is not null)
        {
            Console.Out.WriteLine($"deck opening in its own window; it writes to {Path.Combine(settings.StateRoot, "deck", "deck.log")}");
            return;
        }
        Open(all ? settings with { DeckAdopt = "all" } : settings, platform, Console.Out);
    });

    static int Run(Action<StandSettings> action)
    {
        try
        {
            action(StandSettings.Resolve(GamePlatform.Current));
            return 0;
        }
        catch (StandException e)
        {
            Console.Error.WriteLine("ERROR: " + e.Message);
            return 1;
        }
        catch (PlatformCapabilityException e)
        {
            Console.Error.WriteLine("ERROR: " + e.Message);
            return 1;
        }
    }

    /// <summary>Opens the deck of this machine and serves it until its window closes.</summary>
    static void Open(StandSettings settings, IGamePlatform platform, TextWriter output)
    {
        if (!OperatingSystem.IsWindows()) throw new StandException(platform.WhyNot("deck"));
        using var log = OwnConsole(settings, ref output);
        new DeckWindow(settings, platform, output).Run();
    }

    /// <summary>
    /// A deck opened by a click, or by anything that is not already a terminal, is given a console
    /// window of its own by Windows. That window is exactly the kind of thing the deck exists to
    /// get rid of: it is not the deck, nobody reads it, and it sits in the taskbar beside it.
    ///
    /// So when this program is the only one on its console - which means the console was made for
    /// it and nobody was there before - the console is let go and everything the deck would have
    /// printed goes to a file beside the deck's other state. Run from a terminal, nothing changes:
    /// the terminal was there first, it is the person's, and the deck writes to it as before.
    /// </summary>
    [SupportedOSPlatform("windows")]
    static TextWriter? OwnConsole(StandSettings settings, ref TextWriter output)
    {
        uint count;
        unsafe
        {
            uint one;
            count = Native.GetConsoleProcessList(&one, 1);
        }
        // 0: no console at all - started as a window program, so there is nowhere to print.
        // 1: a console made for this program alone, which is a window nobody asked for.
        // More: somebody's terminal, which was there first and is theirs; print to it as before.
        if (count > 1) return null;
        var file = Path.Combine(settings.StateRoot, "deck", "deck.log");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var log = new StreamWriter(new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true,
        };
        log.WriteLine($"the deck has no console of its own; this is where it writes ({DateTime.Now:yyyy-MM-dd HH:mm:ss})");
        output = log;
        Native.FreeConsole();
        return log;
    }
}
