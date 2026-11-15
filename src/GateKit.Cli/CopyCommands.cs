using System.CommandLine;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using GateKit.Platform;
using GateKit.Copies;

namespace GateKit.Cli;

/// <summary>The commands of a game copy: up, down, exec, ... (docs/contracts/cli.md, section 2).</summary>
public static class CopyCommands
{
    /// <summary>`--instance NAME`, valid before or after any command.</summary>
    public static readonly Option<string?> InstanceOption = new("--instance")
    {
        Description = "work on this game copy: a cloned game folder with a private profile, not the installed game (also GATEKIT_INSTANCE)",
        Recursive = true,
    };

    /// <summary>`--installed-game`, valid before or after any command: names the installed game, the
    /// only way a command with no instance writes into it (<see cref="CopyLayout.RequireNamed"/>).</summary>
    public static readonly Option<bool> InstalledGameOption = new(CopySettings.InstalledGameOption)
    {
        Description = "work on the installed game itself and the player's own profile; a command that writes refuses the installed game without it",
        Recursive = true,
    };

    static readonly JsonSerializerOptions ResultJson = new()
    {
        WriteIndented = true,
        IndentSize = 1,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>All copy commands, to be added to the root.</summary>
    public static IEnumerable<Command> Create()
    {
        yield return DoctorCommand();
        yield return GameCommands.Create();
        yield return Build();
        yield return Up();
        yield return Down();
        yield return Simple("status", "the copy, its game processes, Script Extender build, last heartbeats", s =>
        {
            foreach (var line in s.Status()) s.Out.WriteLine(line);
        });
        yield return Instances();
        yield return Crashes();
        yield return SaveCommand();
        yield return Simple("destroy", "close this copy's game and delete its clone, profile and records", s => s.Out.WriteLine(s.Destroy()));
        yield return Exec();
        yield return Shot();
        yield return Key();
        yield return Sound();
        yield return Link();
        yield return Simple("reload", "copy the linked Lua into the game and restart Lua in place", async s =>
            s.Out.WriteLine($"reloaded in {Seconds(await s.ReloadAsync())} s"));
        yield return BridgeReload("bridge-reload");
        yield return Bridge();
        yield return Logs();
        yield return Watch();
        yield return Simple("checkpoint", "save the world as it is now, to return to with `reset`", async s =>
            s.Out.WriteLine("checkpoint: " + await s.Session().CheckpointAsync()));
        yield return Reset();
        yield return ScriptExtender();
        yield return Simple("firewall", "which game copies the firewall lets listen, and the one line that covers the rest", s =>
        {
            foreach (var line in FirewallReport.Read(s.Settings, s.Platform).Lines()) s.Out.WriteLine(line);
        });
    }

    /// <summary>`doctor` resolves the settings itself, since it must report a failure to resolve them.</summary>
    static Command DoctorCommand()
    {
        var json = new Option<bool>("--json") { Description = $"the checks and the locations as JSON ({HomeReport.Schema})" };
        var command = new Command("doctor", "check that this machine has what GateKit needs, and say how to fix what it has not; "
            + "list every location GateKit keeps, where it comes from, and what is out of place") { json };
        command.SetAction(parse =>
        {
            var result = Doctor.Examine(GamePlatform.Current, parse.GetValue(InstanceOption));
            if (parse.GetValue(json))
            {
                var node = result.Home?.ToJson(result.Checks) ?? new System.Text.Json.Nodes.JsonObject { ["schema"] = HomeReport.Schema };
                Console.Out.WriteLine(node.ToJsonString(new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }));
                return result.Checks.Any(c => c.Verdict == Doctor.Verdict.Bad) ? 1 : 0;
            }
            var code = Doctor.Report(result.Checks, Console.Out);
            result.Home?.Write(Console.Out);
            return code;
        });
        return command;
    }

    static string Seconds(TimeSpan span) => span.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>Runs an action on a copy: settings resolved, errors printed as "ERROR: ..." with exit code 1.</summary>
    static async Task<int> Run(ParseResult parse, Func<Copies.GameCopy, Task> action)
    {
        try
        {
            var platform = GamePlatform.Current;
            var settings = CopySettings.Resolve(platform, instanceOverride: parse.GetValue(InstanceOption),
                installedGame: parse.GetValue(InstalledGameOption));
            var copy = new Copies.GameCopy(settings, platform, Console.Out);
            await action(copy);
            return 0;
        }
        catch (CopyException e)
        {
            Console.Error.WriteLine("ERROR: " + e.Message);
            return 1;
        }
        catch (PlatformCapabilityException e)
        {
            Console.Error.WriteLine("ERROR: " + e.Message);
            return 1;
        }
        // A file the game or another call holds for a moment, a lock that did not come: a sentence
        // and exit 1, never a stack trace (N37 of the third run, bridge-reload beside a call).
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or TimeoutException)
        {
            Console.Error.WriteLine("ERROR: " + e.Message + " (a file of the channel or the copy was held by the game or "
                + "another call; try again in a moment)");
            return 1;
        }
    }

    static Command Simple(string name, string description, Action<Copies.GameCopy> action) =>
        Simple(name, description, s => { action(s); return Task.CompletedTask; });

    static Command Simple(string name, string description, Func<Copies.GameCopy, Task> action)
    {
        var command = new Command(name, description);
        command.SetAction((parse, _) => Run(parse, action));
        return command;
    }

    static Command Crashes()
    {
        var json = new Option<bool>("--json") { Description = "the dumps as JSON" };
        var limit = new Option<int>("--limit") { Description = "at most this many, newest first", DefaultValueFactory = _ => 10 };
        var command = new Command("crashes", "the crash dumps this copy left (GateKit's Script Extender writes them into the copy's crash-dumps folder), newest first: the exception, the faulting address as module+0xRVA and the function there by `decomp at`") { json, limit };
        command.SetAction((parse, _) => Run(parse, s =>
        {
            var dumps = CrashDumps.Read(CrashDumps.Folder(s.Layout)).Take(parse.GetValue(limit)).Select(d => CrashDumps.Named(d, CrashDumps.ExtenderPdbs(s.Layout))).ToList();
            if (parse.GetValue(json))
                s.Out.WriteLine(GateKit.Json.JsonText.Format(new System.Text.Json.Nodes.JsonArray([.. dumps.Select(d => (System.Text.Json.Nodes.JsonNode)d.ToJson())])));
            else if (dumps.Count == 0) s.Out.WriteLine("no crash dumps in " + CrashDumps.Folder(s.Layout));
            else foreach (var d in dumps) s.Out.WriteLine(d.Line());
            return Task.CompletedTask;
        }));
        return command;
    }

    static Command Down()
    {
        var stock = new Option<bool>("--stock") { Description = "also put the stock Script Extender back into this copy (a copy otherwise keeps the build `se install` gave it; the installed game always gets stock back)" };
        var command = new Command("down", "close the game, restore saves and mod list; the installed game also gets the stock Script Extender back") { stock };
        command.SetAction((parse, _) => Run(parse, s => { s.Out.WriteLine(s.Down(parse.GetValue(stock))); return Task.CompletedTask; }));
        return command;
    }

    static Command Build()
    {
        var command = new Command("build", "pack the bridge mods (GateKit, Weave) into .paks with the built-in LSLib and install them into the profile");
        command.SetAction((parse, _) => Run(parse, s => { foreach (var pak in s.Build()) s.Out.WriteLine("installed " + pak); return Task.CompletedTask; }));
        return command;
    }

    static Command Up()
    {
        var level = new Option<string?>("--level") { Description = "start a fresh test game in MODULE:LEVEL, e.g. GustavX:TUT_Avernus_C (base game and bridge only)" };
        var save = new Option<string?>("--save") { Description = "load this save (its folder name under Savegames/Story) instead of the last one" };
        var mods = new Option<string?>("--mods") { Description = "comma-separated mod folders to load besides the base game (default: none in a cloned copy or with --level; the installed game without --level keeps your own list)" };
        var storyLog = new Option<bool>("--story-log") { Description = "also write Script Extender's logs, the Osiris trace included (see `logs --story`)" };
        var mainMenu = new Option<bool>("--main-menu") { Description = "stop in the main menu instead of loading the last save (first-launch screens gone through in a cloned copy, never in the installed game)" };
        var gameArgs = new Argument<string[]>("game-args") { Description = "extra game arguments, after `--`", Arity = ArgumentArity.ZeroOrMore };
        var command = new Command("up", "back up, configure, start the game, wait until it is playable") { level, save, mods, storyLog, mainMenu, gameArgs };
        command.SetAction((parse, cancel) => Run(parse, async s =>
        {
            var modList = parse.GetValue(mods) is { } text ? text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : null;
            await s.UpAsync(new UpOptions(parse.GetValue(level), parse.GetValue(save), modList, parse.GetValue(storyLog), parse.GetValue(gameArgs),
                parse.GetValue(mainMenu)), cancel);
        }));
        return command;
    }

    /// <summary>`save list|export|import|copy`: a copy's saves, and moving one to another copy
    /// (docs/contracts/cli.md 2.13).</summary>
    static Command SaveCommand()
    {
        var command = new Command("save", "a copy's saves, and bringing one copy's save into another (then `up --save NAME`)");

        var list = new Command("list", "the saves of this copy, newest first, across its player profiles");
        list.SetAction((parse, _) => Run(parse, s =>
        {
            foreach (var save in SaveStore.List(s.Layout.Profile))
                Console.Out.WriteLine($"{save.Written.ToLocalTime():yyyy-MM-dd HH:mm:ss}  {save.Name}  ({save.Player})");
            return Task.CompletedTask;
        }));
        command.Subcommands.Add(list);

        var name = new Argument<string>("name") { Description = "the save's folder name (as `save list` shows it)" };
        var output = new Option<string>("--out") { Description = "the folder the save folder is written into", Required = true };
        var export = new Command("export", "write one save of this copy to <out>/<name>") { name, output };
        export.SetAction((parse, _) => Run(parse, s =>
        {
            Console.Out.WriteLine("exported to " + SaveStore.Export(s.Layout.Profile, parse.GetValue(name)!, parse.GetValue(output)!));
            return Task.CompletedTask;
        }));
        command.Subcommands.Add(export);

        var folder = new Argument<string>("folder") { Description = "a save folder (Savegames/Story/<name>), e.g. what `save export` wrote" };
        var import = new Command("import", "put a save folder into this copy (every player profile it has), for `up --save NAME`") { folder };
        import.SetAction((parse, _) => Run(parse, s =>
        {
            Console.Out.WriteLine(Imported(s, parse.GetValue(folder)!));
            return Task.CompletedTask;
        }));
        command.Subcommands.Add(import);

        var copyName = new Argument<string>("name") { Description = "the save's folder name in the source copy" };
        var from = new Option<string>("--from") { Description = "the copy the save is in", Required = true };
        var to = new Option<string>("--to") { Description = "the copy it goes to", Required = true };
        var copy = new Command("copy", "bring a save of one copy into another") { copyName, from, to };
        copy.SetAction((parse, _) => Run(parse, _ =>
        {
            var platform = GamePlatform.Current;
            var source = new Copies.GameCopy(CopySettings.Resolve(platform, instanceOverride: parse.GetValue(from)), platform, Console.Out);
            var target = new Copies.GameCopy(CopySettings.Resolve(platform, instanceOverride: parse.GetValue(to)), platform, Console.Out);
            var save = SaveStore.Find(source.Layout.Profile, parse.GetValue(copyName)!);
            Console.Out.WriteLine(Imported(target, save.Folder));
            return Task.CompletedTask;
        }));
        command.Subcommands.Add(copy);
        return command;
    }

    /// <summary>Imports into a copy; never into the installed game, whose saves are the player's.</summary>
    static string Imported(Copies.GameCopy copy, string folder)
    {
        if (copy.Instance is null)
            throw new CopyException("`save import` goes into a copy (--instance NAME), never into the installed game: its saves are the player's");
        var into = SaveStore.Import(copy.Layout.Profile, folder, Instance.Players);
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)));
        return $"imported {name} into instance {copy.Instance.Name} ({string.Join(", ", into)}); start from it with "
            + $"`gatekit --instance {copy.Instance.Name} up --save {name}` (bg3_up save), or load it in a running copy (GK.Gui.LoadLatest loads the newest)";
    }

    static Command Instances()
    {
        var command = new Command("instances", "the game copies that exist and which are running");
        command.SetAction((parse, _) => Run(parse, ListInstances));
        var list = new Command("list", "the game copies that exist and which are running");
        list.SetAction((parse, _) => Run(parse, ListInstances));
        var name = new Argument<string>("name") { Description = "the copy's name: 1-32 letters, digits, '_' or '-'" };
        var create = new Command("create", "create or refresh a copy (clone and private profile) without starting it") { name };
        create.SetAction((parse, _) => RunNamed(parse, parse.GetValue(name)!, s => s.Out.WriteLine(s.CreateInstance())));
        var destroyName = new Argument<string>("name") { Description = "the copy's name" };
        var destroy = new Command("destroy", "close a copy's game and delete the copy") { destroyName };
        destroy.SetAction((parse, _) => RunNamed(parse, parse.GetValue(destroyName)!, s => s.Out.WriteLine(s.Destroy())));
        var renameFrom = new Argument<string>("name") { Description = "the copy as it is called now" };
        var renameTo = new Argument<string>("new-name") { Description = "what to call it: 1-32 letters, digits, '_' or '-'" };
        var rename = new Command("rename", "give a copy another name; nothing in it changes and nothing is cloned")
            { renameFrom, renameTo };
        rename.SetAction((parse, _) => RunNamed(parse, parse.GetValue(renameFrom)!,
            s => s.Out.WriteLine(s.Rename(parse.GetValue(renameTo)!))));
        command.Add(list);
        command.Add(create);
        command.Add(rename);
        command.Add(destroy);
        return command;

        static Task ListInstances(Copies.GameCopy s)
        {
            foreach (var line in s.InstanceList()) s.Out.WriteLine(line);
            return Task.CompletedTask;
        }
    }

    static async Task<int> RunNamed(ParseResult parse, string instance, Action<Copies.GameCopy> action)
    {
        try
        {
            var platform = GamePlatform.Current;
            var copy = new Copies.GameCopy(CopySettings.Resolve(platform, instanceOverride: instance), platform, Console.Out);
            action(copy);
            return await Task.FromResult(0);
        }
        catch (CopyException e)
        {
            Console.Error.WriteLine("ERROR: " + e.Message);
            return 1;
        }
    }

    static Command Exec()
    {
        var side = new Argument<string>("side") { Description = "client, server or ui (the client inside the UI's own context: Ext.UI)" };
        side.AcceptOnlyFromAmong("client", "server", "ui");
        var code = new Argument<string>("code") { Description = "a Lua chunk, or @file.lua; `GK` is the bridge's API" };
        var timeout = new Option<double>("--timeout") { Description = "seconds to wait for the answer", DefaultValueFactory = _ => 20 };
        var command = new Command("exec", "run Lua in the game and print what it returns as JSON") { side, code, timeout };
        command.SetAction((parse, _) => Run(parse, async s =>
        {
            var result = await s.ExecAsync(parse.GetValue(side)!, parse.GetValue(code)!, TimeSpan.FromSeconds(parse.GetValue(timeout)));
            s.Out.WriteLine(result is null ? "null" : result.ToJsonString(ResultJson));
        }));
        return command;
    }

    static Command Shot()
    {
        var output = new Argument<string>("out") { Description = "picture file: .jpg (default) or .png" };
        var width = new Option<int>("--width") { Description = "scale down to this width when wider (0: full size)", DefaultValueFactory = _ => 1150 };
        var command = new Command("shot", "a picture of the game, taken inside it (the window may stay behind other windows)") { output, width };
        command.SetAction((parse, _) => Run(parse, s =>
        {
            s.Out.WriteLine(s.Shot(parse.GetValue(output)!, parse.GetValue(width)));
            return Task.CompletedTask;
        }));
        return command;
    }

    static Command Key()
    {
        var name = new Argument<string>("name") { Description = $"key: {GameKeys.Known}" };
        var hold = new Option<double>("--hold") { Description = "seconds to keep the key down (0 to 30); without it, a tap" };
        var command = new Command("key", "press a key in the game window without bringing it forward") { name, hold };
        command.SetAction((parse, _) => Run(parse, s =>
        {
            var seconds = parse.GetValue(hold);
            s.Key(parse.GetValue(name)!, seconds > 0 ? seconds : null);
            return Task.CompletedTask;
        }));
        return command;
    }

    /// <summary>`sound`: how loud this copy's game is. Each copy mixes its own sound, so this
    /// is the way to keep several running games from being one wall of noise.</summary>
    static Command Sound()
    {
        var command = new Command("sound", "how loud this copy's game is: the categories, their volumes, mute and unmute");
        command.SetAction((parse, _) => Run(parse, List));
        var list = new Command("list", "every sound category with its volume (0 to 100), and the mute-when-not-in-front flag");
        list.SetAction((parse, _) => Run(parse, List));

        var setCategory = new Argument<string>("category") { Description = "the game's key (MusicVolume), GateKit's short name (music) or the RTPC name" };
        var setValue = new Argument<string>("volume") { Description = "0 (silent) to 100 (the slider's full position)" };
        var set = new Command("set", "set one category's volume in this copy of the game") { setCategory, setValue };
        set.SetAction((parse, _) => Run(parse, async s =>
        {
            s.RequireNamed("sound set");
            var category = SoundControl.Category(parse.GetValue(setCategory));
            var value = SoundControl.Volume(parse.GetValue(setValue));
            s.Out.WriteLine(Print(await s.Sound().SetAsync(category, value)));
        }));

        var muteCategory = new Argument<string?>("category") { Description = "a category; without one the master, which silences this copy", Arity = ArgumentArity.ZeroOrOne };
        var mute = new Command("mute", "silence a category, remembering how loud it was") { muteCategory };
        mute.SetAction((parse, _) => Run(parse, async s =>
            s.Out.WriteLine(Named(s, "sound mute") +Print(await s.Sound().MuteAsync(SoundControl.Category(parse.GetValue(muteCategory)))))));

        var unmuteCategory = new Argument<string?>("category") { Description = "a category; without one every category `mute` silenced in this copy", Arity = ArgumentArity.ZeroOrOne };
        var unmute = new Command("unmute", "put back what `mute` took away") { unmuteCategory };
        unmute.SetAction((parse, _) => Run(parse, async s =>
            s.Out.WriteLine(Named(s, "sound unmute") +Print(await s.Sound().UnmuteAsync(SoundControl.Category(parse.GetValue(unmuteCategory)))))));

        var state = new Argument<string?>("on-off") { Description = "on or off; without it the flag is only read", Arity = ArgumentArity.ZeroOrOne };
        var unfocused = new Command("unfocused", "the game's own flag: silence itself while its window is not the one in front") { state };
        unfocused.SetAction((parse, _) => Run(parse, async s =>
        {
            var wanted = parse.GetValue(state) is { Length: > 0 } word ? SoundControl.OnOff(word) : (bool?)null;
            if (wanted is not null) s.RequireNamed("sound unfocused");
            var now = await s.Sound().WhenUnfocusedAsync(wanted);
            s.Out.WriteLine("mute when the window is not in front: " + (now?.GetValue<bool>() == true ? "on" : "off"));
        }));

        command.Add(list);
        command.Add(set);
        command.Add(mute);
        command.Add(unmute);
        command.Add(unfocused);
        return command;

        static async Task List(Copies.GameCopy s)
        {
            var sound = s.Sound();
            var flag = await sound.WhenUnfocusedAsync(null);
            foreach (var line in SoundControl.Lines(await sound.ListAsync(), flag?.GetValue<bool>())) s.Out.WriteLine(line);
        }

        static string Print(JsonNode? value) => value is null ? "null" : value.ToJsonString(ResultJson);

        // the check first, then nothing of its own to print
        static string Named(Copies.GameCopy s, string verb) { s.RequireNamed(verb); return ""; }
    }

    static Command Link()
    {
        var root = new Argument<string>("mod-root") { Description = "folder that holds Mods/<Folder>/ScriptExtender/Lua" };
        var command = new Command("link", "register a mod's Lua for hot reload (`reload`, `watch`)") { root };
        command.SetAction((parse, _) => Run(parse, s =>
        {
            s.Out.WriteLine("linked: " + string.Join(", ", s.Link(parse.GetValue(root)!)));
            return Task.CompletedTask;
        }));
        return command;
    }

    static Command BridgeReload(string name)
    {
        var command = new Command(name, "put the bridge's own Lua into the running game: link it and reload (it survives `reset`; `down` removes the link)");
        command.SetAction((parse, _) => Run(parse, async s =>
            s.Out.WriteLine($"bridge Lua reloaded in {Seconds(await s.BridgeReloadAsync())} s (linked: it also survives `reset`; `down` removes the link)")));
        return command;
    }

    static Command Bridge()
    {
        var command = new Command("bridge", "the bridge mod");
        command.Add(BridgeReload("reload"));
        return command;
    }

    static Command Logs()
    {
        var story = new Option<bool>("--story") { Description = "Script Extender's log files instead, the Osiris trace included (needs `up --story-log`)" };
        var lines = new Option<int>("--lines") { Description = "how many of the last lines", DefaultValueFactory = _ => 80 };
        var grep = new Option<string?>("--grep") { Description = "only lines matching this regular expression (case-insensitive)" };
        var command = new Command("logs", "what Script Extender's console shows: prints, warnings, Lua errors") { story, lines, grep };
        command.SetAction((parse, _) => Run(parse, s =>
        {
            foreach (var line in s.Logs(parse.GetValue(story), parse.GetValue(lines), parse.GetValue(grep))) s.Out.WriteLine(line);
            return Task.CompletedTask;
        }));
        return command;
    }

    static Command Watch()
    {
        var command = new Command("watch", "reload whenever a linked Lua file changes (Ctrl+C to stop)");
        command.SetAction((parse, cancel) => Run(parse, s => s.WatchAsync(cancel)));
        return command;
    }

    static Command Reset()
    {
        var noIntro = new Option<bool>("--keep-scenes") { Description = "do not fast-forward the level's opening scene" };
        var command = new Command("reset", "bring back the world of the last checkpoint without restarting the game") { noIntro };
        command.SetAction((parse, _) => Run(parse, async s =>
        {
            var took = await s.Session().ResetAsync(skipIntro: !parse.GetValue(noIntro));
            s.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"world reset in {took.TotalSeconds:0.0} s"));
        }));
        return command;
    }

    static Command ScriptExtender()
    {
        var command = new Command("se", "which Script Extender build the game loads (a local build through DebugLoadSE, or stock)");
        var status = new Command("status", "the build the next start loads");
        status.SetAction((parse, _) => Run(parse, s => Print(s, s.SeStatus())));
        var folder = new Argument<string>("folder")
        {
            Description = "folder with BG3ScriptExtender.dll (or the dll); without it, the build pinned in se-release.json is fetched",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var install = new Command("install", "make GateKit's build the one every start loads when nothing names one, fetching it if it is not here; "
            + "with --instance it is also put into that copy's bin, with --installed-game into the installed game's") { folder };
        install.SetAction(async (parse, cancel) => await Run(parse, async s =>
        {
            var given = parse.GetValue(folder);
            var into = parse.GetValue(InstalledGameOption);
            var choice = given is null ? await s.SeInstallPinnedAsync(into, cancel) : s.SeInstall(given, into);
            var json = choice.Installed is { } st ? Json(st) : [];
            json["recorded"] = choice.Recorded;
            json["into"] = choice.Into;
            await Write(s, json);
        }));
        var remove = new Command("remove", "the stock build again, in the copy --instance names or, with --installed-game, in the installed game");
        remove.SetAction((parse, _) => Run(parse, s => Print(s, s.SeRemove(parse.GetValue(InstalledGameOption)))));
        command.Add(status);
        command.Add(install);
        command.Add(remove);
        return command;

        static JsonObject Json(ScriptExtenderSwitch.Status st) =>
            new() { ["fork"] = st.Fork, ["dll"] = st.Dll, ["source"] = st.Source, ["sha256"] = st.Sha256, ["managed"] = st.Managed };

        static Task Print(Copies.GameCopy s, ScriptExtenderSwitch.Status st) => Write(s, Json(st));

        static Task Write(Copies.GameCopy s, JsonObject json)
        {
            s.Out.WriteLine(json.ToJsonString(ResultJson));
            return Task.CompletedTask;
        }
    }
}
