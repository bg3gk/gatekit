using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using GateKit.Copies;
using ModelContextProtocol.Protocol;

namespace GateKit.Mcp;

/// <summary>The ledgers as tools (docs/contracts/mcp.md 3.9): <c>bg3_wish</c> and <c>bg3_record</c>.
/// No game is needed for either.</summary>
static class RecordTools
{
    public static IReadOnlyList<GateKitTool> Create(ToolHost host) => [Wish(), RecordQuery()];

    static Tool Define(string name, string description, string schema) => new()
    {
        Name = name,
        Description = description,
        InputSchema = JsonSerializer.Deserialize<JsonElement>(schema),
    };

    static string Root() => Record.CoverageRoot()
        ?? throw new CopyException("the coverage record lives in the repository, and this is not a checkout of it");

    static GateKitTool Wish() => new(
        Define("bg3_wish",
            "The wishes: every place the API lied, hid something or made you guess, as a ledger with one id scheme (W-0001 and "
            + "on) instead of numbers inside a report's table. `add` records a new one and answers its id; `update` moves one to "
            + "a new state (sent to whoever fixes it, fixed with the commit, seen_live once a run saw the fix work, dropped with "
            + "why); `list` shows them as they stand. Say what you did, what happened and what you wanted instead - not a "
            + "complaint: these become general fixes. Rows are appended, never rewritten: research/coverage/record/wishes.jsonl.",
            """
            {
             "type": "object",
             "required": ["action"],
             "properties": {
              "action": {"type": "string", "enum": ["add", "update", "list"]},
              "text": {"type": "string", "description": "add: the wish."},
              "from": {"type": "string", "description": "add: who has it (a role: runner world, keeper, overseer...). Default GATEKIT_ROLE."},
              "id": {"type": "string", "description": "update: the wish, W-0001 and on."},
              "state": {"type": "string", "enum": ["open", "sent", "fixed", "seen_live", "dropped"], "description": "add (default open) or update: where it stands. list: only these."},
              "commit": {"type": "string", "description": "The commit that fixed it."},
              "note": {"type": "string", "description": "Free text: why dropped, what is left, where it was sent."}
             }
            }
            """),
        (args, _) =>
        {
            var root = Root();
            JsonNode answer = args.Need("action") switch
            {
                "add" => RecordBook.AddWish(root, args.Str("from") ?? Record.Who().Role, args.Need("text"), args.Str("state") ?? "open",
                    args.Str("note"), args.Str("commit")),
                "update" => RecordBook.UpdateWish(root, args.Need("id"), args.Need("state"), args.Str("commit"), args.Str("note")),
                "list" => new JsonArray([.. RecordBook.Wishes(root)
                    .Where(w => args.Str("state") is not { } only || w["state"]?.GetValue<string>() == only).Select(w => (JsonNode)w.DeepClone())]),
                var other => throw new ArgumentException($"`action` is add, update or list, not {other}"),
            };
            return Task.FromResult(Results.Text(answer));
        });

    static GateKitTool RecordQuery() => new(
        Define("bg3_record",
            "Questions asked of the whole coverage record, no game needed. `tally`: the counts now - types seen and never seen, "
            + "verified, fields named by rung and still owed, systems described and sure, claims written and checked (the same row "
            + "`gatekit record tally --write` appends to counts.jsonl). `yield` with `run`: what that run produced - types first "
            + "seen per game copy, role and hour, and its replays' pass share. `check`: whatever in the record does not hold "
            + "together (a finding naming no recipe, a replayable recipe without a finding, a claim's check naming no recipe, a "
            + "value outside a ledger's vocabulary, a sighting of no registered type).",
            """
            {
             "type": "object",
             "properties": {
              "action": {"type": "string", "enum": ["tally", "yield", "check"], "description": "Default tally."},
              "run": {"type": "string", "description": "yield: the run (GATEKIT_RUN of its sessions). Default this server's run."}
             }
            }
            """),
        (args, _) =>
        {
            var root = Root();
            JsonNode answer = (args.Str("action") ?? "tally") switch
            {
                "tally" => RecordBook.Tally(root),
                "yield" => RecordBook.Yield(root, args.Str("run") ?? RecordBook.Context.Who().Run),
                "check" => RecordBook.Check(root) is var problems && problems.Count == 0
                    ? new JsonObject { ["ok"] = true }
                    : new JsonObject { ["ok"] = false, ["problems"] = new JsonArray([.. problems.Select(p => (JsonNode)p!)]) },
                var other => throw new ArgumentException($"`action` is tally, yield or check, not {other}"),
            };
            return Task.FromResult(Results.Text(answer));
        });
}

/// <summary>`gatekit record ...`: the coverage ledgers from the command line (docs/contracts/cli.md 8.11).</summary>
public static class RecordCommands
{
    static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        IndentSize = 1,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static Command CreateCommand()
    {
        var root = new Command("record", "the coverage record's ledgers (research/coverage/record): the counts, whether the record holds "
            + "together, what a run produced, and the wishes");
        var coverage = new Option<string?>("--coverage")
        {
            Description = "the coverage record (default research/coverage of the checkout this is run in)",
            Recursive = true,
        };
        var json = new Option<bool>("--json") { Description = "the answer as JSON", Recursive = true };
        root.Options.Add(coverage);
        root.Options.Add(json);

        string Folder(ParseResult p) => p.GetValue(coverage) is { } given ? Path.GetFullPath(given)
            : Record.CoverageRoot() ?? throw new CopyException("not a checkout of GateKit; say where the record is with --coverage");
        int Run(ParseResult p, Func<string, TextWriter, int> body)
        {
            try { return body(Folder(p), p.InvocationConfiguration.Output); }
            catch (Exception e) when (e is CopyException or ArgumentException or GateKit.Names.NamesException)
            {
                p.InvocationConfiguration.Error.WriteLine(e.Message);
                return 1;
            }
        }
        void Print(TextWriter output, JsonNode node, bool asJson)
        {
            if (asJson) { output.WriteLine(node.ToJsonString(Indented).ReplaceLineEndings("\n")); return; }
            foreach (var (key, value) in node.AsObject())
                output.WriteLine($"{key.Replace('_', ' '),-28} {(value is JsonValue ? value.ToJsonString() : value?.ToJsonString())}");
        }

        // tally
        {
            var write = new Option<bool>("--write") { Description = "append the counts to research/coverage/record/counts.jsonl" };
            var run = new Option<string?>("--run") { Description = "the run the counts are taken for (default GATEKIT_RUN, else unknown)" };
            var c = new Command("tally", "the counts of the record now: types seen and never seen, verified, fields named by rung and owed, "
                + "systems described and sure, claims written and checked") { write, run };
            c.SetAction(p => Run(p, (folder, output) =>
            {
                var row = RecordBook.Tally(folder, p.GetValue(run));
                if (p.GetValue(write)) Record.Append(folder, "counts", row);
                Print(output, row, p.GetValue(json));
                if (p.GetValue(write) && !p.GetValue(json)) output.WriteLine("appended to " + Record.PathOf(folder, "counts"));
                return 0;
            }));
            root.Subcommands.Add(c);
        }
        // check
        {
            var c = new Command("check", "whatever in the record does not hold together; exit 1 with the list, 0 when it all does");
            c.SetAction(p => Run(p, (folder, output) =>
            {
                var problems = RecordBook.Check(folder);
                if (p.GetValue(json))
                    output.WriteLine(new JsonObject { ["ok"] = problems.Count == 0, ["problems"] = new JsonArray([.. problems.Select(x => (JsonNode)x!)]) }
                        .ToJsonString(Indented).ReplaceLineEndings("\n"));
                else
                {
                    foreach (var problem in problems) output.WriteLine(problem);
                    output.WriteLine(problems.Count == 0 ? "the record holds together" : $"{problems.Count} problems");
                }
                return problems.Count == 0 ? 0 : 1;
            }));
            root.Subcommands.Add(c);
        }
        // yield
        {
            var run = new Option<string>("--run") { Description = "the run (GATEKIT_RUN of its sessions)", Required = true };
            var c = new Command("yield", "what one run produced: types first seen per game copy, role and hour; its replays' pass share") { run };
            c.SetAction(p => Run(p, (folder, output) =>
            {
                Print(output, RecordBook.Yield(folder, p.GetValue(run)!), p.GetValue(json));
                return 0;
            }));
            root.Subcommands.Add(c);
        }
        // wish
        {
            var wish = new Command("wish", "the wishes ledger: add one, move one to a new state, list them");
            var text = new Argument<string>("text") { Description = "the wish: what you did, what happened, what you wanted instead" };
            var from = new Option<string?>("--from") { Description = "who has it (default GATEKIT_ROLE, else unknown)" };
            var state = new Option<string?>("--state") { Description = "open, sent, fixed, seen_live or dropped" };
            var commit = new Option<string?>("--commit") { Description = "the commit that fixed it" };
            var note = new Option<string?>("--note") { Description = "free text: why dropped, what is left, where it was sent" };

            var add = new Command("add", "record a new wish under the next id (W-0001 and on)") { text, from, state, commit, note };
            add.SetAction(p => Run(p, (folder, output) =>
            {
                var row = RecordBook.AddWish(folder, p.GetValue(from) ?? Record.Who().Role, p.GetValue(text)!, p.GetValue(state) ?? "open",
                    p.GetValue(note), p.GetValue(commit));
                output.WriteLine(p.GetValue(json) ? row.ToJsonString(Indented).ReplaceLineEndings("\n") : $"{row["id"]} {row["state"]}");
                return 0;
            }));
            wish.Subcommands.Add(add);

            var id = new Argument<string>("id") { Description = "the wish, W-0001 and on" };
            var to = new Argument<string>("state") { Description = "open, sent, fixed, seen_live or dropped" };
            var set = new Command("set", "move a wish to a new state (a row is appended; nothing is rewritten)") { id, to, commit, note };
            set.SetAction(p => Run(p, (folder, output) =>
            {
                var row = RecordBook.UpdateWish(folder, p.GetValue(id)!, p.GetValue(to)!, p.GetValue(commit), p.GetValue(note));
                output.WriteLine(p.GetValue(json) ? row.ToJsonString(Indented).ReplaceLineEndings("\n") : $"{row["id"]} {row["state"]}");
                return 0;
            }));
            wish.Subcommands.Add(set);

            var list = new Command("list", "the wishes as they stand, one line each") { state };
            list.SetAction(p => Run(p, (folder, output) =>
            {
                var rows = RecordBook.Wishes(folder).Where(w => p.GetValue(state) is not { } only || w["state"]?.GetValue<string>() == only).ToList();
                if (p.GetValue(json)) output.WriteLine(new JsonArray([.. rows.Select(r => (JsonNode)r.DeepClone())]).ToJsonString(Indented).ReplaceLineEndings("\n"));
                else foreach (var row in rows) output.WriteLine($"{row["id"]}  {row["state"],-9} {row["from"]}: {row["text"]}");
                return 0;
            }));
            wish.Subcommands.Add(list);
            root.Subcommands.Add(wish);
        }
        return root;
    }
}
