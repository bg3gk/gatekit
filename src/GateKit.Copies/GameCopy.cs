using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GateKit.Bridge;
using GateKit.Platform;

namespace GateKit.Copies;

/// <summary>What `up` starts.</summary>
/// <param name="Level">MODULE:LEVEL: a fresh test game in that level, no menu, no save; only the base game, the bridge and <paramref name="Mods"/>.</param>
/// <param name="Save">A save folder name under Savegames/Story to load instead of the last one.</param>
/// <param name="Mods">Mod folders to keep besides the base game; null = the player's own list (unless a level is given).</param>
/// <param name="StoryLog">Also record Script Extender's logs, the Osiris trace included (`logs --story`).</param>
/// <param name="GameArgs">Extra raw game arguments.</param>
/// <param name="MainMenu">Stop in the main menu (first-launch screens gone through in an instance)
/// instead of loading the last save: what a recipe of the menus starts from.</param>
/// <param name="Splash">Stop on the splash screen before the main menu ("press any key"), which is
/// left standing for the caller to pass; first-launch screens are gone through as for the main menu.</param>
/// <param name="UntilLua">Return as soon as the bridge first answers (the client's first heartbeat,
/// in its LoadModule state, right after the startup videos): no menu, no session; the game goes on
/// loading its menu meanwhile.</param>
/// <param name="FirstLaunch">Start as a first launch: the game's own switch `ShowFirstTimeSetup` is
/// set at the first Lua, and the start ends on the first first-launch page (`Eula`), which is left
/// standing with the pages after it for the caller. An instance only: the pages write the copy's
/// own profile.</param>
/// <param name="CoverStart">A file of the executable's function starts (one hexadecimal offset a
/// line): the extender puts coverage counters on them as it starts, before the engine, and saves a
/// record beside the file at every state the client enters up to the main menu (`cover start
/// --at-start` writes the file). Null: no coverage of the start.</param>
/// <param name="FastLoad">Loads without the game's waits (GateKit's build: the client's frame caps
/// and the server's tick lifted during a load, PrepareRunning's streaming wait and the screen fades
/// cut); for automated runs, never for pictures of a load. Also on when `GATEKIT_FAST_LOAD=1`.</param>
/// <param name="Headless">A game for logic only (GateKit's build; the night's W3 checked each switch
/// against the logic, `gatekit logic-diff`): fast loads, the game's Lua collector held while
/// loading, nothing drawn or presented from the start, the tick waits slept instead of spun, the
/// window kept off the screen. No pictures (`shot`) and no window to look at. Also on when
/// `GATEKIT_HEADLESS=1`.</param>
public sealed record UpOptions(string? Level = null, string? Save = null, IReadOnlyList<string>? Mods = null,
    bool StoryLog = false, IReadOnlyList<string>? GameArgs = null, bool MainMenu = false, bool Splash = false,
    bool StartupVideos = false, bool UntilLua = false, bool FirstLaunch = false, string? CoverStart = null,
    bool FastLoad = false, bool Headless = false);

/// <summary>
/// A game copy: the installed game or an instance. Starts the game directly (no launcher), talks
/// to it through the bridge mod, and puts the player's saves and mod list back afterwards;
/// docs/contracts/cli.md describes every command.
/// </summary>
public sealed class GameCopy
{
    const string FormerBridge = "Gwynerva_DevKit";

    public GameCopy(CopySettings settings, IGamePlatform platform, TextWriter? output = null, IModPacker? packer = null)
    {
        Settings = settings;
        Platform = platform;
        Layout = new CopyLayout(settings, platform);
        Out = output ?? Console.Out;
        Packer = packer ?? (settings.Packer == PackerKind.Divine
            ? new DivinePacker(settings.Divine, settings.DotnetRoot)
            : new LSLibPacker());
    }

    public CopySettings Settings { get; }
    public IGamePlatform Platform { get; }
    public CopyLayout Layout { get; }
    public Instance? Instance => Layout.Instance;
    public TextWriter Out { get; }
    public IModPacker Packer { get; }

    ScriptExtenderSwitch SeSwitch => new(Layout.Bin, Layout.State);

    /// <summary>Refuses <paramref name="verb"/> when it would write into the installed game and that
    /// was not named (<see cref="CopyLayout.RequireNamed"/>).</summary>
    public void RequireNamed(string verb) => Layout.RequireNamed(verb);

    // --- processes ---

    bool SameFolder(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), Platform.PathComparison);

    /// <summary>The processes of this home's game: started from its bin. Other instances' games
    /// are never seen, awaited or killed.</summary>
    public IReadOnlyList<int> GamePids() =>
        Platform.GameProcesses().Where(p => SameFolder(Path.GetDirectoryName(p.ExecutablePath)!, Layout.Bin)).Select(p => p.Pid).ToList();

    public GameSession Session() => new(Layout, () => GamePids().Count > 0, Out);

    /// <summary>How loud this copy's game is (`gatekit sound`, the bridge's GK.Sound).</summary>
    public SoundControl Sound() => new(Session());

    public void KillGame()
    {
        var killed = GamePids();
        foreach (var pid in killed) Platform.Kill(pid);
        var watch = Stopwatch.StartNew();
        // A game can take twenty seconds and more to exit (seen at a closing of the third run).
        while (GamePids().Count > 0 && watch.Elapsed < TimeSpan.FromSeconds(60)) Thread.Sleep(500);
        // Gone from the process list is not yet let go: the next `up` failed on BG3ScriptExtender.dll
        // "being used by another process" (D11 of the day run). The copy's own extender dll is waited
        // for until it can be opened alone - not the executable, which in a copy is a link to the
        // installed game's and is held whenever the player plays.
        if (killed.Count > 0) WaitUntilFree(Path.Combine(Layout.Bin, "BG3ScriptExtender.dll"), TimeSpan.FromSeconds(30));
        ClearCrashMarker();
    }

    /// <summary>Waits until nobody holds <paramref name="path"/> (or it is not there); says so when it waited.</summary>
    void WaitUntilFree(string path, TimeSpan limit)
    {
        var watch = Stopwatch.StartNew();
        while (File.Exists(path))
        {
            try
            {
                using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                if (watch.Elapsed > TimeSpan.FromSeconds(1))
                    Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {Path.GetFileName(path)} was let go {watch.Elapsed.TotalSeconds:0} s after the game closed"));
                return;
            }
            catch (IOException) when (watch.Elapsed < limit) { Thread.Sleep(250); }
            catch (IOException) { Out.WriteLine($"  {Path.GetFileName(path)} is still held {limit.TotalSeconds:0} s after the game closed"); return; }
            catch (UnauthorizedAccessException) { return; }   // read-only here: not ours to wait for
        }
    }

    /// <summary>The game leaves this folder behind when it does not exit by itself (`gatekit` always
    /// kills it) and takes it on the next start as "a mod crashed": every mod off, a key to press.</summary>
    void ClearCrashMarker() => FileTree.TryRemoveTree(Layout.CrashMarker);

    // --- build ---

    IReadOnlyList<BridgeMod>? _bridgeMods;

    /// <summary>The mods every test game gets (Weave, then the GateKit bridge), from GATEKIT_BRIDGE.</summary>
    public IReadOnlyList<BridgeMod> BridgeMods => _bridgeMods ??= Settings.BridgeSource is { } source
        ? GateKit.Copies.BridgeMods.Find(source)
        : throw new CopyException("no bridge source: set GATEKIT_BRIDGE to the repository's mods folder");

    /// <summary>A packed bridge mod: a cache, one folder per copy (the installed game is "installed").</summary>
    string BuiltPak(BridgeMod mod) => Settings.BuildCache is { } cache
        ? Path.Combine(cache, Settings.Instance is { Length: > 0 } name ? name : "installed", mod.Pak)
        : Path.Combine(Layout.State, "build", mod.Pak);
    string InstalledPak(BridgeMod mod) => Path.Combine(Layout.Mods, mod.Pak);

    /// <summary>Packs the bridge mods and installs them into the profile; returns the installed paks.</summary>
    public IReadOnlyList<string> Build()
    {
        RequireNamed("build");
        using var start = TakeStart("build");
        return BuildHeld();
    }

    /// <summary>The copy's start lock (<c>&lt;state&gt;/start.lock</c>, the home's <see cref="GateKit.Home.SharedFile"/>
    /// lock): held by one `up` or `build` of this copy from its first look at the copy to its end,
    /// so two of them never prepare, pack or start one copy at once. Another one is refused at once.</summary>
    public string StartLock => Path.Combine(Layout.State, "start");

    IDisposable TakeStart(string verb)
    {
        // a second is long enough for a lock being let go of, and far shorter than any start
        try { return GateKit.Home.SharedFile.Lock(StartLock, TimeSpan.FromSeconds(1)); }
        catch (GateKit.Home.HomeException)
        {
            throw new CopyException(BusyText(verb, Instance?.Name, OwnerText()));
        }
    }

    /// <summary>What a second `up` or `build` of a copy says while another one holds its start.</summary>
    public static string BusyText(string verb, string? copy, string owner) =>
        $"another `up` or `build` of {(copy is null ? "the installed game" : $"instance {copy}")} is under way in another process; "
        + $"this `{verb}` changed nothing (the copy's last start: {owner}). When that one ends, `status` says whether the game is up.";

    IReadOnlyList<string> BuildHeld()
    {
        if (GamePids().Count > 0) throw new CopyException("the game is running and holds the mod files open; run `down` first");
        var installed = new List<string>();
        foreach (var mod in BridgeMods)
        {
            // packed beside the cached pak and moved over it, then installed the same way: a pak is
            // there whole or not at all, whatever reads it
            var built = BuiltPak(mod);
            var packing = Path.ChangeExtension(built, $".{Environment.ProcessId}.packing.pak");
            try
            {
                Packer.Pack(mod.Root, packing);
                File.Move(packing, built, overwrite: true);
            }
            finally { FileRetry.TryDelete(packing); }
            FileTree.CopyFile(built, InstalledPak(mod));
            installed.Add(InstalledPak(mod));
        }
        JsonFiles.WriteObject(BridgeBuiltRecord, new JsonObject { ["source"] = Settings.BridgeSource, ["built"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) });
        return installed;
    }

    /// <summary>Which bridge sources the copy's installed paks were packed from (one file per copy).</summary>
    string BridgeBuiltRecord => Path.Combine(Layout.State, "bridge-built.json");

    /// <summary>The installed paks are stale when a source file is newer, or when they were packed
    /// from other bridge sources: a checkout whose files are older than another's must still get
    /// its own bridge into the copy it starts.</summary>
    bool BridgePakStale() => !string.Equals(JsonFiles.ReadObject(BridgeBuiltRecord)["source"]?.ToString(), Settings.BridgeSource, StringComparison.Ordinal)
        || BridgeMods.Any(mod =>
    {
        var pak = InstalledPak(mod);
        if (!File.Exists(pak)) return true;
        var installed = File.GetLastWriteTimeUtc(pak);
        return Directory.EnumerateFiles(Path.Combine(mod.Root, "Mods", mod.Folder), "*", SearchOption.AllDirectories)
            .Any(f => File.GetLastWriteTimeUtc(f) > installed);
    });

    /// <summary>Format of a copy's owner.json.</summary>
    public const int OwnerVersion = 1;

    /// <summary>Who started this copy last: the GateKit build, its checkout, its bridge sources.</summary>
    public string OwnerRecord => Path.Combine(Layout.State, "owner.json");

    /// <summary>The copy's owner record, or an empty object.</summary>
    public JsonObject Owner()
    {
        try { return GateKit.Home.SharedFile.Read(OwnerRecord, OwnerVersion); }
        catch (GateKit.Home.HomeException e) { throw new CopyException(e.Message); }
    }

    /// <summary>"build X from checkout Y", for messages about a copy someone else started.</summary>
    public string OwnerText() => Owner() is { Count: > 0 } o
        ? $"started by GateKit {o["build"]} from {o["checkout"] ?? "an installed GateKit"} at {o["started"]}"
        : "no record of who started it";

    void RecordOwner()
    {
        try
        {
            GateKit.Home.SharedFile.Replace(OwnerRecord, OwnerVersion, new JsonObject
            {
                ["build"] = typeof(GameCopy).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                    ?? typeof(GameCopy).Assembly.GetName().Version?.ToString(),
                ["checkout"] = CopySettings.RepositoryRoot(),
                ["bridge"] = Settings.BridgeSource,
                ["pid"] = Environment.ProcessId,
                ["started"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            });
        }
        catch (GateKit.Home.HomeException e) { throw new CopyException(e.Message); }
    }

    // --- up ---

    /// <summary>How long a start may sit in the main menu, with nothing moving it on, before it counts as failed.</summary>
    public static readonly TimeSpan MenuLimit = TimeSpan.FromSeconds(60);

    const string MenuProbe = """
        local stuck, screen = GK.Gui.Stuck()
        local out = { stuck = stuck, widget = screen and screen.widget }
        if (stuck == "firstlaunch" and PRESS) or (stuck == "splash" and SPLASH) then
            local result = GK.Cheat.Recover() or {}
            out.did, out.ok = result.did, result.ok
            out.error = result.error and tostring(result.error)
        end
        return out
        """;

    /// <summary>One look at what holds a starting client in the menu, passing the screens a start
    /// passes (the splash screen always, the first-launch screens in an instance): what holds it,
    /// and whether the look moved the game on. With <paramref name="passSplash"/> false the splash
    /// screen is named and left standing.</summary>
    async Task<(string? Stuck, bool Moved, bool Answered)> LookInMenuAsync(GameSession session, CancellationToken cancel, bool passSplash = true)
    {
        var press = Instance is not null;
        JsonNode? seen = null;
        try
        {
            seen = await session.LuaAsync("ui", "local PRESS, SPLASH = " + (press ? "true" : "false") + ", " + (passSplash ? "true" : "false") + ";\n" + MenuProbe,
                TimeSpan.FromSeconds(10), cancel);
        }
        catch (CopyException) { return (null, false, false); }  // a look that failed is no answer; the menu time still counts
        var stuck = seen.Field("stuck")?.GetValue<string>();
        var widget = seen.Field("widget")?.GetValue<string>();
        if (stuck == "splash" && !passSplash) return (stuck, false, true);
        if (stuck is "firstlaunch" or "splash")
        {
            if (stuck == "firstlaunch" && !press)
                throw new CopyException($"the game shows its first-launch screen ({widget}); GateKit goes through it only in an "
                    + "instance (--instance), never in the installed game - accept it in the game yourself, or use an instance");
            if (seen.Field("ok")?.GetValue<bool>() != true)
                throw new CopyException($"the game shows {(stuck == "splash" ? "its splash screen" : "its first-launch screen")} ({widget}) "
                    + $"and going through it failed: {seen.Field("error")}");
            Out.WriteLine($"  {seen.Field("did")}");
            return (stuck, true, true);
        }
        return (stuck, false, true);
    }

    /// <summary>One look at what holds a starting client in the menu; true when it moved the game on.</summary>
    async Task<bool> MenuStepAsync(GameSession session, UpOptions options, TimeSpan menuTime, CancellationToken cancel)
    {
        var (stuck, moved, answered) = await LookInMenuAsync(session, cancel);
        if (moved) return true;
        if (menuTime < MenuLimit) return false;
        var holding = stuck switch
        {
            null when !answered => "the bridge did not say what holds it",
            null => "GK.Gui.Stuck() names nothing",
            "mainmenu" => "the main menu",
            _ => stuck,
        };
        throw new CopyException($"the game stayed in the menu for {MenuLimit.TotalSeconds:0} s ({holding}) instead of "
            + (options.Level is { } level ? $"loading level '{level}' (-testLoadLevel; is MODULE:LEVEL right?)" : "loading a save")
            + "; the game is left running for a look - `down` before trying again");
    }

    /// <summary>A start waiting for a level or a save while its client is in the menu, followed by
    /// its stages: each page the interface enters is looked at (the splash screen passed, a
    /// first-launch screen gone through in an instance); true when the game moved on (a page passed,
    /// the client gone from Menu). No stage within MenuLimit fails the start as
    /// <see cref="MenuStepAsync"/> does, naming what holds it.</summary>
    async Task<bool> MenuStageAsync(GameSession session, StartStages stages, UpOptions options, TimeSpan menuTime, CancellationToken cancel)
    {
        StartStages.Stage stage;
        try { stage = await stages.NextAsync(session, MenuLimit, cancel); }
        catch (CopyException) { return await MenuStepAsync(session, options, menuTime, cancel); }   // the client's Lua went with a load: a look
        if (stage.Left is not null) return true;
        if (stage.Full) return await MenuStepAsync(session, options, menuTime, cancel);
        if (stage.None) return await MenuStepAsync(session, options, MenuLimit, cancel);
        var (_, moved, _) = await LookInMenuAsync(session, cancel);
        return moved;
    }

    /// <summary>Why <paramref name="level"/> is not something -testLoadLevel can take (null = it is):
    /// the game wants MODULE:LEVEL, and given a bare level it sits in the main menu.</summary>
    public static string? LevelProblem(string level)
    {
        var colon = level.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0 && colon < level.Length - 1 && level.IndexOf(':', colon + 1) < 0 && !level.Any(char.IsWhiteSpace)) return null;
        return $"level '{level}' is not MODULE:LEVEL; the game's -testLoadLevel wants the module too "
            + $"(base-game levels are mostly in GustavX, e.g. GustavX:{(colon < 0 && level.Length > 0 ? level : "TUT_Avernus_C")}); "
            + "given a bare level the game only sits in the main menu";
    }

    /// <summary>What `-loadSaveGame` is given for the save folder <paramref name="save"/>. The game
    /// reads its command line in the system's ANSI code page, so a folder name that is not ASCII
    /// never matches as itself; it also matches a save by the name after its unique id
    /// (`<id>__<name>`, code:ecl::SavegameManager::GetSavegameIndex), which is taken when it is
    /// ASCII and no other save of the copy's profiles (<paramref name="folders"/>) has it. GateKit's
    /// Script Extender build gives the game the value in UTF-8 from the wide command line (its
    /// switch WideCommandLine): with it (<paramref name="wideCommandLine"/>) the folder is passed
    /// as it is.</summary>
    public static string LoadSaveArgument(string save, IReadOnlyCollection<string> folders, bool wideCommandLine = false)
    {
        if (save.All(char.IsAscii) || wideCommandLine) return save;
        static string? NamePart(string folder) => folder.IndexOf("__", StringComparison.Ordinal) is var cut and >= 0 ? folder[(cut + 2)..] : null;
        if (NamePart(save) is { Length: > 0 } name && name.All(char.IsAscii)
            && folders.Count(f => string.Equals(NamePart(f), name, StringComparison.OrdinalIgnoreCase)) == 1)
            return name;
        throw new CopyException($"save folder '{save}' is not ASCII, and the game reads its command line in the system's code page; "
            + "the name after its unique id is not one the game can tell apart either (another save has it, or it is not ASCII) - "
            + "load it from the main menu (`up --main-menu`, then GK.Gui) or give the folder an ASCII name");
    }

    /// <summary>Starts the game and waits until it is playable (the client reaches Running).</summary>
    public async Task<GameSession> UpAsync(UpOptions options, CancellationToken cancel = default)
    {
        RequireNamed("up");
        using var start = TakeStart("up");
        if (GamePids().Count > 0) throw new CopyException($"the game is already running ({OwnerText()}); run `down` first");
        CleanStaleTestFiles();
        if (options.Level is { } asked && LevelProblem(asked) is { } problem) throw new CopyException(problem);
        if ((options.MainMenu || options.Splash) && (options.Level is not null || options.Save is not null))
            throw new CopyException("a start in the main menu takes no level and no save");
        if (options.MainMenu && options.Splash)
            throw new CopyException("--splash stops before the main menu and --main-menu in it: give one of them");
        if (options.UntilLua && (options.Level is not null || options.Save is not null || options.MainMenu || options.Splash))
            throw new CopyException("--until-lua returns when the bridge first answers, before any menu or level: it takes no --level, --save, --main-menu or --splash");
        if (options.FirstLaunch && (options.Level is not null || options.Save is not null || options.MainMenu || options.Splash || options.UntilLua))
            throw new CopyException("--first-launch ends on the game's first first-launch page: it takes no --level, --save, --main-menu, --splash or --until-lua");
        if (options.FirstLaunch && Instance is null)
            throw new CopyException("--first-launch is for an instance (--instance NAME): the first-launch pages write the profile's own settings, never the installed game's");
        if (Instance is not null) PrepareInstance(options.Save, allSaves: options.Level is null, withMods: options.Mods is { Count: > 0 });
        RecordOwner();
        RetireFormerBridge();
        // A start runs the bridge as built: rebuilt whenever its sources are newer than the installed pak.
        if (BridgePakStale())
        {
            BuildHeld();
            Out.WriteLine("bridge pak rebuilt (its sources were newer than the installed pak)");
        }
        var backup = new ProfileBackup(Layout, Platform);
        if (Instance is null) backup.Save();

        List<string>? extra = options.GameArgs is { Count: > 0 } given ? [.. given] : null;
        if (options.MainMenu || options.Splash || options.UntilLua || options.FirstLaunch) extra ??= [];   // without -continueGame the game stays in its main menu
        List<string> everySave = [];
        if (options.Save is { } save)
        {
            if (!Directory.Exists(Path.Combine(Layout.Saves, save)))
            {
                var there = Directory.Exists(Layout.Saves) ? string.Join(", ", Directory.EnumerateDirectories(Layout.Saves).Select(Path.GetFileName)) : "";
                throw new CopyException($"no save folder '{save}' in {Layout.Saves}; there are: [{there}]");
            }
            everySave = Directory.Exists(Path.Combine(Layout.Profile, "PlayerProfiles"))
                ? Directory.EnumerateDirectories(Path.Combine(Layout.Profile, "PlayerProfiles"))
                    .Select(p => Path.Combine(p, "Savegames", "Story")).Where(Directory.Exists)
                    .SelectMany(Directory.EnumerateDirectories).Select(f => Path.GetFileName(f)!).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : [];
            extra ??= [];   // -loadSaveGame itself once the run's Script Extender build is known (below)
        }
        if (options.Level is { } level) extra = ["-testLoadLevel", level, .. extra ?? []];
        // A copy starts bare - the base game and the bridge - unless mods are named; only the
        // installed game keeps the player's own list when nothing is asked.
        if (Instance is not null || options.Level is not null || options.Mods is not null)
            EditModSettings(text => ModSettings.Isolate(text, options.Mods ?? []));
        EditModSettings(text => ModSettings.EnableBridge(text, BridgeMods));
        if (Instance is not null) Instance.Spread();
        else backup.LendDebugProfiles(withSaves: options.Level is null);
        // A start does not wait on startup videos: GateKit's extender build hides every one, the
        // game's and the mods' (its StartupVideos switch), unless the start asks for them.
        ConfigureExtender(options.StoryLog, options.StartupVideos, options.CoverStart, directSaveLoad: options.Save is not null,
            fastLoad: options.FastLoad || FastLoadAsked(), headless: options.Headless || HeadlessAsked());
        var se = SeSwitch.ApplyForRun(Settings.ScriptExtender, Layout.SeSettings,
            keepInstalled: Instance is not null && !Settings.ScriptExtenderStock);
        ConfigureLoader();
        if (se is not null) Out.WriteLine($"Script Extender for this run: {se}");
        if (options.Save is { } saveFolder)
        {
            // GateKit's build (a local build: SeSwitch's fork) reads the value in UTF-8 from the
            // wide command line; the stock extender leaves the game's narrow reading.
            var argument = LoadSaveArgument(saveFolder, everySave, wideCommandLine: SeSwitch.Current().Fork);
            extra!.InsertRange(options.Level is null ? 0 : 2, ["-loadSaveGame", argument]);
        }
        if (LooseSources().Count > 0) SyncLoose();
        if (Directory.Exists(Layout.Channel))
        {
            // a capture armed in a game that has since closed is not re-armed by a new start (start it
            // again after `up`, as before); what it spilled was the last session's to drain
            foreach (var pattern in new[] { "state_*.json", "res_*.json", "queue_*.jsonl", "cmd_*.tmp", "seen_armed_*.json", "seen_spill_*.json" })
                foreach (var stale in Directory.EnumerateFiles(Layout.Channel, pattern).ToList()) FileRetry.TryDelete(stale);
        }

        // The bridge's event log reads this name at its first load in the process: what a log of
        // another process left open (fixture rows, strays) is of a world that is gone, and is not carried.
        void NameProcess()
        {
            try
            {
                JsonFiles.WriteText(Path.Combine(Layout.Channel, "process.txt"), Guid.NewGuid().ToString("N"));
            }
            catch (IOException e) { Out.WriteLine("note: the game process was not named for the event log: " + e.Message); }
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
            if (attempt > 1)
            {
                // the reading of the second start is its own log's
                FileTree.TryRemoveTree(Layout.SeLogs);
                Directory.CreateDirectory(Layout.SeLogs);
            }
            NameProcess();
            try { Platform.StartGame(launch); }
            catch (IOException e) { throw new CopyException(e.Message, e); }
            var appeared = Stopwatch.StartNew();
            while (GamePids().Count == 0)
            {
                if (appeared.Elapsed > TimeSpan.FromSeconds(60)) throw new CopyException("the game process did not appear");
                await Task.Delay(500, cancel);
            }
            var reading = await AwaitBridgeAsync(session, cancel, options.CoverStart is null ? 1 : 4);
            if (reading is null) break;
            KillGame();
            if (!reading.Restart || attempt == 2)
                throw new CopyException($"the bridge did not report{(attempt == 2 ? " in the second start either" : "")}: {reading.Text}; the game was closed");
            Out.WriteLine($"{reading.Text}; restarting");
        }
        if (options.UntilLua)
        {
            Out.WriteLine($"the bridge answers after {started.Elapsed.TotalSeconds:0} s (client in {session.State(Side.Client)?.GameState ?? "an unknown state"})");
            return session;
        }
        if (options.FirstLaunch)
        {
            await SwitchFirstLaunchAsync(session, cancel);
            await ReachFirstLaunchAsync(session, await StartStages.OpenAsync(session, cancel), started, cancel);
            Out.WriteLine($"on the first-launch page Eula after {started.Elapsed.TotalSeconds:0} s (the pages are the caller's: GK.Gui.Look, Click, Select, Set)");
            return session;
        }
        // A start through the main menu has the game skip its own pages by its own switches, set
        // while the client still loads its module; a level's start has no menu.
        if (options.Level is null) await SwitchMenuPagesAsync(session, skipSplash: !options.Splash, cancel);
        // Never a silent wait in the menu: the start's stages are followed by the rows of the
        // extender's timeline (StartStages) where the build records the interface's pages, else
        // what holds the client is asked (GK.Gui.Stuck). The game's own first-launch and licence
        // screens are gone through in an instance (the owner's standing consent covers GateKit's
        // copies, never the installed game); anything else that keeps the game in the menu for
        // MenuLimit fails the start, naming it.
        var stages = await StartStages.OpenAsync(session, cancel);
        if (options.Splash)
        {
            await ReachMainMenuAsync(session, stages, started, cancel, stopOnSplash: true);
            Out.WriteLine($"on the splash screen after {started.Elapsed.TotalSeconds:0} s (GK.Cheat.Recover() on the UI context passes it)");
            return session;
        }
        if (options.MainMenu)
        {
            await ReachMainMenuAsync(session, stages, started, cancel);
            Out.WriteLine($"in the main menu after {started.Elapsed.TotalSeconds:0} s");
            return session;
        }
        await session.WaitStateAsync("Running", Side.Client, TimeSpan.FromSeconds(300), started, cancel,
            menuTime => stages is null ? MenuStepAsync(session, options, menuTime, cancel) : MenuStageAsync(session, stages, options, menuTime, cancel),
            lookEvery: stages is null ? null : TimeSpan.Zero);
        Out.WriteLine($"playable after {started.Elapsed.TotalSeconds:0} s");
        return session;
    }

    /// <summary>Waits for the bridge's first report of a start (null when it came), or for what says
    /// it will not come: the game process ends, or Script Extender's log shows the module loaded
    /// with no Lua of the bridge. The limit counts the time the start shows no progress - its
    /// runtime log not growing - and is <see cref="CopySettings.StartTimeout"/>; a busy machine
    /// that is slow but moving is waited for, up to four limits in all. A start that puts coverage
    /// counters on first (`slower`) writes nothing for tens of seconds: its limits are that many times longer.</summary>
    async Task<StartReading?> AwaitBridgeAsync(GameSession session, CancellationToken cancel, int slower = 1)
    {
        var since = Stopwatch.StartNew();
        var still = Stopwatch.StartNew();
        long length = -1;
        while (true)
        {
            if (session.State(Side.Client) is not null) return null;
            var log = RuntimeLogText();
            var reading = StartCheck.Read(log, GamePids().Count > 0, since.Elapsed, ModSettings.BridgeFolder, Layout.SeLogs);
            if (reading.Verdict is StartVerdict.Exited or StartVerdict.ModsOff) return reading;
            if ((log?.Length ?? -1) != length)
            {
                length = log?.Length ?? -1;
                still.Restart();
            }
            if (still.Elapsed >= slower * Settings.StartTimeout || since.Elapsed >= 4 * slower * Settings.StartTimeout) return reading;
            await Task.Delay(500, cancel);
        }
    }

    /// <summary>The text of this start's Script Extender runtime logs, or null when there is none.</summary>
    string? RuntimeLogText()
    {
        if (!Directory.Exists(Layout.SeLogs)) return null;
        var files = Directory.EnumerateFiles(Layout.SeLogs, RuntimeLogPrefix + "*.log").OrderBy(File.GetLastWriteTimeUtc).ToList();
        if (files.Count == 0) return null;
        try { return string.Concat(files.Select(FileRetry.ReadAllText)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>How long a start to the main menu may go from the bridge's first answer with no new page.</summary>
    static readonly TimeSpan PageLimit = TimeSpan.FromSeconds(300);

    /// <summary>A start that is to stop in the main menu: the splash screen and (in an instance) the
    /// first-launch screens are gone through, and the main menu is shown. With
    /// <paramref name="stopOnSplash"/> the splash screen is not passed and the start ends on it. With
    /// <paramref name="stages"/> each page is the timeline's row of it, looked at once when it comes;
    /// without, what holds the client is looked at until it is the page wanted.</summary>
    async Task ReachMainMenuAsync(GameSession session, StartStages? stages, Stopwatch started, CancellationToken cancel, bool stopOnSplash = false)
    {
        if (stages is null)
        {
            await ReachMainMenuByLooksAsync(session, started, cancel, stopOnSplash);
            return;
        }
        Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {started.Elapsed.TotalSeconds,5:0.0} s  {session.State(Side.Client)?.GameState ?? "?"} (the bridge answers)"));
        var wanted = stopOnSplash ? "splash screen" : "main menu";
        while (true)
        {
            var stage = await stages.NextAsync(session, PageLimit, cancel);
            if (stage.Full)
            {
                Out.WriteLine("  the extender's timeline is full: the rest of the start is followed by GK.Gui.Stuck");
                await ReachMainMenuByLooksAsync(session, started, cancel, stopOnSplash);
                return;
            }
            if (stage.Page is { } page) Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {started.Elapsed.TotalSeconds,5:0.0} s  page {page}"));
            if (stage.Page == "MainMenu" && stopOnSplash)
                throw new CopyException("the game shows its main menu and never showed its splash screen; the game is left running - `down` before trying again");
            if (stage.Page == "MainMenu" || (stage.Page == "SplashScreen" && stopOnSplash)) return;
            if (stage.Page is null && !stage.None) continue;   // the client left Menu: not a page
            // a page on the way (or none in the limit): looked at once - the splash screen passed, a first-launch screen gone through
            var (stuck, moved, _) = await LookInMenuAsync(session, cancel, passSplash: !stopOnSplash);
            if (stuck == "splash" && stopOnSplash) return;
            if (stage.None && !moved)
                throw new CopyException($"the game showed no new page in {PageLimit.TotalSeconds:0} s on its way to the {wanted} (GK.Gui.Stuck(): {stuck ?? "nothing"}); "
                    + "the game is left running for a look - `down` before trying again");
        }
    }

    /// <summary>As <see cref="ReachMainMenuAsync"/>, by looking: the client reaches Menu, then what
    /// holds it is asked (GK.Gui.Stuck) until it names the page wanted.</summary>
    async Task ReachMainMenuByLooksAsync(GameSession session, Stopwatch started, CancellationToken cancel, bool stopOnSplash)
    {
        await session.WaitStateAsync("Menu", Side.Client, TimeSpan.FromSeconds(300), started, cancel);
        var until = Stopwatch.StartNew();
        string? stuck = null;
        while (until.Elapsed < TimeSpan.FromSeconds(180))
        {
            (stuck, var moved, _) = await LookInMenuAsync(session, cancel, passSplash: !stopOnSplash);
            if (stuck == (stopOnSplash ? "splash" : "mainmenu")) return;
            if (stopOnSplash && stuck == "mainmenu")
                throw new CopyException("the game shows its main menu and never showed its splash screen; the game is left running - `down` before trying again");
            await Task.Delay(moved ? 250 : 1000, cancel);
        }
        throw new CopyException($"the game did not show its {(stopOnSplash ? "splash screen" : "main menu")} in 180 s of the menu state (GK.Gui.Stuck(): {stuck ?? "nothing"}); "
            + "the game is left running for a look - `down` before trying again");
    }

    // The game shows its first-launch pages while this switch is on; it is read when the Eula page
    // loads in Menu and turned off when the main menu's page loads (research/studies/startup-videos.md,
    // "The first launch").
    const string FirstLaunchSwitch = """
        local switches = Ext.Utils.GetGlobalSwitches()
        switches.ShowFirstTimeSetup = true
        return { on = switches.ShowFirstTimeSetup == true, state = tostring(Ext.Utils.GetGameState()) }
        """;

    // What holds the menu, once something does: answered when GK.Gui.Stuck() names a screen.
    const string FirstLaunchProbe = """
        return GK.Observe({}, function(_, final)
            local stuck, screen = GK.Gui.Stuck()
            if stuck or final then return { stuck = stuck, widget = screen and screen.widget } end
        end, WITHIN)
        """;

    // The game's switches for the pages before the main menu: `SkipSplashScreen` closes the "press
    // any key" page as it opens, `ShowFirstTimeSetup` off makes Eula go straight on
    // (research/studies/startup-videos.md, "The \"press any key\" page" and "The first launch").
    const string MenuPagesSwitch = """
        local switches = Ext.Utils.GetGlobalSwitches()
        if FIRST then switches.ShowFirstTimeSetup = false end
        switches.SkipSplashScreen = SKIP
        return { firstTimeSetup = switches.ShowFirstTimeSetup == true, skipSplash = switches.SkipSplashScreen == true,
            state = tostring(Ext.Utils.GetGameState()) }
        """;

    /// <summary>Sets the game's switches for the pages before the main menu at the bridge's first
    /// answer: the splash page skipped unless <paramref name="skipSplash"/> is false, the
    /// first-launch pages off in an instance (the installed game's are the player's). A switch the
    /// game does not take is said; the start's stages still pass any page that comes.</summary>
    async Task SwitchMenuPagesAsync(GameSession session, bool skipSplash, CancellationToken cancel)
    {
        // Research hook: client Lua run at the bridge's first answer, before anything else of the
        // start (GATEKIT_FIRST_LUA), its answer printed - a switch the game reads early is set here.
        if (Environment.GetEnvironmentVariable("GATEKIT_FIRST_LUA") is { Length: > 0 } early)
        {
            try { Out.WriteLine($"  GATEKIT_FIRST_LUA: {(await session.LuaAsync("client", early, TimeSpan.FromSeconds(20), cancel))?.ToJsonString()}"); }
            catch (CopyException e) { Out.WriteLine($"  GATEKIT_FIRST_LUA failed: {e.Message}"); }
        }
        var first = Instance is not null;
        var code = $"local FIRST, SKIP = {(first ? "true" : "false")}, {(skipSplash ? "true" : "false")}\n" + MenuPagesSwitch;
        JsonNode? answer;
        try { answer = await session.LuaAsync("client", code, TimeSpan.FromSeconds(20), cancel); }
        catch (CopyException e)
        {
            Out.WriteLine($"  the game's page switches were not set ({e.Message}); the pages are passed as they come");
            return;
        }
        var skipped = answer.Field("skipSplash")?.GetValue<bool>();
        var setup = answer.Field("firstTimeSetup")?.GetValue<bool>();
        if (skipped != skipSplash || (first && setup != false))
        {
            Out.WriteLine($"  the game's page switches read SkipSplashScreen {skipped?.ToString() ?? "nothing"}, ShowFirstTimeSetup {setup?.ToString() ?? "nothing"} after they were set; the pages are passed as they come");
            return;
        }
        Out.WriteLine($"  SkipSplashScreen {(skipSplash ? "on" : "off")}{(first ? ", ShowFirstTimeSetup off" : "")} at the first Lua (client in {answer.Field("state")})");
    }

    async Task SwitchFirstLaunchAsync(GameSession session, CancellationToken cancel)
    {
        var answer = await session.LuaAsync("client", FirstLaunchSwitch, TimeSpan.FromSeconds(20), cancel);
        if (answer.Field("on")?.GetValue<bool>() != true)
            throw new CopyException($"the game's switch ShowFirstTimeSetup did not read true after it was set (it reads {answer.Field("on")?.ToString() ?? "nothing"}, client in {answer.Field("state")})");
        Out.WriteLine($"  ShowFirstTimeSetup set at the first Lua (client in {answer.Field("state")})");
    }

    /// <summary>A first-launch start followed to its first page: with <paramref name="stages"/> the
    /// timeline's rows until the page `Eula` is entered (another page first fails the start), else
    /// the client's Menu state; then the page is confirmed standing (`GK.Gui.Stuck()` names it).
    /// Nothing is pressed.</summary>
    async Task ReachFirstLaunchAsync(GameSession session, StartStages? stages, Stopwatch started, CancellationToken cancel)
    {
        if (stages is null) await session.WaitStateAsync("Menu", Side.Client, TimeSpan.FromSeconds(300), started, cancel);
        while (stages is not null)
        {
            var stage = await stages.NextAsync(session, PageLimit, cancel);
            if (stage.Full) break;
            if (stage.Page is { } page) Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {started.Elapsed.TotalSeconds,5:0.0} s  page {page}"));
            if (stage.Page == "Eula") break;
            if (stage.Page is not null || stage.None)
                throw new CopyException($"the game {(stage.None ? $"showed no page in {PageLimit.TotalSeconds:0} s" : $"showed {stage.Page}")} instead of its first-launch page Eula; "
                    + "the game is left running for a look - `down` before trying again");
        }
        var code = string.Create(CultureInfo.InvariantCulture, $"local WITHIN = {PageLimit.TotalMilliseconds:0}\n") + FirstLaunchProbe;
        var seen = await session.LuaAsync("ui", code, PageLimit + TimeSpan.FromSeconds(30), cancel);
        var stuck = seen.Field("stuck")?.GetValue<string>();
        var widget = seen.Field("widget")?.GetValue<string>();
        if (stuck != "firstlaunch" || widget != "Eula")
            throw new CopyException($"the game stands on {widget ?? stuck ?? "nothing GK.Gui.Stuck() names"}, not on its first-launch page Eula; "
                + "the game is left running for a look - `down` before trying again");
    }

    /// <summary>
    /// What `gatekit` says to the game through its own environment.
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
        JsonFiles.WriteText(Layout.ModSettingsFile, change(text));
    }

    /// <summary>Instead of a backup: the instance's clone and profile are made ready for a start.
    /// The profile is the instance's own and thrown away, so nothing has to be put back.</summary>
    /// <param name="withMods">Bring the player's mod files into the copy (mods were named); a bare
    /// copy holds GateKit's paks only.</param>
    void PrepareInstance(string? save, bool allSaves, bool withMods)
    {
        var instance = Instance!;
        var realPublic = Path.Combine(Settings.Profile, "PlayerProfiles", "Public");
        // While the installed game is up, its mod list is the cut-down one of the copy that runs it;
        // the player's own list is then in that copy's backup.
        var kept = Path.Combine(Layout.SharedState, "backup", "modsettings.lsx");
        var sharedLoose = ReadLoose(Path.Combine(Layout.SharedState, "loose.json")).Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            var cache = Platform.ScriptExtenderCache(Settings.GameFolder);
            // A local build in the clone's bin makes the loader config the switcher's, whichever
            // state folder installed it (GATEKIT_STATE may name another): "no record" is not
            // "stock", and taking it for stock would drop DebugLoadSE and start the copy on the
            // cached stock extender.
            instance.SyncGame(sharedLoose, cache is null ? null : Platform.ToGamePath(cache),
                File.Exists(SeSwitch.Record) || File.Exists(SeSwitch.Dll));
            instance.SyncProfile(Settings.Profile, BridgeMods.Select(m => m.Pak).ToHashSet(StringComparer.OrdinalIgnoreCase), withMods);
            FileTree.CopyFile(File.Exists(kept) ? kept : Path.Combine(realPublic, "modsettings.lsx"), Layout.ModSettingsFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CopyException)
        {
            throw new CopyException($"instance {instance.Name}: {e.Message}\n(instances live in {Settings.InstancesRoot}; "
                + "GATEKIT_INSTANCES names another folder - writable, on the game's drive)", e);
        }
        var realSaves = Path.Combine(realPublic, "Savegames", "Story");
        // From the player's real profile: the save asked for when the copy has none of that name,
        // or all of them for a start without a level - merged, never over a newer one of the copy's.
        if (save is not null && Directory.Exists(Path.Combine(realSaves, save)) && !Directory.Exists(Path.Combine(Layout.Saves, save)))
            FileTree.CopyTree(Path.Combine(realSaves, save), Path.Combine(Layout.Saves, save));
        else if (allSaves && Directory.Exists(realSaves))
            SaveStore.Merge(realSaves, Layout.Saves);
        Directory.CreateDirectory(Layout.State);
        // What the copy's executables are, and whether they are the installed game's verified ones.
        try { GameIntegrity.RecordCopy(Layout, Platform); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or GateKit.Json.JsonProblemException)
        {
            Out.WriteLine($"note: the copy's fingerprint was not recorded: {e.Message}");
        }
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

    /// <summary>The log file Script Extender writes its console output to (LogRuntime).</summary>
    public const string RuntimeLogPrefix = "Extender Runtime";

    /// <summary>The log file Script Extender writes the Osiris trace to (EnableLogging).</summary>
    public const string StoryLogPrefix = "Osiris Runtime";

    /// <summary>Script Extender settings for a run: no console window of its own - its output goes
    /// to a log file in the copy's folder, which `logs` reads - developer mode, and on request the
    /// Osiris trace beside it; startup videos only when asked for.</summary>
    /// <summary>`GATEKIT_FAST_LOAD=1`: every start of this process asks for fast loads (`test --fast-load`
    /// sets it, so a run's every start and the loads in it have them).</summary>
    public static bool FastLoadAsked() => Environment.GetEnvironmentVariable("GATEKIT_FAST_LOAD") == "1";

    /// <summary>The switches of GateKit's build that make a load fast (Extender/Shared/FastLoad.h in the
    /// fork), all written by every start, on or off together.</summary>
    public static readonly string[] FastLoadSwitches =
        ["FastLoadClientFrames", "FastLoadServerTick", "FastLoadStreamingWait", "FastLoadFades"];

    /// <summary>`GATEKIT_HEADLESS=1`: every start of this process is headless (`test --headless` sets it).</summary>
    public static bool HeadlessAsked() => Environment.GetEnvironmentVariable("GATEKIT_HEADLESS") == "1";

    /// <summary>A headless game's pace of skipped frames in microseconds (RenderCutSkipPaceUs):
    /// `GATEKIT_HEADLESS_SKIP_PACE_US`, else the default; 0 is off.</summary>
    public static int HeadlessSkipPaceUs() =>
        int.TryParse(Environment.GetEnvironmentVariable("GATEKIT_HEADLESS_SKIP_PACE_US"), out var us) && us >= 0 ? us : DefaultSkipPaceUs;

    /// <summary>Off (round 7). A blind start of Basic_Level_A at 4 ms: CPU by Running 71.2 -> 36.7 s,
    /// Running 18.7 -> 17.3 s (16.7 ms: 34.4 s, Running 18.9 s). But the pace also slows an in-session
    /// load (the client, uncapped there, waits on the main thread each tick): tests/headless/arrival
    /// recorded 27 ambient rock throws at 4 ms against 13 with the pace off and 13-15 natively - the
    /// new level's server runs longer before the client is ready. Kept for a blind start by hand.</summary>
    public const int DefaultSkipPaceUs = 0;

    /// <summary>The switches of GateKit's build a headless game turns on beyond the fast loads, each
    /// checked against the logic by the night's W3 (`logic-diff` of tests/headless): the Lua
    /// collector held while loading, nothing drawn from the client's first Running on (the start
    /// drawn: `RenderCutFromRunning`, written beside these - see ConfigureExtender), the tick waits slept,
    /// PrepareRunning's most wait kept (its gate on the level's start dialogue: round 5).</summary>
    public static readonly string[] HeadlessSwitches =
        ["FastLoadLuaGC", "RenderCut", "RenderCutSkip", "IdleSleep", "FastLoadStreamingGate"];

    /// <summary>`GATEKIT_HEADLESS_EXCEPT=IdleSleep,KeepWindowOffScreen`: a headless (or fast-load) start
    /// with these of its switches left off - for finding which cut a difference of the logic comes from, one at a time
    /// (the night's W3, round 5: a dialogue that never showed headless).</summary>
    public static bool HeadlessExcepted(string name) =>
        (Environment.GetEnvironmentVariable("GATEKIT_HEADLESS_EXCEPT") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(name, StringComparer.OrdinalIgnoreCase);

    void ConfigureExtender(bool storyLog, bool startupVideos, string? coverStart, bool directSaveLoad = false, bool fastLoad = false,
        bool headless = false)
    {
        fastLoad |= headless;
        WriteLayoutData();
        FileTree.TryRemoveTree(Layout.SeLogs);
        Directory.CreateDirectory(Layout.SeLogs);
        JsonFiles.Edit(Layout.SeSettings, s =>
        {
            s["CreateConsole"] = false;
            // Developer mode, unless the run is to see the extender as a player's game has it
            // (GATEKIT_SE_DEVELOPER_MODE=0): what a mod's user gets, and what he does not.
            s["DeveloperMode"] = Environment.GetEnvironmentVariable("GATEKIT_SE_DEVELOPER_MODE") != "0";
            s["LogRuntime"] = true;
            s["EnableLogging"] = storyLog;
            s["LogDirectory"] = Platform.ToGamePath(Layout.SeLogs);
            // A copy `gatekit` starts does not pull its own window to the front. The SDL hint in
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
            // A headless game has no window to look at either: off the screen from its creation.
            s["KeepWindowOffScreen"] = (headless && !HeadlessExcepted("KeepWindowOffScreen"))
                || new DeckRecord(Settings.StateRoot).Running() is not null;
            // Script Extender opens its console whatever CreateConsole says when Shift is down as
            // the game starts - a capital letter typed elsewhere is enough, and consoles appeared
            // on the owner's desktop (D2 of the day run). GateKit's own build reads this switch;
            // a build without it ignores the key. A console that opens anyway is hidden by the
            // start's guard (KeepForeground), never closed.
            s["ShiftOpensConsole"] = false;
            // GateKit's build composes the startup videos of the game and the mods; off, it hides
            // them all. A build without the switch ignores it.
            s["StartupVideos"] = startupVideos;
            // Coverage of the start, before any Lua (developer mode): every run says it, so a
            // later run without it starts without counters.
            s["CoverageAtStart"] = coverStart is null ? "" : Platform.ToGamePath(Path.GetFullPath(coverStart));
            // A save asked for is loaded without the main menu (GateKit's build: the menu's states
            // taken out of the client's queue once the game's Init has queued the save's load).
            // Every run says it, so a later start without --save goes through the menu again. A
            // build without the switch ignores it.
            s["DirectSaveLoad"] = directSaveLoad;
            // Loads without the game's waits (GateKit's build; the night's W3 measured an
            // in-session load bound by the ticks, not by its work): every run says it, so a later
            // start without the option loads at the game's pace. A build without them ignores them.
            foreach (var name in FastLoadSwitches) s[name] = fastLoad && !HeadlessExcepted(name);
            // A headless game (GateKit's build): every run says it, so a later start draws again.
            foreach (var name in HeadlessSwitches) s[name] = headless && !HeadlessExcepted(name);
            // The start of a headless game is drawn (round 5's RenderCutFromRunning): the interface's
            // controls are laid out only by a drawn frame, and verbs that walk them (a hotbar slot by
            // its action) find nothing in a game never drawn (round 6: aim/not_taken,
            // clean/attack_in_fight). GATEKIT_HEADLESS_BLIND_START=1 draws nothing from the process
            // start: dialogues, fights and loads stay 1:1 there (the bridge has the party replicated
            // so the view models learn who is playable), ~790 MB less. Round 7: the hotbar verbs failed
            // because the local player had no characters (not a layout); with the user's, hotbar and
            // party components replicated too they pass, and the 1:1 suite holds - one selftest
            // (fixtures_and_attention, a reaction's attention) still fails blind, so not the default yet.
            s["RenderCutFromRunning"] = headless && Environment.GetEnvironmentVariable("GATEKIT_HEADLESS_BLIND_START") != "1";
            // A skipped frame returns at once: with nothing drawn the main thread looped ~1,600 updates
            // a second through the start and loads (round 6: 88 vs 68 CPU-s by Running). The pace
            // sleeps a skipped update out to this many microseconds (GateKit's build, round 7;
            // GATEKIT_HEADLESS_SKIP_PACE_US overrides, 0 turns it off). Every run says it.
            s["RenderCutSkipPaceUs"] = headless ? HeadlessSkipPaceUs() : 0;
            // Measuring (GateKit's build, round 7): VirtualAlloc calls of at least N MB written into the
            // start's timeline with their callers - GATEKIT_LARGE_ALLOC_MB, else off. Every run says it.
            s["LargeAllocTraceMB"] = int.TryParse(Environment.GetEnvironmentVariable("GATEKIT_LARGE_ALLOC_MB"), out var largeMb) && largeMb > 0 ? largeMb : 0;
            // A copy that dies leaves a dump in its own folder and no reporter window
            // (GateKit's build; `crashes` reads them). The installed game's settings stay the player's.
            if (Instance is not null)
            {
                // A copy studies the game as it is: Script Extender's patch of the game's code for
                // achievements with mods (ls::ModuleSettings::IsModded, esv::SavegameManager::ThrowError)
                // stays off there. The installed game's setting is the player's.
                s["EnableAchievements"] = false;
                // The treasure generation's wrapper (GateKit's build), inert until a test pins a seed
                // (GK.Rolls.PinSources): a copy's differential checks get the same loot whatever the
                // order of a level's treasure requests (the night's W3, round 6).
                s["TreasurePin"] = true;
                Directory.CreateDirectory(CrashDumps.Folder(Layout));
                s["CrashDumpDirectory"] = Platform.ToGamePath(CrashDumps.Folder(Layout));
                s["LaunchCrashReporter"] = false;
            }
        });
    }

    /// <summary>The loader (DWrite.dll) turns its own debug console on with Shift too; it reads the same
    /// switch from its config. And it asks Script Extender's update server at every start (a window
    /// "looking for updates", or "update failed" offline) although a copy loads the build GateKit
    /// installed (DebugLoadSE): DisableUpdates leaves that out. Called after the run's build is put in
    /// place - installing a build writes the loader's config anew and would drop these. Only in an
    /// instance: the installed game's file is the player's. Written as a new file, never in place - a
    /// file of the clone may be a hard link to the installed game's.</summary>
    void ConfigureLoader()
    {
        if (Instance is not null)
        {
            var loader = Path.Combine(Layout.Bin, "ScriptExtenderUpdaterConfig.json");
            if (File.Exists(loader))
            {
                var config = JsonFiles.ReadObject(loader);
                static bool IsTrue(System.Text.Json.Nodes.JsonNode? node, bool value) =>
                    node is JsonValue v && v.TryGetValue<bool>(out var b) && b == value;
                if (!IsTrue(config["ShiftOpensConsole"], false) || !IsTrue(config["DisableUpdates"], true))
                {
                    config["ShiftOpensConsole"] = false;
                    config["DisableUpdates"] = true;
                    File.Delete(loader);
                    JsonFiles.WriteObject(loader, config);
                }
            }
        }
    }

    // --- down ---

    /// <summary>Closes the game and puts everything back. Returns the line to print.
    /// A game copy of GateKit's own (an instance) keeps the Script Extender build `se install` gave
    /// it: putting stock back there only made every restart start without the fork (N17 of the third
    /// coverage run). The player's installed game does get stock back - it is theirs, and the
    /// updater, crash reports and other mods expect the signed build - and so does a copy with
    /// <paramref name="stock"/>. A dll still held by a game that is slow to exit is said, not an error:
    /// the loader's switch is already off by then, so the next start loads stock regardless.</summary>
    public string Down(bool stock = false)
    {
        RequireNamed("down");
        // A copy another checkout started may be stopped from here (the person asked for it by
        // name), but it is said whose it was, so nobody's run ends without a trace.
        var other = Owner()["checkout"]?.ToString() is { } owner && GamePids().Count > 0
            && !string.Equals(owner, CopySettings.RepositoryRoot(), StringComparison.Ordinal) ? OwnerText() : null;
        KillGame();
        if (Instance is null) new ProfileBackup(Layout, Platform).Restore();
        string? note = null;
        if (Instance is null || stock)
        {
            try { SeSwitch.Remove(TimeSpan.FromSeconds(60)); }
            catch (CopyException e) { note = e.Message; }
        }
        UnlinkAll();
        CleanStaleTestFiles();
        var line = Instance is not null
            ? $"instance {Instance.Name}: game closed" + (stock ? ", stock Script Extender back" : SeSwitch.Current().Fork ? ", its Script Extender build kept" : "")
            : "game closed, saves and mod list restored";
        if (other is not null) line += $" (it was {other})";
        return note is null ? line : line + "\nnote: " + note;
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
        yield return new EventLogReader(Layout.Channel).KindsLine();
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

    /// <summary>Creates or refreshes this home's instance without starting it.</summary>
    public string CreateInstance()
    {
        if (Instance is null) throw new CopyException("`instances create` needs an instance name");
        if (GamePids().Count > 0) throw new CopyException($"instance {Instance.Name} is running; run `down` first");
        PrepareInstance(null, allSaves: false, withMods: false);
        return $"instance {Instance.Name} ready in {Instance.Root}";
    }

    /// <summary>Gives this copy another name. The game must not be running: a copy is a folder,
    /// and a folder with a game open in it does not move.</summary>
    public string Rename(string newName)
    {
        if (Instance is null) throw new CopyException("`rename` needs an instance: GATEKIT_INSTANCE or --instance <name>");
        if (GamePids().Count > 0) throw new CopyException($"instance {Instance.Name} is running; run `down` first");
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
        if (Instance is null) throw new CopyException("`destroy` needs an instance: GATEKIT_INSTANCE or --instance <name>");
        Down();
        Instance.Destroy();
        return $"instance {Instance.Name} deleted";
    }

    // --- exec, key, shot, logs ---

    /// <summary>Runs Lua on "client", "server" or "ui"; "@path" runs a file.</summary>
    public Task<JsonNode?> ExecAsync(string side, string code, TimeSpan? timeout = null)
    {
        RequireNamed("exec");
        if (code.StartsWith('@')) code = File.ReadAllText(code[1..], Encoding.UTF8); // UTF8 drops a BOM
        return Session().LuaAsync(side, code, timeout);
    }

    int FirstPid() => GamePids() is { Count: > 0 } pids ? pids[0] : throw new CopyException("the game is not running");

    /// <summary>Presses a key in the game without bringing its window forward. The bridge does it
    /// through the game's own input system, which needs no window and works on every platform;
    /// only when the bridge is not there does the window get the key from the operating system.</summary>
    public void Key(string name, double? hold = null)
    {
        RequireNamed("key");
        var code = GameKeys.Parse(name) ?? throw new CopyException($"unknown key '{name}'; known: {GameKeys.Known}");
        if (hold is < 0 or > 30) throw new CopyException($"a hold is 0 to 30 seconds, not {hold}");
        var session = Session();
        if (session.State(Side.Client) is not null)
        {
            if (hold is { } seconds && seconds > 0)
            {
                // N49 of the third coverage run: a tap does not move the camera; held down in the
                // game's own input and let go on the game's timer, then waited for here, so the
                // call ends with the key up.
                var lua = string.Create(CultureInfo.InvariantCulture, $"return GK.Key(\"{name}\", nil, {seconds})");
                session.LuaAsync("client", lua, TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                Thread.Sleep(TimeSpan.FromSeconds(seconds + 0.2));
                return;
            }
            session.LuaAsync("client", $"return GK.Key(\"{name}\")", TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            return;
        }
        if (hold is > 0) throw new CopyException("a held key needs the bridge (GateKit's Script Extender build answering); the window's own key messages only tap");
        if (!Platform.CanPostKeys) throw new CopyException(Platform.WhyNot("key") + "; the bridge is not answering either (is the game up with GateKit's Script Extender build?)");
        try { Platform.PostKey(FirstPid(), code); }
        catch (IOException e) { throw new CopyException("key not sent: " + e.Message, e); }
    }

    /// <summary>A screenshot of the game (JPEG unless the name ends in .png), scaled to
    /// <paramref name="width"/> at most. The bridge takes it inside the game, off the frame the
    /// renderer is about to show and before anything of the extender's is drawn over it, which
    /// needs no window and works on every platform; the window is only photographed when the
    /// bridge cannot do it. With <paramref name="overlay"/> the frame is taken after the extender's
    /// own layer is drawn (its windows, a picture of GK.Show); only the game can take that one.</summary>
    public string Shot(string output, int width = 1150, bool overlay = false)
    {
        var path = Path.GetFullPath(output);
        var picture = ShotFromGame(overlay);
        if (picture is null)
        {
            if (overlay) throw new CopyException("the game took no picture with the extender's layer in it: that needs the bridge "
                + "answering, GateKit's newest Script Extender build under the DX11 renderer (gatekit se status), and no other picture on its way");
            if (!Platform.CanCaptureWindow) throw new CopyException(Platform.WhyNot("shot")
                + "; the bridge could not take one either (is the game up with GateKit's Script Extender build?)");
            try { picture = Platform.CaptureWindow(FirstPid()); }
            catch (IOException e) { throw new CopyException("screenshot failed: " + e.Message, e); }
        }
        ImageFiles.Save(picture, path, width);
        return path;
    }

    /// <summary>How long the game is given to hand the frame over before the window is tried instead.</summary>
    static readonly TimeSpan ShotPatience = TimeSpan.FromSeconds(10);

    /// <summary>The frame as the game itself writes it into the channel folder, or null when the
    /// bridge is not there or its renderer cannot read the frame.</summary>
    Screenshot? ShotFromGame(bool overlay = false)
    {
        var session = Session();
        if (session.State(Side.Client) is null) return null;
        var name = $"shot_{Environment.ProcessId}_{Stopwatch.GetTimestamp()}.png";
        var file = Path.Combine(Layout.Channel, name);
        try
        {
            var asked = session.LuaAsync("client", $"return GK.Shot(\"{ModSettings.BridgeFolder}/{name}\"{(overlay ? ", true" : "")})",
                TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            // the game says why it takes none (a headless game draws nothing: its last frame would be
            // a stale picture, and the window's photo the same) - never a picture then
            if (asked is JsonValue said && said.TryGetValue<string>(out var why)) throw new CopyException("no picture: " + why);
            if (asked is not JsonValue value || !value.TryGetValue<bool>(out var ok) || !ok) return null;
            var waited = Stopwatch.StartNew();
            while (!File.Exists(file))
            {
                if (waited.Elapsed > ShotPatience) throw new CopyException(
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
    /// A game this home started writes both to log files and has no console window at all. A game
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
            throw new CopyException($"no {RuntimeLogPrefix} log in {Layout.SeLogs}: start the game with `up`, "
                + "or turn LogRuntime on in the game's ScriptExtenderSettings.json");
        try { return Platform.ReadConsole(FirstPid()); }
        catch (IOException e)
        {
            throw new CopyException($"{e.Message}\n(a game started by `up` writes its output to {Layout.SeLogs} "
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

    /// <summary>Files a test run that was killed left in the copy (<see cref="TestFiles"/>) are taken out.</summary>
    public void CleanStaleTestFiles()
    {
        if (new TestFiles(Layout).CleanStale() is { } line) Out.WriteLine(line);
    }

    /// <summary>The files of a test, or of tests that share them, written into the copy before its
    /// start (<see cref="TestFiles.Place"/>); a copy whose clone is not made yet is made first.</summary>
    public void PlaceTestFiles(string tests, IReadOnlyList<(string Destination, string Source)> files)
    {
        if (Instance is not null && !Directory.Exists(Path.Combine(Layout.Game, "bin")))
        {
            if (GamePids().Count > 0) throw new CopyException($"files: instance {Instance.Name} has no game folder and its game is running");
            PrepareInstance(null, allSaves: false, withMods: false);
        }
        new TestFiles(Layout).Place(tests, files);
    }

    /// <summary>In an instance most of Data is links to the real game: before loose files go into
    /// <paramref name="folder"/> the links on the way become real folders, and the place is checked
    /// to be inside the instance.</summary>
    void OwnInInstance(string folder)
    {
        if (Instance is null) return;
        Instance.OwnFolder(folder);
        if (!Instance.Inside(folder)) throw new CopyException($"{folder} is not inside instance {Instance.Name}; nothing was written");
    }

    /// <summary>Registers every mod under &lt;modRoot&gt;/Mods/*/ScriptExtender/Lua for hot reload.</summary>
    public IReadOnlyList<string> Link(string modRoot)
    {
        RequireNamed("link");
        var root = Path.GetFullPath(modRoot);
        var mods = Path.Combine(root, "Mods");
        var found = Directory.Exists(mods)
            ? Directory.EnumerateDirectories(mods).Where(d => Directory.Exists(Path.Combine(d, "ScriptExtender", "Lua"))).Order(StringComparer.Ordinal).ToList()
            : [];
        if (found.Count == 0) throw new CopyException($"no Mods/<Folder>/ScriptExtender/Lua under {root}");
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
        RequireNamed("reload");
        if (LooseSources().Count == 0) throw new CopyException("nothing is linked; run `link <mod folder>` first");
        var watch = Stopwatch.StartNew();
        // A call in flight is let finish first (N37 of the third run: a bridge-reload during a
        // runner's call failed with a stack trace and did not reload). Up to 30 s, then it goes on.
        var session = Session();
        var waited = Stopwatch.StartNew();
        while (waited.Elapsed < TimeSpan.FromSeconds(30)
            && session.Bridge.Pending(Side.Client) + session.Bridge.Pending(Side.Server) > 0)
        {
            if (waited.Elapsed < TimeSpan.FromSeconds(0.3)) Out.WriteLine("a call is in flight; the reload waits for it (at most 30 s)");
            await Task.Delay(250);
        }
        SyncLoose();
        WriteLayoutData();
        await session.ResetLuaAsync();
        return watch.Elapsed;
    }

    /// <summary>The record's layouts for the bridge's named reads (LayoutData); a failure to write
    /// them is said and does not stop a start.</summary>
    void WriteLayoutData()
    {
        try
        {
            if (LayoutData.Write(CopySettings.RepositoryRoot(), Layout.Channel) is { } count)
                Out.WriteLine($"layouts for named reads: {count} components");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Out.WriteLine($"layouts for named reads not written: {e.Message}");
        }
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
        RequireNamed("watch");
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
            catch (CopyException e) { Out.WriteLine($"{now} reload failed: {e.Message}"); }
        }
    }

    // --- Script Extender switch ---

    public ScriptExtenderSwitch.Status SeStatus() => SeSwitch.Current();

    /// <summary>What `se install` did: the build recorded as the default of every start that names
    /// none, and the game it was written into - a copy, or the installed game only when that was
    /// named (<c>Installed</c> is null when no game was touched).</summary>
    public sealed record SeChoice(string Recorded, string? Into, ScriptExtenderSwitch.Status? Installed);

    /// <summary>Makes <paramref name="folder"/>'s build the default and, in an instance, installs it
    /// into the copy's bin. The installed game is written only when <paramref name="installedGame"/>
    /// names it: without an instance and without it, nothing of any game changes.</summary>
    public SeChoice SeInstall(string folder, bool installedGame = false)
    {
        var full = Path.GetFullPath(folder);
        var dll = File.Exists(full) ? full : Path.Combine(full, "BG3ScriptExtender.dll");
        if (!File.Exists(dll)) throw new CopyException($"no BG3ScriptExtender.dll in {folder}");
        if (installedGame && Instance is not null)
            throw new CopyException("--installed-game names the installed game; leave out --instance, or leave out --installed-game to install into the copy");
        var write = Instance is not null || installedGame;
        if (write && GamePids().Count > 0) throw new CopyException("the game is running; run `down` first");
        var status = write ? SeSwitch.Install(full) : null;
        // The build later runs load when nothing names one: chosen once, not exported by every shell.
        CopySettings.RememberScriptExtender(Settings.StateRoot, full);
        return new SeChoice(full, write ? Layout.Bin : null, status);
    }

    /// <summary>
    /// Installs the build pinned in se-release.json, downloading it once into the home's se-builds
    /// folder. A build already fetched for that tag is reused.
    /// </summary>
    public async Task<SeChoice> SeInstallPinnedAsync(bool installedGame = false, CancellationToken cancel = default)
    {
        if ((Instance is not null || installedGame) && GamePids().Count > 0) throw new CopyException("the game is running; run `down` first");
        var repo = CopySettings.RepositoryRoot()
            ?? throw new CopyException("se-release.json belongs to the GateKit repository; run gatekit from it, or use `se install <folder>`");
        var pinFile = Path.Combine(repo, "se-release.json");
        if (!File.Exists(pinFile))
            throw new CopyException($"{pinFile} is missing; use `se install <folder>` with a build of your own");
        var pin = ScriptExtenderRelease.Pin.Read(pinFile);
        var into = Path.Combine(Settings.SeBuilds ?? Path.Combine(Settings.StateRoot, "se"), pin.Tag);
        if (!File.Exists(Path.Combine(into, "BG3ScriptExtender.dll")))
            await new ScriptExtenderRelease().FetchAsync(pin, into, Out, cancel);
        else
            Out.WriteLine($"{pin.Tag} is already here");
        return SeInstall(into, installedGame);
    }

    /// <summary>The stock build again in a copy, or in the installed game when <paramref name="installedGame"/>
    /// names it; without either nothing is changed and it says so.</summary>
    public ScriptExtenderSwitch.Status SeRemove(bool installedGame = false)
    {
        if (installedGame && Instance is not null)
            throw new CopyException("--installed-game names the installed game; leave out --instance, or leave out --installed-game to remove from the copy");
        if (Instance is null && !installedGame)
            throw new CopyException("`se remove` without --instance would change the installed game: name it with --installed-game, or name a copy with --instance");
        return SeSwitch.Remove();
    }
}
