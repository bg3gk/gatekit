using System.Diagnostics;
using System.Globalization;
using GateKit.Home;

namespace GateKit.Testing;

/// <summary>
/// Where unit tests write: one folder per test process under the home's scratch folder
/// (<c>&lt;home&gt;/scratch/unit/&lt;pid&gt;-&lt;started&gt;</c>), never the system temp folder.
/// The run's folder is removed when the process ends; a folder left by a process that is gone
/// (killed, crashed) is removed when the next run starts, so nothing piles up. Every unit-test
/// project compiles this file (unit/Directory.Build.props).
/// </summary>
public static class Scratch
{
    static readonly Lazy<string> RunFolder = new(Start);

    /// <summary>This process's folder, made on first use.</summary>
    public static string Run => RunFolder.Value;

    /// <summary>A path for one test's files, unique, under <see cref="Run"/>; not created.</summary>
    public static string NewPath(string name) => Path.Combine(Run, name + "-" + Guid.NewGuid().ToString("N")[..12]);

    /// <summary>Removes every run folder in <paramref name="root"/> whose process is gone; returns how many.</summary>
    public static int Sweep(string root)
    {
        if (!Directory.Exists(root)) return 0;
        var removed = 0;
        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            if (!Gone(Path.GetFileName(folder))) continue;
            if (TryRemove(folder)) removed++;
        }
        return removed;
    }

    static string Start()
    {
        var root = Path.Combine(GateKitHome.Current.Scratch.Require(), "unit");
        Directory.CreateDirectory(root);
        // a test that looks for gatekit.json from its own folder must not find the machine's
        var marker = Path.Combine(root, GateKitHome.IsolationMarker);
        if (!File.Exists(marker)) File.WriteAllText(marker, "");
        Sweep(root);
        var started = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
        var mine = Path.Combine(root, $"{Environment.ProcessId}-{started}");
        Directory.CreateDirectory(mine);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryRemove(mine);
        return mine;
    }

    /// <summary>Whether the process a run folder is named after has ended (a name not of that form counts as gone).</summary>
    static bool Gone(string name)
    {
        var dash = name.IndexOf('-', StringComparison.Ordinal);
        if (dash <= 0 || !int.TryParse(name[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out var pid)) return true;
        if (pid == Environment.ProcessId) return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited;
        }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return true; }
    }

    /// <summary>Deletes a folder (links as links, never what they point to); false when something still holds it.</summary>
    static bool TryRemove(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
