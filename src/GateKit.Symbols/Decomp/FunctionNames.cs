using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GateKit.Symbols.Decomp;

/// <summary>
/// The names given to functions of the game's code by evidence (docs/contracts/cli.md, "decomp
/// name"): one row a function in <c>functions.jsonl</c> of the coverage record, keyed by the binary
/// and the function's start. A row holds an address, our own name and sentence, and what backs
/// them - never code of the game. The symbol database names what a fact settles (an export, a
/// Toolkit pair, a Script Extender label); this ledger names what an instrument showed: the
/// instruction a memory watch caught, the function <c>decomp at</c> put it in, what was read there.
/// </summary>
internal static class FunctionNames
{
    public const string FileName = "functions.jsonl";

    /// <summary>Where a name comes from, as for a field's (gatekit names): game and toolkit are
    /// facts, inferred is reasoning from the game's code and needs its evidence, extender is Script
    /// Extender's own name, unknown says so.</summary>
    public static readonly IReadOnlyList<string> Sources = ["game", "toolkit", "confirmed", "inferred", "extender", "unknown"];

    public sealed record Row(string Binary, long Rva, string Name, string Source, string Evidence, string? Does, IReadOnlyList<string> Legacy)
    {
        /// <summary>The function's own variables and parameters by the decompiler's name: param_1 -> stream.</summary>
        public IReadOnlyDictionary<string, string> Vars { get; init; } = new SortedDictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Which structure a variable or parameter points at: stream -> esv::roll::stream::Stream.</summary>
        public IReadOnlyDictionary<string, string> Structs { get; init; } = new SortedDictionary<string, string>(StringComparer.Ordinal);

        /// <summary>The Steam build of the game the address is in, once stamped.</summary>
        public string? Build { get; init; }

        /// <summary>The facts that find the function again in another build, once stamped.</summary>
        public JsonObject? Fingerprint { get; init; }

        /// <summary>Its size and the hash of its masked body in <see cref="Build"/>, once stamped.</summary>
        public long? Size { get; init; }
        public string? Body { get; init; }

        /// <summary>The row comes from the live layer and is in no checkout's registry yet.</summary>
        public bool Live { get; init; }

        /// <summary>A guess's grounds (what it rests on, what would confirm it), when the row is one.</summary>
        public string? Guess { get; init; }
    }

    /// <summary>
    /// The rows with the live layer's functions beside them: every live claim of a function is a row
    /// of its own, marked live - a guess on a function that already has an id too. The live layer's
    /// claim of an id it has not committed yet takes the place of the registry's row of that id.
    /// Without a live layer the rows stay as they are.
    /// </summary>
    public static List<Row> WithLive(List<Row> rows, Registry.LiveLayer? live)
    {
        if (live is null) return rows;
        IReadOnlyList<Registry.Claim> claims;
        try { claims = live.Latest(); }
        catch (Exception e) when (e is IOException or GateKit.Json.JsonProblemException or UnauthorizedAccessException) { return rows; }
        var result = new List<Row>(rows);
        foreach (var claim in claims.Where(c => c.Kind == Registry.EntryKind.Function && c.Binary is not null && c.Rva is not null))
        {
            var row = new Row(claim.Binary!, claim.Rva!.Value, Registry.RegistryId.Unplace(claim.Id), claim.Rung ?? "unknown",
                Registry.RegistryForms.EvidenceText(claim.Entry["evidence"]), GateKit.Json.JsonText.Text(claim.Entry["does"]), [])
            { Build = claim.Build, Live = true, Guess = GuessText(claim.Entry["guess"]) };
            var same = result.FindIndex(r => r.Rva == row.Rva && r.Name == row.Name && string.Equals(r.Binary, row.Binary, StringComparison.OrdinalIgnoreCase));
            if (same < 0) result.Add(row);
            else if (claim.Committed is null)
                result[same] = result[same] with { Source = row.Source, Evidence = row.Evidence, Does = row.Does ?? result[same].Does, Live = true, Guess = row.Guess };
        }
        return result;
    }

    /// <summary>A guess as one line: what it rests on, and what would confirm it.</summary>
    static string? GuessText(JsonNode? guess) => guess is JsonObject g
        ? $"rests on: {GateKit.Json.JsonText.Text(g["rests_on"])}  would be confirmed by: {GateKit.Json.JsonText.Text(g["would_confirm"])}"
        : null;

    /// <summary>Whether a row's rung names a function by evidence: every rung but a guess and unknown.</summary>
    public static bool ByEvidence(Row row) => row.Source is not ("guess" or "unknown");

    /// <summary>How the names of one function stand together (<see cref="IdStanding"/>): read from
    /// the rows there and the live layer's settlement of the address.</summary>
    public static (IdStanding Standing, string? Keep) StandingOf(IReadOnlyList<Row> there, Registry.LiveLayer? live)
    {
        if (there.Select(r => r.Name).Distinct(StringComparer.Ordinal).Count() < 2) return (IdStanding.One, null);
        (string? Keep, bool Folded)? settled = null;
        try { settled = live?.Settled(there[0].Binary, there[0].Build, there[0].Rva); }
        catch (Exception e) when (e is IOException or GateKit.Json.JsonProblemException or UnauthorizedAccessException) { }
        return Standings.Of(there.Any(r => r.Live), settled);
    }

    /// <summary>The home's live layer, or null when the home has none to read.</summary>
    public static Registry.LiveLayer? HomeLive()
    {
        try { return Registry.LiveLayer.OfHome(); }
        catch (GateKit.Home.HomeException) { return null; }
    }

    static SortedDictionary<string, string> Map(JsonNode? node)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (node is JsonObject o)
            foreach (var (key, value) in o)
                if ((string?)value is { Length: > 0 } text) map[key] = text;
        return map;
    }

    static JsonObject Json(IReadOnlyDictionary<string, string> map)
    {
        var o = new JsonObject();
        foreach (var (key, value) in map) o[key] = value;
        return o;
    }

    static readonly UTF8Encoding Utf8 = new(false);

    public static string PathOf(string coverage) => Path.Combine(coverage, FileName);

    /// <summary>The registry beside the coverage record when it holds functions, else null (the ledger is read then).</summary>
    internal static Registry.RegistryStore? FunctionRegistry(string coverage)
    {
        var store = Registry.RegistryStore.Of(coverage);
        return Directory.Exists(Path.Combine(store.Root, Registry.RegistryStore.Folder(Registry.EntryKind.Function))) ? store : null;
    }

    /// <summary>The registry beside the coverage record when it holds types, else null.</summary>
    internal static Registry.RegistryStore? TypeRegistry(string coverage)
    {
        var store = Registry.RegistryStore.Of(coverage);
        return Directory.Exists(Path.Combine(store.Root, Registry.RegistryStore.Folder(Registry.EntryKind.Type))) ? store : null;
    }

    /// <summary>
    /// The functions named by evidence as rows: from the registry (every function placed in a build
    /// table) when it holds functions, else from the ledger. <paramref name="signature"/> gives the
    /// decompiler's signature at a function, so that a parameter kept by its storage shows under the
    /// decompiler's name of that place; without it a register stands for param_1 to param_4.
    /// </summary>
    public static List<Row> Read(string coverage, Func<string, long, string?>? signature = null)
    {
        if (FunctionRegistry(coverage) is not { } store) return ReadLedger(coverage);
        var rows = new List<Row>();
        foreach (var (build, binary) in store.Tables())
            foreach (var place in store.Table(build, binary))
                if (store.Get(place.Id) is { Kind: Registry.EntryKind.Function } entry)
                    rows.Add(Registry.RegistryForms.ToFunctionRow(entry.Json, binary, build, place, signature?.Invoke(binary, place.Rva)));
        return rows;
    }

    /// <summary>The rows of the ledger file itself; none when the file is not there. A line that cannot be read is an error.</summary>
    public static List<Row> ReadLedger(string coverage)
    {
        var rows = new List<Row>();
        var path = PathOf(coverage);
        if (!File.Exists(path)) return rows;
        var n = 0;
        foreach (var line in File.ReadAllLines(path, Utf8))
        {
            n++;
            if (line.Trim().Length == 0) continue;
            try
            {
                var o = GateKit.Json.JsonText.Parse(line, path, n).Root!.AsObject();
                rows.Add(new Row((string)o["binary"]!, ParseRva((string)o["rva"]!), (string)o["name"]!, (string)o["source"]!,
                    (string?)o["evidence"] ?? "", (string?)o["does"],
                    (o["legacy"] as JsonArray)?.Select(x => (string)x!).ToList() ?? [])
                {
                    Vars = Map(o["vars"]), Structs = Map(o["structs"]), Build = (string?)o["build"], Fingerprint = o["fingerprint"]?.DeepClone() as JsonObject,
                    Size = (long?)o["size"], Body = (string?)o["body"],
                });
            }
            catch (Exception e) when (e is GateKit.Json.JsonProblemException or InvalidOperationException or NullReferenceException or FormatException)
            {
                throw new SymbolsException($"{path}:{n}: not a row of the ledger of function names ({e.Message})");
            }
        }
        return rows;
    }

    static long ParseRva(string text) =>
        long.Parse(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    internal static string Line(Row row)
    {
        var o = new JsonObject
        {
            ["binary"] = row.Binary,
            ["rva"] = string.Create(CultureInfo.InvariantCulture, $"0x{row.Rva:X}"),
            ["name"] = row.Name,
            ["source"] = row.Source,
            ["evidence"] = row.Evidence,
        };
        if (row.Does is { Length: > 0 }) o["does"] = row.Does;
        if (row.Legacy.Count > 0) o["legacy"] = new JsonArray([.. row.Legacy.Select(l => (JsonNode)l)]);
        if (row.Vars.Count > 0) o["vars"] = Json(row.Vars);
        if (row.Structs.Count > 0) o["structs"] = Json(row.Structs);
        if (row.Build is not null) o["build"] = row.Build;
        if (row.Fingerprint is not null) o["fingerprint"] = row.Fingerprint.DeepClone();
        if (row.Size is { } size) o["size"] = size;
        if (row.Body is not null) o["body"] = row.Body;
        return GateKit.Json.JsonText.Inline(o);
    }

    /// <summary>Writes the ledger file whole, in the order of binary and address.</summary>
    public static void WriteLedger(string coverage, IEnumerable<Row> rows)
    {
        var text = string.Concat(rows.OrderBy(r => r.Binary, StringComparer.Ordinal).ThenBy(r => r.Rva).Select(r => Line(r) + "\n"));
        File.WriteAllText(PathOf(coverage), text, Utf8);
    }

    /// <summary>
    /// Writes one row: into the registry when it holds functions - the entry of the row's id made or
    /// changed (what the row does not carry, such as the names other sources give, stays), its place
    /// in the build table set - else into the ledger. A row whose name differs from
    /// <paramref name="previous"/> renames that entry first (`registry rename`: every citation follows).
    /// The evidence kind of a new sentence is <paramref name="evidenceKind"/>, else what the rung implies.
    /// </summary>
    public static void Save(string coverage, Row row, string? previous, Func<string, long, string?>? signature = null, string? evidenceKind = null, TextWriter? output = null)
    {
        if (FunctionRegistry(coverage) is not { } store)
        {
            var rows = ReadLedger(coverage);
            rows.RemoveAll(r => r.Rva == row.Rva && string.Equals(r.Binary, row.Binary, StringComparison.OrdinalIgnoreCase));
            rows.Add(row);
            WriteLedger(coverage, rows);
            return;
        }
        var id = Registry.RegistryId.Placed(row.Name);
        if (previous is not null && Registry.RegistryId.Placed(previous) is var old && old != id && store.Get(old) is not null)
            Registry.RegistryRefs.Rename(store, old, id, output ?? TextWriter.Null);
        var existing = store.Get(id);
        var entry = existing is null ? new JsonObject { ["id"] = id } : (JsonObject)existing.Json.DeepClone();
        entry.Remove("schema");
        entry["rung"] = row.Source;
        if (row.Does is { Length: > 0 } does) { entry["state"] = "described"; entry["does"] = does; }
        if (Registry.RegistryForms.EvidenceText(entry["evidence"]) != row.Evidence)
            entry["evidence"] = row.Evidence.Length == 0 ? new JsonArray()
                : new JsonArray(new JsonObject { ["kind"] = evidenceKind ?? Registry.Rungs.EvidenceKindOf(row.Source), ["says"] = row.Evidence });
        if ((entry["evidence"] as JsonArray)?.Count == 0) entry.Remove("evidence");
        var fresh = Registry.RegistryForms.FromFunctionRow(row, signature ?? ((_, _) => null)).Entry;
        var before = (entry["parameters"] as JsonArray ?? []).Concat(entry["locals"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        // the variables as they stand, when the row says nothing new of them (without a signature a storage could not be read again)
        var unchanged = Registry.RegistryForms.DecompilerVariables(entry, signature?.Invoke(row.Binary, row.Rva)).SequenceEqual(row.Vars);
        foreach (var key in unchanged ? [] : new[] { "parameters", "locals" })
        {
            entry.Remove(key);
            if (fresh[key] is not JsonArray list) continue;
            // what a variable carries beyond its storage and name (a type, a rung) stays with it
            foreach (var v in list.OfType<JsonObject>())
                if (before.FirstOrDefault(b => (string?)b["storage"] == (string?)v["storage"] && (string?)b["decompiler_name"] == (string?)v["decompiler_name"]) is { } was)
                    foreach (var (k, value) in was)
                        if (!v.ContainsKey(k)) v[k] = value?.DeepClone();
            entry[key] = list.DeepClone();
        }
        entry.Remove("ties");
        if (fresh["ties"] is JsonArray ties) entry["ties"] = ties.DeepClone();
        if (row.Fingerprint is { Count: > 0 }) entry["fingerprint"] = row.Fingerprint.DeepClone();
        if (Registry.RegistryCheck.Refusals(entry, existing?.Json) is { Count: > 0 } refused)
            throw new SymbolsException($"{id}: not written - " + string.Join("; ", refused));
        store.Write(Registry.EntryKind.Function, entry, create: false);
        if (row.Build is not null) store.Place(row.Build, row.Binary, new Registry.BuildRow(id, row.Rva, row.Size, row.Body));
    }

    /// <summary>`registry stamp`: every function named by evidence given the build its address is in and
    /// its fingerprint, from <paramref name="stamp"/> (binary, start) -> (build, fingerprint, size, body);
    /// a row it answers null for is left as it is. Returns the rows stamped.</summary>
    public static int Stamp(string coverage, Func<string, long, (string Build, JsonObject Fingerprint, long Size, string? Body)?> stamp, TextWriter output)
    {
        var rows = Read(coverage);
        var done = 0;
        var stamped = rows.Select(r =>
        {
            if (stamp(r.Binary, r.Rva) is not { } s) return r;
            done++;
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{r.Binary}+0x{r.Rva:X}  {r.Name}  build {s.Build}, {s.Size} bytes{(s.Body is null ? ", no body hash" : "")}"));
            return r with { Build = s.Build, Fingerprint = s.Fingerprint, Size = s.Size, Body = s.Body };
        }).ToList();
        if (FunctionRegistry(coverage) is null) WriteLedger(coverage, stamped);
        else foreach (var row in stamped) Save(coverage, row, null);
        return done;
    }

    /// <summary>The row of the function that starts at <paramref name="start"/>, or null.</summary>
    public static Row? Find(string? coverage, string binary, long start)
    {
        if (coverage is null) return null;
        try { return Read(coverage).FirstOrDefault(r => r.Rva == start && string.Equals(r.Binary, binary, StringComparison.OrdinalIgnoreCase)); }
        catch (SymbolsException) { return null; }
    }

    /// <summary>The rows, or none when there is no record or it cannot be read.</summary>
    public static List<Row> ReadOrNone(string? coverage, Func<string, long, string?>? signature = null)
    {
        if (coverage is null) return [];
        try { return Read(coverage, signature); }
        catch (Exception e) when (e is SymbolsException or GateKit.Json.JsonProblemException or Registry.RegistryException) { return []; }
    }

    /// <summary>What is wrong with a name before it is written, or null.</summary>
    public static string? Problem(string name, string source, string evidence)
    {
        if (name.Length == 0 || name.Any(char.IsWhiteSpace)) return "a name is one word, with :: between its parts (esv::roll::DrawDie)";
        if (name.StartsWith("FUN_", StringComparison.Ordinal) || name.StartsWith("sub_", StringComparison.Ordinal))
            return $"{name} is a machine's name: it tells nothing a reader does not have from the address";
        if (!Sources.Contains(source)) return $"--source is one of {string.Join(", ", Sources)}, not {source}";
        if (source != "unknown" && evidence.Trim().Length == 0)
            return "say what backs the name with --evidence: what an instrument read, forced or repeated, and where";
        return null;
    }

    /// <summary>The decompiler's signature of the function at an address - the head of its pseudo-code
    /// up to the body, where the database's own signature is shorter - or null. It is read to find a
    /// parameter's place and never kept.</summary>
    internal static string? SignatureAt(DecompSession s, string binary, long rva)
    {
        try
        {
            var row = s.DbIfExists?.One("SELECT f.id, f.signature FROM functions f JOIN binaries b ON b.id = f.binary_id WHERE b.name = ? COLLATE NOCASE AND f.rva = ?", binary, rva);
            if (row is null) return null;
            if (row[1] is string signature && Registry.RegistryForms.SignatureParameters(signature).Count > 0) return signature;
            var text = CodeCommand.Text(s, Convert.ToInt64(row[0], CultureInfo.InvariantCulture));
            if (text is null) return row[1] as string;
            var body = text.IndexOf("\n{", StringComparison.Ordinal);
            var head = (body < 0 ? text : text[..body]).Split('\n').Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith("/*", StringComparison.Ordinal) && !l.StartsWith("//", StringComparison.Ordinal) && !l.StartsWith('*'));
            return string.Join(" ", head);
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { return null; }
    }

    /// <summary>The game build of a binary's bytes as the pseudo-code database records it (`decomp stamp`), or null.</summary>
    internal static string? BuildAt(DecompSession s, string binary)
    {
        try { return (s.DbIfExists?.Scalar("SELECT game_build FROM binary_meta WHERE binary = ? COLLATE NOCASE", binary) as string)?.Split(',')[0]; }
        catch (Microsoft.Data.Sqlite.SqliteException) { return null; }
    }

    /// <summary>`decomp name`: resolves the address to its function and writes the name.</summary>
    public static int Set(DecompSession s, string address, string name, string source, string evidence, string? does, string? coverageGiven, bool dry, string? evidenceKind = null)
    {
        if (Problem(name, source, evidence) is { } problem) throw new SymbolsException(problem);
        var kind = evidenceKind ?? Registry.Rungs.EvidenceKindOf(source);
        if (Registry.Rungs.IsApplied(source) && !Registry.Rungs.Confirming.Contains(kind))
            throw new SymbolsException($"a {source} name needs evidence that can confirm it (--evidence-kind {string.Join(", ", Registry.Rungs.Confirming)}), not {kind}");
        if (!Registry.RegistryId.IsValid(name)) throw new SymbolsException($"{name} is not a registry id (registry/README.md, \"The id\")");
        var coverage = RecordTypes.FindCoverage(coverageGiven)
            ?? throw new SymbolsException("the ledger of function names lives in the coverage record, and none was found; say where it is with --coverage or GATEKIT_COVERAGE");
        var named = NameAtCommand.Resolve(s, address) ?? throw new SymbolsException($"no function found at {address}: a name is given to a function the databases know");
        string? Signature(string b, long r) => SignatureAt(s, b, r);
        var rows = Read(coverage, Signature);
        var was = rows.FirstOrDefault(r => r.Rva == named.Start && string.Equals(r.Binary, named.Binary, StringComparison.OrdinalIgnoreCase));
        if (was is not null && Registry.Rungs.Rank(source) > Registry.Rungs.Rank(was.Source) && was.Name != name)
            throw new SymbolsException($"{named.Binary}+0x{named.Start:X} is already named {was.Name} from a stronger source ({was.Source}); a {source} name does not replace it");
        var registry = FunctionRegistry(coverage);
        var build = was?.Build ?? BuildAt(s, named.Binary);
        if (registry is not null && build is null)
            throw new SymbolsException($"no build is recorded for {named.Binary}: `gatekit decomp stamp` first, so that the address is put in its build's table");
        // the names other sources give the function stay with it: the symbol database's, the decompiler's when it is a real one
        var others = new List<(string Name, string Source)>();
        if (named.SymbolName is { Length: > 0 } symbolName && symbolName != name) others.Add((symbolName, "symbols"));
        if (named.DecompName is { Length: > 0 } decompName && decompName != name && !decompName.StartsWith("FUN_", StringComparison.Ordinal)) others.Add((decompName, "decompiler"));
        var row = new Row(named.Binary, named.Start, name, source, evidence.Trim(), string.IsNullOrWhiteSpace(does) ? was?.Does : does.Trim(), [])
        {
            Vars = was?.Vars ?? new SortedDictionary<string, string>(StringComparer.Ordinal),
            Structs = was?.Structs ?? new SortedDictionary<string, string>(StringComparer.Ordinal),
            Build = build, Fingerprint = was?.Fingerprint, Size = was?.Size, Body = was?.Body,
        };
        if (row.Fingerprint is null && s.Symbols is not null && registry is not null)
        {
            var reader = new Registry.FingerprintReader(s.Symbols, s.DbIfExists, (b, r) => rows.FirstOrDefault(x => x.Rva == r && string.Equals(x.Binary, b, StringComparison.OrdinalIgnoreCase)) is { } x ? Registry.RegistryId.Placed(x.Name) : null)
            {
                Candidates = b => [Path.Combine(s.Settings.Work, b, b), Path.Combine(s.Settings.Game, "bin", b)],
            };
            try
            {
                var place = reader.Place(named.Binary, named.Start);
                row = row with { Fingerprint = reader.Read(named.Binary, named.Start).ToJson(), Size = place.Size, Body = place.Body };
            }
            catch (SymbolsException) { }
        }
        var inside = named.Rva == named.Start ? "" : string.Create(CultureInfo.InvariantCulture, $" (the function that holds +0x{named.Rva:X})");
        s.Out.WriteLine($"{named.Binary}+0x{named.Start:X}{inside}: {(was is null ? "named" : was.Name == name ? "kept as" : $"renamed from {was.Name} to")} {name} [{source}]");
        if (others.Count > 0) s.Out.WriteLine("  also known as: " + string.Join(", ", others.Select(o => $"{o.Name} ({o.Source})")));
        if (dry) { s.Out.WriteLine("  --dry-run: nothing written"); return 0; }
        Save(coverage, row, was?.Name, Signature, kind, s.Out);
        if (registry is not null && others.Count > 0) AddKnownAs(registry, Registry.RegistryId.Placed(name), others);
        s.Out.WriteLine($"  written to {(registry is null ? PathOf(coverage) : registry.Relative(registry.PathOf(Registry.EntryKind.Function, Registry.RegistryId.Placed(name))))}");
        return 0;
    }

    /// <summary>Adds the names other sources give an entry, each once.</summary>
    internal static void AddKnownAs(Registry.RegistryStore store, string id, IEnumerable<(string Name, string Source)> names)
    {
        if (store.Get(id) is not { } entry) return;
        var json = (JsonObject)entry.Json.DeepClone();
        json.Remove("schema");
        var list = json["known_as"] as JsonArray ?? [];
        foreach (var (name, source) in names)
            if (!list.OfType<JsonObject>().Any(k => (string?)k["name"] == name && (string?)k["source"] == source))
                list.Add(new JsonObject { ["name"] = name, ["source"] = source });
        json["known_as"] = list.DeepClone();
        store.Write(entry.Kind, json, create: false);
    }

    static readonly System.Text.RegularExpressions.Regex Word = new(@"^[A-Za-z_][A-Za-z0-9_]*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>`decomp vars`: names the variables and parameters of a named function (`old=new`), and
    /// says which structure one points at (`--struct var=Struct`). A pair whose new name is empty
    /// takes the name off.</summary>
    public static int Vars(DecompSession s, string address, IReadOnlyList<string> pairs, IReadOnlyList<string> structs, string? coverageGiven)
    {
        var coverage = RecordTypes.FindCoverage(coverageGiven)
            ?? throw new SymbolsException("the ledger of function names lives in the coverage record, and none was found; say where it is with --coverage or GATEKIT_COVERAGE");
        var named = NameAtCommand.Resolve(s, address) ?? throw new SymbolsException($"no function found at {address}");
        string? Signature(string b, long r) => SignatureAt(s, b, r);
        var rows = Read(coverage, Signature);
        var was = rows.FirstOrDefault(r => r.Rva == named.Start && string.Equals(r.Binary, named.Binary, StringComparison.OrdinalIgnoreCase))
            ?? throw new SymbolsException($"{named.Binary}+0x{named.Start:X} has no name yet: name the function first (`gatekit decomp name`), then its variables");
        var vars = new SortedDictionary<string, string>(was.Vars.ToDictionary(p => p.Key, p => p.Value), StringComparer.Ordinal);
        var points = new SortedDictionary<string, string>(was.Structs.ToDictionary(p => p.Key, p => p.Value), StringComparer.Ordinal);
        foreach (var pair in pairs)
        {
            var at = pair.IndexOf('=');
            if (at <= 0) throw new SymbolsException($"{pair}: a variable is named as old=new (param_1=stream)");
            var (old, fresh) = (pair[..at], pair[(at + 1)..]);
            if (!Word.IsMatch(old)) throw new SymbolsException($"{old} is not a variable's name as the decompiler writes it");
            if (fresh.Length == 0)
            {
                // a name is taken off by the decompiler's name or by the one given; what it pointed at goes with it
                var key = vars.ContainsKey(old) ? old : vars.FirstOrDefault(p => p.Value == old).Key;
                if (key is null) throw new SymbolsException($"{old}: no variable of {was.Name} carries that name or was named from it");
                points.Remove(key);
                points.Remove(vars[key]);
                vars.Remove(key);
                continue;
            }
            if (!Word.IsMatch(fresh)) throw new SymbolsException($"{fresh}: a variable's name is one word of letters, digits and _");
            vars[old] = fresh;
        }
        foreach (var pair in structs)
        {
            var at = pair.IndexOf('=');
            if (at <= 0) throw new SymbolsException($"{pair}: --struct is var=Structure (stream=esv::roll::stream::Stream)");
            if (pair[(at + 1)..].Length == 0) points.Remove(pair[..at]); else points[pair[..at]] = pair[(at + 1)..];
        }
        Save(coverage, was with { Vars = vars, Structs = points }, was.Name, Signature, null, s.Out);
        s.Out.WriteLine($"{named.Binary}+0x{named.Start:X} {was.Name}: {vars.Count} variable{(vars.Count == 1 ? "" : "s")} named"
            + (points.Count > 0 ? $", {points.Count} pointing at a structure" : ""));
        foreach (var (old, fresh) in vars) s.Out.WriteLine($"  {old} -> {fresh}" + (points.TryGetValue(fresh, out var of) || points.TryGetValue(old, out of) ? $"  ({of})" : ""));
        return 0;
    }

    // --- structures that are no component's: a stream of the roll manager, a task of the cursor ---

    public const string StructsFile = "structs.jsonl";

    public sealed record Field(string Struct, long Offset, string Name, string? Type, string Source, string Evidence);

    /// <summary>The named fields of structures that are no component's: from the registry's types that
    /// are not a layout's when it holds types, else from the ledger.</summary>
    public static List<Field> ReadFields(string? coverage)
    {
        if (coverage is not null && TypeRegistry(coverage) is { } store)
            return [.. store.All().Where(e => e.Kind == Registry.EntryKind.Type && !Registry.RegistryForms.IsLayout(e.Json))
                .SelectMany(e => (e.Json["fields"] as JsonArray ?? []).OfType<JsonObject>().Select(f => Registry.RegistryForms.ToStructField(e.Id, f)))];
        return ReadFieldLedger(coverage);
    }

    /// <summary>The rows of the ledger file of structure fields itself.</summary>
    public static List<Field> ReadFieldLedger(string? coverage)
    {
        var fields = new List<Field>();
        if (coverage is null) return fields;
        var path = Path.Combine(coverage, StructsFile);
        if (!File.Exists(path)) return fields;
        var n = 0;
        foreach (var line in File.ReadAllLines(path, Utf8))
        {
            n++;
            if (line.Trim().Length == 0) continue;
            try
            {
                var o = GateKit.Json.JsonText.Parse(line, path, n).Root!.AsObject();
                fields.Add(new Field((string)o["struct"]!, ParseRva((string)o["offset"]!), (string)o["name"]!, (string?)o["type"], (string)o["source"]!, (string?)o["evidence"] ?? ""));
            }
            catch (Exception e) when (e is GateKit.Json.JsonProblemException or InvalidOperationException or NullReferenceException or FormatException)
            {
                throw new SymbolsException($"{path}:{n}: not a row of the ledger of structure fields ({e.Message})");
            }
        }
        return fields;
    }

    static string FieldLine(Field f)
    {
        var o = new JsonObject
        {
            ["struct"] = f.Struct,
            ["offset"] = string.Create(CultureInfo.InvariantCulture, $"0x{f.Offset:X}"),
            ["name"] = f.Name,
        };
        if (f.Type is { Length: > 0 }) o["type"] = f.Type;
        o["source"] = f.Source;
        o["evidence"] = f.Evidence;
        return GateKit.Json.JsonText.Inline(o);
    }

    /// <summary>`decomp field`: names the field at an offset of a structure that is no component's
    /// (a component's field is named with `gatekit names set`, in its layout).</summary>
    public static int SetField(DecompSession s, string structure, string offset, string name, string? type, string source, string evidence, string? coverageGiven)
    {
        if (structure.Length == 0 || structure.Any(char.IsWhiteSpace)) throw new SymbolsException("a structure's name is one word, with :: between its parts");
        if (!Word.IsMatch(name)) throw new SymbolsException($"{name}: a field's name is one word of letters, digits and _");
        if (name.StartsWith("field_", StringComparison.Ordinal)) throw new SymbolsException($"{name} is a machine's name: it tells nothing the offset does not");
        if (!Sources.Contains(source)) throw new SymbolsException($"--source is one of {string.Join(", ", Sources)}, not {source}");
        if (source != "unknown" && evidence.Trim().Length == 0)
            throw new SymbolsException("say what backs the name with --evidence: what an instrument read, forced or repeated, and where");
        long at;
        try { at = ParseRva(offset); }
        catch (FormatException) { throw new SymbolsException($"{offset}: an offset is hexadecimal, 0x70"); }
        var coverage = RecordTypes.FindCoverage(coverageGiven)
            ?? throw new SymbolsException("the ledger of structure fields lives in the coverage record, and none was found; say where it is with --coverage or GATEKIT_COVERAGE");
        if (TypeRegistry(coverage) is { } store)
        {
            if (!Registry.RegistryId.IsValid(structure)) throw new SymbolsException($"{structure} is not a registry id");
            var existing = store.Get(structure);
            if (existing is not null && (existing.Kind != Registry.EntryKind.Type || Registry.RegistryForms.IsLayout(existing.Json)))
                throw new SymbolsException($"{structure} is a component's layout (or not a type): its fields are named with `gatekit names set`");
            var entry = existing is null ? new JsonObject { ["id"] = structure, ["kind"] = "struct" } : (JsonObject)existing.Json.DeepClone();
            entry.Remove("schema");
            var list = (entry["fields"] as JsonArray ?? []).OfType<JsonObject>().Where(f => f["offset"]!.GetValue<long>() != at).Select(f => f.DeepClone()).ToList();
            list.Add(Registry.RegistryForms.FromStructField(new Field(structure, at, name, type, source, evidence.Trim())));
            entry["fields"] = new JsonArray([.. list.OrderBy(f => f!["offset"]!.GetValue<long>())]);
            store.Write(Registry.EntryKind.Type, entry, create: false);
            s.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{structure}+0x{at:X}: {name}{(type is null ? "" : " (" + type + ")")} [{source}]"));
            return 0;
        }
        var fields = ReadFieldLedger(coverage);
        fields.RemoveAll(f => f.Struct == structure && f.Offset == at);
        fields.Add(new Field(structure, at, name, type, source, evidence.Trim()));
        File.WriteAllText(Path.Combine(coverage, StructsFile),
            string.Concat(fields.OrderBy(f => f.Struct, StringComparer.Ordinal).ThenBy(f => f.Offset).Select(f => FieldLine(f) + "\n")), Utf8);
        s.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{structure}+0x{at:X}: {name}{(type is null ? "" : " (" + type + ")")} [{source}]"));
        return 0;
    }

    /// <summary>`decomp struct`: the named fields of a structure, or the structures the ledger has.</summary>
    public static int ShowStruct(DecompSession s, string? structure, string? coverageGiven)
    {
        var fields = ReadFields(RecordTypes.FindCoverage(coverageGiven));
        if (structure is null)
        {
            foreach (var g in fields.GroupBy(f => f.Struct).OrderBy(g => g.Key, StringComparer.Ordinal))
                s.Out.WriteLine($"{g.Key}  ({g.Count()} field{(g.Count() == 1 ? "" : "s")})");
            return 0;
        }
        foreach (var line in Legend(fields, structure)) s.Out.WriteLine(line);
        return 0;
    }

    /// <summary>The named fields of a structure, one a line, in the order of their offsets.</summary>
    public static IEnumerable<string> Legend(IReadOnlyList<Field> fields, string structure)
    {
        var mine = fields.Where(f => f.Struct == structure).OrderBy(f => f.Offset).ToList();
        if (mine.Count == 0) { yield return $"{structure}: no field named yet (`gatekit decomp field`)"; yield break; }
        yield return structure;
        foreach (var f in mine)
            yield return string.Create(CultureInfo.InvariantCulture, $"  +0x{f.Offset:X}  {f.Name}{(f.Type is null ? "" : "  " + f.Type)}");
    }

    /// <summary>`decomp names`: the ledger, or the rows whose name, address or sentence holds the text.</summary>
    public static int List(DecompSession s, string? text, string? coverageGiven, bool json)
    {
        var coverage = RecordTypes.FindCoverage(coverageGiven);
        var live = HomeLive();
        var all = coverage is null ? [] : WithLive(Read(coverage, (b, r) => SignatureAt(s, b, r)), live)
            .OrderBy(r => r.Binary, StringComparer.Ordinal).ThenBy(r => r.Rva).ThenBy(r => Registry.Rungs.Rank(r.Source)).ThenBy(r => r.Live).ToList();
        var rows = text is { Length: > 0 } ? all.Where(r => Line(r).Contains(text, StringComparison.OrdinalIgnoreCase)).ToList() : all;
        if (json)
        {
            s.Out.WriteLine("[" + string.Join(",\n ", rows.Select(Line)) + "]");
            return 0;
        }
        var clashes = all.GroupBy(r => (Binary: r.Binary.ToLowerInvariant(), r.Rva))
            .Where(g => StandingOf([.. g], live).Standing == IdStanding.Clash).Select(g => g.Key).ToHashSet();
        foreach (var r in rows)
        {
            var clash = clashes.Contains((r.Binary.ToLowerInvariant(), r.Rva)) ? "  (a clash, not settled: `registry clashes`)" : "";
            s.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{r.Binary}+0x{r.Rva:X}  {r.Name}  [{r.Source}]{(r.Live ? " (live)" : "")}{clash}"));
            if (r.Does is { Length: > 0 }) s.Out.WriteLine("  does: " + r.Does);
            if (r.Evidence.Length > 0) s.Out.WriteLine("  evidence: " + r.Evidence);
            if (r.Guess is not null) s.Out.WriteLine("  guess: " + r.Guess);
        }
        s.Out.WriteLine(Count(rows));
        return 0;
    }

    /// <summary>The closing line of `decomp names`: the names by evidence, and apart from them the
    /// guesses and the names on the rung unknown, which no evidence backs.</summary>
    internal static string Count(IReadOnlyList<Row> rows)
    {
        static string Of(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";
        var byEvidence = rows.Count(ByEvidence);
        var guesses = rows.Count(r => r.Source == "guess");
        var unknown = rows.Count(r => r.Source == "unknown");
        var line = Of(byEvidence, "name", "names") + " by evidence";
        if (guesses > 0) line += ", " + Of(guesses, "guess", "guesses");
        if (unknown > 0) line += ", " + Of(unknown, "name", "names") + " on the rung unknown";
        var functions = rows.Select(r => (r.Binary.ToLowerInvariant(), r.Rva)).Distinct().Count();
        return line + $" ({Of(functions, "function", "functions")})";
    }
}
