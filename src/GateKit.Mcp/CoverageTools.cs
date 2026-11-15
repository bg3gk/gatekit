using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GateKit.Bridge;
using GateKit.Copies;
using ModelContextProtocol.Protocol;

namespace GateKit.Mcp;

/// <summary>
/// The coverage loop, as tools an agent can reach.
///
/// Mapping the engine means noticing a component type the first moment anything carries it,
/// then closing in until the claim is evidence rather than confidence. The record these tools
/// write is described in research/coverage/README.md; the reading in the game is done by the
/// bridge's <c>GK.Probe</c> / <c>GK.Seen</c>.
///
/// The tools, in the order a run uses them:
/// <list type="bullet">
/// <item><c>bg3_pause</c> - stop the world while you think.</item>
/// <item><c>bg3_seen_start</c> / <c>_mark</c> / <c>_new</c> / <c>_drain</c> / <c>_status</c> -
///   the standing capture: what came alive, and its bytes.</item>
/// <item><c>bg3_coverage_todo</c> - the ledger of what has never been seen, which decides where
///   a run goes next.</item>
/// <item><c>bg3_probe</c> - the per-offset survey of one component: the start of a layout.</item>
/// <item><c>bg3_probe_fields</c> - raw bytes beside what Script Extender returns for the same
///   instance, in the same call: how a wrong layout is caught.</item>
/// <item><c>bg3_recipe</c> - a confirmed finding leaves the shortest sequence that makes the
///   component appear again, as a test `gatekit test` replays (<see cref="RecipeTest"/>).</item>
/// <item><c>bg3_run_report</c> - what a run found, what is open, and what it spent.</item>
/// </list>
/// </summary>
public static class CoverageTools
{
    const string Empty = """{"type": "object", "properties": {}}""";
    static readonly string[] Sides = ["client", "server"];

    /// <summary>Asks one side, and when it gives no answer at all says so instead of failing the
    /// whole tool: in the main menu the server stays in UnloadLevel and never answers, while the
    /// client's capture holds what it saw there (N31 of the third coverage run, where every
    /// two-sided tool timed out and the client's main-menu sightings were lost).</summary>
    internal static async Task<(JsonNode? Answer, string? Gone)> AskSide(GameSession session, string side, string code,
        TimeSpan limit, CancellationToken cancel)
    {
        try { return (await session.LuaAsync(side, code, limit, cancel), null); }
        catch (CopyException e) when (IsSilence(e))
        {
            return (null, e.Message + " - that side does not answer (in the main menu the server stays in UnloadLevel); "
                + "the other side was still asked");
        }
    }

    /// <summary>Whether a copy error is a side that did not answer, rather than a Lua error.</summary>
    internal static bool IsSilence(CopyException e) => e.Message.StartsWith("no answer from", StringComparison.Ordinal);

    static readonly JsonSerializerOptions Stored = new()
    {
        WriteIndented = true,
        IndentSize = 1,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    static Tool Define(string name, string description, string schema) => new()
    {
        Name = name,
        Description = description,
        InputSchema = JsonSerializer.Deserialize<JsonElement>(schema),
    };

    /// <summary>Where the coverage record lives: the repository's own folder, never a machine path.</summary>
    static string Root()
    {
        var repository = CopySettings.RepositoryRoot()
            ?? throw new CopyException("the coverage record lives in the repository, and this is not a checkout of it");
        return Path.Combine(repository, "research", "coverage");
    }

    /// <summary>The record of this server: the host's when it names one (a test), else the repository's.</summary>
    static string Root(ToolHost host) => host.CoverageRoot ?? Root();

    /// <summary>One writer of the committed record at a time, across processes
    /// (<see cref="GateKit.Copies.Record.Lock"/>, shared with the ledgers and `gatekit test`).</summary>
    public static class RecordLock
    {
        public static IDisposable Take(string root, int seconds = 120) => GateKit.Copies.Record.Lock(root, seconds);
    }

    static JsonObject ReadObject(string path) =>
        File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? [] : [];

    /// <summary>Always with newlines, never carriage returns: the record is committed, and a
    /// Windows run must not rewrite every line of it for a machine on the other side.</summary>
    static void Write(string path, JsonNode value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var text = value.ToJsonString(Stored).ReplaceLineEndings("\n");
        // The record is committed while a run goes on, and git maps a file it is reading: for that
        // moment Windows refuses to write it ("a user-mapped section is open"). The lock keeps our
        // own writers apart; somebody else's reader is waited out, briefly, rather than failed on.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.WriteAllText(path, text);
                return;
            }
            catch (IOException) when (attempt < 40)
            {
                Thread.Sleep(100);
            }
        }
    }

    public static IReadOnlyList<GateKitTool> Create(ToolHost host) =>
    [
        Pause(host),
        SeenStart(host), SeenMark(host), SeenNew(host), SeenDrain(host), SeenStatus(host),
        Todo(), Probe(host), ProbeView(host), ProbeFields(host), ProbeWatch(host), Verify(host),
        Recipe(), RunReport(),
    ];

    // ---------------------------------------------------------------------------------------
    // What moved across a moment

    static GateKitTool ProbeWatch(ToolHost host) => new(
        Define("bg3_probe_watch",
            "Read a component's raw bytes, let one moment pass, read them again, and say WHICH BYTES MOVED. This is the third step "
            + "of closing in - forcing a value - made into one call: arrange the situation, watch the field, and you have the "
            + "evidence that a field means what you think rather than a guess that fits.\n"
            + "Use it like this: `do` is what makes the moment happen (a Lua chunk - an action, a cheat, a setting changed); or "
            + "give `seconds` and simply watch time pass. Instances are paired by entity, so a field that moved on one entity and "
            + "not another is told apart from one that moved on all of them - which is usually the whole question.\n"
            + "A field that moves when you change one thing and stays put when you change another is the strongest evidence this "
            + "toolkit can produce without reading the code. A field that never moves whatever you do is worth recording as "
            + "unresolved rather than guessed at.",
            """
            {
             "type": "object",
             "required": ["side", "component"],
             "properties": {
              "side": {"type": "string", "enum": ["client", "server"]},
              "component": {"type": "string", "description": "The engine's own name."},
              "do": {"type": "string", "description": "Lua run on this side between the two readings: what makes the moment happen."},
              "do_side": {"type": "string", "enum": ["client", "server", "ui"], "description": "Run `do` on this side instead (the UI, say, while watching a client component)."},
              "seconds": {"type": "number", "description": "Wait this long between the readings instead of, or as well as, running `do`."},
              "max": {"type": "integer", "description": "Instances read (default 16)."},
              "unpause": {"type": "boolean", "description": "Let the world run during the moment and stop it again afterwards. Needed when the thing you are watching takes game time; without it a paused game may show nothing moving, which is not the same as nothing moving."}
             }
            }
            """),
        async (args, cancel) =>
        {
            var (copy, _) = host.Begin();
            var session = copy.Session();
            var side = args.Need("side");
            var component = args.Need("component");
            var read = $"return GK.Probe.Dump({Lua.Value(component)}, {args.Int("max") ?? 16})";

            var before = await session.LuaAsync(side, read, TimeSpan.FromSeconds(120), cancel);
            var wasPaused = false;
            if (args.Flag("unpause"))
            {
                var state = await session.LuaAsync("server", "return GK.Pause.State()", TimeSpan.FromSeconds(20), cancel);
                wasPaused = state.Field("paused")?.GetValue<bool>() ?? false;
                if (wasPaused) await session.LuaAsync("server", "return GK.Pause.Set(false)", TimeSpan.FromSeconds(20), cancel);
            }

            string? failed = null;
            if (args.Str("do") is { Length: > 0 } chunk)
            {
                try { await session.LuaAsync(args.Str("do_side") ?? side, chunk, TimeSpan.FromSeconds(120), cancel); }
                catch (Exception e) { failed = e.Message; }
            }
            if (args.Num("seconds") is { } seconds and > 0)
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(seconds, 120)), cancel);

            var after = await session.LuaAsync(side, read, TimeSpan.FromSeconds(120), cancel);
            if (wasPaused) await session.LuaAsync("server", "return GK.Pause.Set(true, \"bg3_probe_watch\")", TimeSpan.FromSeconds(20), cancel);

            var answer = Survey.RenderMoved(before, after, component);
            if (failed is not null)
                answer = $"the `do` chunk failed: {failed}\nWhat follows is what moved anyway, which may be nothing.\n\n" + answer;
            return Results.Text(answer);
        });

    // ---------------------------------------------------------------------------------------
    // The batch comparison: every layout against the bytes of the live instances

    static GateKitTool Verify(ToolHost host) => new(
        Define("bg3_verify",
            "Check many layouts at once against the running game: for every live instance of each component, each field must hold "
            + "what the raw bytes at its offset say. This is the only thing that moves a type into `verified live` - everything "
            + "else is a reading of the game's code that nothing has tested. It changes nothing in the game.\n"
            + "Aim it with `prefix` at the namespace you have just made come alive: a type with no live holder is skipped, not "
            + "failed, so a run that has just lit up a system should verify that system while it is still alive. A disagreement "
            + "is the valuable answer - it means the layout is wrong, ours or Script Extender's author's - and bg3_probe_fields "
            + "then shows that one instance byte by byte.\n"
            + "With `record`, the types that agreed are written into coverage.json as verified. Only pass it when the game is in a "
            + "state you would defend: the record says a layout was proved, and it is read by people who will not re-check it.",
            """
            {
             "type": "object",
             "required": ["side"],
             "properties": {
              "side": {"type": "string", "enum": ["client", "server"]},
              "prefix": {"type": "string", "description": "Only components whose name starts with this, e.g. \"esv::lock\". Omitted: every layout in the build, which is slow."},
              "max": {"type": "integer", "description": "Instances read per type (default 16)."},
              "record": {"type": "boolean", "description": "Write the types that agreed into coverage.json as verified."},
              "detail": {"type": "integer", "description": "How many disagreements to describe in full (default 20)."}
             }
            }
            """),
        async (args, cancel) =>
        {
            var (copy, _) = host.Begin();
            var session = copy.Session();
            var side = args.Need("side");
            var root = Root();
            var layouts = LayoutCheck.Load(root, args.Str("prefix") ?? "");
            if (layouts.Count == 0) return Results.Text("no layout in the build matches that prefix.");

            var max = args.Int("max") ?? 16;
            var agreed = new Dictionary<string, int>(StringComparer.Ordinal);
            var disagreed = new Dictionary<string, string>(StringComparer.Ordinal);
            var nothingAlive = new List<string>();
            var notCompared = new List<string>();

            // Forty types a call: the bridge answers one chunk, and a hundred round trips would
            // outlast the situation the run has just staged.
            foreach (var batch in layouts.Chunk(40))
            {
                var specs = string.Join(", ", batch.Select(l =>
                    $"{{ Name = {Lua.Value(l.Component)}, ExtName = {Lua.Value(l.ExtName)}, Fields = {Lua.List([.. l.Fields.Select(f => f.Name)])} }}"));
                var found = await session.LuaAsync(side, $"return GK.Probe.MappedMany({{{specs}}}, {max})",
                    TimeSpan.FromSeconds(180), cancel);
                foreach (var layout in batch)
                {
                    var rows = Rows(found?[layout.Component]).OfType<JsonObject>().ToList();
                    if (rows.Count == 0) { nothingAlive.Add(layout.Component); continue; }
                    var (compared, problem) = LayoutCheck.Compare(layout, rows);
                    if (problem is not null) disagreed[layout.Component] = problem;
                    else if (compared > 0) agreed[layout.Component] = compared;
                    else notCompared.Add(layout.Component);
                }
            }

            var recorded = args.Flag("record") ? Record(root, side, agreed) : 0;
            var answer = new JsonObject
            {
                ["side"] = side,
                ["layouts_checked"] = layouts.Count,
                ["agreed"] = agreed.Count,
                ["disagreed"] = disagreed.Count,
                ["nothing_alive"] = nothingAlive.Count,
                ["present_but_nothing_comparable"] = notCompared.Count,
                ["recorded_as_verified"] = recorded,
                ["disagreements"] = new JsonArray([.. disagreed.Values.Take(args.Int("detail") ?? 20).Select(t => (JsonNode)t!)]),
            };
            if (disagreed.Count > 0)
                answer["note"] = "A disagreement means the layout is wrong, not the game. Look at one instance with "
                    + "bg3_probe_fields before changing anything, and record what you find rather than working around it.";
            return Results.Text(answer);
        });

    /// <summary>Writes the types that agreed into coverage.json, for the side they were read on.</summary>
    static int Record(string root, string side, Dictionary<string, int> agreed)
    {
        if (agreed.Count == 0) return 0;
        using var held = RecordLock.Take(root);
        return RecordHeld(root, side, agreed, "bg3_verify", "mapped fields equal raw memory of the same live instance");
    }

    /// <summary><see cref="Record"/> for a caller that already holds the record's lock.</summary>
    static int RecordHeld(string root, string side, Dictionary<string, int> agreed, string by, string stage)
    {
        if (agreed.Count == 0) return 0;
        var path = Path.Combine(root, "coverage.json");
        var index = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var written = 0;
        foreach (var row in index["components"]!.AsArray())
        {
            if (row is not JsonObject component) continue;
            if (component["side"]?.GetValue<string>() != side) continue;
            if (component["name"]?.GetValue<string>() is not { } name || !agreed.TryGetValue(name, out var instances)) continue;
            if (component["mapped"] is null || component["verified"] is not null) continue;
            component["verified"] = new JsonObject
            {
                ["date"] = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["by"] = by,
                ["stage"] = stage,
                ["instances"] = instances,
            };
            written++;
        }
        Write(path, index);
        return written;
    }

    // ---------------------------------------------------------------------------------------
    // Pause

    static GateKitTool Pause(ToolHost host) => new(
        Define("bg3_pause",
            "Stop the world while you think, or let it go on. The whole coverage loop leans on this: nothing may come alive and "
            + "vanish between two of your decisions, or a finding stops being reproducible. Honestly: Baldur's Gate 3 has no pause "
            + "a mod can press, so this is the engine's own time scale at its floor (0.001) - one real second buys one millisecond "
            + "of game time. Everything is frozen for practical purposes and not mathematically stopped; the frame loop still runs, "
            + "so the bridge still answers; sound is mixed in real time and does not slow. It cooperates with GK.Speed, which cannot "
            + "put the pace back under it. Unpausing hands the pace back to whatever situation GK.Speed sees.",
            """
            {
             "type": "object",
             "properties": {
              "paused": {"type": "boolean", "description": "true stops the world, false lets it go on. Omitted: just report the state."},
              "by": {"type": "string", "description": "A note for the record: who asked for the pause and what for."}
             }
            }
            """),
        async (args, cancel) =>
        {
            var (copy, _) = host.Begin();
            var code = args.Has("paused")
                ? $"return GK.Pause.Set({(args.Flag("paused") ? "true" : "false")}, {Lua.Value(args.Str("by"))})"
                : "return GK.Pause.State()";
            return Results.Text(await copy.Session().LuaAsync("server", code, TimeSpan.FromSeconds(20), cancel));
        });

    // ---------------------------------------------------------------------------------------
    // The standing capture

    static GateKitTool SeenStart(ToolHost host) => new(
        Define("bg3_seen_start",
            "Begin the standing capture on both sides: every tick, each component type that has holders is counted, and the first "
            + "few distinct instances of each are sampled as raw bytes. Nothing is prepared in advance - whatever the game is made "
            + "to do feeds it. Start it once when the game is playable, then play; bg3_seen_new says what appeared, bg3_seen_drain "
            + "moves the samples into the record. A side that does not answer (the server in the main menu) is named under `gone` and the other side is still asked. The capture lives in the game's Lua state, so a world reset, a Lua reload or a "
            + "level change stops it: drain first, start again after.",
            """
            {
             "type": "object",
             "properties": {
              "label": {"type": "string", "description": "What is happening from now on (\"idle\", \"character creation\", \"first fight\"). It is stored with every sample and is how a recipe is later written."},
              "per_label": {"type": "integer", "description": "Samples kept per type per label (default 3)."},
              "per_type": {"type": "integer", "description": "Cap on samples of one type over the whole run (default 12)."},
              "max_bytes": {"type": "integer", "description": "Most bytes kept of one instance (default 1024)."},
              "role": {"type": "string", "description": "Your role in the run (runner world, pathfinder...), kept on every sighting this server records. Default GATEKIT_ROLE, else unknown."},
              "run": {"type": "string", "description": "The run's id, the same for every session of it. Default GATEKIT_RUN, else unknown."},
              "stretch": {"type": "string", "description": "Which stretch of your work this is. Default GATEKIT_STRETCH, else unknown."}
             }
            }
            """),
        async (args, cancel) =>
        {
            var (copy, _) = host.Begin();
            RecordBook.Context.Start(host.Instance, args.Str("role"), args.Str("run"), args.Str("stretch"));
            var options = new StringBuilder("{ Label = ").Append(Lua.Value(args.Str("label") ?? "idle"));
            if (args.Int("per_label") is { } perLabel) options.Append(", PerLabel = ").Append(perLabel);
            if (args.Int("per_type") is { } perType) options.Append(", PerType = ").Append(perType);
            if (args.Int("max_bytes") is { } maxBytes) options.Append(", MaxBytes = ").Append(maxBytes);
            options.Append(" }");
            var answers = new JsonObject();
            var session = copy.Session();
            // Every layout in the build goes with the capture, so each sample of such a type is also
            // read through Script Extender in the tick it is taken, and bg3_seen_drain compares the
            // two: the only way a type that lives one frame is ever verified (D14 of the day run).
            var layouts = LayoutCheck.Load(Root());
            foreach (var side in Sides)
            {
                var handed = await HandLayouts(session, side, layouts, cancel);
                var (answer, gone) = await AskSide(session, side, $"return GK.Seen.Start({options})", TimeSpan.FromSeconds(180), cancel);
                if (gone is null && answer is JsonObject started) started["layouts"] = handed;
                answers[side] = gone is null ? answer : new JsonObject { ["started"] = false, ["gone"] = gone };
            }
            return Results.Text(answers);
        });

    /// <summary>The layouts the capture reads through Script Extender, in batches; what it says when
    /// the bridge cannot take them (a bridge older than Seen.AddSpecs: reload it).</summary>
    static async Task<JsonNode?> HandLayouts(GameSession session, string side, IReadOnlyList<LayoutCheck.Layout> layouts, CancellationToken cancel)
    {
        if (layouts.Count == 0) return 0;
        var first = true;
        JsonNode? held = null;
        foreach (var batch in layouts.Chunk(120))
        {
            var specs = string.Join(", ", batch.Select(l =>
                $"[{Lua.Value(l.Component)}] = {{ ExtName = {Lua.Value(l.ExtName)}, Fields = {Lua.List([.. l.Fields.Select(f => f.Name)])} }}"));
            JsonNode? answer;
            string? gone;
            try
            {
                (answer, gone) = await AskSide(session, side, $"return GK.Seen.AddSpecs({{{specs}}}, {(first ? "true" : "false")})",
                    TimeSpan.FromSeconds(60), cancel);
            }
            catch (CopyException e)
            {
                // The capture still starts; its samples just cannot be compared on drain.
                return "not handed over (a bridge without GK.Seen.AddSpecs? `bridge-reload`): " + e.Message.Split('\n')[0];
            }
            if (gone is not null) return "not handed over: " + gone;
            first = false;
            held = answer;
        }
        return held;
    }

    static GateKitTool SeenMark(ToolHost host) => new(
        Define("bg3_seen_mark",
            "Name what happens from now on. Every sample taken until the next mark carries this label, and the label is what turns "
            + "a sighting into a recipe: it is the record of what the game was being made to do when the type came alive. Mark "
            + "before each thing you try, not after.",
            """
            {
             "type": "object",
             "required": ["label"],
             "properties": {"label": {"type": "string", "description": "A short description of the situation: \"opening the character sheet\", \"picking a lock\", \"long rest\"."}}
            }
            """),
        async (args, cancel) =>
        {
            var (copy, _) = host.Begin();
            var label = Lua.Value(args.Need("label"));
            var answers = new JsonObject();
            var session = copy.Session();
            foreach (var side in Sides)
            {
                var (answer, gone) = await AskSide(session, side, $"return GK.Seen.Mark({label})", TimeSpan.FromSeconds(30), cancel);
                answers[side] = gone is null ? answer : new JsonObject { ["gone"] = gone };
            }
            return Results.Text(answers);
        });

    static GateKitTool SeenNew(ToolHost host) => new(
        Define("bg3_seen_new",
            "What has come alive since you last asked - the cursor of the capture, and the cheapest question in the loop. Ask it "
            + "after every thing you try. A name here is a SIGHTING, not a finding: it says the type had a holder, not what the "
            + "type is. When something appears, stop going forward and close in on it (do it again, vary it, force a value, read "
            + "the code). Each row says which label it first appeared under and the most holders seen at once.",
            """
            {
             "type": "object",
             "properties": {"known": {"type": "boolean", "description": "Keep names the committed record has already seen before (default false: only types new to GateKit's whole record are listed, which is what a run is for)."}}
            }
            """),
        async (args, cancel) =>
        {
            var (copy, _) = host.Begin();
            var root = Root(host);
            var known = ReadObject(Path.Combine(root, "seen", "index.json"));
            var fresh = new JsonArray();
            var repeats = new JsonArray();
            var silent = new JsonObject();
            var session = copy.Session();
            foreach (var side in Sides)
            {
                var (found, gone) = await AskSide(session, side, "return GK.Seen.New()", TimeSpan.FromSeconds(60), cancel);
                if (gone is not null) { silent[side] = gone; continue; }
                foreach (var row in Rows(found))
                {
                    if (row is not JsonObject entry || entry["Name"]?.GetValue<string>() is not { } name) continue;
                    entry["side"] = side;
                    (known.ContainsKey(name) ? repeats : fresh).Add(entry.DeepClone());
                }
            }
            // A type named here is recorded here (D27 of the day run: two server types listed under
            // new_to_the_record never reached the record - the capture that held them was gone by
            // the drain). The sighting goes into seen/index.json at once; the drain adds samples.
            if (fresh.Count > 0) RecordSightings(root, fresh);
            RecordBook.FromNew(root, host.Instance, fresh, repeats, known);
            var answer = new JsonObject
            {
                ["new_to_the_record"] = fresh,
                ["seen_before"] = args.Flag("known") ? repeats : repeats.Count,
                ["gone"] = silent.Count > 0 ? silent : null,
            };
            return Results.Text(answer);
        });

    /// <summary>Writes sightings (rows of GK.Seen.New with their side) into seen/index.json.</summary>
    static void RecordSightings(string root, JsonArray rows)
    {
        using var held = RecordLock.Take(root);
        var indexPath = Path.Combine(root, "seen", "index.json");
        var index = ReadObject(indexPath);
        foreach (var row in rows)
        {
            if (row?["Name"]?.GetValue<string>() is not { } name || row["side"]?.GetValue<string>() is not { } side) continue;
            if (!index.ContainsKey(name)) index[name] = new JsonObject();
            var byName = index[name]!.AsObject();
            if (!byName.ContainsKey(side))
                byName[side] = new JsonObject { ["max"] = 0, ["labels"] = new JsonArray(), ["levels"] = new JsonArray(), ["samples"] = 0 };
            var record = byName[side]!.AsObject();
            var max = Num(row["Max"]);
            if (max > Num(record["max"])) record["max"] = max;
            AddOnce(record["labels"]!.AsArray(), row["Label"]?.GetValue<string>());
        }
        Write(indexPath, Sorted(index));
    }

    static GateKitTool SeenDrain(ToolHost host) => new(
        Define("bg3_seen_drain",
            "Move the queued samples out of the game and into the record: research/coverage/seen/<side>/<type>.json for the raw "
            + "bytes (derived from the game, never committed) and seen/index.json for where each type was observed. Drain when the "
            + "queue has grown, and always before a world reset or a Lua reload, which lose it. It answers with how many samples "
            + "each side gave and which types the record had never held before; a side with no capture running is named under "
            + "not_running, and the other side is still drained - a side that does not answer at all (the server in the main menu, "
            + "which stays in UnloadLevel) is named there too, so the client's main-menu sightings are not lost.",
            """
            {
             "type": "object",
             "properties": {"level": {"type": "string", "description": "The level these samples were taken in; read from the game when omitted."}}
            }
            """),
        async (args, cancel) =>
        {
            var (copy, _) = host.Begin();
            var session = copy.Session();
            var root = Root(host);
            // Several copies of the game are drained into one record. Read, change, write is not
            // one step, so two drains at once would each write back what it read and one of them
            // would be lost - quietly, which for a sighting means a type goes back to "never seen".
            using var held = RecordLock.Take(root);
            var indexPath = Path.Combine(root, "seen", "index.json");
            var index = ReadObject(indexPath);
            var seenBefore = RecordBook.SeenKeys(index);

            var level = args.Str("level");
            if (level is null)
            {
                try
                {
                    level = (await session.LuaAsync("server", "return GK.Stage.CurrentLevel()", TimeSpan.FromSeconds(20), cancel))
                        ?.GetValue<string>();
                }
                catch (Exception) { level = ""; }  // the level name is a courtesy, never a reason to lose samples
            }

            var counts = new JsonObject();
            var firstTime = new JsonArray();
            var stopped = new JsonObject();
            var layoutsByName = LayoutCheck.Load(root).GroupBy(l => l.Component, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var compared = new JsonObject();
            var verified = new JsonObject();
            var disagreements = new List<string>();
            foreach (var side in Sides)
            {
                // A world reset, a Lua reload or a level change ends the capture in that Lua state;
                // what is not running has nothing queued, and says so instead of failing.
                var (status, gone) = await AskSide(session, side, "return GK.Seen.Status()", TimeSpan.FromSeconds(30), cancel);
                if (gone is not null)
                {
                    stopped[side] = "not drained: " + gone;
                    counts[side] = 0;
                    continue;
                }
                if (SeenCapture.NotRunning(status, side) is { } why)
                {
                    stopped[side] = why;
                    counts[side] = 0;
                    continue;
                }
                var added = new Dictionary<string, List<JsonObject>>(StringComparer.Ordinal);
                while (true)
                {
                    var got = await session.LuaAsync(side, "return GK.Seen.Drain(120)", TimeSpan.FromSeconds(120), cancel);
                    foreach (var row in Rows(got.Field("Rows")))
                    {
                        if (row is not JsonObject sample || sample["Name"]?.GetValue<string>() is not { } name) continue;
                        if (!added.TryGetValue(name, out var list)) added[name] = list = [];
                        list.Add(sample);
                    }
                    if ((Num(got.Field("Left"))) <= 0) break;
                }

                // Each sample of a type with a layout was also read through Script Extender in the
                // tick it was taken: the two readings are compared here, and the types that agree
                // are recorded as verified (D14 of the day run - a one-frame type has no other way in).
                var agreed = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var (name, rows) in added)
                {
                    if (!layoutsByName.TryGetValue(name, out var layout)) continue;
                    var (fine, problem) = LayoutCheck.Compare(layout, rows);
                    if (problem is not null) { if (disagreements.Count < 20) disagreements.Add($"{side}: {problem}"); }
                    else if (fine > 0) agreed[name] = fine;
                }
                compared[side] = agreed.Count;
                verified[side] = RecordHeld(root, side, agreed, "bg3_seen_drain",
                    "mapped fields equal raw memory of the same instance, read in the tick of capture");

                foreach (var (name, rows) in added)
                {
                    var path = Path.Combine(root, "seen", side, Safe(name) + ".json");
                    var kept = File.Exists(path)
                        ? JsonNode.Parse(File.ReadAllText(path))!.AsObject()
                        : new JsonObject { ["component"] = name, ["side"] = side, ["size"] = rows[0]["Size"]?.DeepClone(), ["samples"] = new JsonArray() };
                    var samples = kept["samples"]!.AsArray();
                    var have = samples.Select(s => s?["hex"]?.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
                    foreach (var row in rows)
                    {
                        var hex = row["Hex"]?.GetValue<string>();
                        if (hex is null || have.Contains(hex) || samples.Count >= 48) continue;
                        have.Add(hex);
                        samples.Add(new JsonObject
                        {
                            ["hex"] = hex,
                            ["entity"] = row["Entity"]?.DeepClone(),
                            ["label"] = row["Label"]?.DeepClone(),
                            // a sample spilled before a load carries the level it was taken in
                            ["level"] = row["Level"] is JsonValue taken && taken.TryGetValue<string>(out var at) && at.Length > 0 ? at : level ?? "",
                        });
                    }
                    Write(path, kept);
                }

                var types = await session.LuaAsync(side, "return GK.Seen.Types()", TimeSpan.FromSeconds(120), cancel);
                foreach (var (name, seen) in types.AsFields() ?? [])
                {
                    if (seen is not JsonObject entry) continue;
                    if (!index.ContainsKey(name))
                    {
                        index[name] = new JsonObject();
                        firstTime.Add(name);
                    }
                    var byName = index[name]!.AsObject();
                    if (!byName.ContainsKey(side))
                        byName[side] = new JsonObject { ["max"] = 0, ["labels"] = new JsonArray(), ["levels"] = new JsonArray(), ["samples"] = 0 };
                    var record = byName[side]!.AsObject();
                    var max = Num(entry["Max"]);
                    if (max > (Num(record["max"]))) record["max"] = max;
                    AddOnce(record["labels"]!.AsArray(), entry["Label"]?.GetValue<string>());
                    AddOnce(record["levels"]!.AsArray(), level);
                    // The raw samples are not committed, so a checkout that never drained this type
                    // holds fewer of them than the record counts: a count never goes down (a drain
                    // from a fresh worktree took "samples": 10 to 1).
                    if (added.ContainsKey(name))
                        record["samples"] = Math.Max((int)Num(record["samples"]),
                            ReadObject(Path.Combine(root, "seen", side, Safe(name) + ".json"))["samples"]?.AsArray().Count ?? 0);
                }
                RecordBook.FromTypes(root, host.Instance, side, types, seenBefore, level);
                counts[side] = added.Sum(pair => pair.Value.Count);
            }

            Write(indexPath, Sorted(index));
            return Results.Text(new JsonObject
            {
                ["samples"] = counts,
                ["level"] = level ?? "",
                ["types_new_to_the_record"] = firstTime,
                ["record"] = index.Count,
                ["agreed_in_the_tick_of_capture"] = compared,
                ["recorded_as_verified"] = verified,
                ["disagreements"] = disagreements.Count > 0 ? new JsonArray([.. disagreements.Select(d => (JsonNode)d!)]) : null,
                ["not_running"] = stopped.Count > 0 ? stopped : null,
            });
        });

    static GateKitTool SeenStatus(ToolHost host) => new(
        Define("bg3_seen_status",
            "Whether the capture runs, how many ticks it has watched, what label it is under, how many types it has met and how "
            + "many samples wait to be drained. It takes nothing out of the queue. If it says the capture is not running, the Lua "
            + "state was reset under it - start it again.", Empty),
        async (_, cancel) =>
        {
            var (copy, _) = host.Begin();
            var answers = new JsonObject();
            var session = copy.Session();
            foreach (var side in Sides)
            {
                var (answer, gone) = await AskSide(session, side, "return GK.Seen.Status()", TimeSpan.FromSeconds(30), cancel);
                answers[side] = gone is null ? answer : new JsonObject { ["gone"] = gone };
            }
            return Results.Text(answers);
        });

    // ---------------------------------------------------------------------------------------
    // The ledger

    static GateKitTool Todo() => new(
        Define("bg3_coverage_todo",
            "The ledger: what GateKit knows about each of the engine's component types, and what has never been seen. THIS, not "
            + "what looks interesting, is what decides where a run goes next. It reads the committed record only - no game needed.\n"
            + "Groups:\n"
            + "  tally     the counts (the default)\n"
            + "  unseen    mapped, with a layout taken from the code, but NO live instance has ever been observed. The layout is a "
            + "reading of the code and nothing has tested it. This is the frontier: find a situation that makes one appear.\n"
            + "  unverified  seen alive, but its fields have never been checked against raw memory (bg3_probe_fields does that)\n"
            + "  waiting   a layout hypothesis not confirmed yet\n"
            + "  alive     seen alive with no layout at all - work for a hypothesis (bg3_probe)\n"
            + "  dead      neither mapped nor ever seen\n"
            + "Narrow with prefix (a namespace, e.g. esv::lock or eoc::character_creation): a namespace is usually one system, so "
            + "a run that takes one namespace at a time has a theme and an end.",
            """
            {
             "type": "object",
             "properties": {
              "group": {"type": "string", "enum": ["tally", "unseen", "unverified", "waiting", "alive", "dead"], "description": "Default tally."},
              "prefix": {"type": "string", "description": "Only types whose name starts with this, e.g. \"esv::lock\"."},
              "limit": {"type": "integer", "description": "Most names to list (default 80)."},
              "namespaces": {"type": "boolean", "description": "Instead of names, the count per namespace - how a run picks its theme."}
             }
            }
            """),
        (args, _) =>
        {
            var ledger = Ledger.Load(Root());
            var group = args.Str("group") ?? "tally";
            if (group == "tally") return Task.FromResult(Results.Text(ledger.Tally()));
            var prefix = args.Str("prefix") ?? "";
            var names = ledger.Group(group, prefix);
            if (args.Flag("namespaces"))
            {
                var spaces = new JsonObject();
                foreach (var pair in names.GroupBy(Ledger.Namespace).OrderByDescending(g => g.Count()))
                    spaces[pair.Key] = pair.Count();
                return Task.FromResult(Results.Text(new JsonObject { ["group"] = group, ["types"] = names.Count, ["by_namespace"] = spaces }));
            }
            var limit = args.Int("limit") ?? 80;
            var listed = new JsonArray();
            foreach (var name in names.Take(limit)) listed.Add(ledger.Describe(name));
            return Task.FromResult(Results.Text(new JsonObject
            {
                ["group"] = group,
                ["prefix"] = prefix,
                ["types"] = names.Count,
                ["shown"] = listed.Count,
                ["list"] = listed,
            }));
        });

    // ---------------------------------------------------------------------------------------
    // Closing in on one component

    static GateKitTool Probe(ToolHost host) => new(
        Define("bg3_probe",
            "Look at every live instance of one component and say what each part of it holds - the starting point for a layout, "
            + "and usable on any type whether Script Extender maps it or not. For every 8-byte slot: what the values look like "
            + "across the instances (a live entity handle, a pointer into the executable with its offset, a heap pointer, a plain "
            + "value). For every 4-byte half: how many distinct values, their range, whether they all read as ordinary floats, and "
            + "the bytes themselves. A field that is the same in every instance is a constant or a default; one that differs with "
            + "the situation is what you can force a value into and prove. An offset shown as `exe+0x...` is in the executable "
            + "this copy runs - a copy starts bg3_dx11.exe - so it is named with `gatekit symbols signature "
            + "bg3_dx11.exe+0x...`; the same number asked of bg3.exe names a different function. Read-only: nothing in the "
            + "game is changed.",
            """
            {
             "type": "object",
             "required": ["side", "component"],
             "properties": {
              "side": {"type": "string", "enum": ["client", "server"], "description": "Which side's entities. A type's namespace says: ecl:: is the client, esv:: the server, eoc:: and ls:: live on both."},
              "component": {"type": "string", "description": "The engine's own name, e.g. esv::lock::LockComponent."},
              "max": {"type": "integer", "description": "Most instances to read (default 16). More instances make a constant field more convincing."},
              "raw": {"type": "boolean", "description": "Also return every instance's bytes as hex."}
             }
            }
            """),
        async (args, cancel) =>
        {
            var (copy, _) = host.Begin();
            var component = args.Need("component");
            var dumped = await copy.Session().LuaAsync(args.Need("side"),
                $"return GK.Probe.Dump({Lua.Value(component)}, {args.Int("max") ?? 16})", TimeSpan.FromSeconds(120), cancel);
            if (dumped is JsonObject refused && refused["Error"] is { } why) return Results.Text($"{component}: {why}");
            return Results.Text(Survey.Render(dumped, component, args.Flag("raw")));
        });

    static GateKitTool ProbeView(ToolHost host) => new(
        Define("bg3_probe_view",
            "Read live instances of a component THROUGH its layout hypothesis, with no Script Extender declaration and no "
            + "rebuild: the file in research/coverage/layouts says what sits where, and this applies it to the bytes. A "
            + "component the extender has never heard of comes back as `Timer = 5.0, TimerStarted = true, field_8 = 0` "
            + "instead of hex. Every field also carries its own bytes, because reading a field under the wrong type is the "
            + "likeliest way to be wrong and the hex is what shows it.\n"
            + "This is the fast half of the loop: name a field, look again, was I right. Edit the layout file, "
            + "`gatekit bridge-reload` (about 1.4 s), look. Declarations in the fork are then written once for a batch of "
            + "findings already known to be right, and one restart validates many. Read-only.",
            """
            {
             "type": "object",
             "required": ["side", "component"],
             "properties": {
              "side": {"type": "string", "enum": ["client", "server"], "description": "Which side's entities. ecl:: is the client, esv:: the server, eoc:: and ls:: live on both."},
              "component": {"type": "string", "description": "The engine's own name, e.g. esv::lock::LockComponent. Its layout file is found by this name, whatever its status - a hypothesis is the point."},
              "max": {"type": "integer", "description": "Most instances to read (default 4)."}
             }
            }
            """),
        async (args, cancel) =>
        {
            var (copy, _) = host.Begin();
            var component = args.Need("component");
            var layout = LayoutCheck.Find(Root(), component);
            if (layout is null)
                return Results.Text($"no layout file for {component}. `bg3_probe` is where a hypothesis starts; "
                    + "write it to research/coverage/layouts and this reads the game through it.");
            var read = await copy.Session().LuaAsync(args.Need("side"),
                $"return GK.Probe.View({Lua.Value(component)}, {Lua.Fields(layout.Fields)}, {args.Int("max") ?? 4})",
                TimeSpan.FromSeconds(120), cancel);
            if (read is JsonObject refused && refused["Error"] is { } why) return Results.Text($"{component}: {why}");
            return Results.Text(new JsonObject
            {
                ["component"] = component,
                ["layout"] = layout.File,
                // Said plainly: this is a hypothesis being read, not a verified type, unless the
                // file says otherwise. bg3_verify is what turns one into the other.
                ["status"] = layout.Status,
                ["read"] = read?.DeepClone(),
            });
        });

    static GateKitTool ProbeFields(ToolHost host) => new(
        Define("bg3_probe_fields",
            "The raw bytes of a live instance beside what Script Extender returns for the named fields of that SAME instance, read "
            + "in the same call. This is how a wrong layout is caught - ours or Script Extender's own author's - because the reader "
            + "sees with their own eyes whether a field's value is where the layout says it is. A suspicion found by reading the "
            + "code is not acted on until a run of this agrees. It changes nothing in the game or in the fork.",
            """
            {
             "type": "object",
             "required": ["side", "component", "ext_name"],
             "properties": {
              "side": {"type": "string", "enum": ["client", "server"]},
              "component": {"type": "string", "description": "The engine's own name, e.g. eoc::combat::ParticipantComponent."},
              "ext_name": {"type": "string", "description": "The name Script Extender gives it on an entity, e.g. CombatParticipant."},
              "fields": {"type": "array", "items": {"type": "string"}, "description": "The fields to read. Omitted: the component is only reported as present or absent."},
              "max": {"type": "integer", "description": "Most instances (default 3)."}
             }
            }
            """),
        async (args, cancel) =>
        {
            var (copy, _) = host.Begin();
            var fields = args.Strings("fields") ?? [];
            var lua = $"return GK.Probe.Mapped({Lua.Value(args.Need("component"))}, {Lua.Value(args.Need("ext_name"))}, "
                + $"{Lua.List(fields)}, {args.Int("max") ?? 3})";
            var rows = await copy.Session().LuaAsync(args.Need("side"), lua, TimeSpan.FromSeconds(60), cancel);
            // The refusal comes back as an object with Error; the rows themselves come back as a list.
            if (rows is JsonObject refused && refused["Error"] is { } why) return Results.Text(why.ToString());
            return Results.Text(Survey.RenderFields(rows));
        });

    // ---------------------------------------------------------------------------------------
    // The record

    static GateKitTool Recipe() => new(
        Define("bg3_recipe",
            "Write down a confirmed finding and the shortest sequence that makes the component appear again. Without a recipe a "
            + "finding is an anecdote however true it is: the recipe is what lets the next person check you, what becomes a "
            + "regression test when the game is patched, and what makes a claim worth sending to Script Extender's author.\n"
            + "The recipe is a TEST that `gatekit --instance <copy> test tests/_coverage -k <name>` replays: it starts in "
            + "`level`, watches `component` on its side from before the first step, runs `steps` in order, and FAILS unless the "
            + "component came alive. So give the steps as you did them, from a fresh game in that level: each is one of "
            + "{\"side\": \"server\", \"lua\": \"...\"} (a chunk run on client, server or ui; an error fails the replay), "
            + "{\"key\": \"Escape\"} (a key, as bg3_key names it), {\"side\": \"client\", \"until\": \"<Lua expression>\", "
            + "\"timeout\": 30} (wait until it is true) or {\"wait\": 2} (let seconds pass); any of them may carry `wait` (a pause "
            + "after it) and `note` (a comment). Each chunk is its own call: a `local` does not reach the next step - look things "
            + "up again or keep them in a global.\n"
            + "Write only what is true in `evidence` and `reached_by`. A sighting is NOT a finding: a claim needs the action repeated, or a value "
            + "forced, or the code read, and the record says which. A component seen in ordinary play is a stronger fact than one "
            + "that only appears after a cheat, and both are worth having. A field you could not work out stays unresolved here - a "
            + "confident wrong name travels into other people's mods and is worse than a missing one.\n"
            + "It writes tests/_coverage/<name>.lua in the checkout this server runs from and appends to "
            + "research/coverage/findings.jsonl. Replay it before you call it done.",
            """
            {
             "type": "object",
             "required": ["component", "name", "level", "steps", "evidence", "reached_by"],
             "properties": {
              "component": {"type": "string", "description": "The engine's own name of the type the recipe makes appear."},
              "name": {"type": "string", "description": "A short file name for the recipe, e.g. lock-picked."},
              "level": {"type": "string", "description": "MODULE:LEVEL the replay starts in, as bg3_up takes it (e.g. GustavX:WLD_Main_A), or \"main menu\" for a recipe of the menus (bg3_up main_menu)."},
              "side": {"type": "string", "enum": ["client", "server"], "description": "Where the component lives. Read off an esv:: (server) or ecl:: (client) name when left out."},
              "steps": {
               "type": "array",
               "description": "What makes it appear, in order, from a fresh game in `level`. Each step is one of lua (with side), key, until (with side), or only wait.",
               "items": {
                "type": "object",
                "properties": {
                 "side": {"type": "string", "enum": ["client", "server", "ui"], "description": "Where `lua` runs or `until` is read."},
                 "lua": {"type": "string", "description": "A chunk run on `side`."},
                 "key": {"type": "string", "description": "A key pressed in the game (bg3_key's names)."},
                 "hold": {"type": "number", "description": "With key: seconds the key stays down (0 to 30), as bg3_key's hold - moving or turning the camera needs it."},
                 "until": {"type": "string", "description": "A Lua expression read on `side` until it is true."},
                 "timeout": {"type": "number", "description": "Seconds `lua` or `until` may take (until: default 30)."},
                 "wait": {"type": "number", "description": "Seconds to let pass after the step; alone, just a pause."},
                 "note": {"type": "string", "description": "A comment above the step: why it is there."}
                }
               }
              },
              "within": {"type": "number", "description": "Seconds after the last step the component may take to appear (default 10)."},
              "claim": {"type": "string", "description": "What you now believe, in one or two sentences. Say which fields you could NOT work out."},
              "evidence": {"type": "string", "enum": ["sighting", "repeated", "replayed", "field_value"], "description": "What actually backs the claim: sighting (seen once - recorded as a lead, not a finding), repeated (made to appear again in a fresh capture), replayed (this recipe was replayed and passed), field_value (a value was forced or read and the field moved as claimed). Anything more goes in `note`."},
              "reached_by": {"type": "string", "enum": ["play", "cheat", "play_with_cheat_setup"], "description": "play: ordinary play produced it. cheat: its cause is staged (an Osi call, a GK.Stage or GK.Cheat call, a request queued the way no player can). play_with_cheat_setup: a staged setup (an item brought, a level given), then the player's own action caused it."},
              "note": {"type": "string", "description": "Free text about the evidence or how it was reached: what the enums cannot say."},
              "may_exist": {"type": "boolean", "description": "The type may already be held before the steps (a persistent component). Without it the replay FAILS when something holds the type before the steps, since appearing then proves nothing; with it the replay needs the holder count to rise or a new holder entity."},
              "fields": {"type": "object", "description": "Offset -> what you believe it is. Leave out what you do not know; do not guess."},
              "open": {"type": "string", "description": "What is still unresolved about this type."}
             }
            }
            """),
        (args, _) =>
        {
            var root = Root();
            var repository = Path.GetDirectoryName(Path.GetDirectoryName(root))!;
            var component = args.Need("component");
            var safe = Safe(args.Need("name")).Trim('_');
            CheckComponent(root, component, args.Need("name"));
            var evidence = args.Need("evidence");
            var reachedBy = args.Need("reached_by");
            var mayExist = args.Flag("may_exist");
            var level = args.Need("level");
            var side = RecipeTest.ComponentSide(component, args.Str("side"));
            var steps = RecipeTest.Steps(args.Node("steps") ?? throw new MissingArgumentException("steps"));
            var path = Path.Combine(repository, "tests", "_coverage", safe + ".lua");

            var header = new StringBuilder()
                .Append("-- ").Append(component).Append(": the shortest way to make it appear again.\n")
                .Append("-- Claim: ").Append((args.Str("claim") ?? "(none stated)").ReplaceLineEndings("\n-- ")).Append('\n')
                .Append("-- Backed by: ").Append(evidence).Append(", reached by ").Append(reachedBy)
                .Append(", in ").Append(level).Append('\n');
            if (args.Str("open") is { Length: > 0 } open)
                header.Append("-- Still unresolved: ").Append(open.ReplaceLineEndings("\n-- ")).Append('\n');
            header.Append("-- Recorded ").Append(DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .Append(". Replay: gatekit --instance <copy> test tests/_coverage -k ").Append(safe).Append("\n\n");

            var test = RecipeTest.Write(header.ToString(), level, component, side, steps, args.Num("within") ?? 10, mayExist);
            var entry = new JsonObject
            {
                ["date"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                ["component"] = component,
                ["recipe"] = "tests/_coverage/" + safe + ".lua",
                ["claim"] = args.Str("claim"),
                ["evidence"] = evidence,
                ["reached_by"] = reachedBy,
                ["level"] = level,
                ["fields"] = args.Node("fields"),
                ["open"] = args.Str("open"),
                ["note"] = args.Str("note"),
                ["kind"] = evidence == "sighting" ? "lead" : "finding",
            };
            GateKit.Copies.Record.Validate("findings", entry);  // a value outside the vocabulary writes nothing
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, test);
            GateKit.Copies.Record.Append(root, "findings", entry);
            return Task.FromResult(Results.Text(new JsonObject
            {
                ["written"] = "tests/_coverage/" + safe + ".lua",
                ["path"] = Path.GetFullPath(path),
                ["recorded_in"] = Path.GetFullPath(Path.Combine(root, "findings.jsonl")),
                ["replay"] = $"gatekit --instance <copy> test tests/_coverage -k {safe}",
                ["recorded_as"] = entry["kind"]!.DeepClone(),
                ["note"] = evidence == "sighting"
                    ? "Recorded as a LEAD, not a finding: one appearance is not evidence. Repeat the action, or force a value, or read the code, then record it again."
                    : "Recorded as a finding. Replay it (the line above) on a clean game before the run ends: it fails unless the component comes alive.",
            }));
        });

    /// <summary>A recipe whose component is not a type replays as a failure forever: seven of the
    /// third run's sixteen were written with the bare namespace <c>esv</c> (a shell slip upstream of
    /// the call), every one failed its replay, and the record said "makes esv live". So the name is
    /// held to the engine's component types - those of coverage.json when the checkout has it - and a
    /// refusal names what was probably meant.</summary>
    public static void CheckComponent(string root, string component, string name)
    {
        var shaped = component.Contains("::", StringComparison.Ordinal) && !component.EndsWith("::", StringComparison.Ordinal)
            && !component.EndsWith("System", StringComparison.Ordinal);
        var index = Path.Combine(root, "coverage.json");
        var ledger = File.Exists(index) ? Ledger.Load(root) : null;
        if (shaped && (ledger is null || ledger.Knows(component))) return;
        var near = ledger?.Near(component, [name]) ?? [];
        throw new ArgumentException($"`component` must be a component type, the engine's full name (e.g. esv::lock::LockComponent); "
            + $"{component} is " + (shaped ? "not one the coverage index knows" : "not a type name") + ". Nothing was written."
            + (near.Count > 0 ? " Did you mean: " + string.Join(", ", near) + "?" : ""));
    }

    static GateKitTool RunReport() => new(
        Define("bg3_run_report",
            "The hand-over a run writes when it ends - what the next agent reads instead of your transcript. End a run when your "
            + "context is filling, when the direction has gone dead, or when you have more confirmed findings than you can carry; "
            + "ending IS writing this. It is appended to research/coverage/runs.jsonl and the text is written beside it, so a crash "
            + "cannot take it with it.",
            """
            {
             "type": "object",
             "required": ["half", "found", "open", "wishes"],
             "properties": {
              "run": {"type": "string", "description": "A short name for what you did, kept as `name`. The run, role and stretch themselves come from bg3_seen_start's arguments, else GATEKIT_RUN / GATEKIT_ROLE / GATEKIT_STRETCH."},
              "half": {"type": "string", "enum": ["world", "interface"], "description": "Which half you worked: inside the game, or the menus around it."},
              "aim": {"type": "string", "description": "Wide (many situations, to light up the unseen) or narrow (one system from every angle) - and why you chose it."},
              "found": {"type": "string", "description": "What was found, with its evidence and the recipes written. Say plainly what is a lead and what is a finding."},
              "open": {"type": "string", "description": "What is still open, and where you would go next."},
              "wishes": {"type": "string", "description": "Every place the API lied, hid something or made you guess: what you did, what happened, what you wanted instead. Not complaints - these become general fixes."},
              "cost": {"type": "string", "description": "What the run spent: tool calls, wall clock, and anything that cost far more than it should have."},
              "api_version": {"type": "string", "description": "The bridge version the findings were made under, if you were told it."}
             }
            }
            """),
        (args, _) =>
        {
            var root = Root();
            var entry = new JsonObject
            {
                ["ended"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                ["run"] = RecordBook.Context.Who().Run,
                ["stretch"] = RecordBook.Context.Who().Stretch,
                ["role"] = RecordBook.Context.Who().Role,
                ["name"] = args.Str("run"),
                ["half"] = args.Need("half"),
                ["aim"] = args.Str("aim"),
                ["found"] = args.Need("found"),
                ["open"] = args.Need("open"),
                ["wishes"] = args.Need("wishes"),
                ["cost"] = args.Str("cost"),
                ["api_version"] = args.Str("api_version"),
            };
            Directory.CreateDirectory(root);
            File.AppendAllText(Path.Combine(root, "runs.jsonl"), entry.ToJsonString() + "\n");
            return Task.FromResult(Results.Text(new JsonObject
            {
                ["recorded"] = "research/coverage/runs.jsonl",
                ["note"] = "The hand-over is stored. Drain the capture (bg3_seen_drain) and close the game copy you started before you stop.",
            }));
        });

    // ---------------------------------------------------------------------------------------

    /// <summary>Rows from Lua: a list, or a table keyed otherwise (its values are the rows).</summary>
    static IEnumerable<JsonNode?> Rows(JsonNode? value) => value switch
    {
        JsonArray array => array,
        JsonObject one => one.Select(pair => pair.Value),
        _ => [],
    };

    static void AddOnce(JsonArray array, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        if (array.Any(item => item?.GetValue<string>() == value)) return;
        array.Add(value);
    }

    static JsonObject Sorted(JsonObject value)
    {
        var sorted = new JsonObject();
        foreach (var pair in value.OrderBy(p => p.Key, StringComparer.Ordinal)) sorted[pair.Key] = pair.Value?.DeepClone();
        return sorted;
    }

    /// <summary>
    /// A JSON number whatever shape it arrived in. Asking a node for a double when the game sent
    /// an integer throws rather than converting, which took `bg3_seen_drain` down mid-run; one
    /// reader for every number in these tools is the fix, not a cast at each call site.
    /// </summary>
    static double Num(JsonNode? node) => LayoutCheck.Real(node) ?? 0;

    /// <summary>A component name as a file name, exactly as the Python library wrote it.</summary>
    static string Safe(string name)
    {
        var text = new StringBuilder();
        var underscore = false;
        foreach (var c in name)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '_' or '.') { text.Append(c); underscore = false; }
            else if (!underscore) { text.Append('_'); underscore = true; }
        }
        return text.ToString();
    }
}

/// <summary>Values written into a Lua chunk. Only what these tools send: strings, numbers, lists.</summary>
static class Lua
{
    public static string Value(string? text) => text is null ? "nil" : JsonSerializer.Serialize(text);

    public static string List(IReadOnlyList<string> items) => "{" + string.Join(", ", items.Select(Value)) + "}";

    /// <summary>A layout's fields as the table GK.Probe.View reads: { {Name=, Offset=, Type=}, ... }.</summary>
    public static string Fields(IEnumerable<LayoutCheck.Field> fields) => "{"
        + string.Join(", ", fields.Select(f =>
            $"{{Name={Value(f.Name)}, Offset={f.Offset.ToString(CultureInfo.InvariantCulture)}, Type={Value(f.Type)}}}"))
        + "}";
}

/// <summary>What GK.Seen.Status() says about a side's standing capture.</summary>
public static class SeenCapture
{
    /// <summary>Why <paramref name="side"/> has nothing to drain (null = its capture runs).</summary>
    public static string? NotRunning(JsonNode? status, string side) =>
        status is JsonObject running && running["Running"] is JsonValue on && on.TryGetValue<bool>(out var isOn) && isOn
            ? null
            : $"no capture is running on {side} - bg3_reset, a Lua reload or a level change stops it; call bg3_seen_start";
}
