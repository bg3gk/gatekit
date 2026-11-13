using System.Globalization;

namespace GateKit.Platform;

/// <summary>
/// Helper modes the platform runs in a copy of the current program (the `gatekit` executable).
/// The program's Main must call <see cref="TryRun"/> first thing, before parsing its own command line.
/// </summary>
public static class PlatformHelper
{
    /// <summary>`gatekit __read-console PID`: dump a game's console buffer (Windows).</summary>
    public const string ReadConsoleVerb = "__read-console";

    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 2 && args[0] == ReadConsoleVerb)
        {
            if (!OperatingSystem.IsWindows())
            {
                Console.Error.Write("reading the console is a Windows helper");
                exitCode = 1;
                return true;
            }
            exitCode = Windows.ConsoleReader.Dump(int.Parse(args[1], CultureInfo.InvariantCulture));
            return true;
        }
        return false;
    }
}
