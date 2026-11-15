using System.Text.Json.Nodes;

namespace GateKit.Leads;

/// <summary>Where a creator is entered from, as the walk up its callers found it.</summary>
/// <param name="Basis">calls: a chain of direct calls from the entry to the creator; queue: the creator runs in a
/// system's update, and the entry is reached from a function that reads that system's index (who hands it work);
/// facts: only the committed facts of a system say its update reaches the creator.</param>
/// <param name="Condition">For a system's update: the guard of the operation that creates the type, read off the
/// pseudo-code around its call (the components and fields that must hold), or null when none was extracted.</param>
public sealed record Entry(string Kind, string Detail, int Depth, string? Function, string Basis, IReadOnlyList<string> Guard, string? Condition = null)
{
    public static readonly Entry Unknown = new("unknown", "", 0, null, "none", []);

    public JsonObject ToJson()
    {
        var o = new JsonObject { ["kind"] = Kind, ["detail"] = Detail };
        if (Kind == "unknown") return o;
        o["depth"] = Depth;
        if (Function is not null) o["function"] = Function;
        o["basis"] = Basis;
        if (Guard.Count > 0) o["guard"] = new JsonArray([.. Guard.Select(g => (JsonNode)g)]);
        if (Condition is not null) o["condition"] = Condition;
        return o;
    }
}

/// <summary>
/// Builds the leads: one row per component type never seen alive, from the committed record and the
/// databases of the user's machine. The same inputs give the same file; tries reported by the runs
/// and fields set by hand survive a rebuild.
/// </summary>
public sealed class Builder(string coverageRoot, ICodeIndex? code, IDataIndex? data, IWikiTitles? wiki)
{
    /// <summary>How far up the callers a walk goes, and how many functions it looks at, before it says "unknown".</summary>
    public const int MaxDepth = 4;
    public const int MaxNodes = 400;
    public const int MaxQueueUsers = 8;
    public const int MaxGuard = 6;

    readonly SystemFacts facts = SystemFacts.Load(coverageRoot);

    public async Task<List<JsonObject>> Build(IReadOnlyList<JsonObject> existing, CancellationToken cancel = default)
    {
        var (unseen, seen, types) = Coverage.Read(coverageRoot);
        var had = existing.Where(r => r["type"] is not null).ToDictionary(r => (string)r["type"]!, StringComparer.Ordinal);
        var imported = History.Import(coverageRoot, unseen);
        var rows = new List<JsonObject>();
        foreach (var type in unseen.Concat(had.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            types.TryGetValue(type, out var info);
            var row = await Row(type, info, imported.GetValueOrDefault(type) ?? [], cancel);
            rows.Add(Lead.Merge(row, had.GetValueOrDefault(type), seen.Contains(type)));
        }
        return rows;
    }

    async Task<JsonObject> Row(string type, Coverage.TypeInfo? info, List<Attempt> imported, CancellationToken cancel)
    {
        var creators = facts.Adds(type).Concat(code?.Adders(type) ?? []).DistinctBy(c => (c.Function, c.Site)).ToList();
        var entry = Walk(creators);
        if (entry.Kind == "system_update" && entry.Basis != "queue" && code is not null)
            entry = entry with { Condition = ConditionOf(entry.Detail, type, creators) };
        var (cls, reachable) = Rules.ClassOf(type);
        if (entry.Kind == "load_save" && cls is "gameplay_event" or "gameplay_state") cls = "lifecycle";
        int users = code?.IndexUsers(type) ?? -1;
        // No creator, and nothing but its registration reads its index (ecs::RegisterComponent<T> and the
        // per-type template beside it: two functions): the shipping game never makes one.
        if (creators.Count == 0 && users is >= 0 and <= 2) reachable = "not_in_shipping_game";
        else if (creators.Count == 0 && reachable == "solo") reachable = "unknown";

        var dataRows = new JsonArray();
        StatUse? example = null;
        void Carried(string what, string call, bool functor = false)
        {
            if (data is null) return;
            int count = data.CountCarrying(call, functor);
            if (count == 0) return;
            var some = data.Carrying(call, 5, functor);
            example ??= some.FirstOrDefault(s => Rules.Shipping(s.Mod)) ?? some.FirstOrDefault();
            dataRows.Add(new JsonObject
            {
                ["what"] = $"{what} {call}: {count} stats entries",
                ["where"] = string.Join(", ", some.Select(s => $"{s.Name} ({s.Kind}.{s.Field}{(Rules.Shipping(s.Mod) ? "" : ", " + s.Mod)})")),
            });
        }
        if (Rules.BoostName(type) is { } boost) Carried("boost", boost);
        if (entry.Kind == "functor")
        {
            foreach (var name in Rules.FunctorNames(entry.Detail)) Carried("functor", name, functor: true);
            // The executor reached is shared by every functor; the calls of the data named like the
            // type's namespace say which one (by name, and said so).
            if (dataRows.Count == 0 && data is not null && Rules.Pascal(type) is { Length: >= 4 } word && !Rules.GenericWords.Contains(word))
                foreach (var name in data.CallsHolding(word, 2, functor: true)) Carried($"functor named like \"{word}\" (by name)", name, functor: true);
        }
        int storyUses = 0;
        if (entry.Kind == "osiris" && data is not null)
        {
            storyUses = data.OsirisUses(entry.Detail);
            dataRows.Add(new JsonObject { ["what"] = $"Osiris {entry.Detail}: the story uses it {storyUses} times", ["where"] = "osiris_uses" });
        }
        // An Osiris function whose name holds the type's own words: evidence by name, not by calls, and said so.
        string? byName = null;
        var core = Rules.Core(type);
        // Two words of the name at least: one word ("Template") matches functions about something else.
        if (data is not null && core.Length >= 8 && core.Skip(1).Any(char.IsUpper) && entry.Kind != "osiris")
        {
            var named = data.OsirisFunctions().Where(f => f.Kind == "call" && f.Name.Contains(core, StringComparison.Ordinal)).Select(f => f.Name).Take(3).ToList();
            if (named.Count > 0)
            {
                byName = named[0];
                dataRows.Add(new JsonObject
                {
                    ["what"] = $"Osiris calls whose name holds \"{core}\" (by name, not a call chain)",
                    ["where"] = string.Join(", ", named.Select(n => $"{n} (story uses {data.OsirisUses(n)})")),
                });
            }
        }

        string? page = null;
        if (wiki is not null && data is not null && example is { Kind: "SpellData" or "StatusData" or "PassiveData" })
            if (data.DisplayName(example.Name) is { Length: > 0 } display)
                page = await wiki.PageFor(display, cancel);

        var instrument = Rules.InstrumentOf(cls, entry.Kind, example?.Kind);
        if (cls == "boost" && example is null) instrument = "stage";   // only a script adding the boost reaches it
        if (byName is not null && entry.Kind is "unknown" or "system_update" && instrument is "sweep" or "play") instrument = "osiris";
        var how = How(type, cls, entry, example, byName, storyUses);
        var row = new JsonObject
        {
            ["type"] = type,
            ["side"] = info?.Side ?? (type.StartsWith("ecl::", StringComparison.Ordinal) ? "client" : "server"),
            ["one_frame"] = info?.OneFrame ?? type.Contains("OneFrame", StringComparison.Ordinal),
            ["class"] = cls,
            ["reachable"] = reachable,
            ["creators"] = new JsonArray([.. creators.Take(12).Select(c => (JsonNode)CreatorJson(c))]),
            ["entry"] = entry.ToJson(),
            ["data"] = dataRows,
        };
        if (creators.Count > 12) row["creators_more"] = creators.Count - 12;
        if (users >= 0) row["index_users"] = users;
        if (page is not null) row["wiki"] = page;
        row["how"] = how;
        row["instrument"] = instrument;
        row["attempts"] = new JsonArray([.. imported.Select(a => (JsonNode)a.ToJson())]);
        return row;
    }

    /// <summary>The guard of the first creating operation of the system (shallowest, then by call site) the
    /// pseudo-code gives one for; the operation's place among the calls of the same helper in its function
    /// comes from the order of the sites.</summary>
    string? ConditionOf(string system, string type, IReadOnlyList<Creator> creators)
    {
        var mine = creators.Where(c => c.System == system).ToList();
        foreach (var c in mine.OrderBy(c => c.Depth ?? 9).ThenBy(c => c.Site ?? "~", StringComparer.Ordinal))
        {
            var siblings = mine.Where(x => x.Function == c.Function && x.Site is not null).Select(x => x.Site!).Order(StringComparer.Ordinal).ToList();
            int occurrence = c.Site is null ? 0 : Math.Max(0, siblings.IndexOf(c.Site));
            if (code!.CreationCondition(c.Binary, system, c.Rva, type, occurrence) is { } condition)
                return condition;
        }
        return null;
    }

    static JsonObject CreatorJson(Creator c)
    {
        var o = new JsonObject { ["system"] = c.System, ["function"] = c.Function };
        if (c.Name is { Length: > 0 } && !c.Name.StartsWith("FUN_", StringComparison.Ordinal)) o["name"] = c.Name.Length > 160 ? c.Name[..157] + "..." : c.Name;
        o["binary"] = c.Binary;
        o["site"] = c.Site;
        o["depth"] = c.Depth;
        o["confidence"] = c.Confidence;
        return o;
    }

    /// <summary>
    /// Walks up from the creators to the nearest function whose name says what it is (Rules.EntryByName,
    /// or a system's registered update). A system's update is not where a runner acts, so from there it
    /// goes on through the functions that read the system's index - those that hand it its requests.
    /// </summary>
    public Entry Walk(IReadOnlyList<Creator> creators)
    {
        var found = new List<Entry>();
        foreach (var c in creators.Where(c => c.System is not null))
            found.Add(new Entry("system_update", c.System!, c.Depth ?? 1, c.Function, "facts", []));
        if (code is not null)
            foreach (var group in creators.Where(c => c.Rva >= 0).GroupBy(c => c.Binary))
                if (Up(group.Key, group.Select(c => c.Rva), ignore: []) is { } e) found.Add(e);
        if (found.Count == 0) return Entry.Unknown;
        // A client copy made when the server's replicates is the last resort: where another creator
        // says why the type exists, that is the lead.
        var best = Best(found.Any(e => e.Kind != "replication") ? found.Where(e => e.Kind != "replication") : found);
        if (best.Kind == "system_update")
        {
            // The components the update reads are its guard only when they are few: a system that
            // reads dozens has no condition a list can say, and its digest is where to look.
            var reads = facts.Reads(best.Detail);
            IReadOnlyList<string> guard = reads.Count <= MaxGuard ? reads : [];
            best = best with { Guard = guard };
            if (code is not null)
            {
                // Only a system few functions talk to has a queue worth following: a hub (a manager every
                // part of the game reads) reaches any entry at all, and that says nothing.
                var users = code.SystemIndexUsers(CodeIndex.Windows, best.Detail);
                if (users.Count is > 0 and <= MaxQueueUsers && Up(CodeIndex.Windows, users, ignore: ["system_update", "load_save"]) is { } outside)
                    return outside with { Basis = "queue", Detail = outside.Detail, Guard = guard, Depth = outside.Depth };
            }
        }
        return best;
    }

    static Entry Best(IEnumerable<Entry> found) => found
        .OrderBy(e => e.Depth).ThenBy(e => Array.IndexOf(Rules.EntryKinds, e.Kind)).ThenBy(e => e.Detail, StringComparer.Ordinal).First();

    Entry? Up(string binary, IEnumerable<long> start, string[] ignore)
    {
        var seen = new HashSet<long>(start);
        var level = seen.ToList();
        for (int depth = 0; depth <= MaxDepth && level.Count > 0; depth++)
        {
            var here = new List<Entry>();
            foreach (var f in level.Order())
            {
                var address = $"{binary}+0x{f:X}";
                if (code!.UpdateOf(binary, f) is { } system)
                {
                    if (!ignore.Contains("system_update")) here.Add(new Entry("system_update", system, depth, address, "calls", []));
                    continue;
                }
                if (Rules.EntryOf(code.NameOf(binary, f)) is { } e && !ignore.Contains(e.Kind))
                    here.Add(new Entry(e.Kind, e.Detail, depth, address, "calls", []));
            }
            if (here.Count > 0) return Best(here);
            var next = new List<long>();
            foreach (var f in level)
                foreach (var caller in code!.Callers(binary, f))
                    if (seen.Count < MaxNodes && seen.Add(caller)) next.Add(caller);
            level = next;
        }
        return null;
    }

    static string How(string type, string cls, Entry entry, StatUse? example, string? byName, int storyUses)
    {
        switch (cls)
        {
            case "multiplayer": return "Needs a second player: two game copies joined over the network, then the action of " + Short(type) + ".";
            case "split_screen_controller": return "Needs a game pad, or split screen with a second local player.";
            case "online_ugc": return "Needs Larian's online services (the mod browser, an account): not reachable offline.";
            case "editor_debug": return "A tool or debugging path: the shipping game likely never creates it.";
        }
        if (cls == "boost" && Rules.BoostName(type) is { } boost)
            return example is null
                ? $"Nothing in the game's data carries the boost {boost}(): only a script adding it (Osi.AddBoosts, a cheat) or a mod would."
                : $"Give a character {example.Name} ({Kind(example.Kind)}), which carries {boost}(...) in {example.Field}, with a capture running.";
        string guard = entry.Condition is not null ? " when " + Cut(entry.Condition, 240)
            : entry.Guard.Count > 0 ? " when it finds " + string.Join(", ", entry.Guard.Select(Short)) : "";
        return entry.Kind switch
        {
            "osiris" => $"Call Osi.{entry.Detail} on a fitting object with a capture running (the story uses it {storyUses} times).",
            "message_handler" => $"Do through the game's own UI what sends the message {entry.Detail} handles.",
            "functor" when example is not null => $"Use {example.Name} ({Kind(example.Kind)}), whose {example.Field} runs the functor, with a capture running.",
            "functor" => $"Make the game execute a stats functor (reached through {entry.Detail}) that hands this work"
                         + (entry.Basis == "queue" ? " to the system through its queue" : "") + ".",
            "status" => $"Apply a status whose events reach {entry.Detail}" + (example is not null ? $", e.g. {example.Name}" : "") + ".",
            "boost" => $"Make {entry.Detail} add a boost" + (example is not null ? $": {example.Name} carries one" : "") + ".",
            "system_update" when byName is not null => $"Try Osi.{byName} (matched by name), then play while {Short(entry.Detail)} runs{guard}.",
            "system_update" when entry.Condition is not null => $"Created by {Short(entry.Detail)}'s update{guard}: make that hold while it runs (`gatekit symbols digest {entry.Detail}`).",
            "system_update" => $"Play while {Short(entry.Detail)} runs: its update adds this{guard}; read `gatekit symbols digest {entry.Detail}` for the condition.",
            "load_save" when entry.Detail.Contains("Menu", StringComparison.Ordinal) => $"Go back to the main menu (or start the game to it): it is added on the path {Short(entry.Detail)}.",
            "load_save" => $"Save and load a game: it is added on the path {Short(entry.Detail)}.",
            "replication" => "Make the server's copy appear: the client creates this when the server's component replicates.",
            _ when byName is not null => $"Try Osi.{byName} (matched by name only) with a capture running.",
            _ => $"No entry is known: catch it with a wide sweep around {Namespace(type)}.",
        };
    }

    static string Kind(string statKind) => statKind switch
    {
        "SpellData" => "a spell: cast it",
        "StatusData" => "a status: Osi.ApplyStatus",
        "PassiveData" => "a passive: Osi.AddPassive",
        "Weapon" or "Armor" or "Object" => "an item: equip it",
        _ => statKind,
    };

    static string Short(string name) => name.Length > 70 ? name[..67] + "..." : name;

    static string Cut(string text, int max) => text.Length > max ? text[..(max - 3)] + "..." : text;

    static string Namespace(string type) => type.LastIndexOf("::", StringComparison.Ordinal) is int at and > 0 ? type[..at] : type;
}

/// <summary>The coverage index as the leads read it: what is mapped, what was seen, and on which side.</summary>
public static class Coverage
{
    public sealed record TypeInfo(string Side, bool OneFrame);

    /// <summary>The never-seen types (mapped, never observed alive - the ledger's group "unseen"),
    /// the seen ones, and each type's side and one-frame flag.</summary>
    public static (List<string> Unseen, HashSet<string> Seen, Dictionary<string, TypeInfo> Types) Read(string root)
    {
        var path = Path.Combine(root, "coverage.json");
        if (!File.Exists(path)) throw new LeadsException($"the coverage index is missing: {path}");
        var types = new Dictionary<string, TypeInfo>(StringComparer.Ordinal);
        var mapped = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in (JsonNode.Parse(File.ReadAllText(path))!["components"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var name = row["name"]?.GetValue<string>();
            if (name is null) continue;
            var side = row["side"]?.GetValue<string>() ?? "?";
            bool oneFrame = row["oneFrame"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
            types[name] = types.TryGetValue(name, out var had) ? had with { Side = had.Side == side ? side : "both", OneFrame = had.OneFrame || oneFrame } : new(side, oneFrame);
            if (Truthy(row["mapped"])) mapped.Add(name);
        }
        var seen = Seen(root);
        return ([.. mapped.Where(t => !seen.Contains(t)).Order(StringComparer.Ordinal)], seen, types);
    }

    /// <summary>The types seen alive at least once (seen/index.json, which bg3_seen_drain and bg3_seen_new keep).</summary>
    public static HashSet<string> Seen(string root)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var path = Path.Combine(root, "seen", "index.json");
        if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject index)
            foreach (var (name, sides) in index)
                if (sides is JsonObject { Count: > 0 }) seen.Add(name);
        return seen;
    }

    static bool Truthy(JsonNode? value) => value switch
    {
        null => false,
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<double>(out var d) => d != 0,
        JsonValue v when v.TryGetValue<string>(out var s) => s.Length > 0,
        _ => true,
    };
}
