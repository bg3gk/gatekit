using GateKit.Platform;

namespace GateKit.Stand;

/// <summary>
/// Checks that this machine has what the stand needs, and says how to fix what it has not.
/// It changes nothing: every check is a look at the file system or the settings.
/// </summary>
public static class Doctor
{
    public enum Verdict { Good, Warn, Bad }

    public sealed record Check(string Name, Verdict Verdict, string Detail, string? Fix = null);

    /// <summary>Runs every check, most fundamental first. Settings that cannot be resolved are a check too.</summary>
    public static IReadOnlyList<Check> Run(IGamePlatform platform, string? instance = null,
        Func<string, string?>? environment = null, string? workingDirectory = null)
    {
        var checks = new List<Check>();
        checks.Add(new Check("platform", Verdict.Good, platform.Name));

        StandSettings settings;
        try
        {
            settings = StandSettings.Resolve(platform, environment, workingDirectory, instance);
        }
        catch (StandException e)
        {
            checks.Add(new Check("settings", Verdict.Bad, e.Message,
                "set BG3_GAME to the game's folder (the one holding bin and Data), or write it into gatekit.json"));
            return checks;
        }

        checks.Add(new Check("settings", Verdict.Good,
            settings.ConfigFile is null ? "environment and defaults; no gatekit.json" : $"read {settings.ConfigFile}"));

        var layout = new StandLayout(settings, platform);

        checks.Add(File.Exists(layout.Exe)
            ? new Check("game", Verdict.Good, layout.Exe)
            : new Check("game", Verdict.Bad, $"{layout.Exe} is missing",
                "set BG3_GAME to the folder holding bin and Data, or GATEKIT_EXE if your executable has another name"));

        checks.Add(Directory.Exists(layout.Profile)
            ? new Check("profile", Verdict.Good, layout.Profile)
            : new Check("profile", Verdict.Warn, $"{layout.Profile} does not exist yet",
                "start the game once by hand, or set GATEKIT_PROFILE"));

        // Script Extender: our own build if one is configured, otherwise whatever the game loads.
        if (settings.ScriptExtender is { } build)
        {
            var dll = Path.Combine(build, "BG3ScriptExtender.dll");
            checks.Add(File.Exists(dll)
                ? new Check("script extender", Verdict.Good, $"own build: {dll}")
                : new Check("script extender", Verdict.Bad, $"{dll} is missing",
                    "run `gatekit se install`, or point GATEKIT_SE at a folder holding BG3ScriptExtender.dll, or set it to \"stock\""));
        }
        else
        {
            var loader = Path.Combine(layout.Bin, "DWrite.dll");
            checks.Add(File.Exists(loader)
                ? new Check("script extender", Verdict.Warn, "the stock extender is installed in the game",
                    "the bridge and Weave need GateKit's build: run `gatekit se install` or set GATEKIT_SE")
                : new Check("script extender", Verdict.Bad, "no extender in the game's bin folder",
                    "install Script Extender, or run `gatekit se install` to use GateKit's build"));
        }

        checks.Add(settings.BridgeSource is { } bridge && Directory.Exists(bridge)
            ? new Check("bridge mods", Verdict.Good, bridge)
            : new Check("bridge mods", Verdict.Bad, settings.BridgeSource ?? "not found",
                "run gatekit from the repository, or set GATEKIT_BRIDGE to its mods folder"));

        checks.Add(settings.Packer == PackerKind.Divine
            ? settings.Divine is { } divine && File.Exists(divine)
                ? new Check("packer", Verdict.Good, $"Divine: {divine}")
                : new Check("packer", Verdict.Bad, $"GATEKIT_PACKER asks for Divine, and {settings.Divine ?? "none"} is not there",
                    "set GATEKIT_DIVINE to LSLib's Divine.exe (it needs a .NET 8 runtime; GATEKIT_DOTNET_ROOT if it is not installed), "
                    + "or unset GATEKIT_PACKER to pack with the built-in LSLib")
            : new Check("packer", Verdict.Good, "built in (LSLib); nothing to install"));

        checks.Add(Writable(settings.StateRoot, out var stateError)
            ? new Check("state folder", Verdict.Good, settings.StateRoot)
            : new Check("state folder", Verdict.Bad, $"{settings.StateRoot}: {stateError}", "set GATEKIT_STATE to a folder you can write"));

        checks.Add(Writable(settings.InstancesRoot, out var instancesError)
            ? new Check("instances", Verdict.Good, settings.InstancesRoot)
            : new Check("instances", Verdict.Warn, $"{settings.InstancesRoot}: {instancesError}",
                "set GATEKIT_INSTANCES to a folder you can write, on the same disk as the game so clones can use hard links"));

        // The offline databases: present or not, and built here or fetched from the published release.
        foreach (var kind in GateKit.Release.DatabaseKind.All)
        {
            var path = kind.DefaultPath;
            checks.Add(File.Exists(path)
                ? new Check($"{kind.Id} database", Verdict.Good, GateKit.Release.DatabaseInstall.Describe(kind, path))
                : new Check($"{kind.Id} database", Verdict.Warn, $"{path} is not there",
                    $"run `{kind.BuildCommand}` to make it here, or `{kind.InstallCommand}` to fetch the published one"));
        }

        foreach (var (capability, can) in new (string, bool)[]
                 { ("key", platform.CanPostKeys), ("shot", platform.CanCaptureWindow), ("console", platform.CanReadConsole) })
            checks.Add(can
                ? new Check(capability, Verdict.Good, "supported")
                : new Check(capability, Verdict.Warn, platform.WhyNot(capability)));

        checks.Add(Deck(platform, settings));
        checks.Add(Firewall(platform, settings));

        return checks;
    }

    /// <summary>Whether a deck is holding the game windows. Not having one is no fault: the games
    /// then stand in windows of their own, as they always did.</summary>
    static Check Deck(IGamePlatform platform, StandSettings settings)
    {
        if (!platform.CanHostWindows) return new Check("deck", Verdict.Warn, platform.WhyNot("deck"));
        return new DeckRecord(settings.StateRoot).Running() is { } deck
            ? new Check("deck", Verdict.Good, $"running, pid {deck.Pid}")
            : new Check("deck", Verdict.Good, "none; every game gets a window of its own (`gatekit deck` opens one)");
    }

    /// <summary>Whether the firewall already lets the game listen. GateKit never writes a firewall
    /// rule - that is the user's to do, in an elevated prompt - so this only ever looks and tells.
    ///
    /// A rule is held against the executable's path, and a copy is a folder of its own: the
    /// installed game being allowed says nothing about a copy. Every copy is checked, because the
    /// dialog this is here to prevent arrives once per copy that has no rule.</summary>
    static Check Firewall(IGamePlatform platform, StandSettings settings)
    {
        var report = FirewallReport.Read(settings, platform);
        if (!report.Readable) return new Check("firewall", Verdict.Good, "nothing to check on this system");
        if (report.FixCommand is null)
            return new Check("firewall", Verdict.Good, $"a rule allows every copy ({report.Copies.Count})");
        var names = string.Join(", ", report.Missing.Select(c => c.Name));
        return new Check("firewall", Verdict.Warn,
            $"no rule for {report.Missing.Count} of {report.Copies.Count} copies ({names}): Windows asks about each, once",
            "`gatekit firewall` prints the one elevated line that covers them all "
            + "(GateKit will not change a firewall rule for you)");
    }

    static bool Writable(string folder, out string error)
    {
        error = "";
        try
        {
            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, ".gatekit-probe");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = e.Message;
            return false;
        }
    }

    /// <summary>Prints the checks; returns the exit code (1 if anything is Bad).</summary>
    public static int Report(IReadOnlyList<Check> checks, TextWriter output)
    {
        var width = checks.Max(c => c.Name.Length);
        foreach (var check in checks)
        {
            var mark = check.Verdict switch { Verdict.Good => "ok  ", Verdict.Warn => "warn", _ => "FAIL" };
            output.WriteLine($"  {mark}  {check.Name.PadRight(width)}  {check.Detail}");
            if (check.Fix is { } fix) output.WriteLine($"        {new string(' ', width)}  -> {fix}");
        }
        var bad = checks.Count(c => c.Verdict == Verdict.Bad);
        var warn = checks.Count(c => c.Verdict == Verdict.Warn);
        output.WriteLine(bad > 0
            ? $"\n{bad} must be fixed before the stand can run" + (warn > 0 ? $", {warn} to look at" : "")
            : warn > 0 ? $"\nready, {warn} to look at" : "\nready");
        return bad > 0 ? 1 : 0;
    }
}
