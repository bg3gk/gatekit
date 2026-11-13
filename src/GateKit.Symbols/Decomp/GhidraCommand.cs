using System.Diagnostics;
using System.Text;
using GateKit.Symbols.Queries;

namespace GateKit.Symbols.Decomp;

/// <summary>`run`: Ghidra without its window over one binary - analysis, the names the symbol
/// database knows, decompilation of every function into functions.jsonl.</summary>
internal static class GhidraCommand
{
    /// <summary>Ghidra (GHIDRA_HOME, else the last ghidra_* folder under the tools folder) and
    /// the JDK to run it with (the last jdk-* folder there, or null to use the system's).</summary>
    public static (string Ghidra, string? Jdk) FindTools(DecompSettings settings)
    {
        string launcher = OperatingSystem.IsWindows() ? "launch.bat" : "launch.sh";
        string? Last(string pattern) => Directory.Exists(settings.Tools)
            ? Directory.EnumerateDirectories(settings.Tools, pattern).Order(StringComparer.Ordinal).LastOrDefault() : null;
        var home = Environment.GetEnvironmentVariable("GHIDRA_HOME");
        var ghidra = string.IsNullOrWhiteSpace(home) ? Last("ghidra_*") : home;
        if (ghidra == null || !File.Exists(Path.Combine(ghidra, "support", launcher)))
            throw new SymbolsException($"Ghidra not found under {settings.Tools} (unpack a ghidra_*_PUBLIC release there, or set GHIDRA_HOME or BG3_DECOMP_TOOLS)");
        return (ghidra, Last("jdk-*"));
    }

    /// <summary>The folder with ApplyNames.java and ExportDecomp.java: GATEKIT_GHIDRA_SCRIPTS, a
    /// ghidra folder next to the program, or tools/ghidra in a folder above it (the repository).</summary>
    public static string FindScripts()
    {
        static bool Has(string folder) => File.Exists(Path.Combine(folder, "ExportDecomp.java"));
        var set = Environment.GetEnvironmentVariable("GATEKIT_GHIDRA_SCRIPTS");
        if (!string.IsNullOrWhiteSpace(set))
            return Has(set) ? set : throw new SymbolsException($"GATEKIT_GHIDRA_SCRIPTS={set} has no ExportDecomp.java");
        var here = Path.Combine(AppContext.BaseDirectory, "ghidra");
        if (Has(here))
            return here;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tools", "ghidra");
            if (Has(candidate))
                return candidate;
        }
        throw new SymbolsException("the Ghidra scripts (tools/ghidra/ExportDecomp.java) were not found; set GATEKIT_GHIDRA_SCRIPTS to their folder");
    }

    static string Quote(string c) => c.Contains(' ', StringComparison.Ordinal) ? $"\"{c}\"" : c;

    /// <summary>Writes the names the symbol database knows for a game executable; prints what happened.</summary>
    static void ExportNames(DecompSession s, string binary, string names)
    {
        int count = GameLabels.WriteGhidraNames(s.Settings.SymbolsDb, binary, names);
        s.Err.WriteLine(count >= 0 ? $"{count} names of {binary} written to {names}" : $"no names for {binary} in {s.Settings.SymbolsDb}");
    }

    public static int Run(DecompSession s, string binaryName, string memory, int cpu, string? range, bool fresh)
    {
        var (ghidra, jdk) = FindTools(s.Settings);
        var scripts = FindScripts();
        var binary = s.Locate(binaryName) ?? throw new SymbolsException($"binary not found: {binaryName}");
        var file = new FileInfo(binary);
        var work = Path.Combine(s.Settings.Work, file.Name);
        Directory.CreateDirectory(work);
        var output = Path.Combine(work, "functions.jsonl");
        var log = Path.Combine(work, "ghidra.log");
        // Ghidra's own settings, cache and temporary files go next to the output, so that a run does
        // not depend on (or leave anything in) the user profile.
        var user = Path.Combine(s.Settings.Work, "ghidra-user");
        foreach (var sub in new[] { "settings", "cache", "tmp" })
            Directory.CreateDirectory(Path.Combine(user, sub));
        var vmargs = $"-Dapplication.settingsdir={Path.Combine(user, "settings")} -Dapplication.cachedir={Path.Combine(user, "cache")}"
                     + $" -Djava.io.tmpdir={Path.Combine(user, "tmp")}";
        var launcher = Path.Combine(ghidra, "support", OperatingSystem.IsWindows() ? "launch.bat" : "launch.sh");
        var cmd = new List<string> { launcher, "fg", "jdk", "Ghidra-Headless", memory, vmargs, "ghidra.app.util.headless.AnalyzeHeadless", work, "project" };
        var scriptPath = new[] { "-scriptPath", scripts };
        bool gameBinary = string.Equals(Path.GetFullPath(file.DirectoryName!).TrimEnd(Path.DirectorySeparatorChar),
                                        Path.GetFullPath(Path.Combine(s.Settings.Game, "bin")).TrimEnd(Path.DirectorySeparatorChar),
                                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        bool projectExists = Directory.Exists(Path.Combine(work, "project.rep")) && !fresh;
        var names = Path.Combine(work, "names.tsv");
        if (projectExists)
        {
            cmd.AddRange(["-process", file.Name, "-noanalysis", .. scriptPath]);
            if (gameBinary && range == null)
            {
                // names found since the first run (after decomp match and a new symbol database)
                ExportNames(s, file.Name, names);
                if (File.Exists(names))
                    cmd.AddRange(["-preScript", "ApplyNames.java", names, "update"]);
            }
        }
        else
        {
            // Ghidra's launcher is a batch file and breaks on "Program Files (x86)": import a copy.
            var copy = Path.Combine(work, file.Name);
            if (!File.Exists(copy) || new FileInfo(copy).Length != file.Length)
                File.Copy(binary, copy, true);
            cmd.AddRange(["-import", copy, "-overwrite", .. scriptPath]);
            if (gameBinary)
            {
                ExportNames(s, file.Name, names);
                if (File.Exists(names))
                    cmd.AddRange(["-preScript", "ApplyNames.java", names]);
            }
            else if (file.Length > 50_000_000)
            {
                // no names to apply, but the script also switches off the analyzers a large image cannot afford
                File.WriteAllText(names, "", new UTF8Encoding(false));
                cmd.AddRange(["-preScript", "ApplyNames.java", names]);
            }
        }
        cmd.AddRange(["-postScript", "ExportDecomp.java", output]);
        if (range != null)
        {
            int dash = range.IndexOf('-');
            string first = dash < 0 ? range : range[..dash], last = dash < 0 ? "" : range[(dash + 1)..];
            cmd.Add(first);
            if (last.Length > 0)
                cmd.Add(last);
        }
        cmd.AddRange(["-max-cpu", cpu.ToString(System.Globalization.CultureInfo.InvariantCulture), "-log", log]);
        s.Err.WriteLine("running: " + string.Join(" ", cmd.Select(Quote)));

        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = work,
        };
        if (OperatingSystem.IsWindows())
            psi.FileName = launcher;
        else
        {
            // the release archive does not always keep the executable bit
            psi.FileName = "bash";
            psi.ArgumentList.Add(launcher);
        }
        foreach (var a in cmd.Skip(1))
            psi.ArgumentList.Add(a);
        if (jdk != null)
        {
            psi.Environment["JAVA_HOME"] = jdk;
            psi.Environment["PATH"] = Path.Combine(jdk, "bin") + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? "");
        }
        var started = Stopwatch.StartNew();
        int exit;
        using (var console = new StreamWriter(Path.Combine(work, "console.log"), append: true, new UTF8Encoding(false)))
        {
            console.Write($"\n==== {Py.Now()} {string.Join(" ", cmd)}\n");
            console.Flush();
            using var process = new Process { StartInfo = psi };
            void Write(object sender, DataReceivedEventArgs e)
            {
                if (e.Data == null)
                    return;
                lock (console)
                {
                    console.Write(e.Data);
                    console.Write('\n');
                    console.Flush();
                }
            }
            process.OutputDataReceived += Write;
            process.ErrorDataReceived += Write;
            process.Start();
            // stdin is closed so that a launcher question (for example a missing JDK) fails instead of waiting
            process.StandardInput.Close();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();
            exit = process.ExitCode;
        }
        long seconds = (long)Math.Round(started.Elapsed.TotalSeconds, MidpointRounding.ToEven);
        File.WriteAllText(Path.Combine(work, "last_run.json"),
                          $"{{\"seconds\": {seconds}, \"exit\": {exit}, \"finished\": \"{Py.Now()}\"}}", new UTF8Encoding(false));
        s.Err.WriteLine($"{file.Name}: exit {exit} after {started.Elapsed.TotalSeconds:F0} s; output {output}");
        return exit;
    }
}
