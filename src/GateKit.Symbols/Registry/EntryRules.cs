using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GateKit.Json;

namespace GateKit.Symbols.Registry;

/// <summary>
/// What the tools do to an entry by themselves when it is written (registry/README.md, "What the
/// tools do by themselves"): the source of its name looked up in the stores, an address in a
/// sentence turned into the id of the function there, a sentence of evidence said twice kept once,
/// and every rung put where its evidence carries it. Nothing here refuses; each act is a line of
/// the journal. `registry note` applies the parts that need no build table, `registry commit` and
/// `registry tidy` all of them.
/// </summary>
public sealed partial class EntryRules(RegistryStore store, INameStores? names, Journal journal)
{
    public Journal Journal { get; } = journal;

    /// <summary>The moment every line of this run carries.</summary>
    public DateTime When { get; init; } = DateTime.UtcNow;

    /// <summary>Leaves every rung as it stands (`registry tidy --keep-rungs`): the check goes on noting the ones above their evidence.</summary>
    public bool KeepRungs { get; init; }

    /// <summary>How many ids' own names a store holds, written by the tool or already said by hand.</summary>
    public int NamesHeld { get; private set; }

    Dictionary<string, List<(string Binary, long Rva)>>? placed;

    /// <summary>Where the build tables put an id, as they were when this run first asked.</summary>
    public IReadOnlyList<(string Binary, long Rva)> PlacesOf(string id)
    {
        if (placed is null)
        {
            placed = new(StringComparer.Ordinal);
            foreach (var (build, binary) in store.Tables())
                foreach (var row in store.Table(build, binary))
                {
                    if (!placed.TryGetValue(row.Id, out var list)) placed[row.Id] = list = [];
                    if (!list.Contains((binary, row.Rva))) list.Add((binary, row.Rva));
                }
        }
        return placed.TryGetValue(id, out var found) ? found : [];
    }

    /// <summary>Applies every rule to an entry about to be written; true when it changed.</summary>
    public bool Apply(EntryKind kind, JsonObject entry, IReadOnlyList<(string Binary, long Rva)> places)
    {
        var changed = Collapse(entry, Journal, When);
        if (names is not null && kind != EntryKind.Global)
        {
            var found = names.Of(kind, JsonText.Text(entry["id"])!, places);
            if (found.Any(f => f.Name == NameSources.Bare(JsonText.Text(entry["id"])!))) NamesHeld++;
            changed |= NameSources.Apply(entry, kind, found, Journal, When);
        }
        changed |= TurnAddresses(entry, places.Select(p => p.Binary).ToList());
        changed |= Collapse(entry, Journal, When);
        if (!KeepRungs) changed |= SettleRungs(entry, kind, Journal, When);
        return changed;
    }

    // --- rungs ---

    /// <summary>Puts the rung of an entry and of each of its fields and values where its own evidence carries it.</summary>
    public static bool SettleRungs(JsonObject entry, EntryKind kind, Journal journal, DateTime when)
    {
        var id = JsonText.Text(entry["id"])!;
        var changed = Settle(entry, kind == EntryKind.Function, id, null, journal, when);
        foreach (var list in new[] { "fields", "values" })
            foreach (var member in (entry[list] as JsonArray ?? []).OfType<JsonObject>())
                changed |= Settle(member, false, id, JsonText.Text(member["name"]), journal, when);
        return changed;
    }

    static bool Settle(JsonObject thing, bool code, string id, string? member, Journal journal, DateTime when)
    {
        var asked = JsonText.Text(thing["rung"]);
        var settled = Rungs.Settle(asked, thing["evidence"], code, out var why);
        if (why is null || settled == asked) return false;
        thing["rung"] = settled;
        journal.Add(new JournalLine(when, Rungs.Rank(settled) > Rungs.Rank(asked) ? "lowered" : "raised", id) { Member = member, From = asked, To = settled, Why = why });
        return true;
    }

    // --- sentences said twice ---

    /// <summary>Keeps once a sentence of evidence, or a question, that an entry or one of its members says twice.</summary>
    public static bool Collapse(JsonObject entry, Journal journal, DateTime when)
    {
        var gone = Distinct(entry, "evidence") + Distinct(entry, "questions");
        foreach (var list in new[] { "fields", "values" })
            foreach (var member in (entry[list] as JsonArray ?? []).OfType<JsonObject>())
                gone += Distinct(member, "evidence");
        if (gone > 0) journal.Add(new JournalLine(when, "collapsed", JsonText.Text(entry["id"])!) { Count = gone });
        return gone > 0;
    }

    static int Distinct(JsonObject owner, string key)
    {
        if (owner[key] is not JsonArray items) return 0;
        var kept = new List<JsonNode?>();
        foreach (var item in items)
            if (!kept.Any(k => JsonNode.DeepEquals(k, item))) kept.Add(item);
        var gone = items.Count - kept.Count;
        if (gone > 0) owner[key] = new JsonArray([.. kept.Select(k => k?.DeepClone())]);
        return gone;
    }

    // --- an address in a sentence ---

    [GeneratedRegex(@"(?<![\w:])([A-Za-z0-9_]+\.(?:exe|dll))(?:@([0-9]+))?\+0x([0-9A-Fa-f]+)\b")] private static partial Regex Placed();

    /// <summary>An address in a sentence in its durable form, the binary, its build and the place: <c>bg3_dx11.exe@25605617+0x1BC3C2B</c>.</summary>
    [GeneratedRegex(@"(?<![\w:])([A-Za-z0-9_]+\.(?:exe|dll))@([0-9]+)\+0x([0-9A-Fa-f]+)\b")] public static partial Regex Qualified();

    /// <summary>The durable form of an address.</summary>
    public static string Qualify(string binary, string build, long rva) => string.Create(CultureInfo.InvariantCulture, $"{binary}@{build}+0x{rva:X}");

    /// <summary>Every sentence of an entry rewritten by a function of its text; true when one changed.</summary>
    public static bool Rewrite(JsonNode? node, Func<string, string> rewrite)
    {
        var changed = false;
        switch (node)
        {
            case JsonObject o:
                foreach (var (key, value) in o.ToList())
                {
                    if (key == "survey") continue;
                    if (RegistryCheck.SentenceKeys.Contains(key) && JsonText.Text(value) is { } text)
                    {
                        var fresh = rewrite(text);
                        if (fresh != text) { o[key] = fresh; changed = true; }
                    }
                    else changed |= Rewrite(value, rewrite);
                }
                break;
            case JsonArray a:
                foreach (var item in a) changed |= Rewrite(item, rewrite);
                break;
        }
        return changed;
    }

    Dictionary<string, List<BuildRow>>? rows;

    /// <summary>Whether an address lies in a function the registry's tables of a binary hold, or the databases know.</summary>
    bool InFunction(string binary, long rva, bool askStores)
    {
        if (rows is null)
        {
            rows = new(StringComparer.OrdinalIgnoreCase);
            foreach (var (build, table) in store.Tables())
            {
                if (!rows.TryGetValue(table, out var of)) rows[table] = of = [];
                of.AddRange(store.Table(build, table));
            }
        }
        return (rows.TryGetValue(binary, out var held) && held.Any(r => r.Rva == rva || (r.Size is { } size && rva > r.Rva && rva < r.Rva + size)))
            || (askStores && names?.InCode(binary, rva) == true);
    }

    /// <summary>The build an address of a binary written in an entry is of: the only build with a table of the binary, else the only one whose table holds the entry.</summary>
    string? BuildOf(string binary, string id)
    {
        if (!buildsOf.TryGetValue(binary, out var builds))
            buildsOf[binary] = builds = [.. store.Tables().Where(t => string.Equals(t.Binary, binary, StringComparison.OrdinalIgnoreCase)).Select(t => t.Build)];
        if (builds.Count > 1) builds = [.. builds.Where(b => store.Table(b, binary).Any(r => r.Id == id))];
        return builds.Count == 1 ? builds[0] : null;
    }

    readonly Dictionary<string, List<string>> buildsOf = new(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"\b(?:thunk_)?FUN_(1[48][0-9a-fA-F]{7,8})\b")] private static partial Regex MachineName();
    [GeneratedRegex(@"(?<![\w.:+])\+?0x([0-9A-Fa-f]{5,})\b")] private static partial Regex Bare();

    Dictionary<string, Dictionary<long, string?>>? starts;

    /// <summary>The one named id that starts at an address of a binary, in any build's table; null when none or several do.</summary>
    string? NamedAt(string binary, long rva)
    {
        if (starts is null)
        {
            starts = new(StringComparer.OrdinalIgnoreCase);
            foreach (var (build, table) in store.Tables())
            {
                if (!starts.TryGetValue(table, out var of)) starts[table] = of = [];
                foreach (var row in store.Table(build, table))
                {
                    if (RegistryId.IsUnnamed(row.Id)) continue;
                    of[row.Rva] = of.TryGetValue(row.Rva, out var held) && held != row.Id ? null : row.Id;
                }
            }
        }
        return starts.TryGetValue(binary, out var ids) && ids.TryGetValue(rva, out var id) ? id : null;
    }

    /// <summary>
    /// Makes the addresses in the sentences of an entry durable. Where exactly one named id starts at
    /// an address it becomes <c>code:&lt;id&gt;</c>: <c>binary+0xRVA</c> by that binary's tables, a
    /// bare address and the decompiler's machine name by the tables of the binaries the entry itself
    /// is placed in. An address of anything else - a call site inside a function, a callee with no
    /// name - is a lead and is kept: it is written with its binary and build
    /// (<see cref="Qualify"/>) when it lies in a function the tables or the databases know. What
    /// cannot be placed is left as written and counted.
    /// </summary>
    public bool TurnAddresses(JsonObject entry, IReadOnlyList<string> binaries)
    {
        var id = JsonText.Text(entry["id"])!;
        var own = binaries.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(b => b.Equals("bg3_dx11.exe", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
        if (own.Count == 0) own = [.. store.Tables().Select(t => t.Binary).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(b => b.Equals("bg3_dx11.exe", StringComparison.OrdinalIgnoreCase) ? 0 : 1)];
        int turned = 0, qualified = 0, left = 0;
        string? Among(long rva) => own.Select(b => NamedAt(b, rva)).FirstOrDefault(found => found is not null);
        string Cite(string named) { turned++; return "code:" + named; }
        // a bare address is the entry's own binary's: written `+0x...` it is a place of the code, written `0x...` it may be a number, and then only the tables' own functions vouch for it
        string Place(Match m, long rva, bool plus)
        {
            if (own.FirstOrDefault(b => InFunction(b, rva, plus)) is not { } binary || BuildOf(binary, id) is not { } build) return m.Value;
            qualified++;
            return Qualify(binary, build, rva);
        }
        var does = JsonText.Text(entry["does"]);
        var changed = Rewrite(entry, text =>
        {
            var fresh = Placed().Replace(text, m =>
            {
                var rva = RegistryStore.ParseHex(m.Groups[3].Value);
                if (NamedAt(m.Groups[1].Value, rva) is { } named) return Cite(named);
                if (m.Groups[2].Success || BuildOf(m.Groups[1].Value, id) is not { } build) return m.Value;
                qualified++;
                return Qualify(m.Groups[1].Value, build, rva);
            });
            fresh = MachineName().Replace(fresh, m => Among(RegistryStore.ParseHex(m.Groups[1].Value) & 0xFFFFFFF) is { } named ? Cite(named) : m.Value);
            fresh = Bare().Replace(fresh, m => !long.TryParse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rva) ? m.Value
                : Among(rva) is { } named ? Cite(named) : Place(m, rva, m.Value[0] == '+'));
            left += RegistryCheck.Addresses(fresh);
            return fresh;
        });
        if (turned + qualified > 0)
            Journal.Add(new JournalLine(When, "turned", id) { Count = turned, Qualified = qualified, Left = left, Why = does != JsonText.Text(entry["does"]) ? "does" : null });
        AddressesLeft += left;
        return changed;
    }

    /// <summary>The addresses still standing, not placed, in sentences of the entries this run has seen.</summary>
    public int AddressesLeft { get; private set; }

    /// <summary>
    /// After a table moved to another build of the same binary (`registry reanchor`): an address in a
    /// sentence, written with the old build, that lies in a function which moved with an equal body
    /// moves by the same offset and takes the new build; any other is stale - left as written, a line
    /// of the journal each.
    /// </summary>
    public (int Moved, int Stale) ReanchorProse(string binary, string sourceBuild, string targetBuild, IReadOnlyList<BuildRow> source, IReadOnlyList<Moved> moved, bool dry)
    {
        int carried = 0, stale = 0;
        var target = moved.Where(m => m.BodyAgrees == true).ToDictionary(m => m.Id, m => m.Row.Rva, StringComparer.Ordinal);
        foreach (var entry in store.All().ToList())
        {
            var json = (JsonObject)entry.Json.DeepClone();
            json.Remove("schema");
            var before = stale;
            var changed = Rewrite(json, text => Qualified().Replace(text, m =>
            {
                if (!m.Groups[1].Value.Equals(binary, StringComparison.OrdinalIgnoreCase) || m.Groups[2].Value != sourceBuild) return m.Value;
                var rva = RegistryStore.ParseHex(m.Groups[3].Value);
                var row = source.FirstOrDefault(r => r.Rva == rva || (r.Size is { } size && rva > r.Rva && rva < r.Rva + size));
                if (row is null || !target.TryGetValue(row.Id, out var start)) { stale++; return m.Value; }
                carried++;
                return Qualify(m.Groups[1].Value, targetBuild, start + (rva - row.Rva));
            }));
            if (stale > before) Journal.Add(new JournalLine(When, "stale", entry.Id) { Count = stale - before, Why = $"addresses of {binary} of build {sourceBuild}: no function that moved to build {targetBuild} with an equal body holds them" });
            if (changed && !dry) store.Write(entry.Kind, json, create: false);
        }
        return (carried, stale);
    }

    // --- one function under two ids ---

    /// <summary>The unnamed ids that share an address - and a body, where both rows carry its hash -
    /// with exactly one named id: each with the id it is.</summary>
    public static List<(string Unnamed, string Named, string Where)> Duplicates(RegistryStore store)
    {
        var found = new List<(string, string, string)>();
        foreach (var (build, binary) in store.Tables())
            foreach (var group in store.Table(build, binary).GroupBy(r => r.Rva))
            {
                var named = group.Where(r => !RegistryId.IsUnnamed(r.Id)).ToList();
                if (named.Count != 1) continue;
                foreach (var row in group.Where(r => RegistryId.IsUnnamed(r.Id)))
                    if ((row.Body is null || named[0].Body is null || row.Body == named[0].Body) && !found.Any(f => f.Item1 == row.Id))
                        found.Add((row.Id, named[0].Id, string.Create(CultureInfo.InvariantCulture, $"{binary}+0x{row.Rva:X}")));
            }
        return found;
    }

    /// <summary>
    /// Folds every such duplicate: the named entry takes the unnamed one's evidence and its open
    /// questions of reach and conditions (what it is, the name answers), every citation of the
    /// unnamed id follows, and the unnamed entry, its rows and its live claims go.
    /// </summary>
    public int Fold(LiveLayer? live, bool dry, TextWriter output)
    {
        var duplicates = Duplicates(store);
        foreach (var (unnamed, named, where) in duplicates)
        {
            output.WriteLine(Journal.Add(new JournalLine(When, "folded", unnamed) { Into = named, Why = "one function at " + where }).Said());
            if (dry) continue;
            if (store.Get(unnamed) is not { } gone || store.Get(named) is not { } kept) continue;
            var entry = (JsonObject)kept.Json.DeepClone();
            entry.Remove("schema");
            foreach (var (key, keep) in new (string, Func<JsonObject, bool>)[] { ("evidence", _ => true), ("questions", q => JsonText.Text(q["about"]) is "reach" or "conditions") })
            {
                var target = entry[key] as JsonArray ?? [];
                foreach (var item in (gone.Json[key] as JsonArray ?? []).OfType<JsonObject>().Where(keep))
                    if (!target.Any(t => JsonNode.DeepEquals(t, item))) target.Add(item.DeepClone());
                if (target.Count > 0 && entry[key] is null) entry[key] = target;
            }
            store.Write(kept.Kind, entry, create: false);
            RegistryRefs.Remove(store, unnamed, live, TextWriter.Null, into: named);
        }
        return duplicates.Count;
    }
}
