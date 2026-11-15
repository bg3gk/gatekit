using System.CommandLine;

namespace GateKit.Symbols;

/// <summary>
/// The commands of this project - `symbols`, `decomp`, `registry`, `refs` - read the home's
/// databases and the repository, never a game copy. The program's `--instance` is an option of its
/// root and parses before any command, so it is taken here too; given, it is refused, as the CLI
/// refuses any option a command cannot honour, rather than read as if it chose something.
/// GATEKIT_INSTANCE in the environment is left alone: it names a copy for the commands that use one.
/// </summary>
public static class CopyFree
{
    /// <summary>The sentence that refuses an explicit `--instance` on <paramref name="command"/>; null when none was given.</summary>
    public static string? Refusal(ParseResult parse, string command)
    {
        string? given = null;
        try { given = parse.GetValue<string?>("--instance"); }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException) { }
        return given is { Length: > 0 }
            ? $"`gatekit {command}` reads no game copy: --instance {given} means nothing here; leave it out (nothing was done)"
            : null;
    }

    /// <summary>Writes the refusal to the invocation's error and gives the exit code 1; 0 when the command may run.</summary>
    public static int Refuse(ParseResult parse, string command)
    {
        if (Refusal(parse, command) is not { } refusal) return 0;
        parse.InvocationConfiguration.Error.WriteLine(refusal);
        return 1;
    }
}
