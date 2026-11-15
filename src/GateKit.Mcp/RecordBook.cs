using System.Globalization;
using System.Text.Json.Nodes;
using GateKit.Names;
using GateKit.Testing;
using GateKit.Copies;

namespace GateKit.Mcp;

/// <summary>
/// The queries and writers of the coverage ledgers (<see cref="Record"/>): the sightings rows the
/// capture tools add, the wishes, and the three questions asked of the whole record - the counts
/// (<c>tally</c>), whether it holds together (<c>check</c>) and what a run produced (<c>yield</c>).
/// docs/contracts/cli.md 8.11 and docs/contracts/mcp.md 3.9.
/// </summary>
public static class RecordBook
{
    /// <summary>The version of what <see cref="Tally"/> counts; a row of counts.jsonl carries it, so a
    /// change of definition is never read as a change of the record. counts-2: `fields_named` is the
    /// names with evidence (inferred, toolkit, game), as the headline of `gatekit names tally`; the
    /// extender's unchecked names and names at rung unknown are counted apart, and
    /// `fields_named_by_rung` counts names only (counts-1 summed every name, rung unknown included,
    /// and counted machine names into the rungs).</summary>
    public const string CountsDefinition = "counts-2";

    // ---------------------------------------------------------------------------------------
    // Who is writing: set by bg3_seen_start for the rest of this server's life

    /// <summary>The role, run and stretch bg3_seen_start was given, and the capture it started.</summary>
    public static class Context
    {
        static string? _role, _run, _stretch;

        /// <summary>The capture this server last started: its instance and start time.</summary>
        public static string? Capture { get; private set; }

        public static void Start(string? instance, string? role, string? run, string? stretch)
        {
            if (role is { Length: > 0 }) _role = role;
            if (run is { Length: > 0 }) _run = run;
            if (stretch is { Length: > 0 }) _stretch = stretch;
            // two starts in one second are two captures
            Capture = (instance ?? "installed") + "@" + Record.Now() + "#" + Guid.NewGuid().ToString("N")[..6];
        }

        /// <summary>What was given to bg3_seen_start, else the environment, else "unknown".</summary>
        public static (string Role, string Run, string Stretch) Who() => Record.Who(_role, _run, _stretch);

        /// <summary>For tests: forget what was given.</summary>
        public static void Reset() => (_role, _run, _stretch, Capture) = (null, null, null, null);
    }

    // ---------------------------------------------------------------------------------------
    // Sightings

    /// <summary>
    /// Rows of sightings.jsonl for the types a capture reports on one side (rows with <c>Name</c>,
    /// <c>Label</c>; GK.Seen.New or GK.Seen.Types). A type <paramref name="knownBefore"/> did not know
    /// on that side, and that has no <c>sighted</c> row yet, is <c>sighted</c>. A type already known is
    /// <c>repeated</c> once per run and side, and never in the capture that wrote a row for it already:
    /// "seen again" means in a later capture. Returns the rows written.
    /// </summary>
    public static int Sightings(string root, string? instance, string side, IEnumerable<JsonNode?> rows,
        Func<string, bool> knownBefore, string? level, string source = "tick", bool held = false)
    {
        var (role, run, stretch) = Context.Who();
        var capture = Context.Capture ?? (instance ?? "installed") + "@unstarted";
        using var lockHeld = held ? null : Record.Lock(root);
        var sighted = new HashSet<string>(StringComparer.Ordinal);
        var thisRun = new HashSet<string>(StringComparer.Ordinal);
        var thisCapture = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in Record.Read(root, "sightings"))
        {
            if (Text(row["side"]) != side || Text(row["type"]) is not { } type) continue;
            if (Text(row["state"]) == "sighted") sighted.Add(type);
            if (Text(row["state"]) == "repeated" && Text(row["run"]) == run) thisRun.Add(type);
            if (Text(row["capture"]) == capture) thisCapture.Add(type);
        }
        var written = new List<JsonObject>();
        var time = Record.Now();
        foreach (var row in rows)
        {
            if (row?["Name"] is not JsonValue n || !n.TryGetValue<string>(out var name)) continue;
            string state;
            if (!knownBefore(name) && !sighted.Contains(name)) state = "sighted";
            else if (!thisRun.Contains(name) && !thisCapture.Contains(name)) state = "repeated";
            else continue;
            sighted.Add(name);
            thisCapture.Add(name);
            if (state == "repeated") thisRun.Add(name);
            written.Add(new JsonObject
            {
                ["type"] = name,
                ["side"] = side,
                ["time"] = time,
                ["instance"] = instance ?? "installed",
                ["role"] = role,
                ["run"] = run,
                ["stretch"] = stretch,
                ["level"] = level ?? Text(row["Level"]) ?? "",
                ["label"] = Text(row["Label"]) ?? "",
                ["source"] = source,
                ["state"] = state,
                ["capture"] = capture,
            });
        }
        Record.Append(root, "sightings", written, held: true);
        return written.Count;
    }

    /// <summary>The rows of bg3_seen_new (GK.Seen.New with their side), against the index as it was
    /// before them.</summary>
    public static void FromNew(string root, string? instance, JsonArray fresh, JsonArray repeats, JsonObject knownBefore)
    {
        foreach (var side in new[] { "client", "server" })
        {
            var rows = fresh.Concat(repeats).Where(r => Text(r?["side"]) == side).ToList();
            if (rows.Count > 0) Sightings(root, instance, side, rows, name => knownBefore[name]?[side] is not null, null);
        }
    }

    /// <summary>The types of a drained capture on one side (GK.Seen.Types: name -> {Max, Label}), against
    /// the index as it was before the drain; the drain holds the record's lock.</summary>
    public static void FromTypes(string root, string? instance, string side, JsonNode? types, HashSet<string> seenBefore, string? level)
    {
        var rows = (types as JsonObject ?? []).Select(pair => (JsonNode?)new JsonObject
        {
            ["Name"] = pair.Key,
            ["Label"] = pair.Value?["Label"]?.DeepClone(),
        }).ToList();
        if (rows.Count > 0) Sightings(root, instance, side, rows, name => seenBefore.Contains(side + "|" + name), level, held: true);
    }

    /// <summary>The (type, side) pairs seen/index.json holds, as "side|type".</summary>
    public static HashSet<string> SeenKeys(JsonObject index)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, sides) in index)
            foreach (var (side, _) in sides as JsonObject ?? [])
                keys.Add(side + "|" + name);
        return keys;
    }

    // ---------------------------------------------------------------------------------------
    // Wishes

    /// <summary>A wish as it stands: the last row of its id.</summary>
    public static List<JsonObject> Wishes(string root) =>
        [.. Record.Read(root, "wishes").GroupBy(w => Text(w["id"]) ?? "").Where(g => g.Key.Length > 0)
            .Select(g => g.Last()).OrderBy(w => Text(w["id"]), StringComparer.Ordinal)];

    /// <summary>Adds a wish under the next id (W-0001 and on). Returns the row written.</summary>
    public static JsonObject AddWish(string root, string from, string text, string state = "open", string? note = null,
        string? commit = null, string? was = null) => Record.AddWish(root, from, text, state, note, commit, was);

    /// <summary>A wish's new state: a row appended with the id, what it said, and what changed.</summary>
    public static JsonObject UpdateWish(string root, string id, string state, string? commit = null, string? note = null)
    {
        using var held = Record.Lock(root);
        var had = Record.Read(root, "wishes").LastOrDefault(w => Text(w["id"]) == id)
            ?? throw new ArgumentException($"no wish {id} in the record; `list` shows them");
        var row = new JsonObject
        {
            ["id"] = id,
            ["time"] = Record.Now(),
            ["from"] = had["from"]?.DeepClone(),
            ["text"] = had["text"]?.DeepClone(),
            ["state"] = state,
        };
        if ((commit ?? Text(had["commit"])) is { Length: > 0 } c) row["commit"] = c;
        if (note is { Length: > 0 }) row["note"] = note;
        if (had["was"] is { } was) row["was"] = was.DeepClone();
        Record.Append(root, "wishes", row, held: true);
        return row;
    }

    // ---------------------------------------------------------------------------------------
    // tally

    /// <summary>The counts of the record now, as a row of counts.jsonl.</summary>
    public static JsonObject Tally(string root, string? run = null)
    {
        var ledger = Ledger.Load(root).Tally();
        var types = (int)ledger["types"]!;
        var seen = (int)ledger["seen_alive"]!;
        var row = new JsonObject
        {
            ["time"] = Record.Now(),
            ["run"] = run ?? Record.Who().Run,
            ["definition"] = CountsDefinition,
            ["types"] = types,
            ["seen"] = seen,
            ["never_seen"] = types - seen,
            ["unseen_mapped"] = ledger["groups"]!["unseen"]!.DeepClone(),
            ["verified"] = ledger["verified_live"]!.DeepClone(),
        };

        // The names, counted by the names tally itself over the same layout files.
        var names = GateKit.Names.Tally.Measure(LayoutStore.All(root, _ => { }));
        var byRung = new JsonObject();
        foreach (var (rung, named, _) in names.ByRung)
            if (named > 0) byRung[rung] = named;
        row["fields"] = names.Fields;
        row["fields_named"] = names.NamedWithEvidence;
        row["fields_extender_unchecked"] = names.ExtenderUnchecked;
        row["fields_named_unknown"] = names.NamedUnknown;
        row["fields_named_by_rung"] = byRung;
        row["owed"] = names.Owed;

        // Systems: described (a `does` list), sure (its `sure` says yes), and the claims.
        var folder = Path.Combine(root, "systems");
        int described = 0, sure = 0;
        if (Directory.Exists(folder))
        {
            foreach (var path in Directory.EnumerateFiles(folder, "*.json", SearchOption.AllDirectories))
            {
                JsonObject? file;
                try { file = JsonNode.Parse(File.ReadAllText(path)) as JsonObject; }
                catch (System.Text.Json.JsonException) { continue; }
                if (file?["does"] is JsonArray { Count: > 0 }) described++;
                if (Text(file?["sure"]) is { } said && said.StartsWith("yes", StringComparison.OrdinalIgnoreCase)) sure++;
            }
        }
        var claims = SystemClaims.Tally(folder);
        row["systems_described"] = described;
        row["systems_sure"] = sure;
        row["claims_written"] = claims["claims"]!.DeepClone();
        row["claims_checked"] = claims["checked"]!.DeepClone();
        row["claims_passed"] = claims["passed"]!.DeepClone();
        return row;
    }

    // ---------------------------------------------------------------------------------------
    // check

    /// <summary>Everything in the record that does not hold together; empty when it all does.</summary>
    public static List<string> Check(string root)
    {
        var problems = new List<string>();
        var repository = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(root)))!;
        var recipes = Path.Combine(repository, "tests", "_coverage");

        // Each ledger: every line a JSON object, every row fitting its ledger.
        var rowsOf = new Dictionary<string, List<JsonObject>>(StringComparer.Ordinal);
        foreach (var ledger in Record.Ledgers)
        {
            var file = Path.GetRelativePath(repository, Record.PathOf(root, ledger)).Replace('\\', '/');
            rowsOf[ledger] = Record.Read(root, ledger, (line, _) => problems.Add($"{file}:{line}: not a JSON object"));
            var number = 0;
            foreach (var row in rowsOf[ledger])
            {
                number++;
                foreach (var problem in Record.Problems(ledger, row)) problems.Add($"{file} row {number}: {problem}");
            }
        }

        // findings.jsonl <-> tests/_coverage.
        var named = new HashSet<string>(StringComparer.Ordinal);
        var line = 0;
        foreach (var finding in rowsOf["findings"])
        {
            line++;
            var recipe = Text(finding["recipe"]);
            if (recipe is null)
            {
                if (Text(finding["moved_to"]) is null)
                    problems.Add($"research/coverage/findings.jsonl row {line}: names no recipe (and no `moved_to` where its text went)");
                continue;
            }
            named.Add(Path.GetFileNameWithoutExtension(recipe));
            if (!File.Exists(Path.Combine(repository, recipe)))
                problems.Add($"research/coverage/findings.jsonl row {line}: {recipe} does not exist");
        }
        if (Directory.Exists(recipes))
        {
            foreach (var test in GameTest.Discover(recipes, null, TextWriter.Null))
                if (!named.Contains(test.Name))
                    problems.Add($"tests/_coverage/{test.Name}.lua: replayable, and no line of findings.jsonl names it");
        }

        // Systems: every check.when names a recipe that exists.
        var systems = Path.Combine(root, "systems");
        if (Directory.Exists(systems))
        {
            foreach (var path in Directory.EnumerateFiles(systems, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                JsonObject? file;
                try { file = JsonNode.Parse(File.ReadAllText(path)) as JsonObject; }
                catch (System.Text.Json.JsonException) { problems.Add($"{Path.GetRelativePath(repository, path).Replace('\\', '/')}: not JSON"); continue; }
                var claim = 0;
                foreach (var item in file?["does"] as JsonArray ?? [])
                {
                    claim++;
                    // a check is an object with `when`, or the text "not checkable live: why"
                    if (item is not JsonObject one || one["check"] is not JsonObject check
                        || check["when"] is not JsonValue w || !w.TryGetValue<string>(out var when)) continue;
                    var lua = when.EndsWith(".lua", StringComparison.Ordinal) ? when : when + ".lua";
                    if (!File.Exists(Path.Combine(recipes, lua)))
                        problems.Add($"{Path.GetRelativePath(repository, path).Replace('\\', '/')} claim {claim}: check.when {when} is no recipe in tests/_coverage");
                }
            }
        }

        // Sightings name the engine's types.
        if (rowsOf["sightings"].Count > 0 && File.Exists(Path.Combine(root, "coverage.json")))
        {
            var types = Ledger.Load(root);
            var row = 0;
            foreach (var sighting in rowsOf["sightings"])
            {
                row++;
                if (Text(sighting["type"]) is { } type && !types.Knows(type))
                    problems.Add($"research/coverage/record/sightings.jsonl row {row}: {type} is not a registered type");
            }
        }
        return problems;
    }

    // ---------------------------------------------------------------------------------------
    // yield

    /// <summary>What one run produced: types first seen, by instance, role and hour; and how many of
    /// its replays passed.</summary>
    public static JsonObject Yield(string root, string run)
    {
        var sightings = Record.Read(root, "sightings").Where(r => Text(r["run"]) == run).ToList();
        var first = sightings.Where(r => Text(r["state"]) == "sighted").ToList();
        var byInstance = new JsonObject();
        foreach (var group in first.GroupBy(r => Text(r["instance"]) ?? "?").OrderBy(g => g.Key, StringComparer.Ordinal))
            byInstance[group.Key] = group.Count();
        var byRole = new JsonObject();
        foreach (var group in first.GroupBy(r => Text(r["role"]) ?? "?").OrderBy(g => g.Key, StringComparer.Ordinal))
            byRole[group.Key] = group.Count();
        var byHour = new JsonObject();
        foreach (var group in first.GroupBy(r => Hour(Text(r["time"]))).OrderBy(g => g.Key, StringComparer.Ordinal))
            byHour[group.Key] = group.Count();
        var replays = Record.Read(root, "replays").Where(r => Text(r["run"]) == run).ToList();
        var passed = replays.Count(r => r["ok"] is JsonValue ok && ok.TryGetValue<bool>(out var yes) && yes);
        return new JsonObject
        {
            ["run"] = run,
            ["first_seen"] = first.Count,
            ["repeated"] = sightings.Count(r => Text(r["state"]) == "repeated"),
            ["first_seen_by_instance"] = byInstance,
            ["first_seen_by_role"] = byRole,
            ["first_seen_by_hour"] = byHour,
            ["replays"] = replays.Count,
            ["replays_passed"] = passed,
            ["replays_pass_share"] = replays.Count == 0 ? null : Math.Round((double)passed / replays.Count, 3),
        };
    }

    static string Hour(string? time) => time is { Length: >= 13 } ? time[..13] + ":00Z" : "?";

    static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
