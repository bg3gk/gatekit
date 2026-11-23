using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GateKit.Bridge;
using GateKit.Json;
using GateKit.Copies;
using ModelContextProtocol.Protocol;

namespace GateKit.Mcp;

/// <summary>
/// The core tools of the server (docs/contracts/mcp.md, "Core tools"). The contract holds their
/// names, schemas and result texts.
/// </summary>
public static partial class CoreTools
{
    public static IReadOnlyList<GateKitTool> Create(ToolHost host) =>
    [
        new(Define("bg3_up",
                "Start Baldur's Gate 3 directly (no Steam window, launcher, logo video or menu) and wait until the last save is "
                + "loaded and playable. Takes about a minute. Backs up the saves and mod list first; bg3_down restores them.",
                """
                {
                 "type": "object",
                 "properties": {
                  "level": {"type": "string", "description": "MODULE:LEVEL, e.g. GustavX:TUT_Avernus_C. Starts a fresh test game straight in that level (about 40 s) instead of the last save; only the base game, the bridge and `mods` are loaded. A level without its module is refused before the game starts; a start the main menu holds for 60 s fails, naming what holds it (first-launch screens are gone through in an instance)."},
                  "save": {"type": "string", "description": "Load this save (its folder name under Savegames/Story) instead of the last one."},
                  "mods": {"type": "array", "items": {"type": "string"}, "description": "Mod folder names to load besides the base game. Omitted: none in an instance or with `level` (a bare start); the installed game without `level` keeps the player's own mod list."},
                  "story_log": {"type": "boolean", "description": "Also record the Osiris trace for bg3_logs story=true."},
                  "main_menu": {"type": "boolean", "description": "Stop in the main menu instead of loading a save (no level, no save); the splash screen is passed, first-launch screens are gone through in an instance; answers once GK.Gui.Stuck() names the main menu."},
                  "until_lua": {"type": "boolean", "description": "Answer as soon as the bridge first answers: the client's LoadModule state, the first moment any Lua runs. No menu, session or server yet; the game goes on loading its main menu. For reading what the start did (bg3_timeline). Refused with level, save or main_menu."},
                  "first_launch": {"type": "boolean", "description": "Start as the game's first launch (its switch ShowFirstTimeSetup set at the first Lua) and answer on the first first-launch page, Eula; nothing is pressed: each page waits in attention (window:<page>) for GK.Gui.Look / Click / Select / Set and the splash screen for a key. An instance only. Refused with level, save, main_menu or until_lua."},
                  "game_args": {"type": "array", "items": {"type": "string"}, "description": "Extra game arguments."}
                 }
                }
                """),
            async (args, cancel) =>
            {
                var (copy, log) = host.Begin();
                await copy.UpAsync(new UpOptions(args.Str("level"), args.Str("save"), args.Strings("mods"),
                    args.Flag("story_log"), args.Strings("game_args"), args.Flag("main_menu"), UntilLua: args.Flag("until_lua"), FirstLaunch: args.Flag("first_launch")), cancel);
                return Results.Text(log.ToString().Trim() is { Length: > 0 } printed ? printed : "playable");
            }),

        new(Define("bg3_down", "Close the game and restore the saves and mod list to what they were before bg3_up.", Empty),
            (_, _) =>
            {
                var (copy, _) = host.Begin();
                return Task.FromResult(Results.Text(copy.Down()));
            }),

        new(Define("bg3_status", "Which game copy this server drives (`instance`; null is the installed game), whether the game runs, and what "
                + "state each side (client, server) last reported (Menu, LoadLevel, Running, ...). When several agents each drive a copy, "
                + "this is how one checks that its tools reach its own copy and no other.", Empty),
            async (_, _) =>
            {
                var (copy, _) = host.Begin();
                var session = copy.Session();
                var pids = copy.GamePids();
                // A heartbeat is only what a running game says now. The installed game's channel
                // still held a "Running" written two days before, and with no process up that read
                // as a live game - a runner whose --instance was dropped took it for its copy.
                return Results.Text(new JsonObject
                {
                    ["instance"] = host.Instance,
                    ["drives"] = session.Target,
                    ["channel"] = copy.Layout.Channel,
                    ["processes"] = new JsonArray([.. pids.Select(p => (JsonNode)JsonValue.Create(p))]),
                    ["client"] = Beat(session, Side.Client, pids.Count > 0),
                    ["server"] = Beat(session, Side.Server, pids.Count > 0),
                    ["client_alive"] = pids.Count > 0 && session.State(Side.Client) is not null,
                    ["film"] = await Film(session, pids.Count > 0),
                });
            }),

        new(Define("bg3_lua",
                "Run a Lua chunk inside the game through Script Extender and return what it returns, as JSON. side 'client' has visuals, "
                + "UI and Ext.System.ClientTimeline; side 'server' has Osi.* and game rules. `GK.Log(...)` prints lines; "
                + "`GK.Dump(entity, {\"Comp\", ...})` reads components into plain tables. Server helpers (who = character uuid, default the "
                + "host): GK.Character(who), GK.Party(), GK.Give(template, count, who), GK.Find(template, who), GK.Equip(itemOrTemplate, who), "
                + "GK.Unequip(item, who), GK.Unsheath(bool, who), GK.Teleport(x, y, z, who) or GK.Teleport(targetUuid), "
                + "GK.Spawn(template[, x, y, z]), GK.Remove(uuid), GK.Watch(osirisEvent, arity), GK.Events(since). Client helpers: GK.Client(), "
                + "GK.Scenes(withTracks), GK.Tracks(timelineId, \"TLShowWeapon\"), GK.Key(\"ESCAPE\"), GK.Events(since). Side 'ui' is the client "
                + "inside the UI context, required for: GK.Dialog(), GK.Answer(n), GK.Commands(), GK.Command(name, param), GK.ViewModels(), "
                + "GK.Properties(type) and anything under Ext.UI. The window does not need focus.",
                """
                {
                 "type": "object",
                 "required": ["side", "code"],
                 "properties": {
                  "side": {"type": "string", "enum": ["client", "server", "ui"]},
                  "code": {"type": "string"},
                  "timeout": {"type": "number", "description": "Seconds to wait for the answer (default 20)."}
                 }
                }
                """),
            async (args, cancel) =>
            {
                var (copy, log) = host.Begin();
                var result = await copy.Session().LuaAsync(args.Need("side"), args.Need("code"), args.Seconds("timeout", 20), cancel);
                var printed = log.ToString().TrimEnd('\r', '\n');
                return Results.Text((printed.Length > 0 ? printed + "\n" : "") + Results.Json(result));
            }),

        new(Define("bg3_wait",
                "Wait until something happens in the game, and say what ended the wait. Ends the moment an attention item appears "
                + "(a dialogue, a roll, a reaction prompt, a message box, a trade, loot, a death, a fight starting or ending, a film, "
                + "a load, a GK.Act job ending - read from the bridge's event log, no polling of the game), or when `expression` "
                + "(Lua, on `side`) is truthy, whichever comes first; the timeout is the last resort. With an expression that comes true "
                + "the answer is its value, as before; otherwise {endedBy: attention|timeout, attention: [...], waitedMs}. "
                + "An event that happened after the caller's previous call (and was not told yet) ends the wait at once. "
                + "until_attention = false waits for the expression alone.",
                """
                {
                 "type": "object",
                 "properties": {
                  "side": {"type": "string", "enum": ["client", "server", "ui"], "description": "Where the expression runs (default client)."},
                  "expression": {"type": "string"},
                  "until_attention": {"type": "boolean", "description": "End on any new attention item (default true)."},
                  "timeout": {"type": "number", "description": "Seconds (default 30)."}
                 }
                }
                """),
            async (args, cancel) =>
            {
                var (copy, _) = host.Begin();
                var side = args.Str("side") ?? "client";
                var expression = args.Str("expression");
                var onAttention = !args.Has("until_attention") || args.Flag("until_attention");
                if (expression is null && !onAttention) throw new ArgumentException("nothing to wait for: give an expression, or leave until_attention on");
                var timeout = args.Seconds("timeout", 30);
                var reader = host.AttentionOf(copy.Layout.Channel);
                var started = DateTime.UtcNow;
                var result = await AttentionWait.RunAsync(reader, onAttention,
                    expression is null ? null : async c => await copy.Session().LuaAsync(side, $"return ({expression})", TimeSpan.FromSeconds(20), c),
                    timeout, cancel, from: host.Attention,
                    guard: new TickGuard(copy.Session().Bridge, timeout, log: reader.Log));
                if (result.EndedBy == "condition") return Results.Text(result.Value);
                if (result.EndedBy == "timeout" && expression is not null) throw new CopyException($"timed out waiting on {side} for: {expression}");
                // the caller has now been told these
                reader.Report(host.Attention);
                if (SchemaCheck.Misfits("common/attention", result.Items) is { Count: > 0 } misfits)
                    throw new CopyException("bg3_wait: the attention items do not fit common/attention: " + string.Join("; ", misfits.Take(10)));
                return Results.Text(new JsonObject
                {
                    ["endedBy"] = result.EndedBy,
                    ["attention"] = result.Items,
                    ["waitedMs"] = (long)(DateTime.UtcNow - started).TotalMilliseconds,
                });
            }),

        new(Define("bg3_state",
                "The game in one call, no screenshot needed: client (game state, controller mode, controlled character), live scenes "
                + "(dialogues, cutscenes: phase, time, actors) and every party member (name, level, position, health, in combat, weapons "
                + "drawn, equipment by slot).", Empty),
            async (_, cancel) =>
            {
                var (copy, _) = host.Begin();
                var session = copy.Session();
                var client = await session.LuaAsync("client", "return { client = GK.Client(), scenes = GK.Scenes() }", TimeSpan.FromSeconds(20), cancel);
                var party = await session.LuaAsync("server", "return GK.Party()", TimeSpan.FromSeconds(20), cancel);
                var state = client as JsonObject ?? new JsonObject { ["client"] = client };
                state["party"] = party;
                return Results.Text(state);
            }),

        new(Define("bg3_events",
                "Observation. What happened in the game, as ONE stream: the event log of both sides merged by the game's clock, each "
                + "row {seq, tick, t, kind, side, key?, attention?, scope?, payload?} (schemas/events/row). Kinds: game_state, osiris "
                + "(GK.Watch), note, the attention kinds (dialogue, roll, reaction, trade, combat_started, death, job_done ...), the game "
                + "log (game_damage, game_status ...), scope rows (scope_begin, scope_track, scope_clean, scope_verify), the log's own "
                + "(family_on, family_off, dropped) and the detailed families status, entity, surface, component while switched on "
                + "(GK.EventLog.On). `dropped` says where `since` is behind the oldest row kept; `counts` gives rows, bytes and drops per "
                + "family. Rows are placed by the registry of the running game; one no registry places is kept with `unplaced` "
                + "(why) and listed per kind in `unplaced`. A server started with GATEKIT_FAIR=1 gives only rows a player could "
                + "know (knows = player; an unplaced row counts as omniscient, and `unplaced` says it was withheld). Pass `next` as "
                + "`since` to read on. Reads the log's files; asks nothing of the game.",
                """
                {
                 "type": "object",
                 "properties": {
                  "since": {"type": "string", "description": "The `next` of an earlier answer; absent: from the start of each side's boot."},
                  "kinds": {"type": "array", "items": {"type": "string"}, "description": "Only these kinds."},
                  "family": {"type": "string", "description": "Only this family: state, osiris, note, attention, game, scope, detail."},
                  "limit": {"type": "integer", "description": "At most this many rows (default 200); `more` says there are more."}
                 }
                }
                """),
            (args, cancel) =>
            {
                var (copy, _) = host.Begin();
                var reader = host.AttentionOf(copy.Layout.Channel);
                var kinds = args.Strings("kinds")?.ToHashSet(StringComparer.Ordinal);
                var family = args.Str("family");
                var limit = Math.Max(1, args.Int("limit") ?? 200);
                bool Keep(JsonObject row) =>
                    (kinds is null || kinds.Contains((string)row["kind"]!)) && (family is null || reader.Log.FamilyOf((string)row["kind"]!) == family);
                var (rows, next) = reader.Stream.Since(args.Str("since"), Keep, limit);
                var more = reader.Stream.Since(next, Keep, 1).Rows.Count > 0;
                var answer = new JsonObject { ["rows"] = new JsonArray([.. rows]), ["next"] = next };
                if (more) answer["more"] = true;
                if (reader.Stream.Dropped(args.Str("since")) is { Count: > 0 } dropped) answer["dropped"] = dropped;
                if (reader.Stream.Unplaced(args.Str("since"), next) is { Count: > 0 } unplaced) answer["unplaced"] = unplaced;
                answer["counts"] = reader.Stream.Counts();
                answer["on"] = reader.Stream.On();
                SchemaCheck.Answer("bg3_events", answer);
                return Task.FromResult(Results.Text(answer));
            }),

        new(Define("bg3_logs",
                "Text of the Script Extender console: mods' prints, warnings, Lua errors. With story=true the Osiris trace instead "
                + "(every story event, rule fired and fact changed; needs bg3_up with story_log=true).",
                """
                {
                 "type": "object",
                 "properties": {
                  "lines": {"type": "integer", "description": "How many of the last lines (default 60)."},
                  "grep": {"type": "string", "description": "Only lines matching this regular expression."},
                  "story": {"type": "boolean"}
                 }
                }
                """),
            (args, _) =>
            {
                var (copy, _) = host.Begin();
                var lines = copy.Logs(args.Flag("story"), args.Int("lines") ?? 60, args.Str("grep"));
                return Task.FromResult(Results.Text(lines.Count > 0 ? string.Join("\n", lines) : "(nothing)"));
            }),

        new(Define("bg3_timeline",
                "The start's timeline of the running game as GateKit's Script Extender recorded it (Ext.StartupVideos.Timeline): every "
                + "stage of the start from the process's first moment, then loads, the loading screen, hints drawn, the interface's pages "
                + "and timelines, read after the fact - also what happened before any Lua existed. One line a row: its place, ms since "
                + "the process was created, side, kind, fields.",
                """
                {
                 "type": "object",
                 "properties": {
                  "kinds": {"type": "array", "items": {"type": "string"}, "description": "Only rows of these kinds (State, UIState, HintShown, LoadingProgress, TimelinePlayback, Step, ...)."},
                  "counters": {"type": "boolean", "description": "Also what the process had read and spent by each row."},
                  "json": {"type": "boolean", "description": "The rows as JSON {rows, total, dropped} instead of lines."}
                 }
                }
                """),
            async (args, cancel) =>
            {
                var (copy, _) = host.Begin();
                var kinds = args.Strings("kinds") is { Count: > 0 } named ? StartTimeline.Kinds(string.Join(",", named)) : null;
                var read = await StartTimeline.ReadAsync(copy.Session(), kinds, cancel);
                return Results.Text(args.Flag("json") ? Results.Json(read) : string.Join("\n", StartTimeline.Lines(read, args.Flag("counters"))));
            }),

        new(Define("bg3_reload",
                "Hot reload: copy a mod's Lua into the running game and restart Lua in place (about a second, no game restart). Pass "
                + "mod_root once (the folder holding Mods\\<Folder>\\ScriptExtender\\Lua) to link a mod; later calls reload everything "
                + "linked. Check bg3_logs afterwards for Lua errors.",
                """
                {
                 "type": "object",
                 "properties": {"mod_root": {"type": "string"}}
                }
                """),
            async (args, _) =>
            {
                var (copy, _) = host.Begin();
                var linked = args.Str("mod_root") is { Length: > 0 } root ? copy.Link(root) : [.. copy.LooseSources().Keys];
                var took = await copy.ReloadAsync();
                return Results.Text(string.Create(CultureInfo.InvariantCulture,
                    $"reloaded {string.Join(", ", linked)} in {took.TotalSeconds:0.0} s"));
            }),

        new(Define("bg3_dialog",
                "The dialogue on screen: speaker, the line being said, and the numbered answers the player can choose. Pass `answer` to "
                + "choose one first; the result is then the dialogue after it moved on ('no dialogue' when it ended). Lines without choices "
                + "show a single answer such as 'Continue' - choose 1 to advance.",
                """
                {
                 "type": "object",
                 "properties": {"answer": {"type": "integer", "description": "Answer number to choose, as listed."}}
                }
                """),
            async (args, cancel) =>
            {
                var (copy, _) = host.Begin();
                var session = copy.Session();
                Task<JsonNode?> Dialog() => session.UiAsync("return GK.Dialog()");
                if (args.Int("answer") is { } n && n != 0)
                {
                    var before = await Dialog();
                    await session.UiAsync($"return GK.Answer({n})");
                    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
                    while (true)
                    {
                        var now = await Dialog();
                        if (!JsonNode.DeepEquals(before, now)) return Results.Text(now is null ? "no dialogue" : Results.Render(now));
                        if (DateTime.UtcNow > deadline)
                            throw new CopyException($"the dialogue did not change after answer {n}: {Results.Json(before)}");
                        await Task.Delay(300, cancel);
                    }
                }
                var dialog = await Dialog();
                return Results.Text(dialog is null ? "no dialogue" : Results.Render(dialog));
            }),

        new(Define("bg3_fast_scenes",
                "Play dialogues and cutscenes `speed` times faster (default 20) so they reach the next choice almost at once; nothing is "
                + "skipped, the story still passes every node. Stays on for later scenes too. speed 1 returns to normal.",
                """
                {
                 "type": "object",
                 "properties": {"speed": {"type": "number"}}
                }
                """),
            async (args, _) =>
            {
                var (copy, _) = host.Begin();
                var speed = args.Num("speed") ?? 20;
                await copy.Session().ClientAsync(string.Create(CultureInfo.InvariantCulture, $"return GK.FastScenes({speed})"));
                return Results.Text(string.Create(CultureInfo.InvariantCulture, $"scenes now play at x{speed:0.######}"));
            }),

        new(Define("bg3_checkpoint",
                "Save the running game's world as it is now, so bg3_reset can return to it. Use right after bg3_up, before changing "
                + "anything. The save is GateKit's own and disappears with bg3_down.", Empty),
            async (_, _) =>
            {
                var (copy, _) = host.Begin();
                return Results.Text("checkpoint saved: " + await copy.Session().CheckpointAsync());
            }),

        new(Define("bg3_reset",
                "Bring back the world of the last bg3_checkpoint without restarting the game: about 12 s instead of a 30-45 s restart. "
                + "Positions, items, gold, flags and story state return to the checkpoint; Lua state of mods is restarted.", Empty),
            async (_, _) =>
            {
                var (copy, _) = host.Begin();
                var took = await copy.Session().ResetAsync();
                return Results.Text(string.Create(CultureInfo.InvariantCulture, $"world reset in {took.TotalSeconds:0.0} s; a standing capture (bg3_seen_start) does not survive it - start it again if one was running"));
            }),

        new(Define("bg3_key",
                "Press a key in the game; works while the game window is in the background. Names: escape, enter, space, tab, backspace, "
                + "left, up, right, down, f1..f12, or one letter or digit. Mouse clicks cannot be sent this way - drive the UI with bg3_lua. "
                + "A press is a tap; `hold` keeps the key down for that many seconds - moving or turning the camera (w a s d, q e) needs "
                + "it - and the call returns once the key is up again.",
                """
                {
                 "type": "object",
                 "required": ["key"],
                 "properties": {
                  "key": {"type": "string"},
                  "hold": {"type": "number", "description": "Seconds to hold the key down (0 to 30). Omitted: a tap."}
                 }
                }
                """),
            async (args, _) =>
            {
                var (copy, _) = host.Begin();
                var key = args.Need("key");
                var hold = args.Num("hold");
                if (hold is > 0)
                {
                    await Task.Run(() => copy.Key(key, hold));
                    return Results.Text(string.Create(CultureInfo.InvariantCulture, $"held {key} for {hold:0.###} s, released"));
                }
                copy.Key(key);
                return Results.Text("pressed " + key);
            }),

        new(Define("bg3_sound",
                "How loud this copy of the game is, so several running games are not one wall of noise. Every copy mixes its own sound, "
                + "so this changes only the instance the server works on and nothing of the player's own settings. action 'list' (default) "
                + "gives the twelve categories of the options screen with their volumes (0 to 100): master, fx, ambient, music, fight, "
                + "cutscene, ui, voice, dialogue, narrator, overhead, and rumble, which is controller vibration and has no volume. 'set' "
                + "takes category and volume; 'mute' silences a category (without one, the master, which silences the copy) and remembers "
                + "how loud it was; 'unmute' puts it back. 'unfocused' reads the game's own flag for silencing itself while its window is "
                + "not in front, and sets it with `on` (the game applies it at the next change of focus). The options screen keeps showing "
                + "the player's own slider positions: the game does not publish them, so they are neither read nor written here.",
                """
                {
                 "type": "object",
                 "properties": {
                  "action": {"type": "string", "enum": ["list", "set", "mute", "unmute", "unfocused"], "description": "Default list."},
                  "category": {"type": "string", "description": "The game's key (MusicVolume), the short name (music) or the RTPC name. Omit for the master."},
                  "volume": {"type": "number", "description": "0 (silent) to 100 (the slider's full position). Required by set."},
                  "on": {"type": "boolean", "description": "For unfocused: set the flag. Omitted, the flag is only read."}
                 }
                }
                """),
            async (args, _) =>
            {
                var (copy, _) = host.Begin();
                var sound = copy.Sound();
                var category = SoundControl.Category(args.Str("category"));
                switch ((args.Str("action") ?? "list").ToLowerInvariant())
                {
                    case "list":
                        var flag = await sound.WhenUnfocusedAsync(null);
                        return Results.Text(string.Join("\n", SoundControl.Lines(await sound.ListAsync(), flag?.GetValue<bool>())));
                    case "set":
                        var volume = args.Num("volume") ?? throw new MissingArgumentException("volume");
                        var set = await sound.SetAsync(category, SoundControl.Volume(volume));
                        return Results.Text($"{category ?? "MasterVolume"} set to {Results.Render(set)}");
                    // Wish 11: mute answered a bare `59.0` - the volume it remembered, with nothing
                    // to say so. Each answer now says what was done.
                    case "mute":
                        var was = await sound.MuteAsync(category);
                        return Results.Text($"muted {category ?? "MasterVolume (this copy is silent)"}; it was at {Results.Render(was)}, "
                            + "which unmute puts back");
                    case "unmute":
                        var restored = await sound.UnmuteAsync(category);
                        return Results.Text(restored is JsonArray { Count: > 0 } keys
                            ? "unmuted " + string.Join(", ", keys.Select(k => k?.GetValue<string>())) + " (back to the volume each had)"
                            : $"nothing to unmute: {category ?? "no category"} was not muted by bg3_sound (a Lua reset forgets what was)");
                    case "unfocused":
                        var now = await sound.WhenUnfocusedAsync(args.Has("on") ? args.Flag("on") : null);
                        return Results.Text("mute when the window is not in front: " + (now?.GetValue<bool>() == true ? "on" : "off"));
                    default:
                        throw new CopyException($"bg3_sound: unknown action '{args.Str("action")}'; one of list, set, mute, unmute, unfocused");
                }
            }),

        new(Define("bg3_screenshot",
                "A picture of the game. The game takes it itself, off the frame it is about to show and before any overlay is drawn over "
                + "it, so it works while the window is in the background or covered by other windows; nothing is moved, focused or paused. "
                + "With overlay the frame is taken after the extender's own layer is drawn.",
                """
                {
                 "type": "object",
                 "properties": {"width": {"type": "integer", "description": "Scale down to this width in pixels (default 1150)."},
                                "overlay": {"type": "boolean", "description": "With the extender's own layer in it: its windows, GateKit's console, a picture of GK.Show (default false)."}}
                }
                """),
            (args, _) =>
            {
                var (copy, _) = host.Begin();
                var file = Path.Combine(copy.Layout.State, "shots",
                    $"mcp-{DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture)}.jpg");
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                // The picture, and where it was saved: `gatekit mcp call` prints text only, and a
                // screenshot answered nothing there (N33 of the third run); a file can be read by anyone.
                var shot = copy.Shot(file, args.Int("width") ?? 1150, args.Flag("overlay"));
                var result = Results.Image(File.ReadAllBytes(shot), "image/jpeg");
                result.Content.Add(new TextContentBlock { Text = "saved: " + Path.GetFullPath(shot) });
                return Task.FromResult(result);
            }),

        new(Define("bg3_call",
                "Call any function of the bridge by name with JSON arguments and get its result: {side: 'server', function: 'Stage.Fight', "
                + "args: [...]} calls GK.Stage.Fight(...). A function added to the bridge's Lua is callable this way at once (after "
                + "`gatekit bridge reload`), with no tool of its own. Side 'ui' for functions that touch the game's UI. bg3_api lists what there is.",
                """
                {
                 "type": "object",
                 "required": ["side", "function"],
                 "properties": {
                  "side": {"type": "string", "enum": ["client", "server", "ui"]},
                  "function": {"type": "string", "description": "Path under GK, e.g. Party or Stage.Fight."},
                  "args": {"type": "array", "description": "Positional arguments, as JSON values."},
                  "timeout": {"type": "number", "description": "Seconds (default 20)."}
                 }
                }
                """),
            async (args, cancel) =>
            {
                var (copy, _) = host.Begin();
                var values = args.Node("args") as JsonArray ?? [];
                var result = await CallFunctionAsync(copy.Session(), args.Need("side"), args.Need("function"),
                    values, args.Seconds("timeout", 20), cancel);
                return Results.Text(result);
            }),

        new(Define("bg3_api",
                "The bridge's functions (GK.*) as written in its Lua source: name, parameters, the comment above each, the file, and "
                + "`side` - where to call it: server, client, ui (client code run in the UI context), or client or server (a shared "
                + "file: each side has its own copy). A function called on another side is nil there. `class`: fair (a player could "
                + "do it through the game itself), cheat or observation, with `why` where the name does not say; inside a test's step "
                + "of class fair a cheat is refused, inside observation anything but an observation. Works without a running game. "
                + "`filter` narrows by any text.",
                """
                {
                 "type": "object",
                 "properties": {"filter": {"type": "string"}}
                }
                """),
            (args, _) =>
            {
                var (copy, _) = host.Begin();
                return Task.FromResult(Results.Text(BridgeApi(copy, args.Str("filter"))));
            }),
    ];

    const string Empty = """{"type": "object", "properties": {}}""";

    static Tool Define(string name, string description, string schema) =>
        new() { Name = name, Description = description, InputSchema = JsonDocument.Parse(schema).RootElement };

    /// <summary>Whether a recorded film plays (D15 of the day run: it read as an endless
    /// StartLoading); null when the client cannot say.</summary>
    static async Task<JsonNode?> Film(GameSession session, bool running)
    {
        if (!running || session.State(Side.Client) is null) return null;
        try
        {
            return await session.LuaAsync("client", "return GK.Gui.FilmPlaying and GK.Gui.FilmPlaying()", TimeSpan.FromSeconds(5));
        }
        catch (CopyException) { return null; }
    }

    /// <summary>A side's heartbeat as bg3_status shows it: what it says only when it is fresh and a game
    /// of this copy runs; otherwise when it was written and that nothing is answering.</summary>
    static JsonNode? Beat(GameSession session, Side side, bool running)
    {
        var path = session.Bridge.StatePath(side);
        if (!File.Exists(path)) return null;
        var fresh = running ? session.State(side) : null;
        if (fresh is not null) return Beat(fresh);
        var written = File.GetLastWriteTime(path);
        return new JsonObject
        {
            ["gameState"] = null,
            ["stale"] = $"last heartbeat written {written.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}; "
                + (running ? "the game runs but this side has not answered for over 5 s" : "no game of this copy is running"),
        };
    }

    static JsonNode? Beat(Heartbeat? state) => state is null ? null : new JsonObject
    {
        ["side"] = state.Side,
        ["boot"] = state.Boot,
        ["time"] = state.Time,
        ["gameState"] = state.GameState,
        ["queue"] = state.Queue,
        ["done"] = state.Done,
    };

    /// <summary>Lua's own truth: only nil and false are false (0 and an empty table are not).</summary>
    static bool Truthy(JsonNode? value) => value is not null && !(value is JsonValue v && v.TryGetValue<bool>(out var b) && !b);

    // --- any bridge function, without a tool of its own ---

    /// <summary>Calls GK.&lt;path&gt;(...) with JSON arguments.</summary>
    public static Task<JsonNode?> CallFunctionAsync(GameSession session, string side, string function,
        JsonArray arguments, TimeSpan timeout, CancellationToken cancel = default)
    {
        var path = function.StartsWith("GK.", StringComparison.Ordinal) ? function[3..] : function;
        if (path.Length == 0 || !path.Split('.').All(part => Identifier().IsMatch(part)))
            throw new CopyException($"not a function path: '{function}' (expected e.g. Stage.Fight or GK.Party)");
        var payload = arguments.ToJsonString();
        // A Lua long string [=[ ... ]=] whose closing bracket the payload does not contain. Never
        // level 0: "[[" followed by a JSON array's "[" reads as something else.
        var level = 1;
        while (level < 8 && payload.Contains($"]{new string('=', level)}]", StringComparison.Ordinal)) level++;
        var (opening, closing) = ($"[{new string('=', level)}[", $"]{new string('=', level)}]");
        var code = $"""
            local f = GK
            for part in ('{path}'):gmatch('[^.]+') do f = f and f[part] end
            if type(f) ~= 'function' then error('GK.{path} is not a function on this side') end
            local a = Ext.Json.Parse({opening}{payload}{closing})
            return f(table.unpack(a, 1, {arguments.Count}))
            """;
        return session.LuaAsync(side, code, timeout, cancel);
    }

    // --- the bridge's own API, read from its Lua ---

    /// <summary>Every function of the bridge mods' Lua (<see cref="BridgeVerbs.Read"/>) with the `---`
    /// comment above it and its class from the mod's registry (Shared/VerbClasses.json).</summary>
    public static JsonArray BridgeApi(Copies.GameCopy copy, string? filter)
    {
        var found = new JsonArray();
        foreach (var mod in copy.BridgeMods)
        {
            var root = Path.Combine(mod.Root, "Mods", mod.Folder, "ScriptExtender", "Lua");
            if (!Directory.Exists(root)) continue;
            var classes = VerbClasses.Of(root);
            foreach (var verb in BridgeVerbs.Read(root))
            {
                var known = classes.ClassOf(verb.Path);
                var entry = new JsonObject
                {
                    ["function"] = verb.Path,
                    ["parameters"] = verb.Parameters,
                    ["where"] = verb.Where,
                    ["side"] = verb.Side,
                    ["class"] = known?.Class ?? "unclassed",
                    ["about"] = verb.About,
                };
                if (known?.Why is { } why) entry["why"] = why;
                if (filter is not { Length: > 0 } wanted
                    || JsonText.Inline(entry).Contains(wanted, StringComparison.OrdinalIgnoreCase))
                    found.Add(entry);
            }
        }
        return found;
    }

    /// <summary>The side a bridge function is called on, from the file it is written in.</summary>
    public static string SideOf(string where) => BridgeVerbs.SideOf(where);

    [GeneratedRegex(@"^[A-Za-z_]\w*$")]
    private static partial Regex Identifier();
}
