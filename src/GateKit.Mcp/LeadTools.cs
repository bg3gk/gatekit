using System.Text.Json;
using System.Text.Json.Nodes;
using GateKit.Leads;
using GateKit.Copies;
using ModelContextProtocol.Protocol;

namespace GateKit.Mcp;

/// <summary>The leads of the coverage hunt as a tool: the same work as <c>gatekit leads</c>, for a
/// session that has the tools and no shell. No game is needed for any of it.</summary>
static class LeadTools
{
    public static IReadOnlyList<GateKitTool> Create() => [Lead()];

    static GateKitTool Lead() => new(
        new Tool
        {
            Name = "bg3_lead",
            Description =
                "The leads of the coverage hunt: for every component type never seen alive, what creates it in the code, where that is "
                + "entered from (an Osiris call, a message the game's UI sends, a stats functor, a status, a boost, a system's update and what "
                + "it reads first, a load), what of the game's data carries it, and one plain sentence of what to do. No game needed.\n"
                + "NO STRETCH WITHOUT A LEAD: take leads with `next`, and say how each went with `report` - lit, empty (tried, nothing "
                + "appeared) or blocked (could not even be tried). Two tries with the lead's own instrument that found nothing park it and "
                + "raise a wish. An entry the walk up the callers did not reach is `unknown`: that is the truth, not an invitation to guess. "
                + "Evidence `by name` is marked as such and is weaker than a call chain.\n"
                + "Actions:\n"
                + "  next    the best open leads (default 3), marked in_work for `instance`; `instrument` keeps to one way of acting\n"
                + "  report  `type`, `outcome` (lit|empty|blocked), `note`; optional `instrument`, `instance`, `run`\n"
                + "  show    one lead in full: `type`\n"
                + "  list    best first; filters `class`, `state`, `reachable`, `prefix`; `limit` (default 30)\n"
                + "  tally   by class, state and reachability, with the true ceiling: every lead the shipping game can create (the default)\n"
                + "  build   rebuild the file from the databases (a minute or more; tries and hand-set fields are kept)",
            InputSchema = JsonSerializer.Deserialize<JsonElement>("""
                {
                 "type": "object",
                 "properties": {
                  "action": {"type": "string", "enum": ["next", "report", "show", "list", "tally", "build"], "description": "Default tally."},
                  "type": {"type": "string", "description": "report, show: the component type, e.g. esv::teleport::SpellComponent."},
                  "outcome": {"type": "string", "enum": ["lit", "empty", "blocked"], "description": "report: how the try went."},
                  "note": {"type": "string", "description": "report: what was done and what happened, one or two sentences."},
                  "instrument": {"type": "string", "enum": ["play", "gui", "stage", "osiris", "sweep", "two_copies", "split_screen", "network", "none"], "description": "next: only leads taken with this. report: what was used (default: the lead's own)."},
                  "instance": {"type": "string", "description": "next, report: the game copy (default: the server's own --instance)."},
                  "run": {"type": "string", "description": "report: the run (default: the claim's, else GATEKIT_RUN)."},
                  "count": {"type": "integer", "description": "next: how many (default 3)."},
                  "class": {"type": "string", "description": "list: only this class."},
                  "state": {"type": "string", "description": "list: only this state (open, in_work, lit, parked, unreachable)."},
                  "reachable": {"type": "string", "description": "list: only this reachability."},
                  "prefix": {"type": "string", "description": "list: only types whose name starts with this."},
                  "limit": {"type": "integer", "description": "list: at most this many (default 30)."},
                  "wiki": {"type": "boolean", "description": "build: ask bg3.wiki for pages (default true; cached answers are used either way)."}
                 }
                }
                """),
        },
        async (args, cancel) =>
        {
            try
            {
                return Results.Text(await Answer(args, cancel));
            }
            catch (Exception e) when (e is LeadsException or CopyException or IOException)
            {
                throw new InvalidOperationException(e.Message);
            }
        });

    static async Task<JsonNode> Answer(Args args, CancellationToken cancel)
    {
        string root = LeadsBuild.Root();
        string? instance = args.Str("instance") ?? (Environment.GetEnvironmentVariable("GATEKIT_INSTANCE") is { Length: > 0 } e ? e : null);
        static JsonArray Array(IEnumerable<JsonObject> rows) => new([.. rows.Select(r => (JsonNode)r.DeepClone())]);
        switch (args.Str("action") ?? "tally")
        {
            case "next":
                return Array(Leads.Lead.Next(root, instance ?? "unknown", args.Str("instrument"), (int)(args.Num("count") ?? 3)));
            case "report":
            {
                var (row, wish) = Leads.Lead.Report(root, args.Need("type"), args.Need("outcome"), args.Need("note"), instance, args.Str("instrument"), args.Str("run"));
                return new JsonObject { ["lead"] = row.DeepClone(), ["wish"] = wish?.DeepClone() };
            }
            case "show":
            {
                var type = args.Need("type");
                return Leads.Lead.Current(root).FirstOrDefault(r => (string?)r["type"] == type)?.DeepClone()
                    ?? throw new LeadsException($"no lead for {type}; `list` shows them");
            }
            case "list":
                return Array(LeadsCommands.Filter(Leads.Lead.Current(root), args.Str("class"), args.Str("state"), args.Str("reachable"), args.Str("prefix"),
                    (int)(args.Num("limit") ?? 30)));
            case "tally":
                return Leads.Lead.Tally(Leads.Lead.Current(root));
            case "build":
                return await LeadsBuild.Run(root, null, null, null, !args.Has("wiki") || args.Flag("wiki"), cancel);
            default:
                throw new LeadsException($"no action '{args.Str("action")}': next, report, show, list, tally or build");
        }
    }
}
