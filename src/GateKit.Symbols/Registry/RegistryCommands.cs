using System.CommandLine;
using System.Globalization;
using System.Text.Json.Nodes;
using GateKit.Json;
using GateKit.Symbols.Decomp;

namespace GateKit.Symbols.Registry;

/// <summary>
/// `gatekit registry`: the registry of our reading of the game's code (registry/ at the root of the
/// repository) and the live layer every process on the machine shares (docs/contracts/cli.md,
/// "registry"). Every subcommand writes to the invocation's writers; an expected failure prints its
/// message and exits with 1.
/// </summary>
public static class RegistryCommands
{
    const string About = """
        The registry of our reading of the game's code: one file an entity under registry/, keyed by its id
        (esv::RollSystem::RollDice), and one table a build of where each id is.

            gatekit registry fingerprint bg3_dx11.exe+0x32955C0   the facts that find a function again after a patch
            gatekit registry stamp                                stamp the ledger of function names with the build and fingerprints
        """;

    public static Command CreateCommand() => Create(DecompSettings.FromEnvironment);

    internal static Command Create(Func<DecompSettings> settings)
    {
        var root = new Command("registry", About);

        int Invoke(ParseResult p, Func<DecompSession, int> body)
        {
            if (CopyFree.Refuse(p, "registry") is not 0 and var refused) return refused;
            var output = p.InvocationConfiguration.Output;
            var error = p.InvocationConfiguration.Error;
            try
            {
                using var s = new DecompSession(settings(), output, error);
                return body(s);
            }
            catch (Exception e) when (e is SymbolsException or RegistryException or JsonProblemException or GateKit.Home.HomeException)
            {
                error.WriteLine(e.Message);
                return 1;
            }
        }

        {
            var target = new Argument<string>("function") { Description = "binary+0xRVA (anywhere inside the function), or a named function's id" };
            var asJson = new Option<bool>("--json") { Description = "the fingerprint as JSON" };
            var coverage = new Option<string?>("--coverage") { Description = "the coverage record (default GATEKIT_COVERAGE, else research/coverage of the repository)" };
            var c = new Command("fingerprint", "the facts about a function that are not code - size, what it calls, its strings, components and virtual-table slots, a hash of its masked body - from the home's databases")
                { target, asJson, coverage };
            c.SetAction(p => Invoke(p, s => FingerprintCommand(s, p.GetValue(target)!, p.GetValue(asJson), p.GetValue(coverage))));
            root.Subcommands.Add(c);
        }
        {
            var target = new Argument<string>("function") { Description = "a registry id, a name, or binary+0xRVA" };
            var limit = new Option<int>("--limit") { Description = "the most bytes a signature may take (default 48)", DefaultValueFactory = _ => 48 };
            var coverage = new Option<string?>("--coverage") { Description = "the coverage record beside the registry" };
            var c = new Command("signature", "a byte pattern for the start of a function - whole instructions, `??` where a relocation or another layout would change a byte - as short as is found once in the binary's code; for every binary the registry places the function in. What a hook or a Script Extender mapping is written from")
                { target, limit, coverage };
            c.SetAction(p => Invoke(p, s =>
            {
                var given = p.GetValue(target)!;
                var places = new List<(string Binary, long Start)>();
                if (RegistryStore.Find(p.GetValue(coverage)) is { } store && store.Lookup(given) is { Count: 1 } entries)
                    places.AddRange(store.Places(entries[0].Id).Select(x => (x.Binary, x.Row.Rva)));
                if (places.Count == 0) places.Add(Locate(s, given, p.GetValue(coverage)));
                var reader = Reader(s, null);
                var bad = 0;
                foreach (var (binary, start) in places)
                {
                    var lines = reader.Signature(binary, start, p.GetValue(limit), out var found);
                    s.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{binary}+0x{start:X}  {(lines is null ? "the binary's file cannot be read" : found == 1 ? $"found once, {lines.Sum(l => (l.Bytes.Length + 1) / 3)} bytes" : $"NOT unique within the limit: {found} places")}"));
                    if (lines is null || found != 1) bad++;
                    foreach (var line in lines ?? []) s.Out.WriteLine($"  {line.Bytes,-32} // {line.Text}");
                }
                return bad == 0 ? 0 : 1;
            }));
            root.Subcommands.Add(c);
        }
        {
            var c = new Command("mappings", "the binding layer of a Script Extender (its BinaryMappings.xml) against the registry: what a table written by hand leads to, and a mapping written from the registry alone");
            var limit = new Option<int>("--limit") { Description = "the most bytes a pattern may take (default 64)", DefaultValueFactory = _ => 64 };
            var coverage = new Option<string?>("--coverage") { Description = "the coverage record beside the registry" };
            {
                var file = new Argument<string>("file") { Description = "a BinaryMappings.xml" };
                var verbose = new Option<bool>("--verbose") { Description = "every symbol, not only those that do not land or that the registry calls otherwise" };
                var check = new Command("check", "for every symbol of the table and every binary of the game: where its pattern lands, whether a function starts there, what the registry calls the place, and whether the registry could write the mapping itself") { file, limit, coverage, verbose };
                check.SetAction(p => Invoke(p, s => Mappings.Check(p.GetValue(file)!, Reader(s, null),
                    RegistryStore.Find(p.GetValue(coverage)) ?? throw new SymbolsException("no registry found"), p.GetValue(limit), p.GetValue(verbose), s.Out)));
                c.Subcommands.Add(check);
            }
            {
                var file = new Argument<string>("file") { Description = "a BinaryMappings.xml" };
                var binary = new Option<string>("--binary") { Description = "the binary whose places are noted (the other binaries follow by `registry reanchor`)", DefaultValueFactory = _ => "bg3_dx11.exe" };
                var write = new Option<bool>("--write") { Description = "write the notes into the live layer; without it only say what would be noted" };
                var adopt = new Command("adopt", "take the names of the table's functions into the registry: every function the table leads to and the registry does not name yet is noted under the table's name at rung extender, with the mapping as its evidence") { file, binary, write, limit, coverage };
                adopt.SetAction(p => Invoke(p, s =>
                {
                    var store = RegistryStore.Find(p.GetValue(coverage)) ?? throw new SymbolsException("no registry found");
                    var landings = Mappings.Land(Mappings.Read(p.GetValue(file)!), Reader(s, null), store, p.GetValue(limit));
                    var live = LiveLayer.OfHome();
                    int noted = 0, refused = 0;
                    foreach (var l in landings.Where(l => l.Binary.Equals(p.GetValue(binary), StringComparison.OrdinalIgnoreCase) && l.Rva is not null && l.Function && l.Id is null))
                    {
                        var id = Mappings.IdOf(l.Symbol);
                        var at = string.Create(CultureInfo.InvariantCulture, $"{l.Binary}+0x{l.Rva:X}");
                        if (!p.GetValue(write)) { s.Out.WriteLine($"would note {id} at {at}"); noted++; continue; }
                        try
                        {
                            LiveCommands.Note(s, new NoteRequest(id)
                            {
                                Kind = "function", At = at, Rung = "extender", Who = "mappings", Task = "binding layer",
                                Evidence = [$"extender-name:Script Extender's table of byte patterns names this function: the pattern of its mapping {l.Mapping} is found once in the binary and {(l.Indirect ? "an instruction it marks refers here" : "marks this place")}"],
                                Ask = ["What the function does: it is named from Script Extender's table and has not been read"], AskAbout = "meaning",
                            }, live, store);
                            s.Out.WriteLine($"noted {id} at {at}");
                            noted++;
                        }
                        catch (Exception e) when (e is RegistryException or SymbolsException)
                        {
                            s.Out.WriteLine($"not noted {id} at {at}: {e.Message}");
                            refused++;
                        }
                    }
                    s.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{noted} {(p.GetValue(write) ? "noted" : "would be noted")}, {refused} refused; places that are not a function's start are left (the registry has no pattern-free way to find them again yet)"));
                    return refused == 0 ? 0 : 1;
                }));
                c.Subcommands.Add(adopt);
            }
            {
                var ids = new Argument<string[]>("function") { Description = "registry ids of functions", Arity = ArgumentArity.ZeroOrMore };
                var all = new Option<bool>("--all") { Description = "every function the registry places in a binary" };
                var count = new Option<bool>("--count") { Description = "print only how many got a mapping and which did not" };
                var emit = new Command("emit", "a <Mapping> for each function, from the registry alone: one pattern of its start that is found once in every binary the registry places it in") { ids, all, count, limit, coverage };
                emit.SetAction(p => Invoke(p, s =>
                {
                    var store = RegistryStore.Find(p.GetValue(coverage)) ?? throw new SymbolsException("no registry found");
                    var reader = Reader(s, null);
                    var bad = 0;
                    if (p.GetValue(all) || p.GetValue(count))
                    {
                        var wanted = p.GetValue(all)
                            ? store.All().Where(e => e.Kind == EntryKind.Function && store.Places(e.Id).Count > 0).Select(e => e.Id).Order(StringComparer.Ordinal).ToList()
                            : [.. p.GetValue(ids)!];
                        var got = 0;
                        foreach (var id in wanted)
                        {
                            var written = Mappings.Emit(id, reader, store, p.GetValue(limit), out var why);
                            if (written is not null) { got++; if (!p.GetValue(count)) { s.Out.WriteLine(written); s.Out.WriteLine(); } }
                            else { bad++; s.Out.WriteLine($"    <!-- {id}: no mapping: {why} -->"); }
                        }
                        s.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"    <!-- {got} of {wanted.Count} functions have a mapping from the registry alone (limit {p.GetValue(limit)} bytes) -->"));
                        return 0;
                    }
                    foreach (var given in p.GetValue(ids)!)
                    {
                        var entries = store.Lookup(given);
                        if (entries.Count != 1) { s.Out.WriteLine($"    <!-- {given}: {(entries.Count == 0 ? "the registry has no such function" : "more than one entry answers to the name")} -->"); bad++; continue; }
                        var written = Mappings.Emit(entries[0].Id, reader, store, p.GetValue(limit), out var why);
                        if (written is null) { s.Out.WriteLine($"    <!-- {entries[0].Id}: no mapping: {why} -->"); bad++; }
                        else s.Out.WriteLine(written);
                        s.Out.WriteLine();
                    }
                    return bad == 0 ? 0 : 1;
                }));
                c.Subcommands.Add(emit);
            }
            {
                var file = new Argument<string>("file") { Description = "the table to write" };
                var write = new Command("write", "the registry's own table in one document: a mapping for every function the registry names and places, its target bound to the registry's id - what a Script Extender loads beside its own table") { file, limit, coverage };
                write.SetAction(p => Invoke(p, s =>
                {
                    var store = RegistryStore.Find(p.GetValue(coverage)) ?? throw new SymbolsException("no registry found");
                    var (written, missing) = Mappings.Write(p.GetValue(file)!, Reader(s, null), store, p.GetValue(limit));
                    s.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"wrote {p.GetValue(file)}: {written} mappings; {missing.Count} functions have none within {p.GetValue(limit)} bytes"));
                    foreach (var (id, why) in missing) s.Out.WriteLine($"  {id}: {why}");
                    return 0;
                }));
                c.Subcommands.Add(write);
            }
            root.Subcommands.Add(c);
        }
        {
            var coverage = new Option<string?>("--coverage") { Description = "the coverage record beside the registry" };
            var c = new Command("stamp", "give every function named by evidence the build of the game its address is in and its fingerprint") { coverage };
            c.SetAction(p => Invoke(p, s => StampCommand(s, p.GetValue(coverage))));
            root.Subcommands.Add(c);
        }
        {
            var what = new Argument<string>("what") { Description = "functions (functions.jsonl), structs (structs.jsonl), layouts (layouts/**) or all" };
            var coverage = new Option<string?>("--coverage") { Description = "the coverage record the stores are read from" };
            var replace = new Option<bool>("--replace") { Description = "write the store's form over an entry that differs from it" };
            var dry = new Option<bool>("--dry-run") { Description = "say what would be written; write nothing" };
            var c = new Command("import", "carry the earlier stores into the registry, again and again without harm, with counts and a round trip that proves nothing was lost") { what, coverage, replace, dry };
            c.SetAction(p => Invoke(p, s => ImportCommand(s, p.GetValue(what)!, p.GetValue(coverage), p.GetValue(replace), p.GetValue(dry))));
            root.Subcommands.Add(c);
        }
        {
            var coverage = new Option<string?>("--coverage") { Description = "the coverage record beside the registry (default: the repository's)" };
            var strict = new Option<bool>("--strict") { Description = "fail on owed work too (an unconfirmed entry with no question of how the game reaches it)" };
            var notes = new Option<int>("--notes") { Description = "with --verbose: how many of each kind to print (default 20)", DefaultValueFactory = _ => 20 };
            var verbose = new Option<bool>("--verbose") { Description = "list the owed work and the notes; without it they are counted in one line each" };
            var c = new Command("check", "every entry valid, at its path, one file an id; build tables in order; every citation of code and tests resolved; the refusals of registry/README.md. Exit 1 on a refusal") { coverage, strict, notes, verbose };
            c.SetAction(p => Invoke(p, s => CheckCommand(s, p.GetValue(coverage), p.GetValue(strict), p.GetValue(notes), p.GetValue(verbose))));
            root.Subcommands.Add(c);
        }
        {
            var old = new Argument<string>("old") { Description = "the entry's id" };
            var fresh = new Argument<string>("new") { Description = "its new id" };
            var coverage = new Option<string?>("--coverage");
            var c = new Command("rename", "rename an entry: its file moves, every build table, every citation in the repository, the live layer's claims and the data files that hold places follow; the old id is left nowhere") { old, fresh, coverage };
            c.SetAction(p => Invoke(p, s => { RegistryRefs.Rename(Store(p.GetValue(coverage)), p.GetValue(old)!, p.GetValue(fresh)!, s.Out, LiveLayer.OfHome()); return 0; }));
            root.Subcommands.Add(c);
        }
        {
            var coverage = new Option<string?>("--coverage");
            var c = new Command("mend", "put the build tables back in order after a merge: sorted, each row in its shard, a row twice kept once; two addresses for one id refused") { coverage };
            c.SetAction(p => Invoke(p, s =>
            {
                var store = Store(p.GetValue(coverage));
                foreach (var (build, binary) in store.Tables())
                    s.Out.WriteLine($"{build}/{binary}: {store.Mend(build, binary, s.Out)} rows");
                return 0;
            }));
            root.Subcommands.Add(c);
        }
        {
            var binary = new Argument<string>("binary") { Description = "the binary (bg3_dx11.exe)" };
            var build = new Option<string?>("--build") { Description = "the build (default: the one the pseudo-code database records for the binary, else the only one there is)" };
            var coverage = new Option<string?>("--coverage");
            var c = new Command("applied", "exactly what may be given to the decompiler as real names and types for one binary and build: the entries and members on an applied rung, as JSON lines") { binary, build, coverage };
            c.SetAction(p => Invoke(p, s => AppliedCommand(s, p.GetValue(binary)!, p.GetValue(build), p.GetValue(coverage))));
            root.Subcommands.Add(c);
        }
        {
            var source = new Option<string>("--source") { Description = "the binary whose table is moved (default bg3_dx11.exe)", DefaultValueFactory = _ => "bg3_dx11.exe" };
            var target = new Option<string>("--target") { Description = "the binary that receives the table (bg3.exe, or the same binary of another build's database)", Required = true };
            var sourceBuild = new Option<string?>("--source-build") { Description = "the build of the source table (default: the one the pseudo-code database records for the source, else the only one)" };
            var targetBuild = new Option<string?>("--target-build") { Description = "the build of the target (default: the one the pseudo-code database records for it)" };
            var dry = new Option<bool>("--dry-run") { Description = "say what would move and what is left for a person; write nothing" };
            var coverage = new Option<string?>("--coverage");
            var c = new Command("reanchor", "move a build table to another build or binary through the pairs of `decomp pair`: a function with one partner above the measured bar moves by itself, the rest is listed for a person")
                { source, target, sourceBuild, targetBuild, dry, coverage };
            c.SetAction(p => Invoke(p, s => Reanchor.Run(s, Store(p.GetValue(coverage)), p.GetValue(source)!, p.GetValue(target)!, p.GetValue(sourceBuild), p.GetValue(targetBuild), p.GetValue(dry))));
            root.Subcommands.Add(c);
        }
        {
            var scope = new Argument<string?>("scope") { Arity = ArgumentArity.ZeroOrOne, Description = "only entries whose id starts with this (esv::roll)" };
            var coverage = new Option<string?>("--coverage");
            var c = new Command("questions", "every open question of the registry, for a person to read at once: what is not known of an entry, above all how the game reaches it") { scope, coverage };
            c.SetAction(p => Invoke(p, s => QuestionsCommand(s, p.GetValue(scope), p.GetValue(coverage))));
            root.Subcommands.Add(c);
        }
        LiveCommands.Add(root, Invoke);
        Digest.Add(root, Invoke);
        return root;
    }

    internal static RegistryStore Store(string? coverage) =>
        RegistryStore.Find(coverage) ?? throw new RegistryException("no registry found: run inside a checkout of GateKit, or set GATEKIT_REGISTRY or --coverage");

    static int ImportCommand(DecompSession s, string what, string? coverageGiven, bool replace, bool dry)
    {
        var coverage = RecordTypes.FindCoverage(coverageGiven) ?? throw new SymbolsException("no coverage record found; say where it is with --coverage");
        var store = RegistryStore.Of(coverage);
        var kinds = what == "all" ? ["functions", "structs", "layouts"] : new[] { what };
        var failed = false;
        foreach (var kind in kinds)
        {
            RegistryImport.Counts counts;
            switch (kind)
            {
                case "functions":
                    counts = RegistryImport.Functions(store, FunctionNames.ReadLedger(coverage), (b, r) => FunctionNames.SignatureAt(s, b, r),
                        b => Sha(s, b), replace, dry, s.Out);
                    break;
                case "structs":
                    counts = RegistryImport.Structs(store, FunctionNames.ReadFieldLedger(coverage), replace, dry, s.Out);
                    break;
                case "layouts":
                    counts = RegistryImport.Layouts(store, coverage, replace, dry, s.Out);
                    break;
                default:
                    throw new RegistryException($"{kind}: import functions, structs, layouts or all");
            }
            s.Out.WriteLine($"{kind}: {counts}");
            failed |= counts.Refused > 0 || (!dry && counts.RoundTrip != counts.Read);
        }
        if (dry) s.Out.WriteLine("--dry-run: nothing written");
        return failed ? 1 : 0;
    }

    static string? Sha(DecompSession s, string binary)
    {
        try { return s.DbIfExists?.Scalar("SELECT sha256 FROM binary_meta WHERE binary = ? COLLATE NOCASE", binary) as string; }
        catch (Microsoft.Data.Sqlite.SqliteException) { return null; }
    }

    static int CheckCommand(DecompSession s, string? coverage, bool strict, int notes, bool verbose)
    {
        var store = Store(coverage);
        var report = RegistryCheck.Run(store);
        foreach (var (rule, where, what) in report.Refusals) s.Out.WriteLine($"refused [{rule}] {where}: {what}");
        static string ByRule(List<(string Rule, string Where, string What)> list) =>
            string.Join(", ", list.GroupBy(r => r.Rule).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} {g.Count()}"));
        // owed work and notes are counts, not a list of hand work: the list is there for whoever asks
        if (verbose || (strict && report.Owed.Count > 0))
            foreach (var group in report.Owed.GroupBy(o => o.Rule))
            {
                s.Out.WriteLine($"owed [{group.Key}]: {group.Count()} entries; the first:");
                foreach (var (_, where, what) in group.Take(notes)) s.Out.WriteLine($"  {where}: {what}");
            }
        if (verbose)
            foreach (var group in report.Notes.GroupBy(n => n.Rule))
            {
                s.Out.WriteLine($"note [{group.Key}]: {group.Count()}");
                foreach (var (_, where, what) in group.Take(notes)) s.Out.WriteLine($"  {where}: {what}");
            }
        s.Out.WriteLine($"{report.Refusals.Count} refused{(report.Refusals.Count > 0 ? $" ({ByRule(report.Refusals)})" : "")}, "
            + $"{report.Owed.Count} owed{(report.Owed.Count > 0 ? $" ({ByRule(report.Owed)})" : "")}, "
            + $"{report.Notes.Count} notes{(report.Notes.Count > 0 ? $" ({ByRule(report.Notes)})" : "")}" + (verbose ? "" : "; --verbose lists them"));
        return report.Refusals.Count > 0 || (strict && report.Owed.Count > 0) ? 1 : 0;
    }

    static int AppliedCommand(DecompSession s, string binary, string? buildGiven, string? coverage)
    {
        var store = Store(coverage);
        var build = store.BuildFor(binary, buildGiven ?? FunctionNames.BuildAt(s, binary))
            ?? throw new RegistryException($"no build of {binary} has a table; say which with --build");
        foreach (var line in Applied.Of(store, binary, build)) s.Out.WriteLine(JsonText.Inline(line));
        return 0;
    }

    static int QuestionsCommand(DecompSession s, string? scope, string? coverage)
    {
        var store = Store(coverage);
        var count = 0;
        foreach (var entry in store.All().Where(e => Digest.InScope(e, scope)).OrderBy(e => e.Id, StringComparer.Ordinal))
        {
            var open = (entry.Json["questions"] as JsonArray ?? []).OfType<JsonObject>().Where(q => JsonText.Text(q["state"]) == "open").ToList();
            if (open.Count == 0) continue;
            s.Out.WriteLine($"code:{entry.Id}  [{JsonText.Text(entry.Json["rung"]) ?? "no rung"}]" + (JsonText.Text(entry.Json["owner"]) is { } owner ? $"  of {owner}" : ""));
            foreach (var q in open)
            {
                s.Out.WriteLine($"  ({JsonText.Text(q["about"])}) {JsonText.Text(q["ask"])}");
                if (JsonText.Text(q["tried"]) is { } tried) s.Out.WriteLine("    tried: " + tried);
                count++;
            }
        }
        s.Out.WriteLine($"{count} open question{(count == 1 ? "" : "s")}");
        return 0;
    }

    /// <summary>The reader of fingerprints over the home's databases; callees named by the ledger first.</summary>
    internal static FingerprintReader Reader(DecompSession s, Func<string, long, string?>? names)
    {
        var symbols = s.Symbols ?? throw new SymbolsException("a fingerprint is read from the symbol database, and there is none (`gatekit symbols build`)");
        return new FingerprintReader(symbols, s.DbIfExists, names)
        {
            Candidates = binary => [Path.Combine(s.Settings.Work, binary, binary), Path.Combine(s.Settings.Game, "bin", binary)],
        };
    }

    /// <summary>The Steam build of the installed game as `gatekit game verify` recorded it, held against
    /// the build the pseudo-code database records for the binary's bytes: they must agree.</summary>
    internal static string BuildOf(FingerprintReader reader, string binary)
    {
        string? home = null;
        try
        {
            if (GateKit.Home.GateKitHome.Current.State.Path is { } state
                && GateKit.Platform.GameFingerprint.Read(Path.Combine(state, GateKit.Platform.GameFingerprint.FileName)) is { } print)
                home = print.BuildId;
        }
        catch (Exception e) when (e is IOException or JsonProblemException) { }
        var decompiled = reader.BuildOf(binary);
        if (home is null && decompiled is null)
            throw new SymbolsException($"no build is known for {binary}: run `gatekit game verify` and `gatekit decomp stamp`");
        if (home is not null && decompiled is not null && !decompiled.Split(',').Contains(home))
            throw new SymbolsException($"the installed game is build {home}, the decompiled {binary} build {decompiled}: an address of one is not an address of the other");
        return home ?? decompiled!.Split(',')[0];
    }

    static Func<string, long, string?> LedgerNames(string? coverage)
    {
        var rows = FunctionNames.ReadOrNone(RecordTypes.FindCoverage(coverage));
        return (binary, rva) => rows.FirstOrDefault(r => r.Rva == rva && string.Equals(r.Binary, binary, StringComparison.OrdinalIgnoreCase)) is { } row
            ? RegistryId.Placed(row.Name) : null;
    }

    static int FingerprintCommand(DecompSession s, string target, bool json, string? coverage)
    {
        var (binary, start) = Locate(s, target, coverage);
        var reader = Reader(s, LedgerNames(coverage));
        var print = reader.Read(binary, start);
        var place = reader.Place(binary, start);
        if (json)
        {
            var o = new System.Text.Json.Nodes.JsonObject
            {
                ["binary"] = binary, ["rva"] = RegistryStore.Hex(start), ["token"] = print.Token(), ["fingerprint"] = print.ToJson(), ["size"] = place.Size,
            };
            if (place.Body is not null) o["body"] = place.Body;
            if (reader.BodyNote is not null) o["body_note"] = reader.BodyNote;
            s.Out.WriteLine(JsonText.Format(o));
            return 0;
        }
        s.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{binary}+0x{start:X}  token {print.Token()}"));
        s.Out.WriteLine("  " + FingerprintReader.Describe(print, place));
        foreach (var call in print.Calls) s.Out.WriteLine("  calls   " + call);
        foreach (var text in print.Strings) s.Out.WriteLine("  string  " + text);
        foreach (var component in print.Components) s.Out.WriteLine("  touches " + component);
        foreach (var (table, slot) in print.Vtables) s.Out.WriteLine($"  slot    {slot} of {table ?? "an unnamed table"}");
        if (reader.BodyNote is not null) s.Out.WriteLine("  body: " + reader.BodyNote);
        return 0;
    }

    /// <summary>The binary and function start a target names: an address, resolved to the function that holds it, or a name of the ledger.</summary>
    internal static (string Binary, long Start) Locate(DecompSession s, string target, string? coverage)
    {
        if (CodeCommand.Address.IsMatch(target))
        {
            var named = NameAtCommand.Resolve(s, target) ?? throw new SymbolsException($"no function found at {target}");
            return (named.Binary, named.Start);
        }
        var rows = FunctionNames.ReadOrNone(RecordTypes.FindCoverage(coverage));
        var row = rows.FirstOrDefault(r => r.Name == target || RegistryId.Placed(r.Name) == target)
            ?? throw new SymbolsException($"{target}: no function of that id is placed in a build; give binary+0xRVA");
        return (row.Binary, row.Rva);
    }

    internal static int StampCommand(DecompSession s, string? coverageGiven)
    {
        var coverage = RecordTypes.FindCoverage(coverageGiven) ?? throw new SymbolsException("no coverage record found; say where it is with --coverage");
        var reader = Reader(s, LedgerNames(coverageGiven));
        var builds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var notes = new SortedSet<string>(StringComparer.Ordinal);
        var done = FunctionNames.Stamp(coverage, (binary, rva) =>
        {
            if (!builds.TryGetValue(binary, out var build)) builds[binary] = build = BuildOf(reader, binary);
            var print = reader.Read(binary, rva);
            var place = reader.Place(binary, rva);
            if (reader.BodyNote is not null) notes.Add(reader.BodyNote.StartsWith("too short", StringComparison.Ordinal) ? "some functions are too short for a body hash" : reader.BodyNote);
            return (build, print.ToJson(), place.Size, place.Body);
        }, s.Out);
        foreach (var note in notes) s.Out.WriteLine("note: " + note);
        s.Out.WriteLine($"{done} rows stamped in {FunctionNames.PathOf(coverage)}");
        return 0;
    }
}
