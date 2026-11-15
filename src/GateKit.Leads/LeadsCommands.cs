using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using GateKit.GameData;
using GateKit.Copies;
using GateKit.Symbols;
using GateKit.Wiki;

namespace GateKit.Leads;

/// <summary>Building the leads file from the databases, shared by the command and the MCP tool.</summary>
public static class LeadsBuild
{
    /// <summary>The coverage root: the one said, else GATEKIT_COVERAGE, else the checkout's.</summary>
    public static string Root(string? given = null)
    {
        var root = given ?? (Environment.GetEnvironmentVariable("GATEKIT_COVERAGE") is { Length: > 0 } env ? env : null) ?? Record.CoverageRoot()
            ?? throw new LeadsException("the leads live in the coverage record of a checkout; say where it is with --coverage or GATEKIT_COVERAGE");
        root = Path.GetFullPath(root);
        if (!File.Exists(Path.Combine(root, "coverage.json"))) throw new LeadsException($"{root} has no coverage.json");
        return root;
    }

    /// <summary>Builds every lead and writes the file; tries and hand-set fields of the rows already
    /// there are kept. The databases are read without the record's lock, which is held only to join
    /// the result with the file as it is then.</summary>
    public static async Task<JsonObject> Run(string root, string? decompDb, string? symbolsDb, string? indexDb, bool useWiki, CancellationToken cancel = default)
    {
        decompDb ??= SymbolPaths.DecompDb;
        symbolsDb ??= SymbolPaths.SymbolsDb;
        indexDb ??= DataPaths.IndexDatabase;
        using var code = new CodeIndex(decompDb, symbolsDb, root);
        using var data = GateKit.Home.HomeDatabase.Usable(indexDb) ? new DataIndex(indexDb) : null;
        var wiki = useWiki ? new WikiTitles(new WikiReader()) : null;
        var fresh = await new Builder(root, code, data, wiki).Build([], cancel);
        using (Record.Lock(root))
        {
            var seen = Coverage.Seen(root);
            var latest = Lead.Read(root).ToDictionary(r => (string)r["type"]!, StringComparer.Ordinal);
            var rows = fresh.Select(r => Lead.Merge(r, latest.GetValueOrDefault((string)r["type"]!), seen.Contains((string)r["type"]!))).ToList();
            var built = rows.Select(r => (string)r["type"]!).ToHashSet(StringComparer.Ordinal);
            foreach (var (type, row) in latest)
                if (!built.Contains(type))
                {
                    Lead.Refresh(row, seen.Contains(type));
                    rows.Add(row);
                }
            Lead.Write(root, rows);
            var tally = Lead.Tally(rows);
            tally["file"] = Lead.PathOf(root);
            tally["data_index"] = data is null ? GateKit.Home.HomeDatabase.LeftOut(indexDb, "the data and the wiki") : "read";
            if (code.SymbolsNote is { } symbolsNote) tally["symbols_db"] = symbolsNote;
            if (wiki is not null) tally["wiki_requests"] = wiki.Asked;
            return tally;
        }
    }
}

/// <summary>The <c>leads</c> command: what to try next for each type never seen alive, and the tries.</summary>
public static class LeadsCommands
{
    const string About =
        "the leads of the coverage hunt: for every component type never seen alive, what creates it in the code, where that is entered from, "
        + "what of the game's data carries it and what a runner could do - built by rules from the facts, handed out and reported on\n\n"
        + "No stretch without a lead: `next` hands out the best open ones, `report` says how each went. Two tries with one instrument that "
        + "found nothing park a lead and raise a wish. An entry the walk up the callers did not reach is `unknown`, never a guess.";

    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static Command CreateCommand()
    {
        var root = new Command("leads", About);
        var coverage = new Option<string?>("--coverage") { Description = "the coverage record (default GATEKIT_COVERAGE, else research/coverage of this checkout)", Recursive = true };
        var json = new Option<bool>("--json") { Description = "the answer as JSON", Recursive = true };
        root.Options.Add(coverage);
        root.Options.Add(json);

        int Run(ParseResult p, Func<string, TextWriter, int> body)
        {
            try
            {
                return body(LeadsBuild.Root(p.GetValue(coverage)), p.InvocationConfiguration.Output);
            }
            catch (Exception e) when (e is LeadsException or CopyException or SymbolsException or IOException)
            {
                p.InvocationConfiguration.Error.WriteLine(e.Message);
                return 1;
            }
        }
        void Json(TextWriter output, JsonNode node) => output.WriteLine(node.ToJsonString(Indented).ReplaceLineEndings("\n"));
        void Rows(ParseResult p, TextWriter output, IReadOnlyList<JsonObject> rows)
        {
            if (p.GetValue(json)) { Json(output, new JsonArray([.. rows.Select(r => (JsonNode)r.DeepClone())])); return; }
            foreach (var r in rows)
                output.WriteLine($"{(int?)r["score"],3}  {r["state"],-11} {r["instrument"],-10} {r["type"]}\n       {r["class"]}, {r["reachable"]}; "
                    + $"entry {r["entry"]?["kind"]} {r["entry"]?["detail"]}\n       {r["how"]}");
            output.WriteLine($"{rows.Count} leads");
        }

        // build
        {
            var decomp = new Option<string?>("--decomp") { Description = "the pseudo-code database (default BG3_DECOMP_WORK/decomp.sqlite)" };
            var symbols = new Option<string?>("--symbols") { Description = "the symbol database (default <local>/symbols/symbols.sqlite)" };
            var index = new Option<string?>("--index") { Description = "the game's data index (default <local>/index/gameindex.sqlite)" };
            var noWiki = new Option<bool>("--no-wiki") { Description = "do not ask bg3.wiki (cached answers are used either way)" };
            var c = new Command("build", "write research/coverage/leads.jsonl: one row per type never seen alive; tries and hand-set fields are kept") { decomp, symbols, index, noWiki };
            c.SetAction((p, cancel) => Task.FromResult(Run(p, (rootPath, output) =>
            {
                var tally = LeadsBuild.Run(rootPath, p.GetValue(decomp), p.GetValue(symbols), p.GetValue(index), !p.GetValue(noWiki), cancel).GetAwaiter().GetResult();
                if (p.GetValue(json)) Json(output, tally);
                else Print(output, tally);
                return 0;
            })));
            root.Subcommands.Add(c);
        }
        // next
        {
            var instrument = new Option<string?>("--instrument") { Description = "only leads taken with this: " + string.Join(", ", Rules.Instruments) };
            var count = new Option<int>("--count") { DefaultValueFactory = _ => 3, Description = "how many" };
            var c = new Command("next", "hand out the best open leads and mark them in_work for that copy") { instrument, count };
            c.SetAction(p => Run(p, (rootPath, output) =>
            {
                var who = Instance(p) ?? "unknown";
                Rows(p, output, Lead.Next(rootPath, who, p.GetValue(instrument), p.GetValue(count)));
                return 0;
            }));
            root.Subcommands.Add(c);
        }
        // report
        {
            var type = new Argument<string>("type") { Description = "the component type, e.g. esv::teleport::SpellComponent" };
            var outcome = new Option<string>("--outcome") { Required = true, Description = "lit, empty (tried, nothing) or blocked (could not be tried)" };
            var note = new Option<string>("--note") { Required = true, Description = "what was done and what happened, one or two sentences" };
            var instrument = new Option<string?>("--instrument") { Description = "what was used (default: the lead's own)" };
            var run = new Option<string?>("--run") { Description = "the run (default: the claim's, else GATEKIT_RUN)" };
            var c = new Command("report", "append a try to a lead; two empties with one instrument park it and raise a wish") { type, outcome, note, instrument, run };
            c.SetAction(p => Run(p, (rootPath, output) =>
            {
                var (row, wish) = Lead.Report(rootPath, p.GetValue(type)!, p.GetValue(outcome)!, p.GetValue(note)!, Instance(p), p.GetValue(instrument), p.GetValue(run));
                if (p.GetValue(json)) Json(output, new JsonObject { ["lead"] = row.DeepClone(), ["wish"] = wish?.DeepClone() });
                else
                {
                    output.WriteLine($"{row["type"]}: {row["state"]}, {(row["attempts"] as JsonArray)?.Count} tries" + (row["parked"] is { } why ? $"; parked: {why}" : ""));
                    if (wish is not null) output.WriteLine($"wish {wish["id"]}: {wish["text"]}");
                }
                return 0;
            }));
            root.Subcommands.Add(c);
        }
        // list
        {
            var cls = new Option<string?>("--class") { Description = "only this class: " + string.Join(", ", Rules.Classes) };
            var state = new Option<string?>("--state") { Description = "only this state: " + string.Join(", ", Rules.States) };
            var reachable = new Option<string?>("--reachable") { Description = "only this: " + string.Join(", ", Rules.Reachables) };
            var prefix = new Option<string?>("--ns") { Description = "only types whose name starts with this" };
            var limit = new Option<int>("--limit") { DefaultValueFactory = _ => 50 };
            var c = new Command("list", "the leads, best first") { cls, state, reachable, prefix, limit };
            c.SetAction(p => Run(p, (rootPath, output) =>
            {
                Rows(p, output, Filter(Lead.Current(rootPath), p.GetValue(cls), p.GetValue(state), p.GetValue(reachable), p.GetValue(prefix), p.GetValue(limit)));
                return 0;
            }));
            root.Subcommands.Add(c);
        }
        // tally
        {
            var c = new Command("tally", "the leads by class, state and reachability, and the true ceiling: every lead the shipping game can create");
            c.SetAction(p => Run(p, (rootPath, output) =>
            {
                var tally = Lead.Tally(Lead.Current(rootPath));
                if (p.GetValue(json)) Json(output, tally);
                else Print(output, tally);
                return 0;
            }));
            root.Subcommands.Add(c);
        }
        return root;
    }

    /// <summary>The copy: the program's global --instance, else GATEKIT_INSTANCE, else null.</summary>
    static string? Instance(ParseResult p)
    {
        string? said = null;
        try { said = p.GetValue<string?>("--instance"); }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException) { }
        return said is { Length: > 0 } ? said
            : Environment.GetEnvironmentVariable("GATEKIT_INSTANCE") is { Length: > 0 } env ? env : null;
    }

    /// <summary>Rows that pass the filters, best first.</summary>
    public static List<JsonObject> Filter(IEnumerable<JsonObject> rows, string? cls, string? state, string? reachable, string? prefix, int limit)
    {
        static string S(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
        return [.. rows.Where(r => (cls is null || S(r["class"]) == cls) && (state is null || S(r["state"]) == state)
                                   && (reachable is null || S(r["reachable"]) == reachable) && (prefix is null || S(r["type"]).StartsWith(prefix, StringComparison.Ordinal)))
            .OrderByDescending(r => (int?)r["score"] ?? 0).ThenBy(r => S(r["type"]), StringComparer.Ordinal).Take(limit)];
    }

    static void Print(TextWriter output, JsonObject tally)
    {
        output.WriteLine($"{tally["leads"]} leads; ceiling {tally["ceiling"]} (all but not_in_shipping_game); named entry {tally["with_named_entry"]}, "
                         + $"data {tally["with_data"]}, wiki page {tally["with_wiki"]}");
        foreach (var key in new[] { "by_class", "by_reachable", "by_state", "by_entry", "by_instrument" })
            output.WriteLine($"  {key[3..],-10} " + string.Join(", ", (tally[key] as JsonObject ?? []).Select(p => $"{p.Key} {p.Value}")));
        if (tally["file"] is { } file) output.WriteLine($"written: {file}");
    }
}
