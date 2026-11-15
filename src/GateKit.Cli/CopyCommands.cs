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
        yield return Probe();
        yield return Cover();
        yield return CoverFold();
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

    /// <summary>Probes on functions named by registry id, an action, and what was seen named back by
    /// registry id: the registry and the running game joined both ways.</summary>
    static Command Probe()
    {
        var ids = new Argument<string[]>("ids") { Description = "registry ids of functions (or binary+0xRVA), each gets a count-only probe", Arity = ArgumentArity.OneOrMore };
        var action = new Option<string?>("--do") { Description = "Lua run on the server once the probes are on: the action to watch (or @file.lua)" };
        var seconds = new Option<double>("--seconds") { Description = "how long to let the game run after the action before the probes are read", DefaultValueFactory = _ => 3 };
        var asJson = new Option<bool>("--json") { Description = "the answer as JSON" };
        var note = new Option<bool>("--note") { Description = "write each caller the registry does not name into the live layer, as unplaced::unnamed_<token>, with what it was seen to call" };
        var command = new Command("probe", "put count-only probes on functions named by registry id, run an action, and name what called them by registry id; needs the Script Extender build with function probes") { ids, action, seconds, asJson, note };
        command.SetAction((parse, _) => Run(parse, async s =>
        {
            var store = GateKit.Symbols.Registry.RegistryStore.Find(null) ?? throw new InvalidOperationException("no registry found (run from a checkout that has registry/)");
            var module = (await s.ExecAsync("server", "local a = Ext.Debug.GetSymbolAddress and Ext.Debug.GetSymbolAddress('ecs__EntityWorld__Update') local m = a and Ext.Debug.ModuleFromAddress(a) return m and m.Module", TimeSpan.FromSeconds(20)))?.GetValue<string>()
                ?? throw new InvalidOperationException("this Script Extender build has no function probes (Ext.Debug.GetSymbolAddress): start the copy with the build that has them");

            // registry -> game: each id's place in the executable that runs
            var wanted = new List<(string Name, long Rva)>();
            foreach (var id in parse.GetValue(ids)!)
            {
                var at = id.IndexOf("+0x", StringComparison.OrdinalIgnoreCase);
                if (at > 0) { wanted.Add((id, long.Parse(id[(at + 3)..], NumberStyles.HexNumber, CultureInfo.InvariantCulture))); continue; }
                var place = store.Places(id).FirstOrDefault(p => string.Equals(p.Binary, module, StringComparison.OrdinalIgnoreCase));
                if (place.Row is null) throw new InvalidOperationException($"{id} has no place in {module} in the registry's build tables");
                wanted.Add((id, place.Row.Rva));
            }

            var list = string.Join(", ", wanted.Select(w => string.Create(CultureInfo.InvariantCulture, $"{w.Rva}")));
            var placed = await s.ExecAsync("server", $$"""
                GKProbeSlots = {}
                local failed = {}
                for i, rva in ipairs({ {{list}} }) do
                    local slot, why = Ext.Debug.ProbeFunction(Ext.Debug.AddressFromModule("{{module}}", rva), { Record = false })
                    if slot then GKProbeSlots[i] = slot else failed[#failed + 1] = { i, tostring(why) } end
                end
                return failed
                """, TimeSpan.FromSeconds(20));
            foreach (var failure in placed?.AsArray() ?? [])
                Console.Error.WriteLine($"not probed: {wanted[failure![0]!.GetValue<int>() - 1].Name}: {failure[1]}");

            if (parse.GetValue(action) is { Length: > 0 } code) await s.ExecAsync("server", code, TimeSpan.FromSeconds(60));
            await Task.Delay(TimeSpan.FromSeconds(parse.GetValue(seconds)));

            var read = await s.ExecAsync("server", """
                local bySlot = {}
                for _, p in ipairs(Ext.Debug.ListProbes()) do bySlot[p.Slot] = p end
                local out = {}
                for i, slot in pairs(GKProbeSlots or {}) do
                    local p = bySlot[slot]
                    local callers = {}
                    for _, c in ipairs(p and p.Callers or {}) do callers[#callers + 1] = { module = c.Caller.Module, rva = c.Caller.RVA, count = c.Count } end
                    out[#out + 1] = { index = i, hits = p and p.Hits or 0, other = p and p.OtherCallers or 0, callers = callers }
                    Ext.Debug.ClearProbe(slot)
                end
                GKProbeSlots = nil
                return out
                """, TimeSpan.FromSeconds(20));

            // game -> registry: every caller named by the id whose function holds it. Which function
            // holds an address is the binary's own fact (a function may lie in several parts), so it
            // is asked of the databases as `registry at` asks; a row's size alone answers when they
            // are not there.
            var live = GateKit.Symbols.Registry.LiveLayer.OfHome();
            var known = new Dictionary<string, (string? Id, long? Start)>(StringComparer.OrdinalIgnoreCase);
            (string? Id, long? Start) Named(string binary, long at)
            {
                var address = string.Create(CultureInfo.InvariantCulture, $"{binary}+0x{at:X}");
                if (known.TryGetValue(address, out var seen)) return seen;
                (string? Id, long? Start) found = (null, null);
                try
                {
                    var answerAt = GateKit.Symbols.Registry.LiveCommands.AtJson(address, store, live, TextWriter.Null);
                    var id = (answerAt["committed"] as JsonArray)?.FirstOrDefault()?["id"]?.GetValue<string>()
                        ?? (answerAt["live"] as JsonArray)?.FirstOrDefault()?["id"]?.GetValue<string>();
                    var function = answerAt["function"]?.GetValue<string>();
                    long? begins = function is null ? null : long.Parse(function[(function.IndexOf("+0x", StringComparison.Ordinal) + 3)..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    found = (id, id is null ? null : begins ?? at);
                }
                catch (Exception e) when (e is GateKit.Symbols.SymbolsException or GateKit.Symbols.Registry.RegistryException or InvalidOperationException)
                {
                    var row = store.At(binary, at).Select(a => a.Row).FirstOrDefault();
                    found = (row?.Id, row?.Rva);
                }
                return known[address] = found;
            }
            var answer = new JsonArray();
            foreach (var probe in read?.AsArray() ?? [])
            {
                var (name, rva) = wanted[probe!["index"]!.GetValue<int>() - 1];
                var callers = new JsonArray();
                foreach (var caller in probe["callers"]?.AsArray() ?? [])
                {
                    var callerModule = caller!["module"]?.GetValue<string>();
                    var callerRva = caller["rva"]?.GetValue<long>() ?? 0;
                    var (callerId, callerStart) = callerModule is null ? (null, (long?)null) : Named(callerModule, callerRva);
                    callers.Add(new JsonObject
                    {
                        ["id"] = callerId, ["offset"] = callerStart is { } start ? GateKit.Symbols.Registry.RegistryStore.Hex(callerRva - start) : null,
                        ["address"] = string.Create(CultureInfo.InvariantCulture, $"{callerModule ?? "?"}+0x{callerRva:X}"), ["count"] = caller["count"]?.GetValue<long>() ?? 0,
                    });
                }
                answer.Add(new JsonObject
                {
                    ["id"] = name, ["address"] = string.Create(CultureInfo.InvariantCulture, $"{module}+0x{rva:X}"),
                    ["hits"] = probe["hits"]!.GetValue<long>(), ["otherCallers"] = probe["other"]!.GetValue<long>(), ["callers"] = callers,
                });
            }

            if (parse.GetValue(asJson)) { s.Out.WriteLine(answer.ToJsonString(ResultJson)); return; }
            var unknown = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            foreach (var probe in answer)
            {
                s.Out.WriteLine($"{probe!["id"]}  {probe["address"]}  called {probe["hits"]}");
                var named = new List<string>();
                foreach (var caller in probe["callers"]!.AsArray())
                {
                    var id = caller!["id"]?.GetValue<string>();
                    if (id is null)
                    {
                        var address = caller["address"]!.GetValue<string>();
                        if (!unknown.TryGetValue(address, out var calls)) unknown[address] = calls = new(StringComparer.Ordinal);
                        calls.Add(probe["id"]!.GetValue<string>());
                    }
                    s.Out.WriteLine($"    from {(id is null ? "unknown" : $"code:{id}+{caller["offset"]}")}  {caller["address"]}  x{caller["count"]}");
                    named.Add($"{caller["count"]} from {(id is null ? caller["address"] : "inside code:" + id)}");
                }
                if (probe["hits"]!.GetValue<long>() > 0)
                    s.Out.WriteLine($"    evidence  watch:A count-only probe on it shows {probe["hits"]} calls: {string.Join(", ", named)}");
            }
            if (unknown.Count > 0)
            {
                s.Out.WriteLine("callers the registry does not name (gatekit decomp at <address>):");
                foreach (var (address, calls) in unknown)
                {
                    // --note: the function goes into the live layer as one seen at work and not named,
                    // so that what is still to read is kept and not only printed
                    string? noted = null;
                    if (parse.GetValue(note))
                    {
                        var called = string.Join(", ", calls.Where(c => !c.Contains("+0x", StringComparison.Ordinal)).Select(c => "code:" + c));
                        if (called.Length > 0)
                            noted = GateKit.Symbols.Registry.LiveCommands.NoteUnnamed(address,
                                [$"watch:A count-only probe shows this function calling {called}"],
                                [$"What this function is for: it calls {called}"], live, store, TextWriter.Null)?.Id;
                    }
                    s.Out.WriteLine("    " + address + (noted is null ? "" : "  noted " + noted));
                }
            }
        }));
        return command;
    }

    /// <summary>The record of coverage while the game is played: counters on the functions of the
    /// running executable, their counts saved to a file, the file folded against the registry.</summary>
    static Command Cover()
    {
        var command = new Command("cover", "record which functions run and who calls them while the game is played; needs the Script Extender build with coverage counters");

        const string Module = "local a = Ext.Debug.GetSymbolAddress and Ext.Debug.GetSymbolAddress('ecs__EntityWorld__Update') local m = a and Ext.Debug.ModuleFromAddress(a) return m and m.Module";

        var minimum = new Option<long>("--min-size") { Description = "leave out functions shorter than this many bytes: the shortest are called the most", DefaultValueFactory = _ => 64 };
        var limit = new Option<int>("--limit") { Description = "at most this many functions, spread evenly over the executable (0: all)", DefaultValueFactory = _ => 0 };
        var hot = new Option<long>("--hot") { Description = "a function called more often than this is taken off by itself", DefaultValueFactory = _ => 200_000 };
        var start = new Command("start", "put a counter on the functions of the running executable") { minimum, limit, hot };
        start.SetAction((parse, _) => Run(parse, async s =>
        {
            var module = (await s.ExecAsync("server", Module, TimeSpan.FromSeconds(20)))?.GetValue<string>()
                ?? throw new InvalidOperationException("this Script Extender build has no coverage counters: start the copy with the build that has them");
            var starts = GateKit.Symbols.Registry.LiveCommands.FunctionStarts(module, parse.GetValue(minimum), TextWriter.Null);
            if (parse.GetValue(limit) is > 0 and var most && starts.Count > most)
            {
                var step = (double)starts.Count / most;
                starts = [.. Enumerable.Range(0, most).Select(i => starts[(int)(i * step)])];
            }
            long placed = 0, refused = 0;
            const int Batch = 2000;
            for (var from = 0; from < starts.Count; from += Batch)
            {
                var list = string.Join(",", starts.Skip(from).Take(Batch).Select(r => string.Create(CultureInfo.InvariantCulture, $"{r}")));
                var answer = await s.ExecAsync("server", $$"""
                    local base = Ext.Debug.AddressFromModule("{{module}}", 0)
                    local list = { {{list}} }
                    for i = 1, #list do list[i] = base + list[i] end
                    return Ext.Debug.CoverStart(list, {{parse.GetValue(hot)}})
                    """, TimeSpan.FromSeconds(120));
                placed += answer?["Placed"]?.GetValue<long>() ?? 0;
                refused += answer?["Refused"]?.GetValue<long>() ?? 0;
            }
            s.Out.WriteLine($"{module}: {starts.Count} functions asked, {placed} counted, {refused} refused");
        }));
        command.Subcommands.Add(start);

        var status = new Command("status", "how many functions are counted, were called, were taken off as called too often");
        status.SetAction((parse, _) => Run(parse, async s =>
            s.Out.WriteLine((await s.ExecAsync("server", "return Ext.Debug.CoverStatus()", TimeSpan.FromSeconds(30)))?.ToJsonString(ResultJson) ?? "null")));
        command.Subcommands.Add(status);

        var file = new Argument<string>("file") { Description = "the file the counts are written to" };
        var reset = new Option<bool>("--reset") { Description = "start the counts again from zero, so that the next file holds the next stretch of play" };
        var save = new Command("save", "write the counts so far to a file: a line for every function called, a line for every pair of a function and the address that called it") { file, reset };
        save.SetAction((parse, _) => Run(parse, async s =>
        {
            var path = Path.GetFullPath(parse.GetValue(file)!).Replace('\\', '/');
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var answer = await s.ExecAsync("server", $"return Ext.Debug.CoverSave(\"{path}\", {(parse.GetValue(reset) ? "true" : "false")})", TimeSpan.FromSeconds(120));
            s.Out.WriteLine($"{path}: {answer?["Functions"]} functions called, {answer?["Pairs"]} pairs");
        }));
        command.Subcommands.Add(save);

        var stop = new Command("stop", "take every counter off; the counts stay for a last `cover save`");
        stop.SetAction((parse, _) => Run(parse, async s =>
            s.Out.WriteLine($"{(await s.ExecAsync("server", "return Ext.Debug.CoverStop()", TimeSpan.FromSeconds(300)))} counters taken off")));
        command.Subcommands.Add(stop);
        return command;
    }

    /// <summary>A saved record of coverage read against the registry: what ran, what of it has a
    /// name, and the unnamed functions that ran the most. Needs no game.</summary>
    public static Command CoverFold()
    {
        var files = new Argument<string[]>("files") { Description = "files written by `cover save`", Arity = ArgumentArity.OneOrMore };
        var top = new Option<int>("--top") { Description = "how many of the unnamed functions to list, the most called first", DefaultValueFactory = _ => 30 };
        var command = new Command("cover-fold", "read saved records of coverage against the registry: how many functions ran, how many of them an id names, and the unnamed ones that ran the most") { files, top };
        command.SetAction(parse =>
        {
            var store = GateKit.Symbols.Registry.RegistryStore.Find(null);
            var calls = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var pairs = new Dictionary<(string Callee, string Caller), long>();
            long lost = 0;
            foreach (var path in parse.GetValue(files)!)
                foreach (var line in File.ReadLines(path))
                {
                    var p = line.Split(' ');
                    if (p[0] == "F" && p.Length >= 4) calls[$"{p[1]}+0x{p[2]}"] = calls.GetValueOrDefault($"{p[1]}+0x{p[2]}") + long.Parse(p[3], CultureInfo.InvariantCulture);
                    else if (p[0] == "E" && p.Length >= 6) { var k = ($"{p[1]}+0x{p[2]}", $"{p[3]}+0x{p[4]}"); pairs[k] = pairs.GetValueOrDefault(k) + long.Parse(p[5], CultureInfo.InvariantCulture); }
                    else if (p[0] == "L" && p.Length >= 2) lost += long.Parse(p[1], CultureInfo.InvariantCulture);
                }
            var named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (store is not null)
                foreach (var address in calls.Keys)
                {
                    var at = address.IndexOf("+0x", StringComparison.Ordinal);
                    var row = store.At(address[..at], GateKit.Symbols.Registry.RegistryStore.ParseHex(address[(at + 1)..])).Select(a => a.Row).FirstOrDefault(r => string.Create(CultureInfo.InvariantCulture, $"{address[..at]}+0x{r.Rva:X}").Equals(address, StringComparison.OrdinalIgnoreCase));
                    if (row is not null) named[address] = row.Id;
                }
            Console.Out.WriteLine($"{calls.Count} functions ran, {calls.Values.Sum()} calls counted, {pairs.Count} pairs of a function and a calling address, {lost} calls lost by the ring");
            Console.Out.WriteLine($"{named.Count} of them the registry names; {calls.Count - named.Count} it does not");
            foreach (var (address, id) in named.OrderByDescending(n => calls[n.Key]).Take(parse.GetValue(top)))
                Console.Out.WriteLine($"    named    {calls[address],12}  code:{id}");
            foreach (var (address, count) in calls.Where(c => !named.ContainsKey(c.Key)).OrderByDescending(c => c.Value).Take(parse.GetValue(top)))
                Console.Out.WriteLine($"    unnamed  {count,12}  {address}  from {pairs.Count(p => p.Key.Callee.Equals(address, StringComparison.OrdinalIgnoreCase))} places");
            return 0;
        });
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
