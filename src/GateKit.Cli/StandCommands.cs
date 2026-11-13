using System.CommandLine;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using GateKit.Platform;
using GateKit.Stand;

namespace GateKit.Cli;

/// <summary>The stand's commands: up, down, exec, ... (docs/contracts/cli.md, section 2).</summary>
public static class StandCommands
{
    /// <summary>`--instance NAME`, valid before or after any command.</summary>
    public static readonly Option<string?> InstanceOption = new("--instance")
    {
        Description = "work on this instance (a game copy of its own; also GATEKIT_INSTANCE)",
        Recursive = true,
    };

    static readonly JsonSerializerOptions ResultJson = new()
    {
        WriteIndented = true,
        IndentSize = 1,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>All stand commands, to be added to the root.</summary>
    public static IEnumerable<Command> Create()
    {
        yield return DoctorCommand();
        yield return Build();
        yield return Up();
        yield return Simple("down", "close the game, restore saves, mod list and Script Extender", s => s.Out.WriteLine(s.Down()));
        yield return Simple("status", "instance, game processes, Script Extender build, last heartbeats", s =>
        {
            foreach (var line in s.Status()) s.Out.WriteLine(line);
        });
        yield return Instances();
        yield return Simple("destroy", "close this instance's game and delete its clone, profile and records", s => s.Out.WriteLine(s.Destroy()));
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
        var command = new Command("doctor", "check that this machine has what the stand needs, and say how to fix what it has not");
        command.SetAction(parse =>
            Doctor.Report(Doctor.Run(GamePlatform.Current, parse.GetValue(InstanceOption)), Console.Out));
        return command;
    }

    static string Seconds(TimeSpan span) => span.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>Runs a stand action: settings resolved, errors printed as "ERROR: ..." with exit code 1.</summary>
    static async Task<int> Run(ParseResult parse, Func<Stand.Stand, Task> action)
    {
        try
        {
            var platform = GamePlatform.Current;
            var settings = StandSettings.Resolve(platform, instanceOverride: parse.GetValue(InstanceOption));
            var stand = new Stand.Stand(settings, platform, Console.Out);
            await action(stand);
            return 0;
        }
        catch (StandException e)
        {
            Console.Error.WriteLine("ERROR: " + e.Message);
            return 1;
        }
        catch (PlatformCapabilityException e)
        {
            Console.Error.WriteLine("ERROR: " + e.Message);
            return 1;
        }
    }

    static Command Simple(string name, string description, Action<Stand.Stand> action) =>
        Simple(name, description, s => { action(s); return Task.CompletedTask; });

    static Command Simple(string name, string description, Func<Stand.Stand, Task> action)
    {
        var command = new Command(name, description);
        command.SetAction((parse, _) => Run(parse, action));
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
        var mods = new Option<string?>("--mods") { Description = "comma-separated mod folders to load besides the base game (default with --level: none; without: your own list)" };
        var storyLog = new Option<bool>("--story-log") { Description = "also write Script Extender's logs, the Osiris trace included (see `logs --story`)" };
        var gameArgs = new Argument<string[]>("game-args") { Description = "extra game arguments, after `--`", Arity = ArgumentArity.ZeroOrMore };
        var command = new Command("up", "back up, configure, start the game, wait until it is playable") { level, save, mods, storyLog, gameArgs };
        command.SetAction((parse, cancel) => Run(parse, async s =>
        {
            var modList = parse.GetValue(mods) is { } text ? text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : null;
            await s.UpAsync(new UpOptions(parse.GetValue(level), parse.GetValue(save), modList, parse.GetValue(storyLog), parse.GetValue(gameArgs)), cancel);
        }));
        return command;
    }

    static Command Instances()
    {
        var command = new Command("instances", "the game copies that exist and which are running");
        command.SetAction((parse, _) => Run(parse, ListInstances));
        var list = new Command("list", "the game copies that exist and which are running");
        list.SetAction((parse, _) => Run(parse, ListInstances));
        var name = new Argument<string>("name") { Description = "instance name: 1-32 letters, digits, '_' or '-'" };
        var create = new Command("create", "create or refresh an instance (clone and private profile) without starting it") { name };
        create.SetAction((parse, _) => RunNamed(parse, parse.GetValue(name)!, s => s.Out.WriteLine(s.CreateInstance())));
        var destroyName = new Argument<string>("name") { Description = "instance name" };
        var destroy = new Command("destroy", "close an instance's game and delete it") { destroyName };
        destroy.SetAction((parse, _) => RunNamed(parse, parse.GetValue(destroyName)!, s => s.Out.WriteLine(s.Destroy())));
        var renameFrom = new Argument<string>("name") { Description = "the instance as it is called now" };
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

        static Task ListInstances(Stand.Stand s)
        {
            foreach (var line in s.InstanceList()) s.Out.WriteLine(line);
            return Task.CompletedTask;
        }
    }

    static async Task<int> RunNamed(ParseResult parse, string instance, Action<Stand.Stand> action)
    {
        try
        {
            var platform = GamePlatform.Current;
            var stand = new Stand.Stand(StandSettings.Resolve(platform, instanceOverride: instance), platform, Console.Out);
            action(stand);
            return await Task.FromResult(0);
        }
        catch (StandException e)
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
        var command = new Command("key", "press a key in the game window without bringing it forward") { name };
        command.SetAction((parse, _) => Run(parse, s => { s.Key(parse.GetValue(name)!); return Task.CompletedTask; }));
        return command;
    }

    /// <summary>`sound`: how loud this instance's game is. Each copy mixes its own sound, so this
    /// is the way to keep several running games from being one wall of noise.</summary>
    static Command Sound()
    {
        var command = new Command("sound", "how loud this instance's game is: the categories, their volumes, mute and unmute");
        command.SetAction((parse, _) => Run(parse, List));
        var list = new Command("list", "every sound category with its volume (0 to 100), and the mute-when-not-in-front flag");
        list.SetAction((parse, _) => Run(parse, List));

        var setCategory = new Argument<string>("category") { Description = "the game's key (MusicVolume), GateKit's short name (music) or the RTPC name" };
        var setValue = new Argument<string>("volume") { Description = "0 (silent) to 100 (the slider's full position)" };
        var set = new Command("set", "set one category's volume in this copy of the game") { setCategory, setValue };
        set.SetAction((parse, _) => Run(parse, async s =>
        {
            var category = SoundControl.Category(parse.GetValue(setCategory));
            var value = SoundControl.Volume(parse.GetValue(setValue));
            s.Out.WriteLine(Print(await s.Sound().SetAsync(category, value)));
        }));

        var muteCategory = new Argument<string?>("category") { Description = "a category; without one the master, which silences this copy", Arity = ArgumentArity.ZeroOrOne };
        var mute = new Command("mute", "silence a category, remembering how loud it was") { muteCategory };
        mute.SetAction((parse, _) => Run(parse, async s =>
            s.Out.WriteLine(Print(await s.Sound().MuteAsync(SoundControl.Category(parse.GetValue(muteCategory)))))));

        var unmuteCategory = new Argument<string?>("category") { Description = "a category; without one every category this instance muted", Arity = ArgumentArity.ZeroOrOne };
        var unmute = new Command("unmute", "put back what `mute` took away") { unmuteCategory };
        unmute.SetAction((parse, _) => Run(parse, async s =>
            s.Out.WriteLine(Print(await s.Sound().UnmuteAsync(SoundControl.Category(parse.GetValue(unmuteCategory)))))));

        var state = new Argument<string?>("on-off") { Description = "on or off; without it the flag is only read", Arity = ArgumentArity.ZeroOrOne };
        var unfocused = new Command("unfocused", "the game's own flag: silence itself while its window is not the one in front") { state };
        unfocused.SetAction((parse, _) => Run(parse, async s =>
        {
            var wanted = parse.GetValue(state) is { Length: > 0 } word ? SoundControl.OnOff(word) : (bool?)null;
            var now = await s.Sound().WhenUnfocusedAsync(wanted);
            s.Out.WriteLine("mute when the window is not in front: " + (now?.GetValue<bool>() == true ? "on" : "off"));
        }));

        command.Add(list);
        command.Add(set);
        command.Add(mute);
        command.Add(unmute);
        command.Add(unfocused);
        return command;

        static async Task List(Stand.Stand s)
        {
            var sound = s.Sound();
            var flag = await sound.WhenUnfocusedAsync(null);
            foreach (var line in SoundControl.Lines(await sound.ListAsync(), flag?.GetValue<bool>())) s.Out.WriteLine(line);
        }

        static string Print(JsonNode? value) => value is null ? "null" : value.ToJsonString(ResultJson);
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
        var install = new Command("install", "load GateKit's build from the next start on, fetching it if it is not here") { folder };
        install.SetAction(async (parse, cancel) => await Run(parse, async s =>
        {
            var given = parse.GetValue(folder);
            await Print(s, given is null ? await s.SeInstallPinnedAsync(cancel) : s.SeInstall(given));
        }));
        var remove = new Command("remove", "the stock build again");
        remove.SetAction((parse, _) => Run(parse, s => Print(s, s.SeRemove())));
        command.Add(status);
        command.Add(install);
        command.Add(remove);
        return command;

        static Task Print(Stand.Stand s, ScriptExtenderSwitch.Status st)
        {
            var json = new JsonObject { ["fork"] = st.Fork, ["dll"] = st.Dll, ["source"] = st.Source, ["sha256"] = st.Sha256, ["managed"] = st.Managed };
            s.Out.WriteLine(json.ToJsonString(ResultJson));
            return Task.CompletedTask;
        }
    }
}
