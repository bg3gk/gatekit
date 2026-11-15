using System.CommandLine;
using System.Globalization;
using System.Text.Json.Nodes;
using GateKit.Json;
using GateKit.Symbols.Decomp;

namespace GateKit.Symbols.Registry;

/// <summary>What one note says: the options of `registry note` and the MCP tool bg3_registry_note.</summary>
public sealed record NoteRequest(string Id)
{
    public string Kind { get; init; } = "function";
    public string? At { get; init; }
    public string? Rung { get; init; }
    public string? Does { get; init; }
    public string? About { get; init; }
    public IReadOnlyList<string> Evidence { get; init; } = [];
    public IReadOnlyList<string> Tests { get; init; } = [];
    public string? RestsOn { get; init; }
    public string? WouldConfirm { get; init; }
    public IReadOnlyList<string> Ask { get; init; } = [];
    public string AskAbout { get; init; } = "reach";
    public IReadOnlyList<string> Fields { get; init; } = [];
    public string? Who { get; init; }
    public string? Task { get; init; }
}

/// <summary>
/// The commands over the live layer: `registry note`, `withdraw`, `at`, `show`, `clashes`, `settle`, `commit`.
/// The same work is reached by the MCP tools bg3_registry_note, bg3_registry_withdraw, bg3_registry_at and bg3_registry_show.
/// </summary>
public static class LiveCommands
{
    /// <summary>Who writes, as the live layer records it: the option, else GATEKIT_WHO, else the user's name.</summary>
    public static string Who(string? given) => given ?? (Environment.GetEnvironmentVariable("GATEKIT_WHO") is { Length: > 0 } w ? w : Environment.UserName);

    /// <summary>For which task: the option, else GATEKIT_TASK, else "unsaid".</summary>
    public static string Task(string? given) => given ?? (Environment.GetEnvironmentVariable("GATEKIT_TASK") is { Length: > 0 } t ? t : "unsaid");

    internal static void Add(Command root, Func<ParseResult, Func<DecompSession, int>, int> invoke)
    {
        {
            var id = new Argument<string>("id") { Description = "the registry id (esv::RollSystem::RollDice); a thing with no name yet: <scope>::unnamed_<token> (`registry fingerprint` gives the token)" };
            var kind = new Option<string>("--kind") { Description = "function (default), type or global", DefaultValueFactory = _ => "function" };
            var at = new Option<string?>("--at") { Description = "binary+0xRVA of the function or global (anywhere inside a function)" };
            var rung = new Option<string?>("--rung") { Description = "game, toolkit, confirmed, inferred, extender, guess or unknown" };
            var does = new Option<string?>("--does") { Description = "our sentence: what the function does" };
            var about = new Option<string?>("--about") { Description = "our sentence: what the type or global is" };
            var evidence = new Option<string[]>("--evidence") { Description = "kind:sentence - watch, read, pairing, game-name, toolkit-name, extender-name, text", AllowMultipleArgumentsPerToken = false };
            var test = new Option<string[]>("--test") { Description = "a test that proves it: its path under tests/ without the extension, then case and step" };
            var restsOn = new Option<string?>("--rests-on") { Description = "for a guess: what it rests on" };
            var wouldConfirm = new Option<string?>("--would-confirm") { Description = "for a guess: what would confirm it" };
            var ask = new Option<string[]>("--ask") { Description = "an open question, above all how the game reaches the thing" };
            var askAbout = new Option<string>("--ask-about") { Description = "what the questions are about: reach (default), conditions, meaning, other", DefaultValueFactory = _ => "reach" };
            var field = new Option<string[]>("--field") { Description = "for a type: offset:name[:type] (0x70:NumRolls:uint64), on the note's rung" };
            var who = new Option<string?>("--who") { Description = "who writes (default GATEKIT_WHO, else the user's name); kept in the live layer only" };
            var task = new Option<string?>("--task") { Description = "for which task (default GATEKIT_TASK); kept in the live layer only" };
            var c = new Command("note", "write an entry or a guess into the live layer every process on this machine reads at once; a guess needs --rests-on and --would-confirm")
                { id, kind, at, rung, does, about, evidence, test, restsOn, wouldConfirm, ask, askAbout, field, who, task };
            c.SetAction(p => invoke(p, s =>
            {
                var claim = Note(s, new NoteRequest(p.GetValue(id)!)
                {
                    Kind = p.GetValue(kind)!, At = p.GetValue(at), Rung = p.GetValue(rung), Does = p.GetValue(does), About = p.GetValue(about),
                    Evidence = p.GetValue(evidence) ?? [], Tests = p.GetValue(test) ?? [], RestsOn = p.GetValue(restsOn), WouldConfirm = p.GetValue(wouldConfirm),
                    Ask = p.GetValue(ask) ?? [], AskAbout = p.GetValue(askAbout)!, Fields = p.GetValue(field) ?? [], Who = p.GetValue(who), Task = p.GetValue(task),
                }, LiveLayer.OfHome());
                s.Out.WriteLine($"noted {claim.Id} [{claim.Rung ?? "no rung"}]" + (claim.Binary is null ? "" : string.Create(CultureInfo.InvariantCulture, $" at {claim.Binary}+0x{claim.Rva:X}")) + " (live)");
                return 0;
            }));
            root.Subcommands.Add(c);
        }
        {
            var address = new Argument<string>("address") { Description = "binary+0xRVA, anywhere inside a function" };
            var coverage = new Option<string?>("--coverage");
            var asJson = new Option<bool>("--json");
            var c = new Command("at", "what is known at an address: the committed registry's ids and the live layer's claims, marked live, with the databases' names") { address, coverage, asJson };
            c.SetAction(p => invoke(p, s => Print(s, AtJson(s, p.GetValue(address)!, RegistryStore.Find(p.GetValue(coverage)), LiveLayer.OfHome()), p.GetValue(asJson))));
            root.Subcommands.Add(c);
        }
        {
            var id = new Argument<string>("id") { Description = "a registry id, or a name another source gives the thing (Script Extender's, the Toolkit's)" };
            var coverage = new Option<string?>("--coverage");
            var asJson = new Option<bool>("--json");
            var c = new Command("show", "what is known of an id: the committed entry and its places, and the live layer's claims, marked live") { id, coverage, asJson };
            c.SetAction(p => invoke(p, s => Print(s, ShowJson(p.GetValue(id)!, RegistryStore.Find(p.GetValue(coverage)), LiveLayer.OfHome()), p.GetValue(asJson))));
            root.Subcommands.Add(c);
        }
        {
            var file = new Argument<string>("file") { Description = "a JSON data file that names registry ids under keys \"id\", at any depth" };
            var coverage = new Option<string?>("--coverage");
            var check = new Option<bool>("--check") { Description = "write nothing: exit 1 when the file's places are not what the registry holds" };
            var c = new Command("places", "write into a data file, under \"places\", where the registry's build tables put every function id the file names: per build and binary { id: [rva, size] } - so that code which meets raw addresses in the game can name them without a hand-written table") { file, coverage, check };
            c.SetAction(p => invoke(p, s =>
            {
                var store = RegistryStore.Find(p.GetValue(coverage)) ?? throw new RegistryException("no registry found");
                var path = p.GetValue(file)!;
                var root = GateKit.Json.JsonText.Read(path).AsObject();
                var (places, missing, ids) = PlacesOf(store, root);
                foreach (var id in missing) s.Out.WriteLine($"no place: {id}");
                var before = root["places"]?.ToString();
                if (p.GetValue(check))
                {
                    var same = before == places.ToString();
                    s.Out.WriteLine(same ? $"{path}: its places are the registry's ({ids} ids)" : $"{path}: its places are not the registry's - run `gatekit registry places {path}`");
                    return same ? 0 : 1;
                }
                root["places"] = places;
                GateKit.Json.JsonText.Write(path, root);
                s.Out.WriteLine($"{path}: {ids} ids, {missing.Count} with no place");
                return 0;
            }));
            root.Subcommands.Add(c);
        }
        {
            var coverage = new Option<string?>("--coverage");
            var c = new Command("clashes", "two different ids claimed for one function and not settled: both kept, the higher rung shown first") { coverage };
            c.SetAction(p => invoke(p, s =>
            {
                var clashes = LiveLayer.OfHome().Clashes(RegistryStore.Find(p.GetValue(coverage)));
                foreach (var clash in clashes)
                {
                    s.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{clash.Binary}+0x{clash.Rva:X} (build {clash.Build ?? "?"}): {clash.Shown} shown"));
                    foreach (var (id, rung, live) in clash.Ids.OrderBy(i => Rungs.Rank(i.Rung)))
                        s.Out.WriteLine($"  {id} [{rung ?? "no rung"}]{(live ? " (live)" : " (committed)")}");
                }
                s.Out.WriteLine($"{clashes.Count} clash{(clashes.Count == 1 ? "" : "es")}");
                return 0;
            }));
            root.Subcommands.Add(c);
        }
        {
            var address = new Argument<string>("address") { Description = "binary+0xRVA of the function the clash is at" };
            var keep = new Option<string?>("--keep") { Description = "the id that stays" };
            var folded = new Option<bool>("--folded") { Description = "every id stands: the linker gave identical code one address" };
            var build = new Option<string?>("--build");
            var who = new Option<string?>("--who");
            var task = new Option<string?>("--task");
            var c = new Command("settle", "settle a clash: keep one id, or declare the ids folded") { address, keep, folded, build, who, task };
            c.SetAction(p => invoke(p, s =>
            {
                var (binary, rva) = Address(p.GetValue(address)!);
                LiveLayer.OfHome().Settle(binary, p.GetValue(build), rva, p.GetValue(keep), p.GetValue(folded), Who(p.GetValue(who)), Task(p.GetValue(task)));
                s.Out.WriteLine($"settled {p.GetValue(address)}");
                return 0;
            }));
            root.Subcommands.Add(c);
        }
        {
            var id = new Argument<string>("id") { Description = "the registry id whose claim is taken back" };
            var all = new Option<bool>("--all") { Description = "every claim of the id, not only its newest" };
            var who = new Option<string?>("--who") { Description = "only claims this writer made" };
            var c = new Command("withdraw", "take a claim back out of the live layer: the id's newest (the one before it is what the id says again), "
                + "or every one with --all; gone for every process at once; what a checkout committed stays") { id, all, who };
            c.SetAction(p => invoke(p, s =>
            {
                var answer = WithdrawJson(p.GetValue(id)!, p.GetValue(all), p.GetValue(who), LiveLayer.OfHome());
                foreach (var gone in answer["withdrawn"]!.AsArray())
                    s.Out.WriteLine($"withdrew {gone!["id"]} [{gone["rung"] ?? "no rung"}] of {gone["when"]} by {gone["who"]} ({gone["task"]})");
                s.Out.WriteLine(answer["now"] is JsonObject now
                    ? $"{now["id"]} now says the claim of {now["when"]} by {now["who"]} [{now["rung"] ?? "no rung"}] (live)"
                    : $"no claim of {answer["id"]} is left in the live layer");
                return 0;
            }));
            root.Subcommands.Add(c);
        }
        {
            var coverage = new Option<string?>("--coverage");
            var c = new Command("commit", "write the live layer into this checkout's registry in its canonical form; who, when and for which task stay behind; a clash not settled holds only its own ids") { coverage };
            c.SetAction(p => invoke(p, s =>
            {
                var done = LiveLayer.OfHome().Commit(RegistryCommands.Store(p.GetValue(coverage)), s.Out);
                s.Out.WriteLine($"{done.Count} entr{(done.Count == 1 ? "y" : "ies")} committed");
                // A claim carries the size of the part of the function its address is in; the build
                // table's row is the whole function's size and the hash of its body, which the
                // stamp measures. Without the databases the rows stay as the claims gave them.
                if (done.Count > 0)
                {
                    try { RegistryCommands.StampCommand(s, p.GetValue(coverage)); }
                    catch (SymbolsException e) { s.Out.WriteLine("not stamped (run `gatekit registry stamp` where the databases are): " + e.Message); }
                }
                return 0;
            }));
            root.Subcommands.Add(c);
        }
    }

    static (string Binary, long Rva) Address(string text)
    {
        var m = CodeCommand.Address.Match(text);
        if (!m.Success) throw new RegistryException($"{text}: an address is binary+0xRVA");
        var rva = RegistryStore.ParseHex(m.Groups[2].Value);
        foreach (var imageBase in new long[] { 0x180000000, 0x140000000 })
            if (rva >= imageBase) { rva -= imageBase; break; }
        return (m.Groups[1].Value, rva);
    }

    static int Print(DecompSession s, JsonObject answer, bool json)
    {
        if (json) { s.Out.WriteLine(JsonText.Format(answer)); return 0; }
        Outline.Print(s.Out, answer, 200);
        return 0;
    }

    /// <summary>A note written with the home's databases at hand (for the MCP tools).</summary>
    public static Claim Note(NoteRequest request, LiveLayer live, RegistryStore? store, TextWriter error)
    {
        using var s = new DecompSession(DecompSettings.FromEnvironment(), TextWriter.Null, error);
        return Note(s, request, live, store);
    }

    /// <summary>Where every function of a binary begins, as the pseudo-code database has them: the
    /// thunks and the functions shorter than <paramref name="minimum"/> bytes left out.</summary>
    public static List<long> FunctionStarts(string binary, long minimum, TextWriter error)
    {
        using var s = new DecompSession(DecompSettings.FromEnvironment(), TextWriter.Null, error);
        return [.. s.Db.Query("SELECT f.rva FROM functions f JOIN binaries b ON b.id = f.binary_id WHERE b.name = ? COLLATE NOCASE AND f.thunk = 0 AND f.size >= ? ORDER BY f.rva",
            binary, minimum).Select(r => Sql.Long(r[0])!.Value)];
    }

    /// <summary>Names many addresses with the databases opened once: for each, the id that names the
    /// function holding it (committed first, else live), the function's start, and the names the
    /// databases give it.</summary>
    public static List<(string Address, string? Id, long? Start, string? Other)> NameMany(IEnumerable<string> addresses, RegistryStore? store, LiveLayer live, TextWriter error)
    {
        using var s = new DecompSession(DecompSettings.FromEnvironment(), TextWriter.Null, error);
        var named = new List<(string, string?, long?, string?)>();
        foreach (var address in addresses)
        {
            try
            {
                var at = AtJson(s, address, store, live);
                var id = (at["committed"] as JsonArray)?.FirstOrDefault()?["id"]?.GetValue<string>() ?? (at["live"] as JsonArray)?.FirstOrDefault()?["id"]?.GetValue<string>();
                var function = at["function"]?.GetValue<string>();
                long? start = function is null ? null : RegistryStore.ParseHex(function[(function.IndexOf("+0x", StringComparison.Ordinal) + 1)..]);
                named.Add((address, id, start, at["symbols"]?.GetValue<string>() ?? at["decompiler"]?.GetValue<string>()));
            }
            catch (Exception e) when (e is SymbolsException or RegistryException) { named.Add((address, null, null, null)); }
        }
        return named;
    }

    /// <summary>A function seen at work that no id names yet, written into the live layer as
    /// <c>unplaced::unnamed_&lt;token&gt;</c> (the token from its fingerprint) with what was seen and
    /// what is asked of it; null when an id, committed or live, already names the function.</summary>
    public static Claim? NoteUnnamed(string address, IReadOnlyList<string> evidence, IReadOnlyList<string> ask, LiveLayer live, RegistryStore? store, TextWriter error)
    {
        using var s = new DecompSession(DecompSettings.FromEnvironment(), TextWriter.Null, error);
        var at = AtJson(s, address, store, live);
        if ((at["committed"] as JsonArray)?.Count > 0 || (at["live"] as JsonArray)?.Count > 0) return null;
        var (binary, start) = Address(address);
        if (s.DbIfExists is not null && NameAtCommand.Resolve(s, address) is { } named) (binary, start) = (named.Binary, named.Start);
        var token = RegistryCommands.Reader(s, null).Read(binary, start).Token();
        return Note(s, new NoteRequest($"unplaced::{RegistryId.UnnamedPrefix}{token}")
        {
            At = string.Create(CultureInfo.InvariantCulture, $"{binary}+0x{start:X}"), Rung = "unknown",
            Evidence = evidence, Ask = ask, AskAbout = "meaning",
        }, live, store);
    }

    /// <summary>What is known at an address, with the home's databases at hand (for the MCP tools).</summary>
    public static JsonObject AtJson(string address, RegistryStore? store, LiveLayer live, TextWriter error)
    {
        using var s = new DecompSession(DecompSettings.FromEnvironment(), TextWriter.Null, error);
        return AtJson(s, address, store, live);
    }

    /// <summary>Writes a note into the live layer: the newest claim of the id (else the committed entry) with what the request says over it.</summary>
    internal static Claim Note(DecompSession s, NoteRequest request, LiveLayer live, RegistryStore? store = null)
    {
        var kind = RegistryStore.ParseKind(request.Kind);
        var id = request.Id;
        if (RegistryId.Parse(id).Member is not null) throw new RegistryException($"{id} is a member: note its entity, with --field for a type's field");
        store ??= RegistryStore.Find(null);
        var basis = live.Of(id).LastOrDefault()?.Entry is { } latest ? (JsonObject)latest.DeepClone()
            : store?.Get(id) is { } committed ? (JsonObject)committed.Json.DeepClone() : new JsonObject { ["id"] = id };
        basis.Remove("schema");
        basis["id"] = id;
        if (kind == EntryKind.Type && basis["kind"] is null) basis["kind"] = "struct";
        if (kind == EntryKind.Global && basis["kind"] is null) basis["kind"] = "variable";
        if (request.Rung is not null)
        {
            if (!Rungs.All.Contains(request.Rung)) throw new RegistryException($"--rung is one of {string.Join(", ", Rungs.All)}");
            basis["rung"] = request.Rung;
        }
        if (request.RestsOn is not null || request.WouldConfirm is not null)
        {
            basis["guess"] = new JsonObject { ["rests_on"] = request.RestsOn ?? "", ["would_confirm"] = request.WouldConfirm ?? "" };
            basis["rung"] ??= "guess";
        }
        if (request.Does is not null) { basis["does"] = request.Does; basis["state"] = "described"; }
        if (request.About is not null) { basis["about"] = request.About; basis["state"] = "described"; }
        var evidence = basis["evidence"] as JsonArray ?? [];
        foreach (var item in request.Evidence)
        {
            var colon = item.IndexOf(':');
            if (colon <= 0) throw new RegistryException($"{item}: evidence is kind:sentence (watch:the write of Hp came from it)");
            var (k, says) = (item[..colon], item[(colon + 1)..].Trim());
            if (k == "test") throw new RegistryException("a test is given with --test");
            evidence.Add(new JsonObject { ["kind"] = k, ["says"] = says });
        }
        foreach (var test in request.Tests) evidence.Add(new JsonObject { ["kind"] = "test", ["test"] = test.StartsWith("test:", StringComparison.Ordinal) ? test[5..] : test });
        if (evidence.Count > 0) basis["evidence"] = evidence.DeepClone();
        var questions = basis["questions"] as JsonArray ?? [];
        foreach (var question in request.Ask) questions.Add(new JsonObject { ["ask"] = question, ["about"] = request.AskAbout, ["state"] = "open" });
        if (questions.Count > 0) basis["questions"] = questions.DeepClone();
        if (request.Fields.Count > 0)
        {
            if (kind != EntryKind.Type) throw new RegistryException("--field names a field of a type (--kind type)");
            var fields = (basis["fields"] as JsonArray ?? []).OfType<JsonObject>().Select(f => (JsonObject)f.DeepClone()).ToList();
            foreach (var text in request.Fields)
            {
                var parts = text.Split(':', 3);
                if (parts.Length < 2) throw new RegistryException($"{text}: a field is offset:name[:type]");
                var offset = RegistryStore.ParseHex(parts[0]);
                fields.RemoveAll(f => f["offset"]?.GetValue<long>() == offset && (string?)f["name"] == parts[1]);
                var f = new JsonObject { ["offset"] = offset, ["name"] = parts[1] };
                if (parts.Length == 3) f["type"] = parts[2];
                if (JsonText.Text(basis["rung"]) is { } fieldRung) f["rung"] = fieldRung;
                if (evidence.Count > 0) f["evidence"] = evidence.DeepClone();
                fields.Add(f);
            }
            basis["fields"] = new JsonArray([.. fields.OrderBy(f => f["offset"]!.GetValue<long>())]);
        }
        string? binary = null, build = null;
        long? rva = null, size = null;
        if (request.At is not null)
        {
            var (b, r) = Address(request.At);
            binary = b;
            rva = r;
            if (kind == EntryKind.Function && s.DbIfExists is not null && NameAtCommand.Resolve(s, request.At) is { } named)
            {
                binary = named.Binary;
                rva = named.Start;
                var dll = s.Symbols?.Scalar("SELECT id FROM dlls WHERE name = ? COLLATE NOCASE", binary);
                if (dll is not null) size = Sql.Long(s.Symbols!.Scalar("SELECT size FROM functions WHERE dll_id = ? AND rva = ?", dll, rva));
            }
            build = FunctionNames.BuildAt(s, binary);
        }
        var claim = new Claim(kind, basis, binary, rva, build, size, Who(request.Who), DateTime.UtcNow, Task(request.Task), null);
        live.Note(claim);
        return claim;
    }

    /// <summary>What is known at an address, as JSON: the databases' names, the committed ids there, the live claims.</summary>
    internal static JsonObject AtJson(DecompSession s, string address, RegistryStore? store, LiveLayer live)
    {
        var (binary, rva) = Address(address);
        var answer = new JsonObject { ["address"] = string.Create(CultureInfo.InvariantCulture, $"{binary}+0x{rva:X}") };
        long start = rva;
        if (s.DbIfExists is not null && NameAtCommand.Resolve(s, address) is { } named)
        {
            binary = named.Binary;
            start = named.Start;
            answer["function"] = string.Create(CultureInfo.InvariantCulture, $"{named.Binary}+0x{named.Start:X}");
            if (named.DecompName is not null) answer["decompiler"] = named.DecompName;
            if (named.SymbolName is not null) answer["symbols"] = named.SymbolName;
        }
        var build = FunctionNames.BuildAt(s, binary);
        var committed = new JsonArray();
        if (store is not null)
            foreach (var (b, row) in store.At(binary, start, store.BuildFor(binary, build)).Concat(start != rva ? store.At(binary, rva, store.BuildFor(binary, build)) : []).DistinctBy(x => x.Row.Id))
                committed.Add(Summary(store.Get(row.Id)?.Json, row.Id, b, live: false));
        answer["committed"] = committed;
        var claims = new JsonArray();
        foreach (var claim in live.At(binary, start).Concat(start != rva ? live.At(binary, rva) : []).DistinctBy(c => c.Id))
            claims.Add(Summary(claim.Entry, claim.Id, claim.Build, live: true, claim));
        answer["live"] = claims;
        return answer;
    }

    /// <summary>Withdraws claims of an id (<see cref="LiveLayer.Withdraw"/>): <c>{id, withdrawn: [...], now?}</c>,
    /// <c>now</c> the id's newest claim left. Refused when the layer holds no claim of it (of that writer).</summary>
    public static JsonObject WithdrawJson(string id, bool all, string? who, LiveLayer live)
    {
        if (!RegistryId.IsValid(id)) throw new RegistryException($"{id}: not a registry id");
        var gone = live.Withdraw(id, all, who);
        if (gone.Count == 0)
            throw new RegistryException($"{id}: the live layer holds no claim of it{(who is null ? "" : $" by {who}")}; nothing was withdrawn");
        var answer = new JsonObject
        {
            ["id"] = id,
            ["withdrawn"] = new JsonArray([.. gone.Select(c => (JsonNode)Summary(c.Entry, c.Id, c.Build, true, c))]),
        };
        if (live.Of(id).OrderBy(c => c.When).LastOrDefault() is { } now) answer["now"] = Summary(now.Entry, now.Id, now.Build, true, now);
        return answer;
    }

    /// <summary>Where the build tables put every function id a data file names (every string under a
    /// key "id", at any depth, the file's own "places" left aside): the places as the file should
    /// hold them, the ids with no place, and how many ids there are.</summary>
    public static (JsonObject Places, List<string> Missing, int Ids) PlacesOf(RegistryStore store, JsonObject root)
    {
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        void Walk(JsonNode? node)
        {
            if (node is JsonObject o)
                foreach (var (key, value) in o)
                {
                    if (key == "places") continue;
                    if (key == "id" && GateKit.Json.JsonText.Text(value) is { } id) ids.Add(id);
                    else Walk(value);
                }
            else if (node is JsonArray a) foreach (var item in a) Walk(item);
        }
        Walk(root);
        var places = new JsonObject();
        var missing = new List<string>();
        foreach (var id in ids)
        {
            var found = store.Places(id);
            if (found.Count == 0) missing.Add(id);
            foreach (var (build, binary, row) in found.OrderBy(f => f.Build, StringComparer.Ordinal).ThenBy(f => f.Binary, StringComparer.Ordinal))
            {
                var ofBuild = places[build] as JsonObject ?? (JsonObject)(places[build] = new JsonObject());
                var ofBinary = ofBuild[binary] as JsonObject ?? (JsonObject)(ofBuild[binary] = new JsonObject());
                ofBinary[id] = new JsonArray(row.Rva, row.Size ?? 0);
            }
        }
        return (places, missing, ids.Count);
    }

    /// <summary>What is known of an id, as JSON: the committed entry and its places, and the live claims.</summary>
    public static JsonObject ShowJson(string text, RegistryStore? store, LiveLayer live)
    {
        var found = store?.Lookup(text) ?? [];
        var id = found.Count == 1 ? found[0].Id : text;
        if (found.Count > 1) throw new RegistryException($"{text} is a name of {found.Count} entries: " + string.Join(", ", found.Select(f => f.Id)));
        var answer = new JsonObject { ["id"] = id };
        if (found.Count == 1)
        {
            var entry = (JsonObject)found[0].Json.DeepClone();
            entry.Remove("schema");
            answer["committed"] = entry;
            answer["file"] = store!.Relative(found[0].Path);
            answer["places"] = new JsonArray([.. store.Places(found[0].Id).Select(p => (JsonNode)new JsonObject
            {
                ["build"] = p.Build, ["binary"] = p.Binary, ["rva"] = RegistryStore.Hex(p.Row.Rva),
            })]);
        }
        var claims = live.Of(RegistryId.IsValid(id) ? id : text);
        answer["live"] = new JsonArray([.. claims.Select(c => (JsonNode)Summary(c.Entry, c.Id, c.Build, true, c))]);
        if (found.Count == 0 && claims.Count == 0) throw new RegistryException($"{text}: nothing is known of it, committed or live");
        return answer;
    }

    static JsonObject Summary(JsonObject? entry, string id, string? build, bool live, Claim? claim = null)
    {
        var o = new JsonObject { ["id"] = id, ["live"] = live };
        if (JsonText.Text(entry?["rung"]) is { } rung) o["rung"] = rung;
        if ((JsonText.Text(entry?["does"]) ?? JsonText.Text(entry?["about"])) is { } sentence) o["says"] = sentence;
        if (entry?["guess"] is JsonObject guess) o["guess"] = guess.DeepClone();
        if (build is not null) o["build"] = build;
        if (claim is not null)
        {
            o["who"] = claim.Who;
            o["when"] = LiveLayer.Time(claim.When);
            o["task"] = claim.Task;
            if (claim.Binary is not null) o["at"] = string.Create(CultureInfo.InvariantCulture, $"{claim.Binary}+0x{claim.Rva:X}");
        }
        return o;
    }
}
