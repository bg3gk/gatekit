using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GateKit.Bridge;
using GateKit.Stand;
using ModelContextProtocol.Protocol;

namespace GateKit.Mcp;

/// <summary>
/// The tools of the old `bg3` server (docs/contracts/mcp.md, section 1.6), on the C# stand.
/// Names, schemas and result texts are the old ones; the gaps the contract lists are filled and
/// each fix is noted there.
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
                  "level": {"type": "string", "description": "MODULE:LEVEL, e.g. GustavX:TUT_Avernus_C. Starts a fresh test game straight in that level (about 40 s) instead of the last save; only the base game, the bridge and `mods` are loaded."},
                  "save": {"type": "string", "description": "Load this save (its folder name under Savegames/Story) instead of the last one."},
                  "mods": {"type": "array", "items": {"type": "string"}, "description": "Mod folder names to load besides the base game. Omit without `level` to keep the player's own mod list."},
                  "story_log": {"type": "boolean", "description": "Also record the Osiris trace for bg3_logs story=true."},
                  "game_args": {"type": "array", "items": {"type": "string"}, "description": "Extra game arguments."}
                 }
                }
                """),
            async (args, cancel) =>
            {
                var (stand, log) = host.Begin();
                await stand.UpAsync(new UpOptions(args.Str("level"), args.Str("save"), args.Strings("mods"),
                    args.Flag("story_log"), args.Strings("game_args")), cancel);
                return Results.Text(log.ToString().Trim() is { Length: > 0 } printed ? printed : "playable");
            }),

        new(Define("bg3_down", "Close the game and restore the saves and mod list to what they were before bg3_up.", Empty),
            (_, _) =>
            {
                var (stand, _) = host.Begin();
                return Task.FromResult(Results.Text(stand.Down()));
            }),

        new(Define("bg3_status", "Whether the game runs and what state each side (client, server) last reported (Menu, LoadLevel, Running, ...).", Empty),
            (_, _) =>
            {
                var (stand, _) = host.Begin();
                var session = stand.Session();
                var any = TimeSpan.FromDays(365);
                return Task.FromResult(Results.Text(new JsonObject
                {
                    ["processes"] = new JsonArray([.. stand.GamePids().Select(p => (JsonNode)JsonValue.Create(p))]),
                    ["client"] = Beat(session.State(Side.Client, any)),
                    ["server"] = Beat(session.State(Side.Server, any)),
                    ["client_alive"] = session.State(Side.Client) is not null,
                }));
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
                var (stand, log) = host.Begin();
                var result = await stand.Session().LuaAsync(args.Need("side"), args.Need("code"), args.Seconds("timeout", 20), cancel);
                var printed = log.ToString().TrimEnd('\r', '\n');
                return Results.Text((printed.Length > 0 ? printed + "\n" : "") + Results.Json(result));
            }),

        new(Define("bg3_wait", "Poll a Lua expression on one side until it is truthy; returns its value. Fails on timeout.",
                """
                {
                 "type": "object",
                 "required": ["side", "expression"],
                 "properties": {
                  "side": {"type": "string", "enum": ["client", "server", "ui"]},
                  "expression": {"type": "string"},
                  "timeout": {"type": "number", "description": "Seconds (default 30)."}
                 }
                }
                """),
            async (args, cancel) =>
            {
                var (stand, _) = host.Begin();
                var side = args.Need("side");
                var expression = args.Need("expression");
                var deadline = DateTime.UtcNow + args.Seconds("timeout", 30);
                while (true)
                {
                    var value = await stand.Session().LuaAsync(side, $"return ({expression})", TimeSpan.FromSeconds(20), cancel);
                    if (Truthy(value)) return Results.Text(value);
                    if (DateTime.UtcNow > deadline) throw new StandException($"timed out waiting on {side} for: {expression}");
                    await Task.Delay(300, cancel);
                }
            }),

        new(Define("bg3_state",
                "The game in one call, no screenshot needed: client (game state, controller mode, controlled character), live scenes "
                + "(dialogues, cutscenes: phase, time, actors) and every party member (name, level, position, health, in combat, weapons "
                + "drawn, equipment by slot).", Empty),
            async (_, cancel) =>
            {
                var (stand, _) = host.Begin();
                var session = stand.Session();
                var client = await session.LuaAsync("client", "return { client = GK.Client(), scenes = GK.Scenes() }", TimeSpan.FromSeconds(20), cancel);
                var party = await session.LuaAsync("server", "return GK.Party()", TimeSpan.FromSeconds(20), cancel);
                var state = client as JsonObject ?? new JsonObject { ["client"] = client };
                state["party"] = party;
                return Results.Text(state);
            }),

        new(Define("bg3_events",
                "What happened in the game since event number `since` (0: everything kept, up to 500 per side): game state changes and "
                + "Osiris events such as DialogStarted, DialogEnded, Equipped, Unequipped, TemplateAddedTo, CombatStarted, EnteredLevel. "
                + "Returns `last` to pass as `since` next time. More events: bg3_lua server `GK.Watch(\"EventName\", arity)`.",
                """
                {
                 "type": "object",
                 "properties": {
                  "since_client": {"type": "integer"},
                  "since_server": {"type": "integer"}
                 }
                }
                """),
            async (args, cancel) =>
            {
                var (stand, _) = host.Begin();
                var session = stand.Session();
                return Results.Text(new JsonObject
                {
                    ["client"] = await session.LuaAsync("client", $"return GK.Events({args.Int("since_client") ?? 0})", TimeSpan.FromSeconds(20), cancel),
                    ["server"] = await session.LuaAsync("server", $"return GK.Events({args.Int("since_server") ?? 0})", TimeSpan.FromSeconds(20), cancel),
                });
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
                var (stand, _) = host.Begin();
                var lines = stand.Logs(args.Flag("story"), args.Int("lines") ?? 60, args.Str("grep"));
                return Task.FromResult(Results.Text(lines.Count > 0 ? string.Join("\n", lines) : "(nothing)"));
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
                var (stand, _) = host.Begin();
                var linked = args.Str("mod_root") is { Length: > 0 } root ? stand.Link(root) : [.. stand.LooseSources().Keys];
                var took = await stand.ReloadAsync();
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
                var (stand, _) = host.Begin();
                var session = stand.Session();
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
                            throw new StandException($"the dialogue did not change after answer {n}: {Results.Json(before)}");
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
                var (stand, _) = host.Begin();
                var speed = args.Num("speed") ?? 20;
                await stand.Session().ClientAsync(string.Create(CultureInfo.InvariantCulture, $"return GK.FastScenes({speed})"));
                return Results.Text(string.Create(CultureInfo.InvariantCulture, $"scenes now play at x{speed:0.######}"));
            }),

        new(Define("bg3_checkpoint",
                "Save the running game's world as it is now, so bg3_reset can return to it. Use right after bg3_up, before changing "
                + "anything. The save is the stand's own and disappears with bg3_down.", Empty),
            async (_, _) =>
            {
                var (stand, _) = host.Begin();
                return Results.Text("checkpoint saved: " + await stand.Session().CheckpointAsync());
            }),

        new(Define("bg3_reset",
                "Bring back the world of the last bg3_checkpoint without restarting the game: about 12 s instead of a 30-45 s restart. "
                + "Positions, items, gold, flags and story state return to the checkpoint; Lua state of mods is restarted.", Empty),
            async (_, _) =>
            {
                var (stand, _) = host.Begin();
                var took = await stand.Session().ResetAsync();
                return Results.Text(string.Create(CultureInfo.InvariantCulture, $"world reset in {took.TotalSeconds:0.0} s"));
            }),

        new(Define("bg3_key",
                "Press a key in the game; works while the game window is in the background. Names: escape, enter, space, tab, backspace, "
                + "left, up, right, down, f1..f12, or one letter or digit. Mouse clicks cannot be sent this way - drive the UI with bg3_lua.",
                """
                {
                 "type": "object",
                 "required": ["key"],
                 "properties": {"key": {"type": "string"}}
                }
                """),
            (args, _) =>
            {
                var (stand, _) = host.Begin();
                var key = args.Need("key");
                stand.Key(key);
                return Task.FromResult(Results.Text("pressed " + key));
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
                var (stand, _) = host.Begin();
                var sound = stand.Sound();
                var category = SoundControl.Category(args.Str("category"));
                switch ((args.Str("action") ?? "list").ToLowerInvariant())
                {
                    case "list":
                        var flag = await sound.WhenUnfocusedAsync(null);
                        return Results.Text(string.Join("\n", SoundControl.Lines(await sound.ListAsync(), flag?.GetValue<bool>())));
                    case "set":
                        var volume = args.Num("volume") ?? throw new KeyNotFoundException("'volume'");
                        return Results.Text(await sound.SetAsync(category, SoundControl.Volume(volume)));
                    case "mute":
                        return Results.Text(await sound.MuteAsync(category));
                    case "unmute":
                        return Results.Text(await sound.UnmuteAsync(category));
                    case "unfocused":
                        var now = await sound.WhenUnfocusedAsync(args.Has("on") ? args.Flag("on") : null);
                        return Results.Text("mute when the window is not in front: " + (now?.GetValue<bool>() == true ? "on" : "off"));
                    default:
                        throw new StandException($"bg3_sound: unknown action '{args.Str("action")}'; one of list, set, mute, unmute, unfocused");
                }
            }),

        new(Define("bg3_screenshot",
                "A picture of the game. The game takes it itself, off the frame it is about to show and before any overlay is drawn over "
                + "it, so it works while the window is in the background or covered by other windows; nothing is moved, focused or paused.",
                """
                {
                 "type": "object",
                 "properties": {"width": {"type": "integer", "description": "Scale down to this width in pixels (default 1150)."}}
                }
                """),
            (args, _) =>
            {
                var (stand, _) = host.Begin();
                var file = Path.Combine(stand.Layout.State, "shots",
                    $"mcp-{DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture)}.jpg");
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                return Task.FromResult(Results.Image(File.ReadAllBytes(stand.Shot(file, args.Int("width") ?? 1150)), "image/jpeg"));
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
                var (stand, _) = host.Begin();
                var values = args.Node("args") as JsonArray ?? [];
                var result = await CallFunctionAsync(stand.Session(), args.Need("side"), args.Need("function"),
                    values, args.Seconds("timeout", 20), cancel);
                return Results.Text(result);
            }),

        new(Define("bg3_api",
                "The bridge's functions (GK.*) as written in its Lua source: name, parameters, the comment above each, and the file (its "
                + "folder tells the side: Server, Client, Shared). Works without a running game. `filter` narrows by any text.",
                """
                {
                 "type": "object",
                 "properties": {"filter": {"type": "string"}}
                }
                """),
            (args, _) =>
            {
                var (stand, _) = host.Begin();
                return Task.FromResult(Results.Text(BridgeApi(stand, args.Str("filter"))));
            }),
    ];

    const string Empty = """{"type": "object", "properties": {}}""";

    static Tool Define(string name, string description, string schema) =>
        new() { Name = name, Description = description, InputSchema = JsonDocument.Parse(schema).RootElement };

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

    /// <summary>Calls GK.&lt;path&gt;(...) with JSON arguments; the port of the old `call_function`.</summary>
    public static Task<JsonNode?> CallFunctionAsync(GameSession session, string side, string function,
        JsonArray arguments, TimeSpan timeout, CancellationToken cancel = default)
    {
        var path = function.StartsWith("GK.", StringComparison.Ordinal) ? function[3..] : function;
        if (path.Length == 0 || !path.Split('.').All(part => Identifier().IsMatch(part)))
            throw new StandException($"not a function path: '{function}' (expected e.g. Stage.Fight or GK.Party)");
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

    /// <summary>Every `function GK.X(...)` of the bridge mods' Lua with the `---` comment above it.</summary>
    public static JsonArray BridgeApi(Stand.Stand stand, string? filter)
    {
        var found = new JsonArray();
        foreach (var mod in stand.BridgeMods)
        {
            var root = Path.Combine(mod.Root, "Mods", mod.Folder, "ScriptExtender", "Lua");
            if (!Directory.Exists(root)) continue;
            foreach (var file in Directory.EnumerateFiles(root, "*.lua", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                var comment = new List<string>();
                // Most of the bridge is written against a local alias - `local Act = GK.Act`, and
                // then `function Act.Lockpick(...)`. Reading only `function GK.x` hid 400-odd
                // functions, GK.Act, GK.Cheat and GK.Stage among them: the whole honest-play
                // surface was missing from the one tool that says what exists. The aliases are
                // collected as the file is read, so a name is resolved by what was bound above it.
                var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var line in File.ReadLines(file))
                {
                    var stripped = line.Trim();
                    if (stripped.StartsWith("---", StringComparison.Ordinal))
                    {
                        comment.Add(stripped[3..].Trim());
                        continue;
                    }
                    if (AliasLine().Match(line) is { Success: true } alias && alias.Groups[2].Value != "GK")
                        aliases[alias.Groups[1].Value] = alias.Groups[2].Value;
                    if (ApiLine().Match(line) is { Success: true } match)
                    {
                        var path = match.Groups[1].Value;
                        var head = path[..path.IndexOf('.')];
                        if (head != "GK")
                        {
                            // A local table that is not part of GK is private to the file.
                            if (!aliases.TryGetValue(head, out var bound)) { comment.Clear(); continue; }
                            path = bound + path[head.Length..];
                        }
                        var entry = new JsonObject
                        {
                            ["function"] = path,
                            ["parameters"] = match.Groups[2].Value.Trim(),
                            ["where"] = Path.GetRelativePath(root, file).Replace('\\', '/'),
                            ["about"] = string.Join(" ", comment),
                        };
                        if (filter is not { Length: > 0 } wanted
                            || entry.ToJsonString().Contains(wanted, StringComparison.OrdinalIgnoreCase))
                            found.Add(entry);
                    }
                    comment.Clear();
                }
            }
        }
        return found;
    }

    [GeneratedRegex(@"^function\s+([A-Za-z_]\w*(?:\.\w+)+)\s*\(([^)]*)\)")]
    private static partial Regex ApiLine();

    /// <summary>`local Act = GK.Act`, with or without a trailing `or {}`.</summary>
    [GeneratedRegex(@"^local\s+([A-Za-z_]\w*)\s*=\s*(GK(?:\.\w+)*)\b")]
    private static partial Regex AliasLine();

    [GeneratedRegex(@"^[A-Za-z_]\w*$")]
    private static partial Regex Identifier();
}
