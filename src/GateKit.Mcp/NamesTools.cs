using System.Text.Json;
using System.Text.Json.Nodes;
using GateKit.Names;
using GateKit.Symbols.Queries;
using ModelContextProtocol.Protocol;

namespace GateKit.Mcp;

/// <summary>The record of field names as a tool: the same work as <c>gatekit names</c>, for a
/// session that has the tools and no shell. No game is needed for any of it.</summary>
static class NamesTools
{
    public static IReadOnlyList<GateKitTool> Create() => [Names()];

    static GateKitTool Names() => new(
        new Tool
        {
            Name = "bg3_names",
            Description =
                "The names of the engine's fields: how many are still a machine's, what is already known about one, and writing "
                + "a name with its evidence. It works on the committed layout files - no game needed.\n"
                + "A name carries the rung it came from, and the rung is part of the name: `game` (the game's own member name) and "
                + "`toolkit` (a Toolkit accessor) are facts somebody can check again; `inferred` is reasoning; `extender` is Script "
                + "Extender's name for the field, which nobody has checked against the game's code; `unknown` says so. The "
                + "game's own name wins: a weaker source does not replace a stronger one, and a real name that gives way stays as an "
                + "alias. EVERY name needs its evidence - what was read, forced or repeated, and where. A plausible name is worse than "
                + "field_8, because field_8 tells the truth about what is known and a wrong name travels into other people's mods.\n"
                + "Actions:\n"
                + "  tally    the measure (the default): fields written down, how many are ours to read, where each name came from\n"
                + "  worst    the components where a reader would pay off most\n"
                + "  suggest  for one `component` or a namespace `prefix`: the fields still carrying a machine's name, each with what "
                + "the symbol database knows at its offset. `certain: true` marks a fact that settles the name by itself\n"
                + "  adopt    the names such facts settle, listed; with `write: true` written, each with the evidence it was read from\n"
                + "  set      write `names`: one or several of {component, offset, name, source, evidence, struct?, type?, legacy?}. "
                + "A batch is written whole or not at all\n"
                + "  pending  what the runs have claimed about fields (bg3_recipe's `fields`) that no layout carries yet",
            InputSchema = JsonSerializer.Deserialize<JsonElement>("""
                {
                 "type": "object",
                 "properties": {
                  "action": {"type": "string", "enum": ["tally", "worst", "suggest", "adopt", "set", "pending"], "description": "Default tally."},
                  "prefix": {"type": "string", "description": "Only types whose name starts with this, e.g. \"esv::lock\"."},
                  "component": {"type": "string", "description": "suggest: one type, by the engine's own name."},
                  "all": {"type": "boolean", "description": "suggest: every field, not only those with a machine's name."},
                  "names": {"description": "set: one name or an array of them.", "type": ["object", "array"]},
                  "write": {"type": "boolean", "description": "adopt: write the names. set: false only checks and says what would change (default true)."},
                  "force": {"type": "boolean", "description": "set: let a weaker source replace a stronger one."}
                 }
                }
                """),
        },
        (args, _) =>
        {
            try
            {
                return Task.FromResult(Results.Text(Answer(args)));
            }
            catch (Exception e) when (e is NamesException or GateKit.Symbols.SymbolsException)
            {
                throw new InvalidOperationException(e.Message);
            }
        });

    static JsonNode Answer(Args args)
    {
        string record = NamesPaths.CoverageRoot();
        string prefix = args.Str("prefix") ?? "";
        switch (args.Str("action") ?? "tally")
        {
            case "tally":
            {
                var unreadable = new List<string>();
                return Tally.Measure(LayoutStore.All(record, unreadable.Add), prefix).ToJson(unreadable);
            }
            case "worst":
                return new JsonArray(Tally.Measure(LayoutStore.All(record), prefix).Worst().Select(w => (JsonNode)new JsonObject
                    { ["component"] = w.Component, ["machine"] = w.Machine, ["fields"] = w.Fields }).ToArray());
            case "suggest":
            {
                var files = LayoutStore.All(record);
                if (args.Str("component") is { } one)
                {
                    files = [LayoutStore.Find(files, one)];
                    prefix = "";
                }
                else if (prefix.Length == 0) throw new NamesException("say one component, or a namespace with prefix");
                using var symbols = new SymbolQueries();
                return FieldNames.ToJson(FieldNames.Owed(files, symbols, prefix, args.Flag("all")));
            }
            case "adopt":
            {
                using var symbols = new SymbolQueries();
                var changes = FieldNames.Adoptable(FieldNames.Owed(LayoutStore.All(record), symbols, prefix));
                bool write = args.Flag("write");
                return new JsonObject { ["written"] = write, ["names"] = FieldNames.ToJson(FieldNames.Apply(record, changes, dryRun: !write)) };
            }
            case "set":
            {
                bool write = !args.Has("write") || args.Flag("write");
                var changes = FieldNames.Parse(args.Node("names") ?? throw new NamesException("set needs names"));
                return new JsonObject { ["written"] = write, ["names"] = FieldNames.ToJson(FieldNames.Apply(record, changes, args.Flag("force"), !write)) };
            }
            case "pending":
                return FieldNames.ToJson(FieldNames.Pending(record));
            default:
                throw new NamesException($"no action '{args.Str("action")}': tally, worst, suggest, adopt, set or pending");
        }
    }
}
