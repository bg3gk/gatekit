using System.Globalization;
using System.Text.Json.Nodes;
using GateKit.Json;

namespace GateKit.Symbols.Registry;

/// <summary>One claim of the live layer: an entry as its writer means it, where it is, and who wrote it when for which task.</summary>
public sealed record Claim(EntryKind Kind, JsonObject Entry, string? Binary, long? Rva, string? Build, long? Size, string Who, DateTime When, string Task, DateTime? Committed)
{
    public string Id => JsonText.Text(Entry["id"])!;
    public string? Rung => JsonText.Text(Entry["rung"]);

    public JsonObject ToJson()
    {
        var entry = (JsonObject)Entry.DeepClone();
        entry.Remove("schema");
        var o = new JsonObject { ["entry"] = entry };
        if (Binary is not null && Rva is { } rva)
        {
            var at = new JsonObject { ["binary"] = Binary, ["rva"] = RegistryStore.Hex(rva) };
            if (Build is not null) at["build"] = Build;
            if (Size is { } size) at["size"] = size;
            o["at"] = at;
        }
        o["who"] = Who;
        o["when"] = LiveLayer.Time(When);
        o["task"] = Task;
        if (Committed is { } committed) o["committed"] = LiveLayer.Time(committed);
        return o;
    }

    public static Claim FromJson(EntryKind kind, JsonObject o)
    {
        var at = o["at"] as JsonObject;
        return new Claim(kind, (JsonObject)o["entry"]!.DeepClone(), JsonText.Text(at?["binary"]), at is null ? null : RegistryStore.ParseHex(JsonText.Text(at["rva"])!),
            JsonText.Text(at?["build"]), at?["size"]?.GetValue<long>(), JsonText.Text(o["who"])!, LiveLayer.ParseTime(JsonText.Text(o["when"])!), JsonText.Text(o["task"])!,
            JsonText.Text(o["committed"]) is { } c ? LiveLayer.ParseTime(c) : null);
    }
}

/// <summary>Two or more ids claimed for one function (one binary, build and address), not settled.</summary>
public sealed record Clash(string Binary, string? Build, long Rva, IReadOnlyList<(string Id, string? Rung, bool Live)> Ids)
{
    /// <summary>The id of the highest rung, shown first.</summary>
    public string Shown => Ids.OrderBy(i => Rungs.Rank(i.Rung)).First().Id;
}

/// <summary>
/// The registry's live layer, shared by every GateKit process on the machine whatever checkout or
/// branch it runs from: one file an id under <c>&lt;home&gt;/state/registry-live/entries/</c>,
/// changed only under its lock and written whole (a reader sees the old file or the new one), held
/// to <c>state/registry-live/1</c> - a build that meets a newer format refuses it by name. A claim
/// written by one process is read by the next read of any other. The live layer keeps who wrote a
/// claim, when and for which task; <see cref="Commit"/> leaves all of that behind.
/// </summary>
public sealed class LiveLayer(string folder)
{
    public const string Schema = "state/registry-live/1", SettledSchema = "state/registry-settled/1";

    public string Folder { get; } = Path.GetFullPath(folder);

    /// <summary>The live layer of the home (<c>&lt;home&gt;/state/registry-live</c>).</summary>
    public static LiveLayer OfHome() =>
        new(Path.Combine(GateKit.Home.GateKitHome.Current.State.Require(), "registry-live"));

    public static string Time(DateTime utc) => utc.ToUniversalTime().ToString(SchemaSet.TimeFormat, CultureInfo.InvariantCulture);

    public static DateTime ParseTime(string text) =>
        DateTime.ParseExact(text, SchemaSet.TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    string EntryPath(EntryKind kind, string id) =>
        Path.Combine([Folder, "entries", RegistryStore.Folder(kind), .. RegistryId.ToPath(id).Split('/')]);

    string SettledPath(string binary, string? build, long rva) =>
        Path.Combine(Folder, "settled", RegistryId.EscapePart(binary), build ?? "any", RegistryStore.Hex(rva) + ".json");

    /// <summary>
    /// Adds a claim under the id's lock: nothing another process wrote meanwhile is lost. A guess
    /// without what it rests on and what would confirm it is refused, and so is a claim whose entry
    /// does not fit its kind's schema.
    /// </summary>
    public void Note(Claim claim)
    {
        var entry = (JsonObject)claim.Entry.DeepClone();
        if (claim.Rung == "guess" || entry["guess"] is not null)
        {
            var guess = entry["guess"] as JsonObject;
            if (JsonText.Text(guess?["rests_on"]) is not { Length: > 0 } || JsonText.Text(guess?["would_confirm"]) is not { Length: > 0 })
                throw new RegistryException($"{claim.Id}: a guess says what it rests on and what would confirm it (--rests-on, --would-confirm)");
            if (claim.Rung is null) entry["rung"] = "guess";
        }
        entry["schema"] = RegistryStore.Schema(claim.Kind);
        var misfits = SchemaSet.Embedded.Validate(entry, RegistryStore.Schema(claim.Kind));
        if (misfits.Count > 0) throw new RegistryException($"{claim.Id}: " + string.Join("; ", misfits.Select(m => $"{m.Pointer} {m.What}")));
        entry.Remove("schema");
        var path = EntryPath(claim.Kind, claim.Id);
        using var held = JsonLock.Take(path);
        var claims = Read(path, claim.Kind)?.Claims.ToList() ?? [];
        claims.Add(claim with { Entry = entry });
        Write(path, claim.Kind, claim.Id, claims);
    }

    /// <summary>
    /// Takes claims of an id out of the layer under the id's lock: its newest one - the one every
    /// reader sees, so the claim before it is what the id says again - or with <paramref name="all"/>
    /// every one, and then the id's file goes. Only claims of <paramref name="who"/> when it is given.
    /// What a checkout already committed stays there. Returns the claims taken out (none when the
    /// layer holds none of the id, or none of that writer).
    /// </summary>
    public IReadOnlyList<Claim> Withdraw(string id, bool all, string? who = null)
    {
        var entity = RegistryId.EntityOf(id);
        foreach (var kind in Enum.GetValues<EntryKind>())
        {
            var path = EntryPath(kind, entity);
            using var held = JsonLock.Take(path);
            if (Read(path, kind) is not { } found) continue;
            var claims = found.Claims.ToList();
            var mine = claims.Where(c => who is null || c.Who == who).OrderBy(c => c.When).ToList();
            if (mine.Count == 0) return [];
            var gone = all ? mine : [mine[^1]];
            var left = claims.Where(c => !gone.Contains(c)).ToList();
            if (left.Count == 0) File.Delete(path);
            else Write(path, kind, found.Id, left);
            return gone;
        }
        return [];
    }

    static void Write(string path, EntryKind kind, string id, IEnumerable<Claim> claims) =>
        JsonText.Write(path, new JsonObject
        {
            ["schema"] = Schema, ["id"] = id, ["kind"] = RegistryStore.Name(kind),
            ["claims"] = new JsonArray([.. claims.Select(c => (JsonNode)c.ToJson())]),
        }, Schema);

    static (string Id, IReadOnlyList<Claim> Claims)? Read(string path, EntryKind kind) =>
        JsonText.ReadIfExists(path, Schema) is JsonObject o
            ? (JsonText.Text(o["id"])!, (o["claims"] as JsonArray ?? []).OfType<JsonObject>().Select(c => Claim.FromJson(kind, c)).ToList())
            : null;

    /// <summary>The claims of one id, oldest first.</summary>
    public IReadOnlyList<Claim> Of(string id)
    {
        var entity = RegistryId.EntityOf(id);
        foreach (var kind in Enum.GetValues<EntryKind>())
            if (Read(EntryPath(kind, entity), kind) is { } found) return found.Claims;
        return [];
    }

    /// <summary>Every claim of the layer.</summary>
    public IReadOnlyList<Claim> All()
    {
        var list = new List<Claim>();
        foreach (var kind in Enum.GetValues<EntryKind>())
        {
            var folder = Path.Combine(Folder, "entries", RegistryStore.Folder(kind));
            if (!Directory.Exists(folder)) continue;
            foreach (var path in Directory.EnumerateFiles(folder, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                if (Read(path, kind) is { } found) list.AddRange(found.Claims);
        }
        return list;
    }

    /// <summary>The newest claim of each id: what the live layer says of it now.</summary>
    public IReadOnlyList<Claim> Latest() => All().GroupBy(c => c.Id).Select(g => g.OrderBy(c => c.When).Last()).ToList();

    /// <summary>The live claims whose function holds an address of a binary.</summary>
    public IReadOnlyList<Claim> At(string binary, long rva) =>
        Latest().Where(c => c.Binary is not null && string.Equals(c.Binary, binary, StringComparison.OrdinalIgnoreCase)
            && (c.Rva == rva || (c.Size is { } size && rva > c.Rva && rva < c.Rva + size))).ToList();

    // --- clashes ---

    /// <summary>How an address was settled: the id kept, or every id standing (folded code); null when it was not.</summary>
    public (string? Keep, bool Folded)? Settled(string binary, string? build, long rva)
    {
        foreach (var path in new[] { SettledPath(binary, build, rva), SettledPath(binary, null, rva) })
            if (JsonText.ReadIfExists(path, SettledSchema) is JsonObject o)
                return (JsonText.Text(o["keep"]), o["folded"] is not null);
        return null;
    }

    /// <summary>Settles an address: <paramref name="keep"/> stays and the other claims there are not committed, or (folded) every id stands.</summary>
    public void Settle(string binary, string? build, long rva, string? keep, bool folded, string who, string task)
    {
        if ((keep is null) == !folded) throw new RegistryException("settle keeps one id (--keep) or declares the ids folded (--folded)");
        var o = new JsonObject { ["schema"] = SettledSchema, ["binary"] = binary };
        if (build is not null) o["build"] = build;
        o["rva"] = RegistryStore.Hex(rva);
        if (keep is not null) o["keep"] = keep;
        if (folded) o["folded"] = true;
        o["who"] = who;
        o["when"] = Time(DateTime.UtcNow);
        o["task"] = task;
        var path = SettledPath(binary, build, rva);
        using var held = JsonLock.Take(path);
        JsonText.Write(path, o, SettledSchema);
    }

    /// <summary>
    /// Two different ids for one function - among the live claims, or a live claim against the
    /// committed registry's table - that are not settled. Both are kept; the higher rung is shown.
    /// </summary>
    public IReadOnlyList<Clash> Clashes(RegistryStore? store)
    {
        var claims = Latest().Where(c => c.Binary is not null && c.Rva is not null).ToList();
        var clashes = new List<Clash>();
        foreach (var group in claims.GroupBy(c => (Binary: c.Binary!.ToLowerInvariant(), c.Build, Rva: c.Rva!.Value)))
        {
            var ids = group.Select(c => (c.Id, c.Rung, Live: true)).ToList();
            if (store is not null)
                foreach (var (_, row) in store.At(group.First().Binary!, group.Key.Rva, group.Key.Build).Where(r => r.Row.Rva == group.Key.Rva))
                    if (!ids.Any(i => i.Id == row.Id))
                        ids.Add((row.Id, store.Get(row.Id) is { } e ? JsonText.Text(e.Json["rung"]) : null, false));
            if (ids.Select(i => i.Id).Distinct().Count() < 2) continue;
            var settled = Settled(group.First().Binary!, group.Key.Build, group.Key.Rva);
            if (settled is { Folded: true } || settled?.Keep is not null) continue;
            clashes.Add(new Clash(group.First().Binary!, group.Key.Build, group.Key.Rva, ids));
        }
        return clashes;
    }

    // --- commit ---

    /// <summary>
    /// Writes the live layer into a checkout's registry in its one canonical form: for every id not
    /// yet committed there, the newest claim's entry merged over the entry that exists, and its place
    /// in the build's table. Who wrote it, when and for which task stay here. An id in a clash that is
    /// not settled is not committed (the rest are), nor an id a settlement dropped, nor an entry the
    /// check of sentences refuses. Returns the ids committed.
    /// </summary>
    public IReadOnlyList<string> Commit(RegistryStore store, TextWriter output)
    {
        var clashes = Clashes(store);
        var blocked = clashes.SelectMany(c => c.Ids.Where(i => i.Live).Select(i => i.Id)).ToHashSet(StringComparer.Ordinal);
        var committed = new List<string>();
        // what a checkout already holds is not written again: the layer is shared by every checkout, each commits for itself
        foreach (var claim in Latest().OrderBy(c => c.Id, StringComparer.Ordinal))
        {
            if (blocked.Contains(claim.Id))
            {
                output.WriteLine($"  held: {claim.Id} is in a clash not settled (`registry clashes`, `registry settle`)");
                continue;
            }
            if (claim.Binary is not null && claim.Rva is { } at && Settled(claim.Binary, claim.Build, at) is { Keep: { } keep } && keep != claim.Id)
            {
                output.WriteLine($"  dropped: {claim.Id} - the address was settled for {keep}");
                continue;
            }
            var existing = store.Get(claim.Id);
            var entry = existing is not null ? (JsonObject)existing.Json.DeepClone() : new JsonObject();
            entry.Remove("schema");
            foreach (var (key, value) in claim.Entry) entry[key] = value?.DeepClone();
            var placed = claim.Binary is null || claim.Rva is null || claim.Build is null
                || store.Table(claim.Build, claim.Binary).Any(r => r.Id == claim.Id && r.Rva == claim.Rva);
            if (existing is not null && placed)
            {
                var was = (JsonObject)existing.Json.DeepClone();
                was.Remove("schema");
                if (JsonNode.DeepEquals(was, entry)) continue;
            }
            var problems = RegistryCheck.Refusals(entry, existing?.Json);
            if (problems.Count > 0)
            {
                output.WriteLine($"  refused: {claim.Id}: " + string.Join("; ", problems));
                continue;
            }
            store.Write(claim.Kind, entry, create: false);
            if (claim.Binary is not null && claim.Rva is { } rva && claim.Build is not null)
                store.Place(claim.Build, claim.Binary, new BuildRow(claim.Id, rva, claim.Size, null));
            MarkCommitted(claim);
            committed.Add(claim.Id);
            output.WriteLine($"  committed: {claim.Id} -> {store.Relative(store.PathOf(claim.Kind, claim.Id))}");
        }
        return committed;
    }

    void MarkCommitted(Claim claim)
    {
        var path = EntryPath(claim.Kind, claim.Id);
        using var held = JsonLock.Take(path);
        var claims = Read(path, claim.Kind)?.Claims.ToList() ?? [];
        var now = DateTime.UtcNow;
        Write(path, claim.Kind, claim.Id, claims.Select(c => c.Committed is null && c.When <= claim.When ? c with { Committed = now } : c));
    }
}
