using GateKit.Stand;

namespace GateKit.Deck;

/// <summary>
/// Which copies a deck may take hold of.
///
/// A deck does three things to a game: it takes its window out of the desktop, it caps how fast it
/// draws, and it mutes it. Doing all three to a copy somebody else started - another person,
/// another session, a game being played - is not something to do by accident, and a person who
/// opens a deck for an unrelated reason should see nothing happen to the game they were playing.
///
/// So the rule is: <b>a deck takes what this stand started, and nothing else.</b> Every stand
/// keeps its own records of the instances it made (<see cref="Instance.Mine"/>), which is what
/// separates them; the instance folders themselves are shared by everyone on the machine and say
/// nothing about who started what.
///
/// Two ways past it, and both of them are somebody asking: <c>gatekit deck --all</c>, and naming a
/// copy with <c>window maximize|mirror|adopt</c>, which is as plain an ask as there is. Everything
/// else is listed by <c>window list</c> and left alone.
/// </summary>
/// <param name="All">Take hold of every copy on the machine (<c>--all</c>).</param>
public sealed record Adoption(bool All = false)
{
    /// <summary>Copies named by hand although this stand did not start them. Asking for a tile is
    /// the asking; <c>window release NAME</c> takes the name off again.</summary>
    public IReadOnlyCollection<string> Invited { get; init; } = [];

    /// <summary>The copies the deck may act on, in the order they came.</summary>
    public IReadOnlyList<GameCopy> Ours(IReadOnlyList<GameCopy> copies) =>
        All ? copies : [.. copies.Where(Allowed)];

    /// <summary>The copies the deck is leaving alone, which is what it says out loud.</summary>
    public IReadOnlyList<GameCopy> NotOurs(IReadOnlyList<GameCopy> copies) =>
        All ? [] : [.. copies.Where(c => !Allowed(c))];

    bool Allowed(GameCopy copy) =>
        copy.Mine || Invited.Contains(copy.Instance, StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads the setting: "mine" (the default) or "all".</summary>
    public static Adoption Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "mine" or "own" => new Adoption(),
        "all" or "any" => new Adoption(true),
        _ => throw new StandException($"a deck takes \"mine\" or \"all\"; \"{value}\" is neither"),
    };

    public override string ToString() => All
        ? "every copy on this machine, including ones this stand did not start"
        : "the copies this stand started; anything else is listed and left alone";
}
