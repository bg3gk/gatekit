using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using GateKit.Json;
using GateKit.Symbols.Decomp;

namespace GateKit.Symbols.Registry;

/// <summary>What the registry's files say changed since a commit: read from git, where there is one.</summary>
public sealed record RegistryChanges(string? Commit, DateTime? Time, IReadOnlyList<string> Added, IReadOnlyList<string> Changed, IReadOnlyList<string> Removed,
    IReadOnlyDictionary<string, int> RungsMoved)
{
    /// <summary>The paths, relative to the registry, of the entries added or changed.</summary>
    public IEnumerable<string> Touched => Added.Concat(Changed);

    static string? Git(string folder, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = Encoding.UTF8 };
            foreach (var argument in new[] { "-C", folder, "-c", "core.quotepath=off" }.Concat(arguments)) start.ArgumentList.Add(argument);
            using var git = Process.Start(start);
            if (git is null) return null;
            var errors = git.StandardError.ReadToEndAsync();
            var text = git.StandardOutput.ReadToEnd();
            git.WaitForExit();
            errors.Wait();
            return git.ExitCode == 0 ? text : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return null; }
    }

    static bool IsEntry(string path) => path.EndsWith(".json", StringComparison.Ordinal)
        && (path.StartsWith("functions/", StringComparison.Ordinal) || path.StartsWith("types/", StringComparison.Ordinal) || path.StartsWith("globals/", StringComparison.Ordinal));

    /// <summary>
    /// The entries added, changed and removed since <paramref name="since"/> - a commit, a range
    /// <c>A..B</c> of two, or a time (the last commit before it); without it, the checkout's last
    /// commit - and how the rungs of the entries that were already there moved. Null when the
    /// registry is in no git repository or git does not know the commit.
    /// </summary>
    public static RegistryChanges? Since(RegistryStore store, string? since)
    {
        if (!Directory.Exists(store.Root)) return null;
        string from = since ?? "HEAD";
        string? to = null;
        if (since is not null && DateTime.TryParse(since, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeLocal, out var moment) && !since.All(char.IsAsciiHexDigit))
        {
            if (Git(store.Root, "rev-list", "-1", "--before=" + moment.ToString("o", CultureInfo.InvariantCulture), "HEAD")?.Trim() is not { Length: > 0 } before) return null;
            from = before;
        }
        else if (since?.IndexOf("..", StringComparison.Ordinal) is > 0 and var cut) (from, to) = (since[..cut], since[(cut + 2)..].TrimStart('.'));
        if (Git(store.Root, "show", "-s", "--format=%H %cI", from)?.Trim().Split(' ') is not { Length: 2 } shown) return null;
        var range = to is { Length: > 0 } ? new[] { from, to } : [from];
        if (Git(store.Root, ["diff", "-U0", "--no-renames", "--relative", .. range, "--", "."]) is not { } diff) return null;
        List<string> added = [], changed = [], removed = [];
        var moved = new SortedDictionary<string, int>(StringComparer.Ordinal);
        string? old = null, path = null, was = null, now = null;
        void Close()
        {
            if (path is not null && was is not null && now is not null && was != now) moved[$"{was} -> {now}"] = moved.GetValueOrDefault($"{was} -> {now}") + 1;
            was = now = null;
        }
        foreach (var line in diff.Split('\n'))
        {
            if (line.StartsWith("--- ", StringComparison.Ordinal)) { Close(); old = line[4..].Trim(); path = null; }
            else if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                var fresh = line[4..].Trim();
                path = fresh == "/dev/null" ? old![2..] : fresh[2..];
                if (!IsEntry(path)) { path = null; continue; }
                (old == "/dev/null" ? added : fresh == "/dev/null" ? removed : changed).Add(path);
            }
            else if (path is not null && line.StartsWith("-  \"rung\": \"", StringComparison.Ordinal)) was = line[12..].TrimEnd(',', '\r').Trim('"');
            else if (path is not null && line.StartsWith("+  \"rung\": \"", StringComparison.Ordinal)) now = line[12..].TrimEnd(',', '\r').Trim('"');
        }
        Close();
        if (to is null && Git(store.Root, "ls-files", "--others", "--exclude-standard", "--", ".") is { } untracked)
            added.AddRange(untracked.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(IsEntry).Except(added));
        return new RegistryChanges(shown[0], DateTime.Parse(shown[1], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal), added, changed, removed, moved);
    }
}

/// <summary>
/// `registry digest`: the one page a person reads after a run that wrote into the registry - what
/// the period added, what the tools did by themselves (the journal), and the short list of what is
/// left for a person to decide. `registry tidy` applies to committed entries what a commit applies
/// to a claim; `registry remove` takes an entry out.
/// </summary>
public static class Digest
{
    internal static void Add(Command root, Func<ParseResult, Func<DecompSession, int>, int> invoke)
    {
        {
            var since = new Option<string?>("--since") { Description = "a commit, a range A..B, or a time: what was added since (default: the checkout's last commit)" };
            var asJson = new Option<bool>("--json") { Description = "the digest as JSON" };
            var coverage = new Option<string?>("--coverage");
            var c = new Command("digest", "one page after a run that wrote into the registry: what it added by kind and rung, what the tools lowered, folded, found and turned by themselves, and the short list a person is needed for") { since, asJson, coverage };
            c.SetAction(p => invoke(p, s =>
            {
                var store = RegistryCommands.Store(p.GetValue(coverage));
                var live = LiveLayer.OfHome();
                var digest = Of(store, live, new Journal(live.Folder), RegistryChanges.Since(store, p.GetValue(since)), p.GetValue(since));
                if (p.GetValue(asJson)) s.Out.WriteLine(JsonText.Format(digest));
                else Print(s.Out, digest);
                return 0;
            }));
            root.Subcommands.Add(c);
        }
        {
            var scope = new Argument<string?>("scope") { Arity = ArgumentArity.ZeroOrOne, Description = "only entries whose id, or owner, starts with this (esv::roll)" };
            var since = new Option<string?>("--since") { Description = "only the entries added or changed since a commit, a range A..B, or a time" };
            var dry = new Option<bool>("--dry-run") { Description = "say what would be done; write nothing" };
            var keepRungs = new Option<bool>("--keep-rungs") { Description = "leave every rung as it stands; `registry check` goes on noting the ones above their evidence" };
            var coverage = new Option<string?>("--coverage");
            var c = new Command("tidy", "do to committed entries what a commit does to a note: write where each name comes from as the stores hold it, make the addresses in sentences durable (an id, or binary@build+0xRVA), keep once what is said twice, put every rung where its evidence carries it, fold an unnamed id into the named one at its address") { scope, since, dry, keepRungs, coverage };
            c.SetAction(p => invoke(p, s => Tidy(s, RegistryCommands.Store(p.GetValue(coverage)), LiveLayer.OfHome(), new DatabaseNameStores(s.Symbols, s.DbIfExists), p.GetValue(scope), p.GetValue(since), p.GetValue(dry), p.GetValue(keepRungs))));
            root.Subcommands.Add(c);
        }
        {
            var id = new Argument<string>("id") { Description = "the entry's id" };
            var coverage = new Option<string?>("--coverage");
            var c = new Command("remove", "take a committed entry out: its file, its rows in every build table, its live claims; refused while anything cites it") { id, coverage };
            c.SetAction(p => invoke(p, s => { RegistryRefs.Remove(RegistryCommands.Store(p.GetValue(coverage)), p.GetValue(id)!, LiveLayer.OfHome(), s.Out); return 0; }));
            root.Subcommands.Add(c);
        }
    }

    /// <summary>Whether an entry is in a scope: by its id, or by the owner an unplaced entry names.</summary>
    public static bool InScope(RegistryEntry entry, string? scope) => scope is null || entry.Id.StartsWith(scope, StringComparison.Ordinal)
        || JsonText.Text(entry.Json["owner"])?.StartsWith(scope, StringComparison.Ordinal) == true;

    /// <summary>`registry tidy`: the rules of <see cref="EntryRules"/> over committed entries.</summary>
    internal static int Tidy(DecompSession s, RegistryStore store, LiveLayer? live, INameStores? names, string? scope, string? since, bool dry, bool keepRungs = false) =>
        Tidy(s.Out, store, live, names, scope, since is null ? null : RegistryChanges.Since(store, since) ?? throw new RegistryException($"--since {since}: git knows no such commit for {store.Root}"), dry, keepRungs);

    public static int Tidy(TextWriter output, RegistryStore store, LiveLayer? live, INameStores? names, string? scope, RegistryChanges? changes, bool dry, bool keepRungs = false)
    {
        var journal = new Journal(dry ? null : live?.Folder);
        var rules = new EntryRules(store, names, journal) { KeepRungs = keepRungs };
        var only = changes?.Touched.ToHashSet(StringComparer.Ordinal);
        int seen = 0, written = 0;
        foreach (var entry in store.All().Where(e => InScope(e, scope) && only?.Contains(store.Relative(e.Path)) != false).ToList())
        {
            seen++;
            var json = (JsonObject)entry.Json.DeepClone();
            json.Remove("schema");
            var said = journal.Lines.Count;
            if (!rules.Apply(entry.Kind, json, rules.PlacesOf(entry.Id))) continue;
            if (!dry) store.Write(entry.Kind, json, create: false);
            written++;
            foreach (var line in journal.Lines.Skip(said)) output.WriteLine(line.Said());
        }
        var folded = rules.Fold(live, dry, output);
        journal.Flush();
        int Count(string act) => journal.Lines.Count(l => l.Act == act);
        var matched = journal.Lines.Where(l => l.Act == "matched").ToList();
        output.WriteLine($"{seen} entr{(seen == 1 ? "y" : "ies")} read, {written} {(dry ? "would change" : "changed")}{(dry ? " (--dry-run: nothing written)" : "")}");
        output.WriteLine($"  names: a store holds the name of {rules.NamesHeld}; {matched.Select(l => l.Id).Distinct().Count()} entries get the evidence written ({matched.Count} items), the rest say it already; {Count("differs")} under another name in a store");
        output.WriteLine($"  rungs: {Count("lowered")} lowered, {Count("raised")} raised to the store's rung");
        output.WriteLine($"  duplicates: {folded} folded");
        output.WriteLine($"  addresses: {journal.Lines.Where(l => l.Act == "turned").Sum(l => l.Count ?? 0)} turned into ids in {journal.Lines.Count(l => l.Act == "turned" && l.Count > 0)} entries ({journal.Lines.Count(l => l.Act == "turned" && l.Count > 0 && l.Why == "does")} of them in what the entry does), "
            + $"{journal.Lines.Where(l => l.Act == "turned").Sum(l => l.Qualified ?? 0)} written with binary and build, {rules.AddressesLeft} not placed");
        output.WriteLine($"  repeated: {journal.Lines.Where(l => l.Act == "collapsed").Sum(l => l.Count ?? 0)} sentences kept once in {Count("collapsed")} entries");
        return 0;
    }

    static JsonObject Tally(IEnumerable<string?> keys)
    {
        var o = new JsonObject();
        foreach (var group in keys.GroupBy(k => k ?? "no rung").OrderBy(g => Rungs.Rank(g.Key)).ThenBy(g => g.Key, StringComparer.Ordinal)) o[group.Key] = group.Count();
        return o;
    }

    /// <summary>The digest as data (`--json`).</summary>
    public static JsonObject Of(RegistryStore store, LiveLayer? live, Journal journal, RegistryChanges? changes, string? since)
    {
        var digest = new JsonObject();
        DateTime? from = changes?.Time;
        if (changes is null && since is not null && DateTime.TryParse(since, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeLocal, out var moment)) from = moment;
        digest["since"] = new JsonObject { ["commit"] = changes?.Commit, ["time"] = from is { } t ? LiveLayer.Time(t) : null };
        var byPath = store.All().ToDictionary(e => store.Relative(e.Path), StringComparer.Ordinal);
        if (changes is not null)
        {
            var added = new JsonObject();
            foreach (var kind in Enum.GetValues<EntryKind>())
            {
                var of = changes.Added.Where(p => p.StartsWith(RegistryStore.Folder(kind) + "/", StringComparison.Ordinal)).ToList();
                added[RegistryStore.Folder(kind)] = new JsonObject
                {
                    ["count"] = of.Count,
                    ["by_rung"] = Tally(of.Where(byPath.ContainsKey).Select(p => JsonText.Text(byPath[p].Json["rung"]))),
                };
            }
            digest["added"] = added;
            digest["changed"] = changes.Changed.Count;
            digest["removed"] = changes.Removed.Count;
            digest["rungs_moved"] = new JsonObject([.. changes.RungsMoved.Select(m => KeyValuePair.Create(m.Key, (JsonNode?)m.Value))]);
        }
        // the journal: the newest line of each act on each id and member is what stands
        var lines = journal.Since(from).GroupBy(l => (l.Act, l.Id, l.Member, l.Name, l.Store)).Select(g => g.Last()).ToList();
        JsonObject Moves(string act)
        {
            var of = lines.Where(l => l.Act == act).ToList();
            return new JsonObject
            {
                ["count"] = of.Count,
                ["by_move"] = new JsonObject([.. of.GroupBy(l => $"{l.From} -> {l.To}").OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => KeyValuePair.Create(g.Key, (JsonNode?)g.Count()))]),
                ["by_reason"] = new JsonObject([.. of.GroupBy(l => (l.Why ?? "").Split(';')[0]).OrderByDescending(g => g.Count()).Select(g => KeyValuePair.Create(g.Key, (JsonNode?)g.Count()))]),
                ["ids"] = new JsonArray([.. of.Select(l => (JsonNode)(l.Id + (l.Member is null ? "" : "." + l.Member)))]),
            };
        }
        digest["lowered"] = Moves("lowered");
        digest["raised"] = Moves("raised");
        digest["folded"] = new JsonArray([.. lines.Where(l => l.Act == "folded").Select(l => (JsonNode)new JsonObject { ["id"] = l.Id, ["into"] = l.Into })]);
        var matched = lines.Where(l => l.Act == "matched").ToList();
        digest["names"] = new JsonObject
        {
            ["matched"] = matched.Count,
            ["entries"] = matched.Select(l => l.Id).Distinct().Count(),
            ["by_store"] = new JsonObject([.. matched.GroupBy(l => l.Store == "bg3_macos_x64" ? "macOS" : l.Store == "type-names" ? "type names" : "Toolkit").OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => KeyValuePair.Create(g.Key, (JsonNode?)g.Count()))]),
            ["by_tie"] = new JsonObject([.. matched.GroupBy(l => l.Tie ?? "name").OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => KeyValuePair.Create(g.Key, (JsonNode?)g.Count()))]),
        };
        // a store's other name still stands while the entry keeps its id and the name under known_as
        var differs = lines.Where(l => l.Act == "differs" && store.Get(l.Id) is { } e && e.Id == l.Id
            && (e.Json["known_as"] as JsonArray ?? []).Any(k => JsonText.Text(k?["name"]) == l.Name)).ToList();
        digest["differs"] = new JsonArray([.. differs.Select(l => (JsonNode)new JsonObject { ["id"] = l.Id, ["store"] = l.Store, ["name"] = l.Name })]);
        var turned = lines.Where(l => l.Act == "turned").ToList();
        var period = changes is null ? store.All() : [.. changes.Touched.Where(byPath.ContainsKey).Select(p => byPath[p])];
        digest["addresses"] = new JsonObject
        {
            ["turned"] = turned.Sum(l => l.Count ?? 0), ["entries"] = turned.Count(l => l.Count > 0),
            ["qualified"] = turned.Sum(l => l.Qualified ?? 0),
            ["stale"] = lines.Where(l => l.Act == "stale").Sum(l => l.Count ?? 0),
            ["left"] = period.Sum(e => AddressesIn(e.Json)), ["left_in"] = period.Count(e => AddressesIn(e.Json) > 0),
        };
        digest["repeated"] = lines.Where(l => l.Act == "collapsed").Sum(l => l.Count ?? 0);
        // rungs a hand kept above their evidence (`registry tidy --keep-rungs`, an edit): the bar's own count, for whoever decides
        var above = new JsonArray();
        foreach (var e in period)
        {
            if (Rungs.Settle(JsonText.Text(e.Json["rung"]), e.Json["evidence"], e.Kind == EntryKind.Function, out _) is { } to && Rungs.Rank(to) > Rungs.Rank(JsonText.Text(e.Json["rung"]))) above.Add(e.Id);
            foreach (var list in new[] { "fields", "values" })
                foreach (var m in (e.Json[list] as JsonArray ?? []).OfType<JsonObject>())
                    if (Rungs.Settle(JsonText.Text(m["rung"]), m["evidence"], false, out _) is { } lower && Rungs.Rank(lower) > Rungs.Rank(JsonText.Text(m["rung"]))) above.Add($"{e.Id}.{JsonText.Text(m["name"])}");
        }
        digest["above"] = above;
        var clashes = live?.Clashes(store) ?? [];
        digest["clashes"] = new JsonArray([.. clashes.Select(c => (JsonNode)new JsonObject
        {
            ["at"] = string.Create(CultureInfo.InvariantCulture, $"{c.Binary}+0x{c.Rva:X}"),
            ["ids"] = new JsonArray([.. c.Ids.OrderBy(i => Rungs.Rank(i.Rung)).Select(i => (JsonNode)i.Id)]),
        })]);
        var refused = lines.Where(l => l.Act == "refused").ToList();
        digest["refused"] = new JsonArray([.. refused.Select(l => (JsonNode)new JsonObject { ["id"] = l.Id, ["why"] = l.Why })]);
        var person = new JsonArray();
        foreach (var c in clashes)
            person.Add(new JsonObject { ["id"] = c.Shown, ["decide"] = string.Create(CultureInfo.InvariantCulture, $"which id the function at {c.Binary}+0x{c.Rva:X} keeps, among {string.Join(", ", c.Ids.Select(i => i.Id))} (`gatekit registry settle`)") });
        foreach (var l in differs)
            person.Add(new JsonObject { ["id"] = l.Id, ["decide"] = $"{StoreWords(l.Store)} names this function {l.Name}: rename it (`gatekit registry rename {l.Id} {l.Name}`), or leave the other name under known_as" });
        foreach (var l in refused)
            person.Add(new JsonObject { ["id"] = l.Id, ["decide"] = "refused, " + l.Why });
        digest["person"] = person;
        return digest;
    }

    static string StoreWords(string? store) => store == "bg3_macos_x64" ? "the macOS build" : $"the Toolkit ({store})";

    static int AddressesIn(JsonNode? node) => node switch
    {
        JsonObject o => o.Sum(p => p.Key == "survey" ? 0 : RegistryCheck.SentenceKeys.Contains(p.Key) && JsonText.Text(p.Value) is { } text ? RegistryCheck.Addresses(text) : AddressesIn(p.Value)),
        JsonArray a => a.Sum(AddressesIn),
        _ => 0,
    };

    static string Pairs(JsonNode? counts) => string.Join(", ", (counts as JsonObject ?? []).Select(p => $"{p.Key} {p.Value}"));

    static string First(JsonNode? ids, int most = 6)
    {
        var all = (ids as JsonArray ?? []).Select(i => JsonText.Text(i)!).ToList();
        return all.Count == 0 ? "" : ": " + string.Join(", ", all.Take(most)) + (all.Count > most ? $", and {all.Count - most} more (--json lists them)" : "");
    }

    /// <summary>The digest as its one page.</summary>
    public static void Print(TextWriter output, JsonObject d)
    {
        var commit = JsonText.Text(d["since"]?["commit"]);
        output.WriteLine("registry digest since " + (commit is not null ? $"{commit[..Math.Min(8, commit.Length)]} ({JsonText.Text(d["since"]?["time"])})" : JsonText.Text(d["since"]?["time"]) ?? "the journal's first line (no commit to compare with)"));
        if (d["added"] is JsonObject added)
        {
            output.WriteLine("added      " + string.Join("; ", added.Select(k => $"{k.Value!["count"]} {k.Key}" + (Pairs(k.Value["by_rung"]) is { Length: > 0 } rungs ? $" ({rungs})" : ""))));
            output.WriteLine($"changed    {d["changed"]} entries; removed {d["removed"]}" + (Pairs(d["rungs_moved"]) is { Length: > 0 } moved ? $"; rungs moved: {moved}" : ""));
        }
        foreach (var act in new[] { "lowered", "raised" })
            output.WriteLine($"{act,-10} {d[act]!["count"]} by the tools" + (Pairs(d[act]!["by_move"]) is { Length: > 0 } moves ? $" ({moves})" : "")
                + (act == "lowered" && Pairs(d[act]!["by_reason"]) is { Length: > 0 } why ? $"; why: {why}" : "") + First(d[act]!["ids"]));
        if (d["above"] is JsonArray { Count: > 0 } above)
            output.WriteLine($"above      {above.Count} rungs stand above what their evidence carries (`gatekit registry tidy` lowers them)" + First(above));
        var folded = d["folded"]!.AsArray();
        output.WriteLine($"folded     {folded.Count} duplicate{(folded.Count == 1 ? "" : "s")}" + (folded.Count == 0 ? "" : ": " + string.Join(", ", folded.Select(f => $"{f!["id"]} into {f["into"]}"))));
        output.WriteLine($"names      {d["names"]!["matched"]} found in a store for {d["names"]!["entries"]} entries" + (Pairs(d["names"]!["by_store"]) is { Length: > 0 } stores ? $" ({stores}; tied by {Pairs(d["names"]!["by_tie"])})" : "")
            + $"; {d["differs"]!.AsArray().Count} under another name in a store");
        output.WriteLine($"addresses  {d["addresses"]!["turned"]} turned into ids in {d["addresses"]!["entries"]} entries; {d["addresses"]!["qualified"]} written with binary and build; {d["addresses"]!["stale"]} stale; "
            + $"{d["addresses"]!["left"]} not placed in {d["addresses"]!["left_in"]} entries");
        output.WriteLine($"repeated   {d["repeated"]} sentences kept once");
        output.WriteLine($"clashes    {d["clashes"]!.AsArray().Count} not settled; refused {d["refused"]!.AsArray().Count}");
        var person = d["person"]!.AsArray();
        output.WriteLine($"a person is needed: {person.Count}");
        foreach (var line in person) output.WriteLine($"  {line!["id"]}: {line["decide"]}");
    }
}
