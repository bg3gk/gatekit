using System.Text;
using System.Text.RegularExpressions;

namespace GateKit.Symbols.Decomp;

/// <summary>
/// Whether the Ghidra scripts of one launch ran. Ghidra ends with 0 even when a script did not
/// compile: it prints a javac error or "The class could not be found" and skips that script and
/// the ones after it. So every script in tools/ghidra prints one last line,
/// <c>gatekit-script: &lt;Name&gt; done key=value ...</c>, and a launch counts only when each script
/// that was asked for printed it and the log of that launch shows no load or compile error.
/// </summary>
internal static partial class ScriptCheck
{
    /// <summary>What a script prints last when it ran to the end.</summary>
    public const string Marker = "gatekit-script: ";

    [GeneratedRegex(@"\b(\w+)\.java:\d+: error:")]
    private static partial Regex CompileError();

    [GeneratedRegex(@"SCRIPT ERROR:\s*(\w+)\.java")]
    private static partial Regex ScriptError();

    [GeneratedRegex(@"GhidraScriptLoadException|The class could not be found|Failed to compile|Unable to compile")]
    private static partial Regex LoadError();

    /// <summary>The scripts asked for in a command line of Ghidra (-preScript / -postScript), in order.</summary>
    public static List<string> Asked(IReadOnlyList<string> arguments)
    {
        var scripts = new List<string>();
        for (int i = 0; i + 1 < arguments.Count; i++)
            if (arguments[i] is "-preScript" or "-postScript")
                scripts.Add(Path.GetFileNameWithoutExtension(arguments[i + 1]));
        return scripts;
    }

    /// <summary>What went wrong with the scripts in the console text of one launch, one sentence
    /// each; empty when every script asked for loaded and printed its last line.</summary>
    public static List<string> Problems(string console, IEnumerable<string> asked)
    {
        var problems = new List<string>();
        var named = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in console.Split('\n'))
        {
            string? script = null;
            if (CompileError().Match(line) is { Success: true } c)
                script = c.Groups[1].Value;
            else if (ScriptError().Match(line) is { Success: true } e)
                script = e.Groups[1].Value;
            else if (LoadError().IsMatch(line))
                script = "";
            if (script == null)
                continue;
            if (named.Add(script))
                problems.Add(script.Length > 0
                    ? $"{script}.java did not load or compile: {Short(line)}"
                    : $"a script did not load or compile: {Short(line)}");
        }
        foreach (var script in asked)
            if (!named.Contains(script) && !Done(console, script))
                problems.Add($"{script}.java never printed its last line (\"{Marker}{script} done\"): it was skipped or stopped early");
        return problems;
    }

    /// <summary>Whether the script printed its last line in this console text.</summary>
    public static bool Done(string console, string script) =>
        console.Contains(Marker + script + " done", StringComparison.Ordinal);

    static string Short(string line)
    {
        line = line.Trim();
        return line.Length > 200 ? line[..200] + "..." : line;
    }

    /// <summary>The console text a launch added to <paramref name="log"/> after <paramref name="from"/> bytes.</summary>
    public static string Since(string log, long from)
    {
        if (!File.Exists(log))
            return "";
        using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (from > stream.Length)
            from = 0;
        stream.Seek(from, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>The length of the log before a launch (0 when there is none yet).</summary>
    public static long Length(string log) => File.Exists(log) ? new FileInfo(log).Length : 0;
}
