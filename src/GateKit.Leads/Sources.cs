namespace GateKit.Leads;

/// <summary>A failure the user can act on: printed as it is, exit code 1.</summary>
public sealed class LeadsException(string message) : Exception(message);

/// <summary>A function that adds a component. <paramref name="System"/>, <paramref name="Site"/> and
/// <paramref name="Depth"/> are known when a system's per-frame update reaches it (the facts of
/// research/coverage/systems); <paramref name="Confidence"/> is the helper classifier's, or "name"
/// when the function was found by its name in the macOS build.</summary>
public sealed record Creator(string? System, string Function, string Binary, string? Site, int? Depth, string Confidence, string? Name = null)
{
    /// <summary>The function's address in its binary, or -1.</summary>
    public long Rva => Function.LastIndexOf("+0x", StringComparison.Ordinal) is int at and >= 0
        && long.TryParse(Function.AsSpan(at + 3), global::System.Globalization.NumberStyles.HexNumber, null, out var v) ? v : -1;
}

/// <summary>An entry of the game's stats data whose field carries a call (a boost or a functor).</summary>
public sealed record StatUse(string Name, string Kind, string Field, string? Mod);

/// <summary>The game's code as the leads need it: who adds a type, who calls a function, what a
/// function is called, which system's update a function is, who uses a system's index.</summary>
public interface ICodeIndex
{
    /// <summary>The functions of the shipping Windows build that add the type through a classified
    /// helper (no system known), and functions of the macOS build named as adding it.</summary>
    IReadOnlyList<Creator> Adders(string type);

    /// <summary>The functions that call <paramref name="rva"/> directly.</summary>
    IReadOnlyList<long> Callers(string binary, long rva);

    /// <summary>A function's name: its own, else the class whose virtual table holds it (Class::slot N), else null.</summary>
    string? NameOf(string binary, long rva);

    /// <summary>The system whose per-frame update <paramref name="rva"/> is, or null.</summary>
    string? UpdateOf(string binary, long rva);

    /// <summary>The functions that read a system's index (who talks to the system), or empty.</summary>
    IReadOnlyList<long> SystemIndexUsers(string binary, string system);

    /// <summary>How many functions read the type's own index in the shipping Windows build; -1 when unknown.</summary>
    int IndexUsers(string type);

    /// <summary>The condition under which a system's update adds the type in <paramref name="function"/>
    /// (<paramref name="occurrence"/>: the add's place among that function's calls of the same helper),
    /// read off the pseudo-code; null when none can be said.</summary>
    string? CreationCondition(string binary, string system, long function, string type, int occurrence) => null;
}

/// <summary>The game's data as the leads need it.</summary>
public interface IDataIndex
{
    /// <summary>Entries whose field calls <paramref name="call"/>( - a boost or a functor - shipping ones first.</summary>
    IReadOnlyList<StatUse> Carrying(string call, int limit, bool functor = false);

    /// <summary>How many entries carry the call (all, including the developers').</summary>
    int CountCarrying(string call, bool functor = false);

    /// <summary>The names of the calls (functors, boosts) in the data whose name holds <paramref name="word"/>, most used first.</summary>
    IReadOnlyList<string> CallsHolding(string word, int limit, bool functor = false);

    /// <summary>The Osiris functions the engine offers: (name, kind).</summary>
    IReadOnlyList<(string Name, string Kind)> OsirisFunctions();

    /// <summary>How many times the story uses an Osiris function.</summary>
    int OsirisUses(string function);

    /// <summary>The English display name of a stats entry, or null.</summary>
    string? DisplayName(string stat);
}

/// <summary>bg3.wiki, asked only for an obvious page: a search result whose title is the name.</summary>
public interface IWikiTitles
{
    Task<string?> PageFor(string name, CancellationToken cancel = default);
}
