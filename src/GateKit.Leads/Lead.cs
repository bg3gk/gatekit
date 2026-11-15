using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GateKit.Copies;

namespace GateKit.Leads;

/// <summary>
/// The rules of a lead's score and state, and the file the leads live in
/// (research/coverage/leads.jsonl, one row per line, sorted by type), read and written under the
/// coverage record's lock.
/// </summary>
public static class Lead
{
    /// <summary>Fields of a row that a person or a run owns and a rebuild keeps as they are.</summary>
    public static readonly string[] Kept = ["claimed", "note", "hand"];

    static readonly JsonSerializerOptions Line = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string PathOf(string coverageRoot) => Path.Combine(coverageRoot, "leads.jsonl");

    static string S(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    public static List<Attempt> Attempts(JsonObject row) => [.. (row["attempts"] as JsonArray ?? []).OfType<JsonObject>().Select(Attempt.From)];

    /// <summary>A freshly built row joined with what the file held for the type: tries the runs
    /// reported (the imported ones are read again), the claim, the note and every field named in
    /// <c>hand</c> stay; score and state are worked out again.</summary>
    public static JsonObject Merge(JsonObject built, JsonObject? had, bool seen)
    {
        var row = built;
        if (had is not null)
        {
            var reported = Attempts(had).Where(a => !a.Source.StartsWith("import", StringComparison.Ordinal));
            row["attempts"] = new JsonArray([.. Attempts(built).Concat(reported).OrderBy(a => a.Time, StringComparer.Ordinal).Select(a => (JsonNode)a.ToJson())]);
            foreach (var key in Kept)
                if (had[key] is { } value) row[key] = value.DeepClone();
            foreach (var key in (had["hand"] as JsonArray ?? []).Select(S).Where(k => k.Length > 0))
                if (had[key] is { } value) row[key] = value.DeepClone();
        }
        Refresh(row, seen);
        return row;
    }

    /// <summary>Score and state again from the row's own fields.</summary>
    public static void Refresh(JsonObject row, bool seen)
    {
        var attempts = Attempts(row);
        // A failure with the lead's own instrument counts in full; one with another instrument a third.
        var own = S(row["instrument"]);
        int failed = attempts.Count(a => a.Outcome is "empty" or "blocked" && a.Instrument == own) * 3
            + attempts.Count(a => a.Outcome is "empty" or "blocked" && a.Instrument != own);
        row["score"] = Score(S(row["reachable"]), S(row["entry"]?["kind"]), S(row["entry"]?["basis"]), row["entry"]?["guard"] is JsonArray { Count: > 0 },
            row["data"] as JsonArray, row["wiki"] is not null, failed, S(row["class"]));
        var hand = (row["hand"] as JsonArray ?? []).Select(S).ToHashSet(StringComparer.Ordinal);
        string state;
        var parked = Parked(attempts, S(row["instrument"]));
        if (seen || attempts.Any(a => a.Outcome == "lit")) state = "lit";
        else if (S(row["reachable"]) == "not_in_shipping_game") state = "unreachable";
        else if (parked is not null) state = "parked";
        else if (row["claimed"] is JsonObject claim && string.CompareOrdinal(S(claim["time"]), attempts.Select(a => a.Time).DefaultIfEmpty("").Max()) > 0) state = "in_work";
        else state = "open";
        if (hand.Contains("state") && S(row["state"]) is { Length: > 0 } set && !seen) state = set;
        row["state"] = state;
        if (state == "parked" && parked is not null) row["parked"] = parked;
        else row.Remove("parked");
        if (state != "in_work") row.Remove("claimed");
        Order(row);
    }

    /// <summary>Why a lead is parked: two tries with one instrument found nothing, or null.</summary>
    public static string? Parked(IReadOnlyList<Attempt> attempts, string instrument)
    {
        // Only the lead's own instrument parks it: tries with another one lower its score, and a
        // lead whose instrument changed (a new fact) is open again.
        var group = attempts.Where(a => a.Outcome is "empty" or "blocked" && a.Instrument == instrument).GroupBy(a => a.Instrument).Where(g => g.Count() >= 2)
            .OrderBy(g => g.Key, StringComparer.Ordinal).FirstOrDefault();
        return group is null ? null
            : $"{group.Count()} tries with {group.Key} found nothing ({string.Join("; ", group.Select(a => a.Time + " " + a.Outcome).Take(4))}): a new instrument or a fact read from code is needed";
    }

    /// <summary>The score: what a runner can do alone, with a named way in and the game's own content
    /// to do it with, comes first; each failed try takes some away.</summary>
    public static int Score(string reachable, string entryKind, string basis, bool guard, JsonArray? data, bool wiki, int failed, string cls)
    {
        int score = reachable switch
        {
            "solo" => 40, "needs_pad" => 15, "needs_two_copies" => 15, "needs_network" => 5, "unknown" => 10, _ => 0,
        };
        score += entryKind switch
        {
            "osiris" or "message_handler" or "functor" or "status" or "boost" => basis == "queue" ? 20 : 25,
            "system_update" => guard ? 15 : 8,
            "load_save" => 12,
            "replication" => 10,
            _ => 0,
        };
        var where = (data ?? []).OfType<JsonObject>().Select(d => S(d["where"])).ToList();
        if (where.Count > 0) score += where.Any(w => w.Length > 0 && !w.Contains("Dev)", StringComparison.Ordinal)) ? 15 : 5;
        if (wiki) score += 5;
        if (cls == "editor_debug") score -= 10;
        if (cls == "boost" && where.Count == 0) score -= 20;   // no content carries it: a cheat's lead, not a player's
        score -= 5 * failed;   // thirds of a failure: 15 for one with the lead's own instrument
        return Math.Max(0, score);
    }

    static readonly string[] FieldOrder =
        ["type", "side", "one_frame", "class", "reachable", "index_users", "creators", "creators_more", "entry", "data", "wiki", "how", "instrument",
         "score", "state", "parked", "claimed", "attempts", "note", "hand"];

    static void Order(JsonObject row)
    {
        var all = row.ToList();
        row.Clear();
        foreach (var key in FieldOrder)
            if (all.FirstOrDefault(p => p.Key == key) is { Key: not null } p) row[key] = p.Value;
        foreach (var (key, value) in all)
            if (!FieldOrder.Contains(key)) row[key] = value;
    }

    public static List<JsonObject> Read(string coverageRoot)
    {
        var path = PathOf(coverageRoot);
        var rows = new List<JsonObject>();
        if (!File.Exists(path)) return rows;
        int number = 0;
        foreach (var line in File.ReadLines(path))
        {
            number++;
            if (line.Trim().Length == 0) continue;
            try
            {
                if (JsonNode.Parse(line) is JsonObject row) { rows.Add(row); continue; }
            }
            catch (JsonException) { }
            throw new LeadsException($"{path}:{number} is not a lead (a JSON object); fix or remove the line, nothing was changed");
        }
        return rows;
    }

    /// <summary>Writes the whole file, sorted by type, LF endings; the caller holds the record's lock.</summary>
    public static void Write(string coverageRoot, IEnumerable<JsonObject> rows)
    {
        var text = new StringBuilder();
        foreach (var row in rows.OrderBy(r => S(r["type"]), StringComparer.Ordinal))
            text.Append(row.ToJsonString(Line)).Append('\n');
        var path = PathOf(coverageRoot);
        var temp = path + ".tmp";
        File.WriteAllText(temp, text.ToString(), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>The rows with what seen/index.json now says applied: a type seen since is lit.</summary>
    public static List<JsonObject> Current(string coverageRoot)
    {
        var seen = Coverage.Seen(coverageRoot);
        var rows = Read(coverageRoot);
        foreach (var row in rows)
            if (seen.Contains(S(row["type"])) && S(row["state"]) != "lit") Refresh(row, true);
        return rows;
    }

    /// <summary>The best open leads, marked in_work for <paramref name="instance"/>.</summary>
    public static List<JsonObject> Next(string coverageRoot, string instance, string? instrument, int count)
    {
        using var held = Record.Lock(coverageRoot);
        var rows = Current(coverageRoot);
        if (rows.Count == 0) throw new LeadsException("no leads yet: run `gatekit leads build` first");
        var picked = rows.Where(r => S(r["state"]) == "open" && (instrument is null || S(r["instrument"]) == instrument))
            .OrderByDescending(r => (int?)r["score"] ?? 0).ThenBy(r => S(r["type"]), StringComparer.Ordinal).Take(count).ToList();
        foreach (var row in picked)
        {
            row["claimed"] = new JsonObject { ["instance"] = instance, ["time"] = Record.Now(), ["run"] = Record.Who().Run };
            row["state"] = "in_work";
            Order(row);
        }
        Write(coverageRoot, rows);
        return picked;
    }

    /// <summary>A try reported. Two tries with nothing on one instrument park the lead and raise a
    /// wish for what would make it reachable. Returns the row and the wish, if one was raised.</summary>
    public static (JsonObject Row, JsonObject? Wish) Report(string coverageRoot, string type, string outcome, string note,
        string? instance, string? instrument, string? run)
    {
        if (!Rules.Outcomes.Contains(outcome)) throw new LeadsException($"an outcome is lit, empty or blocked, not {outcome}");
        if (instrument is not null && !Rules.Instruments.Contains(instrument))
            throw new LeadsException($"an instrument is one of {string.Join(", ", Rules.Instruments)}, not {instrument}");
        using var held = Record.Lock(coverageRoot);
        var rows = Current(coverageRoot);
        var row = rows.FirstOrDefault(r => S(r["type"]) == type)
            ?? throw new LeadsException($"no lead for {type}; `gatekit leads list` shows them");
        var claim = row["claimed"] as JsonObject;
        var claimedRun = run ?? (claim is not null ? S(claim["run"]) : "");
        var attempt = new Attempt(Record.Now(), claimedRun is { Length: > 0 } ? claimedRun : Record.Who().Run,
            instance ?? (claim is not null ? S(claim["instance"]) : "unknown"), instrument ?? S(row["instrument"]), outcome, note, "report");
        var wasParked = S(row["state"]) == "parked";
        var attempts = (row["attempts"] as JsonArray) ?? [];
        attempts.Add(attempt.ToJson());
        row["attempts"] = attempts;
        row.Remove("claimed");
        Refresh(row, false);
        JsonObject? wish = null;
        if (!wasParked && S(row["state"]) == "parked")
            wish = Record.AddWish(coverageRoot, "leads", $"lead {type} is parked: {S(row["parked"])}. What would make it reachable "
                + $"(entry: {S(row["entry"]?["kind"])} {S(row["entry"]?["detail"])})?", held: true);
        Write(coverageRoot, rows);
        return (row, wish);
    }

    /// <summary>Counts by class, state and reachability; the ceiling is every lead the shipping game can create.</summary>
    public static JsonObject Tally(IReadOnlyList<JsonObject> rows)
    {
        JsonObject By(Func<JsonObject, string> key) =>
            new([.. rows.GroupBy(key).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => KeyValuePair.Create(g.Key, (JsonNode?)g.Count()))]);
        var cells = new JsonObject();
        foreach (var cls in rows.GroupBy(r => S(r["class"])).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var byState = new JsonObject();
            foreach (var st in cls.GroupBy(r => S(r["state"])).OrderBy(g => g.Key, StringComparer.Ordinal))
                byState[st.Key] = new JsonObject([.. st.GroupBy(r => S(r["reachable"])).OrderBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g => KeyValuePair.Create(g.Key, (JsonNode?)g.Count()))]);
            cells[cls.Key] = byState;
        }
        int notShipping = rows.Count(r => S(r["reachable"]) == "not_in_shipping_game");
        return new JsonObject
        {
            ["leads"] = rows.Count,
            ["by_class"] = By(r => S(r["class"])),
            ["by_state"] = By(r => S(r["state"])),
            ["by_reachable"] = By(r => S(r["reachable"])),
            ["by_entry"] = By(r => S(r["entry"]?["kind"])),
            ["by_instrument"] = By(r => S(r["instrument"])),
            ["with_named_entry"] = rows.Count(r => S(r["entry"]?["kind"]) is { Length: > 0 } k && k != "unknown"),
            ["with_data"] = rows.Count(r => r["data"] is JsonArray { Count: > 0 }),
            ["with_wiki"] = rows.Count(r => r["wiki"] is not null),
            ["ceiling"] = rows.Count - notShipping,
            ["ceiling_means"] = "every lead minus those whose type the shipping game's code never adds nor reads (not_in_shipping_game)",
            ["class_x_state_x_reachable"] = cells,
        };
    }
}
