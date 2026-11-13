using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace GateKit.Platform.Windows;

/// <summary>
/// Starts a program that outlives this one and shares nothing with it: no console, no inherited
/// handles, its own process group.
///
/// .NET's own <c>Process.Start</c> cannot do this. It always asks for handle inheritance, and
/// inheritance on Windows is all or nothing - every inheritable handle the parent holds goes
/// across, not only the three standard ones. So a deck started from a terminal ended up holding
/// that terminal's output pipe, and the shell, which waits for every holder of the pipe to let
/// go, sat there looking hung for as long as the deck was open.
///
/// This is the same call the game is started with, for the same reason.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe class DetachedStart
{
    public static void Run(string program, IReadOnlyList<string> arguments, string runCopyFolder)
    {
        var commandLine = new StringBuilder(WindowsPlatform.Quote(program));
        foreach (var argument in arguments) commandLine.Append(' ').Append(WindowsPlatform.Quote(argument));
        commandLine.Append('\0');

        var environment = new StringBuilder();
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (string.Equals((string)entry.Key, RunCopy.Marker, StringComparison.OrdinalIgnoreCase)) continue;
            environment.Append((string)entry.Key).Append('=').Append(entry.Value as string ?? "").Append('\0');
        }
        environment.Append(RunCopy.Marker).Append('=').Append(runCopyFolder).Append('\0').Append('\0');

        var startup = new Native.StartupInfo { cb = sizeof(Native.StartupInfo) };
        Native.ProcessInformation info;
        var line = commandLine.ToString().ToCharArray();
        var block = environment.ToString().ToCharArray();
        fixed (char* linePointer = line)
        fixed (char* blockPointer = block)
        {
            var ok = Native.CreateProcess(program, linePointer, 0, 0, inheritHandles: 0,
                Native.DetachedProcess | Native.CreateNewProcessGroup | Native.CreateUnicodeEnvironment,
                // The folder it was started from, not the folder it was copied to: that is what
                // tells GateKit which repository it belongs to, and with it the state folder, the
                // bridge mods and where its log goes. A copy that forgets it is a stranger.
                blockPointer, Environment.CurrentDirectory, &startup, &info);
            if (ok == 0) throw new IOException($"could not start {program}: Win32 error {Marshal.GetLastPInvokeError()}");
        }
        Native.CloseHandle(info.hThread);
        Native.CloseHandle(info.hProcess);
    }
}
