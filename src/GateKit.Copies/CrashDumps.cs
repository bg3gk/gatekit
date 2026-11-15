using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

namespace GateKit.Copies;

/// <summary>
/// The crash dumps of a game copy. GateKit's Script Extender writes a dump when the game dies -
/// <c>&lt;pid&gt;-&lt;yyyymmdd&gt;-&lt;hhmmss&gt;.dmp</c> with a <c>.txt</c> beside it that names the
/// exception, the faulting address as <c>module+0xRVA</c> and the thread - into the folder its
/// setting <c>CrashDumpDirectory</c> names; `gatekit` points it at the copy's <c>crash-dumps</c>
/// folder and switches the reporter window off (<c>LaunchCrashReporter</c>). This reads them back and
/// names the function at the address with the decompiler's database (<c>gatekit decomp at</c>).
/// A dump of a hang is not written: the extender's surface has no call that writes a dump of a
/// running game.
/// </summary>
public static class CrashDumps
{
    /// <summary>One dump: its files, what the extender wrote of it, and the function's name when known.</summary>
    public sealed record Dump(string Path, DateTime Written, string? Exception, string? Address, string? Absolute, string? Thread)
    {
        /// <summary>The function at <see cref="Address"/>, from `decomp at` (null: not asked, or unknown).</summary>
        public string? Function { get; init; }

        /// <summary>Why no function is named: which module the address is in and what knows it (null when one is named).</summary>
        public string? Unnamed { get; init; }

        public JsonObject ToJson() => new()
        {
            ["dump"] = System.IO.Path.ChangeExtension(Path, ".dmp"), ["written"] = Written.ToString("o", CultureInfo.InvariantCulture),
            ["exception"] = Exception, ["address"] = Address, ["absolute"] = Absolute, ["thread"] = Thread, ["function"] = Function,
            ["unnamed"] = Unnamed,
        };

        public string Line() => $"{Written.ToLocalTime():yyyy-MM-dd HH:mm:ss}  {Exception ?? "?"} at {Address ?? "?"}"
            + (Function is { } f ? "  " + f : "") + "  (" + System.IO.Path.ChangeExtension(Path, ".dmp") + ")"
            + (Unnamed is { } why ? "\n    no function named: " + why : "");
    }

    /// <summary>Script Extender's own module: the decompiler's database holds the game's binaries, never it.</summary>
    public const string ExtenderModule = "BG3ScriptExtender.dll";

    /// <summary>Why no function is named at <paramref name="address"/> (module+0xRVA): the module, and that
    /// no database of GateKit holds it, with where its symbols are when <paramref name="pdbs"/> names a .pdb
    /// that exists (the extender's build folder, the copy's bin); else what `decomp at` said.</summary>
    public static string WhyUnnamed(string address, string? decompSaid, IEnumerable<string> pdbs)
    {
        var plus = address.IndexOf('+', StringComparison.Ordinal);
        var module = plus > 0 ? address[..plus] : address;
        if (module.Equals(ExtenderModule, StringComparison.OrdinalIgnoreCase))
        {
            var pdb = pdbs.FirstOrDefault(File.Exists);
            return $"the address is inside Script Extender's own module ({module}); GateKit keeps no database of it (`decomp` holds the game's binaries)"
                + (pdb is not null
                    ? $"; its symbols are {pdb}: a debugger opened on the .dmp with that folder on its symbol path names the function"
                    : "; no .pdb of this build was found in its build folder or the copy's bin");
        }
        return decompSaid is { Length: > 0 } said
            ? $"`decomp at {address}` named none: {said}"
            : $"`decomp at {address}` named none: no database of GateKit holds {module}, or it is not decompiled yet";
    }

    /// <summary>The folder the copy's dumps go to.</summary>
    public static string Folder(CopyLayout layout) => System.IO.Path.Combine(layout.State, "crash-dumps");

    /// <summary>The dumps in <paramref name="folder"/>, newest first; with <paramref name="since"/>, only those written after it.</summary>
    public static List<Dump> Read(string folder, DateTime? since = null)
    {
        if (!Directory.Exists(folder)) return [];
        return Directory.EnumerateFiles(folder, "*.txt")
            .Select(path => (path, written: File.GetLastWriteTimeUtc(path)))
            .Where(f => since is null || f.written > since.Value.ToUniversalTime())
            .OrderByDescending(f => f.written)
            .Select(f => Parse(f.path, File.ReadAllText(f.path), f.written))
            .ToList();
    }

    /// <summary>The extender's text beside a dump: "Key: value" lines.</summary>
    public static Dump Parse(string path, string text, DateTime written)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n'))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0) continue;
            fields[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        return new Dump(path, written, fields.GetValueOrDefault("Exception"), fields.GetValueOrDefault("Address"),
            fields.GetValueOrDefault("Absolute"), fields.GetValueOrDefault("Thread"));
    }

    /// <summary>The .pdb files where a copy's Script Extender's symbols may be: beside the build it
    /// loads (its build folder) and in the copy's bin.</summary>
    public static IReadOnlyList<string> ExtenderPdbs(CopyLayout layout)
    {
        var list = new List<string>();
        if (new ScriptExtenderSwitch(layout.Bin, layout.State).Current().Source is { } dll)
            list.Add(System.IO.Path.ChangeExtension(dll, ".pdb"));
        list.Add(System.IO.Path.Combine(layout.Bin, "BG3ScriptExtender.pdb"));
        return list;
    }

    /// <summary>The function at a dump's address, asked of this program's own `decomp at`; when none
    /// is named, <see cref="Dump.Unnamed"/> says why (<see cref="WhyUnnamed"/>).</summary>
    public static Dump Named(Dump dump, IEnumerable<string> pdbs)
    {
        if (dump.Address is not { } address) return dump;
        string? said = null;
        if (Environment.ProcessPath is { } self && !IsExtender(address))
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(self, ["decomp", "at", address])
                {
                    RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                });
                if (process is not null)
                {
                    var error = process.StandardError.ReadToEndAsync();
                    var output = process.StandardOutput.ReadToEnd();
                    if (process.WaitForExit(30000))
                    {
                        // `decomp at` says the function by the decompiler's and the symbols' names, a line each
                        var lines = output.Split('\n').Select(l => l.Trim()).ToList();
                        string? Said(string label) => lines.FirstOrDefault(l => l.StartsWith(label, StringComparison.Ordinal))?[label.Length..].Trim() is { Length: > 0 } s
                            && !s.StartsWith('(') ? s : null;
                        var name = Said("named:") ?? Said("symbols:") ?? Said("decompiler:");
                        if (process.ExitCode == 0 && name is not null) return dump with { Function = name };
                        said = error.Result.Trim().Split('\n').FirstOrDefault()?.Trim();
                    }
                    else
                    {
                        try { process.Kill(); } catch (InvalidOperationException) { }
                        said = "it did not answer in 30 s";
                    }
                }
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { said = e.Message; }
        }
        return dump with { Unnamed = WhyUnnamed(address, said, pdbs) };
    }

    static bool IsExtender(string address) => address.StartsWith(ExtenderModule + "+", StringComparison.OrdinalIgnoreCase);
}
