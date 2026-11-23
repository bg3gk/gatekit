namespace GateKit.Copies;

/// <summary>What a start that has not heard from the bridge has come to, read from what the game
/// left behind: whether its process lives, and what Script Extender's runtime log says.</summary>
public enum StartVerdict
{
    /// <summary>Nothing says the start went wrong yet: the game is still getting there.</summary>
    Loading,
    /// <summary>The game process is gone.</summary>
    Exited,
    /// <summary>The game runs and Script Extender wrote no log: the extender did not load.</summary>
    NoExtender,
    /// <summary>The game loaded its module and Script Extender ran no Lua of the bridge: the game
    /// started with the bridge's mod off. A plain restart brings the mods back.</summary>
    ModsOff,
    /// <summary>Script Extender loaded the bridge's Lua and the bridge never reported.</summary>
    Silent,
}

/// <summary>A reading of a start without the bridge: the verdict and the sentence that says it.</summary>
public sealed record StartReading(StartVerdict Verdict, string Text)
{
    /// <summary>Whether a second start may help: only when the game turned the mods off.</summary>
    public bool Restart => Verdict == StartVerdict.ModsOff;
}

/// <summary>Reads a start the bridge has not answered (docs/contracts/cli.md, "up", step 16).</summary>
public static class StartCheck
{
    /// <summary>The line Script Extender's client writes once the game has loaded its module (after
    /// the mods' bootstrap scripts, when the mods are on).</summary>
    public const string ModuleLoaded = "OnGameStateChanged(): Loaded module";

    /// <summary>Reads a start from <paramref name="log"/> (the runtime log's text; null when there is
    /// none), whether the game process still runs, how long it was waited for, and the bridge's mod
    /// folder.</summary>
    public static StartReading Read(string? log, bool running, TimeSpan waited, string bridgeFolder, string logFolder)
    {
        var seconds = $"{waited.TotalSeconds:0} s";
        if (!running)
            return new(StartVerdict.Exited, $"the game process ended {seconds} after it started, before the bridge reported"
                + (log is null ? $" and before Script Extender wrote a log in {logFolder}" : $"; the last line of Script Extender's log: {LastLine(log)}")
                + " (`gatekit crashes` lists a dump if it crashed)");
        if (log is null)
            return new(StartVerdict.NoExtender, $"the game ran {seconds} and Script Extender wrote no log in {logFolder}: the extender did not load "
                + "(`gatekit status` names the build this copy loads; `gatekit se install` gives it GateKit's)");
        var bootstrap = $"Mods/{bridgeFolder}/ScriptExtender/Lua/Bootstrap";
        if (log.Contains(bootstrap, StringComparison.Ordinal))
            return new(StartVerdict.Silent, $"Script Extender loaded the bridge's Lua and the bridge did not report in {seconds}; "
                + $"the last line of its log: {LastLine(log)} (`gatekit logs` shows the rest)");
        if (log.Contains(ModuleLoaded, StringComparison.Ordinal))
            return new(StartVerdict.ModsOff, $"the game loaded its module with the bridge's mod off: Script Extender ran no Lua of {bridgeFolder}");
        return new(StartVerdict.Loading, $"the game ran {seconds} without loading its module; the last line of Script Extender's log: {LastLine(log)} "
            + "(a busy machine slows a start; GATEKIT_START_TIMEOUT gives it longer)");
    }

    static string LastLine(string log) =>
        log.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) is { } line ? $"\"{line}\"" : "(empty)";
}
