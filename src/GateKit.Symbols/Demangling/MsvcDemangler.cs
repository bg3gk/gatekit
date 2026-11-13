namespace GateKit.Symbols.Demangling;

/// <summary>Flags of <see cref="MsvcDemangler.Undecorate"/>; the values match dbghelp's UNDNAME_* constants.</summary>
[Flags]
public enum UndecorateFlags
{
    /// <summary>Full declaration with every Microsoft keyword (UNDNAME_COMPLETE).</summary>
    Complete = 0x0000,

    /// <summary>Full declaration without __cdecl, __ptr64 and the other Microsoft keywords (UNDNAME_NO_MS_KEYWORDS).</summary>
    NoMsKeywords = 0x0002,

    /// <summary>Qualified name only (UNDNAME_NAME_ONLY).</summary>
    NameOnly = 0x1000,
}

/// <summary>
/// Managed Microsoft Visual C++ name undecorator. Produces the same text as dbghelp's UnDecorateSymbolName,
/// so it can replace that Windows-only call on every platform. Thread-safe: every call works on its own state.
/// </summary>
public static class MsvcDemangler
{
    /// <summary>
    /// Undecorates <paramref name="mangled"/> the way dbghelp's UnDecorateSymbolName does with the same flags.
    /// Returns null where dbghelp fails (empty input). Like dbghelp, a name that cannot be parsed comes back
    /// unchanged, and a name that is not decorated (does not start with '?') is returned as it is.
    /// </summary>
    public static string? Undecorate(string mangled, UndecorateFlags flags)
    {
        ArgumentNullException.ThrowIfNull(mangled);
        if (mangled.Length == 0)
        {
            return null;
        }
        if (mangled[0] != '?' || mangled.StartsWith("??@", StringComparison.Ordinal))
        {
            return mangled;
        }
        return new Undecorator(mangled, flags).Run();
    }
}
