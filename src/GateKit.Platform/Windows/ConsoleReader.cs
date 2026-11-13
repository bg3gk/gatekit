using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace GateKit.Platform.Windows;

/// <summary>
/// Reads the screen buffer of Script Extender's console window (CreateConsole=true), so nothing
/// in the game has to cooperate. A process can be attached to one console only, so the reading
/// happens in a short-lived copy of this program (<see cref="PlatformHelper"/>).
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe class ConsoleReader
{
    public static IReadOnlyList<string> ReadInChild(int pid)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new IOException("this process has no path"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // Run through `dotnet gatekit.dll`: the host needs the entry assembly first.
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            && Assembly.GetEntryAssembly()?.Location is { Length: > 0 } entry)
            start.ArgumentList.Add(entry);
        start.ArgumentList.Add(PlatformHelper.ReadConsoleVerb);
        start.ArgumentList.Add(pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var child = Process.Start(start) ?? throw new IOException("could not start the console reader");
        var output = child.StandardOutput.ReadToEndAsync();
        var error = child.StandardError.ReadToEndAsync();
        if (!child.WaitForExit(20_000))
        {
            child.Kill();
            throw new IOException("the console reader did not finish in 20 s");
        }
        if (child.ExitCode != 0) throw new IOException("could not read the console: " + error.Result.Trim());
        return output.Result.Split('\n');
    }

    /// <summary>Runs in the child: attaches to the game's console and writes its rows to stdout.</summary>
    public static int Dump(int pid)
    {
        using var stdout = Console.OpenStandardOutput(); // a pipe: stays valid when the console goes
        var stderr = Console.OpenStandardError();
        void Fail(string message)
        {
            var bytes = Encoding.UTF8.GetBytes(message);
            stderr.Write(bytes);
            stderr.Flush();
        }
        Native.FreeConsole();
        if (Native.AttachConsole((uint)pid) == 0)
        {
            Fail($"AttachConsole failed, error {Marshal.GetLastPInvokeError()} (is CreateConsole on?)");
            return 1;
        }
        var handle = Native.CreateFile("CONOUT$", Native.GenericRead | Native.GenericWrite,
            Native.FileShareRead | Native.FileShareWrite, 0, Native.OpenExisting, 0, 0);
        if (handle == Native.InvalidHandle || Native.GetConsoleScreenBufferInfo(handle, out var info) == 0)
        {
            Fail($"GetConsoleScreenBufferInfo failed, error {Marshal.GetLastPInvokeError()}");
            return 1;
        }
        var width = info.SizeX;
        var buffer = new char[width + 1];
        var rows = new List<string>();
        fixed (char* row = buffer)
        {
            for (var y = 0; y <= info.CursorY; y++)
            {
                Native.ReadConsoleOutputCharacter(handle, row, (uint)width, (uint)(y << 16), out var read);
                rows.Add(new string(row, 0, (int)read).TrimEnd());
            }
        }
        Native.CloseHandle(handle);
        stdout.Write(Encoding.UTF8.GetBytes(string.Join("\n", rows)));
        stdout.Flush();
        return 0;
    }
}
