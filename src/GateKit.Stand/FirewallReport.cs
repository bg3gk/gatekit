using GateKit.Platform;

namespace GateKit.Stand;

/// <summary>
/// Which game copies the firewall already lets listen, and what single command would cover the
/// rest.
///
/// Windows asks "allow this program on the network?" the first time a program listens, and it
/// asks per executable path - not per program. A stand copy is a folder of its own, so every new
/// copy is a new path, and a dialog nobody asked for arrives in the middle of whatever the person
/// was doing, elevated, stealing the keyboard. That is the thing the deck exists to prevent, and
/// a firewall rule is the only way to prevent this one.
///
/// GateKit never writes a firewall rule itself. A rule is a change to the machine's security, it
/// needs elevation, and the person makes it knowingly or not at all. What this does is gather
/// every copy that still lacks one and hand over one line to run, so that the answer is given
/// once rather than once per copy.
/// </summary>
public sealed record FirewallReport(IReadOnlyList<FirewallReport.Copy> Copies, bool Readable)
{
    /// <summary>A game copy's executable, and whether a rule already allows it.</summary>
    public sealed record Copy(string Name, string Executable, bool? Allowed);

    /// <summary>The copies no rule covers yet.</summary>
    public IReadOnlyList<Copy> Missing => [.. Copies.Where(c => c.Allowed == false)];

    /// <summary>
    /// The one elevated line that covers every copy that lacks a rule, or null when none does.
    /// One rule per executable, named after the copy, so that `instances destroy` leaves a rule
    /// whose name says what it was for and the person can remove it.
    /// </summary>
    public string? FixCommand
    {
        get
        {
            if (Missing.Count == 0) return null;
            var rules = Missing.Select(c =>
                $"New-NetFirewallRule -DisplayName \"Baldur's Gate 3 (GateKit: {c.Name})\" -Direction Inbound "
                + $"-Program \"{c.Executable}\" -Action Allow -Profile Any");
            return string.Join("; ", rules);
        }
    }

    /// <summary>Reads the rules for the installed game and for every copy that exists.</summary>
    public static FirewallReport Read(StandSettings settings, IGamePlatform platform)
    {
        var copies = new List<Copy>();
        var readable = true;
        Add("(installed game)", Path.Combine(settings.GameFolder, "bin", settings.ExeName));
        foreach (var name in Instance.Names(settings.InstancesRoot))
        {
            var one = new Instance(name, settings, platform);
            Add(name, Path.Combine(one.Game, "bin", settings.ExeName));
        }
        return new FirewallReport(copies, readable);

        void Add(string name, string executable)
        {
            var allowed = platform.FirewallAllows(executable);
            if (allowed is null) readable = false;
            copies.Add(new Copy(name, executable, allowed));
        }
    }

    /// <summary>The report as the command line prints it.</summary>
    public IEnumerable<string> Lines()
    {
        if (!Readable)
        {
            yield return "the firewall's rules cannot be read on this system; nothing to report";
            yield break;
        }
        foreach (var copy in Copies)
            yield return $"{(copy.Allowed == true ? "allowed" : "asks   "),-8} {copy.Name,-16} {copy.Executable}";
        yield return "";
        if (FixCommand is null)
        {
            yield return "every copy is allowed: Windows will not ask about any of them.";
            yield break;
        }
        yield return $"{Missing.Count} cop{(Missing.Count == 1 ? "y" : "ies")} will make Windows ask. "
            + "GateKit does not write firewall rules; run this once in an elevated PowerShell:";
        yield return "";
        yield return FixCommand;
    }
}
