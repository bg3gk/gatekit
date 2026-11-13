using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GateKit.Bridge;
using GateKit.Platform;

namespace GateKit.Stand;

/// <summary>What `up` starts.</summary>
/// <param name="Level">MODULE:LEVEL: a fresh test game in that level, no menu, no save; only the base game, the bridge and <paramref name="Mods"/>.</param>
/// <param name="Save">A save folder name under Savegames/Story to load instead of the last one.</param>
/// <param name="Mods">Mod folders to keep besides the base game; null = the player's own list (unless a level is given).</param>
/// <param name="StoryLog">Also record Script Extender's logs, the Osiris trace included (`logs --story`).</param>
/// <param name="GameArgs">Extra raw game arguments.</param>
public sealed record UpOptions(string? Level = null, string? Save = null, IReadOnlyList<string>? Mods = null,
    bool StoryLog = false, IReadOnlyList<string>? GameArgs = null);

/// <summary>
/// The test stand: starts the game directly (no launcher), talks to it through the bridge mod,
/// and puts the player's saves and mod list back afterwards. A port of the old Python stand;
/// docs/contracts/cli.md describes every command and the deliberate differences.
/// </summary>
public sealed class Stand
{
    const string FormerBridge = "Gwynerva_DevKit";

    public Stand(StandSettings settings, IGamePlatform platform, TextWriter? output = null, IModPacker? packer = null)
    {
        Settings = settings;
        Platform = platform;
        Layout = new StandLayout(settings, platform);
        Out = output ?? Console.Out;
        Packer = packer ?? (settings.Packer == PackerKind.Divine
            ? new DivinePacker(settings.Divine, settings.DotnetRoot)
            : new LSLibPacker());
    }

    public StandSettings Settings { get; }
    public IGamePlatform Platform { get; }
    public StandLayout Layout { get; }
    public Instance? Instance => Layout.Instance;
    public TextWriter Out { get; }
    public IModPacker Packer { get; }

    ScriptExtenderSwitch SeSwitch => new(Layout.Bin, Layout.State);

    // --- processes ---

    bool SameFolder(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), Platform.PathComparison);

    /// <summary>The processes of this stand's game: started from its bin. Other instances' games
    /// are never seen, awaited or killed.</summary>
    public IReadOnlyList<int> GamePids() =>
        Platform.GameProcesses().Where(p => SameFolder(Path.GetDirectoryName(p.ExecutablePath)!, Layout.Bin)).Select(p => p.Pid).ToList();

    public GameSession Session() => new(Layout, () => GamePids().Count > 0, Out);

    /// <summary>How loud this instance's game is (`gatekit sound`, the bridge's GK.Sound).</summary>
    public SoundControl Sound() => new(Session());

    public void KillGame()
    {
        foreach (var pid in GamePids()) Platform.Kill(pid);
        var watch = Stopwatch.StartNew();
        while (GamePids().Count > 0 && watch.Elapsed < TimeSpan.FromSeconds(20)) Thread.Sleep(500);
        ClearCrashMarker();
    }

    /// <summary>The game leaves this folder behind when it does not exit by itself (the stand always
    /// kills it) and takes it on the next start as "a mod crashed": every mod off, a key to press.</summary>
    void ClearCrashMarker() => FileTree.TryRemoveTree(Layout.CrashMarker);

    // --- build ---

    IReadOnlyList<BridgeMod>? _bridgeMods;

    /// <summary>The mods every test game gets (Weave, then the GateKit bridge), from GATEKIT_BRIDGE.</summary>
    public IReadOnlyList<BridgeMod> BridgeMods => _bridgeMods ??= Settings.BridgeSource is { } source
        ? GateKit.Stand.BridgeMods.Find(source)
        : throw new StandException("no bridge source: set GATEKIT_BRIDGE to the repository's mods folder");

    string BuiltPak(BridgeMod mod) => Path.Combine(Layout.State, "build", mod.Pak);
    string InstalledPak(BridgeMod mod) => Path.Combine(Layout.Mods, mod.Pak);

    /// <summary>Packs the bridge mods and installs them into the profile; returns the installed paks.</summary>
    public IReadOnlyList<string> Build()
    {
        if (GamePids().Count > 0) throw new StandException("the game is running and holds the mod files open; run `down` first");
        var installed = new List<string>();
        foreach (var mod in BridgeMods)
        {
            Packer.Pack(mod.Root, BuiltPak(mod));
            FileTree.CopyFile(BuiltPak(mod), InstalledPak(mod));
            installed.Add(InstalledPak(mod));
        }
        return installed;
    }

    bool BridgePakStale() => BridgeMods.Any(mod =>
    {
        var pak = InstalledPak(mod);
        if (!File.Exists(pak)) return true;
        var installed = File.GetLastWriteTimeUtc(pak);
        return Directory.EnumerateFiles(Path.Combine(mod.Root, "Mods", mod.Folder), "*", SearchOption.AllDirectories)
            .Any(f => File.GetLastWriteTimeUtc(f) > installed);
    });

    // --- up ---

    /// <summary>Starts the game and waits until it is playable (the client reaches Running).</summary>
    public async Task<GameSession> UpAsync(UpOptions options, CancellationToken cancel = default)
    {
        if (GamePids().Count > 0) throw new StandException("the game is already running; run `down` first");
        if (Instance is not null) PrepareInstance(options.Save, allSaves: options.Level is null);
        RetireFormerBridge();
        // A start runs the bridge as built: rebuilt whenever its sources are newer than the installed pak.
        if (BridgePakStale())
        {
            Build();
            Out.WriteLine("bridge pak rebuilt (its sources were newer than the installed pak)");
        }
        var backup = new ProfileBackup(Layout, Platform);
        if (Instance is null) backup.Save();

        List<string>? extra = options.GameArgs is { Count: > 0 } given ? [.. given] : null;
        if (options.Save is { } save)
        {
            if (!Directory.Exists(Path.Combine(Layout.Saves, save)))
            {
                var there = Directory.Exists(Layout.Saves) ? string.Join(", ", Directory.EnumerateDirectories(Layout.Saves).Select(Path.GetFileName)) : "";
                throw new StandException($"no save folder '{save}' in {Layout.Saves}; there are: [{there}]");
            }
            extra = ["-loadSaveGame", save, .. extra ?? []];
        }
        if (options.Level is { } level) extra = ["-testLoadLevel", level, .. extra ?? []];
        if (options.Level is not null || options.Mods is not null)
            EditModSettings(text => ModSettings.Isolate(text, options.Mods ?? []));
        EditModSettings(text => ModSettings.EnableBridge(text, BridgeMods));
        if (Instance is not null) Instance.Spread();
        else backup.LendDebugProfiles(withSaves: options.Level is null);
        SkipSplash(true);
        ConfigureExtender(options.StoryLog);
        var se = SeSwitch.ApplyForRun(Settings.ScriptExtender, Layout.SeSettings);
        if (se is not null) Out.WriteLine($"Script Extender for this run: {se}");
        if (LooseSources().Count > 0) SyncLoose();
        if (Directory.Exists(Layout.Channel))
        {
            foreach (var pattern in new[] { "state_*.json", "res_*.json", "queue_*.jsonl", "cmd_*.tmp" })
                foreach (var stale in Directory.EnumerateFiles(Layout.Channel, pattern).ToList()) FileRetry.TryDelete(stale);
        }

        var launch = new GameLaunch(Layout.Exe, ["--skip-launcher", .. extra ?? ["-continueGame"]], Layout.Bin,
            Instance?.Root, Layout.Game, Settings.GameFolder, GameEnvironment());
        var session = Session();
        var started = Stopwatch.StartNew();
        // A game that starts here never takes the foreground: whatever the user is typing in keeps
        // the caret, and the new window goes behind, in a deck's tile or not.
        using var foreground = Platform.KeepForeground(Layout.Exe, Out);
        // The game can still decide to start with every mod off (seen once after a new .pak
        // appeared); the bridge then never reports, and a plain restart brings the mods back.
        for (var attempt = 1; ; attempt++)
        {
            ClearCrashMarker();
            try { Platform.StartGame(launch); }
            catch (IOException e) { throw new StandException(e.Message, e); }
            var appeared = Stopwatch.StartNew();
            while (GamePids().Count == 0)
            {
                if (appeared.Elapsed > TimeSpan.FromSeconds(60)) throw new StandException("the game process did not appear");
                await Task.Delay(500, cancel);
            }
            var heartbeat = Stopwatch.StartNew();
            while (heartbeat.Elapsed < Settings.StartTimeout && session.State(Side.Client) is null) await Task.Delay(500, cancel);
            if (session.State(Side.Client) is not null) break;
            KillGame();
            if (attempt == 2) throw new StandException("the bridge mod did not load in two starts; is Script Extender installed?");
            Out.WriteLine("mods were off for this start (a pak changed); restarting");
        }
        await session.WaitStateAsync("Running", Side.Client, TimeSpan.FromSeconds(300), started, cancel);
        Out.WriteLine($"playable after {started.Elapsed.TotalSeconds:0} s");
        return session;
    }

    /// <summary>
    /// What the stand says to the game through its own environment.
    ///
    /// One thing so far, and it is the one that matters: the game is built on SDL, and SDL has a
    /// switch for the thing every copy started here used to do - throw its window over whatever
    /// you were doing and take the keyboard. With
    /// <c>SDL_WINDOW_NO_ACTIVATION_WHEN_SHOWN</c> set, SDL shows the window with Windows'
    /// <c>SW_SHOWNA</c> - "the window that is currently active remains active" - instead of
    /// <c>SW_SHOW</c>, which activates it.
    ///
    /// It is a variable rather than a call because SDL says so: "environment variables are
    /// considered to have override priority", and a hint the game sets itself loses to one in the
    /// environment unless the game sets it with SDL_HINT_OVERRIDE. So the game does not have to
    /// cooperate, nothing is patched, nothing is hooked, and it holds on Linux too - the X11
    /// backend reads the same variable.
    ///
    /// The guard that used to catch the window after the fact (<c>KeepForeground</c>) stays, as a
    /// net that reports rather than a hand that fights.
    /// </summary>
    /// <remarks>A variable the person has already set themselves is left alone: this is a default
    /// GateKit chooses, not a decision it takes away. It is also what makes the choice testable -
    /// the same start can be run with the hint off and the difference measured.</remarks>
    static IReadOnlyDictionary<string, string> GameEnvironment()
    {
        var wanted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in new[] { ("SDL_WINDOW_NO_ACTIVATION_WHEN_SHOWN", "1") })
            if (Environment.GetEnvironmentVariable(name) is null or "") wanted[name] = value;
        return wanted;
    }

    void EditModSettings(Func<string, string> change)
    {
        var text = File.ReadAllText(Layout.ModSettingsFile, Encoding.UTF8);
        File.WriteAllText(Layout.ModSettingsFile, change(text), JsonFiles.Utf8);
    }

    /// <summary>Instead of a backup: the instance's clone and profile are made ready for a start.
    /// The profile is the instance's own and thrown away, so nothing has to be put back.</summary>
    void PrepareInstance(string? save, bool allSaves)
    {
        var instance = Instance!;
        var realPublic = Path.Combine(Settings.Profile, "PlayerProfiles", "Public");
        // While the installed game is up, its mod list is the stand's cut-down one; the player's
        // own list is then in that stand's backup.
        var kept = Path.Combine(Layout.SharedState, "backup", "modsettings.lsx");
        var sharedLoose = ReadLoose(Path.Combine(Layout.SharedState, "loose.json")).Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            var cache = Platform.ScriptExtenderCache(Settings.GameFolder);
            instance.SyncGame(sharedLoose, cache is null ? null : Platform.ToGamePath(cache), File.Exists(SeSwitch.Record));
            instance.SyncProfile(Settings.Profile, BridgeMods.Select(m => m.Pak).ToHashSet(StringComparer.OrdinalIgnoreCase));
            FileTree.CopyFile(File.Exists(kept) ? kept : Path.Combine(realPublic, "modsettings.lsx"), Layout.ModSettingsFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or StandException)
        {
            throw new StandException($"instance {instance.Name}: {e.Message}\n(instances live in {Settings.InstancesRoot}; "
                + "GATEKIT_INSTANCES names another folder - writable, on the game's drive)", e);
        }
        var realSaves = Path.Combine(realPublic, "Savegames", "Story");
        if (save is not null && Directory.Exists(Path.Combine(realSaves, save)))
            FileTree.CopyTree(Path.Combine(realSaves, save), Path.Combine(Layout.Saves, save));
        else if (allSaves && Directory.Exists(realSaves))
            FileTree.CopyTree(realSaves, Layout.Saves);
        Directory.CreateDirectory(Layout.State);
        WarnIfTheFirewallWillAsk();
    }

    /// <summary>A firewall rule is held against an executable's path, and this copy is a path of
    /// its own: the first time the game in it listens, Windows throws up an elevated dialog over
    /// whatever the person was doing. GateKit cannot answer it - a rule is a change to the
    /// machine's security and the person's to make - so it says, before the game starts, that the
    /// dialog is coming and what answers it for every copy at once.</summary>
    void WarnIfTheFirewallWillAsk()
    {
        if (Platform.FirewallAllows(Layout.Exe) != false) return;
        Out.WriteLine($"note: no firewall rule for this copy ({Instance!.Name}), so Windows will ask once, "
            + "in a window of its own. `gatekit firewall` prints the one elevated line that covers every copy.");
    }

    /// <summary>A profile that still carries the bridge under its old name is brought over.</summary>
    void RetireFormerBridge()
    {
        var oldPak = Path.Combine(Layout.Mods, FormerBridge + ".pak");
        if (File.Exists(oldPak)) File.Delete(oldPak);
        var oldChannel = Path.Combine(Layout.ScriptExtenderFolder, FormerBridge);
        if (Directory.Exists(oldChannel))
        {
            if (!Directory.Exists(Layout.Channel)) Directory.Move(oldChannel, Layout.Channel);
            else
            {
                foreach (var item in Directory.EnumerateFileSystemEntries(oldChannel).ToList())
                {
                    var target = Path.Combine(Layout.Channel, Path.GetFileName(item));
                    if (!FileTree.Exists(target)) FileTree.Move(item, target);
                }
                FileTree.TryRemoveTree(oldChannel);
            }
        }
        var stale = Path.Combine(Layout.Game, "Data", "Mods", FormerBridge);
        if (Instance is null || !FileTree.Exists(stale) || Instance.Inside(stale)) FileTree.TryRemoveTree(stale);
    }

    /// <summary>An empty loose file takes the place of the studio logo video. Removed on `down`;
    /// a file that is not empty is somebody else's and is left alone.</summary>
    void SkipSplash(bool on)
    {
        var splash = Layout.Splash;
        var folder = Path.GetDirectoryName(splash)!;
        if (on && !File.Exists(splash))
        {
            OwnInInstance(folder);
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(splash, []);
        }
        else if (!on && File.Exists(splash) && new FileInfo(splash).Length == 0)
        {
            File.Delete(splash);
            if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }
    }

    /// <summary>The log file Script Extender writes its console output to (LogRuntime).</summary>
    public const string RuntimeLogPrefix = "Extender Runtime";

    /// <summary>The log file Script Extender writes the Osiris trace to (EnableLogging).</summary>
    public const string StoryLogPrefix = "Osiris Runtime";

    /// <summary>Script Extender settings for a run: no console window of its own - its output goes
    /// to a log file in the stand's folder, which `logs` reads - developer mode, and on request the
    /// Osiris trace beside it.</summary>
    void ConfigureExtender(bool storyLog)
    {
        FileTree.TryRemoveTree(Layout.SeLogs);
        Directory.CreateDirectory(Layout.SeLogs);
        JsonFiles.Edit(Layout.SeSettings, s =>
        {
            s["CreateConsole"] = false;
            s["DeveloperMode"] = true;
            s["LogRuntime"] = true;
            s["EnableLogging"] = storyLog;
            s["LogDirectory"] = Platform.ToGamePath(Layout.SeLogs);
            // A copy the stand starts does not pull its own window to the front. The SDL hint in
            // the game's environment settles the showing of the window; this settles the other
            // way in, SetForegroundWindow, which SDL_RaiseWindow calls outright and the game may
            // call itself. Refused and written down rather than passed on - and the writing down
            // is the half that was missing, because until now nobody could say who was asking.
            // Nothing is hidden or disabled by it: a click still gives the copy the keyboard.
            s["KeepWindowInBackground"] = true;
            // And where no screen reaches, but only when a deck is open: the deck is going to
            // draw this copy in a tile, so its own window is never looked at and should never be
            // seen. Done inside the game, at the moment the window is created and before it has
            // ever been shown, which is the only moment early enough - the deck's own concealment
            // cannot run until the copy has shared a frame, by which time it has been on screen
            // for seconds. Without a deck the window appears as it always has; it simply takes no
            // keyboard.
            s["KeepWindowOffScreen"] = new DeckRecord(Settings.StateRoot).Running() is not null;
        });
    }

    // --- down ---

    /// <summary>Closes the game and puts everything back. Returns the line to print.</summary>
    public string Down()
    {
        KillGame();
        if (Instance is null) new ProfileBackup(Layout, Platform).Restore();
        SeSwitch.Remove();
        SkipSplash(false);
        UnlinkAll();
        return Instance is not null ? $"instance {Instance.Name}: game closed" : "game closed, saves and mod list restored";
    }

    // --- status, instances ---

    public IEnumerable<string> Status()
    {
        yield return "instance: " + (Instance is not null ? $"{Instance.Name}  ({Instance.Root})" : "none (the installed game)");
        yield return "platform: " + Platform.Name;
        yield return "processes: [" + string.Join(", ", GamePids()) + "]";
        var se = SeSwitch.Current();
        yield return "script extender: " + (se.Fork ? $"local build from {se.Source}" : "stock");
        var session = Session();
        foreach (var side in new[] { Side.Client, Side.Server })
        {
            var state = session.State(side, null);
            var age = File.Exists(Path.Combine(Layout.Channel, $"state_{side.Name()}.json"))
                ? (DateTime.UtcNow - File.GetLastWriteTimeUtc(Path.Combine(Layout.Channel, $"state_{side.Name()}.json"))).TotalSeconds
                : (double?)null;
            yield return $"{side.Name()} " + (state is null ? "none"
                : string.Create(CultureInfo.InvariantCulture, $"{{gameState: {state.GameState}, boot: {state.Boot}, done: {state.Done}, queue: {state.Queue}, age: {age:0.0} s}}"));
        }
    }

    public IEnumerable<string> InstanceList()
    {
        var running = Platform.GameProcesses();
        foreach (var name in Instance.Names(Settings.InstancesRoot))
        {
            var one = new Instance(name, Settings, Platform);
            var pids = running.Where(p => one.Inside(p.ExecutablePath)).Select(p => p.Pid).ToList();
            yield return $"{name,-16} {(pids.Count > 0 ? $"running, pid [{string.Join(", ", pids)}]" : "stopped"),-24} {one.Root}";
        }
        var others = running.Where(p => SameFolder(Path.GetDirectoryName(p.ExecutablePath)!, Path.Combine(Settings.GameFolder, "bin"))).Select(p => p.Pid).ToList();
        yield return $"{"(installed game)",-16} {(others.Count > 0 ? $"running, pid [{string.Join(", ", others)}]" : "stopped"),-24} {Settings.GameFolder}";
    }

    /// <summary>Creates or refreshes this stand's instance without starting it.</summary>
    public string CreateInstance()
    {
        if (Instance is null) throw new StandException("`instances create` needs an instance name");
        if (GamePids().Count > 0) throw new StandException($"instance {Instance.Name} is running; run `down` first");
        PrepareInstance(null, allSaves: false);
        return $"instance {Instance.Name} ready in {Instance.Root}";
    }

    /// <summary>Gives this copy another name. The game must not be running: a copy is a folder,
    /// and a folder with a game open in it does not move.</summary>
    public string Rename(string newName)
    {
        if (Instance is null) throw new StandException("`rename` needs an instance: GATEKIT_INSTANCE or --instance <name>");
        if (GamePids().Count > 0) throw new StandException($"instance {Instance.Name} is running; run `down` first");
        var was = Instance.Name;
        Instance.RenameTo(newName);
        // The deck knows copies by name too: a label follows its copy rather than being left
        // behind on a name that no longer exists.
        var deck = new DeckRecord(Settings.StateRoot);
        if (deck.Labels().TryGetValue(was, out var label) && label is { Length: > 0 })
        {
            deck.Label(newName, label);
            deck.Label(was, null);
        }
        return $"instance {was} is now {newName}";
    }

    public string Destroy()
    {
        if (Instance is null) throw new StandException("`destroy` needs an instance: GATEKIT_INSTANCE or --instance <name>");
        Down();
        Instance.Destroy();
        return $"instance {Instance.Name} deleted";
    }

    // --- exec, key, shot, logs ---

    /// <summary>Runs Lua on "client", "server" or "ui"; "@path" runs a file.</summary>
    public Task<JsonNode?> ExecAsync(string side, string code, TimeSpan? timeout = null)
    {
        if (code.StartsWith('@')) code = File.ReadAllText(code[1..], Encoding.UTF8); // UTF8 drops a BOM
        return Session().LuaAsync(side, code, timeout);
    }

    int FirstPid() => GamePids() is { Count: > 0 } pids ? pids[0] : throw new StandException("the game is not running");

    /// <summary>Presses a key in the game without bringing its window forward. The bridge does it
    /// through the game's own input system, which needs no window and works on every platform;
    /// only when the bridge is not there does the window get the key from the operating system.</summary>
    public void Key(string name)
    {
        var code = GameKeys.Parse(name) ?? throw new StandException($"unknown key '{name}'; known: {GameKeys.Known}");
        var session = Session();
        if (session.State(Side.Client) is not null)
        {
            session.LuaAsync("client", $"return GK.Key(\"{name}\")", TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            return;
        }
        if (!Platform.CanPostKeys) throw new StandException(Platform.WhyNot("key") + "; the bridge is not answering either (is the game up with GateKit's Script Extender build?)");
        try { Platform.PostKey(FirstPid(), code); }
        catch (IOException e) { throw new StandException("key not sent: " + e.Message, e); }
    }

    /// <summary>A screenshot of the game (JPEG unless the name ends in .png), scaled to
    /// <paramref name="width"/> at most. The bridge takes it inside the game, off the frame the
    /// renderer is about to show and before anything of the extender's is drawn over it, which
    /// needs no window and works on every platform; the window is only photographed when the
    /// bridge cannot do it.</summary>
    public string Shot(string output, int width = 1150)
    {
        var path = Path.GetFullPath(output);
        var picture = ShotFromGame();
        if (picture is null)
        {
            if (!Platform.CanCaptureWindow) throw new StandException(Platform.WhyNot("shot")
                + "; the bridge could not take one either (is the game up with GateKit's Script Extender build?)");
            try { picture = Platform.CaptureWindow(FirstPid()); }
            catch (IOException e) { throw new StandException("screenshot failed: " + e.Message, e); }
        }
        ImageFiles.Save(picture, path, width);
        return path;
    }

    /// <summary>How long the game is given to hand the frame over before the window is tried instead.</summary>
    static readonly TimeSpan ShotPatience = TimeSpan.FromSeconds(10);

    /// <summary>The frame as the game itself writes it into the channel folder, or null when the
    /// bridge is not there or its renderer cannot read the frame.</summary>
    Screenshot? ShotFromGame()
    {
        var session = Session();
        if (session.State(Side.Client) is null) return null;
        var name = $"shot_{Environment.ProcessId}_{Stopwatch.GetTimestamp()}.png";
        var file = Path.Combine(Layout.Channel, name);
        try
        {
            var asked = session.LuaAsync("client", $"return GK.Shot(\"{ModSettings.BridgeFolder}/{name}\")",
                TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            if (asked is not JsonValue value || !value.TryGetValue<bool>(out var ok) || !ok) return null;
            var waited = Stopwatch.StartNew();
            while (!File.Exists(file))
            {
                if (waited.Elapsed > ShotPatience) throw new StandException(
                    $"the game accepted the screenshot but wrote no file in {ShotPatience.TotalSeconds:0} s");
                Thread.Sleep(50);
            }
            return ImageReader.Read(FileRetry.ReadAllBytes(file));
        }
        finally { FileRetry.TryDelete(file); }
    }

    /// <summary>What Script Extender printed (prints, warnings, Lua errors), or with
    /// <paramref name="story"/> the Osiris trace (needs `up --story-log`).
    ///
    /// A game this stand started writes both to log files and has no console window at all. A game
    /// started another way may still have one, and is read from it, so nothing that worked before
    /// stopped working.</summary>
    public IReadOnlyList<string> Logs(bool story, int lines = 80, string? grep = null)
    {
        var pattern = grep is null ? null : new Regex(grep, RegexOptions.IgnoreCase);
        var all = story ? LogFileLines(StoryLogPrefix) : ConsoleLines();
        var kept = all.Where(l => l.Trim().Length > 0 && (pattern is null || pattern.IsMatch(l))).ToList();
        return kept.Skip(Math.Max(0, kept.Count - lines)).ToList();
    }

    /// <summary>The lines of this run's extender log files of one kind, oldest file first.</summary>
    IReadOnlyList<string> LogFileLines(string prefix)
    {
        if (!Directory.Exists(Layout.SeLogs)) return [];
        return Directory.EnumerateFiles(Layout.SeLogs, prefix + "*.log").OrderBy(File.GetLastWriteTimeUtc)
            .SelectMany(f => FileRetry.ReadAllText(f).Split('\n').Select(l => l.TrimEnd('\r'))).ToList();
    }

    IReadOnlyList<string> ConsoleLines()
    {
        if (LogFileLines(RuntimeLogPrefix) is { Count: > 0 } written) return written;
        if (!Platform.CanReadConsole)
            throw new StandException($"no {RuntimeLogPrefix} log in {Layout.SeLogs}: start the game with `up`, "
                + "or turn LogRuntime on in the game's ScriptExtenderSettings.json");
        try { return Platform.ReadConsole(FirstPid()); }
        catch (IOException e)
        {
            throw new StandException($"{e.Message}\n(a game started by `up` writes its output to {Layout.SeLogs} "
                + "instead of a console window; this one has neither)", e);
        }
    }

    // --- hot reload ---
    // Lua files loose in <game>/Data/Mods/<Folder>/ScriptExtender/Lua win over the same files in
    // a .pak, and Ext.Debug.Reset() loads them again: change a mod's Lua while the game runs.
    // Only Lua is copied; a whole mod folder linked into Data/Mods has hung the game at 87 %.

    static Dictionary<string, string> ReadLoose(string path)
    {
        var record = File.Exists(path) ? JsonFiles.ReadObject(path) : [];
        return record.Where(p => p.Value is JsonValue).ToDictionary(p => p.Key, p => p.Value!.ToString());
    }

    public IReadOnlyDictionary<string, string> LooseSources() => ReadLoose(Layout.LooseRecord);

    /// <summary>In an instance most of Data is links to the real game: before loose files go into
    /// <paramref name="folder"/> the links on the way become real folders, and the place is checked
    /// to be inside the instance.</summary>
    void OwnInInstance(string folder)
    {
        if (Instance is null) return;
        Instance.OwnFolder(folder);
        if (!Instance.Inside(folder)) throw new StandException($"{folder} is not inside instance {Instance.Name}; nothing was written");
    }

    /// <summary>Registers every mod under &lt;modRoot&gt;/Mods/*/ScriptExtender/Lua for hot reload.</summary>
    public IReadOnlyList<string> Link(string modRoot)
    {
        var root = Path.GetFullPath(modRoot);
        var mods = Path.Combine(root, "Mods");
        var found = Directory.Exists(mods)
            ? Directory.EnumerateDirectories(mods).Where(d => Directory.Exists(Path.Combine(d, "ScriptExtender", "Lua"))).Order(StringComparer.Ordinal).ToList()
            : [];
        if (found.Count == 0) throw new StandException($"no Mods/<Folder>/ScriptExtender/Lua under {root}");
        var sources = ReadLoose(Layout.LooseRecord);
        foreach (var mod in found) sources[Path.GetFileName(mod)] = Path.Combine(mod, "ScriptExtender", "Lua");
        var record = new JsonObject();
        foreach (var (key, value) in sources) record[key] = value;
        JsonFiles.WriteObject(Layout.LooseRecord, record);
        SyncLoose();
        return found.Select(Path.GetFileName).OfType<string>().ToList();
    }

    void SyncLoose()
    {
        foreach (var (folder, source) in LooseSources())
        {
            var target = Layout.LooseTarget(folder);
            OwnInInstance(Path.GetDirectoryName(target)!);
            FileTree.TryRemoveTree(target);
            FileTree.CopyTree(source, target);
        }
    }

    /// <summary>Removes the loose copies from the game folder (the sources stay).</summary>
    void UnlinkAll()
    {
        foreach (var folder in LooseSources().Keys)
        {
            var target = Layout.LooseTarget(folder);
            if (Instance is not null && FileTree.Exists(target) && !Instance.Inside(target)) continue; // still a link: nothing of ours there
            FileTree.TryRemoveTree(target);
            foreach (var parent in new[] { Path.GetDirectoryName(target)!, Path.GetDirectoryName(Path.GetDirectoryName(target)!)! })
            {
                if (Directory.Exists(parent) && !FileTree.IsLink(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
            }
        }
        if (File.Exists(Layout.LooseRecord)) File.Delete(Layout.LooseRecord);
    }

    /// <summary>Copies the linked mods' Lua in and restarts both Lua states in place.</summary>
    public async Task<TimeSpan> ReloadAsync()
    {
        if (LooseSources().Count == 0) throw new StandException("nothing is linked; run `link <mod folder>` first");
        var watch = Stopwatch.StartNew();
        SyncLoose();
        await Session().ResetLuaAsync();
        return watch.Elapsed;
    }

    /// <summary>Links the bridge mods' own Lua (GateKit and Weave) and reloads it.</summary>
    public async Task<TimeSpan> BridgeReloadAsync()
    {
        foreach (var root in BridgeMods.Select(m => m.Root).Distinct()) Link(root);
        return await ReloadAsync();
    }

    /// <summary>Newest modification time of any linked .lua file.</summary>
    public DateTime TreeStamp() =>
        LooseSources().Values.Where(Directory.Exists)
            .SelectMany(s => Directory.EnumerateFiles(s, "*.lua", SearchOption.AllDirectories))
            .Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();

    /// <summary>Reloads whenever a linked Lua file changes, until cancelled.</summary>
    public async Task WatchAsync(CancellationToken cancel)
    {
        var seen = TreeStamp();
        Out.WriteLine("watching " + (LooseSources().Count > 0 ? string.Join(", ", LooseSources().Keys) : "nothing (run `link` first)"));
        while (!cancel.IsCancellationRequested)
        {
            try { await Task.Delay(500, cancel); }
            catch (OperationCanceledException) { break; }
            var stamp = TreeStamp();
            if (stamp == seen) continue;
            seen = stamp;
            var now = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            try
            {
                var took = await ReloadAsync();
                Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{now} reloaded in {took.TotalSeconds:0.0} s"));
            }
            catch (StandException e) { Out.WriteLine($"{now} reload failed: {e.Message}"); }
        }
    }

    // --- Script Extender switch ---

    public ScriptExtenderSwitch.Status SeStatus() => SeSwitch.Current();

    public ScriptExtenderSwitch.Status SeInstall(string folder)
    {
        if (GamePids().Count > 0) throw new StandException("the game is running; run `down` first");
        var status = SeSwitch.Install(Path.GetFullPath(folder));
        // Also the build later runs load when nothing names one: chosen once, not exported by
        // every shell. An instance of its own still installs into its own bin, as before.
        StandSettings.RememberScriptExtender(Settings.StateRoot, Path.GetFullPath(folder));
        return status;
    }

    /// <summary>
    /// Installs the build pinned in se-release.json, downloading it once into the state folder.
    /// A build already fetched for that tag is reused.
    /// </summary>
    public async Task<ScriptExtenderSwitch.Status> SeInstallPinnedAsync(CancellationToken cancel = default)
    {
        if (GamePids().Count > 0) throw new StandException("the game is running; run `down` first");
        var repo = StandSettings.RepositoryRoot()
            ?? throw new StandException("se-release.json belongs to the GateKit repository; run gatekit from it, or use `se install <folder>`");
        var pinFile = Path.Combine(repo, "se-release.json");
        if (!File.Exists(pinFile))
            throw new StandException($"{pinFile} is missing; use `se install <folder>` with a build of your own");
        var pin = ScriptExtenderRelease.Pin.Read(pinFile);
        var into = Path.Combine(Settings.StateRoot, "se", pin.Tag);
        if (!File.Exists(Path.Combine(into, "BG3ScriptExtender.dll")))
            await new ScriptExtenderRelease().FetchAsync(pin, into, Out, cancel);
        else
            Out.WriteLine($"{pin.Tag} is already here");
        var pinned = SeSwitch.Install(into);
        StandSettings.RememberScriptExtender(Settings.StateRoot, into);
        return pinned;
    }

    public ScriptExtenderSwitch.Status SeRemove() => SeSwitch.Remove();
}
