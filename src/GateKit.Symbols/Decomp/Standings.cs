using System.Globalization;

namespace GateKit.Symbols.Decomp;

/// <summary>How the ids of one function stand together.</summary>
internal enum IdStanding
{
    /// <summary>One id, or none.</summary>
    One,
    /// <summary>Several ids for code the linker folded: the committed registry holds them together,
    /// or `registry settle --folded` said so.</summary>
    Folded,
    /// <summary>Several ids, a live claim among them, not settled: a clash (`registry clashes`).</summary>
    Clash,
    /// <summary>Several ids, settled for one of them (`registry settle --keep`).</summary>
    Kept,
}

/// <summary>The one rule for several ids at one function, as `registry clashes` reads it
/// (docs/contracts/cli.md, "The live layer"), for the commands that show them.</summary>
internal static class Standings
{
    /// <summary>The standing of several distinct ids at one function: <paramref name="anyLive"/>
    /// when the live layer claims one of them, <paramref name="settled"/> the address's settlement.</summary>
    public static (IdStanding Standing, string? Keep) Of(bool anyLive, (string? Keep, bool Folded)? settled)
    {
        if (settled is { Folded: true }) return (IdStanding.Folded, null);
        if (settled?.Keep is { } keep) return (IdStanding.Kept, keep);
        return (anyLive ? IdStanding.Clash : IdStanding.Folded, null);
    }

    /// <summary>The line that says how <paramref name="count"/> ids at a function stand; null for one.</summary>
    public static string? Line(IdStanding standing, string? keep, int count, string binary, long rva) => standing switch
    {
        IdStanding.Folded => $"{count} ids share this code (the linker folded them)",
        IdStanding.Kept => $"{count} ids claimed for this code; settled for {keep}",
        IdStanding.Clash => string.Create(CultureInfo.InvariantCulture,
            $"{count} ids claimed for this code: a clash, not settled - `gatekit registry settle {binary}+0x{rva:X} --folded` if the linker folded them, --keep <id> if one is right"),
        _ => null,
    };

    /// <summary>The word for a standing in JSON; null for one id.</summary>
    public static string? Word(IdStanding standing) => standing switch
    {
        IdStanding.Folded => "folded",
        IdStanding.Kept => "settled",
        IdStanding.Clash => "clash",
        _ => null,
    };
}
